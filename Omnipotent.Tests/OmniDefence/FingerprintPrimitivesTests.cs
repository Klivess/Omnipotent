using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;
using static Omnipotent.Tests.OmniDefence.FingerprintTestKit;

namespace Omnipotent.Tests.OmniDefence
{
    public class FingerprintPrimitivesTests
    {
        // ── CIDR ──

        [Fact]
        public void CidrSet_MatchesV4AndV6_AndNarrowestWins()
        {
            var b = new CidrSet.Builder();
            Assert.True(b.Add("10.0.0.0/8", "Wide"));
            Assert.True(b.Add("10.1.2.0/24", "Narrow"));
            Assert.True(b.Add("2600:1f00::/24", "AWS6"));
            Assert.True(b.Add("192.0.2.7", "Single"));
            Assert.False(b.Add("not-an-ip", "x"));
            Assert.False(b.Add("10.0.0.0/33", "x"));
            var set = b.Build();

            Assert.Equal("Narrow", set.Lookup("10.1.2.200"));
            Assert.Equal("Wide", set.Lookup("10.200.0.1"));
            Assert.Equal("AWS6", set.Lookup("2600:1f18::1"));
            Assert.Equal("Single", set.Lookup("192.0.2.7"));
            Assert.Null(set.Lookup("192.0.2.8"));
            Assert.Null(set.Lookup("11.0.0.0"));
            Assert.Equal("Narrow", set.Lookup("::ffff:10.1.2.3"));
        }

        [Fact]
        public void CidrSet_Union_CombinesSets()
        {
            var a = new CidrSet.Builder(); a.Add("1.0.0.0/24", "A");
            var c = new CidrSet.Builder(); c.Add("2.0.0.0/24", "C");
            var u = CidrSet.Union(a.Build(), c.Build());
            Assert.Equal("A", u.Lookup("1.0.0.9"));
            Assert.Equal("C", u.Lookup("2.0.0.9"));
        }

        [Fact]
        public void IntelService_ParsesFeedFormats()
        {
            var svc = new IpIntelService(null, null);
            Assert.Equal(2, svc.IngestFeed(IpIntelService.DatacenterFeeds.First(f => f.Name == "aws"),
                "{\"prefixes\":[{\"ip_prefix\":\"3.5.140.0/22\"}],\"ipv6_prefixes\":[{\"ipv6_prefix\":\"2600:1f14::/35\"}]}", fromCache: false));
            Assert.Equal(1, svc.IngestFeed(IpIntelService.CrawlerFeeds.First(f => f.Name == "googlebot"),
                "{\"prefixes\":[{\"ipv4Prefix\":\"66.249.64.0/27\"}]}", fromCache: false));
            Assert.Equal(2, svc.IngestFeed(IpIntelService.TorFeed, "185.220.101.1\n# comment\n185.220.101.2\n", fromCache: false));
        }

        // ── user agents ──

        [Theory]
        [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36")]
        [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1")]
        [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
        [InlineData("curl/8.4.0")]
        [InlineData("python-requests/2.31.0")]
        [InlineData("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")]
        [InlineData("")]
        [InlineData(null)]
        public void LegacyParse_KeepsTripwireSemantics(string? ua)
        {
            // Frozen copy of the original Tripwires implementation.
            static (string, string, string) Original(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return ("Unknown", "Unknown", "Unknown");
                var bot = new System.Text.RegularExpressions.Regex(@"bot|crawler|spider|slurp|preview|facebookexternalhit|discordbot|telegrambot|whatsapp|skypeuripreview|curl|wget|python-requests|headless", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string V(string name, string pattern)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(value, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!m.Success) return name;
                    string major = m.Groups[1].Value.Split('.')[0];
                    return string.IsNullOrWhiteSpace(major) ? name : $"{name} {major}";
                }
                string browser = value.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? V("Edge", @"Edg/([\d.]+)")
                    : value.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? V("Opera", @"OPR/([\d.]+)")
                    : value.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? V("Chrome", @"Chrome/([\d.]+)")
                    : value.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? V("Firefox", @"Firefox/([\d.]+)")
                    : value.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? V("Safari", @"Version/([\d.]+)")
                    : "Other";
                string os = value.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) ? "Windows"
                    : value.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
                    : value.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || value.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
                    : value.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
                    : value.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
                    : "Other";
                string device = bot.IsMatch(value) ? "Bot"
                    : value.Contains("iPad", StringComparison.OrdinalIgnoreCase) || value.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "Tablet"
                    : value.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || value.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "Mobile"
                    : "Desktop";
                return (browser, os, device);
            }

            Assert.Equal(Original(ua), UserAgentParser.LegacyParse(ua));
        }

        [Theory]
        [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36", UaKind.Browser, "Chrome", 128)]
        [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0", UaKind.Browser, "Edge", 128)]
        [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15", UaKind.Browser, "Safari", 17)]
        [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36", UaKind.HeadlessBrowser, "HeadlessChrome", 120)]
        [InlineData("curl/8.4.0", UaKind.Script, "curl", 0)]
        [InlineData("Go-http-client/1.1", UaKind.Script, "Go http", 0)]
        [InlineData("Mozilla/5.0 zgrab/0.x", UaKind.Scanner, "zgrab", 0)]
        [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", UaKind.Crawler, "Googlebot", 0)]
        [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", UaKind.PreviewBot, "Discordbot", 0)]
        [InlineData("", UaKind.Empty, "(none)", 0)]
        public void Parse_ClassifiesKind(string ua, UaKind kind, string family, int major)
        {
            var p = UserAgentParser.Parse(ua);
            Assert.Equal(kind, p.Kind);
            Assert.Equal(family, p.Family);
            Assert.Equal(major, p.MajorVersion);
        }

        // ── probes / signals ──

        [Theory]
        [InlineData("/wp-login.php", true)]
        [InlineData("/.env", true)]
        [InlineData("/.git/config", true)]
        [InlineData("/cgi-bin/luci", true)]
        [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php", true)]
        [InlineData("/km/dashboard", false)]
        [InlineData("/omnidefence/ip-map", false)]
        [InlineData("/", false)]
        public void ProbePatterns_FlagSensitivePaths(string route, bool probe)
            => Assert.Equal(probe, ProbePatterns.IsProbe(route));

        [Fact]
        public void Ja4hLite_IsStableAcrossHeaderOrder_AndIgnoresCookieValues()
        {
            var a = Browser("1.1.1.1", 0);
            var b = Browser("1.1.1.1", 0);
            b.HeaderNames = a.HeaderNames.Reverse().ToArray();
            Assert.Equal(a.ComputeJa4hLite(), b.ComputeJa4hLite());
            Assert.StartsWith("ge11nr", a.ComputeJa4hLite());
            Assert.Equal(3, a.ComputeJa4hLite().Split('_').Length);
        }

        [Fact]
        public void FromAuditRow_RebuildsFlagsAndOutcome()
        {
            string headers = "{\"Host\":\"klive.dev:5000\",\"Accept-Language\":\"en-US\",\"Sec-CH-UA\":\"x\",\"Cookie\":\"[REDACTED:12]\"}";
            var s = RequestSignals.FromAuditRow(1000, "1.2.3.4", "GET", "/x", null, 401, "curl/8", "DirectApi", null, null, true, "InvalidPassword", headers);
            Assert.Equal(RequestOutcome.InvalidPassword, s.Outcome);
            Assert.False(s.Secure);
            Assert.True(s.Has(HeaderFlags.AcceptLanguage));
            Assert.True(s.Has(HeaderFlags.SecChUa));
            Assert.True(s.Has(HeaderFlags.Cookie));
            Assert.Equal(1_000_000, s.UtcMs);
        }

        [Fact]
        public void Fold_MeasuresInterBurstRegularity_NotIntraBurstChatter()
        {
            // Bursts of 3 requests (a page load) every 10 s: gaps are between burst starts.
            var signals = Enumerable.Range(0, 10).SelectMany(i => new[]
            {
                Script("1.1.1.1", i * 10_000L), Script("1.1.1.1", i * 10_000L + 50), Script("1.1.1.1", i * 10_000L + 120)
            });
            var fp = Fold(signals);
            Assert.Equal(10, fp.Bursts);
            Assert.Equal(9, fp.GapCount);
            Assert.InRange(fp.GapMean, 9.99, 10.01);
            Assert.True(fp.GapCv < 0.01);
        }

        // ── beacon ──

        [Fact]
        public void BeaconAnalyzer_FlagsHeadlessTells_AndDerivesStableDeviceId()
        {
            var payload = JObject.Parse(@"{
                ""env"": { ""webdriver"": true, ""ua"": """ + ChromeUa + @""", ""languages"": [],
                    ""webgl"": { ""renderer"": ""Google SwiftShader"" }, ""tz"": ""Europe/London"",
                    ""screen"": { ""w"": 800, ""h"": 600, ""dpr"": 1 }, ""cores"": 4, ""plugins"": 0, ""chrome"": false,
                    ""uaData"": { ""platform"": ""Linux"", ""brands"": [ { ""brand"": ""HeadlessChrome"", ""version"": ""128"" } ] },
                    ""canvas"": ""abc123"" },
                ""input"": { ""pointer"": 0, ""keys"": 0, ""untrusted"": 40 } }");
            var obs = BeaconAnalyzer.Analyze(payload, ChromeUa, "Europe/London")!;

            Assert.True(obs.Webdriver);
            Assert.True(obs.SoftwareRenderer);
            Assert.True(obs.UaMismatch); // Linux platform vs Windows UA
            Assert.Contains(obs.Anomalies, a => a.Contains("languages is empty"));
            Assert.Contains(obs.Anomalies, a => a.Contains("800x600"));
            Assert.Contains(obs.Anomalies, a => a.Contains("window.chrome missing"));
            Assert.True(obs.TimezoneMatchesGeo);
            Assert.NotNull(obs.DeviceId);
            Assert.Equal(obs.DeviceId, BeaconAnalyzer.Analyze(payload, ChromeUa, null)!.DeviceId);

            var fp = new IpFingerprint { Ip = "1.1.1.1" };
            BeaconAnalyzer.Merge(fp, obs, ipsForDevice: 4);
            Assert.Equal(40, fp.Beacon!.UntrustedEvents);
            Assert.Contains("IpRotation:4", IpClassifier.BuildTags(fp, new IpIntel()));
        }

        [Fact]
        public void BeaconNonce_IsBoundToIp()
        {
            var od = new Omnipotent.Services.OmniDefence.OmniDefence();
            string nonce = od.IssueBeaconNonce("1.2.3.4");
            Assert.True(od.ValidateBeaconNonce("1.2.3.4", nonce));
            Assert.False(od.ValidateBeaconNonce("1.2.3.5", nonce));
            Assert.False(od.ValidateBeaconNonce("1.2.3.4", null));
            Assert.False(od.ValidateBeaconNonce("1.2.3.4", "forged"));
        }
    }
}
