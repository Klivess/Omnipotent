using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.ComputerControl;
using System.Collections.Concurrent;

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
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> steerQueue = new(StringComparer.Ordinal);

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
        public void Steer(Project project, string text)
        {
            // Origin stamp travels WITH the text: a steer can be injected mid-wake seconds from
            // now, or replayed as a missed-steer wake much later — either way the Commander must
            // see when Klives actually said it, not when it finally reached a session.
            text = $"[sent {Data_Handling.TemporalFormat.NowStamp()}] {text}";
            if (activeWakeCts.ContainsKey(project.ProjectID))
            {
                steerQueue.GetOrAdd(project.ProjectID, _ => new ConcurrentQueue<string>()).Enqueue(text);
                return;
            }
            Wake(project, $"Message from Klives: {text}");
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
            foreach (var pending in parent.RuntimeState.ListPendingTriggers(project.ProjectID)
                .Where(t => t.ClaimedByWakeID == null && string.Equals(t.Payload, triggerDescription, StringComparison.Ordinal)))
                parent.RuntimeState.RemoveTrigger(project.ProjectID, pending.TriggerID);

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
                    Text = $"Commander woke. Trigger: {Trunc(triggerDescription, 200)}",
                    PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { leaseGeneration = acquired.Lease.Generation }),
                });
            }
            catch
            {
                parent.RuntimeState.ReleaseWakeLease(project.ProjectID, wakeID, acquired.Lease.Generation);
                if (digest.ActiveWakeID == wakeID) { digest.ActiveWakeID = null; parent.Digests.SaveDigest(digest); }
                throw;
            }

            _ = Task.Run(() => ExecuteWakeAsync(project, wakeID, acquired.Lease.Generation, triggerDescription));
            return wakeID;
        }

        private async Task ExecuteWakeAsync(Project project, string wakeID, long leaseGeneration, string triggerDescription)
        {
            string projectID = project.ProjectID;
            long wakeStartSeq = parent.EventLog.GetLastSequence(projectID);
            string outcome = ProjectEventTypes.WakeCompleted;
            string outcomeText = "Wake completed; Commander asleep.";
            int stuckTrips = 0;
            bool endedAtWorkSlice = false;
            int productiveActions = 0;
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
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

                var settings = parent.Settings.Get(projectID);
                string model = settings.CommanderModel;
                string fallbackModel = settings.CommanderFallbackModel;
                bool visionEnabled = settings.VisionEnabled;
                int sliceToolCalls = settings.WorkSliceToolCalls;
                int sliceModelTurns = settings.WorkSliceModelTurns;
                int maxLoopTrips = settings.MaxConvergenceTripsPerSlice;
                parent.SubAgents.EnsureCommander(projectID);

                // The Commander is video-tier: core tools plus the full computer-use surface.
                var toolDefs = ProjectCommanderAgent.BuildCoreToolDefinitions();
                toolDefs.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions());

                string sessionId = $"projects-commander-{projectID}-{wakeID}";
                llm.StartToolSession(sessionId, ProjectCommanderAgent.BuildSystemPrompt(project));
                llm.AppendUserMessageToToolSession(sessionId, await parent.WakeCycle.BuildWakeSeed(project, triggerDescription));
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
                int toolCalls = 0;
                int modelTurns = 0;

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
                            llm.AppendUserMessageToToolSession(sessionId, $"STEERING FROM KLIVES (mid-wake — take this into account now): {steer}");
                            klivesInvolved = true;
                        }

                    if (agentSteerQueue.TryGetValue(projectID, out var aq))
                        while (aq.TryDequeue(out var report))
                            llm.AppendUserMessageToToolSession(sessionId,
                                $"MESSAGE FROM A SUB-AGENT (mid-wake — take this into account now): {report}");

                    if (toolCalls >= sliceToolCalls)
                        llm.AppendUserMessageToToolSession(sessionId,
                            $"CONTEXT WORK SLICE COMPLETE ({sliceToolCalls} tool calls). This is not a work limit. Stop calling tools, record verified status and the exact next action; productive work continues immediately in a fresh context.");

                    bool finalModelTurn = modelTurns >= sliceModelTurns - 1;
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
                    try
                    {
                        resp = await llm.QueryToolSessionAsync(sessionId, toolDefs,
                            maxTokensOverride: settings.CommanderMaxOutputTokens,
                            modelOverride: model, cancellationToken: cts.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception primaryError) when (!string.IsNullOrWhiteSpace(fallbackModel)
                        && !string.Equals(model, fallbackModel, StringComparison.OrdinalIgnoreCase))
                    {
                        parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.Status, "system",
                            $"Commander route '{model}' failed ({Trunc(primaryError.Message, 180)}); trying configured fallback '{fallbackModel}' once."));
                        model = fallbackModel;
                        resp = await llm.QueryToolSessionAsync(sessionId, toolDefs,
                            maxTokensOverride: settings.CommanderMaxOutputTokens,
                            modelOverride: model, cancellationToken: cts.Token);
                    }
                    modelTurns++;
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

                    }
                    finally { await budgetLease.DisposeAsync(); }

                    bool sliceComplete = toolCalls >= sliceToolCalls || finalModelTurn;
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0 || sliceComplete)
                    {
                        endedAtWorkSlice = sliceComplete;
                        string final = string.IsNullOrWhiteSpace(resp.Response) ? "(no closing status)" : resp.Response.Trim();
                        if (sliceComplete)
                            parent.RuntimeState.SetResumeAction(projectID, new ProjectResumeAction
                            {
                                Kind = "work-slice",
                                Summary = final,
                                RecordedBy = "commander",
                                ToolName = "resume",
                            });
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
                            });
                            llm.AppendToolResult(sessionId, call.id, toolName, contract.ErrorText!);
                            continue;
                        }
                        argsJson = contract.NormalizedArgumentsJson!;

                        string sig = toolName + "|" + argsJson;
                        recentSignatures[sig] = recentSignatures.TryGetValue(sig, out var n) ? n + 1 : 1;
                        if (recentSignatures[sig] >= StuckIdenticalCallThreshold)
                        {
                            stuckTrips++;
                            llm.AppendToolResult(sessionId, call.id, toolName,
                                $"LOOP DETECTED: identical {toolName} call {recentSignatures[sig]}× — the result won't change. Change strategy and gather different evidence; context rollover will not make repetition productive.");
                            if (stuckTrips >= maxLoopTrips)
                            {
                                outcomeText = $"Wake stopped by the convergence guard after {stuckTrips} repeated-call loop trips.";
                                parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.CommanderMessage, "commander",
                                    $"Stopped after {stuckTrips} repeated-call detections. The next attempt must use a different strategy, not repeat the same tool inputs."));
                                goto done;
                            }
                            continue;
                        }

                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
                            Type = ProjectEventTypes.ToolCall, Author = "commander",
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id,
                            PayloadJson = ProjectCommanderTools.AuditPayload(toolName, argsJson),
                        });

                        var result = await parent.CommanderToolDispatch(project, "commander", wakeID, toolName, argsJson, cts.Token);
                        if (ProjectWorkProgress.RecordIfNovel(parent.RuntimeState, projectID, "commander", toolName, argsJson, result))
                            productiveActions++;

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
                            var frames = result.Frames.Count > 0
                                ? result.Frames.Select(f => (f.Jpeg, "image/jpeg")).ToList()
                                : new List<(byte[] data, string mimeType)> { (result.Jpeg, "image/jpeg") };
                            llm.AppendUserContentToToolSession(sessionId,
                                $"Visual result after {toolName} (oldest to newest). The final frame is current and gridded; verify it before acting further.",
                                frames);
                        }

                        if (result.EndWake) { outcomeText = "Wake ended by a tool (constraint)."; goto done; }
                    }
                }
                done: ;
            }
            catch (OperationCanceledException)
            {
                outcome = ProjectEventTypes.WakeCancelled;
                outcomeText = "Wake cancelled because the project was paused, archived, or a recovery was requested.";
            }
            catch (RemoteLLMException ex)
            {
                var failure = FailureFrom(ex, wakeID);
                if (ex.IsRetryable)
                {
                    DateTime retryAt = DateTime.UtcNow + (ex.RetryAfter is { } ra && ra > TimeSpan.Zero
                        ? ra : TimeSpan.FromMinutes(15));
                    failure.RetryAt = retryAt;
                    parent.RuntimeState.RecordExecutionFailure(projectID, failure, openCircuit: true, circuitRetryAt: retryAt);
                    outcome = ProjectEventTypes.WakeDeferred;
                    outcomeText = $"Wake deferred by {ex.Kind}; retry after {retryAt:O}.";
                }
                else
                {
                    parent.RuntimeState.RecordExecutionFailure(projectID, failure, openCircuit: true);
                    parent.RuntimeState.SetBlocker(projectID, new ProjectRuntimeBlocker
                    {
                        Category = ex.Kind switch
                        {
                            RemoteLLMFailureKind.InsufficientProviderCredit => ProjectBlockerCategory.Capacity,
                            RemoteLLMFailureKind.Authentication => ProjectBlockerCategory.Configuration,
                            _ => ProjectBlockerCategory.Configuration,
                        },
                        Code = ex.Kind.ToString(),
                        Summary = Trunc(ex.Message, 400),
                        Retryable = false,
                    });
                    parent.RuntimeState.SetDisposition(projectID, ProjectExecutionDisposition.Blocked);
                    var blocked = parent.Store.GetProject(projectID);
                    if (blocked != null && blocked.Status is ProjectStatus.Active or ProjectStatus.Planning)
                    {
                        blocked.Status = ProjectStatus.Blocked;
                        blocked.BlockedReason = $"{ex.Kind}: {Trunc(ex.Message, 300)}";
                        blocked.BlockedAt = DateTime.UtcNow;
                        parent.Store.SaveProject(blocked);
                    }
                    parent.EventLog.Append(WakeEvt(projectID, wakeID, ProjectEventTypes.ProjectBlocked, "system",
                        $"Project blocked by action-required provider failure: {ex.Kind}."));
                    outcome = ProjectEventTypes.WakeFailed;
                    outcomeText = $"Wake failed and project blocked: {ex.Kind}.";
                }
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
                    parent.RuntimeState.RecordExecutionSuccess(projectID, verifiedProgressSequence);
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
                if (!endedAtWorkSlice && outcome == ProjectEventTypes.WakeCompleted)
                {
                    var resume = parent.RuntimeState.Get(projectID).Checkpoint.ResumeAction;
                    if (resume?.Kind == "work-slice") parent.RuntimeState.ClearResumeAction(projectID, resume.ActionID);
                }
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
                if (leftoverSteers != null) missed.AddRange(leftoverSteers.Select(s => $"Message from Klives: {s}"));
                if (leftoverAgentMessages != null) missed.AddRange(leftoverAgentMessages);
                // Legacy/in-flight keepalives are ephemeral and state-specific. A planning nudge
                // queued before plan approval must never be replayed into an Active project (and
                // vice versa). Current keepalives no longer queue, but this also drains old state.
                missed.RemoveAll(t => t.StartsWith("Periodic keepalive:", StringComparison.Ordinal)
                    && ((refreshed.Status == ProjectStatus.Active && t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))
                        || (refreshed.Status == ProjectStatus.Planning && !t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))));
                missed = missed.Distinct().ToList();
                if (missed.Count == 0) return false;

                Wake(refreshed, missed.Count == 1 ? missed[0]
                    : "Stimuli that arrived while you were awake:\n\n" + string.Join("\n\n", missed));
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
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = runtime.ProjectID, WakeID = lease.WakeID, AgentID = "commander",
                        Type = ProjectEventTypes.WakeCancelled, Author = "system",
                        Text = "Omnipotent restarted mid-wake. The fenced lease was released and its typed resume action was requeued.",
                    });
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

        private static ProjectEvent WakeEvt(string projectID, string wakeID, string type, string author, string text) => new()
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander", Type = type, Author = author, Text = text,
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

        private static ProjectExecutionFailure FailureFrom(RemoteLLMException ex, string wakeID) => new()
        {
            Category = ex.Kind switch
            {
                RemoteLLMFailureKind.RateLimited => ProjectFailureCategory.RateLimited,
                RemoteLLMFailureKind.InsufficientProviderCredit => ProjectFailureCategory.Capacity,
                RemoteLLMFailureKind.Authentication => ProjectFailureCategory.Authentication,
                RemoteLLMFailureKind.InvalidRequest or RemoteLLMFailureKind.ModelUnavailable => ProjectFailureCategory.Configuration,
                RemoteLLMFailureKind.EmptyResponse or RemoteLLMFailureKind.Network
                    or RemoteLLMFailureKind.Timeout or RemoteLLMFailureKind.ProviderUnavailable => ProjectFailureCategory.Transient,
                _ => ProjectFailureCategory.Unknown,
            },
            Code = ex.Kind.ToString(),
            Summary = Trunc(ex.Message, 400),
            Retryable = ex.IsRetryable,
            RetryAt = ex.RetryAfter is { } retry && retry > TimeSpan.Zero ? DateTime.UtcNow + retry : null,
            WakeID = wakeID,
        };
    }
}
