using System.Globalization;
using W = Omnipotent.Services.OmniDefence.Fingerprint.ClassifierWeights;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>
    /// Pure, deterministic IP classifier. Hard rules decide the unambiguous cases; everything
    /// else is weighted evidence summed per class and softmaxed into a confidence. Every
    /// piece of evidence carries a human-readable reason so the UI can say *why*.
    /// </summary>
    public static class IpClassifier
    {
        private static readonly IpClass[] Candidates =
        {
            IpClass.Human, IpClass.LikelyHuman, IpClass.ApiClient, IpClass.DeclaredBot, IpClass.ResearchScanner,
            IpClass.Scraper, IpClass.HeadlessAutomation, IpClass.VulnScanner, IpClass.CredentialAttacker
        };

        private static readonly HashSet<string> ResearchScannerUas = new(StringComparer.OrdinalIgnoreCase)
        {
            "Censys", "Shodan", "InternetMeasurement", "Palo Alto Expanse", "LeakIX", "ODIN", "Modat", "Scanner"
        };

        public static Classification Classify(IpFingerprint fp, IpIntel? intel, int? associatedProfileRank)
        {
            intel ??= new IpIntel();
            var result = new Classification();
            var tags = BuildTags(fp, intel);
            result.Tags = tags;

            // ── hard rules ──
            if (!string.IsNullOrEmpty(fp.ManualClass) && IpClassInfo.TryParse(fp.ManualClass, out var manual))
            {
                return Decide(result, manual, 1.0, new Evidence(manual, 99, "Manually labelled by Klives"), manualLabel: true);
            }

            int rank = Math.Max(fp.MaxProfileRank, associatedProfileRank ?? -1);
            if (rank >= 5)
            {
                return Decide(result, IpClass.Owner, 1.0, new Evidence(IpClass.Owner, 99, "Linked to a Klives-rank profile"));
            }

            bool probing = fp.ProbeHits >= W.ProbeHitsForVulnScanner || fp.HoneypotHits > 0;

            if (intel.VerifiedCrawler != null && !probing)
            {
                string how = intel.ReverseDnsForwardConfirmed == true && intel.ReverseDns != null
                    ? $"forward-confirmed rDNS {intel.ReverseDns}"
                    : "IP is inside the crawler's published ranges";
                return Decide(result, IpClass.VerifiedCrawler, 0.99, new Evidence(IpClass.VerifiedCrawler, 99, $"Verified {intel.VerifiedCrawler}: {how}"));
            }

            if (probing)
            {
                if (intel.ResearchScanner != null)
                {
                    return Decide(result, IpClass.ResearchScanner, 0.9,
                        new Evidence(IpClass.ResearchScanner, 99, $"Probing traffic from known survey scanner {intel.ResearchScanner}"),
                        ProbeEvidence(fp));
                }
                double conf = Math.Min(0.99, 0.8 + fp.ProbeHits * 0.02 + fp.HoneypotHits * 0.05);
                return Decide(result, IpClass.VulnScanner, conf, ProbeEvidence(fp).ToArray());
            }

            if (fp.ProfileRequests == 0 && fp.InvalidPasswords >= W.InvalidPasswordsForAttacker)
            {
                return Decide(result, IpClass.CredentialAttacker, Math.Min(0.99, 0.75 + fp.InvalidPasswords * 0.02),
                    new Evidence(IpClass.CredentialAttacker, 99, $"{fp.InvalidPasswords} invalid-password requests and never authenticated"));
            }
            if (fp.ProfileRequests == 0 && fp.LoginFailures >= W.LoginFailuresForAttacker)
            {
                return Decide(result, IpClass.CredentialAttacker, Math.Min(0.99, 0.75 + fp.LoginFailures * 0.02),
                    new Evidence(IpClass.CredentialAttacker, 99, $"{fp.LoginFailures}/{fp.LoginAttempts} failed logins and never authenticated"));
            }

            // ── weighted evidence ──
            var ev = new List<Evidence>();
            void Add(IpClass c, double w, string reason) => ev.Add(new Evidence(c, w, reason));

            ScoreUserAgent(fp, intel, Add);
            ScoreBrowserConsistency(fp, Add);
            ScoreTransport(fp, Add);
            ScoreBehaviour(fp, Add);
            ScoreIntel(fp, intel, Add);
            ScoreBeacon(fp, Add);
            ScoreTls(fp, Add);

            var scores = Candidates.ToDictionary(c => c, _ => 0.0);
            foreach (var e in ev)
            {
                if (IpClassInfo.TryParse(e.Class, out var c) && scores.ContainsKey(c)) scores[c] += e.Weight;
            }

            // "Human" means proven by real input; without it the best a browser gets is LikelyHuman.
            bool provenHuman = fp.Beacon?.HasTrustedInput == true;
            if (!provenHuman)
            {
                scores[IpClass.LikelyHuman] += Math.Max(0, scores[IpClass.Human]);
                scores[IpClass.Human] = double.NegativeInfinity;
            }

            double denom = Math.Exp(W.UnknownBaseline);
            foreach (var s in scores.Values) if (!double.IsNegativeInfinity(s)) denom += Math.Exp(s);
            var top = scores.OrderByDescending(kv => kv.Value).First();
            double confidence = Math.Exp(top.Value) / denom;

            result.Scores = scores.Where(kv => !double.IsNegativeInfinity(kv.Value) && kv.Value != 0)
                .ToDictionary(kv => kv.Key.ToString(), kv => Math.Round(kv.Value, 2));
            result.Evidence = ev.OrderByDescending(e => Math.Abs(e.Weight)).ToList();

            bool tooLittleTraffic = fp.Requests < W.MinRequestsForWeakVerdict && top.Value < W.StrongEvidence;
            if (top.Value <= W.UnknownBaseline || confidence < W.MinConfidence || tooLittleTraffic)
            {
                result.Class = IpClass.Unknown;
                result.Confidence = Math.Round(Math.Exp(W.UnknownBaseline) / denom, 3);
                return result;
            }

            result.Class = top.Key;
            result.Confidence = Math.Round(confidence, 3);
            // Lead with what supports the verdict, then the counter-evidence.
            result.Evidence = ev.Where(e => e.Class == top.Key.ToString()).OrderByDescending(e => e.Weight)
                .Concat(ev.Where(e => e.Class != top.Key.ToString()).OrderByDescending(e => Math.Abs(e.Weight)))
                .ToList();
            return result;
        }

        private static Classification Decide(Classification r, IpClass cls, double confidence, params Evidence[] evidence)
            => Decide(r, cls, confidence, (IEnumerable<Evidence>)evidence);

        private static Classification Decide(Classification r, IpClass cls, double confidence, Evidence first, IEnumerable<Evidence> rest)
            => Decide(r, cls, confidence, new[] { first }.Concat(rest));

        private static Classification Decide(Classification r, IpClass cls, double confidence, Evidence evidence, bool manualLabel)
        {
            r.Manual = manualLabel;
            return Decide(r, cls, confidence, new[] { evidence });
        }

        private static Classification Decide(Classification r, IpClass cls, double confidence, IEnumerable<Evidence> evidence)
        {
            r.Class = cls;
            r.Confidence = Math.Round(confidence, 3);
            r.Evidence = evidence.ToList();
            r.Scores = new Dictionary<string, double> { [cls.ToString()] = 99 };
            return r;
        }

        private static List<Evidence> ProbeEvidence(IpFingerprint fp)
        {
            var list = new List<Evidence>();
            if (fp.HoneypotHits > 0)
                list.Add(new Evidence(IpClass.VulnScanner, 99, $"Hit honeypot routes {fp.HoneypotHits}×"));
            if (fp.ProbeHits > 0)
            {
                string kinds = string.Join(", ", fp.ProbeKinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));
                string samples = string.Join(" ", fp.SampleProbes.Take(4));
                list.Add(new Evidence(IpClass.VulnScanner, 99, $"{fp.ProbeHits} sensitive-path probes ({kinds}): {samples}"));
            }
            if (fp.Requests >= 5 && fp.NotFound * 2 > fp.Requests)
                list.Add(new Evidence(IpClass.VulnScanner, 2, $"{Pct(fp.NotFound, fp.Requests)} of requests hit non-existent routes"));
            return list;
        }

        // ── scorers ──

        private static void ScoreUserAgent(IpFingerprint fp, IpIntel intel, Action<IpClass, double, string> add)
        {
            string? kindName = IpFingerprint.Top(fp.UaKinds);
            if (kindName == null || !Enum.TryParse(kindName, out UaKind kind)) return;
            string family = IpFingerprint.Top(fp.UaFamilies) ?? "unknown";
            string dominance = fp.UaKinds.Count > 1 ? $" (on {Pct(fp.UaKinds[kindName], fp.Requests)} of requests)" : "";

            switch (kind)
            {
                case UaKind.Scanner:
                    if (ResearchScannerUas.Contains(family.Split(' ')[0]) || ResearchScannerUas.Contains(family))
                        add(IpClass.ResearchScanner, W.ResearchScannerUa, $"User-Agent identifies survey scanner {family}");
                    else
                        add(IpClass.VulnScanner, W.OffensiveToolUa, $"User-Agent is offensive tool {family}{dominance}");
                    break;
                case UaKind.HeadlessBrowser:
                    add(IpClass.HeadlessAutomation, W.HeadlessUa, $"User-Agent admits automation: {family}");
                    break;
                case UaKind.Crawler:
                    if (intel.CrawlerVerificationFailed && fp.ClaimedCrawler != null && IpIntelService.IsVerifiable(fp.ClaimedCrawler))
                        add(IpClass.Scraper, W.FakeCrawler, $"Claims to be {fp.ClaimedCrawler} but fails its rDNS/range verification");
                    else
                        add(IpClass.DeclaredBot, W.UnverifiableCrawlerUa, $"Declares itself as crawler {family}" + (IpIntelService.IsVerifiable(family) ? " (verification pending)" : ""));
                    break;
                case UaKind.PreviewBot:
                    add(IpClass.DeclaredBot, W.DeclaredBotUa, $"Link-preview bot {family}");
                    break;
                case UaKind.Monitor:
                    add(IpClass.DeclaredBot, W.DeclaredBotUa, $"Uptime monitor {family}");
                    break;
                case UaKind.OtherBot:
                    add(IpClass.DeclaredBot, W.DeclaredBotUa, $"User-Agent declares a bot: {Short(fp.LastUserAgent)}");
                    break;
                case UaKind.Script:
                    if (fp.ProfileRequests > 0 && fp.DirectRequests > 0)
                        add(IpClass.ApiClient, W.ScriptUaApiClient, $"Authenticated {family} client");
                    else
                        add(IpClass.Scraper, W.ScriptUaScraper, $"HTTP library/CLI User-Agent {family}{dominance}");
                    break;
                case UaKind.Empty:
                    add(IpClass.Scraper, W.EmptyUaScraper, "No User-Agent header");
                    add(IpClass.VulnScanner, W.EmptyUaScanner, "No User-Agent header");
                    break;
                case UaKind.Unknown:
                    add(IpClass.Scraper, W.UnknownUa, $"Unrecognised non-browser User-Agent {Short(fp.LastUserAgent)}");
                    break;
                case UaKind.Browser:
                    // Judged on header consistency below.
                    break;
            }

            if (fp.UserAgents.Count >= 5 && fp.Requests >= 10)
                add(IpClass.Scraper, 1.5, $"Rotates User-Agents ({fp.UserAgents.Count} distinct)");
        }

        private static void ScoreBrowserConsistency(IpFingerprint fp, Action<IpClass, double, string> add)
        {
            if (fp.BrowserClaims == 0) return;
            string claim = IpFingerprint.Top(fp.UaFamilies) ?? "a browser";
            long n = fp.BrowserClaims;
            bool anyInconsistency = false;

            if (fp.MissingClientHints * 2 > n)
            {
                anyInconsistency = true;
                add(IpClass.Scraper, W.MissingHeaderScraper, $"UA claims {claim} but {Pct(fp.MissingClientHints, n)} of requests lack Sec-CH-UA");
                add(IpClass.HeadlessAutomation, W.MissingHeaderHeadless * 0.5, $"UA claims {claim} without client hints");
            }
            if (fp.MissingSecFetch * 2 > n)
            {
                anyInconsistency = true;
                add(IpClass.Scraper, W.MissingHeaderScraper, $"UA claims {claim} but {Pct(fp.MissingSecFetch, n)} of requests lack Sec-Fetch-*");
            }
            if (fp.MissingAcceptLanguage * 2 > n)
            {
                anyInconsistency = true;
                add(IpClass.Scraper, W.MissingHeaderScraper, $"UA claims {claim} but sends no Accept-Language");
                add(IpClass.HeadlessAutomation, W.MissingHeaderHeadless * 0.5, "Browser without Accept-Language");
            }
            if (fp.SecChUaMismatch > 0)
            {
                anyInconsistency = true;
                add(IpClass.HeadlessAutomation, W.SecChUaMismatch, $"Sec-CH-UA brands contradict the User-Agent ({fp.SecChUaMismatch}×)");
                add(IpClass.Scraper, W.SecChUaMismatch * 0.5, "Spoofed User-Agent (client hints disagree)");
            }

            if (!anyInconsistency)
                add(IpClass.LikelyHuman, W.ConsistentBrowser, $"Headers consistent with {claim}");
            if (fp.WithBrotli * 2 > fp.Requests)
                add(IpClass.LikelyHuman, W.BrotliBrowser, "Accepts brotli like modern browsers");
        }

        private static void ScoreTransport(IpFingerprint fp, Action<IpClass, double, string> add)
        {
            if (fp.HostIsIp * 2 > fp.Requests)
            {
                add(IpClass.VulnScanner, W.HostIsIp * 0.6, "Addresses the server by raw IP (Host header), not klive.dev");
                add(IpClass.ResearchScanner, W.HostIsIp, "Addresses the server by raw IP, typical of internet-wide sweeps");
                add(IpClass.Scraper, W.HostIsIp * 0.3, "Host header is a raw IP");
            }
            if (fp.Http10 * 2 > fp.Requests)
            {
                add(IpClass.VulnScanner, W.Http10, "Speaks HTTP/1.0");
                add(IpClass.Scraper, W.Http10, "Speaks HTTP/1.0");
            }
            if (fp.PlainHttp * 2 > fp.Requests && fp.BrowserClaims * 2 > fp.Requests)
                add(IpClass.Scraper, W.PlainHttp, "Browser UA over plain HTTP (port 5000)");
            if (fp.ForwardedHeader * 2 > fp.Requests)
                add(IpClass.Scraper, W.ForwardedHeader, "Sends X-Forwarded-For/Via (proxy chain or spoofing)");
        }

        private static void ScoreBehaviour(IpFingerprint fp, Action<IpClass, double, string> add)
        {
            bool website = fp.WebsiteRequests * 2 > fp.Requests;

            if (fp.ProbeHits > 0)
                add(IpClass.VulnScanner, W.ProbeBelowThreshold * fp.ProbeHits, $"{fp.ProbeHits} sensitive-path probe(s): {string.Join(" ", fp.SampleProbes.Take(3))}");
            if (fp.Requests >= 5 && fp.NotFound * 2 > fp.Requests)
                add(IpClass.VulnScanner, W.NotFoundStorm, $"{Pct(fp.NotFound, fp.Requests)} of requests hit non-existent routes");

            if (fp.AuthFailures > 0 && fp.ProfileRequests == 0 && !website)
                add(IpClass.CredentialAttacker, W.AuthFailures * Math.Min(3, fp.AuthFailures / 2.0), $"{fp.AuthFailures} auth failures, never authenticated");

            if (fp.ProfileRequests > 0)
            {
                if (website)
                    add(IpClass.LikelyHuman, W.LoggedInWebsiteUser, "Logged-in user of the KM website");
                else
                    add(IpClass.ApiClient, W.AuthenticatedDirect, $"Authenticated direct API use ({Pct(fp.ProfileRequests, fp.Requests)} of requests)");
            }
            else if (website)
            {
                add(IpClass.LikelyHuman, W.WebsiteOrigin, $"{Pct(fp.WebsiteRequests, fp.Requests)} of requests made by the KM website's JavaScript");
            }

            if (fp.Conditional > 0 && fp.Conditional * 4 > fp.Requests)
                add(IpClass.LikelyHuman, W.ConditionalRequests, "Revalidates with ETags like a browser cache");

            if (!website && fp.GapCount >= 8 && fp.GapCv < 0.15)
                add(IpClass.Scraper, W.RegularTiming, $"Machine-regular pacing: {fp.GapCount} gaps of {fp.GapMean:F1}s ± {fp.GapStdDev:F2}s");

            if (fp.PeakPerMinute >= 600)
                add(IpClass.Scraper, W.VeryHighRate, $"Peak {fp.PeakPerMinute} requests/minute");
            else if (fp.PeakPerMinute >= 120 && !website)
                add(IpClass.Scraper, W.HighRate, $"Peak {fp.PeakPerMinute} requests/minute");

            int distinct = fp.Routes.Count;
            if (!website && distinct >= 50)
                add(IpClass.Scraper, W.WideRouteCoverage, $"Walked {distinct}{(fp.RoutesCapped ? "+" : "")} distinct routes");

            if (fp.RobotsFetches > 0)
            {
                add(IpClass.DeclaredBot, W.RobotsFetch, "Fetched robots.txt");
                add(IpClass.Scraper, W.RobotsFetch * 0.5, "Fetched robots.txt");
            }
            if (fp.RobotsTrapHits > 0)
                add(IpClass.Scraper, W.RobotsTrap, "Fetched the path robots.txt disallows (ignores robots)");

            if (fp.Requests >= 200 && fp.ActiveHours >= 20)
                add(IpClass.Scraper, W.AroundTheClock, $"Active in {fp.ActiveHours}/24 hours of the day");

            if (fp.Requests <= 3 && fp.Routes.Count == 1 && fp.Routes.Contains("/") && fp.ProfileRequests == 0 && !website)
                add(IpClass.ResearchScanner, W.SingleRootHit, "Only ever requested / (survey-style touch)");
        }

        private static void ScoreIntel(IpFingerprint fp, IpIntel intel, Action<IpClass, double, string> add)
        {
            if (intel.LooksDatacenter)
            {
                string who = intel.DatacenterProvider ?? intel.AsName ?? intel.Org ?? intel.Isp ?? "hosting provider";
                add(IpClass.Scraper, W.Datacenter, $"Datacenter address ({who})");
                add(IpClass.VulnScanner, W.Datacenter * 0.4, "Datacenter address");
                add(IpClass.ResearchScanner, W.Datacenter * 0.4, "Datacenter address");
                add(IpClass.LikelyHuman, W.DatacenterAgainstHuman, "People rarely browse from datacenters");
            }
            else if (intel.IsMobile == true)
            {
                add(IpClass.LikelyHuman, W.Mobile, "Mobile carrier network");
            }
            else if (intel.IsHosting == false && intel.IsProxy != true)
            {
                add(IpClass.LikelyHuman, W.ResidentialForHuman, "Residential/business ISP");
            }

            if (intel.IsProxy == true) add(IpClass.Scraper, W.Proxy, "Known VPN/proxy exit");
            if (intel.IsTor)
            {
                add(IpClass.VulnScanner, W.Tor, "Tor exit node");
                add(IpClass.Scraper, W.Tor, "Tor exit node");
            }
            if (intel.ResearchScanner != null)
                add(IpClass.ResearchScanner, W.ResearchOrg, $"Belongs to survey-scanner {intel.ResearchScanner}");

            if (intel.AbuseScore >= 75)
                add(IpClass.VulnScanner, W.AbuseHigh, $"AbuseIPDB confidence {intel.AbuseScore}% ({intel.AbuseReports ?? 0} reports)");
            else if (intel.AbuseScore >= 25)
                add(IpClass.VulnScanner, W.AbuseMid, $"AbuseIPDB confidence {intel.AbuseScore}%");

            if (intel.GreyNoiseRiot == true)
                add(IpClass.DeclaredBot, W.GreyNoiseRiot, $"GreyNoise RIOT: known business service {intel.GreyNoiseName}".TrimEnd());
            else if (string.Equals(intel.GreyNoiseClassification, "malicious", StringComparison.OrdinalIgnoreCase))
                add(IpClass.VulnScanner, W.GreyNoiseMalicious, "GreyNoise classifies this IP as malicious");
            else if (string.Equals(intel.GreyNoiseClassification, "benign", StringComparison.OrdinalIgnoreCase))
                add(IpClass.ResearchScanner, W.GreyNoiseBenign, $"GreyNoise: benign scanner {intel.GreyNoiseName}".TrimEnd());
        }

        private static void ScoreBeacon(IpFingerprint fp, Action<IpClass, double, string> add)
        {
            var b = fp.Beacon;
            if (b == null || b.Count == 0) return;

            add(IpClass.LikelyHuman, W.BeaconPresent, $"Executed the site's JavaScript ({b.Count} beacon(s))");
            if (b.Webdriver) add(IpClass.HeadlessAutomation, W.Webdriver, "navigator.webdriver is true");
            if (b.SoftwareRenderer) add(IpClass.HeadlessAutomation, W.SoftwareRenderer, $"Software WebGL renderer: {b.WebglRenderer}");
            foreach (var anomaly in b.Anomalies.Take(4)) add(IpClass.HeadlessAutomation, W.HeadlessTell, anomaly);
            if (b.UaMismatch) add(IpClass.HeadlessAutomation, W.HeadlessTell * 2, "Browser-reported brands/platform contradict the User-Agent header");

            if (b.HasTrustedInput)
            {
                add(IpClass.Human, W.TrustedInput, $"Real input events: {b.TrustedPointer} pointer, {b.TrustedKeys} key, {b.TrustedTouch} touch, {b.TrustedScroll} scroll");
                if (b.PointerEntropy >= 2.5) add(IpClass.Human, W.HighPointerEntropy, $"Organic pointer paths (entropy {b.PointerEntropy:F1})");
            }
            else if (b.Count >= 3 && b.TrustedPointer + b.TrustedKeys + b.TrustedTouch == 0)
            {
                add(IpClass.HeadlessAutomation, W.BeaconNoInput, $"{b.Count} page sessions with zero real input");
            }
            if (b.UntrustedEvents > 10 && b.UntrustedEvents > b.TrustedPointer + b.TrustedKeys)
                add(IpClass.HeadlessAutomation, W.HeadlessTell * 2, $"{b.UntrustedEvents} synthetic (untrusted) input events");
        }

        private static void ScoreTls(IpFingerprint fp, Action<IpClass, double, string> add)
        {
            var t = fp.Tls;
            if (t == null) return;
            if (t.UaMismatch)
            {
                add(IpClass.HeadlessAutomation, W.TlsUaMismatch, $"TLS fingerprint is {t.Ja4Family}, not the browser the UA claims");
                add(IpClass.Scraper, W.TlsUaMismatch, $"TLS fingerprint ({t.Ja4Family}) contradicts User-Agent");
            }
            if (t.KnownHumanJa4) add(IpClass.LikelyHuman, W.KnownHumanJa4, "TLS fingerprint seen with verified humans");
        }

        // ── tags ──

        public static List<string> BuildTags(IpFingerprint fp, IpIntel intel)
        {
            var tags = new List<string>();
            if (intel.LooksDatacenter) tags.Add("Datacenter" + (intel.DatacenterProvider != null ? ":" + intel.DatacenterProvider : ""));
            if (intel.IsProxy == true) tags.Add("VPN/Proxy");
            if (intel.IsTor) tags.Add("Tor");
            if (intel.IsMobile == true) tags.Add("Mobile");
            if (intel.IsHosting == false && intel.IsProxy != true && intel.IsMobile != true && !intel.IsTor && intel.DatacenterProvider == null) tags.Add("Residential");
            if (intel.AbuseScore >= 25) tags.Add("Abuse:" + intel.AbuseScore);
            if (!string.IsNullOrEmpty(intel.GreyNoiseClassification)) tags.Add("GreyNoise:" + intel.GreyNoiseClassification);
            if (intel.VerifiedCrawler != null) tags.Add("Verified:" + intel.VerifiedCrawler);
            if (intel.CrawlerVerificationFailed) tags.Add("FakeCrawler");
            if (fp.SecChUaMismatch > 0 || fp.Beacon?.UaMismatch == true || fp.Tls?.UaMismatch == true) tags.Add("SpoofedUA");
            if (fp.HostIsIp * 2 > fp.Requests) tags.Add("HostIsIP");
            if (fp.Tls != null && fp.Tls.NoSniCount * 2 > fp.Requests) tags.Add("NoSNI");
            if (fp.Beacon?.TimezoneMatchesGeo == false) tags.Add("TzMismatch");
            int devices = fp.Beacon?.DeviceIds.Count ?? 0;
            if (devices >= 3) tags.Add($"MultiDevice:{devices}");
            if (fp.Beacon?.MaxIpsPerDevice >= 3) tags.Add($"IpRotation:{fp.Beacon.MaxIpsPerDevice}");
            if (fp.RobotsTrapHits > 0) tags.Add("RobotsViolator");
            if (fp.HoneypotHits > 0) tags.Add("HoneypotHit");
            if (fp.Beacon?.HasTrustedInput == true) tags.Add("RealInput");
            if (fp.ProfileRequests > 0) tags.Add("Authenticated");
            if (fp.WebsiteRequests * 2 > fp.Requests) tags.Add("Website");
            if (fp.PlainHttp * 2 > fp.Requests) tags.Add("PlainHTTP");
            if (fp.Conditional > 0 && fp.Conditional * 4 > fp.Requests) tags.Add("ConditionalRequests");
            return tags;
        }

        private static string Pct(long part, long whole) => whole <= 0 ? "0%" : ((double)part / whole).ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "");
        private static string Short(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 60 ? s : s.Substring(0, 60) + "…";
    }
}
