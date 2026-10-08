using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Specialized;
using System.Security.Cryptography;

namespace Omnipotent.Tests.OmniTumblr
{
    public class OmniTumblrAuthTests
    {
        private static readonly DateTime Start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static NameValueCollection Query(params (string Key, string Value)[] pairs)
        {
            var q = new NameValueCollection();
            foreach (var (k, v) in pairs) q[k] = v;
            return q;
        }

        [Fact]
        public async Task AppCredentials_AreVerifiedBeforeSaving_AndStoredEncrypted()
        {
            using var h = new Harness(Start);
            var result = await h.Auth.SaveAppAsync("my-consumer-key", "my-consumer-secret", null, TumblrAuthMode.OAuth1, CancellationToken.None);

            Assert.True(result.Saved);
            Assert.True(result.Verified);
            Assert.Equal(("my-consumer-key", "my-consumer-secret"), h.Auth.AppCredentials());
            var app = h.Store.Read(s => OmniTumblrStore.Clone(s.App));
            Assert.StartsWith(OmniTumblrVault.Prefix, app.ConsumerKeyCipher);
            Assert.DoesNotContain("my-consumer", app.ConsumerKeyCipher);
            Assert.Equal("-key", app.ConsumerKeyHint);
            Assert.Equal(OmniTumblrAppConfig.DefaultCallbackUrl, app.CallbackUrl);
        }

        [Fact]
        public async Task RejectedAppCredentials_AreNotSaved()
        {
            using var h = new Harness(Start);
            h.Api.OnRequestToken = (_, _) => throw new TumblrApiException(TumblrErrorKind.Unauthorized, 401, null, "Invalid consumer key");
            var result = await h.Auth.SaveAppAsync("bad", "worse", null, null, CancellationToken.None);
            Assert.False(result.Saved);
            Assert.Contains("rejected", result.Message);
            Assert.Null(h.Auth.AppCredentials());
        }

        [Fact]
        public async Task OAuth1Flow_CreatesAConnectionWithTheAccountsBlogs()
        {
            using var h = new Harness(Start);
            await h.Auth.SaveAppAsync("ck", "cs", null, TumblrAuthMode.OAuth1, CancellationToken.None);
            var (flowId, url, mode) = await h.Auth.BeginAsync(null, null, CancellationToken.None);
            Assert.Equal(TumblrAuthMode.OAuth1, mode);
            Assert.Contains("oauth_token=req-token", url);
            Assert.Equal("pending", h.Auth.GetFlow(flowId)!.State);

            var result = await h.Auth.HandleCallbackAsync(Query(("oauth_token", "req-token"), ("oauth_verifier", "v123")), CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal("klives", result.UserName);
            Assert.Equal(2, result.BlogCount);
            Assert.Contains(h.Api.Calls, c => c == "oauth/access_token:req-token:req-secret:v123");
            var flow = h.Auth.GetFlow(flowId)!;
            Assert.Equal("completed", flow.State);
            Assert.Null(flow.RequestTokenSecretCipher);
            var conn = h.Connection(flow.ConnectionId!);
            Assert.Equal(ConnectionHealth.Healthy, conn.Health);
            Assert.Equal(TumblrAuthMode.OAuth1, conn.AuthMode);
            Assert.Equal(2, conn.Blogs.Count);

            var creds = await h.Auth.GetCredentialsAsync(conn.ConnectionId, CancellationToken.None);
            Assert.Equal(("access-token", "access-secret"), (creds.Token, creds.TokenSecret));

            // Replaying the callback does not create a second connection.
            var replay = await h.Auth.HandleCallbackAsync(Query(("oauth_token", "req-token"), ("oauth_verifier", "v123")), CancellationToken.None);
            Assert.True(replay.Ok);
            Assert.Single(h.Store.Read(s => s.Connections.Values.ToList()));
        }

        [Fact]
        public async Task OAuth2Flow_StoresTokens_AndRefreshesThemAheadOfExpiry()
        {
            using var h = new Harness(Start);
            await h.Auth.SaveAppAsync("ck", "cs", null, TumblrAuthMode.OAuth2, CancellationToken.None);
            var (flowId, url, mode) = await h.Auth.BeginAsync(null, null, CancellationToken.None);
            Assert.Equal(TumblrAuthMode.OAuth2, mode);
            Assert.Contains("state=" + flowId, url);

            var result = await h.Auth.HandleCallbackAsync(Query(("code", "the-code"), ("state", flowId)), CancellationToken.None);
            Assert.True(result.Ok, result.Message);
            string connectionId = h.Auth.GetFlow(flowId)!.ConnectionId!;
            Assert.Equal(Start.AddSeconds(2520), h.Connection(connectionId).AccessTokenExpiresUtc);

            var fresh = await h.Auth.GetCredentialsAsync(connectionId, CancellationToken.None);
            Assert.Equal("oauth2-access", fresh.Token);
            Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("oauth2/refresh"));

            h.Clock.Advance(TimeSpan.FromMinutes(40)); // within five minutes of expiry
            var refreshed = await h.Auth.GetCredentialsAsync(connectionId, CancellationToken.None);
            Assert.Equal("oauth2-access-2", refreshed.Token);
            Assert.Contains("oauth2/refresh:oauth2-refresh-1", h.Api.Calls);

            // The rotated refresh token is what gets used next time.
            h.Clock.Advance(TimeSpan.FromMinutes(40));
            await h.Auth.GetCredentialsAsync(connectionId, CancellationToken.None);
            Assert.Contains("oauth2/refresh:oauth2-refresh-2", h.Api.Calls);
        }

        [Fact]
        public async Task AFailedRefresh_MarksTheConnectionForReauthorization()
        {
            using var h = new Harness(Start);
            await h.Auth.SaveAppAsync("ck", "cs", null, TumblrAuthMode.OAuth2, CancellationToken.None);
            var (flowId, _, _) = await h.Auth.BeginAsync(null, null, CancellationToken.None);
            await h.Auth.HandleCallbackAsync(Query(("code", "c"), ("state", flowId)), CancellationToken.None);
            string connectionId = h.Auth.GetFlow(flowId)!.ConnectionId!;
            h.Api.OnRefresh = _ => throw new TumblrApiException(TumblrErrorKind.Unauthorized, 401, null, "invalid_grant");

            h.Clock.Advance(TimeSpan.FromHours(1));
            await Assert.ThrowsAsync<ConnectionUnavailableException>(() => h.Auth.GetCredentialsAsync(connectionId, CancellationToken.None));
            Assert.Equal(ConnectionHealth.NeedsReauth, h.Connection(connectionId).Health);
        }

        [Fact]
        public async Task DeniedAuthorization_FailsTheFlowWithAClearMessage()
        {
            using var h = new Harness(Start);
            await h.Auth.SaveAppAsync("ck", "cs", null, TumblrAuthMode.OAuth2, CancellationToken.None);
            var (flowId, _, _) = await h.Auth.BeginAsync(null, null, CancellationToken.None);
            var result = await h.Auth.HandleCallbackAsync(Query(("error", "access_denied"), ("state", flowId)), CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Contains("declined", result.Message);
            Assert.Equal("failed", h.Auth.GetFlow(flowId)!.State);
        }

        [Fact]
        public async Task AnUnknownCallback_IsRejected()
        {
            using var h = new Harness(Start);
            var result = await h.Auth.HandleCallbackAsync(Query(("oauth_token", "nope"), ("oauth_verifier", "x")), CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Contains("expired", result.Message);
        }

        [Fact]
        public async Task ReconnectingTheSameUser_UpdatesTheExistingConnection_AndClearsReauth()
        {
            using var h = new Harness(Start);
            await h.Auth.SaveAppAsync("ck", "cs", null, TumblrAuthMode.OAuth1, CancellationToken.None);
            var (first, _, _) = await h.Auth.BeginAsync(null, null, CancellationToken.None);
            await h.Auth.HandleCallbackAsync(Query(("oauth_token", "req-token"), ("oauth_verifier", "v")), CancellationToken.None);
            string connectionId = h.Auth.GetFlow(first)!.ConnectionId!;
            h.Auth.MarkNeedsReauth(connectionId, "revoked");

            h.Api.OnRequestToken = (_, _) => ("req-token-2", "req-secret-2");
            h.Api.OnAccessToken = _ => ("access-2", "secret-2");
            var (second, _, _) = await h.Auth.BeginAsync(null, connectionId, CancellationToken.None);
            var result = await h.Auth.HandleCallbackAsync(Query(("oauth_token", "req-token-2"), ("oauth_verifier", "v2")), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal(connectionId, h.Auth.GetFlow(second)!.ConnectionId);
            Assert.Single(h.Store.Read(s => s.Connections.Values.ToList()));
            Assert.Equal(ConnectionHealth.Healthy, h.Connection(connectionId).Health);
            Assert.Equal("access-2", (await h.Auth.GetCredentialsAsync(connectionId, CancellationToken.None)).Token);
        }

        [Fact]
        public async Task UserInfo_UpdatesFollowersAndDetectsRenamesByUuid()
        {
            using var h = new Harness(Start);
            string conn = h.Connect();
            string blogId = h.AddBlog(conn, b => { b.Name = "oldname"; b.Uuid = "t:meme"; });

            await h.Auth.RefreshUserInfoAsync(conn, CancellationToken.None);

            var blog = h.Blog(blogId);
            Assert.Equal("memeblog", blog.Name);
            Assert.Equal(120, blog.Stats.Followers);
            Assert.Single(h.Store.Read(s => s.InsightsOf(blogId).Snapshots.ToList()));
            Assert.Contains(h.Store.Read(s => s.Events.ToList()), e => e.Kind == "blog.renamed");
        }
    }

    public class OmniTumblrStoreTests
    {
        [Fact]
        public async Task State_SurvivesAFlushAndReload()
        {
            using var dir = new TempDir();
            byte[] key = RandomNumberGenerator.GetBytes(32);
            string root = Path.Combine(dir.Path, "data");
            var store = new OmniTumblrStore(root, _ => { }, new OmniTumblrVault(key));
            store.Load();
            string blogId = store.Mutate(s =>
            {
                s.App.ConsumerKeyCipher = s.Vault.Protect("ck", "app:consumer-key");
                s.App.ConsumerSecretCipher = s.Vault.Protect("cs", "app:consumer-secret");
                s.MarkApp();
                var blog = new OmniTumblrBlog { Name = "memeblog", ConnectionId = "c1" };
                blog.UsedContentKeys.Add("ig:1");
                blog.Strategy.Slots.Add(new WeeklySlot(DayOfWeek.Monday, 600));
                s.Blogs[blog.BlogId] = blog;
                s.PostsOf(blog.BlogId).Add(new OmniTumblrPost { BlogId = blog.BlogId, Caption = "hello", ScheduledUtc = new DateTime(2026, 10, 9, 17, 0, 0, DateTimeKind.Utc) });
                OmniTumblrInsights.RecordSnapshot(s.InsightsOf(blog.BlogId), DateTime.UtcNow, 100, 5);
                s.MarkBlog(blog.BlogId);
                s.MarkPosts(blog.BlogId);
                s.MarkInsights(blog.BlogId);
                s.AddEvent(EventLevel.Success, "test", "an event", blog.BlogId);
                return blog.BlogId;
            });
            await store.FlushAsync();

            var reloaded = new OmniTumblrStore(root, _ => { }, new OmniTumblrVault(key));
            reloaded.Load();
            reloaded.Read(s =>
            {
                Assert.Equal("ck", s.Vault.Unprotect(s.App.ConsumerKeyCipher, "app:consumer-key"));
                var blog = s.Blog(blogId)!;
                Assert.Equal("memeblog", blog.Name);
                Assert.Contains("ig:1", blog.UsedContentKeys);
                Assert.Equal(2, blog.Strategy.Slots.Count);
                var post = Assert.Single(s.PostsOf(blogId));
                Assert.Equal("hello", post.Caption);
                Assert.Equal(DateTimeKind.Utc, post.ScheduledUtc.Kind);
                Assert.Equal(new DateTime(2026, 10, 9, 17, 0, 0, DateTimeKind.Utc), post.ScheduledUtc);
                Assert.Equal(100, s.InsightsOf(blogId).Snapshots.Single().Followers);
                Assert.Contains(s.Events, e => e.Kind == "test");
                return 0;
            });
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task ACorruptPostsFile_IsSetAside_AndTheBlogStillLoads()
        {
            using var dir = new TempDir();
            byte[] key = RandomNumberGenerator.GetBytes(32);
            string root = Path.Combine(dir.Path, "data");
            var store = new OmniTumblrStore(root, _ => { }, new OmniTumblrVault(key));
            store.Load();
            string blogId = store.Mutate(s =>
            {
                var blog = new OmniTumblrBlog { Name = "memeblog" };
                s.Blogs[blog.BlogId] = blog;
                s.PostsOf(blog.BlogId).Add(new OmniTumblrPost { BlogId = blog.BlogId });
                s.MarkBlog(blog.BlogId);
                s.MarkPosts(blog.BlogId);
                return blog.BlogId;
            });
            await store.FlushAsync();
            File.WriteAllText(Path.Combine(root, "blogs", blogId, "posts.json"), "{ this is not json");

            var logs = new List<string>();
            var reloaded = new OmniTumblrStore(root, logs.Add, new OmniTumblrVault(key));
            reloaded.Load();
            Assert.NotNull(reloaded.Read(s => s.Blog(blogId)));
            Assert.Empty(reloaded.Read(s => s.PostsOf(blogId).ToList()));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "blogs", blogId), "posts.json.corrupt-*"));
            Assert.Contains(logs, l => l.Contains("posts.json"));
        }

        [Fact]
        public void TheVault_BindsCiphertextToItsPurpose_AndRejectsTampering()
        {
            var vault = new OmniTumblrVault(RandomNumberGenerator.GetBytes(32));
            string cipher = vault.Protect("secret-token", "conn:a:token");
            Assert.Equal("secret-token", vault.Unprotect(cipher, "conn:a:token"));
            Assert.Null(vault.Unprotect(cipher, "conn:b:token"));
            char[] chars = cipher.ToCharArray();
            chars[^5] = chars[^5] == 'A' ? 'B' : 'A';
            Assert.Null(vault.Unprotect(new string(chars), "conn:a:token"));
            Assert.Null(vault.Unprotect("plaintext", "conn:a:token"));
            Assert.Null(new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)).Unprotect(cipher, "conn:a:token"));
        }

        [Fact]
        public void TheRealVault_CreatesItsKeyFileOnce()
        {
            using var dir = new TempDir();
            string keyPath = Path.Combine(dir.Path, "secrets.key");
            var vault = new OmniTumblrVault(keyPath, () => false, _ => { }, null);
            vault.EnsureReady();
            string cipher = vault.Protect("x", "p");
            Assert.Equal(32, new FileInfo(keyPath).Length);
            var again = new OmniTumblrVault(keyPath, () => true, _ => { }, null);
            Assert.Equal("x", again.Unprotect(cipher, "p"));
        }

        [Fact]
        public void Snapshots_StayHourlyForTwoWeeks_ThenDaily()
        {
            var insights = new BlogInsights();
            var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int hour = 0; hour < 24 * 60; hour++)
                OmniTumblrInsights.RecordSnapshot(insights, start.AddHours(hour), 1000 + hour, null);
            var now = start.AddHours(24 * 60 - 1);
            Assert.True(insights.Snapshots.Count < 24 * 15 + 60);
            Assert.True(insights.Snapshots.Count(s => s.Utc >= now.AddDays(-13)) >= 24 * 13);
            Assert.Equal(insights.Snapshots.Count, insights.Snapshots.Select(s => s.Utc).Distinct().Count());
            Assert.Equal(1000 + 24 * 60 - 1, insights.Snapshots.Last().Followers);
        }

        [Fact]
        public void ActivityItems_AreDedupedAndCountedPerDay()
        {
            var activity = new BlogActivity();
            var day = new DateTime(2026, 10, 7, 10, 0, 0, DateTimeKind.Utc);
            var items = new List<TumblrNotification>
            {
                new() { Id = "1", Type = "like", Utc = day },
                new() { Id = "2", Type = "reblog_naked", Utc = day.AddMinutes(1) },
                new() { Id = "3", Type = "follow", Utc = day.AddMinutes(2), FromBlog = "newfan" },
            };
            Assert.Equal(3, OmniTumblrInsights.MergeActivity(activity, items));
            Assert.Equal(0, OmniTumblrInsights.MergeActivity(activity, items));
            var counts = activity.Daily["2026-10-07"];
            Assert.Equal(1, counts["like"]);
            Assert.Equal(1, counts["reblog"]);
            Assert.Equal(1, counts["follow"]);
            Assert.Equal("3", activity.Recent.First().Id);
            Assert.Equal(day.AddMinutes(2), activity.NewestSeenUtc);
        }
    }

    public class OmniTumblrMigrationTests
    {
        [Fact]
        public void V1Accounts_AndPostedHistory_AreImported_WithAutopilotOff()
        {
            using var dir = new TempDir();
            string v1 = Path.Combine(dir.Path, "OmniTumblr");
            Directory.CreateDirectory(Path.Combine(v1, "Accounts"));
            Directory.CreateDirectory(Path.Combine(v1, "Posts"));
            File.WriteAllText(Path.Combine(v1, "Accounts", "memeblog.json"), new JObject
            {
                ["AccountId"] = "11112222",
                ["BlogName"] = "memeblog.tumblr.com",
                ["ConsumerKey"] = "old-key",
                ["ConsumerSecret"] = "old-secret",
                ["OAuthToken"] = "old-token",
                ["OAuthTokenSecret"] = "old-token-secret",
                ["IsPaused"] = false,
                ["FollowerCount"] = 250,
                ["ContentConfig"] = new JObject
                {
                    ["ContentSource"] = "MemeScraper",
                    ["CaptionMode"] = "AIGenerated",
                    ["AICaptionPrompt"] = "be unhinged but kind",
                    ["Tags"] = new JArray("memes", "funny"),
                    ["RotateTags"] = false,
                    ["PostsPerDay"] = 2,
                    ["PreferredPostHoursUTC"] = new JArray(9, 18),
                    ["ActiveDaysOfWeek"] = new JArray(1, 5),
                    ["MemeScraperNicheFilter"] = "gaming",
                },
            }.ToString());
            File.WriteAllText(Path.Combine(v1, "Posts", "1.json"), new JObject
            {
                ["PostId"] = "1", ["AccountId"] = "11112222", ["PostType"] = "Video", ["Status"] = "Posted",
                ["PostedTime"] = "2026-04-01T18:00:00Z", ["TumblrPostId"] = 123456789, ["Caption"] = "old caption",
                ["SourceType"] = "MemeScraper", ["SourceId"] = "reel-42",
            }.ToString());
            File.WriteAllText(Path.Combine(v1, "Posts", "2.json"), new JObject
            {
                ["PostId"] = "2", ["AccountId"] = "11112222", ["PostType"] = "Video", ["Status"] = "Queued",
                ["SourceType"] = "MemeScraper", ["SourceId"] = "reel-43",
            }.ToString());

            using var h = new Harness();
            string? summary = OmniTumblrMigration.MigrateV1(h.Store, v1, "https://klive.dev/omnitumblr/oauth/callback", h.Clock.Now, _ => { });

            Assert.NotNull(summary);
            Assert.Contains("1 blog(s)", summary);
            Assert.Equal(("old-key", "old-secret"), h.Auth.AppCredentials());
            var conn = h.Store.Read(s => OmniTumblrStore.Clone(s.Connections.Values.Single()));
            var blog = h.Store.Read(s => OmniTumblrStore.Clone(s.Blogs.Values.Single()));
            Assert.Equal("memeblog", blog.Name);
            Assert.Equal(conn.ConnectionId, blog.ConnectionId);
            Assert.False(blog.Autopilot);
            Assert.Equal(250, blog.Stats.Followers);
            Assert.Equal(CaptionMode.AI, blog.Strategy.CaptionMode);
            Assert.Equal("be unhinged but kind", blog.Strategy.Ai.Instructions);
            Assert.Equal(ContentSourceKind.MemeScraper, blog.Strategy.Source);
            Assert.Equal(new[] { "gaming" }, blog.Strategy.MemeScraper.Niches);
            Assert.Equal("UTC", blog.Strategy.TimeZone);
            Assert.Equal(4, blog.Strategy.Slots.Count); // Mon+Fri × 09:00+18:00
            Assert.Contains(blog.Strategy.Slots, s => s.Day == DayOfWeek.Friday && s.Minute == 18 * 60);
            Assert.Equal(2, blog.Strategy.MaxPostsPerDay);
            Assert.Contains("ig:reel-42", blog.UsedContentKeys);
            Assert.Contains("ig:reel-43", blog.UsedContentKeys);

            var post = Assert.Single(h.Posts(blog.BlogId));
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.Equal(PostOrigin.Imported, post.Origin);
            Assert.Equal("123456789", post.TumblrPostId);
            Assert.Equal(new DateTime(2026, 4, 1, 18, 0, 0, DateTimeKind.Utc), post.PublishedUtc);

            // The imported OAuth1 token works.
            var creds = h.Auth.GetCredentialsAsync(conn.ConnectionId, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(("old-token", "old-token-secret"), (creds.Token, creds.TokenSecret));

            // Runs once.
            Assert.Null(OmniTumblrMigration.MigrateV1(h.Store, v1, null, h.Clock.Now, _ => { }));
            Assert.Single(h.Store.Read(s => s.Blogs.Values.ToList()));
        }

        [Fact]
        public void NoV1Data_IsANoOp_ButRemembersTheLegacyCallback()
        {
            using var h = new Harness();
            Assert.Null(OmniTumblrMigration.MigrateV1(h.Store, Path.Combine(h.Dir.Path, "missing"), "https://example.com/cb", h.Clock.Now, _ => { }));
            Assert.Equal("https://example.com/cb", h.Store.Read(s => s.App.CallbackUrl));
            Assert.NotNull(h.Store.Read(s => s.Engine.MigratedV1Utc));
        }
    }

    public class OmniTumblrStrategyRulesTests
    {
        [Fact]
        public void Normalize_CleansListsAndClampsRanges()
        {
            var s = new OmniTumblrStrategy
            {
                Slots = new List<WeeklySlot> { new(DayOfWeek.Monday, 5000), new(DayOfWeek.Monday, 1439), new(DayOfWeek.Sunday, -3) },
                JitterMinutes = 999,
                FixedTags = new List<string> { "#Memes", "memes", " " },
                CaptionPool = new List<string> { " a ", "", "a" },
            };
            OmniTumblrStrategyRules.Normalize(s);
            Assert.Equal(2, s.Slots.Count);
            Assert.Equal(DayOfWeek.Sunday, s.Slots[0].Day);
            Assert.Equal(0, s.Slots[0].Minute);
            Assert.Equal(120, s.JitterMinutes);
            Assert.Equal(new[] { "Memes" }, s.FixedTags);
            Assert.Equal(new[] { "a", "a" }, s.CaptionPool);
        }

        [Fact]
        public void Validate_ExplainsWhatIsMissingForAutopilot()
        {
            var s = OmniTumblrStrategyRules.Preset("manual");
            var errors = OmniTumblrStrategyRules.Validate(s, autopilot: true);
            Assert.Contains(errors, e => e.Contains("time slot"));
            Assert.Contains(errors, e => e.Contains("content source"));
            Assert.Empty(OmniTumblrStrategyRules.Validate(OmniTumblrStrategyRules.Preset("weekly-memes"), autopilot: true));
            Assert.Equal(7, OmniTumblrStrategyRules.Preset("daily-memes").Slots.Count);

            var bad = OmniTumblrStrategy.CreateDefault();
            bad.TimeZone = "Not/AZone";
            bad.CaptionMode = CaptionMode.Rotate;
            var problems = OmniTumblrStrategyRules.Validate(bad, autopilot: false);
            Assert.Contains(problems, e => e.Contains("time zone"));
            Assert.Contains(problems, e => e.Contains("pool"));
        }
    }
}
