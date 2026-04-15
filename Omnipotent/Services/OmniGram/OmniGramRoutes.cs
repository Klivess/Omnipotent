using Newtonsoft.Json;
using Omnipotent.Services.OmniGram.Models;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.OmniGram
{
#pragma warning disable CS4014
    public class OmniGramRoutes
    {
        private readonly OmniGram service;

        public OmniGramRoutes(OmniGram service)
        {
            this.service = service;
        }

        public async Task RegisterRoutes()
        {
            await RegisterDashboardRoutes();
            await RegisterAccountRoutes();
            await RegisterPostRoutes();
            await RegisterAnalyticsRoutes();
            await RegisterContentConfigRoutes();
            await RegisterEventRoutes();
        }

        // ── Dashboard ──

        private async Task RegisterDashboardRoutes()
        {
            await service.CreateAPIRoute("/omnigram/dashboard-stats", async (req) =>
            {
                try
                {
                    var stats = service.AnalyticsTracker.GetFleetDashboardStats();
                    await req.ReturnResponse(JsonConvert.SerializeObject(stats));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
        }

        // ── Accounts ──

        private async Task RegisterAccountRoutes()
        {
            await service.CreateAPIRoute("/omnigram/accounts", async (req) =>
            {
                try
                {
                    var accounts = service.AccountManager.GetAllAccounts().Select(a => new
                    {
                        a.AccountId,
                        a.Username,
                        a.IsActive,
                        a.IsPaused,
                        LoginStatus = a.LoginStatus.ToString(),
                        a.FollowerCount,
                        a.FollowingCount,
                        a.MediaCount,
                        a.LastPostTime,
                        a.LastLoginTime,
                        a.LoginErrorMessage,
                        a.AddedDate,
                        a.Tags,
                        a.Notes,
                        a.ProfilePicUrl,
                        a.Biography,
                        ContentSource = a.ContentConfig.ContentSource.ToString(),
                        CaptionMode = a.ContentConfig.CaptionMode.ToString(),
                        PostsPerDay = a.ContentConfig.PostsPerDay
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(accounts));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/accounts/detail", async (req) =>
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
                        a = account,
                        Config = account.ContentConfig,
                        RecentPosts = service.PostScheduler.GetAllPosts()
                            .Where(p => p.AccountId == accountId)
                            .OrderByDescending(p => p.ScheduledTime)
                            .Take(20)
                            .Select(p => new
                            {
                                p.PostId,
                                ContentType = p.ContentType.ToString(),
                                Status = p.Status.ToString(),
                                p.Caption,
                                p.ScheduledTime,
                                p.PostedTime,
                                p.ErrorMessage,
                                SourceType = p.SourceType.ToString(),
                                MediaCount = p.MediaPaths?.Count ?? 0,
                                p.RetryCount
                            })
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/accounts/add", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string username = body.username;
                    string password = body.password;

                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    {
                        await req.ReturnResponse("Username and password are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await service.AccountManager.AddAccountAsync(username, password);
                    await service.AnalyticsTracker.LogEvent(account.AccountId, "AccountAdded", $"Account @{username} added.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.AccountId,
                        account.Username,
                        LoginStatus = account.LoginStatus.ToString()
                    }));
                }
                catch (InvalidOperationException ex)
                {
                    await req.ReturnResponse(ex.Message, code: HttpStatusCode.Conflict);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/accounts/remove", async (req) =>
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
                        await service.AnalyticsTracker.LogEvent(accountId, "AccountRemoved", $"Account @{account?.Username} removed.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/accounts/pause", async (req) =>
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
                    await service.AnalyticsTracker.LogEvent(accountId, "AccountPaused", "Account paused by commander.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/accounts/resume", async (req) =>
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
                    await service.AnalyticsTracker.LogEvent(accountId, "AccountResumed", "Account resumed by commander.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/accounts/update-notes", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    string notes = body.notes;
                    string tagsStr = body.tags;

                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (notes != null) account.Notes = notes;
                    if (tagsStr != null)
                        account.Tags = ((string)tagsStr).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

                    await service.AccountManager.SaveAccountToDisk(account);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Content Configuration ──

        private async Task RegisterContentConfigRoutes()
        {
            await service.CreateAPIRoute("/omnigram/accounts/config", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(account.ContentConfig));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/accounts/config/update", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var configJson = body.config?.ToString();
                    if (!string.IsNullOrEmpty(configJson))
                    {
                        var newConfig = JsonConvert.DeserializeObject<OmniGramAccountContentConfig>(configJson);
                        if (newConfig != null)
                        {
                            // Preserve used content paths from existing config
                            newConfig.UsedContentPaths = account.ContentConfig.UsedContentPaths;
                            account.ContentConfig = newConfig;
                        }
                    }

                    await service.AccountManager.SaveAccountToDisk(account);
                    await service.AnalyticsTracker.LogEvent(accountId, "ConfigUpdated", "Content configuration updated by commander.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/content-folder/list", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var files = service.MediaManager.ListContentFolder(
                        account.ContentConfig.ContentFolderPath,
                        account.ContentConfig.UsedContentPaths);
                    await req.ReturnResponse(JsonConvert.SerializeObject(files));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/content-folder/reset-used", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    var account = service.AccountManager.GetAccountById(accountId);
                    if (account == null)
                    {
                        await req.ReturnResponse("Account not found.", code: HttpStatusCode.NotFound);
                        return;
                    }

                    account.ContentConfig.UsedContentPaths.Clear();
                    await service.AccountManager.SaveAccountToDisk(account);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Posts ──

        private async Task RegisterPostRoutes()
        {
            await service.CreateAPIRoute("/omnigram/posts", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var statusFilter = req.userParameters.Get("status");

                    var query = service.PostScheduler.GetAllPosts().AsEnumerable();

                    if (!string.IsNullOrEmpty(accountId))
                        query = query.Where(p => p.AccountId == accountId);
                    if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<OmniGramPostStatus>(statusFilter, true, out var status))
                        query = query.Where(p => p.Status == status);

                    var posts = query.OrderByDescending(p => p.ScheduledTime).Take(200).Select(p => new
                    {
                        p.PostId,
                        p.AccountId,
                        Username = service.AccountManager.GetAccountById(p.AccountId)?.Username ?? "Unknown",
                        ContentType = p.ContentType.ToString(),
                        Status = p.Status.ToString(),
                        p.Caption,
                        p.ScheduledTime,
                        p.PostedTime,
                        p.ErrorMessage,
                        SourceType = p.SourceType.ToString(),
                        MediaCount = p.MediaPaths?.Count ?? 0,
                        p.RetryCount,
                        p.MaxRetries
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(posts));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/queue", async (req) =>
            {
                try
                {
                    var queue = service.PostScheduler.GetQueuedPosts().Select(p => new
                    {
                        p.PostId,
                        p.AccountId,
                        Username = service.AccountManager.GetAccountById(p.AccountId)?.Username ?? "Unknown",
                        ContentType = p.ContentType.ToString(),
                        p.Caption,
                        p.ScheduledTime,
                        SourceType = p.SourceType.ToString(),
                        MediaCount = p.MediaPaths?.Count ?? 0
                    });
                    await req.ReturnResponse(JsonConvert.SerializeObject(queue));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/posts/schedule", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    string contentTypeStr = body.contentType;
                    string caption = body.caption ?? "";
                    string hashtags = body.hashtags ?? "";
                    string scheduledTimeStr = body.scheduledTime;
                    var mediaPathsToken = body.mediaPaths;

                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var contentType = Enum.TryParse<OmniGramContentType>(contentTypeStr, true, out var ct)
                        ? ct : OmniGramContentType.Photo;

                    var scheduledTime = DateTime.TryParse(scheduledTimeStr, out var st)
                        ? st : DateTime.Now.AddMinutes(5);

                    var mediaPaths = mediaPathsToken != null
                        ? JsonConvert.DeserializeObject<List<string>>(mediaPathsToken.ToString())
                        : new List<string>();

                    var post = new OmniGramPost
                    {
                        AccountId = accountId,
                        ContentType = contentType,
                        Caption = caption,
                        Hashtags = hashtags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                        ScheduledTime = scheduledTime,
                        MediaPaths = mediaPaths,
                        SourceType = OmniGramContentSource.ManualUpload
                    };

                    var scheduled = await service.PostScheduler.SchedulePostAsync(post);
                    await service.AnalyticsTracker.LogEvent(accountId, "PostScheduled",
                        $"Manually scheduled {contentType} post for {scheduledTime}.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        scheduled.PostId,
                        scheduled.ScheduledTime,
                        Status = scheduled.Status.ToString()
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/posts/publish-now", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string accountId = body.accountId;
                    string contentTypeStr = body.contentType;
                    string caption = body.caption ?? "";
                    string hashtags = body.hashtags ?? "";
                    var mediaPathsToken = body.mediaPaths;

                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var contentType = Enum.TryParse<OmniGramContentType>(contentTypeStr, true, out var ct)
                        ? ct : OmniGramContentType.Photo;

                    var mediaPaths = mediaPathsToken != null
                        ? JsonConvert.DeserializeObject<List<string>>(mediaPathsToken.ToString())
                        : new List<string>();

                    var post = new OmniGramPost
                    {
                        AccountId = accountId,
                        ContentType = contentType,
                        Caption = caption,
                        Hashtags = hashtags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                        ScheduledTime = DateTime.Now,
                        MediaPaths = mediaPaths,
                        SourceType = OmniGramContentSource.ManualUpload
                    };

                    var scheduled = await service.PostScheduler.SchedulePostAsync(post);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        scheduled.PostId,
                        Status = "Queued for immediate publish"
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/posts/cancel", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string postId = body.postId;
                    if (string.IsNullOrWhiteSpace(postId))
                    {
                        await req.ReturnResponse("postId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var success = await service.PostScheduler.CancelPostAsync(postId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            await service.CreateAPIRoute("/omnigram/posts/trigger-pull", async (req) =>
            {
                try
                {
                    await service.PostScheduler.PullFromMemeScraperAsync();
                    await service.PostScheduler.PullFromContentFoldersAsync();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = "Content pull triggered." }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Analytics ──

        private async Task RegisterAnalyticsRoutes()
        {
            await service.CreateAPIRoute("/omnigram/analytics", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");

                    if (string.IsNullOrEmpty(accountId))
                    {
                        var summaries = service.AccountManager.GetAllAccounts()
                            .Select(a => service.AnalyticsTracker.GetAccountAnalyticsSummary(a.AccountId));
                        await req.ReturnResponse(JsonConvert.SerializeObject(summaries));
                    }
                    else
                    {
                        var summary = service.AnalyticsTracker.GetAccountAnalyticsSummary(accountId);
                        await req.ReturnResponse(JsonConvert.SerializeObject(summary));
                    }
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/analytics/snapshots", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var daysStr = req.userParameters.Get("days");
                    int days = int.TryParse(daysStr, out var d) ? d : 30;

                    if (string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var snapshots = service.AnalyticsTracker.GetSnapshots(accountId, days);
                    await req.ReturnResponse(JsonConvert.SerializeObject(snapshots));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await service.CreateAPIRoute("/omnigram/analytics/trigger-snapshot", async (req) =>
            {
                try
                {
                    await service.AnalyticsTracker.TakeDailySnapshots();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = "Snapshot collection triggered." }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);
        }

        // ── Events ──

        private async Task RegisterEventRoutes()
        {
            await service.CreateAPIRoute("/omnigram/events", async (req) =>
            {
                try
                {
                    var accountId = req.userParameters.Get("accountId");
                    var countStr = req.userParameters.Get("count");
                    int count = int.TryParse(countStr, out var c) ? c : 100;

                    var events = service.AnalyticsTracker.GetRecentEvents(count, accountId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(events));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)),
                        code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
        }
    }
#pragma warning restore CS4014
}
