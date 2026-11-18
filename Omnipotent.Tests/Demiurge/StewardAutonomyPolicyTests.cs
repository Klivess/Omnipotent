using Newtonsoft.Json.Linq;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge
{
    /// <summary>
    /// Klives' hard limits: what a Steward's agents may never do, and what they must ask about first.
    /// These hold at dispatch, whatever the model decides — and with nothing set, nothing changes.
    /// </summary>
    [Collection("DemiurgeSerial")]
    public class StewardAutonomyPolicyTests : IDisposable
    {
        public StewardAutonomyPolicyTests() => StewardAutonomyPolicy.ResetForTests();

        public void Dispose()
        {
            StewardAutonomyPolicy.ResetForTests();
            GC.SuppressFinalize(this);
        }

        private static StewardSettings Settings() => new() { StewardID = "p" };

        [Fact]
        public void DefaultSettings_LimitNothing()
        {
            var s = Settings();
            foreach (string tool in new[] { "run_powershell", "klivemail_send", "account_register" })
                Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, tool, "{}", out _));
            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_navigate", "{\"url\":\"https://anything.example\"}", out var ask));
            Assert.Null(ask);
            Assert.Equal("", StewardAutonomyPolicy.Describe(s));
        }

        [Fact]
        public void BlockedSites_AndTheirSubdomains_AreRefusedForEveryWebRoute()
        {
            var s = Settings();
            Assert.True(s.TrySet("blockedDomains", new JArray("https://www.facebook.com", "reddit.com")));
            Assert.Equal(new[] { "facebook.com", "reddit.com" }, s.BlockedDomains);

            foreach (string tool in new[] { "computer_navigate", "computer_open_browser", "web_fetch", "http_request" })
                Assert.NotNull(StewardAutonomyPolicy.FindViolation("p", s, tool, "{\"url\":\"https://m.facebook.com/login\"}", out _));
            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_navigate", "{\"url\":\"https://notfacebook.com\"}", out _));
        }

        [Fact]
        public void RawCdpNavigation_CannotStepAroundABlockedSite()
        {
            var s = Settings();
            Assert.True(s.TrySet("blockedDomains", new JArray("facebook.com")));
            Assert.NotNull(StewardAutonomyPolicy.FindViolation("p", s, "computer_cdp",
                "{\"action\":\"send\",\"method\":\"Page.navigate\",\"params\":{\"url\":\"https://www.facebook.com/\"}}", out _));
            // Models often send params as a JSON string.
            Assert.NotNull(StewardAutonomyPolicy.FindViolation("p", s, "computer_cdp",
                "{\"action\":\"send\",\"method\":\"Target.createTarget\",\"params\":\"{\\\"url\\\":\\\"https://facebook.com/x\\\"}\"}", out _));
            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_cdp",
                "{\"action\":\"send\",\"method\":\"Page.reload\",\"params\":{}}", out _));
            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_cdp",
                "{\"action\":\"send\",\"method\":\"Page.navigate\",\"params\":{\"url\":\"https://example.com/\"}}", out _));
        }

        [Fact]
        public void AskFirstSites_NeedAnApproval_ThatThenLastsADay()
        {
            var s = Settings();
            s.TrySet("askFirstDomains", JToken.FromObject("paypal.com, stripe.com"));

            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_navigate", "{\"url\":\"https://www.paypal.com/signin\"}", out var ask));
            Assert.Equal("paypal.com", ask);

            StewardAutonomyPolicy.RecordApproval("p", "paypal.com");
            StewardAutonomyPolicy.FindViolation("p", s, "computer_navigate", "{\"url\":\"https://www.paypal.com/signin\"}", out ask);
            Assert.Null(ask);
            // Approval is per Steward.
            StewardAutonomyPolicy.FindViolation("other", s, "computer_navigate", "{\"url\":\"https://www.paypal.com/\"}", out ask);
            Assert.Equal("paypal.com", ask);
        }

        [Fact]
        public void CapabilitySwitches_RefuseTheirTools_AndSayWhatToDoInstead()
        {
            var s = Settings();
            s.TrySet("allowHostShell", "false");
            s.TrySet("allowOutboundEmail", "false");
            s.TrySet("allowAccountCreation", "false");

            Assert.Contains("own desktop", StewardAutonomyPolicy.FindViolation("p", s, "run_bash", "{}", out _));
            Assert.NotNull(StewardAutonomyPolicy.FindViolation("p", s, "execute_csharp", "{}", out _));
            Assert.Contains("Draft", StewardAutonomyPolicy.FindViolation("p", s, "klivemail_send", "{}", out _));
            Assert.NotNull(StewardAutonomyPolicy.FindViolation("p", s, "account_register", "{}", out _));
            Assert.Null(StewardAutonomyPolicy.FindViolation("p", s, "computer_terminal", "{}", out _));

            string described = StewardAutonomyPolicy.Describe(s);
            Assert.Contains("Host shell/script tools are OFF", described);
            Assert.Contains("Outbound email is OFF", described);
        }
    }
}
