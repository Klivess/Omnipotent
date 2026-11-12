using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules
{
    /// <summary>
    /// A person's time zone at a moment, from the rollups' month-by-month zone estimates (plan T4: zones are facts that
    /// change over time). Null when it is not known, so a caller stamps the time as "author zone unknown" instead of guessing.
    /// One instance per unit of work: it caches what it reads.
    /// </summary>
    public sealed class PersonZones
    {
        private readonly OmniscienceStores stores;
        private readonly IPolicyOracle policy;
        private readonly Dictionary<string, string> canonical = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Person, string Month), TimeZoneInfo?> zones = new();
        private readonly bool hasZones;

        public PersonZones(OmniscienceStores stores, IPolicyOracle policy)
        {
            this.stores = stores;
            this.policy = policy;
            using var c = stores.Intel.OpenRead();
            hasZones = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_zones'") > 0;
        }

        public TimeZoneInfo? At(string? person, long atMs)
        {
            if (!hasZones || person == null) return null;
            if (!canonical.TryGetValue(person, out var id)) canonical[person] = id = policy.Canonical(person);
            string month = RollupModule.Month(DateOnly.FromDateTime(Instant.ToUtcDateTime(atMs)));
            if (zones.TryGetValue((id, month), out var known)) return known;
            using var c = stores.Intel.OpenRead();
            string? name = c.Scalar<string>(null, "SELECT zone FROM rollup_zones WHERE person_id=$p AND month=$m AND zone IS NOT NULL", ("$p", id), ("$m", month));
            return zones[(id, month)] = name != null && Zones.TryGet(name, out var found) ? found : null;
        }
    }
}
