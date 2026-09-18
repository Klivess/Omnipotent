using System.Diagnostics;
using System.Text;

namespace Omnipotent.Data_Handling
{
    /// <summary>
    /// Runs shell scripts on the HOST machine (the box Omnipotent itself runs on), used by both
    /// KliveAgent and Projects. Scripts inherit Omnipotent's own security context â€” if Omnipotent
    /// is running elevated ("as admin"), so do these; this deliberately does NOT trigger a UAC
    /// prompt (that can't work headless) â€” elevation is a property of how Omnipotent was launched.
    ///
    /// PowerShell scripts use a temporary .ps1; Bash scripts are streamed on stdin so the same path
    /// works with WSL and Git Bash. Stdout/stderr are captured concurrently, and a timeout kills
    /// the whole process tree rather than hanging a wake forever.
    /// </summary>
    public static class HostShell
    {
        public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string Interpreter)
        {
            public bool Success => !TimedOut && ExitCode == 0;

            /// <summary>A single agent-facing block: status line + stdout + stderr, capped in size.</summary>
            public string Format(int maxChars = 16000)
            {
                var sb = new StringBuilder();
                sb.AppendLine(TimedOut
                    ? $"[{Interpreter}] TIMED OUT â€” process tree killed."
                    : $"[{Interpreter}] exit code {ExitCode}{(Success ? " (success)" : " (non-zero)")}.");
                if (!string.IsNullOrWhiteSpace(Stdout)) { sb.AppendLine("â”€â”€ stdout â”€â”€"); sb.AppendLine(Stdout.TrimEnd()); }
                if (!string.IsNullOrWhiteSpace(Stderr)) { sb.AppendLine("â”€â”€ stderr â”€â”€"); sb.AppendLine(Stderr.TrimEnd()); }
                string s = sb.ToString().TrimEnd();
                return s.Length <= maxChars ? s : s[..maxChars] + $"\n[â€¦output truncated to {maxChars} chars]";
            }
        }

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Runs a PowerShell script. Prefers PowerShell 7 (pwsh) and falls back to Windows
        /// PowerShell (powershell.exe). Runs with -NoProfile -NonInteractive and ExecutionPolicy
        /// Bypass so an unsigned throwaway script isn't blocked.
        /// </summary>
        public static Task<ShellResult> RunPowerShellAsync(string script, TimeSpan? timeout = null, string? workingDir = null, CancellationToken ct = default)
        {
            string exe = ResolveOnPath("pwsh.exe") ?? ResolveOnPath("pwsh") ?? "powershell.exe";
            return RunWithScriptFileAsync(exe,
                file => $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"",
                ".ps1", WrapPowerShell(script), timeout, workingDir, "powershell", ct);
        }

        /// <summary>PowerShell itself exits zero after many failed native commands. Capture the
        /// final native exit code and convert terminating PowerShell errors into a process failure,
        /// while retaining the original stdout/stderr for diagnosis.</summary>
        private static string WrapPowerShell(string script) =>
            "$ErrorActionPreference = 'Stop'\n" +
            "$__omniNativeExit = 0\n" +
            "try {\n" + (script ?? "").Replace("\r\n", "\n").Replace('\r', '\n') + "\n" +
            "  if ($null -ne $LASTEXITCODE) { $__omniNativeExit = [int]$LASTEXITCODE }\n" +
            "} catch {\n" +
            "  [Console]::Error.WriteLine($_.Exception.ToString())\n" +
            "  exit 1\n" +
            "}\n" +
            "if ($__omniNativeExit -ne 0) { exit $__omniNativeExit }\n";

        /// <summary>
        /// Runs a Bash script. Resolves bash from PATH (WSL/Git Bash on Windows). Returns a clear
        /// error result if no bash is installed rather than throwing.
        /// </summary>
        public static async Task<ShellResult> RunBashAsync(string script, TimeSpan? timeout = null, string? workingDir = null, CancellationToken ct = default)
        {
            string? bash = ResolveOnPath("bash.exe") ?? ResolveOnPath("bash");
            if (bash == null)
                return new ShellResult(-1, "", "bash is not installed or not on PATH on this host (install WSL or Git Bash, or use PowerShell).", false, "bash");
            // Feed the script on stdin. Converting a temp path to /mnt/c works for WSL but not Git
            // Bash (/c), which was why perfectly valid commands repeatedly exited 1 with no useful
            // output. Both interpreters accept `bash -s`, so stdin is the portable path.
            try
            {
                return await RunProcessAsync(bash, "--noprofile --norc -s", timeout ?? DefaultTimeout,
                    workingDir, "bash", ct, script.Replace("\r\n", "\n"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new ShellResult(-1, "", $"Failed to run bash: {ex.Message}", false, "bash");
            }
        }

        private static async Task<ShellResult> RunWithScriptFileAsync(
            string exe, Func<string, string> argsFor, string ext, string script,
            TimeSpan? timeout, string? workingDir, string label, CancellationToken ct)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "omni-shell-" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                await File.WriteAllTextAsync(tempFile, script, ct);
                return await RunProcessAsync(exe, argsFor(tempFile), timeout ?? DefaultTimeout, workingDir, label, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new ShellResult(-1, "", $"Failed to run {label}: {ex.Message}", false, label);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        private static async Task<ShellResult> RunProcessAsync(string exe, string arguments, TimeSpan timeout, string? workingDir, string label, CancellationToken ct,
            string? standardInput = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput != null,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)
                    ? Path.GetTempPath() : workingDir,
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
                return new ShellResult(-1, "", $"Failed to start {exe}.", false, label);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                if (standardInput != null)
                {
                    await proc.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutCts.Token);
                    await proc.StandardInput.FlushAsync(timeoutCts.Token);
                    proc.StandardInput.Close();
                }
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Give the readers a beat to flush what was produced before the kill.
                try { await Task.Delay(150, CancellationToken.None); } catch { }
                bool byCaller = ct.IsCancellationRequested;
                if (byCaller) ct.ThrowIfCancellationRequested();
                return new ShellResult(-1, stdout.ToString(),
                    $"Timed out after {timeout.TotalSeconds:0}s.\n" + stderr,
                    true, label);
            }
            // WaitForExitAsync returns when the process exits, but the async readers may have one
            // more callback queued; a short join ensures the last lines are captured.
            try { await Task.Delay(50, CancellationToken.None); } catch { }
            return new ShellResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false, label);
        }

        /// <summary>Resolves an executable name against PATH (and PATHEXT-less direct hit), else null.</summary>
        /// <summary>Result of a detached launch: the child PID and its interpreter. No output
        /// capture and no waiting happen; the caller owns the reap (sentinel-file contract).</summary>
        public sealed record DetachedShellResult(int Pid, string Interpreter)
        {
            /// <summary>Agent-facing block: the launch is fire-and-forget.</summary>
            public string Format(int maxChars = 16000)
            {
                string s = $"[{Interpreter}] DETACHED - pid {Pid}. The child runs independently: no stdout/stderr capture, not killed by any timeout. Use the sentinel-file contract to detect completion.";
                return s.Length <= maxChars ? s : s[..maxChars];
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct DetachStartupInfo
        {
            public uint cb;
            public IntPtr lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DetachProcessInfo
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            System.Text.StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref DetachStartupInfo lpStartupInfo,
            out DetachProcessInfo lpProcessInformation);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        const uint CREATE_NO_WINDOW = 0x08000000;
        // NOTE: do NOT add DETACHED_PROCESS (0x08) here. It tells the OS the child is a
        // console process with no console that cannot use std/console handles; PowerShell
        // and bash expect a console, so the child initializes and self-exits 0 before it
        // ever reads its script. CREATE_NO_WINDOW gives a hidden-but-valid console instead,
        // and CREATE_BREAKAWAY_FROM_JOB already keeps the child out of any job object.
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

        /// <summary>
        /// Launches a PowerShell job DETACHED: returns the child PID immediately, capturing
        /// nothing and waiting on nothing. CREATE_BREAKAWAY_FROM_JOB keeps the child out of
        /// any job object the host runs under, so the caller's timeout or death cannot kill
        /// the job. The temp script file is left in place for the lifetime of the child; the
        /// OS reclaims it. The caller owns completion detection (sentinel-file contract).
        /// </summary>
        public static async Task<DetachedShellResult> RunPowerShellDetachedAsync(string script, string? workingDir = null, CancellationToken ct = default)
        {
            string exe = ResolveOnPath("pwsh.exe") ?? ResolveOnPath("pwsh") ?? "powershell.exe";
            string tempFile = Path.Combine(Path.GetTempPath(), "omni-shell-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                await File.WriteAllTextAsync(tempFile, WrapPowerShell(script), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            return await StartDetachedAsync(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempFile}\"", workingDir, "powershell");
        }

        /// <summary>
        /// Launches a Bash job DETACHED (see <see cref="RunPowerShellDetachedAsync"/>). The
        /// script goes to a temp file the child reads; the caller owns completion detection
        /// (sentinel-file contract).
        /// </summary>
        public static async Task<DetachedShellResult> RunBashDetachedAsync(string script, string? workingDir = null, CancellationToken ct = default)
        {
            string? bash = ResolveOnPath("bash.exe") ?? ResolveOnPath("bash");
            if (bash == null) throw new FileNotFoundException("bash is not installed or not on PATH on this host (install WSL or Git Bash, or use PowerShell).");
            string tempFile = Path.Combine(Path.GetTempPath(), "omni-shell-" + Guid.NewGuid().ToString("N") + ".sh");
            try
            {
                await File.WriteAllTextAsync(tempFile, (script ?? "").Replace("\r\n", "\n"), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            return await StartDetachedAsync(bash, $"--noprofile --norc \"{tempFile}\"", workingDir, "bash");
        }

        private static async Task<DetachedShellResult> StartDetachedAsync(string exe, string arguments, string? workingDir, string label)
        {
            string? dir = string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)
                ? Path.GetTempPath() : workingDir;
            // CreateProcessW wants the full command line; quote the image path because the
            // temp working directory differs from the exe's own directory.
            string commandLine = $"\"{exe}\" {arguments}";
            var cmdLine = new System.Text.StringBuilder(commandLine);
            var si = new DetachStartupInfo { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DetachStartupInfo>() };
            var pi = new DetachProcessInfo();
            bool ok = await Task.Run(() => CreateProcessW(
                null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB,
                IntPtr.Zero, dir, ref si, out pi));
            if (!ok)
            {
                int winErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                // ACCESS_DENIED (5) means this process is a NON-PRIMARY member of a job object, so the OS
                // refuses CREATE_BREAKAWAY_FROM_JOB. Fall back to a plain no-window child: it still escapes
                // the caller's timeout/kill via the sentinel-file contract, it just stays in the parent job
                // (a host that closes its job with KILL_ON_JOB_CLOSE would take the child down with it).
                if (winErr == 5)
                {
                    si = new DetachStartupInfo { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DetachStartupInfo>() };
                    pi = new DetachProcessInfo();
                    ok = await Task.Run(() => CreateProcessW(
                        null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW,
                        IntPtr.Zero, dir, ref si, out pi));
                    if (!ok)
                        throw new System.ComponentModel.Win32Exception(
                            System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Failed to start detached " + label + " (fallback).");
                }
                else
                {
                    throw new System.ComponentModel.Win32Exception(winErr, "Failed to start detached " + label + " (win32 err " + winErr + ").");
                }
            }
            uint pid = pi.dwProcessId;
            try { CloseHandle(pi.hProcess); } catch { }
            try { CloseHandle(pi.hThread); } catch { }
            return new DetachedShellResult((int)pid, label);
        }

        private static string? ResolveOnPath(string exe)
        {
            try
            {
                if (Path.IsPathRooted(exe) && File.Exists(exe)) return exe;
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathEnv)) return null;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try { string candidate = Path.Combine(dir.Trim(), exe); if (File.Exists(candidate)) return candidate; }
                    catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
