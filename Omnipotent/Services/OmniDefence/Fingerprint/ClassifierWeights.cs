namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>
    /// Every tunable number the classifier uses, in one place. Weights are log-odds-ish
    /// points: evidence adds them to a class, and the class scores go through a softmax
    /// against <see cref="UnknownBaseline"/> to produce the confidence.
    /// </summary>
    public static class ClassifierWeights
    {
        // ── decision ──
        public const double UnknownBaseline = 1.5;
        public const double MinConfidence = 0.40;
        /// <summary>Below this many requests, only strong evidence (>= <see cref="StrongEvidence"/>) earns a verdict.</summary>
        public const int MinRequestsForWeakVerdict = 3;
        public const double StrongEvidence = 4.0;

        // ── hard-rule thresholds ──
        public const int ProbeHitsForVulnScanner = 3;
        public const int InvalidPasswordsForAttacker = 5;
        public const int LoginFailuresForAttacker = 5;

        // ── user agent ──
        public const double OffensiveToolUa = 4.0;
        public const double ResearchScannerUa = 4.0;
        public const double HeadlessUa = 4.0;
        public const double DeclaredBotUa = 3.0;
        public const double UnverifiableCrawlerUa = 2.5;
        public const double FakeCrawler = 4.0;
        public const double ScriptUaScraper = 2.5;
        public const double ScriptUaApiClient = 3.0;
        public const double EmptyUaScraper = 1.5;
        public const double EmptyUaScanner = 1.0;
        public const double UnknownUa = 1.0;

        // ── browser consistency ──
        public const double ConsistentBrowser = 2.0;
        public const double MissingHeaderScraper = 1.5;
        public const double MissingHeaderHeadless = 1.5;
        public const double SecChUaMismatch = 2.0;
        public const double BrotliBrowser = 0.5;
        public const double ConditionalRequests = 0.5;
        public const double WebsiteOrigin = 1.5;
        public const double LoggedInWebsiteUser = 2.5;
        public const double AuthenticatedDirect = 3.0;

        // ── transport ──
        public const double HostIsIp = 1.5;
        public const double Http10 = 0.75;
        public const double PlainHttp = 0.5;
        public const double ForwardedHeader = 0.5;

        // ── behaviour ──
        public const double NotFoundStorm = 2.0;
        public const double ProbeBelowThreshold = 1.5;
        public const double RegularTiming = 2.0;
        public const double HighRate = 1.5;
        public const double VeryHighRate = 2.5;
        public const double WideRouteCoverage = 1.5;
        public const double RobotsFetch = 1.0;
        public const double RobotsTrap = 5.0;
        public const double AroundTheClock = 1.0;
        public const double SingleRootHit = 1.0;
        public const double AuthFailures = 1.5;

        // ── network intel ──
        public const double Datacenter = 1.5;
        public const double DatacenterAgainstHuman = -1.5;
        public const double Mobile = 1.0;
        public const double ResidentialForHuman = 0.5;
        public const double Proxy = 0.5;
        public const double Tor = 0.75;
        public const double ResearchOrg = 6.0;
        public const double AbuseHigh = 2.5;
        public const double AbuseMid = 1.0;
        public const double GreyNoiseMalicious = 2.5;
        public const double GreyNoiseBenign = 2.5;
        public const double GreyNoiseRiot = 2.0;

        // ── browser beacon ──
        public const double Webdriver = 6.0;
        public const double SoftwareRenderer = 2.0;
        public const double HeadlessTell = 1.0;
        public const double TrustedInput = 6.0;
        public const double HighPointerEntropy = 1.0;
        public const double BeaconNoInput = 1.5;
        public const double BeaconPresent = 1.5;

        // ── TLS ──
        public const double TlsUaMismatch = 3.0;
        public const double KnownHumanJa4 = 1.0;

        // ── scoring deltas applied once per class transition ──
        public static double ScoreDelta(IpClass cls) => cls switch
        {
            IpClass.VulnScanner => 40,
            IpClass.CredentialAttacker => 30,
            IpClass.HeadlessAutomation => 15,
            IpClass.Scraper => 10,
            _ => 0
        };
    }
}
