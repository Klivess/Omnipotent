using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience.Deduction
{
#pragma warning disable CS4014
    /// <summary>
    /// API surface for the Deduction Engine + targeting: knowledge panel (facts,
    /// relationships, aliases, open questions, completeness), profile changelogs,
    /// Big Five series, the human-in-the-loop review queue, tracking tiers and the
    /// target-suggestion queue.
    /// </summary>
    public class DeductionRoutes
    {
        private readonly Omniscience service;

        public DeductionRoutes(Omniscience service) { this.service = service; }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/omniscience/persons/facts", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    await req.ReturnResponse(BuildFactsPayload(personId));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/relationships", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    await req.ReturnResponse(BuildRelationshipsPayload(personId));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/aliases", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    var resolver = new AliasResolver(service, service.Db);
                    var conclusions = resolver.ComputeAliasConclusions(personId);
                    await req.ReturnResponse(new JObject(
                        new JProperty("aliases", new JArray(conclusions.OrderByDescending(c => c.Confidence).Select(c => new JObject(
                            new JProperty("name", c.Name),
                            new JProperty("kind", c.Kind),
                            new JProperty("confidence", Math.Round(c.Confidence, 2)),
                            new JProperty("uses", c.UsageCount),
                            new JProperty("distinct_speakers", c.DistinctSpeakers),
                            new JProperty("evidence_message_ids", new JArray(c.EvidenceMessageIds))))))).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/open-questions", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    await req.ReturnResponse(BuildOpenQuestionsPayload(personId));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/changelog", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT generated_at, changes_markdown FROM profile_changelogs
                        WHERE person_id=$p ORDER BY generated_at DESC LIMIT 20";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        arr.Add(new JObject(
                            new JProperty("generated_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).UtcDateTime.ToString("o")),
                            new JProperty("changes_markdown", r.GetString(1))));
                    await req.ReturnResponse(new JObject(new JProperty("changelogs", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/bigfive-series", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT generated_at, traits_json FROM personality_profiles
                        WHERE person_id=$p ORDER BY generated_at ASC LIMIT 100";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        try
                        {
                            var traits = JObject.Parse(r.GetString(1));
                            if (traits["big_five_estimate"] is JObject b5)
                                arr.Add(new JObject(
                                    new JProperty("generated_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).UtcDateTime.ToString("o")),
                                    new JProperty("big_five", b5)));
                        }
                        catch { }
                    }
                    await req.ReturnResponse(new JObject(new JProperty("series", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/review/queue", async req =>
            {
                try { await req.ReturnResponse(BuildReviewQueuePayload()); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/review/resolve", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string suggestionId = body.Value<string>("suggestionId") ?? "";
                    string action = body.Value<string>("action") ?? "";
                    if (suggestionId.Length == 0 || action is not ("accept" or "reject"))
                    { await req.ReturnResponse("suggestionId and action(accept|reject) required", code: HttpStatusCode.BadRequest); return; }
                    await ResolveMergeSuggestionAsync(suggestionId, action == "accept");
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/targets/suggestions", async req =>
            {
                try { await req.ReturnResponse(BuildTargetSuggestionsPayload()); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/targets/dismiss", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    await service.Db.WriteLock.WaitAsync();
                    try
                    {
                        using var conn = service.Db.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "UPDATE target_suggestions SET dismissed=1 WHERE person_id=$p";
                        cmd.Parameters.AddWithValue("$p", personId);
                        cmd.ExecuteNonQuery();
                    }
                    finally { service.Db.WriteLock.Release(); }
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/tier-set", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    string tier = (body.Value<string>("tier") ?? "").ToLowerInvariant();
                    if (personId.Length == 0 || tier is not ("tracked" or "watch" or "archive"))
                    { await req.ReturnResponse("personId and tier(tracked|watch|archive) required", code: HttpStatusCode.BadRequest); return; }
                    await SetTierAsync(personId, tier);
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/profile-era", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    int era = body.Value<int?>("era") ?? 0;
                    if (personId.Length == 0 || era < 2010 || era > DateTime.UtcNow.Year)
                    { await req.ReturnResponse("personId and era(year) required", code: HttpStatusCode.BadRequest); return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool ok = await service.Profiler.GenerateEraProfileAsync(personId, era, CancellationToken.None);
                            _ = service.ServiceLog($"[Omniscience] Era profile {era} for {personId}: {(ok ? "ok" : "skipped (too sparse)")}");
                        }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Era profile failed"); }
                    });
                    await req.ReturnResponse("{\"ok\":true,\"message\":\"era profile queued\"}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/era-profiles", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT era, generated_at, profile_markdown, traits_json FROM personality_profiles
                        WHERE person_id=$p AND era IS NOT NULL ORDER BY era ASC, generated_at DESC";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    var seen = new HashSet<string>();
                    while (r.Read())
                    {
                        string era = r.GetString(0);
                        if (!seen.Add(era)) continue; // newest generation per era
                        arr.Add(new JObject(
                            new JProperty("era", era),
                            new JProperty("generated_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).UtcDateTime.ToString("o")),
                            new JProperty("profile_markdown", r.IsDBNull(2) ? "" : r.GetString(2)),
                            new JProperty("traits_json", r.IsDBNull(3) ? "{}" : r.GetString(3))));
                    }
                    await req.ReturnResponse(new JObject(new JProperty("eras", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/radar/alerts", async req =>
            {
                try
                {
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT matched_alias, author_display, channel_label, snippet, occurred_at, notified
                        FROM radar_alerts ORDER BY occurred_at DESC LIMIT 100";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        arr.Add(new JObject(
                            new JProperty("matched_alias", r.GetString(0)),
                            new JProperty("author_display", r.IsDBNull(1) ? "" : r.GetString(1)),
                            new JProperty("channel_label", r.IsDBNull(2) ? "" : r.GetString(2)),
                            new JProperty("snippet", r.IsDBNull(3) ? "" : r.GetString(3)),
                            new JProperty("occurred_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime.ToString("o")),
                            new JProperty("notified", r.GetInt32(5) == 1)));
                    await req.ReturnResponse(new JObject(
                        new JProperty("aliases_watched", new JArray(service.Radar.CurrentAliases)),
                        new JProperty("alerts", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/identity-links", async req =>
            {
                try
                {
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT s.suggestion_id, s.person_id_a, COALESCE(pa.display_name,''),
                               s.person_id_b, COALESCE(pb.display_name,''), s.score, s.reason
                        FROM person_link_suggestions s
                        LEFT JOIN persons pa ON pa.person_id = s.person_id_a
                        LEFT JOIN persons pb ON pb.person_id = s.person_id_b
                        WHERE s.status='pending'
                          AND pa.merged_into_person_id IS NULL AND pb.merged_into_person_id IS NULL
                        ORDER BY s.score DESC LIMIT 50";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        arr.Add(new JObject(
                            new JProperty("suggestion_id", r.GetString(0)),
                            new JProperty("person_id_a", r.GetString(1)),
                            new JProperty("display_a", r.GetString(2)),
                            new JProperty("person_id_b", r.GetString(3)),
                            new JProperty("display_b", r.GetString(4)),
                            new JProperty("score", r.GetDouble(5)),
                            new JProperty("reason", r.IsDBNull(6) ? "" : r.GetString(6))));
                    await req.ReturnResponse(new JObject(new JProperty("links", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/identity-links/resolve", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string suggestionId = body.Value<string>("suggestionId") ?? "";
                    string action = body.Value<string>("action") ?? "";
                    if (suggestionId.Length == 0 || action is not ("accept" or "reject"))
                    { await req.ReturnResponse("suggestionId and action(accept|reject) required", code: HttpStatusCode.BadRequest); return; }
                    await ResolveLinkSuggestionAsync(suggestionId, action == "accept");
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/persons/observe", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    string observation = (body.Value<string>("observation") ?? "").Trim();
                    string category = (body.Value<string>("category") ?? "misc").Trim();
                    if (personId.Length == 0 || observation.Length < 3)
                    { await req.ReturnResponse("personId and observation required", code: HttpStatusCode.BadRequest); return; }

                    // HUMINT: Klives' own observations are first-class, high-confidence evidence.
                    await service.Db.WriteLock.WaitAsync();
                    try
                    {
                        using var conn = service.Db.Open();
                        using var tx = conn.BeginTransaction();
                        GraphAssembler.UpsertFact(conn, tx, personId, category, observation, 0.92, "manual",
                            new JArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "manual", null);
                        tx.Commit();
                    }
                    finally { service.Db.WriteLock.Release(); }
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/watchlists", async req =>
            {
                try
                {
                    var arr = new JArray();
                    using var conn = service.Db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT w.watch_id, w.label, w.terms, w.person_id, COALESCE(p.display_name,''), w.enabled, w.notify, w.created_at
                        FROM watchlists w LEFT JOIN persons p ON p.person_id = w.person_id
                        ORDER BY w.created_at DESC";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        arr.Add(new JObject(
                            new JProperty("watch_id", r.GetString(0)),
                            new JProperty("label", r.GetString(1)),
                            new JProperty("terms", r.GetString(2)),
                            new JProperty("person_id", r.IsDBNull(3) ? null : r.GetString(3)),
                            new JProperty("person_display", r.GetString(4)),
                            new JProperty("enabled", r.GetInt32(5) == 1),
                            new JProperty("notify", r.GetInt32(6) == 1)));
                    await req.ReturnResponse(new JObject(new JProperty("watchlists", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/watchlists/add", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string label = (body.Value<string>("label") ?? "").Trim();
                    string terms = (body.Value<string>("terms") ?? "").Trim();
                    if (label.Length == 0 || terms.Length == 0)
                    { await req.ReturnResponse("label and terms required", code: HttpStatusCode.BadRequest); return; }
                    await service.Db.WriteLock.WaitAsync();
                    try
                    {
                        using var conn = service.Db.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"INSERT INTO watchlists(watch_id, label, terms, person_id, notify, created_at)
                            VALUES($id,$l,$t,$p,$n,$now)";
                        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                        cmd.Parameters.AddWithValue("$l", label);
                        cmd.Parameters.AddWithValue("$t", terms);
                        cmd.Parameters.AddWithValue("$p", (object?)body.Value<string>("personId") ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("$n", (body.Value<bool?>("notify") ?? true) ? 1 : 0);
                        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        cmd.ExecuteNonQuery();
                    }
                    finally { service.Db.WriteLock.Release(); }
                    service.Radar.RefreshWatchlists();
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/watchlists/remove", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string watchId = body.Value<string>("watchId") ?? "";
                    if (watchId.Length == 0) { await req.ReturnResponse("watchId required", code: HttpStatusCode.BadRequest); return; }
                    await service.Db.WriteLock.WaitAsync();
                    try
                    {
                        using var conn = service.Db.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "DELETE FROM watchlists WHERE watch_id=$id";
                        cmd.Parameters.AddWithValue("$id", watchId);
                        cmd.ExecuteNonQuery();
                    }
                    finally { service.Db.WriteLock.Release(); }
                    service.Radar.RefreshWatchlists();
                    await req.ReturnResponse("{\"ok\":true}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/deduction/status", async req =>
            {
                try { await req.ReturnResponse(BuildDeductionStatusPayload()); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/deduction/run", async req =>
            {
                try
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await service.Extraction.RunPassAsync(CancellationToken.None);
                            await service.Graph.RunAsync(CancellationToken.None);
                            await service.Aliases.RunAsync(CancellationToken.None);
                            await service.TargetSuggestions.RunAsync(CancellationToken.None);
                            await service.IdentityLinks.RunAsync(CancellationToken.None);
                        }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Manual deduction run failed"); }
                    });
                    await req.ReturnResponse("{\"ok\":true,\"message\":\"deduction pass queued\"}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            try { await req.ReturnResponse(new JObject(new JProperty("error", ex.Message)).ToString(Formatting.None), code: HttpStatusCode.InternalServerError); }
            catch { }
        }

        // ── Payload builders ──

        private string BuildFactsPayload(string personId)
        {
            var facts = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT fact_id, category, fact_text, confidence, status, source_context,
                       first_evidence_at, last_evidence_at, evidence_message_ids_json, derived_from_json, extracted_by
                FROM person_facts WHERE person_id=$p AND status IN ('active','superseded','contradicted')
                ORDER BY status='active' DESC, category, confidence DESC LIMIT 300";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                facts.Add(new JObject(
                    new JProperty("fact_id", r.GetString(0)),
                    new JProperty("category", r.GetString(1)),
                    new JProperty("fact", r.GetString(2)),
                    new JProperty("confidence", Math.Round(r.GetDouble(3), 2)),
                    new JProperty("status", r.GetString(4)),
                    new JProperty("source_context", r.IsDBNull(5) ? null : r.GetString(5)),
                    new JProperty("first_evidence_at", r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)).UtcDateTime.ToString("o")),
                    new JProperty("last_evidence_at", r.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)).UtcDateTime.ToString("o")),
                    new JProperty("evidence_message_ids", TryParseArray(r.IsDBNull(8) ? null : r.GetString(8))),
                    new JProperty("derivation", r.IsDBNull(9) ? null : r.GetString(9)),
                    new JProperty("extracted_by", r.GetString(10))));
            }
            return new JObject(new JProperty("facts", facts)).ToString(Formatting.None);
        }

        private string BuildRelationshipsPayload(string personId)
        {
            var rels = new JArray();
            var entities = new JArray();
            using var conn = service.Db.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT er.rel_type, er.confidence, er.evidence_count, er.valid_from, er.valid_to,
                           er.related_person_id, COALESCE(p.display_name,''), er.entity_id, COALESCE(e.canonical_name,'')
                    FROM entity_relationships er
                    LEFT JOIN persons p ON p.person_id = er.related_person_id
                    LEFT JOIN entities e ON e.entity_id = er.entity_id
                    WHERE er.owner_person_id=$p AND er.status='active'
                    ORDER BY er.evidence_count DESC LIMIT 60";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rels.Add(new JObject(
                        new JProperty("rel_type", r.GetString(0)),
                        new JProperty("confidence", Math.Round(r.GetDouble(1), 2)),
                        new JProperty("evidence_count", r.GetInt32(2)),
                        new JProperty("valid_from", r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).UtcDateTime.ToString("yyyy-MM")),
                        new JProperty("valid_to", r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime.ToString("yyyy-MM")),
                        new JProperty("related_person_id", r.IsDBNull(5) ? null : r.GetString(5)),
                        new JProperty("related_display", !r.IsDBNull(5) ? r.GetString(6) : r.GetString(8))));
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT kind, canonical_name, descriptor, mention_count, linked_person_id FROM entities
                    WHERE owner_person_id=$p ORDER BY mention_count DESC LIMIT 80";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    entities.Add(new JObject(
                        new JProperty("kind", r.GetString(0)),
                        new JProperty("name", r.GetString(1)),
                        new JProperty("descriptor", r.IsDBNull(2) ? null : r.GetString(2)),
                        new JProperty("mentions", r.GetInt32(3)),
                        new JProperty("linked_person_id", r.IsDBNull(4) ? null : r.GetString(4))));
                }
            }
            return new JObject(new JProperty("relationships", rels), new JProperty("entities", entities)).ToString(Formatting.None);
        }

        private string BuildOpenQuestionsPayload(string personId)
        {
            var questions = new JArray();
            var hypotheses = new JArray();
            JObject? completeness = null;
            using var conn = service.Db.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT slot, question FROM open_questions WHERE person_id=$p AND status='open' ORDER BY slot";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    questions.Add(new JObject(new JProperty("slot", r.GetString(0)), new JProperty("question", r.GetString(1))));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT statement, rationale, status, evidence_json, created_at FROM hypotheses
                    WHERE person_id=$p ORDER BY status='open' DESC, created_at DESC LIMIT 25";
                cmd.Parameters.AddWithValue("$p", personId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    hypotheses.Add(new JObject(
                        new JProperty("statement", r.GetString(0)),
                        new JProperty("rationale", r.IsDBNull(1) ? null : r.GetString(1)),
                        new JProperty("status", r.GetString(2)),
                        new JProperty("evidence_message_ids", TryParseArray(r.IsDBNull(3) ? null : r.GetString(3))),
                        new JProperty("created_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).UtcDateTime.ToString("o"))));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload_json FROM person_statistics WHERE person_id=$p AND module_name='deduction_summary'";
                cmd.Parameters.AddWithValue("$p", personId);
                if (cmd.ExecuteScalar() is string raw)
                    try { completeness = JObject.Parse(raw); } catch { }
            }
            return new JObject(
                new JProperty("open_questions", questions),
                new JProperty("hypotheses", hypotheses),
                new JProperty("completeness", completeness)).ToString(Formatting.None);
        }

        private string BuildReviewQueuePayload()
        {
            var merges = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT s.suggestion_id, s.owner_person_id, COALESCE(p.display_name,''),
                       ea.canonical_name, ea.kind, eb.canonical_name, s.score, s.reason, s.created_at
                FROM entity_merge_suggestions s
                LEFT JOIN persons p ON p.person_id = s.owner_person_id
                LEFT JOIN entities ea ON ea.entity_id = s.entity_id_a
                LEFT JOIN entities eb ON eb.entity_id = s.entity_id_b
                WHERE s.status='pending' ORDER BY s.score DESC, s.created_at DESC LIMIT 60";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                merges.Add(new JObject(
                    new JProperty("suggestion_id", r.GetString(0)),
                    new JProperty("owner_person_id", r.GetString(1)),
                    new JProperty("owner_display", r.GetString(2)),
                    new JProperty("entity_a", r.IsDBNull(3) ? "" : r.GetString(3)),
                    new JProperty("kind", r.IsDBNull(4) ? "" : r.GetString(4)),
                    new JProperty("entity_b", r.IsDBNull(5) ? "" : r.GetString(5)),
                    new JProperty("score", r.GetDouble(6)),
                    new JProperty("reason", r.IsDBNull(7) ? "" : r.GetString(7))));
            }
            return new JObject(new JProperty("merge_suggestions", merges)).ToString(Formatting.None);
        }

        private string BuildTargetSuggestionsPayload()
        {
            var arr = new JArray();
            using var conn = service.Db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT t.person_id, COALESCE(p.display_name,''), p.tier, t.score, t.reasons_json, t.computed_at
                FROM target_suggestions t
                LEFT JOIN persons p ON p.person_id = t.person_id
                WHERE t.dismissed=0 AND (p.tier IS NULL OR p.tier != 'tracked')
                ORDER BY t.score DESC LIMIT 20";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject(
                    new JProperty("person_id", r.GetString(0)),
                    new JProperty("display_name", r.GetString(1)),
                    new JProperty("tier", r.IsDBNull(2) ? "archive" : r.GetString(2)),
                    new JProperty("score", r.GetDouble(3)),
                    new JProperty("reasons", TryParseArray(r.GetString(4))),
                    new JProperty("computed_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).UtcDateTime.ToString("o"))));
            }
            return new JObject(new JProperty("suggestions", arr)).ToString(Formatting.None);
        }

        private string BuildDeductionStatusPayload()
        {
            using var conn = service.Db.Open();
            long Count(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            return new JObject(
                new JProperty("last_extraction_at", service.Extraction.LastPassAt?.ToString("o")),
                new JProperty("last_extraction_summary", service.Extraction.LastPassSummary),
                new JProperty("windows_extracted", Count("SELECT COUNT(*) FROM extraction_results WHERE status='ok'")),
                new JProperty("windows_pending_graph", Count("SELECT COUNT(*) FROM extraction_results WHERE graph_applied=0 AND status='ok'")),
                new JProperty("facts_active", Count("SELECT COUNT(*) FROM person_facts WHERE status='active'")),
                new JProperty("qa_pairs", Count("SELECT COUNT(*) FROM qa_pairs")),
                new JProperty("name_usages", Count("SELECT COUNT(*) FROM name_usages")),
                new JProperty("stimulus_reply_pairs", Count("SELECT COUNT(*) FROM stimulus_reply_pairs")),
                new JProperty("entities", Count("SELECT COUNT(*) FROM entities")),
                new JProperty("relationships", Count("SELECT COUNT(*) FROM entity_relationships WHERE status='active'")),
                new JProperty("open_hypotheses", Count("SELECT COUNT(*) FROM hypotheses WHERE status='open'")),
                new JProperty("tracked_persons", Count("SELECT COUNT(*) FROM persons WHERE tier='tracked'"))
            ).ToString(Formatting.None);
        }

        // ── Mutations ──

        // Accept = merge the two person records (keeping the lower id, the convention the
        // existing /persons/merge route uses) and recompute the kept person next pass.
        private async Task ResolveLinkSuggestionAsync(string suggestionId, bool accept)
        {
            string? a = null, b = null;
            await service.Db.WriteLock.WaitAsync();
            try
            {
                using var conn = service.Db.Open();
                using var tx = conn.BeginTransaction();
                using (var get = conn.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT person_id_a, person_id_b FROM person_link_suggestions WHERE suggestion_id=$id AND status='pending'";
                    get.Parameters.AddWithValue("$id", suggestionId);
                    using var r = get.ExecuteReader();
                    if (r.Read()) { a = r.GetString(0); b = r.GetString(1); }
                }

                if (accept && a != null && b != null)
                {
                    string keep = string.CompareOrdinal(a, b) <= 0 ? a : b;
                    string drop = keep == a ? b : a;
                    using (var c1 = conn.CreateCommand())
                    {
                        c1.Transaction = tx;
                        c1.CommandText = "UPDATE platform_identities SET person_id=$keep WHERE person_id=$drop";
                        c1.Parameters.AddWithValue("$keep", keep);
                        c1.Parameters.AddWithValue("$drop", drop);
                        c1.ExecuteNonQuery();
                    }
                    using (var c2 = conn.CreateCommand())
                    {
                        c2.Transaction = tx;
                        c2.CommandText = "UPDATE persons SET merged_into_person_id=$keep, updated_at=$now WHERE person_id=$drop";
                        c2.Parameters.AddWithValue("$keep", keep);
                        c2.Parameters.AddWithValue("$drop", drop);
                        c2.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        c2.ExecuteNonQuery();
                    }
                    using (var c3 = conn.CreateCommand())
                    {
                        c3.Transaction = tx;
                        c3.CommandText = "DELETE FROM person_statistics WHERE person_id=$drop";
                        c3.Parameters.AddWithValue("$drop", drop);
                        c3.ExecuteNonQuery();
                    }
                }

                using (var mark = conn.CreateCommand())
                {
                    mark.Transaction = tx;
                    mark.CommandText = "UPDATE person_link_suggestions SET status=$s WHERE suggestion_id=$id";
                    mark.Parameters.AddWithValue("$s", accept ? "accepted" : "rejected");
                    mark.Parameters.AddWithValue("$id", suggestionId);
                    mark.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private async Task ResolveMergeSuggestionAsync(string suggestionId, bool accept)
        {
            await service.Db.WriteLock.WaitAsync();
            try
            {
                using var conn = service.Db.Open();
                using var tx = conn.BeginTransaction();

                string? entityA = null, entityB = null;
                using (var get = conn.CreateCommand())
                {
                    get.Transaction = tx;
                    get.CommandText = "SELECT entity_id_a, entity_id_b FROM entity_merge_suggestions WHERE suggestion_id=$id AND status='pending'";
                    get.Parameters.AddWithValue("$id", suggestionId);
                    using var r = get.ExecuteReader();
                    if (r.Read()) { entityA = r.GetString(0); entityB = r.GetString(1); }
                }

                if (accept && entityA != null && entityB != null)
                {
                    // Fold B into A: combine mention counts/descriptors, repoint relationships.
                    using (var upd = conn.CreateCommand())
                    {
                        upd.Transaction = tx;
                        upd.CommandText = @"UPDATE entities SET
                                mention_count = mention_count + COALESCE((SELECT mention_count FROM entities WHERE entity_id=$b),0),
                                descriptor = COALESCE(descriptor,'') ||
                                    CASE WHEN (SELECT descriptor FROM entities WHERE entity_id=$b) IS NULL THEN ''
                                         ELSE '; ' || (SELECT descriptor FROM entities WHERE entity_id=$b) END
                            WHERE entity_id=$a";
                        upd.Parameters.AddWithValue("$a", entityA);
                        upd.Parameters.AddWithValue("$b", entityB);
                        upd.ExecuteNonQuery();
                    }
                    using (var repoint = conn.CreateCommand())
                    {
                        repoint.Transaction = tx;
                        repoint.CommandText = "UPDATE entity_relationships SET entity_id=$a WHERE entity_id=$b";
                        repoint.Parameters.AddWithValue("$a", entityA);
                        repoint.Parameters.AddWithValue("$b", entityB);
                        repoint.ExecuteNonQuery();
                    }
                    using (var del = conn.CreateCommand())
                    {
                        del.Transaction = tx;
                        del.CommandText = "DELETE FROM entities WHERE entity_id=$b";
                        del.Parameters.AddWithValue("$b", entityB);
                        del.ExecuteNonQuery();
                    }
                }

                using (var mark = conn.CreateCommand())
                {
                    mark.Transaction = tx;
                    mark.CommandText = "UPDATE entity_merge_suggestions SET status=$s WHERE suggestion_id=$id";
                    mark.Parameters.AddWithValue("$s", accept ? "accepted" : "rejected");
                    mark.Parameters.AddWithValue("$id", suggestionId);
                    mark.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { service.Db.WriteLock.Release(); }
        }

        private async Task SetTierAsync(string personId, string tier)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await service.Db.WriteLock.WaitAsync();
            try
            {
                using var conn = service.Db.Open();
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE persons SET tier=$t, updated_at=$now WHERE person_id=$p";
                    cmd.Parameters.AddWithValue("$t", tier);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.ExecuteNonQuery();
                }
                // Keep the profiler allow-list in sync: tracked ⇒ dossiers; otherwise not.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO person_profile_targets(person_id, enabled, added_at, updated_at)
                        VALUES($p,$e,$now,$now)
                        ON CONFLICT(person_id) DO UPDATE SET enabled=excluded.enabled, updated_at=excluded.updated_at";
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.Parameters.AddWithValue("$e", tier == "tracked" ? 1 : 0);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { service.Db.WriteLock.Release(); }
            // Promotion to tracked enqueues their history automatically: the embedding
            // indexer and extraction job both key off persons.tier on their next pass.
        }

        private static JArray TryParseArray(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new JArray();
            try { return JArray.Parse(json); } catch { return new JArray(); }
        }
    }
}
