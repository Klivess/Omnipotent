using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>
    /// Slow/external IP intelligence: reverse DNS with forward confirmation, published range
    /// lists (Tor, clouds, crawlers), and the free keyed reputation APIs. Everything here is
    /// cached and looked up asynchronously; <see cref="Compose"/> only ever reads memory, so
    /// the classifier never waits on the network.
    /// </summary>
    public sealed class IpIntelService
    {
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniDefence/1.0 (+https://klive.dev)");
            return client;
        }

        public static readonly TimeSpan RdnsTtl = TimeSpan.FromDays(7);
        public static readonly TimeSpan KeyedTtl = TimeSpan.FromDays(7);
        public static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(4);

        // ── crawler verification ──
        /// <summary>rDNS suffixes that prove a crawler (FCrDNS). Crawlers absent here are verified by range only.</summary>
        public static readonly Dictionary<string, string[]> CrawlerRdnsSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Googlebot"] = new[] { ".googlebot.com", ".google.com", ".googleusercontent.com" },
            ["Bingbot"] = new[] { ".search.msn.com" },
            ["Applebot"] = new[] { ".applebot.apple.com" },
            ["YandexBot"] = new[] { ".yandex.ru", ".yandex.net", ".yandex.com" },
            ["Baiduspider"] = new[] { ".baidu.com", ".baidu.jp" },
            ["Amazonbot"] = new[] { ".crawl.amazonbot.amazon" },
            ["PetalBot"] = new[] { ".petalsearch.com" },
            ["SeznamBot"] = new[] { ".seznam.cz" },
        };

        /// <summary>Survey-scanner organisations by rDNS suffix.</summary>
        public static readonly (string Suffix, string Org)[] ResearchRdns =
        {
            (".censys-scanner.com", "Censys"), (".shodan.io", "Shodan"), (".shadowserver.org", "Shadowserver"),
            (".binaryedge.ninja", "BinaryEdge"), (".stretchoid.com", "Stretchoid"), (".internet-measurement.com", "InternetMeasurement"),
            (".onyphe.net", "ONYPHE"), (".leakix.net", "LeakIX"), (".alphastrike.io", "Alpha Strike Labs"),
            (".netsystemsresearch.com", "Net Systems Research"), (".recyber.net", "Recyber"), (".rapid7.com", "Rapid7 Sonar"),
            (".bitsight.net", "BitSight"), (".criminalip.com", "Criminal IP"), (".ipip.net", "IPIP"), (".internettl.org", "InternetTL"),
            (".arbor-observatory.com", "Arbor Observatory"), (".modat.io", "Modat"), (".driftnet.io", "Driftnet"), (".academic-scanner.org", "Academic scanner"),
        };

        /// <summary>Survey-scanner organisations by AS name / org / ISP substring.</summary>
        public static readonly (string Needle, string Org)[] ResearchOrgs =
        {
            ("censys", "Censys"), ("shodan", "Shodan"), ("shadowserver", "Shadowserver"), ("binaryedge", "BinaryEdge"),
            ("stretchoid", "Stretchoid"), ("internet measurement", "InternetMeasurement"), ("onyphe", "ONYPHE"), ("leakix", "LeakIX"),
            ("alpha strike", "Alpha Strike Labs"), ("recyber", "Recyber"), ("driftnet", "Driftnet"), ("criminal ip", "Criminal IP"),
            ("bitsight", "BitSight"), ("rapid7", "Rapid7"), ("palo alto networks", "Palo Alto Xpanse"), ("modat", "Modat"),
        };

        // ── feeds ──
        public sealed record Feed(string Name, string Url, string Label, FeedKind Kind, TimeSpan Refresh);
        public enum FeedKind { TorList, AwsJson, GcpJson, OracleJson, CsvFirstColumn, PlainCidrs, GoogleStyleJson }

        public static readonly Feed[] DatacenterFeeds =
        {
            new("aws", "https://ip-ranges.amazonaws.com/ip-ranges.json", "AWS", FeedKind.AwsJson, TimeSpan.FromDays(1)),
            new("gcp", "https://www.gstatic.com/ipranges/cloud.json", "Google Cloud", FeedKind.GcpJson, TimeSpan.FromDays(1)),
            new("oracle", "https://docs.oracle.com/en-us/iaas/tools/public_ip_ranges.json", "Oracle Cloud", FeedKind.OracleJson, TimeSpan.FromDays(1)),
            new("digitalocean", "https://digitalocean.com/geo/google.csv", "DigitalOcean", FeedKind.CsvFirstColumn, TimeSpan.FromDays(1)),
            new("linode", "https://geoip.linode.com/", "Linode", FeedKind.CsvFirstColumn, TimeSpan.FromDays(1)),
            new("cloudflare4", "https://www.cloudflare.com/ips-v4", "Cloudflare", FeedKind.PlainCidrs, TimeSpan.FromDays(1)),
            new("cloudflare6", "https://www.cloudflare.com/ips-v6", "Cloudflare", FeedKind.PlainCidrs, TimeSpan.FromDays(1)),
        };

        public static readonly Feed[] CrawlerFeeds =
        {
            new("googlebot", "https://developers.google.com/static/search/apis/ipranges/googlebot.json", "Googlebot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("google-special", "https://developers.google.com/static/search/apis/ipranges/special-crawlers.json", "Googlebot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("bingbot", "https://www.bing.com/toolbox/bingbot.json", "Bingbot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("applebot", "https://search.developer.apple.com/applebot.json", "Applebot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("gptbot", "https://openai.com/gptbot.json", "GPTBot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("chatgpt-user", "https://openai.com/chatgpt-user.json", "GPTBot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
            new("oai-searchbot", "https://openai.com/searchbot.json", "GPTBot", FeedKind.GoogleStyleJson, TimeSpan.FromDays(1)),
        };

        public static readonly Feed TorFeed = new("tor", "https://check.torproject.org/torbulkexitlist", "Tor", FeedKind.TorList, TimeSpan.FromHours(1));

        /// <summary>Crawlers we can verify by published range (in addition to rDNS).</summary>
        public static readonly HashSet<string> RangeVerifiedCrawlers = new(CrawlerFeeds.Select(f => f.Label), StringComparer.OrdinalIgnoreCase);

        private volatile CidrSet _datacenter = CidrSet.Empty;
        private volatile CidrSet _crawlers = CidrSet.Empty;
        private volatile CidrSet _tor = CidrSet.Empty;
        private readonly ConcurrentDictionary<string, CidrSet> _feedSets = new();
        private readonly ConcurrentDictionary<string, DateTime> _feedLoadedUtc = new();
        public bool CrawlerFeedsLoaded { get; private set; }

        // ── per-IP cache ──
        public sealed class RdnsResult
        {
            public string? Host;
            public bool ForwardConfirmed;
            public long FetchedUtc;
        }

        public sealed class AbuseResult
        {
            public int Score;
            public int Reports;
            public string? UsageType;
            public bool IsTor;
            public string? Domain;
            public long FetchedUtc;
        }

        public sealed class GreyNoiseResult
        {
            public bool Noise;
            public bool Riot;
            public string? Classification;
            public string? Name;
            public long FetchedUtc;
        }

        private readonly ConcurrentDictionary<string, RdnsResult> _rdns = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AbuseResult> _abuse = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, GreyNoiseResult> _greyNoise = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _lookupGate = new(4, 4);
        private readonly ConcurrentDictionary<string, int> _dailyUsage = new();

        private readonly OmniDefenceStore? _store;
        private readonly string? _feedCacheDir;

        public IpIntelService(OmniDefenceStore? store, string? feedCacheDir)
        {
            _store = store;
            _feedCacheDir = feedCacheDir;
        }

        // ── wiring ──
        public Func<long> NowMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public Func<OmniDefenceSettings> Settings { get; set; } = () => new OmniDefenceSettings();
        /// <summary>Resolves an API key by setting name (backed by OmniSettings); null = provider off.</summary>
        public Func<string, Task<string?>> GetApiKey { get; set; } = _ => Task.FromResult<string?>(null);
        /// <summary>Called when new intel for an IP is available (engine reclassifies it).</summary>
        public Action<string>? IntelUpdated { get; set; }
        /// <summary>Called when a feed set changes (engine reclassifies everything).</summary>
        public Action? FeedsUpdated { get; set; }
        public Action<Exception, string>? LogError { get; set; }
        public Action<string>? Log { get; set; }

        public int DatacenterRanges => _datacenter.Count;
        public int CrawlerRanges => _crawlers.Count;
        public int TorExits => _tor.Count;
        public IReadOnlyDictionary<string, DateTime> FeedLoadedUtc => _feedLoadedUtc;
        public IReadOnlyDictionary<string, int> DailyUsage => _dailyUsage;

        // ── composition (engine thread, memory only) ──

        public IpIntel Compose(string ip, IpRecord? rec, IpFingerprint fp)
        {
            var intel = new IpIntel();
            if (rec != null)
            {
                lock (rec)
                {
                    intel.IsHosting = rec.IsHosting;
                    intel.IsProxy = rec.IsProxy;
                    intel.IsMobile = rec.IsMobile;
                    intel.Country = rec.Country;
                    intel.Timezone = rec.Timezone;
                    intel.Asn = rec.Asn;
                    intel.AsName = rec.AsName;
                    intel.Isp = rec.Isp;
                    intel.Org = rec.Org;
                    intel.ReverseDns = rec.ReverseDns;
                }
            }

            intel.DatacenterProvider = _datacenter.Lookup(ip);
            intel.IsTor = _tor.Lookup(ip) != null;
            string? rangeCrawler = _crawlers.Lookup(ip);

            if (_rdns.TryGetValue(ip, out var rdns))
            {
                intel.ReverseDns = rdns.Host ?? intel.ReverseDns;
                intel.ReverseDnsForwardConfirmed = rdns.ForwardConfirmed;
            }

            // Crawler verification: published range, or forward-confirmed rDNS in the crawler's domain.
            string? claimed = fp.ClaimedCrawler;
            if (rangeCrawler != null) intel.VerifiedCrawler = rangeCrawler;
            else if (rdns?.ForwardConfirmed == true && rdns.Host != null)
            {
                foreach (var (crawler, suffixes) in CrawlerRdnsSuffixes)
                {
                    if (suffixes.Any(sfx => rdns.Host.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Google's user-content hosts are only proof for Google's own fetchers.
                        if (rdns.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) && claimed != "Googlebot") continue;
                        intel.VerifiedCrawler = crawler;
                        break;
                    }
                }
            }
            if (claimed != null && intel.VerifiedCrawler == null && IsVerifiable(claimed))
            {
                // Only call it a fake once the check that could have proven it has actually run.
                bool rangeOnly = !CrawlerRdnsSuffixes.ContainsKey(claimed);
                intel.CrawlerVerificationFailed = rangeOnly ? CrawlerFeedsLoaded : rdns != null;
            }

            // Survey scanners by rDNS, then by network owner.
            string? host = intel.ReverseDns;
            if (host != null)
            {
                foreach (var (suffix, org) in ResearchRdns)
                {
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { intel.ResearchScanner = org; break; }
                }
            }
            if (intel.ResearchScanner == null)
            {
                string owner = $"{intel.AsName} {intel.Org} {intel.Isp} {intel.Asn}".ToLowerInvariant();
                foreach (var (needle, org) in ResearchOrgs)
                {
                    if (owner.Contains(needle)) { intel.ResearchScanner = org; break; }
                }
            }

            if (_abuse.TryGetValue(ip, out var abuse))
            {
                intel.AbuseScore = abuse.Score;
                intel.AbuseReports = abuse.Reports;
                intel.AbuseUsageType = abuse.UsageType;
                if (abuse.IsTor) intel.IsTor = true;
            }
            if (_greyNoise.TryGetValue(ip, out var gn))
            {
                intel.GreyNoiseClassification = gn.Noise || gn.Riot ? gn.Classification : null;
                intel.GreyNoiseRiot = gn.Riot;
                intel.GreyNoiseName = gn.Name;
            }
            intel.UpdatedMs = NowMs();
            return intel;
        }

        public static bool IsVerifiable(string crawler) => CrawlerRdnsSuffixes.ContainsKey(crawler) || RangeVerifiedCrawlers.Contains(crawler);

        public RdnsResult? GetRdns(string ip) => _rdns.TryGetValue(ip, out var r) ? r : null;
        public AbuseResult? GetAbuse(string ip) => _abuse.TryGetValue(ip, out var r) ? r : null;
        public GreyNoiseResult? GetGreyNoise(string ip) => _greyNoise.TryGetValue(ip, out var r) ? r : null;

        // ── scheduling (engine thread → async lookups) ──

        /// <summary>Decides which lookups this IP deserves and starts them. Never blocks.</summary>
        public void Consider(IpFingerprint fp, IpRecord? rec, bool allowKeyed = true)
        {
            string ip = fp.Ip;
            if (!IPAddress.TryParse(ip, out var addr) || IsPrivate(addr)) return;
            var settings = Settings();
            long nowUtc = NowMs() / 1000;

            bool isOwner = fp.Class == nameof(IpClass.Owner);
            if (isOwner) return;

            if (settings.IntelReverseDns && (!_rdns.TryGetValue(ip, out var r) || nowUtc - r.FetchedUtc > RdnsTtl.TotalSeconds))
                Start(ip, "rdns", () => LookupRdnsAsync(ip, addr));

            bool relevant = fp.Requests >= 20 || fp.ProbeHits > 0 || fp.HoneypotHits > 0 || fp.AuthFailures > 0 || fp.RobotsTrapHits > 0
                || (fp.Class == nameof(IpClass.Unknown) && fp.Requests >= 3)
                || fp.Class is nameof(IpClass.Scraper) or nameof(IpClass.HeadlessAutomation) or nameof(IpClass.VulnScanner) or nameof(IpClass.CredentialAttacker);
            bool benignKnown = fp.Class is nameof(IpClass.ApiClient) or nameof(IpClass.Human) or nameof(IpClass.VerifiedCrawler)
                || fp.ProfileRequests > 0;
            if (!allowKeyed || !relevant || benignKnown) return;

            if (settings.IntelAbuseIpDb && (!_abuse.TryGetValue(ip, out var a) || nowUtc - a.FetchedUtc > KeyedTtl.TotalSeconds))
                Start(ip, "abuseipdb", () => LookupAbuseIpDbAsync(ip));
            if (settings.IntelGreyNoise && (!_greyNoise.TryGetValue(ip, out var g) || nowUtc - g.FetchedUtc > KeyedTtl.TotalSeconds))
                Start(ip, "greynoise", () => LookupGreyNoiseAsync(ip));
        }

        private void Start(string ip, string provider, Func<Task<bool>> work)
        {
            string key = provider + "|" + ip;
            if (!_inFlight.TryAdd(key, 0)) return;
            _ = Task.Run(async () =>
            {
                await _lookupGate.WaitAsync();
                try
                {
                    if (await work()) IntelUpdated?.Invoke(ip);
                }
                catch (Exception ex) { LogError?.Invoke(ex, $"Intel lookup {provider} for {ip} failed."); }
                finally
                {
                    _lookupGate.Release();
                    // Keep failed keys out for a while so a dead provider isn't hammered per request.
                    _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => _inFlight.TryRemove(key, out byte _));
                }
            });
        }

        private async Task<bool> LookupRdnsAsync(string ip, IPAddress addr)
        {
            var result = new RdnsResult { FetchedUtc = NowMs() / 1000 };
            try
            {
                var ptr = Dns.GetHostEntryAsync(addr);
                if (await Task.WhenAny(ptr, Task.Delay(DnsTimeout)) == ptr && ptr.IsCompletedSuccessfully)
                {
                    string host = ptr.Result.HostName?.TrimEnd('.') ?? "";
                    if (!string.IsNullOrEmpty(host) && !IPAddress.TryParse(host, out _))
                    {
                        result.Host = host.Length > 253 ? host.Substring(0, 253) : host;
                        var fwd = Dns.GetHostAddressesAsync(result.Host);
                        if (await Task.WhenAny(fwd, Task.Delay(DnsTimeout)) == fwd && fwd.IsCompletedSuccessfully)
                        {
                            result.ForwardConfirmed = fwd.Result.Any(a => a.Equals(addr) || (a.IsIPv4MappedToIPv6 && a.MapToIPv4().Equals(addr)));
                        }
                    }
                }
            }
            catch { /* NXDOMAIN etc.: "no PTR" is itself the answer */ }
            _rdns[ip] = result;
            await PersistAsync(ip, "rdns", result);
            return true;
        }

        private async Task<bool> LookupAbuseIpDbAsync(string ip)
        {
            string? key = await GetApiKey("AbuseIPDBKey");
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!TryConsumeBudget("abuseipdb", Settings().AbuseIpDbDailyBudget)) return false;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.abuseipdb.com/api/v2/check?ipAddress={Uri.EscapeDataString(ip)}&maxAgeInDays=90");
            request.Headers.Add("Key", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return false;
            var data = JObject.Parse(await response.Content.ReadAsStringAsync())["data"];
            if (data == null) return false;
            var result = new AbuseResult
            {
                Score = data.Value<int?>("abuseConfidenceScore") ?? 0,
                Reports = data.Value<int?>("totalReports") ?? 0,
                UsageType = data.Value<string?>("usageType"),
                IsTor = data.Value<bool?>("isTor") ?? false,
                Domain = data.Value<string?>("domain"),
                FetchedUtc = NowMs() / 1000
            };
            _abuse[ip] = result;
            await PersistAsync(ip, "abuseipdb", result);
            return true;
        }

        private async Task<bool> LookupGreyNoiseAsync(string ip)
        {
            string? key = await GetApiKey("GreyNoiseKey");
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!TryConsumeBudget("greynoise", Settings().GreyNoiseDailyBudget)) return false;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.greynoise.io/v3/community/{Uri.EscapeDataString(ip)}");
            request.Headers.Add("key", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request);
            // 404 = "not observed scanning the internet", which is itself useful.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound) return false;
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            var result = new GreyNoiseResult
            {
                Noise = body.Value<bool?>("noise") ?? false,
                Riot = body.Value<bool?>("riot") ?? false,
                Classification = body.Value<string?>("classification"),
                Name = body.Value<string?>("name"),
                FetchedUtc = NowMs() / 1000
            };
            _greyNoise[ip] = result;
            await PersistAsync(ip, "greynoise", result);
            return true;
        }

        private bool TryConsumeBudget(string provider, int dailyBudget)
        {
            string day = DateTimeOffset.FromUnixTimeMilliseconds(NowMs()).UtcDateTime.ToString("yyyyMMdd");
            string key = provider + ":" + day;
            int used = _dailyUsage.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (used > dailyBudget)
            {
                _dailyUsage.AddOrUpdate(key, dailyBudget, (_, n) => n - 1);
                return false;
            }
            _ = _store?.SetSettingAsync("intel_usage:" + key, used.ToString());
            return true;
        }

        private async Task PersistAsync(string ip, string provider, object result)
        {
            if (_store == null) return;
            try { await _store.UpsertIntelAsync(ip, provider, NowMs() / 1000, JsonConvert.SerializeObject(result)); }
            catch (Exception ex) { LogError?.Invoke(ex, $"Persisting {provider} intel for {ip} failed."); }
        }

        // ── startup / feeds ──

        public async Task LoadAsync()
        {
            if (_store == null) return;
            long minUtc = NowMs() / 1000 - (long)KeyedTtl.TotalSeconds * 4;
            foreach (var (ip, provider, fetched, json) in await _store.LoadIntelAsync(minUtc))
            {
                if (json == null) continue;
                try
                {
                    switch (provider)
                    {
                        case "rdns": _rdns[ip] = JsonConvert.DeserializeObject<RdnsResult>(json)!; break;
                        case "abuseipdb": _abuse[ip] = JsonConvert.DeserializeObject<AbuseResult>(json)!; break;
                        case "greynoise": _greyNoise[ip] = JsonConvert.DeserializeObject<GreyNoiseResult>(json)!; break;
                    }
                }
                catch { /* stale shape: refetch */ }
            }

            string today = DateTimeOffset.FromUnixTimeMilliseconds(NowMs()).UtcDateTime.ToString("yyyyMMdd");
            foreach (var kv in await _store.LoadSettingsAsync())
            {
                if (kv.Key.StartsWith("intel_usage:", StringComparison.Ordinal) && kv.Key.EndsWith(today, StringComparison.Ordinal) && int.TryParse(kv.Value, out int n))
                    _dailyUsage[kv.Key.Substring("intel_usage:".Length)] = n;
            }

            // Disk snapshots make a restart work offline; the refresh loop replaces them.
            foreach (var feed in DatacenterFeeds.Concat(CrawlerFeeds).Append(TorFeed))
            {
                string? cached = ReadFeedCache(feed);
                if (cached != null) IngestFeed(feed, cached, fromCache: true);
            }
            RebuildSets();
        }

        /// <summary>Refreshes each feed when its interval elapses. Runs until cancelled.</summary>
        public async Task RunFeedLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool changed = false;
                if (Settings().IntelFeeds)
                {
                    foreach (var feed in DatacenterFeeds.Concat(CrawlerFeeds).Append(TorFeed))
                    {
                        if (_feedLoadedUtc.TryGetValue(feed.Name, out var at) && DateTime.UtcNow - at < feed.Refresh) continue;
                        try
                        {
                            string body = await Http.GetStringAsync(feed.Url, ct);
                            if (IngestFeed(feed, body, fromCache: false) > 0)
                            {
                                WriteFeedCache(feed, body);
                                changed = true;
                            }
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            // Retry after a short backoff rather than on every loop.
                            _feedLoadedUtc[feed.Name] = DateTime.UtcNow - feed.Refresh + TimeSpan.FromMinutes(30);
                            Log?.Invoke($"Intel feed {feed.Name} refresh failed: {ex.GetBaseException().Message}");
                        }
                    }
                }
                if (changed)
                {
                    RebuildSets();
                    FeedsUpdated?.Invoke();
                }
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Parses a feed body into its own CIDR set. Returns the number of ranges.</summary>
        public int IngestFeed(Feed feed, string body, bool fromCache)
        {
            var builder = new CidrSet.Builder();
            try
            {
                switch (feed.Kind)
                {
                    case FeedKind.TorList:
                    case FeedKind.PlainCidrs:
                        foreach (var line in body.Split('\n'))
                        {
                            string t = line.Trim();
                            if (t.Length > 0 && !t.StartsWith('#')) builder.Add(t, feed.Label);
                        }
                        break;
                    case FeedKind.CsvFirstColumn:
                        foreach (var line in body.Split('\n'))
                        {
                            string t = line.Trim();
                            if (t.Length == 0 || t.StartsWith('#')) continue;
                            builder.Add(t.Split(',')[0].Trim(), feed.Label);
                        }
                        break;
                    case FeedKind.AwsJson:
                        {
                            var j = JObject.Parse(body);
                            foreach (var p in j["prefixes"] ?? new JArray()) builder.Add(p.Value<string>("ip_prefix"), feed.Label);
                            foreach (var p in j["ipv6_prefixes"] ?? new JArray()) builder.Add(p.Value<string>("ipv6_prefix"), feed.Label);
                            break;
                        }
                    case FeedKind.GcpJson:
                    case FeedKind.GoogleStyleJson:
                        {
                            var j = JObject.Parse(body);
                            foreach (var p in j["prefixes"] ?? new JArray())
                            {
                                builder.Add(p.Value<string>("ipv4Prefix"), feed.Label);
                                builder.Add(p.Value<string>("ipv6Prefix"), feed.Label);
                            }
                            break;
                        }
                    case FeedKind.OracleJson:
                        {
                            var j = JObject.Parse(body);
                            foreach (var region in j["regions"] ?? new JArray())
                                foreach (var c in region["cidrs"] ?? new JArray())
                                    builder.Add(c.Value<string>("cidr"), feed.Label);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                LogError?.Invoke(ex, $"Intel feed {feed.Name} could not be parsed.");
                return 0;
            }
            if (builder.Count == 0) return 0;
            _feedSets[feed.Name] = builder.Build();
            if (!fromCache) _feedLoadedUtc[feed.Name] = DateTime.UtcNow;
            else _feedLoadedUtc.TryAdd(feed.Name, DateTime.MinValue);
            return builder.Count;
        }

        private void RebuildSets()
        {
            _datacenter = Merge(DatacenterFeeds);
            _crawlers = Merge(CrawlerFeeds);
            _tor = _feedSets.TryGetValue(TorFeed.Name, out var tor) ? tor : CidrSet.Empty;
            CrawlerFeedsLoaded = CrawlerFeeds.Any(f => _feedSets.ContainsKey(f.Name));
        }

        private CidrSet Merge(IEnumerable<Feed> feeds)
        {
            var sets = feeds.Where(f => _feedSets.ContainsKey(f.Name)).Select(f => _feedSets[f.Name]).ToArray();
            return sets.Length == 0 ? CidrSet.Empty : CidrSet.Union(sets);
        }

        private string? FeedCachePath(Feed feed) => _feedCacheDir == null ? null : Path.Combine(_feedCacheDir, feed.Name + ".txt");

        private string? ReadFeedCache(Feed feed)
        {
            try
            {
                string? path = FeedCachePath(feed);
                return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch { return null; }
        }

        private void WriteFeedCache(Feed feed, string body)
        {
            try
            {
                string? path = FeedCachePath(feed);
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", body);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch { /* the in-memory set is what matters */ }
        }

        public static bool IsPrivate(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0;
            }
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }
    }
}
