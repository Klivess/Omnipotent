using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;
using static Omnipotent.Tests.OmniDefence.FingerprintTestKit;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// One synthetic traffic pattern per class, asserting both the verdict and that the
    /// evidence explains it (the map's "why" panel is only as good as these reasons).
    /// </summary>
    public class IpClassifierTests
    {
        private const long T0 = 1_780_000_000_000; // fixed epoch ms, keeps hour buckets deterministic

        private static Classification Classify(IpFingerprint fp, IpIntel? intel = null, int? rank = null) => IpClassifier.Classify(fp, intel, rank);

        [Fact]
        public void PythonPollerFromDatacenter_IsScraper_WithTimingEvidence()
        {
            var fp = Fold(Every(20, T0, 5_000, (ip, t) => Script(ip, t), "3.3.3.3"));
            var result = Classify(fp, new IpIntel { IsHosting = true, DatacenterProvider = "AWS" });

            Assert.Equal(IpClass.Scraper, result.Class);
            Assert.True(result.Confidence >= 0.6, $"confidence {result.Confidence}");
            Assert.Contains(result.Evidence, e => e.Reason.Contains("python-requests"));
            Assert.Contains(result.Evidence, e => e.Reason.Contains("Machine-regular pacing"));
            Assert.Contains("Datacenter:AWS", result.Tags);
        }

        [Fact]
        public void SingleCurlRequest_IsUnknown_NotEnoughEvidence()
        {
            var fp = Fold(new[] { Script("4.4.4.4", T0, ua: "curl/8.4.0") });
            Assert.Equal(IpClass.Unknown, Classify(fp).Class);
        }

        [Fact]
        public void ChromeOnWebsite_WithoutBeacon_IsLikelyHuman()
        {
            var fp = Fold(new[] { 0, 800, 5_000, 40_000, 95_000 }.Select(d => Browser("5.5.5.5", T0 + d)));
            var result = Classify(fp, new IpIntel { IsHosting = false, IsMobile = false, IsProxy = false });

            Assert.Equal(IpClass.LikelyHuman, result.Class);
            Assert.Contains(result.Evidence, e => e.Reason.StartsWith("Headers consistent with Chrome"));
            Assert.Contains("Residential", result.Tags);
        }

        [Fact]
        public void ChromeWithTrustedInputBeacon_IsHuman()
        {
            var fp = Fold(new[] { 0, 900, 7_000 }.Select(d => Browser("6.6.6.6", T0 + d)));
            fp.Beacon = new BeaconSummary { Count = 2, TrustedPointer = 140, TrustedKeys = 12, TrustedScroll = 9, PointerEntropy = 3.1 };
            var result = Classify(fp, new IpIntel { IsHosting = false });

            Assert.Equal(IpClass.Human, result.Class);
            Assert.Contains(result.Evidence, e => e.Reason.StartsWith("Real input events"));
            Assert.Contains("RealInput", result.Tags);
        }

        [Fact]
        public void WebdriverBeacon_IsHeadlessAutomation_EvenWithBrowserHeaders()
        {
            var fp = Fold(new[] { 0, 900, 7_000 }.Select(d => Browser("7.7.7.7", T0 + d)));
            fp.Beacon = new BeaconSummary { Count = 3, Webdriver = true, SoftwareRenderer = true, WebglRenderer = "Google SwiftShader" };
            var result = Classify(fp);

            Assert.Equal(IpClass.HeadlessAutomation, result.Class);
            Assert.Contains(result.Evidence, e => e.Reason == "navigator.webdriver is true");
        }

        [Fact]
        public void SpoofedChromeWithoutClientHints_LeansScraperOrHeadless_AndIsTaggedSpoofed()
        {
            var signals = Every(12, T0, 3_000, (ip, t) =>
            {
                var s = Browser(ip, t, origin: "DirectApi");
                s.Flags &= ~(HeaderFlags.SecChUa | HeaderFlags.SecFetchSite | HeaderFlags.SecFetchMode);
                s.SecChUa = null;
                s.SecFetchSite = null;
                return s;
            }, "8.8.4.4");
            var fp = Fold(signals);
            var result = Classify(fp, new IpIntel { IsHosting = true });

            Assert.True(result.Class is IpClass.Scraper or IpClass.HeadlessAutomation, $"got {result.Class}");
            Assert.Contains(result.Evidence, e => e.Reason.Contains("lack Sec-CH-UA"));
        }

        [Fact]
        public void ClientHintVersionMismatch_IsTaggedSpoofedUA()
        {
            var fp = Fold(Every(4, T0, 2_000, (ip, t) =>
            {
                var s = Browser(ip, t);
                s.SecChUa = "\"Chromium\";v=\"110\", \"Google Chrome\";v=\"110\"";
                return s;
            }, "9.9.9.1"));
            Assert.Contains("SpoofedUA", Classify(fp).Tags);
        }

        [Fact]
        public void VerifiedGooglebot_IsVerifiedCrawler()
        {
            var fp = Fold(Every(5, T0, 30_000, (ip, t) => Script(ip, t, ua: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", route: "/"), "66.249.66.1"));
            var result = Classify(fp, new IpIntel { VerifiedCrawler = "Googlebot", ReverseDns = "crawl-66-249-66-1.googlebot.com", ReverseDnsForwardConfirmed = true });

            Assert.Equal(IpClass.VerifiedCrawler, result.Class);
            Assert.Contains("googlebot.com", result.Evidence[0].Reason);
        }

        [Fact]
        public void FakeGooglebot_FailingVerification_IsScraper_WithFakeCrawlerTag()
        {
            var fp = Fold(Every(5, T0, 30_000, (ip, t) => Script(ip, t, ua: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"), "45.1.1.1"));
            var result = Classify(fp, new IpIntel { CrawlerVerificationFailed = true, IsHosting = true });

            Assert.Equal(IpClass.Scraper, result.Class);
            Assert.Contains("FakeCrawler", result.Tags);
            Assert.Contains(result.Evidence, e => e.Reason.Contains("fails its rDNS"));
        }

        [Fact]
        public void WordPressProber_IsVulnScanner()
        {
            var routes = new[] { "/wp-login.php", "/.env", "/.git/config", "/phpmyadmin/index.php", "/" };
            var fp = Fold(routes.Select((r, i) => Script("1.2.3.4", T0 + i * 1000, ua: "Mozilla/5.0 zgrab/0.x", route: r, status: 404, matched: false)));
            var result = Classify(fp);

            Assert.Equal(IpClass.VulnScanner, result.Class);
            Assert.True(result.Confidence >= 0.8);
            Assert.Contains(result.Evidence, e => e.Reason.Contains("sensitive-path probes"));
        }

        [Fact]
        public void ProbingFromKnownSurveyOrg_IsResearchScanner()
        {
            var routes = new[] { "/.env", "/.git/HEAD", "/wp-login.php" };
            var fp = Fold(routes.Select((r, i) => Script("162.142.125.1", T0 + i * 1000, ua: "Mozilla/5.0 (compatible; CensysInspect/1.1)", route: r, status: 404, matched: false)));
            Assert.Equal(IpClass.ResearchScanner, Classify(fp, new IpIntel { ResearchScanner = "Censys" }).Class);
        }

        [Fact]
        public void ShodanUaTouchingRoot_IsResearchScanner()
        {
            var fp = Fold(new[] { Script("198.20.69.1", T0, ua: "Mozilla/5.0 (compatible; Shodan)", route: "/") });
            fp.HostIsIp = 1;
            var result = Classify(fp);
            Assert.Equal(IpClass.ResearchScanner, result.Class);
        }

        [Fact]
        public void HoneypotHit_IsVulnScanner()
        {
            var fp = Fold(new[] { Script("2.2.2.2", T0, route: "/admin/backup") }, honeypot: r => r == "/admin/backup");
            Assert.Equal(IpClass.VulnScanner, Classify(fp).Class);
            Assert.Contains("HoneypotHit", Classify(fp).Tags);
        }

        [Fact]
        public void RobotsTrapHit_IsScraper_WithRobotsViolatorTag()
        {
            var fp = Fold(new[]
            {
                Script("11.11.11.11", T0, ua: "Mozilla/5.0 (compatible; SomeCrawler/1.0)", route: "/robots.txt"),
                Script("11.11.11.11", T0 + 2000, ua: "Mozilla/5.0 (compatible; SomeCrawler/1.0)", route: "/archive/abcd/export"),
                Script("11.11.11.11", T0 + 4000, ua: "Mozilla/5.0 (compatible; SomeCrawler/1.0)", route: "/"),
            }, robotsTrap: "/archive/abcd/export");
            var result = Classify(fp);

            Assert.Equal(IpClass.Scraper, result.Class);
            Assert.Contains("RobotsViolator", result.Tags);
        }

        [Fact]
        public void RepeatedInvalidPasswords_IsCredentialAttacker()
        {
            var fp = Fold(Every(6, T0, 2_000, (ip, t) =>
            {
                var s = Script(ip, t, status: 401);
                s.Outcome = RequestOutcome.InvalidPassword;
                s.DenyReason = "InvalidPassword";
                return s;
            }, "12.12.12.12"));
            var result = Classify(fp);

            Assert.Equal(IpClass.CredentialAttacker, result.Class);
            Assert.Contains("never authenticated", result.Evidence[0].Reason);
        }

        [Fact]
        public void AuthenticatedScript_IsApiClient()
        {
            var fp = Fold(Every(10, T0, 60_000, (ip, t) =>
            {
                var s = Script(ip, t);
                s.ProfileId = "pid-bot";
                s.ProfileRank = 2;
                s.RequestOrigin = "DirectApiProfile";
                return s;
            }, "13.13.13.13"));
            Assert.Equal(IpClass.ApiClient, Classify(fp).Class);
        }

        [Fact]
        public void KlivesRankProfile_IsOwner_RegardlessOfOtherSignals()
        {
            var fp = Fold(Every(10, T0, 5_000, (ip, t) => Script(ip, t), "14.14.14.14"));
            var result = Classify(fp, new IpIntel { IsHosting = true }, rank: 5);
            Assert.Equal(IpClass.Owner, result.Class);
            Assert.Equal(1.0, result.Confidence);
        }

        [Fact]
        public void DiscordPreviewBot_IsDeclaredBot()
        {
            var fp = Fold(Every(3, T0, 10_000, (ip, t) => Script(ip, t, ua: "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", route: "/shared/abc"), "15.15.15.15"));
            Assert.Equal(IpClass.DeclaredBot, Classify(fp).Class);
        }

        [Fact]
        public void ManualLabel_WinsOverEverything()
        {
            var fp = Fold(Every(10, T0, 5_000, (ip, t) => Script(ip, t), "16.16.16.16"));
            fp.ManualClass = nameof(IpClass.ApiClient);
            var result = Classify(fp, new IpIntel { IsHosting = true });
            Assert.Equal(IpClass.ApiClient, result.Class);
            Assert.True(result.Manual);
        }

        [Fact]
        public void Evidence_LeadsWithTheWinningClass()
        {
            var fp = Fold(Every(20, T0, 5_000, (ip, t) => Script(ip, t), "17.17.17.17"));
            var result = Classify(fp, new IpIntel { IsHosting = true });
            Assert.Equal(result.Class.ToString(), result.Evidence[0].Class);
        }
    }
}
