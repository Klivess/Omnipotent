using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Replica;
using System.Net;
using System.Net.Http;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience
{
#pragma warning disable CS4014
    /// <summary>
    /// HTTP routes specific to the Replica system. Kept separate from
    /// <see cref="OmniscienceRoutes"/> so the dossier-replica feature has a clear
    /// surface and can evolve independently.
    ///
    /// Endpoints (all under KMPermissions.Klives):
    ///   GET  /omniscience/replica/status?personId=...
    ///   POST /omniscience/replica/train          { personId }
    ///   GET  /omniscience/replica/job?jobId=...
    ///   GET  /omniscience/replica/chats?personId=...
    ///   POST /omniscience/replica/chats/new      { personId, title? }
    ///   POST /omniscience/replica/chats/rename   { chatId, title }
    ///   POST /omniscience/replica/chats/delete   { chatId }
    ///   GET  /omniscience/replica/chats/messages?chatId=...
    ///   POST /omniscience/replica/chats/send     { chatId, personId, message, useSelfCritique? }
    ///   GET  /omniscience/notifications
    ///   POST /omniscience/notifications/dismiss  { notifId }
    /// </summary>
    public class ReplicaRoutes
    {
        private readonly Omniscience service;
        public ReplicaRoutes(Omniscience service) { this.service = service; }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/omniscience/replica/status", async req =>
            {
                try
                {
                    string? personId = req.userParameters?["personId"];
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    var payload = BuildStatusPayload(personId);
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/train", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    string? personId = (string?)body?.personId;
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    if (!IsProfileTarget(personId)) { await Err(req, "Person is not in the profile-targets allow-list."); return; }

                    // Reject if a job for this person is already running.
                    if (HasRunningJob(personId)) { await Err(req, "Training already in progress for this person."); return; }

                    // Fire-and-forget; UI polls /replica/job?jobId=
                    var trainer = service.ReplicaTrainer;
                    var t = Task.Run(async () =>
                    {
                        try { await trainer.TrainAsync(personId, CancellationToken.None); }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, $"[Replica] Training task crashed for person {personId}."); }
                    });
                    // Wait briefly for the job row to appear so we can return a jobId.
                    long jobId = await WaitForJobIdAsync(personId, TimeSpan.FromSeconds(5));
                    await req.ReturnResponse(new JObject { ["ok"] = true, ["job_id"] = jobId }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/job", async req =>
            {
                try
                {
                    string? jobIdStr = req.userParameters?["jobId"];
                    if (!long.TryParse(jobIdStr, out long jobId)) { await Err(req, "jobId required"); return; }
                    var payload = BuildJobPayload(jobId);
                    if (payload == null) { await Err(req, "Job not found"); return; }
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // ── chats ──

            await service.CreateAPIRoute("/omniscience/replica/chats", async req =>
            {
                try
                {
                    string? personId = req.userParameters?["personId"];
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    var payload = BuildChatsPayload(personId);
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/chats/new", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    string? personId = (string?)body?.personId;
                    string? title = (string?)body?.title;
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    long chatId = CreateChat(personId, title);
                    await req.ReturnResponse(new JObject { ["ok"] = true, ["chat_id"] = chatId }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/chats/rename", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    long chatId = (long?)body?.chatId ?? 0;
                    string? title = (string?)body?.title;
                    if (chatId <= 0) { await Err(req, "chatId required"); return; }
                    RenameChat(chatId, title ?? "");
                    await req.ReturnResponse(new JObject { ["ok"] = true }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/chats/delete", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    long chatId = (long?)body?.chatId ?? 0;
                    if (chatId <= 0) { await Err(req, "chatId required"); return; }
                    DeleteChat(chatId);
                    await req.ReturnResponse(new JObject { ["ok"] = true }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/chats/messages", async req =>
            {
                try
                {
                    string? chatIdStr = req.userParameters?["chatId"];
                    if (!long.TryParse(chatIdStr, out long chatId)) { await Err(req, "chatId required"); return; }
                    var payload = BuildChatMessagesPayload(chatId);
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/chats/send", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    long chatId = (long?)body?.chatId ?? 0;
                    string? personId = (string?)body?.personId;
                    string? message = (string?)body?.message;
                    bool useSelfCritique = (bool?)body?.useSelfCritique ?? false;
                    if (chatId <= 0) { await Err(req, "chatId required"); return; }
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    if (string.IsNullOrWhiteSpace(message)) { await Err(req, "message required"); return; }

                    var result = await service.ReplicaChat.SendAsync(chatId, personId, message, useSelfCritique, CancellationToken.None);
                    var payload = new JObject
                    {
                        ["ok"] = true,
                        ["draft"] = result.Draft,
                        ["polished"] = result.Polished,
                        ["used_self_critique"] = result.UsedSelfCritique,
                        ["draft_latency_ms"] = result.DraftLatencyMs,
                        ["polish_latency_ms"] = result.PolishLatencyMs,
                        ["model_used"] = result.ModelUsed,
                    };
                    // Auto-title freshly-created chats from the first user message.
                    AutoTitleIfBlank(chatId, message);
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── notifications ──

            await service.CreateAPIRoute("/omniscience/notifications", async req =>
            {
                try
                {
                    var payload = BuildNotificationsPayload();
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/notifications/dismiss", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    long notifId = (long?)body?.notifId ?? 0;
                    if (notifId <= 0) { await Err(req, "notifId required"); return; }
                    DismissNotification(notifId);
                    await req.ReturnResponse(new JObject { ["ok"] = true }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── payload builders ──

        private JObject BuildStatusPayload(string personId)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT status, version, built_at, model_used, messages_embedded,
                                       prompt_token_estimate, last_error, last_status_at,
                                       (CASE WHEN dossier_json IS NULL THEN 0 ELSE 1 END) AS has_dossier
                                FROM replicas WHERE person_id=$p";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return new JObject { ["person_id"] = personId, ["status"] = ReplicaStatus.None };
            return new JObject
            {
                ["person_id"] = personId,
                ["status"] = r.GetString(0),
                ["version"] = r.GetInt32(1),
                ["built_at"] = r.IsDBNull(2) ? null : (long?)r.GetInt64(2),
                ["model_used"] = r.IsDBNull(3) ? null : r.GetString(3),
                ["messages_embedded"] = r.GetInt32(4),
                ["prompt_token_estimate"] = r.GetInt32(5),
                ["last_error"] = r.IsDBNull(6) ? null : r.GetString(6),
                ["last_status_at"] = r.GetInt64(7),
                ["has_dossier"] = r.GetInt32(8) == 1,
            };
        }

        private JObject? BuildJobPayload(long jobId)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT person_id, started_at, finished_at, status, stage, progress_pct, log_json, error
                                FROM replica_training_jobs WHERE job_id=$j";
            cmd.Parameters.AddWithValue("$j", jobId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            JArray logs = new();
            try { logs = JArray.Parse(r.IsDBNull(6) ? "[]" : r.GetString(6)); } catch { }
            return new JObject
            {
                ["job_id"] = jobId,
                ["person_id"] = r.GetString(0),
                ["started_at"] = r.GetInt64(1),
                ["finished_at"] = r.IsDBNull(2) ? null : (long?)r.GetInt64(2),
                ["status"] = r.GetString(3),
                ["stage"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["progress_pct"] = r.GetInt32(5),
                ["log"] = logs,
                ["error"] = r.IsDBNull(7) ? null : r.GetString(7),
            };
        }

        private JArray BuildChatsPayload(string personId)
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT chat_id, title, created_at, updated_at, archived
                FROM replica_chats WHERE person_id=$p AND archived=0
                ORDER BY updated_at DESC";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject
                {
                    ["chat_id"] = r.GetInt64(0),
                    ["title"] = r.IsDBNull(1) ? null : r.GetString(1),
                    ["created_at"] = r.GetInt64(2),
                    ["updated_at"] = r.GetInt64(3),
                    ["archived"] = r.GetInt32(4) == 1,
                });
            }
            return arr;
        }

        private JArray BuildChatMessagesPayload(long chatId)
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT message_id, role, content, sent_at, latency_ms, used_self_critique, model_used
                FROM replica_chat_messages WHERE chat_id=$c ORDER BY sent_at ASC";
            cmd.Parameters.AddWithValue("$c", chatId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject
                {
                    ["message_id"] = r.GetInt64(0),
                    ["role"] = r.GetString(1),
                    ["content"] = r.GetString(2),
                    ["sent_at"] = r.GetInt64(3),
                    ["latency_ms"] = r.IsDBNull(4) ? null : (long?)r.GetInt64(4),
                    ["used_self_critique"] = r.GetInt32(5) == 1,
                    ["model_used"] = r.IsDBNull(6) ? null : r.GetString(6),
                });
            }
            return arr;
        }

        private JArray BuildNotificationsPayload()
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT notif_id, kind, person_id, payload_json, created_at
                FROM replica_notifications WHERE dismissed=0 ORDER BY created_at DESC LIMIT 50";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                JObject payload = new();
                try { payload = JObject.Parse(r.IsDBNull(3) ? "{}" : r.GetString(3)); } catch { }
                arr.Add(new JObject
                {
                    ["notif_id"] = r.GetInt64(0),
                    ["kind"] = r.GetString(1),
                    ["person_id"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["payload"] = payload,
                    ["created_at"] = r.GetInt64(4),
                });
            }
            return arr;
        }

        // ── mutations ──

        private long CreateChat(string personId, string? title)
        {
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_chats(person_id, title, created_at, updated_at)
                    VALUES($p, $t, $c, $c); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return (long)(cmd.ExecuteScalar() ?? 0L);
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private void RenameChat(long chatId, string title)
        {
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE replica_chats SET title=$t, updated_at=$u WHERE chat_id=$c";
                cmd.Parameters.AddWithValue("$t", title);
                cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$c", chatId);
                cmd.ExecuteNonQuery();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private void DeleteChat(long chatId)
        {
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM replica_chat_messages WHERE chat_id=$c";
                    cmd.Parameters.AddWithValue("$c", chatId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM replica_chats WHERE chat_id=$c";
                    cmd.Parameters.AddWithValue("$c", chatId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private void AutoTitleIfBlank(long chatId, string firstMessage)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title FROM replica_chats WHERE chat_id=$c";
            cmd.Parameters.AddWithValue("$c", chatId);
            var existing = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing)) return;
            string title = firstMessage.Trim();
            if (title.Length > 60) title = title.Substring(0, 60).TrimEnd() + "…";
            RenameChat(chatId, title);
        }

        private void DismissNotification(long notifId)
        {
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE replica_notifications SET dismissed=1 WHERE notif_id=$n";
                cmd.Parameters.AddWithValue("$n", notifId);
                cmd.ExecuteNonQuery();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        // ── helpers ──

        private bool IsProfileTarget(string personId)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT enabled FROM person_profile_targets WHERE person_id=$p";
            cmd.Parameters.AddWithValue("$p", personId);
            var e = cmd.ExecuteScalar();
            if (e == null) return false;
            return Convert.ToInt32(e) == 1;
        }

        private bool HasRunningJob(string personId)
        {
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM replica_training_jobs WHERE person_id=$p AND status IN ('queued','running')";
            cmd.Parameters.AddWithValue("$p", personId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
        }

        private async Task<long> WaitForJobIdAsync(string personId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                using (var conn = service.Db.Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT job_id FROM replica_training_jobs
                        WHERE person_id=$p ORDER BY started_at DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("$p", personId);
                    var v = cmd.ExecuteScalar();
                    if (v != null) return Convert.ToInt64(v);
                }
                await Task.Delay(50);
            }
            return 0;
        }

        private async Task Err(KliveAPI.KliveAPI.UserRequest req, string msg)
        {
            await req.ReturnResponse(new JObject { ["error"] = msg }.ToString(Formatting.None), code: HttpStatusCode.BadRequest);
        }

        private async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            _ = service.ServiceLogError(ex, "[ReplicaRoutes] handler failure");
            await req.ReturnResponse(new JObject { ["error"] = ex.Message }.ToString(Formatting.None), code: HttpStatusCode.InternalServerError);
        }
    }
#pragma warning restore CS4014
}
