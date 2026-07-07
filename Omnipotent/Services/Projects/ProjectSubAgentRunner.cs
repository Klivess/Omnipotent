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

        private const int MaxToolCallsPerWake = 30; // sub-agents do focused legwork, not strategy
        private const int StuckIdenticalCallThreshold = 3;
        private const int RecentEventsForSeed = 30;

        public ProjectSubAgentRunner(Projects parent)
        {
            this.parent = parent;
        }

        /// <summary>Wakes a sub-agent for a directed stimulus. No-op if it is already awake.</summary>
        public string? Wake(Project project, ProjectAgentRecord agent, string trigger)
        {
            string key = $"{project.ProjectID}/{agent.AgentID}";
            if (!activeWakes.TryAdd(key, true)) return null; // already awake

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
                finally { activeWakes.TryRemove(key, out _); }
            });
            return wakeID;
        }

        private async Task ExecuteWakeAsync(Project project, ProjectAgentRecord agent, string wakeID, string trigger)
        {
            string projectID = project.ProjectID;
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = $"Agent {agent.AgentID} finished its wake.";
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
                llm.AppendUserMessageToToolSession(sessionId, BuildWakeSeed(project, agent, trigger));

                var recentSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
                int toolCalls = 0;

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (toolCalls >= MaxToolCallsPerWake)
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"WAKE TOOL BUDGET REACHED ({MaxToolCallsPerWake}). Stop and report your status to the commander via send_agent_message, then reply with a one-line summary.");

                    var resp = await llm.QueryToolSessionAsync(sessionId, toolDefs, modelOverride: model, cancellationToken: cts.Token);
                    if (!resp.Success) throw new Exception($"LLM query failed: {resp.ErrorMessage}");

                    if (resp.PromptTokens > 0 || resp.CompletionTokens > 0)
                        await parent.Budget.RecordTokenSpendAsync(projectID, resp.PromptTokens, resp.CompletionTokens, resp.GenerationId);

                    bool overBudget = toolCalls >= MaxToolCallsPerWake;
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0 || overBudget)
                    {
                        string final = string.IsNullOrWhiteSpace(resp.Response) ? "(no closing summary)" : resp.Response.Trim();
                        parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, ProjectEventTypes.AgentMessage, final));
                        break;
                    }

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
                            Text = $"{toolName}", ToolName = toolName, ToolCallId = call.id, PayloadJson = argsJson,
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
                try { parent.EventLog.Append(Evt(projectID, wakeID, agent.AgentID, outcome, outcomeText)); }
                catch { }
            }
        }

        private static string BuildSystemPrompt(Project project, ProjectAgentRecord agent) =>
$@"You are a {agent.Tier}-tier SUB-AGENT (role: {agent.Role}, ID: {agent.AgentID}) in an autonomous project task force. The COMMANDER assigns you work; you do focused legwork and report back.

THE PROJECT'S GOAL (context, not your whole job): {project.Goal}

RULES:
- Do the specific task in your trigger message. Don't expand scope — the commander owns strategy.
- Work with your tools, verify results, then send your findings to the commander with send_agent_message(agentID: ""commander"", message: ...) BEFORE you finish. An unreported result is a wasted wake.
- If blocked, report the blocker rather than spinning. If an action needs approval or spends money, that's the commander's call — report it as a recommendation.
- Be concise and factual. Everything you do is on a timeline Klives watches.";

        private string BuildWakeSeed(Project project, ProjectAgentRecord agent, string trigger)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            var sb = new StringBuilder();
            sb.AppendLine("── PROJECT PLAN (commander's, for context) ──");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(digest.CurrentPlan is { Length: > 0 } p ? p : "(none)", 400));

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

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
