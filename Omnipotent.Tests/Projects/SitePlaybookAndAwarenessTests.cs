using System.Text.Json;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Two ways an agent stops re-deriving what is already known: a site playbook written by whoever got
    /// through a website first, and a live view of its own computer (open tabs, crashes) at each wake.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class SitePlaybookAndAwarenessTests : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "playbooks-" + Guid.NewGuid().ToString("N"), "playbooks.json");

        public SitePlaybookAndAwarenessTests() => ComputerAwareness.ResetForTests();

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
            ComputerAwareness.ResetForTests();
            GC.SuppressFinalize(this);
        }

        [Theory]
        [InlineData("https://www.Example.com/signup?x=1", "example.com")]
        [InlineData("app.example.com", "app.example.com")]
        [InlineData("not a domain", null)]
        [InlineData("", null)]
        public void Domains_AreNormalised(string input, string? expected) =>
            Assert.Equal(expected, ProjectSitePlaybookStore.NormalizeDomain(input));

        [Fact]
        public void APlaybookIsFoundFromAnyUrlOnItsSite_MostSpecificFirst()
        {
            var store = new ProjectSitePlaybookStore(path);
            store.Save("google.com", "a", "Google in general", null, null, null, verified: false);
            store.Save("accounts.google.com", "a", "Sign-in specifics", null, null, null, verified: true);

            Assert.Equal("accounts.google.com", store.FindForUrl("https://accounts.google.com/signin/v2")!.Domain);
            Assert.Equal("google.com", store.FindForUrl("https://mail.google.com/")!.Domain);
            Assert.Null(store.FindForUrl("https://example.org/"));
        }

        [Fact]
        public void SavingMerges_SuppliedFieldsReplace_OmittedFieldsAreKept_AndItSurvivesARestart()
        {
            var store = new ProjectSitePlaybookStore(path);
            store.Save("etsy.com", "project A / commander", "Sell digital prints", new[] { "Signup at /join", "Verify email" },
                new[] { "Cookie wall reject is second" }, new[] { new SitePlaybookUrl { Label = "signup", Url = "https://www.etsy.com/join" } },
                verified: true);
            store.Save("https://www.etsy.com/your/shops", "project B / lister", null, null, null, null, verified: false,
                addPitfalls: new[] { "Listing photos must be 2000px wide" });

            var reloaded = new ProjectSitePlaybookStore(path).FindForUrl("etsy.com")!;
            Assert.Equal("Sell digital prints", reloaded.Summary);
            Assert.Equal(2, reloaded.Steps.Count);
            Assert.Equal(2, reloaded.Pitfalls.Count);
            Assert.Equal(2, reloaded.Revisions);
            Assert.Equal("project B / lister", reloaded.UpdatedBy);
            Assert.NotNull(reloaded.VerifiedAt);
            string described = ProjectSitePlaybookStore.Describe(reloaded);
            Assert.Contains("SITE PLAYBOOK — etsy.com", described);
            Assert.Contains("1. Signup at /join", described);
            Assert.Contains("https://www.etsy.com/join", described);
        }

        [Fact]
        public void AnEmptyPlaybookIsRefused()
        {
            var store = new ProjectSitePlaybookStore(path);
            Assert.Throws<InvalidOperationException>(() => store.Save("example.com", "a", "", null, null, null, verified: false));
            Assert.Throws<InvalidOperationException>(() => store.Save("??", "a", "x", null, null, null, verified: false));
        }

        // ── computer awareness ───────────────────────────────────────────────────────────────────

        private static ComputerDaemonHealth Health(params (long Seq, string Kind, DateTime At)[] events) => new()
        {
            Protocol = 2,
            Events = events.Select(e => new ComputerDaemonHealth.DaemonEvent
            {
                Seq = e.Seq, Kind = e.Kind, At = e.At.ToString("yyyy-MM-ddTHH:mm:ssZ"), Message = e.Kind,
                Details = JsonDocument.Parse("{\"lastKnownTabs\":[{\"url\":\"https://shop.example/admin\"}]}").RootElement.Clone(),
            }).ToList(),
        };

        [Fact]
        public void NewCrashes_AreReportedOnce_WithTheTabsTheBrowserHadOpen()
        {
            var now = DateTime.UtcNow;
            string first = ComputerAwareness.TakeNewEvents("c", Health((1, "started", now), (2, "browser-exited", now)));
            Assert.Contains("Chromium exited", first);
            Assert.Contains("https://shop.example/admin", first);

            Assert.Equal("", ComputerAwareness.TakeNewEvents("c", Health((1, "started", now), (2, "browser-exited", now))));

            string next = ComputerAwareness.TakeNewEvents("c", Health((2, "browser-exited", now), (3, "oom-kill", now)));
            Assert.Contains("killed a process for memory", next);
            Assert.DoesNotContain("Chromium exited", next);
        }

        [Fact]
        public void TheFirstLookAfterARestart_SkipsOldNews()
        {
            var old = DateTime.UtcNow.AddHours(-3);
            Assert.Equal("", ComputerAwareness.TakeNewEvents("c2", Health((5, "browser-hung-killed", old))));
        }

        [Fact]
        public async Task ASleepingOrMissingComputerIsDescribed_NotWoken()
        {
            var asleep = new DesktopContainerRecord { ContainerID = "abcdef1234567890", Suspended = true };
            var gone = new DesktopContainerRecord { ContainerID = "abcdef1234567890", Lost = true };

            Assert.Contains("asleep", await ComputerAwareness.DescribeForSeedAsync(asleep, null, CancellationToken.None));
            Assert.Contains("missing", await ComputerAwareness.DescribeForSeedAsync(gone, null, CancellationToken.None));
            Assert.Equal("", await ComputerAwareness.DescribeForSeedAsync(null, null, CancellationToken.None));
        }

        [Fact]
        public async Task ALiveComputer_ListsItsTabsForTheSeed()
        {
            using var daemon = new ComputerDaemonTests.FakeDaemon();
            ComputerDaemonToken.KeyOverride = () => System.Text.Encoding.UTF8.GetBytes("k");
            ComputerDaemonClient.ResetForTests();
            try
            {
                var record = new DesktopContainerRecord { ContainerID = "live-desktop-0001", Width = 1280, Height = 800, BrowserServiceHostPort = daemon.Port };
                string block = await ComputerAwareness.DescribeForSeedAsync(record,
                    new ComputerDaemonClient("127.0.0.1", daemon.Port, record.ContainerID), CancellationToken.None);

                Assert.Contains("Desktop live-desktop", block);
                Assert.Contains("memory 400/1536 MB", block);
                Assert.Contains("Browser: not running", block);   // the fake daemon reports no browser
            }
            finally
            {
                ComputerDaemonToken.KeyOverride = null;
                ComputerDaemonClient.ResetForTests();
            }
        }
    }
}
