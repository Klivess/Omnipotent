using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveAPI.Telemetry;
using Omnipotent.Services.KliveMail.Models;

namespace Omnipotent.Services.KliveMail.Persistence
{
    // All KliveMail data access. Writes go through KliveMailDb's single write lock; reads use
    // their own short-lived connection (WAL allows concurrent readers).
    //
    // Read cost rule: a list never touches a message body. A row's flags (is_read, is_deleted,
    // has_attachments) sit after body_html in the record, so reading them from the table walks the
    // row's overflow chain; every list, count and thread query therefore names the index that
    // carries those flags (INDEXED BY: this database has no statistics, and left to itself SQLite
    // picked the one-bit is_deleted index and sorted every row), and the preview comes from
    // message_digest. Only GetMessageAsync reads a body.
    public sealed class KliveMailRepository
    {
        public const string MailDomain = "klive.dev";
        /// <summary>Response-cache dataset for everything KliveMail stores.</summary>
        public const string CacheKey = "klivemail";
        public const int MaxBulkIds = 500;

        private readonly KliveMailDb db;

        public KliveMailRepository(KliveMailDb db) { this.db = db; }

        // ─────────────────────────── Ingestion ───────────────────────────

        public async Task InsertMessageAsync(StoredMessage m, CancellationToken ct = default)
        {
            var digest = KliveMailDigest.Build(m.Subject, m.BodyText, m.BodyHtml);
            await db.WithWriteLockAsync(async conn =>
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO messages
                        (id,to_address,from_address,from_name,subject,date_utc,received_utc,message_id,in_reply_to,references_raw,thread_id,body_text,body_html,has_attachments,raw_size,is_read,is_deleted)
                        VALUES ($id,$to,$from,$fromName,$subject,$date,$received,$messageId,$inReplyTo,$references,$thread,$bodyText,$bodyHtml,$hasAtt,$rawSize,$read,0)";
                    cmd.Parameters.AddWithValue("$id", m.Id);
                    cmd.Parameters.AddWithValue("$to", m.ToAddress ?? "");
                    cmd.Parameters.AddWithValue("$from", m.FromAddress ?? "");
                    cmd.Parameters.AddWithValue("$fromName", (object?)m.FromName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$subject", (object?)m.Subject ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$date", (object?)m.DateUtc?.ToString("o") ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$received", m.ReceivedUtc.ToString("o"));
                    cmd.Parameters.AddWithValue("$messageId", (object?)m.MessageId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$inReplyTo", (object?)m.InReplyTo ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$references", (object?)m.ReferencesRaw ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$thread", string.IsNullOrEmpty(m.ThreadId) ? m.Id : m.ThreadId);
                    cmd.Parameters.AddWithValue("$bodyText", (object?)m.BodyText ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$bodyHtml", (object?)m.BodyHtml ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$hasAtt", m.HasAttachments ? 1 : 0);
                    cmd.Parameters.AddWithValue("$rawSize", m.RawSize);
                    // A sent copy is filed read; inbound mail always arrives unread.
                    cmd.Parameters.AddWithValue("$read", m.IsRead ? 1 : 0);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                foreach (var a in m.Attachments)
                {
                    await using var acmd = conn.CreateCommand();
                    acmd.Transaction = tx;
                    acmd.CommandText = @"INSERT INTO attachments
                        (id,message_id,file_name,content_type,size_bytes,storage_path,is_inline,content_id)
                        VALUES ($id,$mid,$fn,$ct,$size,$path,$inline,$cid)";
                    acmd.Parameters.AddWithValue("$id", a.Id);
                    acmd.Parameters.AddWithValue("$mid", m.Id);
                    acmd.Parameters.AddWithValue("$fn", a.FileName ?? "attachment");
                    acmd.Parameters.AddWithValue("$ct", (object?)a.ContentType ?? DBNull.Value);
                    acmd.Parameters.AddWithValue("$size", a.SizeBytes);
                    acmd.Parameters.AddWithValue("$path", a.StoragePath ?? "");
                    acmd.Parameters.AddWithValue("$inline", a.IsInline ? 1 : 0);
                    acmd.Parameters.AddWithValue("$cid", (object?)a.ContentId ?? DBNull.Value);
                    await acmd.ExecuteNonQueryAsync(ct);
                }

                await using (var fcmd = conn.CreateCommand())
                {
                    fcmd.Transaction = tx;
                    fcmd.CommandText = @"INSERT INTO messages_fts(message_id,from_address,from_name,subject,body_text,body_html,attachments)
                        VALUES ($mid,$from,$fromName,$subject,$bodyText,$bodyHtml,$attach)";
                    fcmd.Parameters.AddWithValue("$mid", m.Id);
                    fcmd.Parameters.AddWithValue("$from", m.FromAddress ?? "");
                    fcmd.Parameters.AddWithValue("$fromName", (object?)m.FromName ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$subject", (object?)m.Subject ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$bodyText", (object?)m.BodyText ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$bodyHtml", (object?)HtmlToText(m.BodyHtml) ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$attach", (object?)string.Join(" ", m.Attachments.Select(a => AttachmentSearchText(a))) ?? DBNull.Value);
                    await fcmd.ExecuteNonQueryAsync(ct);
                }

                await using (var dcmd = conn.CreateCommand())
                {
                    dcmd.Transaction = tx;
                    AddDigestInsert(dcmd, m.Id, digest);
                    await dcmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return true;
            }, ct);
            CacheDeps.Bump(CacheKey);
        }

        private static void AddDigestInsert(SqliteCommand cmd, string messageId, KliveMailDigest.Digest digest)
        {
            cmd.CommandText = "INSERT OR REPLACE INTO message_digest(message_id,snippet,code,version) VALUES ($mid,$snippet,$code,$v)";
            cmd.Parameters.AddWithValue("$mid", messageId);
            cmd.Parameters.AddWithValue("$snippet", digest.Snippet);
            cmd.Parameters.AddWithValue("$code", (object?)digest.Code ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", KliveMailDigest.Version);
        }

        // ─────────────────────────── Lists / detail ───────────────────────────

        /// <summary>Opening the connection is its own span: the live list spent 5-7 s in the handler
        /// with nothing in its trace to say whether that was the open or the query.</summary>
        private async Task<SqliteConnection> OpenTracedAsync(CancellationToken ct)
        {
            using (RequestTrace.AmbientSpan("mail-open")) return await db.OpenAsync(ct);
        }

        // Columns every list returns, in MapSummary's order. The flags and the id come from the
        // index the query names, the rest from the row's first page; the preview from the digest.
        private const string SummaryColumns =
            "m.id,m.to_address,m.from_address,m.from_name,m.subject,m.received_utc,m.date_utc,m.thread_id,m.has_attachments,m.is_read,d.snippet,d.code";

        /// <summary>The SQL a list query runs; public so the cost tests can price exactly what ships.</summary>
        internal static string ListSql(MessageQuery q)
        {
            bool box = !string.IsNullOrWhiteSpace(q.Mailbox);
            var where = new List<string>();
            if (box) where.Add("m.to_address = $mailbox");
            where.Add("m.is_deleted = $deleted");
            if (q.UnreadOnly) where.Add("m.is_read = 0");
            if (q.HasAttachmentOnly) where.Add("m.has_attachments = 1");
            if (q.CodesOnly) where.Add("m.id IN (SELECT message_id FROM message_digest WHERE code IS NOT NULL)");
            return $@"SELECT {SummaryColumns}
                FROM messages m INDEXED BY {(box ? "idx_messages_box" : "idx_messages_feed")}
                LEFT JOIN message_digest d ON d.message_id = m.id
                WHERE {string.Join(" AND ", where)}
                ORDER BY m.received_utc DESC LIMIT $limit OFFSET $offset";
        }

        public Task<List<MessageSummary>> ListMessagesAsync(string? mailbox, bool unreadOnly, bool hasAttachmentOnly, bool trash, int page, int pageSize, CancellationToken ct = default)
            => ListMessagesAsync(new MessageQuery
            {
                Mailbox = mailbox, UnreadOnly = unreadOnly, HasAttachmentOnly = hasAttachmentOnly, Trash = trash, Page = page, PageSize = pageSize
            }, ct);

        public async Task<List<MessageSummary>> ListMessagesAsync(MessageQuery q, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            int page = Math.Max(1, q.Page);
            int pageSize = Math.Clamp(q.PageSize, 1, 200);

            var list = new List<MessageSummary>();
            await using var conn = await OpenTracedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ListSql(q);
            if (!string.IsNullOrWhiteSpace(q.Mailbox)) cmd.Parameters.AddWithValue("$mailbox", NormalizeAddress(q.Mailbox));
            cmd.Parameters.AddWithValue("$deleted", q.Trash ? 1 : 0);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using (RequestTrace.AmbientSpan("mail-query"))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            }
            return list;
        }

        internal const string ThreadSql = @"SELECT " + SummaryColumns + @"
                FROM messages m INDEXED BY idx_messages_conv
                LEFT JOIN message_digest d ON d.message_id = m.id
                WHERE m.thread_id = $thread AND m.is_deleted = 0 ORDER BY m.received_utc ASC";

        public async Task<List<MessageSummary>> GetThreadAsync(string threadId, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            var list = new List<MessageSummary>();
            await using var conn = await OpenTracedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ThreadSql;
            cmd.Parameters.AddWithValue("$thread", threadId);
            using (RequestTrace.AmbientSpan("mail-query"))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            }
            return list;
        }

        public async Task<StoredMessage?> GetMessageAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await OpenTracedAsync(ct);
            using var span = RequestTrace.AmbientSpan("mail-message");
            StoredMessage? msg = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id,to_address,from_address,from_name,subject,date_utc,received_utc,message_id,in_reply_to,references_raw,thread_id,body_text,body_html,has_attachments,raw_size,is_read,is_deleted
                    FROM messages WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) msg = MapMessage(reader);
            }
            if (msg == null) return null;

            await using (var acmd = conn.CreateCommand())
            {
                acmd.CommandText = "SELECT id,message_id,file_name,content_type,size_bytes,storage_path,is_inline,content_id FROM attachments WHERE message_id = $id";
                acmd.Parameters.AddWithValue("$id", id);
                await using var ar = await acmd.ExecuteReaderAsync(ct);
                while (await ar.ReadAsync(ct)) msg.Attachments.Add(MapAttachment(ar));
            }
            return msg;
        }

        /// <summary>The stored code for one message, or null (also null before the digest backfill reaches it).</summary>
        public async Task<string?> GetCodeAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT code FROM message_digest WHERE message_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteScalarAsync(ct) as string;
        }

        public async Task<StoredAttachment?> GetAttachmentAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,message_id,file_name,content_type,size_bytes,storage_path,is_inline,content_id FROM attachments WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapAttachment(reader);
            return null;
        }

        /// <summary>
        /// Search runs the full-text match once, into a set of ids, then walks the date index and keeps
        /// the rows in that set. Driving from the match instead (the old query) fetched each hit from
        /// the table to test is_deleted, which walks its body: 28.9 s for one live search.
        /// </summary>
        internal static string SearchSql(bool mailbox, bool from) => $@"SELECT {SummaryColumns}
                FROM messages m INDEXED BY {(mailbox ? "idx_messages_box" : "idx_messages_feed")}
                LEFT JOIN message_digest d ON d.message_id = m.id
                WHERE {(mailbox ? "m.to_address = $mailbox AND " : "")}m.is_deleted = 0
                  AND m.id IN (SELECT message_id FROM messages_fts WHERE messages_fts MATCH $q)
                  {(from ? "AND m.from_address LIKE $fromEsc" : "")}
                ORDER BY m.received_utc DESC LIMIT $limit OFFSET $offset";

        public async Task<List<MessageSummary>> SearchAsync(string query, int page = 1, int pageSize = 50, string? mailbox = null, string? from = null, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            string ftsQuery = BuildFtsQuery(query);
            if (string.IsNullOrEmpty(ftsQuery)) return new List<MessageSummary>();

            bool hasMailbox = !string.IsNullOrWhiteSpace(mailbox);
            bool hasFrom = !string.IsNullOrWhiteSpace(from);
            var list = new List<MessageSummary>();
            await using var conn = await OpenTracedAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SearchSql(hasMailbox, hasFrom);
            if (hasMailbox) cmd.Parameters.AddWithValue("$mailbox", NormalizeAddress(mailbox!));
            if (hasFrom) cmd.Parameters.AddWithValue("$fromEsc", "%" + from!.Trim() + "%");
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using (RequestTrace.AmbientSpan("mail-query"))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            }
            return list;
        }

        // ─────────────────────────── Mutations ───────────────────────────

        public async Task<bool> SetReadAsync(string id, bool read, CancellationToken ct = default)
            => await SetReadManyAsync(new[] { id }, read, ct) > 0 || await ExistsAsync(id, ct);

        public async Task<bool> SoftDeleteAsync(string id, CancellationToken ct = default)
            => await SetDeletedManyAsync(new[] { id }, true, ct) > 0 || await ExistsAsync(id, ct);

        /// <summary>Sets the read flag on up to <see cref="MaxBulkIds"/> messages; returns how many changed.</summary>
        public Task<int> SetReadManyAsync(IEnumerable<string> ids, bool read, CancellationToken ct = default)
            => UpdateFlagAsync("is_read", read, ids, ct);

        /// <summary>Moves messages to the trash (deleted) or back out of it; returns how many changed.</summary>
        public Task<int> SetDeletedManyAsync(IEnumerable<string> ids, bool deleted, CancellationToken ct = default)
            => UpdateFlagAsync("is_deleted", deleted, ids, ct);

        private async Task<int> UpdateFlagAsync(string column, bool value, IEnumerable<string> ids, CancellationToken ct)
        {
            var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal).Take(MaxBulkIds).ToList();
            if (list.Count == 0) return 0;
            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                var names = new List<string>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    names.Add("$i" + i);
                    cmd.Parameters.AddWithValue("$i" + i, list[i]);
                }
                cmd.CommandText = $"UPDATE messages SET {column} = $v WHERE id IN ({string.Join(",", names)}) AND {column} <> $v";
                cmd.Parameters.AddWithValue("$v", value ? 1 : 0);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            if (rows > 0) CacheDeps.Bump(CacheKey);
            return rows;
        }

        /// <summary>Marks every unread message read, in one mailbox or (null) everywhere; returns how many changed.</summary>
        public async Task<int> MarkAllReadAsync(string? mailbox, CancellationToken ct = default)
        {
            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                bool box = !string.IsNullOrWhiteSpace(mailbox);
                cmd.CommandText = box
                    ? "UPDATE messages SET is_read = 1 WHERE id IN (SELECT id FROM messages INDEXED BY idx_messages_box WHERE to_address = $mb AND is_deleted = 0 AND is_read = 0)"
                    : "UPDATE messages SET is_read = 1 WHERE id IN (SELECT id FROM messages INDEXED BY idx_messages_feed WHERE is_deleted = 0 AND is_read = 0)";
                if (box) cmd.Parameters.AddWithValue("$mb", NormalizeAddress(mailbox!));
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            if (rows > 0) CacheDeps.Bump(CacheKey);
            return rows;
        }

        private async Task<bool> ExistsAsync(string id, CancellationToken ct)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM messages WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteScalarAsync(ct) != null;
        }

        // ─────────────────────────── Mailboxes ───────────────────────────

        public async Task<List<MailboxInfo>> ListMailboxesAsync(CancellationToken ct = default)
            => (await GetSummaryAsync(DateTime.UtcNow, ct)).Mailboxes;

        public async Task<bool> CreateMailboxAsync(string address, string? displayName, CancellationToken ct = default)
        {
            address = NormalizeAddress(address);
            if (string.IsNullOrWhiteSpace(address) || !address.EndsWith("@" + MailDomain, StringComparison.OrdinalIgnoreCase))
                return false;

            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO mailboxes(address,display_name,created_utc) VALUES($a,$d,$c)";
                cmd.Parameters.AddWithValue("$a", address);
                cmd.Parameters.AddWithValue("$d", (object?)displayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            if (rows > 0) CacheDeps.Bump(CacheKey);
            return rows > 0;
        }

        public async Task<bool> DeleteMailboxAsync(string address, CancellationToken ct = default)
        {
            address = NormalizeAddress(address);
            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM mailboxes WHERE address = $a";
                cmd.Parameters.AddWithValue("$a", address);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            if (rows > 0) CacheDeps.Bump(CacheKey);
            return rows > 0;
        }

        // ─────────────────────────── Stats ───────────────────────────

        /// <summary>Per-address counts in one pass over the mailbox index (it carries every column read).
        /// Replaced two correlated COUNTs per pinned mailbox, each walking every row: 81 × 2 × 363 row
        /// reads every 5 s on the live store.</summary>
        internal const string AddressCountsSql = @"SELECT to_address, COUNT(*) AS total,
                    SUM(CASE WHEN is_read = 0 THEN 1 ELSE 0 END) AS unread, MAX(received_utc) AS last_received
                FROM messages INDEXED BY idx_messages_box WHERE is_deleted = 0 GROUP BY to_address";

        internal const string TrashCountSql = "SELECT COUNT(*) FROM messages INDEXED BY idx_messages_feed WHERE is_deleted = 1";

        /// <summary>Arrivals since a cutoff (deleted mail still arrived), by UTC day, from the date index.</summary>
        internal const string ArrivalsSql = @"SELECT substr(received_utc, 1, 10) AS day, COUNT(*) AS n,
                    SUM(CASE WHEN received_utc >= $day THEN 1 ELSE 0 END) AS last_day
                FROM messages INDEXED BY idx_messages_feed
                WHERE is_deleted IN (0, 1) AND received_utc >= $since GROUP BY day";

        public const int DailyDays = 30;

        public async Task<MailSummary> GetSummaryAsync(DateTime nowUtc, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            var summary = new MailSummary();
            var byAddress = new Dictionary<string, MailboxInfo>(StringComparer.OrdinalIgnoreCase);
            await using var conn = await db.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = AddressCountsSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var info = new MailboxInfo
                    {
                        Address = reader.GetString(0),
                        Total = reader.GetInt32(1),
                        Unread = reader.GetInt32(2),
                        LastReceivedUtc = reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3)),
                    };
                    byAddress[info.Address] = info;
                    summary.Total += info.Total;
                    summary.Unread += info.Unread;
                    if (info.LastReceivedUtc is { } last && (summary.LastReceivedUtc == null || last > summary.LastReceivedUtc))
                        summary.LastReceivedUtc = last;
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = TrashCountSql;
                summary.Trash = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT address, display_name FROM mailboxes ORDER BY address ASC";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string address = reader.GetString(0);
                    byAddress.Remove(address, out var counts);
                    summary.Mailboxes.Add(new MailboxInfo
                    {
                        Address = address,
                        DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Total = counts?.Total ?? 0,
                        Unread = counts?.Unread ?? 0,
                        LastReceivedUtc = counts?.LastReceivedUtc,
                        Pinned = true,
                    });
                }
            }
            summary.Others = byAddress.Values
                .OrderByDescending(m => m.LastReceivedUtc ?? DateTime.MinValue)
                .ThenBy(m => m.Address, StringComparer.Ordinal)
                .ToList();

            DateTime today = nowUtc.Date;
            DateTime since = today.AddDays(-(DailyDays - 1));
            var perDay = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ArrivalsSql;
                cmd.Parameters.AddWithValue("$since", since.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$day", nowUtc.AddDays(-1).ToString("o"));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    perDay[reader.GetString(0)] = reader.GetInt32(1);
                    summary.Received24h += reader.GetInt32(2);
                }
            }
            for (var day = since; day <= today; day = day.AddDays(1))
            {
                string key = day.ToString("yyyy-MM-dd");
                int n = perDay.TryGetValue(key, out int count) ? count : 0;
                summary.Daily.Add(new DayCount { D = key, N = n });
                if (day > today.AddDays(-7)) summary.Received7d += n;
            }
            return summary;
        }

        public async Task<(int Total, int Unread, int Trash)> GetStatsAsync(CancellationToken ct = default)
        {
            var summary = await GetSummaryAsync(DateTime.UtcNow, ct);
            return (summary.Total, summary.Unread, summary.Trash);
        }

        // ─────────────────────────── Digest backfill ───────────────────────────

        /// <summary>
        /// Builds the digest for every message that has none, or one from older rules, in batches so
        /// arriving mail never waits long for the write lock. Safe to run at every start: when every
        /// digest is current it is one indexed query. Returns how many it built.
        /// </summary>
        public async Task<int> BackfillDigestsAsync(CancellationToken ct = default, int batchSize = 40)
        {
            int built = 0;
            while (!ct.IsCancellationRequested)
            {
                var batch = new List<(string Id, string? Subject, string? Text, string? Html)>();
                await using (var conn = await db.OpenAsync(ct))
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT m.id, m.subject, m.body_text, m.body_html FROM messages m
                        LEFT JOIN message_digest d ON d.message_id = m.id
                        WHERE d.message_id IS NULL OR d.version < $v LIMIT $n";
                    cmd.Parameters.AddWithValue("$v", KliveMailDigest.Version);
                    cmd.Parameters.AddWithValue("$n", batchSize);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        batch.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
                if (batch.Count == 0) break;

                var digests = batch.Select(row => (row.Id, Digest: KliveMailDigest.Build(row.Subject, row.Text, row.Html))).ToList();
                await db.WithWriteLockAsync(async conn =>
                {
                    await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                    foreach (var (id, digest) in digests)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        AddDigestInsert(cmd, id, digest);
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }, ct);
                built += digests.Count;
                if (batch.Count < batchSize) break;
            }
            if (built > 0) CacheDeps.Bump(CacheKey);
            return built;
        }

        // ─────────────────────────── Helpers ───────────────────────────

        public static string NormalizeAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            address = address.Trim().ToLowerInvariant();
            if (!address.Contains('@')) address = address + "@" + MailDomain;
            return address;
        }

        // Decode an HTML body into searchable plain text: drop scripts/styles, replace block tags
        // with spaces, strip the rest of the markup, collapse whitespace, and HTML-decode entities.
        internal static string HtmlToText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var s = html;
            s = Regex.Replace(s, "(?is)<style[^>]*>.*?</style>", " ");
            s = Regex.Replace(s, "(?is)<script[^>]*>.*?</script>", " ");
            s = Regex.Replace(s, "(?is)<head(?:\\s[^>]*)?>.*?</head>", " ");
            s = Regex.Replace(s, "(?is)<br\\s*/?>", " ");
            s = Regex.Replace(s, "(?is)</(p|div|li|tr|h[1-6]|table|section|article)>", " ");
            s = Regex.Replace(s, "(?s)<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, "\\s+", " ");
            return s.Trim();
        }

        // Build the searchable text for an attachment: its file name plus, for text-like content
        // types, a bounded slice of the stored file's contents.
        internal static string AttachmentSearchText(StoredAttachment a)
        {
            string text = a.FileName ?? "";
            try
            {
                var ct = a.ContentType ?? "";
                bool textLike = ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || ct is "application/json" or "application/xml" or "application/csv" or "image/svg+xml";
                if (textLike && !string.IsNullOrWhiteSpace(a.StoragePath) && File.Exists(a.StoragePath))
                {
                    var raw = HtmlToText(File.ReadAllText(a.StoragePath));
                    if (raw.Length > 4000) raw = raw[..4000];
                    text = text + " " + raw;
                }
            }
            catch { /* unreadable/binary attachment: index the file name only */ }
            return text;
        }

        // Read one message's attachments and build the combined search text (file names + text content).
        private string AttachmentSearchTextForMessage(SqliteConnection conn, string messageId)
        {
            var parts = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT file_name,content_type,storage_path FROM attachments WHERE message_id = $mid";
                cmd.Parameters.AddWithValue("$mid", messageId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var a = new StoredAttachment
                    {
                        FileName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ContentType = reader.IsDBNull(1) ? null : reader.GetString(1),
                        StoragePath = reader.IsDBNull(2) ? "" : reader.GetString(2)
                    };
                    parts.Add(AttachmentSearchText(a));
                }
            }
            return string.Join(" ", parts.Where(pt => !string.IsNullOrWhiteSpace(pt)));
        }

        // One-off backfill: after the v2 FTS rebuild, recompute body_html / attachments for every
        // non-deleted message so pre-feature messages become searchable with the same logic the
        // insert path uses. Runs inside the write lock, in a single transaction.
        public async Task BackfillFtsSearchColumnsAsync(CancellationToken ct = default)
        {
            await db.WithWriteLockAsync(async conn =>
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                try
                {
                    await using (var clear = conn.CreateCommand())
                    {
                        clear.Transaction = tx;
                        clear.CommandText = "DELETE FROM messages_fts;";
                        await clear.ExecuteNonQueryAsync(ct);
                    }

                    await using (var seld = conn.CreateCommand())
                    {
                        seld.Transaction = tx;
                        seld.CommandText = "SELECT id,coalesce(from_address,''),coalesce(from_name,''),coalesce(subject,''),coalesce(body_text,''),coalesce(body_html,'') FROM messages WHERE is_deleted = 0";
                        await using var r = await seld.ExecuteReaderAsync(ct);
                        await using var ins = conn.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText = @"INSERT INTO messages_fts(message_id,from_address,from_name,subject,body_text,body_html,attachments)
                            VALUES ($mid,$from,$fromName,$subject,$bodyText,$bodyHtml,$attach)";
                        while (await r.ReadAsync(ct))
                        {
                            ins.Parameters.Clear();
                            ins.Parameters.AddWithValue("$mid", r.GetString(0));
                            ins.Parameters.AddWithValue("$from", r.GetString(1));
                            ins.Parameters.AddWithValue("$fromName", r.GetString(2));
                            ins.Parameters.AddWithValue("$subject", r.GetString(3));
                            ins.Parameters.AddWithValue("$bodyText", r.GetString(4));
                            ins.Parameters.AddWithValue("$bodyHtml", HtmlToText(r.GetString(5)));
                            ins.Parameters.AddWithValue("$attach", AttachmentSearchTextForMessage(conn, r.GetString(0)));
                            await ins.ExecuteNonQueryAsync(ct);
                        }
                    }

                    await tx.CommitAsync(ct);
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }, ct);
        }

        // Convert free user input into a safe FTS5 prefix query (avoids MATCH syntax errors).
        private static string BuildFtsQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";
            var tokens = Regex.Matches(query, "[A-Za-z0-9]+")
                .Select(m => m.Value)
                .Where(t => t.Length > 0)
                .Take(10)
                .ToList();
            if (tokens.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(tokens[i]).Append('*');
            }
            return sb.ToString();
        }

        private static MessageSummary MapSummary(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            ToAddress = r.GetString(1),
            FromAddress = r.GetString(2),
            FromName = r.IsDBNull(3) ? null : r.GetString(3),
            Subject = r.IsDBNull(4) ? null : r.GetString(4),
            ReceivedUtc = ParseUtc(r.GetString(5)),
            DateUtc = r.IsDBNull(6) ? (DateTime?)null : ParseUtc(r.GetString(6)),
            ThreadId = r.GetString(7),
            HasAttachments = r.GetInt32(8) != 0,
            IsRead = r.GetInt32(9) != 0,
            Snippet = r.IsDBNull(10) ? "" : r.GetString(10),
            Code = r.IsDBNull(11) ? null : r.GetString(11)
        };

        private static StoredMessage MapMessage(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            ToAddress = r.GetString(1),
            FromAddress = r.GetString(2),
            FromName = r.IsDBNull(3) ? null : r.GetString(3),
            Subject = r.IsDBNull(4) ? null : r.GetString(4),
            DateUtc = r.IsDBNull(5) ? (DateTime?)null : ParseUtc(r.GetString(5)),
            ReceivedUtc = ParseUtc(r.GetString(6)),
            MessageId = r.IsDBNull(7) ? null : r.GetString(7),
            InReplyTo = r.IsDBNull(8) ? null : r.GetString(8),
            ReferencesRaw = r.IsDBNull(9) ? null : r.GetString(9),
            ThreadId = r.GetString(10),
            BodyText = r.IsDBNull(11) ? null : r.GetString(11),
            BodyHtml = r.IsDBNull(12) ? null : r.GetString(12),
            HasAttachments = r.GetInt32(13) != 0,
            RawSize = r.GetInt64(14),
            IsRead = r.GetInt32(15) != 0,
            IsDeleted = r.GetInt32(16) != 0
        };

        private static StoredAttachment MapAttachment(SqliteDataReader r) => new()
        {
            Id = r.GetString(0),
            MessageId = r.GetString(1),
            FileName = r.GetString(2),
            ContentType = r.IsDBNull(3) ? null : r.GetString(3),
            SizeBytes = r.GetInt64(4),
            StoragePath = r.GetString(5),
            IsInline = r.GetInt32(6) != 0,
            ContentId = r.IsDBNull(7) ? null : r.GetString(7)
        };

        private static DateTime ParseUtc(string s)
            => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;
    }
}
