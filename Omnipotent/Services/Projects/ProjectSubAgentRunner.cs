using System.Collections.Concurrent;
using System.Text;
using Omnipotent.Services.ComputerControl;

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
        private sealed record ActiveWake(string WakeID, CancellationTokenSource Cancellation);
        private readonly ConcurrentDictionary<string, ActiveWake> activeWakes = new(StringComparer.Ordinal); // key: projectID/agentID
        private readonly ConcurrentDictionary<string, object> wakeGates = new(StringComparer.Ordinal);

        // Triggers/messages that arrived for a sub-agent while it was already awake. Parity with the
        // Commander: a directed stimulus to a busy sub-agent must not vanish. Drained at each tool-loop
        // turn boundary (so it lands mid-wake, like Commander steering) and, for any leftover at the
        // finish/enqueue race, folded into a follow-up wake. Keyed by projectID/agentID.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> steerQueue = new(StringComparer.Ordinal);

        private const int MaxToolCallsPerWake = 60; // sub-agents do focused legwork, not strategy
        private const int StuckIdenticalCallThreshold = 3;
        private const int RecentEventsForSeed = 30;
        private const int MaxPendingTriggers = 12;
        // Parity with the Commander: a worker cut off by its tool budget mid-task chains straight
        // into a continuation wake instead of sitting dead until the Commander notices. The cap
        // paces a never-converging task; natural completion resets the streak.
        private const int MaxConsecutiveContinuations = 4;
        private readonly ConcurrentDictionary<string, int> consecutiveContinuations = new(StringComparer.Ordinal);

        public ProjectSubAgentRunner(Projects parent)
        {
            this.parent = parent;
        }

        private static string Key(string projectID, string agentID) => $"{projectID}/{agentID}";
        private object WakeGate(string key) => wakeGates.GetOrAdd(key, _ => new object());

        /// <summary>
        /// Wakes a sub-agent for a directed stimulus. If it is already awake, the trigger is queued
        /// (deduped, capped) so the running wake picks it up at its next turn boundary and, failing
        /// that, a follow-up wake fires — no directed message is dropped. Returns the wake ID that
        /// accepted the trigger (including the current wake when live steering accepted it), or null
        /// when the agent is unavailable or its bounded steering queue is full.
        /// </summary>
        public string? Wake(Project project, ProjectAgentRecord agent, string trigger, bool queueIfBusy = true)
        {
            if (project.Status != ProjectStatus.Active || agent.Retired) return null;
            string key = Key(project.ProjectID, agent.AgentID);
            ActiveWake active;
            lock (WakeGate(key))
            {
                if (activeWakes.TryGetValue(key, out var current))
                {
                    if (!queueIfBusy) return null;
                    var q = steerQueue.GetOrAdd(key, _ => new ConcurrentQueue<string>());
                    if (q.Contains(trigger)) return current.WakeID;
                    if (q.Count >= MaxPendingTriggers) return null;
                    q.Enqueue(trigger);
                    return current.WakeID;
                }

                active = new ActiveWake(Guid.NewGuid().ToString("N"), new CancellationTokenSource());
                activeWakes[key] = active;
            }

            try
            {
                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = project.ProjectID,
                    WakeID = active.WakeID,
                    AgentID = agent.AgentID,
                    Type = ProjectEventTypes.AgentWake,
                    Author = "system",
                    Text = $"Agent {agent.AgentID} ({agent.Role}) woke. Trigger: {Trunc(trigger, 200)}",
                });
            }
            catch
            {
                lock (WakeGate(key))
                    activeWakes.TryRemove(new KeyValuePair<string, ActiveWake>(key, active));
                active.Cancellation.Dispose();
                throw;
            }
            _ = Task.Run(async () =>
            {
                bool continueAfterBudget = false;
                try { continueAfterBudget = await ExecuteWakeAsync(project, agent, active.WakeID, trigger, active.Cancellation); }
                finally { FinishWake(project, agent, key, active, continueAfterBudget); }
            });
            return active.WakeID;
        }

        /// <summary>Atomically closes the active slot and captures any steering that missed the
        /// final model-turn boundary, then starts one follow-up wake for it. A budget-capped wake
        /// with nothing queued chains into an immediate continuation (momentum must not wait for
        /// the Commander to notice the worker went quiet).</summary>
        private void FinishWake(Project project, ProjectAgentRecord agent, string key, ActiveWake active, bool continueAfterBudget)
        {
            ConcurrentQueue<string>? pending = null;
            try
            {
                lock (WakeGate(key))
                {
                    activeWakes.TryRemove(new KeyValuePair<string, ActiveWake>(key, active));
                    steerQueue.TryRemove(key, out pending);
                }
                active.Cancellation.Dispose();
                if (!continueAfterBudget) consecutiveContinuations.TryRemove(key, out _);

                var refreshed = parent.Store.GetProject(project.ProjectID);
                if (refreshed == null || refreshed.Status != ProjectStatus.Active) return;

                var missed = pending == null ? new List<string>() : pending.ToList().Distinct().ToList();
                if (missed.Count > 0)
                {
                    Wake(refreshed, agent, missed.Count == 1 ? missed[0]
                        : "Messages that arrived while you were awake:\n\n" + string.Join("\n\n", missed));
                    return;
                }

                if (!continueAfterBudget) return;
                int streak = consecutiveContinuations.AddOrUpdate(key, 1, (_, n) => n + 1);
                if (streak > MaxConsecutiveContinuations) return; // the Commander picks it up from the report
                Wake(refreshed, agent,
                    "Continuation: your previous wake ended at its tool-call budget mid-task, not because the work was done. " +
                    "Resume immediately from your recent activity, finish the task, and report to the commander with send_agent_message.");
            }
            catch { /* never mask the wake outcome */ }
        }

        public int CancelProject(string projectID)
        {
            int cancelled = 0;
            string prefix = projectID + "/";
            foreach (var kv in activeWakes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                try { kv.Value.Cancellation.Cancel(); cancelled++; } catch { }
                steerQueue.TryRemove(kv.Key, out _);
            }
            return cancelled;
        }

        public bool CancelAgent(string projectID, string agentID)
        {
            string key = Key(projectID, agentID);
            steerQueue.TryRemove(key, out _);
            if (!activeWakes.TryGetValue(key, out var active)) return false;
            try { active.Cancellation.Cancel(); return true; } catch { return false; }
        }

        /// <summary>Runs one bounded wake. Returns true when the wake ended at its tool budget
        /// mid-task (and completed cleanly) — the signal for FinishWake to chain a continuation.</summary>
        private async Task<bool> ExecuteWakeAsync(Project project, ProjectAgentRecord agent, string wakeID, string trigger, CancellationTokenSource cts)
        {
            string projectID = project.ProjectID;
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = $"Agent {agent.AgentID} finished its wake.";
            bool endedAtToolBudget = false;
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
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
                    if (parent.Store.GetProject(projectID)?.Status != ProjectStatus.Active ||
                        parent.SubAgents.ListActive(projectID).All(a => a.AgentID != agent.AgentID))
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
                            $"WAKE TOOL BUDGET REACHED ({MaxToolCallsPerWake}). Stop and report your status to the commander via send_agent_message, then reply with a one-line summary of exactly where you stopped. A continuation wake follows automatically if the task is unfinished.");

                    var budgetLease = await parent.Budget.TryAcquireLlmTurnAsync(projectID, cts.Token);
                    if (budgetLease == null)
                    {
                        outcomeText = $"Agent {agent.AgentID} stopped before the next model call because no token budget remained.";
                        break;
                    }
                    KliveLLM.KliveLLM.KliveLLMResponse resp;
                    try
                    {
                    resp = await llm.QueryToolSessionAsync(sessionId, toolDefs, modelOverride: model, cancellationToken: cts.Token);
                    if (!resp.Success) throw new Exception($"LLM query failed: {resp.ErrorMessage}");

                    if (resp.PromptTokens > 0 || resp.CompletionTokens > 0)
                    {
                        wakePromptTokens += resp.PromptTokens;
                        wakeCompletionTokens += resp.CompletionTokens;
                        wakeCostUsd += resp.CostUsd ?? parent.Budget.EstimateCost(resp.PromptTokens, resp.CompletionTokens);
                        await parent.Budget.RecordTokenSpendAsync(projectID, resp.PromptTokens, resp.CompletionTokens, resp.GenerationId, resp.CostUsd);
                    }

                    }
                    finally { await budgetLease.DisposeAsync(); }

                    bool overBudget = toolCalls >= MaxToolCallsPerWake;
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0 || overBudget)
                    {
                        endedAtToolBudget = overBudget;
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
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id,
                            PayloadJson = toolName.StartsWith("computer_", StringComparison.Ordinal) ? null : argsJson,
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
                            var frames = result.Frames.Count > 0
                                ? result.Frames.Select(f => (f.Jpeg, "image/jpeg")).ToList()
                                : new List<(byte[] data, string mimeType)> { (result.Jpeg, "image/jpeg") };
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Visual result after {toolName} (oldest to newest). The final frame is current and gridded; verify before continuing.",
                                frames);
                        }
                        if (result.EndWake) goto done;
                    }
                }
                done: ;
            }
            catch (OperationCanceledException)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = $"Agent {agent.AgentID} wake cancelled because the project or agent was stopped.";
            }
            catch (Exception ex)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = $"Agent {agent.AgentID} wake failed: {ex.Message}";
            }
            finally
            {
                try { if (parent.Desktops != null) await parent.Desktops.ReleaseAgentInputsAsync(projectID, agent.AgentID); } catch { }
                if (wakePromptTokens > 0 || wakeCompletionTokens > 0)
                    outcomeText += $" (this wake: ~${wakeCostUsd:0.###}, {wakePromptTokens + wakeCompletionTokens} tokens)";
                try { parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, outcome, outcomeText)); }
                catch { }
                parent.StimulusQueue.AcknowledgeWake(wakeID, outcome == ProjectEventTypes.WakeCompleted);
            }
            return endedAtToolBudget && outcome == ProjectEventTypes.WakeCompleted;
        }

        private static string BuildSystemPrompt(Project project, ProjectAgentRecord agent)
        {
            // Only video-tier sub-agents actually get a desktop container; entice them to live on it.
            string desktopNote = ProjectTierRouter.TierGetsDesktop(agent.Tier)
                ? @"
- YOUR DESKTOP is a real computer that's yours — use it, don't just poke at it. Open a browser and actually browse, install and use the right GUI app for the task, organise your work into real files and folders, and keep the machine tidy. Use computer_terminal for commands inside your isolated Linux desktop (`sudo apt-get ...`, pip/venv, git, tests) rather than typing shell commands through VNC; it defaults to persistent /project and works even during a visual-frame outage. Use computer_type only for actual GUI fields. Anything that must outlive the machine goes in /project."
                : "";

            return
$@"You are a {agent.Tier}-tier SUB-AGENT (role: {agent.Role}, ID: {agent.AgentID}) in an autonomous project task force. The COMMANDER assigns you work; you do focused legwork and report back.

THE PROJECT'S GOAL (context, not your whole job): {project.Goal}

RULES:
- Do the specific task in your trigger message. Don't expand scope — the commander owns strategy.
- Work with your tools, verify results, then send your findings to the commander with send_agent_message(agentID: ""commander"", message: ...) BEFORE you finish. An unreported result is a wasted wake.
- If blocked, report the blocker rather than spinning. If an action needs approval or spends money, that's the commander's call — report it as a recommendation.
- When your work changes a tracked number, update the matching Observable (update_observable) so Klives' live dashboard stays current.{desktopNote}
- For browser/GUI work: observe, locate by OCR or grid coordinates, take one action, wait for the expected screen state, then observe again. Do not retry blind clicks. CAPTCHA, login verification, and 2FA are human-only blockers; report them to the commander.
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

            // Shared account registry — same block the Commander sees; reuse before creating duplicates.
            string accounts = "";
            try { accounts = parent.WakeCycle.DescribeAccounts?.Invoke(project.ProjectID) ?? ""; } catch { }
            if (!string.IsNullOrWhiteSpace(accounts))
            {
                sb.AppendLine("── SHARED ACCOUNTS (global registry — reuse before creating; account_list for details) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(accounts, ProjectsContextBudget.AccountsBudget));
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
            if (toolName.StartsWith("computer_", StringComparison.Ordinal)) return ComputerAudit.Describe(toolName, argsJson);
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
