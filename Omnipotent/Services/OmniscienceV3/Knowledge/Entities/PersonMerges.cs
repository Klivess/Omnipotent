using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Entities
{
    public sealed record MergeOutcome(long MergeId, string Survivor, string Absorbed, bool Changed, string Reason);

    public sealed record MergeRow(long MergeId, string FromPersonId, string IntoPersonId, long MergedAtMs, string Actor, string? Reason, long? UndoneAtMs);

    /// <summary>
    /// Person merges and unmerges (plan R3: a merge log plus a canonical-id map in corpus.db; derived stores re-derive the
    /// affected people and everything joins through the map, so a merge never rewrites history). Fixes F264/F550/F122:
    /// <list type="bullet">
    /// <item>the survivor is chosen by fixed rules, never arbitrarily: Klives's own person, then a tracked person, then the one
    /// with more identities, then the older, then the smaller id;</item>
    /// <item>every person-keyed derived row re-attributes through the canonical map and the change log (person.merge /
    /// person.unmerge), which re-consolidates both people;</item>
    /// <item>merges need an explicit confirmation from the caller and can always be undone: unmerge replays the remaining
    /// merge log for the affected people.</item>
    /// </list>
    /// </summary>
    public sealed class PersonMerges
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly CorpusReader corpus;

        public PersonMerges(OmniscienceStores stores, IClock clock, CorpusReader corpus)
        {
            this.stores = stores;
            this.clock = clock;
            this.corpus = corpus;
        }

        public (string Survivor, string Absorbed, string Reason) ChooseSurvivor(string personA, string personB)
        {
            string a = corpus.Canonical(personA), b = corpus.Canonical(personB);
            var candidates = new[] { a, b }.Select(p => (Id: p, Self: corpus.IsSelf(p), Tracked: corpus.TierOf(p) == "tracked",
                Identities: corpus.IdentitiesOf(corpus.PersonIdsOf(p)).Count, Created: Created(p))).ToList();
            var ordered = candidates
                .OrderByDescending(x => x.Self).ThenByDescending(x => x.Tracked).ThenByDescending(x => x.Identities).ThenBy(x => x.Created).ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList();
            var s = ordered[0];
            var o = ordered[1];
            string reason = s.Self != o.Self ? "Klives's own person survives"
                : s.Tracked != o.Tracked ? "the tracked person survives"
                : s.Identities != o.Identities ? "the person with more identities survives"
                : s.Created != o.Created ? "the older person survives"
                : "ids compared";
            return (s.Id, o.Id, reason);
        }

        private long Created(string person)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<long?>(null, "SELECT created_at_ms FROM persons WHERE person_id=$p", ("$p", person)) ?? long.MaxValue;
        }

        public async Task<MergeOutcome> MergeAsync(string personA, string personB, string actor, string reason, CancellationToken ct)
        {
            string a = corpus.Canonical(personA), b = corpus.Canonical(personB);
            if (a == b) return new MergeOutcome(0, a, b, false, "Already the same person.");
            var (survivor, absorbed, rule) = ChooseSurvivor(a, b);
            long now = clock.NowMs;
            long id = await stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO person_merges(from_person_id, into_person_id, merged_at_ms, actor, reason) VALUES($f,$i,$t,$a,$r)",
                    ("$f", absorbed), ("$i", survivor), ("$t", now), ("$a", actor), ("$r", reason + " (" + rule + ")"));
                long mergeId = c.Scalar<long>(tx, "SELECT last_insert_rowid()");
                c.Exec(tx, "UPDATE person_canonical SET canonical_id=$s WHERE canonical_id=$x", ("$s", survivor), ("$x", absorbed));
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($x,$s) ON CONFLICT(person_id) DO UPDATE SET canonical_id=excluded.canonical_id",
                    ("$x", absorbed), ("$s", survivor));
                c.Exec(tx, "INSERT OR IGNORE INTO person_canonical(person_id, canonical_id) VALUES($s,$s)", ("$s", survivor));
                ChangeLog.Append(c, tx, now, ChangeKinds.PersonMerge, absorbed, survivor, null, SourceKinds.Operator);
                return mergeId;
            }, ct).ConfigureAwait(false);
            await AuditAsync(actor, "person.merge", absorbed, new { mergeId = id, survivor, absorbed, rule, reason }, ct).ConfigureAwait(false);
            return new MergeOutcome(id, survivor, absorbed, true, rule);
        }

        /// <summary>Undoes one merge and recomputes the canonical map of everyone in the merged component by replaying the
        /// merges that remain, in their original order.</summary>
        public async Task<bool> UnmergeAsync(long mergeId, string actor, CancellationToken ct)
        {
            long now = clock.NowMs;
            var undone = await stores.Corpus.WriteAsync<MergeRow?>((c, tx) =>
            {
                var row = c.Query(tx, "SELECT merge_id, from_person_id, into_person_id, merged_at_ms, actor, reason, undone_at_ms FROM person_merges WHERE merge_id=$m",
                    Map, ("$m", mergeId)).FirstOrDefault();
                if (row == null || row.UndoneAtMs != null) return null;
                c.Exec(tx, "UPDATE person_merges SET undone_at_ms=$t WHERE merge_id=$m", ("$t", now), ("$m", mergeId));
                string root = c.Scalar<string>(tx, "SELECT canonical_id FROM person_canonical WHERE person_id=$p", ("$p", row.IntoPersonId)) ?? row.IntoPersonId;
                var component = c.Query(tx, "SELECT person_id FROM person_canonical WHERE canonical_id=$r", r => r.GetString(0), ("$r", root))
                    .Append(root).Append(row.FromPersonId).Append(row.IntoPersonId).Distinct(StringComparer.Ordinal).ToList();
                var set = component.ToHashSet(StringComparer.Ordinal);
                var parent = component.ToDictionary(p => p, p => p, StringComparer.Ordinal);
                string Find(string p)
                {
                    while (parent[p] != p) p = parent[p];
                    return p;
                }
                foreach (var m in c.Query(tx, "SELECT merge_id, from_person_id, into_person_id, merged_at_ms, actor, reason, undone_at_ms FROM person_merges WHERE undone_at_ms IS NULL ORDER BY merged_at_ms, merge_id", Map))
                {
                    if (!set.Contains(m.FromPersonId) || !set.Contains(m.IntoPersonId)) continue;
                    string from = Find(m.FromPersonId), into = Find(m.IntoPersonId);
                    if (from != into) parent[from] = into;
                }
                foreach (var p in component)
                    c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$c) ON CONFLICT(person_id) DO UPDATE SET canonical_id=excluded.canonical_id",
                        ("$p", p), ("$c", Find(p)));
                ChangeLog.Append(c, tx, now, ChangeKinds.PersonUnmerge, row.FromPersonId, row.IntoPersonId, null, SourceKinds.Operator);
                return row;
            }, ct).ConfigureAwait(false);
            if (undone == null) return false;
            await AuditAsync(actor, "person.unmerge", undone.FromPersonId, new { mergeId, from = undone.FromPersonId, into = undone.IntoPersonId }, ct).ConfigureAwait(false);
            return true;
        }

        public IReadOnlyList<MergeRow> History(string personId)
        {
            var ids = corpus.PersonIdsOf(personId);
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, "SELECT merge_id, from_person_id, into_person_id, merged_at_ms, actor, reason, undone_at_ms FROM person_merges WHERE from_person_id IN ({keys}) OR into_person_id IN ({keys})",
                ids, Map).GroupBy(m => m.MergeId).Select(g => g.First()).OrderBy(m => m.MergedAtMs).ToList();
        }

        private static MergeRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64OrNull(6));

        private Task AuditAsync(string actor, string action, string target, object detail, CancellationToken ct)
        {
            long now = clock.NowMs;
            return stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,$x,$g,$d)",
                ("$t", now), ("$a", actor), ("$x", action), ("$g", target), ("$d", JsonSerializer.Serialize(detail, KernelJson.Options))), ct);
        }
    }
}
