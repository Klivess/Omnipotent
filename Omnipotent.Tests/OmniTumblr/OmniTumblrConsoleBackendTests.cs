using Omnipotent.Services.OmniTumblr;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniTumblr
{
    /// <summary>
    /// The backend behind the Nov 2026 OmniTumblr console: the blog page's content runway, MemeScraper previews,
    /// the store's lock and saves, and how often Tumblr's edge refused the server.
    /// </summary>
    public class OmniTumblrConsoleBackendTests
    {
        private static readonly DateTime Now = new(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
        private readonly ITestOutputHelper output;

        public OmniTumblrConsoleBackendTests(ITestOutputHelper output) => this.output = output;

        private static string MemeBlog(Harness h) => h.AddBlog(h.Connect(), b =>
        {
            b.Autopilot = true;
            b.Strategy.Source = ContentSourceKind.MemeScraper;
            b.Strategy.MemeScraper.MaxAgeDays = 0;
        });

        // ─────────────────────────────── runway ───────────────────────────────

        /// <summary>
        /// The live failure: GET /omnitumblr/blog p95 109 s / max 194 s with 20,234 downloaded reels, because the
        /// runway looked for every one of them on the server's media drive on every page load and poll.
        /// </summary>
        [Fact]
        public void Runway_Over20000Reels_LooksForOnlyThePreviewOnDisk()
        {
            using var h = new Harness(Now);
            string blogId = MemeBlog(h);
            for (int i = 0; i < 20_000; i++)
                h.Catalog.Reels.Add(new ReelInfo
                {
                    PostId = "r" + i, Owner = "src" + (i % 50), Views = 20_000 - i, Likes = 10, CreatedUtc = Now.AddDays(-1),
                    DownloadedUtc = Now.AddDays(-1), FilePath = $@"D:\media\r{i}.mp4",
                });
            int looked = 0;
            h.Content.FileExists = path => { Interlocked.Increment(ref looked); return !path.EndsWith("r3.mp4"); }; // one file has gone

            var (eligible, next) = h.Content.Runway(h.Blog(blogId).Strategy, blogId, new HashSet<string>(), Now, 12);

            Assert.Equal(20_000, eligible);
            Assert.Equal(12, next.Count);
            Assert.DoesNotContain(next, c => c.Key == "ig:r3");
            Assert.Equal("ig:r0", next[0].Key); // best first, as autopilot will use them
            Assert.True(looked <= OmniTumblrContentSources.RunwayDiskChecks(12), $"looked for {looked} files");
            Assert.Equal(13, looked); // twelve found, one missing: nothing more
        }

        [Fact]
        public void Runway_WhenTheFirstFilesAreGone_StopsLookingAfterThreeTimesThePreview()
        {
            using var h = new Harness(Now);
            string blogId = MemeBlog(h);
            for (int i = 0; i < 500; i++)
                h.Catalog.Reels.Add(new ReelInfo { PostId = "r" + i, Views = 1000 - i, CreatedUtc = Now.AddDays(-1), DownloadedUtc = Now, FilePath = $@"D:\gone\r{i}.mp4" });
            int looked = 0;
            h.Content.FileExists = _ => { looked++; return false; };

            var (eligible, next) = h.Content.Runway(h.Blog(blogId).Strategy, blogId, new HashSet<string>(), Now, 12);

            Assert.Equal(500, eligible);
            Assert.Empty(next);
            Assert.Equal(36, looked);
        }

        [Fact]
        public void ContentPreview_UsesTheFrameMemeScraperAlreadyKeeps()
        {
            using var h = new Harness(Now);
            var reel = h.AddReel("777");
            string frame = h.Dir.File("memescraper-thumbs/777.jpg");
            h.Catalog.Thumbnails["777"] = frame;
            var candidate = new ContentCandidate { Key = OmniTumblrContentSources.ReelKey("777"), Source = ContentSourceKind.MemeScraper, Kind = PostKind.Video, FilePath = reel.FilePath! };

            Assert.Equal(frame, h.Content.ExistingThumbnail(candidate));
            Assert.Null(h.Content.ExistingThumbnail(new ContentCandidate { Key = "lib:a.mp4", Source = ContentSourceKind.Library, FilePath = h.Dir.File("a.mp4") }));
            Assert.Null(h.Content.ExistingThumbnail(new ContentCandidate { Key = "ig:888", Source = ContentSourceKind.MemeScraper, FilePath = "x.mp4" }));
        }

        [Fact]
        public void Thumbnails_AreKeptByTheBrowser_OnlyWhenTheUrlNamesTheVersion()
        {
            Assert.Contains("immutable", OmniTumblrRoutes.MediaCaching(full: false, versioned: true));
            Assert.DoesNotContain("immutable", OmniTumblrRoutes.MediaCaching(full: false, versioned: false));
            Assert.DoesNotContain("immutable", OmniTumblrRoutes.MediaCaching(full: true, versioned: true));
        }

        [Fact]
        public void PostSummary_MediaVersion_ChangesWithThePost()
        {
            var post = new OmniTumblrPost { BlogId = "b", UpdatedUtc = Now };
            string before = OmniTumblrViews.Summary(post, null).MediaVersion;
            post.UpdatedUtc = Now.AddSeconds(1);
            Assert.NotEqual(before, OmniTumblrViews.Summary(post, null).MediaVersion);
            Assert.False(string.IsNullOrEmpty(before));
        }

        // ─────────────────────────────── store ───────────────────────────────

        private static MediaFingerprint BigFingerprint(int seed)
        {
            var rng = new Random(seed);
            var frames = Enumerable.Range(0, 240).Select(_ => new FrameHash((ulong)rng.NextInt64() | 1UL, (ulong)rng.NextInt64())).ToArray();
            return OmniTumblrFingerprintMath.Create(120, frames, frames);
        }

        /// <summary>
        /// Every new fingerprint used to re-serialise the whole fingerprints.json (up to 5,000 × ~10 KB) inside the
        /// lock every route reads through. Now it is copied by reference under the lock and serialised outside it.
        /// </summary>
        [Fact]
        public async Task Save_SerialisesFingerprintsOutsideTheLock()
        {
            using var dir = new TempDir();
            var store = new OmniTumblrStore(Path.Combine(dir.Path, "data"), _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            store.Load();
            store.Mutate(s =>
            {
                for (int i = 0; i < 3000; i++) s.Fingerprints["ig:" + i] = BigFingerprint(i);
                s.MarkFingerprints();
            });

            await store.FlushAsync();
            var stats = store.Stats;
            output.WriteLine($"3,000 fingerprints: {stats.FingerprintBytes / 1024 / 1024.0:0.0} MB serialised in {stats.FingerprintSerialiseMs} ms; lock held {stats.LastSaveLockMs} ms");

            Assert.Equal(3000, stats.Fingerprints);
            Assert.True(stats.FingerprintBytes > 10_000_000, $"{stats.FingerprintBytes} bytes");
            Assert.True(stats.LastSaveLockMs < stats.FingerprintSerialiseMs, $"lock {stats.LastSaveLockMs} ms vs serialise {stats.FingerprintSerialiseMs} ms");
            Assert.True(stats.LastSaveLockMs < 100, $"lock held {stats.LastSaveLockMs} ms");

            var reloaded = new OmniTumblrStore(Path.Combine(dir.Path, "data"), _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            reloaded.Load();
            Assert.Equal(3000, reloaded.Read(s => s.Fingerprints.Count));
            Assert.Equal(store.Read(s => s.Fingerprints["ig:42"].WholeHashes), reloaded.Read(s => s.Fingerprints["ig:42"].WholeHashes));
        }

        [Fact]
        public async Task BackgroundSaves_WriteFingerprintsAtMostOncePerInterval_ButAnExplicitFlushWritesThem()
        {
            using var dir = new TempDir();
            string root = Path.Combine(dir.Path, "data");
            var store = new OmniTumblrStore(root, _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32))) { FingerprintSaveInterval = TimeSpan.FromHours(1) };
            store.Load();
            store.StartFlusher();
            try
            {
                store.Mutate(s => { s.Fingerprints["ig:1"] = BigFingerprint(1); s.MarkFingerprints(); });
                await WaitFor(() => store.Stats.FingerprintsSavedUtc != null);
                Assert.Equal(1, Reload(root).Read(s => s.Fingerprints.Count));

                long saves = store.Stats.Saves;
                store.Mutate(s => { s.Fingerprints["ig:2"] = BigFingerprint(2); s.MarkFingerprints(); s.AddEvent(EventLevel.Info, "plan", "planned"); });
                await WaitFor(() => store.Stats.Saves > saves); // the event went out…
                Assert.Equal(1, Reload(root).Read(s => s.Fingerprints.Count)); // …the fingerprints waited

                await store.FlushAsync();
                Assert.Equal(2, Reload(root).Read(s => s.Fingerprints.Count));
            }
            finally
            {
                await store.StopAsync();
            }
        }

        [Fact]
        public void LockWaits_AreCounted()
        {
            using var dir = new TempDir();
            var store = new OmniTumblrStore(Path.Combine(dir.Path, "data"), _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            store.Load();
            using var held = new ManualResetEventSlim();
            var holder = Task.Run(() => store.Read(_ => { held.Set(); Thread.Sleep(120); return 0; }));
            held.Wait();
            store.Read(_ => 0);
            holder.Wait();

            var stats = store.Stats;
            Assert.Equal(1, stats.Waits);
            Assert.True(stats.MaxWaitMs >= 50, $"{stats.MaxWaitMs} ms");
            Assert.Equal(1, stats.WaitsLastHour);
        }

        private static OmniTumblrStore Reload(string root)
        {
            var store = new OmniTumblrStore(root, _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            store.Load();
            return store;
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition never held");
                await Task.Delay(25);
            }
        }

        // ─────────────────────────────── edge history ───────────────────────────────

        private static OmniTumblrEvent Blocked(DateTime at, string? trigger = "tagged") => new()
        {
            Utc = at, Level = EventLevel.Warning, Kind = OmniTumblrFleetPulse.BlockedKind,
            Message = "Tumblr's edge is refusing this server's requests (an HTML 403 from nginx, before the API) — the server's network, not the accounts or posts. Posts and sync wait; next check 14:21 UTC."
                + (trigger == null ? "" : $" First refused: {trigger} at {at:HH:mm:ss} UTC (x-rid -)."),
        };

        private static OmniTumblrEvent Cleared(DateTime at) => new() { Utc = at, Level = EventLevel.Success, Kind = OmniTumblrFleetPulse.ClearedKind, Message = "Tumblr is answering this server again after 38 min." };

        /// <summary>The live pattern, 28 Oct–13 Nov 2026: blocked about 38 minutes, open about 30, round the clock.</summary>
        private static List<OmniTumblrEvent> LivePattern(DateTime from, DateTime to)
        {
            var events = new List<OmniTumblrEvent>();
            for (DateTime t = from; t < to; t = t.AddMinutes(68))
            {
                events.Add(Blocked(t, events.Count % 4 == 0 ? "blog/notifications" : "tagged"));
                if (t.AddMinutes(38) < to) events.Add(Cleared(t.AddMinutes(38)));
            }
            return events;
        }

        [Fact]
        public void EdgeHistory_ReadsTheLivePattern()
        {
            var events = LivePattern(Now.AddDays(-16), Now.AddMinutes(-5));
            var history = OmniTumblrFleetPulse.EdgeHistory(events, Now, new TumblrEdgeGateStatus { Blocked = false, LastClearedUtc = Now.AddMinutes(-20) });

            Assert.InRange(history.AnsweredPct7d!.Value, 42, 46); // 30 of every 68 minutes
            Assert.InRange(history.BlockedHours7d, 92, 97);
            Assert.InRange(history.Blocks7d, 147, 150);
            Assert.Equal(38, history.MedianBlockMinutes);
            Assert.Equal(30, history.MedianOpenMinutes);
            Assert.Equal(14, history.Days.Count);
            Assert.All(history.Days, d => Assert.True(d.Covered));
            Assert.InRange(history.Days[5].BlockedMinutes, 790, 820); // a whole day: 38/68 of 1,440 minutes
            Assert.Equal("tagged", history.Triggers[0].Key);
            Assert.Contains(history.Triggers, t => t.Key == "blog/notifications");
            Assert.All(history.Spans, sp => Assert.True((sp.ToUtc ?? Now) >= Now.AddHours(-72)));
        }

        [Fact]
        public void EdgeHistory_AnOpenBlockRunsToNow_OnlyWhileTheGateSaysBlocked()
        {
            var events = new List<OmniTumblrEvent> { Cleared(Now.AddDays(-3)), Blocked(Now.AddHours(-2)) };

            var blocked = OmniTumblrFleetPulse.EdgeHistory(events, Now, new TumblrEdgeGateStatus { Blocked = true, BlockedSinceUtc = Now.AddHours(-2) });
            Assert.Equal(2, blocked.BlockedHours24h);
            Assert.Null(blocked.Spans.Single().ToUtc);

            // A restart reopens the gate without a "cleared" event: how long that block lasted is unknown, so it counts nothing.
            var restarted = OmniTumblrFleetPulse.EdgeHistory(events, Now, new TumblrEdgeGateStatus { Blocked = false });
            Assert.Equal(0, restarted.BlockedHours24h);
            Assert.Empty(restarted.Spans);
        }

        [Fact]
        public void EdgeHistory_SaysNothingAboutTimeBeforeTheLogStarts()
        {
            var events = new List<OmniTumblrEvent> { Blocked(Now.AddHours(-3)), Cleared(Now.AddHours(-2)) };
            var history = OmniTumblrFleetPulse.EdgeHistory(events, Now, null);

            Assert.Equal(Now.AddHours(-3), history.CoverageStartUtc);
            Assert.InRange(history.AnsweredPct7d!.Value, 66, 67); // 1 h blocked of the 3 h on record, not of 7 days
            Assert.False(history.Days[0].Covered);
            Assert.Null(OmniTumblrFleetPulse.EdgeHistory(new List<OmniTumblrEvent> { Blocked(Now.AddMinutes(-20)) }, Now, null).AnsweredPct7d);
        }

        [Theory]
        [InlineData("… next check 14:21 UTC. First refused: tagged at 14:16:35 UTC (x-rid -).", "tagged")]
        [InlineData("… next check 14:21 UTC. First refused: blog/posts:create at 09:00:01 UTC (x-rid 7f).", "blog/posts:create")]
        [InlineData("… next check 14:21 UTC.", null)]
        public void EdgeHistory_FindsTheRefusedCall(string message, string? trigger) =>
            Assert.Equal(trigger, OmniTumblrFleetPulse.TriggerOf(message));

        [Fact]
        public void Attention_SaysHowOftenTumblrRefuses_EvenWhileItAnswers()
        {
            using var h = new Harness(Now);
            h.Connect();
            var history = OmniTumblrFleetPulse.EdgeHistory(LivePattern(Now.AddDays(-8), Now.AddMinutes(-40)), Now, null);
            var answering = h.Store.Read(s => OmniTumblrViews.Attention(s, Now, true, true, true, new TumblrEdgeGateStatus { Blocked = false }, history));
            var item = Assert.Single(answering, a => a.Title == "Tumblr keeps refusing this server");
            Assert.Equal("warning", item.Level);
            Assert.Contains("56% of the time", item.Detail);

            var blocked = h.Store.Read(s => OmniTumblrViews.Attention(s, Now, true, true, true,
                new TumblrEdgeGateStatus { Blocked = true, BlockedSinceUtc = Now.AddMinutes(-10), NextCheckUtc = Now.AddMinutes(5) }, history));
            Assert.Contains("refused the server 56%", Assert.Single(blocked, a => a.Level == "error" && a.Title.StartsWith("Tumblr is refusing")).Detail);
            Assert.DoesNotContain(blocked, a => a.Title == "Tumblr keeps refusing this server");

            var healthy = OmniTumblrFleetPulse.EdgeHistory(new List<OmniTumblrEvent> { Blocked(Now.AddDays(-2)), Cleared(Now.AddDays(-2).AddMinutes(30)) }, Now, null);
            Assert.DoesNotContain(h.Store.Read(s => OmniTumblrViews.Attention(s, Now, true, true, true, null, healthy)), a => a.Title.Contains("refus"));
        }

        [Fact]
        public void Week_CountsWhatEachBlogDidFromTheLog()
        {
            using var h = new Harness(Now);
            string conn = h.Connect();
            string a = h.AddBlog(conn, name: "one");
            string b = h.AddBlog(conn, name: "two");
            h.Store.Mutate(s =>
            {
                s.AddEvent(EventLevel.Success, "publish.ok", "ok", a, utc: Now.AddDays(-1));
                s.AddEvent(EventLevel.Success, "publish.ok", "ok", a, utc: Now.AddDays(-8)); // older than a week
                s.AddEvent(EventLevel.Warning, "publish.edge-wait", "wait", a, utc: Now.AddHours(-3));
                s.AddEvent(EventLevel.Warning, "reblog.none", "nothing", a, utc: Now.AddHours(-2));
                s.AddEvent(EventLevel.Warning, "reblog.none", "nothing", a, utc: Now.AddHours(-1));
                s.AddEvent(EventLevel.Info, "reblog.picked", "picked", b, utc: Now.AddHours(-1));
                s.AddEvent(EventLevel.Warning, "publish.missed", "missed", b, utc: Now.AddHours(-1));
            });

            var week = h.Store.Read(s => OmniTumblrFleetPulse.Week(s, Now)).ToDictionary(w => w.BlogId);
            Assert.Equal((1, 1, 2, 0), (week[a].Published, week[a].EdgeWaits, week[a].ReblogsEmpty, week[a].ReblogsPicked));
            Assert.Equal((0, 1, 1), (week[b].Published, week[b].ReblogsPicked, week[b].Missed));
        }
    }
}
