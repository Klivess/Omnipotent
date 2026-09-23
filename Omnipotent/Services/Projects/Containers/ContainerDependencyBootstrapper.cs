using System.Diagnostics;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Self-healing for the desktop-container layer's host dependencies. When the Docker daemon
    /// is unreachable it walks the remediation ladder itself instead of just reporting failure:
    ///   1. Not installed → install via winget (silent).
    ///   2. WSL unable to run on this Windows build → remove the incompatible package, report restart.
    ///   3. Not running → launch; running with a dead engine → restart Docker's own processes.
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
        // Docker Desktop's cold start (WSL VM boot + engine) takes minutes on this i5-3570 host. A
        // one-minute budget declared healthy starts failed and fed the restart churn.
        private static readonly TimeSpan DaemonStartBudget = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan WingetBudget = TimeSpan.FromMinutes(20);

        /// <summary>Human-readable state of the last bootstrap attempt, for tool results and logs.</summary>
        public string LastStatus { get; private set; } = "no bootstrap attempted yet";
        /// <summary>True while an attempt is in flight (so callers can say "in progress, retry later").</summary>
        public bool InProgress { get; private set; }
        public string LastDiagnostics { get; private set; } = "not collected";
        public DateTime? LastAttemptUtc => lastAttemptUtc == DateTime.MinValue ? null : lastAttemptUtc;
        /// <summary>True while a completed repair needs one Windows restart before Docker can start.</summary>
        public bool RestartRequired { get; private set; }

        private readonly string markerDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Omnipotent", "DockerRecovery");

        public ContainerDependencyBootstrapper(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Ensures the Docker daemon is running, repairing/starting Docker Desktop as needed.
        /// Returns true when the daemon answers the probe. Re-entrant calls while an attempt is
        /// running (or within the cooldown after a failed one) return false immediately with
        /// <see cref="LastStatus"/> explaining why.
        ///
        /// Ladder, each rung bounded: (1) WSL health — a WSL package this Windows build cannot run
        /// is removed and the host is reported as needing one restart; (2) the WSL VM memory cap;
        /// (3) launch Docker Desktop, or, when it is already running with a dead engine, stop only
        /// Docker's own processes and service and launch it again; (4) wait for the engine with a
        /// budget sized for this host's cold start. Never unregisters a distro, resets Docker, or
        /// restarts Windows.
        /// </summary>
        public async Task<bool> EnsureDaemonAsync(Func<CancellationToken, Task<string?>> probeAsync, CancellationToken ct = default,
            string dockerUri = "npipe://./pipe/docker_engine")
        {
            if (await probeAsync(ct) == null) { Healthy(); return true; }
            if (!IsLocalDesktopEndpoint(dockerUri))
            {
                Status($"Configured Docker endpoint {dockerUri} is unavailable. Local Docker Desktop recovery does not apply to this endpoint.");
                return false;
            }

            if (!await gate.WaitAsync(0, ct))
                return false; // an attempt is already running; its LastStatus is current
            try
            {
                InProgress = true;
                if (DateTime.UtcNow - lastAttemptUtc < AttemptCooldown)
                    return false; // recent attempt failed; don't thrash installs
                lastAttemptUtc = DateTime.UtcNow;

                // Re-probe under the gate — the daemon may have come up while we waited.
                if (await probeAsync(ct) == null) { Healthy(); return true; }

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

                // (1) Docker Desktop's engine lives in WSL. When WSL itself cannot run, no amount
                // of relaunching helps — this is the state the host was left in on 2026-09-18.
                string wslOutput = await ProbeWslAsync(ct);
                LastDiagnostics = Describe(exe, wslOutput);
                string? wslRepair = await WslHostCompatibility.RepairIncompatiblePackageAsync(wslOutput, markerDirectory, log, ct);
                if (wslRepair != null)
                {
                    RestartRequired = true;
                    Status("Docker engine cannot start until Windows restarts once: " + wslRepair);
                    return false;
                }
                // WSL answers normally again; a restart flagged by an earlier attempt is no longer
                // what stands between the host and a running engine.
                RestartRequired = false;

                // (2) Bound the VM before (re)starting it; the cap applies from this start onwards.
                string? cap = WslHostCompatibility.EnsureMemoryCap(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
                if (cap != null) log($"Desktop bootstrap: {cap}.");

                // (3) Launch, or restart a Docker Desktop whose UI is up but whose engine is dead.
                // Launching an already-running Docker Desktop only focuses its window, which is why
                // earlier recovery "attempts" left a wedged engine exactly as it was.
                bool wasRunning = DockerDesktopProcessesRunning();
                if (wasRunning)
                {
                    Status("Docker Desktop is running but its engine is not answering — restarting Docker Desktop's own processes.");
                    await StopDockerDesktopAsync(ct);
                }
                else Status("Starting Docker Desktop…");
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
                catch (Exception ex)
                {
                    Status($"Failed to launch Docker Desktop ({ex.Message}).");
                    return false;
                }

                // (4) Wait with a budget sized for a cold WSL + engine start on this host.
                var deadline = DateTime.UtcNow + DaemonStartBudget;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    if (await probeAsync(ct) == null)
                    {
                        Healthy(wasRunning ? "Docker daemon recovered after restarting Docker Desktop." : "Docker daemon is up.");
                        lastAttemptUtc = DateTime.MinValue; // success clears the cooldown
                        return true;
                    }
                }

                LastDiagnostics = Describe(exe, await ProbeWslAsync(ct));
                Status($"Docker engine did not answer within {DaemonStartBudget.TotalMinutes:0} minutes of (re)starting Docker Desktop. " +
                    LastDiagnostics + " Preserve Docker data. The next supervised attempt runs after the cooldown.");
                return false;
            }
            finally
            {
                InProgress = false;
                gate.Release();
            }
        }

        internal static bool IsLocalDesktopEndpoint(string endpoint) =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme == "npipe" && uri.Host is "." or "localhost"
            && uri.AbsolutePath is "/pipe/docker_engine" or "/pipe/dockerDesktopLinuxEngine";

        internal static async Task<(int ExitCode, string Output)> RunProcessAsync(
            string executable, IEnumerable<string> arguments, TimeSpan budget, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            try
            {
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
                // Drain both pipes concurrently; installers and diagnostics can fill stderr.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(budget);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
                    string output = (await stdout + " " + await stderr).Replace("\0", "").Trim();
                    return (process.ExitCode, output.Length > 2000 ? output[..2000] : output);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return (-1, $"Timed out after {budget.TotalSeconds:0}s.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static readonly string[] DockerDesktopProcessNames =
        {
            "Docker Desktop", "com.docker.backend", "com.docker.proxy", "com.docker.dev-envs",
            "com.docker.extensions", "com.docker.wsl-distro-proxy", "vpnkit", "docker-index",
        };

        private static bool DockerDesktopProcessesRunning() =>
            DockerDesktopProcessNames.Take(2).Any(name =>
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                return found.Length > 0;
            });

        /// <summary>Stops Docker Desktop's own processes, service and WSL distros, for a cold start.</summary>
        private async Task StopDockerDesktopAsync(CancellationToken ct)
        {
            foreach (string name in DockerDesktopProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            // The Windows service owns the privileged half; restart it so it is not left in the
            // "Starting" state it hung in on 2026-09-18. Requires Omnipotent's elevation.
            var restart = await RunProcessAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                new[] { "-NoProfile", "-NonInteractive", "-Command",
                    "$s = Get-Service -Name 'com.docker.service' -ErrorAction SilentlyContinue; " +
                    "if ($s) { Restart-Service -Name 'com.docker.service' -Force -ErrorAction Stop; 'restarted' } else { 'absent' }" },
                TimeSpan.FromSeconds(60), ct);
            if (restart.ExitCode != 0) log($"Desktop bootstrap: Docker service restart did not complete ({restart.Output}).");
            await ResetDockerWslVmAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        private static readonly HashSet<string> DockerDistros = new(StringComparer.OrdinalIgnoreCase)
        {
            "docker-desktop", "docker-desktop-data",
        };

        /// <summary>
        /// Killing Docker Desktop's Windows processes leaves its WSL distro running, and a relaunch
        /// reattaches to it. On 2026-09-23 that distro was "Running" while its init never answered
        /// ("Waiting for Procd service"), so every restart reattached to the same wedged VM. Docker's
        /// own distros are always terminated; the whole WSL VM is shut down only when no other
        /// distro is running, so an unrelated WSL workload is never interrupted.
        /// </summary>
        private async Task ResetDockerWslVmAsync(CancellationToken ct)
        {
            string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (!File.Exists(wsl)) return;
            var list = await RunProcessAsync(wsl, new[] { "-l", "-v" }, TimeSpan.FromSeconds(20), ct);
            var running = ParseRunningDistros(list.Output);
            if (running.All(DockerDistros.Contains))
            {
                var shutdown = await RunProcessAsync(wsl, new[] { "--shutdown" }, TimeSpan.FromSeconds(60), ct);
                log($"Desktop bootstrap: WSL VM shut down for a cold Docker start (exit {shutdown.ExitCode}).");
                return;
            }
            foreach (string distro in DockerDistros)
                await RunProcessAsync(wsl, new[] { "--terminate", distro }, TimeSpan.FromSeconds(30), ct);
            log($"Desktop bootstrap: terminated Docker's WSL distros; left {string.Join(", ", running.Where(d => !DockerDistros.Contains(d)))} running.");
        }

        /// <summary>Pure: running distro names from `wsl -l -v` (UTF-16 output arrives with NULs).</summary>
        internal static List<string> ParseRunningDistros(string output)
        {
            var running = new List<string>();
            foreach (string raw in output.Replace("\0", "").Split('\n'))
            {
                var parts = raw.Replace("*", " ").Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[^2].Equals("Running", StringComparison.OrdinalIgnoreCase))
                    running.Add(string.Join(' ', parts[..^2]));
            }
            return running;
        }

        /// <summary>Both wsl.exe front doors (built-in and standalone package), bounded.</summary>
        private static async Task<string> ProbeWslAsync(CancellationToken ct)
        {
            var outputs = new List<string>();
            foreach (string wsl in new[]
            {
                Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe"),
            }.Where(File.Exists))
            {
                var status = await RunProcessAsync(wsl, new[] { "--status" }, TimeSpan.FromSeconds(20), ct);
                outputs.Add($"{wsl} --status (exit {status.ExitCode}): {status.Output}");
            }
            return outputs.Count == 0 ? "wsl.exe not found" : string.Join(" | ", outputs);
        }

        private static string Describe(string exe, string wslOutput)
        {
            string version;
            try { version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?"; } catch { version = "?"; }
            string wsl = wslOutput.Replace("\r", " ").Replace("\n", " ").Trim();
            if (wsl.Length > 600) wsl = wsl[..600] + "…";
            return $"Docker Desktop {version}; Windows build {Environment.OSVersion.Version.Build}; WSL: {wsl}";
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
            var result = await RunProcessAsync("winget", new[]
            {
                "install", "-e", "--id", "Docker.DockerDesktop", "--silent", "--disable-interactivity",
                "--accept-package-agreements", "--accept-source-agreements",
            }, WingetBudget, ct);
            if (result.ExitCode == 0) return true;
            Status($"Docker install failed (exit {result.ExitCode}): {result.Output}");
            return false;
        }

        private void Healthy(string message = "Docker daemon is up.")
        {
            RestartRequired = false;
            Status(message);
        }

        private void Status(string s)
        {
            if (LastStatus == s) return;
            LastStatus = s;
            log($"Desktop bootstrap: {s}");
        }
    }
}
