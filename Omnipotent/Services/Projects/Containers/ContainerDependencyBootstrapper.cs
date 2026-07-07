using System.Diagnostics;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Self-healing for the desktop-container layer's host dependencies. When the Docker daemon
    /// is unreachable it walks the remediation ladder itself instead of just reporting failure:
    ///   1. Docker Desktop installed but not running → launch it and wait for the daemon.
    ///   2. Not installed → install via winget (silent), then launch and wait.
    /// Single-flight with a cooldown so repeated tool failures don't spawn parallel installs.
    /// Best-effort by design: a FRESH install can still require WSL2 enablement or a reboot,
    /// which cannot be automated — <see cref="LastStatus"/> always carries the precise state so
    /// the agent/logs say exactly what remains instead of a mystery timeout.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ContainerDependencyBootstrapper
    {
        private readonly Action<string> log;
        private readonly SemaphoreSlim gate = new(1, 1);
        private DateTime lastAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DaemonStartBudget = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan WingetBudget = TimeSpan.FromMinutes(20);

        /// <summary>Human-readable state of the last bootstrap attempt, for tool results and logs.</summary>
        public string LastStatus { get; private set; } = "no bootstrap attempted yet";
        /// <summary>True while an attempt is in flight (so callers can say "in progress, retry later").</summary>
        public bool InProgress { get; private set; }

        public ContainerDependencyBootstrapper(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Ensures the Docker daemon is running, installing/starting Docker Desktop as needed.
        /// Returns true when the daemon answers the probe. Re-entrant calls while an attempt is
        /// running (or within the cooldown after a failed one) return false immediately with
        /// <see cref="LastStatus"/> explaining why.
        /// </summary>
        public async Task<bool> EnsureDaemonAsync(Func<CancellationToken, Task<string?>> probeAsync, CancellationToken ct = default)
        {
            if (await probeAsync(ct) == null) return true;

            if (!await gate.WaitAsync(0, ct))
                return false; // an attempt is already running; its LastStatus is current
            try
            {
                InProgress = true;
                if (DateTime.UtcNow - lastAttemptUtc < AttemptCooldown)
                    return false; // recent attempt failed; don't thrash installs
                lastAttemptUtc = DateTime.UtcNow;

                // Re-probe under the gate — the daemon may have come up while we waited.
                if (await probeAsync(ct) == null) { Status("Docker daemon is up."); return true; }

                string? exe = FindDockerDesktopExe();
                if (exe == null)
                {
                    Status("Docker Desktop is not installed — installing via winget (this can take many minutes)…");
                    if (!await WingetInstallDockerAsync(ct))
                        return false; // WingetInstall sets LastStatus with the specific failure
                    exe = FindDockerDesktopExe();
                    if (exe == null)
                    {
                        Status("winget reported success but Docker Desktop.exe was not found afterwards — a reboot or manual first launch is probably required.");
                        return false;
                    }
                    Status("Docker Desktop installed.");
                }

                Status("Starting Docker Desktop…");
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Status($"Failed to launch Docker Desktop ({ex.Message}).");
                    return false;
                }

                var deadline = DateTime.UtcNow + DaemonStartBudget;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    if (await probeAsync(ct) == null)
                    {
                        Status("Docker daemon is up.");
                        lastAttemptUtc = DateTime.MinValue; // success clears the cooldown
                        return true;
                    }
                }
                Status($"Docker Desktop was started but the daemon didn't answer within {DaemonStartBudget.TotalMinutes:0} minutes — a fresh install may need WSL2 enabled, a reboot, or the first-run dialog accepted once.");
                return false;
            }
            finally
            {
                InProgress = false;
                gate.Release();
            }
        }

        /// <summary>Locates Docker Desktop.exe in its standard install locations.</summary>
        private static string? FindDockerDesktopExe()
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string candidate = Path.Combine(root, "Docker", "Docker", "Docker Desktop.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private async Task<bool> WingetInstallDockerAsync(CancellationToken ct)
        {
            ProcessStartInfo psi = new()
            {
                FileName = "winget",
                Arguments = "install -e --id Docker.DockerDesktop --silent --disable-interactivity " +
                            "--accept-package-agreements --accept-source-agreements",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Process proc;
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("winget did not start");
            }
            catch (Exception ex)
            {
                Status($"winget is unavailable on this host ({ex.Message}) — install Docker Desktop manually from docker.com.");
                return false;
            }

            using (proc)
            {
                // Stream output into the service log so the (long) install is observable.
                var stdout = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                        if (!string.IsNullOrWhiteSpace(line)) log($"winget: {line.Trim()}");
                }, ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(WingetBudget);
                try
                {
                    await proc.WaitForExitAsync(timeout.Token);
                    await stdout;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Status($"winget install exceeded {WingetBudget.TotalMinutes:0} minutes and was abandoned — install Docker Desktop manually.");
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    // Most common cause: Omnipotent isn't elevated and the installer needs admin.
                    Status($"winget install failed (exit {proc.ExitCode}) — likely needs elevation. Run Omnipotent as admin once, or install Docker Desktop manually.");
                    return false;
                }
            }
            return true;
        }

        private void Status(string s)
        {
            LastStatus = s;
            log($"Desktop bootstrap: {s}");
        }
    }
}
