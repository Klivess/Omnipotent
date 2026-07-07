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
        public Task ReconcileAsync(CancellationToken ct = default) => orchestrator.ReconcileAsync(ct);

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

        /// <summary>
        /// Ensures a desktop exists for the given agent under the project's allocation mode and
        /// returns a tool adapter bound to it. PerAgentContainers → a container per agent;
        /// SharedDesktopWithInputLock → one project desktop all agents share behind the lock.
        /// </summary>
        public async Task<ContainerToolAdapter> GetAdapterForAgentAsync(
            Project project, string agentID,
            Func<string, Task<string>>? resolveSecretsAsync = null,
            CancellationToken ct = default)
        {
            var record = await EnsureDesktopAsync(project, agentID, ct);
            var transport = GetTransport(record);
            bool shared = project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock;
            return new ContainerToolAdapter(
                transport, record.ContainerID, agentID,
                inputLock: shared ? inputLock : null,
                resolveSecretsAsync: resolveSecretsAsync);
        }

        /// <summary>Creates (or resolves) the desktop record an agent should use. A freshly created
        /// container is probed until its desktop server answers, so the first computer_* tool doesn't
        /// race the container's boot (x11vnc/Xvfb take a few seconds to come up).</summary>
        public async Task<DesktopContainerRecord> EnsureDesktopAsync(Project project, string agentID, CancellationToken ct = default)
        {
            if (project.DesktopAllocation == DesktopAllocationMode.SharedDesktopWithInputLock)
            {
                var shared = registry.ForProject(project.ProjectID).FirstOrDefault(r => r.AgentID == null);
                if (shared != null) return shared;
                shared = await orchestrator.CreateDesktopContainerAsync(project.ProjectID, agentID: null, ct: ct);
                await WaitForDesktopReadyAsync(shared, ct);
                return shared;
            }

            var mine = registry.ForProject(project.ProjectID).FirstOrDefault(r => r.AgentID == agentID);
            if (mine != null) return mine;
            mine = await orchestrator.CreateDesktopContainerAsync(project.ProjectID, agentID, ct: ct);
            await WaitForDesktopReadyAsync(mine, ct);
            return mine;
        }

        /// <summary>
        /// Blocks until a freshly created container's desktop server accepts an RFB handshake, or a
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
                try { await transport.ConnectAsync(ct); log($"Desktop {record.ContainerID[..12]} ready after {attempt} probe(s)."); return; }
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
            return transports.GetOrAdd(record.ContainerID,
                _ => new VncTransport(vncHost, record.VncHostPort, log));
        }

        /// <summary>Tears down a container and its transport (agent retired / project completed).</summary>
        public async Task DisposeDesktopAsync(string containerID, CancellationToken ct = default)
        {
            if (transports.TryRemove(containerID, out var t)) t.Dispose();
            await orchestrator.StopContainerAsync(containerID, ct);
        }
    }
}
