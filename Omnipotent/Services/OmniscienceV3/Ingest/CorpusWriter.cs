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
    public sealed partial class CorpusWriter : IDisposable
    {
        private readonly SqliteConnection c;
        private readonly SqliteTransaction tx;
        private readonly IngestSource source;
        private readonly string sourceKind;
        private readonly long now;
        private readonly ForgottenSet forgotten;

        private readonly Dictionary<string, IdentityState> identities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<NameInterval>> names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Hash, long From)> avatars = new(StringComparer.Ordinal);
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

        /// <param name="forgotten">
        /// The tombstones of forgotten people (N20, <see cref="Forgotten.Load"/>): nothing written by their accounts, in
        /// their direct chats or about their presence is stored again, and mentions of them are dropped. Every ingest
        /// path loads it once per batch and passes it; only tests that forget nobody leave it out.
        /// </param>
        public CorpusWriter(SqliteConnection c, SqliteTransaction tx, IngestSource source, string sourceKind, long nowMs, ForgottenSet? forgotten = null)
        {
            if (!SourceKinds.IsKnown(sourceKind)) throw new ArgumentException($"Unknown source kind '{sourceKind}'.", nameof(sourceKind));
            this.c = c;
            this.tx = tx;
            this.source = source;
            this.sourceKind = sourceKind;
            now = nowMs;
            this.forgotten = forgotten ?? ForgottenSet.None;

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

            string authorId = CorpusIds.Identity(m.Author.Platform, m.Author.PlatformUserId);
            // A forgotten person's message, or anything in their direct chat, is not stored again; not even quarantined,
            // which would keep their id (N20).
            if (forgotten.Any && forgotten.Blocks(authorId, CorpusIds.Conversation(m.Conversation.Platform.Trim().ToLowerInvariant(), m.Conversation.ChannelId)))
            {
                stats += new IngestStats { Forgotten = 1 };
                return false;
            }
            var author = Identity(m.Author, m.SentAtMs);
            var (conversationId, guildId) = Conversation(m.Conversation, m.SentAtMs);
            Member(conversationId, authorId, m.SentAtMs);
            foreach (var member in m.Conversation.Members ?? Array.Empty<IngestIdentity>())
            {
                if (string.IsNullOrWhiteSpace(member.Platform) || string.IsNullOrWhiteSpace(member.PlatformUserId)) continue;
                string memberId = CorpusIds.Identity(member.Platform, member.PlatformUserId);
                if (forgotten.Identities.Contains(memberId)) continue;
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
            bool changed = false, versioned = false;

            if (!string.Equals(m.Content, cur.Content, StringComparison.Ordinal) && IncomingContentWins(m, cur))
            {
                versioned = true;
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

            // The platform's own payload replaces what a poorer copy recorded about the message (its type, flags,
            // forward, embeds, poll): a row first written from a client library's relay, an export or v2 is brought
            // up to what the platform says (F394, F452). The text is not touched here, so this is not logged as a
            // new version and derived work is not redone for it.
            bool platformPayload = m.RawJson != null && source.Rank >= IngestSource.PlatformRank;
            if (platformPayload && source.Rank == cur.Rank)
            {
                // The platform showing the message again: what it gained since (an embed that finished loading) is added,
                // and after an edit the kept payload is the edited one.
                changed |= Details(m, messageId) > 0;
                if (versioned) c.Exec(tx, "UPDATE messages SET raw_json=$raw, flags=$f WHERE message_id=$m", ("$raw", m.RawJson), ("$f", m.Flags), ("$m", messageId));
            }
            if (platformPayload && source.Rank > cur.Rank)
            {
                c.Exec(tx, """
                    UPDATE messages SET raw_json=$raw, type=$ty, flags=$f, is_system=$sys, is_webhook=$wh, is_bot=MAX(is_bot,$bot),
                        forwarded_from_json=COALESCE($fw, forwarded_from_json), best_source_id=$src, best_source_rank=$rank
                    WHERE message_id=$m
                    """, ("$raw", m.RawJson), ("$ty", m.Type), ("$f", m.Flags), ("$sys", m.IsSystem ? 1 : 0), ("$wh", m.IsWebhook ? 1 : 0),
                    ("$bot", m.Author.IsBot ? 1 : 0), ("$fw", m.ForwardedFromJson), ("$src", source.SourceId), ("$rank", source.Rank), ("$m", messageId));
                Details(m, messageId);
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
            // Text beats no text whatever the source: a copy captured without the right to read content must never
            // shadow the real message (F458), and an empty copy never blanks a captured one.
            if (string.IsNullOrEmpty(cur.Content)) return m.Content.Length > 0;
            if (m.Content.Length == 0) return false;
            return source.Rank > cur.Rank;
        }

        private void Parts(IngestMessage m, string messageId, string? person, string conversationId)
        {
            Details(m, messageId);
            Attachments(m, messageId, person, conversationId);
            Reactions(m, messageId, person, conversationId);
        }

        /// <summary>Mentions, embeds, stickers and the poll. Rows that exist are left as they are; returns how many were new.</summary>
        private int Details(IngestMessage m, string messageId)
        {
            int added = 0;
            foreach (var mention in m.Mentions)
                if (!(mention.Kind == "user" && forgotten.Identities.Contains(mention.Target))) added += insertMention.Run(messageId, mention.Kind, mention.Target);
            for (int i = 0; i < m.Embeds.Count; i++)
            {
                var e = m.Embeds[i];
                added += insertEmbed.Run(messageId, i, e.Type, e.Provider, e.Url, e.Title, e.Description, e.Json);
            }
            foreach (var s in m.Stickers) added += insertSticker.Run(messageId, s.Id, s.Name);
            if (m.Poll is { } poll) added += insertPoll.Run(messageId, poll.Question, poll.AnswersJson, poll.ExpiryMs);
            return added;
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
                    string actorId = CorpusIds.Identity(r.Actor.Platform, r.Actor.PlatformUserId);
                    if (forgotten.Identities.Contains(actorId))
                    {
                        stats += new IngestStats { Forgotten = 1 };
                        continue;
                    }
                    Identity(r.Actor, r.AtMs ?? m.SentAtMs);
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

            ObserveProfile(id, i, seenAtMs, state.Canonical);
            return state;
        }

        /// <summary>Records the names and picture an account is seen with: its username, display name, a server nickname, its avatar.</summary>
        private void ObserveProfile(string id, IngestIdentity i, long seenAtMs, string? person)
        {
            // A profile that is "as of now" (see IngestIdentity.ObservedAtMs) is recorded at that time, not the message's.
            long nameAt = i.ObservedAtMs ?? seenAtMs;
            long? messageAt = i.ObservedAtMs != null ? seenAtMs : null;
            ObserveName(id, "username", i.Username, nameAt, person, messageAt);
            ObserveName(id, "display", i.DisplayName, nameAt, person, messageAt);
            if (NullIfBlank(i.NicknameScope) is { } scope)
            {
                // A scope with no nickname says the account has none there now: the one it had ends (F451).
                if (NullIfBlank(i.Nickname) != null) ObserveName(id, NicknameKind(scope), i.Nickname, nameAt, person, messageAt);
                else CloseName(id, NicknameKind(scope), nameAt, person);
            }
            ObserveAvatar(id, i.AvatarHash, nameAt, person);
        }

        /// <summary>
        /// An account that was only seen — in a member list, online, in voice — rather than doing something in a
        /// conversation. Its names and picture are recorded against its id, but it is not made into a person: a
        /// server's thousands of silent members are not people Klives knows (F397). The records link up by id the
        /// moment the account writes or reacts. An account already known is treated as usual.
        /// </summary>
        private (string Id, string? Person) Witness(IngestIdentity i, long atMs)
        {
            string id = CorpusIds.Identity(i.Platform, i.PlatformUserId);
            bool known = identities.ContainsKey(id);
            if (!known)
            {
                using var r = FindReader(findIdentity, id);
                known = r.Read();
            }
            if (known) return (id, Identity(i, atMs).Canonical);
            ObserveProfile(id, i, atMs, null);
            return (id, null);
        }

        /// <summary>The name kind a nickname is stored under: one timeline per server or group, keyed by its id (F421).</summary>
        public static string NicknameKind(string scope) => "nick:" + scope.Trim();

        /// <summary>
        /// Records that <paramref name="value"/> was the identity's name at <paramref name="atMs"/>. Sightings arrive
        /// in any order (a backfill runs backwards), so: a different name after the current one closes it and opens a
        /// new interval; a name before the earliest known one opens an interval that ends where the earliest began;
        /// the same name earlier just moves the start back. A different name sighted inside a closed interval is not
        /// recorded — the surrounding sightings already bound it and splitting on one message would invent precision.
        /// <para>
        /// <paramref name="firstKnownAt"/> is given when the name is "as of now" on an older message. The only name
        /// ever known for an account is then taken to reach back to that message — there is nothing better to show
        /// for it — but once a second name is known, old messages no longer move anything.
        /// </para>
        /// </summary>
        private void ObserveName(string identityId, string kind, string? value, long atMs, string? person, long? firstKnownAt = null)
        {
            value = NullIfBlank(value);
            if (value == null) return;
            var timeline = Timeline(identityId, kind);

            if (timeline.Count == 0)
            {
                long from = firstKnownAt is { } f && f < atMs ? f : atMs;
                InsertName(identityId, kind, value, from, null);
                timeline.Add(new NameInterval(value, from, null));
                Change(ChangeKinds.IdentityName, identityId, person, null);
                return;
            }
            if (firstKnownAt is { } older && timeline.Count == 1 && timeline[0].Value == value && older < timeline[0].From)
            {
                c.Exec(tx, "UPDATE identity_names SET valid_from_ms=$n WHERE identity_id=$i AND name_kind=$k AND valid_from_ms=$f",
                    ("$n", older), ("$i", identityId), ("$k", kind), ("$f", timeline[0].From));
                timeline[0] = timeline[0] with { From = older };
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

        /// <summary>The known intervals of one kind of name for an identity, oldest first (read once per transaction).</summary>
        private List<NameInterval> Timeline(string identityId, string kind)
        {
            string key = identityId + "\u0001" + kind;
            if (!names.TryGetValue(key, out var timeline))
            {
                timeline = c.Query(tx, "SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id=$i AND name_kind=$k ORDER BY valid_from_ms",
                    r => new NameInterval(r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2)), ("$i", identityId), ("$k", kind));
                names[key] = timeline;
            }
            return timeline;
        }

        /// <summary>Ends the open interval of a name that was taken away (a server nickname removed). True when one was open.</summary>
        private bool CloseName(string identityId, string kind, long atMs, string? person)
        {
            var timeline = Timeline(identityId, kind);
            if (timeline.Count == 0 || timeline[^1].To != null || timeline[^1].From >= atMs) return false;
            c.Exec(tx, "UPDATE identity_names SET valid_to_ms=$t WHERE identity_id=$i AND name_kind=$k AND valid_from_ms=$f",
                ("$t", atMs), ("$i", identityId), ("$k", kind), ("$f", timeline[^1].From));
            timeline[^1] = timeline[^1] with { To = atMs };
            Change(ChangeKinds.IdentityName, identityId, person, null);
            return true;
        }

        /// <summary>
        /// Records the account's picture (its platform hash; the file is fetched by the media side). Only a newer
        /// sighting replaces the current one: an older message cannot say what the picture was back then (F400).
        /// </summary>
        private void ObserveAvatar(string identityId, string? hash, long atMs, string? person)
        {
            hash = NullIfBlank(hash);
            if (hash == null) return;
            if (!avatars.TryGetValue(identityId, out var current))
            {
                current = c.Query(tx, "SELECT avatar_hash, valid_from_ms FROM identity_avatars WHERE identity_id=$i AND valid_to_ms IS NULL ORDER BY valid_from_ms DESC LIMIT 1",
                    r => (Hash: (string?)r.GetString(0), From: r.GetInt64(1)), ("$i", identityId)).FirstOrDefault();
                avatars[identityId] = current;
            }
            if (current.Hash == hash) return;
            if (current.Hash != null)
            {
                if (atMs <= current.From) return;
                c.Exec(tx, "UPDATE identity_avatars SET valid_to_ms=$t WHERE identity_id=$i AND valid_to_ms IS NULL", ("$t", atMs), ("$i", identityId));
            }
            c.Exec(tx, "INSERT OR IGNORE INTO identity_avatars(identity_id, avatar_hash, valid_from_ms) VALUES($i,$h,$f)", ("$i", identityId), ("$h", hash), ("$f", atMs));
            avatars[identityId] = (hash, atMs);
            Change(ChangeKinds.IdentityUpsert, identityId, person, null);
        }

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
            var cur = c.Query(tx, "SELECT title, kind, guild_id, first_seen_ms, parent_conversation_id FROM conversations WHERE conversation_id=$c",
                r => (Found: true, Title: r.GetStringOrNull(0), Kind: r.GetString(1), Guild: r.GetStringOrNull(2), First: r.GetInt64(3), Parent: r.GetStringOrNull(4)),
                ("$c", id)).FirstOrDefault();
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
            // A vague kind gives way to a specific one, and a DM that gained people is a group (F402).
            bool kindLearned = cv.Kind != cur.Kind && !ConversationKinds.IsVague(cv.Kind)
                               && (ConversationKinds.IsVague(cur.Kind) || (cur.Kind == ConversationKinds.Dm && cv.Kind == ConversationKinds.GroupDm));
            bool guildLearned = cur.Guild == null && guildId != null;
            bool parentLearned = cur.Parent == null && parent != null;
            if (retitled || kindLearned || guildLearned || parentLearned)
            {
                c.Exec(tx, """
                    UPDATE conversations SET title=COALESCE($t, title), kind=$k, guild_id=COALESCE(guild_id, $g),
                        parent_conversation_id=COALESCE(parent_conversation_id, $par)
                    WHERE conversation_id=$id
                    """, ("$t", title), ("$k", kindLearned ? cv.Kind : cur.Kind), ("$g", guildId), ("$par", parent), ("$id", id));
                if (retitled)
                {
                    // The new title holds from when it was seen; an older message cannot rename the conversation backwards.
                    long renamedAt = cv.ObservedAtMs ?? atMs;
                    if (cur.Title == null || renamedAt > cur.First)
                    {
                        c.Exec(tx, "UPDATE conversation_names SET valid_to_ms=$n WHERE conversation_id=$id AND valid_to_ms IS NULL AND valid_from_ms < $n", ("$n", renamedAt), ("$id", id));
                        c.Exec(tx, "INSERT OR IGNORE INTO conversation_names(conversation_id, name, valid_from_ms) VALUES($id,$n,$f)", ("$id", id), ("$n", title), ("$f", renamedAt));
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
