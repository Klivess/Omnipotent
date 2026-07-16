using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// On-disk map of project/agent → desktop container. Atomic-write JSON (same .tmp+move
    /// pattern as OmniGlobalSettingsManager) so the record of which containers exist survives
    /// restarts; <see cref="ContainerOrchestrator.ReconcileAsync"/> trues it up against real
    /// Docker state on every boot.
    /// </summary>
    public class ContainerRegistry
    {
        private readonly string path;
        private readonly Action<string> log;
        private readonly object gate = new();
        private List<DesktopContainerRecord> records = new();

        public ContainerRegistry(Action<string> log)
        {
            this.log = log ?? (_ => { });
            path = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsContainersFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Load();
        }

        private void Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) { records = new(); return; }
                try { records = JsonConvert.DeserializeObject<List<DesktopContainerRecord>>(File.ReadAllText(path)) ?? new(); }
                catch (Exception ex)
                {
                    log($"ContainerRegistry: failed to load ({ex.Message}) — starting empty, file preserved.");
                    records = new();
                }
            }
        }

        private void SaveLocked()
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(records, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }

        public void Add(DesktopContainerRecord record)
        {
            lock (gate)
            {
                // Reconciliation can discover a just-started Docker container in the small window
                // before its provisioning path persists the record. Treat Add as an idempotent
                // upsert so that race cannot leave two menu entries for the same desktop.
                records.RemoveAll(r => r.ContainerID == record.ContainerID);
                records.Add(record);
                SaveLocked();
            }
        }

        public void Remove(string containerID)
        {
            lock (gate)
            {
                records.RemoveAll(r => r.ContainerID == containerID);
                SaveLocked();
            }
        }

        public void Update(DesktopContainerRecord record)
        {
            lock (gate)
            {
                records.RemoveAll(r => r.ContainerID == record.ContainerID);
                records.Add(record);
                SaveLocked();
            }
        }

        public List<DesktopContainerRecord> All()
        {
            lock (gate) return records.ToList();
        }

        public List<DesktopContainerRecord> ForProject(string projectID)
        {
            lock (gate) return records.Where(r => r.ProjectID == projectID && !r.Lost).ToList();
        }

        /// <summary>The desktop an agent should use: its own container if one exists, else the project's shared desktop.</summary>
        public DesktopContainerRecord? ResolveForAgent(string projectID, string agentID)
        {
            lock (gate)
            {
                return records.FirstOrDefault(r => r.ProjectID == projectID && r.AgentID == agentID && !r.Lost)
                    ?? records.FirstOrDefault(r => r.ProjectID == projectID && r.AgentID == null && !r.Lost);
            }
        }
    }
}
