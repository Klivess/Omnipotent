using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The platform's desktop subsystem: owns the orchestrator, the registry, the per-container
    /// VNC transport pool, and the shared-desktop input lock. Implements both allocation
    /// mechanics the Commander can choose between (§4) — per-agent containers and one shared
    /// project desktop with an input lock. Docker being unreachable is non-fatal: desktop
    /// operations surface a clear error while non-desktop preparation can continue.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ContainerDesktopManager
    {
        private readonly ContainerRegistry registry;
        private readonly ContainerOrchestrator orchestrator;
        private readonly InputLockCoordinator inputLock = new();
        private readonly Action<string> log;

        // One transport per container ID, lazily connected.
        private readonly ConcurrentDictionary<string, VncTransport> transports = new(StringComparer.Ordinal);
        // Captures and input must observe one coherent desktop state.  VncTransport serialises
        // socket writes, but this gate serialises the whole observe → act → settle transaction.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> actionGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> provisioningGates = new(StringComparer.Ordinal);
        // A session handoff touches two persistent Chromium profiles. Keep competing handoffs for
        // one project serial so an agent cannot copy half of A while another replaces A from B.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> browserSessionHandoffGates = new(StringComparer.Ordinal);
        // Startup, project resume, and the desktop-list route can all request reconciliation at
        // once. Docker reconciliation mutates the persisted registry, so keep it single-flight.
        private readonly SemaphoreSlim reconcileGate = new(1, 1);
        // Admission is a check-then-start; hold it across the start so two concurrent requests
        // cannot both see the last free slot. Evictions only ever TRY other owners' gates.
        private readonly SemaphoreSlim admissionGate = new(1, 1);
        // Klives watching or driving a desktop from the website counts as use.
        private readonly ConcurrentDictionary<string, DateTime> lastViewedUtc = new(StringComparer.Ordinal);
        private readonly string vncHost;
        private long daemonMemoryTotalBytes;
        private DateTime daemonMemorySampledUtc = DateTime.MinValue;
        private System.Threading.Timer? supervisionTimer;
        private int supervising;
        private volatile bool daemonAnswering = true;
        private string? lastDaemonProblem;
        private DateTime? lastSupervisionUtc;

        /// <summary>Desktops unused this long are stopped in place (0 disables). Set from host settings.</summary>
        public TimeSpan IdleSuspendAfter { get; set; } = DesktopCapacityPolicy.DefaultIdleSuspendAfter;

        public ContainerRegistry Registry => registry;
        public ContainerOrchestrator Orchestrator => orchestrator;
        public InputLockCoordinator InputLock => inputLock;
        /// <summary>Raised when desktop discoverability changes so project clients can refresh.</summary>
        public event Action<DesktopContainerRecord, string>? DesktopChanged;

        public ContainerDesktopManager(
            Action<string> log,
            Func<string, string> imageForProject,
            string dockerUri,
            string vncHost = "127.0.0.1")
        {
            this.log = log ?? (_ => { });
            this.vncHost = vncHost;
            registry = new ContainerRegistry(log);
            orchestrator = new ContainerOrchestrator(registry, log, imageForProject, dockerUri, vncHost);
            bootstrapper = new ContainerDependencyBootstrapper(this.log);
            // A browser-service rejection now degrades to docker exec instead of failing the action;
            // this is where the harness/service disagreement still gets reported.
            BrowserServiceClient.Diagnostics = this.log;
            ComputerDaemonClient.Diagnostics = this.log;
        }

        /// <summary>Boot reconciliation — reattach to surviving containers (§9 restart/redeploy).</summary>
        public async Task ReconcileAsync(CancellationToken ct = default)
        {
            await reconcileGate.WaitAsync(ct);
            try
            {
                var before = registry.All()
                    .GroupBy(r => r.ContainerID, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (g.Last().Lost, g.Last().VncHostPort), StringComparer.Ordinal);
                await orchestrator.ReconcileAsync(ct);
                // Docker may assign a new ephemeral host port when a stopped container restarts.
                // Never leave the pooled VNC client dialing its persisted, now-stale endpoint.
                var records = registry.All()
                    .GroupBy(r => r.ContainerID, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
                foreach (var pair in transports.ToArray())
                {
                    if (!records.TryGetValue(pair.Key, out var record) || record.Lost || pair.Value.Port != record.VncHostPort)
                        if (transports.TryRemove(new KeyValuePair<string, VncTransport>(pair.Key, pair.Value))) pair.Value.Dispose();
                }
                foreach (var record in records.Values)
                {
                    if (!before.TryGetValue(record.ContainerID, out var prior))
                        NotifyDesktopChanged(record, "discovered");
                    else if (prior.Lost != record.Lost || prior.VncHostPort != record.VncHostPort)
                        NotifyDesktopChanged(record, record.Lost ? "unavailable" : "restored");
                }
            }
            finally { reconcileGate.Release(); }
        }

        /// <summary>Probes the Docker daemon; null when healthy, else a human-readable reason.</summary>
        public Task<string?> ProbeDaemonAsync(CancellationToken ct = default) => orchestrator.ProbeDaemonAsync(ct);

        private readonly ContainerDependencyBootstrapper bootstrapper;

        public async Task<object> GetHostHealthAsync(CancellationToken ct = default)
        {
            string? problem = await ProbeDaemonAsync(ct);
            long memory = problem == null ? await GetDaemonMemoryTotalAsync(ct) : Interlocked.Read(ref daemonMemoryTotalBytes);
            var live = registry.All().Where(r => !r.Lost).ToList();
            return new
            {
                endpoint = orchestrator.DockerUri,
                daemonProblem = problem,
                recoveryInProgress = bootstrapper.InProgress,
                recoveryStatus = bootstrapper.LastStatus,
                restartRequired = bootstrapper.RestartRequired,
                lastAttemptUtc = bootstrapper.LastAttemptUtc,
                diagnostics = bootstrapper.LastDiagnostics,
                lastSupervisionUtc,
                capacity = new
                {
                    daemonMemoryGB = Math.Round(memory / (1024.0 * 1024 * 1024), 1),
                    desktopMemoryLimitGB = Math.Round(DesktopCapacityPolicy.DesktopMemoryLimitBytes / (1024.0 * 1024 * 1024), 2),
                    maxRunningDesktops = DesktopCapacityPolicy.MaxRunningDesktops(memory),
                    running = live.Count(r => !r.Suspended),
                    stopped = live.Count(r => r.Suspended),
                    idleSuspendMinutes = IdleSuspendAfter.TotalMinutes,
                },
                computers = registry.All().Select(r => new
                {
                    r.ContainerID, r.ProjectID, r.AgentID, r.Lost, r.Suspended, r.LastUsedAt,
                }).ToArray(),
            };
        }

        private async Task<long> GetDaemonMemoryTotalAsync(CancellationToken ct)
        {
            long cached = Interlocked.Read(ref daemonMemoryTotalBytes);
            if (cached > 0 && DateTime.UtcNow - daemonMemorySampledUtc < TimeSpan.FromMinutes(10)) return cached;
            long total = await orchestrator.GetDaemonMemoryTotalAsync(ct);
            if (total > 0)
            {
                Interlocked.Exchange(ref daemonMemoryTotalBytes, total);
                daemonMemorySampledUtc = DateTime.UtcNow;
                return total;
            }
            return cached;
        }

        /// <summary>
        /// Continuous supervision. Recovery used to run only when an agent's tool happened to fail
        /// or at Omnipotent start, so a daemon that died overnight stayed dead until something
        /// tripped over it — and nothing ever stopped an idle desktop. Every minute this probes the
        /// daemon (walking the bounded recovery ladder when it is down, then reattaching desktops
        /// when it returns) and stops desktops that have been idle long enough to release their RAM.
        /// </summary>
        public void StartSupervision(TimeSpan? firstRun = null)
        {
            supervisionTimer ??= new System.Threading.Timer(_ => _ = SuperviseOnceAsync(), null,
                firstRun ?? TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
        }

        internal async Task SuperviseOnceAsync()
        {
            if (Interlocked.Exchange(ref supervising, 1) == 1) return; // a slow tick is still running
            try
            {
                lastSupervisionUtc = DateTime.UtcNow;
                // Generous: a recovery attempt includes Docker Desktop's multi-minute cold start.
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                string? problem = await ProbeDaemonAsync(cts.Token);
                if (problem != null)
                {
                    if (daemonAnswering || problem != lastDaemonProblem)
                        log($"Desktop supervisor: Docker daemon unavailable — {problem}");
                    daemonAnswering = false;
                    lastDaemonProblem = problem;
                    string? stillBroken = await TryBootstrapAsync(ProjectSettings.Defaults.DesktopImage, cts.Token);
                    if (stillBroken != null) return;
                }
                if (!daemonAnswering)
                {
                    daemonAnswering = true;
                    lastDaemonProblem = null;
                    log("Desktop supervisor: Docker daemon answering again — reattaching desktops.");
                    await ReconcileAsync(cts.Token);
                }
                await GetDaemonMemoryTotalAsync(cts.Token);
                await SuspendIdleDesktopsAsync(cts.Token);
            }
            catch (Exception ex) { log($"Desktop supervisor tick failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref supervising, 0); }
        }

        /// <summary>Stops, in place, every desktop idle past <see cref="IdleSuspendAfter"/> that is
        /// not in use (no action in flight, no input lease, not being watched, CPU quiet).</summary>
        internal async Task<int> SuspendIdleDesktopsAsync(CancellationToken ct)
        {
            if (IdleSuspendAfter <= TimeSpan.Zero) return 0;
            int stopped = 0;
            var now = DateTime.UtcNow;
            foreach (var record in registry.All().Where(r => DesktopCapacityPolicy.IsIdle(r, now, IdleSuspendAfter)).ToList())
            {
                if (IsPinned(record)) continue;
                double? cpu = await orchestrator.SampleCpuPercentAsync(record.ContainerID, ct);
                if (cpu == null || cpu >= DesktopCapacityPolicy.BusyCpuPercent) continue; // working, or unknown
                try
                {
                    if (await SuspendDesktopAsync(record.ContainerID, ct))
                    {
                        stopped++;
                        log($"Desktop {Short(record.ContainerID)} ({record.ProjectID}/{record.AgentID ?? "shared"}) stopped after {(now - record.LastUsedAt).TotalMinutes:0} idle minutes to release its memory; it resumes on next use.");
                    }
                }
                catch (Exception ex) { log($"Idle desktop {Short(record.ContainerID)} could not be stopped: {ex.Message}"); }
            }
            return stopped;
        }

        /// <summary>Records that Klives is watching or driving a desktop, so it is never stopped under him.</summary>
        public void MarkViewed(string containerID)
        {
            if (!string.IsNullOrEmpty(containerID)) lastViewedUtc[containerID] = DateTime.UtcNow;
        }

        private bool IsPinned(DesktopContainerRecord record)
        {
            if (actionGates.TryGetValue(record.ContainerID, out var gate) && gate.CurrentCount == 0) return true;
            if (inputLock.CurrentHolder(record.ContainerID) != null) return true;
            if (lastViewedUtc.TryGetValue(record.ContainerID, out var viewed) && DateTime.UtcNow - viewed < TimeSpan.FromMinutes(5)) return true;
            string ownerKey = $"{record.ProjectID}/{record.AgentID ?? "shared"}";
            return provisioningGates.TryGetValue(ownerKey, out var provisioning) && provisioning.CurrentCount == 0;
        }

        /// <summary>
        /// Makes room for one more running desktop, stopping least-recently-used idle desktops if the
        /// fleet's memory ceilings would otherwise exceed Docker's VM. Throws
        /// <see cref="DesktopCapacityException"/> rather than overcommit: starting anyway is what
        /// turned a busy host into an unreachable daemon for every project. Caller holds
        /// <see cref="admissionGate"/>.
        /// </summary>
        private async Task AdmitLockedAsync(string? requesterContainerID, CancellationToken ct)
        {
            long memory = await GetDaemonMemoryTotalAsync(ct);
            // A daemon that answers but cannot report its size right now is not evidence the fleet
            // is full; evicting on missing data would stop every other desktop.
            if (memory <= 0) return;
            int slots = DesktopCapacityPolicy.MaxRunningDesktops(memory);
            var busy = new HashSet<string>(StringComparer.Ordinal);
            for (int round = 0; round < 4; round++)
            {
                var evictions = DesktopCapacityPolicy.ChooseEvictions(registry.All(), slots, requesterContainerID,
                    DateTime.UtcNow, r => busy.Contains(r.ContainerID) || IsPinned(r));
                if (evictions == null) break;
                if (evictions.Count == 0) return;
                bool allStopped = true;
                foreach (var victim in evictions)
                {
                    double? cpu = await orchestrator.SampleCpuPercentAsync(victim.ContainerID, ct);
                    if (cpu == null || cpu >= DesktopCapacityPolicy.BusyCpuPercent
                        || !await TrySuspendForEvictionAsync(victim, ct))
                    {
                        busy.Add(victim.ContainerID);
                        allStopped = false;
                        continue;
                    }
                    log($"Desktop {Short(victim.ContainerID)} ({victim.ProjectID}/{victim.AgentID ?? "shared"}) stopped to make room — {slots} desktop slot(s) on a {memory / (1024.0 * 1024 * 1024):0.#} GB Docker VM.");
                }
                if (allStopped) return;
            }

            var holders = registry.All().Where(r => !r.Lost && !r.Suspended && r.ContainerID != requesterContainerID)
                .Select(r => $"{r.ProjectID[..Math.Min(8, r.ProjectID.Length)]}/{r.AgentID ?? "shared"}").ToList();
            throw new DesktopCapacityException(
                $"All {slots} computer slot(s) on this host are in active use ({string.Join(", ", holders)}). " +
                "Your computer was not started, so the host stays stable for everyone; its files and apps are intact. " +
                $"Retry in a few minutes — idle computers are stopped automatically after {IdleSuspendAfter.TotalMinutes:0} minutes. " +
                "Meanwhile continue with non-desktop work.");
        }

        private async Task<bool> TrySuspendForEvictionAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            string ownerKey = $"{record.ProjectID}/{record.AgentID ?? "shared"}";
            var provisionGate = provisioningGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
            if (!await provisionGate.WaitAsync(0, ct)) return false; // never wait on another owner: no lock cycles
            try
            {
                var actionGate = actionGates.GetOrAdd(record.ContainerID, _ => new SemaphoreSlim(1, 1));
                if (!await actionGate.WaitAsync(0, ct)) return false;
                try
                {
                    if (record.Suspended) return true;
                    await orchestrator.SuspendContainerAsync(record, ct);
                    if (transports.TryRemove(record.ContainerID, out var transport)) transport.Dispose();
                }
                finally { actionGate.Release(); }
            }
            catch (Exception ex)
            {
                log($"Desktop {Short(record.ContainerID)} could not be stopped to make room: {ex.Message}");
                return false;
            }
            finally { provisionGate.Release(); }
            NotifyDesktopChanged(record, "suspended");
            return true;
        }

        private static string Short(string containerID) => containerID[..Math.Min(12, containerID.Length)];

        /// <summary>One-line remedy shown to the agent/logs when the desktop layer is unusable.</summary>
        public string SetupHint =>
            $"Desktop control needs Docker running on the host ({orchestrator.DockerUri}) and the desktop image built. " +
            $"Auto-setup state: {bootstrapper.LastStatus} " +
            "Website work must wait for this repair; scripts may continue only for non-browser preparation or diagnostics.";

        /// <summary>
        /// Self-heals the desktop layer's host dependencies: installs/starts Docker (winget →
        /// launch → wait for daemon) and auto-builds the default desktop image from the shipped
        /// build context. Safe to fire-and-forget from tool failures — the bootstrapper is
        /// single-flight with a cooldown. Returns null when the layer is ready, else the
        /// current human-readable status.
        /// </summary>
        public async Task<string?> TryBootstrapAsync(string imageTag, CancellationToken ct = default)
        {
            bool daemonUp = await bootstrapper.EnsureDaemonAsync(orchestrator.ProbeDaemonAsync, ct, orchestrator.DockerUri);
            if (!daemonUp) return bootstrapper.LastStatus;

            // Only the built-in tag is owned by the shipped Docker context. A project can point at
            // a custom image, but self-healing must never overwrite that tag with our base image.
            if (!string.Equals(imageTag, ProjectSettings.Defaults.DesktopImage, StringComparison.OrdinalIgnoreCase))
                return await orchestrator.ImageExistsAsync(imageTag, ct)
                    ? null
                    : $"configured custom desktop image '{imageTag}' is not present on this host; custom images are not auto-built from the shipped base context.";

            string contextDir = ResolveBuildContextDirectory();
            string? imageProblem = await orchestrator.EnsureImageBuiltAsync(imageTag, contextDir, "desktop.Dockerfile", ct);
            return imageProblem; // null = fully ready
        }

        /// <summary>Finds the shipped context in a published build and, for developer/test runs,
        /// in the source tree. A candidate is accepted only when every Docker COPY dependency is
        /// present, preventing a partial output folder from shadowing the complete source context.</summary>
        internal static string ResolveBuildContextDirectory()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "Services", "Projects", "Containers"),
                Path.Combine(Environment.CurrentDirectory, "Services", "Projects", "Containers"),
                Path.Combine(Environment.CurrentDirectory, "Omnipotent", "Services", "Projects", "Containers"),
            };
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "Services", "Projects", "Containers"));
                candidates.Add(Path.Combine(dir.FullName, "Omnipotent", "Services", "Projects", "Containers"));
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(candidate =>
                       ContainerOrchestrator.DesktopBuildContextFiles.All(name => File.Exists(Path.Combine(candidate, name))))
                   ?? candidates[0];
        }

        // What a desktop MUST have before visual work is worth attempting: a display, a window manager
        // (focus, maximise, wmctrl), a frame the agent can see and a route for its input — VNC or the
        // in-container daemon, whichever answers. The XFCE panel and session make the desktop pleasant
        // for a human, but an agent can work without them, and the entrypoint restarts them within
        // seconds; failing readiness over them used to restart the whole container, killing the
        // browser and whatever the agent was halfway through.
        private static readonly string[] RequiredCapabilities = { "display", "window-manager", "frame", "input" };

        /// <summary>Components reported when down but never a reason to fail readiness or restart.</summary>
        private static readonly string[] AdvisoryCapabilities = { "desktop-shell", "panel" };

        // Chromium being installed only proves the image contains a binary. Browser work also
        // requires the visible process, its local CDP endpoint, and a tab the inspection helper
        // can actually query. Without this live probe, a detached launcher failure was reported
        // to agents as the misleading "Browser opened." success.
        private static readonly string[] RequiredBrowserCapabilities =
            { "browser-process", "browser-cdp", "browser-tabs" };

        // One shell probe of the baked stack. `set +e` so a missing tool yields "no", not a
        // non-zero exit that would mask the other answers.
        private const string ReadinessProbeScript =
            "set +e\n" +
            "echo \"display=$(xdpyinfo -display :1 >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"desktop-shell=$(pgrep -x xfce4-session >/dev/null 2>&1 && pgrep -x xfdesktop >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"panel=$(pgrep -x xfce4-panel >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"window-manager=$(DISPLAY=:1 wmctrl -m >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"xvfb=$(pgrep -x Xvfb >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"x11vnc=$(pgrep -x x11vnc >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"chromium=$(command -v chromium >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"firefox=$(command -v firefox >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"browser-inspect=$([ -f /usr/local/bin/browser-inspect.py ] && echo yes || echo no)\"\n" +
            "echo \"ffmpeg=$(command -v ffmpeg >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"image-version=$(sed -n 's/.*\\\"imageVersion\\\":\\\"\\([^\\\"]*\\)\\\".*/\\1/p' /etc/klive-desktop.json 2>/dev/null)\"\n";

        private const string BrowserReadinessProbeScript =
            "set +e\n" +
            "echo \"browser-process=$(pgrep -x chromium >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"browser-cdp=$(python3 -c 'import urllib.request; urllib.request.urlopen(\\\"http://127.0.0.1:9222/json/version\\\", timeout=2).read(); print(\\\"up\\\")' 2>/dev/null || echo down)\"\n" +
            "echo \"browser-tabs=$(python3 /usr/local/bin/browser-inspect.py tabs 1 0 >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "printf 'chromium-log='\n" +
            "tail -n 12 /tmp/chromium.log 2>/dev/null | tr '\\n' ' ' | cut -c 1-1200\n";

        /// <summary>
        /// Preflight a project's human-usable desktop: self-heal Docker and provision new computers, then
        /// probe the visible shell and framebuffer. Browser CDP is an optional inspection aid and
        /// is deliberately not part of this gate. The result's facts
        /// are recorded by the caller so later wakes start from known-good state instead of
        /// re-deriving the environment.
        /// </summary>
        public async Task<DesktopReadiness> EnsureDesktopReadyAsync(
            Project project, string agentID, string imageTag, CancellationToken ct = default)
        {
            string? owner = project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock ? null : agentID;
            bool hasComputer = registry.ForProject(project.ProjectID).Any(r => r.AgentID == owner && !r.Lost);
            // An existing computer must remain usable even when building a new base image fails.
            string? bootstrap = hasComputer
                ? (await bootstrapper.EnsureDaemonAsync(orchestrator.ProbeDaemonAsync, ct, orchestrator.DockerUri)
                    ? null : bootstrapper.LastStatus)
                : await TryBootstrapAsync(imageTag, ct);
            if (bootstrap != null)
                return new DesktopReadiness { Ok = false, Summary = $"Desktop not ready — {bootstrap}" };

            DesktopContainerRecord record;
            try { record = await EnsureDesktopAsync(project, agentID, requireVisualReady: true, ct); }
            catch (Exception ex)
            {
                return new DesktopReadiness { Ok = false, Summary = $"Desktop provisioning failed: {ex.Message}" };
            }

            var readiness = await ProbeReadinessAsync(record, ct);
            if (readiness.Ok) return readiness;

            // Repair ladder, cheapest and least destructive first. A container restart kills the
            // browser and everything the agent had open, so it is the last rung, not the first.
            //   1. Wait one supervision cycle: the entrypoint restarts a dead window manager, panel or
            //      session within five seconds, and a busy host can simply be slow to answer.
            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            readiness = await ProbeReadinessAsync(record, ct);
            if (readiness.Ok)
                return WithSummaryPrefix(readiness, "Desktop ready after a transient fault cleared on its own. ");

            //   2. Repair components in place through the daemon (no Docker on the path, no restart).
            string? repairedInPlace = await RepairComponentsInPlaceAsync(record, readiness, ct);
            if (repairedInPlace != null)
            {
                readiness = await ProbeReadinessAsync(record, ct);
                if (readiness.Ok)
                    return WithSummaryPrefix(readiness, $"Desktop ready after in-place repair ({repairedInPlace}). ");
            }

            //   3. Only a desktop whose display is gone, or that nothing can reach, is restarted.
            if (!NeedsContainerRestart(readiness.Capabilities))
                return readiness;

            // Repair the existing machine. Replacing it destroys apt installs and home files;
            // a temporary VNC or daemon fault is never evidence that those files are disposable.
            log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} failed readiness; restarting its existing container.");

            if (project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock &&
                inputLock.CurrentHolder(record.ContainerID) is { } holder && holder != agentID)
                return new DesktopReadiness
                {
                    Ok = false,
                    ContainerID = record.ContainerID,
                    ImageVersion = readiness.ImageVersion,
                    Capabilities = readiness.Capabilities,
                    Summary = readiness.Summary + $" Repair is deferred while agent {holder} controls the shared desktop.",
                };

            await RestartDesktopAsync(record, agentID, ct);
            var repaired = await ProbeReadinessAsync(record, ct);
            if (!repaired.Ok) return repaired;
            return new DesktopReadiness
            {
                Ok = true,
                ContainerID = repaired.ContainerID,
                ImageVersion = repaired.ImageVersion,
                Capabilities = repaired.Capabilities,
                Summary = "Desktop ready after automatic shell/VNC repair. " + repaired.Summary,
            };
        }

        private static DesktopReadiness WithSummaryPrefix(DesktopReadiness readiness, string prefix) => new()
        {
            Ok = readiness.Ok,
            ContainerID = readiness.ContainerID,
            ImageVersion = readiness.ImageVersion,
            Capabilities = readiness.Capabilities,
            Summary = prefix + readiness.Summary,
        };

        /// <summary>
        /// A restart is warranted only when the display server itself is gone, or when neither the VNC
        /// route nor the daemon can reach the desktop at all. Anything narrower is repaired in place.
        /// </summary>
        internal static bool NeedsContainerRestart(IReadOnlyDictionary<string, string> capabilities)
        {
            bool daemonUp = string.Equals(capabilities.GetValueOrDefault("computerd"), "up", StringComparison.Ordinal);
            bool displayUp = string.Equals(capabilities.GetValueOrDefault("display"), "up", StringComparison.Ordinal);
            bool vncUp = string.Equals(capabilities.GetValueOrDefault("vnc"), "up", StringComparison.Ordinal);
            if (!daemonUp && !vncUp) return true;          // unreachable by every route
            if (!displayUp && capabilities.ContainsKey("display")) return true;  // the display server died
            return false;
        }

        /// <summary>
        /// Restarts missing desktop components inside the container through the daemon: the window
        /// manager (focus and maximise depend on it) and, when it is gone, the shell pieces. Returns what
        /// was done, or null when there was nothing to do or no daemon to do it with.
        /// </summary>
        private async Task<string?> RepairComponentsInPlaceAsync(DesktopContainerRecord record, DesktopReadiness readiness,
            CancellationToken ct)
        {
            var caps = readiness.Capabilities;
            if (!string.Equals(caps.GetValueOrDefault("computerd"), "up", StringComparison.Ordinal)) return null;
            var steps = new List<string>();
            var script = new System.Text.StringBuilder("set +e\nexport DISPLAY=:1\nif [ -r /tmp/desktop-session.env ]; then . /tmp/desktop-session.env; fi\n");
            if (!CapabilityIsReady("window-manager", caps))
            {
                script.Append("pgrep -x xfwm4 >/dev/null 2>&1 || (nohup xfwm4 --compositor=off >>/tmp/xfwm4.log 2>&1 </dev/null &)\n");
                steps.Add("window manager restarted");
            }
            if (!CapabilityIsReady("panel", caps))
            {
                script.Append("pgrep -x xfce4-panel >/dev/null 2>&1 || (nohup xfce4-panel >>/tmp/xfce4-panel.log 2>&1 </dev/null &)\n");
                steps.Add("panel restarted");
            }
            if (steps.Count == 0) return null;
            script.Append("sleep 2\n");
            try
            {
                var result = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, script.ToString(), "/home/agent", 20, idempotent: true, ct);
                log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} in-place repair: {string.Join(", ", steps)} (exit {result.ExitCode}).");
                return string.Join(", ", steps);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} in-place repair failed: {ex.Message}");
                return null;
            }
        }

        private async Task<DesktopReadiness> EnsureBrowserReadyAsync(
            DesktopContainerRecord record, DesktopReadiness desktopReadiness, CancellationToken ct)
        {
            try { await orchestrator.ExecuteDesktopControlAsync(record.ContainerID, ContainerDesktopControlCommand.LaunchBrowser, null, ct); }
            catch (Exception ex)
            {
                return new DesktopReadiness
                {
                    Ok = false, ContainerID = record.ContainerID, ImageVersion = desktopReadiness.ImageVersion,
                    Capabilities = desktopReadiness.Capabilities,
                    Summary = desktopReadiness.Summary + $" Browser launch command failed: {ex.Message}",
                };
            }

            Dictionary<string, string> caps = new(desktopReadiness.Capabilities, StringComparer.Ordinal);
            string lastLog = "";
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                try
                {
                    var probe = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID,
                        BrowserReadinessProbeScript, "/home/agent", 10, ct);
                    if (probe.Success)
                    {
                        foreach (var pair in ParseCapabilities(probe.Stdout)) caps[pair.Key] = pair.Value;
                        caps.TryGetValue("chromium-log", out lastLog);
                        if (BrowserControlIsReady(caps))
                        {
                            return new DesktopReadiness
                            {
                                Ok = true, ContainerID = record.ContainerID, ImageVersion = desktopReadiness.ImageVersion,
                                Capabilities = caps,
                                Summary = desktopReadiness.Summary + " Browser control ready (process up, CDP up, inspectable tab up).",
                            };
                        }
                    }
                    else lastLog = probe.Format(1200);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { lastLog = ex.Message; }
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }

            string browserState = string.Join(", ", RequiredBrowserCapabilities.Select(cap =>
                $"{cap} {caps.GetValueOrDefault(cap, "unknown")}"));
            string logTail = string.IsNullOrWhiteSpace(lastLog) ? "(no Chromium log output)" : TruncateOneLine(lastLog, 1200);
            return new DesktopReadiness
            {
                Ok = false, ContainerID = record.ContainerID, ImageVersion = desktopReadiness.ImageVersion,
                Capabilities = caps,
                Summary = desktopReadiness.Summary +
                    $" Browser failed live readiness after launch: {browserState}. Chromium log: {logTail}",
            };
        }

        private async Task<string?> ArchiveBrokenBrowserProfileAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            const string recoveryScript =
                "set +e\n" +
                "profile=\"${OMNIPOTENT_BROWSER_PROFILE:-}\"\n" +
                "if [ -z \"$profile\" ]; then echo 'browser-profile-recovery=skipped:no-profile-path'; exit 0; fi\n" +
                "pkill -x chromium >/dev/null 2>&1 || true\n" +
                "sleep 1\n" +
                "stamp=$(date -u +%Y%m%dT%H%M%SZ)\n" +
                "if [ -e \"$profile\" ]; then mv \"$profile\" \"${profile}.recovery-${stamp}\" || exit 1; echo \"browser-profile-recovery=archived:${profile}.recovery-${stamp}\"; else echo 'browser-profile-recovery=created-empty'; fi\n" +
                "mkdir -p \"$profile\"\n";
            try
            {
                var result = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, recoveryScript, "/home/agent", 20, ct);
                if (!result.Success)
                {
                    log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} browser-profile recovery failed: {result.Format(1200)}");
                    return null;
                }
                string summary = TruncateOneLine(result.Stdout, 1200);
                log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} {summary}");
                return summary;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} browser-profile recovery failed: {ex.Message}");
                return null;
            }
        }

        private async Task<DesktopReadiness> ProbeReadinessAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            // The daemon answers from inside the container over HTTP; only a desktop without one (or
            // whose daemon is down) still pays for a docker exec probe.
            var daemon = orchestrator.DaemonFor(record.ContainerID);
            var health = await daemon.HealthAsync(ct, fresh: true);
            Dictionary<string, string> caps;
            if (health is { IsControlPlane: true })
            {
                caps = CapabilitiesFromHealth(health);
            }
            else
            {
                ContainerShellResult probe;
                try { probe = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, ReadinessProbeScript, "/home/agent", 30, idempotent: true, ct); }
                catch (Exception ex)
                {
                    return new DesktopReadiness { Ok = false, ContainerID = record.ContainerID,
                        Summary = $"Desktop is up but the readiness probe failed: {ex.Message}" };
                }
                if (!probe.Success)
                    return new DesktopReadiness { Ok = false, ContainerID = record.ContainerID,
                        Summary = "Desktop readiness probe returned non-zero: " + probe.Format(2000) };
                caps = ParseCapabilities(probe.Stdout);
                caps["computerd"] = health == null ? "down" : "outdated";
            }

            try
            {
                var frame = await GetTransport(record).CaptureFrameAsync(ct);
                caps["vnc"] = "up";
                caps["frame"] = IsCompleteFrame(frame.bgra, frame.width, frame.height) ? "usable" : "incomplete";
                caps["frame-size"] = $"{frame.width}x{frame.height}";
                caps["frame-route"] = "vnc";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                caps["vnc"] = "down";
                caps["frame"] = "unavailable";
                caps["vnc-error"] = ex.Message;
            }

            // A wedged x11vnc is not a broken desktop when the daemon can see and drive the same display.
            bool daemonInput = health is { IsControlPlane: true, AuthConfigured: true } && health.Has("input") && health.Tools.GetValueOrDefault("xdotool");
            if (caps["frame"] != "usable" && health is { IsControlPlane: true, AuthConfigured: true } && health.Has("screenshot"))
            {
                var frame = await daemon.CaptureBgraAsync(ct);
                if (frame is { } captured && IsCompleteFrame(captured.Bgra, captured.Width, captured.Height))
                {
                    caps["frame"] = "usable";
                    caps["frame-size"] = $"{captured.Width}x{captured.Height}";
                    caps["frame-route"] = "computerd";
                }
            }
            caps["input"] = caps["vnc"] == "up" || daemonInput ? "up" : "down";
            caps["input-route"] = caps["vnc"] == "up" ? "vnc" : daemonInput ? "computerd" : "none";

            caps.TryGetValue("image-version", out var imageVersion);
            var missing = RequiredCapabilities.Where(c => !CapabilityIsReady(c, caps)).ToList();
            var degraded = DegradedCapabilities(caps);
            string capsText = string.Join(", ", new[]
            {
                $"display {caps.GetValueOrDefault("display", "?")}",
                $"desktop-shell {caps.GetValueOrDefault("desktop-shell", "?")}",
                $"panel {caps.GetValueOrDefault("panel", "?")}",
                $"window-manager {caps.GetValueOrDefault("window-manager", "?")}",
                $"vnc {caps.GetValueOrDefault("vnc", "?")}",
                $"frame {caps.GetValueOrDefault("frame", "?")} {caps.GetValueOrDefault("frame-size", "")}".TrimEnd(),
                $"chromium {caps.GetValueOrDefault("chromium", "?")}",
                $"firefox {caps.GetValueOrDefault("firefox", "?")}",
                $"browser-inspect {caps.GetValueOrDefault("browser-inspect", "?")}",
                $"ffmpeg {caps.GetValueOrDefault("ffmpeg", "?")}",
            });
            bool ok = DesktopControlIsReady(caps);
            string routes = $" Input via {caps.GetValueOrDefault("input-route", "vnc")}, frames via {caps.GetValueOrDefault("frame-route", "vnc")}, " +
                            $"control daemon {caps.GetValueOrDefault("computerd", "down")}.";
            string advisory = degraded.Count == 0 ? "" : $" Cosmetic components down (self-restarting, not blocking): {string.Join(", ", degraded)}.";
            return new DesktopReadiness
            {
                Ok = ok,
                ContainerID = record.ContainerID,
                ImageVersion = imageVersion ?? "",
                Capabilities = caps,
                Summary = ok
                    ? $"Desktop ready (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}.{routes}{advisory}"
                    : $"Desktop degraded (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}. Missing: {string.Join(", ", missing)}.{routes}",
            };
        }

        internal static bool BrowserControlIsReady(IReadOnlyDictionary<string, string> capabilities) =>
            RequiredBrowserCapabilities.All(cap => string.Equals(capabilities.GetValueOrDefault(cap), "up", StringComparison.Ordinal));

        internal static bool DesktopControlIsReady(IReadOnlyDictionary<string, string> capabilities) =>
            RequiredCapabilities.All(cap => CapabilityIsReady(cap, capabilities));

        /// <summary>Advisory components that are down, for the readiness summary.</summary>
        internal static IReadOnlyList<string> DegradedCapabilities(IReadOnlyDictionary<string, string> capabilities) =>
            AdvisoryCapabilities.Where(cap => capabilities.ContainsKey(cap) && !CapabilityIsReady(cap, capabilities)).ToList();

        private static bool CapabilityIsReady(string capability, IReadOnlyDictionary<string, string> capabilities) =>
            capability switch
            {
                // Input is served by VNC, or by the daemon when VNC is down. A probe that predates the
                // daemon only reports "vnc", which is the input route it implies.
                "input" => string.Equals(capabilities.GetValueOrDefault("input"), "up", StringComparison.Ordinal)
                           || (!capabilities.ContainsKey("input")
                               && string.Equals(capabilities.GetValueOrDefault("vnc"), "up", StringComparison.Ordinal)),
                "display" or "desktop-shell" or "panel" or "window-manager" or "vnc" =>
                    string.Equals(capabilities.GetValueOrDefault(capability), "up", StringComparison.Ordinal),
                "frame" => string.Equals(capabilities.GetValueOrDefault(capability), "usable", StringComparison.Ordinal),
                _ => string.Equals(capabilities.GetValueOrDefault(capability), "yes", StringComparison.Ordinal),
            };

        /// <summary>The exec probe's vocabulary, filled from the daemon's /health instead of a docker exec.</summary>
        internal static Dictionary<string, string> CapabilitiesFromHealth(ComputerDaemonHealth health)
        {
            string Up(bool value) => value ? "up" : "down";
            var image = health.Image?.Capabilities ?? new List<string>();
            var caps = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["display"] = Up(health.Component("display") || health.Component("xvfb")),
                ["desktop-shell"] = Up(health.Component("session") && health.Component("desktop")),
                ["panel"] = Up(health.Component("panel")),
                ["window-manager"] = Up(health.Component("windowManager")),
                ["xvfb"] = Up(health.Component("xvfb")),
                ["x11vnc"] = Up(health.Component("x11vnc")),
                ["chromium"] = image.Count == 0 || image.Contains("chromium") ? "yes" : "no",
                ["firefox"] = image.Count == 0 || image.Contains("firefox") ? "yes" : "no",
                ["browser-inspect"] = "yes",
                ["ffmpeg"] = health.Tools.TryGetValue("ffmpeg", out bool ffmpeg) && ffmpeg ? "yes" : "no",
                ["image-version"] = health.Image?.Version ?? "",
                ["computerd"] = "up",
                ["computerd-protocol"] = health.Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["browser-process"] = Up(health.Browser?.Process == true),
                ["browser-cdp"] = Up(health.Browser?.Cdp == true),
            };
            if (health.Resources?.Memory is { } memory)
            {
                if (memory.UsedMB is { } used && memory.LimitMB is { } limit)
                    caps["memory"] = $"{used:0}/{limit:0} MB";
                if (memory.OomKills is { } kills) caps["oom-kills"] = kills.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return caps;
        }

        private static bool BrowserFailedToStart(IReadOnlyDictionary<string, string> capabilities) =>
            !string.Equals(capabilities.GetValueOrDefault("browser-process"), "up", StringComparison.Ordinal);

        private static Dictionary<string, string> ParseCapabilities(string? stdout)
        {
            var caps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) caps[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return caps;
        }

        private static string TruncateOneLine(string text, int max)
        {
            string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
        }

        /// <summary>
        /// Ensures a desktop exists for the given agent under the project's allocation mode and
        /// returns a tool adapter bound to it. PerAgentContainers → a container per agent;
        /// SharedDesktopWithInputLock → one project desktop all agents share behind the lock.
        /// </summary>
        public async Task<ContainerToolAdapter> GetAdapterForAgentAsync(
            Project project, string agentID,
            Func<string, Task<string>>? resolveSecretsAsync = null,
            int actionSettleMs = 350, int typingDelayMs = 18,
            bool requireVisualReady = true,
            CancellationToken ct = default)
        {
            var record = await EnsureDesktopAsync(project, agentID, requireVisualReady, ct);
            await EnsureHelpersCurrentAsync(record, ct);
            var transport = GetTransport(record);
            bool shared = project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock;
            return new ContainerToolAdapter(
                transport, record.ContainerID, agentID,
                actionGates.GetOrAdd(record.ContainerID, _ => new SemaphoreSlim(1, 1)),
                inputLock: shared ? inputLock : null,
                dockerControlAsync: (command, argument, token) => orchestrator.ExecuteDesktopControlAsync(record.ContainerID, command, argument, token),
                terminalAsync: (command, workingDirectory, timeoutSeconds, token) =>
                    orchestrator.ExecuteDesktopShellAsync(record.ContainerID, command, workingDirectory, timeoutSeconds, token),
                resolveSecretsAsync: resolveSecretsAsync,
                takeBrowserSessionAsync: (sourceAgentID, token) =>
                    TakeBrowserSessionAsync(project, agentID, sourceAgentID, token),
                actionSettleMs: actionSettleMs,
                typingDelayMs: typingDelayMs,
                // 0 on a container created before the helper service existed; the adapter then falls
                // back to docker exec, so an old desktop keeps working without being recreated.
                browserServiceHostPort: record.BrowserServiceHostPort,
                browserServiceHost: vncHost,
                desktopWidth: record.Width,
                desktopHeight: record.Height);
        }

        /// <summary>The control daemon of a desktop, for health pages and repair actions.</summary>
        public ComputerDaemonClient DaemonFor(string containerID) => orchestrator.DaemonFor(containerID);

        /// <summary>
        /// Every computer (optionally one project's) with what its own daemon reports — the data behind
        /// the website's computer pages. Probes run in parallel, each bounded, and a sleeping or missing
        /// computer is never woken to be described.
        /// </summary>
        public async Task<List<object>> DescribeComputersAsync(string? projectID, CancellationToken ct = default)
        {
            var records = (projectID == null ? registry.All() : registry.ForProject(projectID))
                .GroupBy(r => r.ContainerID, StringComparer.Ordinal).Select(g => g.Last()).ToList();
            var described = await Task.WhenAll(records.Select(async record =>
            {
                ComputerDaemonHealth? health = null;
                if (!record.Lost && !record.Suspended)
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(5));
                    try { health = await DaemonFor(record.ContainerID).HealthAsync(budget.Token); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { health = null; }
                }
                bool vnc = transports.TryGetValue(record.ContainerID, out var transport) && transport.Connected;
                string state = record.Lost ? "missing" : record.Suspended ? "asleep"
                    : health == null ? (vnc ? "running" : "unreachable")
                    : health.Component("display") || health.Component("xvfb") ? "running" : "degraded";
                var memory = health?.Resources?.Memory;
                return (object)new
                {
                    record.ContainerID,
                    record.ProjectID,
                    AgentID = record.AgentID ?? "shared",
                    record.Width,
                    record.Height,
                    record.CreatedAt,
                    record.LastUsedAt,
                    state,
                    vncConnected = vnc,
                    actionInFlight = actionGates.TryGetValue(record.ContainerID, out var gate) && gate.CurrentCount == 0,
                    controlledBy = inputLock.CurrentHolder(record.ContainerID),
                    daemon = health == null ? null : new
                    {
                        health.Protocol,
                        health.UptimeSeconds,
                        health.AuthConfigured,
                        imageVersion = health.Image?.Version,
                        health.Components,
                        browser = new
                        {
                            up = health.BrowserUp,
                            process = health.Browser?.Process ?? false,
                            tabs = health.Browser?.Tabs,
                            processes = health.Browser?.ProcessCount ?? 0,
                        },
                        memory = memory == null ? null : new { memory.UsedMB, memory.LimitMB, memory.AvailableMB, memory.OomKills },
                        load = health.Resources?.Load1,
                        cpus = health.Resources?.Cpus,
                        diskFreeMB = health.Resources?.RootFreeMB,
                        events = health.Events.TakeLast(8).Select(e => new { e.Seq, e.At, e.Kind, e.Message }).ToList(),
                    },
                };
            }));
            return described.ToList();
        }

        /// <summary>
        /// A repair Klives (or the watchdog) asks for by name. Ordered from gentlest to most disruptive:
        /// restarting the browser keeps everything but open pages; restarting desktop components keeps
        /// the browser; restarting the computer keeps installed software, files and sign-ins but ends
        /// every running process; refreshing helpers swaps in this build's in-container tooling.
        /// </summary>
        public async Task<string> RepairComputerAsync(string containerID, string action, CancellationToken ct = default)
        {
            var record = registry.All().LastOrDefault(r => r.ContainerID == containerID && !r.Lost)
                ?? throw new InvalidOperationException("That computer is not in the registry (or its container is gone).");
            string id12 = record.ContainerID[..Math.Min(12, record.ContainerID.Length)];
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "restart-browser":
                {
                    if (record.Suspended) await ResumeDesktopAsync(record.ContainerID, ct);
                    await orchestrator.ExecuteDesktopShellAsync(record.ContainerID,
                        "pkill -x chromium >/dev/null 2>&1; for i in $(seq 1 20); do pgrep -x chromium >/dev/null || break; sleep 0.25; done; " +
                        "pkill -KILL -x chromium >/dev/null 2>&1; true", "/home/agent", 20, idempotent: true, ct);
                    await orchestrator.ExecuteDesktopControlAsync(record.ContainerID, ContainerDesktopControlCommand.LaunchBrowser, null, ct);
                    log($"Desktop {id12}: browser restarted on request.");
                    return "Browser restarted with its saved profile; sign-ins are kept, open pages were closed.";
                }
                case "restart-components":
                {
                    if (record.Suspended) await ResumeDesktopAsync(record.ContainerID, ct);
                    const string script =
                        "set +e\nexport DISPLAY=:1\nif [ -r /tmp/desktop-session.env ]; then . /tmp/desktop-session.env; fi\n" +
                        "pkill -x xfce4-panel; pkill -x xfdesktop; pkill -x xfwm4; sleep 1\n" +
                        "(nohup xfwm4 --compositor=off >>/tmp/xfwm4.log 2>&1 </dev/null &)\n" +
                        "(nohup xfce4-panel >>/tmp/xfce4-panel.log 2>&1 </dev/null &)\n" +
                        "(nohup xfdesktop >>/tmp/xfdesktop.log 2>&1 </dev/null &)\nsleep 2\n";
                    var result = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, script, "/home/agent", 30, idempotent: true, ct);
                    log($"Desktop {id12}: desktop components restarted on request (exit {result.ExitCode}).");
                    return "Window manager, panel and desktop restarted; the browser and other apps kept running.";
                }
                case "restart-computer":
                {
                    string owner = record.AgentID ?? "klives";
                    if (record.Suspended) await ResumeDesktopAsync(record.ContainerID, ct);
                    else await RestartDesktopAsync(record, owner, ct);
                    log($"Desktop {id12}: computer restarted on request.");
                    return "Computer restarted. Installed software, files and browser sign-ins are kept; running apps were closed.";
                }
                case "refresh-helpers":
                {
                    HelpersCurrent.TryRemove(record.ContainerID, out _);
                    HelperRefreshRetryAfter.TryRemove(record.ContainerID, out _);
                    string? refreshed = await orchestrator.SyncDesktopHelpersAsync(record.ContainerID, ResolveBuildContextDirectory(), ct);
                    string? fingerprint = ShippedHelperFingerprint();
                    if (fingerprint != null) HelpersCurrent[record.ContainerID] = fingerprint;
                    return refreshed == null ? "Helpers were already current." : char.ToUpperInvariant(refreshed[0]) + refreshed[1..] + ".";
                }
                default:
                    throw new ArgumentException("action must be restart-browser, restart-components, restart-computer or refresh-helpers.");
            }
        }

        /// <summary>A desktop daemon's privileged JSON route for the website's diagnostics drawer.</summary>
        public Task<string?> ComputerDiagnosticsAsync(string containerID, CancellationToken ct = default) =>
            DaemonFor(containerID).GetJsonAsync("/diag", "diag", ct, timeoutSeconds: 15);

        // Desktops whose in-container helpers are known to match this build (container → fingerprint
        // of the shipped helpers), and when a failed refresh may be retried.
        private static readonly ConcurrentDictionary<string, string> HelpersCurrent = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> HelperRefreshRetryAfter = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> helperRefreshGates = new(StringComparer.Ordinal);
        private static string? shippedHelperFingerprint;

        private static string? ShippedHelperFingerprint()
        {
            if (shippedHelperFingerprint != null) return shippedHelperFingerprint;
            try
            {
                var hashes = ContainerOrchestrator.ShippedHelperHashes(ResolveBuildContextDirectory());
                return shippedHelperFingerprint = string.Join(",", hashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value));
            }
            catch { return null; }
        }

        /// <summary>
        /// Makes sure this desktop runs the helpers this build ships (see
        /// <see cref="ContainerOrchestrator.SyncDesktopHelpersAsync"/>): one cheap check per desktop per
        /// process, a refresh in place when they differ. Never fails the caller — a desktop with old
        /// helpers still works, it just lacks the newer verification and DevTools modes.
        /// </summary>
        private async Task EnsureHelpersCurrentAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            string? fingerprint = ShippedHelperFingerprint();
            if (fingerprint == null) return;
            if (HelpersCurrent.TryGetValue(record.ContainerID, out var known) && known == fingerprint) return;
            if (HelperRefreshRetryAfter.TryGetValue(record.ContainerID, out var retryAfter) && DateTime.UtcNow < retryAfter) return;
            var gate = helperRefreshGates.GetOrAdd(record.ContainerID, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(45), ct)) return;
            string id12 = record.ContainerID[..Math.Min(12, record.ContainerID.Length)];
            try
            {
                if (HelpersCurrent.TryGetValue(record.ContainerID, out known) && known == fingerprint) return;
                string? refreshed = await orchestrator.SyncDesktopHelpersAsync(record.ContainerID, ResolveBuildContextDirectory(), ct);
                HelpersCurrent[record.ContainerID] = fingerprint;
                HelperRefreshRetryAfter.TryRemove(record.ContainerID, out _);
                if (refreshed != null) log($"Desktop {id12}: {refreshed} in place so it runs this build's browser helpers.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                HelperRefreshRetryAfter[record.ContainerID] = DateTime.UtcNow.AddMinutes(5);
                log($"Desktop {id12}: browser helper refresh failed ({ex.GetType().Name}: {ex.Message}); it keeps its current helpers and is retried later.");
            }
            finally { gate.Release(); }
        }

        /// <summary>
        /// Directly gives <paramref name="recipientAgentID"/> a copy of another Project agent's
        /// persistent Chromium sign-in state. Cookie/session values never pass through an LLM tool
        /// result, the event log, or the dashboard; only the local profile bytes move between the
        /// two agent-owned virtual desktops. The destination's previous profile is replaced.
        /// </summary>
        public async Task<string> TakeBrowserSessionAsync(Project project, string recipientAgentID,
            string sourceAgentID, CancellationToken ct = default)
        {
            string recipient = (recipientAgentID ?? "").Trim();
            string source = (sourceAgentID ?? "").Trim();
            if (recipient.Length == 0) throw new ArgumentException("A recipient agent ID is required.", nameof(recipientAgentID));
            if (source.Length == 0) throw new ArgumentException("Provide sourceAgentId — the commander is 'commander'.", nameof(sourceAgentID));
            if (string.Equals(source, recipient, StringComparison.OrdinalIgnoreCase))
                return "Your browser session is already your own; no handoff was needed.";

            var sourceRecord = registry.ResolveForAgent(project.ProjectID, source);
            if (sourceRecord == null)
                throw new InvalidOperationException($"Agent '{source}' has no desktop browser session to take yet.");
            var recipientRecord = await EnsureDesktopAsync(project, recipient, requireVisualReady: false, ct);
            if (string.Equals(sourceRecord.ContainerID, recipientRecord.ContainerID, StringComparison.Ordinal))
                return "Both agents already resolve to the same desktop and browser session.";

            var handoffGate = browserSessionHandoffGates.GetOrAdd(project.ProjectID, _ => new SemaphoreSlim(1, 1));
            await handoffGate.WaitAsync(ct);
            // The recipient adapter is calling this while holding its own action gate. Acquiring it
            // again would self-deadlock; only drain the source desktop, which is the one another
            // agent could otherwise still be mutating while its profile is copied.
            var actionGatesToHold = new[] { sourceRecord.ContainerID }
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => actionGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
            var heldActionGates = new List<SemaphoreSlim>(actionGatesToHold.Count);
            try
            {
                foreach (var gate in actionGatesToHold)
                {
                    await gate.WaitAsync(ct);
                    heldActionGates.Add(gate);
                }

                await orchestrator.WithBrowserLaunchLocksAsync(
                    new[] { sourceRecord.ContainerID, recipientRecord.ContainerID },
                    async () =>
                    {
                        await StopBrowserForSessionHandoffAsync(sourceRecord.ContainerID, ct);
                        await StopBrowserForSessionHandoffAsync(recipientRecord.ContainerID, ct);
                        BrowserProfileHandoff.Copy(
                            BrowserProfileHandoff.ProfilePath(project.ProjectID, sourceRecord.AgentID),
                            BrowserProfileHandoff.ProfilePath(project.ProjectID, recipientRecord.AgentID));
                    }, ct);

                // Start the recipient's one visible Chromium immediately. Its supervisor deletes
                // any runtime Singleton artefacts and establishes the normal CDP/control channel.
                await orchestrator.ExecuteDesktopControlAsync(recipientRecord.ContainerID,
                    ContainerDesktopControlCommand.LaunchBrowser, null, ct);
                log($"Browser session handed from {source} to {recipient} in project {project.ProjectID}; token values were not exposed.");
                return $"Browser session taken from agent '{source}'. Your visible Chromium was restarted with that sign-in; inspect the target site to verify it still accepts the session.";
            }
            finally
            {
                for (int i = heldActionGates.Count - 1; i >= 0; i--) heldActionGates[i].Release();
                handoffGate.Release();
            }
        }

        private async Task StopBrowserForSessionHandoffAsync(string containerID, CancellationToken ct)
        {
            const string stopBrowser =
                "set +e\n" +
                "pkill -x chromium >/dev/null 2>&1 || true\n" +
                "for i in $(seq 1 40); do pgrep -x chromium >/dev/null 2>&1 || exit 0; sleep 0.25; done\n" +
                "echo 'Chromium did not exit before the session handoff.' >&2\n" +
                "exit 1\n";
            var result = await orchestrator.ExecuteDesktopShellAsync(containerID, stopBrowser, "/home/agent", 20, ct);
            if (!result.Success)
                throw new InvalidOperationException("Could not stop Chromium for the browser-session handoff: "
                    + (result.Stderr + " " + result.Stdout).Trim());
        }

        /// <summary>Creates (or resolves) the desktop record an agent should use. A freshly created
        /// container is probed until its desktop server answers, so the first computer_* tool doesn't
        /// race the container's boot (x11vnc/Xvfb take a few seconds to come up).</summary>
        public async Task<DesktopContainerRecord> EnsureDesktopAsync(Project project, string agentID,
            bool requireVisualReady = true, CancellationToken ct = default)
        {
            bool sharedAllocation = project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock;
            string ownerKey = sharedAllocation ? $"{project.ProjectID}/shared" : $"{project.ProjectID}/{agentID}";
            var provisionGate = provisioningGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
            DesktopContainerRecord record;
            bool created = false;
            bool resumed = false;
            await provisionGate.WaitAsync(ct);
            try
            {
                string? targetAgent = sharedAllocation ? null : agentID;
                var owned = registry.ForProject(project.ProjectID)
                    .Where(r => r.AgentID == targetAgent && !r.Lost)
                    .OrderByDescending(r => r.LastUsedAt)
                    .ThenByDescending(r => r.CreatedAt)
                    .ToList();
                var existing = owned.FirstOrDefault();
                // Registry drift used to leave an old and a replacement desktop running for one
                // owner, making browser state nondeterministic. Keep the newest active record and
                // retire every duplicate while this owner's provisioning gate is held.
                foreach (var duplicate in owned.Skip(1))
                {
                    log($"Desktop owner {ownerKey} has duplicate container {duplicate.ContainerID[..12]}; removing it in favour of {existing!.ContainerID[..12]}.");
                    if (transports.TryRemove(duplicate.ContainerID, out var duplicateTransport)) duplicateTransport.Dispose();
                    if (actionGates.TryRemove(duplicate.ContainerID, out var duplicateActionGate)) duplicateActionGate.Dispose();
                    await orchestrator.StopContainerAsync(duplicate.ContainerID, ct);
                }

                // Image updates apply to new computers. Existing machines own their installed
                // applications and home directories; acquisition must never erase them.
                if (existing != null)
                {
                    record = existing;
                    if (record.Suspended)
                    {
                        await admissionGate.WaitAsync(ct);
                        try
                        {
                            await AdmitLockedAsync(record.ContainerID, ct);
                            await orchestrator.ResumeContainerAsync(record, ct);
                        }
                        finally { admissionGate.Release(); }
                        if (transports.TryRemove(record.ContainerID, out var oldTransport)) oldTransport.Dispose();
                        resumed = true;
                    }
                }
                else
                {
                    await admissionGate.WaitAsync(ct);
                    try
                    {
                        await AdmitLockedAsync(null, ct);
                        record = await orchestrator.CreateDesktopContainerAsync(project.ProjectID, targetAgent, ct: ct);
                    }
                    finally { admissionGate.Release(); }
                    created = true;
                }
                if (!created && DateTime.UtcNow - record.LastUsedAt > TimeSpan.FromMinutes(5))
                {
                    record.LastUsedAt = DateTime.UtcNow;
                    registry.Update(record);
                }
            }
            finally { provisionGate.Release(); }

            if (created) NotifyDesktopChanged(record, "created");
            else if (resumed) NotifyDesktopChanged(record, "resumed");

            // Readiness probing is deliberately outside the provisioning lock: a terminal call
            // may use the newly started container while a visual caller waits for Xvfb/x11vnc.
            if ((created || resumed) && requireVisualReady) await WaitForDesktopReadyAsync(record, ct);
            return record;
        }

        /// <summary>
        /// Blocks until a freshly created container returns a complete framebuffer, or a
        /// bounded budget elapses. Each connect attempt already fails fast (VncTransport's handshake
        /// timeout), so this is a short retry loop, not an open-ended wait. Failure is explicit:
        /// callers must never receive a container record whose framebuffer was never proved usable.
        /// </summary>
        private async Task WaitForDesktopReadyAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var transport = GetTransport(record);
            string lastError = "no framebuffer capture was attempted";
            var daemon = orchestrator.DaemonFor(record.ContainerID);
            for (int attempt = 1; DateTime.UtcNow < deadline; attempt++)
            {
                try
                {
                    var frame = await transport.CaptureFrameAsync(ct);
                    if (!IsCompleteFrame(frame.bgra, frame.width, frame.height))
                        throw new InvalidOperationException($"VNC returned an incomplete {frame.width}x{frame.height} framebuffer ({frame.bgra?.LongLength ?? 0} bytes) while the desktop session was still starting.");
                    log($"Desktop {record.ContainerID[..12]} returned its first frame after {attempt} probe(s).");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    if (attempt == 1) log($"Waiting for desktop {record.ContainerID[..12]} to come up ({ex.Message})…");
                    // From the third attempt on, a frame the daemon captures proves the display is up
                    // just as well; the agent's tools fall back to the daemon while VNC catches up.
                    if (attempt >= 3 && await daemon.CaptureBgraAsync(ct) is { } captured
                        && IsCompleteFrame(captured.Bgra, captured.Width, captured.Height))
                    {
                        log($"Desktop {record.ContainerID[..12]} is serving frames through its control daemon; VNC is not answering yet ({ex.Message}).");
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            // The opaque "no usable framebuffer" is what agents kept reporting with nothing to act
            // on. Probe the live container so the failure names the actual cause — display down,
            // x11vnc crash-looping (with its own log), or a persistently black root.
            string diagnostics = await DescribeDesktopFailureAsync(record, ct);
            throw new InvalidOperationException(
                $"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} did not return a usable VNC framebuffer within 45 seconds. " +
                $"Last capture error: {lastError}. Live container state — {diagnostics}");
        }

        /// <summary>Best-effort in-container probe for the framebuffer-timeout error message: which
        /// desktop processes are up and the tail of x11vnc's own log. Never throws — a diagnostic
        /// that fails must not mask the underlying readiness failure it is describing.</summary>
        private async Task<string> DescribeDesktopFailureAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            const string probe =
                "set +e\n" +
                "echo \"xvfb=$(pgrep -x Xvfb >/dev/null 2>&1 && echo up || echo down)\"\n" +
                "echo \"x11vnc=$(pgrep -x x11vnc >/dev/null 2>&1 && echo up || echo down)\"\n" +
                "echo \"xfwm4=$(pgrep -x xfwm4 >/dev/null 2>&1 && echo up || echo down)\"\n" +
                "echo \"display=$(xdpyinfo -display :1 >/dev/null 2>&1 && echo up || echo down)\"\n" +
                "echo \"xsetroot=$(command -v xsetroot >/dev/null 2>&1 && echo present || echo missing)\"\n" +
                "echo \"--- x11vnc.log tail ---\"\n" +
                "tail -n 12 /tmp/x11vnc.log 2>/dev/null || echo \"(no x11vnc log)\"\n";
            try
            {
                using var diagTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                diagTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var result = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, probe, "/home/agent", 8, diagTimeout.Token);
                string text = (result.Stdout + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : "\n" + result.Stderr)).Trim();
                return string.IsNullOrWhiteSpace(text)
                    ? "diagnostic probe returned no output."
                    : text.Replace("\r\n", " | ").Replace('\n', ' ').Trim();
            }
            catch (Exception ex)
            {
                return $"diagnostic probe could not run ({ex.Message}).";
            }
        }

        /// <summary>
        /// A frame is usable once the transport has delivered a STRUCTURALLY COMPLETE framebuffer:
        /// sane dimensions and at least width*height*4 bytes of pixel data. This is a rigid, confirmed
        /// signal rather than a heuristic — <see cref="VncTransport.CaptureFrameAsync"/> only returns
        /// after a full FramebufferUpdate covering the whole screen has been applied (it throws
        /// otherwise), so a frame that reaches this method already proves the RFB handshake succeeded
        /// and x11vnc is serving a live Xvfb display of the expected size.
        ///
        /// We deliberately do NOT judge pixel brightness. A dark desktop — a dark theme, a dark solid
        /// root, a fullscreen dark application — is a perfectly valid desktop. The previous luminance
        /// heuristic rejected those as "black" and was the direct cause of spurious "no usable
        /// framebuffer" readiness failures that left agents with a working desktop reported as broken.
        /// </summary>
        internal static bool IsCompleteFrame(byte[] bgra, int width, int height)
        {
            if (bgra == null || width < 320 || height < 200) return false;
            return bgra.LongLength >= checked((long)width * height * 4);
        }

        /// <summary>The transport for a container by ID (for the live-view stream route), or null if unknown.</summary>
        public VncTransport? GetTransportByContainerID(string containerID)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID && !r.Lost);
            return record == null ? null : GetTransport(record);
        }

        /// <summary>
        /// Everything Klives' remote-control input route needs for one container: the pooled
        /// transport, the per-container action gate (so human input serialises with agent
        /// observe→act→settle transactions instead of interleaving mid-drag), and the registry
        /// record for project/agent attribution. Null when the container is unknown or retired.
        /// </summary>
        public (VncTransport Transport, SemaphoreSlim ActionGate, DesktopContainerRecord Record)? GetRemoteControlByContainerID(string containerID)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID && !r.Lost);
            if (record == null) return null;
            MarkViewed(containerID);
            return (GetTransport(record),
                actionGates.GetOrAdd(record.ContainerID, _ => new SemaphoreSlim(1, 1)),
                record);
        }

        private VncTransport GetTransport(DesktopContainerRecord record)
        {
            if (transports.TryGetValue(record.ContainerID, out var existing) && existing.Port != record.VncHostPort)
                if (transports.TryRemove(new KeyValuePair<string, VncTransport>(record.ContainerID, existing))) existing.Dispose();
            return transports.GetOrAdd(record.ContainerID,
                _ => new VncTransport(vncHost, record.VncHostPort, log));
        }

        private async Task RestartDesktopAsync(DesktopContainerRecord record, string agentID, CancellationToken ct)
        {
            string ownerKey = $"{record.ProjectID}/{record.AgentID ?? "shared"}";
            var provisionGate = provisioningGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
            await provisionGate.WaitAsync(ct);
            try
            {
                var actionGate = actionGates.GetOrAdd(record.ContainerID, _ => new SemaphoreSlim(1, 1));
                if (!await actionGate.WaitAsync(0, ct))
                    throw new InvalidOperationException("Desktop repair deferred while an action is in progress.");
                try
                {
                    if (inputLock.CurrentHolder(record.ContainerID) is { } holder && holder != agentID)
                        throw new InvalidOperationException("Desktop repair deferred while another agent controls it.");
                    await orchestrator.SuspendContainerAsync(record, ct);
                    await orchestrator.ResumeContainerAsync(record, ct);
                    if (transports.TryRemove(record.ContainerID, out var transport)) transport.Dispose();
                }
                finally { actionGate.Release(); }
            }
            finally { provisionGate.Release(); }
            await WaitForDesktopReadyAsync(record, ct);
        }

        public async Task ResumeDesktopAsync(string containerID, CancellationToken ct = default)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID && !r.Lost)
                ?? throw new InvalidOperationException("Computer is missing; resume cannot recreate its filesystem.");
            string ownerKey = $"{record.ProjectID}/{record.AgentID ?? "shared"}";
            var provisionGate = provisioningGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
            await provisionGate.WaitAsync(ct);
            try
            {
                await admissionGate.WaitAsync(ct);
                try
                {
                    if (record.Suspended) await AdmitLockedAsync(record.ContainerID, ct);
                    await orchestrator.ResumeContainerAsync(record, ct);
                }
                finally { admissionGate.Release(); }
                if (transports.TryRemove(containerID, out var transport)) transport.Dispose();
                NotifyDesktopChanged(record, "resumed");
            }
            finally { provisionGate.Release(); }
            await WaitForDesktopReadyAsync(record, ct);
        }

        /// <summary>Stops an idle desktop in place. Returns true only when it was actually stopped.</summary>
        public async Task<bool> SuspendDesktopAsync(string containerID, CancellationToken ct = default)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID && !r.Lost);
            if (record == null || record.Suspended) return false;
            string ownerKey = $"{record.ProjectID}/{record.AgentID ?? "shared"}";
            var provisionGate = provisioningGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
            await provisionGate.WaitAsync(ct);
            try
            {
                // An acquisition since the reaper's snapshot wins over resource cleanup.
                if (DateTime.UtcNow - record.LastUsedAt < TimeSpan.FromMinutes(5)
                    || inputLock.CurrentHolder(containerID) != null) return false;
                var actionGate = actionGates.GetOrAdd(containerID, _ => new SemaphoreSlim(1, 1));
                if (!await actionGate.WaitAsync(0, ct)) return false;
                try
                {
                    await orchestrator.SuspendContainerAsync(record, ct);
                    if (transports.TryRemove(containerID, out var transport)) transport.Dispose();
                    NotifyDesktopChanged(record, "suspended");
                    return true;
                }
                finally { actionGate.Release(); }
            }
            finally { provisionGate.Release(); }
        }

        /// <summary>Tears down a container and its transport (agent retired / project completed).</summary>
        public async Task DisposeDesktopAsync(string containerID, CancellationToken ct = default)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID);
            if (transports.TryRemove(containerID, out var t)) t.Dispose();
            if (actionGates.TryRemove(containerID, out var gate)) gate.Dispose();
            if (record != null)
            {
                string ownerKey = record.AgentID == null
                    ? $"{record.ProjectID}/shared"
                    : $"{record.ProjectID}/{record.AgentID}";
                if (provisioningGates.TryRemove(ownerKey, out var provisioningGate)) provisioningGate.Dispose();
            }
            await orchestrator.StopContainerAsync(containerID, ct);
            if (record != null) NotifyDesktopChanged(record, "removed");
        }

        private void NotifyDesktopChanged(DesktopContainerRecord record, string change)
        {
            try { DesktopChanged?.Invoke(record, change); }
            catch (Exception ex) { log($"Desktop lifecycle notification failed for {record.ContainerID}: {ex.Message}"); }
        }

        /// <summary>Wake cancellation/retirement cleanup for a shared desktop lease. Input events
        /// are released by the active adapter; this guarantees no expired wake still owns the lock.</summary>
        public async Task ReleaseAgentInputsAsync(string projectID, string agentID)
        {
            foreach (var record in registry.ForProject(projectID))
            {
                inputLock.Release(record.ContainerID, agentID);
                if (transports.TryGetValue(record.ContainerID, out var transport) && transport.Connected)
                    try { await transport.ReleaseAllAsync(CancellationToken.None); } catch { }
            }
        }
    }

    /// <summary>A desktop was not started because every slot the host can safely run is in use.</summary>
    public sealed class DesktopCapacityException : InvalidOperationException
    {
        public DesktopCapacityException(string message) : base(message) { }
    }
}
