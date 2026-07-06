using Omnipotent.Service_Manager;
using Omnipotent.Services.Projects.Containers;
using Omnipotent.Services.Projects.Discord;
using Omnipotent.Services.Projects.Stimulus;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Projects — a fully autonomous, persistent 24/7 agent task force (KliveAgent extension).
    /// A Project is a goal + a budget pursued by one Commander agent and a fleet of sub-agents
    /// inside isolated desktop containers, communicating with Klives via the KM website and
    /// Discord. Design doc: "Projects — KliveAgent Extension" Draft 2, 3 July 2026.
    ///
    /// Phase 1 scope (this commit): the durable substrate everything else builds on —
    ///   * Project records (goal, budgets, caps) with atomic JSON storage.
    ///   * The per-project append-only event log: the single source of truth (§7).
    ///   * The standing digest + rehydrate-on-wake context assembly (digest + recent events
    ///     + BM25 retrieval, all budget-fitted).
    ///   * REST routes for project CRUD and timeline reads (Klives-only).
    ///
    /// Later phases: container fleet + VNC transport (P2), Commander/tiers/budget/vault (P3),
    /// stimulus bus (P4), Discord (P5), KM website section (P6), watchdog + hardening (P7).
    /// </summary>
    public class Projects : OmniService
    {
        public ProjectStore Store { get; private set; } = null!;
        public ProjectEventLogStore EventLog { get; private set; } = null!;
        public ProjectDigestStore Digests { get; private set; } = null!;
        public ProjectRetrievalIndex Retrieval { get; private set; } = null!;
        public ProjectWakeCycle WakeCycle { get; private set; } = null!;
        // ── Phase 3: orchestration ──
        /// <summary>Per-project settings — Projects' own setting system, not OmniSettings.</summary>
        public ProjectSettingsStore Settings { get; private set; } = null!;
        public ProjectVault Vault { get; private set; } = null!;
        public ProjectBudgetLedger Budget { get; private set; } = null!;
        public ProjectTierRouter TierRouter { get; private set; } = null!;
        public ProjectGateManager Gates { get; private set; } = null!;
        public ProjectSubAgentManager SubAgents { get; private set; } = null!;
        public ProjectCommanderRunner CommanderRunner { get; private set; } = null!;
        // ── Phase 4: stimulus bus ──
        public StimulusHookStore Hooks { get; private set; } = null!;
        public StimulusQueue StimulusQueue { get; private set; } = null!;
        public StimulusBus Bus { get; private set; } = null!;
        public StimulusAdapterManager Adapters { get; private set; } = null!;
        // ── Phase 5: Discord ──
        /// <summary>Per-project Discord integration. Null until KliveBotDiscord is available.</summary>
        public ProjectDiscordManager? DiscordManager { get; private set; }
        private ProjectReportScheduler? reportScheduler;
        // ── Phase 7: watchdog ──
        public ProjectWatchdog Watchdog { get; private set; } = null!;
        private System.Threading.Timer? keepaliveTimer;
        /// <summary>The desktop-container subsystem (P2). Null when containers are disabled or off-Windows.</summary>
        public ContainerDesktopManager? Desktops { get; private set; }
        private ProjectsRoutes routes = null!;

        /// <summary>Inter-agent messaging over the bus: (projectID, fromAgent, toAgent, message).</summary>
        public Func<string, string, string, string, Task>? SendAgentMessageHook { get; set; }
        /// <summary>P5 hook: surface a human-only obstacle through Discord. Set when Discord exists.</summary>
        public Func<string, Task>? RequestHumanHook { get; set; }
        public ProjectArtifactStore Artifacts { get; private set; } = null!;
        public ProjectSubAgentRunner SubAgentRunner { get; private set; } = null!;
        private System.Threading.Timer? retentionTimer;

        public Projects()
        {
            name = "Projects";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            Store = new ProjectStore(msg => ServiceLog(msg));
            EventLog = new ProjectEventLogStore(msg => ServiceLog(msg));
            Digests = new ProjectDigestStore(msg => ServiceLog(msg));
            Retrieval = new ProjectRetrievalIndex(EventLog);
            EventLog.EventAppended += Retrieval.Ingest;
            WakeCycle = new ProjectWakeCycle(EventLog, Digests, Retrieval);

            // Phase 3: orchestration subsystems.
            Settings = new ProjectSettingsStore();
            Vault = new ProjectVault(msg => ServiceLog(msg));
            var costFetcher = new OpenRouterCostFetcher(
                tokenProvider: () => GetStringOmniSettingNullable("OpenRouterLLMToken"),
                log: msg => ServiceLog(msg));
            Budget = new ProjectBudgetLedger(Store, EventLog, costFetcher, msg => ServiceLog(msg));
            TierRouter = new ProjectTierRouter(Settings);
            Gates = new ProjectGateManager(EventLog, msg => ServiceLog(msg));
            SubAgents = new ProjectSubAgentManager(Store, EventLog);
            CommanderRunner = new ProjectCommanderRunner(this);
            SubAgentRunner = new ProjectSubAgentRunner(this);
            Artifacts = new ProjectArtifactStore(msg => ServiceLog(msg));
            // 48h raw-media retention sweep (§7), hourly.
            retentionTimer = new System.Threading.Timer(_ =>
            {
                try { Artifacts.RunRetentionSweep(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: artifact retention sweep failed"); }
            }, null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

            // Phase 4: stimulus bus. Triage uses a free omni model with a cheap paid fallback.
            Hooks = new StimulusHookStore(EventLog);
            StimulusQueue = new StimulusQueue(msg => ServiceLog(msg));
            var triageAgent = new StimulusAgent(
                queryModelAsync: QueryUtilityModelAsync,
                modelsForProject: pid => { var s = Settings.Get(pid); return (s.StimulusFreeModel, s.StimulusFallbackModel); },
                log: msg => ServiceLog(msg));
            Bus = new StimulusBus(Hooks, StimulusQueue, triageAgent, EventLog, Store, msg => ServiceLog(msg));
            Bus.DeliverToAgent = DeliverStimulusAsync;
            Adapters = new StimulusAdapterManager(Bus, Hooks, msg => ServiceLog(msg));

            // Inter-agent messages ride the same bus (§5.2, uniform protocol): a Commander↔agent
            // message is a durable stimulus envelope delivered to the target agent's channel, which
            // wakes it. No triage — a directed message is always relevant.
            SendAgentMessageHook = async (projectID, fromAgent, toAgent, message) =>
            {
                EventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    AgentID = toAgent,
                    Type = ProjectEventTypes.AgentMessage,
                    Author = fromAgent == "commander" ? "commander" : "agent",
                    Text = $"{fromAgent} → {toAgent}: {message}",
                });
                await StimulusQueue.EnqueueAsync(new Stimulus.StimulusEnvelope
                {
                    ProjectID = projectID,
                    HookID = "inter-agent",
                    SourceKind = "inter-agent",
                    Payload = $"Message from {fromAgent}: {message}",
                    Verdict = "Directed inter-agent message.",
                }, toAgent);
            };

            // Crash recovery: clear any wake left active by a restart (rehydrate-on-wake safe).
            try { CommanderRunner.RecoverInterruptedWakes(); }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: failed to recover interrupted wakes"); }

            // Replay durable undelivered stimuli, then arm the source adapters.
            try { Bus.Replay(); Adapters.ArmAll(); }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: stimulus replay/arm failed"); }

            routes = new ProjectsRoutes(this);
            await routes.RegisterRoutes();

            await InitialiseDesktopsAsync();
            await InitialiseDiscordAsync();

            // Phase 7: watchdog + a keepalive that guarantees each active project wakes at least
            // periodically (the "no stimuli" half of stall prevention — dev note #1).
            Watchdog = new ProjectWatchdog(this, msg => ServiceLog(msg));
            Watchdog.Start();
            keepaliveTimer = new System.Threading.Timer(_ => KeepaliveTick(), null,
                TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

            ServiceLog("Projects service started.");
        }

        /// <summary>
        /// Delivers a confirmed stimulus to its destination agent — a Commander wake or a
        /// sub-agent wake, both idempotent (no-op if that agent is already awake).
        /// Paused/budget-paused projects do not wake.
        /// </summary>
        private Task DeliverStimulusAsync(StimulusEnvelope env)
        {
            var project = Store.GetProject(env.ProjectID);
            if (project == null) return Task.CompletedTask;
            if (project.Status != ProjectStatus.Active) return Task.CompletedTask;

            string trigger = $"[{env.SourceKind}] {env.Verdict}\n{env.Payload}";
            if (string.IsNullOrEmpty(env.DestinationAgentID) || env.DestinationAgentID == "commander")
            {
                CommanderRunner.Wake(project, trigger);
                return Task.CompletedTask;
            }

            var agent = SubAgents.ListActive(project.ProjectID).FirstOrDefault(a => a.AgentID == env.DestinationAgentID);
            if (agent == null)
            {
                // Target retired/unknown — surface to the Commander instead of dropping.
                CommanderRunner.Wake(project, $"[undeliverable stimulus for {env.DestinationAgentID}] {trigger}");
                return Task.CompletedTask;
            }
            SubAgentRunner.Wake(project, agent, trigger);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Periodic keepalive: wakes each active project that has been idle, so a project with no
        /// external stimuli still makes forward progress toward its goal (dev note #1). Cheap — a
        /// wake is a no-op if one is already active, and a healthy busy project simply keeps going.
        /// </summary>
        private void KeepaliveTick()
        {
            try
            {
                foreach (var project in Store.ListProjects())
                {
                    if (project.Status != ProjectStatus.Active) continue;
                    var tail = EventLog.ReadTail(project.ProjectID, 20);
                    var lastWake = tail.LastOrDefault(e => e.Type == ProjectEventTypes.CommanderWake);
                    // If nothing has woken it in the last ~15 min, nudge it to reassess and act.
                    if (lastWake == null || DateTime.UtcNow - lastWake.Timestamp > TimeSpan.FromMinutes(14))
                        CommanderRunner.Wake(project, "Periodic keepalive: reassess the plan and make the next concrete progress toward the goal.");
                }
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: keepalive tick failed"); }
        }

        /// <summary>
        /// Recalls from KliveAgent's shared memory. Projects is part of KliveAgent, so a project
        /// agent draws on (and contributes to) the same memory pool as the assistant itself —
        /// Klives' preferences and past learnings transfer across projects.
        /// </summary>
        private async Task<string> RecallKliveAgentMemoriesAsync(string query, int max)
        {
            try
            {
                var svc = await GetServicesByType<KliveAgent.KliveAgent>();
                if (svc == null || svc.Length == 0) return "(memory service unavailable)";
                var memory = ((KliveAgent.KliveAgent)svc[0]).Memory;
                var results = await memory.RecallMemoriesAsync(query, max);
                if (results == null || results.Count == 0) return "No relevant memories.";
                return string.Join("\n", results.Select(m => $"• {m.Content}"));
            }
            catch (Exception ex) { return $"(memory recall failed: {ex.Message})"; }
        }

        private async Task<string> SaveKliveAgentMemoryAsync(string content, string[] tags)
        {
            try
            {
                var svc = await GetServicesByType<KliveAgent.KliveAgent>();
                if (svc == null || svc.Length == 0) return "(memory service unavailable)";
                var memory = ((KliveAgent.KliveAgent)svc[0]).Memory;
                await memory.SaveMemoryAsync(content, tags ?? Array.Empty<string>(), source: "projects", importance: 2);
                return "Saved to shared memory.";
            }
            catch (Exception ex) { return $"(memory save failed: {ex.Message})"; }
        }

        /// <summary>Queries the utility model with a one-shot prompt; used by triage and digest rebuilds.</summary>
        private async Task<string?> QueryUtilityModelAsync(string prompt, string modelOverride)
        {
            var llmServices = await GetServicesByType<KliveLLM.KliveLLM>();
            if (llmServices == null || llmServices.Length == 0) return null;
            var llm = (KliveLLM.KliveLLM)llmServices[0];
            string sid = $"projects-triage-{Guid.NewGuid():N}";
            llm.StartToolSession(sid, null);
            llm.AppendUserMessageToToolSession(sid, prompt);
            var resp = await llm.QueryToolSessionAsync(sid, new List<KliveLLM.HFWrapper.HFTool>(), modelOverride: modelOverride);
            return resp.Success ? resp.Response : null;
        }

        /// <summary>OpenRouter token for the cost fetcher; null when unset (fetcher then no-ops to the estimate).</summary>
        private async Task<string?> GetStringOmniSettingNullable(string name)
        {
            try { return await GetStringOmniSetting(name, defaultValue: null, sensitive: true); }
            catch { return null; }
        }

        /// <summary>
        /// Dispatches one Commander/agent tool call. Non-computer tools go to ProjectCommanderTools;
        /// computer_* tools (video tier only) go to the acting agent's container adapter. Bridges
        /// the runner to the P2 desktop subsystem and P4/P5 hooks without the runner knowing them.
        /// </summary>
        public async Task<CommanderToolResult> CommanderToolDispatch(
            Project project, string actingAgentID, string wakeID, string toolName, string argsJson, CancellationToken ct)
        {
            if (toolName.StartsWith("computer_", StringComparison.Ordinal))
                return await DispatchComputerToolAsync(project, actingAgentID, toolName, argsJson, ct);

            var tools = new ProjectCommanderTools(
                project, EventLog, Digests, SubAgents, Gates, Budget, Vault, Store, actingAgentID, wakeID)
            {
                SendAgentMessageAsync = SendAgentMessageHook,
                // request_human surfaces through the project's own Discord channel when present.
                RequestHumanAsync = DiscordManager == null ? RequestHumanHook
                    : what => DiscordManager.PostReportAsync(project, "Human assistance needed", what),
                HookStore = Hooks,
                RearmAdapters = () => Adapters.ArmAll(),
                Artifacts = Artifacts,
                CompleteProjectAsync = () => CompleteProjectAsync(project),
                RecallMemoriesAsync = RecallKliveAgentMemoriesAsync,
                SaveMemoryAsync = SaveKliveAgentMemoryAsync,
            };
            return await tools.DispatchAsync(toolName, argsJson, ct);
        }

        private async Task<CommanderToolResult> DispatchComputerToolAsync(
            Project project, string actingAgentID, string toolName, string argsJson, CancellationToken ct)
        {
            if (!Settings.Get(project.ProjectID).ContainersEnabled)
                return new CommanderToolResult("This project has containers disabled (a text-only project). Ask Klives to enable containers in project settings if the goal needs a desktop.");
            if (Desktops == null)
                return new CommanderToolResult("Desktop containers are unavailable on this host — computer-use tools cannot run. Use text-tier tools, or ask Klives to enable containers.");
            if (!OperatingSystem.IsWindows())
                return new CommanderToolResult("Desktop control is only wired for the Windows host build.");
            return await DispatchComputerToolWindowsAsync(project, actingAgentID, toolName, argsJson, ct);
        }

        [SupportedOSPlatform("windows")]
        private async Task<CommanderToolResult> DispatchComputerToolWindowsAsync(
            Project project, string actingAgentID, string toolName, string argsJson, CancellationToken ct)
        {
            try
            {
                var adapter = await Desktops!.GetAdapterForAgentAsync(
                    project, actingAgentID,
                    resolveSecretsAsync: text => Task.FromResult(Vault.ResolveSecrets(project.ProjectID, text)),
                    ct: ct);
                var result = await adapter.ExecuteAsync(toolName, argsJson, ct);

                // The screenshot rides the vision path back to the model AND becomes an artifact
                // with a capture-time description (the permanent record once the raw JPEG expires).
                var artifactIDs = new List<string>();
                if (result.Jpeg != null)
                {
                    var art = Artifacts.Save(project.ProjectID, result.Jpeg, "image/jpeg",
                        description: $"Desktop after {toolName} by {actingAgentID}: {result.Text}");
                    artifactIDs.Add(art.ArtifactID);
                }
                return new CommanderToolResult(result.Text) { Jpeg = result.Jpeg, ArtifactIDs = artifactIDs };
            }
            catch (Exception ex) { return new CommanderToolResult($"{toolName} failed: {ex.Message}"); }
        }

        /// <summary>
        /// Completes a project (only reached through the complete_project approval gate):
        /// status flip, Discord archive, desktops released. The event log and artifacts stay.
        /// </summary>
        public async Task CompleteProjectAsync(Project project)
        {
            project.Status = ProjectStatus.Completed;
            project.CompletedAt = DateTime.UtcNow;
            Store.SaveProject(project);
            EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = ProjectEventTypes.Status,
                Author = "system",
                Text = "Project completed. Goal achieved and confirmed by Klives.",
            });
            if (DiscordManager != null)
            {
                try { await DiscordManager.ArchiveChannelAsync(project); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: channel archive failed"); }
            }
            if (Desktops != null && OperatingSystem.IsWindows())
            {
                foreach (var rec in Desktops.Registry.ForProject(project.ProjectID))
                {
                    try { await Desktops.DisposeDesktopAsync(rec.ContainerID); }
                    catch (Exception ex) { _ = ServiceLogError(ex, "Projects: desktop teardown failed"); }
                }
            }
        }

        /// <summary>Post-wake digest refresh + compaction, via the utility model. Never blocks a wake's hot path.</summary>
        public async Task RebuildDigestAfterWakeAsync(Project project, long wakeStartSeq)
        {
            try
            {
                var llmServices = await GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0) return;
                var llm = (KliveLLM.KliveLLM)llmServices[0];
                string utilityModel = TierRouter.GetUtilityModel(project.ProjectID);

                await Digests.RebuildDigestAsync(project, EventLog, async prompt =>
                {
                    string sid = $"projects-digest-{project.ProjectID}-{Guid.NewGuid():N}";
                    llm.StartToolSession(sid, null);
                    llm.AppendUserMessageToToolSession(sid, prompt);
                    var resp = await llm.QueryToolSessionAsync(sid, new List<KliveLLM.HFWrapper.HFTool>(), modelOverride: utilityModel);
                    return resp.Success ? resp.Response : null;
                });

                // Keep the budget line in the digest fresh even if the model didn't restate it.
                var digest = Digests.GetDigest(project.ProjectID);
                digest.BudgetState = Budget.DescribeState(project.ProjectID);
                digest.OrgChart = SubAgents.DescribeOrgChart(project.ProjectID);
                Digests.SaveDigest(digest);
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: post-wake digest rebuild failed (non-fatal)"); }
        }

        /// <summary>
        /// Brings up the per-project Discord integration (P5): channel-per-project, button
        /// approvals racing the website, reply routing, twice-daily reports. Non-fatal if
        /// KliveBotDiscord isn't up yet — the website surface works regardless.
        /// </summary>
        private async Task InitialiseDiscordAsync()
        {
            try
            {
                var services = await GetServicesByType<KliveBotDiscord>();
                if (services == null || services.Length == 0)
                {
                    ServiceLog("Projects: KliveBotDiscord not available — Discord surface disabled (website still works).");
                    return;
                }
                var discord = (KliveBotDiscord)services[0];
                DiscordManager = new ProjectDiscordManager(this, discord, msg => ServiceLog(msg));
                DiscordManager.Initialise();

                // A gate opening posts an approval card to the project's channel; the button press
                // and a website click race to resolve the same gate (first responder wins).
                Gates.GateOpened += gate =>
                {
                    var project = Store.GetProject(gate.ProjectID);
                    if (project != null) _ = DiscordManager!.PostApprovalAsync(project, gate);
                };

                reportScheduler = new ProjectReportScheduler(this, DiscordManager, msg => ServiceLog(msg));
                reportScheduler.Start();
                ServiceLog("Projects: Discord surface initialised.");
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: Discord init failed (non-fatal)"); }
        }

        /// <summary>
        /// Brings up the desktop-container subsystem (P2). Whether a given PROJECT uses containers
        /// is its own per-project setting (ProjectSettings.ContainersEnabled), checked at desktop
        /// creation — so the subsystem itself always comes up (best-effort) as long as the host
        /// can run it. The frame encoder is Windows-only (System.Drawing) and Docker may be
        /// absent, so both are guarded: failure leaves Desktops null and the text tier fully works.
        /// </summary>
        private Task InitialiseDesktopsAsync()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    ServiceLog("Projects: desktop containers are only wired for the Windows host build (frame encoding uses System.Drawing).");
                    return Task.CompletedTask;
                }
                StartDesktops();
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: desktop subsystem init failed (non-fatal)"); }
            return Task.CompletedTask;
        }

        [SupportedOSPlatform("windows")]
        private void StartDesktops()
        {
            var manager = new ContainerDesktopManager(
                msg => ServiceLog(msg),
                imageForProject: pid => Settings.Get(pid).DesktopImage,
                dockerUri: ProjectContainerConfig.ResolveDockerUri());
            Desktops = manager;

            // Screen-diff hooks need the desktop subsystem; hand it to the adapter manager and
            // re-arm so any screen-diff hooks created while desktops were down come alive.
            Adapters.Desktops = manager;
            Adapters.Projects = Store;
            Adapters.Artifacts = Artifacts;
            Adapters.ArmAll();

            _ = Task.Run(async () =>
            {
                try { await manager.ReconcileAsync(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: container reconcile failed"); }
            });

            _ = RegisterScreenStreamRouteAsync();
        }

        /// <summary>
        /// Live-view WebSocket: /projects/containers/screen/stream?containerID=..&amp;fps=..
        /// Pushes JPEG frames from a container's VNC transport, mirroring HostControl's
        /// /kliveagent/screen/stream. Read-only capture, so it coexists with an acting agent.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private async Task RegisterScreenStreamRouteAsync()
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute",
                    "/projects/containers/screen/stream",
                    (Func<HttpListenerContext, WebSocket, NameValueCollection, Profiles.KMProfileManager.KMProfile?, Task>)(async (context, socket, query, user) =>
                    {
                        if (user == null || user.KlivesManagementRank < Profiles.KMProfileManager.KMPermissions.Klives)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        await StreamContainerScreenAsync(socket, query);
                    }),
                    Profiles.KMProfileManager.KMPermissions.Klives);
                ServiceLog("Projects: container screen-stream route registered (/projects/containers/screen/stream).");
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: failed to register container screen-stream route (non-fatal)"); }
        }

        [SupportedOSPlatform("windows")]
        private async Task StreamContainerScreenAsync(WebSocket socket, NameValueCollection query)
        {
            string containerID = query["containerID"] ?? "";
            var transport = Desktops?.GetTransportByContainerID(containerID);
            if (transport == null)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "unknown container", CancellationToken.None); } catch { }
                return;
            }
            int fps = Math.Clamp(int.TryParse(query["fps"], out var f) ? f : ProjectContainerConfig.DefaultStreamFps, 1, 30);
            int delayMs = Math.Max(33, 1000 / fps);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    byte[] jpeg;
                    try
                    {
                        var (bgra, w, h) = await transport.CaptureFrameAsync();
                        jpeg = VncFrameEncoder.EncodeJpeg(bgra, w, h, quality: 45);
                    }
                    catch { await Task.Delay(delayMs); continue; }
                    await socket.SendAsync(new ArraySegment<byte>(jpeg), WebSocketMessageType.Binary, true, CancellationToken.None);
                    await Task.Delay(delayMs);
                }
            }
            catch { /* viewer gone */ }
            finally
            {
                try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }
}
