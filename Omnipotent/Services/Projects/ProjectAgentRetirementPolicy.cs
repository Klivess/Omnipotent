namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Which agents lose their slots when the agent cap drops below the roster, and in what order.
    ///
    /// Klives can lower the cap at any moment, including to 1 while four workers are mid-assignment.
    /// "Instantly" is the requirement, and that rules out asking the Commander to choose: a Commander
    /// wake is an LLM round trip measured in minutes, so deferring the choice would leave the roster
    /// over its cap for exactly as long as the model takes to answer — and would fail outright on a
    /// budget-paused or provider-blocked project, which is precisely when Klives is most likely to be
    /// trimming the fleet. So the harness picks, synchronously, using the ranking the Commander is
    /// told to use for its own retirements, and the Commander is notified in the same breath with the
    /// full handover so it can re-staff the work. The choice is the Commander's policy; the execution
    /// does not wait for the Commander's turn.
    ///
    /// Pure: roster in, ordered IDs out. No store access, no clock beyond the one passed in.
    /// </summary>
    public static class ProjectAgentRetirementPolicy
    {
        /// <summary>
        /// Disruption cost of retiring one agent, lowest first. The ordering encodes one idea: take
        /// slots from agents whose work is finished or never started before taking them from agents
        /// holding live work, and take a bounded deliverable before an ongoing beat — a task agent's
        /// work has a definition of done somebody else can finish, while a standing mission's beat
        /// simply stops the moment nobody owns it.
        /// </summary>
        public static int DisruptionRank(ProjectAgentRecord agent, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(agent);
            if (ProjectSubAgentManager.IsReclaimable(agent, nowUtc)) return 0;   // finished and quiet
            if (agent.MissionKind == ProjectAgentMissionKind.Task
                && agent.WorkStatus == ProjectAgentWorkStatus.Completed) return 1; // finished, report still fresh
            if (ProjectSubAgentManager.IsIdle(agent)) return 2;                  // holding a slot, doing nothing
            if (agent.LastWakeAt == null) return 3;                              // spawned, never started
            if (nowUtc - LastActivity(agent) >= ProjectSubAgentManager.SilenceThreshold) return 4; // probably stalled
            if (agent.MissionKind == ProjectAgentMissionKind.Task) return 5;     // live bounded work
            return 6;                                                            // live standing beat
        }

        /// <summary>
        /// The agents to retire so the roster fits <paramref name="cap"/>, in the order they should go.
        ///
        /// Only ever picks a LEAF of the org tree. Retiring a parent forces its one-level-deep helpers
        /// out with it, so picking parents first would overshoot the cap — asked to free one slot it
        /// would free three. Peeling leaves frees exactly as many slots as were asked for, and a parent
        /// becomes a leaf once its helpers are gone, so nothing is unreachable.
        /// </summary>
        public static List<ProjectAgentRecord> SelectForCap(
            IReadOnlyList<ProjectAgentRecord> roster, int cap, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(roster);
            var remaining = roster.Where(a => !a.Retired).ToList();
            // The Commander holds the floor slot: a cap of 1 is a Commander-only roster, and it is
            // never a candidate here (retiring it would leave the project with nobody to hand to).
            int target = Math.Max(1, cap);
            var picked = new List<ProjectAgentRecord>();

            while (remaining.Count > target)
            {
                var candidate = LeavesOf(remaining)
                    .OrderBy(a => DisruptionRank(a, nowUtc))
                    .ThenBy(a => a.ActiveMilestoneIDs.Count)
                    // Newest first among equals: the least work has been invested in the agent that
                    // was spawned most recently, and reversing a just-made staffing decision costs
                    // the project less than unwinding one that has been running for hours.
                    .ThenByDescending(a => a.CreatedAt)
                    .ThenBy(a => a.AgentID, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (candidate == null) break; // roster is Commander-only; nothing further can be freed
                picked.Add(candidate);
                remaining.Remove(candidate);
            }
            return picked;
        }

        /// <summary>
        /// Expands an explicit removal request to include the helpers that must go with it, deepest
        /// first. Klives removing a sub-agent by hand cannot leave orphaned helpers pointing at a
        /// retired parent — they would keep a slot, keep waking, and report to nobody.
        /// </summary>
        public static List<ProjectAgentRecord> ExpandWithDescendants(
            IReadOnlyList<ProjectAgentRecord> roster, IEnumerable<string> agentIDs)
        {
            ArgumentNullException.ThrowIfNull(roster);
            ArgumentNullException.ThrowIfNull(agentIDs);
            var active = roster.Where(a => !a.Retired).ToList();
            var selected = new Dictionary<string, ProjectAgentRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (string id in agentIDs.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var root = active.FirstOrDefault(a =>
                    string.Equals(a.AgentID, id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (root == null || ProjectSubAgentManager.IsCommander(root)) continue;
                foreach (var member in WithDescendants(active, root))
                    selected[member.AgentID] = member;
            }

            // Deepest first so every agent is a leaf by the time it is retired — the retirement path
            // refuses to retire an agent that still has active children, and that guard stays intact.
            return selected.Values
                .OrderByDescending(a => DepthOf(active, a))
                .ThenBy(a => a.AgentID, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Active agents with no active children — the only ones safe to retire directly.</summary>
        private static List<ProjectAgentRecord> LeavesOf(IReadOnlyList<ProjectAgentRecord> active) =>
            active.Where(a => !ProjectSubAgentManager.IsCommander(a)
                && !active.Any(c => string.Equals(c.ParentAgentID, a.AgentID, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        private static IEnumerable<ProjectAgentRecord> WithDescendants(
            IReadOnlyList<ProjectAgentRecord> active, ProjectAgentRecord root)
        {
            yield return root;
            foreach (var child in active.Where(c =>
                string.Equals(c.ParentAgentID, root.AgentID, StringComparison.OrdinalIgnoreCase)))
            {
                // Depth is hard-capped at one level of helpers, but recurse anyway rather than assume
                // it: a stored roster from an older build is data, not a guarantee.
                foreach (var descendant in WithDescendants(active.Where(x => x.AgentID != root.AgentID).ToList(), child))
                    yield return descendant;
            }
        }

        private static int DepthOf(IReadOnlyList<ProjectAgentRecord> active, ProjectAgentRecord agent)
        {
            int depth = 0;
            var cur = agent;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cur?.ParentAgentID != null && seen.Add(cur.AgentID))
            {
                depth++;
                cur = active.FirstOrDefault(a =>
                    string.Equals(a.AgentID, cur!.ParentAgentID, StringComparison.OrdinalIgnoreCase));
            }
            return depth;
        }

        private static DateTime LastActivity(ProjectAgentRecord a) =>
            a.LastReportAt ?? a.LastWakeAt ?? a.CreatedAt;
    }
}
