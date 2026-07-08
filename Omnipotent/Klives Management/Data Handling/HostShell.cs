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
    /// Scripts are written to a temp file and executed by path (no fragile inline quoting), stdout
    /// and stderr are captured concurrently (so a chatty script can't deadlock on a full pipe), and
    /// a timeout kills the whole process tree rather than hanging a wake forever.
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
                ".ps1", script, timeout, workingDir, "powershell", ct);
        }

        /// <summary>
        /// Runs a Bash script. Resolves bash from PATH (WSL/Git Bash on Windows). Returns a clear
        /// error result if no bash is installed rather than throwing.
        /// </summary>
        public static Task<ShellResult> RunBashAsync(string script, TimeSpan? timeout = null, string? workingDir = null, CancellationToken ct = default)
        {
            string? bash = ResolveOnPath("bash.exe") ?? ResolveOnPath("bash");
            if (bash == null)
                return Task.FromResult(new ShellResult(-1, "", "bash is not installed or not on PATH on this host (install WSL or Git Bash, or use PowerShell).", false, "bash"));
            // Bash needs LF line endings; normalise so a CRLF-authored script doesn't choke.
            return RunWithScriptFileAsync(bash,
                file => $"\"{ToBashPath(file)}\"",
                ".sh", script.Replace("\r\n", "\n"), timeout, workingDir, "bash", ct);
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
            catch (Exception ex)
            {
                return new ShellResult(-1, "", $"Failed to run {label}: {ex.Message}", false, label);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        private static async Task<ShellResult> RunProcessAsync(string exe, string arguments, TimeSpan timeout, string? workingDir, string label, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Give the readers a beat to flush what was produced before the kill.
                try { await Task.Delay(150, CancellationToken.None); } catch { }
                bool byCaller = ct.IsCancellationRequested;
                return new ShellResult(-1, stdout.ToString(),
                    (byCaller ? "Cancelled." : $"Timed out after {timeout.TotalSeconds:0}s.") + "\n" + stderr,
                    !byCaller, label);
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

        /// <summary>Converts a Windows temp path to the /mnt/c form WSL bash understands (Git Bash also accepts it).</summary>
        private static string ToBashPath(string windowsPath)
        {
            if (windowsPath.Length >= 2 && windowsPath[1] == ':')
                return "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
            return windowsPath.Replace('\\', '/');
        }
    }
}
