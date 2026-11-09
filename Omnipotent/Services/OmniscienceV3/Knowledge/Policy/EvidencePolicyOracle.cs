using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>
    /// The MinorGuard oracle the engine runs with (plan Guardrails; wraps the Phase 1 <see cref="OperatorPolicyOracle"/>).
    /// Klives's explicit mark always wins; on top of it, the age evidence the deduction pipeline found (time-adjusted
    /// birth intervals from age, school-year and birthday statements, minus those Klives rejected in review) can only make
    /// the band stricter, never looser. There is deliberately no "mark as adult" override: a misread age is corrected by
    /// rejecting that age statement, which recomputes the band.
    /// </summary>
    public sealed class EvidencePolicyOracle : IPolicyOracle
    {
        private readonly IPolicyOracle inner;
        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly ConcurrentDictionary<string, (IReadOnlyList<AgeEvidenceRow> Rows, long ReadAtMs)> cache = new(StringComparer.Ordinal);
        private long tableCheckedAtMs = long.MinValue;
        private bool tableExists;
        private static readonly long CacheMs = 30_000;

        public EvidencePolicyOracle(IPolicyOracle inner, OmniscienceStores stores, IClock clock)
        {
            this.inner = inner;
            this.stores = stores;
            this.clock = clock;
        }

        public string Canonical(string personId) => inner.Canonical(personId);

        public AgeBand BandOf(string personId, long atMs)
        {
            var marked = inner.BandOf(personId, atMs);
            if (marked == AgeBand.Minor) return AgeBand.Minor;
            var rows = EvidenceFor(Canonical(personId));
            return rows.Count == 0 ? marked : AgeBands.Stricter(marked, AgeBands.Evaluate(rows, atMs));
        }

        /// <summary>Drops the cached evidence of a person (called after the deduction module writes age evidence or a review
        /// decision changes it) and invalidates every policy-dependent cached response.</summary>
        public void Invalidate(string canonicalPersonId)
        {
            cache.TryRemove(canonicalPersonId, out _);
            ReadModelStore.PolicyChanged();
        }

        /// <summary>The person's unrejected age evidence (all merged ids), with Klives's rejections applied.</summary>
        public IReadOnlyList<AgeEvidenceRow> EvidenceFor(string canonicalPersonId)
        {
            long now = clock.NowMs;
            if (cache.TryGetValue(canonicalPersonId, out var hit) && now - hit.ReadAtMs < CacheMs) return hit.Rows;
            if (!TableExists(now)) return Array.Empty<AgeEvidenceRow>();
            List<string> ids;
            using (var c = stores.Corpus.OpenRead())
                ids = c.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$c", r => r.GetString(0), ("$c", canonicalPersonId));
            if (!ids.Contains(canonicalPersonId)) ids.Add(canonicalPersonId);
            List<AgeEvidenceRow> rows;
            using (var i = stores.Intel.OpenRead())
                rows = i.QueryIn(null, """
                    SELECT evidence_id, person_id, kind, birth_earliest_ms, birth_latest_ms, month, day, source_kind, asserted_at_ms, message_id, message_version,
                           claim_id, probability, status, natural_key
                    FROM kn_age_evidence WHERE person_id IN ({keys}) AND status IN ('active','inconsistent')
                    """, ids, Map);
            HashSet<string> rejected;
            using (var o = stores.Operator.OpenRead())
                rejected = o.Query(null, "SELECT natural_key FROM gold_labels WHERE kind='age_evidence' AND verdict='reject' AND superseded_at_ms IS NULL", r => r.GetString(0))
                    .ToHashSet(StringComparer.Ordinal);
            var result = rows.Where(r => !rejected.Contains(r.NaturalKey)).ToList();
            cache[canonicalPersonId] = (result, now);
            return result;
        }

        private bool TableExists(long now)
        {
            if (tableExists) return true;
            if (now - tableCheckedAtMs < CacheMs) return false;
            tableCheckedAtMs = now;
            using var c = stores.Intel.OpenRead();
            tableExists = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='kn_age_evidence'") > 0;
            return tableExists;
        }

        internal static AgeEvidenceRow Map(SqliteDataReader r)
        {
            long? e = r.GetInt64OrNull(3), l = r.GetInt64OrNull(4);
            return new AgeEvidenceRow(r.GetString(0), r.GetString(1), r.GetString(2), e != null && l != null ? new TimeInterval(e.Value, l.Value) : null,
                r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetString(7), r.GetInt64(8), r.GetString(9), r.GetInt32(10),
                r.GetStringOrNull(11), r.GetDouble(12), r.GetString(13), r.GetString(14));
        }
    }
}
