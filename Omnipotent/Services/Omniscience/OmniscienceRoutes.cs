using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience
{
#pragma warning disable CS4014
    /// <summary>
    /// HTTP API for the Omniscience dashboard. Routes are intentionally narrow + stable
    /// so the Nuxt frontend can pin to them. Reads use a fresh connection per call;
    /// writes go through the matching subsystem (Discord ingester / scheduler).
    /// </summary>
    public class OmniscienceRoutes
    {
        private readonly Omniscience service;

        public OmniscienceRoutes(Omniscience service) { this.service = service; }

        public async Task RegisterRoutes()
        {
            await RegisterSourceRoutes();
            await RegisterPersonRoutes();
            await RegisterConversationRoutes();
            await RegisterStatRoutes();
            await RegisterScheduleRoutes();
        }

        // ── Sources ──
        private async Task RegisterSourceRoutes()
        {
            await service.CreateAPIRoute("/omniscience/sources", async req =>
            {
                try
                {
                    var rows = new List<JObject>();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT source_id, platform, label, status, last_status_message, self_username, self_platform_user_id,
                                               added_at, last_full_sync_at, last_event_at FROM harvest_sources ORDER BY added_at DESC";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        rows.Add(new JObject(
                            new JProperty("source_id", r.GetString(0)),
                            new JProperty("platform", r.GetString(1)),
                            new JProperty("label", r.IsDBNull(2) ? null : r.GetString(2)),
                            new JProperty("status", r.GetString(3)),
                            new JProperty("last_status_message", r.IsDBNull(4) ? null : r.GetString(4)),
                            new JProperty("self_username", r.IsDBNull(5) ? null : r.GetString(5)),
                            new JProperty("self_platform_user_id", r.IsDBNull(6) ? null : r.GetString(6)),
                            new JProperty("added_at", r.GetInt64(7)),
                            new JProperty("last_full_sync_at", r.IsDBNull(8) ? (long?)null : r.GetInt64(8)),
                            new JProperty("last_event_at", r.IsDBNull(9) ? (long?)null : r.GetInt64(9))
                        ));
                    }
                    await req.ReturnResponse(new JArray(rows).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/sources/add", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    string platform = (string?)body?.platform ?? "discord";
                    string token = (string?)body?.token ?? "";
                    string label = (string?)body?.label ?? "";
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        await req.ReturnResponse("token is required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!string.Equals(platform, "discord", StringComparison.OrdinalIgnoreCase))
                    {
                        await req.ReturnResponse("Only 'discord' is supported in this build.", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var (ok, selfId, selfName, error) = await service.Discord.AddSourceAsync(token, label, CancellationToken.None);
                    if (!ok)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = false, error }), code: HttpStatusCode.BadRequest);
                        return;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true, self_id = selfId, self_username = selfName }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/sources/remove", async req =>
            {
                try
                {
                    string sourceId = req.userParameters?["sourceId"] ?? "";
                    if (string.IsNullOrWhiteSpace(sourceId))
                    {
                        await req.ReturnResponse("sourceId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    await service.Discord.RemoveSourceAsync(sourceId);
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/sources/backfill", async req =>
            {
                try
                {
                    string sourceId = req.userParameters?["sourceId"] ?? "";
                    if (string.IsNullOrWhiteSpace(sourceId))
                    {
                        await req.ReturnResponse("sourceId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    await service.Discord.RequestBackfillAsync(sourceId, CancellationToken.None);
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── Persons ──
        private async Task RegisterPersonRoutes()
        {
            await service.CreateAPIRoute("/omniscience/persons", async req =>
            {
                try
                {
                    string? search = req.userParameters?["search"];
                    string? platform = req.userParameters?["platform"];
                    string? relatedTo = req.userParameters?["relatedTo"];
                    int.TryParse(req.userParameters?["limit"] ?? "100", out int limit);
                    if (limit <= 0 || limit > 500) limit = 100;
                    int.TryParse(req.userParameters?["offset"] ?? "0", out int offset);
                    if (offset < 0) offset = 0;

                    var rows = new List<JObject>();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    var where = new List<string> { "p.merged_into_person_id IS NULL" };
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        // Search across: person display_name, identity username/display_name,
                        // alt-names (nicknames + global-name aliases), and the social_graph
                        // analytic payload so users can be located by their relationships.
                        where.Add(@"(
                            p.display_name LIKE $s
                            OR EXISTS (SELECT 1 FROM platform_identities pi WHERE pi.person_id=p.person_id AND (pi.platform_username LIKE $s OR pi.display_name LIKE $s))
                            OR EXISTS (SELECT 1 FROM identity_alt_names an JOIN platform_identities pi ON pi.identity_id=an.identity_id WHERE pi.person_id=p.person_id AND an.alt_name LIKE $s)
                            OR EXISTS (SELECT 1 FROM person_statistics ps WHERE ps.person_id=p.person_id AND ps.module_name IN ('social_graph','mention_affinity') AND ps.payload_json LIKE $s)
                        )");
                        cmd.Parameters.AddWithValue("$s", "%" + search + "%");
                    }
                    if (!string.IsNullOrWhiteSpace(platform))
                    {
                        where.Add("EXISTS (SELECT 1 FROM platform_identities pi WHERE pi.person_id=p.person_id AND pi.platform=$pl)");
                        cmd.Parameters.AddWithValue("$pl", platform);
                    }
                    if (!string.IsNullOrWhiteSpace(relatedTo))
                    {
                        // Find people whose ID appears in the target's social_graph or
                        // mention_affinity payload, OR vice-versa. Substring match on the
                        // person_id key keeps the SQL simple and fast.
                        where.Add(@"EXISTS (
                            SELECT 1 FROM person_statistics ps
                            WHERE ps.person_id=$rel
                              AND ps.module_name IN ('social_graph','mention_affinity')
                              AND ps.payload_json LIKE '%' || p.person_id || '%')");
                        cmd.Parameters.AddWithValue("$rel", relatedTo);
                    }
                        cmd.CommandText = $@"SELECT p.person_id, p.display_name, p.created_at, p.updated_at,
                            (SELECT COUNT(*) FROM messages m JOIN platform_identities pi ON pi.identity_id=m.author_identity_id WHERE pi.person_id=p.person_id) AS msg_count,
                            (SELECT GROUP_CONCAT(platform || ':' || COALESCE(platform_username,''), '|') FROM platform_identities WHERE person_id=p.person_id) AS handles,
                            (SELECT json_group_array(json_object('platform', platform, 'username', platform_username, 'display_name', display_name)) FROM platform_identities WHERE person_id=p.person_id) AS idents_json,
                            EXISTS(SELECT 1 FROM person_profile_targets t WHERE t.person_id=p.person_id AND t.enabled=1) AS profile_targeted
                        FROM persons p
                        WHERE {string.Join(" AND ", where)}
                        ORDER BY msg_count DESC
                        LIMIT {limit} OFFSET {offset}";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        JArray idents;
                        try { idents = string.IsNullOrEmpty(r.IsDBNull(6) ? null : r.GetString(6)) ? new JArray() : JArray.Parse(r.GetString(6)); }
                        catch { idents = new JArray(); }
                        rows.Add(new JObject(
                            new JProperty("person_id", r.GetString(0)),
                            new JProperty("display_name", r.IsDBNull(1) ? "" : r.GetString(1)),
                            new JProperty("created_at", r.GetInt64(2)),
                            new JProperty("updated_at", r.GetInt64(3)),
                            new JProperty("message_count", r.GetInt32(4)),
                            new JProperty("handles", r.IsDBNull(5) ? "" : r.GetString(5)),
                            new JProperty("identities", idents),
                            new JProperty("profile_targeted", r.GetInt32(7) != 0)
                        ));
                    }
                    await req.ReturnResponse(new JArray(rows).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/get", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (string.IsNullOrWhiteSpace(personId))
                    {
                        await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var dossier = await BuildDossier(personId);
                    if (dossier == null)
                    {
                        await req.ReturnResponse("not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    await req.ReturnResponse(dossier.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/messages", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    int.TryParse(req.userParameters?["limit"] ?? "200", out int limit);
                    if (limit <= 0 || limit > 1000) limit = 200;
                    int.TryParse(req.userParameters?["offset"] ?? "0", out int offset);
                    if (offset < 0) offset = 0;

                    using var conn = service.Db.Open();
                    var idents = Analytics.AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                    if (idents.Count == 0)
                    {
                        await req.ReturnResponse("[]");
                        return;
                    }
                    string inC = string.Join(",", idents.Select((_, i) => "$i" + i));
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"SELECT m.message_id, m.conversation_id, m.sent_at, m.content, c.title, c.kind, c.guild_name
                        FROM messages m LEFT JOIN conversations c ON c.conversation_id = m.conversation_id
                        WHERE m.author_identity_id IN ({inC})
                        ORDER BY m.sent_at DESC LIMIT {limit} OFFSET {offset}";
                    int idx = 0; foreach (var i in idents) cmd.Parameters.AddWithValue("$i" + idx++, i);
                    using var r = cmd.ExecuteReader();
                    var arr = new JArray();
                    while (r.Read())
                    {
                        arr.Add(new JObject(
                            new JProperty("message_id", r.GetString(0)),
                            new JProperty("conversation_id", r.GetString(1)),
                            new JProperty("sent_at", r.GetInt64(2)),
                            new JProperty("content", r.IsDBNull(3) ? "" : r.GetString(3)),
                            new JProperty("conversation_title", r.IsDBNull(4) ? null : r.GetString(4)),
                            new JProperty("conversation_kind", r.IsDBNull(5) ? null : r.GetString(5)),
                            new JProperty("guild_name", r.IsDBNull(6) ? null : r.GetString(6))
                        ));
                    }
                    await req.ReturnResponse(arr.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/recompute", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (string.IsNullOrWhiteSpace(personId))
                    {
                        await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var started = await service.Scheduler.StartPersonRecomputeAsync(personId, CancellationToken.None);
                    if (!started.Accepted)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = false, accepted = false, run_id = started.RunId, message = started.Message }), code: HttpStatusCode.Conflict);
                        return;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true, accepted = true, run_id = started.RunId, message = started.Message }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/profile-targets", async req =>
            {
                try
                {
                    await req.ReturnResponse(service.Scheduler.GetProfileTargetsJson().ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/profile-targets/set", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (string.IsNullOrWhiteSpace(personId))
                    {
                        await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    bool enabled = !string.Equals(req.userParameters?["enabled"], "false", StringComparison.OrdinalIgnoreCase)
                        && req.userParameters?["enabled"] != "0";
                    await service.Scheduler.SetProfileTargetAsync(personId, enabled, CancellationToken.None);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true, person_id = personId, enabled }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/merge", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}");
                    string keep = (string?)body?.keep_person_id ?? "";
                    string drop = (string?)body?.merge_person_id ?? "";
                    if (string.IsNullOrWhiteSpace(keep) || string.IsNullOrWhiteSpace(drop) || keep == drop)
                    {
                        await req.ReturnResponse("keep_person_id and merge_person_id required and distinct", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    await service.Db.WriteLock.WaitAsync();
                    try
                    {
                        using var conn = service.Db.Open();
                        using var tx = conn.BeginTransaction();
                        using var c1 = conn.CreateCommand();
                        c1.Transaction = tx;
                        c1.CommandText = "UPDATE platform_identities SET person_id=$keep WHERE person_id=$drop";
                        c1.Parameters.AddWithValue("$keep", keep);
                        c1.Parameters.AddWithValue("$drop", drop);
                        c1.ExecuteNonQuery();

                        using var c2 = conn.CreateCommand();
                        c2.Transaction = tx;
                        c2.CommandText = "UPDATE persons SET merged_into_person_id=$keep, updated_at=$now WHERE person_id=$drop";
                        c2.Parameters.AddWithValue("$keep", keep);
                        c2.Parameters.AddWithValue("$drop", drop);
                        c2.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        c2.ExecuteNonQuery();

                        // Drop old person_statistics for the deprecated id (we'll recompute on the kept id).
                        using var c3 = conn.CreateCommand();
                        c3.Transaction = tx;
                        c3.CommandText = "DELETE FROM person_statistics WHERE person_id=$drop";
                        c3.Parameters.AddWithValue("$drop", drop);
                        c3.ExecuteNonQuery();

                        tx.Commit();
                    }
                    finally { service.Db.WriteLock.Release(); }
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── Conversations ──
        private async Task RegisterConversationRoutes()
        {
            await service.CreateAPIRoute("/omniscience/conversations", async req =>
            {
                try
                {
                    int.TryParse(req.userParameters?["limit"] ?? "200", out int limit);
                    if (limit <= 0 || limit > 1000) limit = 200;
                    string? platform = req.userParameters?["platform"];
                    string? kind = req.userParameters?["kind"];

                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    var where = new List<string>();
                    if (!string.IsNullOrWhiteSpace(platform)) { where.Add("c.platform=$pl"); cmd.Parameters.AddWithValue("$pl", platform); }
                    if (!string.IsNullOrWhiteSpace(kind)) { where.Add("c.kind=$k"); cmd.Parameters.AddWithValue("$k", kind); }
                    string whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
                    cmd.CommandText = $@"SELECT c.conversation_id, c.platform, c.kind, c.guild_name, c.title, c.first_seen, c.last_seen,
                            (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=c.conversation_id) AS msg_count
                        FROM conversations c {whereSql}
                        ORDER BY msg_count DESC LIMIT {limit}";
                    using var r = cmd.ExecuteReader();
                    var arr = new JArray();
                    while (r.Read())
                    {
                        arr.Add(new JObject(
                            new JProperty("conversation_id", r.GetString(0)),
                            new JProperty("platform", r.GetString(1)),
                            new JProperty("kind", r.GetString(2)),
                            new JProperty("guild_name", r.IsDBNull(3) ? null : r.GetString(3)),
                            new JProperty("title", r.IsDBNull(4) ? null : r.GetString(4)),
                            new JProperty("first_seen", r.IsDBNull(5) ? (long?)null : r.GetInt64(5)),
                            new JProperty("last_seen", r.IsDBNull(6) ? (long?)null : r.GetInt64(6)),
                            new JProperty("message_count", r.GetInt32(7))
                        ));
                    }
                    await req.ReturnResponse(arr.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/conversations/messages", async req =>
            {
                try
                {
                    string convId = req.userParameters?["conversationId"] ?? "";
                    if (string.IsNullOrWhiteSpace(convId))
                    {
                        await req.ReturnResponse("conversationId required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    int.TryParse(req.userParameters?["limit"] ?? "200", out int limit);
                    if (limit <= 0 || limit > 1000) limit = 200;
                    int.TryParse(req.userParameters?["offset"] ?? "0", out int offset);
                    if (offset < 0) offset = 0;

                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT m.message_id, m.sent_at, m.content, m.author_identity_id,
                            pi.platform_username, pi.display_name, pi.person_id
                        FROM messages m LEFT JOIN platform_identities pi ON pi.identity_id=m.author_identity_id
                        WHERE m.conversation_id=$c
                        ORDER BY m.sent_at DESC LIMIT $l OFFSET $o";
                    cmd.Parameters.AddWithValue("$c", convId);
                    cmd.Parameters.AddWithValue("$l", limit);
                    cmd.Parameters.AddWithValue("$o", offset);
                    using var r = cmd.ExecuteReader();
                    var arr = new JArray();
                    while (r.Read())
                    {
                        arr.Add(new JObject(
                            new JProperty("message_id", r.GetString(0)),
                            new JProperty("sent_at", r.GetInt64(1)),
                            new JProperty("content", r.IsDBNull(2) ? "" : r.GetString(2)),
                            new JProperty("author_identity_id", r.GetString(3)),
                            new JProperty("author_username", r.IsDBNull(4) ? null : r.GetString(4)),
                            new JProperty("author_display_name", r.IsDBNull(5) ? null : r.GetString(5)),
                            new JProperty("author_person_id", r.IsDBNull(6) ? null : r.GetString(6))
                        ));
                    }
                    await req.ReturnResponse(arr.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        // ── Stats ──
        private async Task RegisterStatRoutes()
        {
            await service.CreateAPIRoute("/omniscience/stats/overview", async req =>
            {
                try
                {
                    using var conn = service.Db.Open();
                    long Scalar(string sql)
                    {
                        using var c = conn.CreateCommand();
                        c.CommandText = sql;
                        var v = c.ExecuteScalar();
                        return v == null || v is DBNull ? 0L : Convert.ToInt64(v);
                    }
                    var obj = new JObject(
                        new JProperty("persons", Scalar("SELECT COUNT(*) FROM persons WHERE merged_into_person_id IS NULL")),
                        new JProperty("identities", Scalar("SELECT COUNT(*) FROM platform_identities")),
                        new JProperty("conversations", Scalar("SELECT COUNT(*) FROM conversations")),
                        new JProperty("messages", Scalar("SELECT COUNT(*) FROM messages")),
                        new JProperty("attachments", Scalar("SELECT COUNT(*) FROM attachments")),
                        new JProperty("sources", Scalar("SELECT COUNT(*) FROM harvest_sources")),
                        new JProperty("personality_profiles", Scalar("SELECT COUNT(DISTINCT person_id) FROM personality_profiles"))
                    );
                    await req.ReturnResponse(obj.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);
        }

        // ── Schedule control ──
        private async Task RegisterScheduleRoutes()
        {
            await service.CreateAPIRoute("/omniscience/schedule/status", async req =>
            {
                try
                {
                    await req.ReturnResponse(service.Scheduler.BuildStatusJson().ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/schedule/run-now", async req =>
            {
                try
                {
                    // fire-and-forget; UI polls /schedule/status
                    _ = Task.Run(() => service.Scheduler.RunNowAsync(CancellationToken.None));
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        // ── helpers ──
        private async Task<JObject?> BuildDossier(string personId)
        {
            using var conn = service.Db.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT person_id, display_name, notes, created_at, updated_at FROM persons WHERE person_id=$p AND merged_into_person_id IS NULL";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                var personObj = new JObject(
                    new JProperty("person_id", r.GetString(0)),
                    new JProperty("display_name", r.IsDBNull(1) ? "" : r.GetString(1)),
                    new JProperty("notes", r.IsDBNull(2) ? null : r.GetString(2)),
                    new JProperty("created_at", r.GetInt64(3)),
                    new JProperty("updated_at", r.GetInt64(4))
                );
                r.Close();

                // identities
                var idents = new JArray();
                using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = @"SELECT identity_id, platform, platform_user_id, platform_username, display_name, avatar_path, bio, first_seen, last_seen
                        FROM platform_identities WHERE person_id=$p OR person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)";
                    cmd2.Parameters.AddWithValue("$p", personId);
                    using var ir = cmd2.ExecuteReader();
                    while (ir.Read())
                    {
                        idents.Add(new JObject(
                            new JProperty("identity_id", ir.GetString(0)),
                            new JProperty("platform", ir.GetString(1)),
                            new JProperty("platform_user_id", ir.GetString(2)),
                            new JProperty("platform_username", ir.IsDBNull(3) ? null : ir.GetString(3)),
                            new JProperty("display_name", ir.IsDBNull(4) ? null : ir.GetString(4)),
                            new JProperty("avatar_path", ir.IsDBNull(5) ? null : ir.GetString(5)),
                            new JProperty("bio", ir.IsDBNull(6) ? null : ir.GetString(6)),
                            new JProperty("first_seen", ir.IsDBNull(7) ? (long?)null : ir.GetInt64(7)),
                            new JProperty("last_seen", ir.IsDBNull(8) ? (long?)null : ir.GetInt64(8))
                        ));
                    }
                }
                personObj["identities"] = idents;
                personObj["profile_targeted"] = service.Scheduler.IsProfileTarget(personId);

                // analytics bundle
                var analytics = new JObject();
                using (var cmd3 = conn.CreateCommand())
                {
                    cmd3.CommandText = "SELECT module_name, module_version, computed_at, payload_json FROM person_statistics WHERE person_id=$p";
                    cmd3.Parameters.AddWithValue("$p", personId);
                    using var ar = cmd3.ExecuteReader();
                    while (ar.Read())
                    {
                        var name = ar.GetString(0);
                        JToken payload;
                        try { payload = JToken.Parse(ar.GetString(3)); } catch { payload = ar.GetString(3); }
                        analytics[name] = new JObject(
                            new JProperty("version", ar.GetInt32(1)),
                            new JProperty("computed_at", ar.GetInt64(2)),
                            new JProperty("payload", payload)
                        );
                    }
                }
                personObj["analytics"] = analytics;

                // latest profile
                using (var cmd4 = conn.CreateCommand())
                {
                    cmd4.CommandText = @"SELECT generated_at, model_used, profile_markdown, traits_json, biographical_markdown FROM personality_profiles
                        WHERE person_id=$p ORDER BY generated_at DESC LIMIT 1";
                    cmd4.Parameters.AddWithValue("$p", personId);
                    using var pr = cmd4.ExecuteReader();
                    if (pr.Read())
                    {
                        JToken traits;
                        try { traits = JToken.Parse(pr.GetString(3)); } catch { traits = pr.IsDBNull(3) ? "" : pr.GetString(3); }
                        personObj["personality_profile"] = new JObject(
                            new JProperty("generated_at", pr.GetInt64(0)),
                            new JProperty("model", pr.IsDBNull(1) ? null : pr.GetString(1)),
                            new JProperty("narrative_markdown", pr.IsDBNull(2) ? "" : pr.GetString(2)),
                            new JProperty("traits", traits),
                            new JProperty("biographical_markdown", pr.IsDBNull(4) ? null : pr.GetString(4))
                        );
                    }
                    else
                    {
                        personObj["personality_profile"] = null;
                    }
                }

                // alt-names (nicknames, divergent global names) for the dossier
                using (var cmdAn = conn.CreateCommand())
                {
                    cmdAn.CommandText = @"SELECT DISTINCT an.alt_name, an.source
                        FROM identity_alt_names an
                        JOIN platform_identities pi ON pi.identity_id = an.identity_id
                        WHERE pi.person_id=$p OR pi.person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)
                        ORDER BY an.last_seen DESC LIMIT 30";
                    cmdAn.Parameters.AddWithValue("$p", personId);
                    var arr = new JArray();
                    try
                    {
                        using var ar = cmdAn.ExecuteReader();
                        while (ar.Read())
                        {
                            arr.Add(new JObject(
                                new JProperty("alt_name", ar.GetString(0)),
                                new JProperty("source", ar.IsDBNull(1) ? null : ar.GetString(1))
                            ));
                        }
                    }
                    catch { /* table may not exist on very old databases */ }
                    personObj["alt_names"] = arr;
                }

                // message volume
                using (var cmd5 = conn.CreateCommand())
                {
                    cmd5.CommandText = @"SELECT COUNT(*) FROM messages m
                        JOIN platform_identities pi ON pi.identity_id=m.author_identity_id
                        WHERE pi.person_id=$p OR pi.person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)";
                    cmd5.Parameters.AddWithValue("$p", personId);
                    personObj["message_count"] = Convert.ToInt32(cmd5.ExecuteScalar());
                }

                return personObj;
            }
        }

        private static async Task Err(UserRequest req, Exception ex)
        {
            await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                code: HttpStatusCode.InternalServerError);
        }
    }
#pragma warning restore CS4014
}
