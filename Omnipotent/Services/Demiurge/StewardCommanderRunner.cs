using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.ComputerControl;
using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Executes one Commander wake: rehydrate context → tool loop → sleep. Cloned from
    /// StratumEngineerTurnRunner's battle-tested loop (background task, stuck-loop detection,
    /// context rollover, post-loop digest compaction, single-flight-per-Steward, crash recovery)
    /// with the design-doc deltas:
    ///   * Wake-triggered, not message-triggered — the seed comes from StewardWakeCycle, not a
    ///     user message. No persistent session survives between wakes (§7).
    ///   * NO hard work or wall-clock timeout. Productive work renews into fresh context slices
    ///     for as long as needed; only budget, cancellation, completion, a real blocker, or
    ///     machine-detected non-convergence stops it.
    /// </summary>
    public class StewardCommanderRunner
    {
        private readonly Demiurge parent;

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

        // What the instant-reply front desk already told Klives, shown to the Commander at its next turn
        // boundary so its own reply carries substance instead of a second acknowledgement.
        private sealed record InstantReplyNote(string Text, DateTime At);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<InstantReplyNote>> instantReplies = new(StringComparer.Ordinal);

        /// <summary>Model turns sent at interactive priority after Klives speaks, until the Commander answers
        /// him. Bounded: a wake can run for hours and must not hold the front of the queue that long.</summary>
        internal const int KlivesInteractiveTurns = 3;

        // The live wake's cancellation source per Steward, so Klives can halt an in-flight wake
        // (pause/archive) instead of waiting for it to run itself out.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> activeWakeCts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> wakeGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> cancelBeforeStart = new(StringComparer.Ordinal);

        // Consecutive wakes that began with free agent slots sitting against unowned dependency-ready
        // work. Only used to escalate the wording of the staffing checkpoint, so it is deliberately
        // in-memory: losing the count on restart costs a firmer sentence, nothing more.
        private readonly ConcurrentDictionary<string, int> underStaffedWakes = new(StringComparer.Ordinal);

        // The last "⚠️" closing each Steward sent Klives. A wake that keeps ending the same way (a provider
        // outage, a fleet halt) used to post the identical warning to the chat and Discord on every retry.
        private readonly ConcurrentDictionary<string, (string Text, DateTime At)> lastTroubleClosings = new(StringComparer.Ordinal);
        internal static readonly TimeSpan TroubleClosingRepeatWindow = TimeSpan.FromMinutes(30);

        private object WakeGate(string stewardID) => wakeGates.GetOrAdd(stewardID, _ => new object());

        /// <summary>True when this exact warning already went to Klives inside the repeat window.</summary>
        internal bool RepeatsRecentTroubleClosing(string stewardID, string closing, DateTime nowUtc)
        {
            if (lastTroubleClosings.TryGetValue(stewardID, out var last)
                && string.Equals(last.Text, closing, StringComparison.Ordinal)
                && nowUtc - last.At < TroubleClosingRepeatWindow)
                return true;
            lastTroubleClosings[stewardID] = (closing, nowUtc);
            return false;
        }

        /// <summary>
        /// Keeps a trigger in the durable inbox for a wake that cannot start yet. The inbox has no payload
        /// dedupe, and a held trigger is usually already there (the drain replays durable payloads), so it
        /// is only added when absent — otherwise every refused attempt would grow the inbox by one.
        /// </summary>
        private void HoldTrigger(string stewardID, string triggerDescription)
        {
            try
            {
                if (parent.RuntimeState.ListPendingTriggers(stewardID)
                    .Any(t => string.Equals(t.Payload, triggerDescription, StringComparison.Ordinal))) return;
                parent.RuntimeState.EnqueueTrigger(stewardID, TriggerFor(triggerDescription));
            }
            catch { /* the inbox is best-effort here; the keepalive still wakes the Steward */ }
        }

        public StewardCommanderRunner(Demiurge parent)
        {
            this.parent = parent;
        }

        /// <summary>
        /// Delivers a Klives message with minimal latency. If a wake is in flight, the message is
        /// injected into that live session so the Commander honours it within the current wake
        /// (fast steering, item 5); otherwise it wakes the Commander normally. The caller is
        /// responsible for having logged the KlivesMessage event.
        /// </summary>
        public string? Steer(Steward steward, string text, string? directiveID = null)
        {
            // Origin stamp travels WITH the text: a steer can be injected mid-wake seconds from
            // now, or replayed as a missed-steer wake much later — either way the Commander must
            // see when Klives actually said it, not when it finally reached a session.
            text = $"[sent {Data_Handling.TemporalFormat.NowStamp()}] {text}";
            lock (WakeGate(steward.StewardID))
            {
                // The persisted lease/digest is authoritative during the short startup window
                // before activeWakeCts is registered. The old activeWakeCts-only check could
                // start a second wake or strand a steer in exactly that race.
                var activeWakeID = parent.RuntimeState.Get(steward.StewardID).ActiveWakeLease?.WakeID
                    ?? parent.Digests.GetDigest(steward.StewardID).ActiveWakeID;
                if (!string.IsNullOrWhiteSpace(activeWakeID))
                {
                    steerQueue.GetOrAdd(steward.StewardID, _ => new ConcurrentQueue<QueuedSteer>())
                        .Enqueue(new QueuedSteer(text, directiveID));
                    return activeWakeID;
                }
                return WakeLocked(steward, $"Message from Klives: {text}", queueIfBusy: true);
            }
        }

        /// <summary>
        /// Delivers a durable Klives directive with next-safe-turn latency. The directive store is
        /// the recovery source of truth; this queue only makes an already-running wake react now.
        /// </summary>
        public string? DeliverHumanDirective(Steward steward, StewardDirective directive)
        {
            if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning)) return null;
            return Steer(steward, $"[directive:{directive.DirectiveID}] {directive.Text}", directive.DirectiveID);
        }

        /// <summary>
        /// Delivers a sub-agent report to the current Commander wake when possible. Returning the
        /// active wake ID lets the durable stimulus queue claim the message instead of retrying it
        /// until the Commander sleeps. If the Commander is asleep, this starts a fresh wake.
        /// </summary>
        public string? DeliverAgentMessage(Steward steward, string trigger)
        {
            if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning)) return null;
            lock (WakeGate(steward.StewardID))
            {
                var digest = parent.Digests.GetDigest(steward.StewardID);
                if (string.IsNullOrWhiteSpace(digest.ActiveWakeID))
                    return WakeLocked(steward, trigger, queueIfBusy: false);

                var q = agentSteerQueue.GetOrAdd(steward.StewardID, _ => new ConcurrentQueue<string>());
                if (q.Contains(trigger)) return digest.ActiveWakeID;
                if (q.Count >= MaxPendingTriggers) return null;
                q.Enqueue(trigger);
                return digest.ActiveWakeID;
            }
        }

        /// <summary>
        /// Halts the Steward's in-flight wake, if any (Klives paused/archived it). Cancels the
        /// live tool loop; rehydrate-on-wake makes this safe — no in-memory state is corrupted, the
        /// committed event log is intact, and a later resume simply wakes fresh. Returns true if a
        /// wake was actually cancelled.
        /// </summary>
        /// <summary>
        /// True while THIS process is running a Commander wake for the Steward. Deliberately reads
        /// only in-process state (no runtime-state file), because its one caller is the corrupt-state
        /// repair path — asking the unreadable file whether it has a lease would be circular.
        /// </summary>
        public bool HasLiveWake(string stewardID) => activeWakeCts.ContainsKey(stewardID);

        /// <summary>Records an instant reply already sent to Klives, for the Commander's next turn.</summary>
        public void NoteInstantReply(string stewardID, string reply)
        {
            if (string.IsNullOrWhiteSpace(stewardID) || string.IsNullOrWhiteSpace(reply)) return;
            var queue = instantReplies.GetOrAdd(stewardID, _ => new ConcurrentQueue<InstantReplyNote>());
            while (queue.Count >= 4 && queue.TryDequeue(out _)) { }
            queue.Enqueue(new InstantReplyNote(reply, DateTime.UtcNow));
        }

        public bool CancelActiveWake(string stewardID)
        {
            steerQueue.TryRemove(stewardID, out _);
            agentSteerQueue.TryRemove(stewardID, out _);
            instantReplies.TryRemove(stewardID, out _);
            pendingTriggers.TryRemove(stewardID, out _);
            if (activeWakeCts.TryGetValue(stewardID, out var cts))
            {
                var lease = parent.RuntimeState.Get(stewardID).ActiveWakeLease;
                if (lease != null)
                    parent.RuntimeState.RequestWakeCancellation(stewardID, lease.WakeID, lease.Generation, "Steward pause/archive requested.");
                try { cts.Cancel(); return true; } catch { return false; }
            }
            lock (WakeGate(stewardID))
            {
                if (!string.IsNullOrWhiteSpace(parent.Digests.GetDigest(stewardID).ActiveWakeID))
                {
                    var lease = parent.RuntimeState.Get(stewardID).ActiveWakeLease;
                    if (lease != null)
                        parent.RuntimeState.RequestWakeCancellation(stewardID, lease.WakeID, lease.Generation, "Cancellation requested before wake start.");
                    cancelBeforeStart[stewardID] = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Disarms a cancel-on-birth request left by a pause/halt that landed in a wake's startup
        /// window (digest marked a wake active, but its CancellationTokenSource was not registered
        /// yet). The flag is consumed by whichever wake starts NEXT — so without this, the first
        /// wake after a resume is cancelled the instant it is created, and the Steward only gets
        /// moving on the follow-up wake its own postamble schedules.
        /// </summary>
        public void ClearPendingCancellation(string stewardID) => cancelBeforeStart.TryRemove(stewardID, out _);

        /// <summary>
        /// Wakes the Commander in response to a trigger. Returns immediately with the wake ID;
        /// the loop runs in the background and streams events. If a wake is already active on the
        /// Steward, the trigger is queued and re-wakes the Commander when the active wake ends
        /// (returns null) — one wake at a time, per §7, but no stimulus is ever dropped.
        /// </summary>
        /// <param name="urgent">
        /// Klives is waiting on this wake without having written a message — he pressed resume. It is
        /// admitted past the fleet budget and its first model turn goes at interactive priority, exactly
        /// like a reply to him, instead of queueing behind every background wake in the fleet.
        /// </param>
        public string? Wake(Steward steward, string triggerDescription, bool queueIfBusy = true, bool urgent = false)
        {
            // Planning Stewards wake too (to draft the Grand Plan and field Klives' messages);
            // execution tools stay gated until the plan is approved and the Steward flips Active.
            if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning)) return null;
            lock (WakeGate(steward.StewardID))
                return WakeLocked(steward, triggerDescription, queueIfBusy, urgent);
        }

        private string? WakeLocked(Steward steward, string triggerDescription, bool queueIfBusy, bool urgent = false)
        {
            var digest = parent.Digests.GetDigest(steward.StewardID);
            if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID))
            {
                if (queueIfBusy)
                {
                var q = pendingTriggers.GetOrAdd(steward.StewardID, _ => new ConcurrentQueue<string>());
                if (q.Count < MaxPendingTriggers && !q.Contains(triggerDescription))
                    q.Enqueue(triggerDescription);
                try { parent.RuntimeState.EnqueueTrigger(steward.StewardID, TriggerFor(triggerDescription)); } catch { }
                }
                return null; // already awake — queued for the follow-up wake
            }

            // A halted fleet admits no model call, so a wake started now would rebuild the whole brief only
            // to defer at its first turn — and the trigger it released would start the next one at once.
            // That loop ran ~250 wakes an hour per Steward for five days (2026-10-23..28) and posted a
            // "Wake stopped" message for each. This sits ahead of the Klives bypass below on purpose: no
            // backoff he can override applies here, only clearing the halt does. The trigger waits in the
            // durable inbox and the keepalive starts the wake once admission reopens.
            if (parent.FleetAdmissionRefusal() is { } fleetRefusal)
            {
                if (queueIfBusy) HoldTrigger(steward.StewardID, triggerDescription);
                parent.NoteWakeWaiting(steward.StewardID, "commander", "fleet-halt",
                    $"The Commander is held because {fleetRefusal} It starts by itself once the halt is cleared.");
                return null;
            }

            string wakeID = Guid.NewGuid().ToString("N");
            var runtime = parent.RuntimeState.Get(steward.StewardID);
            // Klives writing (or pressing resume) is itself the instruction to try again now. A backoff
            // left by earlier failures — up to fifteen minutes for an open circuit — used to hold his
            // message in the inbox for the whole of it. One probe costs a failed request at worst; if the
            // cause persists, the probe defers again on its own and the backoff resumes from there.
            bool klivesWaiting = urgent || TriggeredByKlives(triggerDescription);
            if (klivesWaiting && (StewardWakeRecovery.BlockedUntil(runtime, "commander", DateTime.UtcNow) != null
                                  || runtime.Health.Circuit.Status == StewardCircuitStatus.Open))
            {
                parent.RuntimeState.ClearProviderAdmission(steward.StewardID);
                if (runtime.Health.Circuit.Status == StewardCircuitStatus.Open)
                    parent.RuntimeState.CloseCircuit(steward.StewardID, halfOpen: true);
                runtime = parent.RuntimeState.Get(steward.StewardID);
            }
            if (StewardWakeRecovery.BlockedUntil(runtime, "commander", DateTime.UtcNow) is { } admissionRetryAt)
            {
                parent.RuntimeState.DeferProviderAdmission(steward.StewardID, "commander", admissionRetryAt);
                if (queueIfBusy)
                    parent.RuntimeState.EnqueueTrigger(steward.StewardID, TriggerFor(triggerDescription));
                parent.NoteWakeWaiting(steward.StewardID, "commander", "provider",
                    $"The Commander is held while the model provider backs off; it retries {StewardWakeWaiting.Until(admissionRetryAt, DateTime.UtcNow)}.");
                return null;
            }
            if (runtime.Health.Circuit.Status == StewardCircuitStatus.Open)
            {
                if (!runtime.Health.Circuit.RetryAt.HasValue || runtime.Health.Circuit.RetryAt > DateTime.UtcNow)
                {
                    parent.NoteWakeWaiting(steward.StewardID, "commander", "circuit",
                        runtime.Health.Circuit.RetryAt is { } circuitRetry
                            ? $"The Commander is paused after repeated failures; it retries {StewardWakeWaiting.Until(circuitRetry, DateTime.UtcNow)}."
                            : "The Commander is paused after repeated failures until the cause is cleared (resume the Steward to retry now).");
                    return null;
                }
                parent.RuntimeState.CloseCircuit(steward.StewardID, halfOpen: true);
            }

            // Wake-boundary admission. Three AIRouter slots keep only so many conversations inside
            // the provider's prefix lifetime; starting one more past that point does not add
            // throughput, it takes every live conversation cold. The trigger stays in the durable
            // inbox and the gate starts this Commander as soon as a permit frees.
            if (!parent.WakeAdmission.TryAdmit(steward.StewardID, StewardWakeAdmission.CommanderAgentID,
                    triggerDescription, out string capacity, mustAdmit: klivesWaiting))
            {
                if (queueIfBusy)
                    parent.RuntimeState.EnqueueTrigger(steward.StewardID, TriggerFor(triggerDescription));
                parent.NoteWakeWaiting(steward.StewardID, "commander", "capacity",
                    $"The Commander is waiting for LLM capacity ({capacity}). It starts by itself the moment a slot frees.");
                return null;
            }

            var acquired = parent.RuntimeState.TryAcquireWakeLease(steward.StewardID, wakeID);
            if (!acquired.Acquired || acquired.Lease == null)
            {
                parent.WakeAdmission.Release(steward.StewardID, StewardWakeAdmission.CommanderAgentID);
                if (queueIfBusy)
                    parent.RuntimeState.EnqueueTrigger(steward.StewardID, TriggerFor(triggerDescription));
                return null;
            }
            // Claim (rather than remove) one durable inbox item for this wake.  A trigger must
            // survive a provider failure/cancellation so it can be replayed; the old exact-match
            // removal acknowledged it before the model had even seen it.
            var claimed = parent.RuntimeState.TryClaimNextTrigger(steward.StewardID, wakeID, acquired.Lease.Generation);
            string effectiveTrigger = claimed.Claimed && claimed.Trigger != null
                ? claimed.Trigger.Payload
                : triggerDescription;
            string? claimedTriggerID = claimed.Claimed ? claimed.Trigger?.TriggerID : null;

            try
            {
                // Legacy/UI mirror; runtime lease is authoritative. Merged in place: a background digest
                // rebuild may be writing the narrative fields of the same file right now.
                parent.Digests.Update(steward.StewardID, d => d.ActiveWakeID = wakeID);
                parent.ClearWakeWaiting(steward.StewardID, "commander");
                parent.EventLog.Append(new StewardEvent
                {
                    StewardID = steward.StewardID,
                    WakeID = wakeID,
                    AgentID = "commander",
                    Type = StewardEventTypes.CommanderWake,
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
                        parent.RuntimeState.AcknowledgeTrigger(steward.StewardID, claimedTriggerID, wakeID,
                            acquired.Lease.Generation, succeeded: false);
                    }
                    catch { }
                }
                parent.RuntimeState.ReleaseWakeLease(steward.StewardID, wakeID, acquired.Lease.Generation);
                parent.WakeAdmission.Release(steward.StewardID, StewardWakeAdmission.CommanderAgentID);
                parent.Digests.Update(steward.StewardID, d => { if (d.ActiveWakeID == wakeID) d.ActiveWakeID = null; });
                throw;
            }

            _ = Task.Run(() => ExecuteWakeAsync(steward, wakeID, acquired.Lease.Generation, effectiveTrigger, claimedTriggerID,
                urgent));
            return wakeID;
        }

        private async Task ExecuteWakeAsync(Steward steward, string wakeID, long leaseGeneration, string triggerDescription,
            string? claimedTriggerID, bool urgent = false)
        {
            string stewardID = steward.StewardID;
            long wakeStartSeq = parent.EventLog.GetLastSequence(stewardID);
            string outcome = StewardEventTypes.WakeCompleted;
            string outcomeText = "Wake completed; Commander asleep.";
            string? outcomePayloadJson = null;
            int stuckTrips = 0;
            int emptyResponseTrips = 0;
            int passiveProseTrips = 0;
            int emptyResponses = 0;
            bool endedAtWorkSlice = false;
            int productiveActions = 0;
            int dispatchedToolCalls = 0;
            int toolCalls = 0, modelTurns = 0;
            int sliceToolCalls = 0, sliceModelTurns = 0, sliceTokenBudget = 0;
            long wakePromptTokens = 0, wakeCompletionTokens = 0; // per-wake cost attribution
            long liveContextTokens = 0; // current request context, NOT cumulative billed prompt tokens
            double wakeCostUsd = 0; // real per-wake spend (OpenRouter usage.cost), falls back to estimate
            bool wakeCostEstimated = false;
            string? lastCommittedTool = null;
            string? lastCommittedResult = null;
            // Rolling record of the last few action→result pairs. Saved as this wake's tail in the
            // finally below, so the NEXT wake opens knowing exactly where this one stopped. Declared
            // out here because the finally block needs it on every exit path.
            var actionTrail = new List<string>();
            string? initialModel = null;
            string? finalModel = null;
            DateTime wakeStartedAtUtc = DateTime.UtcNow;
            var providerRecovery = new StewardWakeRecovery();
            int modelResponses = 0;
            StewardResumeAction? startingResumeAction = null;
            // Whether Klives is expecting a reply from this wake — either it was triggered by his
            // message, or he steered it mid-flight. Drives the Discord reply mirror.
            bool klivesInvolved = TriggeredByKlives(triggerDescription);
            // Whether he has actually been answered yet. A wake has no wall-clock limit, so a reply
            // owed but never sent is how the Commander reads as unresponsive: every exit path below
            // must either carry prose to him or leave this false for the closing backstop.
            bool repliedToKlives = false;
            // Turns that go to the front of the shared LLM queue because Klives is waiting on them.
            int interactiveTurns = klivesInvolved ? KlivesInteractiveTurns : urgent ? 1 : 0;
            bool instantRepliesOn = false;
            // Set once the provider is resolved; read by the RemoteLLMException handler below, which
            // is outside the scope the LLM service is declared in. A flat-fee router's rate limit is a
            // one-minute fair-use window, not a spent balance, and must not stand the Steward down for
            // fifteen minutes.
            bool flatFeeProvider = false;
            using var cts = new CancellationTokenSource();
            activeWakeCts[stewardID] = cts; // registered so Klives can halt this wake
            if (cancelBeforeStart.TryRemove(stewardID, out _)) cts.Cancel();

            try
            {
                var running = parent.RuntimeState.MarkWakeRunning(stewardID, wakeID, leaseGeneration);
                if (!running.Applied) throw new OperationCanceledException(running.Reason);
                startingResumeAction = parent.RuntimeState.Get(stewardID).Checkpoint.ResumeAction;
                var llmServices = await parent.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                    throw new InvalidOperationException("KliveLLM service not available.");
                var llm = (KliveLLM.KliveLLM)llmServices[0];
                // Every call of this wake is routed and recorded as this Steward's Commander lane.
                var llmCaller = KliveLLM.LlmConsumerCatalog.Callers.DemiurgeCommander.For(stewardID);
                flatFeeProvider = await llm.IsFlatFeeProviderAsync(llmCaller);
                if (!await llm.SupportsNativeToolCallingAsync(llmCaller))
                    throw new InvalidOperationException("The Commander requires a remote LLM provider with native tool calling.");
                if (parent.ProviderCredit != null && await llm.IsOpenRouterActiveAsync(llmCaller))
                {
                    var credit = await parent.ProviderCredit.CheckAsync(cts.Token);
                    if (credit.Status == OpenRouterCreditStatus.Exhausted)
                        throw new OpenRouterCreditExhaustedException(credit);
                }

                var settings = parent.Settings.Get(stewardID);
                instantRepliesOn = settings.InstantRepliesEnabled;
                var modelRoutes = settings.CommanderRoutes.ToList();
                if (modelRoutes.Count == 0) throw new InvalidOperationException("Commander has no configured model routes.");
                // Index 0 is the primary. These routes are sent to whichever router KliveLLM's
                // RemoteLLMProvider setting selects. On OpenRouter it receives index 0 as `model` and the
                // later routes as its ordered fallback set (a successful backup is pinned for the rest of
                // this wake); other routers get the primary only — see KliveLLM.ApplyModelFallback.
                string model = modelRoutes[0];
                initialModel = model;
                finalModel = model;
                // Sampling parameters Klives pinned on the Commander route. Unset ones are simply not
                // sent, so an unconfigured route behaves exactly as it did before.
                var commanderParameters = settings.ParametersForRoute(StewardSettings.RouteNames.Commander);
                var routeSampling = ModelParameterCatalog.ToSamplingParameters(commanderParameters);
                string? routeReasoning = ModelParameterCatalog.ReasoningEffort(commanderParameters);
                bool visionEnabled = await StewardAgentToolCatalog.ResolveEffectiveVisionAsync(
                    llm, settings.VisionEnabled, StewardAgentTier.TextImageVideo, modelRoutes, cts.Token, llmCaller);
                // Live indicator: supplying a token sink switches KliveLLM to its SSE transport, which
                // is what lets the Conversation panel show the Commander generating before the turn
                // commits an event. Off → buffered path, and the indicator degrades to phase-only.
                Action<string>? activitySink = settings.LiveActivityStreaming
                    ? chunk => parent.Activity.AppendToken(stewardID, "commander", chunk)
                    : null;
                sliceToolCalls = settings.WorkSliceToolCalls;
                sliceModelTurns = settings.WorkSliceModelTurns;
                sliceTokenBudget = Math.Clamp(settings.WorkSliceTokenBudget, 16_000, 2_000_000);
                int maxOutputTokens = Math.Clamp(settings.CommanderMaxOutputTokens, 512, 32_768);
                int maxLoopTrips = settings.MaxConvergenceTripsPerSlice;
                int toolResultKeepRecent = Math.Clamp(settings.ToolResultKeepRecent, 2, 256);
                bool attemptLedgerEnabled = settings.AttemptLedgerEnabled;
                var contextPolicy = await parent.ResolveContextPolicyAsync(
                    llm, modelRoutes, maxOutputTokens, sliceTokenBudget, cts.Token, llmCaller);
                if (contextPolicy != null)
                {
                    maxOutputTokens = contextPolicy.MaxOutputTokens;
                    sliceTokenBudget = contextPolicy.WorkSliceTokenBudget;
                }
                parent.SubAgents.EnsureCommander(stewardID);

                // The Commander is video-tier: core tools plus the full computer-use surface. The
                // canonical set is what validation, auditing and dispatch speak; the folded set is
                // what the model is offered, kept under the provider tool-count limit.
                var canonicalToolDefs = StewardAgentToolCatalog.BuildCommanderCanonical(visionEnabled);
                var toolDefs = StewardToolFacade.Fold(canonicalToolDefs);

                // Rehydrate current state every wake, then append only changed sections to a
                // bounded, compatible transcript. A rotation uses the full seed from durable stores.
                string sessionId = $"demiurge-commander-{stewardID}";
                var briefSections = new List<ToolSessionBriefSection>();
                await parent.WakeCycle.BuildWakeSeed(steward, triggerDescription,
                    settings.RecentEventsConsidered, settings.RecentEventsBudget,
                    settings.ChronologicalRecentEvents, flatFeeProvider, briefSections);
                var promptStart = llm.StartOrContinueBriefedToolSession(sessionId,
                    StewardCommanderAgent.BuildSystemPrompt(steward, visionEnabled), StewardPromptContinuity.FitReferences(briefSections),
                    StewardPromptContinuity.CompatibilityKey(await llm.GetBriefProviderKeyAsync(llmCaller),
                        modelRoutes, toolDefs, commanderParameters),
                    StewardPromptContinuity.MessageBudget(sliceTokenBudget, maxOutputTokens, toolDefs, contextPolicy),
                    caller: llmCaller);
                parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.Status, "system",
                    $"Prompt continuity: {promptStart.Reason}; appended ~{promptStart.AppendedTokens:N0} tokens " +
                    $"(full brief ~{promptStart.FullBriefTokens:N0})."));

                // Messages the compactor must never summarise. These carry the wake's whole brief; the
                // generic compactor clips a user turn to 240 chars, which would silently erase it.
                int protectedBriefMessages = 1;

                string? previousWakeTail = parent.RuntimeState.Get(stewardID).Checkpoint.LastWakeTail;
                if (!promptStart.Continued && !string.IsNullOrWhiteSpace(previousWakeTail))
                {
                    llm.AppendUserMessageToToolSession(sessionId,
                        "── HOW YOUR PREVIOUS WAKE ENDED (verbatim; the event log and query_events hold the rest) ──\n"
                        + previousWakeTail);
                    if (!promptStart.Continued) protectedBriefMessages++;
                }

                if (klivesInvolved)
                {
                    llm.AppendUserMessageToToolSession(sessionId, instantRepliesOn
                        ? "Klives is waiting on an answer. He has already been sent an instant acknowledgement in your voice " +
                          "(it appears in the conversation as your message). Do not acknowledge again: call reply_to_klives with " +
                          "substance — the answer, the result, or a decision — on your FIRST turn if you can answer from what you " +
                          "know, otherwise straight after the first piece of work that answers him. This wake may run for hours."
                        : "Klives is waiting on an answer. Call reply_to_klives on your FIRST turn, then get on with the work — " +
                          "this wake may run for hours and he should not have to wait that long to hear from you.");
                    if (!promptStart.Continued) protectedBriefMessages++;
                }
                var approvedPlan = parent.GrandPlans.GetCurrentApproved(stewardID)?.Content;
                var readyMilestones = parent.GrandPlans.GetReadyMilestones(stewardID);
                // Staffing pressure has to be CONTINUOUS. This previously fired only while the roster
                // was `Count <= 1` — and ListActive counts the Commander, so one spawn took the count to
                // 2 and the checkpoint never fired again for the life of the Steward. That single
                // condition is why live Stewards sat at one worker with most of the cap unused.
                var roster = parent.SubAgents.ListActive(stewardID);
                int underStaffedStreak = 0;
                if (StewardStaffing.IsUnderStaffed(steward, readyMilestones, roster))
                    underStaffedStreak = underStaffedWakes.AddOrUpdate(stewardID, 1, (_, n) => n + 1);
                else
                    underStaffedWakes.TryRemove(stewardID, out _);
                // The streak counts this wake; the composer wants how many PRECEDING wakes were already
                // under-staffed, so it can say "the 3rd consecutive wake" and mean it.
                string? staffingCheckpoint = StewardStaffing.ComposeCheckpoint(
                    steward, approvedPlan, readyMilestones, roster, DateTime.UtcNow,
                    Math.Max(0, underStaffedStreak - 1));
                if (staffingCheckpoint != null)
                {
                    llm.AppendUserMessageToToolSession(sessionId, staffingCheckpoint);
                    if (!promptStart.Continued) protectedBriefMessages++;
                }

                var recentSignatures = new Dictionary<string, int>(StringComparer.Ordinal);
                // Advances on every dispatched action that can change external state, so an
                // observation repeated after a navigation/click is judged against the new world
                // rather than counted as a loop.
                int worldEpoch = 0;

                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    parent.RuntimeState.HeartbeatWakeLease(stewardID, wakeID, leaseGeneration);
                    if (!await providerRecovery.WaitForAdmissionAsync(
                            () => StewardWakeRecovery.BlockedUntil(parent.RuntimeState.Get(stewardID), "commander", DateTime.UtcNow),
                            () => parent.RuntimeState.HeartbeatWakeLease(stewardID, wakeID, leaseGeneration), cts.Token))
                    {
                        DateTime? retryAt = StewardWakeRecovery.BlockedUntil(parent.RuntimeState.Get(stewardID), "commander", DateTime.UtcNow);
                        parent.RuntimeState.RecordDependencyHealth(stewardID, StewardWakeRecovery.DependencyKey("commander"),
                            false, "ProviderCircuit", "The active wake is waiting for provider recovery.", retryAt);
                        outcome = StewardEventTypes.WakeDeferred;
                        outcomeText = $"Wake deferred by open provider circuit until {retryAt?.ToString("O") ?? "manual recovery"}.";
                        break;
                    }

                    // Budget guardrail: RecordTokenSpendAsync flips Status→BudgetPaused synchronously when
                    // the token budget is exhausted, but doesn't cancel this wake. Re-read status at the
                    // turn boundary so the pause actually stops the loop BEFORE the next (costly) LLM call
                    // — otherwise the in-flight wake overshoots the budget to natural completion.
                    var freshStatus = parent.Store.GetSteward(stewardID)?.Status;
                    if (freshStatus is not (StewardStatus.Active or StewardStatus.Planning))
                    {
                        outcomeText = $"Wake stopped because Steward status is {freshStatus?.ToString() ?? "missing"}.";
                        break;
                    }

                    // Fast steering: fold in any Klives messages that arrived since the last turn so
                    // the Commander adjusts within THIS wake. Injected only at a turn boundary (never
                    // mid tool-call-batch) so the tool_call/tool_result pairing stays valid.
                    if (steerQueue.TryGetValue(stewardID, out var sq))
                        while (sq.TryDequeue(out var steer))
                        {
                            llm.AppendUserMessageToToolSession(sessionId,
                                $"STEERING FROM KLIVES (mid-wake — take this into account now): {steer.Text}\n" +
                                (instantRepliesOn
                                    ? "He has been sent an instant acknowledgement in your voice. Act on this now; call reply_to_klives with the substance — the answer, the result, or a question — as soon as you have it, this turn if you already can."
                                    : "He is waiting on an answer: call reply_to_klives THIS turn, then carry on working. Do not save your response for the end of the wake."));
                            klivesInvolved = true;
                            repliedToKlives = false; // a new message earns its own answer
                            interactiveTurns = KlivesInteractiveTurns;
                        }

                    if (instantReplies.TryGetValue(stewardID, out var notes))
                        while (notes.TryDequeue(out var note))
                        {
                            if (DateTime.UtcNow - note.At > TimeSpan.FromMinutes(30)) continue; // stale: the event log has it
                            llm.AppendUserMessageToToolSession(sessionId,
                                $"(For your awareness) Seconds after Klives' latest message you sent him this instant reply: \"{note.Text}\" " +
                                "Do not repeat or re-acknowledge it. Act on his message; use reply_to_klives for the substance " +
                                "(the result, the answer that needed checking, or a question).");
                        }

                    if (agentSteerQueue.TryGetValue(stewardID, out var aq))
                        while (aq.TryDequeue(out var report))
                            llm.AppendUserMessageToToolSession(sessionId,
                                $"MESSAGE FROM A SUB-AGENT (mid-wake — take this into account now): {report}");

                    var budgetLease = await parent.Budget.TryAcquireLlmTurnAsync(stewardID, cts.Token);
                    if (budgetLease == null)
                    {
                        // Deferred, not completed: no model call was made and no work was attempted.
                        // Leaving this as WakeCompleted inflated the analytics success rate and hid
                        // budget exhaustion behind an apparently healthy wake. The reason comes from
                        // the ledger rather than being assumed here — admission can now also be
                        // refused fleet-wide by the prompt-cache kill switch.
                        outcome = StewardEventTypes.WakeDeferred;
                        outcomeText = "Wake stopped before the next model call because "
                            + parent.Budget.DescribeAdmissionRefusal(stewardID) + ".";
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
                            using var modelTurn = parent.WakeAdmission.BeginModelTurn(
                                stewardID, StewardWakeAdmission.CommanderAgentID);
                            parent.Activity.BeginThinking(stewardID, "commander", "commander", model);
                            // Klives waiting on this turn puts it at the front of the shared queue, in every
                            // scheduler mode; otherwise the session's own class (agent) applies.
                            KliveLLM.AIRouterWorkClass? turnClass = interactiveTurns > 0
                                ? KliveLLM.AIRouterWorkClass.Interactive : null;
                            if (interactiveTurns > 0) interactiveTurns--;
                            resp = await providerRecovery.ExecuteAsync(async () =>
                            {
                                var response = await llm.QueryToolSessionAsync(sessionId, toolDefs,
                                maxTokensOverride: maxOutputTokens,
                                modelOverride: model, cancellationToken: cts.Token,
                                onToken: activitySink,
                                thinkingOverride: routeReasoning, modelRoutes: modelRoutes,
                                compactAboveTokensOverride: contextPolicy?.CompactionTriggerTokens ?? 0,
                                contextWindowTokensOverride: contextPolicy?.ContextWindowTokens,
                                enableOpenRouterContextCompression: contextPolicy != null,
                                samplingParameters: routeSampling,
                                compactProtectPrefixMessages: protectedBriefMessages,
                                workClass: turnClass);
                                if (!response.Success)
                                    throw StewardProviderFailure.FromUnsuccessfulResponse(response.ErrorMessage, response.Model ?? model, maxOutputTokens);
                                return response;
                            }, (failure, attempt, delay) =>
                            {
                                parent.RuntimeState.HeartbeatWakeLease(stewardID, wakeID, leaseGeneration);
                                parent.EventLog.Append(new StewardEvent
                                {
                                    StewardID = stewardID, WakeID = wakeID, AgentID = "commander",
                                    Type = StewardEventTypes.WakeRetry, Author = "system",
                                    Text = $"Keeping this wake active; inference retry {attempt} in {delay.TotalSeconds:0}s. Committed tools and conversation are retained.",
                                    PayloadJson = JsonConvert.SerializeObject(new { reason = failure.Kind.ToString(), attempt, delayMs = delay.TotalMilliseconds }),
                                });
                            }, cts.Token, model: model);
                            modelResponses++;
                            modelTurns++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { modelTurns++; throw; }

                        if (!resp.Success)
                            throw StewardProviderFailure.FromUnsuccessfulResponse(resp.ErrorMessage, resp.Model ?? model, maxOutputTokens);

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
                        liveContextTokens = StewardWorkSliceBoundary.MeasureLiveContext(
                            resp.PromptTokens, resp.CompletionTokens);
                        wakeCostUsd += resp.CostUsd ?? parent.Budget.EstimateCost(resp.PromptTokens, resp.CompletionTokens);
                        if (!resp.CostUsd.HasValue) wakeCostEstimated = true;
                        await parent.Budget.RecordTokenSpendAsync(
                            stewardID,
                            resp.PromptTokens,
                            resp.CompletionTokens,
                            resp.GenerationId,
                            resp.CostUsd,
                            new StewardTokenUsageContext
                            {
                                OccurredAt = DateTime.UtcNow,
                                WakeID = wakeID,
                                AgentID = "commander",
                                Source = "commander",
                                Operation = "wake-model-turn",
                                Model = finalModel,
                                SourceReference = $"{wakeID}:turn:{modelTurns}",
                                Label = $"Commander model turn {modelTurns}",
                                Provider = resp.Provider,
                                RoutedProvider = resp.RoutedProvider,
                                CacheMetricsAvailable = resp.CacheMetricsAvailable,
                                CacheWritePromptTokens = resp.CacheWritePromptTokens,
                                UpstreamInferenceCostUsd = resp.UpstreamInferenceCostUsd,
                                FlatFee = resp.RouterId != null ? resp.FlatFee : null,
                                TurnIndex = modelTurns,
                                RequestDurationMs = resp.RequestDurationMs,
                                QueueDurationMs = resp.QueueDurationMs,
                                ProviderDurationMs = resp.ProviderDurationMs,
                                LatencyBreakdownAvailable = resp.LatencyBreakdownAvailable,
                                RouterStrategy = resp.RouterStrategy,
                                RouterAttempt = resp.RouterAttempt,
                                ContextWasCompacted = resp.ContextWasCompacted,
                                CacheEpochID = resp.CacheEpochID,
                                CacheEpochTurnIndex = resp.CacheEpochTurnIndex,
                                PromptAssemblyStatus = modelTurns == 1 ? promptStart.Reason : null,
                                AppendedBriefTokens = modelTurns == 1 ? promptStart.AppendedTokens : 0,
                                FullBriefTokens = modelTurns == 1 ? promptStart.FullBriefTokens : 0,
                                CacheSessionID = sessionId,
                                ResponseCacheStatus = resp.ResponseCacheStatus,
                                PromptCacheTelemetryVersion = StewardPromptCacheTelemetry.CurrentVersion,
                            },
                            cachedPromptTokens: resp.CachedPromptTokens);
                    }

                    }
                    finally { await budgetLease.DisposeAsync(); }

                    string? sliceBoundary = StewardWorkSliceBoundary.Describe(
                        toolCalls, sliceToolCalls, modelTurns, sliceModelTurns,
                        liveContextTokens, sliceTokenBudget);
                    bool sliceComplete = sliceBoundary != null;
                    if (resp.ToolCalls is { Count: > 0 } || !string.IsNullOrWhiteSpace(resp.Response))
                        emptyResponseTrips = 0;
                    else
                        emptyResponses++;
                    if (resp.ToolCalls is { Count: > 0 }) passiveProseTrips = 0;
                    // A model response and its returned tool calls are one atomic protocol turn.
                    // Even when this response crosses a context boundary, execute and journal the
                    // complete batch first. Rejecting it here caused every batched web_fetch call
                    // at the boundary to be surfaced as a fake tool failure without any HTTP work.
                    if (resp.ToolCalls == null || resp.ToolCalls.Count == 0)
                    {
                        if (!sliceComplete && string.IsNullOrWhiteSpace(resp.Response)
                            && ++emptyResponseTrips <= 2)
                        {
                            parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.Status, "system",
                                $"Model returned an empty response with no tool calls (retry {emptyResponseTrips}/2); the wake remains active."));
                            llm.AppendUserMessageToToolSession(sessionId,
                                "Your last turn was empty. Continue with the next evidence-producing tool action, or give a concrete closing status explaining the real external wait/blocker. Do not silently abandon the wake.");
                            continue;
                        }
                        if (!sliceComplete && !string.IsNullOrWhiteSpace(resp.Response)
                            && parent.Store.GetSteward(stewardID)?.Status is StewardStatus.Active or StewardStatus.Planning
                            && ++passiveProseTrips <= 2)
                        {
                            parent.EventLog.Append(WakeEvt(stewardID, wakeID,
                                StewardEventTypes.CommanderThought, "commander", resp.Response.Trim()));
                            llm.AppendUserMessageToToolSession(sessionId,
                                "That response described status or intent but executed no Steward action. " +
                                StewardPromptHygiene.CapabilityTruth + " Use native tool calls now: perform the " +
                                "next goal step, call complete_steward if the goal is verified complete, request " +
                                "the specific human-only dependency if one exists, or create a hook if the work " +
                                "is genuinely waiting on a future external event. Do not end with another plan.");
                            continue;
                        }
                        endedAtWorkSlice = sliceComplete;
                        string final = string.IsNullOrWhiteSpace(resp.Response)
                            ? StewardWakeStatus.ForCommander(parent.Digests.GetDigest(stewardID),
                                parent.RuntimeState.Get(stewardID), null)
                            : resp.Response.Trim();
                        if (sliceComplete)
                        {
                            parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.Status, "system",
                                $"WORK_SLICE_ROLLOVER: {sliceBoundary}. No tool calls were pending."));
                            parent.RuntimeState.SetResumeAction(stewardID, new StewardResumeAction
                            {
                                Kind = "work-slice",
                                Summary = StewardWorkSliceBoundary.ResumeSummary(sliceBoundary!,
                                    Array.Empty<string?>(), lastCommittedTool, lastCommittedResult, final),
                                RecordedBy = "commander",
                                ToolName = "resume",
                            });
                            // The next wake rotates only if its context budget requires it.
                        }
                        parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.CommanderMessage, "commander", final));
                        // When Klives spoke to the Commander, the answer must reach him where he
                        // asked — the event log alone reads as silence from Discord.
                        if (klivesInvolved && parent.DiscordManager != null)
                        {
                            try { await parent.DiscordManager.PostCommanderReplyAsync(steward, final); }
                            catch { /* best-effort surface */ }
                        }
                        repliedToKlives = true;
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(resp.Response))
                        parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.CommanderThought, "commander", resp.Response.Trim()));

                    foreach (var call in resp.ToolCalls)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        // The model calls a folded tool; everything below this line — audit, loop
                        // detection, policy and dispatch — speaks the canonical name it resolves to.
                        string offeredName = call.function?.name ?? "";
                        var unfolded = StewardToolFacade.Unfold(offeredName, call.function?.arguments);
                        string toolName = unfolded.IsValid ? unfolded.ToolName : offeredName;
                        string argsJson = unfolded.IsValid ? unfolded.ArgumentsJson : call.function?.arguments ?? "";
                        toolCalls++;

                        var contract = unfolded.IsValid
                            ? StewardToolContract.ValidateAndNormalize(toolName, argsJson, canonicalToolDefs)
                            : null;
                        string? rejection = unfolded.ErrorText ?? (contract!.IsValid ? null : contract.ErrorText);
                        if (rejection == null) argsJson = contract!.NormalizedArgumentsJson!;

                        // Commit intent before every result, including validation failures. Every
                        // subsequent path owes this call a matching result for restart recovery.
                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = stewardID, WakeID = wakeID, AgentID = "commander",
                            Type = StewardEventTypes.ToolCall, Author = "commander",
                            Text = DescribeCall(toolName, argsJson), ToolName = toolName, ToolCallId = call.id,
                            PayloadJson = StewardCommanderTools.AuditPayload(toolName, argsJson),
                        });

                        void RecordRejectedResult(string text)
                        {
                            parent.EventLog.Append(new StewardEvent
                            {
                                StewardID = stewardID, WakeID = wakeID, AgentID = "commander",
                                Type = StewardEventTypes.ToolResult, Author = "system",
                                Text = text, ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = "{\"succeeded\":false}",
                            });
                            llm.AppendToolResult(sessionId, call.id, offeredName, text,
                                keepRecentFull: toolResultKeepRecent, preservePromptPrefix: true);
                            liveContextTokens += AddedContextTokens(text);
                            lastCommittedTool = toolName;
                            lastCommittedResult = text;
                        }

                        bool RejectBeforeDispatch(string reason)
                        {
                            int repeats = StewardToolCallConvergence.RegisterRejectedCall(
                                recentSignatures, toolName, argsJson, reason);
                            bool stop = false;
                            string resultText = reason;
                            if (repeats >= StuckIdenticalCallThreshold)
                            {
                                stuckTrips++;
                                resultText = $"LOOP DETECTED: identical invalid {toolName} call {repeats}x was rejected before dispatch. Change the arguments or choose a different operation. Last validation error: {reason}";
                                parent.RuntimeState.RecordFailedApproach(stewardID, new StewardFailedApproach
                                {
                                    Key = $"rejected-call:{toolName}",
                                    Approach = DescribeCall(toolName, argsJson),
                                    Outcome = $"Rejected {repeats}x before dispatch with an unchanged validation result: {reason}",
                                    Instead = "Correct the arguments from the validation error or choose a materially different operation; unchanged invalid calls will never dispatch.",
                                    Evidence = new List<StewardEvidenceReference>
                                    {
                                        new() { Kind = StewardEvidenceKind.ToolResult, Reference = call.id ?? toolName },
                                    },
                                });
                                if (stuckTrips >= maxLoopTrips)
                                {
                                    outcomeText = $"Wake stopped by the convergence guard after {stuckTrips} repeated invalid-call loop trips.";
                                    var previousResume = parent.RuntimeState.Get(stewardID).Checkpoint.ResumeAction;
                                    parent.RuntimeState.SetResumeAction(stewardID, StewardLoopRecovery.Create(
                                        previousResume, "commander", toolName,
                                        $"The previous wake repeatedly submitted invalid arguments for {DescribeCall(toolName, argsJson)}. Read the validation error, change the call shape or operation, and do not resubmit the same payload."));
                                    parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.CommanderMessage, "commander",
                                        $"Stopped after {stuckTrips} repeated invalid-call detections. The next attempt must correct the arguments or use a different operation."));
                                    stop = true;
                                }
                            }
                            RecordRejectedResult(resultText);
                            return stop;
                        }

                        if (rejection != null)
                        {
                            if (RejectBeforeDispatch(rejection)) goto done;
                            continue;
                        }

                        parent.Activity.BeginTool(stewardID, "commander", toolName, DescribeCall(toolName, argsJson));

                        int signatureCount = StewardToolCallConvergence.RegisterCall(
                            recentSignatures, toolName, argsJson, worldEpoch);
                        if (signatureCount >= StuckIdenticalCallThreshold)
                        {
                            stuckTrips++;
                            string loopResult = $"LOOP DETECTED: identical {toolName} call {signatureCount}× with nothing changed in between — the result won't change. Take an action that changes the page/screen first, or gather different evidence.";
                            // Durably record the dead end. A loop trip is a machine-certain signal that this
                            // exact approach does not work, and it must outlive this wake's context: without
                            // it the next wake re-derives the same failure from scratch. Pure observations are
                            // exempt: "you inspected twice" is not an obstacle a future agent needs warning
                            // about, and recording it buries the real dead ends under noise.
                            if (!StewardToolCallConvergence.IsReadOnlyObservation(toolName))
                                parent.RuntimeState.RecordFailedApproach(stewardID, new StewardFailedApproach
                                {
                                    Key = $"repeated-call:{toolName}",
                                    Approach = DescribeCall(toolName, argsJson),
                                    Outcome = $"Repeated {signatureCount}× with an unchanged result; the convergence guard tripped.",
                                    Instead = "Gather different evidence or use a materially different strategy; identical inputs will keep producing this result.",
                                    Evidence = new List<StewardEvidenceReference>
                                    {
                                        new() { Kind = StewardEvidenceKind.ToolResult, Reference = call.id ?? toolName },
                                    },
                                });
                            RecordRejectedResult(loopResult);
                            if (stuckTrips >= maxLoopTrips)
                            {
                                outcomeText = $"Wake stopped by the convergence guard after {stuckTrips} repeated-call loop trips.";
                                var previousResume = parent.RuntimeState.Get(stewardID).Checkpoint.ResumeAction;
                                parent.RuntimeState.SetResumeAction(stewardID, StewardLoopRecovery.Create(
                                    previousResume, "commander", toolName,
                                    $"The previous wake hit the convergence guard after repeatedly attempting {DescribeCall(toolName, argsJson)}. Inspect current external state, do not repeat those inputs, and use a materially different strategy or report the durable obstacle."));
                                parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.CommanderMessage, "commander",
                                    $"Stopped after {stuckTrips} repeated-call detections. The next attempt must use a different strategy, not repeat the same tool inputs."));
                                goto done;
                            }
                            continue;
                        }

                        CommanderToolResult result;
                        try
                        {
                            dispatchedToolCalls++;
                            result = await parent.CommanderToolDispatch(steward, "commander", wakeID, toolName, argsJson, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            string cancelled = $"TOOL_CANCELLED: {toolName} was interrupted before a result could be committed; its external outcome is unknown. Inspect state before retrying.";
                            parent.EventLog.Append(new StewardEvent
                            {
                                StewardID = stewardID, WakeID = wakeID, AgentID = "commander",
                                Type = StewardEventTypes.ToolResult, Author = "system",
                                Text = cancelled, ToolName = toolName, ToolCallId = call.id,
                                PayloadJson = "{\"succeeded\":false}",
                            });
                            parent.RuntimeState.SetResumeAction(stewardID, new StewardResumeAction
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
                        if (StewardToolCallConvergence.IsWorldChanging(toolName)) worldEpoch++;
                        result = StewardToolContract.AttachWarnings(unfolded.Warnings, result);
                        result = StewardToolContract.AttachWarnings(contract, result);
                        if (!visionEnabled && result.Jpeg != null)
                            result = result with
                            {
                                ResultText = result.ResultText +
                                    "\nRAW_IMAGE_OMITTED: this model has no image channel. Verify through desktop op=read_screen/window_state or browser op=inspect; do not infer anything from the unseen frame.",
                            };
                        if (StewardWorkProgress.RecordIfNovel(parent.RuntimeState, stewardID, "commander", toolName, argsJson, result))
                            productiveActions++;

                        // Durable per-intent attempt ledger. The convergence guard above only sees
                        // byte-identical calls inside this one wake; this survives wakes and restarts and
                        // tolerates cosmetic argument differences, so a fourth attempt at something that
                        // failed on three previous wakes actually says so.
                        //
                        // Poll-shaped tools are skipped rather than recorded-and-ignored. They are both the
                        // most frequent calls in any GUI wake and the ones that can never be warned about, so
                        // recording them would rewrite the runtime state file dozens of times per wake for no
                        // signal — and would make a step's attempt count read as 40 screenshots rather than
                        // the three real attempts it made.
                        if (attemptLedgerEnabled && !StewardAttemptKey.IsDeliberatelyRepeatable(toolName))
                        {
                            try
                            {
                                var prior = parent.RuntimeState.RecordAttempt(stewardID,
                                    StewardAttemptKey.For(toolName, argsJson), toolName,
                                    DescribeCall(toolName, argsJson), result.Succeeded, result.ResultText,
                                    wakeID, "commander");
                                if (StewardAttemptWarning.ShouldWarn(prior, result.Succeeded, toolName))
                                    result = result with
                                    {
                                        ResultText = result.ResultText + "\n" + StewardAttemptWarning.Render(prior!),
                                    };
                            }
                            catch { /* the ledger is advisory; never fail a committed tool result over it */ }
                        }

                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = stewardID, WakeID = wakeID, AgentID = "commander",
                            Type = StewardEventTypes.ToolResult, Author = "commander",
                            Text = result.AuditText ?? result.ResultText, ToolName = toolName, ToolCallId = call.id,
                            ArtifactIDs = result.ArtifactIDs,
                            PayloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(new { succeeded = result.Succeeded }),
                        });
                        llm.AppendToolResult(sessionId, call.id, offeredName, result.ResultText,
                            keepRecentFull: toolResultKeepRecent, preservePromptPrefix: true);
                        liveContextTokens += AddedContextTokens(result.ResultText);
                        lastCommittedTool = toolName;
                        lastCommittedResult = result.ResultText;
                        actionTrail.Add($"{DescribeCall(toolName, argsJson)} → {DemiurgeContextBudget.TruncateToTokens(result.ResultText ?? "", 300)}");
                        if (actionTrail.Count > DemiurgeContextBudget.WakeTailActions) actionTrail.RemoveAt(0);
                        // The Commander answered him inside the wake, so the closing backstop below
                        // must not append a second, redundant reply on top of it.
                        if (toolName == "reply_to_klives" && result.Succeeded) { repliedToKlives = true; interactiveTurns = 0; }

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
                                frames, preservePromptPrefix: true);
                            // Charge what the provider will actually bill, from the real frame size. The
                            // old flat 1200/frame under-counted a 1920x1080 screenshot by 40-60%, so the
                            // work-slice boundary consistently let context overshoot its ceiling.
                            liveContextTokens += frames.Count * DemiurgeContextBudget.EstimateImageTokens(
                                result.FrameWidth, result.FrameHeight);
                        }

                        if (result.EndWake) { outcomeText = "Wake ended by a tool (constraint)."; goto done; }
                    }

                    // Crossing a boundary never discards calls that the model already returned.
                    // Re-evaluate after the entire batch so a batch that crosses the tool-call
                    // limit is also committed atomically before the fresh context starts.
                    sliceBoundary = StewardWorkSliceBoundary.Describe(
                        toolCalls, sliceToolCalls, modelTurns, sliceModelTurns,
                        liveContextTokens, sliceTokenBudget);
                    if (sliceBoundary != null)
                    {
                        endedAtWorkSlice = true;
                        string rollover = StewardWorkSliceBoundary.CompletedBatchMessage(
                            sliceBoundary, resp.ToolCalls.Count);
                        parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.Status, "system", rollover));
                        parent.RuntimeState.SetResumeAction(stewardID, new StewardResumeAction
                        {
                            Kind = "work-slice",
                            Summary = StewardWorkSliceBoundary.ResumeSummary(sliceBoundary!,
                                resp.ToolCalls.Select(x => x.function?.name), lastCommittedTool,
                                lastCommittedResult, resp.Response),
                            RecordedBy = "commander",
                            ToolName = "resume",
                        });
                        // The next wake rotates only if its context budget requires it.
                        outcomeText = rollover;
                        break;
                    }
                }
                done: ;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                outcome = StewardEventTypes.WakeCancelled;
                var lease = parent.RuntimeState.Get(stewardID).ActiveWakeLease;
                string reason = lease?.WakeID == wakeID ? lease.CancellationReason ?? "Wake stop requested." : "Wake stop requested.";
                outcomeText = $"Wake cancelled: {reason}";
                outcomePayloadJson = JsonConvert.SerializeObject(new { reason = "wake-cancellation-requested", detail = reason });
            }
            catch (OpenRouterCreditExhaustedException ex)
            {
                DateTime retryAt = DateTime.UtcNow.AddMinutes(15);
                var failure = new StewardExecutionFailure
                {
                    Category = StewardFailureCategory.Capacity,
                    Code = "OpenRouterCreditExhausted",
                    Summary = ex.Message,
                    Retryable = true,
                    RetryAt = retryAt,
                    WakeID = wakeID,
                    Provider = "OpenRouter",
                    HttpStatus = 402,
                };
                parent.RuntimeState.RecordExecutionFailure(stewardID, failure, openCircuit: true, circuitRetryAt: retryAt);
                parent.RuntimeState.RecordDependencyHealth(stewardID, StewardProviderFailure.DependencyKey,
                    healthy: false, failure.Code, failure.Summary, retryAt);
                outcomePayloadJson = JsonConvert.SerializeObject(new
                    { provider = "OpenRouter", status = 402, code = failure.Code, remainingUsd = ex.Check.RemainingUsd });
                outcome = StewardEventTypes.WakeDeferred;
                outcomeText = $"Wake deferred before model dispatch: {ex.Message} Automatic retry after {retryAt:O}.";
            }
            catch (RemoteLLMException ex)
            {
                var failure = StewardProviderFailure.ToExecutionFailure(ex, wakeID);
                string providerDetail = StewardProviderFailure.Describe(ex);
                outcomePayloadJson = StewardProviderFailure.ToPayloadJson(ex);
                DateTime retryAt = StewardWakeRecovery.AutomaticRetryAt(ex, flatFeeProvider);
                // A provider failure is telemetry, never an autonomous Steward veto. Keep the
                // circuit finite so a restored provider or adjusted route can recover itself.
                failure.Retryable = true;
                failure.RetryAt = retryAt;
                parent.RuntimeState.RecordExecutionFailure(stewardID, failure,
                    openCircuit: StewardWakeRecovery.IsSharedFailure(ex), circuitRetryAt: retryAt);
                parent.RuntimeState.RecordDependencyHealth(stewardID, StewardWakeRecovery.DependencyKey("commander"),
                    healthy: false, ex.Kind.ToString(), providerDetail, retryAt);
                outcome = StewardEventTypes.WakeDeferred;
                outcomeText = $"Wake deferred by provider failure: {providerDetail}; automatic retry after {retryAt:O}.";
            }
            catch (Exception ex)
            {
                outcome = StewardEventTypes.WakeFailed;
                outcomeText = $"Wake failed: {ex.Message}";
                var currentHealth = parent.RuntimeState.Get(stewardID).Health;
                bool openCircuit = currentHealth.ConsecutiveFailures >= 2;
                DateTime? retryAt = openCircuit ? DateTime.UtcNow.AddMinutes(15) : null;
                parent.RuntimeState.RecordExecutionFailure(stewardID, new StewardExecutionFailure
                {
                    Category = StewardFailureCategory.Unknown,
                    Code = ex.GetType().Name,
                    Summary = Trunc(ex.Message, 400),
                    Retryable = true,
                    RetryAt = retryAt,
                    WakeID = wakeID,
                }, openCircuit, retryAt);
                if (openCircuit)
                {
                    outcome = StewardEventTypes.WakeDeferred;
                    outcomeText = $"Wake deferred after repeated infrastructure failures; retry after {retryAt:O}.";
                }
            }
            finally
            {
                // The wake is over on every path — clear the live indicator before the postamble, so
                // the panel never shows a Commander still "thinking" after it went to sleep.
                parent.Activity.End(stewardID, "commander");
                try { if (parent.Desktops != null) await parent.Desktops.ReleaseAgentInputsAsync(stewardID, "commander"); } catch { }

                // Hand the NEXT wake a verbatim view of how this one ended. The wake seed rehydrates
                // everything durable from disk, but not the fine-grained "I was midway through X" detail
                // — without this, starting each wake in a fresh session loses it entirely.
                try
                {
                    // A failed preflight must not replace the last real handoff with outage prose.
                    if (actionTrail.Count > 0 || modelResponses > 0 && !string.IsNullOrWhiteSpace(outcomeText))
                    {
                        var tail = new System.Text.StringBuilder();
                        tail.AppendLine($"Ended {DateTime.UtcNow:O} — {outcome}: {outcomeText}");
                        if (actionTrail.Count > 0)
                        {
                            tail.AppendLine($"Last {actionTrail.Count} action(s), oldest first:");
                            foreach (var step in actionTrail) tail.AppendLine($"- {step}");
                        }
                        parent.RuntimeState.SetLastWakeTail(stewardID,
                            DemiurgeContextBudget.TruncateToTokens(tail.ToString(), DemiurgeContextBudget.WakeTailBudget));
                    }
                }
                catch { /* the tail is an optimisation; never fail a wake over it */ }
                try
                {
                    // Per-wake cost attribution: the ledger is cumulative, so stamp this wake's own
                    // token spend + cost onto its closing event for the timeline/reports. wakeCostUsd is
                    // the real OpenRouter spend when available, else the provisional estimate.
                    if (wakePromptTokens > 0 || wakeCompletionTokens > 0)
                        outcomeText += $" (this wake: ~${wakeCostUsd:0.###}, {wakePromptTokens + wakeCompletionTokens} tokens)";
                    var outcomeEvent = WakeEvt(stewardID, wakeID, outcome, "system", outcomeText);
                    outcomeEvent.PayloadJson = outcomePayloadJson;
                    parent.EventLog.Append(outcomeEvent);
                    parent.EventLog.Append(StewardWakeDiagnostics.Create(stewardID, wakeID, "commander",
                        outcome, wakeStartedAtUtc, modelTurns, toolCalls, dispatchedToolCalls, productiveActions,
                        emptyResponses, stuckTrips, endedAtWorkSlice, initialModel, finalModel,
                        sliceToolCalls, sliceModelTurns, liveContextTokens, sliceTokenBudget, lastCommittedTool,
                        wakePromptTokens, wakeCompletionTokens, wakeCostUsd,
                        wakeCostEstimated ? "provisional-or-mixed" : "actual",
                        modelResponses, providerRecovery.Retries, providerRecovery.WaitMs));

                    // Backstop: every exit owes Klives prose when he was waiting on one. The ordinary
                    // no-tool-calls ending already replied and cleared this. A work-slice rollover,
                    // an EndWake tool, a budget stop, a cancellation or a provider failure all used
                    // to end in total silence — which is precisely what an unresponsive Commander
                    // looks like from the chat panel.
                    if (klivesInvolved && !repliedToKlives)
                    {
                        bool trouble = outcome is StewardEventTypes.WakeFailed
                            or StewardEventTypes.WakeDeferred or StewardEventTypes.WakeCancelled;
                        string closing = trouble
                            ? $"⚠️ {outcomeText}"
                            : $"{outcomeText} {StewardWakeStatus.ForCommander(parent.Digests.GetDigest(stewardID), parent.RuntimeState.Get(stewardID))}";
                        // The same trouble, again, is not news: one copy per window instead of one per retry.
                        if (!(trouble && RepeatsRecentTroubleClosing(stewardID, closing, DateTime.UtcNow)))
                        {
                            parent.EventLog.Append(WakeEvt(stewardID, wakeID, StewardEventTypes.CommanderMessage, "commander", closing));
                            if (parent.DiscordManager != null)
                            {
                                try { await parent.DiscordManager.PostCommanderReplyAsync(steward, closing); }
                                catch { }
                            }
                        }
                        repliedToKlives = true;
                    }

                    // The digest rebuild no longer runs here. It is a full utility-model round trip at the
                    // lowest priority in the AIRouter queue, and it used to sit in front of the release
                    // below — so Klives' next message, a worker's report or a resume all waited for it.
                    // It is scheduled after the next wake has been started (see the end of this block);
                    // its digest writes are merged, so it can safely run beside that wake.
                }
                catch { /* never mask the wake outcome */ }

                // Hold the single-flight marker through the outcome writes above. The wake ID check
                // fences a stale finally block defensively.
                lock (WakeGate(stewardID))
                {
                    parent.Digests.Update(stewardID, digest =>
                    {
                        digest.RecentStuckLoopTrips = stuckTrips;
                        if (digest.ActiveWakeID == wakeID) digest.ActiveWakeID = null;
                    });
                }

                long? verifiedProgressSequence = parent.EventLog.ReadSince(stewardID, wakeStartSeq, max: 2000)
                    .Where(e => e.Type is StewardEventTypes.ArtifactAdded or StewardEventTypes.AgentSpawned
                        or StewardEventTypes.GrandPlanProgress or StewardEventTypes.StewardFileChanged
                        or StewardEventTypes.AccountChanged or StewardEventTypes.MoneySpent
                        or StewardEventTypes.CheckpointChanged)
                    .Select(e => (long?)e.Sequence).Max();
                if (!verifiedProgressSequence.HasValue && productiveActions > 0)
                    verifiedProgressSequence = parent.EventLog.ReadSince(stewardID, wakeStartSeq, max: 2000)
                        .Where(e => e.Type == StewardEventTypes.ToolResult && e.AgentID == "commander")
                        .Select(e => (long?)e.Sequence).Max();
                if (outcome == StewardEventTypes.WakeCompleted)
                {
                    parent.RuntimeState.RecordExecutionSuccess(stewardID, verifiedProgressSequence, providerAgentID: "commander");
                }
                // The durable inbox entry is only removed after a successful wake. Failed and
                // deferred wakes release their claim, allowing recovery/retry to replay the exact
                // payload instead of silently losing it.
                if (!string.IsNullOrWhiteSpace(claimedTriggerID))
                {
                    try
                    {
                        parent.RuntimeState.AcknowledgeTrigger(stewardID, claimedTriggerID, wakeID,
                            leaseGeneration, outcome == StewardEventTypes.WakeCompleted);
                    }
                    catch { /* never mask the wake outcome */ }
                }
                parent.RuntimeState.ReleaseWakeLease(stewardID, wakeID, leaseGeneration);
                var finalStewardStatus = parent.Store.GetSteward(stewardID)?.Status;
                if (finalStewardStatus == StewardStatus.Paused)
                    parent.RuntimeState.SetDisposition(stewardID, StewardExecutionDisposition.Paused);
                else if (finalStewardStatus == StewardStatus.Archived)
                    parent.RuntimeState.SetDisposition(stewardID, StewardExecutionDisposition.Archived);
                else if (finalStewardStatus == StewardStatus.Completed)
                    parent.RuntimeState.SetDisposition(stewardID, StewardExecutionDisposition.Completed);

                // Deregister this wake's cancellation source (only if it's still ours).
                activeWakeCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(stewardID, cts));
                parent.StimulusQueue.AcknowledgeWake(wakeID, outcome == StewardEventTypes.WakeCompleted);

                // A context rollover never limits productive work. Queued stimuli take precedence;
                // otherwise a fresh wake resumes immediately from the durable checkpoint.
                bool continueAfterSlice = StewardWorkSliceBoundary.ShouldContinueAssignment(
                    endedAtWorkSlice, outcome == StewardEventTypes.WakeCompleted);
                if (StewardLoopRecovery.ShouldClearAfterProgress(startingResumeAction,
                        outcome == StewardEventTypes.WakeCompleted, productiveActions, stuckTrips))
                    parent.RuntimeState.ClearResumeAction(stewardID, startingResumeAction!.ActionID);
                var consumedResume = parent.RuntimeState.Get(stewardID).Checkpoint.ResumeAction;
                if (modelResponses > 0 && StewardWorkSliceBoundary.ShouldClearConsumedResume(endedAtWorkSlice, consumedResume))
                    parent.RuntimeState.ClearResumeAction(stewardID, consumedResume!.ActionID);
                // A Commander rolling straight into its next slice KEEPS its conversation permit:
                // its prefix is warm right now, and handing the permit to a cold newcomer while this
                // one re-queues turns one warm conversation into two cold ones. Only a Commander that
                // is genuinely going to sleep gives the permit back — and that is the moment another
                // Steward that has been waiting gets to start.
                // A wake deferred before its first model call changed nothing, and the trigger it just
                // released is the one a drain would start next: re-waking here is a hot loop, not a retry.
                // The keepalive restarts it once admission reopens (its queued-work check).
                bool deferredBeforeModel = outcome == StewardEventTypes.WakeDeferred && modelResponses == 0;
                bool rollsStraightOn = !deferredBeforeModel && DrainPendingTriggers(stewardID);
                if (!rollsStraightOn && continueAfterSlice) rollsStraightOn = ContinueProductiveWork(stewardID);
                if (rollsStraightOn) parent.WakeAdmission.NoteTurn(stewardID, StewardWakeAdmission.CommanderAgentID);
                else
                    parent.ResumeDeferredWakes(
                        parent.WakeAdmission.Release(stewardID, StewardWakeAdmission.CommanderAgentID));

                // A productive Commander that stops while open work remains, with nobody else on it, is
                // re-woken within a minute — while its prompt prefix is still warm — instead of idling
                // until the 14-minute keepalive and then paying a cold restart.
                if (!rollsStraightOn && outcome == StewardEventTypes.WakeCompleted && productiveActions > 0 && !endedAtWorkSlice)
                    ScheduleMomentum(steward, wakeStartSeq);

                // Refresh the standing digest in the background now that the next wake (if any) is
                // already on its way. Skipped for a cancelled wake (Klives paused it): the watermark is
                // sequence-based, so the next rebuild covers these events anyway.
                if (modelResponses > 0 && outcome != StewardEventTypes.WakeCancelled)
                    parent.ScheduleDigestRebuild(steward, wakeStartSeq);
            }
        }

        /// <summary>Queues the momentum follow-up described in <see cref="StewardMomentum"/>, if it applies.</summary>
        private void ScheduleMomentum(Steward steward, long wakeStartSeq)
        {
            try
            {
                string stewardID = steward.StewardID;
                var fresh = parent.Store.GetSteward(stewardID);
                bool armedHook = parent.EventLog.ReadSince(stewardID, wakeStartSeq, max: 2000)
                    .Any(e => e.Type == StewardEventTypes.HookChanged);
                if (!StewardMomentum.ShouldContinue(
                        parent.Settings.Get(stewardID).MomentumEnabled,
                        fresh?.Status,
                        parent.RuntimeState.HasOpenSteps(stewardID),
                        parent.SubAgentRunner.HasLiveWake(stewardID),
                        parent.Gates.ListPending(stewardID).Count,
                        armedHook))
                    return;
                parent.RuntimeState.EnqueueTrigger(stewardID, StewardMomentum.CreateTrigger(DateTime.UtcNow));
            }
            catch { /* momentum is an optimisation; the keepalive still covers the Steward */ }
        }

        /// <summary>Renews productive work in a fresh context. There is deliberately no continuation
        /// count limit: budget, cancellation, completion, blockers and convergence are the stopping
        /// conditions; context rollover is not.</summary>
        private bool ContinueProductiveWork(string stewardID)
        {
            try
            {
                var refreshed = parent.Store.GetSteward(stewardID);
                if (refreshed == null || refreshed.Status is not (StewardStatus.Active or StewardStatus.Planning)) return false;
                string resume = parent.RuntimeState.Get(stewardID).Checkpoint.ResumeAction?.Summary
                    ?? "Resume from the most recent verified action and continue the current objective.";
                return null != Wake(refreshed,
                    "Continue the active assignment now. " +
                    $"Exact durable checkpoint: {StewardPromptHygiene.ScrubState(resume, "Resume from the latest verified external state.")}");
            }
            catch { return false; /* never mask the wake outcome */ }
        }

        /// <summary>Klives-message triggers get their wake's closing status mirrored to Discord.</summary>
        private static bool TriggeredByKlives(string trigger) =>
            trigger.Contains("Message from Klives", StringComparison.Ordinal);

        /// <summary>Re-wakes the Commander with any triggers or steers that arrived during (or at the tail
        /// end of) the wake that just ended — so nothing is stranded by the finish/enqueue race.
        /// Returns true if a follow-up wake was started for them.</summary>
        private bool DrainPendingTriggers(string stewardID)
        {
            try
            {
                pendingTriggers.TryRemove(stewardID, out var queued);
                steerQueue.TryRemove(stewardID, out var leftoverSteers);
                agentSteerQueue.TryRemove(stewardID, out var leftoverAgentMessages);
                var refreshed = parent.Store.GetSteward(stewardID);
                if (refreshed == null || refreshed.Status is not (StewardStatus.Active or StewardStatus.Planning)) return false; // halted/archived: drop, events stay logged

                var missed = new List<string>();
                if (queued != null) missed.AddRange(queued);
                if (leftoverSteers != null) missed.AddRange(leftoverSteers.Select(s => $"Message from Klives: {s.Text}"));
                if (leftoverAgentMessages != null) missed.AddRange(leftoverAgentMessages);
                // RuntimeState is the durable wake inbox. Earlier versions wrote here when a
                // wake was busy, but only drained the in-memory queues above; after a restart
                // every such trigger was stranded forever. Fold all applicable persisted work
                // back into the scheduler. We start one exact payload at a time so WakeLocked's
                // exact-match removal acknowledges it without losing the remaining FIFO work.
                var runtime = parent.RuntimeState.Get(stewardID);
                var durableTriggers = parent.RuntimeState.ListPendingTriggers(stewardID, includeClaimed: false)
                    .Where(t => StewardRuntimeStateStore.EvaluateApplicability(t, runtime, DateTime.UtcNow)
                        == StewardWakeTriggerApplicability.Applicable)
                    .ToList();
                var durablePayloads = durableTriggers.Select(t => t.Payload).ToHashSet(StringComparer.Ordinal);
                missed.AddRange(durableTriggers.Select(t => t.Payload));
                // Legacy/in-flight keepalives are ephemeral and state-specific. A planning nudge
                // queued before plan approval must never be replayed into an Active Steward (and
                // vice versa). Current keepalives no longer queue, but this also drains old state.
                missed.RemoveAll(t => t.StartsWith("Periodic keepalive:", StringComparison.Ordinal)
                    && ((refreshed.Status == StewardStatus.Active && t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))
                        || (refreshed.Status == StewardStatus.Planning && !t.Contains("PLANNING", StringComparison.OrdinalIgnoreCase))));
                missed = missed.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                if (missed.Count == 0) return false;

                // Preserve any legacy in-memory-only remainder before consuming the first one.
                // The durable cap is 512 and EnqueueTrigger returns a visible store result; unlike
                // the old 12-item in-memory cap this cannot silently forget a command.
                foreach (string trigger in missed.Where(x => !durablePayloads.Contains(x)))
                    try { parent.RuntimeState.EnqueueTrigger(stewardID, TriggerFor(trigger)); } catch { }
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
        /// <summary>
        /// Delivers watchdog advice to a Commander that is already working, without cancelling it.
        /// Cancelling a live wake discards everything it had in flight — a half-filled signup form,
        /// an unread worker report — and the next wake pays to re-derive all of it. Advisory
        /// diagnoses (understaffing, pacing) are exactly the kind that must never do that.
        /// Returns false when no wake is live, in which case the caller should force-wake instead.
        /// </summary>
        public bool NudgeActiveWake(Steward steward, string advice)
        {
            lock (WakeGate(steward.StewardID))
            {
                var activeWakeID = parent.RuntimeState.Get(steward.StewardID).ActiveWakeLease?.WakeID
                    ?? parent.Digests.GetDigest(steward.StewardID).ActiveWakeID;
                if (string.IsNullOrWhiteSpace(activeWakeID)) return false;
                var queue = agentSteerQueue.GetOrAdd(steward.StewardID, _ => new ConcurrentQueue<string>());
                if (queue.Count >= MaxPendingTriggers) return true; // already saturated with advice
                queue.Enqueue($"[watchdog] {advice}");
                return true;
            }
        }

        public string ForceWake(Steward steward, string reason)
        {
            string trigger = $"[watchdog recovery] {reason}";
            lock (WakeGate(steward.StewardID))
            {
                var digest = parent.Digests.GetDigest(steward.StewardID);
                if (!string.IsNullOrWhiteSpace(digest.ActiveWakeID))
                {
                    // Recovery queues behind the current single-flight lease. Cancellation is a
                    // request, not permission to overlap: the old wake's finally block starts this
                    // trigger only after its complete postamble has relinquished the marker.
                    var q = pendingTriggers.GetOrAdd(steward.StewardID, _ => new ConcurrentQueue<string>());
                    if (!q.Contains(trigger) && q.Count < MaxPendingTriggers) q.Enqueue(trigger);
                    parent.RuntimeState.EnqueueTrigger(steward.StewardID, TriggerFor(trigger));
                    var lease = parent.RuntimeState.Get(steward.StewardID).ActiveWakeLease;
                    if (lease != null)
                        parent.RuntimeState.RequestWakeCancellation(steward.StewardID, lease.WakeID, lease.Generation, reason);
                    if (activeWakeCts.TryGetValue(steward.StewardID, out var oldCts))
                    {
                        try { oldCts.Cancel(); } catch { }
                    }
                    else cancelBeforeStart[steward.StewardID] = true;
                    return digest.ActiveWakeID!;
                }
                return WakeLocked(steward, trigger, queueIfBusy: false) ?? "";
            }
        }

        /// <summary>Startup crash recovery: clear any wake left active by a restart (rehydrate-on-wake makes this safe).</summary>
        public void RecoverInterruptedWakes() => RecoverInterruptedWakes(
            (steward, trigger) => Wake(steward, trigger, queueIfBusy: false));

        internal void RecoverInterruptedWakes(Action<Steward, string> resumeWake)
        {
            var recovered = new HashSet<string>(StringComparer.Ordinal);
            var resumptions = new List<(Steward Steward, string Trigger)>();
            foreach (var runtime in parent.RuntimeState.ListWithActiveWakeLeases())
            {
                var lease = runtime.ActiveWakeLease;
                if (lease == null) continue; // worker-only leases are recovered by StewardSubAgentRunner
                try
                {
                    string outcome = StewardWakeRecovery.RecordInterruption(
                        parent.EventLog, runtime.StewardID, lease.WakeID, "commander");
                    ReleaseClaimedTriggers(runtime.StewardID, lease.WakeID, lease.Generation,
                        succeeded: outcome == StewardEventTypes.WakeCompleted);
                    parent.RuntimeState.ReleaseWakeLease(runtime.StewardID, lease.WakeID, lease.Generation);
                    var legacyDigest = parent.Digests.GetDigest(runtime.StewardID);
                    if (legacyDigest.ActiveWakeID == lease.WakeID)
                    {
                        legacyDigest.ActiveWakeID = null;
                        parent.Digests.SaveDigest(legacyDigest);
                    }
                    var steward = parent.Store.GetSteward(runtime.StewardID);
                    if (steward?.Status is (StewardStatus.Active or StewardStatus.Planning)
                        && (outcome != StewardEventTypes.WakeCompleted || runtime.Checkpoint.ResumeAction?.Kind == "work-slice")
                        && !StewardLoopRecovery.DefersAutomaticWake(runtime.Checkpoint.ResumeAction, DateTime.UtcNow))
                    {
                        string resume = runtime.Checkpoint.ResumeAction?.Summary
                            ?? "Rehydrate committed state and continue from the last verified action without re-discovery.";
                        resumptions.Add((steward, $"Recovery after process restart. Exact resume action: {resume}"));
                    }
                    recovered.Add(runtime.StewardID);
                }
                catch { }
            }
            foreach (var digest in parent.Digests.AllDigestsWithActiveWakes())
            {
                try
                {
                    if (recovered.Contains(digest.StewardID)) continue;
                    string outcome = StewardWakeRecovery.RecordInterruption(
                        parent.EventLog, digest.StewardID, digest.ActiveWakeID!, "commander");
                    ReleaseClaimedTriggers(digest.StewardID, digest.ActiveWakeID, null,
                        succeeded: outcome == StewardEventTypes.WakeCompleted);
                    digest.ActiveWakeID = null;
                    parent.Digests.SaveDigest(digest);
                }
                catch { }
            }
            // Finish every legacy cleanup before starting replacements. Previously the second
            // pass erased a newly started wake's digest marker, breaking steering and recovery.
            foreach (var (steward, trigger) in resumptions)
                resumeWake(steward, trigger);
        }

        /// <summary>
        /// Starts one applicable persisted inbox item for each idle Steward. This is deliberately
        /// separate from interrupted-wake recovery: a process can restart while no wake is active
        /// yet still have durable human/stimulus work waiting in the inbox.
        /// </summary>
        public void RecoverPendingTriggers()
        {
            foreach (var steward in parent.Store.ListStewards())
            {
                if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning)) continue;
                try
                {
                    lock (WakeGate(steward.StewardID))
                    {
                        if (!string.IsNullOrWhiteSpace(parent.Digests.GetDigest(steward.StewardID).ActiveWakeID)) continue;
                        var runtime = parent.RuntimeState.Get(steward.StewardID);
                        var next = parent.RuntimeState.ListPendingTriggers(steward.StewardID, includeClaimed: false)
                            .FirstOrDefault(t => StewardRuntimeStateStore.EvaluateApplicability(t, runtime, DateTime.UtcNow)
                                == StewardWakeTriggerApplicability.Applicable);
                        if (next != null) WakeLocked(steward, next.Payload, queueIfBusy: false);
                    }
                }
                catch { /* startup recovery is best effort per Steward */ }
            }
        }

        /// <summary>
        /// A process may die after claiming a durable inbox item but before the wake's finally
        /// block acknowledges it. Release that claim during recovery so the payload is eligible
        /// for the next fenced wake instead of being stranded forever as "in progress".
        /// </summary>
        private void ReleaseClaimedTriggers(string stewardID, string? wakeID, long? fallbackGeneration, bool succeeded = false)
        {
            if (string.IsNullOrWhiteSpace(wakeID)) return;
            foreach (var trigger in parent.RuntimeState.ListPendingTriggers(stewardID, includeClaimed: true)
                .Where(x => string.Equals(x.ClaimedByWakeID, wakeID, StringComparison.Ordinal)))
            {
                long? generation = trigger.ClaimedByLeaseGeneration ?? fallbackGeneration;
                if (!generation.HasValue) continue;
                try
                {
                    parent.RuntimeState.AcknowledgeTrigger(stewardID, trigger.TriggerID, wakeID,
                        generation.Value, succeeded: succeeded);
                }
                catch { /* recovery proceeds even if one old trigger is malformed */ }
            }
        }

        private static StewardEvent WakeEvt(string stewardID, string wakeID, string type, string author, string text) => new()
        {
            StewardID = stewardID, WakeID = wakeID, AgentID = "commander", Type = type, Author = author, Text = text,
        };

        private static string DescribeCall(string toolName, string argsJson)
        {
            if (toolName.StartsWith("computer_", StringComparison.Ordinal)) return ComputerAudit.Describe(toolName, argsJson);
            try
            {
                string safeArgs = StewardCommanderTools.AuditPayload(toolName, argsJson) ?? "{}";
                var jo = Newtonsoft.Json.Linq.JObject.Parse(safeArgs);
                var bits = jo.Properties().Take(4).Select(p => $"{p.Name}={Trunc(p.Value.ToString(), 60)}");
                return $"{toolName}({string.Join(", ", bits)})";
            }
            catch { return toolName; }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

        private static long AddedContextTokens(string? text) =>
            DemiurgeContextBudget.EstimateTokens(text) + 16L; // role/tool envelope overhead

        private static StewardWakeTrigger TriggerFor(string text)
        {
            var kind = text.StartsWith("Periodic keepalive:", StringComparison.Ordinal) ? StewardWakeTriggerKind.Keepalive
                : text.Contains("Message from Klives", StringComparison.Ordinal) ? StewardWakeTriggerKind.HumanMessage
                : text.StartsWith("Continuation:", StringComparison.Ordinal)
                    || text.StartsWith("Automatic context rollover:", StringComparison.Ordinal)
                    || text.StartsWith("Continue the active assignment", StringComparison.Ordinal)
                    || text.StartsWith(StewardMomentum.TriggerPrefix, StringComparison.Ordinal)
                    ? StewardWakeTriggerKind.Continuation
                : text.Contains("watchdog", StringComparison.OrdinalIgnoreCase) ? StewardWakeTriggerKind.Recovery
                : text.Contains("sub-agent", StringComparison.OrdinalIgnoreCase) ? StewardWakeTriggerKind.AgentMessage
                : StewardWakeTriggerKind.Other;
            return new StewardWakeTrigger
            {
                Kind = kind,
                Payload = text,
                Priority = kind switch
                {
                    StewardWakeTriggerKind.HumanMessage => 100,
                    StewardWakeTriggerKind.Recovery => 90,
                    StewardWakeTriggerKind.AgentMessage => 60,
                    StewardWakeTriggerKind.Continuation => 30,
                    StewardWakeTriggerKind.Keepalive => -10,
                    _ => 0,
                },
                Durable = kind != StewardWakeTriggerKind.Keepalive,
                DiscardWhenInapplicable = kind == StewardWakeTriggerKind.Keepalive,
                CoalescingKey = kind == StewardWakeTriggerKind.Keepalive ? "keepalive" : null,
                AllowedDispositions = new List<StewardExecutionDisposition>
                {
                    StewardExecutionDisposition.Running,
                    StewardExecutionDisposition.Waiting,
                },
            };
        }

    }
}
