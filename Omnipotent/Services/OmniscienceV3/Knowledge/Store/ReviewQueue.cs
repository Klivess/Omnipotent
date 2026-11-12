using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    public static class ReviewKinds
    {
        /// <summary>Two literal claims of a one-value-for-life attribute disagree.</summary>
        public const string Contradiction = "contradiction";
        /// <summary>A Klives-confirmed fact has newer contrary evidence: "possible change since &lt;date&gt;".</summary>
        public const string PossibleChange = "possible_change";
        /// <summary>Age statements that cannot all be true (joke or misread candidates).</summary>
        public const string AgeConflict = "age_conflict";
        /// <summary>An age statement that restricts the person (Klives can reject a misread here).</summary>
        public const string AgeEvidence = "age_evidence";
        /// <summary>A state attribute flips back and forth: not a real change over time.</summary>
        public const string Flapping = "flapping";
        /// <summary>New evidence for a fact Klives rejected.</summary>
        public const string RejectedResurfaced = "rejected_resurfaced";
        /// <summary>The critic could not confirm a high-impact fact.</summary>
        public const string Unproven = "unproven";
        /// <summary>Two identities that may be the same person (X67; never merged automatically).</summary>
        public const string IdentityLink = "identity_link";
        /// <summary>A learned alias that would feed alerts: needs confirmation first.</summary>
        public const string Alias = "alias";
        /// <summary>A hypothesis the evidence watcher confirmed or refuted.</summary>
        public const string Hypothesis = "hypothesis";
    }

    public static class ReviewStates
    {
        public const string Open = "open";
        public const string Resolved = "resolved";
        public const string Dismissed = "dismissed";
    }

    public sealed record ReviewItem(string ItemId, string Kind, string? SubjectId, string? NaturalKey, string Title, string? DetailJson, double Priority,
        string State, long CreatedAtMs, long UpdatedAtMs, long? ResolvedAtMs, string? Resolution, string? Actor);

    /// <summary>
    /// The review queue (intel.db <c>kn_review_items</c>): everything that needs Klives's decision, deduplicated by
    /// (kind, natural key) so re-running a module refreshes an item instead of piling up copies. Items whose condition
    /// disappears (the evidence changed) are closed automatically with the reason; decisions themselves are gold labels
    /// in operator.db, so they survive rebuilds.
    /// </summary>
    public sealed class ReviewQueue
    {
        private readonly OmniscienceStores stores;
        private readonly IClock clock;

        public ReviewQueue(OmniscienceStores stores, IClock clock)
        {
            this.stores = stores;
            this.clock = clock;
        }

        public static string ItemIdFor(string kind, string key) => NaturalKeys.Id("rv", kind + "\u0001" + key, 20);

        /// <summary>Opens or refreshes an item (a dismissed or resolved item with the same key stays closed).</summary>
        public Task<string> RaiseAsync(string kind, string? subjectId, string naturalKey, string title, object? detail, double priority, long sourceSeq, CancellationToken ct)
        {
            string id = ItemIdFor(kind, naturalKey);
            long now = clock.NowMs;
            string? json = detail == null ? null : JsonSerializer.Serialize(detail, KernelJson.Options);
            return stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO kn_review_items(item_id, kind, subject_id, natural_key, title, detail_json, priority, state, created_at_ms, updated_at_ms, source_seq)
                    VALUES($id,$k,$s,$key,$t,$d,$p,'open',$n,$n,$seq)
                    ON CONFLICT(item_id) DO UPDATE SET title=excluded.title, detail_json=excluded.detail_json, priority=excluded.priority,
                        updated_at_ms=excluded.updated_at_ms, source_seq=excluded.source_seq
                    WHERE kn_review_items.state='open'
                    """, ("$id", id), ("$k", kind), ("$s", subjectId), ("$key", naturalKey), ("$t", title), ("$d", json), ("$p", Math.Round(priority, 4)),
                    ("$n", now), ("$seq", sourceSeq));
                return id;
            }, ct);
        }

        /// <summary>Closes open items of <paramref name="kind"/> about <paramref name="subjectId"/> whose key is not in <paramref name="stillTrue"/>.</summary>
        public Task<int> AutoResolveAsync(string kind, string subjectId, IReadOnlySet<string> stillTrue, string reason, CancellationToken ct)
        {
            long now = clock.NowMs;
            return stores.Intel.WriteAsync((c, tx) =>
            {
                var open = c.Query(tx, "SELECT item_id, natural_key FROM kn_review_items WHERE kind=$k AND subject_id=$s AND state='open'",
                    r => (Id: r.GetString(0), Key: r.GetStringOrNull(1)), ("$k", kind), ("$s", subjectId));
                int n = 0;
                foreach (var item in open.Where(o => o.Key == null || !stillTrue.Contains(o.Key)))
                    n += c.Exec(tx, "UPDATE kn_review_items SET state='resolved', resolution=$r, resolved_at_ms=$t, updated_at_ms=$t, actor='system' WHERE item_id=$i",
                        ("$r", reason), ("$t", now), ("$i", item.Id));
                return n;
            }, ct);
        }

        public Task<bool> ResolveAsync(string itemId, string state, string resolution, string actor, CancellationToken ct)
        {
            if (state is not (ReviewStates.Resolved or ReviewStates.Dismissed)) throw new ArgumentException("State must be resolved or dismissed.", nameof(state));
            long now = clock.NowMs;
            return stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE kn_review_items SET state=$st, resolution=$r, actor=$a, resolved_at_ms=$t, updated_at_ms=$t WHERE item_id=$i AND state='open'",
                ("$st", state), ("$r", resolution), ("$a", actor), ("$t", now), ("$i", itemId)) == 1, ct);
        }

        public ReviewItem? Get(string itemId)
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, $"SELECT {Columns} FROM kn_review_items WHERE item_id=$i", Map, ("$i", itemId)).FirstOrDefault();
        }

        /// <summary>Keyset page sorted by priority (cursor "priority|itemId") or by age (cursor "createdAt|itemId").</summary>
        public (IReadOnlyList<ReviewItem> Items, string? NextCursor) Page(string? state, string? kind, string? subjectId, bool byAge, string? cursor, int limit)
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (state != null) { where.Add("state=$st"); args.Add(("$st", state)); }
            if (kind != null) { where.Add("kind=$k"); args.Add(("$k", kind)); }
            if (subjectId != null) { where.Add("subject_id=$s"); args.Add(("$s", subjectId)); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0) throw new ArgumentException("Malformed cursor.");
                string head = cursor[..bar], id = cursor[(bar + 1)..];
                if (byAge)
                {
                    if (!long.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw new ArgumentException("Malformed cursor.");
                    where.Add("(created_at_ms < $ca OR (created_at_ms = $ca AND item_id < $ci))");
                    args.Add(("$ca", at));
                }
                else
                {
                    if (!double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) throw new ArgumentException("Malformed cursor.");
                    where.Add("(priority < $cp OR (priority = $cp AND item_id < $ci))");
                    args.Add(("$cp", p));
                }
                args.Add(("$ci", id));
            }
            int lim = Math.Clamp(limit, 1, 500);
            args.Add(("$l", lim + 1));
            string order = byAge ? "created_at_ms DESC, item_id DESC" : "priority DESC, item_id DESC";
            using var c = stores.Intel.OpenRead();
            var rows = c.Query(null, $"SELECT {Columns} FROM kn_review_items {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY {order} LIMIT $l", Map, args.ToArray());
            if (rows.Count <= lim) return (rows, null);
            var page = rows.Take(lim).ToList();
            var last = page[^1];
            string next = (byAge ? last.CreatedAtMs.ToString(CultureInfo.InvariantCulture) : last.Priority.ToString("R", CultureInfo.InvariantCulture)) + "|" + last.ItemId;
            return (page, next);
        }

        private const string Columns = "item_id, kind, subject_id, natural_key, title, detail_json, priority, state, created_at_ms, updated_at_ms, resolved_at_ms, resolution, actor";

        private static ReviewItem Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3),
            r.GetString(4), r.GetStringOrNull(5), r.GetDouble(6), r.GetString(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64OrNull(10), r.GetStringOrNull(11), r.GetStringOrNull(12));
    }
}
