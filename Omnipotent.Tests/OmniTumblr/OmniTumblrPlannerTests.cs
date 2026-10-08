using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    public class OmniTumblrPlannerTests
    {
        // Wednesday 7 Oct 2026, 12:00 UTC. Weekly Friday 18:00 London = Fri 9 Oct 17:00 UTC.
        private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static string AutopilotBlog(Harness h, string connection, Action<OmniTumblrStrategy>? configure = null, string name = "memeblog") =>
            h.AddBlog(connection, b =>
            {
                b.Autopilot = true;
                b.Strategy.Source = ContentSourceKind.MemeScraper;
                b.Strategy.CaptionMode = CaptionMode.AI;
                configure?.Invoke(b.Strategy);
            }, name);

        [Fact]
        public async Task Planning_FillsEachSlotInTheHorizon_WithDistinctContent()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, s =>
            {
                s.PlanAheadDays = 14;
                s.Slots = new List<WeeklySlot> { new(DayOfWeek.Friday, 18 * 60), new(DayOfWeek.Monday, 9 * 60) };
            });
            for (int i = 0; i < 6; i++) h.AddReel("r" + i, views: 1000 * (i + 1));

            var outcome = await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);

            var posts = h.Posts(blogId).OrderBy(p => p.SlotUtc).ToList();
            Assert.Equal(4, outcome.Planned); // Fri 9, Mon 12, Fri 16, Mon 19
            Assert.Equal(4, posts.Count);
            Assert.Equal(new DateTime(2026, 10, 9, 17, 0, 0, DateTimeKind.Utc), posts[0].SlotUtc);
            Assert.All(posts, p => Assert.Equal(PostStatus.Planned, p.Status));
            Assert.All(posts, p => Assert.True(p.CaptionPending));
            Assert.Equal(4, posts.Select(p => p.Content!.Key).Distinct().Count());
            Assert.Equal("ig:r5", posts[0].Content!.Key); // "Best" picks the most viewed first
            Assert.Equal(4, h.Blog(blogId).UsedContentKeys.Count);

            // Planning again is a no-op: every slot is taken.
            var again = await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            Assert.Equal(0, again.Planned);
            Assert.Equal(4, h.Posts(blogId).Count);
        }

        [Fact]
        public async Task ContentUsedByAnotherBlog_IsNotReused_UnlessAllowed()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string first = AutopilotBlog(h, conn, name: "first");
            string second = AutopilotBlog(h, conn, name: "second");
            h.AddReel("only");

            Assert.Equal(1, (await h.Planner.PlanBlogAsync(first, CancellationToken.None)).Planned);
            var blocked = await h.Planner.PlanBlogAsync(second, CancellationToken.None);
            Assert.Equal(0, blocked.Planned);
            Assert.NotNull(blocked.Warning);
            Assert.NotNull(h.Blog(second).Health.ContentWarning);

            h.Store.Mutate(s => s.Blog(second)!.Strategy.MemeScraper.AllowReuseAcrossBlogs = true);
            Assert.Equal(1, (await h.Planner.PlanBlogAsync(second, CancellationToken.None)).Planned);
            Assert.Null(h.Blog(second).Health.ContentWarning);
        }

        [Fact]
        public async Task Filters_NicheSourceAgeAndViews_AreApplied()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, s =>
            {
                s.PlanAheadDays = 30;
                s.MemeScraper.Niches = new List<string> { "gaming" };
                s.MemeScraper.MaxAgeDays = 10;
                s.MemeScraper.MinViews = 500;
            });
            h.Catalog.Niches["gamer_memes"] = new List<string> { "Gaming" };
            h.Catalog.Niches["cat_memes"] = new List<string> { "cats" };
            h.AddReel("ok", owner: "gamer_memes", views: 900);
            h.AddReel("wrong-niche", owner: "cat_memes", views: 99999);
            h.AddReel("too-old", owner: "gamer_memes", views: 900, created: Start.AddDays(-30));
            h.AddReel("too-small", owner: "gamer_memes", views: 10);

            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var keys = h.Posts(blogId).Select(p => p.Content!.Key).ToList();
            Assert.Equal(new[] { "ig:ok" }, keys);
        }

        [Fact]
        public async Task ACancelledPostFreesItsSlotForNewContent_ButASkippedPostKeepsItEmpty()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn);
            h.AddReel("a", views: 3000);
            h.AddReel("b", views: 2000);
            h.AddReel("c", views: 1000);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var first = Assert.Single(h.Posts(blogId));

            h.Store.Mutate(s => s.FindPost(first.PostId)!.Status = PostStatus.Cancelled);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var replacement = Assert.Single(h.Posts(blogId), p => p.Status == PostStatus.Planned);
            Assert.Equal(first.SlotUtc, replacement.SlotUtc);
            Assert.Equal("ig:b", replacement.Content!.Key); // the cancelled content stays used

            h.Store.Mutate(s => s.FindPost(replacement.PostId)!.Status = PostStatus.Skipped);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            Assert.DoesNotContain(h.Posts(blogId), p => p.Status == PostStatus.Planned);
        }

        [Fact]
        public async Task ChangingTheSchedule_MovesPlannedPostsToTheNewSlots()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn);
            h.AddReel("a");
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var post = Assert.Single(h.Posts(blogId));
            h.Store.Mutate(s => s.FindPost(post.PostId)!.Caption = "kept caption");

            h.Store.Mutate(s => s.Blog(blogId)!.Strategy.Slots = new List<WeeklySlot> { new(DayOfWeek.Saturday, 10 * 60) });
            int moved = h.Planner.ReconcileSlots(blogId);

            Assert.Equal(1, moved);
            var after = h.Post(post.PostId);
            Assert.Equal(new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), after.SlotUtc); // Sat 10:00 BST
            Assert.Equal("kept caption", after.Caption);
            Assert.Equal(PostStatus.Planned, after.Status);
        }

        [Fact]
        public async Task Preparation_WritesAnAiCaption_MergesSuggestedTags_AndMakesThePostReady()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, s => s.FixedTags = new List<string> { "memes" });
            h.AddReel("a");
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);

            int prepared = await h.Planner.PrepareDueAsync(5, CancellationToken.None);

            Assert.Equal(1, prepared);
            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.False(post.CaptionPending);
            Assert.Equal("when the build finally passes", post.Caption);
            Assert.Equal(new[] { "memes", "programming", "relatable" }, post.Tags);
            Assert.Equal("A cat at a keyboard.", post.CaptionInfo.AltText);
            Assert.Equal("test-model", post.CaptionInfo.Model);
            Assert.Contains("original caption", h.Model.Calls.Single().User);
        }

        [Fact]
        public async Task WithApprovalRequired_APreparedPostWaitsForApproval()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn);
            h.Store.Mutate(s => s.Blog(blogId)!.RequireApproval = true);
            h.AddReel("a");
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal(PostStatus.AwaitingApproval, post.Status);
            Assert.False(post.Approved);
        }

        [Fact]
        public async Task AFailingAiCaption_IsRetriedWithBackoff_ThenFallsBackNearItsDeadline()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn, s =>
            {
                s.FixedCaption = "fallback caption";
                s.Ai.Fallback = CaptionFallback.Fixed;
            });
            h.AddReel("a");
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            h.Model.Default = "I'm sorry, I can't.";

            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            var post = Assert.Single(h.Posts(blogId));
            Assert.True(post.CaptionPending);
            Assert.Equal(PostStatus.Planned, post.Status);
            Assert.Equal(1, post.CaptionInfo.Failures);
            Assert.Equal(Start.AddMinutes(3), post.CaptionInfo.RetryAfterUtc);

            // Not retried before its backoff elapses.
            Assert.Equal(0, await h.Planner.PrepareDueAsync(5, CancellationToken.None));

            // Close to the slot, the fallback caption is used rather than missing the post.
            h.Clock.Now = post.ScheduledUtc.AddMinutes(-10);
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            post = h.Post(post.PostId);
            Assert.False(post.CaptionPending);
            Assert.Equal("fallback caption", post.Caption);
            Assert.Equal(PostStatus.Ready, post.Status);
        }

        [Fact]
        public async Task SwapContent_ReplacesThePostsMediaAndRewritesItsCaption()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn);
            h.AddReel("a", views: 5000);
            h.AddReel("b", views: 100);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            var before = Assert.Single(h.Posts(blogId));
            Assert.Equal("ig:a", before.Content!.Key);

            var (ok, _) = await h.Planner.SwapContentAsync(before.PostId, CancellationToken.None);

            Assert.True(ok);
            var after = h.Post(before.PostId);
            Assert.Equal("ig:b", after.Content!.Key);
            Assert.Equal(PostStatus.Planned, after.Status);
            Assert.True(after.CaptionPending);
            Assert.Contains("ig:a", h.Blog(blogId).UsedContentKeys);
        }

        [Fact]
        public async Task RefillSlot_PlansDifferentContentForAFailedPost_WithinGrace()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = AutopilotBlog(h, conn);
            h.AddReel("bad", views: 5000);
            h.AddReel("good", views: 100);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var failed = Assert.Single(h.Posts(blogId));
            h.Clock.Now = failed.ScheduledUtc.AddMinutes(1);
            h.Store.Mutate(s =>
            {
                s.FindPost(failed.PostId)!.Status = PostStatus.Failed;
                s.Blog(blogId)!.RejectedContentKeys.Add("ig:bad");
            });

            string? newPostId = await h.Planner.RefillSlotAsync(failed.PostId, CancellationToken.None);

            Assert.NotNull(newPostId);
            var refill = h.Post(newPostId!);
            Assert.Equal(failed.SlotUtc, refill.SlotUtc);
            Assert.Equal("ig:good", refill.Content!.Key);
            Assert.True(refill.ScheduledUtc >= h.Clock.Now.AddMinutes(2));
            // A second refill for the same slot is a no-op.
            Assert.Null(await h.Planner.RefillSlotAsync(failed.PostId, CancellationToken.None));
        }

        [Fact]
        public async Task FolderAndLibrarySources_ProduceStableKeys()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string folder = Path.Combine(h.Dir.Path, "folder");
            h.Dir.File("folder/one.mp4");
            h.Dir.File("folder/two.jpg");
            h.Dir.File("folder/notes.txt");
            string blogId = h.AddBlog(conn, b =>
            {
                b.Autopilot = true;
                b.Strategy.Source = ContentSourceKind.Folder;
                b.Strategy.Folder.Path = folder;
                b.Strategy.PlanAheadDays = 14;
                b.Strategy.CaptionMode = CaptionMode.None;
            });

            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            var posts = h.Posts(blogId);
            Assert.Equal(2, posts.Count);
            Assert.Contains(posts, p => p.Kind == PostKind.Video && p.Content!.Key == "file:one.mp4:2048");
            Assert.Contains(posts, p => p.Kind == PostKind.Photo && p.Content!.Key == "file:two.jpg:2048");

            // Library: uploads for this blog only, first in first out.
            h.Store.Mutate(s => s.Blog(blogId)!.Strategy.Source = ContentSourceKind.Library);
            string libDir = h.Content.LibraryDirectoryFor(blogId);
            Directory.CreateDirectory(libDir);
            File.WriteAllBytes(Path.Combine(libDir, "a.mp4"), new byte[10]);
            var (eligible, next) = h.Content.Runway(h.Blog(blogId).Strategy, blogId, new HashSet<string>(), Start);
            Assert.Equal(1, eligible);
            Assert.Equal("lib:a.mp4", next.Single().Key);
        }
    }
}
