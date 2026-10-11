using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Specialized;
using System.Security.Cryptography;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    public class OmniGramPlannerTests
    {
        private static string Autopilot(Harness h, Action<OmniGramAccount>? configure = null) => h.ConnectAccount(configure: a =>
        {
            a.Autopilot = true;
            a.Strategy.TimeZone = "UTC";
            a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60), new(DayOfWeek.Monday, 9 * 60) };
            a.Strategy.PlanAheadDays = 7;
            configure?.Invoke(a);
        });

        [Fact]
        public async Task Planning_fills_each_free_slot_with_the_best_unused_reel()
        {
            using var h = new Harness(); // Wed 7 Oct 2026 12:00 UTC
            h.AddReel("weak", views: 100);
            h.AddReel("strong", views: 1_000_000);
            h.AddReel("middling", views: 10_000);
            string acct = Autopilot(h);

            var outcome = await h.Planner.PlanAccountAsync(acct, CancellationToken.None);

            Assert.Equal(2, outcome.Planned);
            var posts = h.Posts(acct).OrderBy(p => p.SlotUtc).ToList();
            Assert.Equal(new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc), posts[0].SlotUtc);
            Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), posts[1].SlotUtc);
            Assert.Equal("ig:strong", posts[0].Content!.Key);
            Assert.Equal("ig:middling", posts[1].Content!.Key);
            Assert.All(posts, p => Assert.Equal(PostFormat.Reel, p.Format));
            Assert.All(posts, p => Assert.True(p.CaptionPending));
            Assert.Contains("ig:strong", h.Account(acct).UsedContentKeys);

            // A second pass finds nothing free: no duplicates.
            Assert.Equal(0, (await h.Planner.PlanAccountAsync(acct, CancellationToken.None)).Planned);
        }

        [Fact]
        public async Task Content_used_by_another_account_is_not_reused_unless_allowed()
        {
            using var h = new Harness();
            h.AddReel("only");
            string a = Autopilot(h);
            string b = h.ConnectAccount("second", "2", acct => { acct.Autopilot = true; acct.Strategy.TimeZone = "UTC"; acct.Strategy.Slots = new() { new(DayOfWeek.Friday, 20 * 60) }; });

            Assert.Equal(1, (await h.Planner.PlanAccountAsync(a, CancellationToken.None)).Planned);
            var shortage = await h.Planner.PlanAccountAsync(b, CancellationToken.None);
            Assert.Equal(0, shortage.Planned);
            Assert.Contains("Ran out of unused MemeScraper reels", shortage.Warning);
            Assert.Equal(shortage.Warning, h.Account(b).Health.ContentWarning);

            h.Store.Mutate(s => s.Account(b)!.Strategy.MemeScraper.AllowReuseAcrossAccounts = true);
            Assert.Equal(1, (await h.Planner.PlanAccountAsync(b, CancellationToken.None)).Planned);
        }

        [Fact]
        public async Task Filters_on_age_views_and_niche_are_applied()
        {
            using var h = new Harness();
            h.AddReel("old", created: h.Clock.Now.AddDays(-400));
            h.AddReel("unpopular", views: 5);
            h.AddReel("other-niche", owner: "cats");
            h.AddReel("good", owner: "dev");
            h.Catalog.Niches["dev"] = new[] { "programming" };
            h.Catalog.Niches["cats"] = new[] { "animals" };
            string acct = Autopilot(h, a =>
            {
                a.Strategy.MemeScraper.MaxAgeDays = 30;
                a.Strategy.MemeScraper.MinViews = 100;
                a.Strategy.MemeScraper.Niches = new() { "programming" };
                a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) };
            });

            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            Assert.Equal("ig:good", h.Posts(acct).Single().Content!.Key);
        }

        [Fact]
        public async Task Preparing_without_ffmpeg_copies_the_media_writes_the_caption_and_readies_the_post()
        {
            using var h = new Harness();
            h.AddReel("r1");
            string acct = Autopilot(h, a => { a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) }; a.Strategy.FixedHashtags = new() { "memes" }; a.Strategy.CreditTemplate = "🎥 @{origin}"; a.Strategy.CaptionFooter = "More daily"; });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);

            Assert.Equal(1, await h.Planner.PrepareDueAsync(4, CancellationToken.None));

            var post = h.Posts(acct).Single();
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal("when the build finally passes", post.Caption);
            Assert.Equal(new[] { "memes", "programming", "relatable" }, post.Hashtags);
            Assert.Equal("🎥 @memesource", post.Credit);
            Assert.Equal("More daily", post.Footer);
            var media = post.Media.Single();
            Assert.True(File.Exists(media.PreparedPath));
            Assert.StartsWith(Path.GetFullPath(h.Store.PreparedDirectory), Path.GetFullPath(media.PreparedPath!));
            Assert.Equal(PostFormat.Reel, media.PreparedFor);
            Assert.StartsWith("copied unchecked", media.PrepareNotes);
            Assert.Contains("never mention them", h.Model.Calls.Single().User);
        }

        [Fact]
        public async Task Rewriting_a_caption_replaces_the_ai_hashtags_and_never_trims_hand_picked_ones()
        {
            using var h = new Harness();
            h.AddReel("r1");
            string acct = Autopilot(h, a => { a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) }; a.Strategy.FixedHashtags = new() { "memes" }; a.Strategy.MaxHashtags = 3; });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            await h.Planner.PrepareDueAsync(4, CancellationToken.None);
            string postId = h.Posts(acct).Single().PostId;
            Assert.Equal(new[] { "memes", "programming", "relatable" }, h.Post(postId).Hashtags);

            // A rewrite swaps the previous suggestions for the new ones instead of piling them up.
            h.Model.Replies.Enqueue(() => "{\"caption\": \"take two\", \"hashtags\": [\"devlife\"]}");
            await h.Planner.PreparePostAsync(postId, forceRegenerate: true, extraInstruction: null, CancellationToken.None);
            Assert.Equal("take two", h.Post(postId).Caption);
            Assert.Equal(new[] { "memes", "devlife" }, h.Post(postId).Hashtags);

            // Hashtags typed by hand survive a rewrite even past the strategy's cap.
            h.Store.Mutate(s => { var p = s.FindPost(postId)!; p.Hashtags = new() { "a", "b", "c", "d" }; p.CaptionInfo.SuggestedHashtags = new(); });
            h.Model.Replies.Enqueue(() => "{\"caption\": \"take three\", \"hashtags\": [\"extra\"]}");
            await h.Planner.PreparePostAsync(postId, forceRegenerate: true, extraInstruction: null, CancellationToken.None);
            Assert.Equal(new[] { "a", "b", "c", "d" }, h.Post(postId).Hashtags);
        }

        [Fact]
        public async Task A_png_cannot_be_prepared_without_ffmpeg_and_the_post_waits_with_a_reason()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string png = h.Dir.File("lib/pic.png");
            string postId = h.Store.Mutate(s =>
            {
                var post = new OmniGramPost { AccountId = acct, Format = PostFormat.Photo, Status = PostStatus.Planned, ScheduledUtc = h.Clock.Now.AddHours(1), Caption = "x",
                    CaptionInfo = new CaptionInfo { Mode = CaptionMode.Manual }, Media = new() { new PostMedia { Path = png, MimeType = "image/png" } } };
                s.PostsOf(acct).Add(post);
                return post.PostId;
            });

            await h.Planner.PreparePostAsync(postId, false, null, CancellationToken.None);

            var waiting = h.Post(postId);
            Assert.Equal(PostStatus.Planned, waiting.Status);
            Assert.Contains("FFmpeg is needed", waiting.BlockedReason);
            Assert.NotNull(waiting.Media[0].PrepareError);
        }

        [Fact]
        public async Task Unusable_media_fails_the_post_rejects_the_content_and_refills_the_slot()
        {
            using var h = new Harness();
            var reel = h.AddReel("gone");
            string acct = Autopilot(h, a => a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            File.Delete(reel.FilePath!);

            await h.Planner.PrepareDueAsync(4, CancellationToken.None);

            var post = h.Posts(acct).Single();
            Assert.Equal(PostStatus.Failed, post.Status);
            Assert.Equal("media.unusable", post.LastErrorCode);
            Assert.Contains("ig:gone", h.Account(acct).RejectedContentKeys);
            Assert.Single(h.RefilledFor, post.PostId);
        }

        [Fact]
        public async Task A_failing_ai_caption_backs_off_then_falls_back_near_the_deadline()
        {
            using var h = new Harness();
            h.AddReel("r1");
            string acct = Autopilot(h, a => { a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) }; a.Strategy.Ai.Fallback = CaptionFallback.Fixed; a.Strategy.FixedCaption = "fallback caption"; });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            h.Model.Default = "not json {";

            await h.Planner.PrepareDueAsync(4, CancellationToken.None);
            var first = h.Posts(acct).Single();
            Assert.True(first.CaptionPending);
            Assert.Equal(1, first.CaptionInfo.Failures);
            Assert.Equal(h.Clock.Now.AddMinutes(3), first.CaptionInfo.RetryAfterUtc);
            Assert.Equal(PostStatus.Planned, first.Status);

            // Within 20 minutes of the slot the fallback applies.
            h.Clock.Now = first.ScheduledUtc.AddMinutes(-15);
            await h.Planner.PrepareDueAsync(4, CancellationToken.None);
            var post = h.Posts(acct).Single();
            Assert.False(post.CaptionPending);
            Assert.Equal("fallback caption", post.Caption);
            Assert.Equal(PostStatus.Ready, post.Status);
        }

        [Fact]
        public async Task Story_posts_never_wait_for_a_caption()
        {
            using var h = new Harness();
            h.AddReel("r1");
            string acct = Autopilot(h, a => { a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) }; a.Strategy.VideoFormat = PostFormat.Story; });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            var planned = h.Posts(acct).Single();
            Assert.Equal(PostFormat.Story, planned.Format);
            Assert.False(planned.CaptionPending);

            await h.Planner.PrepareDueAsync(4, CancellationToken.None);
            Assert.Equal(PostStatus.Ready, h.Posts(acct).Single().Status);
            Assert.Empty(h.Model.Calls);
        }

        [Fact]
        public async Task Changing_the_schedule_moves_planned_posts_or_cancels_them()
        {
            using var h = new Harness();
            h.AddReel("a");
            h.AddReel("b");
            string acct = Autopilot(h);
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            Assert.Equal(2, h.Posts(acct).Count);

            h.Store.Mutate(s => s.Account(acct)!.Strategy.Slots = new() { new(DayOfWeek.Saturday, 12 * 60) });
            Assert.Equal(2, h.Planner.ReconcileSlots(acct));

            var posts = h.Posts(acct).OrderBy(p => p.SlotUtc).ToList();
            Assert.Equal(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), posts[0].SlotUtc);
            Assert.Equal(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc), posts[1].SlotUtc); // second free Saturday within 14 days
            Assert.All(posts, p => Assert.Equal(PostStatus.Planned, p.Status));
        }

        [Fact]
        public async Task A_failed_autopilot_post_is_replaced_with_different_content()
        {
            using var h = new Harness();
            h.AddReel("first", views: 5000);
            h.AddReel("second", views: 10);
            string acct = Autopilot(h, a => a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            string failedId = h.Posts(acct).Single().PostId;
            h.Store.Mutate(s => { var p = s.FindPost(failedId)!; p.Status = PostStatus.Failed; });

            string? replacement = await h.Planner.RefillSlotAsync(failedId, CancellationToken.None);

            Assert.NotNull(replacement);
            Assert.Equal("ig:second", h.Post(replacement!).Content!.Key);
            Assert.Equal(h.Post(failedId).SlotUtc, h.Post(replacement).SlotUtc);
        }

        [Fact]
        public async Task Swapping_content_keeps_the_slot_and_rewrites_the_caption()
        {
            using var h = new Harness();
            h.AddReel("first", views: 5000);
            h.AddReel("second", views: 10);
            string acct = Autopilot(h, a => a.Strategy.Slots = new() { new(DayOfWeek.Friday, 18 * 60) });
            await h.Planner.PlanAccountAsync(acct, CancellationToken.None);
            await h.Planner.PrepareDueAsync(4, CancellationToken.None);
            string postId = h.Posts(acct).Single().PostId;

            var (ok, _) = await h.Planner.SwapContentAsync(postId, CancellationToken.None);

            Assert.True(ok);
            var post = h.Post(postId);
            Assert.Equal("ig:second", post.Content!.Key);
            Assert.Equal(PostStatus.Planned, post.Status);
            Assert.True(post.CaptionPending);
            Assert.Contains("ig:first", h.Account(acct).UsedContentKeys); // never comes back
        }

        [Fact]
        public void A_published_reel_with_also_post_story_queues_a_story_companion()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string reelId = h.AddReadyPost(acct, p => { p.Status = PostStatus.Published; p.PublishedUtc = h.Clock.Now; p.Reel.AlsoPostStory = true; });

            string? storyId = h.Planner.CreateCompanionStory(reelId);

            Assert.NotNull(storyId);
            var story = h.Post(storyId!);
            Assert.Equal(PostFormat.Story, story.Format);
            Assert.True(story.ManualOverride);
            Assert.Equal(reelId, story.CompanionPostId);
            Assert.Equal(storyId, h.Post(reelId).CompanionPostId);
            Assert.Null(h.Planner.CreateCompanionStory(reelId)); // only once
        }
    }

    public class OmniGramAuthTests
    {
        [Fact]
        public async Task Connecting_with_a_token_creates_an_encrypted_healthy_account()
        {
            using var h = new Harness();
            var result = await h.Auth.ConnectAsync("IGAAT-pasted-token-xxxxxxxxxxxx", null, null, "token", null, "three-a-week", autopilot: true, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.True(result.IsNew);
            var a = h.Account(result.AccountId!);
            Assert.Equal("memepage", a.Username);
            Assert.Equal("1784", a.IgUserId);
            Assert.Equal(ConnectionState.Healthy, a.Connection);
            Assert.True(a.Autopilot);
            Assert.Equal(3, a.Strategy.Slots.Count);
            Assert.Equal(h.Clock.Now.AddDays(60), a.TokenExpiresUtc);
            Assert.DoesNotContain("pasted-token", a.TokenCipher);
            Assert.Equal(("IGAAT-pasted-token-xxxxxxxxxxxx", "1784"), h.Auth.GetCredentials(a.AccountId));
            Assert.Equal(1200, a.Stats.Followers);
        }

        [Fact]
        public async Task A_token_for_an_imported_v1_account_attaches_to_it()
        {
            using var h = new Harness();
            string imported = h.Store.Mutate(s =>
            {
                var a = new OmniGramAccount { Username = "MemePage", Connection = ConnectionState.AwaitingConnection, Legacy = new LegacyAccountInfo { ImportedUtc = h.Clock.Now }, Notes = "keep me" };
                a.Strategy.FixedCaption = "v1 caption";
                s.Accounts[a.AccountId] = a;
                return a.AccountId;
            });

            var result = await h.Auth.ConnectAsync("IGAAT-token-for-memepage-xxxx", null, null, "token", null, null, false, CancellationToken.None);

            Assert.True(result.AttachedToImported);
            Assert.Equal(imported, result.AccountId);
            var a = h.Account(imported);
            Assert.True(a.IsConnected);
            Assert.Equal("memepage", a.Username);
            Assert.Equal("keep me", a.Notes);
            Assert.Equal("v1 caption", a.Strategy.FixedCaption);
            Assert.Single(h.Store.Read(s => s.Accounts));
        }

        [Fact]
        public async Task Connecting_a_chosen_imported_account_attaches_even_when_it_was_renamed()
        {
            using var h = new Harness();
            string imported = h.Store.Mutate(s =>
            {
                var a = new OmniGramAccount { Username = "old_v1_name", Connection = ConnectionState.AwaitingConnection, Legacy = new LegacyAccountInfo { ImportedUtc = h.Clock.Now } };
                s.Accounts[a.AccountId] = a;
                return a.AccountId;
            });

            // The website passes the imported account's id; Instagram now calls it "memepage".
            var result = await h.Auth.ConnectAsync("IGAAT-token-for-memepage-xxxx", null, null, "token", imported, null, false, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.False(result.IsNew);
            Assert.True(result.AttachedToImported);
            Assert.Equal(imported, result.AccountId);
            Assert.Equal("memepage", h.Account(imported).Username);
            Assert.Single(h.Store.Read(s => s.Accounts));
        }

        [Fact]
        public async Task An_imported_account_cannot_take_an_instagram_account_that_is_already_connected()
        {
            using var h = new Harness();
            string connected = (await h.Auth.ConnectAsync("IGAAT-first-token-xxxxxxxxxxxx", null, null, "token", null, null, false, CancellationToken.None)).AccountId!;
            string imported = h.Store.Mutate(s =>
            {
                var a = new OmniGramAccount { Username = "v1copy", Connection = ConnectionState.AwaitingConnection, Legacy = new LegacyAccountInfo { ImportedUtc = h.Clock.Now } };
                s.Accounts[a.AccountId] = a;
                return a.AccountId;
            });

            var result = await h.Auth.ConnectAsync("IGAAT-second-token-xxxxxxxxxxx", null, null, "token", imported, null, false, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("already connected", result.Message);
            Assert.Null(h.Account(imported).IgUserId);
            Assert.Equal(("IGAAT-first-token-xxxxxxxxxxxx", "1784"), h.Auth.GetCredentials(connected));
        }

        [Fact]
        public async Task Personal_accounts_and_rejected_tokens_are_refused_with_a_reason()
        {
            using var h = new Harness();
            h.Api.OnProfile = _ => new IgProfile { UserId = "9", Username = "personal", AccountType = "PERSONAL" };
            var personal = await h.Auth.ConnectAsync("IGAAT-personal-token-xxxxxxxx", null, null, "token", null, null, false, CancellationToken.None);
            Assert.False(personal.Ok);
            Assert.Contains("Professional", personal.Message);

            h.Api.OnProfile = _ => throw new InstagramApiException(IgErrorKind.TokenInvalid, 400, 190, null, "Invalid OAuth access token");
            var bad = await h.Auth.ConnectAsync("IGAAT-bad-token-xxxxxxxxxxxxxx", null, null, "token", null, null, false, CancellationToken.None);
            Assert.False(bad.Ok);
            Assert.Contains("rejected the token", bad.Message);
            Assert.Empty(h.Store.Read(s => s.Accounts));
        }

        [Fact]
        public async Task Reconnecting_as_a_different_instagram_account_keeps_the_old_one()
        {
            using var h = new Harness();
            string first = h.ConnectAccount("memepage", "1784");
            h.Api.OnProfile = _ => new IgProfile { UserId = "5555", Username = "otherpage", AccountType = "BUSINESS" };
            var result = await h.Auth.ConnectAsync("IGAAT-other-token-xxxxxxxxxxxx", null, null, "token", first, null, false, CancellationToken.None);
            Assert.True(result.IsNew);
            Assert.NotEqual(first, result.AccountId);
            Assert.Equal(2, h.Store.Read(s => s.Accounts.Count));
        }

        [Fact]
        public async Task The_business_login_flow_completes_from_the_redirect()
        {
            using var h = new Harness();
            Assert.True((await h.Auth.SaveAppAsync("123456789", "app-secret", null)).Saved);
            var (flowId, url) = await h.Auth.BeginAsync(null, "daily-reels", autopilot: false);
            Assert.Contains(flowId, url);

            var result = await h.Auth.HandleCallbackAsync(new NameValueCollection { ["state"] = flowId, ["code"] = "AQBxyz#_" }, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Contains("oauth/code:AQBxyz#_", h.Api.Calls);
            Assert.Contains("oauth/exchange", h.Api.Calls);
            var a = h.Account(result.AccountId!);
            Assert.Equal("oauth", a.ConnectedVia);
            Assert.Equal(7, a.Strategy.Slots.Count);
            Assert.Equal(InstagramGraphClient.Scopes.Length, a.Scopes.Count);
            Assert.Equal(h.Clock.Now.AddSeconds(5183944), a.TokenExpiresUtc);
            Assert.NotNull(h.Store.Read(s => s.App.VerifiedUtc));
            Assert.Equal("completed", h.Auth.GetFlow(flowId)!.State);

            var again = await h.Auth.HandleCallbackAsync(new NameValueCollection { ["state"] = flowId, ["code"] = "AQBxyz" }, CancellationToken.None);
            Assert.True(again.Ok);
            Assert.Equal("Already connected", again.Title);
        }

        [Fact]
        public async Task Declined_or_unknown_authorizations_fail_cleanly()
        {
            using var h = new Harness();
            await h.Auth.SaveAppAsync("123", "secret", null);
            var unknown = await h.Auth.HandleCallbackAsync(new NameValueCollection { ["state"] = "nope", ["code"] = "x" }, CancellationToken.None);
            Assert.False(unknown.Ok);

            var (flowId, _) = await h.Auth.BeginAsync(null, null, false);
            var denied = await h.Auth.HandleCallbackAsync(new NameValueCollection { ["state"] = flowId, ["error"] = "access_denied", ["error_reason"] = "user_denied" }, CancellationToken.None);
            Assert.False(denied.Ok);
            Assert.Contains("declined", denied.Message);
            Assert.Equal("failed", h.Auth.GetFlow(flowId)!.State);

            var (expiring, _) = await h.Auth.BeginAsync(null, null, false);
            h.Clock.Advance(TimeSpan.FromMinutes(31));
            h.Auth.ExpireFlows();
            Assert.Equal("failed", h.Auth.GetFlow(expiring)!.State);
        }

        [Fact]
        public async Task App_settings_are_validated_and_the_secret_is_encrypted()
        {
            using var h = new Harness();
            Assert.False((await h.Auth.SaveAppAsync("abc", "secret", null)).Saved);
            Assert.False((await h.Auth.SaveAppAsync("123", "secret", "http://insecure.example/cb")).Saved);
            Assert.True((await h.Auth.SaveAppAsync("123", "secret", null)).Saved);
            Assert.Equal(("123", "secret"), h.Auth.AppCredentials());
            Assert.DoesNotContain("secret", h.Store.Read(s => s.App.AppSecretCipher));
            Assert.Equal(OmniGramAppConfig.DefaultRedirectUrl, h.Auth.RedirectUrl);
        }

        [Fact]
        public void Token_refresh_is_due_only_for_tokens_older_than_a_day_with_under_fifty_days_left()
        {
            var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            OmniGramAccount Make(DateTime issued, DateTime expires) => new() { IgUserId = "1", TokenCipher = "ogv1:x", TokenIssuedUtc = issued, TokenExpiresUtc = expires, Connection = ConnectionState.Healthy };
            Assert.False(OmniGramAuth.RefreshDue(Make(now.AddHours(-2), now.AddDays(10)), now));
            Assert.False(OmniGramAuth.RefreshDue(Make(now.AddDays(-5), now.AddDays(55)), now));
            Assert.True(OmniGramAuth.RefreshDue(Make(now.AddDays(-12), now.AddDays(48)), now));
            Assert.False(OmniGramAuth.RefreshDue(Make(now.AddDays(-70), now.AddDays(-1)), now)); // expired: can't refresh
        }

        [Fact]
        public async Task Refreshing_renews_the_token_and_a_refusal_asks_for_a_reconnect()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount(configure: a => { a.TokenIssuedUtc = h.Clock.Now.AddDays(-20); a.TokenExpiresUtc = h.Clock.Now.AddDays(40); });

            Assert.True(await h.Auth.RefreshTokenIfDueAsync(acct, force: false, CancellationToken.None));
            var refreshed = h.Account(acct);
            Assert.Equal(h.Clock.Now.AddSeconds(5183944), refreshed.TokenExpiresUtc);
            Assert.Equal("refreshed-token-memepage", h.Auth.GetCredentials(acct).Token);
            Assert.False(await h.Auth.RefreshTokenIfDueAsync(acct, force: false, CancellationToken.None)); // just renewed

            h.Api.OnRefresh = _ => throw new InstagramApiException(IgErrorKind.TokenInvalid, 400, 190, 460, "The session has been invalidated because the user changed their password");
            await Assert.ThrowsAsync<InstagramApiException>(() => h.Auth.RefreshTokenIfDueAsync(acct, force: true, CancellationToken.None));
            Assert.Equal(ConnectionState.NeedsReauth, h.Account(acct).Connection);
            Assert.Throws<AccountUnavailableException>(() => h.Auth.GetCredentials(acct));
        }
    }

    public class OmniGramStoreAndHostTests
    {
        [Fact]
        public void Secrets_are_bound_to_their_purpose()
        {
            var vault = new OmniGramVault(RandomNumberGenerator.GetBytes(32));
            string cipher = vault.Protect("token-value", "account:a:token");
            Assert.StartsWith(OmniGramVault.Prefix, cipher);
            Assert.Equal("token-value", vault.Unprotect(cipher, "account:a:token"));
            Assert.Null(vault.Unprotect(cipher, "account:b:token"));
            Assert.Null(vault.Unprotect("plain", "account:a:token"));
        }

        [Fact]
        public async Task State_survives_a_reload()
        {
            using var dir = new TempDir();
            var key = RandomNumberGenerator.GetBytes(32);
            string accountId;
            {
                var store = new OmniGramStore(dir.Path, _ => { }, new OmniGramVault(key));
                store.Load();
                accountId = store.Mutate(s =>
                {
                    var a = new OmniGramAccount { Username = "persisted", Autopilot = true };
                    a.TokenCipher = s.Vault.Protect("t", "account:" + a.AccountId + ":token");
                    s.Accounts[a.AccountId] = a;
                    s.PostsOf(a.AccountId).Add(new OmniGramPost { AccountId = a.AccountId, Caption = "hello", Status = PostStatus.Ready });
                    s.InsightsOf(a.AccountId).Days["2026-10-06"] = new AccountDay { Day = "2026-10-06", Reach = 42 };
                    s.MarkAccount(a.AccountId);
                    s.MarkPosts(a.AccountId);
                    s.MarkInsights(a.AccountId);
                    s.AddEvent(EventLevel.Info, "test", "an event", a.AccountId);
                    return a.AccountId;
                });
                await store.FlushAsync();
            }
            var reloaded = new OmniGramStore(dir.Path, _ => { }, new OmniGramVault(key));
            reloaded.Load();
            reloaded.Read(s =>
            {
                var a = s.Account(accountId)!;
                Assert.Equal("persisted", a.Username);
                Assert.True(a.Autopilot);
                Assert.Equal("t", s.Vault.Unprotect(a.TokenCipher, "account:" + accountId + ":token"));
                Assert.Equal("hello", s.PostsOf(accountId).Single().Caption);
                Assert.Equal(42, s.InsightsOf(accountId).Days["2026-10-06"].Reach);
                Assert.Contains(s.Events, e => e.Kind == "test");
                return 0;
            });
        }

        [Fact]
        public void A_corrupt_file_is_parked_and_the_rest_still_loads()
        {
            using var dir = new TempDir();
            var store = new OmniGramStore(dir.Path, _ => { }, new OmniGramVault(RandomNumberGenerator.GetBytes(32)));
            store.Load();
            File.WriteAllText(Path.Combine(dir.Path, "engine.json"), "{ not json");
            var reloaded = new OmniGramStore(dir.Path, _ => { }, new OmniGramVault(RandomNumberGenerator.GetBytes(32)));
            reloaded.Load();
            Assert.NotNull(reloaded.Read(s => s.Engine));
            Assert.Single(Directory.GetFiles(dir.Path, "engine.json.corrupt-*"));
        }

        [Fact]
        public void Media_links_are_signed_expire_and_resolve_only_inside_the_prepared_folder()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);

            string url = h.Host.CreateUrl(postId, 0, ".mp4");
            Assert.StartsWith("https://klive.test/omnigram/public/media?t=", url);
            Assert.EndsWith("&f=media.mp4", url);
            string token = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;
            Assert.True(h.Host.TryValidate(token, out string pid, out int index, out _));
            var resolved = h.Host.Resolve(pid, index);
            Assert.Equal(Path.GetFullPath(h.Post(postId).Media[0].PreparedPath!), resolved!.Value.Path);
            Assert.Equal("video/mp4", resolved.Value.MimeType);

            string tampered = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");
            Assert.False(h.Host.TryValidate(tampered, out _, out _, out var error));
            Assert.Equal("bad signature", error);
            string otherPost = token.Replace(postId, "aaaaaaaaaaaa");
            Assert.False(h.Host.TryValidate(otherPost, out _, out _, out _));

            h.Clock.Advance(TimeSpan.FromHours(7));
            Assert.False(h.Host.TryValidate(token, out _, out _, out error));
            Assert.Equal("expired", error);

            // A prepared path that points outside the prepared folder is never served.
            h.Store.Mutate(s => s.FindPost(postId)!.Media[0].PreparedPath = h.Post(postId).Media[0].Path);
            Assert.Null(h.Host.Resolve(postId, 0));
        }

        [Fact]
        public void Fetches_by_metas_crawler_are_recorded_as_proof_the_url_works()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            h.Host.RecordFetch(postId, "facebookexternalua");
            h.Host.RecordFetch(postId, "curl/8");
            var post = h.Post(postId);
            Assert.Equal(2, post.Publish.MediaFetches);
            Assert.Equal(h.Clock.Now, h.Store.Read(s => s.Engine.LastExternalMediaFetchUtc));
            Assert.True(OmniGramMediaHost.IsMetaFetcher("Mozilla/5.0 (compatible; meta-externalagent/1.1)"));
            Assert.False(OmniGramMediaHost.IsMetaFetcher("Googlebot"));
        }
    }
}
