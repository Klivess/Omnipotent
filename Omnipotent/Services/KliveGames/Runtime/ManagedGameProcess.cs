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
        private volatile bool _adopted;
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

        /// <summary>
        /// Wraps a server process that outlived the app — an orphan left behind when Omnipotent was
        /// killed rather than shut down. Resource sampling, exit detection, stop and kill all work; only
        /// stdio does not, because those pipes belonged to the previous host process and cannot be
        /// reattached. Such a server is driven through its remote console instead (see
        /// <see cref="ConsoleAttached"/>).
        /// </summary>
        public static ManagedGameProcess Adopt(Process process, int ringCapacity = 500, Func<string, Task>? logError = null)
        {
            var wrapper = new ManagedGameProcess(new LaunchSpec(), ringCapacity, logError) { _adopted = true };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                int code;
                try { code = process.ExitCode; } catch { code = -1; }
                try { wrapper.OnExited?.Invoke(code); } catch { }
            };
            wrapper._process = process;
            return wrapper;
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

        /// <summary>False for an adopted process: its stdout never reaches us and nothing can be written
        /// to its stdin, so console output and commands must come from a remote console (or not at all)
        /// until the server is restarted under this host.</summary>
        public bool ConsoleAttached => !_adopted;

        /// <summary>When the process was created. With the PID this is the only process identity Windows
        /// guarantees across an app restart, since PIDs are recycled.</summary>
        public DateTime? StartTimeUtc
        {
            get
            {
                try { return _process?.StartTime.ToUniversalTime(); }
                catch { return null; }
            }
        }

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

        /// <summary>Records a line as though the process had printed it — used to explain out-of-band
        /// events (an adoption, a stop we could not deliver) in the live console and its replay buffer.</summary>
        public void AppendConsoleLine(string line) => HandleLine(line);

        /// <summary>Sends a single console command (a line written to the process's stdin).</summary>
        public async Task SendCommandAsync(string command)
        {
            if (command == null) return;
            var process = _process;
            if (process == null || !IsRunning) return;
            if (_adopted) return; // no stdin to write to — the caller reports this to the user
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
