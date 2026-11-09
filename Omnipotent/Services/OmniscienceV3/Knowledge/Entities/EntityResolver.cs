using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Entities
{
    /// <summary>
    /// Things people own or care about (pets, devices, vehicles, games, projects), from verified claims that carry an entity
    /// (replaces v2's entity handling, F225/F226). The owner is the claim's subject, never the speaker (a sister's dog is the
    /// sister's dog even when the subject talks about it); duplicates are resolved by name across kinds (a kind disagreement
    /// no longer splits one cat into two entities) and descriptors are structured key/value attributes, merged across
    /// claims with the most recent value winning per key.
    /// </summary>
    public sealed class EntityResolver
    {
        private readonly KnowledgeKit kit;

        public EntityResolver(KnowledgeKit kit) => this.kit = kit;

        public async Task<int> RebuildAsync(string personId, IReadOnlyList<VerifiedClaim> claims, long sourceSeq, CancellationToken ct)
        {
            string canonical = kit.Corpus.Canonical(personId);
            var withEntity = claims.Where(c => c.Entity?.Name is { Length: > 0 } && c.SubjectPersonId == canonical && c.Polarity == Polarity.Affirmed).ToList();
            var clusters = new List<List<VerifiedClaim>>();
            foreach (var c in withEntity.OrderBy(c => c.AssertedAtMs))
            {
                string name = NaturalKeys.Normalize(c.Entity!.Name);
                var target = clusters.FirstOrDefault(cl => cl.Any(x => NaturalKeys.Normalize(x.Entity!.Name) == name
                                                                        || (name.Length >= 4 && TextMatch.BestSubstringSimilarity(name, NaturalKeys.Normalize(x.Entity!.Name)) >= 0.9
                                                                            && Math.Abs(name.Length - NaturalKeys.Normalize(x.Entity!.Name).Length) <= 2)));
                if (target == null) clusters.Add(new List<VerifiedClaim> { c });
                else target.Add(c);
            }
            long now = kit.Context.Clock.NowMs;
            var liveIds = new List<string>();
            foreach (var cluster in clusters)
            {
                var name = cluster.GroupBy(c => c.Entity!.Name!.Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                string kind = cluster.Select(c => (c.Entity!.Kind ?? "other").Trim().ToLowerInvariant()).GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                var descriptors = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var c in cluster.OrderBy(c => c.AssertedAtMs))
                    foreach (var (k, v) in c.Entity!.Descriptors ?? new Dictionary<string, string>())
                        if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v)) descriptors[k.Trim().ToLowerInvariant()] = v.Trim();
                var evidence = cluster.SelectMany(c => c.Evidence).GroupBy(e => e.MessageId).Select(g => g.First())
                    .Select(e => new { e.MessageId, e.Version, e.Quote, e.SentAtMs }).OrderBy(e => e.SentAtMs).ToList();
                string id = NaturalKeys.Id("en", canonical + "|" + NaturalKeys.Normalize(name), 20);
                liveIds.Add(id);
                await kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                    INSERT INTO kn_entities(entity_id, owner_person_id, kind, name, name_norm, attributes_json, first_evidence_ms, last_evidence_ms, mentions, evidence_json,
                        status, updated_at_ms, source_seq)
                    VALUES($id,$o,$k,$n,$nn,$a,$f,$l,$m,$e,'active',$now,$seq)
                    ON CONFLICT(entity_id) DO UPDATE SET kind=excluded.kind, name=excluded.name, attributes_json=excluded.attributes_json, first_evidence_ms=excluded.first_evidence_ms,
                        last_evidence_ms=excluded.last_evidence_ms, mentions=excluded.mentions, evidence_json=excluded.evidence_json, status='active',
                        updated_at_ms=excluded.updated_at_ms, source_seq=excluded.source_seq
                    """, ("$id", id), ("$o", canonical), ("$k", kind), ("$n", name), ("$nn", NaturalKeys.Normalize(name)),
                    ("$a", JsonSerializer.Serialize(descriptors, KernelJson.Options)), ("$f", evidence.Min(e => e.SentAtMs)), ("$l", evidence.Max(e => e.SentAtMs)),
                    ("$m", evidence.Count), ("$e", JsonSerializer.Serialize(evidence.Take(50), KernelJson.Options)), ("$now", now), ("$seq", sourceSeq)), ct).ConfigureAwait(false);
            }
            // Entities whose evidence is gone (edited, deleted, merged away) are retracted, not deleted.
            await kit.Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var existing = c.Query(tx, "SELECT entity_id FROM kn_entities WHERE owner_person_id=$o AND status='active'", r => r.GetString(0), ("$o", canonical));
                foreach (var stale in existing.Except(liveIds, StringComparer.Ordinal))
                    c.Exec(tx, "UPDATE kn_entities SET status='retracted', updated_at_ms=$n WHERE entity_id=$i", ("$n", now), ("$i", stale));
            }, ct).ConfigureAwait(false);
            return clusters.Count;
        }
    }
}
