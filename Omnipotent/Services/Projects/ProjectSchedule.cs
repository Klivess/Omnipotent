namespace Omnipotent.Services.Projects
{
    /// <summary>One agent's place in the schedule: what it is doing, or why it is not, and when it next runs.</summary>
    public sealed class ProjectScheduleEntry
    {
        public string AgentID { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>working · waiting (for LLM capacity) · held (provider backoff/circuit) · sleeping · done</summary>
        public string State { get; set; } = "sleeping";
        public string Detail { get; set; } = "";
        public DateTime? NextWakeAt { get; set; }
    }

    public sealed class ProjectScheduleRouter
    {
        public int InFlight { get; set; }
        public int Slots { get; set; }
        public int Waiting { get; set; }
        public int ReservedSlots { get; set; }
        public string Mode { get; set; } = "";
    }

    public sealed class ProjectScheduleSnapshot
    {
        public DateTime Now { get; set; }
        public List<ProjectScheduleEntry> Agents { get; set; } = new();
        public ProjectScheduleRouter Router { get; set; } = new();
        public int LiveConversations { get; set; }
        public int ConversationBudget { get; set; }
        public string BudgetSource { get; set; } = "";
        public int DeferredWakes { get; set; }
    }

    /// <summary>
    /// Answers "why is nothing happening?" for one project, from the same state the wake paths read:
    /// the live wakes, the wake-boundary admission queue, provider backoff, the durable trigger inbox,
    /// the keepalive floor and each worker's heartbeat interval. The timeline shows what happened; this
    /// shows what is about to happen, so a quiet project reads as queued, held or resting — never as
    /// silently stalled.
    /// </summary>
    public static class ProjectSchedule
    {
        /// <summary>The Commander keepalive floor (<c>Projects.KeepaliveProject</c>).</summary>
        public static readonly TimeSpan CommanderKeepalive = TimeSpan.FromMinutes(14);

        public static ProjectScheduleSnapshot Describe(Projects parent, string projectID, DateTime now)
        {
            var snapshot = new ProjectScheduleSnapshot { Now = now };
            var project = parent.Store.GetProject(projectID);
            if (project == null) return snapshot;
            var runtime = parent.RuntimeState.Get(projectID);
            var settings = parent.Settings.Get(projectID);
            var tail = parent.EventLog.ReadTail(projectID, 200);
            bool running = project.Status is ProjectStatus.Active or ProjectStatus.Planning;

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
                snapshot.Router = new ProjectScheduleRouter
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
            var commander = new ProjectScheduleEntry { AgentID = "commander", Label = "Commander" };
            var lastCommanderWake = tail.LastOrDefault(e => e.Type == ProjectEventTypes.CommanderWake);
            if (!running)
            {
                commander.State = "done";
                commander.Detail = $"Project is {project.Status}.";
            }
            else if (parent.CommanderRunner.HasLiveWake(projectID) || runtime.ActiveWakeLease != null)
            {
                commander.State = "working";
                commander.Detail = DescribeActivity(parent, projectID, "commander") ?? "In a wake.";
            }
            else if (ProjectWakeRecovery.BlockedUntil(runtime, "commander", now) is { } blocked)
            {
                commander.State = "held";
                commander.Detail = $"Held while the model provider backs off; retries {ProjectWakeWaiting.Until(blocked, now)}.";
                commander.NextWakeAt = blocked;
            }
            else if (runtime.Health.Circuit.Status == ProjectCircuitStatus.Open)
            {
                commander.State = "held";
                commander.Detail = "Paused after repeated failures" + (runtime.Health.Circuit.RetryAt is { } at
                    ? $"; retries {ProjectWakeWaiting.Until(at, now)}." : " — resume the project to retry now.");
                commander.NextWakeAt = runtime.Health.Circuit.RetryAt;
            }
            else if (parent.WakeAdmission.IsDeferred(projectID, ProjectWakeAdmission.CommanderAgentID))
            {
                commander.State = "waiting";
                commander.Detail = LatestWaitingNote(tail, "commander")
                    ?? "Waiting for LLM capacity; starts by itself the moment a slot frees.";
            }
            else
            {
                commander.State = "sleeping";
                var pending = parent.RuntimeState.ListPendingTriggers(projectID, includeClaimed: false)
                    .Where(t => ProjectRuntimeStateStore.EvaluateApplicability(t, runtime, now) != ProjectWakeTriggerApplicability.Stale)
                    .ToList();
                DateTime? triggerAt = pending.Count == 0 ? null
                    : pending.Min(t => t.NotBefore.HasValue && t.NotBefore.Value > now ? t.NotBefore.Value : now);
                DateTime keepaliveAt = (lastCommanderWake?.Timestamp ?? now) + CommanderKeepalive;
                if (keepaliveAt < now) keepaliveAt = now;
                commander.NextWakeAt = triggerAt.HasValue && triggerAt.Value < keepaliveAt ? triggerAt : keepaliveAt;
                commander.Detail = triggerAt.HasValue && triggerAt.Value <= keepaliveAt
                    ? pending.Any(t => t.Payload.StartsWith(ProjectMomentum.TriggerPrefix, StringComparison.Ordinal))
                        ? "Between wakes; continues the open work shortly."
                        : "Asleep; queued work starts it shortly."
                    : "Asleep until its next check-in, or sooner if a worker, a hook or you wake it.";
            }
            snapshot.Agents.Add(commander);

            // ── Workers ──
            foreach (var agent in parent.SubAgents.ListActive(projectID))
            {
                if (ProjectSubAgentManager.IsCommander(agent) || agent.Retired) continue;
                var entry = new ProjectScheduleEntry
                {
                    AgentID = agent.AgentID,
                    Label = string.IsNullOrWhiteSpace(agent.Role) ? agent.AgentID : agent.Role,
                };
                if (!running)
                {
                    entry.State = "done";
                    entry.Detail = $"Project is {project.Status}.";
                }
                else if (parent.SubAgentRunner.IsAwake(projectID, agent.AgentID))
                {
                    entry.State = "working";
                    entry.Detail = DescribeActivity(parent, projectID, agent.AgentID) ?? "In a wake.";
                }
                else if (ProjectWakeRecovery.BlockedUntil(runtime, agent.AgentID, now) is { } blocked)
                {
                    entry.State = "held";
                    entry.Detail = $"Held while the model provider backs off; retries {ProjectWakeWaiting.Until(blocked, now)}.";
                    entry.NextWakeAt = blocked;
                }
                else if (parent.WakeAdmission.IsDeferred(projectID, agent.AgentID))
                {
                    entry.State = "waiting";
                    entry.Detail = LatestWaitingNote(tail, agent.AgentID)
                        ?? "Waiting for LLM capacity; starts by itself the moment a slot frees.";
                }
                else if (agent.MissionKind == ProjectAgentMissionKind.Task && agent.WorkStatus == ProjectAgentWorkStatus.Completed)
                {
                    entry.State = "done";
                    entry.Detail = "Delivered its task; the Commander can re-task or retire it.";
                }
                else if (agent.WorkStatus == ProjectAgentWorkStatus.Idle && agent.ActiveMilestoneIDs.Count == 0
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
                        var interval = ProjectWorkerHeartbeat.Interval(settings.WorkerHeartbeatMinutes,
                            settings.WorkerHeartbeatMaxMinutes, parent.SubAgentRunner.UnproductiveStreak(projectID, agent.AgentID));
                        var next = (agent.LastWakeAt ?? agent.CreatedAt) + interval;
                        entry.NextWakeAt = next < now ? now : next;
                        entry.Detail = $"Between wakes; checks back in {ProjectWakeWaiting.Until(entry.NextWakeAt.Value, now).Replace("in ", "")} "
                            + "unless the Commander, a hook or you wake it sooner.";
                    }
                    else entry.Detail = "Asleep until the Commander, a hook or you wake it.";
                }
                snapshot.Agents.Add(entry);
            }
            return snapshot;
        }

        private static string? DescribeActivity(Projects parent, string projectID, string agentID)
        {
            try
            {
                var activity = parent.Activity.ListForProject(projectID).FirstOrDefault(a => a.AgentID == agentID);
                if (activity == null) return null;
                return activity.Phase switch
                {
                    ProjectActivityPhases.Tool => $"Running {activity.ToolName}.",
                    ProjectActivityPhases.Writing => "Writing.",
                    _ => "Thinking (or waiting for an LLM slot).",
                };
            }
            catch { return null; }
        }

        private static string? LatestWaitingNote(IReadOnlyList<ProjectEvent> tail, string agentID)
        {
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                var e = tail[i];
                if (!string.Equals(e.AgentID, agentID, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Type == ProjectEventTypes.WakeWaiting) return e.Text;
                if (e.Type is ProjectEventTypes.CommanderWake or ProjectEventTypes.AgentWake) return null;
            }
            return null;
        }
    }
}
