using DSharpPlus.Entities;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Cyberdefence service for Omnipotent. Tracks every API request, every
    /// authentication event, every KM profile action, and every IP that talks
    /// to KliveAPI. Provides Klives-only routes to query/filter the data and
    /// to ban / watch / honeypot / scan attacker IPs.
    /// </summary>
    public class OmniDefence : OmniService
    {
        private OmniDefenceStore store = null!;
        private IpThreatTracker tracker = null!;
        private OmniDefenceScanner scanner = new();
        private static readonly HttpClient GeoIpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        // Discord notification dedupe state (separate from DB so it survives intra-request scenarios fast)
        private readonly ConcurrentDictionary<string, byte> recentlyScannedIps = new();
        private readonly ConcurrentDictionary<string, DateTime> lastScanByIp = new();
        private readonly ConcurrentDictionary<string, byte> geoLookupsInFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> geoLookupCooldown = new(StringComparer.OrdinalIgnoreCase);

        // Cached set of routes registered as honeypots (route -> response kind)
        private readonly ConcurrentDictionary<string, string> honeypotRoutes = new(StringComparer.OrdinalIgnoreCase);

        // Cached set of blocked geographic regions (id -> row).
        private readonly ConcurrentDictionary<long, BlockedRegionRow> blockedRegions = new();

        public OmniDefence()
        {
            name = "OmniDefence";
            threadAnteriority = ThreadAnteriority.High;
        }

        public OmniDefenceStore Store => store;
        public IpThreatTracker Tracker => tracker;
        public OmniDefenceScanner Scanner => scanner;

        protected override async void ServiceMain()
        {
            try
            {
                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory));
                store = new OmniDefenceStore(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDatabaseFile));
                await store.InitializeAsync();

                tracker = new IpThreatTracker(store);
                await tracker.LoadAsync();
                await BackfillProfileAssociationsAsync();

                foreach (var hp in await store.ListHoneypotRoutesAsync())
                {
                    honeypotRoutes[hp.Route] = hp.ResponseKind;
                }

                foreach (var region in await store.ListBlockedRegionsAsync())
                {
                    blockedRegions[region.Id] = region;
                }

                await ServiceLog($"OmniDefence active. Loaded {tracker.All().Count()} IP records, {honeypotRoutes.Count} honeypot routes, {blockedRegions.Count} blocked regions.");

                // Periodic flush so threat scores survive restarts.
                _ = PeriodicFlushLoop();

                await OmniDefenceRoutes.RegisterAsync(this);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence failed to start.");
            }
        }

        private async Task PeriodicFlushLoop()
        {
            while (cancellationToken == null || !cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken?.Token ?? CancellationToken.None); }
                catch (OperationCanceledException) { return; }
                try { await tracker.PersistAllDirtyAsync(); }
                catch (Exception ex) { await ServiceLogError(ex, "OmniDefence periodic flush failed."); }
            }
        }

        // ------------------------------------------------------------------
        //  Pre-dispatch hook from KliveAPI
        // ------------------------------------------------------------------

        public IpThreatTracker.GateDecision EvaluateRequestGate(string ip, string route)
        {
            if (string.IsNullOrEmpty(ip)) return IpThreatTracker.GateDecision.Allow;
            if (tracker.IsLinkedToKlives(ip)) return IpThreatTracker.GateDecision.Allow;
            // Honeypot routes always honeypot regardless of IP status.
            if (honeypotRoutes.ContainsKey(route)) return IpThreatTracker.GateDecision.Honeypot;
            return tracker.Evaluate(ip);
        }

        public bool IsLinkedToKlives(string ip) => tracker.IsLinkedToKlives(ip);

        public bool IsHoneypotRoute(string route) => honeypotRoutes.ContainsKey(route);

        // ------------------------------------------------------------------
        //  Recording API (called from KliveAPI ProcessRequestAsync / KMProfileManager etc.)
        // ------------------------------------------------------------------

        public async Task RecordRequestAsync(RequestRow row, RequestOutcome outcome)
        {
            try
            {
                if (!string.IsNullOrEmpty(row.Ip))
                {
                    tracker.LinkProfile(tracker.GetOrCreate(row.Ip!), row.ProfileId, row.ProfileName, row.ProfileRank, row.UtcTimestamp);
                    var rec = tracker.RecordOutcome(row.Ip!, outcome);
                    _ = EnrichIpRecordAsync(rec);
                    if (string.IsNullOrWhiteSpace(rec.AssociatedProfileId)) _ = MaybeAlertAsync(rec, row);
                }
                await store.InsertRequestAsync(row);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence RecordRequestAsync failed.");
            }
        }

        public async Task RecordAuthEventAsync(AuthEventRow row)
        {
            try { await store.InsertAuthEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordAuthEventAsync failed."); }
        }

        public async Task RecordProfileAction(KMProfileManager.KMProfile? profile, string category, string action, object? detail = null, string? ip = null)
        {
            try
            {
                var row = new ProfileActionRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ProfileId = profile?.UserID,
                    ProfileName = profile?.Name,
                    Ip = ip,
                    Category = category ?? "",
                    Action = action ?? "",
                    DetailJson = detail == null ? null : JsonConvert.SerializeObject(detail)
                };
                await store.InsertProfileActionAsync(row);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordProfileAction failed."); }
        }

        public async Task RecordIpEventAsync(IpEventRow row)
        {
            try { await store.InsertIpEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordIpEventAsync failed."); }
        }

        private async Task BackfillProfileAssociationsAsync()
        {
            try
            {
                var rows = await store.QueryAsync(@"
                    SELECT r.ip, r.profile_id, r.profile_name, r.profile_rank, r.utc_ts
                    FROM requests r
                    INNER JOIN (
                        SELECT ip, MAX(utc_ts) AS last_profile_seen
                        FROM requests
                        WHERE ip IS NOT NULL AND ip <> '' AND profile_id IS NOT NULL AND profile_id <> ''
                        GROUP BY ip
                    ) latest ON latest.ip = r.ip AND latest.last_profile_seen = r.utc_ts
                    WHERE r.profile_id IS NOT NULL AND r.profile_id <> ''",
                    new());

                foreach (var row in rows)
                {
                    string? ip = row.TryGetValue("ip", out var rawIp) ? rawIp as string : null;
                    string? profileId = row.TryGetValue("profile_id", out var rawProfileId) ? rawProfileId as string : null;
                    if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(profileId)) continue;

                    string? profileName = row.TryGetValue("profile_name", out var rawProfileName) ? rawProfileName as string : null;
                    int? profileRank = row.TryGetValue("profile_rank", out var rawProfileRank) && rawProfileRank != null ? Convert.ToInt32(rawProfileRank) : null;
                    long seenUtc = row.TryGetValue("utc_ts", out var rawSeen) && rawSeen != null ? Convert.ToInt64(rawSeen) : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    tracker.LinkProfile(tracker.GetOrCreate(ip), profileId, profileName, profileRank, seenUtc);
                }

                await tracker.PersistAllDirtyAsync();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence profile association backfill failed.");
            }
        }

        private async Task EnrichIpRecordAsync(IpRecord rec)
        {
            if (rec == null || string.IsNullOrWhiteSpace(rec.Ip)) return;
            lock (rec)
            {
                bool hasDetailedGeo = (!string.IsNullOrWhiteSpace(rec.City) || !string.IsNullOrWhiteSpace(rec.Region) || !string.IsNullOrWhiteSpace(rec.Isp) || !string.IsNullOrWhiteSpace(rec.Org)) && rec.Latitude.HasValue && rec.Longitude.HasValue;
                bool isKnownPrivate = string.Equals(rec.Country, "Private", StringComparison.OrdinalIgnoreCase) && string.Equals(rec.Asn, "Local", StringComparison.OrdinalIgnoreCase);
                if (hasDetailedGeo || isKnownPrivate) return;
            }

            if (!geoLookupsInFlight.TryAdd(rec.Ip, 0)) return;
            try
            {
                if (geoLookupCooldown.TryGetValue(rec.Ip, out var lastFail) && DateTime.UtcNow - lastFail < TimeSpan.FromHours(6)) return;

                if (!IPAddress.TryParse(rec.Ip, out var address) || IsPrivateOrLocalAddress(address))
                {
                    lock (rec)
                    {
                        rec.Country = "Private";
                        rec.Asn = "Local";
                        rec.City = null;
                        rec.Region = null;
                        rec.Isp = null;
                        rec.Org = null;
                        rec.Latitude = null;
                        rec.Longitude = null;
                    }
                    await tracker.PersistAsync(rec);
                    return;
                }

                string url = $"http://ip-api.com/json/{rec.Ip}?fields=status,country,countryCode,regionName,city,zip,isp,org,as,lat,lon,timezone,query,message";
                string json = await GeoIpClient.GetStringAsync(url);
                var result = JsonConvert.DeserializeObject<GeoIpResponse>(json);
                if (result?.Status == "success")
                {
                    lock (rec)
                    {
                        rec.Country = string.IsNullOrWhiteSpace(result.CountryCode) ? result.Country : result.CountryCode;
                        rec.Asn = result.Asn;
                        rec.City = result.City;
                        rec.Region = result.RegionName;
                        rec.Isp = result.Isp;
                        rec.Org = result.Org;
                        rec.Latitude = result.Latitude;
                        rec.Longitude = result.Longitude;
                    }
                    await tracker.PersistAsync(rec);
                    await RecordIpEventAsync(new IpEventRow
                    {
                        UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Ip = rec.Ip,
                        Kind = "GeoIP",
                        Detail = JsonConvert.SerializeObject(new { result.Country, result.CountryCode, result.RegionName, result.City, result.Zip, result.Isp, result.Org, result.Asn, result.Latitude, result.Longitude, result.Timezone })
                    });
                    await EnforceBlockedRegionsAsync(rec);
                }
                else
                {
                    geoLookupCooldown[rec.Ip] = DateTime.UtcNow;
                }
            }
            catch
            {
                geoLookupCooldown[rec.Ip] = DateTime.UtcNow;
            }
            finally
            {
                geoLookupsInFlight.TryRemove(rec.Ip, out _);
            }
        }

        private static bool IsPrivateOrLocalAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.ToString().StartsWith("fc", StringComparison.OrdinalIgnoreCase) || address.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private sealed class GeoIpResponse
        {
            [JsonProperty("status")]
            public string? Status { get; set; }

            [JsonProperty("country")]
            public string? Country { get; set; }

            [JsonProperty("countryCode")]
            public string? CountryCode { get; set; }

            [JsonProperty("regionName")]
            public string? RegionName { get; set; }

            [JsonProperty("city")]
            public string? City { get; set; }

            [JsonProperty("zip")]
            public string? Zip { get; set; }

            [JsonProperty("isp")]
            public string? Isp { get; set; }

            [JsonProperty("org")]
            public string? Org { get; set; }

            [JsonProperty("timezone")]
            public string? Timezone { get; set; }

            [JsonProperty("as")]
            public string? Asn { get; set; }

            [JsonProperty("lat")]
            public double? Latitude { get; set; }

            [JsonProperty("lon")]
            public double? Longitude { get; set; }
        }

        // ------------------------------------------------------------------
        //  Discord notifier (replaces the per-request spam)
        // ------------------------------------------------------------------

        private async Task MaybeAlertAsync(IpRecord rec, RequestRow row)
        {
            try
            {
                if (rec == null) return;
                if (row.DenyReason == null && row.StatusCode < 400) return; // only alert on suspicious activity

                bool first;
                int? escalation = null;
                lock (rec)
                {
                    first = rec.FirstAlertedUtc == null && rec.UnauthAttempts >= tracker.FirstAlertThreshold;
                    if (first)
                    {
                        rec.FirstAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        rec.LastAlertedUtc = rec.FirstAlertedUtc;
                        rec.EscalationLevel = 1;
                    }
                    else if (rec.EscalationLevel < 2 && rec.UnauthAttempts >= tracker.Escalation2Threshold)
                    {
                        rec.EscalationLevel = 2;
                        escalation = 2;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                    else if (rec.EscalationLevel < 3 && rec.UnauthAttempts >= tracker.Escalation3Threshold)
                    {
                        rec.EscalationLevel = 3;
                        escalation = 3;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                }

                if (first)
                {
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: New attacker detected ({rec.Ip})",
                        BuildAlertDescription(rec, row, "First detection"),
                        DiscordColor.Yellow);
                    await SendDiscordBuilderSafeAsync(builder);
                }
                if (escalation.HasValue)
                {
                    var color = escalation == 3 ? DiscordColor.Red : DiscordColor.Orange;
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: Escalation L{escalation} ({rec.Ip})",
                        BuildAlertDescription(rec, row, $"Escalated to level {escalation}"),
                        color);
                    await SendDiscordBuilderSafeAsync(builder);
                }
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence MaybeAlertAsync failed."); }
        }

        private static string BuildAlertDescription(IpRecord rec, RequestRow row, string reason)
        {
            return $"**Reason:** {reason}\n" +
                   $"**IP:** `{rec.Ip}`\n" +
                   $"**Threat score:** {rec.ThreatScore:F0}\n" +
                   $"**Unauth attempts:** {rec.UnauthAttempts}\n" +
                   $"**Total requests:** {rec.TotalRequests}\n" +
                   $"**Status:** {rec.Status}\n" +
                   $"**Last route:** `{row.Method} {row.Route}`\n" +
                   $"**User-agent:** {row.UserAgent ?? "(none)"}";
        }

        public async Task SendBlockNotificationAsync(IpRecord rec, string actor)
        {
            try
            {
                var builder = KliveBotDiscord.MakeSimpleEmbed(
                    $"OmniDefence: IP blocked ({rec.Ip})",
                    $"**Actor:** {actor}\n" +
                    $"**Reason:** {rec.LastBlockReason ?? "(none)"}\n" +
                    $"**Threat score:** {rec.ThreatScore:F0}\n" +
                    $"**Unauth attempts:** {rec.UnauthAttempts}",
                    DiscordColor.DarkRed);
                await SendDiscordBuilderSafeAsync(builder);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence SendBlockNotificationAsync failed."); }
        }

        private async Task SendDiscordBuilderSafeAsync(DiscordMessageBuilder builder)
        {
            try
            {
                await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", builder);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  Honeypot helpers (used by KliveAPI integration to pick a response)
        // ------------------------------------------------------------------

        public string GenerateHoneypotResponse(string ip)
        {
            return JunkPayloadGenerator.GenerateJunkJson(ip ?? "unknown");
        }

        public Task RegisterHoneypotRouteAsync(string route, string? note)
        {
            honeypotRoutes[route] = "JunkJson";
            return store.UpsertHoneypotRouteAsync(new HoneypotRouteRow
            {
                Route = route,
                CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ResponseKind = "JunkJson",
                Note = note
            });
        }

        public Task RemoveHoneypotRouteAsync(string route)
        {
            honeypotRoutes.TryRemove(route, out _);
            return store.DeleteHoneypotRouteAsync(route);
        }

        public IReadOnlyDictionary<string, string> HoneypotRouteSnapshot() =>
            new Dictionary<string, string>(honeypotRoutes, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        //  Blocked region helpers
        // ------------------------------------------------------------------

        public IReadOnlyCollection<BlockedRegionRow> BlockedRegionsSnapshot() => blockedRegions.Values.ToList();

        public void RegisterBlockedRegion(BlockedRegionRow row)
        {
            if (row == null || row.Id <= 0) return;
            blockedRegions[row.Id] = row;
        }

        public void UnregisterBlockedRegion(long id)
        {
            blockedRegions.TryRemove(id, out _);
        }

        public bool IsLatLonInBlockedRegion(double latitude, double longitude, out BlockedRegionRow? region)
        {
            foreach (var r in blockedRegions.Values)
            {
                if (latitude >= r.LatMin && latitude <= r.LatMax && longitude >= r.LonMin && longitude <= r.LonMax)
                {
                    region = r;
                    return true;
                }
            }
            region = null;
            return false;
        }

        private async Task EnforceBlockedRegionsAsync(IpRecord rec)
        {
            if (rec == null || !rec.Latitude.HasValue || !rec.Longitude.HasValue) return;
            if (IsLinkedToKlives(rec.Ip)) return;
            if (string.Equals(rec.Status, nameof(IpThreatTracker.IpStatus.Blocked), StringComparison.OrdinalIgnoreCase)) return;
            if (!IsLatLonInBlockedRegion(rec.Latitude.Value, rec.Longitude.Value, out var region) || region == null) return;
            string reason = $"Region block #{region.Id}: {region.Reason}";
            lock (rec)
            {
                rec.Status = nameof(IpThreatTracker.IpStatus.Blocked);
                rec.LastBlockReason = reason;
            }
            await tracker.PersistAsync(rec);
            await RecordIpEventAsync(new IpEventRow
            {
                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ip = rec.Ip,
                Kind = "RegionBlock",
                Detail = reason
            });
        }

        // ------------------------------------------------------------------
        //  Convenience helpers
        // ------------------------------------------------------------------

        public static string ExtractClientIp(HttpListenerRequest req)
        {
            if (req == null) return "";
            try
            {
                string? remote = req.RemoteEndPoint?.Address?.ToString();
                // Honour X-Forwarded-For only when the request hits us locally
                // (i.e. a known reverse proxy on this box). Anything else and we
                // trust the socket peer.
                if (remote == "127.0.0.1" || remote == "::1")
                {
                    string? xff = req.Headers["X-Forwarded-For"];
                    if (!string.IsNullOrWhiteSpace(xff))
                    {
                        var first = xff.Split(',')[0].Trim();
                        if (!string.IsNullOrEmpty(first)) return first;
                    }
                }
                return remote ?? "";
            }
            catch { return ""; }
        }

        public static string HashBody(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0) return "";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bodyBytes));
        }

        /// <summary>
        /// Throttles port scans to one per IP per hour.
        /// </summary>
        public bool TryAcquireScanSlot(string ip)
        {
            var now = DateTime.UtcNow;
            if (lastScanByIp.TryGetValue(ip, out var last) && (now - last) < TimeSpan.FromHours(1))
            {
                return false;
            }
            lastScanByIp[ip] = now;
            return true;
        }
    }
}
