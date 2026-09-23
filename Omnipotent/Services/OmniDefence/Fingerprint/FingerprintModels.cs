using Newtonsoft.Json;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>Primary classification of an IP. Exactly one per IP.</summary>
    public enum IpClass
    {
        Unknown,
        Owner,
        Human,
        LikelyHuman,
        ApiClient,
        VerifiedCrawler,
        DeclaredBot,
        ResearchScanner,
        Scraper,
        HeadlessAutomation,
        VulnScanner,
        CredentialAttacker
    }

    public static class IpClassInfo
    {
        public sealed record Info(IpClass Class, string Label, string Color, bool Malicious, string Description);

        /// <summary>Served to the website so the map legend is driven by the server.</summary>
        public static readonly Info[] All =
        {
            new(IpClass.Owner, "Owner", "#5fd3ff", false, "Linked to a Klives-rank profile."),
            new(IpClass.Human, "Human", "#52ffb9", false, "Real person in a real browser: trusted input events from the browser beacon."),
            new(IpClass.LikelyHuman, "Likely human", "#a6f5a0", false, "Browser-consistent headers and behaviour, no beacon proof yet."),
            new(IpClass.ApiClient, "API client", "#7aa7ff", false, "Authenticated non-browser tool or integration."),
            new(IpClass.VerifiedCrawler, "Verified crawler", "#4f8dff", false, "Search/AI crawler proven by forward-confirmed rDNS or published ranges."),
            new(IpClass.DeclaredBot, "Declared bot", "#c79cff", false, "Self-identifying bot: link previews, uptime monitors, SEO crawlers."),
            new(IpClass.ResearchScanner, "Research scanner", "#9aa7b0", false, "Internet-wide survey scanner (Censys, Shodan, Shadowserver…)."),
            new(IpClass.Scraper, "Scraper", "#ffc247", true, "Automated harvesting: script clients, datacenter origin, machine timing."),
            new(IpClass.HeadlessAutomation, "Headless automation", "#ff8a3d", true, "Browser automation posing as a human (webdriver, spoofed UA, no real input)."),
            new(IpClass.VulnScanner, "Vuln scanner", "#ff6071", true, "Exploit/probe traffic: sensitive paths, honeypots, 404 storms."),
            new(IpClass.CredentialAttacker, "Credential attacker", "#ff4fd8", true, "Brute-force or credential-stuffing login attempts."),
            new(IpClass.Unknown, "Unknown", "#56636b", false, "Not enough evidence yet."),
        };

        public static Info Get(IpClass c) => All.First(i => i.Class == c);

        public static bool TryParse(string? value, out IpClass cls) =>
            Enum.TryParse(value, ignoreCase: true, out cls) && Enum.IsDefined(cls);
    }

    /// <summary>One reason a class was chosen. Shown in the UI as the "why".</summary>
    public sealed class Evidence
    {
        public string Class = "";
        public double Weight;
        public string Reason = "";

        public Evidence() { }
        public Evidence(IpClass cls, double weight, string reason) { Class = cls.ToString(); Weight = weight; Reason = reason; }
    }

    public sealed class Classification
    {
        public IpClass Class;
        public double Confidence;
        public List<Evidence> Evidence = new();
        public List<string> Tags = new();
        public Dictionary<string, double> Scores = new();
        public bool Manual;

        public string TagString() => string.Join(",", Tags);
    }

    /// <summary>
    /// External intelligence about an IP (network ownership, reputation, rDNS).
    /// Assembled from the IpRecord's ip-api fields plus <see cref="IpIntelService"/> lookups.
    /// </summary>
    public sealed class IpIntel
    {
        public bool? IsHosting;
        public bool? IsProxy;
        public bool? IsMobile;
        public string? Country;
        public string? Timezone;
        public string? Asn;
        public string? AsName;
        public string? Isp;
        public string? Org;

        public string? ReverseDns;
        public bool? ReverseDnsForwardConfirmed;
        /// <summary>Crawler name when proven by FCrDNS or a published range.</summary>
        public string? VerifiedCrawler;
        /// <summary>The UA claimed a verifiable crawler but the proof failed.</summary>
        public bool CrawlerVerificationFailed;
        /// <summary>Survey-scanner organisation identified by rDNS / ASN.</summary>
        public string? ResearchScanner;
        /// <summary>Cloud provider from published range lists.</summary>
        public string? DatacenterProvider;
        public bool IsTor;

        public int? AbuseScore;
        public int? AbuseReports;
        public string? AbuseUsageType;
        public string? GreyNoiseClassification;
        public bool? GreyNoiseRiot;
        public string? GreyNoiseName;

        public long UpdatedMs;

        public bool LooksDatacenter => IsHosting == true || DatacenterProvider != null
            || (AbuseUsageType?.Contains("Data Center", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Aggregated browser-beacon evidence for an IP.</summary>
    public sealed class BeaconSummary
    {
        public long Count;
        public long FirstMs;
        public long LastMs;
        public bool Webdriver;
        public bool SoftwareRenderer;
        public string? WebglRenderer;
        public string? Timezone;
        public bool? TimezoneMatchesGeo;
        public string? Screen;
        public string? Platform;
        public string? Languages;
        public int? Cores;
        public double? MemoryGb;
        public string? Brands;
        public bool UaMismatch;
        public List<string> Anomalies = new();
        public long TrustedPointer;
        public long TrustedKeys;
        public long TrustedScroll;
        public long TrustedTouch;
        public long UntrustedEvents;
        public double? PointerEntropy;
        public HashSet<string> DeviceIds = new();
        /// <summary>Most IPs any of this IP's devices has been seen on (rotation signal).</summary>
        public int MaxIpsPerDevice;

        [JsonIgnore]
        public bool HasTrustedInput => TrustedPointer >= 20 || TrustedKeys >= 5 || TrustedTouch >= 3 || (TrustedScroll >= 5 && TrustedPointer >= 5);
    }

    /// <summary>TLS / transport evidence (JA4 phase).</summary>
    public sealed class TlsSummary
    {
        public Dictionary<string, long> Ja4 = new();
        public string? Sni;
        public long NoSniCount;
        public double? RttMs;
        public bool UaMismatch;
        public string? Ja4Family;
        public bool KnownHumanJa4;
    }

    /// <summary>
    /// Everything the engine has folded about one IP. Owned by the engine thread; readers
    /// take <c>lock(fp)</c> to snapshot it.
    /// </summary>
    public sealed class IpFingerprint
    {
        public const int MaxRoutes = 128;
        public const int MaxVariants = 16;
        public const double BurstGapSeconds = 1.0;
        public const double SessionGapSeconds = 30 * 60;

        public string Ip = "";
        public long FirstSeenMs;
        public long LastSeenMs;
        public long Requests;

        // Outcomes
        public long NotFound;
        public long ClientErrors;
        public long ServerErrors;
        public long PreBlocked;
        public long AuthFailures;
        public long InvalidPasswords;
        public long LoginAttempts;
        public long LoginFailures;
        public long HoneypotHits;
        public long RobotsFetches;
        public long RobotsTrapHits;
        public long ProbeHits;
        public Dictionary<string, long> ProbeKinds = new();
        public List<string> SampleProbes = new();

        // Routes
        public HashSet<string> Routes = new(StringComparer.OrdinalIgnoreCase);
        public bool RoutesCapped;

        // Origin / identity
        public long WebsiteRequests;
        public long DirectRequests;
        public long ProfileRequests;
        public int MaxProfileRank = -1;
        public string? LastProfileId;
        public long ViaBatch;

        // Header consistency
        public long BrowserClaims;
        public long WithAcceptLanguage;
        public long WithSecFetch;
        public long WithClientHints;
        public long WithBrotli;
        public long WithCookie;
        public long WithReferer;
        public long Conditional;
        public long HostIsIp;
        public long ForwardedHeader;
        public long Http10;
        public long PlainHttp;
        public long AcceptWildcardOnly;
        /// <summary>Browser-claimed requests missing headers that browser version always sends.</summary>
        public long MissingSecFetch;
        public long MissingClientHints;
        public long MissingAcceptLanguage;
        public long SecChUaMismatch;

        public Dictionary<string, long> UserAgents = new();
        public Dictionary<string, long> UaKinds = new();
        public Dictionary<string, long> UaFamilies = new();
        public Dictionary<string, long> Ja4h = new();
        public string? ClaimedCrawler;
        public string? LastUserAgent;

        // Timing (inter-burst gaps, Welford)
        public long GapCount;
        public double GapMean;
        public double GapM2;
        public long Bursts;
        public long Sessions;
        public int[] HourHistogram = new int[24];
        public long CurrentMinute;
        public int CurrentMinuteCount;
        public int PeakPerMinute;
        public long LastBurstStartMs;

        // Linked evidence
        public BeaconSummary? Beacon;
        public TlsSummary? Tls;

        // Latest classification
        public string Class = nameof(IpClass.Unknown);
        public double Confidence;
        public List<Evidence> Evidence = new();
        public List<string> Tags = new();
        public Dictionary<string, double> Scores = new();
        public string? ManualClass;
        public long ClassifiedMs;
        public long ClassChangedMs;

        [JsonIgnore] public bool Dirty;
        [JsonIgnore] public bool PendingClassify;
        [JsonIgnore] public bool Urgent;

        [JsonIgnore] public double GapStdDev => GapCount > 1 ? Math.Sqrt(GapM2 / (GapCount - 1)) : 0;
        [JsonIgnore] public double GapCv => GapMean > 0 ? GapStdDev / GapMean : 0;
        [JsonIgnore] public int ActiveHours => HourHistogram.Count(h => h > 0);

        public static void Bump(Dictionary<string, long> map, string? key, int cap = MaxVariants)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (map.TryGetValue(key, out long n)) { map[key] = n + 1; return; }
            if (map.Count >= cap)
            {
                // Evict the rarest so a rotating-UA client can't grow this without bound.
                var rarest = map.OrderBy(kv => kv.Value).First();
                if (rarest.Value > 1) return;
                map.Remove(rarest.Key);
            }
            map[key] = 1;
        }

        public static string? Top(Dictionary<string, long> map) => map.Count == 0 ? null : map.OrderByDescending(kv => kv.Value).First().Key;
    }
}
