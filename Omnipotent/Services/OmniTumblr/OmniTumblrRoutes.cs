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
            await RegisterProfileRoutes();
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
            // POST /omnitumblr/oauth/begin — Step 1: exchange consumer credentials for an auth URL
            await service.CreateAPIRoute("/omnitumblr/oauth/begin", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string consumerKey    = body.consumerKey;
                    string consumerSecret = body.consumerSecret;
                    string blogName       = body.blogName;

                    if (string.IsNullOrWhiteSpace(consumerKey) || string.IsNullOrWhiteSpace(consumerSecret) || string.IsNullOrWhiteSpace(blogName))
                    {
                        await req.ReturnResponse("consumerKey, consumerSecret, and blogName are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var (flowId, authUrl) = await service.AccountManager.BeginOAuthFlowAsync(consumerKey, consumerSecret, blogName);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { flowId, authorizationUrl = authUrl, callbackUrl = service.OAuthCallbackUrl }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // GET /omnitumblr/oauth/callback — Tumblr redirects here after the user authorizes.
            // Query params: oauth_token (request token), oauth_verifier
            await service.CreateAPIRoute("/omnitumblr/oauth/callback", async (req) =>
            {
                try
                {
                    var requestToken = req.userParameters.Get("oauth_token");
                    var verifier     = req.userParameters.Get("oauth_verifier");
                    var callbackQuery = req.req.Url.Query;

                    if (string.IsNullOrWhiteSpace(requestToken) || string.IsNullOrWhiteSpace(verifier))
                    {
                        await req.ReturnResponse("<html><body style='font-family:sans-serif;background:#111;color:#f00;padding:40px'>" +
                            "<h2>Authorization failed</h2><p>Missing oauth_token or oauth_verifier parameters.</p></body></html>",
                            contentType: "text/html", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await service.AccountManager.CompleteOAuthCallbackAsync(requestToken, callbackQuery);

                    await req.ReturnResponse(
                        $"<html><body style='font-family:sans-serif;background:#111;color:#e0e0e0;padding:40px;text-align:center'>" +
                        $"<h2 style='color:#4caf50'>✓ Authorization Successful</h2>" +
                        $"<p>Blog <strong>{HtmlEncode(account.BlogName)}</strong> has been connected to OmniTumblr.</p>" +
                        $"<p>You can close this tab and return to the dashboard.</p>" +
                        $"</body></html>",
                        contentType: "text/html");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(
                        $"<html><body style='font-family:sans-serif;background:#111;color:#f00;padding:40px'>" +
                        $"<h2>Authorization Error</h2><p>{HtmlEncode(ex.Message)}</p>" +
                        $"<p>Please close this tab and try again from the dashboard.</p></body></html>",
                        contentType: "text/html", code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            // GET /omnitumblr/oauth/callback-url — returns the configured OAuth callback URL
            await service.CreateAPIRoute("/omnitumblr/oauth/callback-url", async (req) =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { callbackUrl = service.OAuthCallbackUrl }));
            }, HttpMethod.Get, KMPermissions.Admin);

            // GET /omnitumblr/oauth/callback-status?flowId=X — poll to check if callback was received
            await service.CreateAPIRoute("/omnitumblr/oauth/callback-status", async (req) =>
            {
                try
                {
                    var flowId = req.userParameters.Get("flowId");
                    if (string.IsNullOrWhiteSpace(flowId))
                    {
                        await req.ReturnResponse("flowId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = service.AccountManager.GetFlowStatus(flowId);
                    if (account == null)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { completed = false }));
                        return;
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        completed = true,
                        account.AccountId,
                        account.BlogName,
                        ConnectionStatus = account.ConnectionStatus.ToString(),
                        account.FollowerCount,
                        account.PostCount
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Admin);

            // POST /omnitumblr/oauth/complete — Step 3: exchange request token + verifier for access token and create account
            await service.CreateAPIRoute("/omnitumblr/oauth/complete", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    string flowId   = body.flowId;
                    string verifier = body.verifier;
                    string blogName = body.blogName;

                    if (string.IsNullOrWhiteSpace(flowId) || string.IsNullOrWhiteSpace(verifier) || string.IsNullOrWhiteSpace(blogName))
                    {
                        await req.ReturnResponse("flowId, verifier, and blogName are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await service.AccountManager.CompleteOAuthFlowAsync(flowId, verifier, blogName);
                    await service.AnalyticsTracker.LogEvent(account.AccountId, "AccountAdded", $"Blog '{blogName}' added via OAuth.");
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

                    if (string.IsNullOrWhiteSpace(blogName) || string.IsNullOrWhiteSpace(consumerKey) || string.IsNullOrWhiteSpace(consumerSecret))
                    {
                        await req.ReturnResponse("blogName, consumerKey, and consumerSecret are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var account = await service.AccountManager.AddAccountAsync(blogName, consumerKey, consumerSecret);
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

        // ── Profile ──

        private async Task RegisterProfileRoutes()
        {
            // GET /omnitumblr/accounts/profile
            await service.CreateAPIRoute("/omnitumblr/accounts/profile", async (req) =>
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

                    // Attempt live refresh from Tumblr
                    var client = service.AccountManager.GetApiInstance(accountId);
                    bool isLive = false;
                    if (client != null)
                    {
                        try
                        {
                            await service.AccountManager.ValidateConnection(client, account);
                            await service.AccountManager.SaveAccountToDisk(account);
                            isLive = true;
                        }
                        catch { /* use cached data */ }
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        account.AccountId,
                        account.BlogName,
                        account.Title,
                        account.Description,
                        account.AvatarUrl,
                        account.Url,
                        account.FollowerCount,
                        account.PostCount,
                        account.LikesCount,
                        account.Notes,
                        IsLive = isLive,
                        ConnectionStatus = account.ConnectionStatus.ToString()
                    }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // POST /omnitumblr/accounts/profile/edit
            // Note: TumblrSharp does not expose a blog-info update method.
            // Title and Description are stored locally. Notes are persisted to OmniTumblr data.
            await service.CreateAPIRoute("/omnitumblr/accounts/profile/edit", async (req) =>
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

                    string title = body.title;
                    string description = body.description;
                    string notes = body.notes;

                    if (title != null) account.Title = title;
                    if (description != null) account.Description = description;
                    if (notes != null) account.Notes = notes;

                    await service.AccountManager.SaveAccountToDisk(account);
                    await service.AnalyticsTracker.LogEvent(accountId, "ProfileEdited", $"Profile info updated locally for '{account.BlogName}'.");

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        success = true,
                        note = "Title and description are stored locally. To update the live Tumblr blog profile, use the Tumblr dashboard directly."
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

            // POST /omnitumblr/posts/publish-now
            await service.CreateAPIRoute("/omnitumblr/posts/publish-now", async (req) =>
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
                        ScheduledTime = DateTime.UtcNow,
                        SourceType = OmniTumblrContentSource.ManualUpload,
                        MediaPaths = body.mediaPaths != null ? JsonConvert.DeserializeObject<List<string>>(body.mediaPaths.ToString()) : new List<string>()
                    };

                    var scheduled = await service.PostScheduler.SchedulePostAsync(post);
                    await service.AnalyticsTracker.LogEvent(accountId, "PostPublishNow", $"{post.PostType} post queued for immediate publish to '{account.BlogName}'.");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { scheduled.PostId, Status = "Queued for immediate publish" }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/posts/draft  — schedule to multiple accounts
            await service.CreateAPIRoute("/omnitumblr/posts/draft", async (req) =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent);
                    var accountIdsToken = body.accountIds;
                    if (accountIdsToken == null)
                    {
                        await req.ReturnResponse("accountIds array is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var accountIds = JsonConvert.DeserializeObject<List<string>>(accountIdsToken.ToString());
                    if (accountIds == null || accountIds.Count == 0)
                    {
                        await req.ReturnResponse("At least one accountId is required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var postType = Enum.TryParse<OmniTumblrPostType>((string)body.postType, out var pt) ? pt : OmniTumblrPostType.Photo;
                    string caption = body.caption ?? "";
                    string title = body.title ?? "";
                    string scheduledTimeStr = body.scheduledTime;
                    var scheduledTime = DateTime.TryParse(scheduledTimeStr, out var st) ? st : DateTime.UtcNow.AddMinutes(5);
                    var tags = body.tags != null ? JsonConvert.DeserializeObject<List<string>>(body.tags.ToString()) : new List<string>();
                    var mediaPaths = body.mediaPaths != null ? JsonConvert.DeserializeObject<List<string>>(body.mediaPaths.ToString()) : new List<string>();

                    var results = new List<object>();
                    foreach (var accountId in accountIds)
                    {
                        var account = service.AccountManager.GetAccountById(accountId);
                        if (account == null) { results.Add(new { accountId, success = false, error = "Account not found" }); continue; }

                        var post = new OmniTumblrPost
                        {
                            AccountId = accountId,
                            PostType = postType,
                            Caption = caption,
                            Title = title,
                            Tags = new List<string>(tags),
                            ScheduledTime = scheduledTime,
                            SourceType = OmniTumblrContentSource.ManualUpload,
                            MediaPaths = new List<string>(mediaPaths)
                        };

                        var scheduled = await service.PostScheduler.SchedulePostAsync(post);
                        await service.AnalyticsTracker.LogEvent(accountId, "DraftPostScheduled", $"Draft {postType} post scheduled for '{account.BlogName}' at {scheduledTime:g}.");
                        results.Add(new { accountId, success = true, postId = scheduled.PostId, scheduledTime = scheduled.ScheduledTime });
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, results }));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new ErrorInformation(ex)), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Admin);

            // POST /omnitumblr/media/upload — raw bytes in body, fileName + accountId in query
            await service.CreateAPIRoute("/omnitumblr/media/upload", async (req) =>
            {
                try
                {
                    var fileName = req.userParameters.Get("fileName");
                    var accountId = req.userParameters.Get("accountId");

                    if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(accountId))
                    {
                        await req.ReturnResponse("fileName and accountId are required.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    byte[] fileData = req.userMessageBytes;
                    if (fileData == null || fileData.Length == 0)
                    {
                        await req.ReturnResponse("No file data provided.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (fileData.Length > 100 * 1024 * 1024)
                    {
                        await req.ReturnResponse("File exceeds 100 MB limit.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var safeFileName = Path.GetFileName(fileName);
                    var uploadPath = await service.MediaManager.StoreUploadedMediaFromBytes(fileData, safeFileName, accountId);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, filePath = uploadPath, fileName = Path.GetFileName(uploadPath) }));
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
                    _ = service.PostScheduler.PullFromMemeScraperAsync();
                    _ = service.PostScheduler.PullFromContentFoldersAsync();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = "Content pull triggered (MemeScraper + Content Folders)." }));
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

        private static string HtmlEncode(string? s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
