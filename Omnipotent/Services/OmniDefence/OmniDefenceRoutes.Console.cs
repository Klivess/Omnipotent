using Newtonsoft.Json;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Specialized;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Reads for the OmniDefence console page (pages/omnidefence.vue) that the older routes answer
    /// expensively or not at all.
    ///
    /// <c>/omnidefence/ip-map</c> sends every IP record with every field named: about 4 MB of JSON
    /// (335 KB compressed) for ~6,000 addresses, and the old page fetched it 643 times in the week to
    /// 10 Nov 2026, 215 MB, most of it from a tab left open overnight. The compact form sends the
    /// fields the page uses once and one row per address, and with <c>since</c> only the addresses
    /// that changed. <c>/omnidefence/events</c> is the server-wide feed of what OmniDefence and
    /// Klives did (automatic and manual blocks, releases, class labels, region rules), which until
    /// now could only be read one address at a time or by exporting the whole table.
    /// </summary>
    internal static partial class OmniDefenceRoutes
    {
        /// <summary>Column order of the compact map. The page reads it from the payload, never by position.</summary>
        internal static readonly string[] CompactIpFields =
        {
            "ip", "firstSeen", "lastSeen", "total", "unauth", "deny", "score", "status",
            "country", "city", "region", "asName", "isp", "org", "asn", "lat", "lon",
            "profileName", "profileRank", "cls", "conf", "classTags", "classUpdated", "notes",
            "isHosting", "isProxy", "isMobile", "lastBlockReason",
        };

        internal const int MaxEvents = 500;

        /// <summary>
        /// Every IP record as columns, most recently seen first. With <paramref name="since"/>, only the
        /// records seen or reclassified at or after it (<c>full: false</c>), for a page that already
        /// holds the rest. Coordinates are rounded to about 100 m and scores to one decimal: the map
        /// cannot show finer, and the digits were a large share of the payload.
        /// </summary>
        internal static object CompactIpMap(IEnumerable<IpRecord> records, long? since, long nowUtc)
        {
            var picked = new List<IpRecord>();
            foreach (var r in records)
            {
                if (r == null || string.IsNullOrEmpty(r.Ip)) continue;
                if (since.HasValue && r.LastSeen < since.Value && (r.ClassUpdatedUtc ?? 0) < since.Value) continue;
                picked.Add(r);
            }
            picked.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));

            static double? Round(double? value, int digits) => value.HasValue ? Math.Round(value.Value, digits) : null;
            var rows = new List<object?[]>(picked.Count);
            foreach (var r in picked)
            {
                rows.Add(new object?[]
                {
                    r.Ip, r.FirstSeen, r.LastSeen, r.TotalRequests, r.UnauthAttempts, r.DenyCount, Math.Round(r.ThreatScore, 1), r.Status,
                    r.Country, r.City, r.Region, r.AsName, r.Isp, r.Org, r.Asn, Round(r.Latitude, 3), Round(r.Longitude, 3),
                    r.AssociatedProfileName, r.AssociatedProfileRank, r.Classification, Round(r.ClassConfidence, 2), r.ClassTags, r.ClassUpdatedUtc, r.Notes,
                    r.IsHosting, r.IsProxy, r.IsMobile, r.LastBlockReason,
                });
            }
            return new { asOf = nowUtc, full = !since.HasValue, since, count = rows.Count, fields = CompactIpFields, rows };
        }

        /// <summary>
        /// Recent IP events across every address, newest first. "GeoIP" rows (one per newly located
        /// address: nobody acted) are left out unless <c>geo=1</c>. Pages back with <c>before=&lt;id&gt;</c>.
        /// Ordered by id, the table's own key, so no index is needed to read from the end.
        /// </summary>
        internal static (string sql, Dictionary<string, object?> p) BuildEventsQuery(NameValueCollection input)
        {
            var sb = new StringBuilder("SELECT id, utc_ts, ip, kind, actor_profile_name, detail FROM ip_events WHERE 1=1");
            var p = new Dictionary<string, object?>();
            if (input.Get("geo") != "1") sb.Append(" AND kind <> 'GeoIP'");
            string? kind = input.Get("kind")?.Trim();
            if (!string.IsNullOrEmpty(kind))
            {
                // A prefix: "StatusChange" matches every StatusChange:<status>.
                sb.Append(" AND substr(kind, 1, length($kind)) = $kind");
                p["$kind"] = kind;
            }
            string? ip = input.Get("ip")?.Trim();
            if (!string.IsNullOrEmpty(ip)) { sb.Append(" AND ip = $ip"); p["$ip"] = ip; }
            if (long.TryParse(input.Get("before"), out long before) && before > 0) { sb.Append(" AND id < $before"); p["$before"] = before; }
            int limit = int.TryParse(input.Get("limit"), out int l) ? Math.Clamp(l, 1, MaxEvents) : 100;
            sb.Append(" ORDER BY id DESC LIMIT $lim");
            p["$lim"] = limit;
            return (sb.ToString(), p);
        }

        /// <summary>Event counts by kind over the last day, from the utc_ts index.</summary>
        internal const string EventCountsSql = "SELECT kind, COUNT(*) AS n FROM ip_events WHERE utc_ts >= $since GROUP BY kind";

        internal static async Task<object> ReadEventsAsync(OmniDefenceStore store, NameValueCollection input, long nowUtc)
        {
            var (sql, parameters) = BuildEventsQuery(input);
            var rows = await store.QueryAsync(sql, parameters);
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in await store.QueryAsync(EventCountsSql, new() { ["$since"] = nowUtc - 86_400 }))
            {
                if (row.TryGetValue("kind", out var k) && k is string name)
                    counts[name] = Convert.ToInt64(row.TryGetValue("n", out var n) ? n ?? 0 : 0);
            }
            return new { asOf = nowUtc, events = rows, counts24h = counts };
        }

        internal static async Task RegisterConsoleRoutesAsync(OmniDefence parent)
        {
            // Typed KliveAPI, as the fingerprint routes: reflection-based registration has failed
            // silently and partially before.
            var apis = await parent.GetServicesByType<KliveAPI.KliveAPI>();
            var api = apis != null && apis.Length > 0 ? (KliveAPI.KliveAPI)apis[0] : null;
            Task Route(string path, Func<KliveAPI.KliveAPI.UserRequest, Task> handler, PermissionDef perm)
                => api != null ? api.CreateRoute(path, handler, HttpMethod.Get, perm) : parent.CreateAPIRoute(path, handler, HttpMethod.Get, perm);

            await Route("/omnidefence/ip-map/compact", async req =>
            {
                // The records change with every request: never from the shared response cache.
                CacheDeps.MarkUncacheable("live OmniDefence IP records");
                long? since = long.TryParse(req.userParameters.Get("since"), out long s) && s > 0 ? s : null;
                object body = CompactIpMap(parent.Tracker.All(), since, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await req.ReturnResponse(JsonConvert.SerializeObject(body), "application/json");
            }, OmniDefencePerms.OverviewView);

            await Route("/omnidefence/events", async req =>
            {
                CacheDeps.MarkUncacheable("live OmniDefence IP events");
                object body = await ReadEventsAsync(parent.Store, req.userParameters, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await req.ReturnResponse(JsonConvert.SerializeObject(body), "application/json");
            }, OmniDefencePerms.TrafficRead);
        }
    }
}
