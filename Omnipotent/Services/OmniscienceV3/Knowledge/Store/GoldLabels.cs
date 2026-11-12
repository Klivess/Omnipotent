using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>What a gold label is about.</summary>
    public static class GoldKinds
    {
        public const string Assertion = "assertion";
        public const string IdentityLink = "identity_link";
        public const string Alias = "alias";
        public const string Inquiry = "inquiry";
        public const string AgeEvidence = "age_evidence";
        public const string Plan = "plan";
        public const string LifeEvent = "life_event";
        public const string Hypothesis = "hypothesis";
    }

    public static class GoldVerdicts
    {
        public const string Confirm = "confirm";
        public const string Reject = "reject";
        /// <summary>Right fact, wrong detail: the correction (value, interval) is in correction_json.</summary>
        public const string Correct = "correct";

        public static bool IsKnown(string? v) => v is Confirm or Reject or Correct;
    }

    public sealed record GoldLabel(long LabelId, string Kind, string NaturalKey, string Verdict, string? CorrectionJson, string? Note, long DecidedAtMs, string Actor);

    /// <summary>
    /// Klives's review decisions (operator.db <c>gold_labels</c>): the ground truth for consolidation ("Klives-confirmed facts
    /// are never overwritten"), calibration, the Quality Lab and inquiry feedback. Labels reference natural keys only, so
    /// they survive any rebuild of intel.db (plan R3). A new decision about the same key supersedes the previous one; the
    /// history stays for as-known-at evaluation. Every decision is audited.
    /// </summary>
    public sealed class GoldLabelStore
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;

        public GoldLabelStore(OmniscienceStores stores, IClock clock)
        {
            this.stores = stores;
            this.clock = clock;
        }

        public async Task<long> RecordAsync(string kind, string naturalKey, string verdict, object? correction, string? note, string actor, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentException.ThrowIfNullOrWhiteSpace(naturalKey);
            if (!GoldVerdicts.IsKnown(verdict)) throw new ArgumentException($"Unknown verdict '{verdict}'.", nameof(verdict));
            long now = clock.NowMs;
            string? correctionJson = correction == null ? null : JsonSerializer.Serialize(correction, KernelJson.Options);
            return await stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE gold_labels SET superseded_at_ms=$n WHERE kind=$k AND natural_key=$key AND superseded_at_ms IS NULL",
                    ("$n", now), ("$k", kind), ("$key", naturalKey));
                c.Exec(tx, """
                    INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES($k,$key,$v,$c,$note,$n,$a)
                    """, ("$k", kind), ("$key", naturalKey), ("$v", verdict), ("$c", correctionJson), ("$note", note), ("$n", now), ("$a", actor));
                long id = c.Scalar<long>(tx, "SELECT last_insert_rowid()");
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($n,$a,'gold.record',$key,$d)",
                    ("$n", now), ("$a", actor), ("$key", naturalKey),
                    ("$d", JsonSerializer.Serialize(new { kind, verdict, labelId = id, note }, KernelJson.Options)));
                return id;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>The active (latest, not superseded) label per natural key of one kind.</summary>
        public IReadOnlyDictionary<string, GoldLabel> Active(string kind)
        {
            using var c = stores.Operator.OpenRead();
            return c.Query(null, """
                    SELECT label_id, kind, natural_key, verdict, correction_json, note, decided_at_ms, actor FROM gold_labels
                    WHERE kind=$k AND superseded_at_ms IS NULL ORDER BY decided_at_ms, label_id
                    """, Map, ("$k", kind))
                .GroupBy(l => l.NaturalKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        }

        public GoldLabel? ActiveFor(string kind, string naturalKey)
        {
            using var c = stores.Operator.OpenRead();
            return c.Query(null, """
                SELECT label_id, kind, natural_key, verdict, correction_json, note, decided_at_ms, actor FROM gold_labels
                WHERE kind=$k AND natural_key=$key AND superseded_at_ms IS NULL ORDER BY decided_at_ms DESC, label_id DESC LIMIT 1
                """, Map, ("$k", kind), ("$key", naturalKey)).FirstOrDefault();
        }

        /// <summary>Every label of a kind decided up to <paramref name="asOfMs"/> that was still active then (for
        /// as-known-at evaluation and calibration fitting).</summary>
        public IReadOnlyList<GoldLabel> ActiveAt(string kind, long asOfMs)
        {
            using var c = stores.Operator.OpenRead();
            return c.Query(null, """
                SELECT label_id, kind, natural_key, verdict, correction_json, note, decided_at_ms, actor FROM gold_labels
                WHERE kind=$k AND decided_at_ms <= $t AND (superseded_at_ms IS NULL OR superseded_at_ms > $t)
                ORDER BY decided_at_ms, label_id
                """, Map, ("$k", kind), ("$t", asOfMs));
        }

        /// <summary>The subject ("person:&lt;id&gt;") a natural key is about, or null.</summary>
        public static string? SubjectOf(string naturalKey)
        {
            try
            {
                using var doc = JsonDocument.Parse(naturalKey);
                return doc.RootElement.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Re-points a natural key at a canonical subject (gold labels are keyed by the canonical id at decision
        /// time and re-resolved through the canonical map when read, plan operator.db notes).</summary>
        public static string Recanonicalize(string naturalKey, Func<string, string> canonicalPerson)
        {
            Dictionary<string, JsonElement>? fields;
            try { fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(naturalKey); }
            catch (JsonException) { return naturalKey; }
            if (fields == null) return naturalKey;
            var rewritten = new List<KeyValuePair<string, object?>>();
            foreach (var (k, v) in fields)
            {
                object? value = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => v.GetRawText(),
                };
                if ((k == "s" || k == "o") && value is string s && s.StartsWith("person:", StringComparison.Ordinal))
                    value = "person:" + canonicalPerson(s["person:".Length..]);
                rewritten.Add(new(k, value));
            }
            return NaturalKeys.Canonical(rewritten);
        }

        private static GoldLabel Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6), r.GetString(7));
    }
}
