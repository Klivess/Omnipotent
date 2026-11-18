using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Tracks a Steward's agent roster and enforces the per-Steward cap and the one-level-deep
    /// delegation rule (§6.2). The Commander spawns/retires sub-agents; a sub-agent may spawn
    /// short-lived helpers but a helper may not spawn further (depth is hard-capped at 1). All
    /// spawns count against the same cap, which is set at initialisation and raisable only
    /// through a budget-style conversation with Klives.
    ///
    /// Layout: Demiurge/Agents/&lt;stewardID&gt;.agents.json
    /// </summary>
    public class StewardSubAgentManager
    {
        private readonly StewardStore stewardStore;
        private readonly StewardEventLogStore eventLog;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public StewardSubAgentManager(StewardStore stewardStore, StewardEventLogStore eventLog)
        {
            this.stewardStore = stewardStore;
            this.eventLog = eventLog;
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "Agents");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string AgentsPath(string stewardID) => Path.Combine(dir, stewardID + ".agents.json");

        // Response-cache dependency key. Every /demiurge/* GET is cacheable (RequireSteward notes a
        // read of the Steward index), so a store that neither notes its reads nor bumps on write has
        // its stale output served back indefinitely — /demiurge/agents would keep listing an agent
        // Klives had already removed. Now that the roster can change from OUTSIDE a Steward save,
        // rather than only as a side effect of one, this instrumentation is load-bearing.
        private static string CacheKey(string stewardID) => "demiurge:agents:" + stewardID;

        public const string CommanderRole = "commander";

        /// <summary>Ensures the Commander record exists (created on first wake). Idempotent.</summary>
        public StewardAgentRecord EnsureCommander(string stewardID)
        {
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
                var commander = agents.FirstOrDefault(a => a.Role == CommanderRole);
                if (commander != null) return commander;
                commander = new StewardAgentRecord
                {
                    AgentID = "commander",
                    StewardID = stewardID,
                    ParentAgentID = null,
                    Tier = StewardAgentTier.TextImageVideo, // Commander perceives desktops
                    Role = CommanderRole,
                    Objective = "Coordinate the Steward to its approved goal.",
                    WorkStatus = StewardAgentWorkStatus.Running,
                };
                agents.Add(commander);
                SaveLocked(stewardID, agents);
                return commander;
            }
        }

        /// <summary>
        /// Spawns a sub-agent under <paramref name="parentAgentID"/>. Enforces the cap and the
        /// one-level delegation depth. Throws InvalidOperationException with a message the
        /// Commander can read and act on (it becomes the tool result).
        /// </summary>
        public StewardAgentRecord Spawn(string stewardID, string parentAgentID, StewardAgentTier tier, string role,
            string objective = "", StewardAgentMissionKind missionKind = StewardAgentMissionKind.Task)
        {
            var steward = stewardStore.GetSteward(stewardID)
                ?? throw new InvalidOperationException("Unknown Steward.");
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
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
                if (active >= steward.SubAgentCap)
                {
                    var now = DateTime.UtcNow;
                    var reclaimable = agents.Where(a => IsReclaimable(a, now)).ToList();
                    throw new InvalidOperationException(
                        $"Agent cap reached ({active} of {steward.SubAgentCap}). "
                        + (reclaimable.Count > 0
                            ? "Reclaimable now: "
                              + string.Join(", ", reclaimable.Select(a =>
                                  $"{a.AgentID} ({a.Role}, finished, quiet {Data_Handling.TemporalFormat.Age(LastActivity(a))})"))
                              + " — retire one with manage_agents op:retire and spawn again, "
                            : "Every agent is holding live work — ")
                        + "or ask Klives for more slots with request_budget_increase kind:agents.");
                }

                var agent = new StewardAgentRecord
                {
                    AgentID = Guid.NewGuid().ToString("N")[..12],
                    StewardID = stewardID,
                    ParentAgentID = parentAgentID,
                    Tier = tier,
                    Role = role,
                    Objective = objective.Trim(),
                    MissionKind = missionKind,
                    WorkStatus = StewardAgentWorkStatus.Assigned,
                };
                agents.Add(agent);
                SaveLocked(stewardID, agents);

                eventLog.Append(new StewardEvent
                {
                    StewardID = stewardID,
                    AgentID = agent.AgentID,
                    Type = StewardEventTypes.AgentSpawned,
                    Author = "commander",
                    Text = $"Spawned {tier} agent '{role}' ({agent.AgentID}) under {parentAgentID} "
                        + $"on a {missionKind.ToString().ToLowerInvariant()} mission.",
                });
                return agent;
            }
        }

        /// <summary>Depth of an agent in the org tree: Commander = 0, its children = 1, their children = 2.</summary>
        private static int AgentDepth(List<StewardAgentRecord> agents, StewardAgentRecord agent)
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

        /// <summary>
        /// Retires one agent. <paramref name="author"/> is who took the slot ("commander", "klives",
        /// "system") and <paramref name="reasonNote"/> is the human-readable why, which matters now
        /// that retirement is no longer always the agent's own work finishing — Klives can lower the
        /// cap out from under a running assignment, and the log line is what tells the difference.
        /// Returns the record as it was immediately before retirement so the caller can snapshot the
        /// work it was holding; null when there was no such active agent.
        /// </summary>
        public StewardAgentRecord? RetireWithRecord(string stewardID, string agentID,
            string author = "commander", string? reasonNote = null)
        {
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return null;
                if (IsCommander(agent))
                    throw new InvalidOperationException("The Commander cannot be retired through the sub-agent lifecycle tool.");
                var activeChildren = agents.Where(a => !a.Retired && a.ParentAgentID == agent.AgentID).ToList();
                if (activeChildren.Count > 0)
                    throw new InvalidOperationException($"Retire or reassign this agent's active children first: {string.Join(", ", activeChildren.Select(a => a.AgentID))}.");
                // Snapshot BEFORE flipping the flag: the caller's handover record is built from this,
                // and a retired record stops being seeded, messageable or rostered.
                var snapshot = Clone(agent);
                agent.Retired = true;
                agent.RetiredAt = DateTime.UtcNow;
                SaveLocked(stewardID, agents);
                eventLog.Append(new StewardEvent
                {
                    StewardID = stewardID,
                    AgentID = agentID,
                    Type = StewardEventTypes.AgentRetired,
                    Author = string.IsNullOrWhiteSpace(author) ? "commander" : author.Trim(),
                    Text = $"Retired agent {agentID} ({agent.Role})."
                        + (string.IsNullOrWhiteSpace(reasonNote) ? "" : " " + reasonNote!.Trim()),
                });
                return snapshot;
            }
        }

        public bool Retire(string stewardID, string agentID) =>
            RetireWithRecord(stewardID, agentID) != null;

        /// <summary>One active agent by ID, or null. Does not resolve roles — see TryResolveActiveTarget.</summary>
        public StewardAgentRecord? Get(string stewardID, string agentID)
        {
            if (string.IsNullOrWhiteSpace(agentID)) return null;
            lock (LockFor(stewardID))
                return LoadLocked(stewardID)
                    .Where(a => !a.Retired && string.Equals(a.AgentID, agentID.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .FirstOrDefault();
        }

        private static StewardAgentRecord Clone(StewardAgentRecord source) =>
            JsonConvert.DeserializeObject<StewardAgentRecord>(JsonConvert.SerializeObject(source))!;

        public List<StewardAgentRecord> ListActive(string stewardID)
        {
            lock (LockFor(stewardID)) return LoadLocked(stewardID).Where(a => !a.Retired).ToList();
        }

        public bool UpdateWorkState(string stewardID, string agentID, StewardAgentWorkStatus status,
            string? lastReport = null, IEnumerable<string>? deliverablePaths = null)
        {
            if (status == StewardAgentWorkStatus.Blocked)
                status = StewardAgentWorkStatus.Assigned;
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
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
                SaveLocked(stewardID, agents);
                return true;
            }
        }

        /// <summary>Records that an agent has begun a wake. Separate from UpdateWorkState so the
        /// timestamp moves even on wakes that end without producing a report.</summary>
        public bool MarkWakeStarted(string stewardID, string agentID, DateTime? nowUtc = null)
        {
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return false;
                agent.LastWakeAt = nowUtc ?? DateTime.UtcNow;
                agent.WorkStatus = StewardAgentWorkStatus.Running;
                SaveLocked(stewardID, agents);
                return true;
            }
        }

        /// <summary>
        /// Records which milestones an agent — including the Commander — is personally working.
        /// Ownership is what makes a milestone "staffed"; without a way to record that the Commander
        /// took something itself, indivisible work stayed permanently unowned and the staffing
        /// heuristic reported an under-staffed Steward forever.
        /// </summary>
        public bool SetActiveMilestones(string stewardID, string agentID, IEnumerable<string> milestoneIDs)
        {
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired);
                if (agent == null) return false;
                agent.ActiveMilestoneIDs = milestoneIDs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                SaveLocked(stewardID, agents);
                return true;
            }
        }

        public bool AssignObjective(string stewardID, string agentID, string objective,
            IEnumerable<string> milestoneIDs, IEnumerable<string>? deliverablePaths = null,
            StewardAgentMissionKind? missionKind = null)
        {
            if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("objective required", nameof(objective));
            lock (LockFor(stewardID))
            {
                var agents = LoadLocked(stewardID);
                var agent = agents.FirstOrDefault(a => a.AgentID == agentID && !a.Retired && a.AgentID != "commander");
                if (agent == null) return false;
                agent.Objective = objective.Trim();
                if (missionKind.HasValue) agent.MissionKind = missionKind.Value;
                agent.ActiveMilestoneIDs = milestoneIDs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                agent.DeliverablePaths = (deliverablePaths ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                agent.WorkStatus = StewardAgentWorkStatus.Assigned;
                agent.LastReport = null;
                agent.LastReportAt = null;
                SaveLocked(stewardID, agents);
                return true;
            }
        }

        /// <summary>
        /// Resolves a messaging target to an active agent. Agent IDs are canonical, but a unique
        /// role is accepted as a human/model-friendly alias. This keeps a stale or guessed target
        /// from being logged as "sent" and then disappearing into an undeliverable bus envelope.
        /// </summary>
        public bool TryResolveActiveTarget(string stewardID, string target,
            out StewardAgentRecord? agent, out string error)
        {
            agent = null;
            target = (target ?? "").Trim();
            if (target.Length == 0)
            {
                error = "Provide an agent ID or role.";
                return false;
            }

            var active = ListActive(stewardID);
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
        public string DescribeOrgChart(string stewardID)
        {
            var active = ListActive(stewardID);
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

        public static bool IsCommander(StewardAgentRecord agent) =>
            string.Equals(agent.AgentID, "commander", StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.Role, CommanderRole, StringComparison.OrdinalIgnoreCase);

        /// <summary>Last sign of life: a report if there is one, else the last wake, else creation.</summary>
        private static DateTime LastActivity(StewardAgentRecord a) =>
            a.LastReportAt ?? a.LastWakeAt ?? a.CreatedAt;

        /// <summary>
        /// A finished bounded worker holding a slot it no longer needs. Standing agents are never
        /// reclaimable — they own an ongoing beat and only the Commander closes one.
        /// </summary>
        public static bool IsReclaimable(StewardAgentRecord a, DateTime nowUtc) =>
            !a.Retired
            && !IsCommander(a)
            && a.MissionKind == StewardAgentMissionKind.Task
            && a.WorkStatus == StewardAgentWorkStatus.Completed
            && a.ActiveMilestoneIDs.Count == 0
            && nowUtc - LastActivity(a) >= ReclaimQuietPeriod;

        public List<StewardAgentRecord> ListReclaimable(string stewardID, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            return ListActive(stewardID).Where(a => IsReclaimable(a, now)).ToList();
        }

        /// <summary>Agents holding no work at all: nothing assigned, nothing in flight.</summary>
        public static bool IsIdle(StewardAgentRecord a) =>
            !a.Retired
            && !IsCommander(a)
            && a.WorkStatus == StewardAgentWorkStatus.Idle
            && a.ActiveMilestoneIDs.Count == 0;

        /// <summary>The status flag for one agent: what the Commander must decide about it, if anything.</summary>
        private static string StatusFlag(StewardAgentRecord a, DateTime nowUtc)
        {
            if (IsCommander(a)) return "";
            if (IsReclaimable(a, nowUtc)) return "  ⚑ FINISHED — reclaimable: retire to free a slot";
            if (a.MissionKind == StewardAgentMissionKind.Task && a.WorkStatus == StewardAgentWorkStatus.Completed)
                return "  ⚑ FINISHED — reported recently; retire once you have used its result";
            if (IsIdle(a)) return "  ⚑ IDLE — holding a slot with no assignment: task it or retire it";
            if (a.LastWakeAt == null)
                return "  ⚑ NEVER WOKEN — spawned but never given work";
            var quiet = nowUtc - LastActivity(a);
            if (quiet >= SilenceThreshold)
                return $"  ⚑ SILENT since {Data_Handling.TemporalFormat.StampMinute(LastActivity(a))} — check on it or re-task it";
            return "";
        }

        /// <summary>
        /// The roster as an operational picture rather than a one-line org chart: what each agent is
        /// for, when it was last heard from, and — the part the old chart could not express at all —
        /// how many slots are free and which agents can be retired to free more. Rendered into the
        /// Commander's wake seed every wake, and into each worker's seed (with
        /// <paramref name="viewerAgentID"/> set) so workers can see and address their peers.
        /// </summary>
        public string DescribeTaskForce(string stewardID, int cap, string? viewerAgentID = null, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var active = ListActive(stewardID);
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
                    sb.AppendLine($"  last report {Data_Handling.TemporalFormat.StampMinute(a.LastReportAt.Value)}: {Clip(a.LastReport!, 240)}");
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

        private List<StewardAgentRecord> LoadLocked(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            string path = AgentsPath(stewardID);
            if (!File.Exists(path)) return new();
            try
            {
                var agents = JsonConvert.DeserializeObject<List<StewardAgentRecord>>(File.ReadAllText(path)) ?? new();
                foreach (var agent in agents)
                    if (agent.WorkStatus == StewardAgentWorkStatus.Blocked)
                        agent.WorkStatus = StewardAgentWorkStatus.Assigned;
                return agents;
            }
            catch { return new(); }
        }

        private void SaveLocked(string stewardID, List<StewardAgentRecord> agents)
        {
            string path = AgentsPath(stewardID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(agents, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            CacheDeps.Bump(CacheKey(stewardID)); // after the move — the new roster is now visible
        }
    }
}
