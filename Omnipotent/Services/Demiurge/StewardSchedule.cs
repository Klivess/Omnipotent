namespace Omnipotent.Services.Demiurge
{
    /// <summary>One agent's place in the schedule: what it is doing, or why it is not, and when it next runs.</summary>
    public sealed class StewardScheduleEntry
    {
        public string AgentID { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>working · waiting (for LLM capacity) · held (provider backoff/circuit) · sleeping · done</summary>
        public string State { get; set; } = "sleeping";
        public string Detail { get; set; } = "";
        public DateTime? NextWakeAt { get; set; }
    }

    public sealed class StewardScheduleRouter
    {
        public int InFlight { get; set; }
        public int Slots { get; set; }
        public int Waiting { get; set; }
        public int ReservedSlots { get; set; }
        public string Mode { get; set; } = "";
    }

    public sealed class StewardScheduleSnapshot
    {
        public DateTime Now { get; set; }
        public List<StewardScheduleEntry> Agents { get; set; } = new();
        public StewardScheduleRouter Router { get; set; } = new();
        public int LiveConversations { get; set; }
        public int ConversationBudget { get; set; }
        public string BudgetSource { get; set; } = "";
        public int DeferredWakes { get; set; }
    }

    /// <summary>
    /// Answers "why is nothing happening?" for one Steward, from the same state the wake paths read:
    /// the live wakes, the wake-boundary admission queue, provider backoff, the durable trigger inbox,
    /// the keepalive floor and each worker's heartbeat interval. The timeline shows what happened; this
    /// shows what is about to happen, so a quiet Steward reads as queued, held or resting — never as
    /// silently stalled.
    /// </summary>
    public static class StewardSchedule
    {
        /// <summary>The Commander keepalive floor (<c>Demiurge.KeepaliveSteward</c>).</summary>
        public static readonly TimeSpan CommanderKeepalive = TimeSpan.FromMinutes(14);

        public static StewardScheduleSnapshot Describe(Demiurge parent, string stewardID, DateTime now)
        {
            var snapshot = new StewardScheduleSnapshot { Now = now };
            var steward = parent.Store.GetSteward(stewardID);
            if (steward == null) return snapshot;
            var runtime = parent.RuntimeState.Get(stewardID);
            var settings = parent.Settings.Get(stewardID);
            var tail = parent.EventLog.ReadTail(stewardID, 200);
            bool running = steward.Status is StewardStatus.Active or StewardStatus.Planning;

            try
            {
                var admission = parent.WakeAdmission.Describe();
                snapshot.LiveConversations = admission.Active;
                snapshot.ConversationBudget = admission.Budget;
                snapshot.BudgetSource = admission.Source;
                snapshot.DeferredWakes = admission.Deferred;
            }
            catch { }
            try
            {
                var router = KliveLLM.KliveLLM.AIRouterSnapshot();
                snapshot.Router = new StewardScheduleRouter
                {
                    InFlight = router.InFlight,
                    Slots = KliveLLM.AIRouterFairUseLimiter.PolicyMaxParallelRequests,
                    Waiting = router.Waiting,
                    ReservedSlots = router.ReservedSlots,
                    Mode = router.Mode,
                };
            }
            catch { }

            // ── Commander ──
            var commander = new StewardScheduleEntry { AgentID = "commander", Label = "Commander" };
            var lastCommanderWake = tail.LastOrDefault(e => e.Type == StewardEventTypes.CommanderWake);
            if (!running)
            {
                commander.State = "done";
                commander.Detail = $"Steward is {steward.Status}.";
            }
            else if (parent.CommanderRunner.HasLiveWake(stewardID) || runtime.ActiveWakeLease != null)
            {
                commander.State = "working";
                commander.Detail = DescribeActivity(parent, stewardID, "commander") ?? "In a wake.";
            }
            else if (StewardWakeRecovery.BlockedUntil(runtime, "commander", now) is { } blocked)
            {
                commander.State = "held";
                commander.Detail = $"Held while the model provider backs off; retries {StewardWakeWaiting.Until(blocked, now)}.";
                commander.NextWakeAt = blocked;
            }
            else if (runtime.Health.Circuit.Status == StewardCircuitStatus.Open)
            {
                commander.State = "held";
                commander.Detail = "Paused after repeated failures" + (runtime.Health.Circuit.RetryAt is { } at
                    ? $"; retries {StewardWakeWaiting.Until(at, now)}." : " — resume the Steward to retry now.");
                commander.NextWakeAt = runtime.Health.Circuit.RetryAt;
            }
            else if (parent.WakeAdmission.IsDeferred(stewardID, StewardWakeAdmission.CommanderAgentID))
            {
                commander.State = "waiting";
                commander.Detail = LatestWaitingNote(tail, "commander")
                    ?? "Waiting for LLM capacity; starts by itself the moment a slot frees.";
            }
            else
            {
                commander.State = "sleeping";
                var pending = parent.RuntimeState.ListPendingTriggers(stewardID, includeClaimed: false)
                    .Where(t => StewardRuntimeStateStore.EvaluateApplicability(t, runtime, now) != StewardWakeTriggerApplicability.Stale)
                    .ToList();
                DateTime? triggerAt = pending.Count == 0 ? null
                    : pending.Min(t => t.NotBefore.HasValue && t.NotBefore.Value > now ? t.NotBefore.Value : now);
                DateTime keepaliveAt = (lastCommanderWake?.Timestamp ?? now) + CommanderKeepalive;
                if (keepaliveAt < now) keepaliveAt = now;
                commander.NextWakeAt = triggerAt.HasValue && triggerAt.Value < keepaliveAt ? triggerAt : keepaliveAt;
                commander.Detail = triggerAt.HasValue && triggerAt.Value <= keepaliveAt
                    ? pending.Any(t => t.Payload.StartsWith(StewardMomentum.TriggerPrefix, StringComparison.Ordinal))
                        ? "Between wakes; continues the open work shortly."
                        : "Asleep; queued work starts it shortly."
                    : "Asleep until its next check-in, or sooner if a worker, a hook or you wake it.";
            }
            snapshot.Agents.Add(commander);

            // ── Workers ──
            foreach (var agent in parent.SubAgents.ListActive(stewardID))
            {
                if (StewardSubAgentManager.IsCommander(agent) || agent.Retired) continue;
                var entry = new StewardScheduleEntry
                {
                    AgentID = agent.AgentID,
                    Label = string.IsNullOrWhiteSpace(agent.Role) ? agent.AgentID : agent.Role,
                };
                if (!running)
                {
                    entry.State = "done";
                    entry.Detail = $"Steward is {steward.Status}.";
                }
                else if (parent.SubAgentRunner.IsAwake(stewardID, agent.AgentID))
                {
                    entry.State = "working";
                    entry.Detail = DescribeActivity(parent, stewardID, agent.AgentID) ?? "In a wake.";
                }
                else if (StewardWakeRecovery.BlockedUntil(runtime, agent.AgentID, now) is { } blocked)
                {
                    entry.State = "held";
                    entry.Detail = $"Held while the model provider backs off; retries {StewardWakeWaiting.Until(blocked, now)}.";
                    entry.NextWakeAt = blocked;
                }
                else if (parent.WakeAdmission.IsDeferred(stewardID, agent.AgentID))
                {
                    entry.State = "waiting";
                    entry.Detail = LatestWaitingNote(tail, agent.AgentID)
                        ?? "Waiting for LLM capacity; starts by itself the moment a slot frees.";
                }
                else if (agent.MissionKind == StewardAgentMissionKind.Task && agent.WorkStatus == StewardAgentWorkStatus.Completed)
                {
                    entry.State = "done";
                    entry.Detail = "Delivered its task; the Commander can re-task or retire it.";
                }
                else if (agent.WorkStatus == StewardAgentWorkStatus.Idle && agent.ActiveMilestoneIDs.Count == 0
                         && string.IsNullOrWhiteSpace(agent.Objective))
                {
                    entry.State = "done";
                    entry.Detail = "No assignment yet.";
                }
                else
                {
                    entry.State = "sleeping";
                    if (settings.WorkerHeartbeatEnabled)
                    {
                        var interval = StewardWorkerHeartbeat.Interval(settings.WorkerHeartbeatMinutes,
                            settings.WorkerHeartbeatMaxMinutes, parent.SubAgentRunner.UnproductiveStreak(stewardID, agent.AgentID));
                        var next = (agent.LastWakeAt ?? agent.CreatedAt) + interval;
                        entry.NextWakeAt = next < now ? now : next;
                        entry.Detail = $"Between wakes; checks back in {StewardWakeWaiting.Until(entry.NextWakeAt.Value, now).Replace("in ", "")} "
                            + "unless the Commander, a hook or you wake it sooner.";
                    }
                    else entry.Detail = "Asleep until the Commander, a hook or you wake it.";
                }
                snapshot.Agents.Add(entry);
            }
            return snapshot;
        }

        private static string? DescribeActivity(Demiurge parent, string stewardID, string agentID)
        {
            try
            {
                var activity = parent.Activity.ListForSteward(stewardID).FirstOrDefault(a => a.AgentID == agentID);
                if (activity == null) return null;
                return activity.Phase switch
                {
                    StewardActivityPhases.Tool => $"Running {activity.ToolName}.",
                    StewardActivityPhases.Writing => "Writing.",
                    _ => "Thinking (or waiting for an LLM slot).",
                };
            }
            catch { return null; }
        }

        private static string? LatestWaitingNote(IReadOnlyList<StewardEvent> tail, string agentID)
        {
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                var e = tail[i];
                if (!string.Equals(e.AgentID, agentID, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Type == StewardEventTypes.WakeWaiting) return e.Text;
                if (e.Type is StewardEventTypes.CommanderWake or StewardEventTypes.AgentWake) return null;
            }
            return null;
        }
    }
}
