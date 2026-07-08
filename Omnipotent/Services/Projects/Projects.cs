using DSharpPlus;
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
        /// <summary>Phase 3 server-push: fans the event log out to WebSocket clients (replaces polling).</summary>
        public ProjectEventBroadcaster EventBroadcaster { get; private set; } = null!;
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
        private System.Threading.Timer? discordInitRetryTimer;
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
            EventBroadcaster = new ProjectEventBroadcaster(EventLog, msg => ServiceLog(msg));
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
            // Alert Klives when a project auto-pauses on budget exhaustion (checks DiscordManager at
            // fire time, so it works even if Discord came up after the ledger was created).
            Budget.BudgetPausedRaised += pid =>
            {
                var proj = Store.GetProject(pid);
                if (proj != null && DiscordManager != null)
                    _ = DiscordManager.PostAttentionAsync(proj, "⛔ Budget exhausted — project paused",
                        $"{Budget.DescribeState(pid)}. Approve a budget increase to continue, or leave it paused.");
            };
            TierRouter = new ProjectTierRouter(Settings);
            Gates = new ProjectGateManager(EventLog, msg => ServiceLog(msg));
            SubAgents = new ProjectSubAgentManager(Store, EventLog);
            CommanderRunner = new ProjectCommanderRunner(this);
            SubAgentRunner = new ProjectSubAgentRunner(this);
            Artifacts = new ProjectArtifactStore(msg => ServiceLog(msg));
            // 48h raw-media retention sweep (§7) + idle/orphan container reap, hourly.
            retentionTimer = new System.Threading.Timer(async _ =>
            {
                try { Artifacts.RunRetentionSweep(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: artifact retention sweep failed"); }
                try { await ReapContainersAsync(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: container reap failed"); }
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

            // Wire the email push source (via KliveMail) before arming so email hooks attach at boot.
            // The Discord push source is wired later in InitialiseDiscordAsync once the bot is confirmed up.
            await WireMailStimulusSourceAsync();

            // Replay durable undelivered stimuli, then arm the source adapters.
            try { Bus.Replay(); Adapters.ArmAll(); }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: stimulus replay/arm failed"); }

            routes = new ProjectsRoutes(this);
            await routes.RegisterRoutes();
            await RegisterEventStreamRouteAsync();

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
                    var tail = EventLog.ReadTail(project.ProjectID, 30);

                    // LLM-outage backoff: if recent wakes keep failing (provider down), don't keep
                    // firing a doomed keepalive every 15 min — back off exponentially (cap 4h) so a
                    // sustained outage produces occasional retries, not a steady stream of WakeFailed.
                    var outcomes = tail.Where(e => e.Type is ProjectEventTypes.WakeCompleted or ProjectEventTypes.WakeFailed).ToList();
                    int consecutiveFailures = 0;
                    for (int i = outcomes.Count - 1; i >= 0 && outcomes[i].Type == ProjectEventTypes.WakeFailed; i--) consecutiveFailures++;
                    if (consecutiveFailures >= 2)
                    {
                        var backoff = TimeSpan.FromMinutes(Math.Min(240, 15 * Math.Pow(2, consecutiveFailures - 1)));
                        if (DateTime.UtcNow - outcomes[^1].Timestamp < backoff) continue; // still backing off
                    }

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

        // ── External/agent-facing API (used by the routes AND by KliveAgent's bridge tools) ──

        /// <summary>
        /// Creates a project the same way the /projects/create route does — seeds settings, logs the
        /// init event, creates the Discord channel (best-effort) and fires the first Commander wake.
        /// Public so the interactive KliveAgent assistant can delegate long-running work to an
        /// autonomous project (Projects is part of KliveAgent — same shared memory).
        /// </summary>
        public async Task<Project> CreateProjectAsync(string name, string goal, double tokenBudgetUsd,
            double moneyBudgetUsd, double moneyAutonomousThresholdUsd, int subAgentCap,
            IDictionary<string, string>? settingsPatch = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));
            if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("goal required", nameof(goal));
            if (tokenBudgetUsd <= 0) throw new ArgumentException("tokenBudgetUsd must be > 0 — a Project is a goal AND a budget", nameof(tokenBudgetUsd));

            var p = Store.CreateProject(name, goal, tokenBudgetUsd, moneyBudgetUsd, moneyAutonomousThresholdUsd, subAgentCap);
            var settings = Settings.EnsureCreated(p.ProjectID);
            if (settingsPatch != null)
            {
                try
                {
                    foreach (var kv in settingsPatch)
                    {
                        if (kv.Key.Equals("projectID", StringComparison.OrdinalIgnoreCase)) continue;
                        settings.TrySet(kv.Key, kv.Value ?? "");
                    }
                    Settings.Save(settings);
                }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: applying create-time settings failed (using defaults)"); }
            }
            EventLog.Append(new ProjectEvent
            {
                ProjectID = p.ProjectID,
                Type = ProjectEventTypes.Status,
                Author = "klives",
                Text = $"Project initialised. Goal: {goal} — token budget ${tokenBudgetUsd:0.##}, money budget ${moneyBudgetUsd:0.##} (autonomous ≤ ${moneyAutonomousThresholdUsd:0.##}), agent cap {subAgentCap}.",
            });
            if (DiscordManager != null)
            {
                try { await DiscordManager.CreateProjectChannelAsync(p); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: create Discord channel failed"); }
            }
            // First wake: a project must start itself (matches the route's behaviour).
            CommanderRunner.Wake(p,
                "Project created by Klives just now. Read the goal, form an initial plan (update_plan), " +
                "create the stimulus hooks you need (create_stimulus_hook), and take the first concrete steps.");
            return p;
        }

        /// <summary>
        /// Logs a Klives message to a project and steers/wakes the Commander (lands within a live wake
        /// if one is running). Returns false if the project ID is unknown. Used by /projects/message
        /// and the KliveAgent bridge.
        /// </summary>
        public bool MessageProject(string projectID, string text)
        {
            var project = Store.GetProject(projectID);
            if (project == null) return false;
            EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = ProjectEventTypes.KlivesMessage,
                Author = "klives",
                Text = text,
            });
            if (project.Status == ProjectStatus.Active)
                CommanderRunner.Steer(project, text);
            return true;
        }

        /// <summary>Compact one-line status (status/goal/budget/agents/last-event) for the bridge. Null if unknown.</summary>
        public string? DescribeProjectStatus(string projectID)
        {
            var p = Store.GetProject(projectID);
            if (p == null) return null;
            int agents = SubAgents.ListActive(p.ProjectID).Count;
            var last = EventLog.ReadTail(p.ProjectID, 1).LastOrDefault();
            string lastText = last != null ? $"{last.Type} @ {last.Timestamp:u}" : "no activity";
            return $"[{p.ProjectID}] \"{p.Name}\" — {p.Status}. Goal: {p.Goal}. Budget: {Budget.DescribeState(p.ProjectID)}. Active agents: {agents}. Last event: {lastText}.";
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
                // request_human surfaces through the project's own Discord channel with an @mention —
                // the agent is blocked on a human-only obstacle, so it should actually ping Klives.
                RequestHumanAsync = DiscordManager == null ? RequestHumanHook
                    : what => DiscordManager.PostAttentionAsync(project, "🙋 Human assistance needed", what),
                HookStore = Hooks,
                RearmAdapters = () => Adapters.ArmAll(),
                GetHookArmInfo = hookID => Adapters.GetArmInfo(hookID),
                Artifacts = Artifacts,
                CompleteProjectAsync = () => CompleteProjectAsync(project),
                DisposeAgentDesktopAsync = agentID => DisposeAgentDesktopAsync(project.ProjectID, agentID),
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
                    // First-class timeline event: the ArtifactAdded type existed but was never appended,
                    // so desktop work never registered as "progress" for the watchdog. Emit it now.
                    EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        AgentID = actingAgentID,
                        Type = ProjectEventTypes.ArtifactAdded,
                        Author = actingAgentID == "commander" ? "commander" : "agent",
                        Text = $"Screenshot after {toolName}",
                        ArtifactIDs = new List<string> { art.ArtifactID },
                    });
                }
                return new CommanderToolResult(result.Text) { Jpeg = result.Jpeg, ArtifactIDs = artifactIDs };
            }
            catch (Exception ex)
            {
                // The most common cause of a desktop-tool failure is that Docker isn't running —
                // which otherwise surfaces as an opaque "The operation has timed out." Diagnose it,
                // kick off dependency self-healing in the background (single-flight; installs/starts
                // Docker and builds the image), and hand the agent an actionable message.
                string? daemon = await Desktops!.ProbeDaemonAsync(ct);
                if (daemon != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string? problem = await Desktops.TryBootstrapAsync(ProjectSettings.Defaults.DesktopImage);
                            ServiceLog(problem == null
                                ? "Projects: desktop layer self-healed — Docker up and image present."
                                : $"Projects: desktop self-heal incomplete — {problem}");
                        }
                        catch (Exception bex) { _ = ServiceLogError(bex, "Projects: desktop self-heal failed"); }
                    });
                    return new CommanderToolResult(
                        $"{toolName} can't run: {daemon} Auto-setup has been kicked off (installing/starting Docker if possible — can take several minutes). " +
                        "Continue with text/HTTP/script tools for now and retry a computer_* tool in ~5 minutes; if it still fails, the result will say exactly what's blocking.");
                }
                return new CommanderToolResult($"{toolName} failed: {ex.Message}");
            }
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

        /// <summary>Disposes a specific agent's own desktop container(s), if any. Safe no-op without Desktops.</summary>
        public async Task DisposeAgentDesktopAsync(string projectID, string agentID)
        {
            if (Desktops == null || string.IsNullOrWhiteSpace(agentID)) return;
            try
            {
                foreach (var rec in Desktops.Registry.ForProject(projectID).Where(r => r.AgentID == agentID))
                    await Desktops.DisposeDesktopAsync(rec.ContainerID);
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: retire-time desktop dispose failed"); }
        }

        /// <summary>
        /// Hourly container reap (§ resource hygiene): prunes orphaned Lost registry records and
        /// tears down desktops that are no longer needed — those owned by a retired agent, belonging
        /// to a finished project, or on a project idle beyond the reap window (recreated on next use).
        /// Never touches a project with a live wake. Idle window via Projects_ContainerIdleReapHours
        /// (host-global resource policy; 0 disables idle reaping).
        /// </summary>
        private async Task ReapContainersAsync()
        {
            if (Desktops == null) return;
            try
            {
                var reg = Desktops.Registry;
                // 1. Prune orphaned Lost records so they don't accumulate forever.
                foreach (var lost in reg.All().Where(r => r.Lost))
                    reg.Remove(lost.ContainerID);

                int idleHours = await GetIntOmniSetting("Projects_ContainerIdleReapHours", 6);
                foreach (var project in Store.ListProjects())
                {
                    var containers = reg.ForProject(project.ProjectID);
                    if (containers.Count == 0) continue;

                    var digest = Digests.GetDigest(project.ProjectID);
                    if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID)) continue; // never reap a live wake

                    bool finished = project.Status is ProjectStatus.Completed or ProjectStatus.Archived;
                    var lastEvt = EventLog.ReadTail(project.ProjectID, 1).LastOrDefault();
                    bool idle = idleHours > 0 && lastEvt != null &&
                                DateTime.UtcNow - lastEvt.Timestamp > TimeSpan.FromHours(idleHours);
                    var activeIDs = new HashSet<string>(
                        SubAgents.ListActive(project.ProjectID).Select(a => a.AgentID), StringComparer.Ordinal);

                    foreach (var c in containers)
                    {
                        bool ownerRetired = c.AgentID != null && !activeIDs.Contains(c.AgentID);
                        if (finished || idle || ownerRetired)
                        {
                            try { await Desktops.DisposeDesktopAsync(c.ContainerID); }
                            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: container reap dispose failed"); }
                        }
                    }
                }
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: container reap failed"); }
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
            if (DiscordManager != null) return; // already initialised (idempotent for the retry timer)
            try
            {
                var services = await GetServicesByType<KliveBotDiscord>();
                if (services == null || services.Length == 0)
                {
                    ServiceLog("Projects: KliveBotDiscord not available yet — will retry Discord init periodically.");
                    ScheduleDiscordInitRetry();
                    return;
                }
                var discord = (KliveBotDiscord)services[0];
                DiscordManager = new ProjectDiscordManager(this, discord, msg => ServiceLog(msg));
                DiscordManager.Initialise();

                // Wire the Discord push stimulus source so 'discord' hooks observe real messages,
                // then re-arm so any existing discord hooks attach to the now-live source.
                Adapters.DiscordSource = handler =>
                {
                    Task OnMsg(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs args)
                    {
                        try
                        {
                            _ = handler(new InboundDiscordStimulus
                            {
                                ChannelId = args.Channel.Id.ToString(),
                                AuthorId = args.Author.Id.ToString(),
                                AuthorName = args.Author.Username,
                                Content = args.Message.Content ?? "",
                                IsPrivate = args.Channel.IsPrivate,
                            });
                        }
                        catch { }
                        return Task.CompletedTask;
                    }
                    discord.Client.MessageCreated += OnMsg;
                    return new ActionDisposable(() => { try { discord.Client.MessageCreated -= OnMsg; } catch { } });
                };
                try { Adapters.ArmAll(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: re-arm after Discord source wiring failed"); }

                // A gate opening posts an approval card to the project's channel; the button press
                // and a website click race to resolve the same gate (first responder wins).
                Gates.GateOpened += gate =>
                {
                    var project = Store.GetProject(gate.ProjectID);
                    if (project != null) _ = DiscordManager!.PostApprovalAsync(project, gate);
                };

                reportScheduler = new ProjectReportScheduler(this, DiscordManager, msg => ServiceLog(msg));
                reportScheduler.Start();
                // Success — stop retrying if a retry timer was running.
                discordInitRetryTimer?.Dispose();
                discordInitRetryTimer = null;
                ServiceLog("Projects: Discord surface initialised.");
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: Discord init failed (non-fatal)"); }
        }

        /// <summary>Retries Discord init periodically until KliveBotDiscord is available (it may start after us).</summary>
        private void ScheduleDiscordInitRetry()
        {
            if (discordInitRetryTimer != null) return; // already scheduled
            discordInitRetryTimer = new System.Threading.Timer(async _ =>
            {
                if (DiscordManager != null) { discordInitRetryTimer?.Dispose(); discordInitRetryTimer = null; return; }
                try { await InitialiseDiscordAsync(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: Discord init retry failed"); }
            }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        /// <summary>
        /// Wires the email push stimulus source: subscribes to KliveMail's inbound-mail event and
        /// exposes a subscribe factory the adapter manager uses to fan out to 'email' hooks. No-op if
        /// KliveMail isn't available (email hooks then arm in the Error state, visible to Klives).
        /// </summary>
        private async Task WireMailStimulusSourceAsync()
        {
            try
            {
                var services = await GetServicesByType<KliveMail.KliveMail>();
                if (services == null || services.Length == 0)
                {
                    ServiceLog("Projects: KliveMail not available — email stimulus hooks will be inert.");
                    return;
                }
                var mail = (KliveMail.KliveMail)services[0];
                Adapters.MailSource = handler =>
                {
                    Action<KliveMail.Models.StoredMessage> h = m =>
                    {
                        _ = handler(new InboundMailStimulus
                        {
                            To = m.ToAddress,
                            From = m.FromAddress,
                            Subject = m.Subject ?? "",
                            BodyPreview = m.BodyText ?? StripHtml(m.BodyHtml),
                        });
                    };
                    mail.MailStored += h;
                    return new ActionDisposable(() => { try { mail.MailStored -= h; } catch { } });
                };
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: failed to wire KliveMail stimulus source"); }
        }

        private static string StripHtml(string? html)
            => string.IsNullOrEmpty(html) ? "" : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Trim();

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
                // Self-heal the desktop layer at startup: if the Docker daemon is unreachable the
                // bootstrapper installs/starts Docker Desktop itself (winget → launch → wait) and
                // auto-builds the desktop image when missing — instead of leaving computer_* tools
                // to fail with opaque timeouts until a human intervenes.
                try
                {
                    string? problem = await manager.TryBootstrapAsync(ProjectSettings.Defaults.DesktopImage);
                    if (problem != null)
                        ServiceLog($"Projects: DESKTOP CONTAINERS NOT READY — {problem}");
                    else
                        ServiceLog("Projects: desktop layer ready (Docker daemon up, desktop image present).");
                }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: desktop bootstrap failed"); }

                try { await manager.ReconcileAsync(); }
                catch (Exception ex) { _ = ServiceLogError(ex, "Projects: container reconcile failed"); }
            });

            _ = RegisterScreenStreamRouteAsync();
        }

        /// <summary>
        /// Authorizes a Projects WebSocket connection as Klives. Browsers cannot set an Authorization
        /// HEADER on a WebSocket, so KliveAPI's header-based gate can never pass for a browser client —
        /// WS routes must register as Anybody and check the ?authorization= password here (the exact
        /// pattern HostControl's /kliveagent/screen/stream uses). This was the root cause of the live
        /// desktop never connecting: the route was registered Klives-gated, so every browser got 401.
        /// </summary>
        private async Task<bool> AuthorizeWsAsKlivesAsync(NameValueCollection query, Profiles.KMProfileManager.KMProfile? user)
        {
            var resolved = user;
            if (resolved == null)
            {
                string? pw = query["authorization"];
                if (!string.IsNullOrEmpty(pw))
                    resolved = await ExecuteServiceMethod<Profiles.KMProfileManager>("GetProfileByPassword", pw) as Profiles.KMProfileManager.KMProfile;
            }
            return resolved != null && resolved.KlivesManagementRank >= Profiles.KMProfileManager.KMPermissions.Klives;
        }

        /// <summary>
        /// Registers the event-stream WebSocket (Phase 3 push): /projects/events/stream?projectID=..&amp;since=..
        /// Per-project subscribers get every ProjectEvent (replay-after-cursor then live); a fleet
        /// subscriber (no projectID) gets a lightweight signal on any project's event. Registered as
        /// Anybody at the KliveAPI layer, authorized in-handler (see AuthorizeWsAsKlivesAsync).
        /// </summary>
        private async Task RegisterEventStreamRouteAsync()
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute",
                    "/projects/events/stream",
                    (Func<HttpListenerContext, WebSocket, NameValueCollection, Profiles.KMProfileManager.KMProfile?, Task>)(async (context, socket, query, user) =>
                    {
                        if (!await AuthorizeWsAsKlivesAsync(query, user))
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        string? projectID = query["projectID"];
                        long since = long.TryParse(query["since"], out var s) ? s : 0;
                        await EventBroadcaster.HandleAsync(socket, projectID, since,
                            (pid, sinceExclusive) => EventLog.ReadSince(pid, sinceExclusive));
                    }),
                    Profiles.KMProfileManager.KMPermissions.Anybody);
                ServiceLog("Projects: event-stream route registered (/projects/events/stream).");
            }
            catch (Exception ex) { _ = ServiceLogError(ex, "Projects: failed to register event-stream route (non-fatal)"); }
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
                        // Anybody at the KliveAPI layer + in-handler Klives auth: browsers can't send an
                        // Authorization header on a WebSocket (see AuthorizeWsAsKlivesAsync).
                        if (!await AuthorizeWsAsKlivesAsync(query, user))
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        await StreamContainerScreenAsync(socket, query);
                    }),
                    Profiles.KMProfileManager.KMPermissions.Anybody);
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
