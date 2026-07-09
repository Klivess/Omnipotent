using System.Collections.Concurrent;
using System.Text;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Executes sub-agent wakes — the fleet half of §6. A sub-agent is woken by a directed
    /// stimulus (usually a Commander message riding the bus), runs a bounded tool loop on the
    /// model its TIER routes to, with only the tools its tier gates open, and reports back to the
    /// Commander via send_agent_message when done. Same rehydrate-on-wake discipline as the
    /// Commander: no persistent conversation, seeded fresh from the log each wake.
    ///
    /// Single-flight per agent (in-memory — a restart clears it, and the durable stimulus queue
    /// re-delivers whatever the interrupted wake never acked).
    /// </summary>
    public class ProjectSubAgentRunner
    {
        private readonly Projects parent;
        private readonly ConcurrentDictionary<string, bool> activeWakes = new(StringComparer.Ordinal); // key: projectID/agentID

        // Triggers/messages that arrived for a sub-agent while it was already awake. Parity with the
        // Commander: a directed stimulus to a busy sub-agent must not vanish. Drained at each tool-loop
        // turn boundary (so it lands mid-wake, like Commander steering) and, for any leftover at the
        // finish/enqueue race, folded into a follow-up wake. Keyed by projectID/agentID.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> steerQueue = new(StringComparer.Ordinal);

        private const int MaxToolCallsPerWake = 30; // sub-agents do focused legwork, not strategy
        private const int StuckIdenticalCallThreshold = 3;
        private const int RecentEventsForSeed = 30;
        private const int MaxPendingTriggers = 12;

        public ProjectSubAgentRunner(Projects parent)
        {
            this.parent = parent;
        }

        private static string Key(string projectID, string agentID) => $"{projectID}/{agentID}";

        /// <summary>
        /// Wakes a sub-agent for a directed stimulus. If it is already awake, the trigger is queued
        /// (deduped, capped) so the running wake picks it up at its next turn boundary and, failing
        /// that, a follow-up wake fires — no directed message is dropped. Returns the wake ID, or null
        /// if it was queued onto an active wake.
        /// </summary>
        public string? Wake(Project project, ProjectAgentRecord agent, string trigger)
        {
            string key = Key(project.ProjectID, agent.AgentID);
            if (!activeWakes.TryAdd(key, true))
            {
                var q = steerQueue.GetOrAdd(key, _ => new ConcurrentQueue<string>());
                if (q.Count < MaxPendingTriggers && !q.Contains(trigger)) q.Enqueue(trigger);
                return null; // already awake — folded into the live wake / a follow-up
            }

            string wakeID = Guid.NewGuid().ToString("N");
            parent.EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                WakeID = wakeID,
                AgentID = agent.AgentID,
                Type = ProjectEventTypes.AgentWake,
                Author = "system",
                Text = $"Agent {agent.AgentID} ({agent.Role}) woke. Trigger: {Trunc(trigger, 200)}",
            });
            _ = Task.Run(async () =>
            {
                try { await ExecuteWakeAsync(project, agent, wakeID, trigger); }
                finally
                {
                    activeWakes.TryRemove(key, out _);
                    DrainPendingSteers(project, agent);
                }
            });
            return wakeID;
        }

        /// <summary>Re-wakes the sub-agent with any steers stranded by the finish/enqueue race.</summary>
        private void DrainPendingSteers(Project project, ProjectAgentRecord agent)
        {
            try
            {
                if (!steerQueue.TryRemove(Key(project.ProjectID, agent.AgentID), out var q) || q.IsEmpty) return;
                var refreshed = parent.Store.GetProject(project.ProjectID);
                if (refreshed == null || refreshed.Status != ProjectStatus.Active) return;
                var missed = q.ToList().Distinct().ToList();
                if (missed.Count == 0) return;
                Wake(refreshed, agent, missed.Count == 1 ? missed[0]
                    : "Messages that arrived while you were awake:\n\n" + string.Join("\n\n", missed));
            }
            catch { /* never mask the wake outcome */ }
        }

        private async Task ExecuteWakeAsync(Project project, ProjectAgentRecord agent, string wakeID, string trigger)
        {
            string projectID = project.ProjectID;
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = $"Agent {agent.AgentID} finished its wake.";
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
            using var cts = new CancellationTokenSource();

            try
            {
                var llmServices = await parent.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llmServices[0];

                var settings = parent.Settings.Get(projectID);
                string model = settings.ModelForTier(agent.Tier);
                bool visionEnabled = agent.Tier != ProjectAgentTier.Text && settings.VisionEnabled;

                // Tier-gated tools: core set filtered by the router, plus computer-use when the
                // tier's perception supports it (§6.1 — the tool gating half of the tier system).
                var toolDefs = ProjectCommanderAgent.BuildCoreToolDefinitions()
                    .Where(t => parent.TierRouter.IsToolAllowed(agent.Tier, t.function.name)
                             && !ProjectTierRouter.IsCommanderOnly(t.function.name))
                    .ToList();
                toolDefs.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions()
                    .Where(t => parent.TierRouter.IsToolAllowed(agent.Tier, t.function.name)));

                string sessionId = $"projects-agent-{projectID}-{agent.AgentID}-{wakeID}";
                llm.StartToolSession(sessionId, BuildSystemPrompt(project, agent));
                llm.AppendUserMessageToToolSession(sessionId, await BuildWakeSeed(project, agent, trigger));

                var recentSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
                int toolCalls = 0;

                string steerKey = Key(projectID, agent.AgentID);

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // Budget guardrail (parity with the Commander): stop this sub-agent wake if the project
                    // was paused (e.g. token budget exhausted) since the last turn, before the next LLM call.
                    if (parent.Store.GetProject(projectID)?.Status == ProjectStatus.BudgetPaused)
                    {
                        outcomeText = $"Agent {agent.AgentID} stopped — project budget paused.";
                        break;
                    }

                    // Fold in any messages that arrived mid-wake (parity with Commander steering),
                    // only at a turn boundary so tool_call/tool_result pairing stays valid.
                    if (steerQueue.TryGetValue(steerKey, out var sq))
                        while (sq.TryDequeue(out var steer))
                            llm.AppendUserMessageToToolSession(sessionId, $"NEW MESSAGE (mid-wake — take this into account now): {steer}");

                    if (toolCalls >= MaxToolCallsPerWake)
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"WAKE TOOL BUDGET REACHED ({MaxToolCallsPerWake}). Stop and report your status to the commander via send_agent_message, then reply with a one-line summary.");

                    var resp = await llm.QueryToolSessionAsync(sessionId, toolDefs, modelOverride: model, cancellationToken: cts.Token);
                    if (!resp.Success) throw new Exception($"LLM query failed: {resp.ErrorMessage}");

                    if (resp.PromptTokens > 0 || resp.CompletionTokens > 0)
                    {
                        wakePromptTokens += resp.PromptTokens;
                        wakeCompletionTokens += resp.CompletionTokens;
                        wakeCostUsd += resp.CostUsd ?? parent.Budget.EstimateCost(resp.PromptTokens, resp.CompletionTokens);
                        await parent.Budget.RecordTokenSpendAsync(projectID, resp.PromptTokens, resp.CompletionTokens, resp.GenerationId, resp.CostUsd);
                    }

                    bool overBudget = toolCalls >= MaxToolCallsPerWake;
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0 || overBudget)
                    {
                        string final = string.IsNullOrWhiteSpace(resp.Response) ? "(no closing summary)" : resp.Response.Trim();
                        parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, ProjectEventTypes.AgentMessage, final));
                        break;
                    }

                    // Persist the sub-agent's intermediate reasoning (parity with the Commander's
                    // CommanderThought) so a human can reconstruct why it acted, not just what it did.
                    if (!string.IsNullOrWhiteSpace(resp.Response))
                        parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, ProjectEventTypes.AgentThought, resp.Response.Trim()));

                    foreach (var call in resp.ToolCalls)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string toolName = call.function?.name ?? "";
                        string argsJson = call.function?.arguments ?? "";
                        toolCalls++;

                        // Tier gating enforced at dispatch too, not just in the offered tool list.
                        if (ProjectTierRouter.IsCommanderOnly(toolName))
                        {
                            llm.AppendToolResult(sessionId, call.id, toolName, $"'{toolName}' is the commander's decision, not yours. Recommend it via send_agent_message instead.");
                            continue;
                        }
                        if (!parent.TierRouter.IsToolAllowed(agent.Tier, toolName) && !toolName.StartsWith("computer_"))
                        {
                            llm.AppendToolResult(sessionId, call.id, toolName, $"Tool '{toolName}' is not available at your tier ({agent.Tier}).");
                            continue;
                        }
                        if (toolName.StartsWith("computer_") && !parent.TierRouter.IsToolAllowed(agent.Tier, toolName))
                        {
                            llm.AppendToolResult(sessionId, call.id, toolName, $"Computer control requires a video-tier agent; you are {agent.Tier}. Ask the commander to respawn you at a higher tier if the job truly needs a desktop.");
                            continue;
                        }

                        string sig = toolName + "|" + argsJson;
                        recentSignatures[sig] = recentSignatures.TryGetValue(sig, out var n) ? n + 1 : 1;
                        if (recentSignatures[sig] >= StuckIdenticalCallThreshold)
                        {
                            llm.AppendToolResult(sessionId, call.id, toolName,
                                $"LOOP DETECTED: identical {toolName} call {recentSignatures[sig]}×. Change approach or report the blocker to the commander.");
                            continue;
                        }

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = agent.AgentID,
                            Type = ProjectEventTypes.ToolCall, Author = "agent",
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id, PayloadJson = argsJson,
                        });

                        var result = await parent.CommanderToolDispatch(project, agent.AgentID, wakeID, toolName, argsJson, cts.Token);

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = agent.AgentID,
                            Type = ProjectEventTypes.ToolResult, Author = "agent",
                            Text = result.ResultText, ToolName = toolName, ToolCallId = call.id,
                            ArtifactIDs = result.ArtifactIDs,
                        });
                        llm.AppendToolResult(sessionId, call.id, toolName, result.ResultText);

                        if (visionEnabled && result.Jpeg != null)
                        {
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Screenshot after your {toolName} call. Verify before continuing.",
                                new List<(byte[] data, string mimeType)> { (result.Jpeg, "image/jpeg") });
                        }
                        if (result.EndWake) goto done;
                    }
                }
                done: ;
            }
            catch (Exception ex)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = $"Agent {agent.AgentID} wake failed: {ex.Message}";
            }
            finally
            {
                if (wakePromptTokens > 0 || wakeCompletionTokens > 0)
                    outcomeText += $" (this wake: ~${wakeCostUsd:0.###}, {wakePromptTokens + wakeCompletionTokens} tokens)";
                try { parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, outcome, outcomeText)); }
                catch { }
            }
        }

        private static string BuildSystemPrompt(Project project, ProjectAgentRecord agent)
        {
            // Only video-tier sub-agents actually get a desktop container; entice them to live on it.
            string desktopNote = ProjectTierRouter.TierGetsDesktop(agent.Tier)
                ? @"
- YOUR DESKTOP is a real computer that's yours — use it, don't just poke at it. Open a browser and actually browse, install and use the right GUI app for the task (you have passwordless sudo: `sudo apt-get update && sudo apt-get install <package>` in the terminal), organise your work into real files and folders, and keep the machine tidy — even personalise it (yes, the wallpaper) if it helps you own it. The GUI is often the shortest, most reliable path, since so many tools and sites are built for a human at a screen — which is exactly what you are equipped to be. Anything that must outlive the machine goes in /project."
                : "";

            return
$@"You are a {agent.Tier}-tier SUB-AGENT (role: {agent.Role}, ID: {agent.AgentID}) in an autonomous project task force. The COMMANDER assigns you work; you do focused legwork and report back.

THE PROJECT'S GOAL (context, not your whole job): {project.Goal}

RULES:
- Do the specific task in your trigger message. Don't expand scope — the commander owns strategy.
- Work with your tools, verify results, then send your findings to the commander with send_agent_message(agentID: ""commander"", message: ...) BEFORE you finish. An unreported result is a wasted wake.
- If blocked, report the blocker rather than spinning. If an action needs approval or spends money, that's the commander's call — report it as a recommendation.
- When your work changes a tracked number, update the matching Observable (update_observable) so Klives' live dashboard stays current.{desktopNote}
- Be concise and factual. Everything you do is on a timeline Klives watches.";
        }

        private async Task<string> BuildWakeSeed(Project project, ProjectAgentRecord agent, string trigger)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            var sb = new StringBuilder();
            sb.AppendLine("── PROJECT PLAN (commander's, for context) ──");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(digest.CurrentPlan is { Length: > 0 } p ? p : "(none)", 400));

            // Live observable values (Klives' dashboard) — same block the Commander sees.
            string observables = "";
            try { observables = parent.Observables.DescribeAll(project.ProjectID); } catch { }
            if (!string.IsNullOrWhiteSpace(observables))
            {
                sb.AppendLine("── OBSERVABLES (live values shown to Klives; keep yours current via update_observable) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(observables, ProjectsContextBudget.ObservablesBudget));
            }

            // Thin cross-system knowledge leg (KliveRAG), keyed by role + task; own project excluded.
            if (parent.WakeCycle.KnowledgeSearchAsync != null)
            {
                try
                {
                    string kq = $"{agent.Role} {ProjectsContextBudget.TruncateToTokens(trigger, 200)}";
                    var kHits = await parent.WakeCycle.KnowledgeSearchAsync(kq, project.ProjectID);
                    if (kHits is { Count: > 0 })
                    {
                        sb.AppendLine("── RELEVANT KNOWLEDGE (Klives' knowledge base) ──");
                        var fitted = ProjectsContextBudget.FitItemsInBudget(
                            kHits, ProjectsContextBudget.SubAgentKnowledgeBudget, h => h.Text, h => h.Score);
                        foreach (var h in fitted)
                            sb.AppendLine($"[{h.Source}] {ProjectsContextBudget.TruncateToTokens(h.Text, 160)} (doc:{h.DocId})");
                    }
                }
                catch { /* best-effort */ }
            }

            // This agent's own recent activity, so consecutive wakes have continuity.
            var mine = parent.EventLog.ReadTail(project.ProjectID, 400)
                .Where(e => e.AgentID == agent.AgentID)
                .TakeLast(RecentEventsForSeed)
                .ToList();
            if (mine.Count > 0)
            {
                sb.AppendLine("── YOUR RECENT ACTIVITY ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    mine.Select((e, i) => (evt: e, idx: mine.Count - 1 - i)),
                    ProjectsContextBudget.RecentEventsBudget / 2,
                    x => ProjectCommanderPrompts.DescribeEvent(x.evt),
                    x => 1.0 / (1.0 + x.idx));
                foreach (var x in fitted.OrderBy(x => x.evt.Sequence))
                    sb.AppendLine(ProjectCommanderPrompts.DescribeEvent(x.evt));
            }

            sb.AppendLine("── YOUR TASK (this wake's trigger) ──");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(trigger, ProjectsContextBudget.StimulusBudget));
            return sb.ToString();
        }

        private static ProjectEvent Evt(string projectID, string wakeID, string agentID, string type, string text) => new()
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = agentID, Type = type, Author = "agent", Text = text,
        };

        private static string DescribeCall(string toolName, string argsJson)
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                var bits = jo.Properties().Take(4).Select(p => $"{p.Name}={Trunc(p.Value.ToString(), 60)}");
                return $"{toolName}({string.Join(", ", bits)})";
            }
            catch { return toolName; }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
