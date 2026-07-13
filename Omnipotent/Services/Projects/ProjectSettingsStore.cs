using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Storage for per-project settings (Projects' own setting system, not OmniSettings). One
    /// small JSON doc per project, atomic rewrite — same durability shape as the ledger/vault.
    ///
    /// SYSTEM DEFAULTS: a single `_system-defaults.json` holds the values NEW projects inherit,
    /// editable by Klives. It layers over the hardcoded <see cref="ProjectSettings.Defaults"/>
    /// (which remain the ultimate fallback). A project's settings are seeded from the system
    /// defaults at creation and are independent thereafter — editing the system defaults never
    /// retroactively changes an existing project.
    ///
    /// Layout: Projects/Settings/&lt;projectID&gt;.settings.json  +  Projects/Settings/_system-defaults.json
    /// </summary>
    public class ProjectSettingsStore
    {
        private readonly string dir;
        private readonly string systemDefaultsPath;
        private readonly object systemLock = new();
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectSettingsStore()
        {
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Settings");
            Directory.CreateDirectory(dir);
            systemDefaultsPath = Path.Combine(dir, "_system-defaults.json");
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string PathFor(string projectID) => Path.Combine(dir, projectID + ".settings.json");

        // ── system defaults (what new projects inherit) ──

        /// <summary>The system-wide default settings new projects inherit. Falls back to the
        /// hardcoded <see cref="ProjectSettings.Defaults"/> when no override file exists.</summary>
        public ProjectSettings GetSystemDefaults()
        {
            lock (systemLock)
            {
                if (File.Exists(systemDefaultsPath))
                {
                    try
                    {
                        var s = JsonConvert.DeserializeObject<ProjectSettings>(File.ReadAllText(systemDefaultsPath));
                        if (s != null) { s.ProjectID = ""; s.NormalizeRoutes(); return s; }
                    }
                    catch { }
                }
                return new ProjectSettings { ProjectID = "" }; // hardcoded ProjectSettings.Defaults
            }
        }

        public void SaveSystemDefaults(ProjectSettings defaults)
        {
            lock (systemLock)
            {
                defaults.ProjectID = "";
                defaults.NormalizeRoutes();
                defaults.UpdatedAt = DateTime.UtcNow;
                string tmp = systemDefaultsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                File.Move(tmp, systemDefaultsPath, overwrite: true);
            }
        }

        // ── per-project settings ──

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
                        if (s != null) { s.ProjectID = projectID; s.NormalizeRoutes(); return s; }
                    }
                    catch { }
                }
            }
            // No per-project file yet — inherit the system defaults (stamped with this project's ID).
            var seeded = GetSystemDefaults();
            seeded.ProjectID = projectID;
            return seeded;
        }

        public void Save(ProjectSettings settings)
        {
            lock (LockFor(settings.ProjectID))
            {
                settings.NormalizeRoutes();
                settings.UpdatedAt = DateTime.UtcNow;
                string path = PathFor(settings.ProjectID);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
                File.Move(tmp, path, overwrite: true);
            }
        }

        /// <summary>Seeds a project's settings from the system defaults at creation (idempotent).</summary>
        public ProjectSettings EnsureCreated(string projectID)
        {
            var s = Get(projectID); // inherits system defaults when no file exists yet
            Save(s);
            return s;
        }
    }
}
