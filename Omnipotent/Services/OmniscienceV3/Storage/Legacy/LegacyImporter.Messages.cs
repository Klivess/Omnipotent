using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    public sealed partial class LegacyImporter
    {
        // ── conversations, guilds, members ───────────────────────────────────────

        private sealed record LegacyConversation(string Id, string? Platform, string? Kind, string? GuildId, string? GuildName, string? ChannelId,
            string? Title, object? FirstSeen, object? LastSeen);

        private async Task ImportConversationsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, CancellationToken ct)
        {
            const string table = "conversations";
            var rows = ReadAll(legacy, table,
                $"conversation_id, platform, {legacy.ColumnOrNull(table, "kind")}, {legacy.ColumnOrNull(table, "guild_id")}, {legacy.ColumnOrNull(table, "guild_name")}, " +
                $"{legacy.ColumnOrNull(table, "channel_id")}, {legacy.ColumnOrNull(table, "title")}, {legacy.ColumnOrNull(table, "first_seen")}, {legacy.ColumnOrNull(table, "last_seen")}",
                // Ordinal 0 is the rowid ReadAll prepends.
                r => new LegacyConversation(r.Text(1) ?? "", r.Text(2), r.Text(3), NullIfBlank(r.Text(4)), NullIfBlank(r.Text(5)), NullIfBlank(r.Text(6)),
                    NullIfBlank(r.Text(7)), r.Raw(8), r.Raw(9)));

            long now = clock.NowMs;
            foreach (var (_, cv) in rows)
            {
                if (string.IsNullOrWhiteSpace(cv.Id) || string.IsNullOrWhiteSpace(cv.Platform)) continue;
                ctx.PlatformSpellings.Add(cv.Platform);
                string platform = cv.Platform.ToLowerInvariant();
                string v3Id = cv.ChannelId != null ? CorpusIds.Conversation(platform, cv.ChannelId) : cv.Id;
                string? guild = cv.GuildId != null ? CorpusIds.Guild(platform, cv.GuildId) : null;
                if (ctx.Gone.Conversations.Contains(v3Id)) { ctx.ForgottenConversations.Add(cv.Id); continue; } // a forgotten person's direct chat
                ctx.Conversations[cv.Id] = (v3Id, guild, platform);
            }

            // A guild's name as v2 saw it, per conversation: the earliest sighting of each distinct name opens its interval.
            var guildNames = rows.Where(x => x.Row.GuildId != null && x.Row.Platform != null && x.Row.GuildName != null)
                .GroupBy(x => CorpusIds.Guild(x.Row.Platform!.ToLowerInvariant(), x.Row.GuildId!), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Row.GuildName!, StringComparer.Ordinal)
                    .Select(n => (Name: n.Key, From: n.Min(x => Ms(x.Row.FirstSeen, now)), Last: n.Max(x => Ms(x.Row.LastSeen, now))))
                    .OrderBy(n => n.From).ToList(), StringComparer.Ordinal);

            var participants = legacy.HasTable("conversation_participants")
                ? legacy.Connection.Query(null, "SELECT conversation_id, identity_id FROM conversation_participants", r => (Conv: r.Text(0) ?? "", Ident: r.Text(1) ?? ""))
                : new List<(string Conv, string Ident)>();

            var tr = report.Table(table);
            var counts = await stores.Corpus.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                var existing = c.Query(tx, "SELECT conversation_id, title, kind, guild_id FROM conversations",
                        r => (Id: r.GetString(0), Title: r.GetStringOrNull(1), Kind: r.GetString(2), Guild: r.GetStringOrNull(3)))
                    .ToDictionary(x => x.Id, StringComparer.Ordinal);
                var existingGuilds = c.Query(tx, "SELECT guild_id, name FROM guilds", r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

                foreach (var (guildId, names) in guildNames)
                {
                    var latest = names.OrderBy(n => n.Last).Last();
                    long first = names.Min(n => n.From), last = names.Max(n => n.Last);
                    if (!existingGuilds.TryGetValue(guildId, out var oldName))
                    {
                        c.Exec(tx, "INSERT INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,$p,$n,$f,$l)",
                            ("$g", guildId), ("$p", guildId[..guildId.IndexOf(':')]), ("$n", latest.Name), ("$f", first), ("$l", last));
                        Change(c, tx, now, ChangeKinds.GuildUpsert, guildId, null, null);
                    }
                    else if (oldName != latest.Name)
                    {
                        c.Exec(tx, "UPDATE guilds SET name=$n, last_seen_ms=MAX(last_seen_ms,$l) WHERE guild_id=$g", ("$n", latest.Name), ("$l", last), ("$g", guildId));
                        Change(c, tx, now, ChangeKinds.GuildUpsert, guildId, null, null);
                    }
                    for (int i = 0; i < names.Count; i++)
                    {
                        long? to = i + 1 < names.Count ? names[i + 1].From : null;
                        c.Exec(tx, """
                            INSERT INTO guild_names(guild_id, name, valid_from_ms, valid_to_ms) VALUES($g,$n,$f,$t)
                            ON CONFLICT(guild_id, valid_from_ms) DO UPDATE SET name=excluded.name, valid_to_ms=excluded.valid_to_ms
                            """, ("$g", guildId), ("$n", names[i].Name), ("$f", names[i].From), ("$t", to));
                    }
                }
                // Guilds known only by id (no name ever recorded) still get a row.
                foreach (var guildId in ctx.Conversations.Values.Select(v => v.GuildId).OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    if (existingGuilds.ContainsKey(guildId) || guildNames.ContainsKey(guildId)) continue;
                    c.Exec(tx, "INSERT OR IGNORE INTO guilds(guild_id, platform, name, first_seen_ms, last_seen_ms) VALUES($g,$p,NULL,$n,$n)",
                        ("$g", guildId), ("$p", guildId[..guildId.IndexOf(':')]), ("$n", now));
                }

                foreach (var (rowid, cv) in rows)
                {
                    if (ctx.ForgottenConversations.Contains(cv.Id)) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                    if (!ctx.Conversations.TryGetValue(cv.Id, out var mapped))
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "conversation without id or platform", cv, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    string kind = NullIfBlank(cv.Kind) ?? "unknown";
                    long first = Ms(cv.FirstSeen, now);
                    if (!existing.TryGetValue(mapped.ConversationId, out var cur))
                    {
                        c.Exec(tx, """
                            INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms) VALUES($id,$p,$k,$g,$t,$f)
                            """, ("$id", mapped.ConversationId), ("$p", mapped.Platform), ("$k", kind), ("$g", mapped.GuildId), ("$t", cv.Title), ("$f", first));
                        if (cv.Title != null)
                            c.Exec(tx, "INSERT OR IGNORE INTO conversation_names(conversation_id, name, valid_from_ms) VALUES($id,$n,$f)",
                                ("$id", mapped.ConversationId), ("$n", cv.Title), ("$f", first));
                        Change(c, tx, now, ChangeKinds.ConversationUpsert, mapped.ConversationId, null, mapped.ConversationId);
                        bc += new BatchCounts(1, 0, 0, 0);
                    }
                    else if (cur.Title != cv.Title || cur.Kind != kind || cur.Guild != mapped.GuildId)
                    {
                        c.Exec(tx, "UPDATE conversations SET title=$t, kind=$k, guild_id=$g WHERE conversation_id=$id",
                            ("$t", cv.Title), ("$k", kind), ("$g", mapped.GuildId), ("$id", mapped.ConversationId));
                        if (cv.Title != null && cur.Title != cv.Title)
                        {
                            c.Exec(tx, "UPDATE conversation_names SET valid_to_ms=$n WHERE conversation_id=$id AND valid_to_ms IS NULL AND valid_from_ms < $n", ("$n", now), ("$id", mapped.ConversationId));
                            c.Exec(tx, "INSERT OR IGNORE INTO conversation_names(conversation_id, name, valid_from_ms) VALUES($id,$n,$f)",
                                ("$id", mapped.ConversationId), ("$n", cv.Title), ("$f", cur.Title == null ? first : now));
                        }
                        Change(c, tx, now, ChangeKinds.ConversationUpsert, mapped.ConversationId, null, mapped.ConversationId);
                        bc += new BatchCounts(0, 1, 0, 0);
                    }
                    else bc += new BatchCounts(0, 0, 1, 0);
                }

                // v2 recorded who wrote in a conversation, not when they joined: membership runs from the conversation's first sighting.
                using var member = new PreparedStatement(c, tx,
                    "INSERT OR IGNORE INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES($c,$i,'participant',$f)", "$c", "$i", "$f");
                var firstSeen = rows.GroupBy(x => x.Row.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => Ms(g.First().Row.FirstSeen, now), StringComparer.Ordinal);
                foreach (var (conv, ident) in participants)
                {
                    if (!ctx.Conversations.TryGetValue(conv, out var mc) || !ctx.Identities.TryGetValue(ident, out var mi)) continue;
                    member.Run(mc.ConversationId, mi.IdentityId, firstSeen.GetValueOrDefault(conv, now));
                }
                return bc;
            }, ct).ConfigureAwait(false);
            tr.Add(counts, rows.Count);
        }

        // ── messages ─────────────────────────────────────────────────────────────

        private sealed record LegacyMessage(string Id, string? Platform, string? PlatformMessageId, string? ConversationId, string? AuthorIdentityId,
            object? SentAt, object? EditedAt, string? Content, string? ReplyTo, string? RawJson, object? CapturedAt);

        private sealed record MessagePlan(
            long Rowid, string MessageId, string Platform, string PlatformMessageId, string ConversationId, string? GuildId, string AuthorIdentityId,
            string? PersonId, LegacyInstant Time, long OrderKey, DiscordRawMessage? Raw, string? ReplyTo, IReadOnlyList<VersionRow> Versions,
            long IngestedAtMs, string? RawJson, long? DeletedAtMs = null);

        private const string MessageColumnsTemplate =
            "message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at, {edited_at}, {content}, {reply}, {raw}, {captured}";

        private static string MessageColumns(LegacyDatabase legacy) => MessageColumnsTemplate
            .Replace("{edited_at}", legacy.ColumnOrNull("messages", "edited_at"))
            .Replace("{content}", legacy.ColumnOrNull("messages", "content"))
            .Replace("{reply}", legacy.ColumnOrNull("messages", "reply_to_message_id"))
            .Replace("{raw}", legacy.ColumnOrNull("messages", "raw_json"))
            .Replace("{captured}", legacy.ColumnOrNull("messages", "captured_at"));

        private static LegacyMessage ReadMessage(SqliteDataReader r) => new(
            r.Text(1) ?? "", r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Raw(6), r.Raw(7), r.Text(8), NullIfBlank(r.Text(9)), r.Text(10), r.Raw(11));

        private async Task ImportMessagesAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            // The query reads "rowid, <columns>", so column ordinals in ReadMessage start at 1.
            await StreamAsync(legacy, "messages", MessageColumns(legacy), r => ReadMessage(r),
                rows => PlanMessages(legacy, ctx, report, rows),
                (c, tx, plans, now) => WriteMessages(c, tx, plans, ctx, now),
                report, progress, ct).ConfigureAwait(false);
        }

        /// <summary>The "problem" of a row that belongs to a forgotten person: it is skipped, never quarantined.</summary>
        private const string ForgottenRow = "forgotten";

        private (List<MessagePlan> Plans, List<(long Rowid, string Reason, LegacyMessage Row)> Rejected, int Forgotten) PlanMessages(
            LegacyDatabase legacy, Context ctx, LegacyImportReport report, IReadOnlyList<(long Rowid, LegacyMessage Row)> rows)
        {
            var edits = LoadEdits(legacy, rows.Where(x => !ctx.RowForgotten(x.Row.AuthorIdentityId, x.Row.ConversationId)).Select(x => x.Row.Id).Where(id => id.Length > 0).ToList());
            var plans = new List<MessagePlan>(rows.Count);
            var rejected = new List<(long, string, LegacyMessage)>();
            int forgotten = 0;
            foreach (var (rowid, m) in rows)
            {
                string? problem = PlanMessage(rowid, m, ctx, edits, report, out var plan);
                if (ReferenceEquals(problem, ForgottenRow)) forgotten++;
                else if (problem != null) rejected.Add((rowid, problem, m));
                else plans.Add(plan!);
            }
            return (plans, rejected, forgotten);
        }

        private string? PlanMessage(long rowid, LegacyMessage m, Context ctx, IReadOnlyDictionary<string, List<LegacyEdit>> edits, LegacyImportReport report, out MessagePlan? plan)
        {
            plan = null;
            if (ctx.RowForgotten(m.AuthorIdentityId, m.ConversationId)) return ForgottenRow;
            if (string.IsNullOrWhiteSpace(m.Platform) || string.IsNullOrWhiteSpace(m.PlatformMessageId)) return "message without platform or platform id";
            string platform = m.Platform.Trim().ToLowerInvariant();
            if (m.AuthorIdentityId == null || !ctx.Identities.TryGetValue(m.AuthorIdentityId, out var author)) return "author identity missing from v2";
            if (m.ConversationId == null || !ctx.Conversations.TryGetValue(m.ConversationId, out var conv)) return "conversation missing from v2";
            if (!LegacyTime.TryNormalize(m.SentAt, out long legacySent, out _)) return "unreadable send time";

            var time = LegacyTime.Message(platform, m.PlatformMessageId.Trim(), legacySent, assumedZone);
            if (!LegacyTime.IsPlausible(time.Ms)) return "send time outside 2000–2100";

            DiscordRawMessage? raw = platform == "discord" ? DiscordRaw.TryParse(m.RawJson) : null;
            string? reply;
            if (raw != null)
            {
                reply = raw.ReplyToPlatformMessageId != null ? CorpusIds.Message(platform, raw.ReplyToPlatformMessageId) : null;
                if (raw.ForwardJson != null && m.ReplyTo != null) report.ForwardsRecovered++;
            }
            else reply = m.ReplyTo;

            edits.TryGetValue(m.Id, out var messageEdits);
            var versions = VersionHistory.Rebuild(m.Content, MsOrNull(m.EditedAt), messageEdits ?? (IReadOnlyList<LegacyEdit>)Array.Empty<LegacyEdit>(), out bool consistent);
            if (!consistent) report.VersionHistoriesRepaired++;

            long orderKey = platform == "discord" && ulong.TryParse(m.PlatformMessageId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong snowflake) && snowflake <= long.MaxValue
                ? (long)snowflake
                : rowid;
            long ingested = Ms(m.CapturedAt, time.Ms);

            report.CountQuality(time.Quality);
            if (platform == "discord" && Math.Abs(time.DriftMs) > 2000) report.DiscordDriftOver2s++;
            if (platform == "discord") report.MaxDiscordDriftMs = Math.Max(report.MaxDiscordDriftMs, Math.Abs(time.DriftMs));
            if (time.WasAmbiguous) report.WallClockAmbiguous++;
            if (time.WasInvalid) report.WallClockInvalid++;

            string messageId = CorpusIds.Message(platform, m.PlatformMessageId);
            string? guild = raw?.GuildId != null ? CorpusIds.Guild(platform, raw.GuildId) : conv.GuildId;
            plan = new MessagePlan(rowid, messageId, platform, m.PlatformMessageId.Trim(), conv.ConversationId, guild, author.IdentityId,
                ctx.CanonicalOf(author.PersonId), time, orderKey, raw, reply, versions, ingested, m.RawJson);
            return null;
        }

        /// <summary>All of v2's logged edits for these messages, oldest first.</summary>
        private static Dictionary<string, List<LegacyEdit>> LoadEdits(LegacyDatabase legacy, IReadOnlyCollection<string> messageIds)
        {
            if (!legacy.HasTable("message_edits") || messageIds.Count == 0) return new Dictionary<string, List<LegacyEdit>>(StringComparer.Ordinal);
            return legacy.Connection.QueryIn(null,
                    "SELECT message_id, old_content, new_content, edited_at, rowid FROM message_edits WHERE message_id IN ({keys})", messageIds,
                    r => (Id: r.Text(0) ?? "", Edit: new LegacyEdit(r.Text(1), r.Text(2), Ms(r.Raw(3), 0), r.GetInt64(4))))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Edit).ToList(), StringComparer.Ordinal);
        }

        private static BatchCounts WriteMessages(SqliteConnection c, SqliteTransaction tx,
            (List<MessagePlan> Plans, List<(long Rowid, string Reason, LegacyMessage Row)> Rejected, int Forgotten) batch, Context ctx, long now)
        {
            var bc = new BatchCounts(0, 0, batch.Forgotten, 0);
            foreach (var (rowid, reason, row) in batch.Rejected)
                if (Quarantine(c, tx, "messages", rowid.ToString(CultureInfo.InvariantCulture), reason, new
                    {
                        row.Id, row.Platform, row.ConversationId, row.AuthorIdentityId,
                        // The account as v3 names it, so that forgetting its owner also finds what was set aside here.
                        Author = row.AuthorIdentityId != null && ctx.Identities.TryGetValue(row.AuthorIdentityId, out var who) ? who.IdentityId : null,
                        row.Content,
                    }, now))
                    bc += new BatchCounts(0, 0, 0, 1);

            var existing = c.QueryIn(tx, "SELECT message_id FROM messages WHERE message_id IN ({keys})", batch.Plans.Select(p => p.MessageId).ToList(), r => r.GetString(0))
                .ToHashSet(StringComparer.Ordinal);
            using var w = new MessageWriter(c, tx, ctx.Gone);
            var lastByConversation = new Dictionary<string, long>(StringComparer.Ordinal);
            var bots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in batch.Plans)
            {
                if (!existing.Add(p.MessageId)) { bc += new BatchCounts(0, 0, 1, 0); continue; } // already imported (or a duplicate in this batch)
                w.Insert(p, now);
                lastByConversation[p.ConversationId] = Math.Max(lastByConversation.GetValueOrDefault(p.ConversationId), p.Time.Ms);
                if (p.Raw?.AuthorBot == true) bots.Add(p.AuthorIdentityId);
                bc += new BatchCounts(1, 0, 0, 0);
            }
            foreach (var (conv, last) in lastByConversation)
                c.Exec(tx, "UPDATE conversations SET last_message_ms=MAX(COALESCE(last_message_ms,0),$t) WHERE conversation_id=$c", ("$t", last), ("$c", conv));
            foreach (var bot in bots)
                c.Exec(tx, "UPDATE identities SET kind='bot' WHERE identity_id=$i AND kind='user'", ("$i", bot));
            return bc;
        }

        /// <summary>Prepared statements for inserting messages and their parts inside one batch transaction.</summary>
        private sealed class MessageWriter : IDisposable
        {
            private readonly SqliteConnection c;
            private readonly SqliteTransaction tx;
            private readonly PreparedStatement message, version, mention, embed, sticker, poll, attachment, reactionCount;
            private readonly ForgottenSet gone;

            public MessageWriter(SqliteConnection c, SqliteTransaction tx, ForgottenSet gone)
            {
                this.c = c;
                this.tx = tx;
                this.gone = gone;
                message = new PreparedStatement(c, tx, """
                    INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, guild_id, author_identity_id, sent_at_ms, time_quality,
                        order_key, type, flags, is_bot, is_system, is_webhook, reply_to_message_id, forwarded_from_json, current_version, content, edited_at_ms,
                        deleted_at_ms, first_source_kind, best_source_id, best_source_rank, ingested_at_ms, ingest_seq, raw_json)
                    VALUES($id,$p,$pm,$c,$g,$a,$t,$q,$o,$ty,$f,$bot,$sys,$wh,$r,$fw,$v,$content,$e,$d,'legacy',$src,0,$ing,$seq,$raw)
                    """, "$id", "$p", "$pm", "$c", "$g", "$a", "$t", "$q", "$o", "$ty", "$f", "$bot", "$sys", "$wh", "$r", "$fw", "$v", "$content", "$e", "$d", "$src", "$ing", "$seq", "$raw");
                version = new PreparedStatement(c, tx,
                    "INSERT OR IGNORE INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,$v,$c,$e,$r,$s)",
                    "$m", "$v", "$c", "$e", "$r", "$s");
                mention = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_mentions(message_id, target_kind, target_id) VALUES($m,$k,$t)", "$m", "$k", "$t");
                embed = new PreparedStatement(c, tx,
                    "INSERT OR IGNORE INTO message_embeds(message_id, idx, type, provider, url, title, description, json) VALUES($m,$i,$t,$p,$u,$ti,$d,$j)",
                    "$m", "$i", "$t", "$p", "$u", "$ti", "$d", "$j");
                sticker = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_stickers(message_id, sticker_id, name) VALUES($m,$s,$n)", "$m", "$s", "$n");
                poll = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO message_polls(message_id, question, answers_json, expiry_ms) VALUES($m,$q,$a,$e)", "$m", "$q", "$a", "$e");
                attachment = new PreparedStatement(c, tx, """
                    INSERT OR IGNORE INTO attachments(attachment_id, message_id, idx, platform_attachment_id, filename, mime, media_kind, size_bytes, width, height,
                        duration_ms, alt_text, is_spoiler, source_url, download_state, created_at_ms)
                    VALUES($id,$m,$i,$pa,$fn,$mi,$k,$s,$w,$h,$d,$alt,$sp,$u,'pending',$t)
                    """, "$id", "$m", "$i", "$pa", "$fn", "$mi", "$k", "$s", "$w", "$h", "$d", "$alt", "$sp", "$u", "$t");
                reactionCount = new PreparedStatement(c, tx, """
                    INSERT INTO reaction_counts(message_id, emoji_key, count, observed_at_ms) VALUES($m,$e,$n,$t)
                    ON CONFLICT(message_id, emoji_key) DO UPDATE SET count=excluded.count, observed_at_ms=excluded.observed_at_ms
                    WHERE excluded.observed_at_ms >= reaction_counts.observed_at_ms
                    """, "$m", "$e", "$n", "$t");
            }

            public void Insert(MessagePlan p, long now)
            {
                long seq = Change(c, tx, now, ChangeKinds.MessageInsert, p.MessageId, p.PersonId, p.ConversationId);
                var latest = p.Versions[^1];
                var raw = p.Raw;
                message.Run(p.MessageId, p.Platform, p.PlatformMessageId, p.ConversationId, p.GuildId, p.AuthorIdentityId, p.Time.Ms, (int)p.Time.Quality,
                    p.OrderKey, raw?.Type ?? 0, raw?.Flags ?? 0, raw?.AuthorBot == true ? 1 : 0, raw?.AuthorSystem == true ? 1 : 0, raw?.IsWebhook == true ? 1 : 0,
                    p.ReplyTo, raw?.ForwardJson, latest.Version, latest.Content, latest.EditedAtMs, p.DeletedAtMs, CorpusIds.LegacySourceId,
                    p.IngestedAtMs, seq, p.RawJson);
                foreach (var v in p.Versions) version.Run(p.MessageId, v.Version, v.Content, v.EditedAtMs, p.IngestedAtMs, CorpusIds.LegacySourceId);
                if (raw == null) return;
                // Somebody else's message stays, but not the record that it pinged a forgotten person.
                foreach (var m in raw.Mentions)
                    if (!(m.Kind == "user" && gone.Identities.Contains(m.Target))) mention.Run(p.MessageId, m.Kind, m.Target);
                for (int i = 0; i < raw.Embeds.Count; i++)
                {
                    var e = raw.Embeds[i];
                    embed.Run(p.MessageId, i, e.Type, e.Provider, e.Url, e.Title, e.Description, e.Json);
                }
                foreach (var s in raw.Stickers) sticker.Run(p.MessageId, s.Id, s.Name);
                if (raw.Poll is { } pl) poll.Run(p.MessageId, pl.Question, pl.AnswersJson, pl.ExpiryMs);
                for (int i = 0; i < raw.Attachments.Count; i++)
                {
                    var a = raw.Attachments[i];
                    string id = CorpusIds.Attachment(p.MessageId, i);
                    if (attachment.Run(id, p.MessageId, i, a.Id, a.Filename, a.ContentType, a.MediaKind, a.Size, a.Width, a.Height, a.DurationMs, a.AltText,
                            a.Spoiler ? 1 : 0, a.Url, p.Time.Ms) == 1)
                        Change(c, tx, now, ChangeKinds.AttachmentAdd, id, p.PersonId, p.ConversationId);
                }
                foreach (var r in raw.Reactions) reactionCount.Run(p.MessageId, r.EmojiKey, r.Count, p.IngestedAtMs);
            }

            public void Dispose()
            {
                message.Dispose(); version.Dispose(); mention.Dispose(); embed.Dispose(); sticker.Dispose(); poll.Dispose(); attachment.Dispose(); reactionCount.Dispose();
            }
        }

        // ── edits ────────────────────────────────────────────────────────────────

        private async Task ImportEditsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            if (!legacy.HasTable("messages")) return;
            string messageColumns = MessageColumns(legacy);
            await StreamAsync(legacy, "message_edits", "message_id", r => r.Text(1) ?? "",
                rows =>
                {
                    // Rebuild each affected message's full history from every edit v2 logged for it.
                    var ids = rows.Select(x => x.Row).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                    var current = legacy.Connection.QueryIn(null, $"SELECT rowid, {messageColumns} FROM messages WHERE message_id IN ({{keys}})", ids,
                            r => ReadMessage(r))
                        .Where(m => !ctx.RowForgotten(m.AuthorIdentityId, m.ConversationId))
                        .ToDictionary(m => m.Id, StringComparer.Ordinal);
                    // Edits of a forgotten person's messages are not even read.
                    var edits = LoadEdits(legacy, ids.Where(current.ContainsKey).ToList());
                    return ids.Select(id => (Id: CurrentId(id, current), Versions: current.TryGetValue(id, out var m)
                            ? VersionHistory.Rebuild(m.Content, MsOrNull(m.EditedAt), edits.GetValueOrDefault(id) ?? new List<LegacyEdit>(), out _)
                            : null))
                        .ToList();
                },
                (c, tx, plans, now) =>
                {
                    var bc = new BatchCounts();
                    foreach (var (id, versions) in plans)
                    {
                        if (versions == null || id == null) { bc += new BatchCounts(0, 0, 1, 0); continue; } // the message itself was never captured by v2
                        var stored = c.Query(tx, "SELECT version, content, edited_at_ms FROM message_versions WHERE message_id=$m ORDER BY version",
                            r => new VersionRow(r.GetInt32(0), r.GetStringOrNull(1), r.GetInt64OrNull(2)), ("$m", id));
                        if (stored.Count == 0) { bc += new BatchCounts(0, 0, 1, 0); continue; } // not imported (quarantined): nothing to amend
                        if (stored.SequenceEqual(versions)) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                        c.Exec(tx, "DELETE FROM message_versions WHERE message_id=$m", ("$m", id));
                        foreach (var v in versions)
                            c.Exec(tx, "INSERT INTO message_versions(message_id, version, content, edited_at_ms, recorded_at_ms, source_id) VALUES($m,$v,$c,$e,$r,$s)",
                                ("$m", id), ("$v", v.Version), ("$c", v.Content), ("$e", v.EditedAtMs), ("$r", now), ("$s", CorpusIds.LegacySourceId));
                        var latest = versions[^1];
                        var owner = c.Query(tx, "SELECT i.person_id, m.conversation_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.message_id=$m",
                            r => (Person: r.GetStringOrNull(0), Conv: r.GetString(1)), ("$m", id)).FirstOrDefault();
                        c.Exec(tx, "UPDATE messages SET content=$c, current_version=$v, edited_at_ms=$e WHERE message_id=$m",
                            ("$c", latest.Content), ("$v", latest.Version), ("$e", latest.EditedAtMs), ("$m", id));
                        Change(c, tx, now, ChangeKinds.MessageVersion, id, ctx.CanonicalOf(owner.Person), owner.Conv);
                        bc += new BatchCounts(0, 1, 0, 0);
                    }
                    return bc;
                }, report, progress, ct).ConfigureAwait(false);

            static string? CurrentId(string legacyId, Dictionary<string, LegacyMessage> current) =>
                current.TryGetValue(legacyId, out var m) && !string.IsNullOrWhiteSpace(m.Platform) && !string.IsNullOrWhiteSpace(m.PlatformMessageId)
                    ? CorpusIds.Message(m.Platform.Trim().ToLowerInvariant(), m.PlatformMessageId) : null;
        }

        // ── deletes ──────────────────────────────────────────────────────────────

        private sealed record LegacyDelete(string MessageId, string? ChannelId, string? AuthorIdentityId, string? Content, object? DeletedAt);

        private async Task ImportDeletesAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "message_deletes";
            await StreamAsync(legacy, table,
                $"message_id, {legacy.ColumnOrNull(table, "channel_id")}, {legacy.ColumnOrNull(table, "author_identity_id")}, {legacy.ColumnOrNull(table, "content")}, deleted_at",
                r => new LegacyDelete(r.Text(1) ?? "", NullIfBlank(r.Text(2)), r.Text(3), r.Text(4), r.Raw(5)),
                rows => rows.Select(x => (x.Rowid, x.Row, Recovery: PlanRecovery(x.Rowid, x.Row, ctx))).ToList(),
                (c, tx, plans, now) =>
                {
                    var bc = new BatchCounts();
                    using var w = new MessageWriter(c, tx, ctx.Gone);
                    foreach (var (rowid, d, recovery) in plans)
                    {
                        if (DeleteForgotten(d, ctx)) { bc += new BatchCounts(0, 0, 1, 0); continue; } // v2 kept the deleted text: not v3
                        long? at = MsOrNull(d.DeletedAt);
                        if (at == null || d.MessageId.Length == 0)
                        {
                            if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "delete without message id or time", d, now)) bc += new BatchCounts(0, 0, 0, 1);
                            continue;
                        }
                        string v3Id = d.MessageId.Contains(':') ? d.MessageId : CorpusIds.Message("discord", d.MessageId);
                        var owner = c.Query(tx, "SELECT m.deleted_at_ms, i.person_id, m.conversation_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.message_id=$m",
                            r => (Found: true, Deleted: r.GetInt64OrNull(0), Person: r.GetStringOrNull(1), Conv: r.GetString(2)), ("$m", v3Id)).FirstOrDefault();
                        if (owner.Found)
                        {
                            if (owner.Deleted == null)
                            {
                                c.Exec(tx, "UPDATE messages SET deleted_at_ms=$t WHERE message_id=$m", ("$t", at.Value), ("$m", v3Id));
                                Change(c, tx, now, ChangeKinds.MessageDelete, v3Id, ctx.CanonicalOf(owner.Person), owner.Conv);
                                bc += new BatchCounts(0, 1, 0, 0);
                            }
                            else bc += new BatchCounts(0, 0, 1, 0);
                        }
                        else if (recovery != null)
                        {
                            // Captured only in v2's deleted-message archive: recovered as a deleted message.
                            w.Insert(recovery with { DeletedAtMs = at }, now);
                            bc += new BatchCounts(1, 0, 0, 0);
                        }
                        else bc += new BatchCounts(0, 0, 1, 0); // deleted before v2 ever captured it: nothing to keep
                    }
                    return bc;
                }, report, progress, ct).ConfigureAwait(false);
        }

        /// <summary>A deleted message that a forgotten person wrote, or that was in their direct chat.</summary>
        private static bool DeleteForgotten(LegacyDelete d, Context ctx)
        {
            if (d.AuthorIdentityId != null && ctx.ForgottenIdentities.Contains(d.AuthorIdentityId)) return true;
            if (d.ChannelId == null || ctx.Gone.Conversations.Count == 0) return false;
            string platform = d.MessageId.Contains(':') ? d.MessageId[..d.MessageId.IndexOf(':')] : "discord";
            return ctx.Gone.Conversations.Contains(CorpusIds.Conversation(platform, d.ChannelId));
        }

        private MessagePlan? PlanRecovery(long rowid, LegacyDelete d, Context ctx)
        {
            if (DeleteForgotten(d, ctx)) return null;
            if (d.Content == null || d.AuthorIdentityId == null || d.ChannelId == null || !ctx.Identities.TryGetValue(d.AuthorIdentityId, out var author)) return null;
            string platform = d.MessageId.Contains(':') ? d.MessageId[..d.MessageId.IndexOf(':')] : "discord";
            string native = CorpusIds.Native(d.MessageId);
            if (platform != "discord" || !Instant.TryFromDiscordSnowflake(native, out long sent) || !LegacyTime.IsPlausible(sent)) return null;
            string conversation = CorpusIds.Conversation(platform, d.ChannelId);
            string? guild = ctx.Conversations.Values.FirstOrDefault(v => v.ConversationId == conversation).GuildId;
            return new MessagePlan(rowid, CorpusIds.Message(platform, native), platform, native, conversation, guild, author.IdentityId,
                ctx.CanonicalOf(author.PersonId), new LegacyInstant(sent, TimeQuality.Exact, 0, false, false), (long)ulong.Parse(native, CultureInfo.InvariantCulture),
                null, null, new[] { new VersionRow(1, d.Content, null) }, sent, null);
        }
    }
}
