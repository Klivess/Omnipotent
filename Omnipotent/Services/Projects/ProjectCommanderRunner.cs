using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.ComputerControl;
using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Executes one Commander wake: rehydrate context → tool loop → sleep. Cloned from
    /// StratumEngineerTurnRunner's battle-tested loop (background task, stuck-loop detection,
    /// context rollover, post-loop digest compaction, single-flight-per-project, crash recovery)
    /// with the design-doc deltas:
    ///   * Wake-triggered, not message-triggered — the seed comes from ProjectWakeCycle, not a
    ///     user message. No persistent session survives between wakes (§7).
    ///   * NO hard work or wall-clock timeout. Productive work renews into fresh context slices
    ///     for as long as needed; only budget, cancellation, completion, a real blocker, or
    ///     machine-detected non-convergence stops it.
    /// </summary>
    public class ProjectCommanderRunner
    {
        private readonly Projects parent;

        private const int StuckIdenticalCallThreshold = 3;
        private const int MaxPendingTriggers = 12;
        // Context slices protect reasoning quality without limiting productive work. Novel,
        // successful work rolls into a fresh wake immediately; convergence checks handle loops.

        // Triggers that arrived while a wake was active. One wake at a time still holds, but a
        // stimulus (e.g. a Klives message mid-wake) must not vanish — it re-wakes the Commander
        // as soon as the active wake finishes.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> pendingTriggers = new(StringComparer.Ordinal);

        // Klives steering that should land WITHIN the current wake (not after it): drained at the
        // top of each tool-loop turn and injected into the live session, so a message reshapes the
        // Commander's behaviour on its very next model turn instead of one whole wake later.
        private sealed record QueuedSteer(string Text, string? DirectiveID);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedSteer>> steerQueue = new(StringComparer.Ordinal);

        // Sub-agent reports use the same low-latency path, but remain distinct from Klives steering
        // so they do not incorrectly mark the wake as requiring a human-facing Discord reply.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> agentSteerQueue = new(StringComparer.Ordinal);

        // The live wake's cancellation source per project, so Klives can halt an in-flight wake
        // (pause/archive) instead of waiting for it to run itself out.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> activeWakeCts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> wakeGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> cancelBeforeStart = new(StringComparer.Ordinal);

        private object WakeGate(string projectID) => wakeGates.GetOrAdd(projectID, _ => new object());

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
        public string? Steer(Project project, string text, string? directiveID = null)
        {
            // Origin stamp travels WITH the text: a steer can be injected mid-wake seconds from
            // now, or replayed as a missed-steer wake much later — either way the Commander must
            // see when Klives actually said it, not when it finally reached a session.
            text = $"[sent {Data_Handling.TemporalFormat.NowStamp()}] {text}";
            lock (WakeGate(project.ProjectID))
            {
                // The persisted lease/digest is authoritative during the short startup window
                // before activeWakeCts is registered. The old activeWakeCts-only check could
                // start a second wake or strand a steer in exactly that race.
                var activeWakeID = parent.RuntimeState.Get(project.ProjectID).ActiveWakeLease?.WakeID
                    ?? parent.Digests.GetDigest(project.ProjectID).ActiveWakeID;
                if (!string.IsNullOrWhiteSpace(activeWakeID))
                {
                    steerQueue.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<QueuedSteer>())
                        .Enqueue(new QueuedSteer(text, directiveID));
                    return activeWakeID;
                }
                return WakeLocked(project, $"Message from Klives: {text}", queueIfBusy: true);
            }
        }

        /// <summary>
        /// Delivers a durable Klives directive with next-safe-turn latency. The directive store is
        /// the recovery source of truth; this queue only makes an already-running wake react now.
        /// </summary>
        public string? DeliverHumanDirective(Project project, ProjectDirective directive)
        {
            if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) return null;
            return Steer(project, $"[directive:{directive.DirectiveID}] {directive.Text}", directive.DirectiveID);
        }

        /// <summary>
        /// Delivers a sub-agent report to the current Commander wake when possible. Returning the
        /// active wake ID lets the durable stimulus queue claim the message instead of retrying it
        /// until the Commander sleeps. If the Commander is asleep, this starts a fresh wake.
        /// </summary>
        public string? DeliverAgentMessage(Project project, string trigger)
        {
            if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) return null;
            lock (WakeGate(project.ProjectID))
            {
                var digest = parent.Digests.GetDigest(project.ProjectID);
                if (string.IsNullOrWhiteSpace(digest.ActiveWakeID))
                    return WakeLocked(project, trigger, queueIfBusy: false);

                var q = agentSteerQueue.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>());
                if (q.Contains(trigger)) return digest.ActiveWakeID;
                if (q.Count >= MaxPendingTriggers) return null;
                q.Enqueue(trigger);
                return digest.ActiveWakeID;
            }
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
            agentSteerQueue.TryRemove(projectID, out _);
            pendingTriggers.TryRemove(projectID, out _);
            if (activeWakeCts.TryGetValue(projectID, out var cts))
            {
                var lease = parent.RuntimeState.Get(projectID).ActiveWakeLease;
                if (lease != null)
                    parent.RuntimeState.RequestWakeCancellation(projectID, lease.WakeID, lease.Generation, "Project pause/archive requested.");
                try { cts.Cancel(); return true; } catch { return false; }
            }
            lock (WakeGate(projectID))
            {
                if (!string.IsNullOrWhiteSpace(parent.Digests.GetDigest(projectID).ActiveWakeID))
                {
                    var lease = parent.RuntimeState.Get(projectID).ActiveWakeLease;
                    if (lease != null)
                        parent.RuntimeState.RequestWakeCancellation(projectID, lease.WakeID, lease.Generation, "Cancellation requested before wake start.");
                    cancelBeforeStart[projectID] = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Wakes the Commander in response to a trigger. Returns immediately with the wake ID;
        /// the loop runs in the background and streams events. If a wake is already active on the
        /// project, the trigger is queued and re-wakes the Commander when the active wake ends
        /// (returns null) — one wake at a time, per §7, but no stimulus is ever dropped.
        /// </summary>
        public string? Wake(Project project, string triggerDescription, bool queueIfBusy = true)
        {
            // Planning projects wake too (to draft the Grand Plan and field Klives' messages);
            // execution tools stay gated until the plan is approved and the project flips Active.
            if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) return null;
            lock (WakeGate(project.ProjectID))
                return WakeLocked(project, triggerDescription, queueIfBusy);
        }

        private string? WakeLocked(Project project, string triggerDescription, bool queueIfBusy)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID))
            {
                if (queueIfBusy)
                {
                var q = pendingTriggers.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>());
                if (q.Count < MaxPendingTriggers && !q.Contains(triggerDescription))
                    q.Enqueue(triggerDescription);
                try { parent.RuntimeState.EnqueueTrigger(project.ProjectID, TriggerFor(triggerDescription)); } catch { }
                }
                return null; // already awake — queued for the follow-up wake
            }

            string wakeID = Guid.NewGuid().ToString("N");
            var runtime = parent.RuntimeState.Get(project.ProjectID);
            if (runtime.Health.Circuit.Status == ProjectCircuitStatus.Open)
            {
                if (!runtime.Health.Circuit.RetryAt.HasValue || runtime.Health.Circuit.RetryAt > DateTime.UtcNow)
                    return null;
                parent.RuntimeState.CloseCircuit(project.ProjectID, halfOpen: true);
            }

            var acquired = parent.RuntimeState.TryAcquireWakeLease(project.ProjectID, wakeID);
            if (!acquired.Acquired || acquired.Lease == null)
            {
                if (queueIfBusy)
                    parent.RuntimeState.EnqueueTrigger(project.ProjectID, TriggerFor(triggerDescription));
                return null;
            }
            // Claim (rather than remove) one durable inbox item for this wake.  A trigger must
            // survive a provider failure/cancellation so it can be replayed; the old exact-match
            // removal acknowledged it before the model had even seen it.
            var claimed = parent.RuntimeState.TryClaimNextTrigger(project.ProjectID, wakeID, acquired.Lease.Generation);
            string effectiveTrigger = claimed.Claimed && claimed.Trigger != null
                ? claimed.Trigger.Payload
                : triggerDescription;
            string? claimedTriggerID = claimed.Claimed ? claimed.Trigger?.TriggerID : null;

            try
            {
                digest.ActiveWakeID = wakeID; // legacy/UI mirror; runtime lease is authoritative
                parent.Digests.SaveDigest(digest);
                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = project.ProjectID,
                    WakeID = wakeID,
                    AgentID = "commander",
                    Type = ProjectEventTypes.CommanderWake,
                    Author = "system",
                    Text = $"Commander woke. Trigger: {Trunc(effectiveTrigger, 200)}",
                    PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { leaseGeneration = acquired.Lease.Generation }),
                });
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(claimedTriggerID))
                {
                    try
                    {
                        parent.RuntimeState.AcknowledgeTrigger(project.ProjectID, claimedTriggerID, wakeID,
                            acquired.Lease.Generation, succeeded: false);
                    }
                    catch { }
                }
                parent.RuntimeState.ReleaseWakeLease(project.ProjectID, wakeID, acquired.Lease.Generation);
                if (digest.ActiveWakeID == wakeID) { digest.ActiveWakeID = null; parent.Digests.SaveDigest(digest); }
                throw;
            }

            _ = Task.Run(() => ExecuteWakeAsync(project, wakeID, acquired.Lease.Generation, effectiveTrigger, claimedTriggerID));
            return wakeID;
        }

        private async Task ExecuteWakeAsync(Project project, string wakeID, long leaseGeneration, string triggerDescription,
            string? claimedTriggerID)
        {
            string projectID = project.ProjectID;
            long wakeStartSeq = parent.EventLog.GetLastSequence(projectID);
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = "Wake completed; Commander asleep.";
            string? outcomePayloadJson = null;
            int stuckTrips = 0;
            int emptyResponseTrips = 0;
            int emptyResponses = 0;
            bool endedAtWorkSlice = false;
            int productiveActions = 0;
            int dispatchedToolCalls = 0;
            int toolCalls = 0, modelTurns = 0;
            int sliceToolCalls = 0, sliceModelTurns = 0, sliceTokenBudget = 0;
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            long liveContextTokens = 0; // current request context, NOT cumulative billed prompt tokens
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
            string? lastCommittedTool = null;
            string? lastCommittedResult = null;
            string? initialModel = null;
            string? finalModel = null;
            DateTime wakeStartedAtUtc = DateTime.UtcNow;
            // Whether Klives is expecting a reply from this wake — either it was triggered by his
            // message, or he steered it mid-flight. Drives the Discord reply mirror.
            bool klivesInvolved = TriggeredByKlives(triggerDescription);
            using var cts = new CancellationTokenSource();
            activeWakeCts[projectID] = cts; // registered so Klives can halt this wake
            if (cancelBeforeStart.TryRemove(projectID, out _)) cts.Cancel();

            try
            {
                var running = parent.RuntimeState.MarkWakeRunning(projectID, wakeID, leaseGeneration);
                if (!running.Applied) throw new OperationCanceledException(running.Reason);
                var llmServices = await parent.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llmServices[0];
                if (!await llm.SupportsNativeToolCallingAsync())
                    throw new InvalidOperationException("The Commander requires a remote LLM provider with native tool calling.");
                if (parent.ProviderCredit != null && await llm.IsOpenRouterActiveAsync())
                {
                    var credit = await parent.ProviderCredit.CheckAsync(cts.Token);
                    if (credit.Status == OpenRouterCreditStatus.Exhausted)
                        throw new OpenRouterCreditExhaustedException(credit);
                }

                var settings = parent.Settings.Get(projectID);
                var modelRoutes = settings.CommanderRoutes.ToList();
                if (modelRoutes.Count == 0) throw new InvalidOperationException("Commander has no configured model routes.");
                // Index 0 is the primary. OpenRouter receives it as `model` and later routes as its
                // ordered fallback set; a successful backup is pinned for the rest of this wake.
                string model = modelRoutes[0];
                initialModel = model;
                finalModel = model;
                bool visionEnabled = settings.VisionEnabled;
                sliceToolCalls = settings.WorkSliceToolCalls;
                sliceModelTurns = settings.WorkSliceModelTurns;
                sliceTokenBudget = Math.Clamp(settings.WorkSliceTokenBudget, 16_000, 2_000_000);
                int maxOutputTokens = Math.Clamp(settings.CommanderMaxOutputTokens, 512, 32_768);
                int maxLoopTrips = settings.MaxConvergenceTripsPerSlice;
                parent.SubAgents.EnsureCommander(projectID);

                // The Commander is video-tier: core tools plus the full computer-use surface.
                var toolDefs = ProjectCommanderAgent.BuildCoreToolDefinitions();
                toolDefs.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions());

                // Keep a clean completed session across ordinary wakes. A context rollover leaves an
                // unfinished tool protocol (or explicitly resets below), so it naturally starts fresh.
                string sessionId = $"projects-commander-{projectID}";
                bool continuingSession = llm.CanContinueToolSession(sessionId);
                string wakeSeed = await parent.WakeCycle.BuildWakeSeed(project, triggerDescription);
                if (continuingSession && llm.GetToolSessionContextTokens(sessionId) +
                    ProjectsContextBudget.EstimateTokens(wakeSeed) >= sliceTokenBudget)
                {
                    llm.ResetSession(sessionId);
                    continuingSession = false;
                }
                if (!continuingSession)
                    llm.StartToolSession(sessionId, ProjectCommanderAgent.BuildSystemPrompt(project));
                llm.AppendUserMessageToToolSession(sessionId, wakeSeed);
                var approvedPlan = parent.GrandPlans.GetCurrentApproved(projectID)?.Content;
                var readyMilestones = parent.GrandPlans.GetReadyMilestones(projectID);
                if (project.Status == ProjectStatus.Active && project.SubAgentCap > 1
                    && parent.SubAgents.ListActive(projectID).Count <= 1
                    && ((approvedPlan?.Workstreams.Count ?? 0) > 1 || (approvedPlan?.Milestones.Count ?? 0) > 1))
                    llm.AppendUserMessageToToolSession(sessionId,
                        $"DELEGATION CHECKPOINT: the approved plan has separable work and {project.SubAgentCap - 1} worker slot(s) are free. " +
                        $"Dependency-ready milestones: {string.Join("; ", readyMilestones.Select(m => $"{m.ID} {m.Title}"))}. " +
                        "Assign only dependency-ready work, set milestone owners, and require explicit deliverables unless the next step is genuinely indivisible.");

                var recentSignatures = new Dictionary<string, int>(StringComparer.Ordinal);

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    parent.RuntimeState.HeartbeatWakeLease(projectID, wakeID, leaseGeneration);
                    var liveRuntime = parent.RuntimeState.Get(projectID);
                    if (liveRuntime.Health.Circuit.Status == ProjectCircuitStatus.Open
                        && (!liveRuntime.Health.Circuit.RetryAt.HasValue || liveRuntime.Health.Circuit.RetryAt > DateTime.UtcNow))
                    {
                        outcome = ProjectEventTypes.WakeDeferred;
                        outcomeText = $"Wake deferred by open provider circuit until {liveRuntime.Health.Circuit.RetryAt?.ToString("O") ?? "manual recovery"}.";
                        break;
                    }

                    // Budget guardrail: RecordTokenSpendAsync flips Status→BudgetPaused synchronously when
                    // the token budget is exhausted, but doesn't cancel this wake. Re-read status at the
                    // turn boundary so the pause actually stops the loop BEFORE the next (costly) LLM call
                    // — otherwise the in-flight wake overshoots the budget to natural completion.
                    var freshStatus = parent.Store.GetProject(projectID)?.Status;
                    if (freshStatus is not (ProjectStatus.Active or ProjectStatus.Planning))
                    {
                        outcomeText = $"Wake stopped because project status is {freshStatus?.ToString() ?? "missing"}.";
                        break;
                    }

                    // Fast steering: fold in any Klives messages that arrived since the last turn so
                    // the Commander adjusts within THIS wake. Injected only at a turn boundary (never
                    // mid tool-call-batch) so the tool_call/tool_result pairing stays valid.
                    if (steerQueue.TryGetValue(projectID, out var sq))
                        while (sq.TryDequeue(out var steer))
                        {
                            llm.AppendUserMessageToToolSession(sessionId, $"STEERING FROM KLIVES (mid-wake — take this into account now): {steer.Text}");
                            klivesInvolved = true;
                        }

                    if (agentSteerQueue.TryGetValue(projectID, out var aq))
                        while (aq.TryDequeue(out var report))
                            llm.AppendUserMessageToToolSession(sessionId,
                                $"MESSAGE FROM A SUB-AGENT (mid-wake — take this into account now): {report}");

                    if (ProjectWorkSliceBoundary.IsToolCallLimitReached(toolCalls, sliceToolCalls))
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"CONTEXT WORK SLICE COMPLETE ({sliceToolCalls} tool calls). This is not a work limit. Stop calling tools, record verified status and the exact next action; productive work continues immediately in a fresh context.");

                    bool finalModelTurn = ProjectWorkSliceBoundary.IsFinalModelTurn(modelTurns, sliceModelTurns);
                    if (finalModelTurn)
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"CONTEXT WORK SLICE COMPLETE ({sliceModelTurns} model turns). This is not a work limit. Give verified status and the exact next action for immediate continuation in a fresh context.");

                    var budgetLease = await parent.Budget.TryAcquireLlmTurnAsync(projectID, cts.Token);
                    if (budgetLease == null)
                    {
                        outcomeText = "Wake stopped before the next model call because no token budget remained.";
                        goto done;
                    }
                    KliveLLM.KliveLLM.KliveLLMResponse resp;
                    try
                    {
                        // Fallback across configured routes happens at the OpenRouter level: the current
                        // `model` is primary and the remaining routes are tried server-side if it fails.
                        // A failure here means every route was exhausted, so it propagates to the outer
                        // handler (circuit breaker / deferral).
                        try
                        {
                            resp = await llm.QueryToolSessionAsync(sessionId, toolDefs,
                                maxTokensOverride: maxOutputTokens,
                                modelOverride: model, cancellationToken: cts.Token, modelRoutes: modelRoutes,
                                compactAboveTokensOverride: 0);
                            modelTurns++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { modelTurns++; throw; }

                        if (!resp.Success)
                            throw ProjectProviderFailure.FromUnsuccessfulResponse(resp.ErrorMessage, resp.Model ?? model, maxOutputTokens);

                        // Do not re-hit a route OpenRouter just bypassed for the rest of this wake. A
                        // successful fallback is safe to pin because it is one of this role's configured
                        // routes; on the next wake the normal preference order is reconsidered.
                        string? servedRoute = modelRoutes.FirstOrDefault(route =>
                            string.Equals(route, resp.Model, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(servedRoute)) model = servedRoute;
                        finalModel = resp.Model ?? model;

                    // Meter this round's spend against the budget. OpenRouter reports the ACTUAL cost in
                    // the response usage object (resp.CostUsd) — authoritative for whatever model is in
                    // use, so the ledger books it directly instead of a flat provisional. The generation
                    // id remains a fallback for providers that don't report a cost.
                    if (resp.PromptTokens > 0 || resp.CompletionTokens > 0)
                    {
                        wakePromptTokens += resp.PromptTokens;
                        wakeCompletionTokens += resp.CompletionTokens;
                        // prompt_tokens already includes the entire live conversation for THIS call.
                        // Summing it across turns double/triple-counts earlier messages and caused the
                        // nominal 64k boundary to fire when only ~30k was actually resident.
                        liveContextTokens = ProjectWorkSliceBoundary.MeasureLiveContext(
                            resp.PromptTokens, resp.CompletionTokens);
                        wakeCostUsd += resp.CostUsd ?? parent.Budget.EstimateCost(resp.PromptTokens, resp.CompletionTokens);
                        await parent.Budget.RecordTokenSpendAsync(projectID, resp.PromptTokens, resp.CompletionTokens, resp.GenerationId, resp.CostUsd);
                    }

                    }
                    finally { await budgetLease.DisposeAsync(); }

                    string? sliceBoundary = ProjectWorkSliceBoundary.Describe(
                        toolCalls, sliceToolCalls, modelTurns, sliceModelTurns,
                        liveContextTokens, sliceTokenBudget);
                    bool sliceComplete = sliceBoundary != null;
                    if (resp.ToolCalls is { Count: > 0 } || !string.IsNullOrWhiteSpace(resp.Response))
                        emptyResponseTrips = 0;
                    else
                        emptyResponses++;
                    // A model response and its returned tool calls are one atomic protocol turn.
                    // Even when this response crosses a context boundary, execute and journal the
                    // complete batch first. Rejecting it here caused every batched web_fetch call
                    // at the boundary to be surfaced as a fake tool failure without any HTTP work.
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0)
                    {
                        if (!sliceComplete && string.IsNullOrWhiteSpace(resp.Response)
                            && ++emptyResponseTrips <= 2)
                        {
                            parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.Status, "system",
                                $"Model returned an empty response with no tool calls (retry {emptyResponseTrips}/2); the wake remains active."));
                            llm.AppendUserMessageToToolSession(sessionId,
                                "Your last turn was empty. Continue with the next evidence-producing tool action, or give a concrete closing status explaining the real external wait/blocker. Do not silently abandon the wake.");
                            continue;
                        }
                        endedAtWorkSlice = sliceComplete;
                        string final = string.IsNullOrWhiteSpace(resp.Response)
                            ? ProjectWakeStatus.ForCommander(parent.Digests.GetDigest(projectID),
                                parent.RuntimeState.Get(projectID), null)
                            : resp.Response.Trim();
                        if (sliceComplete)
                        {
                            parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.Status, "system",
                                $"WORK_SLICE_ROLLOVER: {sliceBoundary}. No tool calls were pending."));
                            parent.RuntimeState.SetResumeAction(projectID, new ProjectResumeAction
                            {
                                Kind = "work-slice",
                                Summary = ProjectWorkSliceBoundary.ResumeSummary(sliceBoundary!,
                                    Array.Empty<string?>(), lastCommittedTool, lastCommittedResult, final),
                                RecordedBy = "commander",
                                ToolName = "resume",
                            });
                            llm.ResetSession(sessionId);
                        }
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

                        var contract = ProjectToolContract.ValidateAndNormalize(toolName, argsJson, toolDefs);
                        if (!contract.IsValid)
                        {
                            parent.EventLog.Append(new ProjectEvent
                            {
                                ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                                Type = ProjectEventTypes.ToolCall, Author = "commander",
                                Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = ProjectCommanderTools.AuditPayload(toolName, argsJson),
                            });
                            parent.EventLog.Append(new ProjectEvent
                            {
                                ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                                Type = ProjectEventTypes.ToolResult, Author = "system",
                                Text = contract.ErrorText!, ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = "{\"succeeded\":false}",
                            });
                            llm.AppendToolResult(sessionId, call.id, toolName, contract.ErrorText!, keepRecentFull: int.MaxValue);
                            liveContextTokens += AddedContextTokens(contract.ErrorText!);
                            lastCommittedTool = toolName;
                            lastCommittedResult = contract.ErrorText!;
                            continue;
                        }
                        argsJson = contract.NormalizedArgumentsJson!;

                        // Commit intent before any guard or dispatch. Every subsequent path must
                        // commit a matching result so restart recovery can detect uncertainty.
                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                            Type = ProjectEventTypes.ToolCall, Author = "commander",
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id,
                            PayloadJson = ProjectCommanderTools.AuditPayload(toolName, argsJson),
                        });

                        string sig = toolName + "|" + argsJson;
                        recentSignatures[sig] = recentSignatures.TryGetValue(sig, out var n) ? n + 1 : 1;
                        if (recentSignatures[sig] >= StuckIdenticalCallThreshold)
                        {
                            stuckTrips++;
                            string loopResult = $"LOOP DETECTED: identical {toolName} call {recentSignatures[sig]}× — the result won't change. Change strategy and gather different evidence; context rollover will not make repetition productive.";
                            parent.EventLog.Append(new ProjectEvent
                            {
                                ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                                Type = ProjectEventTypes.ToolResult, Author = "system",
                                Text = loopResult, ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = "{\"succeeded\":false}",
                            });
                            llm.AppendToolResult(sessionId, call.id, toolName, loopResult, keepRecentFull: int.MaxValue);
                            liveContextTokens += AddedContextTokens(loopResult);
                            lastCommittedTool = toolName;
                            lastCommittedResult = loopResult;
                            if (stuckTrips >= maxLoopTrips)
                            {
                                outcomeText = $"Wake stopped by the convergence guard after {stuckTrips} repeated-call loop trips.";
                                parent.RuntimeState.SetResumeAction(projectID, new ProjectResumeAction
                                {
                                    Kind = "loop-recovery",
                                    RecordedBy = "commander",
                                    ToolName = toolName,
                                    Summary = $"The previous wake hit the convergence guard after repeatedly attempting {DescribeCall(toolName, argsJson)}. Inspect current external state, do not repeat those inputs, and use a materially different strategy or report the durable obstacle."
                                });
                                parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.CommanderMessage, "commander",
                                    $"Stopped after {stuckTrips} repeated-call detections. The next attempt must use a different strategy, not repeat the same tool inputs."));
                                goto done;
                            }
                            continue;
                        }

                        CommanderToolResult result;
                        try
                        {
                            dispatchedToolCalls++;
                            result = await parent.CommanderToolDispatch(project, "commander", wakeID, toolName, argsJson, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            string cancelled = $"TOOL_CANCELLED: {toolName} was interrupted before a result could be committed; its external outcome is unknown. Inspect state before retrying.";
                            parent.EventLog.Append(new ProjectEvent
                            {
                                ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                                Type = ProjectEventTypes.ToolResult, Author = "system",
                                Text = cancelled, ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = "{\"succeeded\":false}",
                            });
                            parent.RuntimeState.SetResumeAction(projectID, new ProjectResumeAction
                            {
                                Kind = "interrupted-tool", RecordedBy = "commander", ToolName = toolName,
                                Summary = $"Inspect external state after interrupted call {DescribeCall(toolName, argsJson)} before any retry.",
                            });
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result = new CommanderToolResult($"TOOL_EXECUTION_FAILED: {toolName}: {Trunc(ex.Message, 500)}")
                            { Succeeded = false };
                        }
                        result = ProjectToolContract.AttachWarnings(contract, result);
                        if (ProjectWorkProgress.RecordIfNovel(parent.RuntimeState, projectID, "commander", toolName, argsJson, result))
                            productiveActions++;

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                            Type = ProjectEventTypes.ToolResult, Author = "commander",
                            Text = result.AuditText ?? result.ResultText, ToolName = toolName, ToolCallId = call.id,
                            ArtifactIDs = result.ArtifactIDs,
                            PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { succeeded = result.Succeeded }),
                        });
                        llm.AppendToolResult(sessionId, call.id, toolName, result.ResultText, keepRecentFull: int.MaxValue);
                        liveContextTokens += AddedContextTokens(result.ResultText);
                        lastCommittedTool = toolName;
                        lastCommittedResult = result.ResultText;

                        // Vision return: the post-action screenshot rides a follow-up user message
                        // (tool-result image support is inconsistent across providers — same
                        // approach Stratum uses for renders). Old screenshots auto-compact.
                        if (visionEnabled && result.Jpeg != null)
                        {
                            var frames = result.Frames.Count > 0
                                ? result.Frames.Select(f => (f.Jpeg, "image/jpeg")).ToList()
                                : new List<(byte[] data, string mimeType)> { (result.Jpeg, "image/jpeg") };
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Visual result after {toolName} (oldest to newest). The final frame is current and gridded; verify it before acting further.",
                                frames);
                            liveContextTokens += 1200L * Math.Max(1, frames.Count);
                        }

                        if (result.EndWake) { outcomeText = "Wake ended by a tool (constraint)."; goto done; }
                    }

                    // Crossing a boundary never discards calls that the model already returned.
                    // Re-evaluate after the entire batch so a batch that crosses the tool-call
                    // limit is also committed atomically before the fresh context starts.
                    sliceBoundary = ProjectWorkSliceBoundary.Describe(
                        toolCalls, sliceToolCalls, modelTurns, sliceModelTurns,
                        liveContextTokens, sliceTokenBudget);
                    if (sliceBoundary != null)
                    {
                        endedAtWorkSlice = true;
                        string rollover = ProjectWorkSliceBoundary.CompletedBatchMessage(
                            sliceBoundary, resp.ToolCalls.Count);
                        parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.Status, "system", rollover));
                        parent.RuntimeState.SetResumeAction(projectID, new ProjectResumeAction
                        {
                            Kind = "work-slice",
                            Summary = ProjectWorkSliceBoundary.ResumeSummary(sliceBoundary!,
                                resp.ToolCalls.Select(x => x.function?.name), lastCommittedTool,
                                lastCommittedResult, resp.Response),
                            RecordedBy = "commander",
                            ToolName = "resume",
                        });
                        llm.ResetSession(sessionId);
                        outcomeText = rollover;
                        break;
                    }
                }
                done: ;
            }
            catch (OperationCanceledException)
            {
                outcome = ProjectEventTypes.WakeCancelled;
                outcomeText = "Wake cancelled because the project was paused, archived, or a recovery was requested.";
            }
            catch (OpenRouterCreditExhaustedException ex)
            {
                DateTime retryAt = DateTime.UtcNow.AddMinutes(15);
                var failure = new ProjectExecutionFailure
                {
                    Category = ProjectFailureCategory.Capacity,
                    Code = "OpenRouterCreditExhausted",
                    Summary = ex.Message,
                    Retryable = true,
                    RetryAt = retryAt,
                    WakeID = wakeID,
                    Provider = "OpenRouter",
                    HttpStatus = 402,
                };
                parent.RuntimeState.RecordExecutionFailure(projectID, failure, openCircuit: true, circuitRetryAt: retryAt);
                parent.RuntimeState.RecordDependencyHealth(projectID, ProjectProviderFailure.DependencyKey,
                    healthy: false, failure.Code, failure.Summary, retryAt);
                outcomePayloadJson = JsonConvert.SerializeObject(new
                    { provider = "OpenRouter", status = 402, code = failure.Code, remainingUsd = ex.Check.RemainingUsd });
                outcome = ProjectEventTypes.WakeDeferred;
                outcomeText = $"Wake deferred before model dispatch: {ex.Message} Automatic retry after {retryAt:O}.";
            }
            catch (RemoteLLMException ex)
            {
                var failure = ProjectProviderFailure.ToExecutionFailure(ex, wakeID);
                string providerDetail = ProjectProviderFailure.Describe(ex);
                outcomePayloadJson = ProjectProviderFailure.ToPayloadJson(ex);
                DateTime retryAt = ProjectProviderFailure.AutomaticRetryAt(ex);
                // A provider failure is telemetry, never an autonomous project veto. Keep the
                // circuit finite so a restored provider or adjusted route can recover itself.
                failure.Retryable = true;
                failure.RetryAt = retryAt;
                parent.RuntimeState.RecordExecutionFailure(projectID, failure, openCircuit: true, circuitRetryAt: retryAt);
                parent.RuntimeState.RecordDependencyHealth(projectID, ProjectProviderFailure.DependencyKey,
                    healthy: false, ex.Kind.ToString(), providerDetail, retryAt);
                outcome = ProjectEventTypes.WakeDeferred;
                outcomeText = $"Wake deferred by provider failure: {providerDetail}; automatic retry after {retryAt:O}.";
            }
            catch (Exception ex)
            {
                outcome = ProjectEventTypes.WakeFailed;
                outcomeText = $"Wake failed: {ex.Message}";
                var currentHealth = parent.RuntimeState.Get(projectID).Health;
                bool openCircuit = currentHealth.ConsecutiveFailures >= 2;
                DateTime? retryAt = openCircuit ? DateTime.UtcNow.AddMinutes(15) : null;
                parent.RuntimeState.RecordExecutionFailure(projectID, new ProjectExecutionFailure
                {
                    Category = ProjectFailureCategory.Unknown,
                    Code = ex.GetType().Name,
                    Summary = Trunc(ex.Message, 400),
                    Retryable = true,
                    RetryAt = retryAt,
                    WakeID = wakeID,
                }, openCircuit, retryAt);
                if (openCircuit)
                {
                    outcome = ProjectEventTypes.WakeDeferred;
                    outcomeText = $"Wake deferred after repeated infrastructure failures; retry after {retryAt:O}.";
                }
            }
            finally
            {
                try { if (parent.Desktops != null) await parent.Desktops.ReleaseAgentInputsAsync(projectID, "commander"); } catch { }
                try
                {
                    // Per-wake cost attribution: the ledger is cumulative, so stamp this wake's own
                    // token spend + cost onto its closing event for the timeline/reports. wakeCostUsd is
                    // the real OpenRouter spend when available, else the provisional estimate.
                    if (wakePromptTokens > 0 || wakeCompletionTokens > 0)
                        outcomeText += $" (this wake: ~${wakeCostUsd:0.###}, {wakePromptTokens + wakeCompletionTokens} tokens)";
                    var outcomeEvent = WakeEvt(projectID, wakeID, outcome, "system", outcomeText);
                    outcomeEvent.PayloadJson = outcomePayloadJson;
                    parent.EventLog.Append(outcomeEvent);
                    parent.EventLog.Append(ProjectWakeDiagnostics.Create(projectID, wakeID, "commander",
                        outcome, wakeStartedAtUtc, modelTurns, toolCalls, dispatchedToolCalls, productiveActions,
                        emptyResponses, stuckTrips, endedAtWorkSlice, initialModel, finalModel,
                        sliceToolCalls, sliceModelTurns, liveContextTokens, sliceTokenBudget, lastCommittedTool));

                    // A failed Klives-triggered wake must not read as silence either.
                    if ((outcome is ProjectEventTypes.WakeFailed or ProjectEventTypes.WakeDeferred)
                        && klivesInvolved && parent.DiscordManager != null)
                    {
                        try { await parent.DiscordManager.PostCommanderReplyAsync(project, $"⚠️ {outcomeText}"); }
                        catch { }
                    }

                    // Refresh budget/org in the digest, then compact — never in the hot path.
                    await parent.RebuildDigestAfterWakeAsync(project, wakeStartSeq);
                }
                catch { /* never mask the wake outcome */ }

                // Hold the single-flight marker through the complete postamble. Clearing it before
                // the outcome and digest writes allowed a replacement wake to overlap those state
                // mutations. The wake ID check fences a stale finally block defensively.
                lock (WakeGate(projectID))
                {
                    var digest = parent.Digests.GetDigest(projectID);
                    digest.RecentStuckLoopTrips = stuckTrips;
                    if (digest.ActiveWakeID == wakeID) digest.ActiveWakeID = null;
                    parent.Digests.SaveDigest(digest);
                }

                long? verifiedProgressSequence = parent.EventLog.ReadSince(projectID, wakeStartSeq, max: 2000)
                    .Where(e => e.Type is ProjectEventTypes.ArtifactAdded or ProjectEventTypes.AgentSpawned
                        or ProjectEventTypes.GrandPlanProgress or ProjectEventTypes.ProjectFileChanged
                        or ProjectEventTypes.AccountChanged or ProjectEventTypes.MoneySpent
                        or ProjectEventTypes.CheckpointChanged)
                    .Select(e => (long?)e.Sequence).Max();
                if (!verifiedProgressSequence.HasValue && productiveActions > 0)
                    verifiedProgressSequence = parent.EventLog.ReadSince(projectID, wakeStartSeq, max: 2000)
                        .Where(e => e.Type == ProjectEventTypes.ToolResult && e.AgentID == "commander")
                        .Select(e => (long?)e.Sequence).Max();
                if (outcome == ProjectEventTypes.WakeCompleted)
                {
                    parent.RuntimeState.ClearDependencyHealth(projectID, ProjectProviderFailure.DependencyKey);
                    parent.RuntimeState.RecordExecutionSuccess(projectID, verifiedProgressSequence);
                }
                // The durable inbox entry is only removed after a successful wake. Failed and
                // deferred wakes release their claim, allowing recovery/retry to replay the exact
                // payload instead of silently losing it.
                if (!string.IsNullOrWhiteSpace(claimedTriggerID))
                {
                    try
                    {
                        parent.RuntimeState.AcknowledgeTrigger(projectID, claimedTriggerID, wakeID,
                            leaseGeneration, outcome == ProjectEventTypes.WakeCompleted);
                    }
                    catch { /* never mask the wake outcome */ }
                }
                parent.RuntimeState.ReleaseWakeLease(projectID, wakeID, leaseGeneration);
                var finalProjectStatus = parent.Store.GetProject(projectID)?.Status;
                if (finalProjectStatus == ProjectStatus.Paused)
                    parent.RuntimeState.SetDisposition(projectID, ProjectExecutionDisposition.Paused);
                else if (finalProjectStatus == ProjectStatus.Archived)
                    parent.RuntimeState.SetDisposition(projectID, ProjectExecutionDisposition.Archived);
                else if (finalProjectStatus == ProjectStatus.Completed)
                    parent.RuntimeState.SetDisposition(projectID, ProjectExecutionDisposition.Completed);

                // Deregister this wake's cancellation source (only if it's still ours).
                activeWakeCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(projectID, cts));
                parent.StimulusQueue.AcknowledgeWake(wakeID, outcome == ProjectEventTypes.WakeCompleted);

                // A context rollover never limits productive work. Queued stimuli take precedence;
                // otherwise a fresh wake resumes immediately from the durable checkpoint.
                bool madeUsefulProgress = verifiedProgressSequence.HasValue || productiveActions > 0;
                bool continueAfterSlice = endedAtWorkSlice && madeUsefulProgress
                    && outcome == ProjectEventTypes.WakeCompleted;
                var consumedResume = parent.RuntimeState.Get(projectID).Checkpoint.ResumeAction;
                if (ProjectWorkSliceBoundary.ShouldClearConsumedResume(endedAtWorkSlice, consumedResume))
                    parent.RuntimeState.ClearResumeAction(projectID, consumedResume!.ActionID);
                if (!DrainPendingTriggers(projectID) && continueAfterSlice)
                    ContinueProductiveWork(projectID);
            }
        }

        /// <summary>Renews productive work in a fresh context. There is deliberately no continuation
        /// count limit: budget, cancellation, completion, blockers and convergence are the stopping
        /// conditions; context rollover is not.</summary>
        private void ContinueProductiveWork(string projectID)
        {
            try
            {
                var refreshed = parent.Store.GetProject(projectID);
                if (refreshed == null || refreshed.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) return;
                string resume = parent.RuntimeState.Get(projectID).Checkpoint.ResumeAction?.Summary
                    ?? "Resume from the most recent verified action and continue the current objective.";
                Wake(refreshed,
                    "Automatic context rollover: the previous work slice ended, not the work. " +
                    $"Exact resume checkpoint: {resume}");
            }
            catch { /* never mask the wake outcome */ }
        }

        /// <summary>Klives-message triggers get their wake's closing status mirrored to Discord.</summary>
        private static bool TriggeredByKlives(string trigger) =>
            trigger.Contains("Message from Klives", StringComparison.Ordinal);

        /// <summary>Re-wakes the Commander with any triggers or steers that arrived during (or at the tail
        /// end of) the wake that just ended — so nothing is stranded by the finish/enqueue race.
        /// Returns true if a follow-up wake was started for them.</summary>
        private bool DrainPendingTriggers(string projectID)
        {
            try
            {
                pendingTriggers.TryRemove(projectID, out var queued);
                steerQueue.TryRemove(projectID, out var leftoverSteers);
                agentSteerQueue.TryRemove(projectID, out var leftoverAgentMessages);
                var refreshed = parent.Store.GetProject(projectID);
                if (refreshed == null || refreshed.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) return false; // halted/archived: drop, events stay logged

                var missed = new List<string>();
                if (queued != null) missed.AddRange(queued);
                if (leftoverSteers != null) missed.AddRange(leftoverSteers.Select(s => $"Message from Klives: {s.Text}"));
                if (leftoverAgentMessages != null) missed.AddRange(leftoverAgentMessages);
                // RuntimeState is the durable wake inbox. Earlier versions wrote here when a
                // wake was busy, but only drained the in-memory queues above; after a restart
                // every such trigger was stranded forever. Fold all applicable persisted work
                // back into the scheduler. We start one exact payload at a time so WakeLocked's
                // exact-match removal acknowledges it without losing the remaining FIFO work.
                var runtime = parent.RuntimeState.Get(projectID);
                var durableTriggers = parent.RuntimeState.ListPendingTriggers(projectID, includeClaimed: false)
                    .Where(t => ProjectRuntimeStateStore.EvaluateApplicability(t, runtime, DateTime.UtcNow)
                        == ProjectWakeTriggerApplicability.Applicable)
                    .ToList();
                var durablePayloads = durableTriggers.Select(t => t.Payload).ToHashSet(StringComparer.Ordinal);
                missed.AddRange(durableTriggers.Select(t => t.Payload));
                // Legacy/in-flight keepalives are ephemeral and state-specific. A planning nudge
                // queued before plan approval must never be replayed into an Active project (and
                // vice versa). Current keepalives no longer queue, but this also drains old state.
                missed.RemoveAll(t => t.StartsWith("Periodic keepalive:", StringComparison.Ordinal)
                    && ((refreshed.Status == ProjectStatus.Active && t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))
                        || (refreshed.Status == ProjectStatus.Planning && !t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))));
                missed = missed.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                if (missed.Count == 0) return false;

                // Preserve any legacy in-memory-only remainder before consuming the first one.
                // The durable cap is 512 and EnqueueTrigger returns a visible store result; unlike
                // the old 12-item in-memory cap this cannot silently forget a command.
                foreach (string trigger in missed.Where(x => !durablePayloads.Contains(x)))
                    try { parent.RuntimeState.EnqueueTrigger(projectID, TriggerFor(trigger)); } catch { }
                Wake(refreshed, missed[0]);
                return true;
            }
            catch { return false; /* never mask the wake outcome */ }
        }

        /// <summary>
        /// Watchdog escalation (P7): forces a fresh wake even if the digest still marks one active
        /// — the previous wake is presumed wedged. Safe by construction: rehydrate-on-wake means
        /// there is no in-memory state to corrupt, so clearing the stale marker and waking again is
        /// indistinguishable from any other wake. Never a hard kill.
        /// </summary>
        public string ForceWake(Project project, string reason)
        {
            string trigger = $"[watchdog recovery] {reason}";
            lock (WakeGate(project.ProjectID))
            {
                var digest = parent.Digests.GetDigest(project.ProjectID);
                if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID))
                {
                    // Recovery queues behind the current single-flight lease. Cancellation is a
                    // request, not permission to overlap: the old wake's finally block starts this
                    // trigger only after its complete postamble has relinquished the marker.
                    var q = pendingTriggers.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>());
                    if (!q.Contains(trigger) && q.Count < MaxPendingTriggers) q.Enqueue(trigger);
                    parent.RuntimeState.EnqueueTrigger(project.ProjectID, TriggerFor(trigger));
                    var lease = parent.RuntimeState.Get(project.ProjectID).ActiveWakeLease;
                    if (lease != null)
                        parent.RuntimeState.RequestWakeCancellation(project.ProjectID, lease.WakeID, lease.Generation, reason);
                    if (activeWakeCts.TryGetValue(project.ProjectID, out var oldCts))
                    {
                        try { oldCts.Cancel(); } catch { }
                    }
                    else cancelBeforeStart[project.ProjectID] = true;
                    return digest.ActiveWakeID!;
                }
                return WakeLocked(project, trigger, queueIfBusy: false) ?? "";
            }
        }

        /// <summary>Startup crash recovery: clear any wake left active by a restart (rehydrate-on-wake makes this safe).</summary>
        public void RecoverInterruptedWakes()
        {
            var recovered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var runtime in parent.RuntimeState.ListWithActiveWakeLeases())
            {
                var lease = runtime.ActiveWakeLease;
                if (lease == null) continue; // worker-only leases are recovered by ProjectSubAgentRunner
                try
                {
                    int uncertainCalls = ProjectToolCallJournal.ReconcileInterruptedWake(
                        parent.EventLog, runtime.ProjectID, lease.WakeID, "commander");
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = runtime.ProjectID, WakeID = lease.WakeID, AgentID = "commander",
                        Type = ProjectEventTypes.WakeCancelled, Author = "system",
                        Text = "Omnipotent restarted mid-wake. The fenced lease was released and its typed resume action was requeued." +
                            (uncertainCalls > 0 ? $" {uncertainCalls} interrupted tool outcome(s) were marked unknown and require inspection before retry." : ""),
                    });
                    ReleaseClaimedTriggers(runtime.ProjectID, lease.WakeID, lease.Generation);
                    parent.RuntimeState.ReleaseWakeLease(runtime.ProjectID, lease.WakeID, lease.Generation);
                    var legacyDigest = parent.Digests.GetDigest(runtime.ProjectID);
                    if (legacyDigest.ActiveWakeID == lease.WakeID)
                    {
                        legacyDigest.ActiveWakeID = null;
                        parent.Digests.SaveDigest(legacyDigest);
                    }
                    var project = parent.Store.GetProject(runtime.ProjectID);
                    if (project?.Status is ProjectStatus.Active or ProjectStatus.Planning)
                    {
                        string resume = runtime.Checkpoint.ResumeAction?.Summary
                            ?? "Rehydrate committed state and continue from the last verified action without re-discovery.";
                        Wake(project, $"Recovery after process restart. Exact resume action: {resume}", queueIfBusy: false);
                    }
                    recovered.Add(runtime.ProjectID);
                }
                catch { }
            }
            foreach (var digest in parent.Digests.AllDigestsWithActiveWakes())
            {
                try
                {
                    if (recovered.Contains(digest.ProjectID))
                    {
                        digest.ActiveWakeID = null;
                        parent.Digests.SaveDigest(digest);
                        continue;
                    }
                    ProjectToolCallJournal.ReconcileInterruptedWake(
                        parent.EventLog, digest.ProjectID, digest.ActiveWakeID, "commander");
                    ReleaseClaimedTriggers(digest.ProjectID, digest.ActiveWakeID, null);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = digest.ProjectID, WakeID = digest.ActiveWakeID,
                        Type = ProjectEventTypes.WakeCancelled, Author = "system",
                        Text = "Omnipotent restarted mid-wake. Committed events preserved; next stimulus rehydrates the Commander.",
                    });
                    digest.ActiveWakeID = null;
                    parent.Digests.SaveDigest(digest);
                }
                catch { }
            }
        }

        /// <summary>
        /// Starts one applicable persisted inbox item for each idle project. This is deliberately
        /// separate from interrupted-wake recovery: a process can restart while no wake is active
        /// yet still have durable human/stimulus work waiting in the inbox.
        /// </summary>
        public void RecoverPendingTriggers()
        {
            foreach (var project in parent.Store.ListProjects())
            {
                if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) continue;
                try
                {
                    lock (WakeGate(project.ProjectID))
                    {
                        if (!string.IsNullOrWhiteSpace(parent.Digests.GetDigest(project.ProjectID).ActiveWakeID)) continue;
                        var runtime = parent.RuntimeState.Get(project.ProjectID);
                        var next = parent.RuntimeState.ListPendingTriggers(project.ProjectID, includeClaimed: false)
                            .FirstOrDefault(t => ProjectRuntimeStateStore.EvaluateApplicability(t, runtime, DateTime.UtcNow)
                                == ProjectWakeTriggerApplicability.Applicable);
                        if (next != null) WakeLocked(project, next.Payload, queueIfBusy: false);
                    }
                }
                catch { /* startup recovery is best effort per project */ }
            }
        }

        /// <summary>
        /// A process may die after claiming a durable inbox item but before the wake's finally
        /// block acknowledges it. Release that claim during recovery so the payload is eligible
        /// for the next fenced wake instead of being stranded forever as "in progress".
        /// </summary>
        private void ReleaseClaimedTriggers(string projectID, string? wakeID, long? fallbackGeneration)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) return;
            foreach (var trigger in parent.RuntimeState.ListPendingTriggers(projectID, includeClaimed: true)
                .Where(x => string.Equals(x.ClaimedByWakeID, wakeID, StringComparison.Ordinal)))
            {
                long? generation = trigger.ClaimedByLeaseGeneration ?? fallbackGeneration;
                if (!generation.HasValue) continue;
                try
                {
                    parent.RuntimeState.AcknowledgeTrigger(projectID, trigger.TriggerID, wakeID,
                        generation.Value, succeeded: false);
                }
                catch { /* recovery proceeds even if one old trigger is malformed */ }
            }
        }

        private static ProjectEvent WakeEvt(string projectID, string wakeID, string type, string author, string text) => new()
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander", Type = type, Author = author, Text = text,
        };

        private static string DescribeCall(string toolName, string argsJson)
        {
            if (toolName.StartsWith("computer_", StringComparison.Ordinal)) return ComputerAudit.Describe(toolName, argsJson);
            try
            {
                string safeArgs = ProjectCommanderTools.AuditPayload(toolName, argsJson) ?? "{}";
                var jo = Newtonsoft.Json.Linq.JObject.Parse(safeArgs);
                var bits = jo.Properties().Take(4).Select(p => $"{p.Name}={Trunc(p.Value.ToString(), 60)}");
                return $"{toolName}({string.Join(", ", bits)})";
            }
            catch { return toolName; }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

        private static long AddedContextTokens(string? text) =>
            ProjectsContextBudget.EstimateTokens(text) + 16L; // role/tool envelope overhead

        private static ProjectWakeTrigger TriggerFor(string text)
        {
            var kind = text.StartsWith("Periodic keepalive:", StringComparison.Ordinal) ? ProjectWakeTriggerKind.Keepalive
                : text.Contains("Message from Klives", StringComparison.Ordinal) ? ProjectWakeTriggerKind.HumanMessage
                : text.StartsWith("Continuation:", StringComparison.Ordinal)
                    || text.StartsWith("Automatic context rollover:", StringComparison.Ordinal) ? ProjectWakeTriggerKind.Continuation
                : text.Contains("watchdog", StringComparison.OrdinalIgnoreCase) ? ProjectWakeTriggerKind.Recovery
                : text.Contains("sub-agent", StringComparison.OrdinalIgnoreCase) ? ProjectWakeTriggerKind.AgentMessage
                : ProjectWakeTriggerKind.Other;
            return new ProjectWakeTrigger
            {
                Kind = kind,
                Payload = text,
                Priority = kind switch
                {
                    ProjectWakeTriggerKind.HumanMessage => 100,
                    ProjectWakeTriggerKind.Recovery => 90,
                    ProjectWakeTriggerKind.AgentMessage => 60,
                    ProjectWakeTriggerKind.Continuation => 30,
                    ProjectWakeTriggerKind.Keepalive => -10,
                    _ => 0,
                },
                Durable = kind != ProjectWakeTriggerKind.Keepalive,
                DiscardWhenInapplicable = kind == ProjectWakeTriggerKind.Keepalive,
                CoalescingKey = kind == ProjectWakeTriggerKind.Keepalive ? "keepalive" : null,
                AllowedDispositions = new List<ProjectExecutionDisposition>
                {
                    ProjectExecutionDisposition.Running,
                    ProjectExecutionDisposition.Waiting,
                },
            };
        }

    }
}
