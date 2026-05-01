using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTumblr.Models;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.OmniTumblr
{
#pragma warning disable CS4014
    public class OmniTumblrRoutes
    {
        private readonly OmniTumblr service;

        public OmniTumblrRoutes(OmniTumblr service)
        {
            this.service = service;
        }

        public async Task RegisterRoutes()
        {
            await RegisterDashboardRoutes();
            await RegisterAccountRoutes();
            await RegisterContentConfigRoutes();
            await RegisterPostRoutes();
            await RegisterAnalyticsRoutes();
            await RegisterEventRoutes();
        }

        // ── Dashboard ──

        private async Task RegisterDashboardRoutes()
        {
            await service.CreateAPIRoute("/omnitumblr/dashboard-stats", async (req) =>
            {
                try
                {
                    var stats = service.AnalyticsTracker.GetFleetDashboardStats();
                    await req.ReturnResponse(JsonConvert.SerializeObject(stats));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
        }

        // ── Accounts ──

        private async Task RegisterAccountRoutes()
        {
            // GET /omnitumblr/accounts
            await service.CreateAPIRoute("/omnitumblr/accounts", async (req) =>
            {
                try
                {
                    var accounts = service.AccountManager.GetAllAccounts().Select(a => new
                    {
                        a.AccountId,
                        a.BlogName,
                        a.IsActive,
                        a.IsPaused,
                        ConnectionStatus = a.ConnectionStatus.ToString(),
                        a.FollowerCount,
                        a.PostCount,
                        a.LikesCount,
                        a.AvatarUrl,
                        a.Title,
                        a.Description,
                        a.Url,
                        a.LastPostTime,
                        a.AddedDate,
                        a.Tags,
                        a.Notes,
                        a.ConnectionErrorMessage,
                        ContentSource = a.ContentConfig.ContentSource.ToString(),
                        CaptionMode = a.ContentConfig.CaptionMode.ToString(),
                        PostsPerDay = a.ContentConfig.PostsPerDay
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(accounts));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // GET /omnitumblr/accounts/detail
            await service.CreateAPIRoute("/omnitumblr/accounts/detail", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        Account = account,
                        Config = account.ContentConfig,
                        RecentPosts = service.PostScheduler.GetAllPosts()
                            .Where(p => p.AccountId == accountId)
                            .OrderByDescending(p => p.ScheduledTime)
                            .Take(20)
                            .Select(p => new
                            {
                                p.PostId,
                                PostType = p.PostType.ToString(),
                                Status = p.Status.ToString(),
                                p.Caption,
                                p.Title,
                                p.ScheduledTime,
                                p.PostedTime,
                                p.ErrorMessage,
                                SourceType = p.SourceType.ToString(),
                                MediaCount = p.MediaPaths?.Count ?? 0,
                                p.RetryCount,
                                p.TumblrPostId
                            })
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // POST /omnitumblr/accounts/add
            await service.CreateAPIRoute("/omnitumblr/accounts/add", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string blogName = body.blogName;
                    string consumerKey = body.consumerKey;
                    string consumerSecret = body.consumerSecret;
                    string oauthToken = body.oauthToken;
                    string oauthTokenSecret = body.oauthTokenSecret;

                    if (string.IsNullOrWhiteSpace(blogName) || string.IsNullOrWhiteSpace(consumerKey)
                        || string.IsNullOrWhiteSpace(consumerSecret) || string.IsNullOrWhiteSpace(oauthToken)
                        || string.IsNullOrWhiteSpace(oauthTokenSecret))
                    {
                        await req.ReturnResponse("blogName, consumerKey, consumerSecret, oauthToken, and oauthTokenSecret are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await service.AccountManager.AddAccountAsync(blogName, consumerKey, consumerSecret, oauthToken, oauthTokenSecret);
                    await service.AnalyticsTracker.LogEvent(account.AccountId, "AccountAdded", $"Blog '{blogName}' added.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.AccountId,
                        account.BlogName,
                        ConnectionStatus = account.ConnectionStatus.ToString(),
                        account.FollowerCount,
                        account.PostCount
                    }));
                }
                catch (InvalidOperationException ex)
                {
                    await req.ReturnResponse(ex.Message, code: HttpStatusCode.Conflict);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/accounts/remove
            await service.CreateAPIRoute("/omnitumblr/accounts/remove", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = service.AccountManager.GetAccountById(accountId);
                    var success = await service.AccountManager.RemoveAccountAsync(accountId);
                    if (success)
                        await service.AnalyticsTracker.LogEvent(accountId, "AccountRemoved", $"Blog '{account?.BlogName}' removed.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/accounts/pause
            await service.CreateAPIRoute("/omnitumblr/accounts/pause", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await service.AccountManager.PauseAccountAsync(accountId);
                    var account = service.AccountManager.GetAccountById(accountId);
                    await service.AnalyticsTracker.LogEvent(accountId, "AccountPaused", $"Blog '{account?.BlogName}' paused.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/accounts/resume
            await service.CreateAPIRoute("/omnitumblr/accounts/resume", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await service.AccountManager.ResumeAccountAsync(accountId);
                    var account = service.AccountManager.GetAccountById(accountId);
                    await service.AnalyticsTracker.LogEvent(accountId, "AccountResumed", $"Blog '{account?.BlogName}' resumed.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/accounts/refresh — re-fetch blog info from Tumblr
            await service.CreateAPIRoute("/omnitumblr/accounts/refresh", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var client = service.AccountManager.GetApiInstance(accountId);
                    if (client == null)
                    {
                        await req.ReturnResponse("No API client for this account.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await service.AccountManager.ValidateConnection(client, account);
                    await service.AccountManager.SaveAccountToDisk(account);
                    await service.AnalyticsTracker.LogEvent(accountId, "AccountRefreshed", $"Blog '{account.BlogName}' refreshed.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.FollowerCount,
                        account.PostCount,
                        account.Description,
                        account.Title,
                        account.Url,
                        ConnectionStatus = account.ConnectionStatus.ToString()
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Content Config ──

        private async Task RegisterContentConfigRoutes()
        {
            // GET /omnitumblr/accounts/config
            await service.CreateAPIRoute("/omnitumblr/accounts/config", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null) { await req.ReturnResponse("Not found.", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(JsonConvert.SerializeObject(account.ContentConfig));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // POST /omnitumblr/accounts/config/update
            await service.CreateAPIRoute("/omnitumblr/accounts/config/update", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null) { await req.ReturnResponse("Not found.", code: HttpStatusCode.NotFound); return; }

                    var newConfig = JsonConvert.DeserializeObject<OmniTumblrAccountContentConfig>(body.config.ToString());
                    if (newConfig != null)
                    {
                        // Preserve used content tracking
                        newConfig.UsedContentPaths = account.ContentConfig.UsedContentPaths;
                        account.ContentConfig = newConfig;
                        await service.AccountManager.SaveAccountToDisk(account);
                        await service.AnalyticsTracker.LogEvent(accountId, "ConfigUpdated", $"Content config updated for '{account.BlogName}'.");
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // GET /omnitumblr/content-folder/list
            await service.CreateAPIRoute("/omnitumblr/content-folder/list", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null) { await req.ReturnResponse("Not found.", code: HttpStatusCode.NotFound); return; }

                    var files = service.MediaManager.GetContentFolderFiles(
                        account.ContentConfig.ContentFolderPath,
                        account.ContentConfig.UsedContentPaths);

                    await req.ReturnResponse(JsonConvert.SerializeObject(files));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // POST /omnitumblr/content-folder/reset-used
            await service.CreateAPIRoute("/omnitumblr/content-folder/reset-used", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null) { await req.ReturnResponse("Not found.", code: HttpStatusCode.NotFound); return; }

                    account.ContentConfig.UsedContentPaths.Clear();
                    await service.AccountManager.SaveAccountToDisk(account);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Posts ──

        private async Task RegisterPostRoutes()
        {
            // GET /omnitumblr/posts
            await service.CreateAPIRoute("/omnitumblr/posts", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var allPosts = service.PostScheduler.GetAllPosts();
                    var filtered = string.IsNullOrEmpty(accountId)
                        ? allPosts
                        : allPosts.Where(p => p.AccountId == accountId);

                    var result = filtered.OrderByDescending(p => p.ScheduledTime).Select(p => new
                    {
                        p.PostId,
                        p.AccountId,
                        PostType = p.PostType.ToString(),
                        Status = p.Status.ToString(),
                        p.Caption,
                        p.Title,
                        p.ScheduledTime,
                        p.PostedTime,
                        p.ErrorMessage,
                        SourceType = p.SourceType.ToString(),
                        MediaCount = p.MediaPaths?.Count ?? 0,
                        p.RetryCount,
                        p.TumblrPostId
                    });

                    await req.ReturnResponse(JsonConvert.SerializeObject(result));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // GET /omnitumblr/queue
            await service.CreateAPIRoute("/omnitumblr/queue", async (req) =>
            {
                try
                {
                    var queue = service.PostScheduler.GetQueuedPosts().Select(p => new
                    {
                        p.PostId,
                        p.AccountId,
                        PostType = p.PostType.ToString(),
                        p.Caption,
                        p.ScheduledTime,
                        MediaCount = p.MediaPaths?.Count ?? 0,
                        p.RetryCount,
                        p.Tags
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(queue));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // POST /omnitumblr/posts/schedule
            await service.CreateAPIRoute("/omnitumblr/posts/schedule", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null) { await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound); return; }

                    var post = new OmniTumblrPost
                    {
                        AccountId = accountId,
                        PostType = Enum.TryParse<OmniTumblrPostType>((string)body.postType, out var pt) ? pt : OmniTumblrPostType.Photo,
                        Caption = body.caption ?? "",
                        Title = body.title ?? "",
                        SourceUrl = body.sourceUrl ?? "",
                        QuoteSource = body.quoteSource ?? "",
                        Tags = body.tags != null ? JsonConvert.DeserializeObject<List<string>>(body.tags.ToString()) : new List<string>(),
                        ScheduledTime = body.scheduledTime != null ? (DateTime)body.scheduledTime : DateTime.UtcNow.AddMinutes(5),
                        SourceType = OmniTumblrContentSource.ManualUpload,
                        MediaPaths = body.mediaPaths != null ? JsonConvert.DeserializeObject<List<string>>(body.mediaPaths.ToString()) : new List<string>()
                    };

                    var scheduled = await service.PostScheduler.SchedulePostAsync(post);
                    await service.AnalyticsTracker.LogEvent(accountId, "PostScheduled", $"{post.PostType} post scheduled for '{account.BlogName}'.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { scheduled.PostId, scheduled.ScheduledTime, Status = scheduled.Status.ToString() }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/posts/cancel
            await service.CreateAPIRoute("/omnitumblr/posts/cancel", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string postId = body.postId;
                    var success = await service.PostScheduler.CancelPostAsync(postId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/posts/trigger-pull
            await service.CreateAPIRoute("/omnitumblr/posts/trigger-pull", async (req) =>
            {
                try
                {
                    _ = service.PostScheduler.PullFromContentFoldersAsync();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = "Content pull triggered." }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Analytics ──

        private async Task RegisterAnalyticsRoutes()
        {
            await service.CreateAPIRoute("/omnitumblr/analytics", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var summary = await service.AnalyticsTracker.GetAccountAnalyticsSummary(accountId);
                    if (summary == null) { await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(JsonConvert.SerializeObject(summary));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
        }

        // ── Events ──

        private async Task RegisterEventRoutes()
        {
            await service.CreateAPIRoute("/omnitumblr/events", async (req) =>
            {
                try
                {
                    var countStr = req.userParameters.Get("count");
                    int count = int.TryParse(countStr, out var c) ? c : 50;
                    var events = service.AnalyticsTracker.GetRecentEvents(count);
                    await req.ReturnResponse(JsonConvert.SerializeObject(events));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
        }
    }
}
