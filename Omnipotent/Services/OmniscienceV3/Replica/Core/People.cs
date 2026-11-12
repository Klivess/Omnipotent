using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>A person as the replica sees them: the canonical id, every merged member id and every identity.</summary>
    public sealed record PersonScope(string CanonicalId, IReadOnlyList<string> MemberIds, IReadOnlyList<string> IdentityIds, string DisplayName, bool IsSelf);

    /// <summary>
    /// Person and identity resolution over corpus.db: merges are resolved through <c>person_canonical</c> (a merged
    /// person is one replica, never two), identities map to canonical persons, Klives's own identities form the "self"
    /// set (plan R5), and names are read as they were at a message's time (plan T5: "Bob" in 2021 is whoever was Bob then).
    /// </summary>
    public static class People
    {
        public static string Canonical(SqliteConnection corpus, string personId) =>
            corpus.Scalar<string>(null, "SELECT canonical_id FROM person_canonical WHERE person_id=$p", ("$p", personId)) ?? personId;

        public static PersonScope? Resolve(SqliteConnection corpus, string personId)
        {
            string canonical = Canonical(corpus, personId);
            var members = corpus.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$c", r => r.GetString(0), ("$c", canonical))
                .Append(canonical).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var persons = corpus.QueryIn(null, "SELECT person_id, display_name, is_self FROM persons WHERE person_id IN ({keys})", members,
                r => (Id: r.GetString(0), Name: r.GetStringOrNull(1), Self: r.GetInt32(2) == 1));
            if (persons.Count == 0) return null;
            var identities = corpus.QueryIn(null, "SELECT identity_id FROM identities WHERE person_id IN ({keys})", members, r => r.GetString(0))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string name = persons.FirstOrDefault(p => p.Id == canonical).Name ?? persons.Select(p => p.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? canonical;
            return new PersonScope(canonical, members, identities, name, persons.Any(p => p.Self));
        }

        /// <summary>Canonical ids of every person Klives marked as himself (his own accounts).</summary>
        public static IReadOnlyList<string> SelfPersons(SqliteConnection corpus) =>
            corpus.Query(null, "SELECT p.person_id, COALESCE(c.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical c ON c.person_id=p.person_id WHERE p.is_self=1",
                r => r.GetString(1)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        /// <summary>
        /// The earliest instant from which the person is no longer (likely) a minor, searched over [<paramref name="fromMs"/>,
        /// <paramref name="toMs"/>]. Returns (restrictedNow, adultFromMs): adultFromMs is null when nothing in the range is
        /// restricted. MinorGuard bands only get less restrictive as time passes for a fixed birth interval, so a binary
        /// search to one-day precision is exact enough.
        /// </summary>
        public static (bool RestrictedAtEnd, long? AdultFromMs) AdultFrom(IPolicyOracle policy, string personId, long fromMs, long toMs)
        {
            if (MinorGuard.IsRestricted(policy.BandOf(personId, toMs))) return (true, null);
            if (fromMs >= toMs || !MinorGuard.IsRestricted(policy.BandOf(personId, fromMs))) return (false, null);
            long lo = fromMs, hi = toMs;
            const long Day = 86_400_000L;
            while (hi - lo > Day)
            {
                long mid = lo + (hi - lo) / 2;
                if (MinorGuard.IsRestricted(policy.BandOf(personId, mid))) lo = mid;
                else hi = mid;
            }
            return (false, hi);
        }
    }

    /// <summary>Identity → canonical person (+ self/bot flags), loaded in batches and cached for one job.</summary>
    public sealed class IdentityDirectory
    {
        public sealed record Entry(string IdentityId, string PersonId, bool IsSelf, bool IsBot);

        private readonly SqliteConnection corpus;
        private readonly Dictionary<string, Entry?> cache = new(StringComparer.Ordinal);

        public IdentityDirectory(SqliteConnection corpus) => this.corpus = corpus;

        public Entry? Get(string identityId)
        {
            if (cache.TryGetValue(identityId, out var hit)) return hit;
            Load(new[] { identityId });
            return cache.GetValueOrDefault(identityId);
        }

        public void Load(IEnumerable<string> identityIds)
        {
            var missing = identityIds.Where(i => !cache.ContainsKey(i)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count == 0) return;
            var rows = corpus.QueryIn(null, """
                SELECT i.identity_id, COALESCE(c.canonical_id, i.person_id), COALESCE(p.is_self, 0), i.kind
                FROM identities i LEFT JOIN person_canonical c ON c.person_id=i.person_id LEFT JOIN persons p ON p.person_id=i.person_id
                WHERE i.identity_id IN ({keys})
                """, missing, r => new Entry(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, string.Equals(r.GetStringOrNull(3), "bot", StringComparison.OrdinalIgnoreCase)));
            foreach (var m in missing) cache[m] = null;
            foreach (var e in rows) cache[e.IdentityId] = e;
            // A person is "self" when any of their merged members is self.
            var selfCanonical = SelfCanonicals();
            foreach (var key in cache.Keys.ToList())
                if (cache[key] is { } e && !e.IsSelf && selfCanonical.Contains(e.PersonId)) cache[key] = e with { IsSelf = true };
        }

        private HashSet<string>? selfCanonicals;
        private HashSet<string> SelfCanonicals() => selfCanonicals ??= People.SelfPersons(corpus).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Display names with validity intervals (plan T5), resolved at a message's time. Preference: display name, then
    /// nickname, then global name, then username; a bio is never a name.
    /// </summary>
    public sealed class NameBook
    {
        private static readonly string[] Preference = { "display", "nick", "nickname", "global", "global_name", "username" };
        private readonly SqliteConnection corpus;
        private readonly Dictionary<string, List<(string Kind, string Value, long From, long? To)>> names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> personNames = new(StringComparer.Ordinal);

        public NameBook(SqliteConnection corpus) => this.corpus = corpus;

        public void Load(IEnumerable<string> identityIds)
        {
            var missing = identityIds.Where(i => !names.ContainsKey(i)).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count == 0) return;
            foreach (var m in missing) names[m] = new();
            foreach (var row in corpus.QueryIn(null, "SELECT identity_id, name_kind, value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id IN ({keys})", missing,
                         r => (Id: r.GetString(0), Kind: r.GetString(1), Value: r.GetString(2), From: r.GetInt64(3), To: r.GetInt64OrNull(4))))
            {
                if (Array.IndexOf(Preference, row.Kind.ToLowerInvariant()) < 0 || string.IsNullOrWhiteSpace(row.Value)) continue;
                names[row.Id].Add((row.Kind.ToLowerInvariant(), row.Value.Trim(), row.From, row.To));
            }
        }

        /// <summary>
        /// The name an identity carried at <paramref name="atMs"/>. When none was valid at that instant, the most recent
        /// name that had already been used before it; never a name first seen later (that would leak post-cutoff data
        /// into an as-of prompt). Null when nothing is known yet.
        /// </summary>
        public string? NameAt(string identityId, long atMs)
        {
            if (!names.TryGetValue(identityId, out var list)) { Load(new[] { identityId }); list = names[identityId]; }
            if (list.Count == 0) return null;
            foreach (var kind in Preference)
            {
                var valid = list.Where(n => n.Kind == kind && n.From <= atMs && (n.To == null || n.To > atMs)).OrderByDescending(n => n.From).FirstOrDefault();
                if (valid.Value != null) return valid.Value;
            }
            var earlier = list.Where(n => n.From <= atMs).OrderBy(n => Array.IndexOf(Preference, n.Kind)).ThenByDescending(n => n.From).FirstOrDefault();
            return earlier.Value;
        }

        public string PersonName(string personId)
        {
            if (personNames.TryGetValue(personId, out var hit)) return hit ?? "someone";
            string? name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", personId));
            personNames[personId] = name;
            return name ?? "someone";
        }
    }

    /// <summary>
    /// The IANA zone a person was in at an instant (plan T4), used for their local time of day in prompts and for
    /// local-month era bucketing. Sources, in order: a time-zone assertion valid at that instant (the knowledge
    /// workstream records zones as state attributes), Klives's own zone for the self person, and otherwise unknown —
    /// the UI and prompts then fall back to UTC rather than guessing (plan R4).
    /// </summary>
    public sealed class PersonZones
    {
        private readonly Storage.OmniscienceStores stores;
        private readonly Func<TimeZoneInfo> klivesZone;

        public PersonZones(Storage.OmniscienceStores stores, Func<TimeZoneInfo> klivesZone)
        {
            this.stores = stores;
            this.klivesZone = klivesZone;
        }

        public (TimeZoneInfo? Zone, string Source) ZoneAt(string personId, bool isSelf, long atMs)
        {
            using (var intel = stores.Intel.OpenRead())
            {
                var rows = intel.Query(null, """
                    SELECT value_text, value_json FROM assertions
                    WHERE subject_kind='person' AND subject_id=$p AND attribute IN ('time_zone','timezone','tz')
                      AND retracted_at_ms IS NULL AND superseded_at_ms IS NULL AND (asserted_at_ms IS NULL OR asserted_at_ms <= $t)
                      AND (valid_from_earliest_ms IS NULL OR valid_from_earliest_ms <= $t)
                      AND (valid_to_latest_ms IS NULL OR valid_to_latest_ms > $t)
                      AND status NOT IN ('rejected','superseded')
                    ORDER BY COALESCE(valid_from_latest_ms, 0) DESC LIMIT 3
                    """, r => r.GetStringOrNull(0) ?? r.GetStringOrNull(1), ("$p", personId), ("$t", atMs));
                foreach (var raw in rows)
                {
                    string candidate = (raw ?? "").Trim().Trim('"');
                    if (Zones.TryGet(candidate, out var z)) return (z, "knowledge");
                }
            }
            if (isSelf) return (klivesZone(), "klives-setting");
            return (null, "unknown");
        }
    }
}
