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
            bool daemonUp = await bootstrapper.EnsureDaemonAsync(orchestrator.ProbeDaemonAsync, ct);
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

        // The capabilities a desktop MUST have before browser work is worth attempting. A missing
        // one is what stalled the first live project (no browser and no browser-inspect helper),
        // so the preflight treats their absence as "not ready" rather than letting a
        // computer_* tool discover it mid-task.
        private static readonly string[] RequiredCapabilities =
            { "display", "window-manager", "vnc", "frame", "chromium", "browser-inspect" };

        // One shell probe of the baked stack. `set +e` so a missing tool yields "no", not a
        // non-zero exit that would mask the other answers.
        private const string ReadinessProbeScript =
            "set +e\n" +
            "echo \"display=$(xdpyinfo -display :1 >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"window-manager=$(DISPLAY=:1 wmctrl -m >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"xvfb=$(pgrep -x Xvfb >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"x11vnc=$(pgrep -x x11vnc >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"chromium=$(command -v chromium >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"firefox=$(command -v firefox >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"browser-inspect=$([ -f /usr/local/bin/browser-inspect.py ] && echo yes || echo no)\"\n" +
            "echo \"ffmpeg=$(command -v ffmpeg >/dev/null 2>&1 && echo yes || echo no)\"\n" +
            "echo \"image-version=$(sed -n 's/.*\\\"imageVersion\\\":\\\"\\([^\\\"]*\\)\\\".*/\\1/p' /etc/klive-desktop.json 2>/dev/null)\"\n";

        /// <summary>
        /// Preflight a project's desktop before browser work: self-heal Docker + the image
        /// (rebuilding/recreating a stale container so it picks up the current baked tools), then
        /// probe the visible-browser control/inspection stack and report exactly what's present. The result's facts
        /// are recorded by the caller so later wakes start from known-good state instead of
        /// re-deriving the environment.
        /// </summary>
        public async Task<DesktopReadiness> EnsureDesktopReadyAsync(
            Project project, string agentID, string imageTag, CancellationToken ct = default)
        {
            string? bootstrap = await TryBootstrapAsync(imageTag, ct);
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

            // A current-looking image can still be incomplete (partial publish context, interrupted
            // build, or external retag). The live probe is authoritative: rebuild once from the
            // verified three-file context, recreate the container, and probe the replacement.
            log($"Desktop {record.ContainerID[..Math.Min(12, record.ContainerID.Length)]} failed readiness; attempting one automatic image/container repair.");
            if (!string.Equals(imageTag, ProjectSettings.Defaults.DesktopImage, StringComparison.OrdinalIgnoreCase))
                return new DesktopReadiness
                {
                    Ok = false,
                    ContainerID = record.ContainerID,
                    ImageVersion = readiness.ImageVersion,
                    Capabilities = readiness.Capabilities,
                    Summary = readiness.Summary + $" The configured custom image '{imageTag}' was preserved and cannot be rebuilt from the shipped base context.",
                };
            string contextDir = ResolveBuildContextDirectory();
            string? rebuild = await orchestrator.EnsureImageBuiltAsync(imageTag, contextDir,
                "desktop.Dockerfile", forceRebuild: true, ct: ct);
            if (rebuild != null)
                return new DesktopReadiness
                {
                    Ok = false,
                    ContainerID = record.ContainerID,
                    ImageVersion = readiness.ImageVersion,
                    Capabilities = readiness.Capabilities,
                    Summary = readiness.Summary + " Automatic repair failed: " + rebuild,
                };

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

            if (transports.TryRemove(record.ContainerID, out var staleTransport)) staleTransport.Dispose();
            if (actionGates.TryRemove(record.ContainerID, out var staleGate)) staleGate.Dispose();
            await orchestrator.StopContainerAsync(record.ContainerID, ct);
            record = await EnsureDesktopAsync(project, agentID, requireVisualReady: true, ct);
            var repaired = await ProbeReadinessAsync(record, ct);
            if (!repaired.Ok) return repaired;
            return new DesktopReadiness
            {
                Ok = true,
                ContainerID = repaired.ContainerID,
                ImageVersion = repaired.ImageVersion,
                Capabilities = repaired.Capabilities,
                Summary = "Desktop ready after automatic repair. " + repaired.Summary,
            };
        }

        private async Task<DesktopReadiness> ProbeReadinessAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            ContainerShellResult probe;
            try { probe = await orchestrator.ExecuteDesktopShellAsync(record.ContainerID, ReadinessProbeScript, "/home/agent", 30, ct); }
            catch (Exception ex)
            {
                return new DesktopReadiness { Ok = false, ContainerID = record.ContainerID,
                    Summary = $"Desktop is up but the readiness probe failed: {ex.Message}" };
            }
            if (!probe.Success)
                return new DesktopReadiness { Ok = false, ContainerID = record.ContainerID,
                    Summary = "Desktop readiness probe returned non-zero: " + probe.Format(2000) };

            var caps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in probe.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) caps[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            try
            {
                var frame = await GetTransport(record).CaptureFrameAsync(ct);
                caps["vnc"] = "up";
                caps["frame"] = IsUsableFrame(frame.bgra, frame.width, frame.height) ? "usable" : "black";
                caps["frame-size"] = $"{frame.width}x{frame.height}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                caps["vnc"] = "down";
                caps["frame"] = "unavailable";
                caps["vnc-error"] = ex.Message;
            }
            caps.TryGetValue("image-version", out var imageVersion);
            var missing = RequiredCapabilities.Where(c => c switch
                {
                    "display" or "window-manager" or "vnc" => !string.Equals(caps.GetValueOrDefault(c), "up", StringComparison.Ordinal),
                    "frame" => !string.Equals(caps.GetValueOrDefault(c), "usable", StringComparison.Ordinal),
                    _ => !string.Equals(caps.GetValueOrDefault(c), "yes", StringComparison.Ordinal),
                }).ToList();
            string capsText = string.Join(", ", new[]
            {
                $"display {caps.GetValueOrDefault("display", "?")}",
                $"window-manager {caps.GetValueOrDefault("window-manager", "?")}",
                $"vnc {caps.GetValueOrDefault("vnc", "?")}",
                $"frame {caps.GetValueOrDefault("frame", "?")} {caps.GetValueOrDefault("frame-size", "")}".TrimEnd(),
                $"chromium {caps.GetValueOrDefault("chromium", "?")}",
                $"firefox {caps.GetValueOrDefault("firefox", "?")}",
                $"browser-inspect {caps.GetValueOrDefault("browser-inspect", "?")}",
                $"ffmpeg {caps.GetValueOrDefault("ffmpeg", "?")}",
            });
            bool ok = missing.Count == 0;
            return new DesktopReadiness
            {
                Ok = ok,
                ContainerID = record.ContainerID,
                ImageVersion = imageVersion ?? "",
                Capabilities = caps,
                Summary = ok
                    ? $"Desktop ready (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}."
                    : $"Desktop degraded (image v{(string.IsNullOrEmpty(imageVersion) ? "?" : imageVersion)}): {capsText}. Missing: {string.Join(", ", missing)}.",
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
        /// timeout), so this is a short retry loop, not an open-ended wait. Failure is explicit:
        /// callers must never receive a container record whose framebuffer was never proved usable.
        /// </summary>
        private async Task WaitForDesktopReadyAsync(DesktopContainerRecord record, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var transport = GetTransport(record);
            for (int attempt = 1; DateTime.UtcNow < deadline; attempt++)
            {
                try
                {
                    var frame = await transport.CaptureFrameAsync(ct);
                    if (!IsUsableFrame(frame.bgra, frame.width, frame.height))
                        throw new InvalidOperationException("VNC returned an all-black or incomplete framebuffer while the desktop session was still starting.");
                    log($"Desktop {record.ContainerID[..12]} returned its first frame after {attempt} probe(s).");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (attempt == 1) log($"Waiting for desktop {record.ContainerID[..12]} to come up ({ex.Message})…");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            throw new InvalidOperationException(
                $"Desktop {record.ContainerID[..12]} did not return a usable VNC framebuffer within 45 seconds.");
        }

        internal static bool IsUsableFrame(byte[] bgra, int width, int height)
        {
            if (width < 320 || height < 200 || bgra.Length < checked(width * height * 4)) return false;
            int pixels = width * height;
            int step = Math.Max(1, pixels / 4096);
            long luminance = 0;
            int sampled = 0, almostBlack = 0;
            for (int pixel = 0; pixel < pixels; pixel += step)
            {
                int i = pixel * 4;
                int y = (bgra[i + 2] * 54 + bgra[i + 1] * 183 + bgra[i] * 19) >> 8;
                luminance += y;
                if (y < 4) almostBlack++;
                sampled++;
            }
            double mean = sampled == 0 ? 0 : (double)luminance / sampled;
            double blackFraction = sampled == 0 ? 1 : (double)almostBlack / sampled;
            return mean >= 8 && blackFraction < 0.995;
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
