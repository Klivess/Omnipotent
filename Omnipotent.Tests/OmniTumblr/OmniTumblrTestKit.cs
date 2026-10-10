using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Tests.OmniTumblr
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnitumblr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name, int bytes = 2048)
        {
            string full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, RandomNumberGenerator.GetBytes(bytes));
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    internal sealed class TestClock
    {
        public TestClock(DateTime startUtc) => Now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        public DateTime Now { get; set; }
        public void Advance(TimeSpan by) => Now = Now + by;
        public Func<DateTime> Func => () => Now;
    }

    /// <summary>A scriptable Tumblr: records every call; each endpoint's behaviour is a replaceable delegate.</summary>
    internal sealed class FakeTumblrApi : ITumblrApi
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<(string Blog, NpfPostRequest Request)> Created { get; } = new();
        public List<TumblrPostSummary> PublishedPosts { get; } = new();
        public int CreateCalls;

        public Func<TumblrCredentials, TumblrUserInfo> UserInfo { get; set; } = _ => new TumblrUserInfo
        {
            Name = "klives",
            Blogs = new List<TumblrBlogRef>
            {
                new() { Name = "memeblog", Uuid = "t:meme", Title = "Meme Blog", Url = "https://memeblog.tumblr.com/", Followers = 120, Primary = true },
                new() { Name = "sideblog", Uuid = "t:side", Title = "Side Blog", Url = "https://sideblog.tumblr.com/", Followers = 7 },
            },
        };
        public Func<string, NpfPostRequest, Task<TumblrCreatedPost>>? OnCreate { get; set; }
        public Func<int, TumblrPostsPage>? OnGetPosts { get; set; }
        public Func<string, TumblrNotesSummary>? OnNotes { get; set; }
        public Func<long?, TumblrNotificationsPage>? OnNotifications { get; set; }
        public Dictionary<string, TumblrLimit> Limits { get; set; } = new();
        public Func<string, string, (string, string)> OnRequestToken { get; set; } = (key, secret) => ("req-token", "req-secret");
        public Func<string, (string, string)> OnAccessToken { get; set; } = verifier => ("access-token", "access-secret");
        public Func<string, TumblrOAuth2Token> OnCodeExchange { get; set; } = code => new TumblrOAuth2Token { AccessToken = "oauth2-access", RefreshToken = "oauth2-refresh-1", ExpiresIn = 2520, Scope = "basic write offline_access" };
        public Func<string, TumblrOAuth2Token> OnRefresh { get; set; } = refresh => new TumblrOAuth2Token { AccessToken = "oauth2-access-2", RefreshToken = "oauth2-refresh-2", ExpiresIn = 2520 };
        public List<TumblrCredentials> CredentialsSeen { get; } = new();

        public Task<TumblrUserInfo> GetUserInfoAsync(TumblrCredentials creds, CancellationToken ct)
        {
            Calls.Enqueue("user/info");
            CredentialsSeen.Add(creds);
            return Task.FromResult(UserInfo(creds));
        }

        public Task<Dictionary<string, TumblrLimit>> GetUserLimitsAsync(TumblrCredentials creds, CancellationToken ct)
        {
            Calls.Enqueue("user/limits");
            return Task.FromResult(Limits);
        }

        public Task<TumblrBlogInfo> GetBlogInfoAsync(TumblrCredentials creds, string blog, CancellationToken ct)
        {
            Calls.Enqueue("blog/info:" + blog);
            return Task.FromResult(new TumblrBlogInfo { Name = blog.StartsWith("t:") ? "memeblog" : blog, Uuid = "t:meme", Title = "Meme Blog", Posts = 42 });
        }

        public Task<long?> GetFollowerCountAsync(TumblrCredentials creds, string blog, CancellationToken ct)
        {
            Calls.Enqueue("blog/followers:" + blog);
            return Task.FromResult<long?>(130);
        }

        public Task<TumblrPostsPage> GetPostsAsync(TumblrCredentials creds, string blog, int offset, int limit, CancellationToken ct)
        {
            Calls.Enqueue($"blog/posts:{blog}:{offset}");
            if (OnGetPosts != null) return Task.FromResult(OnGetPosts(offset));
            lock (PublishedPosts)
                return Task.FromResult(new TumblrPostsPage
                {
                    Posts = PublishedPosts.OrderByDescending(p => p.PublishedUtc).Skip(offset).Take(limit).ToList(),
                    TotalPosts = PublishedPosts.Count,
                });
        }

        public Task<TumblrNotesSummary> GetNotesSummaryAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct)
        {
            Calls.Enqueue("blog/notes:" + postId);
            return Task.FromResult(OnNotes?.Invoke(postId) ?? new TumblrNotesSummary { TotalNotes = 10, TotalLikes = 6, TotalReblogs = 3 });
        }

        public Task<TumblrNotificationsPage> GetNotificationsAsync(TumblrCredentials creds, string blog, long? before, CancellationToken ct)
        {
            Calls.Enqueue("blog/notifications");
            return Task.FromResult(OnNotifications?.Invoke(before) ?? new TumblrNotificationsPage());
        }

        public async Task<TumblrCreatedPost> CreatePostAsync(TumblrCredentials creds, string blog, NpfPostRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref CreateCalls);
            Calls.Enqueue("blog/posts:create:" + blog);
            lock (Created) Created.Add((blog, request));
            if (OnCreate != null) return await OnCreate(blog, request);
            string id = (1000000000000L + CreateCalls).ToString();
            lock (PublishedPosts)
                PublishedPosts.Add(new TumblrPostSummary { Id = id, Slug = request.Slug, PublishedUtc = DateTime.UtcNow, Tags = (request.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() });
            return new TumblrCreatedPost { Id = id };
        }

        public Task DeletePostAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct)
        {
            Calls.Enqueue("blog/post:delete:" + postId);
            return Task.CompletedTask;
        }

        public Task<(string Token, string Secret)> GetOAuth1RequestTokenAsync(string consumerKey, string consumerSecret, string callbackUrl, CancellationToken ct)
        {
            Calls.Enqueue("oauth/request_token");
            return Task.FromResult(OnRequestToken(consumerKey, consumerSecret));
        }

        public Task<(string Token, string Secret)> GetOAuth1AccessTokenAsync(string consumerKey, string consumerSecret, string requestToken, string requestTokenSecret, string verifier, CancellationToken ct)
        {
            Calls.Enqueue($"oauth/access_token:{requestToken}:{requestTokenSecret}:{verifier}");
            return Task.FromResult(OnAccessToken(verifier));
        }

        public string BuildOAuth1AuthorizeUrl(string requestToken) => "https://www.tumblr.com/oauth/authorize?oauth_token=" + requestToken;

        public string BuildOAuth2AuthorizeUrl(string consumerKey, string state, string redirectUri) => $"https://www.tumblr.com/oauth2/authorize?state={state}";

        public Task<TumblrOAuth2Token> ExchangeOAuth2CodeAsync(string consumerKey, string consumerSecret, string code, string redirectUri, CancellationToken ct)
        {
            Calls.Enqueue("oauth2/code:" + code);
            return Task.FromResult(OnCodeExchange(code));
        }

        public Task<TumblrOAuth2Token> RefreshOAuth2TokenAsync(string consumerKey, string consumerSecret, string refreshToken, CancellationToken ct)
        {
            Calls.Enqueue("oauth2/refresh:" + refreshToken);
            return Task.FromResult(OnRefresh(refreshToken));
        }
    }

    internal sealed class FakeCaptionModel : ICaptionModel
    {
        public Queue<Func<string>> Replies { get; } = new();
        public List<(string System, string User, int Images)> Calls { get; } = new();
        public bool Images { get; set; } = true;
        public bool Available { get; set; } = true;
        public string Default { get; set; } = "{\"caption\": \"when the build finally passes\", \"tags\": [\"Programming\", \"#relatable\"], \"alt_text\": \"A cat at a keyboard.\"}";

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);
        public Task<bool> SupportsImagesAsync(string? model, CancellationToken ct) => Task.FromResult(Images);

        public Task<CaptionModelReply> CompleteAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> jpegImages, string? model, int maxTokens, CancellationToken ct)
        {
            lock (Calls) Calls.Add((systemPrompt, userPrompt, jpegImages.Count));
            string text = Replies.Count > 0 ? Replies.Dequeue()() : Default;
            return Task.FromResult(new CaptionModelReply { Text = text, Model = "test-model" });
        }
    }

    internal sealed class FakeReelCatalog : IReelCatalog
    {
        public List<ReelInfo> Reels { get; } = new();
        public Dictionary<string, IReadOnlyList<string>> Niches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsReady { get; set; } = true;
        public IReadOnlyList<ReelInfo> GetReels() => Reels;
        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetSourceNiches() => Niches;
    }

    /// <summary>The real engine components wired to fakes, on a temp directory and a controllable clock.</summary>
    internal sealed class Harness : IDisposable
    {
        public TempDir Dir { get; } = new();
        public TestClock Clock { get; }
        public FakeTumblrApi Api { get; } = new();
        public FakeCaptionModel Model { get; } = new();
        public FakeReelCatalog Catalog { get; } = new();
        public List<string> Alerts { get; } = new();
        public OmniTumblrStore Store { get; }
        public OmniTumblrAuth Auth { get; }
        public OmniTumblrMediaTools Media { get; }
        public OmniTumblrContentSources Content { get; }
        public OmniTumblrCaptioner Captioner { get; }
        public OmniTumblrPlanner Planner { get; }
        public OmniTumblrPublisher Publisher { get; }
        public OmniTumblrApiBudget Budget { get; }
        public OmniTumblrAnalyticsSync Sync { get; }
        /// <summary>Shared by the publisher and sync, as in production. The fake API does not report to it; tests do, as the real client would.</summary>
        public TumblrEdgeGate Gate { get; }
        public List<string> RefilledFor { get; } = new();

        public Harness(DateTime? startUtc = null)
        {
            Clock = new TestClock(startUtc ?? new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
            Store = new OmniTumblrStore(Path.Combine(Dir.Path, "data"), _ => { }, new OmniTumblrVault(RandomNumberGenerator.GetBytes(32)));
            Store.Load();
            Media = new OmniTumblrMediaTools(Path.Combine(Dir.Path, "no-ffmpeg-here"));
            Content = new OmniTumblrContentSources(Catalog, Media, Store.LibraryDirectory);
            Captioner = new OmniTumblrCaptioner(Model);
            Auth = new OmniTumblrAuth(Store, Api, Clock.Func);
            Planner = new OmniTumblrPlanner(Store, Content, Captioner, Media, Clock.Func);
            Gate = new TumblrEdgeGate(Clock.Func);
            Publisher = new OmniTumblrPublisher(Store, Api, Auth, Clock.Func, message => { lock (Alerts) Alerts.Add(message); return Task.CompletedTask; }, Gate)
            {
                ReconcileDelay = TimeSpan.Zero,
                OnContentFailure = postId => { lock (RefilledFor) RefilledFor.Add(postId); return Task.CompletedTask; },
            };
            Budget = new OmniTumblrApiBudget(Clock.Func);
            Sync = new OmniTumblrAnalyticsSync(Store, Api, Auth, Budget, Clock.Func, _ => { }, Gate);
        }

        /// <summary>What the real client does on a bare nginx 403: tell the gate, then throw.</summary>
        public TumblrApiException EdgeRefusal(string label = "blog/posts:create")
        {
            Gate.RecordRefused(label);
            return new TumblrApiException(TumblrErrorKind.EdgeBlocked, 403, null, $"{label} failed (403): 403 Forbidden (HTML page from Tumblr's nginx edge, not an API response)", label);
        }

        /// <summary>Configures the app and a healthy OAuth1 connection; returns the connection id.</summary>
        public string Connect(string userName = "klives")
        {
            return Store.Mutate(s =>
            {
                s.App.ConsumerKeyCipher = s.Vault.Protect("consumer-key", "app:consumer-key");
                s.App.ConsumerSecretCipher = s.Vault.Protect("consumer-secret", "app:consumer-secret");
                s.App.ConsumerKeyHint = "-key";
                var conn = new OmniTumblrConnection { UserName = userName, AuthMode = TumblrAuthMode.OAuth1, Health = ConnectionHealth.Healthy };
                conn.TokenCipher = s.Vault.Protect("token", $"conn:{conn.ConnectionId}:token");
                conn.TokenSecretCipher = s.Vault.Protect("token-secret", $"conn:{conn.ConnectionId}:token-secret");
                conn.Blogs = Api.UserInfo(new TumblrCredentials()).Blogs;
                s.Connections[conn.ConnectionId] = conn;
                s.MarkApp();
                s.MarkConnections();
                return conn.ConnectionId;
            });
        }

        public string AddBlog(string connectionId, Action<OmniTumblrBlog>? configure = null, string name = "memeblog")
        {
            return Store.Mutate(s =>
            {
                var blog = new OmniTumblrBlog { Name = name, Uuid = "t:" + name, ConnectionId = connectionId, Strategy = OmniTumblrStrategy.CreateDefault() };
                blog.Strategy.JitterMinutes = 0;
                configure?.Invoke(blog);
                s.Blogs[blog.BlogId] = blog;
                s.PostsOf(blog.BlogId);
                s.InsightsOf(blog.BlogId);
                s.MarkBlog(blog.BlogId);
                return blog.BlogId;
            });
        }

        public ReelInfo AddReel(string postId, string owner = "memesource", long views = 1000, DateTime? created = null)
        {
            var reel = new ReelInfo
            {
                PostId = postId,
                ShortCode = "sc" + postId,
                Owner = owner,
                Description = "original caption #fyp follow @memesource",
                Views = views,
                Likes = views / 10,
                CreatedUtc = created ?? Clock.Now.AddDays(-2),
                DownloadedUtc = Clock.Now.AddDays(-1),
                FilePath = Dir.File($"reels/{postId}.mp4"),
                Url = $"https://www.instagram.com/reel/sc{postId}/",
            };
            Catalog.Reels.Add(reel);
            return reel;
        }

        /// <summary>A Ready manual post due now with a real media file.</summary>
        public string AddReadyPost(string blogId, Action<OmniTumblrPost>? configure = null)
        {
            string media = Dir.File($"media/{Guid.NewGuid():N}.mp4");
            return Store.Mutate(s =>
            {
                var post = new OmniTumblrPost
                {
                    BlogId = blogId,
                    Status = PostStatus.Ready,
                    Origin = PostOrigin.Manual,
                    Kind = PostKind.Video,
                    ScheduledUtc = Clock.Now.AddMinutes(-1),
                    Approved = true,
                    Caption = "a caption",
                    Tags = new List<string> { "memes" },
                    Media = new List<PostMedia> { new() { Path = media, MimeType = "video/mp4", Bytes = 2048, Width = 720, Height = 1280, DurationSeconds = 12 } },
                };
                configure?.Invoke(post);
                s.PostsOf(blogId).Add(post);
                s.MarkPosts(blogId);
                return post.PostId;
            });
        }

        public OmniTumblrPost Post(string postId) => Store.Read(s => OmniTumblrStore.Clone(s.FindPost(postId)!));
        public OmniTumblrBlog Blog(string blogId) => Store.Read(s => OmniTumblrStore.Clone(s.Blog(blogId)!));
        public OmniTumblrConnection Connection(string id) => Store.Read(s => OmniTumblrStore.Clone(s.Connection(id)!));
        public List<OmniTumblrPost> Posts(string blogId) => Store.Read(s => s.PostsOf(blogId).Select(p => OmniTumblrStore.Clone(p)).ToList());

        public void Dispose()
        {
            try { Store.FlushAsync().GetAwaiter().GetResult(); } catch { }
            Dir.Dispose();
        }
    }
}
