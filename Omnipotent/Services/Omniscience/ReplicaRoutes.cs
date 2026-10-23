using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Replica;
using System.Net;
using System.Net.Http;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.Omniscience
{
#pragma warning disable CS4014
    /// <summary>
    /// HTTP routes specific to the Replica system. Kept separate from
    /// <see cref="OmniscienceRoutes"/> so the dossier-replica feature has a clear
    /// surface and can evolve independently.
    ///
    /// Endpoints (gated by the omniscience.replica.* permissions):
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
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

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
            }, HttpMethod.Post, OmnisciencePerms.ReplicaTrain);

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
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

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
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

            await service.CreateAPIRoute("/omniscience/replica/chats/new", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    string? personId = (string?)body?.personId;
                    string? title = (string?)body?.title;
                    long? asOfAt = (long?)body?.asOfAt;
                    string? contextKind = (string?)body?.contextKind;
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    long chatId = CreateChat(personId, title, asOfAt, contextKind);
                    await req.ReturnResponse(new JObject { ["ok"] = true, ["chat_id"] = chatId, ["as_of_at"] = asOfAt,
                        ["context_kind"] = string.IsNullOrWhiteSpace(contextKind) ? "auto" : contextKind,
                        ["is_simulation"] = true, ["simulation_label"] = ReplicaChatOrchestrator.SimulationLabel }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

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
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

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
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

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
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

            await service.CreateAPIRoute("/omniscience/replica/chats/send", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    long chatId = body.Value<long?>("chatId") ?? 0;
                    string? personId = body.Value<string>("personId");
                    string? message = body.Value<string>("message");
                    bool useSelfCritique = body.Value<bool?>("useSelfCritique") ?? false;
                    if (chatId <= 0) { await Err(req, "chatId required"); return; }
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    if (string.IsNullOrWhiteSpace(message)) { await Err(req, "message required"); return; }

                    var options = new ReplicaChatOptions
                    {
                        AsOfAt = body.Value<long?>("asOfAt"),
                        ContextKind = body.Value<string>("contextKind") ?? "auto",
                        IncludeEvidence = body.Value<bool?>("includeEvidence") ?? true,
                    };
                    var result = await service.ReplicaChat.SendAsync(chatId, personId, message, useSelfCritique, options, CancellationToken.None);
                    AutoTitleIfBlank(chatId, message);
                    await req.ReturnResponse(BuildGenerationPayload(result).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

            await service.CreateAPIRoute("/omniscience/replica/chats/settings", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    long chatId = body.Value<long?>("chatId") ?? 0;
                    if (chatId <= 0) { await Err(req, "chatId required"); return; }
                    UpdateChatSettings(chatId, body.Value<long?>("asOfAt"), body.Value<string>("contextKind") ?? "auto");
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

            await service.CreateAPIRoute("/omniscience/replica/eras", async req =>
            {
                try
                {
                    string? personId = req.userParameters?["personId"];
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    var eras = await service.ReplicaTemporal.GetErasAsync(personId, true, CancellationToken.None);
                    await req.ReturnResponse(new JObject
                    {
                        ["eras"] = JArray.FromObject(eras),
                        ["low_coverage"] = eras.Count == 0 || eras.Sum(e => e.MessageCount) < 50,
                        ["is_simulation"] = true,
                        ["simulation_label"] = ReplicaChatOrchestrator.SimulationLabel,
                    }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

            await service.CreateAPIRoute("/omniscience/replica/evidence", async req =>
            {
                try
                {
                    long.TryParse(req.userParameters?["messageId"], out long messageId);
                    if (messageId <= 0) { await Err(req, "messageId required"); return; }
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT evidence_json FROM replica_chat_messages WHERE message_id=$m AND role='assistant'";
                    cmd.Parameters.AddWithValue("$m", messageId);
                    string? json = cmd.ExecuteScalar() as string;
                    JArray evidence = new();
                    try { evidence = JArray.Parse(json ?? "[]"); } catch { }
                    await req.ReturnResponse(new JObject
                    {
                        ["message_id"] = messageId,
                        ["evidence"] = evidence,
                        ["label"] = "Historical evidence consulted — not proof of the generated statement",
                        ["is_simulation"] = true,
                    }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, OmnisciencePerms.MessagesRead);

            await service.CreateAPIRoute("/omniscience/replica/compare", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string? personId = body.Value<string>("personId");
                    string? message = body.Value<string>("message");
                    long leftAt = body.Value<long?>("leftAsOfAt") ?? 0;
                    long rightAt = body.Value<long?>("rightAsOfAt") ?? 0;
                    if (string.IsNullOrWhiteSpace(personId) || string.IsNullOrWhiteSpace(message) || leftAt <= 0 || rightAt <= 0)
                    { await Err(req, "personId, message, leftAsOfAt and rightAsOfAt required"); return; }
                    string context = body.Value<string>("contextKind") ?? "auto";
                    var left = await service.ReplicaChat.GenerateDetailedOnceAsync(personId, message,
                        new ReplicaChatOptions { AsOfAt = leftAt, ContextKind = context }, null, false, CancellationToken.None);
                    var right = await service.ReplicaChat.GenerateDetailedOnceAsync(personId, message,
                        new ReplicaChatOptions { AsOfAt = rightAt, ContextKind = context }, null, false, CancellationToken.None);
                    if (left == null || right == null) { await Err(req, "Both eras need a ready replica and captured coverage."); return; }
                    await req.ReturnResponse(new JObject
                    {
                        ["ok"] = true,
                        ["left"] = BuildGenerationPayload(left),
                        ["right"] = BuildGenerationPayload(right),
                        ["is_simulation"] = true,
                    }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

            await service.CreateAPIRoute("/omniscience/replica/feedback", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string? personId = body.Value<string>("personId");
                    int rating = Math.Clamp(body.Value<int?>("rating") ?? 0, -1, 1);
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    string feedbackId = SaveFeedback(personId, body.Value<long?>("chatId"), body.Value<long?>("messageId"), rating,
                        body.Value<string>("note"), body.Value<long?>("asOfAt"), body.Value<string>("contextKind"));
                    await req.ReturnResponse(new JObject { ["ok"] = true, ["feedback_id"] = feedbackId }.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaChat);

            await service.CreateAPIRoute("/omniscience/replica/feedback", async req =>
            {
                try
                {
                    string? personId = req.userParameters?["personId"];
                    if (string.IsNullOrWhiteSpace(personId)) { await Err(req, "personId required"); return; }
                    await req.ReturnResponse(BuildFeedbackPayload(personId).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

            // ── notifications ──

            await service.CreateAPIRoute("/omniscience/notifications", async req =>
            {
                try
                {
                    var payload = BuildNotificationsPayload();
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, OmnisciencePerms.PersonsRead);

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
            }, HttpMethod.Post, OmnisciencePerms.ReviewAct);
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
            JObject payload;
            if (!r.Read())
            {
                payload = new JObject { ["person_id"] = personId, ["status"] = ReplicaStatus.None, ["version"] = 0 };
            }
            else
            {
                payload = new JObject
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
            r.Close();
            AddReadiness(conn, payload, personId);
            return payload;
        }

        private static void AddReadiness(SqliteConnection conn, JObject payload, string personId)
        {
            long Scalar(string sql)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$p", personId);
                    return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
                }
                catch { return 0; }
            }
            long messages = Scalar("SELECT COALESCE((SELECT message_count FROM person_message_counts WHERE person_id=$p),0)");
            long eras = Scalar("SELECT COUNT(*) FROM replica_era_profiles WHERE person_id=$p");
            long facts = Scalar("SELECT COUNT(*) FROM person_facts WHERE person_id=$p AND status='active'");
            long latest = Scalar("SELECT COALESCE((SELECT last_message_at FROM person_message_counts WHERE person_id=$p),0)");
            long embedded = 0, pairs = 0, embeddedStimuli = 0;
            try
            {
                var idents = Analytics.AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count > 0)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        string inC = Analytics.AnalyticHelpers.BindInClause(cmd, "i", idents);
                        cmd.CommandText = $@"SELECT
                            (SELECT COUNT(*) FROM message_embeddings e JOIN messages m ON m.message_id=e.message_id WHERE m.author_identity_id IN ({inC})),
                            (SELECT COUNT(*) FROM stimulus_reply_pairs s WHERE s.replier_identity_id IN ({inC})),
                            (SELECT COUNT(*) FROM stimulus_reply_pairs s JOIN message_embeddings e ON e.message_id=s.stimulus_message_id WHERE s.replier_identity_id IN ({inC}))";
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            embedded = reader.GetInt64(0);
                            pairs = reader.GetInt64(1);
                            embeddedStimuli = reader.GetInt64(2);
                        }
                    }
                }
            }
            catch { }
            var warnings = new JArray();
            if (messages < 50) warnings.Add("Sparse message history");
            if (pairs < 5) warnings.Add("Few harvested stimulus/reply pairs");
            if (embeddedStimuli < pairs) warnings.Add("Stimulus embeddings are still backfilling");
            if (eras == 0) warnings.Add("Temporal eras have not been built");
            payload["target_messages"] = messages;
            payload["embedded_messages"] = embedded;
            payload["stimulus_reply_pairs"] = pairs;
            payload["embedded_stimuli"] = embeddedStimuli;
            payload["active_facts"] = facts;
            payload["era_count"] = eras;
            payload["latest_message_at"] = latest == 0 ? null : latest;
            payload["is_stale"] = payload.Value<long?>("built_at") is long built && latest > built;
            payload["coverage_warnings"] = warnings;
            payload["pipeline_version"] = "temporal-v2";
            payload["is_simulation"] = true;
            payload["simulation_label"] = ReplicaChatOrchestrator.SimulationLabel;
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
            cmd.CommandText = @"SELECT chat_id, title, created_at, updated_at, archived, as_of_at, COALESCE(context_kind,'auto')
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
                    ["as_of_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                    ["context_kind"] = r.GetString(6),
                    ["is_simulation"] = true,
                    ["simulation_label"] = ReplicaChatOrchestrator.SimulationLabel,
                });
            }
            return arr;
        }

        private JArray BuildChatMessagesPayload(long chatId)
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT message_id, role, content, sent_at, latency_ms, used_self_critique, model_used,
                       as_of_at, context_kind, evidence_json, style_json, generation_version
                FROM replica_chat_messages WHERE chat_id=$c ORDER BY message_id ASC";
            cmd.Parameters.AddWithValue("$c", chatId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int evidenceCount = 0;
                try { evidenceCount = JArray.Parse(r.IsDBNull(9) ? "[]" : r.GetString(9)).Count; } catch { }
                JObject? style = null;
                try { style = JObject.Parse(r.IsDBNull(10) ? "{}" : r.GetString(10)); } catch { }
                arr.Add(new JObject
                {
                    ["message_id"] = r.GetInt64(0),
                    ["role"] = r.GetString(1),
                    ["content"] = r.GetString(2),
                    ["sent_at"] = r.GetInt64(3),
                    ["latency_ms"] = r.IsDBNull(4) ? null : (long?)r.GetInt64(4),
                    ["used_self_critique"] = r.GetInt32(5) == 1,
                    ["model_used"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["as_of_at"] = r.IsDBNull(7) ? null : (long?)r.GetInt64(7),
                    ["context_kind"] = r.IsDBNull(8) ? "auto" : r.GetString(8),
                    ["evidence_count"] = evidenceCount,
                    ["style"] = style,
                    ["generation_version"] = r.IsDBNull(11) ? "legacy" : r.GetString(11),
                    ["is_simulation"] = true,
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

        private long CreateChat(string personId, string? title, long? asOfAt = null, string? contextKind = null)
        {
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_chats(person_id, title, created_at, updated_at, as_of_at, context_kind)
                    VALUES($p,$t,$c,$c,$asOf,$context); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$asOf", asOfAt == null ? (object)DBNull.Value : asOfAt.Value);
                cmd.Parameters.AddWithValue("$context", string.IsNullOrWhiteSpace(contextKind) ? "auto" : contextKind);
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

        private static JObject BuildGenerationPayload(ReplicaChatOrchestrator.ReplicaChatResult result)
        {
            return new JObject
            {
                ["ok"] = true,
                ["message_id"] = result.AssistantMessageId,
                ["draft"] = result.Draft,
                ["polished"] = result.Polished,
                ["used_self_critique"] = result.UsedSelfCritique,
                ["draft_latency_ms"] = result.DraftLatencyMs,
                ["polish_latency_ms"] = result.PolishLatencyMs,
                ["model_used"] = result.ModelUsed,
                ["provider"] = result.Provider,
                ["as_of_at"] = result.AsOfAt,
                ["era"] = result.Era,
                ["context_kind"] = result.ContextKind,
                ["style"] = JObject.FromObject(result.Style),
                ["evidence_count"] = result.Evidence.Count,
                ["low_coverage"] = result.LowCoverage,
                ["prompt_token_estimate"] = result.PromptTokenEstimate,
                ["generation_version"] = result.GenerationVersion,
                ["is_simulation"] = true,
                ["simulation_label"] = ReplicaChatOrchestrator.SimulationLabel,
            };
        }

        private void UpdateChatSettings(long chatId, long? asOfAt, string contextKind)
        {
            string context = contextKind switch { "dm" or "group_dm" or "guild_channel" => contextKind, _ => "auto" };
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE replica_chats SET as_of_at=$asOf, context_kind=$context, updated_at=$now WHERE chat_id=$chat";
                cmd.Parameters.AddWithValue("$asOf", asOfAt == null ? (object)DBNull.Value : asOfAt.Value);
                cmd.Parameters.AddWithValue("$context", context);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$chat", chatId);
                cmd.ExecuteNonQuery();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private string SaveFeedback(string personId, long? chatId, long? messageId, int rating, string? note,
            long? asOfAt, string? contextKind)
        {
            string id = Guid.NewGuid().ToString("N");
            if (note != null && note.Length > 2000) note = note[..2000];
            service.Db.WriteLock.Wait();
            try
            {
                using var conn = service.Db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO replica_feedback
                    (feedback_id,person_id,chat_id,chat_message_id,rating,note,as_of_at,context_kind,created_at)
                    VALUES($id,$p,$chat,$message,$rating,$note,$asOf,$context,$created)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$p", personId);
                cmd.Parameters.AddWithValue("$chat", chatId == null ? (object)DBNull.Value : chatId.Value);
                cmd.Parameters.AddWithValue("$message", messageId == null ? (object)DBNull.Value : messageId.Value);
                cmd.Parameters.AddWithValue("$rating", rating);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$asOf", asOfAt == null ? (object)DBNull.Value : asOfAt.Value);
                cmd.Parameters.AddWithValue("$context", (object?)contextKind ?? "auto");
                cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { service.Db.WriteLock.Release(); }
            return id;
        }

        private JObject BuildFeedbackPayload(string personId)
        {
            var items = new JArray();
            int positive = 0, negative = 0, neutral = 0;
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT feedback_id,chat_id,chat_message_id,rating,note,as_of_at,context_kind,created_at
                FROM replica_feedback WHERE person_id=$p ORDER BY created_at DESC LIMIT 100";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int rating = r.GetInt32(3);
                if (rating > 0) positive++; else if (rating < 0) negative++; else neutral++;
                items.Add(new JObject
                {
                    ["feedback_id"] = r.GetString(0),
                    ["chat_id"] = r.IsDBNull(1) ? null : (long?)r.GetInt64(1),
                    ["message_id"] = r.IsDBNull(2) ? null : (long?)r.GetInt64(2),
                    ["rating"] = rating,
                    ["note"] = r.IsDBNull(4) ? null : r.GetString(4),
                    ["as_of_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                    ["context_kind"] = r.IsDBNull(6) ? "auto" : r.GetString(6),
                    ["created_at"] = r.GetInt64(7),
                });
            }
            return new JObject
            {
                ["total"] = positive + negative + neutral,
                ["positive"] = positive,
                ["negative"] = negative,
                ["neutral"] = neutral,
                ["items"] = items,
            };
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
