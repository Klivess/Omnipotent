using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniDefence.Fingerprint;
using System.Net;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>Fingerprinting routes: classification detail, overrides, settings, and the public beacon/robots endpoints.</summary>
    internal static partial class OmniDefenceRoutes
    {
        private static async Task RegisterFingerprintRoutesAsync(OmniDefence parent)
        {
            // Registered on the typed KliveAPI instance: reflection-based registration has
            // failed silently before, and the public beacon routes must exist for the site.
            var apis = await parent.GetServicesByType<KliveAPI.KliveAPI>();
            var api = apis != null && apis.Length > 0 ? (KliveAPI.KliveAPI)apis[0] : null;
            Task Route(string path, Func<KliveAPI.KliveAPI.UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions perm, long? maxBody = null)
            {
                if (api == null) return parent.CreateAPIRoute(path, handler, method, perm);
                return maxBody.HasValue ? api.CreateBufferedRoute(path, handler, method, perm, maxBody.Value) : api.CreateRoute(path, handler, method, perm);
            }

            // ── public ──

            await Route("/robots.txt", async req =>
            {
                await req.ReturnResponse(parent.BuildRobotsTxt(), "text/plain");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);

            await Route("/omnidefence/fp/nonce", async req =>
            {
                // Per-IP value: must never be served from the shared response cache.
                CacheDeps.MarkUncacheable("per-IP beacon nonce");
                string ip = OmniDefence.ExtractClientIp(req.req);
                bool enabled = parent.Settings.BeaconEnabled && parent.Settings.FingerprintEnabled;
                await req.ReturnResponse(JsonConvert.SerializeObject(new { enabled, nonce = enabled ? parent.IssueBeaconNonce(ip) : null }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);

            await Route("/omnidefence/fp", async req =>
            {
                string ip = OmniDefence.ExtractClientIp(req.req);
                if (!parent.TryTakeBeaconSlot(ip))
                {
                    await req.ReturnResponse("{\"ok\":false}", "application/json", null, (HttpStatusCode)429);
                    return;
                }
                JObject? payload = null;
                try { payload = JObject.Parse(req.userMessageContent ?? ""); } catch { }
                if (payload == null || !parent.ValidateBeaconNonce(ip, payload.Value<string?>("nonce")))
                {
                    await req.ReturnResponse("{\"ok\":false,\"reason\":\"nonce\"}", "application/json", null, HttpStatusCode.Forbidden);
                    return;
                }
                bool ok = parent.IngestBeacon(ip, req.req.UserAgent, payload);
                await req.ReturnResponse(ok ? "{\"ok\":true}" : "{\"ok\":false}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Anybody, OmniDefence.BeaconMaxBytes);

            // ── Klives ──

            await Route("/omnidefence/classes", async req =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(IpClassInfo.All.Select(i => new
                {
                    id = i.Class.ToString(),
                    label = i.Label,
                    color = i.Color,
                    malicious = i.Malicious,
                    description = i.Description
                })), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            await Route("/omnidefence/ip/fingerprint", async req =>
            {
                string? ip = req.userParameters.Get("ip");
                if (string.IsNullOrWhiteSpace(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                ip = ip.Trim();

                var engine = parent.Fingerprints;
                var intelSvc = parent.Intel;
                string? fpJson = engine?.SnapshotJson(ip);
                var record = parent.Tracker.Get(ip);
                object? intel = null;
                if (engine != null && intelSvc != null)
                {
                    intel = engine.Read(ip, fp => intelSvc.Compose(ip, record, fp));
                }

                var devices = await parent.Store.DevicesForIpAsync(ip);
                var deviceIds = devices.Select(d => d["device_id"] as string).Where(d => d != null).Select(d => d!).ToList();
                var linked = (await parent.Store.IpsForDevicesAsync(deviceIds))
                    .Where(r => !string.Equals(r["ip"] as string, ip, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    ip,
                    record,
                    fingerprint = fpJson == null ? null : JToken.Parse(fpJson),
                    intel,
                    rdns = intelSvc?.GetRdns(ip),
                    abuse = intelSvc?.GetAbuse(ip),
                    greyNoise = intelSvc?.GetGreyNoise(ip),
                    devices,
                    linkedIps = linked
                }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            await Route("/omnidefence/ip/reclassify", async req =>
            {
                var body = ParseJsonBody(req);
                var engine = parent.Fingerprints;
                if (engine == null) { await req.ReturnResponse("Fingerprinting not running", "text/plain", null, HttpStatusCode.ServiceUnavailable); return; }
                if (ReadBoolean(body, "all")) engine.ReclassifyAll();
                else
                {
                    string ip = (body.GetValueOrDefault("ip") as string ?? "").Trim();
                    if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                    engine.Reclassify(ip);
                }
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await Route("/omnidefence/ip/class", async req =>
            {
                var body = ParseJsonBody(req);
                string ip = (body.GetValueOrDefault("ip") as string ?? "").Trim();
                string? cls = (body.GetValueOrDefault("class") as string)?.Trim();
                var engine = parent.Fingerprints;
                if (engine == null) { await req.ReturnResponse("Fingerprinting not running", "text/plain", null, HttpStatusCode.ServiceUnavailable); return; }
                if (string.IsNullOrEmpty(ip)) { await req.ReturnResponse("Missing ip", "text/plain", null, HttpStatusCode.BadRequest); return; }
                IpClass parsed = IpClass.Unknown;
                bool clear = string.IsNullOrEmpty(cls) || cls.Equals("auto", StringComparison.OrdinalIgnoreCase);
                if (!clear && !IpClassInfo.TryParse(cls, out parsed)) { await req.ReturnResponse("Invalid class", "text/plain", null, HttpStatusCode.BadRequest); return; }

                engine.Post(ip, fp => fp.ManualClass = clear ? null : parsed.ToString());
                await parent.RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = ip,
                    Kind = clear ? "ClassOverrideCleared" : "ClassOverride:" + parsed,
                    ActorProfileId = req.user?.UserID,
                    ActorProfileName = req.user?.Name,
                    Detail = clear ? "Returned to automatic classification" : $"Manually labelled {parsed}"
                });
                await req.ReturnResponse("{\"ok\":true}", "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await Route("/omnidefence/fingerprint/status", async req =>
            {
                var engine = parent.Fingerprints;
                var intelSvc = parent.Intel;
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    running = engine != null,
                    fingerprints = engine?.Count ?? 0,
                    backlog = engine?.Backlog ?? 0,
                    signalsFolded = engine?.SignalsFolded ?? 0,
                    classifications = engine?.Classifications ?? 0,
                    backfillRunning = engine?.BackfillRunning ?? false,
                    backfillRowsFolded = engine?.BackfillRowsFolded ?? 0,
                    classBreakdown = ClassBreakdown(parent),
                    tagBreakdown = TagBreakdown(parent),
                    intel = intelSvc == null ? null : new
                    {
                        datacenterRanges = intelSvc.DatacenterRanges,
                        crawlerRanges = intelSvc.CrawlerRanges,
                        torExits = intelSvc.TorExits,
                        feeds = intelSvc.FeedLoadedUtc.ToDictionary(kv => kv.Key, kv => kv.Value == DateTime.MinValue ? "cached" : kv.Value.ToString("O")),
                        usageToday = intelSvc.DailyUsage
                    },
                    settings = await FingerprintSettingsView(parent, parent.Settings)
                }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        }

        // ── helpers ──

        private static Dictionary<string, int> ClassBreakdown(OmniDefence parent)
        {
            var counts = IpClassInfo.All.ToDictionary(i => i.Class.ToString(), _ => 0);
            foreach (var rec in parent.Tracker.All())
            {
                string cls = rec.Classification ?? nameof(IpClass.Unknown);
                counts[cls] = counts.TryGetValue(cls, out int n) ? n + 1 : 1;
            }
            return counts;
        }

        private static Dictionary<string, int> TagBreakdown(OmniDefence parent)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in parent.Tracker.All())
            {
                string? tags = rec.ClassTags;
                if (string.IsNullOrEmpty(tags)) continue;
                foreach (var raw in tags.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Group "Datacenter:AWS" under "Datacenter", "Abuse:80" under "Abuse", etc.
                    string tag = raw.Split(':')[0];
                    counts[tag] = counts.TryGetValue(tag, out int n) ? n + 1 : 1;
                }
            }
            return counts;
        }

        private static async Task<object> FingerprintSettingsView(OmniDefence parent, OmniDefenceSettings st) => new
        {
            enabled = st.FingerprintEnabled,
            classScoreDeltas = st.ClassScoreDeltas,
            autoActions = st.AutoActions,
            allowedActions = OmniDefenceSettings.AllowedActions,
            actionableClasses = OmniDefenceSettings.ActionableClasses,
            autoActionMinConfidence = st.AutoActionMinConfidence,
            robotsTrapEnabled = st.RobotsTrapEnabled,
            robotsTrapPath = st.RobotsTrapPath,
            intelReverseDns = st.IntelReverseDns,
            intelFeeds = st.IntelFeeds,
            intelAbuseIpDb = st.IntelAbuseIpDb,
            intelGreyNoise = st.IntelGreyNoise,
            abuseIpDbDailyBudget = st.AbuseIpDbDailyBudget,
            greyNoiseDailyBudget = st.GreyNoiseDailyBudget,
            beaconEnabled = st.BeaconEnabled,
            // Only whether a key is set; never the key.
            abuseIpDbKeySet = await parent.HasIntelApiKeyAsync("AbuseIPDBKey"),
            greyNoiseKeySet = await parent.HasIntelApiKeyAsync("GreyNoiseKey"),
        };

        private static void ApplyFingerprintSettings(OmniDefenceSettings next, Dictionary<string, object?> body)
        {
            if (!body.TryGetValue("fingerprint", out var raw) || raw == null) return;
            JObject? fp = raw as JObject ?? (raw is string str ? TryParseObject(str) : JObject.FromObject(raw));
            if (fp == null) return;

            bool? Bool(string key) => fp[key]?.Type == JTokenType.Boolean ? fp.Value<bool>(key) : null;
            if (Bool("enabled") is bool en) next.FingerprintEnabled = en;
            if (Bool("classScoreDeltas") is bool csd) next.ClassScoreDeltas = csd;
            if (Bool("robotsTrapEnabled") is bool rt) next.RobotsTrapEnabled = rt;
            if (Bool("intelReverseDns") is bool ir) next.IntelReverseDns = ir;
            if (Bool("intelFeeds") is bool ifd) next.IntelFeeds = ifd;
            if (Bool("intelAbuseIpDb") is bool ia) next.IntelAbuseIpDb = ia;
            if (Bool("intelGreyNoise") is bool ig) next.IntelGreyNoise = ig;
            if (Bool("beaconEnabled") is bool be) next.BeaconEnabled = be;
            if (fp["autoActionMinConfidence"] is JValue conf && conf.Type is JTokenType.Float or JTokenType.Integer) next.AutoActionMinConfidence = conf.Value<double>();
            if (fp["abuseIpDbDailyBudget"] is JValue ab && ab.Type == JTokenType.Integer) next.AbuseIpDbDailyBudget = ab.Value<int>();
            if (fp["greyNoiseDailyBudget"] is JValue gb && gb.Type == JTokenType.Integer) next.GreyNoiseDailyBudget = gb.Value<int>();
            if (fp["autoActions"] is JObject actions)
            {
                foreach (var prop in actions.Properties())
                {
                    if (OmniDefenceSettings.ActionableClasses.Contains(prop.Name)) next.AutoActions[prop.Name] = prop.Value.ToString();
                }
            }
        }

        private static JObject? TryParseObject(string s)
        {
            try { return JObject.Parse(s); } catch { return null; }
        }
    }
}
