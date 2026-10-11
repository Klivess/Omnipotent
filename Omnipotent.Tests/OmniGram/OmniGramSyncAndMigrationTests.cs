using Newtonsoft.Json;
using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    public class OmniGramSyncTests
    {
        [Fact]
        public async Task An_account_refresh_reads_profile_quota_media_insights_days_and_comments()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            await h.PublishUntilSettledAsync();
            string mediaId = h.Post(postId).IgMediaId!;
            h.Api.Media.Add(new IgMediaItem { Id = "made-in-app", Caption = "posted by hand", MediaType = "IMAGE", MediaProductType = "FEED", TimestampUtc = h.Clock.Now.AddDays(-3), LikeCount = 40, CommentsCount = 2 });
            h.Api.Media[0].LikeCount = 77;
            h.Api.Media[0].CommentsCount = 1;
            h.Api.Limit = new IgPublishingLimit { QuotaUsage = 3, QuotaTotal = 100 };
            h.Api.OnComments = id => id == mediaId
                ? new List<IgComment> { new() { Id = "c1", Text = "lol", Username = "fan", TimestampUtc = h.Clock.Now, Replies = { new IgComment { Id = "c1r", Text = "thanks", Username = "memepage", TimestampUtc = h.Clock.Now } } } }
                : new List<IgComment> { new() { Id = "c2", Text = "nice", Username = "fan2", TimestampUtc = h.Clock.Now }, new() { Id = "c3", Text = "first", Username = "fan3", TimestampUtc = h.Clock.Now } };
            h.Clock.Advance(TimeSpan.FromHours(3)); // past the 2-hour insights milestone

            var errors = await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);

            Assert.Empty(errors);
            var account = h.Account(acct);
            Assert.Equal(1200, account.Stats.Followers);
            Assert.Equal(100, account.Quota.QuotaTotal);
            Assert.Equal(3, account.Quota.QuotaUsage);
            var post = h.Post(postId);
            Assert.Equal(77, post.Metrics.Likes);
            Assert.Equal(10, post.Metrics.Views); // the fake reports 10 for every metric
            Assert.NotNull(post.Metrics.InsightsSyncedUtc);
            var insights = h.Store.Read(s => OmniGramStore.Clone(s.InsightsOf(acct)));
            Assert.Equal(2, insights.Index.Count);
            Assert.Equal(postId, insights.Index.Single(m => m.Id == mediaId).OmniPostId);
            Assert.Null(insights.Index.Single(m => m.Id == "made-in-app").OmniPostId);
            Assert.NotEmpty(insights.Days);
            Assert.Equal(100, insights.Days.Values.First().Reach);
            Assert.True(insights.Comments.Single(c => c.Id == "c1").Replied);
            Assert.True(insights.Comments.Single(c => c.Id == "c1r").Own);
            Assert.Contains(h.Store.Read(s => s.Events), e => e.Kind == "comments.new" && e.Message.Contains("3 new comments"));
        }

        [Fact]
        public async Task A_published_post_missing_from_instagram_is_marked_removed()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            await h.PublishUntilSettledAsync();
            h.Api.Media.Clear();
            h.Api.Media.Add(new IgMediaItem { Id = "newer", TimestampUtc = h.Clock.Now.AddHours(5), MediaProductType = "FEED" });
            h.Clock.Advance(TimeSpan.FromHours(6));

            await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);

            Assert.Equal(PostStatus.Removed, h.Post(postId).Status);
        }

        [Fact]
        public async Task Metrics_instagram_refuses_are_learned_and_not_asked_for_again()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            await h.PublishUntilSettledAsync();
            var asked = new List<string>();
            h.Api.OnMediaInsights = (id, metrics) =>
            {
                asked.Add(string.Join(",", metrics));
                if (metrics.Contains("reels_skip_rate"))
                    throw new InstagramApiException(IgErrorKind.BadRequest, 400, 100, null, "(#100) metric[9] must be one of the following values: views, reach, likes, comments, shares, saved, total_interactions, ig_reels_avg_watch_time, ig_reels_video_view_total_time");
                return new IgInsightValues { Values = { ["views"] = 5000, ["reach"] = 4000, ["ig_reels_avg_watch_time"] = 7200 } };
            };
            h.Clock.Advance(TimeSpan.FromHours(3));

            await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);

            Assert.Equal(2, asked.Count);
            Assert.DoesNotContain("reels_skip_rate", asked[1]);
            Assert.DoesNotContain("reposts", asked[1]);
            var unsupported = h.Store.Read(s => s.Engine.UnsupportedMetrics["REELS"].ToList());
            Assert.Contains("reels_skip_rate", unsupported);
            var post = h.Post(postId);
            Assert.Equal(5000, post.Metrics.Views);
            Assert.Equal(7200, post.Metrics.AvgWatchTimeMs);
        }

        [Fact]
        public void Insights_are_read_as_posts_cross_milestones()
        {
            var published = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var post = new OmniGramPost { Status = PostStatus.Published, PublishedUtc = published, Format = PostFormat.Reel };
            Assert.False(OmniGramAnalyticsSync.NeedsInsights(post, published.AddHours(1)));
            Assert.True(OmniGramAnalyticsSync.NeedsInsights(post, published.AddHours(3)));
            post.Metrics.InsightsSyncedUtc = published.AddHours(3);
            Assert.False(OmniGramAnalyticsSync.NeedsInsights(post, published.AddHours(20)));
            Assert.True(OmniGramAnalyticsSync.NeedsInsights(post, published.AddDays(1.1)));
            var story = new OmniGramPost { Status = PostStatus.Published, PublishedUtc = published, Format = PostFormat.Story };
            Assert.True(OmniGramAnalyticsSync.NeedsInsights(story, published.AddHours(2)));
            Assert.False(OmniGramAnalyticsSync.NeedsInsights(story, published.AddHours(25)));
        }

        [Fact]
        public async Task Readings_at_a_day_and_a_week_are_kept_for_fair_comparison()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            string postId = h.AddReadyPost(acct);
            await h.PublishUntilSettledAsync();
            long views = 100;
            h.Api.OnMediaInsights = (_, _) => new IgInsightValues { Values = { ["views"] = views, ["likes"] = views / 10 } };

            h.Clock.Advance(TimeSpan.FromHours(25));
            await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);
            views = 900;
            h.Clock.Advance(TimeSpan.FromDays(7));
            await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);

            var m = h.Post(postId).Metrics;
            Assert.Equal(100, m.ViewsAt24h);
            Assert.Equal(900, m.ViewsAt7d);
            Assert.Equal(900, m.Views);
            Assert.True(m.History.Count >= 3);
        }

        [Fact]
        public async Task A_missing_comments_permission_is_noted_without_asking_to_reconnect()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            h.Api.Media.Add(new IgMediaItem { Id = "x", TimestampUtc = h.Clock.Now.AddHours(-1), MediaProductType = "FEED", CommentsCount = 3 });
            h.Api.OnComments = _ => throw new InstagramApiException(IgErrorKind.PermissionDenied, 400, 10, null, "(#10) Application does not have permission for this action");

            var errors = await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);

            Assert.Contains(errors, e => e.StartsWith("Comments: Permission missing"));
            Assert.Equal(ConnectionState.Healthy, h.Account(acct).Connection);
        }

        [Fact]
        public async Task Due_jobs_run_from_the_persisted_schedule_and_respect_the_budget()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            Assert.Equal(0, await h.Sync.RunDueAsync(CancellationToken.None)); // first runs are staggered a few minutes out
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            h.Budget.Observe(new IgUsageHeaders { ObservedUtc = h.Clock.Now, AppUsagePercent = 95 });
            int ran = await h.Sync.RunDueAsync(CancellationToken.None);
            Assert.True(ran >= 2); // token + profile (+ limit) still run
            Assert.Equal(0, h.Api.Count("media/list")); // background jobs wait while usage is high
            Assert.Equal(1, h.Api.Count("me"));

            h.Budget.Observe(new IgUsageHeaders { ObservedUtc = h.Clock.Now, AppUsagePercent = 10 });
            h.Clock.Advance(TimeSpan.FromMinutes(25));
            await h.Sync.RunDueAsync(CancellationToken.None);
            Assert.Equal(1, h.Api.Count("media/list"));
        }

        [Fact]
        public async Task An_invalid_token_during_sync_parks_the_account()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount();
            h.Api.OnProfile = _ => throw new InstagramApiException(IgErrorKind.TokenInvalid, 400, 190, 467, "Invalid OAuth access token");
            await h.Sync.SyncAccountNowAsync(acct, CancellationToken.None);
            Assert.Equal(ConnectionState.NeedsReauth, h.Account(acct).Connection);
        }
    }

    public class OmniGramMigrationTests
    {
        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        [Fact]
        public void V1_accounts_history_and_used_content_are_imported_once_without_passwords()
        {
            using var h = new Harness();
            string v1 = Path.Combine(h.Dir.Path, "v1");
            string usedFile = h.Dir.File("folder/used.mp4", 3000);
            WriteJson(Path.Combine(v1, "Accounts", "dankpage.json"), new
            {
                AccountId = "11112222",
                Username = "dankpage",
                Password = "hunter2",
                IsActive = true,
                IsPaused = false,
                AddedDate = new DateTime(2026, 4, 10),
                Tags = new[] { "memes" },
                Notes = "main page",
                FollowerCount = 5400,
                LoginStatus = "Error",
                ContentConfig = new
                {
                    ContentSource = "MemeScraper",
                    MemeScraperNicheFilter = "programming",
                    CaptionMode = "RandomFromList",
                    CandidateCaptions = new[] { "lol", "so true" },
                    Hashtags = new[] { "#memes", "#dev", "#code" },
                    RotateHashtags = true,
                    MaxHashtagsPerPost = 2,
                    PostsPerDay = 2,
                    MinIntervalMinutes = 180,
                    PreferredPostHoursUTC = new[] { 18, 9, 13 },
                    ActiveDaysOfWeek = new[] { 1, 3 },
                    ScheduleRandomOffsetMinutes = 20,
                    UsedContentPaths = new[] { usedFile },
                },
            });
            WriteJson(Path.Combine(v1, "Posts", "p1.json"), new { PostId = "p1", AccountId = "11112222", ContentType = "Reel", Caption = "old post", Hashtags = new[] { "memes" }, ScheduledTime = new DateTime(2026, 4, 12, 18, 0, 0), PostedTime = new DateTime(2026, 4, 12, 18, 2, 0), Status = "Posted", SourceType = "MemeScraper", SourceId = "reel-1" });
            WriteJson(Path.Combine(v1, "Posts", "p2.json"), new { PostId = "p2", AccountId = "11112222", ContentType = "Reel", ScheduledTime = new DateTime(2026, 4, 20), Status = "Queued", SourceType = "MemeScraper", SourceId = "reel-2" });
            WriteJson(Path.Combine(v1, "Analytics", "11112222", "2026-04-11.json"), new { Timestamp = new DateTime(2026, 4, 11, 2, 0, 0), FollowerCount = 5000, FollowingCount = 10, MediaCount = 80 });

            string? summary = OmniGramMigration.MigrateV1(h.Store, v1, h.Clock.Now, _ => { });

            Assert.Contains("Imported 1 v1 account(s)", summary);
            var a = h.Store.Read(s => OmniGramStore.Clone(s.AccountByUsername("dankpage")!));
            Assert.Equal(ConnectionState.AwaitingConnection, a.Connection);
            Assert.False(a.Autopilot);
            Assert.False(a.IsConnected);
            Assert.Equal("11112222", a.Legacy!.V1AccountId);
            Assert.Equal("main page", a.Notes);
            Assert.Equal(5400, a.Stats.Followers);
            Assert.Null(a.TokenCipher);
            Assert.DoesNotContain("hunter2", JsonConvert.SerializeObject(a));

            var st = a.Strategy;
            Assert.Equal("UTC", st.TimeZone);
            Assert.Equal(4, st.Slots.Count); // Mon + Wed × the first two preferred hours (09:00, 13:00)
            Assert.Contains(st.Slots, x => x.Day == DayOfWeek.Monday && x.Minute == 9 * 60);
            Assert.Contains(st.Slots, x => x.Day == DayOfWeek.Wednesday && x.Minute == 13 * 60);
            Assert.Equal(20, st.JitterMinutes);
            Assert.Equal(180, st.MinGapMinutes);
            Assert.Equal(ContentSourceKind.MemeScraper, st.Source);
            Assert.Equal(new[] { "programming" }, st.MemeScraper.Niches);
            Assert.Equal(CaptionMode.Rotate, st.CaptionMode);
            Assert.Equal(new[] { "lol", "so true" }, st.CaptionPool);
            Assert.Empty(st.FixedHashtags);
            Assert.Equal(new[] { "memes", "dev", "code" }, st.RotatingHashtags);
            Assert.Equal(2, st.RotatingHashtagsPerPost);

            Assert.Contains("ig:reel-1", a.UsedContentKeys);
            Assert.Contains("ig:reel-2", a.UsedContentKeys); // queued in v1: reserved, never reposted
            Assert.Contains(OmniGramContentSources.FileKey(new FileInfo(usedFile)), a.UsedContentKeys);

            var posts = h.Posts(a.AccountId);
            var imported = Assert.Single(posts);
            Assert.Equal(PostStatus.Published, imported.Status);
            Assert.Equal(PostOrigin.Imported, imported.Origin);
            Assert.Equal("old post", imported.Caption);

            var snapshots = h.Store.Read(s => s.InsightsOf(a.AccountId).Snapshots.ToList());
            Assert.Equal(5000, Assert.Single(snapshots).Followers);
            Assert.True(h.Store.Read(s => s.Engine.V1PasswordFilesPresent));

            Assert.Null(OmniGramMigration.MigrateV1(h.Store, v1, h.Clock.Now, _ => { })); // once only
        }

        [Fact]
        public void V1_credentials_can_be_removed_while_keeping_the_rest_of_the_file()
        {
            using var dir = new TempDir();
            string v1 = Path.Combine(dir.Path, "v1");
            WriteJson(Path.Combine(v1, "Accounts", "a.json"), new { Username = "a", Password = "secret", Notes = "keep" });
            WriteJson(Path.Combine(v1, "Accounts", "b.json"), new { Username = "b", Password = "" });
            Directory.CreateDirectory(Path.Combine(v1, "Sessions"));
            File.WriteAllText(Path.Combine(v1, "Sessions", "a.session"), "cookies");

            Assert.True(OmniGramMigration.V1PasswordsPresent(v1));
            var (files, sessions) = OmniGramMigration.RemoveV1Credentials(v1);

            Assert.Equal(1, files);
            Assert.Equal(1, sessions);
            Assert.False(OmniGramMigration.V1PasswordsPresent(v1));
            string kept = File.ReadAllText(Path.Combine(v1, "Accounts", "a.json"));
            Assert.Contains("keep", kept);
            Assert.DoesNotContain("secret", kept);
        }

        [Fact]
        public void A_missing_v1_folder_is_a_quiet_no_op()
        {
            using var h = new Harness();
            string? summary = OmniGramMigration.MigrateV1(h.Store, Path.Combine(h.Dir.Path, "nothing-here"), h.Clock.Now, _ => { });
            Assert.Equal("No OmniGram v1 accounts to import.", summary);
            Assert.False(h.Store.Read(s => s.Engine.V1PasswordFilesPresent));
        }
    }

    public class OmniGramViewTests
    {
        [Fact]
        public void Account_states_explain_what_needs_doing_first()
        {
            using var h = new Harness();
            string connect = h.Store.Mutate(s => { var a = new OmniGramAccount { Username = "imported", Legacy = new LegacyAccountInfo() }; s.Accounts[a.AccountId] = a; return a.AccountId; });
            string reauth = h.ConnectAccount("reauth", "2", a => { a.Connection = ConnectionState.NeedsReauth; a.ConnectionDetail = "expired"; });
            string paused = h.ConnectAccount("paused", "3", a => a.Paused = true);
            string ok = h.ConnectAccount("ok", "4", a => a.Autopilot = true);

            var states = h.Store.Read(s => s.Accounts.Values.ToDictionary(a => a.AccountId, a => OmniGramViews.AccountSummary(s, a, h.Clock.Now)));
            Assert.Equal("connect", states[connect].State);
            Assert.Contains("v1", states[connect].StateReason);
            Assert.Equal("error", states[reauth].State);
            Assert.Equal("paused", states[paused].State);
            Assert.Equal("ok", states[ok].State);
            Assert.Equal("https://www.instagram.com/ok/", states[ok].ProfileUrl);
        }

        [Fact]
        public void Attention_lists_connections_public_url_problems_and_v1_passwords()
        {
            using var h = new Harness();
            h.Store.Mutate(s =>
            {
                var a = new OmniGramAccount { Username = "imported", Legacy = new LegacyAccountInfo() };
                s.Accounts[a.AccountId] = a;
                s.Engine.V1PasswordFilesPresent = true;
                s.Engine.PublicUrlCheck = new PublicUrlCheck { Ok = false, Detail = "HTTP 502" };
            });
            h.ConnectAccount("reauth", "2", a => a.Connection = ConnectionState.NeedsReauth);

            var items = h.Store.Read(s => OmniGramViews.Attention(s, h.Clock.Now, publishingEnabled: false, aiAvailable: true, memeScraperReady: true, "https://klive.test"));

            Assert.Contains(items, i => i.Title == "Connect @imported" && i.Action == "connect");
            Assert.Contains(items, i => i.Title == "Reconnect @reauth" && i.Level == "error");
            Assert.Contains(items, i => i.Action == "public-url" && i.Detail.Contains("HTTP 502"));
            Assert.Contains(items, i => i.Action == "v1-passwords");
            Assert.Contains(items, i => i.Title == "Publishing is switched off");

            // Proof that Instagram fetched media recently outweighs a failed self-check.
            h.Store.Mutate(s => s.Engine.LastExternalMediaFetchUtc = h.Clock.Now.AddHours(-1));
            var after = h.Store.Read(s => OmniGramViews.Attention(s, h.Clock.Now, true, true, true, "https://klive.test"));
            Assert.DoesNotContain(after, i => i.Action == "public-url");
            Assert.DoesNotContain(after, i => i.Title == "FFmpeg is not usable");

            var noFfmpeg = h.Store.Read(s => OmniGramViews.Attention(s, h.Clock.Now, true, true, true, "https://klive.test", "ffmpeg cannot run (FFmpeg/ffmpeg.exe): not a valid application"));
            Assert.Contains(noFfmpeg, i => i.Title == "FFmpeg is not usable" && i.Detail.StartsWith("ffmpeg cannot run") && i.Action == "settings");
        }

        [Fact]
        public void Analytics_sum_views_and_engagement_and_find_the_best_times()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount(configure: a => a.Strategy.TimeZone = "UTC");
            for (int i = 0; i < 4; i++)
            {
                int n = i;
                h.AddReadyPost(acct, p =>
                {
                    p.Status = PostStatus.Published;
                    p.PublishedUtc = h.Clock.Now.AddDays(-2 - n * 7);
                    p.IgMediaId = "m" + n;
                    p.Hashtags = new() { "memes" };
                    p.Metrics = new PostMetrics { Views = 1000 * (n + 1), Reach = 800, Likes = 100, Comments = 10, Shares = 5, Saves = 5 };
                });
            }
            h.Store.Mutate(s =>
            {
                var insights = s.InsightsOf(acct);
                for (int i = 0; i < 4; i++)
                    insights.Index.Add(new IndexedMedia { Id = "m" + i, PublishedUtc = h.Clock.Now.AddDays(-2 - i * 7), Views = 1000 * (i + 1), Likes = 100, ProductType = "REELS", OmniPostId = "x" });
                insights.Days["2026-10-05"] = new AccountDay { Day = "2026-10-05", Reach = 300, Follows = 12, Unfollows = 2 };
                insights.OnlineFollowersByHour = Enumerable.Range(0, 24).ToDictionary(hr => hr, hr => hr == 10 ? 900.0 : 100.0);
                insights.Demographics = new DemographicsSnapshot { Followers = { ["country"] = new Dictionary<string, long> { ["GB"] = 75, ["US"] = 25 } } };
            });

            var dto = h.Store.Read(s => OmniGramViews.Analytics(s, acct, 30, h.Clock.Now));

            Assert.Equal(4, dto.PostsPublished);
            Assert.Equal(10000, dto.Views);
            Assert.Equal(2500, dto.AvgViews);
            Assert.Equal(2500, dto.MedianViews);
            Assert.Equal(120, dto.AvgInteractions);
            Assert.Equal(15.0, dto.EngagementRate); // 480 interactions ÷ 3,200 reach
            Assert.Equal(300, dto.AccountReach);
            Assert.Equal(12, dto.NewFollows);
            Assert.Equal(2, dto.Unfollows);
            Assert.Equal("memes", dto.Hashtags.Single().Key);
            Assert.Equal(75, dto.Demographics["country"][0].AvgViews); // percent share
            Assert.Equal(24, dto.OnlineByHour.Count);
            Assert.Equal(900, dto.OnlineByHour.Max());
            Assert.Equal("m3", dto.TopPosts[0].Id);
            Assert.NotEmpty(dto.Heatmap);
        }

        [Fact]
        public void Fleet_kpis_count_published_posts_and_success()
        {
            using var h = new Harness();
            string acct = h.ConnectAccount(configure: a => a.Autopilot = true);
            h.AddReadyPost(acct, p => { p.Status = PostStatus.Published; p.PublishedUtc = h.Clock.Now.AddDays(-1); p.Metrics.Views = 300; });
            h.AddReadyPost(acct, p => { p.Status = PostStatus.Failed; p.UpdatedUtc = h.Clock.Now.AddDays(-1); });
            h.AddReadyPost(acct);

            var k = h.Store.Read(s => OmniGramViews.FleetKpis(s, h.Clock.Now));

            Assert.Equal(1, k.Published7d);
            Assert.Equal(1, k.Pending);
            Assert.Equal(1, k.Failed7d);
            Assert.Equal(50.0, k.SuccessRate30d);
            Assert.Equal(300, k.AvgViews30d);
            Assert.Equal(1, k.AutopilotAccounts);
            Assert.Equal("memepage", k.NextPostAccount);
        }
    }
}
