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
        public ProjectAgentRecord Spawn(string projectID, string parentAgentID, ProjectAgentTier tier, string role)
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

                int active = agents.Count(a => !a.Retired);
                if (active >= project.SubAgentCap)
                    throw new InvalidOperationException($"Agent cap reached ({project.SubAgentCap}). Retire an agent or request a cap increase from Klives.");

                var agent = new ProjectAgentRecord
                {
                    AgentID = Guid.NewGuid().ToString("N")[..12],
                    ProjectID = projectID,
                    ParentAgentID = parentAgentID,
                    Tier = tier,
                    Role = role,
                };
                agents.Add(agent);
                SaveLocked(projectID, agents);

                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    AgentID = agent.AgentID,
                    Type = ProjectEventTypes.AgentSpawned,
                    Author = "commander",
                    Text = $"Spawned {tier} agent '{role}' ({agent.AgentID}) under {parentAgentID}.",
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
                $"{a.Role}[id={a.AgentID}, tier={a.Tier}]" +
                (a.ParentAgentID == null ? "" : $"←{a.ParentAgentID}")));
        }

        private List<ProjectAgentRecord> LoadLocked(string projectID)
        {
            string path = AgentsPath(projectID);
            if (!File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<List<ProjectAgentRecord>>(File.ReadAllText(path)) ?? new(); }
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
