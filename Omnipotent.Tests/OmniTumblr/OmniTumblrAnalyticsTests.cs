using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    public class OmniTumblrAnalyticsSyncTests
    {
        private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task PostsSync_UpdatesOurPostsMetrics_ByIdOrSlug_AndIndexesEverything()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = h.AddBlog(conn);
            string byId = h.AddReadyPost(blogId, p => { p.Status = PostStatus.Published; p.TumblrPostId = "100"; p.PublishedUtc = Start.AddDays(-2); });
            string bySlug = h.AddReadyPost(blogId, p => { p.Status = PostStatus.Published; p.TumblrPostId = "stale-id"; p.Slug = "my-slug-1234"; p.PublishedUtc = Start.AddHours(-3); });
            h.Api.OnGetPosts = offset => offset > 0 ? new TumblrPostsPage() : new TumblrPostsPage
            {
                TotalPosts = 3,
                Posts = new List<TumblrPostSummary>
                {
                    new() { Id = "300", Slug = "my-slug-1234", PublishedUtc = Start.AddHours(-3), NoteCount = 4, PostUrl = "https://memeblog.tumblr.com/post/300", Tags = new() { "memes" } },
                    new() { Id = "200", PublishedUtc = Start.AddDays(-1), NoteCount = 99, Type = "photo" },
                    new() { Id = "100", PublishedUtc = Start.AddDays(-2), NoteCount = 57 },
                },
            };

            await h.Sync.SyncBlogNowAsync(blogId, CancellationToken.None);

            var first = h.Post(byId);
            Assert.Equal(57, first.Metrics.Notes);
            Assert.Equal(57, first.Metrics.NotesAt24h);
            Assert.Null(first.Metrics.NotesAt7d);
            var second = h.Post(bySlug);
            Assert.Equal("300", second.TumblrPostId); // corrected from the slug match
            Assert.Equal("https://memeblog.tumblr.com/post/300", second.TumblrUrl);
            var index = h.Store.Read(s => s.InsightsOf(blogId).Index.ToList());
            Assert.Equal(3, index.Count);
            Assert.Equal(bySlug, index.Single(p => p.Id == "300").OmniPostId);
            Assert.Null(index.Single(p => p.Id == "200").OmniPostId);
            Assert.Equal(3, h.Blog(blogId).Stats.Posts);
        }

        [Fact]
        public async Task NotesBreakdown_IsReadAtMilestones_AndAVanishedPostIsMarkedRemoved()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = h.AddBlog(conn);
            string alive = h.AddReadyPost(blogId, p => { p.Status = PostStatus.Published; p.TumblrPostId = "1"; p.PublishedUtc = Start.AddHours(-5); });
            string gone = h.AddReadyPost(blogId, p => { p.Status = PostStatus.Published; p.TumblrPostId = "2"; p.PublishedUtc = Start.AddHours(-6); });
            h.Api.OnNotes = id => id == "2"
                ? throw new TumblrApiException(TumblrErrorKind.NotFound, 404, null, "not found")
                : new TumblrNotesSummary { TotalNotes = 20, TotalLikes = 12, TotalReblogs = 5 };

            await h.Sync.SyncBlogNowAsync(blogId, CancellationToken.None);

            var post = h.Post(alive);
            Assert.Equal(20, post.Metrics.Notes);
            Assert.Equal(12, post.Metrics.Likes);
            Assert.Equal(5, post.Metrics.Reblogs);
            Assert.Equal(3, post.Metrics.Replies);
            Assert.Equal(PostStatus.Removed, h.Post(gone).Status);

            Assert.False(OmniTumblrAnalyticsSync.NeedsBreakdown(post, Start.AddHours(10)));
            Assert.True(OmniTumblrAnalyticsSync.NeedsBreakdown(post, Start.AddDays(1)));
        }

        [Fact]
        public async Task ActivitySync_PagesBackToWhatItHasSeen()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = h.AddBlog(conn);
            h.Api.OnNotifications = before => before == null
                ? new TumblrNotificationsPage
                {
                    Items = new() { new() { Id = "a", Type = "like", Utc = Start.AddMinutes(-1) }, new() { Id = "b", Type = "follow", Utc = Start.AddMinutes(-2), FromBlog = "fan" } },
                    NextBefore = 12345,
                }
                : new TumblrNotificationsPage { Items = new() { new() { Id = "c", Type = "reblog_with_content", Utc = Start.AddHours(-1) } } };

            await h.Sync.SyncBlogNowAsync(blogId, CancellationToken.None);

            var activity = h.Store.Read(s => OmniTumblrStore.Clone(s.InsightsOf(blogId).Activity));
            Assert.Equal(3, activity.Recent.Count);
            Assert.Equal(1, activity.Daily["2026-10-07"]["follow"]);
            Assert.Equal(1, activity.Daily["2026-10-07"]["reblog"]);
        }

        [Fact]
        public async Task DueJobs_RunOnTheirCadence_AndAnUnauthorizedJobParksTheConnection()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            h.AddBlog(conn);

            Assert.Equal(0, await h.Sync.RunDueAsync(CancellationToken.None)); // first runs are staggered
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            int ran = await h.Sync.RunDueAsync(CancellationToken.None);
            Assert.True(ran >= 6, $"expected every job to run, ran {ran}");
            Assert.Contains("user/info", h.Api.Calls);
            Assert.Contains("user/limits", h.Api.Calls);
            Assert.Equal(0, await h.Sync.RunDueAsync(CancellationToken.None));

            h.Api.UserInfo = _ => throw new TumblrApiException(TumblrErrorKind.Unauthorized, 401, null, "revoked");
            h.Clock.Advance(TimeSpan.FromHours(4));
            await h.Sync.RunDueAsync(CancellationToken.None);
            Assert.Equal(ConnectionHealth.NeedsReauth, h.Connection(conn).Health);
        }

        [Fact]
        public void TheBudget_StopsBackgroundWorkLongBeforeTumblrsLimits()
        {
            var clock = new TestClock(Start);
            var budget = new OmniTumblrApiBudget(clock.Func);
            Assert.True(budget.AllowBackground());
            for (int i = 0; i < 601; i++) budget.Count("blog/posts");
            Assert.False(budget.AllowBackground());
            clock.Advance(TimeSpan.FromHours(1));
            Assert.True(budget.AllowBackground());
            budget.Observe(new TumblrRateLimitHeaders { PerHourRemaining = 100, ObservedUtc = clock.Now });
            Assert.False(budget.AllowBackground());
            Assert.Equal(601, budget.CallsToday());
        }
    }

    public class OmniTumblrViewsTests
    {
        private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Analytics_RanksTagsAndSources_BuildsTheHeatmapInTheBlogsTimeZone_AndCountsFollowers()
        {
            using var h = new Harness(Now);
            string conn = h.Connect();
            string blogId = h.AddBlog(conn, b => { b.Strategy.TimeZone = "America/New_York"; b.Stats.Followers = 200; });
            void Published(string id, DateTime when, long notes, string tag, string origin) => h.AddReadyPost(blogId, p =>
            {
                p.Status = PostStatus.Published;
                p.TumblrPostId = id;
                p.PublishedUtc = when;
                p.Metrics.Notes = notes;
                p.Tags = new List<string> { tag };
                p.Content = new ContentRef { Origin = origin, Key = "ig:" + id };
            });
            Published("1", Now.AddDays(-3), 10, "cats", "catpage");
            Published("2", Now.AddDays(-4), 30, "cats", "catpage");
            Published("3", Now.AddDays(-5), 2, "dogs", "dogpage");
            Published("4", Now.AddDays(-6), 4, "dogs", "dogpage");
            h.Store.Mutate(s =>
            {
                var insights = s.InsightsOf(blogId);
                insights.Snapshots.Add(new BlogSnapshot { Utc = Now.AddDays(-20), Followers = 150 });
                insights.Snapshots.Add(new BlogSnapshot { Utc = Now.AddDays(-1), Followers = 195 });
                OmniTumblrInsights.MergeIndex(insights, new[]
                {
                    new TumblrPostSummary { Id = "1", PublishedUtc = Now.AddDays(-3), NoteCount = 10 },
                    new TumblrPostSummary { Id = "2", PublishedUtc = Now.AddDays(-4), NoteCount = 30 },
                    new TumblrPostSummary { Id = "9", PublishedUtc = Now.AddDays(-2), NoteCount = 500, Summary = "made by hand" },
                }, p => p.Id is "1" or "2" ? "ours" : null);
            });

            var a = h.Store.Read(s => OmniTumblrViews.Analytics(s, blogId, 30, Now));

            Assert.Equal(4, a.PostsPublished);
            Assert.Equal(46, a.Notes);
            Assert.Equal(11.5, a.AvgNotes);
            Assert.Equal(7, a.MedianNotes);
            Assert.Equal(50, a.FollowersDelta);
            Assert.Equal("cats", a.Tags[0].Key);
            Assert.Equal(20, a.Tags[0].AvgNotes);
            Assert.Equal("catpage", a.Sources[0].Key);
            Assert.Equal("9", a.TopPosts[0].Id);
            Assert.False(a.TopPosts[0].Ours);
            Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/New_York").Id, a.TimeZone);
            // Post 9 was published at 12:00 UTC = 08:00 in New York (EDT).
            Assert.Contains(a.Heatmap, c => c.Hour == 8 && c.AvgNotes > 0);
            Assert.Equal(31, a.Posting.Count);
        }

        [Fact]
        public void Attention_ExplainsWhatNeedsDoing()
        {
            using var h = new Harness(Now);
            var none = h.Store.Read(s => OmniTumblrViews.Attention(s, Now, true, true, true));
            Assert.Contains(none, a => a.Action == "settings" && a.Level == "error");

            string conn = h.Connect();
            h.Store.Mutate(s => { s.Connection(conn)!.Health = ConnectionHealth.NeedsReauth; s.Connection(conn)!.HealthDetail = "revoked"; });
            string blogId = h.AddBlog(conn, b => { b.Autopilot = true; b.Health.ContentWarning = "out of reels"; b.Health.ConsecutiveFailures = 3; b.Health.LastError = "boom"; });
            var items = h.Store.Read(s => OmniTumblrViews.Attention(s, Now, publishingEnabled: false, aiAvailable: false, memeScraperReady: false));

            Assert.Contains(items, a => a.Action == "reconnect" && a.ConnectionId == conn);
            Assert.Contains(items, a => a.Title.Contains("switched off"));
            Assert.Contains(items, a => a.Title.Contains("AI captions"));
            Assert.Contains(items, a => a.Title.Contains("MemeScraper"));
            Assert.Contains(items, a => a.BlogId == blogId && a.Title.Contains("failing"));
            Assert.Contains(items, a => a.BlogId == blogId && a.Title.Contains("out of content"));

            var summary = h.Store.Read(s => OmniTumblrViews.BlogSummary(s, s.Blog(blogId)!, Now));
            Assert.Equal("error", summary.State);
            Assert.StartsWith("Reconnect", summary.StateReason);
        }

        [Fact]
        public void FleetKpis_SumBlogsAndCompareWeeks()
        {
            using var h = new Harness(Now);
            string conn = h.Connect();
            string a = h.AddBlog(conn, b => b.Stats.Followers = 100, "a");
            string b = h.AddBlog(conn, x => x.Stats.Followers = 50, "b");
            h.AddReadyPost(a, p => { p.Status = PostStatus.Published; p.PublishedUtc = Now.AddDays(-1); p.Metrics.Notes = 10; });
            h.AddReadyPost(b, p => { p.Status = PostStatus.Published; p.PublishedUtc = Now.AddDays(-9); p.Metrics.Notes = 30; });
            h.AddReadyPost(b, p => { p.Status = PostStatus.Failed; p.UpdatedUtc = Now.AddDays(-2); });
            h.AddReadyPost(a, p => p.ScheduledUtc = Now.AddHours(3));

            var k = h.Store.Read(s => OmniTumblrViews.FleetKpis(s, Now));
            Assert.Equal(150, k.Followers);
            Assert.Equal(1, k.Published7d);
            Assert.Equal(1, k.PublishedPrev7d);
            Assert.Equal(20, k.AvgNotes30d);
            Assert.Equal(1, k.Failed7d);
            Assert.Equal(66.7, k.SuccessRate30d);
            Assert.Equal(1, k.Pending);
            Assert.Equal(Now.AddHours(3), k.NextPostUtc);
            Assert.Equal("a", k.NextPostBlog);
        }
    }
}
