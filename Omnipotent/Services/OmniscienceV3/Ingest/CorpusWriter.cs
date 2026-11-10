using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest
{
    /// <summary>
    /// The one way normalised records enter corpus.db. Live sources and every importer go through it, so the
    /// rules are stated once:
    /// <list type="bullet">
    /// <item><b>Idempotent.</b> Ids are natural keys, so the same message arriving again (a re-import, a backfill
    /// over a live capture) changes nothing and is counted as unchanged.</item>
    /// <item><b>Nothing is overwritten.</b> Different content becomes a new version; the old one is kept. A newer
    /// edit time wins; without edit times the higher-ranked source wins; otherwise the stored content stays.</item>
    /// <item><b>Every mutation is logged</b> in the change log inside the same transaction (plan R3), tagged with
    /// the source kind so imports and backfills never raise live alerts (plan T5).</item>
    /// <item><b>Names are intervals.</b> A username or display name seen on a message is valid from that message's
    /// time, not from the time it was imported (plan T5).</item>
    /// </list>
    /// Create one per write transaction, inside the corpus writer; it only touches the database.
    /// </summary>
    public sealed class CorpusWriter : IDisposable
    {
        private readonly SqliteConnection c;
        private readonly SqliteTransaction tx;
        private readonly IngestSource source;
        private readonly string sourceKind;
        private readonly long now;

        private readonly Dictionary<string, IdentityState> identities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<NameInterval>> names = new(StringComparer.Ordinal);
        private readonly HashSet<string> conversations = new(StringComparer.Ordinal);
        private readonly HashSet<string> guilds = new(StringComparer.Ordinal);
        private readonly HashSet<string> members = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> lastMessage = new(StringComparer.Ordinal);
        private readonly PreparedStatement findMessage, insertMessage, insertVersion, insertMention, insertEmbed, insertSticker, insertPoll,
            insertAttachment, insertReaction, upsertReactionCount, findIdentity, insertIdentity, findCanonical;
        private IngestStats stats = new();

        private sealed class IdentityState
        {
            public required string PersonId;
            public required string Canonical;
            public long First, Last;
            public bool SeenDirty;
            public bool IsBot;
        }

        private sealed record NameInterval(string Value, long From, long? To);

        public CorpusWriter(SqliteConnection c, SqliteTransaction tx, IngestSource source, string sourceKind, long nowMs)
        {
            if (!SourceKinds.IsKnown(sourceKind)) throw new ArgumentException($"Unknown source kind '{sourceKind}'.", nameof(sourceKind));
            this.c = c;
            this.tx = tx;
            this.source = source;
            this.sourceKind = sourceKind;
            now = nowMs;

            c.Exec(tx, """
                INSERT INTO sources(source_id, platform, kind, label, status, rank, created_at_ms, updated_at_ms) VALUES($s,$p,$k,$l,'active',$r,$n,$n)
                ON CONFLICT(source_id) DO UPDATE SET label=COALESCE(excluded.label, sources.label), rank=excluded.rank, updated_at_ms=excluded.updated_at_ms
                """, ("$s", source.SourceId), ("$p", source.Platform), ("$k", source.Kind), ("$l", source.Label), ("$r", source.Rank), ("$n", nowMs));

            findMessage = new PreparedStatement(c, tx, """
                SELECT current_version, content, edited_at_ms, deleted_at_ms, best_source_rank, sent_at_ms, time_quality, reply_to_message_id
                FROM messages WHERE message_id=$m
                """, "$m");
            insertMessage = new PreparedStatement(c, tx, """
                INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality,
                    order_key, type, flags, is_bot, is_system, is_webhook, reply_to_message_id, forwarded_from_json, current_version, content, edited_at_ms,
                    deleted_at_ms, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq, raw_json)
                VALUES($id,$p,$pm,$c,$g,$a,$t,$q,$o,$ty,$f,$bot,$sys,$wh,$r,$fw,1,$content,$e,$d,$sk,$src,$rank,$ing,$seq,$raw)
                """, "$id", "$p", "$pm", "$c", "$g", "$a", "$t", "$q", "$o", "$ty", "$f", "$bot", "$sys", "$wh", "$r", "$fw", "$content", "$e", "$d", "$sk", "$src", "$rank", "$ing", "$seq", "$raw");
            insertVersion = new PreparedStatement(c, tx,
                "INSERT OR IGNORE INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,$v,$c,$e,$r,$s)",
                "$m", "$v", "$c", "$e", "$r", "$s");
            insertMention = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_mentions(message_id, target_kind, target_id) VALUES($m,$k,$t)", "$m", "$k", "$t");
            insertEmbed = new PreparedStatement(c, tx,
                "INSERT OR IGNORE INTO message_embeds(message_id, idx, type, provider, url, title, description, json) VALUES($m,$i,$t,$p,$u,$ti,$d,$j)",
                "$m", "$i", "$t", "$p", "$u", "$ti", "$d", "$j");
            insertSticker = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_stickers(message_id, sticker_id, name) VALUES($m,$s,$n)", "$m", "$s", "$n");
            insertPoll = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_polls(message_id, question, answers_json, expiry_ms) VALUES($m,$q,$a,$e)", "$m", "$q", "$a", "$e");
            insertAttachment = new PreparedStatement(c, tx, """
                INSERT OR IGNORE INTO attachments(attachment_id, message_id, idx, platform_attachment_id, filename, mime, media_kind, size_bytes, width, height,
                    duration_ms, alt_text, is_spoiler, source_url, cas_sha256, download_state, download_error, created_at_ms)
                VALUES($id,$m,$i,$pa,$fn,$mi,$k,$s,$w,$h,$d,$alt,$sp,$u,$cas,$st,$err,$t)
                """, "$id", "$m", "$i", "$pa", "$fn", "$mi", "$k", "$s", "$w", "$h", "$d", "$alt", "$sp", "$u", "$cas", "$st", "$err", "$t");
            insertReaction = new PreparedStatement(c, tx, """
                INSERT OR IGNORE INTO reactions(message_id, emoji_key, emoji_name, identity_id, added_at_ms, time_quality, source_kind) VALUES($m,$e,$n,$i,$t,$q,$sk)
                """, "$m", "$e", "$n", "$i", "$t", "$q", "$sk");
            upsertReactionCount = new PreparedStatement(c, tx, """
                INSERT INTO reaction_counts(message_id, emoji_key, count, observed_at_ms) VALUES($m,$e,$n,$t)
                ON CONFLICT(message_id, emoji_key) DO UPDATE SET count=excluded.count, observed_at_ms=excluded.observed_at_ms
                WHERE excluded.observed_at_ms >= reaction_counts.observed_at_ms
                """, "$m", "$e", "$n", "$t");
            findIdentity = new PreparedStatement(c, tx, "SELECT person_id, first_seen_ms, last_seen_ms, kind FROM identities WHERE identity_id=$i", "$i");
            insertIdentity = new PreparedStatement(c, tx,
                "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES($id,$p,$u,$person,$k,$f,$f)",
                "$id", "$p", "$u", "$person", "$k", "$f");
            findCanonical = new PreparedStatement(c, tx, "SELECT canonical_id FROM person_canonical WHERE person_id=$p", "$p");
        }

        /// <summary>
        /// The person an identity gets when it is first seen. Derived from the identity, not random, so importing the
        /// same data into a rebuilt corpus yields the same person ids and Klives's decisions (which reference them)
        /// still apply.
        /// </summary>
        public static string PersonIdFor(string identityId) =>
            "p_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("person|" + identityId))).ToLowerInvariant()[..24];

        /// <summary>Writes one message and everything that hangs off it. Returns false when it was rejected (and quarantined).</summary>
        public bool Message(IngestMessage m)
        {
            string platform = (m.Conversation.Platform ?? "").Trim().ToLowerInvariant();
            string? problem = Validate(m, platform);
            if (problem != null)
            {
                c.Exec(tx, "INSERT OR IGNORE INTO quarantine(origin, reason, payload, at_ms) VALUES($o,$r,$p,$t)",
                    ("$o", $"ingest:{source.SourceId}:{m.PlatformMessageId}"), ("$r", problem),
                    ("$p", JsonSerializer.Serialize(new { m.PlatformMessageId, m.SentAtMs, channel = m.Conversation.ChannelId, author = m.Author.PlatformUserId }, KernelJson.Options)),
                    ("$t", now));
                stats += new IngestStats { Rejected = 1 };
                return false;
            }

            var author = Identity(m.Author, m.SentAtMs);
            string authorId = CorpusIds.Identity(m.Author.Platform, m.Author.PlatformUserId);
            var (conversationId, guildId) = Conversation(m.Conversation, m.SentAtMs);
            Member(conversationId, authorId, m.SentAtMs);
            foreach (var member in m.Conversation.Members ?? Array.Empty<IngestIdentity>())
            {
                if (string.IsNullOrWhiteSpace(member.Platform) || string.IsNullOrWhiteSpace(member.PlatformUserId)) continue;
                string memberId = CorpusIds.Identity(member.Platform, member.PlatformUserId);
                if (members.Contains(conversationId + "\u0001" + memberId)) continue;
                Identity(member, m.SentAtMs);
                Member(conversationId, memberId, m.SentAtMs);
            }

            string messageId = CorpusIds.Message(platform, m.PlatformMessageId);
            string? replyTo = string.IsNullOrWhiteSpace(m.ReplyToPlatformMessageId) ? null : CorpusIds.Message(platform, m.ReplyToPlatformMessageId);

            var existing = ReadExisting(messageId);
            if (existing == null)
            {
                long seq = Change(ChangeKinds.MessageInsert, messageId, author.Canonical, conversationId);
                // A platform clock ahead of ours must not make a message look ingested before it was sent (plan T1).
                long ingested = Math.Max(now, m.SentAtMs);
                insertMessage.Run(messageId, platform, m.PlatformMessageId.Trim(), conversationId, guildId, authorId, m.SentAtMs, (int)m.Quality, m.OrderKey,
                    m.Type, m.Flags, m.Author.IsBot ? 1 : 0, m.IsSystem ? 1 : 0, m.IsWebhook ? 1 : 0, replyTo, m.ForwardedFromJson, m.Content, m.EditedAtMs,
                    m.DeletedAtMs, sourceKind, source.SourceId, source.Rank, ingested, seq, m.RawJson);
                insertVersion.Run(messageId, 1, m.Content, m.EditedAtMs, now, source.SourceId);
                Parts(m, messageId, author.Canonical, conversationId);
                stats += new IngestStats { Inserted = 1 };
            }
            else
            {
                bool changed = Merge(m, messageId, existing.Value, replyTo, author.Canonical, conversationId);
                // A later copy may know attachments or reactions the first one lacked; both inserts ignore what exists.
                int before = stats.AttachmentsAdded + stats.ReactionsAdded;
                Attachments(m, messageId, author.Canonical, conversationId);
                Reactions(m, messageId, author.Canonical, conversationId);
                changed |= stats.AttachmentsAdded + stats.ReactionsAdded != before;
                stats += changed ? new IngestStats { Updated = 1 } : new IngestStats { Unchanged = 1 };
            }

            if (!lastMessage.TryGetValue(conversationId, out long last) || m.SentAtMs > last) lastMessage[conversationId] = m.SentAtMs;
            return true;
        }

        /// <summary>Flushes what is batched per transaction (seen ranges, last-message times) and returns the totals.</summary>
        public IngestStats Complete()
        {
            foreach (var (id, s) in identities)
            {
                if (!s.SeenDirty) continue;
                c.Exec(tx, "UPDATE identities SET first_seen_ms=MIN(first_seen_ms,$f), last_seen_ms=MAX(last_seen_ms,$l) WHERE identity_id=$i",
                    ("$f", s.First), ("$l", s.Last), ("$i", id));
                s.SeenDirty = false;
            }
            foreach (var (conversation, last) in lastMessage)
                c.Exec(tx, "UPDATE conversations SET last_message_ms=MAX(COALESCE(last_message_ms,0),$t) WHERE conversation_id=$c", ("$t", last), ("$c", conversation));
            lastMessage.Clear();
            return stats;
        }

        // ── messages ─────────────────────────────────────────────────────────────

        private static string? Validate(IngestMessage m, string platform)
        {
            if (platform.Length == 0) return "message without a platform";
            if (string.IsNullOrWhiteSpace(m.PlatformMessageId)) return "message without an id";
            if (string.IsNullOrWhiteSpace(m.Conversation.ChannelId)) return "message without a conversation";
            if (string.IsNullOrWhiteSpace(m.Author.Platform) || string.IsNullOrWhiteSpace(m.Author.PlatformUserId)) return "message without an author";
            if (!LegacyTime.IsPlausible(m.SentAtMs)) return "send time outside 2000–2100";
            return null;
        }

        private readonly record struct Existing(int Version, string? Content, long? EditedAtMs, long? DeletedAtMs, int Rank, long SentAtMs, TimeQuality Quality, string? ReplyTo);

        private Existing? ReadExisting(string messageId)
        {
            using var r = FindReader(findMessage, messageId);
            if (!r.Read()) return null;
            return new Existing(r.GetInt32(0), r.GetStringOrNull(1), r.GetInt64OrNull(2), r.GetInt64OrNull(3), r.GetInt32(4), r.GetInt64(5),
                (TimeQuality)r.GetInt32(6), r.GetStringOrNull(7));
        }

        private bool Merge(IngestMessage m, string messageId, Existing cur, string? replyTo, string? person, string conversationId)
        {
            bool changed = false;

            if (!string.Equals(m.Content, cur.Content, StringComparison.Ordinal) && IncomingContentWins(m, cur))
            {
                int version = cur.Version + 1;
                c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,$v,$c,$e,$r,$s)",
                    ("$m", messageId), ("$v", version), ("$c", m.Content), ("$e", m.EditedAtMs), ("$r", now), ("$s", source.SourceId));
                c.Exec(tx, """
                    UPDATE messages SET content=$c, current_version=$v, edited_at_ms=COALESCE($e, edited_at_ms),
                        best_source_id=CASE WHEN $rank >= best_source_rank THEN $src ELSE best_source_id END,
                        best_source_rank=MAX(best_source_rank, $rank)
                    WHERE message_id=$m
                    """, ("$c", m.Content), ("$v", version), ("$e", m.EditedAtMs), ("$rank", source.Rank), ("$src", source.SourceId), ("$m", messageId));
                Change(ChangeKinds.MessageVersion, messageId, person, conversationId);
                changed = true;
            }

            if (m.DeletedAtMs != null && cur.DeletedAtMs == null)
            {
                c.Exec(tx, "UPDATE messages SET deleted_at_ms=$t WHERE message_id=$m", ("$t", m.DeletedAtMs), ("$m", messageId));
                Change(ChangeKinds.MessageDelete, messageId, person, conversationId);
                changed = true;
            }

            // A wall-clock time read in a guessed zone is replaced by a better reading: a platform instant, or the
            // same export re-imported with the zone corrected. Platform instants are never replaced.
            bool storedIsGuess = cur.Quality is TimeQuality.InferredZone or TimeQuality.LegacyUnknownZone;
            bool better = m.Quality < cur.Quality || (m.Quality == TimeQuality.InferredZone && cur.Quality == TimeQuality.InferredZone && sourceKind == SourceKinds.Import);
            if (storedIsGuess && better && m.SentAtMs != cur.SentAtMs)
            {
                c.Exec(tx, "UPDATE messages SET sent_at_ms=$t, time_quality=$q WHERE message_id=$m", ("$t", m.SentAtMs), ("$q", (int)m.Quality), ("$m", messageId));
                Change(ChangeKinds.MessageVersion, messageId, person, conversationId);
                changed = true;
            }

            if (replyTo != null && cur.ReplyTo == null)
            {
                c.Exec(tx, "UPDATE messages SET reply_to_message_id=$r WHERE message_id=$m", ("$r", replyTo), ("$m", messageId));
                changed = true;
            }
            return changed;
        }

        private bool IncomingContentWins(IngestMessage m, Existing cur)
        {
            // A deletion notice with no text must not blank what was captured.
            if (m.Content == null) return false;
            if (m.EditedAtMs != null) return cur.EditedAtMs == null || m.EditedAtMs > cur.EditedAtMs;
            // No edit time on the incoming copy: it cannot be shown to be newer than an edit we already hold.
            if (cur.EditedAtMs != null) return false;
            return source.Rank > cur.Rank;
        }

        private void Parts(IngestMessage m, string messageId, string? person, string conversationId)
        {
            foreach (var mention in m.Mentions) insertMention.Run(messageId, mention.Kind, mention.Target);
            for (int i = 0; i < m.Embeds.Count; i++)
            {
                var e = m.Embeds[i];
                insertEmbed.Run(messageId, i, e.Type, e.Provider, e.Url, e.Title, e.Description, e.Json);
            }
            foreach (var s in m.Stickers) insertSticker.Run(messageId, s.Id, s.Name);
            if (m.Poll is { } poll) insertPoll.Run(messageId, poll.Question, poll.AnswersJson, poll.ExpiryMs);
            Attachments(m, messageId, person, conversationId);
            Reactions(m, messageId, person, conversationId);
        }

        private void Attachments(IngestMessage m, string messageId, string? person, string conversationId)
        {
            for (int i = 0; i < m.Attachments.Count; i++)
            {
                var a = m.Attachments[i];
                string id = CorpusIds.Attachment(messageId, i);
                string? mime = a.Stored?.Mime ?? a.Mime;
                string kind = a.MediaKind ?? MediaKinds.Classify(mime, a.Filename);
                string state = a.Stored != null ? DownloadStates.Done
                    : a.NotStored != null ? a.NotStored.Value.State
                    : a.Unavailable || string.IsNullOrWhiteSpace(a.SourceUrl) ? DownloadStates.Unavailable
                    : DownloadStates.Pending;
                if (a.Stored != null) MediaStore.Register(c, tx, a.Stored, now);
                int inserted = insertAttachment.Run(id, messageId, i, a.PlatformAttachmentId, a.Filename, mime, kind, a.Stored?.SizeBytes ?? a.SizeBytes, a.Width, a.Height,
                    a.DurationMs, a.AltText, a.Spoiler ? 1 : 0, a.SourceUrl, a.Stored?.Sha256, state, a.NotStored?.Reason, m.SentAtMs);
                if (inserted == 1)
                {
                    Change(ChangeKinds.AttachmentAdd, id, person, conversationId);
                    stats += new IngestStats { AttachmentsAdded = 1 };
                    if (a.Stored != null)
                    {
                        Change(ChangeKinds.AttachmentStored, id, person, conversationId);
                        stats += new IngestStats { AttachmentsStored = 1 };
                    }
                }
                else if (a.Stored != null)
                {
                    // Known before without its bytes (an expired link, a failed download): this copy supplies them.
                    int filled = c.Exec(tx, """
                        UPDATE attachments SET cas_sha256=$cas, download_state='done', download_error=NULL, mime=COALESCE($mi, mime), size_bytes=$s
                        WHERE attachment_id=$id AND cas_sha256 IS NULL
                        """, ("$cas", a.Stored.Sha256), ("$mi", mime), ("$s", a.Stored.SizeBytes), ("$id", id));
                    if (filled == 1)
                    {
                        Change(ChangeKinds.AttachmentStored, id, person, conversationId);
                        stats += new IngestStats { AttachmentsStored = 1 };
                    }
                }
            }
        }

        private void Reactions(IngestMessage m, string messageId, string? person, string conversationId)
        {
            foreach (var r in m.Reactions)
            {
                if (string.IsNullOrWhiteSpace(r.EmojiKey)) continue;
                if (r.Actor != null)
                {
                    Identity(r.Actor, r.AtMs ?? m.SentAtMs);
                    string actorId = CorpusIds.Identity(r.Actor.Platform, r.Actor.PlatformUserId);
                    // Without its own time a reaction is only known to be no earlier than the message: the time stays empty.
                    int inserted = insertReaction.Run(messageId, r.EmojiKey, r.EmojiName, actorId, r.AtMs, (int)(r.AtMs != null ? TimeQuality.Exact : TimeQuality.Day), sourceKind);
                    if (inserted == 1)
                    {
                        Change(ChangeKinds.ReactionChange, messageId, person, conversationId);
                        stats += new IngestStats { ReactionsAdded = 1 };
                    }
                }
                else if (r.Count is { } count)
                {
                    upsertReactionCount.Run(messageId, r.EmojiKey, count, now);
                }
            }
        }

        // ── people ───────────────────────────────────────────────────────────────

        private IdentityState Identity(IngestIdentity i, long seenAtMs)
        {
            string id = CorpusIds.Identity(i.Platform, i.PlatformUserId);
            if (!identities.TryGetValue(id, out var state))
            {
                using (var r = FindReader(findIdentity, id))
                {
                    if (r.Read())
                        state = new IdentityState { PersonId = r.GetString(0), Canonical = "", First = r.GetInt64(1), Last = r.GetInt64(2), IsBot = r.GetString(3) == "bot" };
                }
                if (state == null)
                {
                    string personId = PersonIdFor(id);
                    int created = c.Exec(tx, "INSERT OR IGNORE INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,0)",
                        ("$p", personId), ("$t", seenAtMs), ("$n", NullIfBlank(i.DisplayName) ?? NullIfBlank(i.Username)));
                    c.Exec(tx, "INSERT OR IGNORE INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", personId));
                    insertIdentity.Run(id, i.Platform.Trim().ToLowerInvariant(), i.PlatformUserId.Trim(), personId, i.IsBot ? "bot" : "user", seenAtMs);
                    state = new IdentityState { PersonId = personId, Canonical = "", First = seenAtMs, Last = seenAtMs, IsBot = i.IsBot };
                    state.Canonical = (string?)findCanonical.ScalarRaw(personId) ?? personId;
                    if (created == 1) Change(ChangeKinds.PersonCreate, personId, state.Canonical, null);
                    Change(ChangeKinds.IdentityUpsert, id, state.Canonical, null);
                    stats += new IngestStats { NewIdentities = 1 };
                }
                else
                {
                    state.Canonical = (string?)findCanonical.ScalarRaw(state.PersonId) ?? state.PersonId;
                }
                identities[id] = state;
            }

            if (seenAtMs < state.First) { state.First = seenAtMs; state.SeenDirty = true; }
            if (seenAtMs > state.Last) { state.Last = seenAtMs; state.SeenDirty = true; }
            if (i.IsBot && !state.IsBot)
            {
                c.Exec(tx, "UPDATE identities SET kind='bot' WHERE identity_id=$i AND kind='user'", ("$i", id));
                state.IsBot = true;
            }
            if (i.IsSelf)
                c.Exec(tx, "UPDATE persons SET is_self=1 WHERE person_id IN ($p,$c) AND is_self=0", ("$p", state.PersonId), ("$c", state.Canonical));

            ObserveName(id, "username", i.Username, seenAtMs, state.Canonical);
            ObserveName(id, "display", i.DisplayName, seenAtMs, state.Canonical);
            return state;
        }

        /// <summary>
        /// Records that <paramref name="value"/> was the identity's name at <paramref name="atMs"/>. Sightings arrive
        /// in any order (a backfill runs backwards), so: a different name after the current one closes it and opens a
        /// new interval; a name before the earliest known one opens an interval that ends where the earliest began;
        /// the same name earlier just moves the start back. A different name sighted inside a closed interval is not
        /// recorded — the surrounding sightings already bound it and splitting on one message would invent precision.
        /// </summary>
        private void ObserveName(string identityId, string kind, string? value, long atMs, string? person)
        {
            value = NullIfBlank(value);
            if (value == null) return;
            string key = identityId + "\u0001" + kind;
            if (!names.TryGetValue(key, out var timeline))
            {
                timeline = c.Query(tx, "SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id=$i AND name_kind=$k ORDER BY valid_from_ms",
                    r => new NameInterval(r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2)), ("$i", identityId), ("$k", kind));
                names[key] = timeline;
            }

            if (timeline.Count == 0)
            {
                InsertName(identityId, kind, value, atMs, null);
                timeline.Add(new NameInterval(value, atMs, null));
                Change(ChangeKinds.IdentityName, identityId, person, null);
                return;
            }

            var latest = timeline[^1];
            var earliest = timeline[0];
            if (atMs >= latest.From)
            {
                if (latest.To != null && atMs < latest.To) return; // inside the last closed interval
                if (latest.To == null && latest.Value == value) return;
                if (atMs == latest.From) return; // two names at the same instant: keep the first recorded
                if (latest.To == null)
                    c.Exec(tx, "UPDATE identity_names SET valid_to_ms=$t WHERE identity_id=$i AND name_kind=$k AND valid_from_ms=$f",
                        ("$t", atMs), ("$i", identityId), ("$k", kind), ("$f", latest.From));
                timeline[^1] = latest with { To = latest.To ?? atMs };
                InsertName(identityId, kind, value, atMs, null);
                timeline.Add(new NameInterval(value, atMs, null));
                Change(ChangeKinds.IdentityName, identityId, person, null);
            }
            else if (atMs < earliest.From)
            {
                if (earliest.Value == value)
                {
                    c.Exec(tx, "UPDATE identity_names SET valid_from_ms=$n WHERE identity_id=$i AND name_kind=$k AND valid_from_ms=$f",
                        ("$n", atMs), ("$i", identityId), ("$k", kind), ("$f", earliest.From));
                    timeline[0] = earliest with { From = atMs };
                }
                else
                {
                    InsertName(identityId, kind, value, atMs, earliest.From);
                    timeline.Insert(0, new NameInterval(value, atMs, earliest.From));
                    Change(ChangeKinds.IdentityName, identityId, person, null);
                }
            }
        }

        private void InsertName(string identityId, string kind, string value, long from, long? to) =>
            c.Exec(tx, "INSERT OR IGNORE INTO identity_names(identity_id, name_kind, value, valid_from_ms, valid_to_ms, source_kind) VALUES($i,$k,$v,$f,$t,$s)",
                ("$i", identityId), ("$k", kind), ("$v", value), ("$f", from), ("$t", to), ("$s", sourceKind));

        // ── places ───────────────────────────────────────────────────────────────

        private (string ConversationId, string? GuildId) Conversation(IngestConversation cv, long atMs)
        {
            string platform = cv.Platform.Trim().ToLowerInvariant();
            string id = CorpusIds.Conversation(platform, cv.ChannelId);
            string? guildId = string.IsNullOrWhiteSpace(cv.GuildId) ? null : CorpusIds.Guild(platform, cv.GuildId);
            if (!conversations.Add(id)) return (id, guildId);

            if (guildId != null && guilds.Add(guildId)) Guild(guildId, platform, NullIfBlank(cv.GuildName), atMs);

            string? title = NullIfBlank(cv.Title);
            string? parent = string.IsNullOrWhiteSpace(cv.ParentChannelId) ? null : CorpusIds.Conversation(platform, cv.ParentChannelId);
            var cur = c.Query(tx, "SELECT title, kind, guild_id, first_seen_ms FROM conversations WHERE conversation_id=$c",
                r => (Found: true, Title: r.GetStringOrNull(0), Kind: r.GetString(1), Guild: r.GetStringOrNull(2), First: r.GetInt64(3)), ("$c", id)).FirstOrDefault();
            if (!cur.Found)
            {
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, parent_conversation_id, title, first_seen_ms) VALUES($id,$p,$k,$g,$par,$t,$f)",
                    ("$id", id), ("$p", platform), ("$k", cv.Kind), ("$g", guildId), ("$par", parent), ("$t", title), ("$f", atMs));
                if (title != null)
                    c.Exec(tx, "INSERT OR IGNORE INTO conversation_names(conversation_id, name, valid_from_ms) VALUES($id,$n,$f)", ("$id", id), ("$n", title), ("$f", atMs));
                Change(ChangeKinds.ConversationUpsert, id, null, id);
                stats += new IngestStats { NewConversations = 1 };
                return (id, guildId);
            }

            bool retitled = title != null && title != cur.Title;
            bool kindLearned = cur.Kind == ConversationKinds.Unknown && cv.Kind != ConversationKinds.Unknown;
            bool guildLearned = cur.Guild == null && guildId != null;
            if (retitled || kindLearned || guildLearned)
            {
                c.Exec(tx, "UPDATE conversations SET title=COALESCE($t, title), kind=$k, guild_id=COALESCE(guild_id, $g) WHERE conversation_id=$id",
                    ("$t", title), ("$k", kindLearned ? cv.Kind : cur.Kind), ("$g", guildId), ("$id", id));
                if (retitled)
                {
                    // The new title holds from this message on; an older message cannot rename the conversation backwards.
                    if (cur.Title == null || atMs > cur.First)
                    {
                        c.Exec(tx, "UPDATE conversation_names SET valid_to_ms=$n WHERE conversation_id=$id AND valid_to_ms IS NULL AND valid_from_ms < $n", ("$n", atMs), ("$id", id));
                        c.Exec(tx, "INSERT OR IGNORE INTO conversation_names(conversation_id, name, valid_from_ms) VALUES($id,$n,$f)", ("$id", id), ("$n", title), ("$f", atMs));
                    }
                }
                Change(ChangeKinds.ConversationUpsert, id, null, id);
            }
            if (atMs < cur.First) c.Exec(tx, "UPDATE conversations SET first_seen_ms=$f WHERE conversation_id=$id", ("$f", atMs), ("$id", id));
            return (id, cur.Guild ?? guildId);
        }

        private void Guild(string guildId, string platform, string? name, long atMs)
        {
            var cur = c.Query(tx, "SELECT name FROM guilds WHERE guild_id=$g", r => (Found: true, Name: r.GetStringOrNull(0)), ("$g", guildId)).FirstOrDefault();
            if (!cur.Found)
            {
                c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,$p,$n,$t,$t)", ("$g", guildId), ("$p", platform), ("$n", name), ("$t", atMs));
                if (name != null) c.Exec(tx, "INSERT OR IGNORE INTO guild_names(guild_id, name, valid_from_ms) VALUES($g,$n,$t)", ("$g", guildId), ("$n", name), ("$t", atMs));
                Change(ChangeKinds.GuildUpsert, guildId, null, null);
            }
            else if (name != null && name != cur.Name)
            {
                c.Exec(tx, "UPDATE guilds SET name=$n, last_seen_ms=MAX(last_seen_ms,$t) WHERE guild_id=$g", ("$n", name), ("$t", atMs), ("$g", guildId));
                c.Exec(tx, "UPDATE guild_names SET valid_to_ms=$t WHERE guild_id=$g AND valid_to_ms IS NULL AND valid_from_ms < $t", ("$t", atMs), ("$g", guildId));
                c.Exec(tx, "INSERT OR IGNORE INTO guild_names(guild_id, name, valid_from_ms) VALUES($g,$n,$t)", ("$g", guildId), ("$n", name), ("$t", atMs));
                Change(ChangeKinds.GuildUpsert, guildId, null, null);
            }
        }

        private void Member(string conversationId, string identityId, long atMs)
        {
            if (!members.Add(conversationId + "\u0001" + identityId)) return;
            long known = c.Scalar<long>(tx, "SELECT COUNT(*) FROM conversation_members WHERE conversation_id=$c AND identity_id=$i", ("$c", conversationId), ("$i", identityId));
            if (known == 0)
                c.Exec(tx, "INSERT OR IGNORE INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'participant',$f)",
                    ("$c", conversationId), ("$i", identityId), ("$f", atMs));
        }

        // ── plumbing ─────────────────────────────────────────────────────────────

        private long Change(string kind, string entityId, string? person, string? conversationId) =>
            ChangeLog.Append(c, tx, now, kind, entityId, person, conversationId, sourceKind);

        /// <summary>Runs a prepared lookup and hands back its reader (the statement is reused, so dispose the reader first).</summary>
        private static SqliteDataReader FindReader(PreparedStatement statement, string key) => statement.Reader(key);

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public void Dispose()
        {
            findMessage.Dispose(); insertMessage.Dispose(); insertVersion.Dispose(); insertMention.Dispose(); insertEmbed.Dispose(); insertSticker.Dispose();
            insertPoll.Dispose(); insertAttachment.Dispose(); insertReaction.Dispose(); upsertReactionCount.Dispose(); findIdentity.Dispose();
            insertIdentity.Dispose(); findCanonical.Dispose();
        }
    }
}
