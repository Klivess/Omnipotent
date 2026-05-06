using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest
{
    /// <summary>
    /// Receives normalised messages from any ingester, dedupes them by (platform,
    /// platform_message_id), upserts conversations + identities (auto-creating one
    /// PersonEntity per fresh platform identity), writes attachments to a content-
    /// addressed store on disk and writes the corresponding row to SQLite.
    /// All writes run inside <see cref="OmniscienceDb.WriteLock"/>.
    /// </summary>
    public class IngestPipeline
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly HttpClient http;

        public IngestPipeline(Omniscience service, OmniscienceDb db, HttpClient http)
        {
            this.service = service;
            this.db = db;
            this.http = http;
        }

        public event Action<HarvestedMessage>? OnMessagePersisted;

        public async Task IngestAsync(HarvestedMessage msg, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(msg.PlatformMessageId)) return;

            string compositeId = msg.Platform + ":" + msg.PlatformMessageId;
            DateTime now = DateTime.UtcNow;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();

                // Skip if already stored.
                if (RowExists(conn, tx, "messages", "message_id", compositeId)) { tx.Rollback(); return; }

                // 1. Author identity (auto-creates Person on first sight).
                string authorIdentityId = UpsertIdentity(conn, tx, msg.Author, now);

                // 2. Other participants (just identity rows; ignore failures silently).
                foreach (var p in msg.Participants)
                {
                    if (string.IsNullOrEmpty(p.PlatformUserId)) continue;
                    UpsertIdentity(conn, tx, p, now);
                }

                // 3. Conversation row.
                string conversationId = UpsertConversation(conn, tx, msg, now);

                // 4. Participant link.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO conversation_participants(conversation_id, identity_id) VALUES($c,$i)";
                    cmd.Parameters.AddWithValue("$c", conversationId);
                    cmd.Parameters.AddWithValue("$i", authorIdentityId);
                    cmd.ExecuteNonQuery();
                }

                // 5. Insert message.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO messages
                        (message_id, platform, platform_message_id, conversation_id, author_identity_id,
                         sent_at, edited_at, content, reply_to_message_id, language, raw_json, captured_at)
                        VALUES($id,$p,$pmid,$c,$a,$sent,$edit,$content,$reply,$lang,$raw,$cap)";
                    cmd.Parameters.AddWithValue("$id", compositeId);
                    cmd.Parameters.AddWithValue("$p", msg.Platform);
                    cmd.Parameters.AddWithValue("$pmid", msg.PlatformMessageId);
                    cmd.Parameters.AddWithValue("$c", conversationId);
                    cmd.Parameters.AddWithValue("$a", authorIdentityId);
                    cmd.Parameters.AddWithValue("$sent", new DateTimeOffset(msg.SentAt).ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$edit", msg.EditedAt.HasValue ? new DateTimeOffset(msg.EditedAt.Value).ToUnixTimeMilliseconds() : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$content", (object?)msg.Content ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$reply", msg.ReplyToPlatformMessageId == null ? (object)DBNull.Value : (msg.Platform + ":" + msg.ReplyToPlatformMessageId));
                    cmd.Parameters.AddWithValue("$lang", DBNull.Value); // filled by analytics module later
                    cmd.Parameters.AddWithValue("$raw", (object?)msg.RawJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$cap", new DateTimeOffset(now).ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            finally
            {
                db.WriteLock.Release();
            }

            // 6. Attachments (downloaded outside the write lock; their DB rows are written under it).
            foreach (var att in msg.Attachments)
            {
                try { await PersistAttachmentAsync(compositeId, att, ct); }
                catch (Exception ex) { _ = service.ServiceLogError(ex, $"Attachment download failed: {att.OriginalUrl}"); }
            }

            try { OnMessagePersisted?.Invoke(msg); } catch { }
        }

        // ── Identity / person upsert ──
        // Returns identity_id for (platform, platform_user_id), creating the row and a backing Person if absent.
        private string UpsertIdentity(SqliteConnection conn, SqliteTransaction tx, HarvestedIdentity ident, DateTime now)
        {
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT identity_id FROM platform_identities WHERE platform=$p AND platform_user_id=$u";
                get.Parameters.AddWithValue("$p", ident.Platform);
                get.Parameters.AddWithValue("$u", ident.PlatformUserId);
                var existing = get.ExecuteScalar();
                if (existing != null && existing != DBNull.Value)
                {
                    string idid = (string)existing;
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE platform_identities
                        SET platform_username=COALESCE($un, platform_username),
                            display_name=COALESCE($dn, display_name),
                            bio=COALESCE($bio, bio),
                            last_seen=$ls
                        WHERE identity_id=$id";
                    upd.Parameters.AddWithValue("$un", (object?)ident.Username ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$dn", (object?)ident.DisplayName ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$bio", (object?)ident.Bio ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$ls", new DateTimeOffset(now).ToUnixTimeMilliseconds());
                    upd.Parameters.AddWithValue("$id", idid);
                    upd.ExecuteNonQuery();
                    UpsertAltNames(conn, tx, idid, ident, now);
                    return idid;
                }
            }

            string personId = Guid.NewGuid().ToString("N");
            string identityId = Guid.NewGuid().ToString("N");
            string displayName = ident.DisplayName ?? ident.Username ?? ident.PlatformUserId;
            long ts = new DateTimeOffset(now).ToUnixTimeMilliseconds();

            using (var pCmd = conn.CreateCommand())
            {
                pCmd.Transaction = tx;
                pCmd.CommandText = @"INSERT INTO persons(person_id, display_name, created_at, updated_at)
                                     VALUES($id,$dn,$ts,$ts)";
                pCmd.Parameters.AddWithValue("$id", personId);
                pCmd.Parameters.AddWithValue("$dn", displayName);
                pCmd.Parameters.AddWithValue("$ts", ts);
                pCmd.ExecuteNonQuery();
            }
            using (var iCmd = conn.CreateCommand())
            {
                iCmd.Transaction = tx;
                iCmd.CommandText = @"INSERT INTO platform_identities
                    (identity_id, person_id, platform, platform_user_id, platform_username,
                     display_name, bio, first_seen, last_seen)
                    VALUES($id,$pid,$pl,$pu,$un,$dn,$bio,$ts,$ts)";
                iCmd.Parameters.AddWithValue("$id", identityId);
                iCmd.Parameters.AddWithValue("$pid", personId);
                iCmd.Parameters.AddWithValue("$pl", ident.Platform);
                iCmd.Parameters.AddWithValue("$pu", ident.PlatformUserId);
                iCmd.Parameters.AddWithValue("$un", (object?)ident.Username ?? DBNull.Value);
                iCmd.Parameters.AddWithValue("$dn", (object?)ident.DisplayName ?? DBNull.Value);
                iCmd.Parameters.AddWithValue("$bio", (object?)ident.Bio ?? DBNull.Value);
                iCmd.Parameters.AddWithValue("$ts", ts);
                iCmd.ExecuteNonQuery();
            }
            UpsertAltNames(conn, tx, identityId, ident, now);
            return identityId;
        }

        // Insert any per-identity alt-names (per-guild nicknames, divergent global names, etc.)
        // \u2014 idempotent via the (identity_id, alt_name) UNIQUE constraint.
        private static void UpsertAltNames(SqliteConnection conn, SqliteTransaction tx, string identityId, HarvestedIdentity ident, DateTime now)
        {
            if (ident.AltNames == null || ident.AltNames.Count == 0) return;
            long ts = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            foreach (var (name, source) in ident.AltNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO identity_alt_names(identity_id, alt_name, source, first_seen, last_seen)
                    VALUES($id,$n,$s,$ts,$ts)
                    ON CONFLICT(identity_id, alt_name) DO UPDATE SET
                        last_seen=excluded.last_seen,
                        source=COALESCE(identity_alt_names.source, excluded.source)";
                cmd.Parameters.AddWithValue("$id", identityId);
                cmd.Parameters.AddWithValue("$n", name.Trim());
                cmd.Parameters.AddWithValue("$s", (object?)source ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ts", ts);
                try { cmd.ExecuteNonQuery(); } catch { /* best-effort: schema may be older */ }
            }
        }

        private string UpsertConversation(SqliteConnection conn, SqliteTransaction tx, HarvestedMessage msg, DateTime now)
        {
            string convKey = msg.Platform + ":" + (msg.ChannelId ?? "");
            using (var get = conn.CreateCommand())
            {
                get.Transaction = tx;
                get.CommandText = "SELECT conversation_id FROM conversations WHERE platform=$p AND channel_id=$c";
                get.Parameters.AddWithValue("$p", msg.Platform);
                get.Parameters.AddWithValue("$c", msg.ChannelId ?? "");
                var existing = get.ExecuteScalar();
                long ms = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                if (existing != null && existing != DBNull.Value)
                {
                    string cid = (string)existing;
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    // COALESCE keeps the first non-null we ever learned for title/guild_name/kind so live
                    // events (which often lack them) don't blank fields populated by the backfiller.
                    upd.CommandText = @"UPDATE conversations
                        SET last_seen=$ls,
                            title      = COALESCE(title, $t),
                            guild_name = COALESCE(guild_name, $gn),
                            guild_id   = COALESCE(guild_id, $g),
                            kind       = CASE WHEN kind='guild_channel' AND $k IN ('dm','group_dm') THEN $k
                                              WHEN kind IS NULL OR kind='' THEN $k
                                              ELSE kind END
                        WHERE conversation_id=$id";
                    upd.Parameters.AddWithValue("$ls", ms);
                    upd.Parameters.AddWithValue("$id", cid);
                    upd.Parameters.AddWithValue("$t", (object?)msg.ChannelTitle ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$gn", (object?)msg.GuildName ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$g", (object?)msg.GuildId ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$k", msg.ConversationKind ?? "");
                    upd.ExecuteNonQuery();
                    return cid;
                }
                string newId = convKey;
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO conversations
                    (conversation_id, platform, kind, guild_id, guild_name, channel_id, title, first_seen, last_seen)
                    VALUES($id,$p,$k,$g,$gn,$c,$t,$ts,$ts)";
                ins.Parameters.AddWithValue("$id", newId);
                ins.Parameters.AddWithValue("$p", msg.Platform);
                ins.Parameters.AddWithValue("$k", msg.ConversationKind);
                ins.Parameters.AddWithValue("$g", (object?)msg.GuildId ?? DBNull.Value);
                ins.Parameters.AddWithValue("$gn", (object?)msg.GuildName ?? DBNull.Value);
                ins.Parameters.AddWithValue("$c", msg.ChannelId ?? "");
                ins.Parameters.AddWithValue("$t", (object?)msg.ChannelTitle ?? DBNull.Value);
                ins.Parameters.AddWithValue("$ts", ms);
                ins.ExecuteNonQuery();
                return newId;
            }
        }

        private static bool RowExists(SqliteConnection conn, SqliteTransaction tx, string table, string idCol, string id)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT 1 FROM {table} WHERE {idCol}=$x LIMIT 1";
            cmd.Parameters.AddWithValue("$x", id);
            return cmd.ExecuteScalar() != null;
        }

        private async Task PersistAttachmentAsync(string messageId, HarvestedAttachment att, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(att.OriginalUrl)) return;

            // Download to temp, hash, move to content-addressed location.
            string tmp = Path.GetTempFileName();
            byte[] hashBytes;
            long size;
            string ext = Path.GetExtension(att.Filename ?? "") ?? "";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var resp = await http.GetAsync(att.OriginalUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await resp.Content.CopyToAsync(fs, ct);
                }
                using (var fs = File.OpenRead(tmp))
                using (var sha = SHA256.Create())
                {
                    hashBytes = sha.ComputeHash(fs);
                    size = new FileInfo(tmp).Length;
                }
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }

            string hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            string baseDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceAttachmentsDirectory);
            string subDir = Path.Combine(baseDir, hex.Substring(0, 2), hex.Substring(2, 2));
            Directory.CreateDirectory(subDir);
            string finalPath = Path.Combine(subDir, hex + ext);
            if (!File.Exists(finalPath))
            {
                File.Move(tmp, finalPath);
            }
            else
            {
                try { File.Delete(tmp); } catch { }
            }

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO attachments
                    (attachment_id, message_id, kind, original_url, local_path, mime, size_bytes, sha256)
                    VALUES($id,$msg,$k,$u,$lp,$m,$s,$h)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("$msg", messageId);
                cmd.Parameters.AddWithValue("$k", (object?)att.Kind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", att.OriginalUrl);
                cmd.Parameters.AddWithValue("$lp", finalPath);
                cmd.Parameters.AddWithValue("$m", (object?)att.Mime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", size);
                cmd.Parameters.AddWithValue("$h", hex);
                cmd.ExecuteNonQuery();
            }
            finally
            {
                db.WriteLock.Release();
            }
        }
    }
}
