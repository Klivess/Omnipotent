using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    public class OmniGramPublisherTests
    {
        [Fact]
        public async Task A_due_reel_goes_through_container_processing_and_publish()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p => { p.Hashtags = new() { "memes", "dev" }; p.Footer = "Follow for more"; p.Reel.CoverFrameSeconds = 1.2; });

            int published = await h.PublishUntilSettledAsync();

            Assert.Equal(1, published);
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.NotNull(post.IgMediaId);
            Assert.StartsWith("https://www.instagram.com/p/", post.Permalink);
            Assert.Equal("REELS", post.MediaProductType);
            Assert.Equal(PublishStage.Published, post.Publish.Stage);

            var (containerId, request) = h.Api.Containers.Single();
            Assert.Equal("REELS", request.MediaType);
            Assert.Equal("a caption\n\nFollow for more\n\n#memes #dev", request.Caption);
            Assert.Equal(1200, request.ThumbOffsetMs);
            Assert.True(request.ShareToFeed);
            Assert.StartsWith("https://klive.test/omnigram/public/media?t=", request.VideoUrl);
            // The signed link resolves to exactly this post's prepared copy.
            string token = System.Web.HttpUtility.ParseQueryString(new Uri(request.VideoUrl!).Query)["t"]!;
            Assert.True(h.Host.TryValidate(token, out string linkPost, out int index, out _));
            Assert.Equal((postId, 0), (linkPost, index));
            Assert.Single(h.Api.PublishCalls, containerId);

            var account = h.Account(acct);
            Assert.Equal(h.Clock.Now, account.Stats.LastPublishedUtc);
            Assert.Equal(0, account.Health.ConsecutiveFailures);
            Assert.Contains(postId, h.PublishedPosts);
            Assert.Contains(h.Store.Read(s => s.Events), e => e.Kind == "publish.ok" && e.PostId == postId);
        }

        [Fact]
        public async Task A_container_still_processing_is_checked_again_later_without_republishing()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Api.OnCreate = _ => "c-slow";
            h.Api.StatusScript["c-slow"] = new Queue<IgContainerStatus>(new[]
            {
                new IgContainerStatus { StatusCode = "IN_PROGRESS" },
                new IgContainerStatus { StatusCode = "IN_PROGRESS" },
                new IgContainerStatus { StatusCode = "FINISHED" },
            });

            await h.Publisher.PublishDueAsync(CancellationToken.None); // creates the container
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await h.Publisher.PublishDueAsync(CancellationToken.None)); // first check: still processing
            var waiting = h.Post(postId);
            Assert.Equal(PostStatus.Publishing, waiting.Status);
            Assert.Equal("Instagram is processing the media…", waiting.BlockedReason);
            Assert.NotNull(waiting.Publish.NextCheckUtc);

            await h.PublishUntilSettledAsync();
            Assert.Equal(PostStatus.Published, h.Post(postId).Status);
            Assert.Single(h.Api.Containers);
            Assert.Single(h.Api.PublishCalls);
        }

        [Fact]
        public async Task A_publish_whose_answer_is_lost_is_adopted_not_sent_twice()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Api.AfterPublish = _ => throw new InstagramApiException(IgErrorKind.Timeout, 0, null, null, "media/publish timed out.") { Ambiguous = true };

            await h.Publisher.PublishDueAsync(CancellationToken.None); // creates the container
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            await h.Publisher.PublishDueAsync(CancellationToken.None); // FINISHED → publish → the answer is lost
            var uncertain = h.Post(postId);
            Assert.Equal(PostStatus.Publishing, uncertain.Status);
            Assert.Equal(PublishStage.PublishRequested, uncertain.Publish.Stage);

            h.Api.AfterPublish = null;
            await h.PublishUntilSettledAsync();

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.Single(h.Api.PublishCalls); // the container read PUBLISHED; nothing was published again
            Assert.Equal(h.Api.Media.Single().Id, post.IgMediaId); // found by time and caption
            Assert.Contains(post.AttemptLog, a => a.Code == "reconciled");
        }

        [Fact]
        public async Task After_a_restart_uploads_with_a_container_are_checked_and_the_rest_go_back_to_ready()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string withContainer = h.AddReadyPost(acct, p => { p.Status = PostStatus.Publishing; p.Attempts = 1; p.Publish = new PublishProgress { Stage = PublishStage.Processing, ContainerId = "c-old", ContainerCreatedUtc = h.Clock.Now.AddMinutes(-2) }; });
            string without = h.AddReadyPost(acct, p => { p.Status = PostStatus.Publishing; p.Attempts = 1; });
            h.Api.Containers.Add(("c-old", new IgContainerRequest { MediaType = "REELS", Caption = "a caption" }));

            Assert.Equal(2, h.Publisher.RecoverInterrupted());
            Assert.Equal(PostStatus.Publishing, h.Post(withContainer).Status);
            Assert.Equal(PostStatus.Ready, h.Post(without).Status);
            Assert.Equal(0, h.Post(without).Attempts);

            await h.PublishUntilSettledAsync();
            Assert.Equal(PostStatus.Published, h.Post(withContainer).Status);
            Assert.Equal(PostStatus.Published, h.Post(without).Status);
            Assert.Contains("c-old", h.Api.PublishCalls); // the existing container was used, not recreated
        }

        [Fact]
        public async Task An_expired_token_parks_the_account_and_keeps_the_post_waiting()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Api.OnCreate = _ => throw new InstagramApiException(IgErrorKind.TokenInvalid, 400, 190, 463, "Error validating access token: Session has expired");

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(0, post.Attempts);
            Assert.Contains("Reconnect", post.BlockedReason);
            Assert.Equal(ConnectionState.NeedsReauth, h.Account(acct).Connection);
            Assert.Single(h.Alerts);

            // Gated from now on: no further calls until reconnected.
            h.Api.OnCreate = null;
            h.Clock.Advance(TimeSpan.FromHours(1));
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal(1, h.Api.Count("media/create"));
            Assert.Contains("Reconnect @memepage", h.Post(postId).BlockedReason);
        }

        [Fact]
        public async Task The_daily_publishing_limit_defers_until_the_oldest_post_leaves_the_window()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string earlier = h.AddReadyPost(acct, p => { p.Status = PostStatus.Published; p.PublishedUtc = h.Clock.Now.AddHours(-20); });
            string postId = h.AddReadyPost(acct);
            h.Api.OnCreate = _ => throw new InstagramApiException(IgErrorKind.DailyPublishLimit, 400, 9, 2207042, "Maximum number of posts per 24 hours reached");

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(0, post.Attempts);
            Assert.Equal(h.Clock.Now.AddHours(4).AddMinutes(2), post.NextAttemptUtc);
        }

        [Fact]
        public async Task A_spam_flag_cools_the_account_down_for_a_day()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Api.OnCreate = _ => throw new InstagramApiException(IgErrorKind.SpamRestricted, 400, 4, 2207051, "We restrict certain activity");

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(h.Clock.Now.AddHours(24), h.Account(acct).Health.CooldownUntilUtc);
            Assert.Equal(h.Clock.Now.AddHours(24), h.Post(postId).NextAttemptUtc);
            Assert.Single(h.Alerts);
            string other = h.AddReadyPost(acct);
            h.Api.OnCreate = null;
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Contains("Cooling down", h.Post(other).BlockedReason);
        }

        [Fact]
        public async Task Media_that_Instagram_cannot_download_is_retried_then_reported()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            int created = 0;
            h.Api.OnCreate = _ => "c-fetch-" + (++created);
            for (int i = 1; i <= 5; i++)
                h.Api.StatusScript["c-fetch-" + i] = new Queue<IgContainerStatus>(new[] { new IgContainerStatus { StatusCode = "ERROR", Status = "Error: Media download has failed (2207052)" } });

            for (int i = 0; i < 6; i++)
            {
                await h.PublishUntilSettledAsync();
                var p = h.Post(postId);
                if (p.Status == PostStatus.Failed) break;
                h.Clock.Now = p.NextAttemptUtc ?? h.Clock.Now;
            }

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Failed, post.Status);
            Assert.Equal(OmniGramPublisher.MaxFetchFailures, created);
            Assert.Contains(h.Alerts, a => a.Contains("cannot download media from https://klive.test"));
            Assert.Empty(h.Api.PublishCalls);
        }

        [Fact]
        public async Task A_refused_repackaged_file_is_fully_converted_once_then_rejected_for_good()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p => { p.Origin = PostOrigin.Autopilot; p.SlotUtc = h.Clock.Now; p.Content = new ContentRef { Kind = ContentSourceKind.MemeScraper, Key = "ig:bad" }; });
            h.Api.OnCreate = _ => "c-bad";
            h.Api.StatusScript["c-bad"] = new Queue<IgContainerStatus>(new[] { new IgContainerStatus { StatusCode = "ERROR", Status = "Error: unsupported video format (2207026)" } });

            await h.PublishUntilSettledAsync();
            var reconvert = h.Post(postId);
            Assert.Equal(PostStatus.Planned, reconvert.Status);
            Assert.True(reconvert.ForceTranscode);
            Assert.Null(reconvert.Media[0].PreparedPath);
            Assert.Empty(h.RefilledFor);

            // Pretend the planner re-encoded it; Instagram refuses again → failed for good.
            h.Store.Mutate(s =>
            {
                var p = s.FindPost(postId)!;
                string prepared = Path.Combine(h.Store.PreparedDirectory, postId + "-0.mp4");
                File.WriteAllBytes(prepared, new byte[100]);
                p.Media[0].PreparedPath = prepared;
                p.Media[0].PreparedFor = PostFormat.Reel;
                p.Media[0].PrepareNotes = "transcoded to H.264/AAC (Instagram refused the repackaged copy)";
                p.Status = PostStatus.Ready;
            });
            await h.PublishUntilSettledAsync();

            var failed = h.Post(postId);
            Assert.Equal(PostStatus.Failed, failed.Status);
            Assert.Contains("ig:bad", h.Account(acct).RejectedContentKeys);
            Assert.Single(h.RefilledFor, postId);
        }

        [Fact]
        public async Task A_refused_cover_frame_is_dropped_and_the_reel_retried()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p => p.Reel.CoverFrameSeconds = 99);
            int calls = 0;
            h.Api.OnCreate = r =>
            {
                calls++;
                if (r.ThumbOffsetMs != null) throw new InstagramApiException(IgErrorKind.InvalidCoverFrame, 400, 1, 2207057, "Thumbnail offset invalid");
                return "c-ok";
            };

            await h.Publisher.PublishDueAsync(CancellationToken.None);
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            await h.PublishUntilSettledAsync();

            Assert.Equal(PostStatus.Published, h.Post(postId).Status);
            Assert.Equal(2, calls);
            Assert.True(h.Post(postId).Publish.DroppedCoverFrame);
        }

        [Fact]
        public async Task Transient_errors_retry_with_backoff_and_give_up_after_five_attempts()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Api.OnCreate = _ => throw new InstagramApiException(IgErrorKind.ServerError, 500, 2, null, "Service temporarily unavailable");

            await h.Publisher.PublishDueAsync(CancellationToken.None);
            var first = h.Post(postId);
            Assert.Equal(PostStatus.Ready, first.Status);
            Assert.Equal(h.Clock.Now.AddMinutes(2), first.NextAttemptUtc);

            for (int i = 0; i < 6; i++)
            {
                var p = h.Post(postId);
                if (p.Status == PostStatus.Failed) break;
                h.Clock.Now = p.NextAttemptUtc ?? h.Clock.Now;
                await h.Publisher.PublishDueAsync(CancellationToken.None);
            }
            var post = h.Post(postId);
            Assert.Equal(PostStatus.Failed, post.Status);
            Assert.Equal(OmniGramPublisher.MaxAttempts, post.Attempts);
            Assert.Contains(h.Alerts, a => a.Contains("failed after 5 attempts"));
        }

        [Fact]
        public async Task Carousels_wait_for_every_item_before_creating_the_album()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p =>
            {
                string second = h.Dir.File("media/second.mp4");
                string prepared = Path.Combine(h.Store.PreparedDirectory, p.PostId + "-1.mp4");
                File.Copy(second, prepared);
                p.Format = PostFormat.Carousel;
                p.Media[0].PreparedFor = PostFormat.Carousel;
                p.Media.Add(new PostMedia { Path = second, MimeType = "video/mp4", PreparedPath = prepared, PreparedMimeType = "video/mp4", PreparedFor = PostFormat.Carousel });
            }, PostFormat.Photo);
            int n = 0;
            h.Api.OnCreate = r => r.IsCarouselItem == true ? "item" + (++n) : "album";
            h.Api.StatusScript["item1"] = new Queue<IgContainerStatus>(new[] { new IgContainerStatus { StatusCode = "IN_PROGRESS" }, new IgContainerStatus { StatusCode = "FINISHED" } });

            await h.PublishUntilSettledAsync();

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            var album = h.Api.Containers.Single(c => c.Id == "album").Request;
            Assert.Equal("CAROUSEL", album.MediaType);
            Assert.Equal(new[] { "item1", "item2" }, album.Children);
            Assert.Null(h.Api.Containers.First(c => c.Id == "item1").Request.MediaType); // an image item
            Assert.Equal("VIDEO", h.Api.Containers.First(c => c.Id == "item2").Request.MediaType); // a video item
            Assert.Single(h.Api.PublishCalls, "album");
        }

        [Fact]
        public async Task Hashtags_in_first_comment_placement_are_commented_after_publishing()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p => { p.HashtagPlacement = HashtagPlacement.FirstComment; p.Hashtags = new() { "memes", "lol" }; });

            await h.PublishUntilSettledAsync();

            Assert.Equal("a caption", h.Api.Containers.Single().Request.Caption);
            var comment = Assert.Single(h.Api.Comments);
            Assert.Equal("#memes #lol", comment.Message);
            var post = h.Post(postId);
            Assert.False(post.FirstCommentPending);
            Assert.NotNull(post.FirstCommentId);
        }

        [Fact]
        public async Task A_failed_first_comment_is_retried_but_never_blocks_the_post()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct, p => { p.FirstComment = "credit: @someone"; });
            int tries = 0;
            h.Api.OnCreateComment = _ => ++tries < 2 ? throw new InstagramApiException(IgErrorKind.ServerError, 500, 2, null, "temporary") : "cm-ok";

            await h.PublishUntilSettledAsync();
            Assert.Equal(PostStatus.Published, h.Post(postId).Status);
            Assert.True(h.Post(postId).FirstCommentPending);

            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Equal("cm-ok", h.Post(postId).FirstCommentId);
            Assert.False(h.Post(postId).FirstCommentPending);
        }

        [Fact]
        public async Task Autopilot_respects_the_daily_cap_and_spacing_but_manual_overrides_do_not()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount(configure: a => { a.Strategy.MaxPostsPerDay = 1; a.Strategy.TimeZone = "UTC"; a.Strategy.MinGapMinutes = 0; });
            h.AddReadyPost(acct, p => { p.Status = PostStatus.Published; p.PublishedUtc = h.Clock.Now.AddHours(-1); });
            string auto = h.AddReadyPost(acct, p => { p.Origin = PostOrigin.Autopilot; p.SlotUtc = h.Clock.Now; });
            string manual = h.AddReadyPost(acct, p => p.ManualOverride = true);

            await h.PublishUntilSettledAsync();

            Assert.Equal(PostStatus.Ready, h.Post(auto).Status);
            Assert.Contains("Daily cap", h.Post(auto).BlockedReason);
            Assert.Equal(new DateTime(2026, 10, 8, 0, 5, 0, DateTimeKind.Utc), h.Post(auto).NextAttemptUtc);
            Assert.Equal(PostStatus.Published, h.Post(manual).Status);
        }

        [Fact]
        public async Task An_autopilot_post_that_missed_its_slot_badly_is_skipped_and_its_content_released()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount(configure: a => a.Strategy.MissedSlotGraceHours = 2);
            string postId = h.AddReadyPost(acct, p =>
            {
                p.Origin = PostOrigin.Autopilot;
                p.SlotUtc = p.ScheduledUtc = h.Clock.Now.AddHours(-5);
                p.Content = new ContentRef { Kind = ContentSourceKind.MemeScraper, Key = "ig:late" };
            });
            h.Store.Mutate(s => s.Account(acct)!.UsedContentKeys.Add("ig:late"));

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(PostStatus.Skipped, h.Post(postId).Status);
            Assert.DoesNotContain("ig:late", h.Account(acct).UsedContentKeys);
            Assert.Empty(h.Api.Containers);
        }

        [Fact]
        public async Task Gates_explain_why_a_post_waits()
        {
            using var h = new Harness();
            string paused = h.ConnectAccount("paused", "1", a => a.Paused = true);
            string approval = h.ConnectAccount("approval", "2", a => a.RequireApproval = true);
            string unconnected = h.ConnectAccount("unconnected", "3", a => { a.TokenCipher = null; a.IgUserId = null; });
            string quota = h.ConnectAccount("quota", "4", a => { a.Quota = new PublishingQuota { QuotaTotal = 2, QuotaUsage = 2, FetchedUtc = h.Clock.Now }; });

            string p1 = h.AddReadyPost(paused);
            string p2 = h.AddReadyPost(approval, p => { p.Origin = PostOrigin.Autopilot; p.Approved = false; p.SlotUtc = h.Clock.Now; });
            string p3 = h.AddReadyPost(unconnected);
            string p4 = h.AddReadyPost(quota);

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal("The account is paused.", h.Post(p1).BlockedReason);
            Assert.Equal("Waiting for approval.", h.Post(p2).BlockedReason);
            Assert.Contains("Connect @unconnected", h.Post(p3).BlockedReason);
            Assert.Contains("limit of 2 API posts per 24 hours", h.Post(p4).BlockedReason);
            Assert.Empty(h.Api.Containers);

            h.Publisher.PublishingEnabled = () => false;
            string p5 = h.AddReadyPost(h.ConnectAccount("fine", "5"));
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            Assert.Contains("switched off", h.Post(p5).BlockedReason);
        }

        [Fact]
        public async Task Stories_and_photos_get_the_right_container_shape()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string story = h.AddReadyPost(acct, p => p.Caption = "", PostFormat.Story);
            string photo = h.AddReadyPost(acct, p => p.AltText = "A dog in a hat.", PostFormat.Photo);

            await h.PublishUntilSettledAsync();

            var storyRequest = h.Api.Containers.Single(c => c.Request.MediaType == "STORIES").Request;
            Assert.NotNull(storyRequest.VideoUrl);
            Assert.Null(storyRequest.Caption);
            var photoRequest = h.Api.Containers.Single(c => c.Request.MediaType == null).Request;
            Assert.NotNull(photoRequest.ImageUrl);
            Assert.Equal("A dog in a hat.", photoRequest.AltText);
            Assert.Equal(PostStatus.Published, h.Post(story).Status);
            Assert.Equal("STORY", h.Post(story).MediaProductType);
            Assert.Equal(PostStatus.Published, h.Post(photo).Status);
        }

        [Fact]
        public async Task A_missing_prepared_copy_sends_the_post_back_to_preparation()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            File.Delete(h.Post(postId).Media[0].PreparedPath!);

            await h.Publisher.PublishDueAsync(CancellationToken.None);

            Assert.Equal(PostStatus.Planned, h.Post(postId).Status);
            Assert.Empty(h.Api.Containers);
        }
    }
}
