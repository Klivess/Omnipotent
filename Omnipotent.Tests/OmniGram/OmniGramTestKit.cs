using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Tests.OmniGram
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnigram-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>A scriptable Instagram: records every call; each endpoint's behaviour is a replaceable delegate.</summary>
    internal sealed class FakeInstagramApi : IInstagramApi
    {
        private readonly TestClock clock;
        private int nextId = 1000;

        public FakeInstagramApi(TestClock clock) => this.clock = clock;

        public ConcurrentQueue<string> Calls { get; } = new();
        public List<(string Id, IgContainerRequest Request)> Containers { get; } = new();
        public Dictionary<string, Queue<IgContainerStatus>> StatusScript { get; } = new();
        public HashSet<string> PublishedContainers { get; } = new();
        public List<IgMediaItem> Media { get; } = new();
        public List<string> PublishCalls { get; } = new();
        public List<(string MediaId, string Message)> Comments { get; } = new();
        public Dictionary<string, IgProfile> Profiles { get; } = new();

        public Func<IgContainerRequest, string>? OnCreate { get; set; }
        /// <summary>Runs inside media_publish after the container is marked published; throw to simulate a lost answer.</summary>
        public Action<string>? AfterPublish { get; set; }
        public Func<string, IReadOnlyCollection<string>, IgInsightValues>? OnMediaInsights { get; set; }
        public Func<IReadOnlyCollection<string>, string?, IgInsightValues>? OnAccountTotals { get; set; }
        public Func<string, List<IgComment>>? OnComments { get; set; }
        public Func<string, string>? OnCreateComment { get; set; }
        public IgPublishingLimit Limit { get; set; } = new() { QuotaUsage = 0, QuotaTotal = 50, QuotaDurationSeconds = 86400 };
        public Func<string, IgShortLivedToken> OnExchangeCode { get; set; } = code => new IgShortLivedToken { AccessToken = "short-" + code, UserId = "1784", Permissions = InstagramGraphClient.Scopes.ToList() };
        public Func<string, IgLongLivedToken> OnLongLived { get; set; } = token => new IgLongLivedToken { AccessToken = "long-" + token + "-xxxxxxxxxxxxxxxx", ExpiresInSeconds = 5183944 };
        public Func<string, IgLongLivedToken> OnRefresh { get; set; } = token => new IgLongLivedToken { AccessToken = "refreshed-" + token, ExpiresInSeconds = 5183944 };

        public string BuildAuthorizeUrl(string appId, string redirectUri, string state, bool forceReauth) => $"https://www.instagram.com/oauth/authorize?client_id={appId}&state={state}";

        public Task<IgShortLivedToken> ExchangeCodeAsync(string appId, string appSecret, string redirectUri, string code, CancellationToken ct)
        {
            Calls.Enqueue("oauth/code:" + code);
            return Task.FromResult(OnExchangeCode(code));
        }

        public Task<IgLongLivedToken> ExchangeForLongLivedAsync(string appSecret, string shortLivedToken, CancellationToken ct)
        {
            Calls.Enqueue("oauth/exchange");
            return Task.FromResult(OnLongLived(shortLivedToken));
        }

        public Task<IgLongLivedToken> RefreshLongLivedAsync(string token, CancellationToken ct)
        {
            Calls.Enqueue("oauth/refresh");
            return Task.FromResult(OnRefresh(token));
        }

        public Func<string, IgProfile>? OnProfile { get; set; }

        public Task<IgProfile> GetProfileAsync(string token, CancellationToken ct)
        {
            Calls.Enqueue("me");
            if (OnProfile != null) return Task.FromResult(OnProfile(token));
            if (Profiles.TryGetValue(token, out var p)) return Task.FromResult(p);
            return Task.FromResult(new IgProfile { Id = "app-1784", UserId = "1784", Username = "memepage", Name = "Meme Page", AccountType = "MEDIA_CREATOR", FollowersCount = 1200, FollowsCount = 50, MediaCount = 30 });
        }

        public Task<string> CreateContainerAsync(string token, string igUserId, IgContainerRequest request, CancellationToken ct)
        {
            Calls.Enqueue("media/create:" + (request.MediaType ?? "IMAGE"));
            string id = OnCreate?.Invoke(request) ?? "c" + Interlocked.Increment(ref nextId);
            lock (Containers) Containers.Add((id, request));
            return Task.FromResult(id);
        }

        public Task<IgContainerStatus> GetContainerStatusAsync(string token, string containerId, CancellationToken ct)
        {
            Calls.Enqueue("container/status:" + containerId);
            if (PublishedContainers.Contains(containerId)) return Task.FromResult(new IgContainerStatus { Id = containerId, StatusCode = "PUBLISHED" });
            if (StatusScript.TryGetValue(containerId, out var queue) && queue.Count > 0)
                return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
            return Task.FromResult(new IgContainerStatus { Id = containerId, StatusCode = "FINISHED", Status = "Finished" });
        }

        public Task<string> PublishContainerAsync(string token, string igUserId, string containerId, CancellationToken ct)
        {
            Calls.Enqueue("media/publish:" + containerId);
            lock (PublishCalls) PublishCalls.Add(containerId);
            var request = Containers.First(c => c.Id == containerId).Request;
            string mediaId = "m" + Interlocked.Increment(ref nextId);
            PublishedContainers.Add(containerId);
            lock (Media)
                Media.Insert(0, new IgMediaItem
                {
                    Id = mediaId,
                    Caption = request.Caption,
                    MediaType = request.MediaType == "CAROUSEL" ? "CAROUSEL_ALBUM" : request.VideoUrl != null ? "VIDEO" : "IMAGE",
                    MediaProductType = request.MediaType == "REELS" ? "REELS" : request.MediaType == "STORIES" ? "STORY" : "FEED",
                    Permalink = $"https://www.instagram.com/p/{mediaId}/",
                    Shortcode = mediaId,
                    TimestampUtc = clock.Now,
                    LikeCount = 0,
                    CommentsCount = 0,
                });
            AfterPublish?.Invoke(containerId);
            return Task.FromResult(mediaId);
        }

        public Task<IgPublishingLimit> GetPublishingLimitAsync(string token, string igUserId, CancellationToken ct)
        {
            Calls.Enqueue("publishing-limit");
            return Task.FromResult(Limit);
        }

        public Task<IgMediaPage> GetMediaPageAsync(string token, string igUserId, string? after, int limit, CancellationToken ct)
        {
            Calls.Enqueue("media/list");
            lock (Media)
            {
                int skip = after == null ? 0 : int.Parse(after);
                var items = Media.Where(m => m.MediaProductType != "STORY").Skip(skip).Take(limit).ToList();
                string? next = skip + items.Count < Media.Count(m => m.MediaProductType != "STORY") ? (skip + items.Count).ToString() : null;
                return Task.FromResult(new IgMediaPage { Items = items, After = next });
            }
        }

        public Task<IgMediaItem> GetMediaItemAsync(string token, string mediaId, CancellationToken ct)
        {
            Calls.Enqueue("media/get:" + mediaId);
            lock (Media) return Task.FromResult(Media.FirstOrDefault(m => m.Id == mediaId) ?? throw new InstagramApiException(IgErrorKind.NotFound, 400, 100, null, "does not exist"));
        }

        public Task<IgInsightValues> GetMediaInsightsAsync(string token, string mediaId, IReadOnlyCollection<string> metrics, CancellationToken ct)
        {
            Calls.Enqueue("media/insights:" + mediaId);
            if (OnMediaInsights != null) return Task.FromResult(OnMediaInsights(mediaId, metrics));
            var v = new IgInsightValues();
            foreach (string m in metrics) v.Values[m] = 10;
            return Task.FromResult(v);
        }

        public Task DeleteMediaAsync(string token, string mediaId, CancellationToken ct)
        {
            Calls.Enqueue("media/delete:" + mediaId);
            lock (Media) Media.RemoveAll(m => m.Id == mediaId);
            return Task.CompletedTask;
        }

        public Task<IgInsightValues> GetAccountTotalsAsync(string token, string igUserId, IReadOnlyCollection<string> metrics, DateTime sinceUtc, DateTime untilUtc, string? breakdown, CancellationToken ct)
        {
            Calls.Enqueue($"account/insights:{breakdown}");
            if (OnAccountTotals != null) return Task.FromResult(OnAccountTotals(metrics, breakdown));
            var v = new IgInsightValues();
            foreach (string m in metrics) v.Values[m] = 100;
            return Task.FromResult(v);
        }

        public Task<IgInsightValues> GetFollowerDemographicsAsync(string token, string igUserId, string breakdown, CancellationToken ct)
        {
            Calls.Enqueue("account/demographics:" + breakdown);
            var v = new IgInsightValues();
            v.Breakdowns["follower_demographics"] = new Dictionary<string, double> { ["GB"] = 60, ["US"] = 40 };
            return Task.FromResult(v);
        }

        public Task<List<(DateTime EndTimeUtc, Dictionary<int, double> ByHour)>> GetOnlineFollowersAsync(string token, string igUserId, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct)
        {
            Calls.Enqueue("account/online-followers");
            var hours = Enumerable.Range(0, 24).ToDictionary(h => h, h => (double)(h == 17 ? 500 : 100));
            return Task.FromResult(new List<(DateTime, Dictionary<int, double>)> { (clock.Now, hours) });
        }

        public Task<List<IgComment>> GetCommentsAsync(string token, string mediaId, int limit, CancellationToken ct)
        {
            Calls.Enqueue("media/comments:" + mediaId);
            return Task.FromResult(OnComments?.Invoke(mediaId) ?? new List<IgComment>());
        }

        public Task<string> CreateCommentAsync(string token, string mediaId, string message, CancellationToken ct)
        {
            Calls.Enqueue("comment/create:" + mediaId);
            lock (Comments) Comments.Add((mediaId, message));
            return Task.FromResult(OnCreateComment?.Invoke(mediaId) ?? "cm" + Interlocked.Increment(ref nextId));
        }

        public Task<string> ReplyToCommentAsync(string token, string commentId, string message, CancellationToken ct)
        {
            Calls.Enqueue("comment/reply:" + commentId);
            return Task.FromResult("r" + Interlocked.Increment(ref nextId));
        }

        public Task SetCommentHiddenAsync(string token, string commentId, bool hidden, CancellationToken ct)
        {
            Calls.Enqueue("comment/hide:" + commentId);
            return Task.CompletedTask;
        }

        public Task DeleteCommentAsync(string token, string commentId, CancellationToken ct)
        {
            Calls.Enqueue("comment/delete:" + commentId);
            return Task.CompletedTask;
        }

        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal sealed class FakeCaptionModel : ICaptionModel
    {
        public Queue<Func<string>> Replies { get; } = new();
        public List<(string System, string User, int Images)> Calls { get; } = new();
        public bool Images { get; set; } = true;
        public bool Available { get; set; } = true;
        public string Default { get; set; } = "{\"caption\": \"when the build finally passes\", \"hashtags\": [\"Programming\", \"#relatable\"], \"alt_text\": \"A cat at a keyboard.\"}";

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
        public FakeInstagramApi Api { get; }
        public FakeCaptionModel Model { get; } = new();
        public FakeReelCatalog Catalog { get; } = new();
        public List<string> Alerts { get; } = new();
        public OmniGramStore Store { get; }
        public OmniGramAuth Auth { get; }
        public OmniGramMediaTools Media { get; }
        public OmniGramMediaHost Host { get; }
        public OmniGramContentSources Content { get; }
        public OmniGramCaptioner Captioner { get; }
        public OmniGramPlanner Planner { get; }
        public OmniGramPublisher Publisher { get; }
        public OmniGramApiBudget Budget { get; }
        public OmniGramAnalyticsSync Sync { get; }
        public List<string> RefilledFor { get; } = new();
        public List<string> PublishedPosts { get; } = new();

        /// <param name="media">Real media tools (e.g. pointing at a real FFmpeg); default simulates a server without FFmpeg.</param>
        public Harness(DateTime? startUtc = null, OmniGramMediaTools? media = null)
        {
            Clock = new TestClock(startUtc ?? new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
            Api = new FakeInstagramApi(Clock);
            Store = new OmniGramStore(Path.Combine(Dir.Path, "data"), _ => { }, new OmniGramVault(RandomNumberGenerator.GetBytes(32)));
            Store.Load();
            Media = media ?? new OmniGramMediaTools(Path.Combine(Dir.Path, "no-ffmpeg", "ffmpeg.exe"), Path.Combine(Dir.Path, "no-ffmpeg", "ffprobe.exe"));
            Host = new OmniGramMediaHost(Store, Clock.Func) { BaseUrl = "https://klive.test" };
            Content = new OmniGramContentSources(Catalog, Media, Store.LibraryDirectory);
            Captioner = new OmniGramCaptioner(Model);
            Auth = new OmniGramAuth(Store, Api, Clock.Func);
            Planner = new OmniGramPlanner(Store, Content, Captioner, Media, Clock.Func)
            {
                OnContentRejected = postId => { lock (RefilledFor) RefilledFor.Add(postId); return Task.CompletedTask; },
            };
            Publisher = new OmniGramPublisher(Store, Api, Auth, Host, Clock.Func, message => { lock (Alerts) Alerts.Add(message); return Task.CompletedTask; })
            {
                OnContentFailure = postId => { lock (RefilledFor) RefilledFor.Add(postId); return Task.CompletedTask; },
                OnPublished = postId => { lock (PublishedPosts) PublishedPosts.Add(postId); return Task.CompletedTask; },
            };
            Budget = new OmniGramApiBudget(Clock.Func);
            Sync = new OmniGramAnalyticsSync(Store, Api, Auth, Budget, Host, Clock.Func, _ => { });
        }

        /// <summary>A connected, healthy account; returns its id.</summary>
        public string ConnectAccount(string username = "memepage", string igUserId = "1784", Action<OmniGramAccount>? configure = null)
        {
            return Store.Mutate(s =>
            {
                var a = new OmniGramAccount
                {
                    Username = username,
                    IgUserId = igUserId,
                    Connection = ConnectionState.Healthy,
                    TokenIssuedUtc = Clock.Now,
                    TokenExpiresUtc = Clock.Now.AddDays(60),
                    ConnectedVia = "token",
                    Strategy = OmniGramStrategy.CreateDefault(),
                };
                a.TokenCipher = s.Vault.Protect("token-" + username, OmniGramAuth.TokenPurpose(a.AccountId));
                a.Strategy.JitterMinutes = 0;
                a.Stats.Followers = 1000;
                configure?.Invoke(a);
                s.Accounts[a.AccountId] = a;
                s.PostsOf(a.AccountId);
                s.InsightsOf(a.AccountId);
                s.MarkAccount(a.AccountId);
                return a.AccountId;
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

        /// <summary>A Ready manual post due now, with a real (prepared) media file.</summary>
        public string AddReadyPost(string accountId, Action<OmniGramPost>? configure = null, PostFormat format = PostFormat.Reel)
        {
            bool video = format is PostFormat.Reel or PostFormat.Story;
            string media = Dir.File($"media/{Guid.NewGuid():N}{(video ? ".mp4" : ".jpg")}");
            return Store.Mutate(s =>
            {
                var post = new OmniGramPost
                {
                    AccountId = accountId,
                    Status = PostStatus.Ready,
                    Origin = PostOrigin.Manual,
                    Format = format,
                    ScheduledUtc = Clock.Now.AddMinutes(-1),
                    Approved = true,
                    Caption = "a caption",
                    Hashtags = new List<string> { "memes" },
                };
                string prepared = Path.Combine(Store.PreparedDirectory, $"{post.PostId}-0{(video ? ".mp4" : ".jpg")}");
                File.Copy(media, prepared, overwrite: true);
                post.Media = new List<PostMedia>
                {
                    new()
                    {
                        Path = media, MimeType = video ? "video/mp4" : "image/jpeg", Bytes = 2048, Width = 720, Height = 1280, DurationSeconds = video ? 12 : null,
                        PreparedPath = prepared, PreparedMimeType = video ? "video/mp4" : "image/jpeg", PreparedFor = format, PrepareNotes = "remuxed (faststart, no edit lists)",
                    },
                };
                configure?.Invoke(post);
                s.PostsOf(accountId).Add(post);
                s.MarkPosts(accountId);
                return post.PostId;
            });
        }

        /// <summary>Runs publishing passes until nothing is left to check (advancing the clock past each wait).</summary>
        public async Task<int> PublishUntilSettledAsync(int maxPasses = 12)
        {
            int published = 0;
            for (int i = 0; i < maxPasses; i++)
            {
                published += await Publisher.PublishDueAsync(CancellationToken.None);
                var next = Store.Read(s => s.AllPosts().Where(p => p.Status == PostStatus.Publishing).Select(p => p.Publish.NextCheckUtc).Where(t => t != null).Min());
                if (next == null) break;
                if (next > Clock.Now) Clock.Now = next.Value;
            }
            return published;
        }

        public OmniGramPost Post(string postId) => Store.Read(s => OmniGramStore.Clone(s.FindPost(postId)!));
        public OmniGramAccount Account(string accountId) => Store.Read(s => OmniGramStore.Clone(s.Account(accountId)!));
        public List<OmniGramPost> Posts(string accountId) => Store.Read(s => s.PostsOf(accountId).Select(p => OmniGramStore.Clone(p)).ToList());

        public void Dispose()
        {
            try { Store.FlushAsync().GetAwaiter().GetResult(); } catch { }
            Dir.Dispose();
        }
    }
}
