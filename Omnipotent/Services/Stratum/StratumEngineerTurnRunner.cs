using Newtonsoft.Json;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Executes one Stratum Engineer conversation turn: user message → background tool loop →
    /// final answer. A turn IS a run (<see cref="StratumAgentType.Engineer"/>) so it reuses the
    /// battle-tested run lifecycle — background Task, cancellation, per-user caps, gate
    /// machinery, crash recovery — while the user-facing record is the conversation timeline.
    /// </summary>
    public class StratumEngineerTurnRunner
    {
        private readonly Stratum parent;
        private readonly StratumTimelineStore timeline;

        private static readonly TimeSpan MaxTurnWallClock = TimeSpan.FromMinutes(30);
        private const int StuckIdenticalCallThreshold = 3;

        public StratumEngineerTurnRunner(Stratum parent, StratumTimelineStore timeline)
        {
            this.parent = parent;
            this.timeline = timeline;
        }

        /// <summary>
        /// Starts a new turn. Returns the run record immediately; the tool loop executes in the
        /// background and streams timeline events. Throws InvalidOperationException when a turn
        /// is already active on this project (409 at the route layer).
        /// </summary>
        public StratumAgentRun StartTurn(StratumProject project, string userID, string userText)
        {
            var meta = timeline.GetMeta(project.ProjectID);
            if (!string.IsNullOrWhiteSpace(meta.ActiveTurnID))
            {
                // Verify the recorded turn is genuinely live (crash recovery may have missed it).
                var live = parent.AgentManager.GetLiveRun(meta.ActiveTurnID);
                if (live != null && (live.Run.Status == StratumRunStatus.Running || live.Run.Status == StratumRunStatus.AwaitingApproval))
                    throw new InvalidOperationException("A turn is already in progress on this project. Wait for it to finish or cancel it.");
                meta.ActiveTurnID = null;
            }

            var run = new StratumAgentRun
            {
                RunID = Guid.NewGuid().ToString("N"),
                ProjectID = project.ProjectID,
                OwnerUserID = userID,
                AgentType = StratumAgentType.Engineer,
                UserPrompt = userText,
                TargetRevisionID = project.Revisions.LastOrDefault()?.RevisionID ?? "",
                CreatedAt = DateTime.UtcNow,
                Status = StratumRunStatus.Pending,
            };

            timeline.Append(new StratumTimelineEvent
            {
                ProjectID = project.ProjectID,
                Type = StratumTimelineEventTypes.UserMessage,
                Author = "user",
                Text = userText,
            });
            timeline.Append(new StratumTimelineEvent
            {
                ProjectID = project.ProjectID,
                TurnID = run.RunID,
                Type = StratumTimelineEventTypes.TurnStarted,
                Author = "system",
                Text = "Engineer turn started.",
            });

            meta.ActiveTurnID = run.RunID;
            timeline.SaveMeta(meta);

            parent.AgentManager.StartRun(run, ctx => ExecuteTurnAsync(ctx, userText));
            return run;
        }

        // ───────────────────────── turn body ─────────────────────────

        private async Task ExecuteTurnAsync(StratumAgentContext ctx, string userText)
        {
            string projectID = ctx.Run.ProjectID;
            long turnStartSeq = timeline.GetLastSequence(projectID);
            var turnOutcome = StratumTimelineEventTypes.TurnCompleted;
            string turnOutcomeText = "Turn completed.";

            try
            {
                var llmServices = await ctx.Parent.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llmServices[0];
                if (!await llm.SupportsNativeToolCallingAsync())
                    throw new InvalidOperationException("The Stratum Engineer requires a remote LLM provider with native tool calling (the local provider has no tool channel).");

                string engineerModel = await parent.GetStringOmniSetting("StratumEngineerModelID", "anthropic/claude-sonnet-4.5");
                string utilityModel = await parent.GetStringOmniSetting("StratumUtilityModelID", "openai/gpt-4.1-mini");
                bool visionEnabled = await parent.GetBoolOmniSetting("StratumVisionEnabled", true);

                var tc = new StratumEngineerTurnContext { Parent = parent, Ctx = ctx };
                tc.Registry = StratumEngineerTools.LoadRegistry(tc);

                // Seed: system prompt → fresh project state + summary + recent messages + user text.
                var meta = timeline.GetMeta(projectID);
                var recent = timeline.ReadSince(projectID, Math.Max(0, meta.LastCompactedSequence), max: 4000)
                    .Where(e => e.Type is StratumTimelineEventTypes.UserMessage or StratumTimelineEventTypes.AgentMessage)
                    .Where(e => e.Sequence <= turnStartSeq) // exclude this turn's own user message (it is the NEW message)
                    .TakeLast(StratumEngineerAgent.RecentMessagesVerbatim)
                    .ToList();

                string sessionId = $"stratum-engineer-{projectID}-{ctx.Run.RunID}";
                llm.StartToolSession(sessionId, StratumEngineerAgent.BuildSystemPrompt());
                llm.AppendUserMessageToToolSession(sessionId, StratumEngineerAgent.BuildTurnSeed(tc, meta, recent, userText));

                var tools = StratumEngineerTools.BuildToolDefinitions();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var recentCallSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
                string finalAnswer = "";

                while (true)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();

                    if (sw.Elapsed > MaxTurnWallClock)
                    {
                        llm.AppendUserMessageToToolSession(sessionId,
                            "TURN BUDGET EXHAUSTED (30 minutes). Stop calling tools NOW and reply with a concise status: what was completed and verified, what remains, and what you will do next turn.");
                    }
                    else if (tc.ToolCallsThisTurn >= StratumEngineerTurnContext.MaxToolCallsPerTurn)
                    {
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"TOOL BUDGET EXHAUSTED ({StratumEngineerTurnContext.MaxToolCallsPerTurn} calls). Stop calling tools NOW and reply with a concise status of completed/remaining work.");
                    }

                    var resp = await llm.QueryToolSessionAsync(sessionId, tools, modelOverride: engineerModel);
                    if (!resp.Success)
                        throw new Exception($"LLM query failed: {resp.ErrorMessage}");

                    bool overBudget = sw.Elapsed > MaxTurnWallClock || tc.ToolCallsThisTurn >= StratumEngineerTurnContext.MaxToolCallsPerTurn;

                    // Assistant prose: final answer when no tool calls follow, otherwise a thought.
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0 || overBudget)
                    {
                        finalAnswer = string.IsNullOrWhiteSpace(resp.Response)
                            ? "(the engineer finished without a closing message)"
                            : resp.Response.Trim();
                        Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.AgentMessage, "agent", finalAnswer);
                        break;
                    }
                    if (!string.IsNullOrWhiteSpace(resp.Response))
                        Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.Thought, "agent", resp.Response.Trim());

                    foreach (var call in resp.ToolCalls)
                    {
                        ctx.Cancellation.ThrowIfCancellationRequested();
                        string toolName = call.function?.name ?? "";
                        string argsJson = call.function?.arguments ?? "";

                        // Stuck detection: the same tool with identical args repeatedly is a loop.
                        string sig = toolName + "|" + argsJson;
                        recentCallSignatures[sig] = recentCallSignatures.TryGetValue(sig, out var n) ? n + 1 : 1;
                        if (recentCallSignatures[sig] >= StuckIdenticalCallThreshold)
                        {
                            llm.AppendToolResult(sessionId, call.id, toolName,
                                $"LOOP DETECTED: you have called {toolName} with identical arguments {recentCallSignatures[sig]} times. The result will not change. Change your approach or report the blocker to the user.");
                            continue;
                        }

                        Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.ToolCall, "agent",
                            DescribeCall(toolName, argsJson), toolName: toolName, toolCallId: call.id, payloadJson: argsJson);

                        var callSw = System.Diagnostics.Stopwatch.StartNew();
                        var outcome = await StratumEngineerTools.DispatchAsync(tc, toolName, argsJson);
                        callSw.Stop();

                        var resultEvt = Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.ToolResult, "agent",
                            outcome.ResultText, toolName: toolName, toolCallId: call.id);
                        resultEvt.ArtifactIDs.AddRange(outcome.ArtifactIDs);

                        llm.AppendToolResult(sessionId, call.id, toolName, outcome.ResultText);

                        // Vision delivery (D7): renders ride a follow-up user message with image
                        // content parts — tool-result image support is inconsistent across providers.
                        if (visionEnabled && outcome.Images.Count > 0)
                        {
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Render produced by your last {toolName} call (isometric + top/front/right, mm axes). Inspect it: does the geometry match the intent (shape, orientation, features)? If wrong, fix it before anything else.",
                                outcome.Images.Select(i => (i.data, i.mime)).ToList());
                            foreach (var artID in outcome.ArtifactIDs.TakeLast(1))
                            {
                                var imgEvt = Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.Image, "agent",
                                    $"Render from {toolName}.", toolName: toolName);
                                imgEvt.ArtifactIDs.Add(artID);
                            }
                        }
                        foreach (var artID in outcome.ArtifactIDs)
                        {
                            var artEvt = Append(projectID, ctx.Run.RunID, StratumTimelineEventTypes.ArtifactAdded, "agent", "", toolName: toolName);
                            artEvt.ArtifactIDs.Add(artID);
                        }
                    }
                }

                // Compact the rolling summary with the utility model (best-effort).
                try
                {
                    var turnEvents = timeline.ReadSince(projectID, turnStartSeq, max: 2000);
                    var meta2 = timeline.GetMeta(projectID);
                    string prompt = StratumEngineerAgent.BuildSummaryPrompt(meta2.RollingSummary, turnEvents);
                    string summarySession = $"stratum-engineer-summary-{ctx.Run.RunID}";
                    llm.StartToolSession(summarySession, null);
                    llm.AppendUserMessageToToolSession(summarySession, prompt);
                    var summaryResp = await llm.QueryToolSessionAsync(summarySession, new List<KliveLLM.HFWrapper.HFTool>(), modelOverride: utilityModel);
                    if (summaryResp.Success && !string.IsNullOrWhiteSpace(summaryResp.Response))
                    {
                        meta2.RollingSummary = summaryResp.Response.Trim();
                        meta2.LastCompactedSequence = turnStartSeq; // recent window stays verbatim
                        timeline.SaveMeta(meta2);
                    }
                }
                catch (Exception ex) { ctx.EmitThought($"Summary compaction failed (non-fatal): {ex.Message}"); }
            }
            catch (OperationCanceledException)
            {
                turnOutcome = StratumTimelineEventTypes.TurnCancelled;
                turnOutcomeText = "Turn cancelled by the user.";
                throw;
            }
            catch (Exception ex)
            {
                turnOutcome = StratumTimelineEventTypes.TurnFailed;
                turnOutcomeText = $"Turn failed: {ex.Message}";
                throw;
            }
            finally
            {
                try
                {
                    Append(projectID, ctx.Run.RunID, turnOutcome, "system", turnOutcomeText);
                    var meta = timeline.GetMeta(projectID);
                    if (meta.ActiveTurnID == ctx.Run.RunID)
                    {
                        meta.ActiveTurnID = null;
                        timeline.SaveMeta(meta);
                    }
                }
                catch { /* never mask the turn's own outcome */ }
            }
        }

        /// <summary>Startup crash recovery: any project whose meta still points at a turn that is
        /// no longer live gets unblocked and a turn-failed marker in its timeline.</summary>
        public void RecoverInterruptedTurns()
        {
            foreach (var meta in timeline.AllMetasWithActiveTurns())
            {
                try
                {
                    Append(meta.ProjectID, meta.ActiveTurnID, StratumTimelineEventTypes.TurnFailed, "system",
                        "Omnipotent restarted while this turn was active. Prior verified work is preserved — send a new message to continue.");
                    meta.ActiveTurnID = null;
                    timeline.SaveMeta(meta);
                }
                catch { /* best effort per project */ }
            }
        }

        private StratumTimelineEvent Append(string projectID, string? turnID, string type, string author, string text,
            string? toolName = null, string? toolCallId = null, string? payloadJson = null)
        {
            var evt = new StratumTimelineEvent
            {
                ProjectID = projectID,
                TurnID = turnID,
                Type = type,
                Author = author,
                Text = text,
                ToolName = toolName,
                ToolCallId = toolCallId,
                PayloadJson = payloadJson,
            };
            return timeline.Append(evt);
        }

        private static string DescribeCall(string toolName, string argsJson)
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(argsJson);
                var bits = jo.Properties()
                    .Select(p => $"{p.Name}={(p.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? Trunc(p.Value.ToString(), 60) : Trunc(p.Value.ToString(Formatting.None), 60))}")
                    .Take(4);
                return $"{toolName}({string.Join(", ", bits)})";
            }
            catch { return toolName; }
        }

        private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
