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
            "echo \"browser-process=$(pgrep -f '[c]hromium.*remote-debugging-port=9222' >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "echo \"browser-cdp=$(python3 -c 'import urllib.request; urllib.request.urlopen(\\\"http://127.0.0.1:9222/json/version\\\", timeout=2).read(); print(\\\"up\\\")' 2>/dev/null || echo down)\"\n" +
            "echo \"browser-tabs=$(python3 /usr/local/bin/browser-inspect.py tabs 1 0 >/dev/null 2>&1 && echo up || echo down)\"\n" +
            "printf 'chromium-log='\n" +
            "tail -n 12 /tmp/chromium.log 2>/dev/null | tr '\\n' ' ' | cut -c 1-1200\n";

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
            if (readiness.Ok)
            {
                readiness = await EnsureBrowserReadyAsync(record, readiness, ct);
                if (readiness.Ok) return readiness;

                // Other project desktops can remain healthy while this project's persistent
                // Chromium profile is locked or corrupted. Recover that one profile first:
                // archive it in-place (never delete it), recreate an empty profile, then retry.
                // Rebuilding the shared image is an expensive and irrelevant first response.
                if (BrowserFailedToStart(readiness.Capabilities))
                {
                    string? profileRecovery = await ArchiveBrokenBrowserProfileAsync(record, ct);
                    if (profileRecovery != null)
                    {
                        var retriedDesktop = await ProbeReadinessAsync(record, ct);
                        if (retriedDesktop.Ok)
                        {
                            var retriedBrowser = await EnsureBrowserReadyAsync(record, retriedDesktop, ct);
                            if (retriedBrowser.Ok)
                                return new DesktopReadiness
                                {
                                    Ok = true, ContainerID = retriedBrowser.ContainerID,
                                    ImageVersion = retriedBrowser.ImageVersion, Capabilities = retriedBrowser.Capabilities,
                                    Summary = "Desktop browser recovered from its archived project-local profile. " + retriedBrowser.Summary,
                                };
                            readiness = retriedBrowser;
                        }
                    }
                }
            }

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
            repaired = await EnsureBrowserReadyAsync(record, repaired, ct);
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
                "pkill -f '[c]hromium' >/dev/null 2>&1 || true\n" +
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

            var caps = ParseCapabilities(probe.Stdout);
            try
            {
                var frame = await GetTransport(record).CaptureFrameAsync(ct);
                caps["vnc"] = "up";
                caps["frame"] = IsCompleteFrame(frame.bgra, frame.width, frame.height) ? "usable" : "incomplete";
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

        internal static bool BrowserControlIsReady(IReadOnlyDictionary<string, string> capabilities) =>
            RequiredBrowserCapabilities.All(cap => string.Equals(capabilities.GetValueOrDefault(cap), "up", StringComparison.Ordinal));

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
            string lastError = "no framebuffer capture was attempted";
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
