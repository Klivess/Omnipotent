using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest
{
    /// <summary>What an account was doing when a platform reported it. <see cref="Fingerprint"/> is over the parts that
    /// mean something changed (see <see cref="CorpusWriter.PresenceFingerprint"/>).</summary>
    public sealed record PresenceSighting(IngestIdentity Who, string? GuildId, string? Status, string? ClientStatusJson, string? ActivitiesJson,
        string? CustomStatus, string Fingerprint);

    /// <summary>Where an account is in voice: <see cref="ChannelId"/> null means it left.</summary>
    public sealed record VoiceSighting(IngestIdentity Who, string? GuildId, string? ChannelId, string? FlagsJson);

    /// <summary>
    /// Everything a live source reports that is not a message: deletions, reactions, typing, presence, voice and
    /// membership (plan D3). The same rules as for messages apply — natural keys, nothing overwritten, every change
    /// logged — plus two of its own. An event that says nothing new since the last one recorded is not stored
    /// again, whichever server it was relayed through and whether or not the service restarted in between
    /// (F443–F445). And being seen is not taking part: presence, typing, voice and membership are recorded against
    /// the account's id without making the account a person (F397); reacting, like writing, does.
    /// </summary>
    public sealed partial class CorpusWriter
    {
        public const string LeftReasonLeft = "left";
        public const string LeftReasonMoved = "moved";
        /// <summary>The account was found gone after a gap in listening: it left some time before the recorded time.</summary>
        public const string LeftReasonUnseen = "unseen";

        private readonly Dictionary<string, string?> lastPresence = new(StringComparer.Ordinal);
        private readonly HashSet<string> eventLogged = new(StringComparer.Ordinal);

        /// <summary>Records a conversation's kind, title, server and parent without a message (a channel created or renamed).</summary>
        public string ConversationSeen(IngestConversation conversation, long atMs) => Conversation(conversation, atMs).ConversationId;

        /// <summary>
        /// Records the names and picture of an account that was seen without doing anything (a member list). It
        /// does not become a person until it takes part in a conversation. Returns its identity id.
        /// </summary>
        public string IdentitySeen(IngestIdentity identity, long atMs) => Witness(identity, atMs).Id;

        public void GuildSeen(string platform, string platformGuildId, string? name, long atMs)
        {
            platform = platform.Trim().ToLowerInvariant();
            string id = CorpusIds.Guild(platform, platformGuildId);
            if (guilds.Add(id)) Guild(id, platform, NullIfBlank(name), atMs);
        }

        /// <summary>
        /// Marks a message deleted at <paramref name="atMs"/>; its text and versions stay. False when the message is
        /// not in the corpus or was already marked.
        /// </summary>
        public bool MessageDeleted(string platform, string platformMessageId, long atMs)
        {
            string id = CorpusIds.Message(platform.Trim().ToLowerInvariant(), platformMessageId);
            int marked = c.Exec(tx, "UPDATE messages SET deleted_at_ms=$t WHERE message_id=$m AND deleted_at_ms IS NULL", ("$t", atMs), ("$m", id));
            if (marked == 0) return false;
            var (conversation, person) = MessageScope(id, null);
            Change(ChangeKinds.MessageDelete, id, person, conversation);
            return true;
        }

        /// <summary>One person's reaction. A reaction taken back and given again is one row whose times moved (same key).</summary>
        public bool ReactionAdded(string platform, string platformMessageId, string? channelId, IngestIdentity actor, string emojiKey, string? emojiName,
            long atMs, TimeQuality quality)
        {
            if (string.IsNullOrWhiteSpace(emojiKey)) return false;
            platform = platform.Trim().ToLowerInvariant();
            string message = CorpusIds.Message(platform, platformMessageId);
            Identity(actor, atMs);
            string actorId = CorpusIds.Identity(actor.Platform, actor.PlatformUserId);
            int n = c.Exec(tx, """
                INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind)
                VALUES($m,$e,$n,$i,0,$t,NULL,$q,$sk)
                ON CONFLICT(message_id, emoji_key, identity_id) DO UPDATE SET added_at_ms=excluded.added_at_ms, removed_at_ms=NULL, time_quality=excluded.time_quality
                WHERE reactions.removed_at_ms IS NOT NULL AND reactions.removed_at_ms <= excluded.added_at_ms
                """, ("$m", message), ("$e", emojiKey), ("$n", emojiName), ("$i", actorId), ("$t", atMs), ("$q", (int)quality), ("$sk", sourceKind));
            if (n == 0) return false;
            LogReaction(message, platform, channelId);
            stats += new IngestStats { ReactionsAdded = 1 };
            return true;
        }

        /// <summary>A reaction taken back. One that was never seen being given is still recorded, with no start.</summary>
        public bool ReactionRemoved(string platform, string platformMessageId, string? channelId, IngestIdentity actor, string emojiKey, string? emojiName, long atMs)
        {
            if (string.IsNullOrWhiteSpace(emojiKey)) return false;
            platform = platform.Trim().ToLowerInvariant();
            string message = CorpusIds.Message(platform, platformMessageId);
            Identity(actor, atMs);
            string actorId = CorpusIds.Identity(actor.Platform, actor.PlatformUserId);
            int n = c.Exec(tx, """
                UPDATE reactions SET removed_at_ms=$t
                WHERE message_id=$m AND emoji_key=$e AND identity_id=$i AND removed_at_ms IS NULL AND (added_at_ms IS NULL OR added_at_ms <= $t)
                """, ("$t", atMs), ("$m", message), ("$e", emojiKey), ("$i", actorId));
            if (n == 0)
                n = c.Exec(tx, """
                    INSERT OR IGNORE INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind)
                    VALUES($m,$e,$n,$i,0,NULL,$t,$q,$sk)
                    """, ("$m", message), ("$e", emojiKey), ("$n", emojiName), ("$i", actorId), ("$t", atMs), ("$q", (int)TimeQuality.Second), ("$sk", sourceKind));
            if (n == 0) return false;
            LogReaction(message, platform, channelId);
            return true;
        }

        /// <summary>Every reaction on a message removed at once, or every reaction with one emoji. Returns how many ended.</summary>
        public int ReactionsCleared(string platform, string platformMessageId, string? channelId, string? emojiKey, long atMs)
        {
            platform = platform.Trim().ToLowerInvariant();
            string message = CorpusIds.Message(platform, platformMessageId);
            int n = c.Exec(tx, """
                UPDATE reactions SET removed_at_ms=$t
                WHERE message_id=$m AND removed_at_ms IS NULL AND ($e IS NULL OR emoji_key=$e) AND (added_at_ms IS NULL OR added_at_ms <= $t)
                """, ("$t", atMs), ("$m", message), ("$e", emojiKey));
            int counts = c.Exec(tx, "UPDATE reaction_counts SET count=0, observed_at_ms=$t WHERE message_id=$m AND ($e IS NULL OR emoji_key=$e) AND observed_at_ms <= $t AND count <> 0",
                ("$t", atMs), ("$m", message), ("$e", emojiKey));
            if (n + counts > 0) LogReaction(message, platform, channelId);
            return n;
        }

        public bool Typing(IngestIdentity who, string platform, string channelId, long atMs)
        {
            platform = platform.Trim().ToLowerInvariant();
            return c.Exec(tx, "INSERT OR IGNORE INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,$s)",
                ("$i", CorpusIds.Identity(who.Platform, who.PlatformUserId)), ("$c", CorpusIds.Conversation(platform, channelId)), ("$t", atMs), ("$s", source.SourceId)) == 1;
        }

        /// <summary>
        /// The fingerprint of a presence: status, which kinds of device are online, and what each activity is (its
        /// kind, name, details and state). Start and end times, party sizes and artwork are left out — they change
        /// while nothing about what the person is doing does (F443).
        /// </summary>
        public static string PresenceFingerprint(string? status, IEnumerable<string>? onlineClients, IEnumerable<string>? activities, string? customStatus)
        {
            var sb = new StringBuilder();
            sb.Append(status).Append('\u001f');
            sb.Append(string.Join(",", (onlineClients ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))).Append('\u001f');
            sb.Append(string.Join("\u001e", (activities ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))).Append('\u001f');
            sb.Append(customStatus);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..20];
        }

        /// <summary>
        /// Records a presence when it differs from the last one recorded for the account. A platform relays the same
        /// change once per shared server, and repeats it after every reconnect; those say nothing new.
        /// </summary>
        public bool Presence(PresenceSighting p, long atMs)
        {
            string id = CorpusIds.Identity(p.Who.Platform, p.Who.PlatformUserId);
            if (!lastPresence.TryGetValue(id, out string? last))
            {
                last = c.Scalar<string>(tx, "SELECT dedupe_key FROM presence_events WHERE identity_id=$i ORDER BY at_ms DESC, event_id DESC LIMIT 1", ("$i", id));
                lastPresence[id] = last;
            }
            if (last != null && last.EndsWith(":" + p.Fingerprint, StringComparison.Ordinal)) return false;

            string platform = p.Who.Platform.Trim().ToLowerInvariant();
            string dedupe = $"{source.SourceId}:{id}:{atMs}:{p.Fingerprint}";
            int n = c.Exec(tx, """
                INSERT OR IGNORE INTO presence_events(identity_id, guild_id, at_ms, status, client_status_json, activities_json, custom_status, source_id, dedupe_key)
                VALUES($i,$g,$t,$s,$cs,$a,$cu,$src,$d)
                """, ("$i", id), ("$g", string.IsNullOrWhiteSpace(p.GuildId) ? null : CorpusIds.Guild(platform, p.GuildId)), ("$t", atMs), ("$s", p.Status),
                ("$cs", p.ClientStatusJson), ("$a", p.ActivitiesJson), ("$cu", NullIfBlank(p.CustomStatus)), ("$src", source.SourceId), ("$d", dedupe));
            lastPresence[id] = dedupe;
            if (n == 0) return false;
            if (eventLogged.Add("presence\u0001" + id)) Change(ChangeKinds.PresenceEvent, id, PersonOf(id), null);
            return true;
        }

        /// <summary>
        /// Applies where an account is in voice now: joins open a session, moving closes one and opens the next,
        /// leaving closes it, and a change of mute/deafen/stream/video updates the open one. At most one session per
        /// account and server is ever open.
        /// </summary>
        public bool Voice(VoiceSighting v, long atMs)
        {
            string platform = v.Who.Platform.Trim().ToLowerInvariant();
            string id = CorpusIds.Identity(platform, v.Who.PlatformUserId);
            string? guild = string.IsNullOrWhiteSpace(v.GuildId) ? null : CorpusIds.Guild(platform, v.GuildId);
            string? channel = string.IsNullOrWhiteSpace(v.ChannelId) ? null : CorpusIds.Conversation(platform, v.ChannelId);
            var open = c.Query(tx, "SELECT session_id, channel_id, flags_json FROM voice_sessions WHERE identity_id=$i AND left_at_ms IS NULL AND guild_id IS $g ORDER BY joined_at_ms DESC",
                r => (Id: r.GetString(0), Channel: r.GetStringOrNull(1), Flags: r.GetStringOrNull(2)), ("$i", id), ("$g", guild));

            bool changed = false;
            int keep = channel != null && open.Count > 0 && open[0].Channel == channel ? 1 : 0;
            foreach (var session in open.Skip(keep))
            {
                // A session that started at or after this moment was opened by a later event already applied.
                changed |= c.Exec(tx, "UPDATE voice_sessions SET left_at_ms=$t, left_reason=$r WHERE session_id=$s AND left_at_ms IS NULL AND joined_at_ms <= $t",
                    ("$t", atMs), ("$r", channel == null ? LeftReasonLeft : LeftReasonMoved), ("$s", session.Id)) > 0;
            }
            if (keep == 1)
            {
                if (v.FlagsJson != null && v.FlagsJson != open[0].Flags)
                    changed |= c.Exec(tx, "UPDATE voice_sessions SET flags_json=$f WHERE session_id=$s", ("$f", v.FlagsJson), ("$s", open[0].Id)) > 0;
            }
            else if (channel != null)
            {
                changed |= c.Exec(tx, """
                    INSERT OR IGNORE INTO voice_sessions(session_id, identity_id, guild_id, channel_id, joined_at_ms, flags_json, source_id) VALUES($s,$i,$g,$c,$t,$f,$src)
                    """, ("$s", $"{platform}:voice:{v.Who.PlatformUserId.Trim()}:{atMs}"), ("$i", id), ("$g", guild), ("$c", channel), ("$t", atMs), ("$f", v.FlagsJson),
                    ("$src", source.SourceId)) > 0;
            }
            if (changed && eventLogged.Add("voice\u0001" + id)) Change(ChangeKinds.VoiceEvent, id, PersonOf(id), null);
            return changed;
        }

        /// <summary>
        /// Closes the sessions this source holds open in a server for accounts that are no longer in voice there —
        /// what a restart or a dropped connection leaves behind (F447). Their real end is unknown, only that it was
        /// before <paramref name="atMs"/>, which the reason records.
        /// </summary>
        public int VoiceReconcile(string platform, string platformGuildId, IReadOnlyCollection<string> platformUserIdsInVoice, long atMs)
        {
            platform = platform.Trim().ToLowerInvariant();
            string guild = CorpusIds.Guild(platform, platformGuildId);
            var present = platformUserIdsInVoice.Select(u => CorpusIds.Identity(platform, u)).ToHashSet(StringComparer.Ordinal);
            var open = c.Query(tx, "SELECT session_id, identity_id FROM voice_sessions WHERE guild_id=$g AND left_at_ms IS NULL AND source_id=$s AND joined_at_ms <= $t",
                r => (Id: r.GetString(0), Identity: r.GetString(1)), ("$g", guild), ("$s", source.SourceId), ("$t", atMs));
            int closed = 0;
            foreach (var session in open)
            {
                if (present.Contains(session.Identity)) continue;
                closed += c.Exec(tx, "UPDATE voice_sessions SET left_at_ms=$t, left_reason=$r WHERE session_id=$s AND left_at_ms IS NULL",
                    ("$t", atMs), ("$r", LeftReasonUnseen), ("$s", session.Id));
                if (eventLogged.Add("voice\u0001" + session.Identity)) Change(ChangeKinds.VoiceEvent, session.Identity, PersonOf(session.Identity), null);
            }
            return closed;
        }

        /// <summary>
        /// A membership change in a server: <paramref name="kind"/> is <c>join</c>, <c>leave</c> or <c>update</c>.
        /// A nickname on <paramref name="who"/> is recorded from this moment; a nickname scope with no nickname ends
        /// the one it had (F451).
        /// </summary>
        public bool MemberEvent(IngestIdentity who, string platformGuildId, string kind, long atMs, string? detailJson)
        {
            var (id, person) = Witness(who, atMs);
            string guild = CorpusIds.Guild(who.Platform.Trim().ToLowerInvariant(), platformGuildId);
            int n = c.Exec(tx, "INSERT OR IGNORE INTO member_events(guild_id, identity_id, kind, at_ms, detail_json, source_id, dedupe_key) VALUES($g,$i,$k,$t,$d,$s,$key)",
                ("$g", guild), ("$i", id), ("$k", kind), ("$t", atMs), ("$d", detailJson), ("$s", source.SourceId), ("$key", $"{source.SourceId}:{guild}:{id}:{kind}:{atMs}"));
            if (n == 0) return false;
            Change(ChangeKinds.MemberEvent, id, person, null);
            return true;
        }

        private void LogReaction(string messageId, string platform, string? channelId)
        {
            if (!eventLogged.Add("reaction\u0001" + messageId)) return;
            var (conversation, person) = MessageScope(messageId, string.IsNullOrWhiteSpace(channelId) ? null : CorpusIds.Conversation(platform, channelId));
            Change(ChangeKinds.ReactionChange, messageId, person, conversation);
        }

        /// <summary>The conversation and author (canonical person) of a stored message; the fallback conversation when it is not stored.</summary>
        private (string? Conversation, string? Person) MessageScope(string messageId, string? fallbackConversation)
        {
            var row = c.Query(tx, """
                SELECT m.conversation_id, COALESCE(pc.canonical_id, i.person_id) FROM messages m
                LEFT JOIN identities i ON i.identity_id = m.author_identity_id
                LEFT JOIN person_canonical pc ON pc.person_id = i.person_id
                WHERE m.message_id=$m
                """, r => (Found: true, Conversation: r.GetStringOrNull(0), Person: r.GetStringOrNull(1)), ("$m", messageId)).FirstOrDefault();
            return row.Found ? (row.Conversation, row.Person) : (fallbackConversation, null);
        }

        private string? PersonOf(string identityId) =>
            c.Scalar<string>(tx, "SELECT COALESCE(pc.canonical_id, i.person_id) FROM identities i LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE i.identity_id=$i",
                ("$i", identityId));
    }
}
