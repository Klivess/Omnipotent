namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Decides when a worker whose assignment is still open should be woken again.
    ///
    /// Workers are otherwise woken by push only — a Commander message, a Klives directive, their own
    /// timer hook, or a work-slice rollover. Nothing periodic reaches them: the service keepalive
    /// iterates projects and wakes only the Commander. So a worker that reported and ended its wake
    /// was never heard from again unless the Commander happened to message it, and a task force
    /// decayed to a single busy Commander within an hour of being staffed.
    ///
    /// The counterweight is cost: an agent with genuinely nothing to do must not be woken every
    /// interval forever. Each consecutive wake that produces no productive action doubles the quiet
    /// period, up to a ceiling; one productive wake resets it.
    /// </summary>
    public static class ProjectWorkerHeartbeat
    {
        /// <summary>
        /// The quiet period this agent must exceed before a heartbeat wakes it, given how many
        /// consecutive wakes it has ended without doing anything productive.
        /// </summary>
        public static TimeSpan Interval(int baseMinutes, int maxMinutes, int unproductiveStreak)
        {
            baseMinutes = Math.Max(1, baseMinutes);
            maxMinutes = Math.Max(baseMinutes, maxMinutes);
            // Doubling is capped before the shift so a long-idle agent cannot overflow the exponent.
            int doublings = Math.Clamp(unproductiveStreak, 0, 16);
            double minutes = baseMinutes * Math.Pow(2, doublings);
            return TimeSpan.FromMinutes(Math.Min(minutes, maxMinutes));
        }

        /// <summary>
        /// Whether the heartbeat should wake this agent now. The caller has already established that
        /// the project is Active, its circuit is closed and its budget is intact.
        /// </summary>
        /// <param name="isAwake">True when a wake for this agent is already in flight.</param>
        public static bool ShouldWake(
            ProjectAgentRecord agent,
            bool isAwake,
            DateTime nowUtc,
            int baseMinutes,
            int maxMinutes,
            int unproductiveStreak)
        {
            if (agent.Retired) return false;
            if (ProjectSubAgentManager.IsCommander(agent)) return false;  // the Commander has its own keepalive
            if (isAwake) return false;

            // A bounded worker that has delivered is genuinely finished — waking it would just make it
            // re-report. Its slot is the Commander's to reclaim, and the task-force block says so.
            if (agent.MissionKind == ProjectAgentMissionKind.Task
                && agent.WorkStatus == ProjectAgentWorkStatus.Completed) return false;

            // Nothing has ever been assigned: an empty-handed wake would have nothing to work on.
            if (agent.WorkStatus == ProjectAgentWorkStatus.Idle
                && agent.ActiveMilestoneIDs.Count == 0
                && string.IsNullOrWhiteSpace(agent.Objective)) return false;

            var last = agent.LastWakeAt ?? agent.CreatedAt;
            return nowUtc - last >= Interval(baseMinutes, maxMinutes, unproductiveStreak);
        }

        /// <summary>The trigger text for a heartbeat wake. Names the mission and makes the cheap exit
        /// explicit, so an agent with nothing due parks itself on a timer instead of inventing work.</summary>
        public static string TriggerFor(ProjectAgentRecord agent) =>
            $"Heartbeat: you still own this assignment and nothing has woken you since {(agent.LastWakeAt.HasValue ? Data_Handling.TemporalFormat.StampWithAge(agent.LastWakeAt.Value) : "you were spawned")}. "
            + $"Your mission: {(string.IsNullOrWhiteSpace(agent.Objective) ? "(none recorded — ask the commander)" : agent.Objective)}. "
            + "Check what has changed, make the next concrete piece of progress, and report it to commander. "
            + "If the next step genuinely cannot happen until a later time or an external event, say so, arm a timer or webhook with stimulus_hook so it wakes you when it can, "
            + "and end with WORK_STATUS: CONTINUING — waiting on <the specific thing>. Do not invent filler work to look busy.";
    }
}
