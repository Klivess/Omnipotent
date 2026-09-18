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
        private static readonly TimeSpan DaemonStartBudget = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan WingetBudget = TimeSpan.FromMinutes(20);

        /// <summary>Human-readable state of the last bootstrap attempt, for tool results and logs.</summary>
        public string LastStatus { get; private set; } = "no bootstrap attempted yet";
        /// <summary>True while an attempt is in flight (so callers can say "in progress, retry later").</summary>
        public bool InProgress { get; private set; }
        public string LastDiagnostics { get; private set; } = "not collected";
        public DateTime? LastAttemptUtc => lastAttemptUtc == DateTime.MinValue ? null : lastAttemptUtc;

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
        public async Task<bool> EnsureDaemonAsync(Func<CancellationToken, Task<string?>> probeAsync, CancellationToken ct = default,
            string dockerUri = "npipe://./pipe/docker_engine")
        {
            if (await probeAsync(ct) == null) { Status("Docker daemon is up."); return true; }
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
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
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
                // Use Docker's own bounded restart command when available. Never kill unrelated
                // WSL workloads, unregister a distro, or infer corruption from a data distro's shell.
                string cli = Path.Combine(Path.GetDirectoryName(exe)!, "resources", "bin", "docker.exe");
                if (File.Exists(cli))
                {
                    Status("Docker engine is unavailable; attempting one Docker Desktop CLI restart.");
                    var restart = await RunProcessAsync(cli, new[] { "desktop", "restart" }, TimeSpan.FromSeconds(60), ct);
                    if (restart.ExitCode == 0)
                    {
                        deadline = DateTime.UtcNow + DaemonStartBudget;
                        while (DateTime.UtcNow < deadline)
                        {
                            if (await probeAsync(ct) == null)
                            {
                                Status("Docker daemon recovered after Desktop restart.");
                                return true;
                            }
                            await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        }
                    }
                    LastDiagnostics = $"Docker Desktop {FileVersionInfo.GetVersionInfo(exe).FileVersion}; restart: {restart.Output}";
                }
                string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe");
                if (!File.Exists(wsl)) wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
                var wslStatus = await RunProcessAsync(wsl, new[] { "--status" }, TimeSpan.FromSeconds(10), ct);
                LastDiagnostics += $"; WSL status (exit {wslStatus.ExitCode}): {wslStatus.Output}";
                Status("Docker engine remains unavailable after bounded recovery. " + LastDiagnostics +
                    " Preserve Docker data. A stopped or non-shell docker-desktop-data distro does not prove corruption; do not unregister it. Check host compatibility and Docker startup logs before choosing a repair.");
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

        private void Status(string s)
        {
            if (LastStatus == s) return;
            LastStatus = s;
            log($"Desktop bootstrap: {s}");
        }
    }
}
