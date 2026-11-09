using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Entities
{
    public static class AliasStatus
    {
        public const string Candidate = "candidate";
        public const string Accepted = "accepted";
        public const string Confirmed = "confirmed";
        public const string Rejected = "rejected";
    }

    public sealed record AliasRow(string AliasId, string PersonId, string Alias, string NameKind, string ScopeKind, string? ScopeId, long ValidFromMs, long? ValidToMs,
        int Usages, int Speakers, int SelfClaims, double Probability, string Status, bool CommonWord);

    /// <summary>
    /// Aliases (plan "Aliases/entities/relations"; replaces v2's AliasResolver, F248–F255). Names come from verified name
    /// claims (nickname, real name) whose kind the model judged (real name vs nickname vs epithet like "bro"/"mate", F199/F249)
    /// and whose sincerity the verifier checked ("I'm Batman" never becomes a name, F250). Each alias is scoped to the
    /// community it was used in (a server, or the DM/group chat) and to the period of use (validity interval, F255); counts
    /// are metadata on a stable key, never text in the fact (F248); distinct speakers are a COUNT DISTINCT across all usage
    /// types (F251); a name that merely echoes any handle or display name the person ever had is not an alias (F252); a
    /// word common in the corpus needs at least two independent speakers. An alias feeds alerts only once Klives confirms it
    /// (F254); accepted aliases raise a review item for that.
    /// </summary>
    public sealed class AliasEngine
    {
        private static readonly HashSet<string> NameAttributes = new(StringComparer.Ordinal) { "nickname", "real_name", "full_name", "surname" };

        private readonly KnowledgeKit kit;
        private readonly ReviewQueue review;

        public AliasEngine(KnowledgeKit kit, ReviewQueue review)
        {
            this.kit = kit;
            this.review = review;
        }

        public async Task<int> RebuildAsync(string personId, IReadOnlyList<VerifiedClaim> claims, long sourceSeq, CancellationToken ct)
        {
            string canonical = kit.Corpus.Canonical(personId);
            long now = kit.Context.Clock.NowMs;
            var band = kit.Context.Policy.BandOf(canonical, now);
            var ids = kit.Corpus.PersonIdsOf(canonical);
            var identities = kit.Corpus.IdentitiesOf(ids).Select(i => i.IdentityId).ToList();
            var known = kit.Corpus.NamesOf(identities).Select(n => NaturalKeys.Normalize(n.Value))
                .Concat(kit.Corpus.IdentitiesOf(ids).Select(i => NaturalKeys.Normalize(i.PlatformUserId))).ToHashSet(StringComparer.Ordinal);
            var gold = kit.Gold.Active(GoldKinds.Alias);
            var rows = new List<(AliasRow Row, IReadOnlyList<object> Evidence)>();
            foreach (var claim in claims.Where(c => NameAttributes.Contains(c.Attribute) && c.Polarity == Polarity.Affirmed && c.SubjectPersonId == canonical))
            {
                if (claim.NameKind is "epithet" or "title" or "none") continue;
                if (Attributes.IsBlockedFor(claim.Attribute, band)) continue; // real names of minors are never stored
                if (known.Contains(claim.ValueNorm)) continue; // an echo of a handle or display name, not an alias
                if (claim.ValueNorm.Length < 2 || claim.ValueNorm.Length > 40) continue;
                foreach (var scope in claim.Evidence.GroupBy(e => e.GuildId != null ? ("guild", e.GuildId) : ("conversation", e.ConversationId)))
                {
                    var existing = rows.FindIndex(r => r.Row.PersonId == canonical && NaturalKeys.Normalize(r.Row.Alias) == claim.ValueNorm
                                                       && r.Row.ScopeKind == scope.Key.Item1 && r.Row.ScopeId == scope.Key.Item2);
                    var evidence = scope.Select(e => (object)new { e.MessageId, e.Version, e.Quote, e.AuthorPersonId, e.SentAtMs }).ToList();
                    if (existing < 0)
                    {
                        rows.Add((new AliasRow("", canonical, claim.ValueText, claim.NameKind ?? (claim.Attribute == "nickname" ? "nickname" : "real_name"), scope.Key.Item1, scope.Key.Item2,
                            scope.Min(e => e.SentAtMs), scope.Max(e => e.SentAtMs), 0, 0, 0, claim.Probability, AliasStatus.Candidate, false), evidence));
                    }
                    else
                    {
                        var (row, ev) = rows[existing];
                        rows[existing] = (row with
                        {
                            ValidFromMs = Math.Min(row.ValidFromMs, scope.Min(e => e.SentAtMs)),
                            ValidToMs = Math.Max(row.ValidToMs ?? 0, scope.Max(e => e.SentAtMs)),
                            Probability = Math.Max(row.Probability, claim.Probability),
                        }, ev.Concat(evidence).ToList());
                    }
                }
            }
            int written = 0;
            foreach (var (row, evidence) in rows)
            {
                var ev = evidence.Select(e => JsonSerializer.SerializeToElement(e, KernelJson.Options)).ToList();
                int usages = ev.Select(e => e.GetProperty("messageId").GetString()).Distinct(StringComparer.Ordinal).Count();
                var speakers = ev.Select(e => e.TryGetProperty("authorPersonId", out var a) ? a.GetString() : null).Where(a => a != null).Distinct(StringComparer.Ordinal).ToList();
                int self = speakers.Count(s => s == canonical);
                bool common = IsCommonWord(row.Alias);
                // Acceptance: two independent speakers, or a sincere self-claim of a non-common word; real names need the person
                // themselves or two other people.
                bool accepted = row.NameKind == "real_name"
                    ? self >= 1 || speakers.Count(s => s != canonical) >= 2
                    : common ? speakers.Count >= 2 && usages >= 2 : speakers.Count >= 2 || (self >= 1 && usages >= 1);
                string key = NaturalKeys.Canonical(("a", NaturalKeys.Normalize(row.Alias)), ("s", "person:" + canonical), ("scope", row.ScopeKind + ":" + row.ScopeId));
                string status = gold.TryGetValue(key, out var label)
                    ? label.Verdict == GoldVerdicts.Reject ? AliasStatus.Rejected : AliasStatus.Confirmed
                    : accepted && row.Probability >= 0.6 ? AliasStatus.Accepted : AliasStatus.Candidate;
                // A name not used for over a year has ended in that community (aliases are period-scoped).
                long? validTo = row.ValidToMs is long last && now - last > (long)TimeSpan.FromDays(365).TotalMilliseconds ? last : null;
                string id = NaturalKeys.Id("al", key, 20);
                double probability = Math.Round(Math.Min(0.99, row.Probability * (accepted ? 1 : 0.7)), 4);
                await kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                    INSERT INTO kn_aliases(alias_id, person_id, alias, alias_norm, name_kind, scope_kind, scope_id, valid_from_ms, valid_to_ms, first_seen_ms, last_seen_ms,
                        usages, speakers, self_claims, probability, status, evidence_json, common_word, updated_at_ms, source_seq)
                    VALUES($id,$p,$a,$n,$k,$sk,$si,$vf,$vt,$f,$l,$u,$s,$self,$prob,$st,$ev,$cw,$now,$seq)
                    ON CONFLICT(alias_id) DO UPDATE SET alias=excluded.alias, name_kind=excluded.name_kind, valid_from_ms=excluded.valid_from_ms, valid_to_ms=excluded.valid_to_ms,
                        first_seen_ms=excluded.first_seen_ms, last_seen_ms=excluded.last_seen_ms, usages=excluded.usages, speakers=excluded.speakers,
                        self_claims=excluded.self_claims, probability=excluded.probability, status=excluded.status, evidence_json=excluded.evidence_json,
                        common_word=excluded.common_word, updated_at_ms=excluded.updated_at_ms, source_seq=excluded.source_seq
                    """, ("$id", id), ("$p", canonical), ("$a", row.Alias), ("$n", NaturalKeys.Normalize(row.Alias)), ("$k", row.NameKind), ("$sk", row.ScopeKind),
                    ("$si", row.ScopeId), ("$vf", row.ValidFromMs), ("$vt", validTo), ("$f", row.ValidFromMs), ("$l", row.ValidToMs ?? row.ValidFromMs), ("$u", usages),
                    ("$s", speakers.Count), ("$self", self), ("$prob", probability), ("$st", status), ("$ev", JsonSerializer.Serialize(ev.Take(50), KernelJson.Options)),
                    ("$cw", common ? 1 : 0), ("$now", now), ("$seq", sourceSeq)), ct).ConfigureAwait(false);
                if (status == AliasStatus.Accepted)
                    await review.RaiseAsync(ReviewKinds.Alias, canonical, key, $"Confirm alias \"{row.Alias}\" ({row.NameKind}) before it is used for alerts",
                        new { aliasId = id, alias = row.Alias, scope = row.ScopeKind + ":" + row.ScopeId, usages, speakers = speakers.Count }, 0.3, sourceSeq, ct).ConfigureAwait(false);
                written++;
            }
            if (MinorGuard.IsRestricted(band))
            {
                // Real names of a minor never stay stored, whenever the band became restricted.
                await kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM kn_aliases WHERE person_id=$p AND name_kind='real_name'", ("$p", canonical)), ct).ConfigureAwait(false);
            }
            return written;
        }

        /// <summary>How common a word is in the corpus (exact arithmetic over the full-text index): appearing in more than one in
        /// a thousand messages makes it a common word, which needs two independent speakers before it counts as an alias.</summary>
        private bool IsCommonWord(string alias)
        {
            string? fts = Retrieval.HybridRetriever.FtsQuery("\"" + alias + "\"");
            if (fts == null) return false;
            using var c = kit.Context.Stores.Corpus.OpenRead();
            long total = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            if (total < 1_000) return false;
            try
            {
                long hits = c.Scalar<long>(null, "SELECT COUNT(*) FROM (SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q LIMIT $l)", ("$q", fts), ("$l", total / 1_000 + 1));
                return hits > total / 1_000;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return false;
            }
        }

        /// <summary>Aliases of a person valid at <paramref name="atMs"/> in a scope (null scope = any), by status.</summary>
        public IReadOnlyList<AliasRow> Aliases(string personId, long atMs, string? guildId = null, bool confirmedOnly = false)
        {
            string canonical = kit.Corpus.Canonical(personId);
            using var c = kit.Context.Stores.Intel.OpenRead();
            return c.Query(null, """
                    SELECT alias_id, person_id, alias, name_kind, scope_kind, scope_id, valid_from_ms, valid_to_ms, usages, speakers, self_claims, probability, status, common_word
                    FROM kn_aliases WHERE person_id=$p
                    """, r => new AliasRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64(6),
                    r.GetInt64OrNull(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetDouble(11), r.GetString(12), r.GetInt32(13) == 1), ("$p", canonical))
                .Where(a => a.Status != AliasStatus.Rejected && (!confirmedOnly || a.Status == AliasStatus.Confirmed))
                .Where(a => a.ValidFromMs <= atMs + (long)TimeSpan.FromDays(30).TotalMilliseconds && (a.ValidToMs == null || a.ValidToMs >= atMs - (long)TimeSpan.FromDays(30).TotalMilliseconds))
                .Where(a => guildId == null || a.ScopeKind != "guild" || a.ScopeId == guildId)
                .ToList();
        }
    }
}
