using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// All <c>/omnidefence/*</c> HTTP routes. Every route requires
    /// an <c>omnidefence.*</c> permission (see <see cref="Omnipotent.Profiles.Permissions.OmniDefencePerms"/>).
    /// </summary>
    internal static partial class OmniDefenceRoutes
    {
        private const string DerivedRequestOriginSql = "COALESCE(NULLIF(request_origin, ''), CASE WHEN client_page IS NOT NULL AND client_page <> '' THEN CASE WHEN profile_id IS NOT NULL AND profile_id <> '' THEN 'WebsiteProfile' ELSE 'WebsiteNoProfile' END WHEN profile_id IS NOT NULL AND profile_id <> '' THEN 'DirectApiProfile' ELSE 'DirectApi' END)";
        private const string RequestSelectSql = "SELECT id, utc_ts, ip, method, route, query, status_code, duration_ms, profile_id, profile_name, profile_rank, perm_required, perm_key, matched_route, body_hash, body_length, user_agent, deny_reason, " + DerivedRequestOriginSql + " AS request_origin, client_page FROM requests";
        private static readonly JsonSerializer CamelCase = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        });

        // ---- The overview's reads of the request log ----
        // Each names the utc_ts index (INDEXED BY), so it costs the window it asks about and not the
        // whole log. Left to the planner, "busiest routes today" did the opposite. SQLite has no
        // statistics for this database, so it cannot tell that "utc_ts >= ?" keeps only the newest
        // rows, and it prefers to walk the whole route index, where the rows come out already
        // grouped. That walk fetches every row ever logged, in route order, to test its utc_ts. On
        // 10 Nov 2026 the log held 1,694,632 rows, about 3,000 of them from the last day, and a build
        // was still in that step more than an hour after it started; the four steps before it took
        // under 51 seconds together. OverviewQueryCostTests holds each read to its window.

        /// <summary>Every request since $week, oldest first: when, and whether it was denied or errored.</summary>
        internal const string OverviewWindowSql =
            "SELECT utc_ts, (deny_reason IS NOT NULL OR status_code >= 400) FROM requests INDEXED BY ix_requests_ts WHERE utc_ts >= $week";

        /// <summary>One hour of the request log. <c>H</c> is the unix time of its first second.</summary>
        internal sealed record HourOfRequests(long H, long Requests, long Denied);

        /// <summary>The last week of the request log by hour, with the last 24 hours counted to the second.</summary>
        internal sealed record RequestWindow(long Requests7d, long Requests24h, long Denied24h, List<HourOfRequests> Hourly);

        /// <summary>
        /// One pass over the week. The rows arrive in time order and are folded into hours here:
        /// grouping in SQL (GROUP BY utc_ts / 3600) sorts the whole window first, because SQLite
        /// cannot tell that the index already delivers it in that order.
        /// </summary>
        internal static Task<RequestWindow> ReadRequestWindowAsync(OmniDefenceStore store, long week, long day)
            => store.WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = OverviewWindowSql;
            cmd.Parameters.AddWithValue("$week", week);
            long requests7d = 0, requests24h = 0, denied24h = 0;
            var hourly = new List<HourOfRequests>(7 * 24 + 1);
            long hour = long.MinValue, hourRequests = 0, hourDenied = 0;
            using var reader = await cmd.ExecuteReaderAsync();
            while (reader.Read())
            {
                long ts = reader.GetInt64(0);
                bool denied = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                long startOfHour = ts - ts % 3600;
                if (startOfHour != hour)
                {
                    if (hourRequests > 0) hourly.Add(new HourOfRequests(hour, hourRequests, hourDenied));
                    hour = startOfHour;
                    hourRequests = 0;
                    hourDenied = 0;
                }
                hourRequests++;
                requests7d++;
                if (denied) hourDenied++;
                if (ts >= day)
                {
                    requests24h++;
                    if (denied) denied24h++;
                }
            }
            if (hourRequests > 0) hourly.Add(new HourOfRequests(hour, hourRequests, hourDenied));
            return new RequestWindow(requests7d, requests24h, denied24h, hourly);
        });

        /// <summary>The ten busiest routes since $t.</summary>
        internal const string OverviewTopRoutesSql =
            "SELECT route, COUNT(*) AS hits FROM requests INDEXED BY ix_requests_ts WHERE utc_ts >= $t GROUP BY route ORDER BY hits DESC, route LIMIT 10";

        /// <summary>Requests since $t by where they came from.</summary>
        internal const string OverviewOriginsSql =
            "SELECT origin, COUNT(*) AS hits FROM (SELECT " + DerivedRequestOriginSql + " AS origin FROM requests INDEXED BY ix_requests_ts WHERE utc_ts >= $t) GROUP BY origin ORDER BY hits DESC";

        public static async Task RegisterAsync(OmniDefence parent)
        {
            // Overview
            await parent.CreateAPIRoute("/omnidefence/overview", async req =>
            {
                CacheDeps.MarkUncacheable("precomputed OmniDefence overview");
                string? json = parent.OverviewJson;
                if (json == null)
                {
                    await req.ReturnResponse("{\"pending\":true}", "application/json", null, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await req.ReturnResponse(json, "application/json");
            }, HttpMethod.Get, OmniDefencePerms.OverviewView);

            // How the overview build is going. Answers even when the overview itself cannot: whether a
            // build is running, for how long, in which step, and how old the overview being served is.
            await parent.CreateAPIRoute("/omnidefence/overview/status", async req =>
            {
                CacheDeps.MarkUncacheable("OmniDefence overview build status");
                await req.ReturnResponse(JsonConvert.SerializeObject(parent.OverviewStatus()), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.OverviewView);

            // Requests filtered query
            await parent.CreateAPIRoute("/omnidefence/requests", async req =>
            {
                CacheDeps.MarkUncacheable("precomputed OmniDefence request list");
                if (new[] { "ip", "profile", "route", "status", "method", "origin" }
                    .Any(name => req.userParameters.Get(name)?.Length > 256))
                {
                    await req.ReturnResponse("{\"error\":\"Filter is too long.\"}", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }
                var (sql, parameters) = BuildRequestsQuery(req.userParameters);
                string key = RequestsKey(sql, parameters);
                ReadSnapshotReply reply = parent.ReadSnapshots.GetOrQueue(key, false,
                    async ct => JsonConvert.SerializeObject(await parent.Store.QueryBoundedAsync(sql, parameters, ct)));
                await SendReadSnapshot(req, reply);
            }, HttpMethod.Get, OmniDefencePerms.TrafficRead);

            // Single request detail (full body, headers, query, etc.)
            await parent.CreateAPIRoute("/omnidefence/request", async req =>
            {
                string? rawId = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(rawId) || !long.TryParse(rawId, out long id) || id <= 0)
                {
                    await req.ReturnResponse("Missing or invalid id", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                var row = await parent.Store.GetRequestByIdAsync(id);
                if (row == null)
                {
                    await req.ReturnResponse("Request not found", "text/plain", null, HttpStatusCode.NotFound);
                    return;
                }
                // Decode headers_json so the client receives a parsed object alongside the raw JSON.
                Dictionary<string, string>? headers = null;
                if (row.TryGetValue("headers_json", out var hdrsRaw) && hdrsRaw is string hdrsStr && !string.IsNullOrWhiteSpace(hdrsStr))
                {
                    try { headers = JsonConvert.DeserializeObject<Dictionary<string, string>>(hdrsStr); }
                    catch { headers = null; }
                }
                var resp = new Dictionary<string, object?>(row)
                {
                    ["headers"] = headers
                };
                await req.ReturnResponse(JsonConvert.SerializeObject(resp), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.TrafficRead);

            // Auth events
            await parent.CreateAPIRoute("/omnidefence/auth-events", async req =>
            {
                var (sql, parameters) = BuildAuthEventsQuery(req);
                var rows = await parent.Store.QueryAsync(sql, parameters);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.AuthRead);

            // Profile actions
            await parent.CreateAPIRoute("/omnidefence/profile-actions", async req =>
            {
                var (sql, parameters) = BuildProfileActionsQuery(req);
                var rows = await parent.Store.QueryAsync(sql, parameters);
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.AuthRead);

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
            }, HttpMethod.Get, OmniDefencePerms.TrafficRead);

            // Tactical map data
            await parent.CreateAPIRoute("/omnidefence/ip-map", async req =>
            {
                var rows = parent.Tracker.All()
                    .OrderByDescending(r => r.LastSeen)
                    .ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(rows), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.OverviewView);

            // IP detail
            await parent.CreateAPIRoute("/omnidefence/ip", async req =>
            {
                CacheDeps.MarkUncacheable("precomputed OmniDefence IP detail");
                string? ip = req.userParameters.Get("ip");
                if (string.IsNullOrWhiteSpace(ip) || ip.Length > 128)
                {
                    await req.ReturnResponse("Missing or invalid ip", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                string key = OmniDefenceReadSnapshotCache.KeyFor("ip", ip);
                ReadSnapshotReply reply = parent.ReadSnapshots.GetOrQueue(key, true, async ct =>
                {
                    var detail = await parent.Store.GetIpDetailBoundedAsync(ip, RequestSelectSql, ct);
                    return JsonConvert.SerializeObject(new
                    {
                        record = parent.Tracker.Get(ip) ?? detail.Record,
                        recentRequests = detail.RecentRequests,
                        events = detail.Events,
                        auth = detail.Auth
                    });
                });
                await SendReadSnapshot(req, reply);
            }, HttpMethod.Get, OmniDefencePerms.TrafficRead);

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
                parent.ReadSnapshots.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", ip));
                _ = parent.SendBlockNotificationAsync(rec, req.user?.Name ?? "Unknown");
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.IpBlock);

            // Unblock
            await parent.CreateAPIRoute("/omnidefence/ip/unblock", async req =>
            {
                await ResetIpToNormalAsync(parent, req, "Unblock");
            }, HttpMethod.Post, OmniDefencePerms.IpBlock);

            // Untrap / release hostile statuses (Blocked / Tarpit / Honeypot)
            await parent.CreateAPIRoute("/omnidefence/ip/untrap", async req =>
            {
                await ResetIpToNormalAsync(parent, req, "Untrap");
            }, HttpMethod.Post, OmniDefencePerms.IpBlock);

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
                parent.ReadSnapshots.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", ip));
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.IpBlock);

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
                parent.ReadSnapshots.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", ip));
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.IpAct);

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
                parent.ReadSnapshots.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", ip));
                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }, HttpMethod.Post, OmniDefencePerms.IpAct);

            // Blocked regions list
            await parent.CreateAPIRoute("/omnidefence/regions", async req =>
            {
                var rows = parent.BlockedRegionsSnapshot().OrderByDescending(r => r.Id).ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(rows.Select(r => new
                {
                    id = r.Id,
                    latMin = r.LatMin,
                    latMax = r.LatMax,
                    lonMin = r.LonMin,
                    lonMax = r.LonMax,
                    reason = r.Reason,
                    createdUtc = r.CreatedUtc,
                    createdBy = r.CreatedBy
                })), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.RegionsManage);

            // Add a blocked region
            await parent.CreateAPIRoute("/omnidefence/regions/add", async req =>
            {
                var body = ParseJsonBody(req);
                double latMin = Convert.ToDouble(body.GetValueOrDefault("latMin") ?? body.GetValueOrDefault("lat_min") ?? 0);
                double latMax = Convert.ToDouble(body.GetValueOrDefault("latMax") ?? body.GetValueOrDefault("lat_max") ?? 0);
                double lonMin = Convert.ToDouble(body.GetValueOrDefault("lonMin") ?? body.GetValueOrDefault("lon_min") ?? 0);
                double lonMax = Convert.ToDouble(body.GetValueOrDefault("lonMax") ?? body.GetValueOrDefault("lon_max") ?? 0);
                string? reason = body.GetValueOrDefault("reason") as string;
                if (latMin >= latMax || lonMin >= lonMax)
                {
                    await req.ReturnResponse("Invalid bounds", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }
                var row = new BlockedRegionRow
                {
                    LatMin = latMin,
                    LatMax = latMax,
                    LonMin = lonMin,
                    LonMax = lonMax,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "Region block" : reason,
                    CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    CreatedBy = req.user?.Name
                };
                long id = await parent.Store.InsertBlockedRegionAsync(row);
                row.Id = id;
                parent.RegisterBlockedRegion(row);
                int blockedCount = await parent.ApplyBlockedRegionToKnownIpsAsync(row, req.user?.UserID, req.user?.Name);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true, id, blockedCount }), "application/json");
            }, HttpMethod.Post, OmniDefencePerms.RegionsManage);

            // Remove a blocked region
            await parent.CreateAPIRoute("/omnidefence/regions/remove", async req =>
            {
                var body = ParseJsonBody(req);
                long id = Convert.ToInt64(body.GetValueOrDefault("id") ?? 0);
                if (id <= 0) { await req.ReturnResponse("Missing id", "text/plain", null, HttpStatusCode.BadRequest); return; }
                bool unblockIpsInRegion = ReadBoolean(body, "unblockIpsInRegion", "unblockIpsInBox", "unblockIps");
                var region = parent.BlockedRegionsSnapshot().FirstOrDefault(r => r.Id == id);
                await parent.Store.DeleteBlockedRegionAsync(id);
                parent.UnregisterBlockedRegion(id);
                int unblockedCount = unblockIpsInRegion && region != null
                    ? await parent.UnblockIpsInRegionAsync(region, req.user?.UserID, req.user?.Name)
                    : 0;
                await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true, unblockedCount }), "application/json");
            }, HttpMethod.Post, OmniDefencePerms.RegionsManage);

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
            }, HttpMethod.Get, OmniDefencePerms.HoneypotsManage);

            await parent.CreateAPIRoute("/omnidefence/honeypot-routes/add", async req =>
            {
                var body = ParseJsonBody(req);
                string route = (body["route"] as string ?? "").Trim();
                string? note = body["note"] as string;
                if (string.IsNullOrEmpty(route)) { await req.ReturnResponse("Missing route", "text/plain", null, HttpStatusCode.BadRequest); return; }
                if (!route.StartsWith('/')) route = "/" + route;
                await parent.RegisterHoneypotRouteAsync(route, note);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.HoneypotsManage);

            await parent.CreateAPIRoute("/omnidefence/honeypot-routes/remove", async req =>
            {
                var body = ParseJsonBody(req);
                string route = (body["route"] as string ?? "").Trim();
                if (string.IsNullOrEmpty(route)) { await req.ReturnResponse("Missing route", "text/plain", null, HttpStatusCode.BadRequest); return; }
                await parent.RemoveHoneypotRouteAsync(route);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.HoneypotsManage);

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
            }, HttpMethod.Get, OmniDefencePerms.AuthRead);

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
            }, HttpMethod.Get, OmniDefencePerms.AuthRead);

            // Settings (thresholds)
            await parent.CreateAPIRoute("/omnidefence/settings", async req =>
            {
                var st = parent.Settings;
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    autoWatchScore = st.AutoWatchScore,
                    autoBlockScore = st.AutoBlockScore,
                    escalation2 = st.Escalation2,
                    escalation3 = st.Escalation3,
                    fingerprint = await FingerprintSettingsView(parent, st)
                }), "application/json");
            }, HttpMethod.Get, OmniDefencePerms.SettingsManage);

            await parent.CreateAPIRoute("/omnidefence/settings/update", async req =>
            {
                var body = ParseJsonBody(req);
                var next = parent.Settings.Clone();
                if (body.TryGetValue("autoWatchScore", out var w) && w != null) next.AutoWatchScore = Convert.ToInt32(w);
                if (body.TryGetValue("autoBlockScore", out var b) && b != null) next.AutoBlockScore = Convert.ToInt32(b);
                if (body.TryGetValue("escalation2", out var e2) && e2 != null) next.Escalation2 = Convert.ToInt32(e2);
                if (body.TryGetValue("escalation3", out var e3) && e3 != null) next.Escalation3 = Convert.ToInt32(e3);
                ApplyFingerprintSettings(next, body);
                await parent.SaveSettingsAsync(next);
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, OmniDefencePerms.SettingsManage);

            // Export
            await parent.CreateAPIRoute("/omnidefence/export", async req =>
            {
                string table = (req.userParameters.Get("table") ?? req.userParameters.Get("kind") ?? "requests").ToLowerInvariant();
                string format = (req.userParameters.Get("format") ?? "json").ToLowerInvariant();
                if (!new[] { "requests", "auth_events", "profile_actions", "ip_records", "ip_events", "ip_fingerprints", "ip_intel", "fp_devices" }.Contains(table))
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
            }, HttpMethod.Get, OmniDefencePerms.DataExport);

            await RegisterFingerprintRoutesAsync(parent);
            await RegisterConsoleRoutesAsync(parent);
        }

        /// <summary>
        /// Runs only on the overview refresh worker. Keep all aggregation and database
        /// access here, never in the HTTP handler above.
        /// </summary>
        internal static Task<string> BuildOverviewJsonAsync(OmniDefence parent)
            => BuildOverviewJsonAsync(parent.Store, parent.Tracker, parent.OverviewStep,
                parent.OverviewBuildMs, () => parent.Fingerprints?.Backlog ?? 0);

        /// <summary>
        /// The build itself, on nothing but the store and the IP records, so a test can run all of it
        /// against a real database. An earlier rework of this build was checked by tests that never
        /// ran it, and in production the overview then went unanswered for eleven days.
        /// </summary>
        internal static async Task<string> BuildOverviewJsonAsync(OmniDefenceStore store, IpThreatTracker tracker,
            Action<string> reportStep, long lastBuildMs, Func<int> fingerprintBacklog, long? nowUtc = null)
        {
            long now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long day = now - 86400;
            long week = now - 86400 * 7;

            // One step at a time, each named and timed. The timings go out in the overview's health
            // block and the name of the running step in /omnidefence/overview/status, so the next slow
            // build shows which step it is instead of an overview that never arrives. The in-memory
            // work is timed as well as the queries: the slow one has been guessed wrong before.
            var stepMs = new Dictionary<string, long>();
            async Task<T> Step<T>(string name, Func<Task<T>> run)
            {
                reportStep(name);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return await run(); }
                finally { stepMs[name] = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }
            T StepNow<T>(string name, Func<T> run)
            {
                reportStep(name);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { return run(); }
                finally { stepMs[name] = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }

            // Windowed counts come from the utc_ts index (the last 7 days only); all-time totals are
            // carried forward by the store. One aggregate over the whole table took ~17 s per
            // refresh in production and ran more than half of every minute.
            var window = await Step("requests in the last 7 days", () => ReadRequestWindowAsync(store, week, day));
            var totals = await Step("all-time request totals", () => store.ReadAuditTotalsAsync());
            // The whole of auth_events on purpose: it held 1,741 rows on 10 Nov 2026.
            var authCountRows = await Step("auth events", () => store.QueryAsync(@"
                SELECT COUNT(*) AS totalAuthEvents,
                    COALESCE(SUM(CASE WHEN utc_ts >= $day AND type IN
                        ('UnauthRoute','InsufficientClearance','InvalidPassword','WebsiteNoProfile','WebsiteInvalidProfile')
                        THEN 1 ELSE 0 END), 0) AS unauth24h
                FROM auth_events", new() { ["$day"] = day }));

            // Enumerate the concurrent tracker once instead of repeatedly sorting it.
            var ips = StepNow("IP records", () => tracker.All().ToArray());
            int blocked = ips.Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Blocked));
            int watched = ips.Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Watch));
            int honey = ips.Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Honeypot));
            int tarpit = ips.Count(r => r.Status == nameof(IpThreatTracker.IpStatus.Tarpit));
            var attackers = ips.Where(r => string.IsNullOrWhiteSpace(r.AssociatedProfileId))
                .OrderByDescending(r => r.ThreatScore).Take(10).ToArray();
            var topAttackers = attackers.Select(r => new
            {
                ip = r.Ip, score = r.ThreatScore, status = r.Status,
                unauth = r.UnauthAttempts, country = r.Country, attacker = true
            });
            var topThreat = attackers.Take(1).Select(r => new
            {
                ip = r.Ip, score = r.ThreatScore, status = r.Status,
                unauth = r.UnauthAttempts, country = r.Country, asn = r.Asn, attacker = true
            }).FirstOrDefault();

            var topRoutes = await Step("busiest routes today", () => store.QueryAsync(
                OverviewTopRoutesSql, new() { ["$t"] = day }));
            var originBreakdown = await Step("request origins today", () => store.QueryAsync(
                OverviewOriginsSql, new() { ["$t"] = day }));
            var classBreakdown = StepNow("class breakdown", () => ClassBreakdown(ips));
            var tagBreakdown = StepNow("tag breakdown", () => TagBreakdown(ips));
            var review = StepNow("review lists", () => JToken.FromObject(OmniDefenceReview.Build(ips, now), CamelCase));
            reportStep("writing the overview");
            var authCounts = authCountRows[0];
            long Count(Dictionary<string, object?> row, string key) => Convert.ToInt64(row[key] ?? 0);
            long reqs24h = window.Requests24h;
            long totalRequests = totals.Requests;
            long reqs7d = window.Requests7d;
            long denied24h = window.Denied24h;
            long totalDenied = totals.Denied;
            long unauth24h = Count(authCounts, "unauth24h");
            long totalAuthEvents = Count(authCounts, "totalAuthEvents");

            var resp = new
            {
                asOfUtc = DateTimeOffset.UtcNow,
                requests24h = reqs24h,
                totalRequests,
                requests7d = reqs7d,
                // The same seven days by hour, oldest first: h is the unix time the hour starts,
                // and hours with no requests are left out.
                hourly = window.Hourly.Select(hour => new { h = hour.H, requests = hour.Requests, denied = hour.Denied }),
                denied24h,
                totalDenied,
                authFailures24h = unauth24h,
                unauth24h,
                totalAuthEvents,
                blockedIps = blocked,
                watchedIps = watched,
                honeypotIps = honey,
                tarpitIps = tarpit,
                totalIps = ips.Length,
                knownIps = ips.Length,
                topThreat,
                topAttackers,
                topRoutes,
                originBreakdown,
                classBreakdown,
                tagBreakdown,
                review,
                // Engine health, so the next stall shows on the page instead of in a log dig.
                health = new
                {
                    overviewBuildMs = lastBuildMs,
                    // This build's time per step, in milliseconds. Writing the overview out is the
                    // one step not in here: the status route's lastBuildMs less this total.
                    buildSteps = stepMs,
                    auditQueue = store.PendingAuditRows,
                    auditRowsDropped = store.DroppedAuditRows,
                    readPoolTimeouts = store.ReadPoolTimeouts,
                    fingerprintBacklog = fingerprintBacklog()
                }
            };
            return JsonConvert.SerializeObject(resp);
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

        internal static void WarmDefaultRequests(OmniDefence parent)
        {
            var input = new NameValueCollection { ["limit"] = "250" };
            var (sql, parameters) = BuildRequestsQuery(input);
            parent.ReadSnapshots.GetOrQueue(RequestsKey(sql, parameters), false,
                async ct => JsonConvert.SerializeObject(await parent.Store.QueryBoundedAsync(sql, parameters, ct)),
                pinned: true);
        }

        private static string RequestsKey(string sql, Dictionary<string, object?> parameters) =>
            OmniDefenceReadSnapshotCache.KeyFor("requests", sql + "|" + JsonConvert.SerializeObject(
                parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()));

        private static Task SendReadSnapshot(KliveAPI.KliveAPI.UserRequest req, ReadSnapshotReply reply)
        {
            if (reply.State == ReadSnapshotState.Ready)
                return req.ReturnResponse(reply.Json!, "application/json");
            var headers = new NameValueCollection { ["Retry-After"] = "1", ["Cache-Control"] = "no-store" };
            return req.ReturnResponse("{\"pending\":true}", "application/json", headers,
                reply.State == ReadSnapshotState.Pending ? HttpStatusCode.Accepted : HttpStatusCode.ServiceUnavailable);
        }

        private static (string sql, Dictionary<string, object?> p) BuildRequestsQuery(NameValueCollection input)
        {
            var sb = new StringBuilder(RequestSelectSql + " WHERE 1=1");
            var p = new Dictionary<string, object?>();
            string? ip = input.Get("ip");
            string? profile = input.Get("profile");
            string? route = input.Get("route");
            string? statusCode = input.Get("status");
            string? method = input.Get("method");
            string? origin = input.Get("origin");
            string? from = input.Get("from");
            string? to = input.Get("to");
            string? denyOnly = input.Get("denyOnly");
            int limit = int.TryParse(input.Get("limit"), out var l) ? Math.Clamp(l, 1, 5000) : 500;
            int offset = int.TryParse(input.Get("offset"), out var o) ? Math.Clamp(o, 0, 100_000) : 0;

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

        private static bool ReadBoolean(Dictionary<string, object?> body, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (!body.TryGetValue(key, out object? value) || value == null) continue;
                if (value is bool boolValue) return boolValue;
                string text = value.ToString()?.Trim() ?? string.Empty;
                if (bool.TryParse(text, out bool parsed)) return parsed;
                if (long.TryParse(text, out long numeric)) return numeric != 0;
            }
            return false;
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
            parent.ReadSnapshots.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", ip));
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
