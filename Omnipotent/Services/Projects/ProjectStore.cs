using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Storage for Project records themselves (goal, budget, status). One JSON index file,
    /// atomic-write (.tmp + move) on every mutation — the same pattern OmniGlobalSettingsManager
    /// and Stratum's meta files use. Project records are few and small; the high-volume data
    /// (events) lives in <see cref="ProjectEventLogStore"/>.
    /// </summary>
    public class ProjectStore
    {
        private readonly string indexPath;
        private readonly Action<string> log;
        private readonly object gate = new();
        private List<Project> projects = new();

        public ProjectStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            indexPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsIndexFile);
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            Load();
        }

        private void Load()
        {
            lock (gate)
            {
                if (!File.Exists(indexPath)) { projects = new List<Project>(); return; }
                try
                {
                    projects = JsonConvert.DeserializeObject<List<Project>>(File.ReadAllText(indexPath)) ?? new List<Project>();
                }
                catch (Exception ex)
                {
                    log($"ProjectStore: failed to load index ({ex.Message}) — starting empty, file preserved.");
                    projects = new List<Project>();
                }
            }
        }

        private void SaveLocked()
        {
            // Unique tmp name so concurrent writers never collide on a shared temp path.
            string tmp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(projects, Formatting.Indented));
            // Retry the replace: on Windows a concurrent move to the same destination can briefly
            // lock it. Production has a single store instance, but this keeps the write race-proof.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, indexPath, overwrite: true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(15); }
            }
        }

        public Project CreateProject(string name, string goal, double tokenBudgetUsd, double moneyBudgetUsd,
            double moneyAutonomousThresholdUsd, int subAgentCap)
        {
            var p = new Project
            {
                ProjectID = Guid.NewGuid().ToString("N"),
                Name = name,
                Goal = goal,
                TokenBudgetUsd = tokenBudgetUsd,
                MoneyBudgetUsd = moneyBudgetUsd,
                MoneyAutonomousThresholdUsd = moneyAutonomousThresholdUsd,
                SubAgentCap = subAgentCap,
            };
            lock (gate)
            {
                projects.Add(p);
                SaveLocked();
            }
            return p;
        }

        public Project? GetProject(string projectID)
        {
            lock (gate) return projects.FirstOrDefault(p => p.ProjectID == projectID);
        }

        public List<Project> ListProjects()
        {
            lock (gate) return projects.ToList();
        }

        /// <summary>Persists a mutation to an existing project (status flips, budget increases, channel ID).</summary>
        public void SaveProject(Project project)
        {
            lock (gate)
            {
                int idx = projects.FindIndex(p => p.ProjectID == project.ProjectID);
                if (idx < 0) throw new InvalidOperationException($"Unknown project {project.ProjectID}");
                projects[idx] = project;
                SaveLocked();
            }
        }
    }
}
