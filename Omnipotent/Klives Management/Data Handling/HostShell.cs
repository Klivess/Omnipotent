using System.Diagnostics;
using System.Text;

namespace Omnipotent.Data_Handling
{
    /// <summary>
    /// Runs shell scripts on the HOST machine (the box Omnipotent itself runs on), used by both
    /// KliveAgent and Projects. Scripts inherit Omnipotent's own security context — if Omnipotent
    /// is running elevated ("as admin"), so do these; this deliberately does NOT trigger a UAC
    /// prompt (that can't work headless) — elevation is a property of how Omnipotent was launched.
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
                    ? $"[{Interpreter}] TIMED OUT — process tree killed."
                    : $"[{Interpreter}] exit code {ExitCode}{(Success ? " (success)" : " (non-zero)")}.");
                if (!string.IsNullOrWhiteSpace(Stdout)) { sb.AppendLine("── stdout ──"); sb.AppendLine(Stdout.TrimEnd()); }
                if (!string.IsNullOrWhiteSpace(Stderr)) { sb.AppendLine("── stderr ──"); sb.AppendLine(Stderr.TrimEnd()); }
                string s = sb.ToString().TrimEnd();
                return s.Length <= maxChars ? s : s[..maxChars] + $"\n[…output truncated to {maxChars} chars]";
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
