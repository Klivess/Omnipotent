using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    public sealed partial class LegacyImporter
    {
        // ── attachments ──────────────────────────────────────────────────────────

        private sealed record LegacyAttachment(string? Id, string? MessageId, string? Kind, string? Url, string? LocalPath, string? Mime, long? Size, string? Sha256);

        private sealed record AttachmentPlan(long Rowid, LegacyAttachment Row, string? MessageId, string? RelativePath, long? FileBytes, string State);

        /// <summary>v2 stored media content-addressed under <c>Attachments/aa/bb/&lt;sha256&gt;.ext</c> in its data folder.</summary>
        internal static string LegacyMediaRelative(string sha256, string fileName) =>
            $"{MediaPaths.LegacyPrefix}Attachments/{sha256[..2]}/{sha256.Substring(2, 2)}/{fileName}";

        private async Task ImportAttachmentsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "attachments";
            await StreamAsync(legacy, table,
                $"attachment_id, message_id, {legacy.ColumnOrNull(table, "kind")}, {legacy.ColumnOrNull(table, "original_url")}, {legacy.ColumnOrNull(table, "local_path")}, " +
                $"{legacy.ColumnOrNull(table, "mime")}, {legacy.ColumnOrNull(table, "size_bytes")}, {legacy.ColumnOrNull(table, "sha256")}",
                r => new LegacyAttachment(r.Text(1), r.Text(2), r.Text(3), NullIfBlank(r.Text(4)), NullIfBlank(r.Text(5)), NullIfBlank(r.Text(6)), r.Number(7), NullIfBlank(r.Text(8))?.ToLowerInvariant()),
                rows =>
                {
                    var gone = ForgottenLegacyMessages(legacy, ctx, rows.Select(x => x.Row.MessageId).OfType<string>());
                    var kept = rows.Where(x => x.Row.MessageId == null || !gone.Contains(x.Row.MessageId)).ToList();
                    return (Plans: kept.Select(x => PlanAttachment(x.Rowid, x.Row, report)).ToList(), Forgotten: rows.Count - kept.Count);
                },
                (c, tx, batch, now) => WriteAttachments(c, tx, batch.Plans, ctx, now) + new BatchCounts(0, 0, batch.Forgotten, 0),
                report, progress, ct).ConfigureAwait(false);
        }

        /// <summary>Which of these v2 message ids belong to a forgotten person (their own messages, and everything in their
        /// direct chats). Reads v2 only when somebody has been forgotten at all.</summary>
        private static HashSet<string> ForgottenLegacyMessages(LegacyDatabase legacy, Context ctx, IEnumerable<string> legacyMessageIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (ctx.ForgottenIdentities.Count + ctx.ForgottenConversations.Count == 0 || !legacy.HasTable("messages")) return result;
            var ids = legacyMessageIds.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            foreach (var (id, author, conversation) in legacy.Connection.QueryIn(null, "SELECT message_id, author_identity_id, conversation_id FROM messages WHERE message_id IN ({keys})", ids,
                         r => (r.Text(0) ?? "", r.Text(1), r.Text(2))))
                if (ctx.RowForgotten(author, conversation)) result.Add(id);
            return result;
        }

        /// <summary>The same question for platform message ids (what v2's reaction log carries), as v3 message ids.</summary>
        private static HashSet<string> ForgottenPlatformMessages(LegacyDatabase legacy, Context ctx, IEnumerable<(string Platform, string PlatformMessageId)> messages)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (ctx.ForgottenIdentities.Count + ctx.ForgottenConversations.Count == 0 || !legacy.HasTable("messages")) return result;
            // v2 indexes messages by (platform, platform id), with the platform spelled as v2 stored it.
            foreach (var group in messages.Distinct().GroupBy(m => m.Platform, StringComparer.Ordinal))
                foreach (string spelling in ctx.PlatformSpellings.Where(s => s.Trim().ToLowerInvariant() == group.Key).DefaultIfEmpty(group.Key))
                    foreach (var chunk in group.Select(m => m.PlatformMessageId).Chunk(400))
                    {
                        var args = chunk.Select((id, n) => ("$k" + n.ToString(CultureInfo.InvariantCulture), (object?)id)).Append(("$p", (object?)spelling)).ToArray();
                        string keys = string.Join(",", chunk.Select((_, n) => "$k" + n.ToString(CultureInfo.InvariantCulture)));
                        foreach (var (id, author, conversation) in legacy.Connection.Query(null,
                                     $"SELECT platform_message_id, author_identity_id, conversation_id FROM messages WHERE platform=$p AND platform_message_id IN ({keys})",
                                     r => (r.Text(0) ?? "", r.Text(1), r.Text(2)), args))
                            if (ctx.RowForgotten(author, conversation)) result.Add(CorpusIds.Message(group.Key, id));
                    }
            return result;
        }

        /// <summary>Finds v2's file on disk (outside the writer): its canonical content-addressed place, or the stored path
        /// when that is still inside v2's data folder.</summary>
        private AttachmentPlan PlanAttachment(long rowid, LegacyAttachment a, LegacyImportReport report)
        {
            string? messageId = a.MessageId == null ? null
                : a.MessageId.Contains(':') ? CorpusIds.Message(a.MessageId[..a.MessageId.IndexOf(':')], CorpusIds.Native(a.MessageId))
                : CorpusIds.Message("discord", a.MessageId);
            if (a.Sha256 == null || a.Sha256.Length < 8 || !a.Sha256.All(Uri.IsHexDigit)) return new AttachmentPlan(rowid, a, messageId, null, null, "pending");

            string fileName = Path.GetFileName(a.LocalPath ?? "") is { Length: > 0 } f && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !f.Contains(':') && f != ".." ? f : a.Sha256;
            string relative = LegacyMediaRelative(a.Sha256, fileName);
            string candidate = MediaPaths.Resolve(stores.Paths, relative);
            if (File.Exists(candidate))
            {
                long bytes = new FileInfo(candidate).Length;
                report.MediaFilesFound++;
                report.MediaBytesReferenced += bytes;
                return new AttachmentPlan(rowid, a, messageId, relative, bytes, "done");
            }
            if (a.LocalPath != null && Path.IsPathFullyQualified(a.LocalPath) && File.Exists(a.LocalPath))
            {
                string rel = Path.GetRelativePath(stores.Paths.LegacyRoot, a.LocalPath).Replace('\\', '/');
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                {
                    long bytes = new FileInfo(a.LocalPath).Length;
                    report.MediaFilesFound++;
                    report.MediaBytesReferenced += bytes;
                    return new AttachmentPlan(rowid, a, messageId, MediaPaths.LegacyPrefix + rel, bytes, "done");
                }
            }
            report.MediaFilesMissing++;
            return new AttachmentPlan(rowid, a, messageId, null, null, "missing");
        }

        private static BatchCounts WriteAttachments(SqliteConnection c, SqliteTransaction tx, List<AttachmentPlan> plans, Context ctx, long now)
        {
            var bc = new BatchCounts();
            foreach (var p in plans)
            {
                var a = p.Row;
                var msg = p.MessageId == null ? default
                    : c.Query(tx, "SELECT m.sent_at_ms, i.person_id, m.conversation_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.message_id=$m",
                        r => (Found: true, Sent: r.GetInt64(0), Person: r.GetStringOrNull(1), Conv: r.GetString(2)), ("$m", p.MessageId)).FirstOrDefault();
                if (!msg.Found)
                {
                    if (Quarantine(c, tx, "attachments", p.Rowid.ToString(CultureInfo.InvariantCulture), "the attachment's message was not imported", a, now)) bc += new BatchCounts(0, 0, 0, 1);
                    continue;
                }
                string? person = ctx.CanonicalOf(msg.Person);
                if (p.RelativePath != null)
                    c.Exec(tx, "INSERT OR IGNORE INTO cas_objects(sha256, size_bytes, mime, relative_path, stored_at_ms) VALUES($s,$b,$m,$p,$t)",
                        ("$s", a.Sha256), ("$b", p.FileBytes), ("$m", a.Mime), ("$p", p.RelativePath), ("$t", now));
                string? cas = p.State == "pending" ? null : a.Sha256;

                // The attachment row the message import created from the payload, matched by its URL.
                var existing = a.Url == null ? default
                    : c.Query(tx, "SELECT attachment_id, download_state, cas_sha256 FROM attachments WHERE message_id=$m AND source_url=$u LIMIT 1",
                        r => (Found: true, Id: r.GetString(0), State: r.GetString(1), Cas: r.GetStringOrNull(2)), ("$m", p.MessageId), ("$u", a.Url)).FirstOrDefault();
                if (existing.Found)
                {
                    if (existing.State == p.State && existing.Cas == cas) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                    c.Exec(tx, """
                        UPDATE attachments SET cas_sha256=$cas, download_state=$st, size_bytes=COALESCE(size_bytes,$b), mime=COALESCE(mime,$m)
                        WHERE attachment_id=$id
                        """, ("$cas", cas), ("$st", p.State), ("$b", p.FileBytes ?? a.Size), ("$m", a.Mime), ("$id", existing.Id));
                    if (p.State == "done") Change(c, tx, now, ChangeKinds.AttachmentStored, existing.Id, person, msg.Conv);
                    bc += new BatchCounts(0, 1, 0, 0);
                    continue;
                }

                // No payload row (v2 rows without a payload, or exports): add it after the message's other attachments.
                bool duplicate = c.Scalar<long>(tx, "SELECT COUNT(*) FROM attachments WHERE message_id=$m AND cas_sha256 IS $s AND source_url IS $u",
                    ("$m", p.MessageId), ("$s", cas), ("$u", a.Url)) > 0;
                if (duplicate) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                int idx = (int)c.Scalar<long>(tx, "SELECT COALESCE(MAX(idx)+1, 0) FROM attachments WHERE message_id=$m", ("$m", p.MessageId));
                string id = CorpusIds.Attachment(p.MessageId!, idx);
                string? filename = a.Url != null && Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : Path.GetFileName(a.LocalPath ?? "");
                c.Exec(tx, """
                    INSERT INTO attachments(attachment_id, message_id, idx, filename, mime, media_kind, size_bytes, source_url, cas_sha256, download_state, created_at_ms)
                    VALUES($id,$m,$i,$fn,$mi,$k,$s,$u,$cas,$st,$t)
                    """, ("$id", id), ("$m", p.MessageId), ("$i", idx), ("$fn", string.IsNullOrEmpty(filename) ? null : filename), ("$mi", a.Mime),
                    ("$k", MediaKinds.FromLegacy(a.Kind, a.Mime, filename)), ("$s", p.FileBytes ?? a.Size), ("$u", a.Url), ("$cas", cas), ("$st", p.State), ("$t", msg.Sent));
                Change(c, tx, now, ChangeKinds.AttachmentAdd, id, person, msg.Conv);
                if (p.State == "done") Change(c, tx, now, ChangeKinds.AttachmentStored, id, person, msg.Conv);
                bc += new BatchCounts(1, 0, 0, 0);
            }
            return bc;
        }

        /// <summary>Files in v2's media folder that no attachment row points at (reported, never imported or deleted).</summary>
        private void CountOrphanMedia(LegacyImportReport report, CancellationToken ct)
        {
            string dir = Path.Combine(stores.Paths.LegacyRoot, "Attachments");
            if (!Directory.Exists(dir)) return;
            HashSet<string> referenced;
            using (var c = stores.Corpus.OpenRead())
                referenced = c.Query(null, "SELECT relative_path FROM cas_objects WHERE relative_path LIKE 'legacy/%'", r => r.GetString(0))
                    .Select(p => MediaPaths.Resolve(stores.Paths, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            long files = 0, bytes = 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (referenced.Contains(Path.GetFullPath(path))) continue;
                files++;
                bytes += new FileInfo(path).Length;
            }
            report.OrphanMediaFiles = files;
            report.OrphanMediaBytes = bytes;
        }

        // ── reactions ────────────────────────────────────────────────────────────

        private sealed record LegacyReaction(string? Platform, string? MessageId, string? ChannelId, string? Reactor, string? Emoji, string? Action, long? Count, object? At);

        private async Task ImportReactionsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "reaction_events";
            await StreamAsync(legacy, table,
                $"{legacy.ColumnOrNull(table, "platform")}, platform_message_id, {legacy.ColumnOrNull(table, "channel_id")}, {legacy.ColumnOrNull(table, "reactor_platform_user_id")}, " +
                $"emoji, action, {legacy.ColumnOrNull(table, "count")}, occurred_at",
                r => new LegacyReaction(NullIfBlank(r.Text(1))?.ToLowerInvariant() ?? "discord", NullIfBlank(r.Text(2)), NullIfBlank(r.Text(3)), NullIfBlank(r.Text(4)),
                    NullIfBlank(r.Text(5)), NullIfBlank(r.Text(6))?.ToLowerInvariant(), r.Number(7), r.Raw(8)),
                rows =>
                {
                    // Not a forgotten person's reactions, and not anybody's reactions to what a forgotten person wrote.
                    var gone = ForgottenPlatformMessages(legacy, ctx, rows.Where(x => x.Row.MessageId != null).Select(x => (x.Row.Platform!, x.Row.MessageId!)));
                    var kept = rows.Where(x => !(x.Row.Reactor != null && ctx.Gone.Identities.Contains(CorpusIds.Identity(x.Row.Platform!, x.Row.Reactor)))
                                               && !(x.Row.MessageId != null && gone.Contains(CorpusIds.Message(x.Row.Platform!, x.Row.MessageId)))
                                               && !(x.Row.ChannelId != null && ctx.Gone.Conversations.Contains(CorpusIds.Conversation(x.Row.Platform!, x.Row.ChannelId)))).ToList();
                    return (Rows: kept, Forgotten: rows.Count - kept.Count);
                },
                (c, tx, batch, now) =>
                {
                    var rows = batch.Rows;
                    var bc = new BatchCounts(0, 0, batch.Forgotten, 0);
                    var touched = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var (rowid, e) in rows)
                    {
                        long? at = MsOrNull(e.At);
                        if (e.MessageId == null || e.Emoji == null || at == null || e.Action is not ("add" or "remove" or "snapshot")
                            || (e.Action != "snapshot" && e.Reactor == null))
                        {
                            if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "reaction event missing message, emoji, reactor, action or time", e, now)) bc += new BatchCounts(0, 0, 0, 1);
                            continue;
                        }
                        string platform = e.Platform!;
                        string message = CorpusIds.Message(platform, e.MessageId);
                        string? conv = e.ChannelId == null ? null : CorpusIds.Conversation(platform, e.ChannelId);
                        string emojiName = e.Emoji.Contains(':') ? e.Emoji[..e.Emoji.LastIndexOf(':')] : e.Emoji;
                        int changed;
                        if (e.Action == "snapshot")
                        {
                            changed = c.Exec(tx, """
                                INSERT INTO reaction_counts(message_id, emoji_key, count, observed_at_ms) VALUES($m,$e,$n,$t)
                                ON CONFLICT(message_id, emoji_key) DO UPDATE SET count=excluded.count, observed_at_ms=excluded.observed_at_ms
                                WHERE excluded.observed_at_ms >= reaction_counts.observed_at_ms
                                """, ("$m", message), ("$e", e.Emoji), ("$n", Math.Max(0, e.Count ?? 0)), ("$t", at.Value));
                        }
                        else
                        {
                            string identity = CorpusIds.Identity(platform, e.Reactor!);
                            if (e.Action == "add")
                            {
                                changed = c.Exec(tx, """
                                    INSERT INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind)
                                    VALUES($m,$e,$n,$i,0,$t,NULL,$q,'legacy')
                                    ON CONFLICT(message_id, emoji_key, identity_id) DO UPDATE SET added_at_ms=excluded.added_at_ms, removed_at_ms=NULL
                                    WHERE reactions.removed_at_ms IS NOT NULL AND reactions.removed_at_ms <= excluded.added_at_ms
                                    """, ("$m", message), ("$e", e.Emoji), ("$n", emojiName), ("$i", identity), ("$t", at.Value), ("$q", (int)TimeQuality.Exact));
                            }
                            else
                            {
                                changed = c.Exec(tx, """
                                    UPDATE reactions SET removed_at_ms=$t
                                    WHERE message_id=$m AND emoji_key=$e AND identity_id=$i AND removed_at_ms IS NULL AND (added_at_ms IS NULL OR added_at_ms <= $t)
                                    """, ("$m", message), ("$e", e.Emoji), ("$i", identity), ("$t", at.Value));
                                if (changed == 0)
                                    changed = c.Exec(tx, """
                                        INSERT OR IGNORE INTO reactions(message_id, emoji_key, emoji_name, identity_id, is_burst, added_at_ms, removed_at_ms, time_quality, source_kind)
                                        VALUES($m,$e,$n,$i,0,NULL,$t,$q,'legacy')
                                        """, ("$m", message), ("$e", e.Emoji), ("$n", emojiName), ("$i", identity), ("$t", at.Value), ("$q", (int)TimeQuality.Exact));
                            }
                        }
                        if (changed > 0) { touched[message] = conv; bc += new BatchCounts(1, 0, 0, 0); }
                        else bc += new BatchCounts(0, 0, 1, 0);
                    }
                    foreach (var (message, conv) in touched) Change(c, tx, now, ChangeKinds.ReactionChange, message, null, conv);
                    return bc;
                }, report, progress, ct).ConfigureAwait(false);
        }

        // ── presence, typing, voice ──────────────────────────────────────────────

        private sealed record LegacyPresence(string Platform, string? UserId, string? Status, string? CustomStatus, string? Activities, object? At);

        private async Task ImportPresenceAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "presence_events";
            await StreamAsync(legacy, table,
                $"{legacy.ColumnOrNull(table, "platform")}, platform_user_id, {legacy.ColumnOrNull(table, "status")}, {legacy.ColumnOrNull(table, "custom_status")}, " +
                $"{legacy.ColumnOrNull(table, "activities_json")}, captured_at",
                r => new LegacyPresence(NullIfBlank(r.Text(1))?.ToLowerInvariant() ?? "discord", NullIfBlank(r.Text(2)), NullIfBlank(r.Text(3)), NullIfBlank(r.Text(4)), NullIfBlank(r.Text(5)), r.Raw(6)),
                rows => rows.ToList(),
                (c, tx, rows, now) =>
                {
                    var bc = new BatchCounts();
                    var touched = new HashSet<string>(StringComparer.Ordinal);
                    using var insert = new PreparedStatement(c, tx, """
                        INSERT OR IGNORE INTO presence_events(identity_id, guild_id, at_ms, status, activities_json, custom_status, source_id, dedupe_key)
                        VALUES($i,NULL,$t,$s,$a,$cs,$src,$d)
                        """, "$i", "$t", "$s", "$a", "$cs", "$src", "$d");
                    foreach (var (rowid, p) in rows)
                    {
                        if (p.UserId != null && ctx.Gone.Identities.Contains(CorpusIds.Identity(p.Platform, p.UserId))) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                        long? at = MsOrNull(p.At);
                        if (p.UserId == null || at == null)
                        {
                            if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "presence event without user or time", p, now)) bc += new BatchCounts(0, 0, 0, 1);
                            continue;
                        }
                        string identity = CorpusIds.Identity(p.Platform, p.UserId);
                        if (insert.Run(identity, at.Value, p.Status, p.Activities, p.CustomStatus, CorpusIds.LegacySourceId, $"legacy:presence:{rowid}") == 1)
                        {
                            touched.Add(identity);
                            bc += new BatchCounts(1, 0, 0, 0);
                        }
                        else bc += new BatchCounts(0, 0, 1, 0);
                    }
                    foreach (var identity in touched) Change(c, tx, now, ChangeKinds.PresenceEvent, identity, PersonOfIdentity(ctx, identity), null);
                    return bc;
                }, report, progress, ct, batchMultiplier: 4).ConfigureAwait(false);
        }

        private sealed record LegacyTyping(string Platform, string? UserId, string? ChannelId, object? At);

        private async Task ImportTypingAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "typing_events";
            await StreamAsync(legacy, table,
                $"{legacy.ColumnOrNull(table, "platform")}, platform_user_id, {legacy.ColumnOrNull(table, "channel_id")}, started_at",
                r => new LegacyTyping(NullIfBlank(r.Text(1))?.ToLowerInvariant() ?? "discord", NullIfBlank(r.Text(2)), NullIfBlank(r.Text(3)), r.Raw(4)),
                rows => rows.ToList(),
                (c, tx, rows, now) =>
                {
                    var bc = new BatchCounts();
                    using var insert = new PreparedStatement(c, tx,
                        "INSERT OR IGNORE INTO typing_events(identity_id, conversation_id, at_ms, source_id) VALUES($i,$c,$t,$s)", "$i", "$c", "$t", "$s");
                    foreach (var (rowid, t) in rows)
                    {
                        if (ctx.Gone.Blocks(t.UserId == null ? null : CorpusIds.Identity(t.Platform, t.UserId), t.ChannelId == null ? null : CorpusIds.Conversation(t.Platform, t.ChannelId)))
                        {
                            bc += new BatchCounts(0, 0, 1, 0);
                            continue;
                        }
                        long? at = MsOrNull(t.At);
                        if (t.UserId == null || t.ChannelId == null || at == null)
                        {
                            if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "typing event without user, channel or time", t, now)) bc += new BatchCounts(0, 0, 0, 1);
                            continue;
                        }
                        bc += insert.Run(CorpusIds.Identity(t.Platform, t.UserId), CorpusIds.Conversation(t.Platform, t.ChannelId), at.Value, CorpusIds.LegacySourceId) == 1
                            ? new BatchCounts(1, 0, 0, 0) : new BatchCounts(0, 0, 1, 0);
                    }
                    return bc;
                }, report, progress, ct, batchMultiplier: 4).ConfigureAwait(false);
        }

        private sealed record LegacyVoice(string Platform, string? UserId, string? GuildId, string? ChannelId, object? JoinedAt, object? LeftAt);

        private static string VoiceSessionId(long rowid) => "legacy:voice:" + rowid.ToString(CultureInfo.InvariantCulture);

        private async Task ImportVoiceAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            const string table = "voice_sessions";
            string columns = $"{legacy.ColumnOrNull(table, "platform")}, platform_user_id, {legacy.ColumnOrNull(table, "guild_id")}, {legacy.ColumnOrNull(table, "channel_id")}, joined_at, left_at";
            LegacyVoice Read(SqliteDataReader r) =>
                new(NullIfBlank(r.Text(1))?.ToLowerInvariant() ?? "discord", NullIfBlank(r.Text(2)), NullIfBlank(r.Text(3)), NullIfBlank(r.Text(4)), r.Raw(5), r.Raw(6));

            BatchCounts Write(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<(long Rowid, LegacyVoice Row)> rows, long now)
            {
                var bc = new BatchCounts();
                var touched = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (rowid, v) in rows)
                {
                    if (v.UserId != null && ctx.Gone.Identities.Contains(CorpusIds.Identity(v.Platform, v.UserId))) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                    long? joined = MsOrNull(v.JoinedAt);
                    long? left = MsOrNull(v.LeftAt);
                    if (v.UserId == null || joined == null)
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "voice session without user or join time", v, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    if (left != null && left < joined) left = null; // a clock glitch in v2: keep the session open rather than negative
                    string identity = CorpusIds.Identity(v.Platform, v.UserId);
                    int n = c.Exec(tx, """
                        INSERT INTO voice_sessions(session_id, identity_id, guild_id, channel_id, joined_at_ms, left_at_ms, source_id) VALUES($id,$i,$g,$c,$j,$l,$s)
                        ON CONFLICT(session_id) DO UPDATE SET left_at_ms=excluded.left_at_ms
                        WHERE voice_sessions.left_at_ms IS NULL AND excluded.left_at_ms IS NOT NULL
                        """, ("$id", VoiceSessionId(rowid)), ("$i", identity), ("$g", v.GuildId == null ? null : CorpusIds.Guild(v.Platform, v.GuildId)),
                        ("$c", v.ChannelId == null ? null : CorpusIds.Conversation(v.Platform, v.ChannelId)), ("$j", joined.Value), ("$l", left), ("$s", CorpusIds.LegacySourceId));
                    if (n > 0) { touched.Add(identity); bc += new BatchCounts(1, 0, 0, 0); }
                    else bc += new BatchCounts(0, 0, 1, 0);
                }
                foreach (var identity in touched) Change(c, tx, now, ChangeKinds.VoiceEvent, identity, PersonOfIdentity(ctx, identity), null);
                return bc;
            }

            await StreamAsync(legacy, table, columns, Read, rows => rows, Write, report, progress, ct).ConfigureAwait(false);

            // v2 closes a session by updating its row in place, which a rowid cursor never revisits: re-read the
            // sessions v3 still holds open.
            if (!legacy.HasTable(table)) return;
            List<long> open;
            using (var c = stores.Corpus.OpenRead())
                open = c.Query(null, "SELECT session_id FROM voice_sessions WHERE left_at_ms IS NULL AND session_id LIKE 'legacy:voice:%'", r => r.GetString(0))
                    .Select(id => long.Parse(id["legacy:voice:".Length..], CultureInfo.InvariantCulture)).ToList();
            if (open.Count == 0) return;
            var refreshed = new List<(long, LegacyVoice)>();
            foreach (var chunk in open.Chunk(500))
                refreshed.AddRange(legacy.Connection.Query(null, $"SELECT rowid, {columns} FROM {table} WHERE rowid IN ({string.Join(",", chunk)}) AND left_at IS NOT NULL",
                    r => (r.GetInt64(0), Read(r))));
            if (refreshed.Count == 0) return;
            long nowMs = clock.NowMs;
            var counts = await stores.Corpus.WriteAsync((c, tx) => Write(c, tx, refreshed, nowMs), ct).ConfigureAwait(false);
            report.Table(table).Updated += counts.Imported;
        }

        private static string? PersonOfIdentity(Context ctx, string identityId) =>
            ctx.PersonByIdentity.TryGetValue(identityId, out var person) ? ctx.CanonicalOf(person) : null;

        // ── ingest cursors ───────────────────────────────────────────────────────

        private async Task ImportIngestCursorsAsync(LegacyDatabase legacy, Context ctx, LegacyImportReport report, CancellationToken ct)
        {
            const string table = "ingest_cursors";
            var rows = ReadAll(legacy, table,
                $"source_id, conversation_id, {legacy.ColumnOrNull(table, "earliest_message_id")}, {legacy.ColumnOrNull(table, "latest_message_id")}, " +
                $"{legacy.ColumnOrNull(table, "fully_backfilled")}, {legacy.ColumnOrNull(table, "last_synced_at")}",
                r => (Source: r.Text(1), Conv: r.Text(2), Earliest: NullIfBlank(r.Text(3)), Latest: NullIfBlank(r.Text(4)), Full: r.Number(5) ?? 0, Synced: r.Raw(6)));
            if (rows.Count == 0) return;
            long now = clock.NowMs;
            var counts = await stores.Corpus.WriteAsync((c, tx) =>
            {
                var bc = new BatchCounts();
                foreach (var (rowid, x) in rows)
                {
                    if (x.Conv != null && ctx.ForgottenConversations.Contains(x.Conv)) { bc += new BatchCounts(0, 0, 1, 0); continue; }
                    if (x.Source == null || x.Conv == null || !ctx.Conversations.TryGetValue(x.Conv, out var conv))
                    {
                        if (Quarantine(c, tx, table, rowid.ToString(CultureInfo.InvariantCulture), "cursor for an unknown source or conversation", x, now)) bc += new BatchCounts(0, 0, 0, 1);
                        continue;
                    }
                    int n = c.Exec(tx, """
                        INSERT INTO ingest_cursors(source_id, conversation_id, oldest_platform_id, newest_platform_id, backward_complete, state, updated_at_ms)
                        VALUES($s,$c,$o,$n,$b,'legacy',$t)
                        ON CONFLICT(source_id, conversation_id) DO UPDATE SET oldest_platform_id=excluded.oldest_platform_id, newest_platform_id=excluded.newest_platform_id,
                            backward_complete=excluded.backward_complete, updated_at_ms=excluded.updated_at_ms
                        WHERE ingest_cursors.state='legacy' AND (ingest_cursors.oldest_platform_id IS NOT excluded.oldest_platform_id
                            OR ingest_cursors.newest_platform_id IS NOT excluded.newest_platform_id OR ingest_cursors.backward_complete <> excluded.backward_complete)
                        """, ("$s", x.Source), ("$c", conv.ConversationId), ("$o", x.Earliest == null ? null : CorpusIds.Native(x.Earliest)),
                        ("$n", x.Latest == null ? null : CorpusIds.Native(x.Latest)), ("$b", x.Full != 0 ? 1 : 0), ("$t", Ms(x.Synced, now)));
                    bc += n > 0 ? new BatchCounts(1, 0, 0, 0) : new BatchCounts(0, 0, 1, 0);
                }
                return bc;
            }, ct).ConfigureAwait(false);
            report.Table(table).Add(counts, rows.Count);
        }
    }
}
