namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Decides whether a Commander wake should be told to change its staffing, and in what terms.
    ///
    /// The predecessor of this logic fired only while the roster held one agent, and since the roster
    /// counts the Commander, that meant "only before the first spawn ever". A project therefore got
    /// exactly one nudge to delegate in its entire life, which is why live rosters sat at one worker
    /// with most of the cap idle. Staffing is a standing duty, not a one-off prompt, so this is
    /// evaluated on every wake and stays silent only when the roster genuinely cannot be improved.
    ///
    /// Kept pure (roster and plan passed in, no store access) so the decision is directly testable.
    /// </summary>
    public static class ProjectStaffing
    {
        /// <summary>Milestones on the runnable frontier that nobody is assigned to.</summary>
        public static List<PlanMilestone> UnstaffedReady(
            IReadOnlyList<PlanMilestone> readyMilestones,
            IReadOnlyList<ProjectAgentRecord> roster)
        {
            var owned = new HashSet<string>(
                roster.Where(a => !a.Retired).SelectMany(a => a.ActiveMilestoneIDs),
                StringComparer.OrdinalIgnoreCase);
            return readyMilestones
                .Where(m => !owned.Contains(m.ID)
                    && (string.IsNullOrWhiteSpace(m.OwnerAgentID)
                        || !roster.Any(a => !a.Retired && a.AgentID == m.OwnerAgentID)))
                .ToList();
        }

        /// <summary>
        /// The staffing brief for one wake, or null when the roster needs no attention. Returned as a
        /// protected brief message so the compactor cannot clip it.
        /// </summary>
        public static string? ComposeCheckpoint(
            Project project,
            GrandPlanContent? approvedPlan,
            IReadOnlyList<PlanMilestone> readyMilestones,
            IReadOnlyList<ProjectAgentRecord> roster,
            DateTime nowUtc,
            int consecutiveUnderStaffedWakes = 0)
        {
            if (project.Status != ProjectStatus.Active || project.SubAgentCap <= 1) return null;

            var active = roster.Where(a => !a.Retired).ToList();
            int used = active.Count;
            int free = Math.Max(0, project.SubAgentCap - used);
            var reclaimable = active.Where(a => ProjectSubAgentManager.IsReclaimable(a, nowUtc)).ToList();
            var idle = active.Where(ProjectSubAgentManager.IsIdle).ToList();
            var unstaffed = UnstaffedReady(readyMilestones, active);

            // Separable work is the precondition for delegating at all: with a single indivisible next
            // step there is nothing to fan out, and nagging would just burn a protected message slot.
            bool separable = (approvedPlan?.Workstreams.Count ?? 0) > 1
                || (approvedPlan?.Milestones.Count ?? 0) > 1;
            if (!separable) return null;

            // Nothing to fix: no free capacity, nothing reclaimable, nobody idle, and every ready
            // milestone already has an owner.
            if (free == 0 && reclaimable.Count == 0 && idle.Count == 0 && unstaffed.Count == 0) return null;
            if (unstaffed.Count == 0 && idle.Count == 0 && reclaimable.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("STAFFING CHECKPOINT: ");
            sb.Append($"{used} of {project.SubAgentCap} agent slots in use, {free} free");
            if (reclaimable.Count > 0)
                sb.Append($", {reclaimable.Count} reclaimable");
            sb.AppendLine(".");

            if (unstaffed.Count > 0)
                sb.AppendLine($"Dependency-ready work with NO owner: {string.Join("; ", unstaffed.Take(8).Select(m => $"{m.ID} {m.Title}"))}"
                    + (unstaffed.Count > 8 ? $" (+{unstaffed.Count - 8} more)" : "") + ".");
            if (idle.Count > 0)
                sb.AppendLine($"Idle workers holding slots with no assignment: {string.Join(", ", idle.Select(a => $"{a.AgentID} ({a.Role})"))} — task them or retire them.");
            if (reclaimable.Count > 0)
                sb.AppendLine($"Finished workers you can retire to free slots: {string.Join(", ", reclaimable.Select(a => $"{a.AgentID} ({a.Role})"))}.");

            if (free > 0 && unstaffed.Count > 0)
                sb.AppendLine($"Act now: staff the unowned work — spawn up to {free} more worker(s) with manage_agents op:spawn, or assign to an idle one with op:assign_work. "
                    + "Spawn a standing mission for an ongoing beat someone must keep owning, and a task mission for a bounded deliverable.");
            else if (free == 0 && reclaimable.Count > 0 && unstaffed.Count > 0)
                sb.AppendLine($"Act now: the roster is full, so retire {string.Join(" / ", reclaimable.Take(3).Select(a => a.AgentID))} first, then staff the unowned work.");
            else if (free == 0 && unstaffed.Count > 0)
                sb.AppendLine("Every slot holds live work. If more parallelism would genuinely move the goal, ask Klives with request_budget_increase kind:agents.");
            sb.Append("Assign only dependency-ready work, set milestone owners, and require explicit deliverables unless the next step is genuinely indivisible.");

            // Escalate only when the same free capacity has gone unused across consecutive wakes: a
            // Commander that reads this every wake and still runs solo is not missing information.
            if (consecutiveUnderStaffedWakes >= 2 && free > 0 && unstaffed.Count > 0)
                sb.Append($"\nThis is the {Ordinal(consecutiveUnderStaffedWakes + 1)} consecutive wake with free slots and unowned ready work. "
                    + "Working it yourself instead of staffing it is costing the project parallel throughput — delegate this wake unless the work is genuinely indivisible, and say why if it is.");

            return sb.ToString();
        }

        /// <summary>Does this wake's roster/plan combination count as under-staffed for the escalation
        /// counter? Deliberately narrower than the checkpoint itself: only free capacity sitting
        /// against unowned ready work escalates, not a merely retirable agent.</summary>
        public static bool IsUnderStaffed(
            Project project,
            IReadOnlyList<PlanMilestone> readyMilestones,
            IReadOnlyList<ProjectAgentRecord> roster)
        {
            if (project.Status != ProjectStatus.Active || project.SubAgentCap <= 1) return false;
            var active = roster.Where(a => !a.Retired).ToList();
            int free = Math.Max(0, project.SubAgentCap - active.Count);
            return free > 0 && UnstaffedReady(readyMilestones, active).Count > 0;
        }

        private static string Ordinal(int n) => n switch
        {
            1 => "1st", 2 => "2nd", 3 => "3rd",
            _ => n + "th",
        };
    }
}
