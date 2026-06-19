using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveMail.Models;

namespace Omnipotent.Services.KliveMail.Persistence
{
    // All KliveMail data access. Writes go through KliveMailDb's single write lock; reads use
    // their own short-lived connection (WAL allows concurrent readers).
    public sealed class KliveMailRepository
    {
        public const string MailDomain = "klive.dev";
        private readonly KliveMailDb db;

        public KliveMailRepository(KliveMailDb db) { this.db = db; }

        // ─────────────────────────── Ingestion ───────────────────────────

        public async Task InsertMessageAsync(StoredMessage m, CancellationToken ct = default)
        {
            await db.WithWriteLockAsync(async conn =>
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO messages
                        (id,to_address,from_address,from_name,subject,date_utc,received_utc,message_id,in_reply_to,references_raw,thread_id,body_text,body_html,has_attachments,raw_size,is_read,is_deleted)
                        VALUES ($id,$to,$from,$fromName,$subject,$date,$received,$messageId,$inReplyTo,$references,$thread,$bodyText,$bodyHtml,$hasAtt,$rawSize,0,0)";
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
                    fcmd.CommandText = @"INSERT INTO messages_fts(message_id,from_address,from_name,subject,body_text)
                        VALUES ($mid,$from,$fromName,$subject,$bodyText)";
                    fcmd.Parameters.AddWithValue("$mid", m.Id);
                    fcmd.Parameters.AddWithValue("$from", m.FromAddress ?? "");
                    fcmd.Parameters.AddWithValue("$fromName", (object?)m.FromName ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$subject", (object?)m.Subject ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue("$bodyText", (object?)m.BodyText ?? DBNull.Value);
                    await fcmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return true;
            }, ct);
        }

        // ─────────────────────────── Lists / detail ───────────────────────────

        public async Task<List<MessageSummary>> ListMessagesAsync(string? mailbox, bool unreadOnly, bool hasAttachmentOnly, bool trash, int page, int pageSize, CancellationToken ct = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            var where = new List<string> { trash ? "is_deleted = 1" : "is_deleted = 0" };
            if (!string.IsNullOrWhiteSpace(mailbox)) where.Add("to_address = $mailbox");
            if (unreadOnly) where.Add("is_read = 0");
            if (hasAttachmentOnly) where.Add("has_attachments = 1");

            string sql = $@"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE {string.Join(" AND ", where)}
                    ORDER BY received_utc DESC LIMIT $limit OFFSET $offset";

            var list = new List<MessageSummary>();
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(mailbox)) cmd.Parameters.AddWithValue("$mailbox", mailbox.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            return list;
        }

        public async Task<List<MessageSummary>> GetThreadAsync(string threadId, CancellationToken ct = default)
        {
            var list = new List<MessageSummary>();
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE thread_id = $thread AND is_deleted = 0 ORDER BY received_utc ASC";
            cmd.Parameters.AddWithValue("$thread", threadId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            return list;
        }

        public async Task<StoredMessage?> GetMessageAsync(string id, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
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

        public async Task<StoredAttachment?> GetAttachmentAsync(string id, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,message_id,file_name,content_type,size_bytes,storage_path,is_inline,content_id FROM attachments WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapAttachment(reader);
            return null;
        }

        public async Task<List<MessageSummary>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            string ftsQuery = BuildFtsQuery(query);
            if (string.IsNullOrEmpty(ftsQuery)) return new List<MessageSummary>();

            var list = new List<MessageSummary>();
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT m.id,m.to_address,m.from_address,m.from_name,m.subject,m.received_utc,m.date_utc,m.thread_id,m.has_attachments,m.is_read,
                    substr(replace(replace(coalesce(m.body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages_fts f JOIN messages m ON m.id = f.message_id
                    WHERE f MATCH $q AND m.is_deleted = 0
                    ORDER BY m.received_utc DESC LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(MapSummary(reader));
            return list;
        }

        // ─────────────────────────── Mutations ───────────────────────────

        public async Task<bool> SetReadAsync(string id, bool read, CancellationToken ct = default)
        {
            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE messages SET is_read = $r WHERE id = $id";
                cmd.Parameters.AddWithValue("$r", read ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            return rows > 0;
        }

        public async Task<bool> SoftDeleteAsync(string id, CancellationToken ct = default)
        {
            int rows = 0;
            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE messages SET is_deleted = 1 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                rows = await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            return rows > 0;
        }

        // ─────────────────────────── Mailboxes ───────────────────────────

        public async Task<List<MailboxInfo>> ListMailboxesAsync(CancellationToken ct = default)
        {
            var list = new List<MailboxInfo>();
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT mb.address, mb.display_name,
                    (SELECT COUNT(*) FROM messages m WHERE m.to_address = mb.address AND m.is_deleted = 0) AS total,
                    (SELECT COUNT(*) FROM messages m WHERE m.to_address = mb.address AND m.is_deleted = 0 AND m.is_read = 0) AS unread
                    FROM mailboxes mb ORDER BY mb.address ASC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MailboxInfo
                {
                    Address = reader.GetString(0),
                    DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Total = reader.GetInt32(2),
                    Unread = reader.GetInt32(3),
                    Pinned = true
                });
            }
            return list;
        }

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
            return rows > 0;
        }

        // ─────────────────────────── Stats ───────────────────────────

        public async Task<(int Total, int Unread, int Trash)> GetStatsAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 0) AS total,
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 0 AND is_read = 0) AS unread,
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 1) AS trash";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            return (0, 0, 0);
        }

        // ─────────────────────────── Helpers ───────────────────────────

        public static string NormalizeAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            address = address.Trim().ToLowerInvariant();
            if (!address.Contains('@')) address = address + "@" + MailDomain;
            return address;
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
            Snippet = r.IsDBNull(10) ? null : r.GetString(10)
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
