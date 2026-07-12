using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The platform's desktop subsystem: owns the orchestrator, the registry, the per-container
    /// VNC transport pool, and the shared-desktop input lock. Implements both allocation
    /// mechanics the Commander can choose between (§4) — per-agent containers and one shared
    /// project desktop with an input lock. Docker being unreachable is non-fatal: desktop
    /// operations surface a clear error and the text-tier of the fleet keeps working (§4:
    /// text-tier agents get no desktop at all).
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
        private readonly string vncHost;

        public ContainerRegistry Registry => registry;
        public ContainerOrchestrator Orchestrator => orchestrator;
        public InputLockCoordinator InputLock => inputLock;

        public ContainerDesktopManager(
            Action<string> log,
            Func<string, string> imageForProject,
            string dockerUri,
            string vncHost = "127.0.0.1")
        {
            this.log = log ?? (_ => { });
            this.vncHost = vncHost;
            registry = new ContainerRegistry(log);
            orchestrator = new ContainerOrchestrator(registry, log, imageForProject, dockerUri);
            bootstrapper = new ContainerDependencyBootstrapper(this.log);
        }

        /// <summary>Boot reconciliation — reattach to surviving containers (§9 restart/redeploy).</summary>
        public async Task ReconcileAsync(CancellationToken ct = default)
        {
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
        }

        /// <summary>Probes the Docker daemon; null when healthy, else a human-readable reason.</summary>
        public Task<string?> ProbeDaemonAsync(CancellationToken ct = default) => orchestrator.ProbeDaemonAsync(ct);

        private readonly ContainerDependencyBootstrapper bootstrapper;

        /// <summary>One-line remedy shown to the agent/logs when the desktop layer is unusable.</summary>
        public string SetupHint =>
            $"Desktop control needs Docker running on the host ({orchestrator.DockerUri}) and the desktop image built. " +
            $"Auto-setup state: {bootstrapper.LastStatus} " +
            "Until the desktop is up, use text/HTTP/script tools — they don't need a desktop.";

        /// <summary>
        /// Self-heals the desktop layer's host dependencies: installs/starts Docker (winget →
        /// launch → wait for daemon) and auto-builds the default desktop image from the shipped
        /// build context. Safe to fire-and-forget from tool failures — the bootstrapper is
        /// single-flight with a cooldown. Returns null when the layer is ready, else the
        /// current human-readable status.
        /// </summary>
        public async Task<string?> TryBootstrapAsync(string defaultImageTag, CancellationToken ct = default)
        {
            bool daemonUp = await bootstrapper.EnsureDaemonAsync(orchestrator.ProbeDaemonAsync, ct);
            if (!daemonUp) return bootstrapper.LastStatus;

            string contextDir = Path.Combine(AppContext.BaseDirectory, "Services", "Projects", "Containers");
            string? imageProblem = await orchestrator.EnsureImageBuiltAsync(defaultImageTag, contextDir, "desktop.Dockerfile", ct);
            return imageProblem; // null = fully ready
        }

        // The capabilities a desktop MUST have before browser work is worth attempting. A missing
        // one is what stalled the first live project (no browser, no browser-inspect helper, no
        // playwright), so the preflight treats their absence as "not ready" rather than letting a
        // computer_* tool discover it mid-task.
        private static readonly string[] RequiredCapabilities = { "display", "chromium", "browser-inspect", "playwright" };

        // One shell probe of the baked stack. `set +e` so a missing tool yields "no", not a
        // non-zero exit that would mask the other answers.
        private const string ReadinessProbeScript =
            "set +e\n" +
            "echo \"display=$(xdpyinfo -display :1 >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"chromium=$(command -v chromium >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"firefox=$(command -v firefox >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"browser-inspect=$([ -f /usr/local/bin/browser-inspect.py ] && echo yes || echo no)\"\n" +
            "echo \"playwright=$(/opt/klive/venv/bin/python -c 'import playwright' >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"ffmpeg=$(command -v ffmpeg >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"image-version=$(sed -n 's/.*\\\"imageVersion\\\":\\\"\\([^\\\"]*\\)\\\".*/\\1/p' /etc/klive-desktop.json 2>/dev/null)\"\n";

        /// <summary>
        /// Preflight a project's desktop before browser work: self-heal Docker + the image
        /// (rebuilding/recreating a stale container so it picks up the current baked tools), then
        /// probe the browser-automation stack and report exactly what's present. The result's facts
        /// are recorded by the caller so later wakes start from known-good state instead of
        /// re-deriving the environment.
        /// </summary>
        public async Task<DesktopReadiness> EnsureDesktopReadyAsync(
            Project project, string agentID, string defaultImageTag, CancellationToken ct = default)
        {
            string? bootstrap = await TryBootstrapAsync(defaultImageTag, ct);
            if (bootstrap != null)
                return new DesktopReadiness { Ok = false, Summary = $"Desktop not ready — {bootstrap}" };

            DesktopContainerRecord record;
            try { record = await EnsureDesktopAsync(project, agentID, requireVisualReady: true, ct); }
            catch (Exception ex)
            {
                return new DesktopReadiness { Ok = false, Summary = $"Desktop provisioning failed: {ex.Message}" };
            }

            ContainerShellResult probe;
            try { probe = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, ReadinessProbeScript, "/home/agent", 30, ct); }
            catch (Exception ex)
            {
                return new DesktopReadiness
                {
                    Ok = false,
                    ContainerID = record.ContainerID,
                    Summary = $"Desktop is up but the readiness probe failed: {ex.Message}",
                };
            }

            var caps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in probe.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) caps[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            caps.TryGetValue("image-version", out var imageVersion);

            var missing = RequiredCapabilities
                .Where(c => c == "display"
                    ? !string.Equals(caps.GetValueOrDefault("display"), "up", StringComparison.Ordinal)
                    : !string.Equals(caps.GetValueOrDefault(c), "yes", StringComparison.Ordinal))
                .ToList();

            string capsText = string.Join(", ", new[]
            {
                $"display {caps.GetValueOrDefault("display", "?")}",
                $"chromium {caps.GetValueOrDefault("chromium", "?")}",
                $"firefox {caps.GetValueOrDefault("firefox", "?")}",
                $"browser-inspect {caps.GetValueOrDefault("browser-inspect", "?")}",
                $"playwright {caps.GetValueOrDefault("playwright", "?")}",
                $"ffmpeg {caps.GetValueOrDefault("ffmpeg", "?")}",
            });

            bool ok = missing.Count == 0;
            string summary = ok
                ? $"Desktop ready (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}. Playwright venv: /opt/klive/venv."
                : $"Desktop degraded (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}. Missing: {string.Join(", ", missing)} — install them via computer_terminal (sudo apt / the /opt/klive/venv pip) before browser work.";

            return new DesktopReadiness
            {
                Ok = ok,
                ContainerID = record.ContainerID,
                ImageVersion = imageVersion ?? "",
                Capabilities = caps,
                Summary = summary,
            };
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
                actionSettleMs: actionSettleMs,
                typingDelayMs: typingDelayMs);
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
            await provisionGate.WaitAsync(ct);
            try
            {
                string? targetAgent = sharedAllocation ? null : agentID;
                var existing = registry.ForProject(project.ProjectID).FirstOrDefault(r => r.AgentID == targetAgent && !r.Lost);

                // A container running a now-stale image (rebuilt with newer baked tools after this
                // container was created) is recreated so the project's long-lived desktop actually
                // gets those tools — the /project bind mount (cookies, browser profiles, work files)
                // survives, so only ephemeral in-container state is lost. Skipped while a *different*
                // agent holds the shared-desktop input lock, so we never yank a desktop mid-action.
                if (existing != null && await orchestrator.IsRecordStaleAsync(existing, ct))
                {
                    string? holder = sharedAllocation ? inputLock.CurrentHolder(existing.ContainerID) : null;
                    if (holder != null && holder != agentID)
                    {
                        log($"Desktop {existing.ContainerID[..12]} is stale but agent {holder} holds the input lock — deferring recreation.");
                    }
                    else
                    {
                        log($"Desktop {existing.ContainerID[..12]} (project {project.ProjectID}) is running a stale image — recreating so it picks up the current desktop tools.");
                        // Tear the container down directly rather than via DisposeDesktopAsync — the
                        // latter also disposes this owner's provisioning gate, which we are holding.
                        if (transports.TryRemove(existing.ContainerID, out var staleTransport)) staleTransport.Dispose();
                        if (actionGates.TryRemove(existing.ContainerID, out var staleGate)) staleGate.Dispose();
                        await orchestrator.StopContainerAsync(existing.ContainerID, ct); // also removes the registry record
                        existing = null;
                    }
                }

                if (existing != null) record = existing;
                else
                {
                    record = await orchestrator.CreateDesktopContainerAsync(project.ProjectID, targetAgent, ct: ct);
                    created = true;
                }
            }
            finally { provisionGate.Release(); }

            // Stamp desktop usage so the reaper can retire desktops agents have stopped touching,
            // independent of overall project activity. The registry write is whole-file, so persist
            // lazily — only when the stamp advances materially — to keep continuous use from churning
            // the file on every action. A freshly created record is already stamped at construction.
            if (!created && DateTime.UtcNow - record.LastUsedAt > TimeSpan.FromMinutes(5))
            {
                record.LastUsedAt = DateTime.UtcNow;
                registry.Update(record);
            }

            // Readiness probing is deliberately outside the provisioning lock: a terminal call
            // may use the newly started container while a visual caller waits for Xvfb/x11vnc.
            if (created && requireVisualReady) await WaitForDesktopReadyAsync(record, ct);
            return record;
        }

        /// <summary>
        /// Blocks until a freshly created container returns a complete framebuffer, or a
        /// bounded budget elapses. Each connect attempt already fails fast (VncTransport's handshake
        /// timeout), so this is a short retry loop, not an open-ended wait. Never throws — if the
        /// desktop never comes up, the first tool call surfaces the clear per-connect error instead.
        /// </summary>
        private async Task WaitForDesktopReadyAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var transport = GetTransport(record);
            for (int attempt = 1; DateTime.UtcNow < deadline; attempt++)
            {
                try
                {
                    await transport.CaptureFrameAsync(ct);
                    log($"Desktop {record.ContainerID[..12]} returned its first frame after {attempt} probe(s).");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (attempt == 1) log($"Waiting for desktop {record.ContainerID[..12]} to come up ({ex.Message})…");
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { return; }
                }
            }
            log($"Desktop {record.ContainerID[..12]} did not become ready within the boot window — tools will report the connection error.");
        }

        /// <summary>The transport for a container by ID (for the live-view stream route), or null if unknown.</summary>
        public VncTransport? GetTransportByContainerID(string containerID)
        {
            var record = registry.All().FirstOrDefault(r => r.ContainerID == containerID && !r.Lost);
            return record == null ? null : GetTransport(record);
        }

        private VncTransport GetTransport(DesktopContainerRecord record)
        {
            if (transports.TryGetValue(record.ContainerID, out var existing) && existing.Port != record.VncHostPort)
                if (transports.TryRemove(new KeyValuePair<string, VncTransport>(record.ContainerID, existing))) existing.Dispose();
            return transports.GetOrAdd(record.ContainerID,
                _ => new VncTransport(vncHost, record.VncHostPort, log));
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
}
