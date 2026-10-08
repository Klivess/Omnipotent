using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;
using KliveApi = Omnipotent.Services.KliveAPI.KliveAPI;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniTumblr
{
    /// <summary>
    /// The /omnitumblr/* HTTP surface. Registered against the typed KliveAPI (the reflection path can fail
    /// silently and leave a partial route table). Every GET is marked uncacheable: the response cache is
    /// dependency-versioned and OmniTumblr's state lives in memory, so a cached copy could be stale.
    /// Errors always come back as JSON {"error": "..."}.
    /// </summary>
    internal sealed class OmniTumblrRoutes
    {
        public const long MaxUploadBytes = 500L * 1024 * 1024;
        private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);
        private static readonly JsonSerializerSettings Json = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Converters = { new StringEnumConverter() },
        };

        private readonly OmniTumblr service;
        private KliveApi? api;

        public OmniTumblrRoutes(OmniTumblr service, KliveApi? api)
        {
            this.service = service;
            this.api = api;
        }

        private sealed class RouteException : Exception
        {
            public RouteException(HttpStatusCode code, string message) : base(message) => Code = code;
            public HttpStatusCode Code { get; }
        }

        private static RouteException Bad(string message) => new(HttpStatusCode.BadRequest, message);
        private static RouteException NotFound(string message) => new(HttpStatusCode.NotFound, message);
        private static RouteException Conflict(string message) => new(HttpStatusCode.Conflict, message);

        // ─────────────────────────────── Registration ───────────────────────────────

        public async Task RegisterAsync()
        {
            var get = HttpMethod.Get;
            var post = HttpMethod.Post;
            var routes = new List<(string Path, Func<UserRequest, Task> Handler, HttpMethod Method, KMPermissions Permission)>
            {
                ("/omnitumblr/overview", Overview, get, KMPermissions.Guest),
                ("/omnitumblr/dashboard-stats", DashboardStats, get, KMPermissions.Guest),
                ("/omnitumblr/blog", BlogDetail, get, KMPermissions.Guest),
                ("/omnitumblr/analytics", Analytics, get, KMPermissions.Guest),
                ("/omnitumblr/posts", Posts, get, KMPermissions.Guest),
                ("/omnitumblr/post", PostDetail, get, KMPermissions.Guest),
                ("/omnitumblr/media", Media, get, KMPermissions.Guest),
                ("/omnitumblr/content/thumb", ContentThumb, get, KMPermissions.Guest),
                ("/omnitumblr/library", Library, get, KMPermissions.Guest),
                ("/omnitumblr/memescraper/options", MemeScraperOptions, get, KMPermissions.Guest),
                ("/omnitumblr/events", Events, get, KMPermissions.Guest),
                ("/omnitumblr/settings", Settings, get, KMPermissions.Admin),
                ("/omnitumblr/connect/status", ConnectStatus, get, KMPermissions.Admin),

                ("/omnitumblr/settings/app", SaveApp, post, KMPermissions.Admin),
                ("/omnitumblr/connect/begin", ConnectBegin, post, KMPermissions.Admin),
                ("/omnitumblr/connections/refresh", ConnectionRefresh, post, KMPermissions.Admin),
                ("/omnitumblr/connections/remove", ConnectionRemove, post, KMPermissions.Admin),
                ("/omnitumblr/blogs/add", BlogsAdd, post, KMPermissions.Admin),
                ("/omnitumblr/blogs/update", BlogsUpdate, post, KMPermissions.Admin),
                ("/omnitumblr/blogs/remove", BlogsRemove, post, KMPermissions.Admin),
                ("/omnitumblr/blogs/refresh", BlogsRefresh, post, KMPermissions.Admin),
                ("/omnitumblr/blogs/plan-now", BlogsPlanNow, post, KMPermissions.Admin),
                ("/omnitumblr/posts/create", PostsCreate, post, KMPermissions.Admin),
                ("/omnitumblr/posts/update", PostsUpdate, post, KMPermissions.Admin),
                ("/omnitumblr/posts/approve", PostsApprove, post, KMPermissions.Admin),
                ("/omnitumblr/posts/publish-now", PostsPublishNow, post, KMPermissions.Admin),
                ("/omnitumblr/posts/regenerate-caption", PostsRegenerateCaption, post, KMPermissions.Admin),
                ("/omnitumblr/posts/swap-content", PostsSwapContent, post, KMPermissions.Admin),
                ("/omnitumblr/posts/skip", PostsSkip, post, KMPermissions.Admin),
                ("/omnitumblr/posts/cancel", PostsCancel, post, KMPermissions.Admin),
                ("/omnitumblr/posts/retry", PostsRetry, post, KMPermissions.Admin),
                ("/omnitumblr/posts/delete-remote", PostsDeleteRemote, post, KMPermissions.Admin),
                ("/omnitumblr/library/delete", LibraryDelete, post, KMPermissions.Admin),
                ("/omnitumblr/captions/preview", CaptionPreview, post, KMPermissions.Admin),
            };

            int registered = 0;
            foreach (var (path, handler, method, permission) in routes)
            {
                try
                {
                    var target = await ResolveApiAsync();
                    await target.CreateRoute(path, Guard(path, handler, method == HttpMethod.Get), method, permission);
                    registered++;
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] failed to register route {path}");
                }
            }

            try
            {
                var target = await ResolveApiAsync();
                // Tumblr redirects the browser here; no KM login is involved, so it is open to anybody and
                // only completes flows this server started.
                await target.CreateRoute("/omnitumblr/oauth/callback", Guard("/omnitumblr/oauth/callback", OAuthCallback, true), HttpMethod.Get, KMPermissions.Anybody);
                await target.CreateStreamingRoute("/omnitumblr/media/upload", Guard("/omnitumblr/media/upload", MediaUpload, false), HttpMethod.Post, KMPermissions.Admin, MaxUploadBytes);
                registered += 2;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniTumblr] failed to register the OAuth callback / upload routes");
            }
            await service.ServiceLog($"[OmniTumblr] {registered}/{routes.Count + 2} HTTP routes registered.", false);
        }

        private async Task<KliveApi> ResolveApiAsync()
        {
            if (api != null) return api;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while ((api = service.FindService<KliveApi>()) == null)
            {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("KliveAPI did not appear within 90 s.");
                await Task.Delay(100);
            }
            return api;
        }

        private Func<UserRequest, Task> Guard(string path, Func<UserRequest, Task> handler, bool isGet) => async req =>
        {
            try
            {
                if (isGet) CacheDeps.MarkUncacheable("omnitumblr live state");
                if (!service.Ready.IsCompleted) await Task.WhenAny(service.Ready, Task.Delay(ReadyWait));
                if (!service.Ready.IsCompletedSuccessfully)
                {
                    string why = service.Ready.IsFaulted
                        ? "OmniTumblr failed to start: " + service.Ready.Exception?.GetBaseException().Message
                        : "OmniTumblr is still starting (" + service.StartupState + "); try again in a moment.";
                    await Respond(req, new { error = why }, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await handler(req);
            }
            catch (RouteException ex)
            {
                await Respond(req, new { error = ex.Message }, ex.Code);
            }
            catch (KliveApi.RequestBodyTooLargeException)
            {
                throw; // the pipeline answers 413 itself
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniTumblr] {path} failed");
                await Respond(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
            }
        };

        // ─────────────────────────────── Helpers ───────────────────────────────

        private OmniTumblrStore Store => service.Store!;

        private static Task Respond(UserRequest req, object body, HttpStatusCode code = HttpStatusCode.OK) =>
            req.ReturnResponse(JsonConvert.SerializeObject(body, Json), "application/json", code: code);

        private static JObject Body(UserRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.userMessageContent)) return new JObject();
            try { return JToken.Parse(req.userMessageContent) as JObject ?? throw Bad("The request body must be a JSON object."); }
            catch (JsonException) { throw Bad("The request body is not valid JSON."); }
        }

        private static string Required(JObject body, string name) =>
            body[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)body[name]) ? ((string)body[name]!).Trim() : throw Bad($"'{name}' is required.");

        private static string RequiredQuery(UserRequest req, string name) =>
            req.userParameters?[name] is string v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : throw Bad($"'{name}' is required.");

        private static string? Query(UserRequest req, string name) =>
            req.userParameters?[name] is string v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        private static bool? OptBool(JObject body, string name) => body[name] == null || body[name]!.Type == JTokenType.Null ? null : TumblrApiClient.Bool(body[name]);

        private static string? OptString(JObject body, string name) => body[name] == null || body[name]!.Type == JTokenType.Null ? null : body[name]!.ToString();

        private static DateTime? OptDate(JObject body, string name)
        {
            var t = body[name];
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Date) return OmniTumblrScheduleMath.AsUtc(t.Value<DateTime>());
            string s = t.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? d.UtcDateTime
                : throw Bad($"'{name}' is not a valid date/time.");
        }

        private static List<string> OptStringList(JObject body, string name) =>
            body[name] is JArray arr ? arr.Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() : new List<string>();

        private static object AppDto(OmniTumblrAppConfig app) => new
        {
            Configured = app.IsConfigured,
            KeyHint = app.ConsumerKeyHint,
            app.CallbackUrl,
            AuthMode = app.PreferredAuthMode,
            app.VerifiedUtc,
            app.LastVerifyError,
            app.UpdatedUtc,
        };

        private static object ConnectionDto(OmniTumblrState s, OmniTumblrConnection c) => new
        {
            c.ConnectionId,
            c.UserName,
            c.AuthMode,
            c.Health,
            c.HealthDetail,
            c.HealthChangedUtc,
            c.LastVerifiedUtc,
            c.CreatedUtc,
            c.AccessTokenExpiresUtc,
            c.LimitsFetchedUtc,
            Limits = c.Limits.Select(kv => new { Key = kv.Key, kv.Value.Description, kv.Value.Limit, kv.Value.Remaining, kv.Value.ResetUtc }).OrderBy(l => l.Key).ToList(),
            Blogs = c.Blogs.Select(b =>
            {
                var managed = s.Blogs.Values.FirstOrDefault(m => (b.Uuid != null && m.Uuid == b.Uuid) || string.Equals(m.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    b.Name, b.Uuid, b.Title, b.Url, b.Followers, b.Primary, b.Type,
                    AvatarUrl = TumblrApiClient.BuildAvatarUrl(b.Name, 64),
                    Managed = managed != null,
                    ManagedBlogId = managed?.BlogId,
                };
            }).ToList(),
            ManagedBlogs = s.Blogs.Values.Count(b => b.ConnectionId == c.ConnectionId),
        };

        private object EngineDto(OmniTumblrState s, bool aiAvailable)
        {
            var budget = service.ApiBudget!;
            var headers = budget.LastHeaders;
            return new
            {
                service.Enabled,
                Running = service.Engine?.Running ?? false,
                service.PublishingEnabled,
                service.StartupState,
                service.StartedUtc,
                Loops = service.Engine?.Statuses ?? Array.Empty<EngineLoopStatus>(),
                Api = new
                {
                    CallsThisHour = budget.CallsThisHour(),
                    CallsToday = budget.CallsToday(),
                    HourlyLimit = OmniTumblrApiBudget.ConsumerHourlyLimit,
                    DailyLimit = OmniTumblrApiBudget.ConsumerDailyLimit,
                    PerHourRemaining = headers?.PerHourRemaining,
                    PerDayRemaining = headers?.PerDayRemaining,
                    HeadersObservedUtc = headers?.ObservedUtc,
                    ByEndpointToday = budget.CallsByEndpointToday(),
                },
                MemeScraperReady = service.ContentSources?.CatalogReady ?? false,
                AiAvailable = aiAvailable,
                Migration = s.Engine.MigrationSummary,
                SyncErrors = service.Sync?.LastErrors.Select(kv => new { Job = kv.Key, Error = kv.Value }).ToList(),
            };
        }

        private async Task<bool> AiAvailableAsync()
        {
            try { return await service.Captioner!.IsAvailableAsync(CancellationToken.None); }
            catch { return false; }
        }

        private static object EventDto(OmniTumblrState s, OmniTumblrEvent e) => new
        {
            e.Utc, e.Level, e.Kind, e.Message, e.BlogId, e.PostId,
            BlogName = e.BlogId == null ? null : s.Blog(e.BlogId)?.Name,
        };

        private static object CandidateDto(ContentCandidate c) => new
        {
            c.Key, Source = c.Source, Kind = c.Kind, c.Origin, c.OriginalUrl, c.Views, c.Likes, c.CreatedUtc,
            OriginalCaption = c.OriginalCaption is { Length: > 300 } text ? text[..300] + "…" : c.OriginalCaption,
            c.DurationSeconds,
            FileName = Path.GetFileName(c.FilePath),
        };

        // ─────────────────────────────── Overview & dashboards ───────────────────────────────

        private async Task Overview(UserRequest req)
        {
            bool ai = await AiAvailableAsync();
            bool reels = service.ContentSources?.CatalogReady ?? false;
            DateTime now = DateTime.UtcNow;
            var dto = Store.Read(s => new
            {
                NowUtc = now,
                Engine = EngineDto(s, ai),
                App = AppDto(s.App),
                Connections = s.Connections.Values.OrderBy(c => c.UserName).Select(c => ConnectionDto(s, c)).ToList(),
                Kpis = OmniTumblrViews.FleetKpis(s, now),
                Blogs = s.Blogs.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(b => OmniTumblrViews.BlogSummary(s, b, now)).ToList(),
                Upcoming = s.AllPosts().Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).Take(16).Select(p => OmniTumblrViews.Summary(p, s.Blog(p.BlogId))).ToList(),
                RecentlyPublished = s.AllPosts().Where(p => p.Status == PostStatus.Published).OrderByDescending(p => p.PublishedUtc).Take(12)
                    .Select(p => OmniTumblrViews.Summary(p, s.Blog(p.BlogId))).ToList(),
                Attention = OmniTumblrViews.Attention(s, now, service.PublishingEnabled, ai, reels),
                Events = s.Events.AsEnumerable().Reverse().Take(40).Select(e => EventDto(s, e)).ToList(),
            });
            await Respond(req, dto);
        }

        /// <summary>The home dashboard tile and the Schemes card (keeps v1's field names, adds v2's).</summary>
        private async Task DashboardStats(UserRequest req)
        {
            DateTime now = DateTime.UtcNow;
            var dto = Store.Read(s =>
            {
                var k = OmniTumblrViews.FleetKpis(s, now);
                return new
                {
                    TotalAccounts = s.Blogs.Count,
                    ActiveAccounts = s.Blogs.Values.Count(b => !b.Paused),
                    PausedAccounts = s.Blogs.Values.Count(b => b.Paused),
                    AutopilotBlogs = k.AutopilotBlogs,
                    PendingCount = k.Pending,
                    FailedCount = k.Failed7d,
                    SuccessRate = k.SuccessRate30d ?? 100,
                    TotalFollowers = k.Followers,
                    FollowerGain7d = k.FollowersDelta7d,
                    PostsThisWeek = k.Published7d,
                    PostsToday = s.AllPosts().Count(p => p.Status == PostStatus.Published && p.PublishedUtc >= now.Date),
                    AvgNotes30d = k.AvgNotes30d,
                    NextPostUtc = k.NextPostUtc,
                    NextPostBlog = k.NextPostBlog,
                    AttentionCount = OmniTumblrViews.Attention(s, now, service.PublishingEnabled, true, true).Count(a => a.Level != "info"),
                };
            });
            await Respond(req, dto);
        }

        private async Task BlogDetail(UserRequest req)
        {
            string blogId = RequiredQuery(req, "blogId");
            DateTime now = DateTime.UtcNow;
            var data = Store.Read(s =>
            {
                var b = s.Blog(blogId) ?? throw NotFound("No such blog.");
                var posts = s.PostsOf(blogId);
                var insights = s.InsightsOf(blogId);
                var conn = s.Connection(b.ConnectionId);
                return new
                {
                    Strategy = OmniTumblrStore.Clone(b.Strategy),
                    Excluded = OmniTumblrPlanner.ExcludedKeys(s, b, b.Strategy) as IReadOnlySet<string>,
                    Dto = new
                    {
                        NowUtc = now,
                        Summary = OmniTumblrViews.BlogSummary(s, b, now),
                        Blog = new
                        {
                            b.BlogId, b.Name, b.Uuid, b.Title, b.Description, b.Url, b.ConnectionId, b.Autopilot, b.Paused,
                            b.RequireApproval, b.Notes, b.AddedUtc, b.Stats, b.Health,
                            Strategy = b.Strategy,
                            UsedContentCount = b.UsedContentKeys.Count,
                            RejectedContentCount = b.RejectedContentKeys.Count,
                        },
                        Connection = conn == null ? null : ConnectionDto(s, conn),
                        Pending = posts.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).Select(p => OmniTumblrViews.Summary(p, b)).ToList(),
                        History = posts.Where(p => p.IsTerminal).OrderByDescending(p => p.PublishedUtc ?? p.UpdatedUtc).Take(80)
                            .Select(p => OmniTumblrViews.Summary(p, b)).ToList(),
                        Events = s.Events.Where(e => e.BlogId == blogId).Reverse().Take(80).Select(e => EventDto(s, e)).ToList(),
                        Activity = insights.Activity.Recent.Take(80).ToList(),
                    },
                };
            });

            object runway;
            try
            {
                var (eligible, next) = service.ContentSources!.Runway(data.Strategy, blogId, data.Excluded, now, 12);
                int perWeek = OmniTumblrScheduleMath.SlotsPerWeek(data.Strategy);
                runway = new
                {
                    Eligible = eligible,
                    SlotsPerWeek = perWeek,
                    WeeksOfContent = perWeek > 0 ? Math.Round(eligible / (double)perWeek, 1) : (double?)null,
                    Next = next.Select(CandidateDto).ToList(),
                    Source = data.Strategy.Source,
                };
            }
            catch (Exception ex)
            {
                runway = new { Error = ex.Message };
            }
            await Respond(req, new { data.Dto.NowUtc, data.Dto.Summary, data.Dto.Blog, data.Dto.Connection, data.Dto.Pending, data.Dto.History, data.Dto.Events, data.Dto.Activity, Runway = runway });
        }

        private async Task Analytics(UserRequest req)
        {
            string? blogId = Query(req, "blogId");
            int days = int.TryParse(Query(req, "days"), out var d) ? Math.Clamp(d, 1, 730) : 30;
            DateTime now = DateTime.UtcNow;
            var dto = Store.Read(s =>
            {
                if (blogId != null && s.Blog(blogId) == null) throw NotFound("No such blog.");
                return OmniTumblrViews.Analytics(s, blogId, days, now);
            });
            await Respond(req, dto);
        }

        private async Task Posts(UserRequest req)
        {
            string? blogId = Query(req, "blogId");
            string? status = Query(req, "status");
            string? q = Query(req, "q");
            int offset = Math.Max(0, int.TryParse(Query(req, "offset"), out var o) ? o : 0);
            int limit = Math.Clamp(int.TryParse(Query(req, "limit"), out var l) ? l : 50, 1, 200);
            var dto = Store.Read(s =>
            {
                var all = (blogId == null ? s.AllPosts() : s.PostsOf(blogId)).ToList();
                IEnumerable<OmniTumblrPost> filtered = all;
                if (status != null)
                {
                    var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => Enum.TryParse<PostStatus>(x, true, out var st) ? st : (PostStatus?)null)
                        .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
                    if (status.Equals("pending", StringComparison.OrdinalIgnoreCase)) filtered = filtered.Where(p => p.IsPending);
                    else if (wanted.Count > 0) filtered = filtered.Where(p => wanted.Contains(p.Status));
                }
                if (q != null)
                    filtered = filtered.Where(p => (p.Caption ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                        || p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
                        || (p.Content?.Origin ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
                var list = filtered.OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc).ToList();
                return new
                {
                    Total = all.Count,
                    Filtered = list.Count,
                    Offset = offset,
                    Rows = list.Skip(offset).Take(limit).Select(p => OmniTumblrViews.Summary(p, s.Blog(p.BlogId))).ToList(),
                };
            });
            await Respond(req, dto);
        }

        private async Task PostDetail(UserRequest req)
        {
            string postId = RequiredQuery(req, "postId");
            var dto = Store.Read(s =>
            {
                var p = s.FindPost(postId) ?? throw NotFound("No such post.");
                return new
                {
                    Post = OmniTumblrViews.Summary(p, s.Blog(p.BlogId)),
                    Media = p.Media.Select((m, i) => new { Index = i, m.MimeType, m.Bytes, m.Width, m.Height, m.DurationSeconds, HasThumbnail = m.ThumbnailPath != null, Exists = File.Exists(m.Path), FileName = Path.GetFileName(m.Path) }).ToList(),
                    p.AttemptLog,
                    MetricsHistory = p.Metrics.History,
                    p.CaptionInfo,
                    p.Content,
                    p.Slug,
                    p.Deferrals,
                    p.NeedsReconcile,
                    p.ManualOverride,
                    p.CreatedUtc,
                    p.UpdatedUtc,
                };
            });
            await Respond(req, dto);
        }

        private async Task Media(UserRequest req)
        {
            string postId = RequiredQuery(req, "postId");
            int index = int.TryParse(Query(req, "index"), out var i) ? i : 0;
            bool full = Query(req, "variant") == "full";
            var media = Store.Read(s =>
            {
                var p = s.FindPost(postId) ?? throw NotFound("No such post.");
                return index >= 0 && index < p.Media.Count ? OmniTumblrStore.Clone(p.Media[index]) : throw NotFound("No such media.");
            });
            string? path = full ? media.Path : media.ThumbnailPath;
            if (path == null || !File.Exists(path)) throw NotFound(full ? "The media file is missing." : "No thumbnail yet.");
            var info = new FileInfo(path);
            if (info.Length > 200L * 1024 * 1024) throw Bad("The file is too large to preview.");
            string type = full ? media.MimeType : OmniTumblrMediaTools.MimeFor(path);
            await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(path), type);
        }

        private async Task ContentThumb(UserRequest req)
        {
            string blogId = RequiredQuery(req, "blogId");
            string key = RequiredQuery(req, "key");
            var strategy = Store.Read(s => OmniTumblrStore.Clone((s.Blog(blogId) ?? throw NotFound("No such blog.")).Strategy));
            var candidate = service.ContentSources!.Resolve(strategy, blogId, key, DateTime.UtcNow) ?? throw NotFound("No such content.");
            string cachePath = Path.Combine(Store.FramesCacheDirectory, $"cand-{OmniTumblrScheduleMath.Fnv1a(key):x8}-{key.Length}.jpg");
            if (!File.Exists(cachePath))
            {
                bool ok = await service.MediaTools!.CreateThumbnailAsync(candidate.FilePath, cachePath, null, CancellationToken.None);
                if (!ok)
                {
                    if (OmniTumblrMediaTools.IsImage(candidate.FilePath))
                    {
                        await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(candidate.FilePath), OmniTumblrMediaTools.MimeFor(candidate.FilePath));
                        return;
                    }
                    throw NotFound("A preview could not be made (is FFmpeg installed?).");
                }
            }
            await req.ReturnBinaryResponse(await File.ReadAllBytesAsync(cachePath), "image/jpeg");
        }

        private async Task Library(UserRequest req)
        {
            string blogId = RequiredQuery(req, "blogId");
            var used = Store.Read(s => (s.Blog(blogId) ?? throw NotFound("No such blog.")).UsedContentKeys.ToHashSet(StringComparer.Ordinal));
            string dir = service.ContentSources!.LibraryDirectoryFor(blogId);
            var files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles().Where(f => OmniTumblrMediaTools.IsSupported(f.Name)).OrderBy(f => f.CreationTimeUtc).Select(f => new
                {
                    FileName = f.Name,
                    Key = OmniTumblrContentSources.LibraryKey(f.Name),
                    Kind = OmniTumblrMediaTools.IsVideo(f.Name) ? "Video" : "Photo",
                    Bytes = f.Length,
                    AddedUtc = f.CreationTimeUtc,
                    Used = used.Contains(OmniTumblrContentSources.LibraryKey(f.Name)),
                }).ToList<object>()
                : new List<object>();
            await Respond(req, new { BlogId = blogId, Files = files });
        }

        private async Task MemeScraperOptions(UserRequest req) => await Respond(req, service.ContentSources!.DescribeCatalog());

        private async Task Events(UserRequest req)
        {
            string? blogId = Query(req, "blogId");
            int limit = Math.Clamp(int.TryParse(Query(req, "limit"), out var l) ? l : 100, 1, 1000);
            var dto = Store.Read(s => s.Events.Where(e => blogId == null || e.BlogId == blogId).Reverse().Take(limit).Select(e => EventDto(s, e)).ToList());
            await Respond(req, dto);
        }

        private async Task Settings(UserRequest req)
        {
            bool ai = await AiAvailableAsync();
            var dto = Store.Read(s => new
            {
                App = AppDto(s.App),
                Connections = s.Connections.Values.OrderBy(c => c.UserName).Select(c => ConnectionDto(s, c)).ToList(),
                Engine = EngineDto(s, ai),
                Presets = OmniTumblrStrategyRules.PresetNames,
                DefaultStrategy = OmniTumblrStrategy.CreateDefault(),
            });
            await Respond(req, dto);
        }

        // ─────────────────────────────── Tumblr app + connections ───────────────────────────────

        private async Task SaveApp(UserRequest req)
        {
            var body = Body(req);
            TumblrAuthMode? mode = Enum.TryParse<TumblrAuthMode>(OptString(body, "authMode"), true, out var m) ? m : null;
            var result = await service.Auth!.SaveAppAsync(OptString(body, "consumerKey"), OptString(body, "consumerSecret"), OptString(body, "callbackUrl"), mode, CancellationToken.None);
            if (!result.Saved) throw Bad(result.Message);
            await Respond(req, new { result.Saved, result.Verified, result.Message, App = Store.Read(s => AppDto(s.App)) });
        }

        private async Task ConnectBegin(UserRequest req)
        {
            var body = Body(req);
            TumblrAuthMode? mode = Enum.TryParse<TumblrAuthMode>(OptString(body, "authMode"), true, out var m) ? m : null;
            string? reconnect = OptString(body, "reconnectConnectionId");
            if (reconnect != null && Store.Read(s => s.Connection(reconnect)) == null) throw NotFound("No such connection.");
            try
            {
                var (flowId, url, actualMode) = await service.Auth!.BeginAsync(mode, reconnect, CancellationToken.None);
                await Respond(req, new { flowId, authorizationUrl = url, mode = actualMode, callbackUrl = service.Auth.CallbackUrl });
            }
            catch (InvalidOperationException ex)
            {
                throw Bad(ex.Message);
            }
            catch (TumblrApiException ex)
            {
                throw Bad($"Tumblr refused to start the authorization ({ex.Code}): {ex.Message}. Check the app's consumer key/secret and that its default callback URL is exactly {service.Auth!.CallbackUrl}.");
            }
        }

        private async Task ConnectStatus(UserRequest req)
        {
            string flowId = RequiredQuery(req, "flowId");
            var flow = service.Auth!.GetFlow(flowId) ?? throw NotFound("No such authorization (it may have expired).");
            var connection = flow.ConnectionId == null ? null : Store.Read(s => s.Connection(flow.ConnectionId) is OmniTumblrConnection c ? ConnectionDto(s, c) : null);
            await Respond(req, new { flow.FlowId, flow.State, flow.Error, flow.Mode, flow.ConnectionId, flow.CreatedUtc, flow.CompletedUtc, Connection = connection });
        }

        private async Task OAuthCallback(UserRequest req)
        {
            AuthCallbackResult result;
            try
            {
                result = await service.Auth!.HandleCallbackAsync(req.userParameters ?? new System.Collections.Specialized.NameValueCollection(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new AuthCallbackResult { Ok = false, Title = "Authorization failed", Message = ex.Message };
            }
            await req.ReturnResponse(CallbackPage(result), "text/html", code: result.Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        }

        private static string CallbackPage(AuthCallbackResult r)
        {
            static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            string accent = r.Ok ? "#4ec98a" : "#ff7b7b";
            string payload = JsonConvert.SerializeObject(new { source = "omnitumblr", type = "oauth", ok = r.Ok, flowId = r.FlowId, message = r.Message });
            return $@"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>OmniTumblr · {E(r.Title)}</title>
<style>body{{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0d0a;color:#e9f1e5;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}}
.card{{max-width:460px;margin:24px;padding:28px 30px;background:#12150f;border:1px solid rgba(146,196,130,.14);border-radius:14px}}
h1{{margin:0 0 8px;font-size:20px;color:{accent}}}p{{margin:0;color:#b3c2ad}}small{{display:block;margin-top:16px;color:#93a48d}}</style></head>
<body><div class=""card""><h1>{(r.Ok ? "✓" : "⚠")} {E(r.Title)}</h1><p>{E(r.Message)}</p><small id=""close"">{(r.Ok ? "This tab will close by itself." : "You can close this tab and try again from OmniTumblr.")}</small></div>
<script>(function(){{var m={payload};try{{if(window.opener)window.opener.postMessage(m,'*');}}catch(e){{}}
{(r.Ok ? "setTimeout(function(){try{window.close();}catch(e){}},2500);" : "")}}})();</script></body></html>";
        }

        private async Task ConnectionRefresh(UserRequest req)
        {
            string connectionId = Required(Body(req), "connectionId");
            if (Store.Read(s => s.Connection(connectionId)) == null) throw NotFound("No such connection.");
            try
            {
                await service.Auth!.RefreshUserInfoAsync(connectionId, CancellationToken.None);
            }
            catch (ConnectionUnavailableException ex) { throw Conflict(ex.Message); }
            catch (TumblrApiException ex) { throw Bad($"Tumblr: {ex.Message}"); }
            await Respond(req, Store.Read(s => ConnectionDto(s, s.Connection(connectionId)!)));
        }

        private async Task ConnectionRemove(UserRequest req)
        {
            string connectionId = Required(Body(req), "connectionId");
            Store.Mutate(s =>
            {
                var conn = s.Connection(connectionId) ?? throw NotFound("No such connection.");
                var blogs = s.Blogs.Values.Where(b => b.ConnectionId == connectionId).Select(b => "@" + b.Name).ToList();
                if (blogs.Count > 0) throw Conflict($"Remove its managed blogs first: {string.Join(", ", blogs)}.");
                s.Connections.Remove(connectionId);
                s.MarkConnections();
                s.AddEvent(EventLevel.Info, "connection.removed", $"Removed the Tumblr account @{conn.UserName}. (Revoke the app's access on Tumblr too if you no longer want it.)");
            });
            await Store.FlushAsync();
            await Respond(req, new { removed = true });
        }

        // ─────────────────────────────── Blogs ───────────────────────────────

        private async Task BlogsAdd(UserRequest req)
        {
            var body = Body(req);
            string connectionId = Required(body, "connectionId");
            var requested = OptStringList(body, "blogs");
            if (requested.Count == 0) throw Bad("Choose at least one blog.");
            bool autopilot = OptBool(body, "autopilot") ?? false;
            string? preset = OptString(body, "preset");
            DateTime now = DateTime.UtcNow;

            var (added, skipped) = Store.Mutate(s =>
            {
                var conn = s.Connection(connectionId) ?? throw NotFound("No such connection.");
                var addedIds = new List<string>();
                var skippedNotes = new List<string>();
                foreach (string raw in requested)
                {
                    string wanted = TumblrApiClient.NormalizeBlogIdentifier(raw);
                    var reference = conn.Blogs.FirstOrDefault(b => b.Uuid == raw || string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase));
                    if (reference == null) { skippedNotes.Add($"@{wanted}: not one of @{conn.UserName}'s blogs."); continue; }
                    if (s.Blogs.Values.Any(b => (reference.Uuid != null && b.Uuid == reference.Uuid) || string.Equals(b.Name, reference.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        skippedNotes.Add($"@{reference.Name} is already managed.");
                        continue;
                    }
                    var strategy = OmniTumblrStrategyRules.Normalize(OmniTumblrStrategyRules.Preset(preset));
                    bool canAutopilot = autopilot && OmniTumblrStrategyRules.Validate(strategy, true).Count == 0;
                    var blog = new OmniTumblrBlog
                    {
                        Name = reference.Name,
                        Uuid = reference.Uuid,
                        Title = reference.Title,
                        Url = reference.Url,
                        ConnectionId = conn.ConnectionId,
                        Autopilot = canAutopilot,
                        AddedUtc = now,
                        Strategy = strategy,
                    };
                    blog.Stats.Followers = reference.Followers;
                    blog.Stats.FollowersSyncedUtc = reference.Followers.HasValue ? now : null;
                    s.Blogs[blog.BlogId] = blog;
                    s.PostsOf(blog.BlogId);
                    var insights = s.InsightsOf(blog.BlogId);
                    if (reference.Followers.HasValue) OmniTumblrInsights.RecordSnapshot(insights, now, reference.Followers, null);
                    s.MarkBlog(blog.BlogId);
                    s.MarkPosts(blog.BlogId);
                    s.MarkInsights(blog.BlogId);
                    s.AddEvent(EventLevel.Success, "blog.added", $"Now managing @{blog.Name}{(blog.Autopilot ? " on autopilot" : "")}.", blog.BlogId, utc: now);
                    addedIds.Add(blog.BlogId);
                }
                return (addedIds, skippedNotes);
            });
            await Store.FlushAsync();
            foreach (string blogId in added)
            {
                service.Sync?.ExpediteBlog(blogId);
                service.Engine?.WakePlanner(blogId);
            }
            service.Engine?.WakeSync();
            DateTime after = DateTime.UtcNow;
            var summaries = Store.Read(s => added.Select(id => OmniTumblrViews.BlogSummary(s, s.Blog(id)!, after)).ToList());
            await Respond(req, new { Added = summaries, Skipped = skipped });
        }

        private async Task BlogsUpdate(UserRequest req)
        {
            var body = Body(req);
            string blogId = Required(body, "blogId");
            OmniTumblrStrategy? strategy = null;
            if (body["strategy"] is JObject strategyJson)
            {
                try { strategy = strategyJson.ToObject<OmniTumblrStrategy>(JsonSerializer.Create(Json)); }
                catch (Exception ex) { throw Bad("The strategy could not be read: " + ex.Message); }
                if (strategy == null) throw Bad("The strategy could not be read.");
                OmniTumblrStrategyRules.Normalize(strategy);
            }
            bool? autopilot = OptBool(body, "autopilot");
            bool? paused = OptBool(body, "paused");
            bool? requireApproval = OptBool(body, "requireApproval");
            string? notes = OptString(body, "notes");
            DateTime now = DateTime.UtcNow;

            var changes = Store.Mutate(s =>
            {
                var blog = s.Blog(blogId) ?? throw NotFound("No such blog.");
                var effective = strategy ?? blog.Strategy;
                bool willAutopilot = autopilot ?? blog.Autopilot;
                var errors = OmniTumblrStrategyRules.Validate(effective, willAutopilot);
                if (errors.Count > 0) throw Bad(string.Join(" ", errors));

                var notesOut = new List<string>();
                var posts = s.PostsOf(blogId);
                if (strategy != null)
                {
                    string Fingerprint(OmniTumblrStrategy x) => JsonConvert.SerializeObject(new { x.CaptionMode, x.FixedCaption, x.CaptionPool, x.Ai });
                    bool captionsChanged = Fingerprint(strategy) != Fingerprint(blog.Strategy);
                    bool tagsChanged = !strategy.FixedTags.SequenceEqual(blog.Strategy.FixedTags) || !strategy.RotatingTags.SequenceEqual(blog.Strategy.RotatingTags)
                        || strategy.RotatingTagsPerPost != blog.Strategy.RotatingTagsPerPost || strategy.MaxTags != blog.Strategy.MaxTags;
                    blog.Strategy = strategy;
                    foreach (var p in posts.Where(p => p.Origin == PostOrigin.Autopilot && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                    {
                        if (captionsChanged && !p.CaptionInfo.Edited)
                        {
                            p.CaptionInfo = new CaptionInfo { Mode = strategy.CaptionMode };
                            p.CaptionPending = true;
                            p.Caption = null;
                            p.Status = PostStatus.Planned;
                        }
                        if (tagsChanged) p.Tags = OmniTumblrPlanner.ComposeTags(strategy, p.CaptionInfo.SuggestedTags);
                        p.TumblrState = strategy.PostState;
                        if (!strategy.CreditSource) p.SourceUrl = null;
                        else if (p.Content?.OriginalUrl != null) p.SourceUrl = p.Content.OriginalUrl;
                        s.Touch(p, now);
                    }
                    if (captionsChanged) notesOut.Add("Upcoming captions will be rewritten in the new style.");
                    s.AddEvent(EventLevel.Info, "strategy.updated", $"@{blog.Name}: strategy updated.", blogId, utc: now);
                }
                if (autopilot.HasValue && autopilot.Value != blog.Autopilot)
                {
                    blog.Autopilot = autopilot.Value;
                    if (!blog.Autopilot)
                    {
                        int cancelled = 0;
                        foreach (var p in posts.Where(p => p.Origin == PostOrigin.Autopilot && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                        {
                            p.Status = PostStatus.Cancelled;
                            p.LastError = "Autopilot was switched off.";
                            if (p.Content != null) blog.UsedContentKeys.Remove(p.Content.Key);
                            s.Touch(p, now);
                            cancelled++;
                        }
                        if (cancelled > 0) notesOut.Add($"Cancelled {cancelled} planned post(s).");
                    }
                    s.AddEvent(EventLevel.Info, "autopilot", $"@{blog.Name}: autopilot {(blog.Autopilot ? "on" : "off")}.", blogId, utc: now);
                }
                if (paused.HasValue && paused.Value != blog.Paused)
                {
                    blog.Paused = paused.Value;
                    s.AddEvent(EventLevel.Info, paused.Value ? "blog.paused" : "blog.resumed", $"@{blog.Name} {(paused.Value ? "paused" : "resumed")}.", blogId, utc: now);
                }
                if (requireApproval.HasValue && requireApproval.Value != blog.RequireApproval)
                {
                    blog.RequireApproval = requireApproval.Value;
                    foreach (var p in posts.Where(p => p.Origin == PostOrigin.Autopilot && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                    {
                        if (blog.RequireApproval)
                        {
                            // Everything autopilot has queued now needs a look before it goes out.
                            p.Approved = false;
                            if (p.Status == PostStatus.Ready) p.Status = PostStatus.AwaitingApproval;
                        }
                        else
                        {
                            p.Approved = true;
                            if (p.Status == PostStatus.AwaitingApproval) p.Status = PostStatus.Ready;
                        }
                        s.Touch(p, now);
                    }
                }
                if (notes != null) blog.Notes = notes.Length > 4000 ? notes[..4000] : notes;
                s.MarkBlog(blogId);
                return notesOut;
            });

            service.Planner!.ReconcileSlots(blogId);
            await Store.FlushAsync();
            service.Engine?.WakePlanner(blogId);
            service.Engine?.WakePublisher();
            DateTime after = DateTime.UtcNow;
            await Respond(req, new { Summary = Store.Read(s => OmniTumblrViews.BlogSummary(s, s.Blog(blogId)!, after)), Notes = changes });
        }

        private async Task BlogsRemove(UserRequest req)
        {
            string blogId = Required(Body(req), "blogId");
            DateTime now = DateTime.UtcNow;
            string name = Store.Mutate(s =>
            {
                var blog = s.Blog(blogId) ?? throw NotFound("No such blog.");
                if (s.PostsOf(blogId).Any(p => p.Status == PostStatus.Publishing)) throw Conflict("A post is uploading right now; try again in a minute.");
                s.Blogs.Remove(blogId);
                s.Posts.Remove(blogId);
                s.Insights.Remove(blogId);
                s.AddEvent(EventLevel.Info, "blog.removed", $"Stopped managing @{blog.Name}. Its history was archived; nothing was deleted on Tumblr.", utc: now);
                return blog.Name;
            });
            await Store.ArchiveBlogFilesAsync(blogId);
            await Store.FlushAsync();
            await Respond(req, new { removed = true, name });
        }

        private async Task BlogsRefresh(UserRequest req)
        {
            string blogId = Required(Body(req), "blogId");
            if (Store.Read(s => s.Blog(blogId)) == null) throw NotFound("No such blog.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            try { await service.Sync!.SyncBlogNowAsync(blogId, cts.Token); }
            catch (OperationCanceledException) { throw new RouteException(HttpStatusCode.GatewayTimeout, "Tumblr took too long to answer; the refresh continues in the background."); }
            DateTime now = DateTime.UtcNow;
            var errors = service.Sync!.LastErrors.Where(kv => kv.Key.EndsWith(":" + blogId)).Select(kv => kv.Value).ToList();
            await Respond(req, new { Summary = Store.Read(s => OmniTumblrViews.BlogSummary(s, s.Blog(blogId)!, now)), Errors = errors });
        }

        private async Task BlogsPlanNow(UserRequest req)
        {
            string blogId = Required(Body(req), "blogId");
            var autopilot = Store.Read(s => (s.Blog(blogId) ?? throw NotFound("No such blog.")).Autopilot);
            if (!autopilot) throw Conflict("Autopilot is off for this blog; switch it on to plan posts.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var outcome = await service.Planner!.PlanBlogAsync(blogId, cts.Token);
            service.Engine?.WakePlanner();
            await Respond(req, outcome);
        }

        // ─────────────────────────────── Posts ───────────────────────────────

        private async Task PostsCreate(UserRequest req)
        {
            var body = Body(req);
            var blogIds = OptStringList(body, "blogIds");
            if (blogIds.Count == 0) throw Bad("Choose at least one blog.");
            if (!Enum.TryParse<PostKind>(OptString(body, "kind") ?? "Video", true, out var kind)) throw Bad("Unknown post kind.");
            string captionMode = (OptString(body, "captionMode") ?? "manual").ToLowerInvariant();
            string? caption = OptString(body, "caption")?.Trim();
            string? title = OptString(body, "title")?.Trim();
            string? linkUrl = OptString(body, "linkUrl")?.Trim();
            string? aiInstruction = OptString(body, "aiInstruction");
            var tags = OptStringList(body, "tags");
            bool includeBlogTags = OptBool(body, "includeBlogTags") ?? true;
            DateTime now = DateTime.UtcNow;
            DateTime scheduled = OptDate(body, "scheduledUtc") ?? now;
            if (scheduled < now.AddMinutes(-5)) throw Bad("The scheduled time is in the past.");
            TumblrPostState? state = Enum.TryParse<TumblrPostState>(OptString(body, "tumblrState"), true, out var st) ? st : null;
            string? sourceUrl = OptString(body, "sourceUrl")?.Trim();

            var media = new List<PostMedia>();
            foreach (string mediaId in OptStringList(body, "mediaIds"))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(mediaId, "^[a-f0-9]{12}$")) throw Bad("Invalid media id.");
                string? path = Directory.EnumerateFiles(Store.UploadsDirectory, mediaId + ".*").FirstOrDefault();
                if (path == null) throw Bad("An uploaded file was not found; upload it again.");
                media.Add(new PostMedia { Path = path, MimeType = OmniTumblrMediaTools.MimeFor(path), Bytes = new FileInfo(path).Length });
            }
            switch (kind)
            {
                case PostKind.Video:
                    if (media.Count(m => m.IsVideo) != 1 || media.Count != 1) throw Bad("A video post needs exactly one video file.");
                    break;
                case PostKind.Photo:
                    if (media.Count == 0 || media.Any(m => m.IsVideo)) throw Bad("A photo post needs one or more images (and no video).");
                    if (media.Count > TumblrNpf.MaxImages) throw Bad($"At most {TumblrNpf.MaxImages} images per post.");
                    break;
                case PostKind.Link:
                    if (string.IsNullOrWhiteSpace(linkUrl) || !Uri.TryCreate(linkUrl, UriKind.Absolute, out _)) throw Bad("A link post needs a valid URL.");
                    break;
                case PostKind.Text:
                    if (string.IsNullOrWhiteSpace(caption) && string.IsNullOrWhiteSpace(title) && captionMode != "ai") throw Bad("A text post needs a title or text.");
                    break;
            }
            if (captionMode == "manual" && kind is PostKind.Video or PostKind.Photo && caption == null) caption = "";

            var created = Store.Mutate(s =>
            {
                var result = new List<PostSummaryDto>();
                foreach (string blogId in blogIds.Distinct())
                {
                    var blog = s.Blog(blogId) ?? throw NotFound($"No such blog: {blogId}.");
                    var post = new OmniTumblrPost
                    {
                        BlogId = blogId,
                        Origin = PostOrigin.Manual,
                        Kind = kind,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                        ScheduledUtc = scheduled,
                        Approved = true,
                        ManualOverride = true,
                        Media = media.Select(m => OmniTumblrStore.Clone(m)).ToList(),
                        Title = string.IsNullOrWhiteSpace(title) ? null : title,
                        LinkUrl = linkUrl,
                        SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl,
                        TumblrState = state ?? blog.Strategy.PostState,
                        Tags = TumblrNpf.NormalizeTags((includeBlogTags ? blog.Strategy.FixedTags : new List<string>()).Concat(tags), Math.Clamp(blog.Strategy.MaxTags, 1, TumblrNpf.MaxTags)),
                    };
                    if (captionMode == "ai")
                    {
                        post.CaptionInfo = new CaptionInfo { Mode = CaptionMode.AI, Direction = string.IsNullOrWhiteSpace(aiInstruction) ? null : aiInstruction.Trim() };
                        post.CaptionPending = true;
                        post.Status = PostStatus.Planned;
                    }
                    else
                    {
                        post.CaptionInfo = new CaptionInfo { Mode = captionMode == "none" ? CaptionMode.None : CaptionMode.Fixed, Edited = true };
                        post.Caption = captionMode == "none" ? "" : caption ?? "";
                        post.Status = post.Media.Count > 0 ? PostStatus.Planned : PostStatus.Ready; // Planned → prepared (thumbnail, dimensions) first
                    }
                    s.PostsOf(blogId).Add(post);
                    s.MarkPosts(blogId);
                    s.AddEvent(EventLevel.Info, "post.created", $"@{blog.Name}: {kind.ToString().ToLowerInvariant()} post {(scheduled <= now.AddMinutes(1) ? "queued to publish now" : $"scheduled for {scheduled:yyyy-MM-dd HH:mm} UTC")}.", blogId, post.PostId, now);
                    result.Add(OmniTumblrViews.Summary(post, blog));
                }
                return result;
            });
            await Store.FlushAsync();
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, new { Created = created });
        }

        private OmniTumblrPost RequirePending(OmniTumblrState s, string postId)
        {
            var post = s.FindPost(postId) ?? throw NotFound("No such post.");
            if (post.Status == PostStatus.Publishing) throw Conflict("The post is uploading right now.");
            if (!post.IsPending) throw Conflict($"The post is already {post.Status.ToString().ToLowerInvariant()}.");
            return post;
        }

        private async Task PostsUpdate(UserRequest req)
        {
            var body = Body(req);
            string postId = Required(body, "postId");
            DateTime now = DateTime.UtcNow;
            DateTime? scheduled = OptDate(body, "scheduledUtc");
            var summary = Store.Mutate(s =>
            {
                var post = RequirePending(s, postId);
                var blog = s.Blog(post.BlogId);
                if (body["caption"] != null)
                {
                    post.Caption = (OptString(body, "caption") ?? "").Trim();
                    post.CaptionInfo.Edited = true;
                    post.CaptionInfo.Error = null;
                    post.CaptionPending = false;
                    if (post.Slug != null && !post.NeedsReconcile) post.Slug = null; // re-derive from the new caption
                }
                if (body["tags"] is JArray)
                    post.Tags = TumblrNpf.NormalizeTags(OptStringList(body, "tags"), TumblrNpf.MaxTags);
                if (body["title"] != null) post.Title = OptString(body, "title");
                if (body["linkUrl"] != null) post.LinkUrl = OptString(body, "linkUrl");
                if (Enum.TryParse<TumblrPostState>(OptString(body, "tumblrState"), true, out var state)) post.TumblrState = state;
                if (scheduled.HasValue)
                {
                    if (scheduled.Value < now.AddMinutes(-5)) throw Bad("The scheduled time is in the past.");
                    post.ScheduledUtc = scheduled.Value;
                    post.NextAttemptUtc = null;
                    post.BlockedReason = null;
                    post.ManualOverride = true;
                }
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.edited", $"@{blog?.Name}: post edited.", post.BlogId, post.PostId, now);
                return OmniTumblrViews.Summary(post, blog);
            });
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, summary);
        }

        private async Task PostsApprove(UserRequest req)
        {
            var body = Body(req);
            string? postId = OptString(body, "postId");
            string? blogId = OptString(body, "blogId");
            DateTime now = DateTime.UtcNow;
            int approved = Store.Mutate(s =>
            {
                var targets = postId != null
                    ? new List<OmniTumblrPost> { RequirePending(s, postId) }
                    : blogId != null ? s.PostsOf(blogId).Where(p => p.Status == PostStatus.AwaitingApproval).ToList()
                    : throw Bad("Give a postId, or a blogId to approve all of its waiting posts.");
                foreach (var p in targets)
                {
                    p.Approved = true;
                    if (p.Status == PostStatus.AwaitingApproval) p.Status = PostStatus.Ready;
                    if (p.BlockedReason == "Waiting for approval.") p.BlockedReason = null;
                    s.Touch(p, now);
                }
                if (targets.Count > 0) s.AddEvent(EventLevel.Info, "post.approved", $"Approved {targets.Count} post(s).", targets[0].BlogId, targets.Count == 1 ? targets[0].PostId : null, now);
                return targets.Count;
            });
            service.Engine?.WakePublisher();
            await Respond(req, new { approved });
        }

        private async Task PostsPublishNow(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            var summary = Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (post.Status == PostStatus.Publishing) throw Conflict("The post is already uploading.");
                if (post.Status is PostStatus.Published or PostStatus.Removed) throw Conflict("The post is already published.");
                if (post.Status is PostStatus.Failed or PostStatus.Cancelled or PostStatus.Skipped)
                {
                    post.Status = post.CaptionPending ? PostStatus.Planned : PostStatus.Ready;
                    post.Attempts = 0;
                }
                if (post.Status == PostStatus.AwaitingApproval || post.Status == PostStatus.Draft) post.Status = PostStatus.Ready;
                post.Approved = true;
                post.ManualOverride = true;
                post.ScheduledUtc = now;
                post.NextAttemptUtc = null;
                post.BlockedReason = post.CaptionPending ? "Writing the caption first." : null;
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.publish-now", $"@{s.Blog(post.BlogId)?.Name}: publishing now on request.", post.BlogId, post.PostId, now);
                return OmniTumblrViews.Summary(post, s.Blog(post.BlogId));
            });
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, summary);
        }

        private async Task PostsRegenerateCaption(UserRequest req)
        {
            var body = Body(req);
            string postId = Required(body, "postId");
            string? instruction = OptString(body, "instruction");
            Store.Mutate(s =>
            {
                var post = RequirePending(s, postId);
                post.CaptionInfo.Mode = CaptionMode.AI;
                post.CaptionInfo.Edited = false;
                if (!string.IsNullOrWhiteSpace(instruction)) post.CaptionInfo.Direction = instruction.Trim();
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try
            {
                await service.Planner!.PreparePostAsync(postId, forceRegenerate: true, extraInstruction: instruction, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new RouteException(HttpStatusCode.GatewayTimeout, "The language model took too long; try again.");
            }
            var (summary, error) = Store.Read(s =>
            {
                var p = s.FindPost(postId) ?? throw NotFound("No such post.");
                return (OmniTumblrViews.Summary(p, s.Blog(p.BlogId)), p.CaptionInfo.Error);
            });
            if (summary.CaptionPending && error != null) throw new RouteException(HttpStatusCode.BadGateway, error);
            service.Engine?.WakePublisher();
            await Respond(req, summary);
        }

        private async Task PostsSwapContent(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (ok, message) = await service.Planner!.SwapContentAsync(postId, cts.Token);
            if (!ok) throw Conflict(message);
            service.Engine?.WakePlanner();
            var summary = Store.Read(s => s.FindPost(postId) is OmniTumblrPost p ? OmniTumblrViews.Summary(p, s.Blog(p.BlogId)) : null);
            await Respond(req, new { message, Post = summary });
        }

        private async Task PostsSkip(UserRequest req) => await EndPost(req, PostStatus.Skipped, "Skipped by hand.");

        private async Task PostsCancel(UserRequest req) => await EndPost(req, PostStatus.Cancelled, "Cancelled by hand.");

        private async Task EndPost(UserRequest req, PostStatus status, string reason)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            var summary = Store.Mutate(s =>
            {
                var post = RequirePending(s, postId);
                post.Status = status;
                post.LastError = reason;
                post.BlockedReason = null;
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post." + status.ToString().ToLowerInvariant(), $"@{s.Blog(post.BlogId)?.Name}: {reason.TrimEnd('.').ToLowerInvariant()} a post.", post.BlogId, post.PostId, now);
                return OmniTumblrViews.Summary(post, s.Blog(post.BlogId));
            });
            // A cancelled autopilot post frees its slot for different content; a skipped one keeps it empty.
            if (status == PostStatus.Cancelled) service.Engine?.WakePlanner(summary.BlogId);
            await Respond(req, summary);
        }

        private async Task PostsRetry(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            var summary = Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (post.Status != PostStatus.Failed) throw Conflict("Only a failed post can be retried.");
                if (post.Media.Any(m => !File.Exists(m.Path))) throw Conflict("Its media file is gone; it cannot be retried.");
                post.Status = post.CaptionPending ? PostStatus.Planned : PostStatus.Ready;
                post.Attempts = 0;
                post.Deferrals = 0;
                post.ManualOverride = true;
                post.ScheduledUtc = post.ScheduledUtc < now ? now : post.ScheduledUtc;
                post.NextAttemptUtc = null;
                post.BlockedReason = null;
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.retry", $"@{s.Blog(post.BlogId)?.Name}: retrying a failed post.", post.BlogId, post.PostId, now);
                return OmniTumblrViews.Summary(post, s.Blog(post.BlogId));
            });
            service.Engine?.WakePublisher();
            await Respond(req, summary);
        }

        private async Task PostsDeleteRemote(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            var target = Store.Read(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (post.Status != PostStatus.Published || post.TumblrPostId == null) throw Conflict("Only a published post can be deleted from Tumblr.");
                var blog = s.Blog(post.BlogId) ?? throw NotFound("The post's blog is gone.");
                return (Identifier: blog.Uuid ?? blog.Name, blog.ConnectionId, TumblrId: post.TumblrPostId, BlogName: blog.Name);
            });
            try
            {
                var creds = await service.Auth!.GetCredentialsAsync(target.ConnectionId, CancellationToken.None);
                await service.TumblrApi!.DeletePostAsync(creds, target.Identifier, target.TumblrId, CancellationToken.None);
            }
            catch (ConnectionUnavailableException ex) { throw Conflict(ex.Message); }
            catch (TumblrApiException ex) when (ex.Kind != TumblrErrorKind.NotFound) { throw Bad($"Tumblr refused ({ex.Code}): {ex.Message}"); }
            DateTime now = DateTime.UtcNow;
            var summary = Store.Mutate(s =>
            {
                var post = s.FindPost(postId)!;
                post.Status = PostStatus.Removed;
                post.LastError = $"Deleted from Tumblr on {now:yyyy-MM-dd HH:mm} UTC.";
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.deleted", $"@{target.BlogName}: deleted a post from Tumblr.", post.BlogId, post.PostId, now);
                return OmniTumblrViews.Summary(post, s.Blog(post.BlogId));
            });
            await Respond(req, summary);
        }

        // ─────────────────────────────── Media ───────────────────────────────

        private async Task MediaUpload(UserRequest req)
        {
            string fileName = Path.GetFileName(RequiredQuery(req, "fileName"));
            string purpose = (Query(req, "purpose") ?? "compose").ToLowerInvariant();
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!OmniTumblrMediaTools.IsSupported(fileName))
                throw Bad("Unsupported file type. Use .mp4/.mov/.m4v videos or .jpg/.png/.gif/.webp images.");

            string destination;
            string? mediaId = null;
            string? blogId = null;
            if (purpose == "library")
            {
                blogId = RequiredQuery(req, "blogId");
                if (Store.Read(s => s.Blog(blogId)) == null) throw NotFound("No such blog.");
                string dir = service.ContentSources!.LibraryDirectoryFor(blogId);
                Directory.CreateDirectory(dir);
                string stem = new string(Path.GetFileNameWithoutExtension(fileName).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(60).ToArray());
                destination = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{(stem.Length == 0 ? "media" : stem)}{ext}");
            }
            else
            {
                mediaId = OmniTumblrIds.New();
                Directory.CreateDirectory(Store.UploadsDirectory);
                destination = Path.Combine(Store.UploadsDirectory, mediaId + ext);
            }

            long bytes;
            try
            {
                bytes = await KliveCloud.KliveCloud.StreamToFileAtomically(destination, req.RequestBodyStream, req.req.ContentLength64);
            }
            catch (InvalidDataException) { throw Bad("The upload was empty."); }
            catch (EndOfStreamException ex) { throw Bad(ex.Message); }

            if (purpose == "library")
            {
                Store.Mutate(s => s.AddEvent(EventLevel.Info, "library.added", $"@{s.Blog(blogId)?.Name}: added {Path.GetFileName(destination)} to the content library.", blogId));
                service.Engine?.WakePlanner(blogId);
            }
            await Respond(req, new
            {
                MediaId = mediaId,
                FileName = Path.GetFileName(destination),
                OriginalName = fileName,
                Kind = OmniTumblrMediaTools.IsVideo(fileName) ? "Video" : "Photo",
                MimeType = OmniTumblrMediaTools.MimeFor(fileName),
                Bytes = bytes,
                Purpose = purpose,
                BlogId = blogId,
            });
        }

        private async Task LibraryDelete(UserRequest req)
        {
            var body = Body(req);
            string blogId = Required(body, "blogId");
            string fileName = Path.GetFileName(Required(body, "fileName"));
            string path = Path.Combine(service.ContentSources!.LibraryDirectoryFor(blogId), fileName);
            if (!File.Exists(path)) throw NotFound("No such library file.");
            bool inUse = Store.Read(s => s.PostsOf(blogId).Any(p => p.IsPending && p.Media.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))));
            if (inUse) throw Conflict("A pending post uses this file; cancel or swap that post first.");
            File.Delete(path);
            await Respond(req, new { deleted = true });
        }

        // ─────────────────────────────── Caption preview ───────────────────────────────

        private async Task CaptionPreview(UserRequest req)
        {
            var body = Body(req);
            string blogId = Required(body, "blogId");
            string? key = OptString(body, "contentKey");
            string? instruction = OptString(body, "instruction");
            DateTime now = DateTime.UtcNow;
            var snap = Store.Read(s =>
            {
                var blog = s.Blog(blogId) ?? throw NotFound("No such blog.");
                var recent = s.PostsOf(blogId).Where(p => !string.IsNullOrWhiteSpace(p.Caption)).OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc).Take(8).Select(p => p.Caption!).ToList();
                return (Blog: OmniTumblrStore.Clone(blog), Excluded: OmniTumblrPlanner.ExcludedKeys(s, blog, blog.Strategy) as IReadOnlySet<string>, Recent: recent);
            });
            var strategy = snap.Blog.Strategy;
            if (body["strategy"] is JObject strategyJson)
            {
                try { strategy = strategyJson.ToObject<OmniTumblrStrategy>(JsonSerializer.Create(Json)) ?? strategy; }
                catch (Exception ex) { throw Bad("The strategy could not be read: " + ex.Message); }
                OmniTumblrStrategyRules.Normalize(strategy);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            ContentCandidate? candidate = key != null
                ? service.ContentSources!.Resolve(strategy, blogId, key, now)
                : (await service.ContentSources!.FindCandidatesAsync(strategy, blogId, snap.Excluded, 1, now, cts.Token)).FirstOrDefault();
            if (candidate == null) throw Conflict("There is no content to caption with this strategy's content settings.");

            var frames = new List<byte[]>();
            if (strategy.Ai.UseVision)
            {
                if (candidate.Kind == PostKind.Video)
                    frames = await service.MediaTools!.ExtractFramesAsync(candidate.FilePath, 4, 512, candidate.DurationSeconds, cts.Token);
                else if (candidate.FilePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || candidate.FilePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    frames.Add(await File.ReadAllBytesAsync(candidate.FilePath, cts.Token));
            }
            try
            {
                var result = await service.Captioner!.GenerateAiAsync(new CaptionRequest
                {
                    Settings = strategy.Ai,
                    BlogName = snap.Blog.Name,
                    BlogTitle = snap.Blog.Title,
                    BlogDescription = snap.Blog.Description,
                    Kind = candidate.Kind,
                    OriginalCaption = candidate.OriginalCaption,
                    Origin = candidate.Origin,
                    RecentCaptions = snap.Recent,
                    Frames = frames,
                    ExtraInstruction = instruction,
                }, cts.Token);
                await Respond(req, new
                {
                    result.Caption,
                    Tags = OmniTumblrPlanner.ComposeTags(strategy, result.Tags),
                    SuggestedTags = result.Tags,
                    result.AltText,
                    result.Model,
                    result.UsedVision,
                    FramesSent = result.UsedVision ? frames.Count : 0,
                    Content = CandidateDto(candidate),
                });
            }
            catch (OperationCanceledException)
            {
                throw new RouteException(HttpStatusCode.GatewayTimeout, "The language model took too long; try again.");
            }
            catch (CaptionGenerationException ex)
            {
                throw new RouteException(HttpStatusCode.BadGateway, ex.Message);
            }
        }
    }
}
