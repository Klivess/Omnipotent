using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// CRUD store for stimulus hooks (§5.1), per project. Every mutation appends a HookChanged
    /// event to the log so hook changes are themselves part of the timeline. One JSON file per
    /// project, atomic rewrite.
    /// </summary>
    public class StimulusHookStore
    {
        private readonly ProjectEventLogStore eventLog;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public StimulusHookStore(ProjectEventLogStore eventLog)
        {
            this.eventLog = eventLog;
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsStimulusDirectory), "Hooks");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string HooksPath(string projectID) => Path.Combine(dir, projectID + ".hooks.json");

        public StimulusHookRecord Create(StimulusHookRecord hook)
        {
            if (string.IsNullOrWhiteSpace(hook.HookID)) hook.HookID = Guid.NewGuid().ToString("N");
            EnsureIngressToken(hook);
            lock (LockFor(hook.ProjectID))
            {
                var hooks = LoadLocked(hook.ProjectID);
                hooks.Add(hook);
                SaveLocked(hook.ProjectID, hooks);
            }
            LogChange(hook.ProjectID, $"Hook created: {hook.SourceKind} → {hook.DestinationAgentID} ({hook.HookID}).");
            return hook;
        }

        public bool Update(StimulusHookRecord hook)
        {
            lock (LockFor(hook.ProjectID))
            {
                var hooks = LoadLocked(hook.ProjectID);
                int i = hooks.FindIndex(h => h.HookID == hook.HookID);
                if (i < 0) return false;
                hooks[i] = hook;
                SaveLocked(hook.ProjectID, hooks);
            }
            LogChange(hook.ProjectID, $"Hook updated: {hook.HookID}.");
            return true;
        }

        public bool Delete(string projectID, string hookID)
        {
            bool removed;
            lock (LockFor(projectID))
            {
                var hooks = LoadLocked(projectID);
                removed = hooks.RemoveAll(h => h.HookID == hookID) > 0;
                if (removed) SaveLocked(projectID, hooks);
            }
            if (removed) LogChange(projectID, $"Hook deleted: {hookID}.");
            return removed;
        }

        public List<StimulusHookRecord> List(string projectID)
        {
            lock (LockFor(projectID)) return LoadLocked(projectID);
        }

        public StimulusHookRecord? Get(string projectID, string hookID)
        {
            lock (LockFor(projectID)) return LoadLocked(projectID).FirstOrDefault(h => h.HookID == hookID);
        }

        /// <summary>All hooks across all projects (for the adapters to arm on boot).</summary>
        public List<StimulusHookRecord> AllHooks()
        {
            var all = new List<StimulusHookRecord>();
            if (!Directory.Exists(dir)) return all;
            foreach (var f in Directory.EnumerateFiles(dir, "*.hooks.json"))
            {
                try { all.AddRange(JsonConvert.DeserializeObject<List<StimulusHookRecord>>(File.ReadAllText(f)) ?? new()); }
                catch { }
            }
            return all;
        }

        private void LogChange(string projectID, string text) =>
            eventLog.Append(new ProjectEvent { ProjectID = projectID, Type = ProjectEventTypes.HookChanged, Author = "commander", Text = text });

        private List<StimulusHookRecord> LoadLocked(string projectID)
        {
            string path = HooksPath(projectID);
            if (!File.Exists(path)) return new();
            try
            {
                var hooks = JsonConvert.DeserializeObject<List<StimulusHookRecord>>(File.ReadAllText(path)) ?? new();
                bool changed = false;
                foreach (var hook in hooks)
                    if (hook.SourceKind == "webhook" && string.IsNullOrWhiteSpace(hook.IngressToken))
                    {
                        EnsureIngressToken(hook);
                        changed = true;
                    }
                if (changed) SaveLocked(projectID, hooks);
                return hooks;
            }
            catch { return new(); }
        }

        public string RotateIngressToken(string projectID, string hookID)
        {
            lock (LockFor(projectID))
            {
                var hooks = LoadLocked(projectID);
                var hook = hooks.FirstOrDefault(h => h.HookID == hookID && h.SourceKind == "webhook")
                    ?? throw new InvalidOperationException("webhook hook not found");
                hook.IngressToken = NewIngressToken();
                SaveLocked(projectID, hooks);
                return hook.IngressToken;
            }
        }

        private static void EnsureIngressToken(StimulusHookRecord hook)
        {
            if (hook.SourceKind == "webhook" && string.IsNullOrWhiteSpace(hook.IngressToken))
                hook.IngressToken = NewIngressToken();
        }

        private static string NewIngressToken() =>
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        private void SaveLocked(string projectID, List<StimulusHookRecord> hooks)
        {
            string path = HooksPath(projectID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(hooks, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
