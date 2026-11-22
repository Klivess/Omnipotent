using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>A rejected claim, kept for the Quality Lab and audits (its value redacted when MinorGuard restricts it).</summary>
    public sealed record RejectedClaim(string ClaimId, string SubjectPersonId, string Attribute, string? ValueText, string Reason, string? Detail,
        IReadOnlyList<string> MessageIds, double? Probability, string? Modality, string? SourceKind, long AssertedAtMs, string Agreement,
        string? ConversationId, string? GuildId, string? Platform, string? AuthorPersonId);

    /// <summary>What the extraction of one slice commits atomically: claims, coverage of its anchors, age evidence.</summary>
    public sealed record SliceCommit(
        string RunId,
        string CoveragePersonId,
        IReadOnlyList<(string MessageId, int Version)> Anchors,
        string CoverageOutcome,
        IReadOnlyList<VerifiedClaim> Verified,
        IReadOnlyList<RejectedClaim> Rejected,
        IReadOnlyList<AgeEvidenceRow> AgeEvidence,
        long SourceSeq);

    /// <summary>
    /// Persistence of claims (intel.db <c>kn_claims</c>), per-message coverage and age evidence. A slice's results and the
    /// coverage of its anchors commit in one transaction, so coverage advances on success only and a crash mid-slice simply
    /// redoes that slice (F176/F230/F231). Restricted values of minors are never written (redacted before storage).
    /// </summary>
    public sealed class ClaimStore
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;

        public ClaimStore(OmniscienceStores stores, IClock clock)
        {
            this.stores = stores;
            this.clock = clock;
        }

        public Task CommitAsync(SliceCommit commit, string promptVersion, string? model, CancellationToken ct)
        {
            long now = clock.NowMs;
            return stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var v in commit.Verified) InsertVerified(c, tx, v, commit.SourceSeq, now);
                foreach (var r in commit.Rejected) InsertRejected(c, tx, r, commit.RunId, promptVersion, model, commit.SourceSeq, now);
                foreach (var a in commit.AgeEvidence) UpsertAge(c, tx, a, commit.SourceSeq, now);
                int newlyCovered = 0;
                foreach (var (messageId, version) in commit.Anchors)
                {
                    if (c.Scalar<long?>(tx, "SELECT 1 FROM kn_coverage WHERE person_id=$p AND message_id=$m", ("$p", commit.CoveragePersonId), ("$m", messageId)) == null) newlyCovered++;
                    c.Exec(tx, """
                        INSERT INTO kn_coverage(person_id, message_id, message_version, prompt_version, run_id, outcome, covered_at_ms) VALUES($p,$m,$v,$pv,$r,$o,$t)
                        ON CONFLICT(person_id, message_id) DO UPDATE SET message_version=excluded.message_version, prompt_version=excluded.prompt_version,
                            run_id=excluded.run_id, outcome=excluded.outcome, covered_at_ms=excluded.covered_at_ms
                        """, ("$p", commit.CoveragePersonId), ("$m", messageId), ("$v", version), ("$pv", promptVersion), ("$r", commit.RunId),
                        ("$o", commit.CoverageOutcome), ("$t", now));
                    // Read: it leaves the person's queue in the same transaction (Evidence.EvidenceQueue). An edit made
                    // since it was queued carries a higher version and stays queued.
                    c.Exec(tx, "DELETE FROM kn_evidence_queue WHERE person_id=$p AND message_id=$m AND version <= $v",
                        ("$p", commit.CoveragePersonId), ("$m", messageId), ("$v", version));
                }
                // How many of the person's messages have been read is kept as a running count. It was recounted from the
                // coverage table inside every state write, on the store's one writer: a count that grows with everything
                // ever read about the person, while every other write to the store waits.
                if (newlyCovered > 0)
                {
                    c.Exec(tx, "INSERT OR IGNORE INTO kn_person_state(person_id, tracked, pending, updated_at_ms) VALUES($p,0,0,$t)", ("$p", commit.CoveragePersonId), ("$t", now));
                    c.Exec(tx, "UPDATE kn_person_state SET covered_messages = covered_messages + $n WHERE person_id=$p", ("$n", newlyCovered), ("$p", commit.CoveragePersonId));
                }
                // Claims of an earlier run about the same message versions are superseded by this run's reading of them.
                foreach (var (messageId, version) in commit.Anchors)
                    c.Exec(tx, """
                        UPDATE kn_claims SET superseded=1
                        WHERE run_id <> $r AND superseded=0 AND person_id=$p AND claim_id IN (SELECT claim_id FROM kn_claim_messages WHERE message_id=$m)
                          AND slice_index >= 0
                        """, ("$r", commit.RunId), ("$p", commit.CoveragePersonId), ("$m", messageId));
            }, ct);
        }

        private static void InsertVerified(SqliteConnection c, SqliteTransaction tx, VerifiedClaim v, long seq, long now)
        {
            var detail = new
            {
                v.NameKind, v.SpeechAct, v.Sincerity, v.IsCurrent, v.TimeSurface, v.TimePrecision, v.TimeCheck, v.Age, v.Plan, v.Entity, v.Relation,
                v.CorrectsMessageId, v.PlanRefersToMessageId, v.PromiserPersonId, v.PromiseePersonId, v.Flags,
            };
            var time = new
            {
                occurred = v.Occurred is { } o ? new[] { o.EarliestMs, o.LatestMs } : null,
                validFrom = v.ValidFrom is { } f ? new[] { f.EarliestMs, f.LatestMs } : null,
                validTo = v.ValidTo is { } t ? new[] { t.EarliestMs, t.LatestMs } : null,
            };
            var primary = v.Evidence.OrderByDescending(e => e.AuthorPersonId == v.SubjectPersonId).ThenByDescending(e => e.SentAtMs).First();
            c.Exec(tx, """
                INSERT OR REPLACE INTO kn_claims(claim_id, run_id, person_id, slice_index, attribute, category, value_text, value_norm, object_id, polarity, modality,
                    source_kind, speech_act, sincerity, name_kind, probability, time_json, detail_json, evidence_json, agreement, verdict, reason, critic, asserted_at_ms,
                    recorded_at_ms, source_seq, prompt_version, model, conversation_id, guild_id, platform, author_person_id, superseded)
                VALUES($id,$run,$p,$slice,$attr,$cat,$vt,$vn,$obj,$pol,$mod,$src,$sa,$sin,$nk,$prob,$time,$detail,$ev,$agr,'verified',$flags,$critic,$as,$rec,$seq,$pv,$model,
                    $conv,$g,$pl,$au,0)
                """,
                ("$id", v.ClaimId), ("$run", v.RunId), ("$p", v.SubjectPersonId), ("$slice", v.SliceIndex), ("$attr", v.Attribute), ("$cat", v.Category),
                ("$vt", v.ValueText), ("$vn", v.ValueNorm), ("$obj", v.ObjectPersonId), ("$pol", v.Polarity), ("$mod", v.Modality), ("$src", v.SourceKind),
                ("$sa", v.SpeechAct), ("$sin", v.Sincerity), ("$nk", v.NameKind), ("$prob", v.Probability), ("$time", JsonSerializer.Serialize(time, KernelJson.Options)),
                ("$detail", JsonSerializer.Serialize(detail, KernelJson.Options)), ("$ev", JsonSerializer.Serialize(v.Evidence, KernelJson.Options)), ("$agr", v.AgreementKind),
                ("$flags", v.Flags), ("$critic", v.Critic), ("$as", v.AssertedAtMs), ("$rec", now), ("$seq", seq), ("$pv", v.PromptVersion), ("$model", v.Model),
                ("$conv", primary.ConversationId), ("$g", primary.GuildId), ("$pl", primary.Platform), ("$au", primary.AuthorPersonId));
            foreach (var e in v.Evidence.Select(e => e.MessageId).Distinct(StringComparer.Ordinal))
                c.Exec(tx, "INSERT OR IGNORE INTO kn_claim_messages(claim_id, message_id) VALUES($c,$m)", ("$c", v.ClaimId), ("$m", e));
        }

        private static void InsertRejected(SqliteConnection c, SqliteTransaction tx, RejectedClaim r, string runId, string promptVersion, string? model, long seq, long now)
        {
            var def = Attributes.Get(r.Attribute);
            c.Exec(tx, """
                INSERT OR REPLACE INTO kn_claims(claim_id, run_id, person_id, slice_index, attribute, category, value_text, value_norm, probability, modality, source_kind,
                    evidence_json, agreement, verdict, reason, asserted_at_ms, recorded_at_ms, source_seq, prompt_version, model, conversation_id, guild_id, platform,
                    author_person_id, detail_json, superseded)
                VALUES($id,$run,$p,-1,$attr,$cat,$vt,$vn,$prob,$mod,$src,$ev,$agr,'rejected',$reason,$as,$rec,$seq,$pv,$model,$conv,$g,$pl,$au,$detail,0)
                """,
                ("$id", r.ClaimId), ("$run", runId), ("$p", r.SubjectPersonId), ("$attr", r.Attribute), ("$cat", def.Category), ("$vt", r.ValueText),
                ("$vn", r.ValueText == null ? null : NaturalKeys.Normalize(r.ValueText)), ("$prob", r.Probability), ("$mod", r.Modality), ("$src", r.SourceKind),
                ("$ev", JsonSerializer.Serialize(r.MessageIds, KernelJson.Options)), ("$agr", r.Agreement), ("$reason", r.Reason), ("$as", r.AssertedAtMs), ("$rec", now),
                ("$seq", seq), ("$pv", promptVersion), ("$model", model), ("$conv", r.ConversationId), ("$g", r.GuildId), ("$pl", r.Platform), ("$au", r.AuthorPersonId),
                ("$detail", r.Detail == null ? null : JsonSerializer.Serialize(new { detail = r.Detail }, KernelJson.Options)));
            foreach (var m in r.MessageIds.Distinct(StringComparer.Ordinal))
                c.Exec(tx, "INSERT OR IGNORE INTO kn_claim_messages(claim_id, message_id) VALUES($c,$m)", ("$c", r.ClaimId), ("$m", m));
        }

        private static void UpsertAge(SqliteConnection c, SqliteTransaction tx, AgeEvidenceRow a, long seq, long now) =>
            c.Exec(tx, """
                INSERT INTO kn_age_evidence(evidence_id, person_id, kind, birth_earliest_ms, birth_latest_ms, month, day, source_kind, asserted_at_ms, message_id,
                    message_version, claim_id, probability, status, natural_key, recorded_at_ms, source_seq)
                VALUES($id,$p,$k,$be,$bl,$mo,$d,$src,$as,$m,$v,$c,$prob,$st,$key,$rec,$seq)
                ON CONFLICT(evidence_id) DO UPDATE SET message_version=excluded.message_version, probability=MAX(probability, excluded.probability),
                    claim_id=excluded.claim_id, status=CASE WHEN kn_age_evidence.status='retracted' THEN 'active' ELSE kn_age_evidence.status END
                """,
                ("$id", a.EvidenceId), ("$p", a.PersonId), ("$k", a.Kind), ("$be", a.Birth?.EarliestMs), ("$bl", a.Birth?.LatestMs), ("$mo", a.Month), ("$d", a.Day),
                ("$src", a.SourceKind), ("$as", a.AssertedAtMs), ("$m", a.MessageId), ("$v", a.MessageVersion), ("$c", a.ClaimId), ("$prob", a.Probability),
                ("$st", a.Status), ("$key", a.NaturalKey), ("$rec", now), ("$seq", seq));

        // ── loading ──────────────────────────────────────────────────────────────

        private sealed class StoredDetail
        {
            public string? NameKind { get; set; }
            public string? SpeechAct { get; set; }
            public string? Sincerity { get; set; }
            public bool? IsCurrent { get; set; }
            public string? TimeSurface { get; set; }
            public string? TimePrecision { get; set; }
            public string? TimeCheck { get; set; }
            public RawAge? Age { get; set; }
            public RawPlan? Plan { get; set; }
            public RawEntity? Entity { get; set; }
            public RawRelation? Relation { get; set; }
            public string? CorrectsMessageId { get; set; }
            public string? PlanRefersToMessageId { get; set; }
            public string? PromiserPersonId { get; set; }
            public string? PromiseePersonId { get; set; }
            public string? Flags { get; set; }
        }

        private sealed class StoredTime
        {
            public long[]? Occurred { get; set; }
            public long[]? ValidFrom { get; set; }
            public long[]? ValidTo { get; set; }
        }

        /// <summary>Every live verified claim about the persons (canonical and merged-in ids).</summary>
        public IReadOnlyList<VerifiedClaim> LoadVerified(IReadOnlyCollection<string> personIds)
        {
            using var c = stores.Intel.OpenRead();
            return c.QueryIn(null, """
                SELECT claim_id, run_id, person_id, slice_index, attribute, category, value_text, value_norm, object_id, polarity, modality, source_kind, probability,
                       time_json, detail_json, evidence_json, agreement, critic, asserted_at_ms, prompt_version, model, reason
                FROM kn_claims WHERE person_id IN ({keys}) AND verdict='verified' AND superseded=0 AND value_text IS NOT NULL
                """, personIds, MapVerified);
        }

        private static VerifiedClaim MapVerified(SqliteDataReader r)
        {
            var time = JsonSerializer.Deserialize<StoredTime>(r.GetStringOrNull(13) ?? "{}", KernelJson.Options) ?? new StoredTime();
            var d = JsonSerializer.Deserialize<StoredDetail>(r.GetStringOrNull(14) ?? "{}", KernelJson.Options) ?? new StoredDetail();
            var evidence = JsonSerializer.Deserialize<List<ClaimEvidence>>(r.GetString(15), KernelJson.Options) ?? new();
            static TimeInterval? I(long[]? a) => a is { Length: 2 } ? new TimeInterval(Math.Min(a[0], a[1]), Math.Max(a[0], a[1])) : null;
            return new VerifiedClaim
            {
                ClaimId = r.GetString(0), RunId = r.GetString(1), SubjectPersonId = r.GetString(2), SliceIndex = r.GetInt32(3), Attribute = r.GetString(4),
                Category = r.GetString(5), ValueText = r.GetString(6), ValueNorm = r.GetStringOrNull(7) ?? NaturalKeys.Normalize(r.GetString(6)),
                ObjectPersonId = r.GetStringOrNull(8), Polarity = r.GetStringOrNull(9) ?? Polarity.Affirmed, Modality = r.GetStringOrNull(10) ?? Modality.Literal,
                SourceKind = r.GetStringOrNull(11) ?? ClaimSource.ThirdParty, Probability = r.GetDoubleOrNull(12) ?? 0.5,
                Occurred = I(time.Occurred), ValidFrom = I(time.ValidFrom), ValidTo = I(time.ValidTo),
                NameKind = d.NameKind, SpeechAct = d.SpeechAct, Sincerity = d.Sincerity, IsCurrent = d.IsCurrent, TimeSurface = d.TimeSurface,
                TimePrecision = d.TimePrecision, TimeCheck = d.TimeCheck, Age = d.Age, Plan = d.Plan, Entity = d.Entity, Relation = d.Relation,
                CorrectsMessageId = d.CorrectsMessageId, PlanRefersToMessageId = d.PlanRefersToMessageId, PromiserPersonId = d.PromiserPersonId,
                PromiseePersonId = d.PromiseePersonId, Flags = d.Flags, Evidence = evidence, AgreementKind = r.GetString(16), Critic = r.GetStringOrNull(17),
                AssertedAtMs = r.GetInt64(18), PromptVersion = r.GetString(19), Model = r.GetStringOrNull(20),
            };
        }

        /// <summary>Claims (any person) citing a message: what an edit or deletion must re-verify.</summary>
        public IReadOnlyList<(string ClaimId, string PersonId)> CitingMessage(string messageId)
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT k.claim_id, k.person_id FROM kn_claim_messages m JOIN kn_claims k ON k.claim_id=m.claim_id
                WHERE m.message_id=$m AND k.superseded=0 AND k.verdict='verified'
                """, r => (r.GetString(0), r.GetString(1)), ("$m", messageId));
        }

        /// <summary>Marks claims superseded (their message changed) and invalidates the coverage of that message for re-extraction.</summary>
        public Task SupersedeAsync(IReadOnlyCollection<string> claimIds, string messageId, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var id in claimIds) c.Exec(tx, "UPDATE kn_claims SET superseded=1 WHERE claim_id=$c", ("$c", id));
                c.Exec(tx, "UPDATE kn_person_state SET covered_messages = MAX(0, covered_messages - 1) WHERE person_id IN (SELECT person_id FROM kn_coverage WHERE message_id=$m)", ("$m", messageId));
                c.Exec(tx, "DELETE FROM kn_coverage WHERE message_id=$m", ("$m", messageId));
                c.Exec(tx, "UPDATE kn_age_evidence SET status='retracted' WHERE message_id=$m AND status IN ('active','inconsistent')", ("$m", messageId));
            }, ct);

        /// <summary>MinorGuard purge: redacts restricted values in stored claims of a person (the claim row stays for counts).</summary>
        public Task<int> RedactRestrictedAsync(IReadOnlyCollection<string> personIds, IReadOnlyCollection<string> attributes, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                int n = 0;
                foreach (var p in personIds)
                    foreach (var a in attributes)
                        n += c.Exec(tx, """
                            UPDATE kn_claims SET value_text=NULL, value_norm=NULL, object_id=NULL, time_json=NULL, detail_json=NULL, evidence_json='[]',
                                verdict='rejected', reason='restricted_for_minor'
                            WHERE person_id=$p AND attribute=$a AND value_text IS NOT NULL
                            """, ("$p", p), ("$a", a));
                return n;
            }, ct);

        /// <summary>Age statements in messages that a later message corrected stop counting (their natural keys stay for audit).</summary>
        public Task RetractAgeEvidenceAsync(IReadOnlyCollection<string> messageIds, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var m in messageIds)
                    c.Exec(tx, "UPDATE kn_age_evidence SET status='corrected' WHERE message_id=$m AND status IN ('active','inconsistent','retracted')", ("$m", m));
            }, ct);

        public Task UpdateAgeStatusAsync(IReadOnlyCollection<string> evidenceIds, string status, CancellationToken ct) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var id in evidenceIds)
                    c.Exec(tx, "UPDATE kn_age_evidence SET status=$s WHERE evidence_id=$i AND status <> 'rejected'", ("$s", status), ("$i", id));
            }, ct);
    }
}
