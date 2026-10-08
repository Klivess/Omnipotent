using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    public class OmniTumblrPublisherTests
    {
        private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static (Harness H, string Conn, string Blog) Setup(Action<OmniTumblrBlog>? configure = null)
        {
            var h = new Harness(Start);
            string conn = h.Connect();
            string blog = h.AddBlog(conn, configure);
            return (h, conn, blog);
        }

        [Fact]
        public async Task ADuePost_IsPublishedOnce_AndEverythingAroundItIsUpdated()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            string postId = h.AddReadyPost(blogId);

            int published = await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(1, published);
            Assert.Equal(1, h.Api.CreateCalls);
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.NotNull(post.TumblrPostId);
            Assert.Equal($"https://www.tumblr.com/memeblog/{post.TumblrPostId}", post.TumblrUrl);
            Assert.Equal(Start, post.PublishedUtc);
            Assert.StartsWith("a-caption-", post.Slug);
            Assert.True(post.AttemptLog.Single().Ok);
            var (blog, request) = h.Api.Created.Single();
            Assert.Equal("t:memeblog", blog); // published by uuid, so a renamed blog still works
            Assert.Equal(post.Slug, request.Slug);
            Assert.Equal(Start, h.Blog(blogId).Stats.LastPublishedUtc);
            Assert.Contains(h.Store.Read(s => s.InsightsOf(blogId).Index.ToList()), p => p.OmniPostId == postId);
            Assert.Contains(h.Store.Read(s => s.Events.ToList()), e => e.Kind == "publish.ok" && e.PostId == postId);

            // Nothing left to do.
            Assert.Equal(0, await h.Publisher.PublishDueAsync(CancellationToken.None));
            Assert.Equal(1, h.Api.CreateCalls);
        }

        [Fact]
        public async Task PostsAreNotPublishedBeforeTheirTime()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            string postId = h.AddReadyPost(blogId, p => p.ScheduledUtc = Start.AddMinutes(30));
            Assert.Equal(0, await h.Publisher.PublishDueAsync(CancellationToken.None));
            Assert.Equal(PostStatus.Ready, h.Post(postId).Status);
            Assert.Equal(Start.AddMinutes(30), h.Publisher.NextDueUtc());
        }

        [Fact]
        public async Task ConcurrentPasses_NeverUploadThePostTwice()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            var gate = new TaskCompletionSource();
            h.Api.OnCreate = async (blog, request) =>
            {
                await gate.Task;
                return new TumblrCreatedPost { Id = "999" };
            };
            string postId = h.AddReadyPost(blogId);

            var first = h.Publisher.PublishDueAsync(CancellationToken.None);
            var second = h.Publisher.PublishDueAsync(CancellationToken.None);
            var third = h.Publisher.PublishDueAsync(CancellationToken.None);
            await Task.Delay(50);
            gate.SetResult();
            await Task.WhenAll(first, second, third);

            Assert.Equal(1, h.Api.CreateCalls);
            Assert.Equal(PostStatus.Published, h.Post(postId).Status);
        }

        [Fact]
        public async Task APausedBlog_HoldsItsPosts_WithAReason()
        {
            var (h, _, blogId) = Setup(b => b.Paused = true);
            using var _h = h;
            string postId = h.AddReadyPost(blogId);
            Assert.Equal(0, await h.Publisher.PublishDueAsync(CancellationToken.None));
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal("The blog is paused.", post.BlockedReason);
            Assert.Equal(0, h.Api.CreateCalls);
        }

        [Fact]
        public async Task ARevokedToken_ParksTheConnection_WithoutSpendingAnAttempt()
        {
            var (h, conn, blogId) = Setup();
            using var _h = h;
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.Unauthorized, 401, null, "token revoked");
            string postId = h.AddReadyPost(blogId);

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(0, post.Attempts);
            Assert.Equal(ConnectionHealth.NeedsReauth, h.Connection(conn).Health);
            Assert.Single(h.Alerts);

            // While the connection needs reauth, nothing is attempted.
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal(1, h.Api.CreateCalls);
            Assert.StartsWith("Reconnect @klives", h.Post(postId).BlockedReason);
        }

        [Fact]
        public async Task TheDailyVideoLimit_DefersToTheReset_AndIsNotAFailure()
        {
            var (h, conn, blogId) = Setup();
            using var _h = h;
            var reset = Start.AddHours(6);
            h.Store.Mutate(s => s.Connection(conn)!.Limits["videos"] = new TumblrLimit { Limit = 20, Remaining = 3, ResetUtc = reset });
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.DailyVideoLimit, 403, 8011, "video limit");
            string postId = h.AddReadyPost(blogId);

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(0, post.Attempts);
            Assert.Equal(1, post.Deferrals);
            Assert.Equal(reset.AddMinutes(2), post.NextAttemptUtc);
            Assert.Equal(0, h.Connection(conn).Limits["videos"].Remaining);
            Assert.Equal(0, h.Blog(blogId).Health.ConsecutiveFailures);

            // Another video post is held back by the known limit without calling Tumblr.
            string second = h.AddReadyPost(blogId);
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal(1, h.Api.CreateCalls);
            Assert.Contains("daily videos limit", h.Post(second).BlockedReason);
        }

        [Fact]
        public async Task AStillTranscodingVideo_RetriesInFiveMinutes()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.VideoTranscoding, 403, 8010, "transcoding");
            string postId = h.AddReadyPost(blogId);
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            var post = h.Post(postId);
            Assert.Equal(Start.AddMinutes(5), post.NextAttemptUtc);
            Assert.Equal(0, post.Attempts);
        }

        [Fact]
        public async Task RejectedMedia_FailsThePost_BlacklistsTheContent_AndAsksForARefill()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.InvalidMedia, 400, 8005, "bad media");
            string postId = h.AddReadyPost(blogId, p =>
            {
                p.Origin = PostOrigin.Autopilot;
                p.SlotUtc = p.ScheduledUtc;
                p.Content = new ContentRef { Kind = ContentSourceKind.MemeScraper, Key = "ig:broken" };
            });

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(PostStatus.Failed, h.Post(postId).Status);
            Assert.Contains("ig:broken", h.Blog(blogId).RejectedContentKeys);
            Assert.Equal(new[] { postId }, h.RefilledFor);
        }

        [Fact]
        public async Task ATimeoutThatActuallyPosted_IsReconciledBySlug_NotPostedTwice()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            h.Api.OnCreate = (blog, request) =>
            {
                // Tumblr created the post, but the response never arrived.
                lock (h.Api.PublishedPosts)
                    h.Api.PublishedPosts.Add(new TumblrPostSummary { Id = "777", Slug = request.Slug, PublishedUtc = Start, PostUrl = "https://memeblog.tumblr.com/post/777" });
                throw new TumblrApiException(TumblrErrorKind.Timeout, 0, null, "timed out") { Ambiguous = true };
            };
            string postId = h.AddReadyPost(blogId);

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.Equal("777", post.TumblrPostId);
            Assert.Equal("reconciled", post.AttemptLog.Last().Code);
            Assert.Equal(1, h.Api.CreateCalls);
        }

        [Fact]
        public async Task ServerErrors_BackOff_AndCheckTumblrBeforeEachRetry_ThenGiveUp()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.ServerError, 500, null, "boom");
            string postId = h.AddReadyPost(blogId);

            await h.Publisher.PublishDueAsync(CancellationToken.None);
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(1, post.Attempts);
            Assert.True(post.NeedsReconcile);
            Assert.Equal(Start.AddMinutes(2), post.NextAttemptUtc);

            for (int i = 0; i < 10 && h.Post(postId).Status != PostStatus.Failed; i++)
            {
                h.Clock.Advance(TimeSpan.FromHours(1));
                await h.Publisher.PublishDueAsync(CancellationToken.None);
            }
            post = h.Post(postId);
            Assert.Equal(PostStatus.Failed, post.Status);
            Assert.Equal(OmniTumblrPublisher.MaxAttempts, post.Attempts);
            Assert.Equal(OmniTumblrPublisher.MaxAttempts, h.Api.CreateCalls);
            Assert.Contains(h.Api.Calls, c => c.StartsWith("blog/posts:t:memeblog:0")); // looked before retrying
            Assert.Single(h.Alerts);
        }

        [Fact]
        public async Task AnAutopilotPostThatMissedItsSlotByTooMuch_IsSkipped()
        {
            var (h, _, blogId) = Setup(b => b.Strategy.MissedSlotGraceHours = 6);
            using var _h = h;
            string postId = h.AddReadyPost(blogId, p =>
            {
                p.Origin = PostOrigin.Autopilot;
                p.ScheduledUtc = Start.AddHours(-7);
                p.SlotUtc = p.ScheduledUtc;
                p.Content = new ContentRef { Key = "ig:x" };
            });
            h.Store.Mutate(s => s.Blog(blogId)!.UsedContentKeys.Add("ig:x"));

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(PostStatus.Skipped, h.Post(postId).Status);
            Assert.DoesNotContain("ig:x", h.Blog(blogId).UsedContentKeys); // the content was never posted, so it can be used later
            Assert.Equal(0, h.Api.CreateCalls);
        }

        [Fact]
        public async Task AutopilotCapsAndSpacing_Apply_ButManualOverridesBypassThem()
        {
            var (h, _, blogId) = Setup(b =>
            {
                b.Strategy.MaxPostsPerDay = 1;
                b.Strategy.MinGapMinutes = 0;
                b.Strategy.TimeZone = "UTC";
            });
            using var _h = h;
            void Autopilot(OmniTumblrPost p) { p.Origin = PostOrigin.Autopilot; p.SlotUtc = p.ScheduledUtc; }
            string first = h.AddReadyPost(blogId, Autopilot);
            string second = h.AddReadyPost(blogId, Autopilot);

            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal(PostStatus.Published, h.Post(first).Status);
            var held = h.Post(second);
            Assert.Equal(PostStatus.Ready, held.Status);
            Assert.Equal(new DateTime(2026, 10, 8, 0, 5, 0, DateTimeKind.Utc), held.NextAttemptUtc);
            Assert.Contains("Daily cap", held.BlockedReason);

            h.Store.Mutate(s =>
            {
                var p = s.FindPost(second)!;
                p.ManualOverride = true;
                p.NextAttemptUtc = null;
            });
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal(PostStatus.Published, h.Post(second).Status);
        }

        [Fact]
        public async Task MinimumSpacing_DelaysTheNextAutopilotPost()
        {
            var (h, _, blogId) = Setup(b => { b.Strategy.MinGapMinutes = 45; b.Stats.LastPublishedUtc = Start.AddMinutes(-15); });
            using var _h = h;
            string postId = h.AddReadyPost(blogId, p => { p.Origin = PostOrigin.Autopilot; p.SlotUtc = p.ScheduledUtc; });
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            var post = h.Post(postId);
            Assert.Equal(Start.AddMinutes(30), post.NextAttemptUtc);
            Assert.Equal(0, h.Api.CreateCalls);
        }

        [Fact]
        public async Task AnUploadInterruptedByARestart_IsCheckedOnTumblrFirst()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            string postId = h.AddReadyPost(blogId, p => { p.Status = PostStatus.Publishing; p.Slug = "a-caption-deadbeef"; p.Attempts = 1; });
            lock (h.Api.PublishedPosts)
                h.Api.PublishedPosts.Add(new TumblrPostSummary { Id = "555", Slug = "a-caption-deadbeef", PublishedUtc = Start.AddMinutes(-2) });

            Assert.Equal(1, h.Publisher.RecoverInterrupted());
            Assert.True(h.Post(postId).NeedsReconcile);
            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.Equal("555", post.TumblrPostId);
            Assert.Equal(0, h.Api.CreateCalls);
        }

        [Fact]
        public async Task AMissingMediaFile_FailsThePostClearly()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            string postId = h.AddReadyPost(blogId, p => p.Media[0].Path = Path.Combine(h.Dir.Path, "gone.mp4"));
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Failed, post.Status);
            Assert.Equal("media.missing", post.LastErrorCode);
            Assert.Equal(0, h.Api.CreateCalls);
        }

        [Fact]
        public async Task TheKillSwitch_HoldsEverything()
        {
            var (h, _, blogId) = Setup();
            using var _h = h;
            h.Publisher.PublishingEnabled = () => false;
            string postId = h.AddReadyPost(blogId);
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Contains("switched off", h.Post(postId).BlockedReason);
            Assert.Equal(0, h.Api.CreateCalls);
        }
    }
}
