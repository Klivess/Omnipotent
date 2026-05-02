using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI;
using System.Net;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// All <c>/omnidefence/*</c> HTTP routes. Every route requires
    /// <see cref="KMProfileManager.KMPermissions.Klives"/>.
    /// </summary>
    internal static class OmniDefenceRoutes
    {
        private const string DerivedRequestOriginSql = "COALESCE(NULLIF(request_origin, ''), CASE WHEN client_page IS NOT NULL AND client_page <> '' THEN CASE WHEN profile_id IS NOT NULL AND profile_id <> '' THEN 'WebsiteProfile' ELSE 'WebsiteNoProfile' END WHEN profile_id IS NOT NULL AND profile_id <> '' THEN 'DirectApiProfile' ELSE 'DirectApi' END)";
        private const string RequestSelectSql = "SELECT id, utc_ts, ip, method, route, query, status_code, duration_ms, profile_id, profile_name, profile_rank, perm_required, matched_route, body_hash, body_length, user_agent, deny_reason, " + DerivedRequestOriginSql + " AS request_origin, client_page FROM requests";

        public static async Task RegisterAsync(OmniDefence parent)
        {
            // Overview
            await parent.CreateAPIRoute("/omnidefence/overview", async req =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long day = now - 86400;
                long week = now - 86400 * 7;

                long reqs24h = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM requests WHERE utc_ts >= $t",
                    new() { ["$t"] = day });
                long totalRequests = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM requests",
                    new());
                long reqs7d = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM requests WHERE utc_ts >= $t",
                    new() { ["$t"] = week });
                long denied24h = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM requests WHERE utc_ts >= $t AND (deny_reason IS NOT NULL OR status_code >= 400)",
                    new() { ["$t"] = day });
                long totalDenied = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM requests WHERE deny_reason IS NOT NULL OR status_code >= 400",
                    new());
                long unauth24h = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM auth_events WHERE utc_ts >= $t AND type IN ('UnauthRoute','InsufficientClearance','InvalidPassword','WebsiteNoProfile','WebsiteInvalidProfile')",
                    new() { ["$t"] = day });
                long totalAuthEvents = await parent.Store.ScalarLongAsync(
                    "SELECT COUNT(*) FROM auth_events",
                    new());

                int blocked = parent.Tracker.All().Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Blocked));
                int watched = parent.Tracker.All().Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Watch));
                int honey = parent.Tracker.All().Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Honeypot));
                int tarpit = parent.Tracker.All().Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Tarpit));

                var topAttackers = parent.Tracker.All()
                    .Where(r => string.IsNullOrWhiteSpace(r.AssociatedProfileId))
                    .OrderByDescending(r => r.ThreatScore)
                    .Take(10)
                    .Select(r => new { ip = r.Ip, score = r.ThreatScore, status = r.Status, unauth = r.UnauthAttempts, country = r.Country, attacker = true });

                var topThreat = parent.Tracker.All()
                    .Where(r => string.IsNullOrWhiteSpace(r.AssociatedProfileId))
                    .OrderByDescending(r => r.ThreatScore)
                    .Select(r => new { ip = r.Ip, score = r.ThreatScore, status = r.Status, unauth = r.UnauthAttempts, country = r.Country, asn = r.Asn, attacker = true })
                    .FirstOrDefault();

                var topRoutes = await parent.Store.QueryAsync(
                    "SELECT route, COUNT(*) AS hits FROM requests WHERE utc_ts >= $t GROUP BY route ORDER BY hits DESC LIMIT 10",
                    new() { ["$t"] = day });

                var originBreakdown = await parent.Store.QueryAsync(
                    $"SELECT origin, COUNT(*) AS hits FROM (SELECT {DerivedRequestOriginSql} AS origin FROM requests WHERE utc_ts >= $t) GROUP BY origin ORDER BY hits DESC",
                    new() { ["$t"] = day });

                var resp = new
                {
                    requests24h = reqs24h,
                    totalRequests,
                    requests7d = reqs7d,
                    denied24h,
                    totalDenied,
                    authFailures24h = unauth24h,
                    unauth24h,
                    totalAuthEvents,
                    blockedIps = blocked,
                    watchedIps = watched,
                    honeypotIps = honey,
                    tarpitIps = tarpit,
                    totalIps = parent.Tracker.All().Count(),
                    knownIps = parent.Tracker.All().Count(),
                    topThreat,
                    topAttackers,
                    topRoutes,
                    originBreakdown
                };

                await req.ReturnResponse(JsonConvert.SerializeObject(resp), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Requests filtered query
            await parent.CreateAPIRoute("/omnidefence/requests", async req =>
            {
                var (sql, parameters) = BuildRequestsQuery(req);
                var rows = await parent.Store.QueryAsync(sql, parameters);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Auth events
            await parent.CreateAPIRoute("/omnidefence/auth-events", async req =>
            {
                var (sql, parameters) = BuildAuthEventsQuery(req);
                var rows = await parent.Store.QueryAsync(sql, parameters);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Profile actions
            await parent.CreateAPIRoute("/omnidefence/profile-actions", async req =>
            {
                var (sql, parameters) = BuildProfileActionsQuery(req);
                var rows = await parent.Store.QueryAsync(sql, parameters);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // IPs list
            await parent.CreateAPIRoute("/omnidefence/ips", async req =>
            {
                string? status = req.userParameters.Get("status");
                double minScore = double.TryParse(req.userParameters.Get("minScore"), out var ms) ? ms : 0;
                string? query = req.userParameters.Get("query") ?? req.userParameters.Get("q");
                int limit = int.TryParse(req.userParameters.Get("limit"), out var l) ? Math.Clamp(l, 1, 1000) : 200;
                int offset = int.TryParse(req.userParameters.Get("offset"), out var o) ? Math.Max(0, o) : 0;

                var rows = FilterIpRecords(parent.Tracker.All(), status, minScore, query, limit, offset);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // IP detail
            await parent.CreateAPIRoute("/omnidefence/ip", async req =>
            {
                string? ip = req.userParameters.Get("ip");
                if (string.IsNullOrWhiteSpace(ip))
                {
                    await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                var record = parent.Tracker.Get(ip) ?? await parent.Store.GetIpRecordAsync(ip);
                var recentReqs = await parent.Store.QueryAsync(
                    RequestSelectSql + " WHERE ip=$ip ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$ip"] = ip });
                var events = await parent.Store.QueryAsync(
                    "SELECT * FROM ip_events WHERE ip=$ip ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$ip"] = ip });
                var auth = await parent.Store.QueryAsync(
                    "SELECT * FROM auth_events WHERE ip=$ip ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$ip"] = ip });
                await req.ReturnResponse(JsonConvert.SerializeObject(new { record, recentRequests = recentReqs, events, auth }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Block
            await parent.CreateAPIRoute("/omnidefence/ip/block", async req =>
            {
                var body = ParseJsonBody(req);
                string ip = (body["ip"] as string ?? "").Trim();
                string reason = body["reason"] as string ?? "Manual block";
                if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                if (parent.IsLinkedToKlives(ip)) { await req.ReturnResponse("Refusing to block a Klives-linked IP", "text/plain", null, HttpStatusCode.Conflict); return; }
                var rec = parent.Tracker.GetOrCreate(ip);
                lock (rec) { rec.Status = nameof(IpThreatTracker.IpStatus.Blocked); rec.LastBlockReason = reason; }
                await parent.Tracker.PersistAsync(rec);
                await parent.RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = ip,
                    Kind = "Block",
                    ActorProfileId = req.user?.UserID,
                    ActorProfileName = req.user?.Name,
                    Detail = reason
                });
                _ = parent.SendBlockNotificationAsync(rec, req.user?.Name ?? "Unknown");
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Unblock
            await parent.CreateAPIRoute("/omnidefence/ip/unblock", async req =>
            {
                await ResetIpToNormalAsync(parent, req, "Unblock");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Untrap / release hostile statuses (Blocked / Tarpit / Honeypot)
            await parent.CreateAPIRoute("/omnidefence/ip/untrap", async req =>
            {
                await ResetIpToNormalAsync(parent, req, "Untrap");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Set status (Watch / Tarpit / Honeypot / Normal / Blocked)
            await parent.CreateAPIRoute("/omnidefence/ip/status", async req =>
            {
                var body = ParseJsonBody(req);
                string ip = (body["ip"] as string ?? "").Trim();
                string status = body["status"] as string ?? "";
                if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                if (!Enum.TryParse<IpThreatTracker.IpStatus>(status, true, out var parsed))
                {
                    await req.ReturnResponse("Invalid status", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                if ((parsed == IpThreatTracker.IpStatus.Blocked || parsed == IpThreatTracker.IpStatus.Tarpit || parsed == IpThreatTracker.IpStatus.Honeypot) && parent.IsLinkedToKlives(ip))
                {
                    await req.ReturnResponse("Refusing to apply hostile status to a Klives-linked IP", "text/plain", null, HttpStatusCode.Conflict);
                    return;
                }
                var rec = parent.Tracker.GetOrCreate(ip);
                lock (rec)
                {
                    rec.Status = parsed.ToString();
                    if (parsed != IpThreatTracker.IpStatus.Blocked) rec.LastBlockReason = null;
                }
                await parent.Tracker.PersistAsync(rec);
                await parent.RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = ip,
                    Kind = "StatusChange:" + parsed,
                    ActorProfileId = req.user?.UserID,
                    ActorProfileName = req.user?.Name
                });
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Note
            await parent.CreateAPIRoute("/omnidefence/ip/note", async req =>
            {
                var body = ParseJsonBody(req);
                string ip = (body["ip"] as string ?? "").Trim();
                string? note = body["note"] as string ?? body["notes"] as string;
                if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                parent.Tracker.SetNotes(ip, note);
                var rec = parent.Tracker.GetOrCreate(ip);
                await parent.Tracker.PersistAsync(rec);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Active port scan
            await parent.CreateAPIRoute("/omnidefence/ip/scan", async req =>
            {
                var body = ParseJsonBody(req);
                string ip = (body["ip"] as string ?? "").Trim();
                if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                if (!parent.TryAcquireScanSlot(ip))
                {
                    await req.ReturnResponse("Rate limited (1 scan per IP per hour)", "text/plain", null, (HttpStatusCode)429);
                    return;
                }
                var result = await parent.Scanner.ScanAsync(ip);
                var rec = parent.Tracker.GetOrCreate(ip);
                lock (rec) { rec.LastScannedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
                await parent.Tracker.PersistAsync(rec);
                await parent.RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = ip,
                    Kind = "Scan",
                    ActorProfileId = req.user?.UserID,
                    ActorProfileName = req.user?.Name,
                    Detail = JsonConvert.SerializeObject(new { open = result.OpenPorts, probed = result.ProbedPorts, durationMs = result.Duration.TotalMilliseconds, error = result.Error })
                });
                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Honeypot routes management
            await parent.CreateAPIRoute("/omnidefence/honeypot-routes", async req =>
            {
                var storedRoutes = await parent.Store.ListHoneypotRoutesAsync();
                var rows = new List<object>();
                foreach (var row in storedRoutes)
                {
                    long hits = await parent.Store.ScalarLongAsync(
                        "SELECT COUNT(*) FROM requests WHERE route=$route AND deny_reason='Honeypot'",
                        new() { ["$route"] = row.Route });
                    long lastHit = await parent.Store.ScalarLongAsync(
                        "SELECT COALESCE(MAX(utc_ts), 0) FROM requests WHERE route=$route AND deny_reason='Honeypot'",
                        new() { ["$route"] = row.Route });
                    rows.Add(new
                    {
                        route = row.Route,
                        createdUtc = row.CreatedUtc,
                        responseKind = row.ResponseKind,
                        note = row.Note,
                        hits,
                        lastHit
                    });
                }
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/omnidefence/honeypot-routes/add", async req =>
            {
                var body = ParseJsonBody(req);
                string route = (body["route"] as string ?? "").Trim();
                string? note = body["note"] as string;
                if (string.IsNullOrEmpty(route)) { await req.ReturnResponse("Missing route", "text/plain", null, HttpStatusCode.BadRequest); return; }
                if (!route.StartsWith('/')) route = "/" + route;
                await parent.RegisterHoneypotRouteAsync(route, note);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/omnidefence/honeypot-routes/remove", async req =>
            {
                var body = ParseJsonBody(req);
                string route = (body["route"] as string ?? "").Trim();
                if (string.IsNullOrEmpty(route)) { await req.ReturnResponse("Missing route", "text/plain", null, HttpStatusCode.BadRequest); return; }
                await parent.RemoveHoneypotRouteAsync(route);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Profiles overview
            await parent.CreateAPIRoute("/omnidefence/profiles", async req =>
            {
                var rows = await parent.Store.QueryAsync(
                    @"SELECT profile_id, profile_name,
                             COUNT(*) AS total_requests,
                             MAX(utc_ts) AS last_seen,
                             COUNT(DISTINCT ip) AS distinct_ips
                      FROM requests
                      WHERE profile_id IS NOT NULL
                      GROUP BY profile_id, profile_name
                      ORDER BY total_requests DESC",
                    new());
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Profile detail
            await parent.CreateAPIRoute("/omnidefence/profile", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", "text/plain", null, HttpStatusCode.BadRequest); return; }
                var recent = await parent.Store.QueryAsync(
                    "SELECT * FROM requests WHERE profile_id=$id ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$id"] = id });
                var actions = await parent.Store.QueryAsync(
                    "SELECT * FROM profile_actions WHERE profile_id=$id ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$id"] = id });
                var auth = await parent.Store.QueryAsync(
                    "SELECT * FROM auth_events WHERE profile_id=$id ORDER BY utc_ts DESC LIMIT 200",
                    new() { ["$id"] = id });
                await req.ReturnResponse(JsonConvert.SerializeObject(new { recentRequests = recent, actions, authEvents = auth }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            // Settings (thresholds)
            await parent.CreateAPIRoute("/omnidefence/settings", async req =>
            {
                var t = parent.Tracker;
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    autoWatchScore = t.AutoWatchScore,
                    autoBlockScore = t.AutoBlockScore,
                    escalation2 = t.Escalation2Threshold,
                    escalation3 = t.Escalation3Threshold
                }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/omnidefence/settings/update", async req =>
            {
                var body = ParseJsonBody(req);
                var t = parent.Tracker;
                if (body.TryGetValue("autoWatchScore", out var w) && w != null) t.AutoWatchScore = Convert.ToInt32(w);
                if (body.TryGetValue("autoBlockScore", out var b) && b != null) t.AutoBlockScore = Convert.ToInt32(b);
                if (body.TryGetValue("escalation2", out var e2) && e2 != null) t.Escalation2Threshold = Convert.ToInt32(e2);
                if (body.TryGetValue("escalation3", out var e3) && e3 != null) t.Escalation3Threshold = Convert.ToInt32(e3);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            // Export
            await parent.CreateAPIRoute("/omnidefence/export", async req =>
            {
                string table = (req.userParameters.Get("table") ?? req.userParameters.Get("kind") ?? "requests").ToLowerInvariant();
                string format = (req.userParameters.Get("format") ?? "json").ToLowerInvariant();
                if (!new[] { "requests", "auth_events", "profile_actions", "ip_records", "ip_events" }.Contains(table))
                {
                    await req.ReturnResponse("Invalid table", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                var rows = await parent.Store.QueryAsync($"SELECT * FROM {table} ORDER BY rowid DESC LIMIT 50000", new());
                if (format == "csv")
                {
                    await req.ReturnResponse(ToCsv(rows), "text/csv");
                }
                else
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
                }
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        }

        // -------- Query builders --------

        private static List<IpRecord> FilterIpRecords(IEnumerable<IpRecord> records, string? status, double minScore, string? query, int limit, int offset)
        {
            var filtered = records.Where(r => r.ThreatScore >= minScore);
            if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(r =>
                    Contains(r.Ip, query) ||
                    Contains(r.Country, query) ||
                    Contains(r.Asn, query) ||
                    Contains(r.Notes, query) ||
                    Contains(r.AssociatedProfileId, query) ||
                    Contains(r.AssociatedProfileName, query));
            }

            return filtered
                .OrderByDescending(r => r.ThreatScore)
                .ThenByDescending(r => r.LastSeen)
                .Skip(offset)
                .Take(limit)
                .ToList();
        }

        private static bool Contains(string? source, string query) => source?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

        private static (string sql, Dictionary<string, object?> p) BuildRequestsQuery(KliveAPI.KliveAPI.UserRequest req)
        {
            var sb = new StringBuilder(RequestSelectSql + " WHERE 1=1");
            var p = new Dictionary<string, object?>();
            string? ip = req.userParameters.Get("ip");
            string? profile = req.userParameters.Get("profile");
            string? route = req.userParameters.Get("route");
            string? statusCode = req.userParameters.Get("status");
            string? method = req.userParameters.Get("method");
            string? origin = req.userParameters.Get("origin");
            string? from = req.userParameters.Get("from");
            string? to = req.userParameters.Get("to");
            string? denyOnly = req.userParameters.Get("denyOnly");
            int limit = int.TryParse(req.userParameters.Get("limit"), out var l) ? Math.Clamp(l, 1, 5000) : 500;
            int offset = int.TryParse(req.userParameters.Get("offset"), out var o) ? Math.Max(0, o) : 0;

            if (!string.IsNullOrWhiteSpace(ip)) { sb.Append(" AND ip LIKE $ip"); p["$ip"] = "%" + ip + "%"; }
            if (!string.IsNullOrWhiteSpace(profile)) { sb.Append(" AND (profile_id=$pid OR profile_name LIKE $pname)"); p["$pid"] = profile; p["$pname"] = "%" + profile + "%"; }
            if (!string.IsNullOrWhiteSpace(route)) { sb.Append(" AND route LIKE $route"); p["$route"] = "%" + route + "%"; }
            if (!string.IsNullOrWhiteSpace(statusCode) && statusCode.EndsWith("xx", StringComparison.OrdinalIgnoreCase) && int.TryParse(statusCode[..1], out var statusHundreds))
            {
                sb.Append(" AND status_code >= $scFrom AND status_code < $scTo");
                p["$scFrom"] = statusHundreds * 100;
                p["$scTo"] = (statusHundreds + 1) * 100;
            }
            else if (!string.IsNullOrWhiteSpace(statusCode) && int.TryParse(statusCode, out var sc)) { sb.Append(" AND status_code=$sc"); p["$sc"] = sc; }
            if (!string.IsNullOrWhiteSpace(method)) { sb.Append(" AND method=$method"); p["$method"] = method.ToUpperInvariant(); }
            if (!string.IsNullOrWhiteSpace(origin)) { sb.Append(" AND ").Append(DerivedRequestOriginSql).Append("=$origin"); p["$origin"] = origin; }
            if (!string.IsNullOrWhiteSpace(from) && long.TryParse(from, out var f)) { sb.Append(" AND utc_ts >= $f"); p["$f"] = f; }
            if (!string.IsNullOrWhiteSpace(to) && long.TryParse(to, out var t)) { sb.Append(" AND utc_ts <= $t"); p["$t"] = t; }
            if (denyOnly == "1" || string.Equals(denyOnly, "true", StringComparison.OrdinalIgnoreCase)) sb.Append(" AND deny_reason IS NOT NULL");
            sb.Append(" ORDER BY utc_ts DESC LIMIT $lim OFFSET $off");
            p["$lim"] = limit;
            p["$off"] = offset;
            return (sb.ToString(), p);
        }

        private static (string sql, Dictionary<string, object?> p) BuildAuthEventsQuery(KliveAPI.KliveAPI.UserRequest req)
        {
            var sb = new StringBuilder("SELECT * FROM auth_events WHERE 1=1");
            var p = new Dictionary<string, object?>();
            string? ip = req.userParameters.Get("ip");
            string? profile = req.userParameters.Get("profile");
            string? type = req.userParameters.Get("type");
            string? from = req.userParameters.Get("from");
            string? to = req.userParameters.Get("to");
            int limit = int.TryParse(req.userParameters.Get("limit"), out var l) ? Math.Clamp(l, 1, 5000) : 500;
            int offset = int.TryParse(req.userParameters.Get("offset"), out var o) ? Math.Max(0, o) : 0;
            if (!string.IsNullOrWhiteSpace(ip)) { sb.Append(" AND ip LIKE $ip"); p["$ip"] = "%" + ip + "%"; }
            if (!string.IsNullOrWhiteSpace(profile)) { sb.Append(" AND (profile_id=$pid OR profile_name LIKE $pname)"); p["$pid"] = profile; p["$pname"] = "%" + profile + "%"; }
            if (!string.IsNullOrWhiteSpace(type)) { sb.Append(" AND type=$type"); p["$type"] = type; }
            if (!string.IsNullOrWhiteSpace(from) && long.TryParse(from, out var f)) { sb.Append(" AND utc_ts >= $f"); p["$f"] = f; }
            if (!string.IsNullOrWhiteSpace(to) && long.TryParse(to, out var t)) { sb.Append(" AND utc_ts <= $t"); p["$t"] = t; }
            sb.Append(" ORDER BY utc_ts DESC LIMIT $lim OFFSET $off");
            p["$lim"] = limit;
            p["$off"] = offset;
            return (sb.ToString(), p);
        }

        private static (string sql, Dictionary<string, object?> p) BuildProfileActionsQuery(KliveAPI.KliveAPI.UserRequest req)
        {
            var sb = new StringBuilder("SELECT * FROM profile_actions WHERE 1=1");
            var p = new Dictionary<string, object?>();
            string? profile = req.userParameters.Get("profile");
            string? cat = req.userParameters.Get("category");
            string? from = req.userParameters.Get("from");
            string? to = req.userParameters.Get("to");
            int limit = int.TryParse(req.userParameters.Get("limit"), out var l) ? Math.Clamp(l, 1, 5000) : 500;
            int offset = int.TryParse(req.userParameters.Get("offset"), out var o) ? Math.Max(0, o) : 0;
            if (!string.IsNullOrWhiteSpace(profile)) { sb.Append(" AND (profile_id=$pid OR profile_name LIKE $pname)"); p["$pid"] = profile; p["$pname"] = "%" + profile + "%"; }
            if (!string.IsNullOrWhiteSpace(cat)) { sb.Append(" AND category=$cat"); p["$cat"] = cat; }
            if (!string.IsNullOrWhiteSpace(from) && long.TryParse(from, out var f)) { sb.Append(" AND utc_ts >= $f"); p["$f"] = f; }
            if (!string.IsNullOrWhiteSpace(to) && long.TryParse(to, out var t)) { sb.Append(" AND utc_ts <= $t"); p["$t"] = t; }
            sb.Append(" ORDER BY utc_ts DESC LIMIT $lim OFFSET $off");
            p["$lim"] = limit;
            p["$off"] = offset;
            return (sb.ToString(), p);
        }

        private static Dictionary<string, object?> ParseJsonBody(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.userMessageContent)) return new();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object?>>(req.userMessageContent);
                return dict ?? new();
            }
            catch { return new(); }
        }

        private static async Task ResetIpToNormalAsync(OmniDefence parent, KliveAPI.KliveAPI.UserRequest req, string eventKind)
        {
            var body = ParseJsonBody(req);
            string ip = (body["ip"] as string ?? "").Trim();
            string? detail = body["reason"] as string ?? body["detail"] as string;
            if (string.IsNullOrEmpty(ip))
            {
                await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest);
                return;
            }

            var rec = parent.Tracker.GetOrCreate(ip);
            lock (rec)
            {
                rec.Status = nameof(IpThreatTracker.IpStatus.Normal);
                rec.LastBlockReason = null;
            }
            await parent.Tracker.PersistAsync(rec);
            await parent.RecordIpEventAsync(new IpEventRow
            {
                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ip = ip,
                Kind = eventKind,
                ActorProfileId = req.user?.UserID,
                ActorProfileName = req.user?.Name,
                Detail = detail
            });
            await req.ReturnResponse("{\"ok\":true}", "application/json");
        }

        private static string ToCsv(List<Dictionary<string, object?>> rows)
        {
            if (rows.Count == 0) return "";
            var sb = new StringBuilder();
            var headers = rows[0].Keys.ToList();
            sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",", headers.Select(h => EscapeCsv(row.TryGetValue(h, out var v) ? v?.ToString() ?? "" : ""))));
            }
            return sb.ToString();
        }

        private static string EscapeCsv(string? s)
        {
            s ??= "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }
    }
}
