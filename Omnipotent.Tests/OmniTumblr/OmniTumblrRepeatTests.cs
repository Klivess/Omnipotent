using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;
using System.Security.Cryptography;

namespace Omnipotent.Tests.OmniTumblr
{
    /// <summary>
    /// The same clip must not go out on several blogs (Oct 27–28 2026: three blogs posted one video within a day,
    /// because Instagram meme accounts repost each other and each repost is a reel with its own key).
    /// </summary>
    public class OmniTumblrRepeatTests
    {
        // Wednesday 7 Oct 2026, 12:00 UTC; the default slot is Friday 18:00 London (Fri 9 Oct 17:00 UTC).
        private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static string AutopilotBlog(Harness h, string connection, string name, Action<OmniTumblrStrategy>? configure = null) =>
            h.AddBlog(connection, b =>
            {
                b.Autopilot = true;
                b.Strategy.Source = ContentSourceKind.MemeScraper;
                b.Strategy.CaptionMode = CaptionMode.AI;
                configure?.Invoke(b.Strategy);
            }, name);

        /// <summary>An autopilot post of <paramref name="reel"/>, as one planned before fingerprints existed.</summary>
        private static string AddReelPost(Harness h, string blogId, ReelInfo reel, DateTime scheduledUtc, PostStatus status) =>
            h.Store.Mutate(s =>
            {
                var post = new OmniTumblrPost
                {
                    BlogId = blogId,
                    Origin = PostOrigin.Autopilot,
                    Kind = PostKind.Video,
                    Status = status,
                    SlotUtc = scheduledUtc,
                    ScheduledUtc = scheduledUtc,
                    PublishedUtc = status == PostStatus.Published ? scheduledUtc : null,
                    Approved = true,
                    Caption = "a caption",
                    Media = new List<PostMedia> { new() { Path = reel.FilePath!, MimeType = "video/mp4" } },
                    Content = new ContentRef { Kind = ContentSourceKind.MemeScraper, Key = OmniTumblrContentSources.ReelKey(reel.PostId), Origin = reel.Owner },
                };
                s.PostsOf(blogId).Add(post);
                s.Blog(blogId)!.UsedContentKeys.Add(post.Content.Key);
                s.MarkPosts(blogId);
                s.MarkBlog(blogId);
                return post.PostId;
            });

        [Fact]
        public async Task TheSameClipRepostedByAnotherAccount_IsNotPlannedOnASecondBlog()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            var original = h.AddReel("orig", owner: "memeaccount", views: 9000);
            var repost = h.AddReel("repost", owner: "otheraccount", views: 8000);
            h.AddReel("different", views: 1000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);

            await h.Planner.PlanBlogAsync(first, CancellationToken.None);
            await h.Planner.PlanBlogAsync(second, CancellationToken.None);

            Assert.Equal("ig:orig", Assert.Single(h.Posts(first)).Content!.Key);
            Assert.Equal("ig:different", Assert.Single(h.Posts(second)).Content!.Key); // by key alone it was ig:repost
        }

        [Fact]
        public async Task OneBlog_NeverPlansTheSameClipTwice()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, "memeblog", s => s.Slots = new List<WeeklySlot> { new(DayOfWeek.Friday, 18 * 60), new(DayOfWeek.Monday, 9 * 60) });
            var original = h.AddReel("orig", views: 9000);
            var repost = h.AddReel("repost", views: 8000);
            h.AddReel("different", views: 1000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);

            Assert.Equal(2, (await h.Planner.PlanBlogAsync(blogId, CancellationToken.None)).Planned);

            Assert.Equal(new[] { "ig:different", "ig:orig" }, h.Posts(blogId).Select(p => p.Content!.Key).OrderBy(k => k));
        }

        [Fact]
        public async Task ReuseAcrossBlogs_StillLetsABlogPostWhatAnotherPosted()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second", s => s.MemeScraper.AllowReuseAcrossBlogs = true);
            var original = h.AddReel("orig", views: 9000);
            var repost = h.AddReel("repost", views: 8000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);

            await h.Planner.PlanBlogAsync(first, CancellationToken.None);
            await h.Planner.PlanBlogAsync(second, CancellationToken.None);

            Assert.Equal("ig:orig", Assert.Single(h.Posts(second)).Content!.Key);
        }

        [Fact]
        public async Task UnreadableMedia_IsJudgedByItsKeyAlone_AsBefore()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, "memeblog");
            h.AddReel("orig", views: 9000);
            h.Fingerprinter.Unreadable = _ => true; // no ffmpeg, or a file it can't decode

            Assert.Equal(1, (await h.Planner.PlanBlogAsync(blogId, CancellationToken.None)).Planned);
            Assert.Equal("ig:orig", Assert.Single(h.Posts(blogId)).Content!.Key);
        }

        [Fact]
        public async Task APlannedRepeatOfAPublishedClip_IsSwapped_AndThePublishedOneKeepsIt()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            var original = h.AddReel("orig", owner: "memeaccount", views: 9000);
            var repost = h.AddReel("repost", owner: "otheraccount", views: 8000);
            h.AddReel("different", views: 1000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);
            string published = AddReelPost(h, first, original, Start.AddHours(-20), PostStatus.Published);
            string repeat = AddReelPost(h, second, repost, Start.AddHours(5), PostStatus.Ready);

            Assert.Equal(1, await h.Planner.SwapRepeatsAsync(CancellationToken.None));

            var swapped = h.Post(repeat);
            Assert.Equal("ig:different", swapped.Content!.Key);
            Assert.Equal(PostStatus.Planned, swapped.Status); // a new caption is written for the new video
            Assert.Equal(Start.AddHours(5), swapped.SlotUtc);
            Assert.Contains("ig:repost", h.Blog(second).UsedContentKeys); // the repeat never comes back
            Assert.Equal("ig:orig", h.Post(published).Content!.Key);
            var ev = Assert.Single(h.Store.Read(s => s.Events.Where(e => e.Kind == "post.repeat").ToList()));
            Assert.Contains("@first already posted", ev.Message);

            // Checked once: nothing more to do, and nothing recomputed.
            int calls = h.Fingerprinter.Calls;
            Assert.Equal(0, await h.Planner.SwapRepeatsAsync(CancellationToken.None));
            Assert.Equal(calls, h.Fingerprinter.Calls);
        }

        [Fact]
        public async Task BetweenTwoPlannedCopies_TheLaterOneIsSwapped()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            var original = h.AddReel("orig", views: 9000);
            var repost = h.AddReel("repost", views: 8000);
            h.AddReel("different", views: 1000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);
            // The second blog's copy goes out first, so it keeps the clip.
            string later = AddReelPost(h, first, original, Start.AddHours(9), PostStatus.Ready);
            string earlier = AddReelPost(h, second, repost, Start.AddHours(3), PostStatus.Ready);

            Assert.Equal(1, await h.Planner.SwapRepeatsAsync(CancellationToken.None));

            Assert.Equal("ig:repost", h.Post(earlier).Content!.Key);
            Assert.Equal("ig:different", h.Post(later).Content!.Key);
            Assert.Contains("@second has planned for", Assert.Single(h.Store.Read(s => s.Events.Where(e => e.Kind == "post.repeat").ToList())).Message);
        }

        [Fact]
        public async Task ARepeat_WithNothingDifferentLeft_IsCancelled_NotPosted()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            var original = h.AddReel("orig", views: 9000);
            var repost = h.AddReel("repost", views: 8000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!);
            AddReelPost(h, first, original, Start.AddHours(-2), PostStatus.Published);
            string repeat = AddReelPost(h, second, repost, Start.AddHours(1), PostStatus.Ready);

            Assert.Equal(1, await h.Planner.SwapRepeatsAsync(CancellationToken.None));

            var cancelled = h.Post(repeat);
            Assert.Equal(PostStatus.Cancelled, cancelled.Status);
            Assert.Contains("same clip @first already posted", cancelled.LastError);
        }

        [Fact]
        public async Task PostsEditedByHand_AndBlogsAllowingReuse_AreLeftAlone()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            string third = AutopilotBlog(h, conn, "third", s => s.MemeScraper.AllowReuseAcrossBlogs = true);
            var original = h.AddReel("orig", views: 9000);
            var repost = h.AddReel("repost", views: 8000);
            var another = h.AddReel("another", views: 7000);
            h.AddReel("different", views: 1000);
            h.Fingerprinter.SameClip(original.FilePath!, repost.FilePath!, another.FilePath!);
            AddReelPost(h, first, original, Start.AddHours(-2), PostStatus.Published);
            string edited = AddReelPost(h, second, repost, Start.AddHours(1), PostStatus.Ready);
            h.Store.Mutate(s => s.FindPost(edited)!.ManualOverride = true);
            string reused = AddReelPost(h, third, another, Start.AddHours(2), PostStatus.Ready);

            Assert.Equal(0, await h.Planner.SwapRepeatsAsync(CancellationToken.None));
            Assert.Equal("ig:repost", h.Post(edited).Content!.Key);
            Assert.Equal("ig:another", h.Post(reused).Content!.Key);
        }

        [Fact]
        public async Task Fingerprints_AreComputedOnce_AndSurviveARestart()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, "first");
            string second = AutopilotBlog(h, conn, "second");
            for (int i = 0; i < 3; i++) h.AddReel("r" + i, views: 1000 * (i + 1));

            await h.Planner.PlanBlogAsync(first, CancellationToken.None);
            int afterFirst = h.Fingerprinter.Calls;
            await h.Planner.PlanBlogAsync(second, CancellationToken.None);
            Assert.True(h.Fingerprinter.Calls - afterFirst <= 1); // only the one new candidate it looked at
            int known = h.Store.Read(s => s.Fingerprints.Count);
            Assert.True(known >= 2);

            await h.Store.FlushAsync();
            var reloaded = new OmniTumblrStore(Path.Combine(h.Dir.Path, "data"), _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            reloaded.Load();
            Assert.Equal(known, reloaded.Read(s => s.Fingerprints.Count));
            var key = OmniTumblrContentSources.ReelKey("r2");
            Assert.Equal(h.Fingerprints.Cached(key)!.WholeHashes, reloaded.Read(s => s.Fingerprints[key].WholeHashes));
        }
    }

    public class OmniTumblrFingerprintMathTests
    {
        private const int W = OmniTumblrFingerprintMath.GridWidth, H = OmniTumblrFingerprintMath.GridHeight;

        /// <summary>Random frames where every bit means something (as for busy, high-contrast footage).</summary>
        private static FrameHash[] RandomFrames(int seed, int count)
        {
            var rng = new Random(seed);
            return Enumerable.Range(0, count).Select(_ => new FrameHash((ulong)rng.NextInt64() | 1UL, ulong.MaxValue)).ToArray();
        }

        private static FrameHash FlipBits(FrameHash hash, Random rng, int bits)
        {
            ulong value = hash.Bits;
            for (int i = 0; i < bits; i++) value ^= 1UL << rng.Next(64);
            return hash with { Bits = value == 0 ? 1 : value };
        }

        /// <summary>A 9-wide row of brightness steps, neighbours at least 25 apart (no near-ties to flip a bit).</summary>
        private static double[] Steps(Random rng)
        {
            var row = new double[9];
            row[0] = rng.Next(30, 226);
            for (int x = 1; x < 9; x++)
            {
                double next;
                do next = rng.Next(30, 226); while (Math.Abs(next - row[x - 1]) < 25);
                row[x] = next;
            }
            return row;
        }

        /// <summary>
        /// A clip as ffmpeg hands it over (raw greyscale grid frames): a picture of 9×8 blocks that changes every
        /// frame, drawn into the given rectangle. Outside it is a static caption bar (white with dark text), or
        /// a flat <paramref name="background"/> such as black letterboxing.
        /// </summary>
        private static byte[] Clip(int seed, int frames, int top, int bottom, int left, int right, byte? background = null)
        {
            var rng = new Random(seed);
            var pictures = Enumerable.Range(0, frames).Select(_ => Enumerable.Range(0, 8).Select(_ => Steps(rng)).ToArray()).ToList();
            var bar = new Random(99);
            var outside = Enumerable.Range(0, W * H).Select(_ => background ?? (byte)(bar.Next(2) == 0 ? 250 : bar.Next(0, 60))).ToArray();
            var raw = new byte[frames * W * H];
            for (int f = 0; f < frames; f++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        bool inside = y >= top && y < bottom && x >= left && x < right;
                        raw[f * W * H + y * W + x] = inside
                            ? (byte)pictures[f][(y - top) * 8 / (bottom - top)][(x - left) * 9 / (right - left)]
                            : outside[y * W + x];
                    }
            return raw;
        }

        [Fact]
        public void DifferenceHash_MarksFlatFrames_AndWhichBitsMeanSomething()
        {
            Assert.True(OmniTumblrFingerprintMath.DifferenceHash(Enumerable.Repeat((byte)16, 72).ToArray()).IsFlat);
            // Every row falls left to right in clear steps: every bit set, every bit meaningful.
            var falling = Enumerable.Range(0, 72).Select(i => (byte)(200 - (i % 9) * 20)).ToArray();
            Assert.Equal(new FrameHash(ulong.MaxValue, ulong.MaxValue), OmniTumblrFingerprintMath.DifferenceHash(falling));
            // Every row rises: no bit set, which is kept apart from "flat".
            var rising = Enumerable.Range(0, 72).Select(i => (byte)(20 + (i % 9) * 20)).ToArray();
            Assert.Equal(new FrameHash(1UL, ulong.MaxValue), OmniTumblrFingerprintMath.DifferenceHash(rising));
            // A frame that is black apart from its top row: only that row's steps mean anything.
            var topRowOnly = Enumerable.Range(0, 72).Select(i => i < 9 ? (byte)(200 - i * 20) : (byte)0).ToArray();
            Assert.Equal(0xFFUL, OmniTumblrFingerprintMath.DifferenceHash(topRowOnly).Mask);
        }

        [Fact]
        public void Frames_SurviveReencodingNoise_AndATrimmedStart()
        {
            var rng = new Random(7);
            var original = RandomFrames(1, 40);
            // Re-encoded: up to 6 bits off per frame; trimmed: the first 1.5 s (3 frames) gone.
            var repost = original.Skip(3).Select(h => FlipBits(h, rng, rng.Next(0, 7))).ToArray();
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(original, repost) >= 0.9);
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(original, RandomFrames(2, 40)) < 0.1);
        }

        [Fact]
        public void FlatFrames_NeverMatch_AndAMostlyFlatClipSaysNothing()
        {
            var blank = new FrameHash[40];
            Assert.Equal(0.0, OmniTumblrFingerprintMath.FrameSimilarity(blank, blank));
            var mostlyBlank = new FrameHash[40];
            mostlyBlank[0] = new FrameHash(12345, ulong.MaxValue);
            Assert.Equal(0.0, OmniTumblrFingerprintMath.FrameSimilarity(mostlyBlank, mostlyBlank));
        }

        [Fact]
        public void ARepostWithACaptionBarAndBorders_HashesLikeTheOriginal()
        {
            var original = Clip(seed: 3, frames: 20, top: 0, bottom: H, left: 0, right: W);
            // The repost: the clip shrunk under a static caption bar, with padding either side.
            var repost = Clip(seed: 3, frames: 20, top: 40, bottom: H, left: 4, right: W - 4);

            Assert.Equal((40, H, 4, W - 4), OmniTumblrFingerprintMath.MovingRegion(repost, 20));
            Assert.Equal((0, H, 0, W), OmniTumblrFingerprintMath.MovingRegion(original, 20));
            var a = OmniTumblrFingerprintMath.HashFrames(original);
            var b = OmniTumblrFingerprintMath.HashFrames(repost);
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Region, b.Region) >= 0.9);
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Whole, b.Whole) < 0.6); // why the moving region is kept
            // A different clip in the same layout is still a different clip.
            var other = OmniTumblrFingerprintMath.HashFrames(Clip(seed: 4, frames: 20, top: 40, bottom: H, left: 4, right: W - 4));
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Region, other.Region) < 0.1);
        }

        [Fact]
        public void LetterboxedClips_DoNotMatchOnTheirBlackBars()
        {
            // Two different clips, each a small picture in the middle of a black frame (Nov 2026: such pairs
            // matched while every bit counted, because the black cells "agreed").
            var a = OmniTumblrFingerprintMath.HashFrames(Clip(seed: 5, frames: 20, top: 48, bottom: 80, left: 0, right: W, background: 0));
            var b = OmniTumblrFingerprintMath.HashFrames(Clip(seed: 6, frames: 20, top: 48, bottom: 80, left: 0, right: W, background: 0));
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Whole, b.Whole) < 0.1);
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Region, b.Region) < 0.1);
            // The same letterboxed clip still matches itself, through its moving region: across the whole frame
            // the thin strip of picture gives too few meaningful bits to say anything.
            var again = OmniTumblrFingerprintMath.HashFrames(Clip(seed: 5, frames: 20, top: 48, bottom: 80, left: 0, right: W, background: 0));
            Assert.True(OmniTumblrFingerprintMath.FrameSimilarity(a.Region, again.Region) >= 0.9);
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(OmniTumblrFingerprintMath.Create(10, a.Whole, a.Region), OmniTumblrFingerprintMath.Create(10, again.Whole, again.Region)));
        }

        [Fact]
        public void StillClips_MustMatchAlmostExactly()
        {
            // Two different night-skyline pictures held over music were within 3 of their 21 meaningful bits:
            // close enough for one frame among many of a moving clip, not for a clip that is one frame.
            ulong meaningful = (1UL << 21) - 1;
            var skyline = new FrameHash(0b1_0110_1100_1011_0101_0011UL, meaningful);
            var otherSkyline = skyline with { Bits = skyline.Bits ^ 0b111UL };
            Assert.True(OmniTumblrFingerprintMath.Alike(skyline, otherSkyline));
            Assert.False(OmniTumblrFingerprintMath.Alike(skyline, otherSkyline, still: true));

            MediaFingerprint Held(FrameHash frame)
            {
                var frames = Enumerable.Repeat(frame, 30).ToArray();
                return OmniTumblrFingerprintMath.Create(15, frames, frames);
            }
            Assert.True(Held(skyline).IsStill);
            Assert.False(OmniTumblrFingerprintMath.IsSameMedia(Held(skyline), Held(otherSkyline)));
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(Held(skyline), Held(skyline))); // a re-upload of the same picture
            Assert.False(OmniTumblrFingerprintMath.Create(15, RandomFrames(8, 30), RandomFrames(8, 30)).IsStill);
        }

        [Fact]
        public void AStillPicture_IsHashedWhole()
        {
            var still = Clip(seed: 5, frames: 1, top: 0, bottom: H, left: 0, right: W);
            var held = Enumerable.Repeat(still, 10).SelectMany(b => b).ToArray(); // the same frame for 5 seconds
            Assert.Equal((0, H, 0, W), OmniTumblrFingerprintMath.MovingRegion(held, 10));
            var expected = OmniTumblrFingerprintMath.HashFrames(still).Whole[0];
            var hashes = OmniTumblrFingerprintMath.HashFrames(held);
            Assert.All(hashes.Whole.Concat(hashes.Region), h => Assert.Equal(expected, h));
        }

        [Fact]
        public void Mirror_IsTheHashOfTheFlippedFrame()
        {
            var rng = new Random(11);
            var cells = Enumerable.Range(0, 8).SelectMany(_ => Steps(rng)).Select(v => (byte)v).ToArray();
            var flipped = Enumerable.Range(0, 72).Select(i => cells[i / 9 * 9 + 8 - i % 9]).ToArray();
            var hash = OmniTumblrFingerprintMath.DifferenceHash(cells);
            Assert.Equal(OmniTumblrFingerprintMath.DifferenceHash(flipped), OmniTumblrFingerprintMath.Mirror(hash));
            Assert.Equal(hash, OmniTumblrFingerprintMath.Mirror(OmniTumblrFingerprintMath.Mirror(hash)));
            Assert.True(OmniTumblrFingerprintMath.Mirror(default).IsFlat); // a flat frame stays flat
        }

        [Fact]
        public void SameMedia_NeedsSimilarLength_SeesMirrors_AndSurvivesStorage()
        {
            var frames = RandomFrames(5, 30);
            var a = OmniTumblrFingerprintMath.Create(15, frames, frames);
            var stored = OmniTumblrStore.Clone(a);
            Assert.Equal(frames, stored.WholeHashes);
            Assert.Equal(frames, stored.RegionHashes);
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(a, stored));
            var mirrored = frames.Select(OmniTumblrFingerprintMath.Mirror).ToArray();
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(a, OmniTumblrFingerprintMath.Create(15, mirrored, mirrored)));
            var other = RandomFrames(6, 30);
            Assert.False(OmniTumblrFingerprintMath.IsSameMedia(a, OmniTumblrFingerprintMath.Create(15, other, other)));
            // Matching on the moving region alone is enough (a captioned repost).
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(a, OmniTumblrFingerprintMath.Create(15, other, frames)));
            // The same opening on a clip three times as long is another video.
            Assert.False(OmniTumblrFingerprintMath.IsSameMedia(a, OmniTumblrFingerprintMath.Create(45, frames, frames)));
            // A picture (one frame) matches itself, not a video that starts with it.
            var still = frames.Take(1).ToArray();
            var photo = OmniTumblrFingerprintMath.Create(null, still, still);
            Assert.True(OmniTumblrFingerprintMath.IsSameMedia(photo, OmniTumblrStore.Clone(photo)));
            Assert.False(OmniTumblrFingerprintMath.IsSameMedia(photo, a));
        }
    }
}
