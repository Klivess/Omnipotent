using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>What a reconcile changed. <see cref="ChangedKeys"/> drive projections (timeline, plans, read models).</summary>
    public sealed record ReconcileResult(int Inserted, int Superseded, int Retracted, int Unchanged, IReadOnlyList<string> ChangedKeys, IReadOnlyList<string> InsertedIds)
    {
        public bool Changed => Inserted + Superseded + Retracted > 0;
    }

    /// <summary>Filters for reading assertions; all optional, combined with AND.</summary>
    public sealed record AssertionQuery
    {
        /// <summary>Every person id that resolves to the subject (canonical id and merged-in ids).</summary>
        public IReadOnlyCollection<string>? SubjectIds { get; init; }
        public string SubjectKind { get; init; } = "person";
        public string? Attribute { get; init; }
        public string? Category { get; init; }
        public string? Producer { get; init; }
        public IReadOnlyCollection<string>? Statuses { get; init; }
        public KnowledgeMode Mode { get; init; } = KnowledgeMode.Current;
        public required long AtMs { get; init; }
        /// <summary>Only what had evidence by this instant (replica as-of, inquiry time scopes): no post-cutoff leakage.</summary>
        public long? EvidenceCutoffMs { get; init; }
        public bool IncludeInferred { get; init; } = true;
        public bool IncludeEvents { get; init; } = true;
        public double? MinProbability { get; init; }
        public int Limit { get; init; } = 500;
    }

    /// <summary>An assertion as seen at an instant: the stored row plus its read-time confidence.</summary>
    public sealed record AssertionView(AssertionRecord Record, AttributeDefinition Definition, double EffectiveProbability);

    /// <summary>
    /// The knowledge store (frozen contract R7 #6): bitemporal, append-only assertions with evidence. Producers describe
    /// what they want to be true for one subject; <see cref="ReconcileAsync"/> turns that into versions (unchanged rows
    /// stay, changed rows are superseded by a new version, vanished rows are retracted), so as-known-at queries can always
    /// reproduce what Omniscience believed at any instant (plan R3 "assertions are append-only"). Confidence decay is never
    /// stored: <see cref="Confidence.Effective"/> computes it at read time from the last corroboration and the as-of.
    /// </summary>
    public sealed class AssertionStore
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;

        public AssertionStore(OmniscienceStores stores, IClock clock)
        {
            this.stores = stores;
            this.clock = clock;
        }

        // ── writing ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Makes <paramref name="desired"/> the live set of <paramref name="producer"/>'s assertions about the subject.
        /// Rows of merged-away ids (<paramref name="subjectIds"/>) that the canonical drafts no longer contain are retracted,
        /// which re-attributes a merged person's knowledge to the survivor (F264). <paramref name="keepUnlisted"/> protects
        /// rows outside the producer's current scope (e.g. a partial re-verification) from retraction.
        /// </summary>
        public Task<ReconcileResult> ReconcileAsync(string moduleId, string producer, string subjectKind, IReadOnlyCollection<string> subjectIds,
            IReadOnlyList<AssertionDraft> desired, Func<string, bool>? keepUnlisted = null, CancellationToken ct = default)
        {
            foreach (var d in desired)
            {
                if (d.Producer != producer) throw new ArgumentException($"Draft for {d.Attribute} names producer '{d.Producer}', expected '{producer}'.");
                if (!(d.Probability >= 0 && d.Probability <= 1)) throw new ArgumentException($"Draft for {d.Attribute} has probability {d.Probability}.");
            }
            var byKey = new Dictionary<string, AssertionDraft>(StringComparer.Ordinal);
            foreach (var d in desired)
            {
                // Two drafts for one key: keep the better-supported one (a producer bug must not create duplicate live rows).
                if (!byKey.TryGetValue(d.NaturalKey, out var existing) || d.Probability > existing.Probability) byKey[d.NaturalKey] = d;
            }
            long now = clock.NowMs;
            return stores.Intel.WriteAsync((c, tx) =>
            {
                var current = LoadLive(c, tx, subjectIds, producer);
                int inserted = 0, superseded = 0, retracted = 0, unchanged = 0;
                var changed = new List<string>();
                var insertedIds = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, draft) in byKey)
                {
                    string hash = ContentHash(draft);
                    var live = current.Where(r => r.NaturalKey == key).ToList();
                    seen.Add(key);
                    if (live.Count == 1 && live[0].ContentHash == hash)
                    {
                        unchanged++;
                        continue;
                    }
                    string? previous = live.OrderByDescending(r => r.RecordedAtMs).Select(r => r.AssertionId).FirstOrDefault();
                    foreach (var old in live)
                    {
                        MarkDead(c, tx, old.AssertionId, now, retract: false);
                        superseded++;
                    }
                    string id = NaturalKeys.Id("a", key + "\u0001" + hash + "\u0001" + (previous ?? ""));
                    if (Insert(c, tx, id, key, hash, draft, moduleId, now))
                    {
                        inserted++;
                        insertedIds.Add(id);
                    }
                    changed.Add(key);
                }
                foreach (var old in current.Where(r => !seen.Contains(r.NaturalKey)))
                {
                    if (keepUnlisted != null && keepUnlisted(old.NaturalKey)) continue;
                    MarkDead(c, tx, old.AssertionId, now, retract: true);
                    retracted++;
                    changed.Add(old.NaturalKey);
                }
                return new ReconcileResult(inserted, superseded, retracted, unchanged, changed.Distinct(StringComparer.Ordinal).ToList(), insertedIds);
            }, ct);
        }

        /// <summary>
        /// Deletes every row of the given attributes about a person (MinorGuard: restricted facts are neither stored nor
        /// kept once the band says minor). Deletion rather than retraction: an as-known-at view must not retain them either.
        /// Returns the number of assertion versions removed.
        /// </summary>
        public Task<int> PurgeAsync(IReadOnlyCollection<string> subjectIds, IReadOnlyCollection<string> attributes, CancellationToken ct = default) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                var ids = new List<string>();
                foreach (var subject in subjectIds)
                    foreach (var attr in attributes)
                        ids.AddRange(c.Query(tx, "SELECT assertion_id FROM assertions WHERE subject_kind='person' AND subject_id=$s AND attribute=$a",
                            r => r.GetString(0), ("$s", subject), ("$a", attr)));
                // Relations that point at the person as their object are restricted too (family names of minors).
                foreach (var subject in subjectIds)
                    foreach (var attr in attributes)
                        ids.AddRange(c.Query(tx, "SELECT assertion_id FROM assertions WHERE object_kind='person' AND object_id=$s AND attribute=$a",
                            r => r.GetString(0), ("$s", subject), ("$a", attr)));
                foreach (var id in ids.Distinct(StringComparer.Ordinal))
                {
                    c.Exec(tx, "DELETE FROM assertion_evidence WHERE assertion_id=$i", ("$i", id));
                    c.Exec(tx, "DELETE FROM kn_assertion_dims WHERE assertion_id=$i", ("$i", id));
                    c.Exec(tx, "DELETE FROM assertions WHERE assertion_id=$i", ("$i", id));
                }
                return ids.Distinct(StringComparer.Ordinal).Count();
            }, ct);

        private sealed record LiveRow(string AssertionId, string NaturalKey, string ContentHash, long RecordedAtMs);

        private static List<LiveRow> LoadLive(SqliteConnection c, SqliteTransaction tx, IReadOnlyCollection<string> subjectIds, string producer)
        {
            var rows = new List<LiveRow>();
            foreach (var subject in subjectIds.Distinct(StringComparer.Ordinal))
                rows.AddRange(c.Query(tx, "SELECT assertion_id, natural_key, content_hash, recorded_at_ms FROM kn_assertion_dims WHERE subject_id=$s AND producer=$p AND live=1",
                    r => new LiveRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("$s", subject), ("$p", producer)));
            return rows;
        }

        private static void MarkDead(SqliteConnection c, SqliteTransaction tx, string assertionId, long now, bool retract)
        {
            c.Exec(tx, retract
                    ? "UPDATE assertions SET retracted_at_ms=$n WHERE assertion_id=$i AND retracted_at_ms IS NULL AND superseded_at_ms IS NULL"
                    : "UPDATE assertions SET superseded_at_ms=$n WHERE assertion_id=$i AND retracted_at_ms IS NULL AND superseded_at_ms IS NULL",
                ("$n", now), ("$i", assertionId));
            c.Exec(tx, "UPDATE kn_assertion_dims SET live=0 WHERE assertion_id=$i", ("$i", assertionId));
        }

        private static bool Insert(SqliteConnection c, SqliteTransaction tx, string id, string key, string hash, AssertionDraft d, string moduleId, long now)
        {
            var def = Attributes.Get(d.Attribute);
            int n = c.Exec(tx, """
                INSERT OR IGNORE INTO assertions(assertion_id, natural_key, subject_kind, subject_id, attribute, attr_class, value_json, value_text, object_kind, object_id,
                    valid_from_earliest_ms, valid_from_latest_ms, valid_to_earliest_ms, valid_to_latest_ms, asserted_at_ms, recorded_at_ms, status, modality, source_kind,
                    probability, calibrated, sensitivity, module_id, module_version, prompt_version, model, job_id, source_seq, derivation_json)
                VALUES($id,$key,$sk,$si,$attr,$cls,$vj,$vt,$ok,$oi,$vfe,$vfl,$vte,$vtl,$as,$rec,$st,$mod,$src,$p,$cal,$sens,$m,$mv,$pv,$model,$job,$seq,$der)
                """,
                ("$id", id), ("$key", key), ("$sk", d.SubjectKind), ("$si", d.SubjectId), ("$attr", d.Attribute), ("$cls", def.Class.ToString()),
                ("$vj", JsonSerializer.Serialize(d.Value, KernelJson.Options)), ("$vt", d.Value.Text), ("$ok", d.ObjectKind), ("$oi", d.ObjectId),
                ("$vfe", d.ValidFrom?.EarliestMs), ("$vfl", d.ValidFrom?.LatestMs), ("$vte", d.ValidTo?.EarliestMs), ("$vtl", d.ValidTo?.LatestMs),
                ("$as", d.AssertedAtMs), ("$rec", now), ("$st", d.Status), ("$mod", d.Modality), ("$src", d.SourceKind), ("$p", Math.Round(d.Probability, 6)),
                ("$cal", d.Calibrated ? 1 : 0), ("$sens", d.Sensitivity), ("$m", moduleId), ("$mv", d.ModuleVersion), ("$pv", d.PromptVersion), ("$model", d.Model),
                ("$job", d.JobId), ("$seq", d.SourceSeq), ("$der", d.Derivation == null ? null : JsonSerializer.Serialize(d.Derivation, KernelJson.Options)));
            if (n == 0) return false;
            foreach (var e in d.Evidence.GroupBy(e => (e.MessageId, e.MessageVersion, e.Role)).Select(g => g.OrderByDescending(x => x.Weight).First()))
                c.Exec(tx, "INSERT OR IGNORE INTO assertion_evidence(assertion_id, message_id, message_version, role, quote, weight) VALUES($a,$m,$v,$r,$q,$w)",
                    ("$a", id), ("$m", e.MessageId), ("$v", e.MessageVersion), ("$r", e.Role), ("$q", e.Quote), ("$w", Math.Round(e.Weight, 6)));
            var dims = d.Dims;
            c.Exec(tx, """
                INSERT OR REPLACE INTO kn_assertion_dims(assertion_id, natural_key, producer, category, attribute, subject_id, object_id, author_person_id, occurred_at_ms,
                    first_evidence_at_ms, recorded_at_ms, valid_from_ms, valid_to_ms, platform, source_id, guild_id, conversation_id, conversation_kind, status, modality,
                    source_kind, probability, evidence_count, independent_count, module_id, model, prompt_version, run_id, job_id, content_hash, live, source_seq)
                VALUES($id,$key,$prod,$cat,$attr,$si,$oi,$au,$occ,$first,$rec,$vf,$vt,$pl,$src,$g,$conv,$ck,$st,$mod,$sk,$p,$ec,$ic,$m,$model,$pv,$run,$job,$hash,1,$seq)
                """,
                ("$id", id), ("$key", key), ("$prod", d.Producer), ("$cat", def.Category), ("$attr", d.Attribute), ("$si", d.SubjectId), ("$oi", d.ObjectId),
                ("$au", dims.AuthorPersonId), ("$occ", dims.OccurredAtMs), ("$first", dims.FirstEvidenceAtMs), ("$rec", now),
                ("$vf", d.ValidFrom?.MidpointMs), ("$vt", d.ValidTo?.MidpointMs), ("$pl", dims.Platform), ("$src", dims.SourceId), ("$g", dims.GuildId),
                ("$conv", dims.ConversationId), ("$ck", dims.ConversationKind), ("$st", d.Status), ("$mod", d.Modality), ("$sk", d.SourceKind),
                ("$p", Math.Round(d.Probability, 6)), ("$ec", dims.EvidenceCount), ("$ic", dims.IndependentCount), ("$m", moduleId), ("$model", d.Model),
                ("$pv", d.PromptVersion), ("$run", dims.RunId), ("$job", d.JobId), ("$hash", hash), ("$seq", d.SourceSeq));
            return true;
        }

        /// <summary>What makes two versions "the same": the fact itself, its validity, status, probability and evidence. Provenance
        /// that does not change the content (job id, model of an identical answer, watermark) does not create a new version.</summary>
        internal static string ContentHash(AssertionDraft d)
        {
            var evidence = d.Evidence.Select(e => $"{e.MessageId}#{e.MessageVersion}|{e.Role}|{NaturalKeys.Normalize(e.Quote)}|{e.Weight.ToString("0.###", CultureInfo.InvariantCulture)}")
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            string material = string.Join("\u0001", new[]
            {
                d.NaturalKey, JsonSerializer.Serialize(d.Value, KernelJson.Options), d.ObjectKind ?? "", d.ObjectId ?? "",
                Ms(d.ValidFrom?.EarliestMs), Ms(d.ValidFrom?.LatestMs), Ms(d.ValidTo?.EarliestMs), Ms(d.ValidTo?.LatestMs), Ms(d.AssertedAtMs),
                d.Status, d.Modality ?? "", d.SourceKind ?? "", d.Probability.ToString("0.0000", CultureInfo.InvariantCulture), d.Calibrated ? "1" : "0",
                d.Sensitivity, d.PromptVersion ?? "", d.Derivation == null ? "" : JsonSerializer.Serialize(d.Derivation, KernelJson.Options), string.Join(";", evidence),
            });
            return NaturalKeys.Sha256Hex(material);
        }

        private static string Ms(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

        // ── reading ──────────────────────────────────────────────────────────────

        private const string Columns =
            "a.assertion_id, a.natural_key, a.subject_kind, a.subject_id, a.attribute, a.attr_class, a.value_json, a.value_text, a.object_kind, a.object_id, " +
            "a.valid_from_earliest_ms, a.valid_from_latest_ms, a.valid_to_earliest_ms, a.valid_to_latest_ms, a.asserted_at_ms, a.recorded_at_ms, a.superseded_at_ms, " +
            "a.retracted_at_ms, a.status, a.modality, a.source_kind, a.probability, a.calibrated, a.sensitivity, a.module_id, a.module_version, a.prompt_version, a.model, " +
            "a.job_id, a.source_seq, a.derivation_json, d.producer, d.category, d.first_evidence_at_ms";

        internal const string SelectColumns = Columns;

        internal static AssertionRecord Map(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetStringOrNull(7),
            r.GetStringOrNull(8), r.GetStringOrNull(9), r.GetInt64OrNull(10), r.GetInt64OrNull(11), r.GetInt64OrNull(12), r.GetInt64OrNull(13),
            r.GetInt64OrNull(14), r.GetInt64(15), r.GetInt64OrNull(16), r.GetInt64OrNull(17), r.GetString(18), r.GetStringOrNull(19), r.GetStringOrNull(20),
            r.GetDoubleOrNull(21), r.GetInt32(22) == 1, r.GetString(23), r.GetString(24), r.GetInt32(25), r.GetStringOrNull(26), r.GetStringOrNull(27),
            r.GetInt64OrNull(28), r.GetInt64(29), r.GetStringOrNull(30), r.GetStringOrNull(31), r.GetStringOrNull(32), r.GetInt64OrNull(33));

        /// <summary>
        /// The assertions about a subject under one of the three bitemporal modes. Current and as-of-world read the live
        /// versions (current knowledge); as-known-at reads every version that was believed at the instant.
        /// </summary>
        public IReadOnlyList<AssertionView> Query(AssertionQuery q)
        {
            if (q.SubjectIds == null || q.SubjectIds.Count == 0) throw new ArgumentException("A subject is required.", nameof(q));
            var rows = new List<AssertionRecord>();
            using (var c = stores.Intel.OpenRead())
            {
                foreach (var subject in q.SubjectIds.Distinct(StringComparer.Ordinal))
                {
                    string sql = q.Mode == KnowledgeMode.AsKnownAt
                        ? $"SELECT {Columns} FROM assertions a LEFT JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id WHERE a.subject_kind=$k AND a.subject_id=$s AND a.recorded_at_ms <= $t"
                        : $"SELECT {Columns} FROM kn_assertion_dims d JOIN assertions a ON a.assertion_id=d.assertion_id WHERE d.subject_id=$s AND d.live=1 AND a.subject_kind=$k";
                    rows.AddRange(c.Query(null, sql, Map, ("$k", q.SubjectKind), ("$s", subject), ("$t", q.AtMs)));
                }
            }
            var result = new List<AssertionView>();
            foreach (var r in rows)
            {
                if (q.Mode == KnowledgeMode.AsKnownAt ? !r.BelievedAt(q.AtMs) : !r.IsLiveRecord) continue;
                if (q.Attribute != null && !string.Equals(r.Attribute, q.Attribute, StringComparison.OrdinalIgnoreCase)) continue;
                var def = Attributes.Get(r.Attribute);
                if (q.Category != null && !string.Equals(def.Category, q.Category, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Producer != null && r.Producer != null && r.Producer != q.Producer) continue;
                if (q.Statuses != null ? !q.Statuses.Contains(r.Status) : !AssertionStatus.IsShownAsKnowledge(r.Status)) continue;
                if (!q.IncludeInferred && r.Status == AssertionStatus.Inferred) continue;
                if (!q.IncludeEvents && def.Class == AttributeClass.Event) continue;
                // Known by the cutoff: its earliest evidence is no later than the cutoff (no post-cutoff leakage, plan T6).
                if (q.EvidenceCutoffMs is long cutoff && (r.FirstEvidenceAtMs ?? r.AssertedAtMs ?? long.MaxValue) > cutoff) continue;
                if (q.Mode != KnowledgeMode.AsKnownAt && !ValidUnderMode(r, def, q)) continue;
                double eff = Confidence.Effective(def, r.Probability ?? 0, r.AssertedAtMs, q.AtMs);
                if (q.MinProbability is double min && eff < min) continue;
                result.Add(new AssertionView(r, def, eff));
            }
            return result
                .OrderBy(v => v.Definition.Category, StringComparer.Ordinal)
                .ThenBy(v => v.Record.Attribute, StringComparer.Ordinal)
                .ThenByDescending(v => v.EffectiveProbability)
                .ThenBy(v => v.Record.AssertionId, StringComparer.Ordinal)
                .Take(Math.Clamp(q.Limit, 1, 5000))
                .ToList();
        }

        private static bool ValidUnderMode(AssertionRecord r, AttributeDefinition def, AssertionQuery q)
        {
            if (def.Class == AttributeClass.Event)
            {
                // An event is part of the world's history once it may have started; as-of-world T excludes events that surely came later.
                return q.Mode == KnowledgeMode.Current || r.ValidFromEarliestMs == null || r.ValidFromEarliestMs <= q.AtMs;
            }
            if (def.Class is AttributeClass.Immutable or AttributeClass.TimeDerived) return true;
            return r.PossiblyValidAt(q.AtMs);
        }

        /// <summary>One version with its evidence.</summary>
        public (AssertionRecord Record, IReadOnlyList<EvidenceRef> Evidence)? Get(string assertionId)
        {
            using var c = stores.Intel.OpenRead();
            var row = c.Query(null, $"SELECT {Columns} FROM assertions a LEFT JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id WHERE a.assertion_id=$i", Map, ("$i", assertionId)).FirstOrDefault();
            if (row == null) return null;
            return (row, EvidenceOf(c, assertionId));
        }

        public IReadOnlyList<EvidenceRef> Evidence(string assertionId)
        {
            using var c = stores.Intel.OpenRead();
            return EvidenceOf(c, assertionId);
        }

        private static List<EvidenceRef> EvidenceOf(SqliteConnection c, string assertionId) =>
            c.Query(null, "SELECT message_id, message_version, role, quote, weight FROM assertion_evidence WHERE assertion_id=$i ORDER BY message_id, message_version",
                r => new EvidenceRef(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetStringOrNull(3), r.GetDoubleOrNull(4) ?? 0), ("$i", assertionId));

        /// <summary>Every version of one natural key, oldest first (the "history" of a fact).</summary>
        public IReadOnlyList<AssertionRecord> History(string naturalKey)
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, $"SELECT {Columns} FROM assertions a LEFT JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id WHERE a.natural_key=$k ORDER BY a.recorded_at_ms, a.assertion_id",
                Map, ("$k", naturalKey));
        }

        /// <summary>Live assertions citing a message (any version): what an edit or deletion of it must re-verify (plan T5).</summary>
        public IReadOnlyList<string> CitingMessage(string messageId)
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT DISTINCT e.assertion_id FROM assertion_evidence e JOIN kn_assertion_dims d ON d.assertion_id=e.assertion_id
                WHERE e.message_id=$m AND d.live=1
                """, r => r.GetString(0), ("$m", messageId));
        }
    }
}
