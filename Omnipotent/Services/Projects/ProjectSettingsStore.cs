using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Storage for per-project settings (Projects' own setting system, not OmniSettings). One
    /// small JSON doc per project, atomic rewrite — same durability shape as the ledger/vault.
    /// Missing files return defaults, so a project created before a new setting existed simply
    /// picks up that setting's default.
    ///
    /// Layout: Projects/Settings/&lt;projectID&gt;.settings.json
    /// </summary>
    public class ProjectSettingsStore
    {
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectSettingsStore()
        {
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Settings");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string PathFor(string projectID) => Path.Combine(dir, projectID + ".settings.json");

        public ProjectSettings Get(string projectID)
        {
            lock (LockFor(projectID))
            {
                string path = PathFor(projectID);
                if (File.Exists(path))
                {
                    try
                    {
                        var s = JsonConvert.DeserializeObject<ProjectSettings>(File.ReadAllText(path));
                        if (s != null) { s.ProjectID = projectID; return s; }
                    }
                    catch { }
                }
                return new ProjectSettings { ProjectID = projectID };
            }
        }

        public void Save(ProjectSettings settings)
        {
            lock (LockFor(settings.ProjectID))
            {
                settings.UpdatedAt = DateTime.UtcNow;
                string path = PathFor(settings.ProjectID);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
                File.Move(tmp, path, overwrite: true);
            }
        }

        /// <summary>Seeds a project's settings with defaults at creation (idempotent).</summary>
        public ProjectSettings EnsureCreated(string projectID)
        {
            var s = Get(projectID);
            Save(s);
            return s;
        }
    }
}
