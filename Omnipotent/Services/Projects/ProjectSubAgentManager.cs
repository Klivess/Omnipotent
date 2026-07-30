using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Tracks a project's agent roster and enforces the per-project cap and the one-level-deep
    /// delegation rule (§6.2). The Commander spawns/retires sub-agents; a sub-agent may spawn
    /// short-lived helpers but a helper may not spawn further (depth is hard-capped at 1). All
    /// spawns count against the same cap, which is set at initialisation and raisable only
    /// through a budget-style conversation with Klives.
    ///
    /// Layout: Projects/Agents/&lt;projectID&gt;.agents.json
    /// </summary>
    public class ProjectSubAgentManager
    {
        private readonly ProjectStore projectStore;
        private readonly ProjectEventLogStore eventLog;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectSubAgentManager(ProjectStore projectStore, ProjectEventLogStore eventLog)
        {
            this.projectStore = projectStore;
            this.eventLog = eventLog;
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Agents");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string AgentsPath(string projectID) => Path.Combine(dir, projectID + ".agents.json");

        public const string CommanderRole = "commander";

        /// <summary>Ensures the Commander record exists (created on first wake). Idempotent.</summary>
        public ProjectAgentRecord EnsureCommander(string projectID)
        {
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var commander = agents.FirstOrDefault(a => a.Role == CommanderRole);
                if (commander != null) return commander;
                commander = new ProjectAgentRecord
                {
                    AgentID = "commander",
                    ProjectID = projectID,
                    ParentAgentID = null,
                    Tier = ProjectAgentTier.TextImageVideo, // Commander perceives desktops
                    Role = CommanderRole,
                    Objective = "Coordinate the project to its approved goal.",
                    WorkStatus = ProjectAgentWorkStatus.Running,
                };
                agents.Add(commander);
                SaveLocked(projectID, agents);
                return commander;
            }
        }

        /// <summary>
        /// Spawns a sub-agent under <paramref name="parentAgentID"/>. Enforces the cap and the
        /// one-level delegation depth. Throws InvalidOperationException with a message the
        /// Commander can read and act on (it becomes the tool result).
        /// </summary>
        public ProjectAgentRecord Spawn(string projectID, string parentAgentID, ProjectAgentTier tier, string role,
            string objective = "", ProjectAgentMissionKind missionKind = ProjectAgentMissionKind.Task)
        {
            var project = projectStore.GetProject(projectID)
                ?? throw new InvalidOperationException("Unknown project.");
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var parent = agents.FirstOrDefault(a => a.AgentID == parentAgentID && !a.Retired)
                    ?? throw new InvalidOperationException($"Parent agent '{parentAgentID}' not found or retired.");

                // Depth: Commander (0) → sub-agent (1) → helper (2). Delegation is one level deep,
                // so an agent at depth ≥ 2 (a helper) may not spawn. Depth is derived by walking up.
                if (AgentDepth(agents, parent) >= 2)
                    throw new InvalidOperationException("Delegation is one level deep: a helper agent cannot spawn further agents.");

                // The cap message is the Commander's only feedback when it tries to grow the team, so
                // it names the agents that can be retired right now instead of leaving it to go
                // hunting. A dead end that states its own way out costs one tool call, not a wake.
                int active = agents.Count(a => !a.Retired);
                if (active >= project.SubAgentCap)
                {
                    var now = DateTime.UtcNow;
                    var reclaimable = agents.Where(a => IsReclaimable(a, now)).ToList();
                    throw new InvalidOperationException(
                        $"Agent cap reached ({active} of {project.SubAgentCap}). "
                        + (reclaimable.Count > 0
                            ? "Reclaimable now: "
                              + string.Join(", ", reclaimable.Select(a =>
                                  $"{a.AgentID} ({a.Role}, finished, quiet {Data_Handling.TemporalFormat.Age(LastActivity(a))})"))
                              + " — retire one with manage_agents op:retire and spawn again, "
                            : "Every agent is holding live work — ")
                        + "or ask Klives for more slots with request_budget_increase kind:agents.");
                }

                var agent = new ProjectAgentRecord
                {
                    AgentID = Guid.NewGuid().ToString("N")[..12],
                    ProjectID = projectID,
                    ParentAgentID = parentAgentID,
                    Tier = tier,
                    Role = role,
                    Objective = objective.Trim(),
                    MissionKind = missionKind,
                    WorkStatus = ProjectAgentWorkStatus.Assigned,
                };
                agents.Add(agent);
                SaveLocked(projectID, agents);

                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    AgentID = agent.AgentID,
                    Type = ProjectEventTypes.AgentSpawned,
                    Author = "commander",
                    Text = $"Spawned {tier} agent '{role}' ({agent.AgentID}) under {parentAgentID} "
                        + $"on a {missionKind.ToString().ToLowerInvariant()} mission.",
                });
                return agent;
            }
        }

        /// <summary>Depth of an agent in the org tree: Commander = 0, its children = 1, their children = 2.</summary>
        private static int AgentDepth(List<ProjectAgentRecord> agents, ProjectAgentRecord agent)
        {
            int depth = 0;
            var cur = agent;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (cur?.ParentAgentID != null && seen.Add(cur.AgentID))
            {
                depth++;
                cur = agents.FirstOrDefault(a => a.AgentID == cur!.ParentAgentID);
            }
            return depth;
        }

        public bool Retire(string projectID, string agentID)
        {
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return false;
                if (string.Equals(agent.AgentID, "commander", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(agent.Role, CommanderRole, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Commander cannot be retired through the sub-agent lifecycle tool.");
                var activeChildren = agents.Where(a => !a.Retired && a.ParentAgentID == agent.AgentID).ToList();
                if (activeChildren.Count > 0)
                    throw new InvalidOperationException($"Retire or reassign this agent's active children first: {string.Join(", ", activeChildren.Select(a => a.AgentID))}.");
                agent.Retired = true;
                agent.RetiredAt = DateTime.UtcNow;
                SaveLocked(projectID, agents);
                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    AgentID = agentID,
                    Type = ProjectEventTypes.AgentRetired,
                    Author = "commander",
                    Text = $"Retired agent {agentID} ({agent.Role}).",
                });
                return true;
            }
        }

        public List<ProjectAgentRecord> ListActive(string projectID)
        {
            lock (LockFor(projectID)) return LoadLocked(projectID).Where(a => !a.Retired).ToList();
        }

        public bool UpdateWorkState(string projectID, string agentID, ProjectAgentWorkStatus status,
            string? lastReport = null, IEnumerable<string>? deliverablePaths = null)
        {
            if (status == ProjectAgentWorkStatus.Blocked)
                status = ProjectAgentWorkStatus.Assigned;
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return false;
                agent.WorkStatus = status;
                if (!string.IsNullOrWhiteSpace(lastReport))
                {
                    agent.LastReport = lastReport.Trim();
                    agent.LastReportAt = DateTime.UtcNow;
                }
                if (deliverablePaths != null)
                    agent.DeliverablePaths = deliverablePaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                SaveLocked(projectID, agents);
                return true;
            }
        }

        /// <summary>Records that an agent has begun a wake. Separate from UpdateWorkState so the
        /// timestamp moves even on wakes that end without producing a report.</summary>
        public bool MarkWakeStarted(string projectID, string agentID, DateTime? nowUtc = null)
        {
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return false;
                agent.LastWakeAt = nowUtc ?? DateTime.UtcNow;
                agent.WorkStatus = ProjectAgentWorkStatus.Running;
                SaveLocked(projectID, agents);
                return true;
            }
        }

        public bool AssignObjective(string projectID, string agentID, string objective,
            IEnumerable<string> milestoneIDs, IEnumerable<string>? deliverablePaths = null,
            ProjectAgentMissionKind? missionKind = null)
        {
            if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("objective required", nameof(objective));
            lock (LockFor(projectID))
            {
                var agents = LoadLocked(projectID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired && a.AgentID != "commander");
                if (agent == null) return false;
                agent.Objective = objective.Trim();
                if (missionKind.HasValue) agent.MissionKind = missionKind.Value;
                agent.ActiveMilestoneIDs = milestoneIDs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                agent.DeliverablePaths = (deliverablePaths ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                agent.WorkStatus = ProjectAgentWorkStatus.Assigned;
                agent.LastReport = null;
                agent.LastReportAt = null;
                SaveLocked(projectID, agents);
                return true;
            }
        }

        /// <summary>
        /// Resolves a messaging target to an active agent. Agent IDs are canonical, but a unique
        /// role is accepted as a human/model-friendly alias. This keeps a stale or guessed target
        /// from being logged as "sent" and then disappearing into an undeliverable bus envelope.
        /// </summary>
        public bool TryResolveActiveTarget(string projectID, string target,
            out ProjectAgentRecord? agent, out string error)
        {
            agent = null;
            target = (target ?? "").Trim();
            if (target.Length == 0)
            {
                error = "Provide an agent ID or role.";
                return false;
            }

            var active = ListActive(projectID);
            agent = active.FirstOrDefault(a =>
                string.Equals(a.AgentID, target, StringComparison.OrdinalIgnoreCase));
            if (agent != null)
            {
                error = "";
                return true;
            }

            var roleMatches = active.Where(a =>
                string.Equals(a.Role, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (roleMatches.Count == 1)
            {
                agent = roleMatches[0];
                error = "";
                return true;
            }
            if (roleMatches.Count > 1)
            {
                error = $"Role '{target}' is ambiguous. Use one of these agent IDs: " +
                        string.Join(", ", roleMatches.Select(a => a.AgentID)) + ".";
                return false;
            }

            string available = active.Count == 0
                ? "none"
                : string.Join(", ", active.Select(a => $"{a.Role} (id: {a.AgentID})"));
            error = $"No active agent matches '{target}'. Available agents: {available}.";
            return false;
        }

        /// <summary>Compact org-chart string for the standing digest / wake seed.</summary>
        public string DescribeOrgChart(string projectID)
        {
            var active = ListActive(projectID);
            if (active.Count == 0) return "(no agents yet)";
            return string.Join("; ", active.Select(a =>
                $"{a.Role}[id={a.AgentID}, tier={a.Tier}, status={a.WorkStatus}, objective={a.Objective}]" +
                (a.ParentAgentID == null ? "" : $"←{a.ParentAgentID}")));
        }

        // ── task-force utilization ──
        //
        // Retirement is deliberately manual: only the Commander retires an agent. That makes slot
        // reclamation entirely dependent on the Commander noticing, so every place a staffing
        // decision is made — the wake seed, the staffing checkpoint, and the cap-reached error —
        // names the reclaimable agents by ID rather than merely reporting that the roster is full.

        /// <summary>Grace period after a bounded agent's final report before it is advertised as
        /// reclaimable, so the Commander gets one wake to act on the report before being told to
        /// retire its author.</summary>
        public static readonly TimeSpan ReclaimQuietPeriod = TimeSpan.FromMinutes(10);

        /// <summary>Silence past this reads as a worker that has stopped making progress. Matches the
        /// watchdog's MaxWakeGap so the seed and the stall detector agree on what "quiet" means.</summary>
        public static readonly TimeSpan SilenceThreshold = TimeSpan.FromMinutes(30);

        public static bool IsCommander(ProjectAgentRecord agent) =>
            string.Equals(agent.AgentID, "commander", StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Role, CommanderRole, StringComparison.OrdinalIgnoreCase);

        /// <summary>Last sign of life: a report if there is one, else the last wake, else creation.</summary>
        private static DateTime LastActivity(ProjectAgentRecord a) =>
            a.LastReportAt ?? a.LastWakeAt ?? a.CreatedAt;

        /// <summary>
        /// A finished bounded worker holding a slot it no longer needs. Standing agents are never
        /// reclaimable — they own an ongoing beat and only the Commander closes one.
        /// </summary>
        public static bool IsReclaimable(ProjectAgentRecord a, DateTime nowUtc) =>
            !a.Retired
            && !IsCommander(a)
            && a.MissionKind == ProjectAgentMissionKind.Task
            && a.WorkStatus == ProjectAgentWorkStatus.Completed
            && a.ActiveMilestoneIDs.Count == 0
            && nowUtc - LastActivity(a) >= ReclaimQuietPeriod;

        public List<ProjectAgentRecord> ListReclaimable(string projectID, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            return ListActive(projectID).Where(a => IsReclaimable(a, now)).ToList();
        }

        /// <summary>Agents holding no work at all: nothing assigned, nothing in flight.</summary>
        public static bool IsIdle(ProjectAgentRecord a) =>
            !a.Retired
            && !IsCommander(a)
            && a.WorkStatus == ProjectAgentWorkStatus.Idle
            && a.ActiveMilestoneIDs.Count == 0;

        /// <summary>The status flag for one agent: what the Commander must decide about it, if anything.</summary>
        private static string StatusFlag(ProjectAgentRecord a, DateTime nowUtc)
        {
            if (IsCommander(a)) return "";
            if (IsReclaimable(a, nowUtc)) return "  ⚑ FINISHED — reclaimable: retire to free a slot";
            if (a.MissionKind == ProjectAgentMissionKind.Task && a.WorkStatus == ProjectAgentWorkStatus.Completed)
                return "  ⚑ FINISHED — reported recently; retire once you have used its result";
            if (IsIdle(a)) return "  ⚑ IDLE — holding a slot with no assignment: task it or retire it";
            if (a.LastWakeAt == null)
                return "  ⚑ NEVER WOKEN — spawned but never given work";
            var quiet = nowUtc - LastActivity(a);
            if (quiet >= SilenceThreshold)
                return $"  ⚑ SILENT {Data_Handling.TemporalFormat.Age(LastActivity(a))} — check on it or re-task it";
            return "";
        }

        /// <summary>
        /// The roster as an operational picture rather than a one-line org chart: what each agent is
        /// for, when it was last heard from, and — the part the old chart could not express at all —
        /// how many slots are free and which agents can be retired to free more. Rendered into the
        /// Commander's wake seed every wake, and into each worker's seed (with
        /// <paramref name="viewerAgentID"/> set) so workers can see and address their peers.
        /// </summary>
        public string DescribeTaskForce(string projectID, int cap, string? viewerAgentID = null, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var active = ListActive(projectID);
            if (active.Count == 0) return "(no agents yet — the roster is empty)";

            var sb = new System.Text.StringBuilder();
            foreach (var a in active.OrderBy(a => IsCommander(a) ? 0 : 1).ThenBy(a => a.Role, StringComparer.OrdinalIgnoreCase))
            {
                string mine = viewerAgentID != null && a.AgentID == viewerAgentID ? " (you)" : "";
                string milestones = a.ActiveMilestoneIDs.Count == 0 ? "" : $", milestones={string.Join("/", a.ActiveMilestoneIDs)}";
                string mission = IsCommander(a) ? "" : $", mission={a.MissionKind.ToString().ToLowerInvariant()}";
                sb.AppendLine($"{a.Role}{mine}[id={a.AgentID}, tier={a.Tier}{mission}, status={a.WorkStatus}{milestones}]"
                    + (a.ParentAgentID == null ? "" : $" ←{a.ParentAgentID}"));
                if (!string.IsNullOrWhiteSpace(a.Objective))
                    sb.AppendLine($"  objective: {Clip(a.Objective, 200)}");
                if (!string.IsNullOrWhiteSpace(a.LastReport) && a.LastReportAt.HasValue)
                    sb.AppendLine($"  last report {Data_Handling.TemporalFormat.Age(a.LastReportAt.Value)}: {Clip(a.LastReport!, 240)}");
                else if (!IsCommander(a))
                    sb.AppendLine("  last report: (none yet)");
                string flag = StatusFlag(a, now);
                if (flag.Length > 0) sb.AppendLine(flag);
            }

            int used = active.Count;
            int free = Math.Max(0, cap - used);
            var reclaimable = active.Where(a => IsReclaimable(a, now)).ToList();
            sb.Append($"SLOTS: {used} of {cap} used · {free} free");
            if (reclaimable.Count > 0)
                sb.Append($" · {reclaimable.Count} reclaimable → retiring these frees a slot each: {string.Join(", ", reclaimable.Select(a => $"{a.AgentID} ({a.Role})"))}");
            if (free == 0 && reclaimable.Count == 0)
                sb.Append(" · roster is full with live work — raise the cap with request_budget_increase kind:agents if more parallelism would help");
            return sb.ToString();
        }

        private static string Clip(string text, int max)
        {
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return text.Length <= max ? text : text[..max] + "…";
        }

        private List<ProjectAgentRecord> LoadLocked(string projectID)
        {
            string path = AgentsPath(projectID);
            if (!File.Exists(path)) return new();
            try
            {
                var agents = JsonConvert.DeserializeObject<List<ProjectAgentRecord>>(File.ReadAllText(path)) ?? new();
                foreach (var agent in agents)
                    if (agent.WorkStatus == ProjectAgentWorkStatus.Blocked)
                        agent.WorkStatus = ProjectAgentWorkStatus.Assigned;
                return agents;
            }
            catch { return new(); }
        }

        private void SaveLocked(string projectID, List<ProjectAgentRecord> agents)
        {
            string path = AgentsPath(projectID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(agents, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
