namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Stall detection and SELF-HEALING (design doc §9 + the one explicit developer note: "prevent
    /// the Steward from stalling before the goal is achieved"). A background loop, architecturally
    /// independent of the Commander's own execution, computes stall signals purely from the event
    /// log + digest — so it keeps working even if the Commander's wake logic is the thing that's
    /// wedged.
    ///
    /// Recovery is automatic and silent: a diagnosed stall force-wakes the Commander immediately
    /// (never a hard kill — the standing no-hard-timeouts rule). Klives is only pinged when the
    /// watchdog's own medicine isn't working — repeated recoveries inside a rolling window, or
    /// wakes that keep failing outright — and never more than once per cooldown. An unanswered
    /// approval gate is "waiting on Klives", not a stall: it gets one aged reminder per gate and
    /// is never force-woken (that would cancel the gate wait).
    /// </summary>
    public class StewardWatchdog
    {
        private readonly Demiurge parent;
        private readonly Action<string> log;
        private CancellationTokenSource? cts;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> stewardFailureLogs
            = new(StringComparer.Ordinal);

        // Tunables (could become OmniSettings; kept as constants for V1).
        private static readonly TimeSpan MaxWakeGap = TimeSpan.FromMinutes(30);      // heartbeat staleness
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ForceWakePacing = TimeSpan.FromMinutes(15); // min gap between self-heals
        private static readonly TimeSpan HealWindow = TimeSpan.FromHours(3);         // window for "it keeps happening"
        private const int HealsBeforeEscalation = 3;                                 // heals in window before pinging Klives
        private static readonly TimeSpan EscalationCooldown = TimeSpan.FromHours(6); // max one ping per Steward per this
        private static readonly TimeSpan GateReminderAge = TimeSpan.FromHours(2);    // unanswered gate → one reminder
        private const int ZeroProgressWakes = 3;                                     // wakes with no action = stalled

        public StewardWatchdog(Demiurge parent, Action<string> log)
        {
            this.parent = parent;
            this.log = log ?? (_ => { });
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop() => cts?.Cancel();

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { log($"Watchdog tick failed: {ex.Message}"); }
                try { await Task.Delay(TickInterval, ct); } catch { break; }
            }
        }

        private async Task TickAsync()
        {
            foreach (var steward in parent.Store.ListStewards())
            {
                if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning)) continue;
                // Per Steward, for the same reason the keepalive is: an unreadable runtime state (or
                // any other per-Steward fault) threw out of the whole loop, so every Steward after it
                // lost its stall detection too. The watchdog is the last line of defence against a
                // wedged Steward — it must not be takeable down by one.
                try { await TickStewardAsync(steward); }
                catch (Exception ex) { NoteStewardFailure(steward, ex); }
            }
        }

        private async Task TickStewardAsync(Steward steward)
        {
            // A pending approval gate means the Steward is waiting on Klives, not stalled.
            // Never force-wake it (that cancels the gate wait); remind him once per aged gate.
            var pending = parent.Gates?.ListPending(steward.StewardID) ?? new List<StewardGate>();
            if (pending.Count > 0)
            {
                await RemindAgedGatesAsync(steward, pending);
                return;
            }

            var (diagnosis, wedgedAgentID) = DiagnoseCore(steward);
            if (diagnosis == null) return;

            await SelfHealAsync(steward, diagnosis, wedgedAgentID);
        }

        /// <summary>Rate-limited so a persistently broken Steward cannot flood the log every 2 minutes.</summary>
        private void NoteStewardFailure(Steward steward, Exception ex)
        {
            DateTime now = DateTime.UtcNow;
            if (stewardFailureLogs.TryGetValue(steward.StewardID, out DateTime last)
                && now - last < TimeSpan.FromMinutes(30)) return;
            stewardFailureLogs[steward.StewardID] = now;
            log($"Watchdog: skipped {steward.Name} ({steward.StewardID}) — stall detection is off for it: {ex.Message}");
        }

        /// <summary>
        /// Marks a diagnosis as advice about how the Steward is being run rather than evidence that
        /// it is wedged. Advisory diagnoses are delivered into the live wake and never cancel it.
        /// </summary>
        private const string AdvisoryPrefix = "advisory";

        private static bool IsAdvisory(string diagnosis) => diagnosis.StartsWith(AdvisoryPrefix, StringComparison.Ordinal);
        private static string StripAdvisory(string diagnosis) =>
            IsAdvisory(diagnosis) ? diagnosis[AdvisoryPrefix.Length..] : diagnosis;

        /// <summary>Returns a stall diagnosis string, or null if the Steward looks healthy.</summary>
        public string? Diagnose(Steward steward)
        {
            string? diagnosis = DiagnoseCore(steward).diagnosis;
            return diagnosis == null ? null : StripAdvisory(diagnosis);
        }

        private (string? diagnosis, string? wedgedAgentID) DiagnoseCore(Steward steward)
        {
            var digest = parent.Digests.GetDigest(steward.StewardID);
            var tail = parent.EventLog.ReadTail(steward.StewardID, 300);
            var now = DateTime.UtcNow;

            // Waiting on Klives is never a stall (also short-circuited in the tick loop; kept
            // here so direct callers of Diagnose get the same answer).
            if ((parent.Gates?.ListPending(steward.StewardID).Count ?? 0) > 0) return (null, null);

            StewardRuntimeState? runtime = null;
            try { runtime = parent.RuntimeState?.Get(steward.StewardID); } catch { }
            if (runtime != null)
            {
                if (runtime.Disposition is StewardExecutionDisposition.Paused or StewardExecutionDisposition.Pausing
                    or StewardExecutionDisposition.Blocked or StewardExecutionDisposition.Completed or StewardExecutionDisposition.Archived)
                    return (null, null);
                if (runtime.Health.Circuit.Status == StewardCircuitStatus.Open
                    && (!runtime.Health.Circuit.RetryAt.HasValue || runtime.Health.Circuit.RetryAt > now))
                    return (null, null); // typed provider/dependency circuit owns recovery timing

                // A durable resume action with a future not-before time is an intentional sleep,
                // not a wedge. The keepalive owns the scheduled wake; force-waking here burns a
                // model turn and defeats the commander's explicit timing decision.
                if (StewardLoopRecovery.DefersAutomaticWake(runtime.Checkpoint.ResumeAction, now))
                    return (null, null);
            }

            // 1. Heartbeat staleness — measured on COMMANDER ACTIVITY, not just wake starts. A
            //    single long wake emits tool calls and thoughts for hours without a new
            //    CommanderWake event, and must not read as stalled while it is visibly working.
            //    The keepalive guarantees a wake at least every ~15 min when healthy, so a 30-min
            //    activity gap really does mean the wake pipeline is wedged.
            var lastActivity = tail.Where(e => IsCommanderActivity(e)).Select(e => (DateTime?)e.Timestamp).Max();
            var leaseBeat = runtime?.ActiveWakeLease?.LastHeartbeatAt;
            DateTime? workerBeat = runtime?.ActiveAgentWakeLeases.Values.Select(x => (DateTime?)x.LastHeartbeatAt).Max();
            var verifiedBeat = runtime?.Health.LastVerifiedProgressAt;
            var approvalBeat = parent.Gates?.LastResolutionAt(steward.StewardID);
            var liveActivity = parent.Activity?.ListForSteward(steward.StewardID) ?? Array.Empty<StewardAgentActivity>();
            var streamBeat = liveActivity.Select(x => (DateTime?)x.UpdatedAt).Max();
            // A request parked in the shared AIRouter queue is a healthy agent waiting for one of
            // three slots, not a wedged one. The queue's park ceiling deliberately exceeds the
            // client-side brief idle limit — dispatching a continuation onto an evicted prefix is the
            // one thing the prefix-efficiency target forbids — so a legitimately parked turn can
            // outlast MaxWakeGap once parks stack. Without this beat the scheduler would manufacture
            // false stalls out of its own correct behaviour.
            // The same reasoning one level up: a wake held at the WAKE BOUNDARY for prompt-cache
            // capacity has not started, so it has nothing queued in AIRouter either — and it is the
            // healthiest state in the system, not a stall. Without this beat the watchdog would
            // diagnose the admission gate's correct behaviour and force-wake straight back into it.
            DateTime? queuedBeat =
                KliveLLM.KliveLLM.AIRouterHasQueuedWork($"demiurge-commander-{steward.StewardID}")
                || KliveLLM.KliveLLM.AIRouterHasQueuedWork($"demiurge-agent-{steward.StewardID}-")
                || (parent.WakeAdmission?.IsDeferred(steward.StewardID) ?? false)
                    ? now : null;
            var observedBeats = new[] { lastActivity, leaseBeat, workerBeat, verifiedBeat, approvalBeat, streamBeat, queuedBeat }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var lastBeat = observedBeats.DefaultIfEmpty(steward.CreatedAt).Max();
            if (tail.Count > 0 && now - lastBeat > MaxWakeGap)
                return ($"No Commander activity or verified worker heartbeat in over {MaxWakeGap.TotalMinutes:0} minutes " +
                    $"(last heartbeat: {(observedBeats.Count == 0 ? "never" : lastBeat.ToString("u"))}).", null);

            // 2. Zero-progress-over-N-wakes: the last N completed wakes produced no tool-call /
            //    artifact / spawn / sub-agent-activity events, and the plan hasn't changed.
            var recentWakeStarts = tail.Where(e => e.Type == StewardEventTypes.CommanderWake).TakeLast(ZeroProgressWakes).ToList();
            if (recentWakeStarts.Count >= ZeroProgressWakes)
            {
                long firstWakeSeq = recentWakeStarts[0].Sequence;
                bool typedProgress = runtime?.Health.LastVerifiedProgressSequence >= firstWakeSeq;
                bool anyProgress = typedProgress || tail.Any(e => e.Sequence >= firstWakeSeq &&
                    (e.Type is StewardEventTypes.ArtifactAdded or StewardEventTypes.StewardFileChanged
                        or StewardEventTypes.CheckpointChanged or StewardEventTypes.GrandPlanProgress
                        or StewardEventTypes.ObservableChanged or StewardEventTypes.HookChanged
                        or StewardEventTypes.AccountChanged or StewardEventTypes.AgentSpawned
                        or StewardEventTypes.MoneySpent));
                if (!anyProgress)
                    return (AdvisoryPrefix + $"No verified progress across the last {ZeroProgressWakes} wakes (no artifacts, file/checkpoint/plan changes, spawns, or spend).", null);
            }

            // 3. Repeated-action loops: the last wake tripped the stuck-loop guard heavily. This
            //    describes a wake that has already ended, so it is advice for whatever is running
            //    now — not grounds to cancel it.
            if (digest.RecentStuckLoopTrips >= 3)
                return (AdvisoryPrefix + $"Commander tripped its stuck-loop guard {digest.RecentStuckLoopTrips}× in its last wake.", null);

            // 4. Wedged sub-agent: it woke, never finished within MaxWakeGap, AND has gone silent
            //    (a live worker emits tool calls constantly — silence, not duration, is the wedge
            //    signal, so a slow-but-working desktop agent is left alone).
            var staleLease = runtime?.ActiveAgentWakeLeases
                .FirstOrDefault(x => now - x.Value.LastHeartbeatAt > MaxWakeGap
                    && !liveActivity.Any(a => a.AgentID == x.Key && now - a.UpdatedAt <= MaxWakeGap)
                    // Same reasoning as the heartbeat beat above: a worker whose turn is parked in
                    // the AIRouter queue is waiting, not wedged.
                    && !KliveLLM.KliveLLM.AIRouterHasQueuedWork($"demiurge-agent-{steward.StewardID}-{x.Key}")
                    && !(parent.WakeAdmission?.IsDeferred(steward.StewardID, x.Key) ?? false)
                    && !(parent.Gates?.LastResolutionAt(steward.StewardID, x.Key) is { } resolved
                        && now - resolved <= MaxWakeGap));
            if (staleLease.HasValue && !string.IsNullOrWhiteSpace(staleLease.Value.Key))
                return ($"Sub-agent {staleLease.Value.Key} has a fenced wake lease with no heartbeat for over {MaxWakeGap.TotalMinutes:0} minutes.", staleLease.Value.Key);

            var staleAgentWake = tail
                .Where(e => e.Type == StewardEventTypes.AgentWake && now - e.Timestamp > MaxWakeGap
                    // Fenced leases are authoritative. An old orphaned start must not keep
                    // diagnosing a worker whose replacement wake is currently healthy.
                    && !(runtime?.ActiveAgentWakeLeases.ContainsKey(e.AgentID ?? "") ?? false))
                .FirstOrDefault(w => !tail.Any(e => e.WakeID == w.WakeID &&
                        (e.Type is StewardEventTypes.WakeCompleted or StewardEventTypes.WakeFailed
                            or StewardEventTypes.WakeCancelled or StewardEventTypes.WakeDeferred))
                    && !tail.Any(e => e.AgentID == w.AgentID && now - e.Timestamp <= MaxWakeGap)
                    && !liveActivity.Any(a => a.AgentID == w.AgentID && now - a.UpdatedAt <= MaxWakeGap)
                    && !(parent.Gates?.LastResolutionAt(steward.StewardID, w.AgentID) is { } resolved
                        && now - resolved <= MaxWakeGap));
            if (staleAgentWake != null)
                return ($"Sub-agent {staleAgentWake.AgentID} has been awake without finishing or emitting any activity for over {MaxWakeGap.TotalMinutes:0} minutes.", staleAgentWake.AgentID);

            // 5. Under-staffed: free agent slots sitting against dependency-ready work nobody owns.
            //    A Commander grinding solo through parallelisable work is healthy by every check above
            //    — it emits activity, makes progress and never loops — while delivering a fraction of
            //    the throughput the roster was budgeted for. Wake it to staff the work; the seed's
            //    task-force block and staffing checkpoint tell it exactly what is unowned.
            if (steward.Status == StewardStatus.Active && steward.SubAgentCap > 1)
            {
                try
                {
                    var roster = parent.SubAgents.ListActive(steward.StewardID);
                    var ready = parent.GrandPlans?.GetReadyMilestones(steward.StewardID) ?? new List<PlanMilestone>();
                    if (StewardStaffing.IsUnderStaffed(steward, ready, roster))
                    {
                        int free = Math.Max(0, steward.SubAgentCap - roster.Count);
                        var unstaffed = StewardStaffing.UnstaffedReady(ready, roster);
                        return (AdvisoryPrefix
                            + $"{free} agent slot(s) are free while {unstaffed.Count} dependency-ready milestone(s) have no owner "
                            + $"({string.Join(", ", unstaffed.Take(3).Select(m => m.ID))}). Staff the work or explain why it is indivisible.", null);
                    }
                }
                catch { }
            }

            return (null, null);
        }

        /// <summary>Commander-side liveness: wake starts, thoughts, closing messages, and any
        /// tool activity or wake outcome attributed to the commander.</summary>
        private static bool IsCommanderActivity(StewardEvent e) =>
            e.Type is StewardEventTypes.CommanderWake or StewardEventTypes.CommanderThought or StewardEventTypes.CommanderMessage
                or StewardEventTypes.StewardFileChanged or StewardEventTypes.CheckpointChanged
                or StewardEventTypes.GrandPlanProgress or StewardEventTypes.ObservableChanged
                or StewardEventTypes.HookChanged or StewardEventTypes.AccountChanged
            || (e.AgentID == "commander" &&
                e.Type is StewardEventTypes.ToolCall or StewardEventTypes.ToolResult
                    or StewardEventTypes.WakeCompleted or StewardEventTypes.WakeFailed
                    or StewardEventTypes.WakeCancelled or StewardEventTypes.WakeDeferred);

        /// <summary>
        /// Recovers a diagnosed stall without involving Klives: cancel only a wedged worker and
        /// notify its commander in place, or force-wake a stalled commander.
        /// Klives is pinged only when recoveries keep happening or wakes keep failing outright.
        /// </summary>
        internal async Task SelfHealAsync(Steward steward, string diagnosis, string? wedgedAgentID)
        {
            var now = DateTime.UtcNow;
            var watchdog = parent.RuntimeState.Get(steward.StewardID).Health.Watchdog;

            // Provider-outage guard: if wakes themselves keep failing, another force-wake is just
            // another failure — the keepalive's exponential backoff owns retries. Tell Klives
            // (rate-limited) because this is usually a provider/credit problem only he can fix.
            var outcomes = parent.EventLog.ReadTail(steward.StewardID, 40)
                .Where(e => e.Type is StewardEventTypes.WakeCompleted or StewardEventTypes.WakeFailed
                    or StewardEventTypes.WakeCancelled or StewardEventTypes.WakeDeferred).ToList();
            int consecutiveFailures = 0;
            for (int i = outcomes.Count - 1; i >= 0 && outcomes[i].Type == StewardEventTypes.WakeFailed; i--) consecutiveFailures++;
            if (consecutiveFailures >= 3)
            {
                await EscalateAsync(steward,
                    $"Wakes are failing repeatedly ({consecutiveFailures} in a row — most recent: {Trunc(outcomes[^1].Text, 200)}). " +
                    "This usually means the LLM provider, its credit, or the model configuration is the problem rather than the Steward itself. Retries continue automatically with backoff.");
                return;
            }

            if (watchdog.LastRecoveryAt.HasValue && now - watchdog.LastRecoveryAt.Value < ForceWakePacing)
                return; // healed recently — give it time to land

            // Diagnosis and lifecycle can change between TickAsync and this method (for example,
            // a wake completes or Klives pauses the Steward). Re-read immediately before every
            // destructive recovery action so a stale watchdog observation cannot cancel useful work.
            var current = parent.Store.GetSteward(steward.StewardID);
            if (current == null || current.Status is not (StewardStatus.Active or StewardStatus.Planning)) return;
            if ((parent.Gates?.ListPending(steward.StewardID).Count ?? 0) > 0) return;
            var rechecked = DiagnoseCore(current);
            if (rechecked.diagnosis == null) return;
            diagnosis = rechecked.diagnosis;
            wedgedAgentID = rechecked.wedgedAgentID;

            bool advisory = IsAdvisory(diagnosis);
            diagnosis = StripAdvisory(diagnosis).TrimStart();

            // Advice for a Commander that is visibly working goes INTO the running wake. Force-waking
            // here used to cancel mid-action — in one run it killed a wake three seconds after a
            // signup form had been filled and was about to be submitted — and every cancellation
            // pays again to rediscover the state it threw away.
            if (advisory && wedgedAgentID == null &&
                parent.CommanderRunner.NudgeActiveWake(current, diagnosis))
            {
                parent.EventLog.Append(new StewardEvent
                {
                    StewardID = current.StewardID,
                    Type = StewardEventTypes.WatchdogReminder,
                    Author = "system",
                    AgentID = "commander",
                    Text = $"Watchdog: {diagnosis} Delivered into the running wake (no cancellation).",
                });
                return;
            }

            // Consume the stuck-loop signal so one bad wake can't re-trigger this diagnosis forever.
            var digest = parent.Digests.GetDigest(steward.StewardID);
            if (digest.RecentStuckLoopTrips > 0)
            {
                digest.RecentStuckLoopTrips = 0;
                parent.Digests.SaveDigest(digest);
            }

            // A wedged sub-agent keeps its single-flight slot until its cancellation finishes;
            // its commander can then re-dispatch the work.
            if (wedgedAgentID != null)
            {
                try { parent.SubAgentRunner.CancelAgent(steward.StewardID, wedgedAgentID, $"Watchdog recovery: {diagnosis}"); } catch { }
            }

            var recorded = parent.RuntimeState.RecordWatchdogRecovery(steward.StewardID, HealWindow, nowUtc: now);
            long lifetimeCount = recorded.State.Health.Watchdog.RecoveryCount;
            int windowCount = recorded.State.Health.Watchdog.RecoveriesUtc.Count;

            parent.EventLog.Append(new StewardEvent
            {
                StewardID = steward.StewardID,
                Type = StewardEventTypes.WatchdogRecovery,
                Author = "system",
                AgentID = "commander",
                Text = $"Watchdog: {diagnosis} Self-healing — {(wedgedAgentID == null ? "recovering the Commander" : "recovering the worker and notifying the Commander")} (recovery #{lifetimeCount} lifetime; {windowCount} in the last {HealWindow.TotalHours:0}h).",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    diagnosis,
                    wedgedAgentID,
                    lifetimeRecoveryCount = lifetimeCount,
                    rollingRecoveryCount = windowCount,
                    rollingWindowHours = HealWindow.TotalHours,
                    initiatedAt = now,
                }),
            });
            log($"Watchdog self-heal on Steward {steward.StewardID}: {diagnosis}");
            string recoveryAdvice = $"{diagnosis} Assess what wedged, avoid repeating it (change approach if you were looping), and continue toward the goal.";
            if (wedgedAgentID != null)
            {
                // Only the diagnosed worker was cancelled. Its healthy commander keeps its
                // conversation and in-flight actions, and receives the recovery report in place.
                if (!parent.CommanderRunner.NudgeActiveWake(current, recoveryAdvice))
                    parent.CommanderRunner.Wake(current, $"[watchdog recovery] {recoveryAdvice}");
            }
            else
                parent.CommanderRunner.ForceWake(current, recoveryAdvice);

            if (windowCount >= HealsBeforeEscalation)
                await EscalateAsync(current,
                    $"{diagnosis} I've auto-recovered the Commander {windowCount}× in the last {HealWindow.TotalHours:0} hours, but it keeps stalling — it may need your steering.");
        }

        /// <summary>One aged reminder per unanswered gate — waiting on Klives is his stall, not the Steward's.</summary>
        private async Task RemindAgedGatesAsync(Steward steward, List<StewardGate> pending)
        {
            foreach (var gate in pending)
            {
                if (DateTime.UtcNow - gate.CreatedAt < GateReminderAge) continue;
                var recorded = parent.RuntimeState.RecordGateReminder(steward.StewardID, gate.GateID);
                if (!recorded.Applied) continue;
                parent.EventLog.Append(new StewardEvent
                {
                    StewardID = steward.StewardID,
                    Type = StewardEventTypes.WatchdogReminder,
                    Author = "system",
                    GateID = gate.GateID,
                    Text = $"Watchdog: approval '{gate.Title}' has been waiting on Klives for over {GateReminderAge.TotalHours:0}h — reminded him.",
                });
                if (parent.DiscordManager != null)
                    await parent.DiscordManager.PostAttentionAsync(steward, "⏳ Waiting on your approval",
                        $"'{gate.Title}' has been pending for over {(DateTime.UtcNow - gate.CreatedAt).TotalHours:0} hours. The Steward is blocked until you approve or deny it.");
            }
        }

        /// <summary>Rate-limited attention ping: at most one per Steward per cooldown, always with
        /// what the watchdog already tried, so a ping means "I couldn't fix this myself".</summary>
        private async Task EscalateAsync(Steward steward, string message)
        {
            var now = DateTime.UtcNow;
            var watchdog = parent.RuntimeState.Get(steward.StewardID).Health.Watchdog;
            if (watchdog.LastEscalationAt.HasValue && now - watchdog.LastEscalationAt.Value < EscalationCooldown) return;
            parent.RuntimeState.RecordWatchdogEscalation(steward.StewardID, nowUtc: now);

            parent.EventLog.Append(new StewardEvent
            {
                StewardID = steward.StewardID,
                Type = StewardEventTypes.WatchdogEscalation,
                Author = "system",
                Text = $"Watchdog escalation to Klives: {message}",
            });
            if (parent.DiscordManager != null)
                await parent.DiscordManager.PostAttentionAsync(steward, "⚠️ Steward needs attention",
                    $"{message}\n\nReply here to steer the Commander. Automatic recovery keeps running regardless.");
            log($"Watchdog escalated Steward {steward.StewardID}: {message}");
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
