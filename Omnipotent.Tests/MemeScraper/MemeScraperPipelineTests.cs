using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Scraping;
using System.Net;

namespace Omnipotent.Tests.MemeScraper
{
    public class ReelDownloaderTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "memescraper-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static byte[] FakeMp4(int size = 20_000)
        {
            var bytes = new byte[size];
            new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }.CopyTo(bytes, 0);
            return bytes;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
            public List<string> Requested = new();
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requested.Add(request.RequestUri!.ToString());
                return Task.FromResult(respond(request));
            }
        }

        [Fact]
        public async Task FallsThroughRefusedAndNonVideoUrlsToTheFirstRealVideo()
        {
            var handler = new StubHandler(req => req.RequestUri!.Host switch
            {
                "expired.example" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "html.example" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + new string('x', 20_000) + "</html>") },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeMp4()) },
            });
            using var downloader = new ReelDownloader(handler);
            string target = Path.Combine(dir, "ReelMedia1.mp4");

            var outcome = await downloader.DownloadAsync(new[]
            {
                "https://expired.example/a.mp4", "https://html.example/b.mp4", "https://good.example/c.mp4", "https://never.example/d.mp4",
            }, target, CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.Equal("https://good.example/c.mp4", outcome.Url);
            Assert.Equal(20_000, new FileInfo(target).Length);
            Assert.False(File.Exists(target + ".part"));
            Assert.DoesNotContain("https://never.example/d.mp4", handler.Requested);
        }

        [Fact]
        public async Task ReportsEveryReasonWhenNothingDownloads()
        {
            var handler = new StubHandler(req => req.RequestUri!.Host == "tiny.example"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeMp4(100)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
            using var downloader = new ReelDownloader(handler);
            string target = Path.Combine(dir, "ReelMedia2.mp4");

            var outcome = await downloader.DownloadAsync(new[] { "https://gone.example/a.mp4", "https://tiny.example/b.mp4", "not a url" }, target, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("gone.example: HTTP 404", outcome.Error);
            Assert.Contains("tiny.example: body too small", outcome.Error);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }

        [Fact]
        public void RecognisesMp4AndWebmHeaders()
        {
            Assert.True(ReelDownloader.LooksLikeVideo(FakeMp4(12)));
            Assert.True(ReelDownloader.LooksLikeVideo(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }));
            Assert.False(ReelDownloader.LooksLikeVideo("<!DOCTYPE h"u8.ToArray()));
        }
    }

    public class ReelDiscoveryChainTests
    {
        private sealed class FakeProvider : IReelProvider
        {
            public string Name { get; }
            public int Calls;
            private readonly Func<ReelDiscoveryRequest, ReelDiscoveryResult> behaviour;
            public FakeProvider(string name, Func<ReelDiscoveryRequest, ReelDiscoveryResult> behaviour)
            {
                Name = name;
                this.behaviour = behaviour;
            }
            public Task<ReelDiscoveryResult> DiscoverAsync(ReelDiscoveryRequest request, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(behaviour(request));
            }
        }

        private static ReelDiscoveryResult Found(string provider, params string[] codes) => new()
        {
            Provider = provider,
            PagesFetched = 1,
            Reels = codes.Select((c, i) => new DiscoveredReel { ShortCode = c, PostID = (1000 + i).ToString(), Provider = provider, VideoUrls = { $"https://cdn.example/{c}.mp4" } }).ToList(),
        };

        private static ReelDiscoveryResult Throws(string message) => throw new ReelProviderException("x", message);

        [Fact]
        public async Task PrimarySuccessSkipsTheFallback()
        {
            var primary = new FakeProvider("inflact", _ => Found("inflact", "AAAAA1", "AAAAA2"));
            var fallback = new FakeProvider("instagram", _ => Found("instagram", "AAAAA1"));
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var outcome = await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);

            Assert.Equal("inflact", outcome.SucceededVia);
            Assert.Equal(2, outcome.Reels.Count);
            Assert.Equal(0, fallback.Calls);
        }

        [Fact]
        public async Task PrimaryFailureFallsBackAndIsRecorded()
        {
            var primary = new FakeProvider("inflact", _ => Throws("page never made a signed API call"));
            var fallback = new FakeProvider("instagram", _ => Found("instagram", "BBBBB1"));
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var outcome = await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);

            Assert.Equal("instagram", outcome.SucceededVia);
            Assert.Equal("BBBBB1", Assert.Single(outcome.Reels).ShortCode);
            Assert.Contains(outcome.Attempts, a => a.Contains("inflact: FAILED") && a.Contains("signed API call"));
            var health = chain.GetProviderHealth().Single(h => h.Name == "inflact");
            Assert.Equal(1, health.ConsecutiveFailures);
        }

        [Fact]
        public async Task PrimarySilentlyEmptyWhileFallbackFindsReelsCountsAsPrimaryFailure()
        {
            var primary = new FakeProvider("inflact", _ => new ReelDiscoveryResult { Provider = "inflact", PagesFetched = 1, CoveredAllNewReels = true });
            var fallback = new FakeProvider("instagram", _ => Found("instagram", "CCCCC1"));
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var outcome = await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);

            Assert.Equal("instagram", outcome.SucceededVia);
            Assert.Single(outcome.Reels);
            Assert.Equal(1, chain.GetProviderHealth().Single(h => h.Name == "inflact").ConsecutiveFailures);
        }

        [Fact]
        public async Task BothEmptyIsAProfileWithNoReels_NotAFailure()
        {
            var primary = new FakeProvider("inflact", _ => new ReelDiscoveryResult { Provider = "inflact" });
            var fallback = new FakeProvider("instagram", _ => new ReelDiscoveryResult { Provider = "instagram" });
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var outcome = await chain.DiscoverReelsAsync("tfck", _ => false, 10, CancellationToken.None);

            Assert.Empty(outcome.Reels);
            Assert.NotNull(outcome.SucceededVia);
            Assert.All(chain.GetProviderHealth(), h => Assert.Equal(0, h.ConsecutiveFailures));
        }

        [Fact]
        public async Task EveryProviderFailingThrowsWithEachReason()
        {
            var primary = new FakeProvider("inflact", _ => Throws("Just a moment..."));
            var fallback = new FakeProvider("instagram", _ => Throws("login wall"));
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var ex = await Assert.ThrowsAsync<ReelProviderException>(() => chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None));

            Assert.Contains("Just a moment", ex.Message);
            Assert.Contains("login wall", ex.Message);
        }

        [Fact]
        public async Task RepeatedlyFailingPrimaryIsBenchedAndSkipped()
        {
            var primary = new FakeProvider("inflact", _ => Throws("blocked"));
            var fallback = new FakeProvider("instagram", _ => Found("instagram", "DDDDD1"));
            var chain = new InstagramScrapeUtilities(primary, fallback);

            for (int i = 0; i < InstagramScrapeUtilities.FailuresBeforeBench; i++)
            {
                await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);
            }
            Assert.Equal(InstagramScrapeUtilities.FailuresBeforeBench, primary.Calls);
            Assert.NotNull(chain.GetProviderHealth().Single(h => h.Name == "inflact").BenchedUntil);

            var outcome = await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);

            Assert.Equal(InstagramScrapeUtilities.FailuresBeforeBench, primary.Calls); // not called while benched
            Assert.Contains(outcome.Attempts, a => a.StartsWith("inflact: skipped, benched"));
            Assert.Equal("instagram", outcome.SucceededVia);
        }

        [Fact]
        public async Task SameReelFromBothProvidersIsMergedByShortCode()
        {
            var primary = new FakeProvider("inflact", _ => new ReelDiscoveryResult { Provider = "inflact" });
            var fallback = new FakeProvider("instagram", _ => new ReelDiscoveryResult
            {
                Provider = "instagram",
                Reels =
                {
                    new DiscoveredReel { ShortCode = "EEEEE1", PostID = "1", ViewCount = 10, Provider = "instagram", VideoUrls = { "https://a.example/1.mp4" } },
                    new DiscoveredReel { ShortCode = "EEEEE1", PostID = "1", ViewCount = 99, Provider = "instagram", VideoUrls = { "https://b.example/1.mp4" } },
                },
            });
            var chain = new InstagramScrapeUtilities(primary, fallback);

            var outcome = await chain.DiscoverReelsAsync("user", _ => false, 10, CancellationToken.None);

            var reel = Assert.Single(outcome.Reels);
            Assert.Equal(99, reel.ViewCount);
            Assert.Equal(new[] { "https://a.example/1.mp4", "https://b.example/1.mp4" }, reel.VideoUrls);
        }
    }
}
