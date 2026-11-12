using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>A chat's settings (operator.db <c>context_json</c>). v2 chats migrated with <c>contextKind</c> and <c>summary</c> read the same way.</summary>
    public sealed record ChatContext
    {
        public string Kind { get; init; } = ChatKinds.Chat;
        public string? ContextKind { get; init; }
        public string? AddresseePersonId { get; init; }
        public IReadOnlyList<string>? Members { get; init; }
        public string? Topic { get; init; }
        public string? ConversationId { get; init; }
        public int? Seed { get; init; }
        public bool Fidelity { get; init; }
        public string? Summary { get; init; }
        public string? RunId { get; init; }
    }

    public static class ChatKinds
    {
        public const string Chat = "chat";
        public const string Group = "group_simulation";
        /// <summary>operator.db needs a person id on every chat; a group simulation uses this marker (its members are in the context).</summary>
        public const string GroupPersonMarker = "group";
    }

    public static class TurnStatuses
    {
        public const string Pending = "pending";
        public const string Answered = "answered";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Superseded = "superseded";
        /// <summary>An assistant line replaced by a regeneration (kept in the transcript, out of the conversation).</summary>
        public const string Replaced = "replaced";
    }

    /// <summary>The fields of a chat line's <c>meta_json</c> the replica reads (unknown v2 fields are preserved on update).</summary>
    public sealed record TurnMeta
    {
        public string? TurnId { get; init; }
        public string? Status { get; init; }
        public string? Error { get; init; }
        public string? VersionId { get; init; }
        public string? GenerationId { get; init; }
        public int? BurstIndex { get; init; }
        public int? BurstSize { get; init; }
        public bool? Simulated { get; init; }
        public string? PersonId { get; init; }
        public string? Speaker { get; init; }
        public int? Rating { get; init; }
        public string? AnsweredBy { get; init; }
        public long? V2MessageId { get; init; }
    }

    public sealed record ChatRecord(string ChatId, string PersonId, string? Title, long? AsOfMs, ChatContext Context, bool Archived, long CreatedAtMs, long UpdatedAtMs);

    public sealed record ChatLine(int Seq, string Role, string Content, TurnMeta Meta, string? MetaJson, long AtMs);

    /// <summary>
    /// Replica chats in operator.db (Klives-authored transcripts, never rebuilt). Writes go through operator's single
    /// writer; sequence numbers are allocated inside the inserting transaction, so concurrent sends never collide.
    /// </summary>
    public sealed class ChatStore
    {
        private readonly SqliteStore op;

        public ChatStore(SqliteStore operatorStore) => op = operatorStore;

        public static ChatContext ParseContext(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ChatContext();
            return RJson.Read<ChatContext>(json) ?? new ChatContext();
        }

        public static TurnMeta ParseMeta(string? json) => RJson.Read<TurnMeta>(json) ?? new TurnMeta();

        /// <summary>Merges <paramref name="patch"/> into existing meta JSON, keeping fields the replica does not know (v2's).</summary>
        public static string MergeMeta(string? existing, object patch)
        {
            JsonObject target;
            try { target = (JsonNode.Parse(string.IsNullOrWhiteSpace(existing) ? "{}" : existing) as JsonObject) ?? new JsonObject(); }
            catch (JsonException) { target = new JsonObject(); }
            var add = JsonSerializer.SerializeToNode(patch, Api.ApiJson.Options) as JsonObject ?? new JsonObject();
            foreach (var (key, value) in add.ToList())
            {
                add.Remove(key);
                if (value != null) target[key] = value; // a patch only sets; absent fields are left as they were
            }
            return target.ToJsonString();
        }

        private const string ChatColumns = "chat_id, person_id, title, as_of_ms, context_json, archived, created_at_ms, updated_at_ms";

        private static ChatRecord MapChat(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetInt64OrNull(3), ParseContext(r.GetStringOrNull(4)),
            r.GetInt32(5) == 1, r.GetInt64(6), r.GetInt64(7));

        public ChatRecord? Get(string chatId)
        {
            using var c = op.OpenRead();
            return c.Query(null, $"SELECT {ChatColumns} FROM replica_chats WHERE chat_id=$c", MapChat, ("$c", chatId)).FirstOrDefault();
        }

        /// <summary>Chats newest-updated first, keyset-paged by (updated_at, chat_id).</summary>
        public (IReadOnlyList<ChatRecord> Items, string? Next) List(string? personId, string? kind, bool includeArchived, string? cursor, int limit)
        {
            var where = new List<string>();
            var p = new List<(string, object?)>();
            if (personId != null) { where.Add("person_id=$p"); p.Add(("$p", personId)); }
            if (kind == ChatKinds.Group) where.Add("person_id='" + ChatKinds.GroupPersonMarker + "'");
            else if (kind == ChatKinds.Chat) where.Add("person_id<>'" + ChatKinds.GroupPersonMarker + "'");
            if (!includeArchived) where.Add("archived=0");
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor.AsSpan(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw new ArgumentException("Malformed cursor.");
                where.Add("(updated_at_ms < $at OR (updated_at_ms = $at AND chat_id < $id))");
                p.Add(("$at", at));
                p.Add(("$id", cursor[(bar + 1)..]));
            }
            p.Add(("$l", Math.Clamp(limit, 1, 200) + 1));
            using var c = op.OpenRead();
            var rows = c.Query(null, $"SELECT {ChatColumns} FROM replica_chats {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY updated_at_ms DESC, chat_id DESC LIMIT $l",
                MapChat, p.ToArray());
            if (rows.Count <= limit) return (rows, null);
            var page = rows.Take(limit).ToList();
            return (page, page[^1].UpdatedAtMs.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].ChatId);
        }

        public IReadOnlyList<ChatLine> Lines(string chatId, int afterSeq = 0, int limit = 100_000)
        {
            using var c = op.OpenRead();
            return c.Query(null, "SELECT seq, role, content, meta_json, at_ms FROM replica_chat_messages WHERE chat_id=$c AND seq > $s ORDER BY seq LIMIT $l",
                r => new ChatLine(r.GetInt32(0), r.GetString(1), r.GetString(2), ParseMeta(r.GetStringOrNull(3)), r.GetStringOrNull(3), r.GetInt64(4)),
                ("$c", chatId), ("$s", afterSeq), ("$l", limit));
        }

        public Task CreateAsync(ChatRecord chat, CancellationToken ct) =>
            op.WriteAsync((c, tx) => c.Exec(tx, $"INSERT INTO replica_chats({ChatColumns}) VALUES($id,$p,$t,$a,$cx,0,$now,$now)",
                ("$id", chat.ChatId), ("$p", chat.PersonId), ("$t", chat.Title), ("$a", chat.AsOfMs), ("$cx", RJson.Write(chat.Context)), ("$now", chat.CreatedAtMs)), ct);

        /// <summary>Appends lines in one transaction; returns their sequence numbers.</summary>
        public Task<IReadOnlyList<int>> AppendAsync(string chatId, IReadOnlyList<(string Role, string Content, object Meta)> lines, long atMs, CancellationToken ct,
            Action<SqliteConnection, SqliteTransaction>? also = null) =>
            op.WriteAsync<IReadOnlyList<int>>((c, tx) =>
            {
                int seq = (int)(c.Scalar<long?>(tx, "SELECT MAX(seq) FROM replica_chat_messages WHERE chat_id=$c", ("$c", chatId)) ?? 0);
                var seqs = new List<int>();
                foreach (var (role, content, meta) in lines)
                {
                    seq++;
                    c.Exec(tx, "INSERT INTO replica_chat_messages(chat_id, seq, role, content, meta_json, at_ms) VALUES($c,$s,$r,$t,$m,$a)",
                        ("$c", chatId), ("$s", seq), ("$r", role), ("$t", content), ("$m", JsonSerializer.Serialize(meta, Api.ApiJson.Options)), ("$a", atMs));
                    seqs.Add(seq);
                }
                c.Exec(tx, "UPDATE replica_chats SET updated_at_ms=$t WHERE chat_id=$c", ("$t", atMs), ("$c", chatId));
                also?.Invoke(c, tx);
                return seqs;
            }, ct);

        /// <summary>Patches the meta of every line matching <paramref name="match"/> (one transaction).</summary>
        public Task<int> PatchAsync(string chatId, Func<ChatLine, bool> match, object patch, CancellationToken ct) =>
            op.WriteAsync((c, tx) =>
            {
                var lines = c.Query(tx, "SELECT seq, role, content, meta_json, at_ms FROM replica_chat_messages WHERE chat_id=$c ORDER BY seq",
                    r => new ChatLine(r.GetInt32(0), r.GetString(1), r.GetString(2), ParseMeta(r.GetStringOrNull(3)), r.GetStringOrNull(3), r.GetInt64(4)), ("$c", chatId));
                int n = 0;
                foreach (var line in lines.Where(match))
                {
                    c.Exec(tx, "UPDATE replica_chat_messages SET meta_json=$m WHERE chat_id=$c AND seq=$s", ("$m", MergeMeta(line.MetaJson, patch)), ("$c", chatId), ("$s", line.Seq));
                    n++;
                }
                return n;
            }, ct);

        public Task UpdateAsync(string chatId, string? title, bool? archived, long nowMs, CancellationToken ct) =>
            op.WriteAsync((c, tx) => c.Exec(tx, "UPDATE replica_chats SET title=COALESCE($t,title), archived=COALESCE($a,archived), updated_at_ms=$n WHERE chat_id=$c",
                ("$t", title), ("$a", archived is bool b ? (b ? 1 : 0) : null), ("$n", nowMs), ("$c", chatId)), ct);

        public Task UpdateContextAsync(string chatId, ChatContext context, CancellationToken ct) =>
            op.WriteAsync((c, tx) => c.Exec(tx, "UPDATE replica_chats SET context_json=$x WHERE chat_id=$c", ("$x", RJson.Write(context)), ("$c", chatId)), ct);

        public Task<bool> DeleteAsync(string chatId, CancellationToken ct) =>
            op.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM replica_chat_messages WHERE chat_id=$c", ("$c", chatId));
                return c.Exec(tx, "DELETE FROM replica_chats WHERE chat_id=$c", ("$c", chatId)) == 1;
            }, ct);

        public static ReplicaChatDto Dto(ChatRecord c) => new(c.ChatId, c.Context.Kind, c.PersonId, c.Title, c.AsOfMs, ContextKinds.Normalize(c.Context.ContextKind),
            c.Context.AddresseePersonId, c.Archived, c.CreatedAtMs, c.UpdatedAtMs, c.Context.Members ?? (IReadOnlyList<string>)Array.Empty<string>(), c.Context.Topic);

        public static ReplicaChatMessageDto Dto(string chatId, ChatLine l) => new(chatId, l.Seq, l.Role, l.Content, l.AtMs, l.Meta.PersonId, l.Meta.Speaker, l.Meta.TurnId,
            l.Meta.Status, l.Meta.Error, l.Role == "assistant", l.Meta.VersionId, l.Meta.GenerationId, l.Meta.BurstIndex, l.Meta.BurstSize);
    }
}
