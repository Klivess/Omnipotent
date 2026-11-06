using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    public sealed partial class LegacyImporter
    {
        public const string LegacyActor = "klives (v2)";

        /// <summary>Canonical JSON (sorted keys) for natural keys: equal content always gives equal text.</summary>
        internal static string NaturalKey(IReadOnlyDictionary<string, object?> parts) =>
            JsonSerializer.Serialize(new SortedDictionary<string, object?>(parts.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal), KernelJson.Options);

        /// <summary>
        /// Everything Klives authored in v2, into operator.db: tiers and pins, notes, watchlists, saved queries, replica
        /// chats with their messages, and his review decisions (identity links, replica ratings, manual facts,
        /// dismissed suggestions) as gold labels keyed by natural keys (plan R3). Idempotent: rows carry v2-derived ids.
        /// </summary>
        private async Task ImportOperatorDataAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, CancellationToken ct)
        {
            var tracked = legacy.HasTable("person_profile_targets")
                ? legacy.Connection.Query(null, "SELECT person_id FROM person_profile_targets WHERE enabled=1", r => r.Text(0) ?? "").ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var pins = legacy.HasTable("workspace_pins")
                ? legacy.Connection.Query(null, "SELECT person_id, pinned_at FROM workspace_pins", r => (Person: r.Text(0) ?? "", At: r.Raw(1)))
                : new List<(string Person, object? At)>();
            var workspaceNotes = ReadAll(legacy, "workspace_notes", "note_id, person_id, content, tags, created_at, updated_at",
                r => (Id: r.Text(1), Person: r.Text(2), Body: r.Text(3), Tags: r.Text(4), Created: r.Raw(5), Updated: r.Raw(6)));
            var watchlists = ReadAll(legacy, "watchlists", "watch_id, label, terms, person_id, enabled, notify, created_at",
                r => (Id: r.Text(1), Label: r.Text(2), Terms: r.Text(3), Person: NullIfBlank(r.Text(4)), Enabled: r.Number(5) ?? 1, Notify: r.Number(6) ?? 1, Created: r.Raw(7)));
            var savedQueries = ReadAll(legacy, "workspace_saved_queries", "query_id, name, query_json, created_at, updated_at, use_count",
                r => (Id: r.Text(1), Name: r.Text(2), Json: r.Text(3), Created: r.Raw(4), Updated: r.Raw(5), Uses: r.Number(6) ?? 0));
            var chats = ReadAll(legacy, "replica_chats",
                $"chat_id, person_id, title, created_at, updated_at, archived, {legacy.ColumnOrNull("replica_chats", "as_of_at")}, " +
                $"{legacy.ColumnOrNull("replica_chats", "context_kind")}, {legacy.ColumnOrNull("replica_chats", "summary")}",
                r => (Id: r.Number(1) ?? 0, Person: r.Text(2), Title: r.Text(3), Created: r.Raw(4), Updated: r.Raw(5), Archived: r.Number(6) ?? 0,
                      AsOf: r.Raw(7), Context: r.Text(8), Summary: r.Text(9)));
            var chatMessages = ReadAll(legacy, "replica_chat_messages",
                $"message_id, chat_id, role, content, sent_at, {legacy.ColumnOrNull("replica_chat_messages", "prompt_token_count")}, " +
                $"{legacy.ColumnOrNull("replica_chat_messages", "completion_token_count")}, {legacy.ColumnOrNull("replica_chat_messages", "latency_ms")}, " +
                $"{legacy.ColumnOrNull("replica_chat_messages", "model_used")}, {legacy.ColumnOrNull("replica_chat_messages", "evidence_json")}, " +
                $"{legacy.ColumnOrNull("replica_chat_messages", "style_json")}, {legacy.ColumnOrNull("replica_chat_messages", "generation_version")}",
                r => (Id: r.Number(1) ?? 0, Chat: r.Number(2) ?? 0, Role: r.Text(3), Content: r.Text(4), At: r.Raw(5), Prompt: r.Number(6), Completion: r.Number(7),
                      Latency: r.Number(8), Model: r.Text(9), Evidence: r.Text(10), Style: r.Text(11), Generation: r.Text(12)));
            var feedback = ReadAll(legacy, "replica_feedback", "feedback_id, person_id, chat_id, chat_message_id, rating, note, created_at",
                r => (Id: r.Text(1), Person: r.Text(2), Chat: r.Number(3), ChatMessage: r.Number(4), Rating: r.Number(5) ?? 0, Note: r.Text(6), Created: r.Raw(7)));
            var links = ReadAll(legacy, "person_link_suggestions", "person_id_a, person_id_b, status, reason, score, created_at",
                r => (A: r.Text(1), B: r.Text(2), Status: r.Text(3), Reason: r.Text(4), Score: r.IsDBNull(5) ? (double?)null : r.GetDouble(5), Created: r.Raw(6)));
            var manualFacts = legacy.HasTable("person_facts")
                ? legacy.Connection.Query(null, "SELECT person_id, category, fact_text, confidence, status, created_at FROM person_facts WHERE extracted_by='manual'",
                    r => (Person: r.Text(0), Category: r.Text(1), Text: r.Text(2), Confidence: r.IsDBNull(3) ? (double?)null : r.GetDouble(3), Status: r.Text(4), Created: r.Raw(5)))
                : new List<(string? Person, string? Category, string? Text, double? Confidence, string? Status, object? Created)>();
            var dismissedTargets = legacy.HasTable("target_suggestions")
                ? legacy.Connection.Query(null, "SELECT person_id, computed_at FROM target_suggestions WHERE dismissed=1", r => (Person: r.Text(0), At: r.Raw(1)))
                : new List<(string? Person, object? At)>();

            long now = clock.NowMs;
            var tr = report.Table("operator");
            var counts = await stores.Operator.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                void Count(int changed) => bc += changed > 0 ? new BatchCounts(1, 0, 0, 0) : new BatchCounts(0, 0, 1, 0);

                // Tiers and pins.
                var pinned = pins.Where(p => p.Person.Length > 0).ToDictionary(p => p.Person, p => p.At, StringComparer.Ordinal);
                foreach (var (id, person) in ctx.Persons)
                {
                    string tier = NullIfBlank(person.Tier) ?? (tracked.Contains(id) ? "tracked" : "archive");
                    if (tracked.Contains(id)) tier = "tracked";
                    bool isPinned = pinned.ContainsKey(id);
                    if (tier == "archive" && !isPinned) continue;
                    Count(c.Exec(tx, """
                        INSERT INTO person_settings(person_id, tier, pinned, updated_at_ms) VALUES($p,$t,$pin,$n)
                        ON CONFLICT(person_id) DO UPDATE SET tier=excluded.tier, pinned=excluded.pinned, updated_at_ms=excluded.updated_at_ms
                        WHERE person_settings.tier <> excluded.tier OR person_settings.pinned <> excluded.pinned
                        """, ("$p", id), ("$t", tier), ("$pin", isPinned ? 1 : 0), ("$n", now)));
                }

                // Notes: v2's free-text person notes and its workspace notes.
                foreach (var (id, person) in ctx.Persons)
                {
                    if (NullIfBlank(person.Notes) is not { } body) continue;
                    Count(Upsert(c, tx, "notes", "note_id", $"v2-person-notes:{id}", """
                        INSERT INTO notes(note_id, subject_kind, subject_id, body, created_at_ms, updated_at_ms) VALUES($id,'person',$s,$b,$c,$u)
                        ON CONFLICT(note_id) DO UPDATE SET body=excluded.body, updated_at_ms=excluded.updated_at_ms WHERE notes.body <> excluded.body
                        """, ("$s", id), ("$b", body), ("$c", Ms(person.CreatedAt, now)), ("$u", Ms(person.UpdatedAt, now))));
                }
                foreach (var (_, n) in workspaceNotes)
                {
                    if (n.Id == null || n.Person == null || NullIfBlank(n.Body) is not { } body) continue;
                    Count(Upsert(c, tx, "notes", "note_id", $"v2-note:{n.Id}", """
                        INSERT INTO notes(note_id, subject_kind, subject_id, body, tags, created_at_ms, updated_at_ms) VALUES($id,'person',$s,$b,$tg,$c,$u)
                        ON CONFLICT(note_id) DO UPDATE SET body=excluded.body, tags=excluded.tags, updated_at_ms=excluded.updated_at_ms
                        WHERE notes.body <> excluded.body OR notes.tags IS NOT excluded.tags
                        """, ("$s", n.Person), ("$b", body), ("$tg", n.Tags), ("$c", Ms(n.Created, now)), ("$u", Ms(n.Updated, now))));
                }

                // Watchlists (keyword watches, optionally scoped to one person).
                foreach (var (_, w) in watchlists)
                {
                    if (w.Id == null || NullIfBlank(w.Label) is not { } label) continue;
                    var terms = (w.Terms ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string definition = JsonSerializer.Serialize(new { terms, personId = w.Person, notify = w.Notify != 0 }, KernelJson.Options);
                    long created = Ms(w.Created, now);
                    Count(Upsert(c, tx, "watchlists", "watch_id", $"v2-watch:{w.Id}", """
                        INSERT INTO watchlists(watch_id, kind, name, definition_json, enabled, created_at_ms, updated_at_ms) VALUES($id,'keywords',$n,$d,$e,$c,$c)
                        ON CONFLICT(watch_id) DO UPDATE SET name=excluded.name, definition_json=excluded.definition_json, enabled=excluded.enabled, updated_at_ms=$now
                        WHERE watchlists.name <> excluded.name OR watchlists.definition_json <> excluded.definition_json OR watchlists.enabled <> excluded.enabled
                        """, ("$n", label), ("$d", definition), ("$e", w.Enabled != 0 ? 1 : 0), ("$c", created), ("$now", now)));
                }

                // Saved workspace queries become saved views.
                foreach (var (_, q) in savedQueries)
                {
                    if (q.Id == null || NullIfBlank(q.Name) is not { } name || q.Json == null) continue;
                    string state = JsonSerializer.Serialize(new { legacyQuery = q.Json }, KernelJson.Options);
                    Count(Upsert(c, tx, "saved_views", "view_id", $"v2-query:{q.Id}", """
                        INSERT INTO saved_views(view_id, page, name, state_json, use_count, created_at_ms, updated_at_ms) VALUES($id,'explore',$n,$s,$u,$c,$up)
                        ON CONFLICT(view_id) DO UPDATE SET name=excluded.name, state_json=excluded.state_json, use_count=excluded.use_count, updated_at_ms=excluded.updated_at_ms
                        WHERE saved_views.name <> excluded.name OR saved_views.state_json <> excluded.state_json OR saved_views.use_count <> excluded.use_count
                        """, ("$n", name), ("$s", state), ("$u", q.Uses), ("$c", Ms(q.Created, now)), ("$up", Ms(q.Updated, now))));
                }

                // Replica chats and their turns.
                var chatIds = new HashSet<long>();
                foreach (var (_, chat) in chats)
                {
                    if (chat.Id <= 0 || chat.Person == null) continue;
                    chatIds.Add(chat.Id);
                    string context = JsonSerializer.Serialize(new { contextKind = chat.Context, summary = chat.Summary }, KernelJson.Options);
                    Count(Upsert(c, tx, "replica_chats", "chat_id", $"v2-chat:{chat.Id}", """
                        INSERT INTO replica_chats(chat_id, person_id, title, as_of_ms, context_json, archived, created_at_ms, updated_at_ms)
                        VALUES($id,$p,$t,$a,$cx,$ar,$c,$u)
                        ON CONFLICT(chat_id) DO UPDATE SET title=excluded.title, as_of_ms=excluded.as_of_ms, context_json=excluded.context_json,
                            archived=excluded.archived, updated_at_ms=excluded.updated_at_ms
                        WHERE replica_chats.updated_at_ms <> excluded.updated_at_ms OR replica_chats.archived <> excluded.archived
                        """, ("$p", chat.Person), ("$t", chat.Title), ("$a", MsOrNull(chat.AsOf)), ("$cx", context), ("$ar", chat.Archived != 0 ? 1 : 0),
                        ("$c", Ms(chat.Created, now)), ("$u", Ms(chat.Updated, now))));
                }
                foreach (var group in chatMessages.Where(m => chatIds.Contains(m.Row.Chat)).GroupBy(m => m.Row.Chat))
                {
                    int seq = 0;
                    foreach (var (_, m) in group.OrderBy(m => m.Row.Id))
                    {
                        seq++;
                        if (m.Role == null || m.Content == null) continue;
                        string meta = JsonSerializer.Serialize(new
                        {
                            v2MessageId = m.Id, model = m.Model, promptTokens = m.Prompt, completionTokens = m.Completion, latencyMs = m.Latency,
                            evidence = m.Evidence, style = m.Style, generation = m.Generation,
                        }, KernelJson.Options);
                        Count(c.Exec(tx, """
                            INSERT OR IGNORE INTO replica_chat_messages(chat_id, seq, role, content, meta_json, at_ms) VALUES($c,$s,$r,$t,$m,$a)
                            """, ("$c", $"v2-chat:{group.Key}"), ("$s", seq), ("$r", m.Role), ("$t", m.Content), ("$m", meta), ("$a", Ms(m.At, now))));
                    }
                }

                // Review decisions as gold labels (natural keys; the intel store can be rebuilt without losing one).
                foreach (var (_, f) in feedback)
                {
                    if (f.Person == null) continue;
                    string key = NaturalKey(new Dictionary<string, object?>
                    {
                        ["personId"] = f.Person, ["chatId"] = f.Chat == null ? null : $"v2-chat:{f.Chat}", ["v2ChatMessageId"] = f.ChatMessage,
                    });
                    string verdict = f.Rating > 0 ? "good" : f.Rating < 0 ? "bad" : "neutral";
                    Count(InsertGold(c, tx, "replica.reply_rating", key, verdict, new { rating = f.Rating, note = f.Note }, f.Note, Ms(f.Created, now)));
                }
                foreach (var (_, l) in links)
                {
                    if (l.A == null || l.B == null || l.Status is not ("accepted" or "rejected")) continue;
                    var pair = new[] { l.A, l.B }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    string key = NaturalKey(new Dictionary<string, object?> { ["persons"] = pair });
                    Count(InsertGold(c, tx, "identity.link", key, l.Status, new { reason = l.Reason, score = l.Score }, null, Ms(l.Created, now)));
                }
                foreach (var f in manualFacts)
                {
                    if (f.Person == null || NullIfBlank(f.Text) is not { } text) continue;
                    string key = NaturalKey(new Dictionary<string, object?> { ["personId"] = f.Person, ["category"] = f.Category, ["text"] = text });
                    Count(InsertGold(c, tx, "fact.manual", key, f.Status == "active" || f.Status == null ? "asserted" : f.Status!,
                        new { confidence = f.Confidence, status = f.Status }, null, Ms(f.Created, now)));
                }
                foreach (var d in dismissedTargets)
                {
                    if (d.Person == null) continue;
                    Count(c.Exec(tx, "INSERT OR IGNORE INTO dismissals(kind, key, until_ms, at_ms) VALUES('target.suggestion',$k,NULL,$t)",
                        ("$k", d.Person), ("$t", Ms(d.At, now))));
                }
                return bc;
            }, ct).ConfigureAwait(false);
            tr.Add(counts, (int)Math.Min(int.MaxValue, counts.Imported + counts.Skipped));
        }

        /// <summary>Runs an upsert whose first parameter is <c>$id</c>; returns rows changed.</summary>
        private static int Upsert(SqliteConnection c, SqliteTransaction tx, string table, string idColumn, string id, string sql, params (string, object?)[] args) =>
            c.Exec(tx, sql, args.Prepend(("$id", (object?)id)).ToArray());

        private static int InsertGold(SqliteConnection c, SqliteTransaction tx, string kind, string naturalKey, string verdict, object? correction, string? note, long decidedAt)
        {
            bool exists = c.Scalar<long>(tx, "SELECT COUNT(*) FROM gold_labels WHERE kind=$k AND natural_key=$n AND actor=$a", ("$k", kind), ("$n", naturalKey), ("$a", LegacyActor)) > 0;
            if (exists) return 0;
            return c.Exec(tx, """
                INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES($k,$n,$v,$c,$note,$t,$a)
                """, ("$k", kind), ("$n", naturalKey), ("$v", verdict), ("$c", correction == null ? null : JsonSerializer.Serialize(correction, KernelJson.Options)),
                ("$note", note), ("$t", decidedAt), ("$a", LegacyActor));
        }
    }
}
