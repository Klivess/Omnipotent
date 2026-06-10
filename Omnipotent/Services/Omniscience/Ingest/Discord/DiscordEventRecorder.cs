using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>
    /// Captures the behavioural gateway events beyond MESSAGE_CREATE: presence (game +
    /// Spotify activity, custom statuses), typing, reactions, voice sessions, edits,
    /// deletions and member updates. Presence/typing are high-volume, so all writes go
    /// through a write-behind queue flushed in batches under the global write lock.
    /// </summary>
    public class DiscordEventRecorder : IAsyncDisposable
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly ConcurrentQueue<Action<SqliteConnection, SqliteTransaction>> queue = new();
        private readonly CancellationTokenSource cts = new();
        private readonly Task flushLoop;

        // Presence dedupe: only record on change, not on every gateway echo.
        private readonly ConcurrentDictionary<string, string> lastPresenceFingerprint = new();
        private readonly ConcurrentDictionary<string, string> lastCustomStatus = new();
        private readonly ConcurrentDictionary<string, string> lastAvatarHash = new();

        private const int FlushIntervalMs = 8000;
        private const int FlushThreshold = 200;

        public DiscordEventRecorder(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
            flushLoop = Task.Run(FlushLoopAsync);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await flushLoop; } catch { }
            await FlushAsync();
        }

        /// <summary>Routes a raw gateway dispatch. Safe to call from any source's gateway.</summary>
        public void OnGatewayEvent(string eventName, JObject d)
        {
            try
            {
                switch (eventName)
                {
                    case "PRESENCE_UPDATE": HandlePresence(d); break;
                    case "TYPING_START": HandleTyping(d); break;
                    case "MESSAGE_REACTION_ADD": HandleReaction(d, "add"); break;
                    case "MESSAGE_REACTION_REMOVE": HandleReaction(d, "remove"); break;
                    case "VOICE_STATE_UPDATE": HandleVoiceState(d); break;
                    case "MESSAGE_DELETE": HandleDelete(d); break;
                    case "GUILD_MEMBER_UPDATE": HandleMemberUpdate(d); break;
                }
            }
            catch (Exception ex)
            {
                _ = service.ServiceLogError(ex, $"[Omniscience] Event recorder failed on {eventName}");
            }
        }

        // ── Presence: status + activities (games, Spotify) + custom status ──
        private void HandlePresence(JObject d)
        {
            string? userId = (d["user"] as JObject)?.Value<string>("id");
            if (string.IsNullOrEmpty(userId)) return;
            string status = d.Value<string>("status") ?? "";
            var activities = d["activities"] as JArray ?? new JArray();

            // Custom status is activity type 4; its 'state' is the user-written text.
            string customStatus = "";
            foreach (var a in activities)
                if (a.Value<int?>("type") == 4)
                    customStatus = a.Value<string>("state") ?? "";

            string activitiesJson = activities.ToString(Newtonsoft.Json.Formatting.None);
            string fingerprint = status + "|" + customStatus + "|" + activitiesJson;
            if (lastPresenceFingerprint.TryGetValue(userId, out var prev) && prev == fingerprint) return;
            lastPresenceFingerprint[userId] = fingerprint;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            queue.Enqueue((conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO presence_events(platform_user_id, status, custom_status, activities_json, captured_at)
                    VALUES($u,$s,$cs,$a,$t)";
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$cs", string.IsNullOrEmpty(customStatus) ? DBNull.Value : customStatus);
                cmd.Parameters.AddWithValue("$a", activities.Count == 0 ? DBNull.Value : activitiesJson);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            });

            // Custom-status text changes are identity history (statuses are micro-blogs).
            if (lastCustomStatus.TryGetValue(userId, out var prevStatusText))
            {
                if (prevStatusText != customStatus)
                    EnqueueIdentityHistory(userId, "custom_status", prevStatusText, customStatus, now);
            }
            lastCustomStatus[userId] = customStatus;
        }

        private void HandleTyping(JObject d)
        {
            string? userId = d.Value<string>("user_id");
            string? channelId = d.Value<string>("channel_id");
            if (string.IsNullOrEmpty(userId)) return;
            long startedAt = (d.Value<long?>("timestamp") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) * 1000;
            queue.Enqueue((conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO typing_events(platform_user_id, channel_id, started_at) VALUES($u,$c,$t)";
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", startedAt);
                cmd.ExecuteNonQuery();
            });
        }

        private void HandleReaction(JObject d, string action)
        {
            string? userId = d.Value<string>("user_id");
            string? messageId = d.Value<string>("message_id");
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(messageId)) return;
            string? channelId = d.Value<string>("channel_id");
            var emojiObj = d["emoji"] as JObject;
            string emoji = emojiObj?.Value<string>("name") ?? "?";
            if (emojiObj?.Value<string>("id") is string custom && !string.IsNullOrEmpty(custom))
                emoji = ":" + emoji + ":";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            queue.Enqueue((conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO reaction_events(platform_message_id, channel_id, reactor_platform_user_id, emoji, action, occurred_at)
                    VALUES($m,$c,$u,$e,$a,$t)";
                cmd.Parameters.AddWithValue("$m", messageId);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", userId);
                cmd.Parameters.AddWithValue("$e", emoji);
                cmd.Parameters.AddWithValue("$a", action);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            });
        }

        private void HandleVoiceState(JObject d)
        {
            string? userId = d.Value<string>("user_id");
            if (string.IsNullOrEmpty(userId)) return;
            string? channelId = d.Value<string>("channel_id"); // null = left voice entirely
            string? guildId = d.Value<string>("guild_id");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            queue.Enqueue((conn, tx) =>
            {
                // Close any open session (covers leave AND channel moves).
                using (var close = conn.CreateCommand())
                {
                    close.Transaction = tx;
                    close.CommandText = @"UPDATE voice_sessions SET left_at=$t
                        WHERE platform_user_id=$u AND left_at IS NULL
                          AND (channel_id IS NOT $c OR $c IS NULL)";
                    close.Parameters.AddWithValue("$t", now);
                    close.Parameters.AddWithValue("$u", userId);
                    close.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                    close.ExecuteNonQuery();
                }
                if (channelId != null)
                {
                    // Open a session only if one isn't already open for this channel.
                    using var check = conn.CreateCommand();
                    check.Transaction = tx;
                    check.CommandText = "SELECT 1 FROM voice_sessions WHERE platform_user_id=$u AND channel_id=$c AND left_at IS NULL LIMIT 1";
                    check.Parameters.AddWithValue("$u", userId);
                    check.Parameters.AddWithValue("$c", channelId);
                    if (check.ExecuteScalar() == null)
                    {
                        using var open = conn.CreateCommand();
                        open.Transaction = tx;
                        open.CommandText = @"INSERT INTO voice_sessions(platform_user_id, guild_id, channel_id, joined_at)
                            VALUES($u,$g,$c,$t)";
                        open.Parameters.AddWithValue("$u", userId);
                        open.Parameters.AddWithValue("$g", (object?)guildId ?? DBNull.Value);
                        open.Parameters.AddWithValue("$c", channelId);
                        open.Parameters.AddWithValue("$t", now);
                        open.ExecuteNonQuery();
                    }
                }
            });
        }

        private void HandleDelete(JObject d)
        {
            string? platformMessageId = d.Value<string>("id");
            if (string.IsNullOrEmpty(platformMessageId)) return;
            string? channelId = d.Value<string>("channel_id");
            string compositeId = DiscordNormaliser.Platform + ":" + platformMessageId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            queue.Enqueue((conn, tx) =>
            {
                // Recover the content we already captured — the deleted-message archive.
                string? content = null, author = null;
                using (var get = conn.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT content, author_identity_id FROM messages WHERE message_id=$id";
                    get.Parameters.AddWithValue("$id", compositeId);
                    using var r = get.ExecuteReader();
                    if (r.Read())
                    {
                        content = r.IsDBNull(0) ? null : r.GetString(0);
                        author = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                }
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO message_deletes(message_id, channel_id, author_identity_id, content, deleted_at)
                    VALUES($id,$c,$a,$txt,$t)";
                cmd.Parameters.AddWithValue("$id", compositeId);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", (object?)author ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$txt", (object?)content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>
        /// Records an edit for a message we already hold: message_edits row (old → new)
        /// plus content update. Returns immediately; the write happens on flush.
        /// </summary>
        public void RecordEdit(string compositeMessageId, string? newContent, DateTime? editedAt)
        {
            long ts = editedAt.HasValue ? new DateTimeOffset(editedAt.Value).ToUnixTimeMilliseconds()
                                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            queue.Enqueue((conn, tx) =>
            {
                string? oldContent = null;
                bool exists = false;
                using (var get = conn.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT content FROM messages WHERE message_id=$id";
                    get.Parameters.AddWithValue("$id", compositeMessageId);
                    using var r = get.ExecuteReader();
                    if (r.Read()) { exists = true; oldContent = r.IsDBNull(0) ? null : r.GetString(0); }
                }
                if (!exists || oldContent == newContent) return;

                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO message_edits(message_id, old_content, new_content, edited_at) VALUES($id,$o,$n,$t)";
                    ins.Parameters.AddWithValue("$id", compositeMessageId);
                    ins.Parameters.AddWithValue("$o", (object?)oldContent ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$n", (object?)newContent ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$t", ts);
                    ins.ExecuteNonQuery();
                }
                using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE messages SET content=$n, edited_at=$t WHERE message_id=$id";
                    upd.Parameters.AddWithValue("$n", (object?)newContent ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$t", ts);
                    upd.Parameters.AddWithValue("$id", compositeMessageId);
                    upd.ExecuteNonQuery();
                }
            });
        }

        private void HandleMemberUpdate(JObject d)
        {
            var user = d["user"] as JObject;
            string? userId = user?.Value<string>("id");
            if (string.IsNullOrEmpty(userId)) return;
            string? avatar = user!.Value<string>("avatar");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrEmpty(avatar))
            {
                if (lastAvatarHash.TryGetValue(userId, out var prev) && prev != avatar)
                    EnqueueIdentityHistory(userId, "avatar", prev, avatar, now);
                lastAvatarHash[userId] = avatar;
            }
        }

        private void EnqueueIdentityHistory(string platformUserId, string field, string? oldValue, string? newValue, long ts)
        {
            queue.Enqueue((conn, tx) =>
            {
                // Resolve identity_id from platform user id; skip unknown users.
                string? identityId = null;
                using (var get = conn.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT identity_id FROM platform_identities WHERE platform='discord' AND platform_user_id=$u";
                    get.Parameters.AddWithValue("$u", platformUserId);
                    identityId = get.ExecuteScalar() as string;
                }
                if (identityId == null) return;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO identity_history(identity_id, field, old_value, new_value, changed_at)
                    VALUES($i,$f,$o,$n,$t)";
                cmd.Parameters.AddWithValue("$i", identityId);
                cmd.Parameters.AddWithValue("$f", field);
                cmd.Parameters.AddWithValue("$o", (object?)oldValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$n", (object?)newValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", ts);
                cmd.ExecuteNonQuery();
            });
        }

        private async Task FlushLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(queue.Count >= FlushThreshold ? 250 : FlushIntervalMs, cts.Token); }
                catch (OperationCanceledException) { break; }
                try { await FlushAsync(); }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Event recorder flush failed"); }
            }
        }

        private async Task FlushAsync()
        {
            if (queue.IsEmpty) return;
            var batch = new List<Action<SqliteConnection, SqliteTransaction>>();
            while (batch.Count < 2000 && queue.TryDequeue(out var op)) batch.Add(op);
            if (batch.Count == 0) return;

            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var op in batch)
                {
                    try { op(conn, tx); }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Event write failed (skipped)"); }
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
