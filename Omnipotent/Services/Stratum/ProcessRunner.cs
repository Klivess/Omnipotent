using System.Diagnostics;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Small shared helper for spawning external CLI tools used by Stratum agents.
    /// Lives outside <see cref="StratumPythonRunner"/> so the Simulation/other agents can share it.
    /// </summary>
    internal static class ProcessRunner
    {
        public static Task<(int exit, string stdout, string stderr)> RunAsync(
            string exe, string[] args, string? workDir, TimeSpan timeout, Action<string>? onStdoutLine, CancellationToken ct)
            => RunAsync(exe, args, workDir, timeout, onStdoutLine, null, ct);

        public static async Task<(int exit, string stdout, string stderr)> RunAsync(
            string exe, string[] args, string? workDir, TimeSpan timeout,
            Action<string>? onStdoutLine, Action<string>? onStderrLine, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");

            var stdoutSb = new System.Text.StringBuilder();
            var stderrSb = new System.Text.StringBuilder();
            var stdoutDone = new TaskCompletionSource<bool>();
            var stderrDone = new TaskCompletionSource<bool>();

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { stdoutDone.TrySetResult(true); return; }
                stdoutSb.AppendLine(e.Data);
                try { onStdoutLine?.Invoke(e.Data); } catch { }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { stderrDone.TrySetResult(true); return; }
                stderrSb.AppendLine(e.Data);
                try { onStderrLine?.Invoke(e.Data); } catch { }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Process {exe} exceeded timeout {timeout.TotalSeconds}s");
            }

            await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(2000));
            return (p.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
        }
    }
}
