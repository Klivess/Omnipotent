using Omnipotent.Services.KliveLLM;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Executes one Commander wake: rehydrate context → tool loop → sleep. Cloned from
    /// StratumEngineerTurnRunner's battle-tested loop (background task, stuck-loop detection,
    /// tool-call budget, post-loop digest compaction, single-flight-per-project, crash recovery)
    /// with the design-doc deltas:
    ///   * Wake-triggered, not message-triggered — the seed comes from ProjectWakeCycle, not a
    ///     user message. No persistent session survives between wakes (§7).
    ///   * NO hard wall-clock timeout (§: "no hard timeouts"). A wake is bounded because it is
    ///     one round of reasoning+action, and a per-wake tool-call budget bounds runaway loops;
    ///     true cross-wake stalls are the watchdog's job (P7), never a kill here.
    /// </summary>
    public class ProjectCommanderRunner
    {
        private readonly Projects parent;

        private const int MaxToolCallsPerWake = 60;
        private const int StuckIdenticalCallThreshold = 3;
        private const int MaxPendingTriggers = 12;

        // Triggers that arrived while a wake was active. One wake at a time still holds, but a
        // stimulus (e.g. a Klives message mid-wake) must not vanish — it re-wakes the Commander
        // as soon as the active wake finishes.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> pendingTriggers = new(StringComparer.Ordinal);

        // Klives steering that should land WITHIN the current wake (not after it): drained at the
        // top of each tool-loop turn and injected into the live session, so a message reshapes the
        // Commander's behaviour on its very next model turn instead of one whole wake later.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> steerQueue = new(StringComparer.Ordinal);

        // The live wake's cancellation source per project, so Klives can halt an in-flight wake
        // (pause/archive) instead of waiting for it to run itself out.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> activeWakeCts = new(StringComparer.Ordinal);

        public ProjectCommanderRunner(Projects parent)
        {
            this.parent = parent;
        }

        /// <summary>
        /// Delivers a Klives message with minimal latency. If a wake is in flight, the message is
        /// injected into that live session so the Commander honours it within the current wake
        /// (fast steering, item 5); otherwise it wakes the Commander normally. The caller is
        /// responsible for having logged the KlivesMessage event.
        /// </summary>
        public void Steer(Project project, string text)
        {
            if (activeWakeCts.ContainsKey(project.ProjectID))
            {
                steerQueue.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>()).Enqueue(text);
                return;
            }
            Wake(project, $"Message from Klives: {text}");
        }

        /// <summary>
        /// Halts the project's in-flight wake, if any (Klives paused/archived it). Cancels the
        /// live tool loop; rehydrate-on-wake makes this safe — no in-memory state is corrupted, the
        /// committed event log is intact, and a later resume simply wakes fresh. Returns true if a
        /// wake was actually cancelled.
        /// </summary>
        public bool CancelActiveWake(string projectID)
        {
            steerQueue.TryRemove(projectID, out _);
            pendingTriggers.TryRemove(projectID, out _);
            if (activeWakeCts.TryGetValue(projectID, out var cts))
            {
                try { cts.Cancel(); return true; } catch { return false; }
            }
            return false;
        }

        /// <summary>
        /// Wakes the Commander in response to a trigger. Returns immediately with the wake ID;
        /// the loop runs in the background and streams events. If a wake is already active on the
        /// project, the trigger is queued and re-wakes the Commander when the active wake ends
        /// (returns null) — one wake at a time, per §7, but no stimulus is ever dropped.
        /// </summary>
        public string? Wake(Project project, string triggerDescription)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID))
            {
                var q = pendingTriggers.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>());
                if (q.Count < MaxPendingTriggers && !q.Contains(triggerDescription))
                    q.Enqueue(triggerDescription);
                return null; // already awake — queued for the follow-up wake
            }

            string wakeID = Guid.NewGuid().ToString("N");
            digest.ActiveWakeID = wakeID;
            parent.Digests.SaveDigest(digest);

            parent.EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                WakeID = wakeID,
                AgentID = "commander",
                Type = ProjectEventTypes.CommanderWake,
                Author = "system",
                Text = $"Commander woke. Trigger: {Trunc(triggerDescription, 200)}",
            });

            _ = Task.Run(() => ExecuteWakeAsync(project, wakeID, triggerDescription));
            return wakeID;
        }

        private async Task ExecuteWakeAsync(Project project, string wakeID, string triggerDescription)
        {
            string projectID = project.ProjectID;
            long wakeStartSeq = parent.EventLog.GetLastSequence(projectID);
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = "Wake completed; Commander asleep.";
            int stuckTrips = 0;
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
            // Whether Klives is expecting a reply from this wake — either it was triggered by his
            // message, or he steered it mid-flight. Drives the Discord reply mirror.
            bool klivesInvolved = TriggeredByKlives(triggerDescription);
            using var cts = new CancellationTokenSource();
            activeWakeCts[projectID] = cts; // registered so Klives can halt this wake

            try
            {
                var llmServices = await parent.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llmServices[0];
                if (!await llm.SupportsNativeToolCallingAsync())
                    throw new InvalidOperationException("The Commander requires a remote LLM provider with native tool calling.");

                var settings = parent.Settings.Get(projectID);
                string model = settings.CommanderModel;
                bool visionEnabled = settings.VisionEnabled;
                parent.SubAgents.EnsureCommander(projectID);

                // The Commander is video-tier: core tools plus the full computer-use surface.
                var toolDefs = ProjectCommanderAgent.BuildCoreToolDefinitions();
                toolDefs.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions());

                string sessionId = $"projects-commander-{projectID}-{wakeID}";
                llm.StartToolSession(sessionId, ProjectCommanderAgent.BuildSystemPrompt(project));
                llm.AppendUserMessageToToolSession(sessionId, await parent.WakeCycle.BuildWakeSeed(project, triggerDescription));

                var recentSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
                int toolCalls = 0;

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // Budget guardrail: RecordTokenSpendAsync flips Status→BudgetPaused synchronously when
                    // the token budget is exhausted, but doesn't cancel this wake. Re-read status at the
                    // turn boundary so the pause actually stops the loop BEFORE the next (costly) LLM call
                    // — otherwise the in-flight wake overshoots the budget to natural completion.
                    var freshStatus = parent.Store.GetProject(projectID)?.Status;
                    if (freshStatus == ProjectStatus.BudgetPaused)
                    {
                        outcomeText = "Wake stopped — token budget exhausted (project paused pending a budget increase).";
                        goto done;
                    }

                    // Fast steering: fold in any Klives messages that arrived since the last turn so
                    // the Commander adjusts within THIS wake. Injected only at a turn boundary (never
                    // mid tool-call-batch) so the tool_call/tool_result pairing stays valid.
                    if (steerQueue.TryGetValue(projectID, out var sq))
                        while (sq.TryDequeue(out var steer))
                        {
                            llm.AppendUserMessageToToolSession(sessionId, $"STEERING FROM KLIVES (mid-wake — take this into account now): {steer}");
                            klivesInvolved = true;
                        }

                    if (toolCalls >= MaxToolCallsPerWake)
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"WAKE TOOL BUDGET REACHED ({MaxToolCallsPerWake} calls). Stop calling tools and reply with a concise status: what you did this wake and what the next wake should do.");

                    var resp = await llm.QueryToolSessionAsync(sessionId, toolDefs, modelOverride: model, cancellationToken: cts.Token);
                    if (!resp.Success) throw new Exception($"LLM query failed: {resp.ErrorMessage}");

                    // Meter this round's spend against the budget. OpenRouter reports the ACTUAL cost in
                    // the response usage object (resp.CostUsd) — authoritative for whatever model is in
                    // use, so the ledger books it directly instead of a flat provisional. The generation
                    // id remains a fallback for providers that don't report a cost.
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
                        string final = string.IsNullOrWhiteSpace(resp.Response) ? "(no closing status)" : resp.Response.Trim();
                        parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.CommanderMessage, "commander", final));
                        // When Klives spoke to the Commander, the answer must reach him where he
                        // asked — the event log alone reads as silence from Discord.
                        if (klivesInvolved && parent.DiscordManager != null)
                        {
                            try { await parent.DiscordManager.PostCommanderReplyAsync(project, final); }
                            catch { /* best-effort surface */ }
                        }
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(resp.Response))
                        parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.CommanderThought, "commander", resp.Response.Trim()));

                    foreach (var call in resp.ToolCalls)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        string toolName = call.function?.name ?? "";
                        string argsJson = call.function?.arguments ?? "";
                        toolCalls++;

                        string sig = toolName + "|" + argsJson;
                        recentSignatures[sig] = recentSignatures.TryGetValue(sig, out var n) ? n + 1 : 1;
                        if (recentSignatures[sig] >= StuckIdenticalCallThreshold)
                        {
                            stuckTrips++;
                            llm.AppendToolResult(sessionId, call.id, toolName,
                                $"LOOP DETECTED: identical {toolName} call {recentSignatures[sig]}× — the result won't change. Change approach or wrap up this wake.");
                            continue;
                        }

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                            Type = ProjectEventTypes.ToolCall, Author = "commander",
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id, PayloadJson = argsJson,
                        });

                        var result = await parent.CommanderToolDispatch(project, "commander", wakeID, toolName, argsJson, cts.Token);

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                            Type = ProjectEventTypes.ToolResult, Author = "commander",
                            Text = result.ResultText, ToolName = toolName, ToolCallId = call.id,
                            ArtifactIDs = result.ArtifactIDs,
                        });
                        llm.AppendToolResult(sessionId, call.id, toolName, result.ResultText);

                        // Vision return: the post-action screenshot rides a follow-up user message
                        // (tool-result image support is inconsistent across providers — same
                        // approach Stratum uses for renders). Old screenshots auto-compact.
                        if (visionEnabled && result.Jpeg != null)
                        {
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Screenshot after your {toolName} call. Verify the screen shows what you expect before acting further.",
                                new List<(byte[] data, string mimeType)> { (result.Jpeg, "image/jpeg") });
                        }

                        if (result.EndWake) { outcomeText = "Wake ended by a tool (constraint)."; goto done; }
                    }
                }
                done: ;
            }
            catch (OperationCanceledException)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = "Wake halted by Klives (project paused or archived).";
            }
            catch (Exception ex)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = $"Wake failed: {ex.Message}";
            }
            finally
            {
                try
                {
                    // Record stuck-loop trips into the digest for the watchdog (P7), clear the active wake.
                    var digest = parent.Digests.GetDigest(projectID);
                    digest.RecentStuckLoopTrips = stuckTrips;
                    if (digest.ActiveWakeID == wakeID) digest.ActiveWakeID = null;
                    parent.Digests.SaveDigest(digest);

                    // Per-wake cost attribution: the ledger is cumulative, so stamp this wake's own
                    // token spend + cost onto its closing event for the timeline/reports. wakeCostUsd is
                    // the real OpenRouter spend when available, else the provisional estimate.
                    if (wakePromptTokens > 0 || wakeCompletionTokens > 0)
                        outcomeText += $" (this wake: ~${wakeCostUsd:0.###}, {wakePromptTokens + wakeCompletionTokens} tokens)";
                    parent.EventLog.Append(WakeEvt(projectID, wakeID, outcome, "system", outcomeText));

                    // A failed Klives-triggered wake must not read as silence either.
                    if (outcome == ProjectEventTypes.WakeFailed && klivesInvolved && parent.DiscordManager != null)
                    {
                        try { await parent.DiscordManager.PostCommanderReplyAsync(project, $"⚠️ {outcomeText}"); }
                        catch { }
                    }

                    // Refresh budget/org in the digest, then compact — never in the hot path.
                    await parent.RebuildDigestAfterWakeAsync(project, wakeStartSeq);
                }
                catch { /* never mask the wake outcome */ }

                // Deregister this wake's cancellation source (only if it's still ours).
                activeWakeCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(projectID, cts));
                DrainPendingTriggers(projectID);
            }
        }

        /// <summary>Klives-message triggers get their wake's closing status mirrored to Discord.</summary>
        private static bool TriggeredByKlives(string trigger) =>
            trigger.Contains("Message from Klives", StringComparison.Ordinal);

        /// <summary>Re-wakes the Commander with any triggers or steers that arrived during (or at the tail
        /// end of) the wake that just ended — so nothing is stranded by the finish/enqueue race.</summary>
        private void DrainPendingTriggers(string projectID)
        {
            try
            {
                pendingTriggers.TryRemove(projectID, out var queued);
                steerQueue.TryRemove(projectID, out var leftoverSteers);
                var refreshed = parent.Store.GetProject(projectID);
                if (refreshed == null || refreshed.Status != ProjectStatus.Active) return; // halted/archived: drop, events stay logged

                var missed = new List<string>();
                if (queued != null) missed.AddRange(queued);
                if (leftoverSteers != null) missed.AddRange(leftoverSteers.Select(s => $"Message from Klives: {s}"));
                missed = missed.Distinct().ToList();
                if (missed.Count == 0) return;

                Wake(refreshed, missed.Count == 1 ? missed[0]
                    : "Stimuli that arrived while you were awake:\n\n" + string.Join("\n\n", missed));
            }
            catch { /* never mask the wake outcome */ }
        }

        /// <summary>
        /// Watchdog escalation (P7): forces a fresh wake even if the digest still marks one active
        /// — the previous wake is presumed wedged. Safe by construction: rehydrate-on-wake means
        /// there is no in-memory state to corrupt, so clearing the stale marker and waking again is
        /// indistinguishable from any other wake. Never a hard kill.
        /// </summary>
        public string ForceWake(Project project, string reason)
        {
            // Cancel the presumed-wedged wake FIRST so it can't keep running (and burning tokens)
            // alongside the fresh one, and so a wake blocked on a gate/steer actually unwinds. Without
            // this the old wake's cts stayed live and ExecuteWakeAsync's registry overwrite made it
            // un-cancelable — briefly running two concurrent wakes. Rehydrate-on-wake makes this safe.
            if (activeWakeCts.TryGetValue(project.ProjectID, out var oldCts))
            {
                try { oldCts.Cancel(); } catch { }
            }
            var digest = parent.Digests.GetDigest(project.ProjectID);
            digest.ActiveWakeID = null; // abandon the presumed-stuck wake's marker
            parent.Digests.SaveDigest(digest);
            string wakeID = Wake(project, $"[watchdog force-wake] {reason}") ?? "";
            return wakeID;
        }

        /// <summary>Startup crash recovery: clear any wake left active by a restart (rehydrate-on-wake makes this safe).</summary>
        public void RecoverInterruptedWakes()
        {
            foreach (var digest in parent.Digests.AllDigestsWithActiveWakes())
            {
                try
                {
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = digest.ProjectID, WakeID = digest.ActiveWakeID,
                        Type = ProjectEventTypes.WakeFailed, Author = "system",
                        Text = "Omnipotent restarted mid-wake. Committed events preserved; next stimulus rehydrates the Commander.",
                    });
                    digest.ActiveWakeID = null;
                    parent.Digests.SaveDigest(digest);
                }
                catch { }
            }
        }

        private static ProjectEvent WakeEvt(string projectID, string wakeID, string type, string author, string text) => new()
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander", Type = type, Author = author, Text = text,
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
