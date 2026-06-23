using System.Diagnostics;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Long-lived wrapper around a single game-server process. Unlike Stratum's ProcessRunner this
    /// NEVER imposes a hard timeout on the running server — a server is expected to run indefinitely.
    /// We only ever stop it gracefully (send the stop command and WAIT), escalating to a process-tree
    /// kill solely on explicit shutdown/delete after the grace window expires. (See the "no hard
    /// timeouts" project rule.)
    ///
    /// Responsibilities: spawn with redirected stdin/stdout/stderr, merge output into a capped ring
    /// buffer + raise <see cref="OnConsoleLine"/> live, accept console commands via stdin, expose
    /// resource sampling, and raise <see cref="OnExited"/> when the process ends.
    /// </summary>
    public sealed class ManagedGameProcess : IDisposable
    {
        private readonly LaunchSpec _spec;
        private readonly int _ringCapacity;
        private readonly Func<string, Task>? _logError;

        private readonly object _ringLock = new();
        private readonly Queue<string> _ring = new();
        private readonly ResourceSampler _sampler = new();

        private Process? _process;
        private volatile bool _stopRequested;
        private DateTime _lastOutputUtc = DateTime.UtcNow;

        /// <summary>Raised for every console line (stdout and stderr merged), in arrival order.</summary>
        public event Action<string>? OnConsoleLine;

        /// <summary>Raised once when the process exits, with its exit code (or -1 if unknown).</summary>
        public event Action<int>? OnExited;

        public ManagedGameProcess(LaunchSpec spec, int ringCapacity = 500, Func<string, Task>? logError = null)
        {
            _spec = spec;
            _ringCapacity = Math.Max(50, ringCapacity);
            _logError = logError;
        }

        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public int? Pid
        {
            get
            {
                try { return _process?.Id; }
                catch { return null; }
            }
        }

        /// <summary>True once a graceful stop or kill has been requested — lets the orchestrator tell an
        /// expected exit from a crash.</summary>
        public bool StopRequested => _stopRequested;

        /// <summary>UTC time the last console line arrived — used by the stall watchdog.</summary>
        public DateTime LastOutputUtc => _lastOutputUtc;

        public Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return Task.CompletedTask;

            _stopRequested = false;
            var psi = new ProcessStartInfo
            {
                FileName = _spec.Executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _spec.WorkingDirectory,
            };
            foreach (var a in _spec.Arguments) psi.ArgumentList.Add(a);
            foreach (var kv in _spec.Environment) psi.Environment[kv.Key] = kv.Value;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => HandleLine(e.Data);
            process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
            process.Exited += (_, _) =>
            {
                int code;
                try { code = process.ExitCode; } catch { code = -1; }
                try { OnExited?.Invoke(code); } catch { }
            };

            if (!process.Start())
                throw new Exception($"Failed to start process '{_spec.Executable}'.");

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return Task.CompletedTask;
        }

        private void HandleLine(string? line)
        {
            if (line == null) return;
            _lastOutputUtc = DateTime.UtcNow;
            lock (_ringLock)
            {
                _ring.Enqueue(line);
                while (_ring.Count > _ringCapacity) _ring.Dequeue();
            }
            try { OnConsoleLine?.Invoke(line); } catch { }
        }

        /// <summary>Sends a single console command (a line written to the process's stdin).</summary>
        public async Task SendCommandAsync(string command)
        {
            if (command == null) return;
            var process = _process;
            if (process == null || !IsRunning) return;
            try
            {
                await process.StandardInput.WriteLineAsync(command);
                await process.StandardInput.FlushAsync();
            }
            catch (Exception ex)
            {
                if (_logError != null) await _logError($"Failed to send command '{command}': {ex.Message}");
            }
        }

        /// <summary>
        /// Graceful stop. Sends <paramref name="stopCommand"/> and WAITS up to <paramref name="grace"/>
        /// for the process to exit on its own. Only if it has not exited and <paramref name="killOnExpiry"/>
        /// is true do we escalate to a process-tree kill. Returns true if the process exited (cleanly or killed),
        /// false if it is still running.
        /// </summary>
        public async Task<bool> StopGracefullyAsync(string stopCommand, TimeSpan grace, bool killOnExpiry)
        {
            var process = _process;
            if (process == null) return true;
            if (!IsRunning) return true;

            _stopRequested = true;

            if (!string.IsNullOrWhiteSpace(stopCommand))
                await SendCommandAsync(stopCommand);

            try
            {
                using var cts = new CancellationTokenSource(grace);
                await process.WaitForExitAsync(cts.Token);
                return true; // exited cleanly within grace
            }
            catch (OperationCanceledException)
            {
                // Grace expired without a clean exit.
                if (killOnExpiry)
                {
                    if (_logError != null)
                        await _logError($"Graceful stop exceeded {grace.TotalSeconds:0}s — escalating to a process-tree kill.");
                    Kill();
                    return true;
                }
                return false;
            }
        }

        /// <summary>Force-terminates the process and its child tree. Used only on explicit shutdown/delete.</summary>
        public void Kill()
        {
            _stopRequested = true;
            try { _process?.Kill(entireProcessTree: true); } catch { }
        }

        public (double cpuPercent, long ramBytes) SampleResources() => _sampler.Sample(_process);

        public IReadOnlyList<string> SnapshotRecentLines(int max)
        {
            lock (_ringLock)
            {
                if (max <= 0 || max >= _ring.Count) return _ring.ToArray();
                return _ring.Skip(_ring.Count - max).ToArray();
            }
        }

        public void Dispose()
        {
            try { _process?.Dispose(); } catch { }
        }
    }
}
