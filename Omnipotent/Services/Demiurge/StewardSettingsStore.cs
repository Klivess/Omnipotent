using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Storage for per-Steward settings (Demiurge's own setting system, not OmniSettings). One
    /// small JSON doc per Steward, atomic rewrite — same durability shape as the ledger/vault.
    ///
    /// SYSTEM DEFAULTS: a single `_system-defaults.json` holds the values NEW Stewards inherit,
    /// editable by Klives. It layers over the hardcoded <see cref="StewardSettings.Defaults"/>
    /// (which remain the ultimate fallback). A Steward's settings are seeded from the system
    /// defaults at creation and are independent thereafter — editing the system defaults never
    /// retroactively changes an existing Steward.
    ///
    /// Layout: Demiurge/Settings/&lt;stewardID&gt;.settings.json  +  Demiurge/Settings/_system-defaults.json
    ///
    /// Reads note a <see cref="CacheDeps"/> version and writes bump it, so the KliveAPI response
    /// cache cannot serve a settings GET that a save has superseded. This is not optional: every
    /// /demiurge/* GET resolves its Steward through StewardStore, which notes `demiurge:index` —
    /// that lone dependency makes the response cacheable, so an uninstrumented settings read would
    /// be pinned to a version no settings save ever bumps and served stale indefinitely.
    /// </summary>
    public class StewardSettingsStore
    {
        private readonly string dir;
        private readonly string systemDefaultsPath;
        private readonly object systemLock = new();
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public StewardSettingsStore()
        {
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "Settings");
            Directory.CreateDirectory(dir);
            systemDefaultsPath = Path.Combine(dir, "_system-defaults.json");
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string PathFor(string stewardID) => Path.Combine(dir, stewardID + ".settings.json");

        // A Steward with no file yet still has a version (0) — noting it unconditionally is what
        // lets the first Save of a previously-inherited Steward invalidate the inherited response.
        private static string CacheKey(string stewardID) => "demiurge:settings:" + stewardID;
        private const string SystemCacheKey = "demiurge:settings:_system-defaults";

        // ── system defaults (what new Stewards inherit) ──

        /// <summary>The system-wide default settings new Stewards inherit. Falls back to the
        /// hardcoded <see cref="StewardSettings.Defaults"/> when no override file exists.</summary>
        public StewardSettings GetSystemDefaults()
        {
            CacheDeps.NoteRead(SystemCacheKey);
            lock (systemLock)
            {
                if (File.Exists(systemDefaultsPath))
                {
                    try
                    {
                        var s = JsonConvert.DeserializeObject<StewardSettings>(File.ReadAllText(systemDefaultsPath));
                        if (s != null) { s.StewardID = ""; s.NormalizeRoutes(); return s; }
                    }
                    catch { }
                }
                return new StewardSettings { StewardID = "" }; // hardcoded StewardSettings.Defaults
            }
        }

        public void SaveSystemDefaults(StewardSettings defaults)
        {
            lock (systemLock)
            {
                defaults.StewardID = "";
                defaults.NormalizeRoutes();
                defaults.UpdatedAt = DateTime.UtcNow;
                string tmp = systemDefaultsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                File.Move(tmp, systemDefaultsPath, overwrite: true);
                CacheDeps.Bump(SystemCacheKey);
            }
        }

        // ── per-Steward settings ──

        public StewardSettings Get(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID))
            {
                string path = PathFor(stewardID);
                if (File.Exists(path))
                {
                    try
                    {
                        var s = JsonConvert.DeserializeObject<StewardSettings>(File.ReadAllText(path));
                        if (s != null) { s.StewardID = stewardID; s.NormalizeRoutes(); return s; }
                    }
                    catch { }
                }
            }
            // No per-Steward file yet — inherit the system defaults (stamped with this Steward's ID).
            var seeded = GetSystemDefaults();
            seeded.StewardID = stewardID;
            return seeded;
        }

        public void Save(StewardSettings settings)
        {
            lock (LockFor(settings.StewardID))
            {
                settings.NormalizeRoutes();
                settings.UpdatedAt = DateTime.UtcNow;
                string path = PathFor(settings.StewardID);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
                File.Move(tmp, path, overwrite: true);
                CacheDeps.Bump(CacheKey(settings.StewardID));
            }
        }

        /// <summary>Seeds a Steward's settings from the system defaults at creation (idempotent).</summary>
        public StewardSettings EnsureCreated(string stewardID)
        {
            var s = Get(stewardID); // inherits system defaults when no file exists yet
            Save(s);
            return s;
        }
    }
}
