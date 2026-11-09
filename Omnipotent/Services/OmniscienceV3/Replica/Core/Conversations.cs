using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>A real corpus message as the replica features see it: who wrote it (canonical person, name then), when, and
    /// the text as it stood at the reading's as-of instant.</summary>
    public sealed record ConvMessage(string MessageId, string ConversationId, string AuthorIdentity, string AuthorPersonId, string Name, long SentAtMs, int Quality,
        string Text, bool Deleted, bool AuthorIsSelf, bool AuthorIsBot);

    /// <summary>
    /// Reads real conversations for replica work (evaluation stimuli, Rehearsal and draft context, social tactics): the text
    /// as it stood at an instant (the latest version edited by then, plan T5), names valid at the message's time, deleted
    /// messages as placeholders, bots and system messages flagged. Index seeks only.
    /// </summary>
    public sealed class Conversations
    {
        private readonly SqliteConnection corpus;
        private readonly IdentityDirectory directory;
        private readonly NameBook names;

        public Conversations(SqliteConnection corpus)
        {
            this.corpus = corpus;
            directory = new IdentityDirectory(corpus);
            names = new NameBook(corpus);
        }

        public IdentityDirectory Directory => directory;
        public NameBook Names => names;

        private sealed record Row(string Id, string Conv, string Author, long SentAt, int Quality, int Version, string? Content, bool Deleted, bool Bot, bool System);

        private const string Columns = "message_id, conversation_id, author_identity_id, sent_at_ms, time_quality, current_version, content, deleted_at_ms IS NOT NULL, is_bot, is_system";

        private static Row Map(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5), r.GetStringOrNull(6),
            r.GetInt32(7) == 1, r.GetInt32(8) == 1, r.GetInt32(9) == 1);

        /// <summary>The given messages in conversation order, with text as of <paramref name="asOfMs"/>.</summary>
        public IReadOnlyList<ConvMessage> ByIds(IReadOnlyCollection<string> ids, long asOfMs) =>
            Materialize(corpus.QueryIn(null, $"SELECT {Columns} FROM messages WHERE message_id IN ({{keys}})", ids, Map), asOfMs);

        /// <summary>The last <paramref name="limit"/> messages of a conversation at or before <paramref name="asOfMs"/>, oldest first.</summary>
        public IReadOnlyList<ConvMessage> Recent(string conversationId, long asOfMs, int limit) =>
            Materialize(corpus.Query(null, $"SELECT {Columns} FROM messages WHERE conversation_id=$c AND sent_at_ms <= $t ORDER BY sent_at_ms DESC, order_key DESC LIMIT $l",
                Map, ("$c", conversationId), ("$t", asOfMs), ("$l", Math.Clamp(limit, 1, 2_000))), asOfMs);

        /// <summary>Messages of a conversation in [from, to), oldest first (bounded).</summary>
        public IReadOnlyList<ConvMessage> Range(string conversationId, long fromMs, long toMs, int limit) =>
            Materialize(corpus.Query(null, $"SELECT {Columns} FROM messages WHERE conversation_id=$c AND sent_at_ms >= $f AND sent_at_ms < $t ORDER BY sent_at_ms, order_key LIMIT $l",
                Map, ("$c", conversationId), ("$f", fromMs), ("$t", toMs), ("$l", Math.Clamp(limit, 1, 5_000))), toMs);

        private IReadOnlyList<ConvMessage> Materialize(List<Row> rows, long asOfMs)
        {
            rows = rows.Where(r => r.SentAt <= asOfMs && !r.System).ToList();
            directory.Load(rows.Select(r => r.Author));
            names.Load(rows.Select(r => r.Author));
            // Text as it stood at the as-of instant: the newest version edited by then (v1 is the message as sent).
            var edited = rows.Where(r => r.Version > 1).Select(r => r.Id).ToList();
            var versions = corpus.QueryIn(null, "SELECT message_id, version, content, edited_at_ms FROM message_versions WHERE message_id IN ({keys})", edited,
                    r => (Id: r.GetString(0), V: r.GetInt32(1), Text: r.GetStringOrNull(2), At: r.GetInt64OrNull(3)))
                .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var result = new List<ConvMessage>(rows.Count);
            foreach (var r in rows.OrderBy(r => r.SentAt).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                string text = r.Content ?? "";
                if (r.Version > 1 && versions.TryGetValue(r.Id, out var list))
                    text = list.Where(v => v.V == 1 || (v.At ?? long.MaxValue) <= asOfMs).OrderByDescending(v => v.V).Select(v => v.Text).FirstOrDefault() ?? text;
                var who = directory.Get(r.Author);
                result.Add(new ConvMessage(r.Id, r.Conv, r.Author, who?.PersonId ?? r.Author, names.NameAt(r.Author, r.SentAt) ?? "someone", r.SentAt, r.Quality,
                    r.Deleted ? "[deleted message]" : TextFeatures.ForPrompt(text), r.Deleted, who?.IsSelf == true, r.Bot || who?.IsBot == true));
            }
            return result;
        }

        /// <summary>The DM conversation between two people (by membership or by both having written in a two-person chat), most recent first.</summary>
        public string? DirectConversation(IReadOnlyCollection<string> identitiesA, IReadOnlyCollection<string> identitiesB)
        {
            if (identitiesA.Count == 0 || identitiesB.Count == 0) return null;
            var a = corpus.QueryIn(null, "SELECT DISTINCT conversation_id FROM conversation_members WHERE identity_id IN ({keys})", identitiesA, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            var both = corpus.QueryIn(null, "SELECT DISTINCT conversation_id FROM conversation_members WHERE identity_id IN ({keys})", identitiesB, r => r.GetString(0)).Where(a.Contains).ToList();
            var candidates = both.Count > 0 ? both : AuthoredIn(identitiesA).Intersect(AuthoredIn(identitiesB), StringComparer.Ordinal).ToList();
            if (candidates.Count == 0) return null;
            var rows = corpus.QueryIn(null, "SELECT conversation_id, kind, guild_id, COALESCE(last_message_ms,0) FROM conversations WHERE conversation_id IN ({keys})", candidates,
                r => (Id: r.GetString(0), Kind: r.GetStringOrNull(1), Guild: r.GetStringOrNull(2), Last: r.GetInt64(3)));
            return rows.Where(r => r.Guild == null && ContextKinds.FromConversationKind(r.Kind) is ContextKinds.Dm or ContextKinds.Other)
                .OrderByDescending(r => ContextKinds.FromConversationKind(r.Kind) == ContextKinds.Dm).ThenByDescending(r => r.Last).Select(r => r.Id).FirstOrDefault();
        }

        private IEnumerable<string> AuthoredIn(IReadOnlyCollection<string> identities) =>
            identities.SelectMany(i => corpus.Query(null, "SELECT DISTINCT conversation_id FROM messages WHERE author_identity_id=$i ORDER BY sent_at_ms DESC LIMIT 200", r => r.GetString(0), ("$i", i)))
                .Distinct(StringComparer.Ordinal);

        public string? ConversationKind(string conversationId)
        {
            var row = corpus.Query(null, "SELECT kind, guild_id FROM conversations WHERE conversation_id=$c", r => (Kind: r.GetStringOrNull(0), Guild: r.GetStringOrNull(1)), ("$c", conversationId)).FirstOrDefault();
            if (row == default) return null;
            return row.Guild != null ? ContextKinds.Guild : ContextKinds.FromConversationKind(row.Kind);
        }

        /// <summary>Real messages as chat turns from the replica person's side: their own lines are "assistant" turns.</summary>
        public static IReadOnlyList<ChatTurn> AsTurns(IEnumerable<ConvMessage> messages, string replicaPersonId, bool group) =>
            messages.Where(m => !m.AuthorIsBot).Select(m => m.AuthorPersonId == replicaPersonId
                ? new ChatTurn("assistant", m.Text, m.SentAtMs)
                : new ChatTurn("user", m.Text, m.SentAtMs, group ? m.Name : null)).ToList();
    }
}
