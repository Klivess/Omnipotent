using Docker.DotNet;
using Docker.DotNet.Models;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Docker lifecycle for the desktop-container fleet (§4). Talks to the local Docker daemon
    /// via Docker.DotNet (named pipe on Windows, unix socket if the host ever moves to Linux).
    /// Containers are created with restart=unless-stopped and are deliberately NOT stopped when
    /// Omnipotent restarts — <see cref="ReconcileAsync"/> reattaches to them on boot instead,
    /// which is what keeps logins and open tabs alive across redeploys.
    ///
    /// VNC ports are bound to 127.0.0.1 only: the RFB connection carries no auth (security type
    /// None), so network isolation IS the auth boundary — nothing off-box can reach a desktop.
    /// </summary>
    public class ContainerOrchestrator
    {
        private readonly ContainerRegistry registry;
        private readonly Action<string> log;
        private readonly Func<string, string> imageForProject;  // projectID → image (per-project setting)
        private readonly string dockerUri;
        private DockerClient? client;
        private readonly SemaphoreSlim clientGate = new(1, 1);

        private const int VncContainerPort = 5901;
        private const long DefaultMemoryBytes = 1L * 1024 * 1024 * 1024; // ~1 GB per desktop (§4)

        public ContainerOrchestrator(
            ContainerRegistry registry,
            Action<string> log,
            Func<string, string> imageForProject,
            string dockerUri)
        {
            this.registry = registry;
            this.log = log ?? (_ => { });
            this.imageForProject = imageForProject;
            this.dockerUri = dockerUri;
        }

        private async Task<DockerClient> GetClientAsync()
        {
            if (client != null) return client;
            await clientGate.WaitAsync();
            try
            {
                if (client != null) return client;
                client = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
                return client;
            }
            finally { clientGate.Release(); }
        }

        /// <summary>
        /// Creates and starts a desktop container. <paramref name="agentID"/> null = the
        /// project's shared desktop; non-null = a per-agent container. The project volume is
        /// mounted at /project in every container of the project (shared directories, §4).
        /// </summary>
        public async Task<DesktopContainerRecord> CreateDesktopContainerAsync(string projectID, string? agentID,
            int width = 1280, int height = 800, CancellationToken ct = default)
        {
            var docker = await GetClientAsync();
            string image = imageForProject(projectID);

            string volumeHostDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsVolumesDirectory), projectID);
            Directory.CreateDirectory(volumeHostDir);

            var create = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = $"omniproj-{projectID[..Math.Min(8, projectID.Length)]}-{(agentID == null ? "shared" : agentID[..Math.Min(8, agentID.Length)])}-{Guid.NewGuid().ToString("N")[..6]}",
                Labels = new Dictionary<string, string>
                {
                    [ContainerLabels.Owner] = "projects",
                    [ContainerLabels.ProjectID] = projectID,
                    [ContainerLabels.AgentID] = agentID ?? "",
                },
                Env = new List<string>
                {
                    $"DISPLAY_WIDTH={width}",
                    $"DISPLAY_HEIGHT={height}",
                },
                ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{VncContainerPort}/tcp"] = default },
                HostConfig = new HostConfig
                {
                    Memory = DefaultMemoryBytes,
                    // Ephemeral host port, loopback only — see class remarks on the auth boundary.
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        [$"{VncContainerPort}/tcp"] = new List<PortBinding> { new() { HostIP = "127.0.0.1", HostPort = "" } },
                    },
                    Binds = new List<string> { $"{volumeHostDir}:/project" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    ShmSize = 256 * 1024 * 1024, // browsers crash with the 64MB default /dev/shm
                },
            }, ct);

            await docker.Containers.StartContainerAsync(create.ID, new ContainerStartParameters(), ct);

            var inspect = await docker.Containers.InspectContainerAsync(create.ID, ct);
            int hostPort = ResolveHostPort(inspect)
                ?? throw new InvalidOperationException($"Container {create.ID} started but no host port was bound for VNC.");

            var record = new DesktopContainerRecord
            {
                ContainerID = create.ID,
                ProjectID = projectID,
                AgentID = agentID,
                VncHostPort = hostPort,
                Width = width,
                Height = height,
            };
            registry.Add(record);
            log($"Created desktop container {create.ID[..12]} for project {projectID}{(agentID == null ? " (shared)" : $" agent {agentID}")} on 127.0.0.1:{hostPort}.");
            return record;
        }

        /// <summary>Stops and removes a container and forgets it in the registry.</summary>
        public async Task StopContainerAsync(string containerID, CancellationToken ct = default)
        {
            var docker = await GetClientAsync();
            try { await docker.Containers.StopContainerAsync(containerID, new ContainerStopParameters { WaitBeforeKillSeconds = 15 }, ct); }
            catch (DockerContainerNotFoundException) { }
            try { await docker.Containers.RemoveContainerAsync(containerID, new ContainerRemoveParameters { Force = true }, ct); }
            catch (DockerContainerNotFoundException) { }
            registry.Remove(containerID);
            log($"Stopped and removed desktop container {containerID[..Math.Min(12, containerID.Length)]}.");
        }

        /// <summary>All live Docker containers carrying our owner label.</summary>
        public async Task<IList<ContainerListResponse>> ListRunningAsync(CancellationToken ct = default)
        {
            var docker = await GetClientAsync();
            return await docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [$"{ContainerLabels.Owner}=projects"] = true },
                },
            }, ct);
        }

        /// <summary>
        /// Boot-time reconciliation: registry ∩ Docker reality. Registry records whose container
        /// is gone are marked Lost (never silently dropped — the Commander should learn its
        /// desktop died); running containers are re-inspected so a changed ephemeral port is
        /// picked up; stopped-but-present containers are restarted.
        /// </summary>
        public async Task ReconcileAsync(CancellationToken ct = default)
        {
            IList<ContainerListResponse> live;
            try { live = await ListRunningAsync(ct); }
            catch (Exception ex)
            {
                log($"ContainerOrchestrator: Docker unreachable during reconcile ({ex.Message}) — desktops unavailable until it returns.");
                return;
            }
            var docker = await GetClientAsync();
            var liveByID = live.ToDictionary(c => c.ID, c => c);

            foreach (var record in registry.All())
            {
                if (!liveByID.TryGetValue(record.ContainerID, out var summary))
                {
                    if (!record.Lost)
                    {
                        record.Lost = true;
                        registry.Update(record);
                        log($"Reconcile: container {record.ContainerID[..12]} (project {record.ProjectID}) is gone — marked lost.");
                    }
                    continue;
                }

                if (!string.Equals(summary.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    try { await docker.Containers.StartContainerAsync(record.ContainerID, new ContainerStartParameters(), ct); }
                    catch (Exception ex) { log($"Reconcile: failed to restart {record.ContainerID[..12]}: {ex.Message}"); continue; }
                }

                try
                {
                    var inspect = await docker.Containers.InspectContainerAsync(record.ContainerID, ct);
                    int? port = ResolveHostPort(inspect);
                    if (port.HasValue && (port.Value != record.VncHostPort || record.Lost))
                    {
                        record.VncHostPort = port.Value;
                        record.Lost = false;
                        registry.Update(record);
                    }
                }
                catch (Exception ex) { log($"Reconcile: inspect failed for {record.ContainerID[..12]}: {ex.Message}"); }
            }
            log($"Container reconcile complete: {registry.All().Count(r => !r.Lost)} live desktop(s).");
        }

        private static int? ResolveHostPort(ContainerInspectResponse inspect)
        {
            if (inspect.NetworkSettings?.Ports != null &&
                inspect.NetworkSettings.Ports.TryGetValue($"{VncContainerPort}/tcp", out var bindings) &&
                bindings is { Count: > 0 } &&
                int.TryParse(bindings[0].HostPort, out int port))
                return port;
            return null;
        }
    }
}
