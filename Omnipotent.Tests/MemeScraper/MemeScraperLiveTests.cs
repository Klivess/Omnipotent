using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Scraping;
using OpenQA.Selenium.Chrome;
using Xunit.Abstractions;

namespace Omnipotent.Tests.MemeScraper
{
    /// <summary>Runs only with MEMESCRAPER_LIVE=1: these drive real Chrome against inflact.com and instagram.com.</summary>
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEMESCRAPER_LIVE") != "1")
                Skip = "Live scrape test; set MEMESCRAPER_LIVE=1 (needs Chrome + internet).";
        }
    }

    /// <summary>
    /// End-to-end checks of the browser-driven providers — the part unit tests can't reach and the
    /// part that broke. Run after inflact/Instagram changes:
    /// <c>MEMESCRAPER_LIVE=1 dotnet test -c Release --filter "FullyQualifiedName~MemeScraperLiveTests"</c>
    /// </summary>
    public class MemeScraperLiveTests
    {
        private const string Account = "bepis.shit";
        private readonly ITestOutputHelper output;

        public MemeScraperLiveTests(ITestOutputHelper output) => this.output = output;

        private sealed class DirectChromeFactory : IScraperBrowserFactory
        {
            public Task<ScraperBrowser> OpenAsync(string purpose, CancellationToken ct)
            {
                var options = new ChromeOptions();
                ScraperBrowser.ApplyOptions(options);
                options.AddArgument("--no-sandbox");
                var driver = new ChromeDriver(options);
                return Task.FromResult(new ScraperBrowser(driver, () =>
                {
                    try { driver.Quit(); } catch { }
                    driver.Dispose();
                }));
            }
        }

        [LiveFact]
        public async Task Inflact_PagesThroughReelsWithDownloadableUrls()
        {
            var provider = new InflactReelProvider(new DirectChromeFactory(), output.WriteLine);
            var result = await provider.DiscoverAsync(new ReelDiscoveryRequest { Username = Account, MaxPages = 3, StopWhenCaughtUp = false }, CancellationToken.None);

            output.WriteLine($"inflact: {result.Reels.Count} reels over {result.PagesFetched} pages; truncated: {result.TruncatedReason}");
            Assert.True(result.PagesFetched >= 2, "expected cursor paging to work");
            Assert.True(result.Reels.Count >= 13, "expected more than one page of reels");
            Assert.All(result.Reels, r => Assert.NotEmpty(r.VideoUrls));
            Assert.All(result.Reels, r => Assert.False(string.IsNullOrEmpty(r.PostID)));

            await AssertDownloads(result.Reels[0]);
        }

        [LiveFact]
        public async Task Inflact_StopsOnceCaughtUp()
        {
            var provider = new InflactReelProvider(new DirectChromeFactory(), output.WriteLine);
            // Everything is "known": paging must stop after the second all-known page.
            var result = await provider.DiscoverAsync(new ReelDiscoveryRequest { Username = Account, MaxPages = 10, IsKnown = _ => true }, CancellationToken.None);
            output.WriteLine($"caught-up run fetched {result.PagesFetched} pages");
            Assert.Equal(2, result.PagesFetched);
            Assert.True(result.CoveredAllNewReels);
        }

        [LiveFact]
        public async Task Inflact_FetchesProfile()
        {
            var provider = new InflactReelProvider(new DirectChromeFactory(), output.WriteLine);
            var profile = await provider.FetchProfileAsync(Account, CancellationToken.None);
            output.WriteLine($"{profile.Username} {profile.AccountID} {profile.Followers} followers");
            Assert.Equal("44198560066", profile.AccountID);
            Assert.True(profile.Followers > 1000);
        }

        [LiveFact]
        public async Task Instagram_ListsNewestReelsAndResolvesVideoUrls()
        {
            var provider = new InstagramWebReelProvider(new DirectChromeFactory(), output.WriteLine) { MaxReelPagesPerRun = 2 };
            var result = await provider.DiscoverAsync(new ReelDiscoveryRequest { Username = Account }, CancellationToken.None);

            output.WriteLine($"instagram: {result.Reels.Count} reels; truncated: {result.TruncatedReason}");
            Assert.Equal(2, result.Reels.Count);
            Assert.All(result.Reels, r => Assert.NotEmpty(r.VideoUrls));
            Assert.All(result.Reels, r => Assert.False(string.IsNullOrEmpty(r.PostID)));
            await AssertDownloads(result.Reels[0]);
        }

        [LiveFact]
        public async Task Instagram_FetchesProfile()
        {
            var provider = new InstagramWebReelProvider(new DirectChromeFactory(), output.WriteLine);
            var profile = await provider.FetchProfileAsync(Account, CancellationToken.None);
            output.WriteLine($"{profile.Username} {profile.AccountID} {profile.Followers} followers");
            Assert.Equal("44198560066", profile.AccountID);
        }

        private async Task AssertDownloads(DiscoveredReel reel)
        {
            string path = Path.Combine(Path.GetTempPath(), $"memescraper-live-{reel.PostID}.mp4");
            using var downloader = new ReelDownloader();
            var outcome = await downloader.DownloadAsync(reel.VideoUrls, path, CancellationToken.None);
            output.WriteLine($"download {reel.ShortCode}: {outcome.Success} {outcome.Bytes} bytes via {outcome.Url} {outcome.Error}");
            try
            {
                Assert.True(outcome.Success, outcome.Error);
                Assert.True(new FileInfo(path).Length > ReelDownloader.MinimumVideoBytes);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
