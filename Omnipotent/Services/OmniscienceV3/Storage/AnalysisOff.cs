using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>
    /// The people Omniscience must not work anything out about, and their accounts. Loaded once per job or request.
    /// </summary>
    public sealed record AnalysisOffSet(IReadOnlySet<string> Persons, IReadOnlySet<string> Identities)
    {
        public static readonly AnalysisOffSet None = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        public bool Any => Persons.Count > 0;

        public bool Person(string? personId) => personId != null && Persons.Contains(personId);

        public bool Author(string? identityId) => identityId != null && Identities.Contains(identityId);
    }

    /// <summary>
    /// "Forget what was worked out about this person" (N20, the lighter of the two forget levels): a standing mark in
    /// operator.db. While it stands, nothing is computed from what the person wrote and nothing computed about them is
    /// kept: no labels, no vectors, no statistics, no alerts, no summaries, no profile. Their messages stay in the
    /// archive, readable and searchable by their words.
    /// <para>
    /// The mark is on the person, and it covers everyone merged with them: a person is off when they, the person they were
    /// merged into, or anyone merged into the same person is marked. So a merge can never switch analysis back on.
    /// </para>
    /// Every module that derives something from messages asks <see cref="Load"/> first.
    /// </summary>
    public static class AnalysisOff
    {
        /// <summary>operator.db <c>dismissals.kind</c>; the key is a person id.</summary>
        public const string Kind = "analysis_off";

        private static long changes;

        /// <summary>
        /// Grows with every switch made in this process, so a reader that caches the set (the knowledge layer's) reloads at
        /// once instead of reading someone for a while after their analysis was switched off. <see cref="Set"/> and
        /// <see cref="Clear"/> count it inside their transaction; whoever commits one counts it again with
        /// <see cref="Changed"/>, so a reader that reloaded in between cannot keep the old set.
        /// </summary>
        public static long Changes => Interlocked.Read(ref changes);

        public static void Changed() => Interlocked.Increment(ref changes);

        public static AnalysisOffSet Load(OmniscienceStores stores)
        {
            List<string> marked;
            using (var op = stores.Operator.OpenRead())
                marked = op.Query(null, "SELECT key FROM dismissals WHERE kind=$k", r => r.GetString(0), ("$k", Kind));
            if (marked.Count == 0) return AnalysisOffSet.None;
            using var corpus = stores.Corpus.OpenRead();
            var persons = new HashSet<string>(marked, StringComparer.Ordinal);
            var roots = corpus.QueryIn(null, "SELECT canonical_id FROM person_canonical WHERE person_id IN ({keys})", marked, r => r.GetString(0)).Concat(marked).Distinct(StringComparer.Ordinal).ToList();
            persons.UnionWith(roots);
            persons.UnionWith(corpus.QueryIn(null, "SELECT person_id FROM person_canonical WHERE canonical_id IN ({keys})", roots, r => r.GetString(0)));
            var identities = corpus.QueryIn(null, "SELECT identity_id FROM identities WHERE person_id IN ({keys})", persons.ToList(), r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            return new AnalysisOffSet(persons, identities);
        }

        public static bool IsOff(OmniscienceStores stores, string personId) => Load(stores).Person(personId);

        /// <summary>The marks themselves (person id → when it was set), for the list Klives sees.</summary>
        public static List<(string Person, long AtMs)> Marks(SqliteStore operatorStore)
        {
            using var op = operatorStore.OpenRead();
            return op.Query(null, "SELECT key, at_ms FROM dismissals WHERE kind=$k ORDER BY at_ms DESC, key", r => (r.GetString(0), r.GetInt64(1)), ("$k", Kind));
        }

        public static void Set(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> persons, long nowMs)
        {
            foreach (string person in persons)
                c.Exec(tx, "INSERT OR IGNORE INTO dismissals(kind, key, until_ms, at_ms) VALUES($k,$p,NULL,$n)", ("$k", Kind), ("$p", person), ("$n", nowMs));
            Changed();
        }

        public static int Clear(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> persons)
        {
            int n = 0;
            foreach (string person in persons) n += c.Exec(tx, "DELETE FROM dismissals WHERE kind=$k AND key=$p", ("$k", Kind), ("$p", person));
            Changed();
            return n;
        }
    }
}
