using Newtonsoft.Json;
using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Analytics;
using Reel = Omnipotent.Services.MemeScraper.InstagramScrapeUtilities.InstagramReel;
using Report = Omnipotent.Services.MemeScraper.MemeScraper.ScrapeReport;
using Source = Omnipotent.Services.MemeScraper.MemeScraperSources.InstagramSource;

namespace Omnipotent.Tests.MemeScraper
{
    /// <summary>
    /// What the 13 Nov 2026 /evolve run changed under the MemeScraper console: the reel index that replaced reading one file
    /// per reel at start-up (1,171 s live), and the source states that hid 9 sources that never had a reel.
    /// </summary>
    public class MemeScraperConsoleBackendTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "memescraper-index-" + Guid.NewGuid().ToString("N"));
        private string ReelsDir => Path.Combine(root, "Reels");
        private string IndexFile => Path.Combine(root, "ReelIndex.json.gz");

        public MemeScraperConsoleBackendTests() => Directory.CreateDirectory(ReelsDir);

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private static Reel MakeReel(int n, int views = 1000) => new()
        {
            PostID = (3_000_000 + n).ToString(),
            ShortCode = "C" + n.ToString("D8"),
            OwnerUsername = "owner" + (n % 3),
            OwnerID = "1" + (n % 3),
            ViewCount = views,
            LikeCount = 40,
            CommentCount = 2,
            CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddHours(n),
            DateTimeReelDownloaded = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc).AddHours(n),
            Description = "a caption " + n,
            InstagramReelInfoFilePath = $"SavedData/MemeScraper/Instagram/Reels/Reel{3_000_000 + n}.json",
            InstagramReelVideoFilePath = $"SavedData/MemeScraper/Instagram/Media/ReelMedia{3_000_000 + n}.mp4",
        };

        private string WriteReelFile(Reel reel)
        {
            string path = Path.Combine(ReelsDir, $"Reel{reel.PostID}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(reel, Formatting.Indented));
            return path;
        }

        private MemeScraperMedia NewMedia() => new(null, new MemeScraperReelIndex(ReelsDir, IndexFile, parallelism: 4), TimeSpan.FromMinutes(10));

        private async Task<MemeScraperMedia> StartAsync()
        {
            var media = NewMedia();
            await media.LoadAllScrapedInstagramReels();
            await media.StartReconcile();
            return media;
        }

        // ─────────────────────────────── Reel index ───────────────────────────────

        [Fact]
        public async Task FirstStartReadsEveryFileThenTheNextReadsNone()
        {
            for (int n = 0; n < 200; n++) WriteReelFile(MakeReel(n));

            var first = await StartAsync();
            Assert.Equal("files", first.LastLoad!.Via);
            Assert.Equal("no index yet", first.LastLoad.IndexProblem);
            Assert.Equal(200, first.LastLoad.FilesRead);
            Assert.Equal(200, first.Count);
            await first.WriteIndexAsync();
            Assert.True(File.Exists(IndexFile));
            Assert.Equal(200, first.LastIndexWrite!.Reels);

            var second = await StartAsync();
            Assert.Equal("index", second.LastLoad!.Via);
            Assert.Equal(0, second.LastLoad.FilesRead);
            Assert.Equal(200, second.Count);
            // The background check lists the directory but opens nothing: every stamp still matches.
            Assert.Equal(200, second.LastReconcile!.Listed);
            Assert.Equal(0, second.LastReconcile.FilesRead);
            Assert.Equal((0, 0, 0), (second.LastReconcile.Added, second.LastReconcile.Changed, second.LastReconcile.Removed));
            Assert.Equal(first.allScrapedReels.Select(r => r.PostID).OrderBy(x => x), second.allScrapedReels.Select(r => r.PostID).OrderBy(x => x));
            Assert.Equal("a caption 7", second.FindReel("3000007")!.Description);
        }

        [Fact]
        public async Task TheCheckReadsOnlyNewAndChangedFilesAndDropsGoneOnes()
        {
            for (int n = 0; n < 50; n++) WriteReelFile(MakeReel(n));
            var first = await StartAsync();
            await first.WriteIndexAsync();

            // While the server was down (or before the index caught up): 3 new reels, 2 deleted, 1 rewritten.
            for (int n = 50; n < 53; n++) WriteReelFile(MakeReel(n));
            File.Delete(Path.Combine(ReelsDir, "Reel3000003.json"));
            File.Delete(Path.Combine(ReelsDir, "Reel3000004.json"));
            string changed = WriteReelFile(MakeReel(10, views: 987_654));
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(5));

            var media = NewMedia();
            await media.LoadAllScrapedInstagramReels();
            Assert.Equal(50, media.Count); // what the index knew, published at once
            Assert.True(media.IsKnown("3000003", null));
            await media.StartReconcile();

            var r = media.LastReconcile!;
            Assert.Equal((3, 1, 2), (r.Added, r.Changed, r.Removed));
            Assert.Equal(4, r.FilesRead);
            Assert.Equal(51, media.Count);
            Assert.Null(media.FindReel("3000003"));
            Assert.False(media.IsKnown("3000004", "C00000004"));
            Assert.True(media.IsKnown("3000052", null));
            Assert.Equal(987_654, media.FindReel("3000010")!.ViewCount);

            // Written back, the next start has nothing left to read.
            await media.WriteIndexAsync();
            var again = await StartAsync();
            Assert.Equal(0, again.LastReconcile!.FilesRead);
            Assert.Equal(51, again.Count);
        }

        [Fact]
        public async Task AReelDownloadedDuringTheCheckIsHeldOnce()
        {
            for (int n = 0; n < 20; n++) WriteReelFile(MakeReel(n));
            var first = await StartAsync();
            await first.WriteIndexAsync();

            var media = NewMedia();
            await media.LoadAllScrapedInstagramReels();
            // A scrape saves a reel before the check lists the directory: the check must not add it a second time.
            var fresh = MakeReel(99);
            await media.SaveInstagramReel(fresh);
            Assert.True(media.TryAddReel(fresh));
            await media.StartReconcile();

            Assert.Equal(21, media.Count);
            Assert.Equal(1, media.allScrapedReels.Count(x => x.PostID == fresh.PostID));
        }

        [Fact]
        public async Task AnUnreadableIndexFallsBackToTheFiles()
        {
            for (int n = 0; n < 12; n++) WriteReelFile(MakeReel(n));
            File.WriteAllText(IndexFile, "this is not gzip");

            var media = await StartAsync();
            Assert.Equal("files", media.LastLoad!.Via);
            Assert.StartsWith("the index could not be read", media.LastLoad.IndexProblem);
            Assert.Equal(12, media.Count);
            Assert.Equal(12, media.LastLoad.FilesRead);
        }

        [Fact]
        public async Task SourceDeletionLeavesTheIndex()
        {
            for (int n = 0; n < 30; n++) WriteReelFile(MakeReel(n));
            var media = await StartAsync();
            var removed = media.RemoveReels(r => r.OwnerUsername == "owner1");
            Assert.Equal(10, removed.Count);
            await media.WriteIndexAsync();

            // The files are still on disk here (the source manager deletes them): the next start reads them back as new.
            var next = NewMedia();
            await next.LoadAllScrapedInstagramReels();
            Assert.Equal(20, next.Count);
        }

        // ─────────────────────────────── Thumbnail backfill ───────────────────────────────

        [Fact]
        public void TheThumbnailBacklogIsNewestFirstAndSkipsWhatExistsFailedOrHasNoVideo()
        {
            // Live: ~1,130 of 20,234 reels had a thumbnail; the rest were made on first view (p95 38 s).
            var reels = Enumerable.Range(0, 6).Select(n => MakeReel(n)).ToList();
            reels[1].InstagramReelVideoFilePath = null;          // no video to take a frame from
            reels[2].PostID = "../evil";                         // never becomes a file name
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3000003.jpg" };
            var backlog = Omnipotent.Services.MemeScraper.MemeScraper.ThumbnailBacklog(reels, existing, id => id == "3000004");

            Assert.Equal(new[] { "3000005", "3000000" }, backlog.Select(r => r.PostID));
        }

        // ─────────────────────────────── Source states ───────────────────────────────

        private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        private static Source Src(string user, string id, DateTime added, int foundLast = 12) => new()
        {
            Username = user,
            AccountID = id,
            FullName = user,
            DownloadReels = true,
            Niches = new List<MemeScraperSources.Niche>(),
            LastSuccessfulScrapeUtc = Now.AddHours(-3),
            NextScrapeDueUtc = Now.AddHours(20),
            LastScrapeReelsFound = foundLast,
            DateTimeAdded = added,
        };

        private static Reel ReelOf(string id, string owner, string ownerId, DateTime downloaded, DateTime posted) => new()
        {
            PostID = id,
            ShortCode = "sc" + id,
            OwnerUsername = owner,
            OwnerID = ownerId,
            ViewCount = 5_000,
            LikeCount = 100,
            CommentCount = 4,
            CreatedAt = posted,
            DateTimeReelDownloaded = downloaded,
            DiscoveredVia = "inflact",
        };

        private static DashboardView Dashboard(List<Source> sources, List<Reel> reels) =>
            MemeScraperInsights.BuildDashboard(sources, reels, new List<Report>(), InsightsQuery.Parse(k => k == "range" ? "30d" : null), Now, 24);

        [Fact]
        public void ASourceThatNeverHadAReelIsEmptyAndNamed()
        {
            // Live 13 Nov: 9 such sources, 270 scrapes between them, all shown as "quiet" and named by no insight.
            var sources = new List<Source>
            {
                Src("busy", "1", Now.AddDays(-60)),
                Src("ghost", "2", Now.AddDays(-31), foundLast: 0),
                Src("ghost2", "3", Now.AddDays(-31), foundLast: 0),
                Src("brandnew", "4", Now.AddDays(-1), foundLast: 0),
            };
            var reels = new List<Reel> { ReelOf("r1", "busy", "1", Now.AddDays(-1), Now.AddDays(-1.5)) };

            var view = Dashboard(sources, reels);
            var state = view.Sources.ToDictionary(s => s.Username, s => s.State);
            Assert.Equal("ok", state["busy"]);
            Assert.Equal("empty", state["ghost"]);
            Assert.Equal("empty", state["ghost2"]);
            Assert.Equal("quiet", state["brandnew"]); // a day is too soon to call it empty
            Assert.Equal(2, view.Library.EmptySources);
            Assert.Contains("found no reels", view.Sources.First(s => s.Username == "ghost").StateReason);

            var insight = Assert.Single(view.Insights, i => i.Title == "2 sources have never had a reel");
            Assert.Equal("warn", insight.Level);
            Assert.Equal(new[] { "ghost", "ghost2" }, insight.Sources);
        }

        [Fact]
        public void AQuietSourceSaysWhenTheAccountLastPosted()
        {
            // Live: @bepis.shit's newest reel was posted on 2 Aug, downloaded 4 Oct; scrapes find 12 known reels daily.
            var sources = new List<Source> { Src("dormant", "1", Now.AddDays(-120)), Src("busy", "2", Now.AddDays(-120)) };
            var posted = Now.AddDays(-103);
            var reels = new List<Reel>
            {
                ReelOf("d1", "dormant", "1", Now.AddDays(-40), posted),
                ReelOf("d0", "dormant", "1", Now.AddDays(-40), posted.AddDays(-9)),
                ReelOf("b1", "busy", "2", Now.AddDays(-2), Now.AddDays(-2.2)),
            };

            var row = Dashboard(sources, reels).Sources.First(s => s.Username == "dormant");
            Assert.Equal("quiet", row.State);
            Assert.Equal(posted, row.LastPostedUtc);
            Assert.Contains("posted 103 days ago", row.StateReason);
        }
    }
}
