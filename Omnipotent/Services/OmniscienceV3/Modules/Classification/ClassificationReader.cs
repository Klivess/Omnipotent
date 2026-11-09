using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Classification
{
    /// <summary>A message's labels under one label set.</summary>
    public sealed record MessageLabels(string MessageId, int Labelset, int ItemVersion, string TextHash, string? Gate, IReadOnlyList<PackedLabel> Labels, long AsOfMs)
    {
        /// <summary>The answered label of a task (null when abstained or not labelled).</summary>
        public string? Answer(string task) => Labels.FirstOrDefault(l => l.Task == task) is { Abstained: false } l ? l.Label : null;

        public bool NonAuthored => Gate != null;
    }

    /// <summary>
    /// The read side of classification for every other module and route: packed per-message labels of the active label set
    /// (or a named one), gold corrections, and the copypasta count. Use this instead of querying the tables directly; the
    /// storage layout is WS-C's to change.
    /// </summary>
    public static class ClassificationReader
    {
        /// <summary>operator.db <c>gold_labels.kind</c> of classification corrections.</summary>
        public const string GoldKind = "classification";
        public const int CopypastaAuthors = 3;

        public static int? ActiveLabelset(SqliteConnection intel)
        {
            bool exists = intel.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='classify_labelsets'") > 0;
            if (!exists) return null;
            return (int?)intel.Scalar<long?>(null, "SELECT labelset FROM classify_labelsets WHERE state='active' ORDER BY labelset DESC LIMIT 1");
        }

        /// <summary>Labels of the given messages under the active label set (or <paramref name="labelset"/>).</summary>
        public static Dictionary<string, MessageLabels> ForMessages(OmniscienceStores stores, IReadOnlyCollection<string> messageIds, int? labelset = null)
        {
            var result = new Dictionary<string, MessageLabels>(StringComparer.Ordinal);
            if (messageIds.Count == 0) return result;
            using var c = stores.Intel.OpenRead();
            int? ls = labelset ?? ActiveLabelset(c);
            if (ls == null) return result;
            var rows = c.QueryInWith("SELECT message_id, item_version, text_hash, codes, gate, as_of_ms FROM classify_message WHERE labelset=$l AND message_id IN ({keys})",
                messageIds, r => new MessageLabels(r.GetString(0), ls.Value, r.GetInt32(1), r.GetString(2), r.GetStringOrNull(4), LabelCodec.Decode(r.GetString(3)), r.GetInt64(5)),
                ("$l", ls.Value));
            var copied = CopypastaHashes(c, rows.Select(r => r.TextHash).Distinct().ToList());
            foreach (var row in rows)
                result[row.MessageId] = copied.Contains(row.TextHash) && row.Gate == null ? row with { Gate = "copypasta" } : row;
            return result;
        }

        /// <summary>Texts posted by at least <see cref="CopypastaAuthors"/> distinct authors (non-authored for style metrics, A5).</summary>
        public static HashSet<string> CopypastaHashes(SqliteConnection intel, IReadOnlyCollection<string> hashes) =>
            intel.QueryIn(null, "SELECT text_hash FROM classify_text_authors WHERE text_hash IN ({keys}) GROUP BY text_hash HAVING COUNT(*) >= " + CopypastaAuthors,
                hashes, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);

        // ── gold ─────────────────────────────────────────────────────────────────

        /// <summary>The natural key of a classification correction (plan R3: gold references natural keys, never intel rows).</summary>
        public static string GoldKey(string messageId, string task) =>
            JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["item"] = "message:" + messageId, ["task"] = task });

        /// <summary>Every active correction: (message, task) → label.</summary>
        public static Dictionary<(string Message, string Task), string> LoadGold(OmniscienceStores stores)
        {
            var result = new Dictionary<(string, string), string>();
            using var op = stores.Operator.OpenRead();
            foreach (var (key, correction) in op.Query(null, "SELECT natural_key, correction_json FROM gold_labels WHERE kind=$k AND superseded_at_ms IS NULL ORDER BY decided_at_ms",
                         r => (r.GetString(0), r.GetStringOrNull(1)), ("$k", GoldKind)))
            {
                try
                {
                    using var k = JsonDocument.Parse(key);
                    string item = k.RootElement.GetProperty("item").GetString() ?? "";
                    string task = k.RootElement.GetProperty("task").GetString() ?? "";
                    if (!item.StartsWith("message:", StringComparison.Ordinal) || correction == null) continue;
                    using var cj = JsonDocument.Parse(correction);
                    if (cj.RootElement.TryGetProperty("label", out var l) && l.GetString() is { } label) result[(item["message:".Length..], task)] = label;
                }
                catch (JsonException)
                {
                    // A malformed decision is ignored rather than guessed at; the review UI shows it as unreadable.
                    continue;
                }
            }
            return result;
        }

        public static Dictionary<string, int> GoldCounts(OmniscienceStores stores) =>
            LoadGold(stores).GroupBy(kv => kv.Key.Task).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        /// <summary>
        /// Records Klives's label for one message and task (X69 "correct the record" → gold). The previous decision for the
        /// same key is superseded, never deleted, and the change is audited. The ensemble applies it at once on the next
        /// re-fusion of that message and the next training run uses it.
        /// </summary>
        public static async Task RecordGoldAsync(OmniscienceStores stores, IClock clock, string actor, string messageId, string task, string label, string? note,
            CancellationToken ct = default)
        {
            var def = ClassificationTasks.Get(task);
            if (def.IndexOf(label) < 0) throw new ArgumentException($"'{label}' is not a label of {task} ({string.Join(", ", def.LabelIds)}).");
            string key = GoldKey(messageId, task);
            long now = clock.NowMs;
            string correction = JsonSerializer.Serialize(new { label = def.Labels[def.IndexOf(label)].Id });
            await stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE gold_labels SET superseded_at_ms=$t WHERE kind=$k AND natural_key=$n AND superseded_at_ms IS NULL", ("$t", now), ("$k", GoldKind), ("$n", key));
                c.Exec(tx, "INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES($k,$n,'label',$c,$note,$t,$a)",
                    ("$k", GoldKind), ("$n", key), ("$c", correction), ("$note", note), ("$t", now), ("$a", actor));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'classify.gold',$g,$d)",
                    ("$t", now), ("$a", actor), ("$g", key), ("$d", correction));
            }, ct).ConfigureAwait(false);
        }
    }
}
