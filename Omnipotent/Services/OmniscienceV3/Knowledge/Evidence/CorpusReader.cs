using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>One message as the knowledge layer sees it: its current version, author resolved to a canonical person.</summary>
    public sealed record CorpusMessage(
        string MessageId,
        string Platform,
        string ConversationId,
        string? GuildId,
        string AuthorIdentityId,
        string? AuthorPersonId,
        long SentAtMs,
        TimeQuality Quality,
        long OrderKey,
        int Type,
        bool IsBot,
        bool IsSystem,
        bool IsWebhook,
        string? ReplyToMessageId,
        string? ForwardedJson,
        int Version,
        string? Content,
        long? EditedAtMs,
        long? DeletedAtMs,
        string FirstSourceKind,
        string BestSourceId,
        long IngestSeq);

    public sealed record ConversationInfo(string ConversationId, string Platform, string Kind, string? GuildId, string? Title, string? GuildName);

    public sealed record IdentityInfo(string IdentityId, string Platform, string PlatformUserId, string PersonId, string Kind);

    /// <summary>A name an identity carried for a period (usernames and display names change; plan T5).</summary>
    public sealed record IdentityName(string IdentityId, string Kind, string Value, long ValidFromMs, long? ValidToMs);

    /// <summary>
    /// Read-only access to corpus.db and operator.db for the knowledge layer. Every query is an index lookup (author/time,
    /// conversation/time, reply, mention, identity-by-person), parameterised, and resolves people through the canonical
    /// map so a merge never splits a person's evidence. Messages are ordered by (sent_at, platform order, ingest_seq) so
    /// minute-precision imports keep their export order (plan R4, F181).
    /// </summary>
    public sealed class CorpusReader
    {
        private readonly OmniscienceStores stores;
        private readonly IPolicyOracle policy;
        private readonly ConcurrentDictionary<string, (bool IsSelf, long AtMs)> selfCache = new(StringComparer.Ordinal);

        public CorpusReader(OmniscienceStores stores, IPolicyOracle policy)
        {
            this.stores = stores;
            this.policy = policy;
        }

        public OmniscienceStores Stores => stores;

        public string Canonical(string personId) => policy.Canonical(personId);

        /// <summary>The canonical id and every person id merged into it.</summary>
        public IReadOnlyList<string> PersonIdsOf(string personId)
        {
            string canonical = Canonical(personId);
            using var c = stores.Corpus.OpenRead();
            var ids = c.Query(null, "SELECT person_id FROM person_canonical WHERE canonical_id=$c", r => r.GetString(0), ("$c", canonical));
            if (!ids.Contains(canonical, StringComparer.Ordinal)) ids.Insert(0, canonical);
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }

        public bool PersonExists(string personId)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<long?>(null, "SELECT 1 FROM persons WHERE person_id=$p", ("$p", personId)) != null;
        }

        public string? DisplayName(string personId)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", Canonical(personId)));
        }

        /// <summary>Klives's own persons (every account he uses forms the explicit "self" set, plan R5).</summary>
        public bool IsSelf(string personId)
        {
            string canonical = Canonical(personId);
            if (selfCache.TryGetValue(canonical, out var hit)) return hit.IsSelf;
            using var c = stores.Corpus.OpenRead();
            bool self = c.Scalar<long?>(null, """
                SELECT 1 FROM persons p WHERE p.is_self=1 AND (p.person_id=$c OR p.person_id IN (SELECT person_id FROM person_canonical WHERE canonical_id=$c)) LIMIT 1
                """, ("$c", canonical)) != null;
            selfCache[canonical] = (self, 0);
            return self;
        }

        public IReadOnlyList<string> SelfPersonIds()
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, "SELECT person_id FROM persons WHERE is_self=1", r => r.GetString(0)).Select(Canonical).Distinct(StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<IdentityInfo> IdentitiesOf(IReadOnlyCollection<string> personIds)
        {
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, "SELECT identity_id, platform, platform_user_id, person_id, kind FROM identities WHERE person_id IN ({keys})", personIds,
                r => new IdentityInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }

        public IdentityInfo? Identity(string identityId)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, "SELECT identity_id, platform, platform_user_id, person_id, kind FROM identities WHERE identity_id=$i",
                r => new IdentityInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$i", identityId)).FirstOrDefault();
        }

        /// <summary>Names (display, username, nickname…) of identities valid at any point of [fromMs, toMs].</summary>
        public IReadOnlyList<IdentityName> NamesOf(IReadOnlyCollection<string> identityIds, long fromMs = long.MinValue, long toMs = long.MaxValue)
        {
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, "SELECT identity_id, name_kind, value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id IN ({keys})", identityIds,
                    r => new IdentityName(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64OrNull(4)))
                .Where(n => n.ValidFromMs <= toMs && (n.ValidToMs == null || n.ValidToMs >= fromMs))
                .ToList();
        }

        /// <summary>The name an identity showed at an instant ("Bob" in 2021 is whoever was "Bob" in 2021): display name first.</summary>
        public string? NameAt(string identityId, long atMs)
        {
            var names = NamesOf(new[] { identityId }, atMs, atMs);
            return names.OrderBy(n => n.Kind == "display" ? 0 : n.Kind == "nickname" ? 1 : 2).ThenByDescending(n => n.ValidFromMs).Select(n => n.Value).FirstOrDefault()
                   ?? NamesOf(new[] { identityId }).OrderByDescending(n => n.ValidFromMs).Select(n => n.Value).FirstOrDefault();
        }

        /// <summary>Persons whose names (any identity, any period) match <paramref name="name"/> case-insensitively.</summary>
        public IReadOnlyList<(string PersonId, string Name, long ValidFromMs, long? ValidToMs)> FindByName(string name, int limit = 50)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, """
                SELECT i.person_id, n.value, n.valid_from_ms, n.valid_to_ms FROM identity_names n JOIN identities i ON i.identity_id=n.identity_id
                WHERE n.value = $n COLLATE NOCASE LIMIT $l
                """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64OrNull(3)), ("$n", name.Trim()), ("$l", limit))
                .Select(x => (Canonical(x.Item1), x.Item2, x.Item3, x.Item4)).ToList();
        }

        // ── messages ─────────────────────────────────────────────────────────────

        private const string MessageColumns =
            "m.message_id, m.platform, m.conversation_id, m.guild_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.sent_at_ms, m.time_quality, " +
            "m.order_key, m.type, m.is_bot, m.is_system, m.is_webhook, m.reply_to_message_id, m.forwarded_from_json, m.current_version, m.content, m.edited_at_ms, " +
            "m.deleted_at_ms, m.first_source_kind, m.best_source_id, m.ingest_seq";

        private const string MessageJoins =
            "LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id";

        private static CorpusMessage MapMessage(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64(6), (TimeQuality)r.GetInt32(7),
            r.GetInt64(8), r.GetInt32(9), r.GetInt32(10) == 1, r.GetInt32(11) == 1, r.GetInt32(12) == 1, r.GetStringOrNull(13), r.GetStringOrNull(14), r.GetInt32(15),
            r.GetStringOrNull(16), r.GetInt64OrNull(17), r.GetInt64OrNull(18), r.GetString(19), r.GetString(20), r.GetInt64(21));

        public static IEnumerable<CorpusMessage> Ordered(IEnumerable<CorpusMessage> messages) =>
            messages.OrderBy(m => m.SentAtMs).ThenBy(m => m.OrderKey).ThenBy(m => m.IngestSeq).ThenBy(m => m.MessageId, StringComparer.Ordinal);

        public IReadOnlyList<CorpusMessage> Messages(IReadOnlyCollection<string> messageIds)
        {
            if (messageIds.Count == 0) return Array.Empty<CorpusMessage>();
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.message_id IN ({{keys}})", messageIds, MapMessage);
        }

        public CorpusMessage? Message(string messageId) => Messages(new[] { messageId }).FirstOrDefault();

        /// <summary>Messages authored by any of the identities, in [fromMs, toMs], up to <paramref name="limit"/> (oldest first).</summary>
        public IReadOnlyList<CorpusMessage> AuthoredBy(IReadOnlyCollection<string> identityIds, long fromMs = long.MinValue, long toMs = long.MaxValue, int limit = 200_000)
        {
            var result = new List<CorpusMessage>();
            using var c = stores.Corpus.OpenRead();
            foreach (var identity in identityIds)
                result.AddRange(c.Query(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.author_identity_id=$a AND m.sent_at_ms BETWEEN $f AND $t ORDER BY m.sent_at_ms LIMIT $l",
                    MapMessage, ("$a", identity), ("$f", fromMs), ("$t", toMs), ("$l", limit)));
            return Ordered(result).Take(limit).ToList();
        }

        /// <summary>Most recent messages authored by the identities before <paramref name="beforeMs"/> (newest first, then re-ordered).</summary>
        public IReadOnlyList<CorpusMessage> RecentAuthoredBy(IReadOnlyCollection<string> identityIds, long beforeMs, int limit)
        {
            var result = new List<CorpusMessage>();
            using var c = stores.Corpus.OpenRead();
            foreach (var identity in identityIds)
                result.AddRange(c.Query(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.author_identity_id=$a AND m.sent_at_ms <= $t ORDER BY m.sent_at_ms DESC LIMIT $l",
                    MapMessage, ("$a", identity), ("$t", beforeMs), ("$l", limit)));
            return Ordered(result.OrderByDescending(m => m.SentAtMs).Take(limit)).ToList();
        }

        /// <summary>Replies to any of <paramref name="parentIds"/>.</summary>
        public IReadOnlyList<CorpusMessage> RepliesTo(IReadOnlyCollection<string> parentIds)
        {
            if (parentIds.Count == 0) return Array.Empty<CorpusMessage>();
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.reply_to_message_id IN ({{keys}})", parentIds, MapMessage);
        }

        /// <summary>Messages that @-mention any of the identities.</summary>
        public IReadOnlyList<CorpusMessage> Mentioning(IReadOnlyCollection<string> identityIds, long fromMs = long.MinValue, long toMs = long.MaxValue)
        {
            if (identityIds.Count == 0) return Array.Empty<CorpusMessage>();
            using var c = stores.Corpus.OpenRead();
            var ids = c.QueryIn(null, "SELECT message_id FROM message_mentions WHERE target_kind='user' AND target_id IN ({keys})", identityIds, r => r.GetString(0));
            return Messages(ids.Distinct(StringComparer.Ordinal).ToList()).Where(m => m.SentAtMs >= fromMs && m.SentAtMs <= toMs).ToList();
        }

        /// <summary>The identities a message @-mentions.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> MentionsOf(IReadOnlyCollection<string> messageIds)
        {
            if (messageIds.Count == 0) return new Dictionary<string, IReadOnlyList<string>>();
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", messageIds,
                    r => (r.GetString(0), r.GetString(1)))
                .GroupBy(x => x.Item1, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Item2).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        }

        /// <summary>Up to <paramref name="before"/> messages before and <paramref name="after"/> after an instant in one conversation.</summary>
        public IReadOnlyList<CorpusMessage> Window(string conversationId, long aroundMs, int before, int after)
        {
            using var c = stores.Corpus.OpenRead();
            var older = c.Query(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.conversation_id=$c AND m.sent_at_ms < $t ORDER BY m.sent_at_ms DESC, m.order_key DESC LIMIT $l",
                MapMessage, ("$c", conversationId), ("$t", aroundMs), ("$l", Math.Max(0, before)));
            var newer = c.Query(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.conversation_id=$c AND m.sent_at_ms >= $t ORDER BY m.sent_at_ms, m.order_key LIMIT $l",
                MapMessage, ("$c", conversationId), ("$t", aroundMs), ("$l", Math.Max(1, after + 1)));
            return Ordered(older.Concat(newer)).ToList();
        }

        /// <summary>Every message of a conversation in [fromMs, toMs] (bounded).</summary>
        public IReadOnlyList<CorpusMessage> ConversationRange(string conversationId, long fromMs, long toMs, int limit = 5_000)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, $"SELECT {MessageColumns} FROM messages m {MessageJoins} WHERE m.conversation_id=$c AND m.sent_at_ms BETWEEN $f AND $t ORDER BY m.sent_at_ms, m.order_key LIMIT $l",
                MapMessage, ("$c", conversationId), ("$f", fromMs), ("$t", toMs), ("$l", limit));
        }

        public string? VersionText(string messageId, int version)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<string>(null, "SELECT content FROM message_versions WHERE message_id=$m AND version=$v", ("$m", messageId), ("$v", version));
        }

        public IReadOnlyDictionary<string, ConversationInfo> Conversations(IReadOnlyCollection<string> conversationIds)
        {
            if (conversationIds.Count == 0) return new Dictionary<string, ConversationInfo>();
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, """
                    SELECT cv.conversation_id, cv.platform, cv.kind, cv.guild_id, cv.title, g.name FROM conversations cv LEFT JOIN guilds g ON g.guild_id=cv.guild_id
                    WHERE cv.conversation_id IN ({keys})
                    """, conversationIds, r => new ConversationInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5)))
                .ToDictionary(x => x.ConversationId, StringComparer.Ordinal);
        }

        /// <summary>Distinct conversations a person's identities have authored in (with message counts).</summary>
        public IReadOnlyList<(string ConversationId, long Messages, long FirstMs, long LastMs)> ConversationsOf(IReadOnlyCollection<string> identityIds)
        {
            var map = new Dictionary<string, (long N, long First, long Last)>(StringComparer.Ordinal);
            using var c = stores.Corpus.OpenRead();
            foreach (var identity in identityIds)
                foreach (var row in c.Query(null, "SELECT conversation_id, COUNT(*), MIN(sent_at_ms), MAX(sent_at_ms) FROM messages WHERE author_identity_id=$a GROUP BY conversation_id",
                             r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), ("$a", identity)))
                {
                    map[row.Item1] = map.TryGetValue(row.Item1, out var e)
                        ? (e.N + row.Item2, Math.Min(e.First, row.Item3), Math.Max(e.Last, row.Item4))
                        : (row.Item2, row.Item3, row.Item4);
                }
            return map.Select(kv => (kv.Key, kv.Value.N, kv.Value.First, kv.Value.Last)).OrderByDescending(x => x.N).ToList();
        }

        /// <summary>Identities that are members of a conversation (or authored in it).</summary>
        public IReadOnlyList<string> MembersOf(string conversationId)
        {
            using var c = stores.Corpus.OpenRead();
            var members = c.Query(null, "SELECT DISTINCT identity_id FROM conversation_members WHERE conversation_id=$c", r => r.GetString(0), ("$c", conversationId));
            return members;
        }

        public string? PersonOfIdentity(string identityId)
        {
            using var c = stores.Corpus.OpenRead();
            string? person = c.Scalar<string>(null, "SELECT person_id FROM identities WHERE identity_id=$i", ("$i", identityId));
            return person == null ? null : Canonical(person);
        }

        public long MaxSeq()
        {
            using var c = stores.Corpus.OpenRead();
            return ChangeLog.MaxSeq(c);
        }

        // ── operator settings ────────────────────────────────────────────────────

        /// <summary>The tier Klives set (tracked / watch / archive), resolved for merged persons (strongest tier wins).</summary>
        public string TierOf(string personId)
        {
            var ids = PersonIdsOf(personId);
            using var c = stores.Operator.OpenRead();
            var tiers = c.QueryIn(null, "SELECT tier FROM person_settings WHERE person_id IN ({keys})", ids, r => r.GetString(0));
            if (tiers.Contains("tracked")) return "tracked";
            if (tiers.Contains("watch")) return "watch";
            return tiers.FirstOrDefault() ?? "archive";
        }

        /// <summary>Canonical ids of every person in one of the tiers.</summary>
        public IReadOnlyList<string> PersonsInTiers(IReadOnlyCollection<string> tiers)
        {
            using var c = stores.Operator.OpenRead();
            return c.QueryIn(null, "SELECT person_id FROM person_settings WHERE tier IN ({keys})", tiers, r => r.GetString(0))
                .Select(Canonical).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The default media-text source: alt text and file names from corpus.db, plus OCR/transcript/caption text that the
    /// media workstream writes as labels (tasks <c>ocr</c>, <c>transcript</c>, <c>caption</c> on attachments, text in signals_json).</summary>
    public sealed class CorpusAttachmentTextSource : IAttachmentTextSource
    {
        private readonly OmniscienceStores stores;

        public CorpusAttachmentTextSource(OmniscienceStores stores) => this.stores = stores;

        public IReadOnlyList<AttachmentText> For(IReadOnlyCollection<string> messageIds)
        {
            if (messageIds.Count == 0) return Array.Empty<AttachmentText>();
            List<(string Id, string Message, string Kind, string? File, string? Alt)> attachments;
            using (var c = stores.Corpus.OpenRead())
                attachments = c.QueryIn(null, "SELECT attachment_id, message_id, media_kind, filename, alt_text FROM attachments WHERE message_id IN ({keys}) ORDER BY message_id, idx", messageIds,
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4)));
            if (attachments.Count == 0) return Array.Empty<AttachmentText>();
            var texts = new Dictionary<string, List<(string Task, string Text)>>(StringComparer.Ordinal);
            using (var i = stores.Intel.OpenRead())
            {
                foreach (var row in i.QueryIn(null, "SELECT item_id, task, signals_json FROM labels WHERE item_kind='attachment' AND task IN ('ocr','transcript','caption') AND item_id IN ({keys})",
                             attachments.Select(a => a.Id).ToList(), r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2))))
                {
                    string? text = ExtractText(row.Item3);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (!texts.TryGetValue(row.Item1, out var list)) texts[row.Item1] = list = new();
                    list.Add((row.Item2, text));
                }
            }
            var result = new List<AttachmentText>();
            foreach (var a in attachments)
            {
                result.Add(new AttachmentText(a.Id, a.Message, a.Kind, a.File, a.Alt, "alt_text"));
                if (texts.TryGetValue(a.Id, out var list))
                    foreach (var (task, text) in list) result.Add(new AttachmentText(a.Id, a.Message, a.Kind, a.File, text, task));
            }
            return result;
        }

        private static string? ExtractText(string? signalsJson)
        {
            if (string.IsNullOrWhiteSpace(signalsJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(signalsJson);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                    return t.GetString();
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}
