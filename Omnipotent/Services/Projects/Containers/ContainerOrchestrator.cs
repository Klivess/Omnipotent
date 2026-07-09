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
        // ~2 GB per desktop: XFCE + Firefox alone sit near 1 GB, and agents are expected to
        // apt-install and run real applications on their machines (§4 revised).
        private const long DefaultMemoryBytes = 2L * 1024 * 1024 * 1024;
        /// <summary>Image label carrying the SHA-256 of the build context, so a changed
        /// Dockerfile/entrypoint triggers a rebuild instead of being silently ignored.</summary>
        private const string ContextHashLabel = "omnipotent.projects.context-hash";

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
        /// Probes the Docker daemon with a short timeout. Returns null when it answers, or a
        /// human-readable reason when it doesn't (daemon not running, wrong endpoint, etc.). This
        /// turns the opaque "The operation has timed out." (a named-pipe connect timeout when no
        /// daemon is listening) into an actionable diagnosis for the agent and the logs.
        /// </summary>
        public async Task<string?> ProbeDaemonAsync(CancellationToken ct = default)
        {
            try
            {
                var docker = await GetClientAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await docker.System.PingAsync(timeout.Token);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return $"the Docker daemon at {dockerUri} did not respond within 4s — it is not running (or is unreachable at that endpoint).";
            }
            catch (Exception ex)
            {
                return $"the Docker daemon at {dockerUri} is unreachable: {ex.Message}";
            }
        }

        /// <summary>The Docker endpoint this orchestrator targets (for diagnostics).</summary>
        public string DockerUri => dockerUri;

        /// <summary>
        /// Executes one fixed desktop-control operation inside an owned desktop container. This is
        /// intentionally not a shell API: operation and arguments are validated before an exec is
        /// created, and browser URLs are passed as a positional parameter rather than interpolated
        /// into a command string.
        /// </summary>
        public async Task ExecuteDesktopControlAsync(string containerID, ContainerDesktopControlCommand command, string? argument,
            CancellationToken ct = default)
        {
            IList<string> cmd = command switch
            {
                ContainerDesktopControlCommand.LaunchBrowser when string.IsNullOrWhiteSpace(argument)
                    => new[] { "sh", "-lc", "DISPLAY=:1 firefox-esr >/dev/null 2>&1 &" },
                ContainerDesktopControlCommand.LaunchBrowser when Uri.TryCreate(argument, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    => new[] { "sh", "-lc", "DISPLAY=:1 firefox-esr --new-window \"$1\" >/dev/null 2>&1 &", "desktop-control", uri.AbsoluteUri },
                ContainerDesktopControlCommand.LaunchTerminal when string.IsNullOrWhiteSpace(argument)
                    => new[] { "sh", "-lc", "DISPLAY=:1 xfce4-terminal >/dev/null 2>&1 &" },
                ContainerDesktopControlCommand.FocusBrowser when string.IsNullOrWhiteSpace(argument)
                    => new[] { "sh", "-lc", "DISPLAY=:1 wmctrl -a Firefox" },
                ContainerDesktopControlCommand.FocusTerminal when string.IsNullOrWhiteSpace(argument)
                    => new[] { "sh", "-lc", "DISPLAY=:1 wmctrl -a Terminal" },
                _ => throw new InvalidOperationException("Invalid isolated desktop-control command."),
            };

            var docker = await GetClientAsync();
            var created = await docker.Exec.ExecCreateContainerAsync(containerID, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Tty = false,
                User = "agent",
                WorkingDir = "/home/agent",
                Cmd = cmd,
            }, ct);
            using var output = await docker.Exec.StartAndAttachContainerExecAsync(created.ID, false, ct);
            var (stdout, stderr) = await output.ReadOutputToEndAsync(ct);
            var inspected = await docker.Exec.InspectContainerExecAsync(created.ID, ct);
            if (inspected.ExitCode != 0)
                throw new InvalidOperationException($"Desktop control command failed (exit {inspected.ExitCode}): {(stderr + stdout).Trim()}");
        }

        /// <summary>True when <paramref name="imageTag"/> exists locally.</summary>
        public async Task<bool> ImageExistsAsync(string imageTag, CancellationToken ct = default)
            => await FindImageAsync(imageTag, ct) != null;

        private async Task<ImagesListResponse?> FindImageAsync(string imageTag, CancellationToken ct)
        {
            var docker = await GetClientAsync();
            var images = await docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
            return images.FirstOrDefault(i => i.RepoTags?.Contains(imageTag) == true);
        }

        /// <summary>
        /// Builds the desktop image from a shipped build context when it's missing on this host —
        /// the second half of dependency self-healing (daemon install being the first). The image
        /// carries a label with the build context's hash, so a shipped change to the
        /// Dockerfile/entrypoint rebuilds the (same-tagged) image on the next bootstrap instead of
        /// being silently ignored; running containers keep the old image until they're recreated.
        /// The context is tarred in-process (no docker CLI needed); .sh files are normalised to LF
        /// so a CRLF git checkout can't produce a container whose entrypoint dies on '\r'. Returns
        /// null on success (or already current), else a human-readable reason.
        /// </summary>
        public async Task<string?> EnsureImageBuiltAsync(string imageTag, string contextDir, string dockerfileName, CancellationToken ct = default)
        {
            try
            {
                bool contextAvailable = Directory.Exists(contextDir) && File.Exists(Path.Combine(contextDir, dockerfileName));
                var existing = await FindImageAsync(imageTag, ct);
                if (!contextAvailable)
                {
                    // No context shipped on this host: a present image (however old) beats no desktop.
                    return existing != null ? null
                        : $"desktop image '{imageTag}' is missing and the build context was not found at {contextDir}.";
                }

                string contextHash = ComputeContextHash(contextDir);
                if (existing?.Labels != null && existing.Labels.TryGetValue(ContextHashLabel, out var builtHash) && builtHash == contextHash)
                    return null; // image is current

                log(existing == null
                    ? $"Desktop image '{imageTag}' not found — building it now (first build takes several minutes)…"
                    : $"Desktop image '{imageTag}' is stale (build context changed) — rebuilding…");
                var docker = await GetClientAsync();
                using var context = CreateTarContext(contextDir);
                string? buildError = null;
                await docker.Images.BuildImageFromDockerfileAsync(
                    new ImageBuildParameters
                    {
                        Dockerfile = dockerfileName,
                        Tags = new List<string> { imageTag },
                        Labels = new Dictionary<string, string> { [ContextHashLabel] = contextHash },
                    },
                    context,
                    null, null,
                    new Progress<JSONMessage>(m =>
                    {
                        if (!string.IsNullOrWhiteSpace(m.Stream)) log($"docker build: {m.Stream.TrimEnd()}");
                        if (m.Error != null) buildError = m.Error.Message;
                    }),
                    ct);

                if (buildError != null) return $"docker build failed: {buildError}";
                if (!await ImageExistsAsync(imageTag, ct)) return "docker build finished but the image is still missing.";
                log($"Desktop image '{imageTag}' built successfully.");
                return null;
            }
            catch (Exception ex)
            {
                return $"desktop image build failed: {ex.Message}";
            }
        }

        /// <summary>In-process tar of the build-context directory (flat — the context is two small files).</summary>
        private static MemoryStream CreateTarContext(string contextDir)
        {
            var ms = new MemoryStream();
            using (var writer = new System.Formats.Tar.TarWriter(ms, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var file in Directory.GetFiles(contextDir))
                {
                    var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, Path.GetFileName(file))
                    {
                        DataStream = new MemoryStream(ReadContextFile(file)),
                    };
                    writer.WriteEntry(entry);
                }
            }
            ms.Position = 0;
            return ms;
        }

        /// <summary>A context file's bytes as they enter the build: .sh normalised to LF.</summary>
        private static byte[] ReadContextFile(string file) =>
            file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(file).Replace("\r\n", "\n"))
                : File.ReadAllBytes(file);

        /// <summary>SHA-256 over the context's file names + normalised contents (order-stable), so
        /// the staleness check sees exactly what the build would see.</summary>
        private static string ComputeContextHash(string contextDir)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var ms = new MemoryStream();
            foreach (var file in Directory.GetFiles(contextDir).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                byte[] name = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0");
                ms.Write(name, 0, name.Length);
                byte[] bytes = ReadContextFile(file);
                ms.Write(bytes, 0, bytes.Length);
            }
            return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).ToLowerInvariant();
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
