using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Specialized;
using System.Net;
using KliveApi = Omnipotent.Services.KliveAPI.KliveAPI;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.OmniGram
{
    /// <summary>
    /// The /omnigram/* HTTP surface. Registered against the typed KliveAPI (v1 used the reflection path, which
    /// can fail silently and leave a partial route table). Every GET is marked uncacheable: the response cache
    /// is dependency-versioned and OmniGram's state lives in memory, so a cached copy could be stale. Errors
    /// always come back as JSON {"error": "..."} with a real status code (v1's website showed "Success" on 500s).
    /// </summary>
    internal sealed class OmniGramRoutes
    {
        public const long MaxUploadBytes = 500L * 1024 * 1024;
        private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);
        private static readonly JsonSerializerSettings Json = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Converters = { new StringEnumConverter() },
        };

        private readonly OmniGram service;
        private KliveApi? api;

        public OmniGramRoutes(OmniGram service, KliveApi? api)
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
        private static RouteException Forbidden(string message) => new(HttpStatusCode.Forbidden, message);

        // ─────────────────────────────── Registration ───────────────────────────────

        public async Task RegisterAsync()
        {
            var get = HttpMethod.Get;
            var post = HttpMethod.Post;
            var routes = new List<(string Path, Func<UserRequest, Task> Handler, HttpMethod Method, PermissionDef Permission)>
            {
                ("/omnigram/overview", Overview, get, OmniGramPerms.OverviewView),
                ("/omnigram/dashboard-stats", DashboardStats, get, OmniGramPerms.OverviewView),
                ("/omnigram/account", AccountDetail, get, OmniGramPerms.AccountsRead),
                ("/omnigram/analytics", Analytics, get, OmniGramPerms.ContentRead),
                ("/omnigram/posts", Posts, get, OmniGramPerms.ContentRead),
                ("/omnigram/post", PostDetail, get, OmniGramPerms.ContentRead),
                ("/omnigram/media", Media, get, OmniGramPerms.ContentRead),
                ("/omnigram/content/thumb", ContentThumb, get, OmniGramPerms.ContentRead),
                ("/omnigram/library", Library, get, OmniGramPerms.ContentRead),
                ("/omnigram/memescraper/options", MemeScraperOptions, get, OmniGramPerms.AccountsRead),
                ("/omnigram/events", Events, get, OmniGramPerms.ContentRead),
                ("/omnigram/comments", Comments, get, OmniGramPerms.ContentRead),
                ("/omnigram/settings", Settings, get, OmniGramPerms.SettingsView),
                ("/omnigram/connect/status", ConnectStatus, get, OmniGramPerms.SettingsView),

                ("/omnigram/settings/app", SaveApp, post, OmniGramPerms.SettingsManage),
                ("/omnigram/settings/check-public-url", CheckPublicUrl, post, OmniGramPerms.SettingsManage),
                ("/omnigram/settings/remove-v1-passwords", RemoveV1Passwords, post, OmniGramPerms.SettingsManage),
                ("/omnigram/connect/begin", ConnectBegin, post, OmniGramPerms.SettingsManage),
                ("/omnigram/connect/token", ConnectToken, post, OmniGramPerms.SettingsManage),
                ("/omnigram/accounts/refresh-token", AccountsRefreshToken, post, OmniGramPerms.SettingsManage),
                ("/omnigram/accounts/update", AccountsUpdate, post, OmniGramPerms.AccountsAct),
                ("/omnigram/accounts/remove", AccountsRemove, post, OmniGramPerms.AccountsManage),
                ("/omnigram/accounts/refresh", AccountsRefresh, post, OmniGramPerms.AccountsAct),
                ("/omnigram/accounts/plan-now", AccountsPlanNow, post, OmniGramPerms.AccountsAct),
                ("/omnigram/posts/create", PostsCreate, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/update", PostsUpdate, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/approve", PostsApprove, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/publish-now", PostsPublishNow, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/retry", PostsRetry, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/skip", PostsSkip, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/cancel", PostsCancel, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/regenerate-caption", PostsRegenerateCaption, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/swap-content", PostsSwapContent, post, OmniGramPerms.ContentPublish),
                ("/omnigram/posts/delete-remote", PostsDeleteRemote, post, OmniGramPerms.AccountsManage),
                ("/omnigram/library/delete", LibraryDelete, post, OmniGramPerms.ContentPublish),
                ("/omnigram/captions/preview", CaptionPreview, post, OmniGramPerms.ContentPublish),
                ("/omnigram/comments/reply", CommentsReply, post, OmniGramPerms.CommentsAct),
                ("/omnigram/comments/hide", CommentsHide, post, OmniGramPerms.CommentsAct),
                ("/omnigram/comments/delete", CommentsDelete, post, OmniGramPerms.CommentsAct),
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
                    await service.ServiceLogError(ex, $"[OmniGram] failed to register route {path}");
                }
            }

            try
            {
                var target = await ResolveApiAsync();
                // Instagram redirects the browser here; no KM login is involved, so it is open to anybody and only
                // completes flows this server started.
                await target.CreateRoute("/omnigram/oauth/callback", Guard("/omnigram/oauth/callback", OAuthCallback, true), HttpMethod.Get, Perms.Public);
                // Instagram downloads post media from here; links are signed and expire (OmniGramMediaHost).
                await target.CreateRoute(OmniGramMediaHost.RoutePath, PublicMedia, HttpMethod.Get, Perms.Public);
                await target.CreateStreamingRoute("/omnigram/media/upload", Guard("/omnigram/media/upload", MediaUpload, false), HttpMethod.Post, OmniGramPerms.ContentPublish, MaxUploadBytes);
                registered += 3;
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniGram] failed to register the OAuth callback / public media / upload routes");
            }
            await service.ServiceLog($"[OmniGram] {registered}/{routes.Count + 3} HTTP routes registered.", false);
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
                if (isGet) CacheDeps.MarkUncacheable("omnigram live state");
                if (!service.Ready.IsCompleted) await Task.WhenAny(service.Ready, Task.Delay(ReadyWait));
                if (!service.Ready.IsCompletedSuccessfully)
                {
                    string why = service.Ready.IsFaulted
                        ? "OmniGram failed to start: " + service.Ready.Exception?.GetBaseException().Message
                        : "OmniGram is still starting (" + service.StartupState + "); try again in a moment.";
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
                await service.ServiceLogError(ex, $"[OmniGram] {path} failed");
                await Respond(req, new { error = ex.Message }, HttpStatusCode.InternalServerError);
            }
        };

        // ─────────────────────────────── Helpers ───────────────────────────────

        private OmniGramStore Store => service.Store!;

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

        private static bool? OptBool(JObject body, string name) => body[name] == null || body[name]!.Type == JTokenType.Null ? null : InstagramGraphClient.Bool(body[name]);

        private static string? OptString(JObject body, string name) => body[name] == null || body[name]!.Type == JTokenType.Null ? null : body[name]!.ToString();

        private static double? OptDouble(JObject body, string name) => body[name] == null || body[name]!.Type == JTokenType.Null ? null : InstagramGraphClient.Double(body[name]);

        private static T? OptEnum<T>(JObject body, string name) where T : struct, Enum =>
            OptString(body, name) is string s ? Enum.TryParse<T>(s, true, out var v) ? v : throw Bad($"'{name}' must be one of: {string.Join(", ", Enum.GetNames<T>())}.") : null;

        private static DateTime? OptDate(JObject body, string name)
        {
            var t = body[name];
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Date) return OmniGramScheduleMath.AsUtc(t.Value<DateTime>());
            string s = t.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? d.UtcDateTime
                : throw Bad($"'{name}' is not a valid date/time.");
        }

        private static List<string>? OptStringList(JObject body, string name) =>
            body[name] is JArray arr ? arr.Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() : null;

        private static object AppDto(OmniGramAppConfig app) => new
        {
            Configured = app.IsConfigured,
            app.AppId,
            app.RedirectUrl,
            app.VerifiedUtc,
            app.LastVerifyError,
            app.UpdatedUtc,
        };

        private static object ConnectionDto(OmniGramAccount a) => new
        {
            a.AccountId,
            a.Username,
            a.Name,
            a.ProfilePictureUrl,
            a.AccountType,
            a.IgUserId,
            Connected = a.IsConnected,
            Connection = a.Connection,
            a.ConnectionDetail,
            a.ConnectionChangedUtc,
            a.ConnectedVia,
            a.ConnectedUtc,
            a.TokenIssuedUtc,
            a.TokenRefreshedUtc,
            a.TokenExpiresUtc,
            a.Scopes,
            Quota = new { a.Quota.QuotaUsage, a.Quota.QuotaTotal, a.Quota.FetchedUtc },
            Imported = a.Legacy != null,
        };

        private async Task<bool> AiAvailableAsync()
        {
            try { return await service.Captioner!.IsAvailableAsync(CancellationToken.None); }
            catch { return false; }
        }

        private string? MediaToolsProblem() => service.MediaTools is { Available: false } tools ? tools.Status : null;

        private object EngineDto(OmniGramState s, bool aiAvailable)
        {
            var budget = service.ApiBudget!;
            var usage = budget.LastUsage;
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
                    Version = service.InstagramApi?.Version,
                    CallsThisHour = budget.CallsThisHour(),
                    CallsToday = budget.CallsToday(),
                    AppUsagePercent = usage?.AppUsagePercent,
                    AccountUsagePercent = usage?.AccountUsagePercent,
                    RegainAccessMinutes = usage?.RegainAccessMinutes,
                    UsageObservedUtc = usage?.ObservedUtc,
                    ByEndpointToday = budget.CallsByEndpointToday(),
                    ProxyActive = !string.IsNullOrEmpty(service.InstagramApi?.Proxy),
                },
                PublicMedia = new
                {
                    BaseUrl = service.MediaHost?.BaseUrl,
                    Check = s.Engine.PublicUrlCheck,
                    s.Engine.LastExternalMediaFetchUtc,
                    s.Engine.LastExternalMediaFetchAgent,
                },
                MemeScraperReady = service.ContentSources?.CatalogReady ?? false,
                AiAvailable = aiAvailable,
                Ffmpeg = new { Available = service.MediaTools?.Available, Status = service.MediaTools?.Status },
                Migration = s.Engine.MigrationSummary,
                V1PasswordFilesPresent = s.Engine.V1PasswordFilesPresent,
                UnsupportedMetrics = s.Engine.UnsupportedMetrics,
                SyncErrors = service.Sync?.LastErrors.Select(kv => new { Job = kv.Key, Error = kv.Value }).ToList(),
            };
        }

        private static object EventDto(OmniGramState s, OmniGramEvent e) => new
        {
            e.Utc,
            e.Level,
            e.Kind,
            e.Message,
            e.AccountId,
            e.PostId,
            Username = s.Account(e.AccountId)?.Username,
        };

        private static object CandidateDto(ContentCandidate c) => new
        {
            c.Key,
            c.Source,
            Kind = c.IsVideo ? "Video" : "Photo",
            c.Origin,
            c.OriginalUrl,
            c.Views,
            c.Likes,
            c.CreatedUtc,
            OriginalCaption = OmniGramCaptioner.CleanSourceCaption(c.OriginalCaption),
            c.DurationSeconds,
            FileName = Path.GetFileName(c.FilePath),
        };

        private OmniGramAccount RequireAccount(OmniGramState s, string accountId) => s.Account(accountId) ?? throw NotFound("No such account.");

        // ─────────────────────────────── Overview ───────────────────────────────

        private async Task Overview(UserRequest req)
        {
            bool ai = await AiAvailableAsync();
            bool reels = service.ContentSources?.CatalogReady ?? false;
            DateTime now = DateTime.UtcNow;
            string baseUrl = service.MediaHost?.BaseUrl ?? "";
            var dto = Store.Read(s => new
            {
                NowUtc = now,
                Engine = EngineDto(s, ai),
                App = AppDto(s.App),
                Kpis = OmniGramViews.FleetKpis(s, now),
                Accounts = s.Accounts.Values.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase).Select(a => OmniGramViews.AccountSummary(s, a, now)).ToList(),
                Upcoming = s.AllPosts().Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).Take(16).Select(p => OmniGramViews.Summary(p, s.Account(p.AccountId))).ToList(),
                RecentlyPublished = s.AllPosts().Where(p => p.Status == PostStatus.Published).OrderByDescending(p => p.PublishedUtc).Take(12)
                    .Select(p => OmniGramViews.Summary(p, s.Account(p.AccountId))).ToList(),
                Attention = OmniGramViews.Attention(s, now, service.PublishingEnabled, ai, reels, baseUrl, MediaToolsProblem()),
                Events = s.Events.AsEnumerable().Reverse().Take(40).Select(e => EventDto(s, e)).ToList(),
            });
            await Respond(req, dto);
        }

        /// <summary>The home dashboard tile and the Schemes card (keeps v1's field names, adds v2's).</summary>
        private async Task DashboardStats(UserRequest req)
        {
            DateTime now = DateTime.UtcNow;
            string baseUrl = service.MediaHost?.BaseUrl ?? "";
            var dto = Store.Read(s =>
            {
                var k = OmniGramViews.FleetKpis(s, now);
                return new
                {
                    TotalAccounts = s.Accounts.Count,
                    ActiveAccounts = s.Accounts.Values.Count(a => a.IsConnected && !a.Paused && a.Connection == ConnectionState.Healthy),
                    ConnectedAccounts = k.ConnectedAccounts,
                    PausedAccounts = s.Accounts.Values.Count(a => a.Paused),
                    ErrorAccounts = s.Accounts.Values.Count(a => a.Connection is ConnectionState.NeedsReauth or ConnectionState.Restricted or ConnectionState.Error),
                    AutopilotAccounts = k.AutopilotAccounts,
                    PendingCount = k.Pending,
                    FailedCount = k.Failed7d,
                    SuccessRate = k.SuccessRate30d ?? 100,
                    TotalFollowers = k.Followers,
                    FollowerGain7d = k.FollowersDelta7d,
                    PostsThisWeek = k.Published7d,
                    PostsToday = s.AllPosts().Count(p => p.Status == PostStatus.Published && p.PublishedUtc >= now.Date),
                    AvgViews30d = k.AvgViews30d,
                    Reach7d = k.Reach7d,
                    NextPostUtc = k.NextPostUtc,
                    NextPostAccount = k.NextPostAccount,
                    AttentionCount = OmniGramViews.Attention(s, now, service.PublishingEnabled, true, true, baseUrl, MediaToolsProblem()).Count(a => a.Level != "info"),
                };
            });
            await Respond(req, dto);
        }

        private async Task AccountDetail(UserRequest req)
        {
            string accountId = RequiredQuery(req, "accountId");
            DateTime now = DateTime.UtcNow;
            var data = Store.Read(s =>
            {
                var a = RequireAccount(s, accountId);
                var posts = s.PostsOf(accountId);
                var insights = s.InsightsOf(accountId);
                return new
                {
                    Strategy = OmniGramStore.Clone(a.Strategy),
                    Excluded = OmniGramPlanner.ExcludedKeys(s, a, a.Strategy) as IReadOnlySet<string>,
                    Dto = new
                    {
                        NowUtc = now,
                        Summary = OmniGramViews.AccountSummary(s, a, now),
                        Account = new
                        {
                            a.AccountId,
                            a.Username,
                            a.Name,
                            a.AccountType,
                            a.ProfilePictureUrl,
                            ProfileUrl = InstagramGraphClient.ProfileUrl(a.Username),
                            a.Biography,
                            a.Website,
                            a.Autopilot,
                            a.Paused,
                            a.RequireApproval,
                            a.Notes,
                            a.Tags,
                            a.AddedUtc,
                            a.Stats,
                            a.Health,
                            a.Quota,
                            a.Strategy,
                            UsedContentCount = a.UsedContentKeys.Count,
                            RejectedContentCount = a.RejectedContentKeys.Count,
                            Legacy = a.Legacy,
                        },
                        Connection = ConnectionDto(a),
                        Pending = posts.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).Select(p => OmniGramViews.Summary(p, a)).ToList(),
                        History = posts.Where(p => !p.IsPending).OrderByDescending(p => p.PublishedUtc ?? p.UpdatedUtc).Take(200).Select(p => OmniGramViews.Summary(p, a)).ToList(),
                        Events = s.Events.Where(e => e.AccountId == accountId).Reverse().Take(80).Select(e => EventDto(s, e)).ToList(),
                        Comments = insights.Comments.Where(c => c.ParentId == null).Take(40).ToList(),
                        Validation = OmniGramStrategyRules.Validate(a.Strategy, a.Autopilot),
                    },
                };
            });

            object runway;
            try
            {
                var (eligible, next) = service.ContentSources!.Runway(data.Strategy, accountId, data.Excluded, now);
                int perWeek = OmniGramScheduleMath.SlotsPerWeek(data.Strategy);
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

            var json = JObject.FromObject(data.Dto, JsonSerializer.Create(Json));
            json["Runway"] = JToken.FromObject(runway, JsonSerializer.Create(Json));
            await req.ReturnResponse(json.ToString(Formatting.None), "application/json");
        }

        private async Task Analytics(UserRequest req)
        {
            string? accountId = Query(req, "accountId");
            int days = int.TryParse(Query(req, "days"), out var d) ? d : 30;
            DateTime now = DateTime.UtcNow;
            var dto = Store.Read(s =>
            {
                if (accountId != null) RequireAccount(s, accountId);
                return OmniGramViews.Analytics(s, accountId, days, now);
            });
            await Respond(req, dto);
        }

        private async Task Posts(UserRequest req)
        {
            string? accountId = Query(req, "accountId");
            string? status = Query(req, "status");
            int limit = Math.Clamp(int.TryParse(Query(req, "limit"), out var l) ? l : 200, 1, 1000);
            PostStatus? filter = status != null && Enum.TryParse<PostStatus>(status, true, out var st) ? st : null;
            var dto = Store.Read(s => s.AllPosts()
                .Where(p => (accountId == null || p.AccountId == accountId) && (filter == null || p.Status == filter))
                .OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc)
                .Take(limit)
                .Select(p => OmniGramViews.Summary(p, s.Account(p.AccountId)))
                .ToList());
            await Respond(req, dto);
        }

        private async Task PostDetail(UserRequest req)
        {
            string postId = RequiredQuery(req, "postId");
            var dto = Store.Read(s =>
            {
                var p = s.FindPost(postId) ?? throw NotFound("No such post.");
                var account = s.Account(p.AccountId);
                return new
                {
                    Post = OmniGramViews.Summary(p, account),
                    Media = p.Media.Select((m, i) => new
                    {
                        Index = i,
                        m.MimeType,
                        m.Bytes,
                        m.Width,
                        m.Height,
                        m.DurationSeconds,
                        HasThumbnail = m.ThumbnailPath != null,
                        Exists = File.Exists(m.Path),
                        FileName = Path.GetFileName(m.Path),
                        Prepared = m.PreparedPath != null && File.Exists(m.PreparedPath),
                        m.PreparedBytes,
                        m.PreparedFor,
                        m.PrepareNotes,
                        m.PrepareError,
                    }).ToList(),
                    p.AttemptLog,
                    MetricsHistory = p.Metrics.History,
                    Metrics = p.Metrics,
                    p.CaptionInfo,
                    p.Content,
                    p.Reel,
                    Publish = new
                    {
                        p.Publish.Stage,
                        p.Publish.ContainerStatus,
                        p.Publish.ContainerStatusDetail,
                        p.Publish.ContainerCreatedUtc,
                        p.Publish.NextCheckUtc,
                        p.Publish.StatusChecks,
                        p.Publish.MediaFetches,
                        p.Publish.LastMediaFetchUtc,
                        p.Publish.LastMediaFetchAgent,
                    },
                    Comments = account == null ? new List<CommentItem>() : s.InsightsOf(account.AccountId).Comments.Where(c => c.OmniPostId == p.PostId || (p.IgMediaId != null && c.MediaId == p.IgMediaId)).ToList(),
                    p.Deferrals,
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
            string variant = Query(req, "variant") ?? "thumb";
            var media = Store.Read(s =>
            {
                var p = s.FindPost(postId) ?? throw NotFound("No such post.");
                return index >= 0 && index < p.Media.Count ? OmniGramStore.Clone(p.Media[index]) : throw NotFound("No such media.");
            });
            string? path = variant switch { "full" => media.Path, "prepared" => media.PreparedPath, _ => media.ThumbnailPath };
            if (path == null || !File.Exists(path)) throw NotFound(variant == "thumb" ? "No thumbnail yet." : "The media file is missing.");
            string type = variant switch
            {
                "full" => media.MimeType,
                "prepared" => media.PreparedMimeType ?? OmniGramMediaTools.MimeFor(path),
                _ => OmniGramMediaTools.MimeFor(path),
            };
            await StreamFileAsync(req, path, type);
        }

        private async Task ContentThumb(UserRequest req)
        {
            string accountId = RequiredQuery(req, "accountId");
            string key = RequiredQuery(req, "key");
            var strategy = Store.Read(s => OmniGramStore.Clone(RequireAccount(s, accountId).Strategy));
            var candidate = service.ContentSources!.Resolve(strategy, accountId, key, DateTime.UtcNow) ?? throw NotFound("No such content.");
            string cachePath = Path.Combine(Store.CacheDirectory, $"cand-{OmniGramScheduleMath.Fnv1a(key):x8}-{key.Length}.jpg");
            if (!File.Exists(cachePath))
            {
                bool ok = await service.MediaTools!.CreateThumbnailAsync(candidate.FilePath, cachePath, null, CancellationToken.None);
                if (!ok)
                {
                    if (OmniGramMediaTools.IsImage(candidate.FilePath))
                    {
                        await StreamFileAsync(req, candidate.FilePath, OmniGramMediaTools.MimeFor(candidate.FilePath));
                        return;
                    }
                    throw NotFound("A preview could not be made (is FFmpeg installed?).");
                }
            }
            await StreamFileAsync(req, cachePath, "image/jpeg");
        }

        private async Task Library(UserRequest req)
        {
            string accountId = RequiredQuery(req, "accountId");
            var used = Store.Read(s => RequireAccount(s, accountId).UsedContentKeys.ToHashSet(StringComparer.Ordinal));
            string dir = service.ContentSources!.LibraryDirectoryFor(accountId);
            var files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles().Where(f => OmniGramMediaTools.IsSupported(f.Name)).OrderBy(f => f.CreationTimeUtc).Select(f => new
                {
                    FileName = f.Name,
                    Key = OmniGramContentSources.LibraryKey(f.Name),
                    Kind = OmniGramMediaTools.IsVideo(f.Name) ? "Video" : "Photo",
                    Bytes = f.Length,
                    AddedUtc = f.CreationTimeUtc,
                    Used = used.Contains(OmniGramContentSources.LibraryKey(f.Name)),
                }).ToList<object>()
                : new List<object>();
            await Respond(req, new { AccountId = accountId, Files = files });
        }

        private async Task MemeScraperOptions(UserRequest req) => await Respond(req, service.ContentSources!.DescribeCatalog());

        private async Task Events(UserRequest req)
        {
            string? accountId = Query(req, "accountId");
            int limit = Math.Clamp(int.TryParse(Query(req, "limit"), out var l) ? l : 100, 1, 1000);
            var dto = Store.Read(s => s.Events.Where(e => accountId == null || e.AccountId == accountId).Reverse().Take(limit).Select(e => EventDto(s, e)).ToList());
            await Respond(req, dto);
        }

        private async Task Comments(UserRequest req)
        {
            string accountId = RequiredQuery(req, "accountId");
            int limit = Math.Clamp(int.TryParse(Query(req, "limit"), out var l) ? l : 100, 1, 400);
            var dto = Store.Read(s =>
            {
                RequireAccount(s, accountId);
                var insights = s.InsightsOf(accountId);
                var mediaById = insights.Index.ToDictionary(m => m.Id, StringComparer.Ordinal);
                return insights.Comments.Where(c => c.ParentId == null).Take(limit).Select(c => new
                {
                    c.Id,
                    c.MediaId,
                    c.OmniPostId,
                    c.Username,
                    c.Text,
                    c.Utc,
                    c.LikeCount,
                    c.Hidden,
                    c.Own,
                    c.Replied,
                    Replies = insights.Comments.Where(r => r.ParentId == c.Id).OrderBy(r => r.Utc).ToList(),
                    Media = mediaById.TryGetValue(c.MediaId, out var m) ? new { m.Permalink, m.Caption, m.ThumbnailUrl, m.ProductType } : null,
                }).ToList();
            });
            await Respond(req, dto);
        }

        private async Task Settings(UserRequest req)
        {
            bool ai = await AiAvailableAsync();
            var dto = Store.Read(s => new
            {
                App = AppDto(s.App),
                Accounts = s.Accounts.Values.OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase).Select(ConnectionDto).ToList(),
                Engine = EngineDto(s, ai),
                Presets = OmniGramStrategyRules.PresetNames,
                DefaultStrategy = OmniGramStrategy.CreateDefault(),
                Scopes = InstagramGraphClient.Scopes,
                PublicMediaRoute = OmniGramMediaHost.RoutePath,
            });
            await Respond(req, dto);
        }

        // ─────────────────────────────── App + connections ───────────────────────────────

        private async Task SaveApp(UserRequest req)
        {
            var body = Body(req);
            var (saved, message) = await service.Auth!.SaveAppAsync(OptString(body, "appId"), OptString(body, "appSecret"), OptString(body, "redirectUrl"));
            if (!saved) throw Bad(message);
            await Respond(req, new { saved, message, App = Store.Read(s => AppDto(s.App)) });
        }

        private async Task CheckPublicUrl(UserRequest req)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromSeconds(30) };
            var check = await service.MediaHost!.CheckAsync(http, cts.Token);
            await Respond(req, check);
        }

        private async Task RemoveV1Passwords(UserRequest req)
        {
            var (files, sessions) = OmniGramMigration.RemoveV1Credentials(service.V1Root);
            bool still = OmniGramMigration.V1PasswordsPresent(service.V1Root);
            Store.Mutate(s =>
            {
                s.Engine.V1PasswordFilesPresent = still;
                s.MarkEngine();
                s.AddEvent(EventLevel.Info, "v1.credentials-removed", $"Removed the plain-text passwords from {files} OmniGram v1 account file(s) and deleted {sessions} v1 session file(s).");
            });
            await Store.FlushAsync();
            await Respond(req, new { files, sessions, remaining = still });
        }

        private async Task ConnectBegin(UserRequest req)
        {
            var body = Body(req);
            string? reconnect = OptString(body, "reconnectAccountId");
            if (reconnect != null && Store.Read(s => s.Account(reconnect)) == null) throw NotFound("No such account.");
            try
            {
                var (flowId, url) = await service.Auth!.BeginAsync(reconnect, OptString(body, "preset"), OptBool(body, "autopilot") ?? false);
                await Respond(req, new { flowId, authorizationUrl = url, redirectUrl = service.Auth.RedirectUrl });
            }
            catch (InvalidOperationException ex)
            {
                throw Bad(ex.Message);
            }
        }

        private async Task ConnectStatus(UserRequest req)
        {
            string flowId = RequiredQuery(req, "flowId");
            var flow = service.Auth!.GetFlow(flowId) ?? throw NotFound("No such authorization (it may have expired).");
            var account = flow.AccountId == null ? null : Store.Read(s => s.Account(flow.AccountId) is OmniGramAccount a ? ConnectionDto(a) : null);
            await Respond(req, new { flow.FlowId, flow.State, flow.Error, flow.AccountId, flow.CreatedUtc, flow.CompletedUtc, Account = account });
        }

        private async Task ConnectToken(UserRequest req)
        {
            var body = Body(req);
            string token = Required(body, "token");
            string? reconnect = OptString(body, "reconnectAccountId");
            if (reconnect != null && Store.Read(s => s.Account(reconnect)) == null) throw NotFound("No such account.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ConnectResult result;
            try
            {
                result = await service.Auth!.ConnectAsync(token, null, null, "token", reconnect, OptString(body, "preset"), OptBool(body, "autopilot") ?? false, cts.Token);
            }
            catch (InstagramApiException ex)
            {
                throw Bad($"Instagram could not be reached to check the token ({ex.Code}): {ex.Message}");
            }
            if (!result.Ok) throw Bad(result.Message);
            service.Sync!.ExpediteAccount(result.AccountId!);
            service.Engine?.WakeSync();
            service.Engine?.WakePlanner(result.AccountId);
            await Respond(req, result);
        }

        private async Task AccountsRefreshToken(UserRequest req)
        {
            string accountId = Required(Body(req), "accountId");
            if (Store.Read(s => s.Account(accountId)) == null) throw NotFound("No such account.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                bool refreshed = await service.Auth!.RefreshTokenIfDueAsync(accountId, force: true, cts.Token);
                await Respond(req, new { refreshed, Account = Store.Read(s => ConnectionDto(s.Account(accountId)!)) });
            }
            catch (AccountUnavailableException ex) { throw Conflict(ex.Message); }
            catch (InstagramApiException ex) { throw Bad($"Instagram refused ({ex.Code}): {ex.Message}"); }
        }

        private async Task OAuthCallback(UserRequest req)
        {
            AuthCallbackResult result;
            try
            {
                result = await service.Auth!.HandleCallbackAsync(req.userParameters ?? new NameValueCollection(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = new AuthCallbackResult { Ok = false, Title = "Authorization failed", Message = ex.Message };
            }
            if (result.Ok && result.AccountId != null)
            {
                service.Sync?.ExpediteAccount(result.AccountId);
                service.Engine?.WakeSync();
                service.Engine?.WakePlanner(result.AccountId);
            }
            await req.ReturnResponse(CallbackPage(result), "text/html", code: result.Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        }

        private static string CallbackPage(AuthCallbackResult r)
        {
            static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
            string accent = r.Ok ? "#4ec98a" : "#ff7b7b";
            string payload = JsonConvert.SerializeObject(new { source = "omnigram", type = "oauth", ok = r.Ok, flowId = r.FlowId, accountId = r.AccountId, message = r.Message });
            return $@"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>OmniGram · {E(r.Title)}</title>
<style>body{{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0d0a;color:#e9f1e5;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}}
.card{{max-width:460px;margin:24px;padding:28px 30px;background:#12150f;border:1px solid rgba(146,196,130,.14);border-radius:14px}}
h1{{margin:0 0 8px;font-size:20px;color:{accent}}}p{{margin:0;color:#b3c2ad}}small{{display:block;margin-top:16px;color:#93a48d}}</style></head>
<body><div class=""card""><h1>{(r.Ok ? "✓" : "⚠")} {E(r.Title)}</h1><p>{E(r.Message)}</p><small>{(r.Ok ? "This tab will close by itself." : "You can close this tab and try again from OmniGram.")}</small></div>
<script>(function(){{var m={payload};try{{if(window.opener)window.opener.postMessage(m,'*');}}catch(e){{}}
{(r.Ok ? "setTimeout(function(){try{window.close();}catch(e){}},2500);" : "")}}})();</script></body></html>";
        }

        /// <summary>
        /// Serves a post's prepared media to Instagram (or anyone holding a valid signed link), with Range support.
        /// Not wrapped in <see cref="Guard"/>: a bad link answers a bare 404/403 and never waits on startup.
        /// </summary>
        private async Task PublicMedia(UserRequest req)
        {
            CacheDeps.MarkUncacheable("omnigram signed media");
            var host = service.MediaHost;
            if (host == null || !service.Ready.IsCompletedSuccessfully)
            {
                await req.ReturnResponse("Not ready.", "text/plain", code: HttpStatusCode.ServiceUnavailable);
                return;
            }
            if (!host.TryValidate(Query(req, "t"), out string postId, out int index, out string? error))
            {
                await req.ReturnResponse(error == "expired" ? "This link has expired." : "Not found.", "text/plain", code: error == "expired" ? HttpStatusCode.Gone : HttpStatusCode.NotFound);
                return;
            }
            var resolved = host.Resolve(postId, index);
            if (resolved == null)
            {
                await req.ReturnResponse("Not found.", "text/plain", code: HttpStatusCode.NotFound);
                return;
            }
            bool head = string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            if (!head) host.RecordFetch(postId, req.req.UserAgent);
            await StreamFileAsync(req, resolved.Value.Path, resolved.Value.MimeType, publicCache: false);
        }

        /// <summary>Sends a file a buffer at a time, honouring a single HTTP Range (as KliveCloud does).</summary>
        private static async Task StreamFileAsync(UserRequest req, string path, string mimeType, bool publicCache = false)
        {
            var info = new FileInfo(path);
            long length = info.Length;
            bool head = string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            var headers = new NameValueCollection { { "Accept-Ranges", "bytes" }, { "Cache-Control", publicCache ? "public, max-age=300" : "private, no-store" }, { "X-Robots-Tag", "noindex" } };
            long start = 0, end = length - 1;
            HttpStatusCode code = HttpStatusCode.OK;
            string? range = req.req.Headers["Range"];
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !range.Contains(','))
            {
                string[] parts = range["bytes=".Length..].Split('-');
                bool ok = parts.Length == 2;
                if (ok && parts[0].Length == 0 && long.TryParse(parts[1], out long suffix))
                {
                    start = Math.Max(0, length - Math.Min(suffix, length));
                }
                else if (ok && long.TryParse(parts[0], out long s))
                {
                    start = s;
                    if (parts[1].Length > 0 && long.TryParse(parts[1], out long e)) end = Math.Min(e, length - 1);
                }
                else ok = false;
                if (!ok || start >= length || start > end)
                {
                    headers.Add("Content-Range", $"bytes */{length}");
                    await req.ReturnBinaryResponse(Array.Empty<byte>(), mimeType, (HttpStatusCode)416, headers);
                    return;
                }
                code = HttpStatusCode.PartialContent;
                headers.Add("Content-Range", $"bytes {start}-{end}/{length}");
            }
            long count = end - start + 1;
            using Stream output = req.PrepareStreamResponse(mimeType, count, code, headers);
            if (head) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[262144];
            long remaining = count;
            try
            {
                while (remaining > 0)
                {
                    int read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // The client hung up mid-transfer (fetchers often read only the start of a video).
            }
        }

        // ─────────────────────────────── Accounts ───────────────────────────────

        private async Task AccountsUpdate(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            bool? autopilot = OptBool(body, "autopilot");
            bool? paused = OptBool(body, "paused");
            bool? approval = OptBool(body, "requireApproval");
            string? notes = OptString(body, "notes");
            var tags = OptStringList(body, "tags");
            OmniGramStrategy? strategy = null;
            if (body["strategy"] is JObject strategyJson)
            {
                try { strategy = strategyJson.ToObject<OmniGramStrategy>(JsonSerializer.Create(Json)); }
                catch (Exception ex) { throw Bad("The strategy could not be read: " + ex.Message); }
                if (strategy == null) throw Bad("The strategy is empty.");
                OmniGramStrategyRules.Normalize(strategy);
            }
            if ((autopilot != null || approval != null || strategy != null) && !req.Can(OmniGramPerms.AccountsManage))
                throw Forbidden("Changing autopilot, approval or the strategy needs the 'Manage accounts' permission.");

            DateTime now = DateTime.UtcNow;
            var notesOut = new List<string>();
            Store.Mutate(s =>
            {
                var a = RequireAccount(s, accountId);
                var effective = strategy ?? a.Strategy;
                bool willAutopilot = autopilot ?? a.Autopilot;
                var errors = OmniGramStrategyRules.Validate(effective, willAutopilot);
                if (errors.Count > 0) throw Bad(string.Join(" ", errors));
                if (willAutopilot && !a.IsConnected) throw Conflict($"Connect @{a.Username} to Instagram before switching autopilot on.");

                if (strategy != null)
                {
                    a.Strategy = strategy;
                    if (a.Health.ContentWarning != null) { a.Health.ContentWarning = null; a.Health.ContentWarningUtc = null; }
                    s.AddEvent(EventLevel.Info, "strategy.updated", $"@{a.Username}: strategy updated.", accountId, utc: now);
                }
                if (approval != null && approval != a.RequireApproval)
                {
                    a.RequireApproval = approval.Value;
                    if (!a.RequireApproval)
                    {
                        foreach (var p in s.PostsOf(accountId).Where(p => p.Status == PostStatus.AwaitingApproval))
                        {
                            p.Status = PostStatus.Ready;
                            p.Approved = true;
                            s.Touch(p, now);
                        }
                    }
                }
                if (paused != null && paused != a.Paused)
                {
                    a.Paused = paused.Value;
                    s.AddEvent(EventLevel.Info, a.Paused ? "account.paused" : "account.resumed", $"@{a.Username} {(a.Paused ? "paused" : "resumed")}.", accountId, utc: now);
                }
                if (autopilot != null && autopilot != a.Autopilot)
                {
                    a.Autopilot = autopilot.Value;
                    if (!a.Autopilot)
                    {
                        int cancelled = 0;
                        foreach (var p in s.PostsOf(accountId).Where(p => p.Origin == PostOrigin.Autopilot && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                        {
                            p.Status = PostStatus.Cancelled;
                            p.LastError = "Autopilot was switched off.";
                            if (p.Content != null) a.UsedContentKeys.Remove(p.Content.Key);
                            s.Touch(p, now);
                            cancelled++;
                        }
                        if (cancelled > 0) notesOut.Add($"Cancelled {cancelled} planned post(s).");
                    }
                    s.AddEvent(EventLevel.Info, a.Autopilot ? "autopilot.on" : "autopilot.off", $"@{a.Username}: autopilot {(a.Autopilot ? "on" : "off")}.", accountId, utc: now);
                }
                if (notes != null) a.Notes = notes.Length > 4000 ? notes[..4000] : notes;
                if (tags != null) a.Tags = tags.Take(30).ToList();
                s.MarkAccount(accountId);
            });
            if (strategy != null) service.Planner!.ReconcileSlots(accountId);
            service.Engine?.WakePlanner(accountId);
            service.Engine?.WakePublisher();
            await Respond(req, new { updated = true, Notes = notesOut });
        }

        private async Task AccountsRemove(UserRequest req)
        {
            string accountId = Required(Body(req), "accountId");
            DateTime now = DateTime.UtcNow;
            string username = Store.Mutate(s =>
            {
                var a = RequireAccount(s, accountId);
                if (s.PostsOf(accountId).Any(p => p.Status == PostStatus.Publishing))
                    throw Conflict("A post is being published right now; try again in a minute.");
                s.Accounts.Remove(accountId);
                s.Posts.Remove(accountId);
                s.Insights.Remove(accountId);
                s.AddEvent(EventLevel.Info, "account.removed", $"Stopped managing @{a.Username}. Its history was archived; nothing was deleted on Instagram.", utc: now);
                return a.Username;
            });
            await Store.ArchiveAccountFilesAsync(accountId);
            await Store.FlushAsync();
            await Respond(req, new { removed = true, username });
        }

        private async Task AccountsRefresh(UserRequest req)
        {
            string accountId = Required(Body(req), "accountId");
            if (Store.Read(s => s.Account(accountId)) == null) throw NotFound("No such account.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var errors = await service.Sync!.SyncAccountNowAsync(accountId, cts.Token);
            await Respond(req, new { refreshed = true, Errors = errors });
        }

        private async Task AccountsPlanNow(UserRequest req)
        {
            string accountId = Required(Body(req), "accountId");
            var account = Store.Read(s => RequireAccount(s, accountId) is { } a ? (a.Autopilot, a.Paused, a.IsConnected) : default);
            if (!account.Autopilot) throw Conflict("Autopilot is off for this account; switch it on first.");
            if (account.Paused) throw Conflict("The account is paused.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var outcome = await service.Planner!.PlanAccountAsync(accountId, cts.Token);
            await service.Planner.PrepareDueAsync(4, cts.Token);
            service.Engine?.WakePublisher();
            await Respond(req, outcome);
        }

        // ─────────────────────────────── Posts ───────────────────────────────

        private sealed class ResolvedMedia
        {
            public string Path = "";
            public bool IsVideo;
            public string? LibraryKey;
        }

        private List<ResolvedMedia> ResolveComposeMedia(JArray items, string accountId)
        {
            var result = new List<ResolvedMedia>();
            foreach (var item in items)
            {
                if (item is not JObject o) throw Bad("Each media item must be an object.");
                if (OptString(o, "mediaId") is string mediaId)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(mediaId, "^[a-f0-9]{12}$")) throw Bad("Unknown upload.");
                    string? file = Directory.Exists(Store.UploadsDirectory) ? Directory.EnumerateFiles(Store.UploadsDirectory, mediaId + ".*").FirstOrDefault() : null;
                    if (file == null) throw Bad("An uploaded file has expired; upload it again.");
                    result.Add(new ResolvedMedia { Path = file, IsVideo = OmniGramMediaTools.IsVideo(file) });
                }
                else if (OptString(o, "libraryFile") is string fileName)
                {
                    string path = Path.Combine(service.ContentSources!.LibraryDirectoryFor(accountId), Path.GetFileName(fileName));
                    if (!File.Exists(path)) throw Bad($"The library file {fileName} does not exist.");
                    result.Add(new ResolvedMedia { Path = path, IsVideo = OmniGramMediaTools.IsVideo(path), LibraryKey = OmniGramContentSources.LibraryKey(Path.GetFileName(path)) });
                }
                else throw Bad("Each media item needs a mediaId or a libraryFile.");
            }
            return result;
        }

        private static void ValidateMediaForFormat(PostFormat format, List<ResolvedMedia> media)
        {
            switch (format)
            {
                case PostFormat.Reel when media.Count != 1 || !media[0].IsVideo:
                    throw Bad("A reel needs exactly one video.");
                case PostFormat.Photo when media.Count != 1 || media[0].IsVideo:
                    throw Bad("A photo post needs exactly one image (use a reel for video).");
                case PostFormat.Story when media.Count != 1:
                    throw Bad("A story needs exactly one image or video.");
                case PostFormat.Carousel when media.Count is < 2 or > 10:
                    throw Bad("A carousel needs 2 to 10 images or videos.");
            }
        }

        private async Task PostsCreate(UserRequest req)
        {
            var body = Body(req);
            var accountIds = OptStringList(body, "accountIds") ?? (OptString(body, "accountId") is string single ? new List<string> { single } : new List<string>());
            if (accountIds.Count == 0) throw Bad("Choose at least one account.");
            var format = OptEnum<PostFormat>(body, "format") ?? throw Bad("'format' is required (Reel, Photo, Carousel or Story).");
            if (body["media"] is not JArray mediaItems || mediaItems.Count == 0) throw Bad("Add the photo or video to post.");
            var captionMode = OptEnum<CaptionMode>(body, "captionMode") ?? CaptionMode.Manual;
            if (captionMode is not (CaptionMode.Manual or CaptionMode.AI or CaptionMode.None)) throw Bad("'captionMode' must be Manual, AI or None.");
            string? caption = OptString(body, "caption")?.Trim();
            if (captionMode == CaptionMode.Manual && caption?.Length > IgCaption.MaxCaptionLength) throw Bad($"The caption is too long ({IgCaption.MaxCaptionLength:N0} characters max).");
            DateTime now = DateTime.UtcNow;
            DateTime? scheduled = OptDate(body, "scheduledUtc");
            if (scheduled != null && scheduled < now.AddMinutes(-5)) throw Bad("The scheduled time is in the past.");
            var hashtags = OptStringList(body, "hashtags");
            var placement = OptEnum<HashtagPlacement>(body, "hashtagPlacement");
            bool useAccountExtras = OptBool(body, "useAccountExtras") ?? true;
            var reel = new ReelSettings
            {
                ShareToFeed = OptBool(body, "shareToFeed") ?? true,
                Trial = OptEnum<TrialReelMode>(body, "trial") ?? TrialReelMode.Off,
                CoverFrameSeconds = OptDouble(body, "coverFrameSeconds"),
                Collaborators = (OptStringList(body, "collaborators") ?? new List<string>()).Select(c => c.TrimStart('@')).Take(3).ToList(),
                AlsoPostStory = OptBool(body, "alsoPostStory") ?? false,
            };

            var created = new List<object>();
            foreach (string accountId in accountIds)
            {
                var media = ResolveComposeMedia(mediaItems, accountId);
                ValidateMediaForFormat(format, media);
                string postId = Store.Mutate(s =>
                {
                    var a = RequireAccount(s, accountId);
                    var post = new OmniGramPost
                    {
                        AccountId = accountId,
                        Origin = PostOrigin.Manual,
                        Format = format,
                        Status = PostStatus.Planned,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                        ScheduledUtc = scheduled ?? now,
                        ManualOverride = scheduled == null,
                        Approved = true,
                        Media = media.Select(m => new PostMedia { Path = m.Path, MimeType = OmniGramMediaTools.MimeFor(m.Path), Bytes = new FileInfo(m.Path).Length }).ToList(),
                        Caption = captionMode == CaptionMode.Manual ? caption ?? "" : null,
                        CaptionPending = captionMode == CaptionMode.AI && format != PostFormat.Story,
                        CaptionInfo = new CaptionInfo { Mode = format == PostFormat.Story ? CaptionMode.None : captionMode, Direction = captionMode == CaptionMode.AI ? OptString(body, "direction") : null, Edited = captionMode == CaptionMode.Manual },
                        // Typed hashtags first, then (optionally) the account's own fixed + rotating ones.
                        Hashtags = IgCaption.NormalizeHashtags((hashtags ?? new List<string>())
                            .Concat(useAccountExtras ? OmniGramPlanner.ComposeHashtags(a.Strategy, Array.Empty<string>()) : Enumerable.Empty<string>()), IgCaption.MaxHashtags),
                        HashtagPlacement = placement ?? a.Strategy.HashtagPlacement,
                        FirstComment = OptString(body, "firstComment")?.Trim() is { Length: > 0 } fc ? fc : null,
                        AltText = OptString(body, "altText")?.Trim() is { Length: > 0 } alt ? alt : null,
                        Footer = useAccountExtras && !string.IsNullOrWhiteSpace(a.Strategy.CaptionFooter) ? a.Strategy.CaptionFooter : null,
                        Reel = OmniGramStore.Clone(reel),
                        LabelAsAiGenerated = OptBool(body, "labelAsAiGenerated") ?? false,
                        Content = media.Count == 1 && media[0].LibraryKey != null ? new ContentRef { Kind = ContentSourceKind.Library, Key = media[0].LibraryKey!, Origin = "library" } : null,
                    };
                    if (format == PostFormat.Story) post.Caption = "";
                    s.PostsOf(accountId).Add(post);
                    if (post.Content != null) a.UsedContentKeys.Add(post.Content.Key);
                    s.MarkPosts(accountId);
                    s.MarkAccount(accountId);
                    s.AddEvent(EventLevel.Info, "post.created",
                        $"@{a.Username}: {(scheduled == null ? "publishing" : "scheduled")} a {OmniGramPublisher.FormatNoun(format)}{(scheduled == null ? " now" : $" for {scheduled:ddd d MMM HH:mm} UTC")}.",
                        accountId, post.PostId, now);
                    return post.PostId;
                });
                created.Add(new { AccountId = accountId, PostId = postId });
            }
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, new { created });
        }

        private async Task PostsUpdate(UserRequest req)
        {
            var body = Body(req);
            string postId = Required(body, "postId");
            DateTime now = DateTime.UtcNow;
            string? caption = OptString(body, "caption");
            var hashtags = OptStringList(body, "hashtags");
            var placement = OptEnum<HashtagPlacement>(body, "hashtagPlacement");
            DateTime? scheduled = OptDate(body, "scheduledUtc");
            var dto = Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (!(post.Status is PostStatus.Draft or PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                    throw Conflict("Only a post that has not been published yet can be edited.");
                if (caption != null)
                {
                    if (caption.Length > IgCaption.MaxCaptionLength) throw Bad($"The caption is too long ({IgCaption.MaxCaptionLength:N0} characters max).");
                    post.Caption = caption.Trim();
                    post.CaptionPending = false;
                    post.CaptionInfo.Edited = true;
                    post.CaptionInfo.Error = null;
                    if (post.BlockedReason?.StartsWith("Waiting for an AI caption") == true) post.BlockedReason = null;
                }
                if (hashtags != null) post.Hashtags = IgCaption.NormalizeHashtags(hashtags, IgCaption.MaxHashtags);
                if (placement != null) post.HashtagPlacement = placement.Value;
                if (body["firstComment"] != null) post.FirstComment = OptString(body, "firstComment")?.Trim() is { Length: > 0 } fc ? fc : null;
                if (body["altText"] != null) post.AltText = OptString(body, "altText")?.Trim() is { Length: > 0 } alt ? alt : null;
                if (scheduled != null)
                {
                    if (scheduled < now.AddMinutes(-5)) throw Bad("The scheduled time is in the past.");
                    post.ScheduledUtc = scheduled.Value;
                    post.ManualOverride = true;
                    post.NextAttemptUtc = null;
                }
                if (OptBool(body, "shareToFeed") is bool feed) post.Reel.ShareToFeed = feed;
                if (OptEnum<TrialReelMode>(body, "trial") is TrialReelMode trial) post.Reel.Trial = trial;
                if (body["coverFrameSeconds"] != null) post.Reel.CoverFrameSeconds = OptDouble(body, "coverFrameSeconds");
                if (OptBool(body, "alsoPostStory") is bool story) post.Reel.AlsoPostStory = story;
                if (post.Status == PostStatus.Planned && !post.CaptionPending && post.Media.All(m => m.PreparedPath != null && m.PreparedFor == post.Format))
                    post.Status = s.Account(post.AccountId)?.RequireApproval == true && !post.Approved ? PostStatus.AwaitingApproval : PostStatus.Ready;
                s.Touch(post, now);
                return OmniGramViews.Summary(post, s.Account(post.AccountId));
            });
            service.Engine?.WakePublisher();
            await Respond(req, dto);
        }

        private async Task PostsApprove(UserRequest req)
        {
            var body = Body(req);
            string? postId = OptString(body, "postId");
            string? accountId = OptString(body, "accountId");
            if (postId == null && accountId == null) throw Bad("Give a postId, or an accountId to approve all of its waiting posts.");
            DateTime now = DateTime.UtcNow;
            int approved = Store.Mutate(s =>
            {
                var targets = postId != null
                    ? new[] { s.FindPost(postId) ?? throw NotFound("No such post.") }.ToList()
                    : s.PostsOf(accountId!).Where(p => p.Status == PostStatus.AwaitingApproval).ToList();
                foreach (var p in targets)
                {
                    p.Approved = true;
                    if (p.Status == PostStatus.AwaitingApproval) p.Status = PostStatus.Ready;
                    s.Touch(p, now);
                }
                return targets.Count;
            });
            service.Engine?.WakePublisher();
            await Respond(req, new { approved });
        }

        private async Task PostsPublishNow(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (!(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Draft))
                    throw Conflict(post.Status == PostStatus.Publishing ? "It is being published right now." : "Only a post that has not been published can be published now.");
                post.ScheduledUtc = now;
                post.ManualOverride = true;
                post.Approved = true;
                post.NextAttemptUtc = null;
                if (post.Status == PostStatus.AwaitingApproval) post.Status = PostStatus.Ready;
                s.Touch(post, now);
            });
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, new { queued = true });
        }

        private async Task PostsRetry(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (post.Status is not (PostStatus.Failed or PostStatus.Skipped or PostStatus.Ready or PostStatus.Planned))
                    throw Conflict("Only a failed, skipped or waiting post can be retried.");
                post.Status = PostStatus.Planned;
                post.Attempts = 0;
                post.Deferrals = 0;
                post.NextAttemptUtc = null;
                post.BlockedReason = null;
                post.ManualOverride = true;
                post.Approved = true;
                post.Publish.Stage = PublishStage.None;
                if (post.ScheduledUtc < now) post.ScheduledUtc = now;
                foreach (var m in post.Media) { m.PrepareError = null; m.PrepareAttemptUtc = null; }
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.retry", "Retrying a post on request.", post.AccountId, post.PostId, now);
            });
            service.Engine?.WakePlanner();
            service.Engine?.WakePublisher();
            await Respond(req, new { queued = true });
        }

        private async Task PostsSkip(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (!(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)) throw Conflict("Only a pending post can be skipped.");
                post.Status = PostStatus.Skipped;
                post.LastError = "Skipped by hand.";
                s.Touch(post, now);
            });
            service.Engine?.WakePlanner();
            await Respond(req, new { skipped = true });
        }

        private async Task PostsCancel(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (!(post.Status is PostStatus.Draft or PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)) throw Conflict("Only a pending post can be cancelled.");
                post.Status = PostStatus.Cancelled;
                post.LastError = "Cancelled by hand.";
                if (post.Content != null && s.Account(post.AccountId) is { } a) { a.UsedContentKeys.Remove(post.Content.Key); s.MarkAccount(a.AccountId); }
                s.Touch(post, now);
            });
            await Respond(req, new { cancelled = true });
        }

        private async Task PostsRegenerateCaption(UserRequest req)
        {
            var body = Body(req);
            string postId = Required(body, "postId");
            string? instruction = OptString(body, "instruction");
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (!(post.Status is PostStatus.Draft or PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)) throw Conflict("Only an unpublished post can get a new caption.");
                if (post.Format == PostFormat.Story) throw Conflict("Stories have no caption.");
                post.CaptionInfo.Mode = CaptionMode.AI;
                post.CaptionInfo.Direction = string.IsNullOrWhiteSpace(instruction) ? post.CaptionInfo.Direction : instruction.Trim();
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try
            {
                await service.Planner!.PreparePostAsync(postId, forceRegenerate: true, instruction, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new RouteException(HttpStatusCode.GatewayTimeout, "The language model took too long; try again.");
            }
            var dto = Store.Read(s => s.FindPost(postId) is { } p ? OmniGramViews.Summary(p, s.Account(p.AccountId)) : null);
            if (dto?.CaptionError != null && dto.CaptionPending) throw new RouteException(HttpStatusCode.BadGateway, dto.CaptionError);
            await Respond(req, dto!);
        }

        private async Task PostsSwapContent(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (ok, message) = await service.Planner!.SwapContentAsync(postId, cts.Token);
            if (!ok) throw Conflict(message);
            service.Engine?.WakePlanner();
            await Respond(req, new { swapped = true, message });
        }

        private async Task PostsDeleteRemote(UserRequest req)
        {
            string postId = Required(Body(req), "postId");
            var target = Store.Read(s =>
            {
                var post = s.FindPost(postId) ?? throw NotFound("No such post.");
                if (post.Status != PostStatus.Published || post.IgMediaId == null) throw Conflict("Only a published post with a known Instagram id can be deleted from Instagram.");
                return (post.AccountId, post.IgMediaId, post.Permalink);
            });
            string token;
            try { (token, _) = service.Auth!.GetCredentials(target.AccountId); }
            catch (AccountUnavailableException ex) { throw Conflict(ex.Message); }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await service.InstagramApi!.DeleteMediaAsync(token, target.IgMediaId, cts.Token);
            }
            catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.PermissionDenied or IgErrorKind.BadRequest)
            {
                throw Conflict($"Instagram does not let this connection delete posts ({ex.Code}). Delete it in the Instagram app: {target.Permalink}");
            }
            catch (InstagramApiException ex)
            {
                throw Bad($"Instagram refused ({ex.Code}): {ex.Message}");
            }
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null) return;
                post.Status = PostStatus.Removed;
                post.LastError = "Deleted from Instagram on request.";
                s.Touch(post, now);
                s.InsightsOf(post.AccountId).Index.RemoveAll(m => m.Id == target.IgMediaId);
                s.MarkInsights(post.AccountId);
                s.AddEvent(EventLevel.Info, "post.deleted", "Deleted a published post from Instagram.", post.AccountId, post.PostId, now);
            });
            await Respond(req, new { deleted = true });
        }

        // ─────────────────────────────── Media ───────────────────────────────

        private async Task MediaUpload(UserRequest req)
        {
            string fileName = Path.GetFileName(RequiredQuery(req, "fileName"));
            string purpose = (Query(req, "purpose") ?? "compose").ToLowerInvariant();
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!OmniGramMediaTools.IsSupported(fileName))
                throw Bad("Unsupported file type. Use .mp4/.mov/.m4v videos or .jpg/.png/.gif/.webp images.");

            string destination;
            string? mediaId = null;
            string? accountId = null;
            if (purpose == "library")
            {
                accountId = RequiredQuery(req, "accountId");
                if (Store.Read(s => s.Account(accountId)) == null) throw NotFound("No such account.");
                string dir = service.ContentSources!.LibraryDirectoryFor(accountId);
                Directory.CreateDirectory(dir);
                string stem = new string(Path.GetFileNameWithoutExtension(fileName).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(60).ToArray());
                destination = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{(stem.Length == 0 ? "media" : stem)}{ext}");
            }
            else
            {
                mediaId = OmniGramIds.New();
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

            var probe = await service.MediaTools!.ProbeAsync(destination, CancellationToken.None);
            if (purpose == "library")
            {
                Store.Mutate(s => s.AddEvent(EventLevel.Info, "library.added", $"@{s.Account(accountId)?.Username}: added {Path.GetFileName(destination)} to the content library.", accountId));
                service.Engine?.WakePlanner(accountId);
            }
            await Respond(req, new
            {
                MediaId = mediaId,
                FileName = Path.GetFileName(destination),
                OriginalName = fileName,
                Kind = OmniGramMediaTools.IsVideo(fileName) ? "Video" : "Photo",
                MimeType = OmniGramMediaTools.MimeFor(fileName),
                Bytes = bytes,
                probe?.Width,
                probe?.Height,
                probe?.DurationSeconds,
                Purpose = purpose,
                AccountId = accountId,
            });
        }

        private async Task LibraryDelete(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            string fileName = Path.GetFileName(Required(body, "fileName"));
            string path = Path.Combine(service.ContentSources!.LibraryDirectoryFor(accountId), fileName);
            if (!File.Exists(path)) throw NotFound("No such library file.");
            bool inUse = Store.Read(s => s.PostsOf(accountId).Any(p => p.IsPending && p.Media.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))));
            if (inUse) throw Conflict("A pending post uses this file; cancel or swap that post first.");
            File.Delete(path);
            await Respond(req, new { deleted = true });
        }

        // ─────────────────────────────── Caption preview ───────────────────────────────

        private async Task CaptionPreview(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            string? key = OptString(body, "contentKey");
            string? mediaId = OptString(body, "mediaId");
            string? instruction = OptString(body, "instruction");
            DateTime now = DateTime.UtcNow;
            var snap = Store.Read(s =>
            {
                var account = RequireAccount(s, accountId);
                var recent = s.PostsOf(accountId).Where(p => !string.IsNullOrWhiteSpace(p.Caption)).OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc).Take(8).Select(p => p.Caption!).ToList();
                return (Account: OmniGramStore.Clone(account), Excluded: OmniGramPlanner.ExcludedKeys(s, account, account.Strategy) as IReadOnlySet<string>, Recent: recent);
            });
            var strategy = snap.Account.Strategy;
            if (body["strategy"] is JObject strategyJson)
            {
                try { strategy = strategyJson.ToObject<OmniGramStrategy>(JsonSerializer.Create(Json)) ?? strategy; }
                catch (Exception ex) { throw Bad("The strategy could not be read: " + ex.Message); }
                OmniGramStrategyRules.Normalize(strategy);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            ContentCandidate? candidate;
            if (mediaId != null)
            {
                var media = ResolveComposeMedia(new JArray(new JObject { ["mediaId"] = mediaId }), accountId)[0];
                candidate = new ContentCandidate { Key = "upload:" + mediaId, Source = ContentSourceKind.None, IsVideo = media.IsVideo, FilePath = media.Path };
            }
            else
            {
                candidate = key != null
                    ? service.ContentSources!.Resolve(strategy, accountId, key, now)
                    : (await service.ContentSources!.FindCandidatesAsync(strategy, accountId, snap.Excluded, 1, now, cts.Token)).FirstOrDefault();
            }
            if (candidate == null) throw Conflict("There is no content to caption with this strategy's content settings.");

            var frames = new List<byte[]>();
            if (strategy.Ai.UseVision)
            {
                if (candidate.IsVideo)
                    frames = await service.MediaTools!.ExtractFramesAsync(candidate.FilePath, 4, 512, candidate.DurationSeconds, cts.Token);
                else
                {
                    string preview = Path.Combine(Store.CacheDirectory, $"preview-{Guid.NewGuid():N}.jpg");
                    if (await service.MediaTools!.CreateThumbnailAsync(candidate.FilePath, preview, null, cts.Token))
                    {
                        frames.Add(await File.ReadAllBytesAsync(preview, cts.Token));
                        try { File.Delete(preview); } catch { }
                    }
                }
            }
            var format = candidate.IsVideo ? strategy.VideoFormat : strategy.ImageFormat;
            try
            {
                var result = await service.Captioner!.GenerateAiAsync(new CaptionRequest
                {
                    Settings = strategy.Ai,
                    Username = snap.Account.Username,
                    AccountName = snap.Account.Name,
                    Biography = snap.Account.Biography,
                    Format = format,
                    IsVideo = candidate.IsVideo,
                    OriginalCaption = candidate.OriginalCaption,
                    Origin = candidate.Origin,
                    RecentCaptions = snap.Recent,
                    Frames = frames,
                    ExtraInstruction = instruction,
                }, cts.Token);
                var hashtags = OmniGramPlanner.ComposeHashtags(strategy, result.Hashtags);
                string? credit = candidate.Source == ContentSourceKind.MemeScraper ? IgCaption.Credit(strategy.CreditTemplate, candidate.Origin) : null;
                string? footer = string.IsNullOrWhiteSpace(strategy.CaptionFooter) ? null : strategy.CaptionFooter;
                await Respond(req, new
                {
                    result.Caption,
                    Hashtags = hashtags,
                    SuggestedHashtags = result.Hashtags,
                    ComposedCaption = IgCaption.ComposeCaption(result.Caption, hashtags, strategy.HashtagPlacement, credit, footer),
                    FirstComment = IgCaption.ComposeFirstComment(null, hashtags, strategy.HashtagPlacement),
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

        // ─────────────────────────────── Comments ───────────────────────────────

        private (string Token, string Username) CommentAccount(string accountId)
        {
            string username = Store.Read(s => RequireAccount(s, accountId).Username);
            try { return (service.Auth!.GetCredentials(accountId).Token, username); }
            catch (AccountUnavailableException ex) { throw Conflict(ex.Message); }
        }

        private async Task CommentsReply(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            string commentId = Required(body, "commentId");
            string message = Required(body, "message");
            if (message.Length > IgCaption.MaxCommentLength) throw Bad("The reply is too long.");
            var (token, username) = CommentAccount(accountId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string replyId;
            try { replyId = await service.InstagramApi!.ReplyToCommentAsync(token, commentId, message, cts.Token); }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.PermissionDenied) { throw Conflict("Instagram refused: reconnect the account and allow it to manage comments."); }
            catch (InstagramApiException ex) { throw Bad($"Instagram refused ({ex.Code}): {ex.Message}"); }
            DateTime now = DateTime.UtcNow;
            Store.Mutate(s =>
            {
                var insights = s.InsightsOf(accountId);
                var parent = insights.Comments.FirstOrDefault(c => c.Id == commentId);
                if (parent != null) parent.Replied = true;
                if (!string.IsNullOrEmpty(replyId))
                    insights.Comments.Insert(0, new CommentItem { Id = replyId, MediaId = parent?.MediaId ?? "", OmniPostId = parent?.OmniPostId, ParentId = commentId, Username = username, Text = message, Utc = now, Own = true });
                s.MarkInsights(accountId);
            });
            await Respond(req, new { replied = true, replyId });
        }

        private async Task CommentsHide(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            string commentId = Required(body, "commentId");
            bool hidden = OptBool(body, "hidden") ?? true;
            var (token, _) = CommentAccount(accountId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await service.InstagramApi!.SetCommentHiddenAsync(token, commentId, hidden, cts.Token); }
            catch (InstagramApiException ex) { throw Bad($"Instagram refused ({ex.Code}): {ex.Message}"); }
            Store.Mutate(s =>
            {
                var c = s.InsightsOf(accountId).Comments.FirstOrDefault(c => c.Id == commentId);
                if (c != null) { c.Hidden = hidden; s.MarkInsights(accountId); }
            });
            await Respond(req, new { hidden });
        }

        private async Task CommentsDelete(UserRequest req)
        {
            var body = Body(req);
            string accountId = Required(body, "accountId");
            string commentId = Required(body, "commentId");
            var (token, _) = CommentAccount(accountId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await service.InstagramApi!.DeleteCommentAsync(token, commentId, cts.Token); }
            catch (InstagramApiException ex) { throw Bad($"Instagram refused ({ex.Code}): {ex.Message}"); }
            Store.Mutate(s =>
            {
                s.InsightsOf(accountId).Comments.RemoveAll(c => c.Id == commentId || c.ParentId == commentId);
                s.MarkInsights(accountId);
            });
            await Respond(req, new { deleted = true });
        }
    }
}
