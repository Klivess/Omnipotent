using DontPanic.TumblrSharp;
using DontPanic.TumblrSharp.Client;
using DontPanic.TumblrSharp.OAuth;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveLocalLLM;
using Omnipotent.Services.MemeScraper;
using System.Security.Cryptography;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblr : OmniService
    {
        private OmniTumblrStore store;
        private OmniTumblrRoutes routes;
        private readonly SemaphoreSlim processLock = new(1, 1);
        private readonly Random random = new();

        public OmniTumblr()
        {
            name = "OmniTumblr";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            store = new OmniTumblrStore(this);
            await store.Load();

            routes = new OmniTumblrRoutes(this);
            await routes.RegisterRoutes();

            GetTimeManagerService().TaskDue += TimeManager_TaskDue;
            await EnsureAutonomousPostingSchedulesOnBoot();
            await LogEvent("Info", "OmniTumblr boot completed and autonomous schedule check executed.");
            await ServiceLog($"OmniTumblr started with {store.Accounts.Count} accounts and {store.Posts.Count} post jobs loaded.");
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (!e.taskName.StartsWith("OmniTumblrPost-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string postId = e.PassableData as string ?? e.taskName.Replace("OmniTumblrPost-", "", StringComparison.OrdinalIgnoreCase);
            _ = ProcessPost(postId);
        }

        public async Task<OmniTumblrAccount> AddManagedAccount(OmniTumblrAddAccountRequest request, string addedBy)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.email) || string.IsNullOrWhiteSpace(request.password))
            {
                throw new Exception("email and password are required.");
            }
            if (string.IsNullOrWhiteSpace(request.blogName))
            {
                throw new Exception("blogName is required.");
            }

            var requestedNiches = NormalizeNiches(request.memeNiches);
            if (request.useMemeScraperSource)
            {
                if (!requestedNiches.Any())
                {
                    throw new Exception("At least one meme niche is required when useMemeScraperSource is true.");
                }

                var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)(await GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>())[0];
                bool nicheHasSources = memeScraper.SourceManager.InstagramSources.Any(source =>
                    source.Niches != null && source.Niches.Any(n => requestedNiches.Contains(n.NicheTagName.Trim(), StringComparer.OrdinalIgnoreCase)));
                if (!nicheHasSources)
                {
                    throw new Exception("No MemeScraper sources match the supplied niches.");
                }
            }

            var account = store.Accounts.Values.FirstOrDefault(x => x.Email.Equals(request.email, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new OmniTumblrAccount
                {
                    AccountId = Guid.NewGuid().ToString("N"),
                    Email = request.email.Trim(),
                    CreatedAtUtc = DateTime.UtcNow,
                    AddedBy = addedBy
                };
            }

            account.EncryptedPassword = EncryptSensitive(request.password.Trim());
            account.BlogName = request.blogName.Trim();
            account.OAuthTokenKey = request.oauthTokenKey?.Trim() ?? account.OAuthTokenKey;
            account.OAuthTokenSecret = request.oauthTokenSecret?.Trim() ?? account.OAuthTokenSecret;
            account.UseMemeScraperSource = request.useMemeScraperSource;
            account.PreferredMemeNiches = requestedNiches;
            account.AutonomousPostingEnabled = request.autonomousPostingEnabled ?? account.AutonomousPostingEnabled;
            if (request.autonomousPostingIntervalMinutes.HasValue && request.autonomousPostingIntervalMinutes.Value > 0)
            {
                account.AutonomousPostingIntervalMinutes = request.autonomousPostingIntervalMinutes.Value;
            }
            if (request.autonomousPostingRandomOffsetMinutes.HasValue && request.autonomousPostingRandomOffsetMinutes.Value >= 0)
            {
                account.AutonomousPostingRandomOffsetMinutes = request.autonomousPostingRandomOffsetMinutes.Value;
            }
            account.AutonomousCaptionPrompt = request.autonomousCaptionPrompt;
            account.UpdatedAtUtc = DateTime.UtcNow;

            var auth = await ValidateTumblrCredentials(account);
            account.Status = auth.Success ? OmniTumblrAccountStatus.Active : OmniTumblrAccountStatus.NeedsVerification;
            account.LastAuthenticationError = auth.Error;
            if (auth.Success)
            {
                account.LastAuthenticatedUtc = DateTime.UtcNow;
            }

            await store.SaveAccount(account);
            await LogEvent("Info", $"Managed tumblr account '{account.Email}' saved.", account.AccountId);
            await EnsureAutonomousScheduleForAccount(account, "account_saved");
            return account;
        }

        public async Task<OmniTumblrAccount> UpdateManagedAccountSettings(OmniTumblrUpdateAccountSettingsRequest request)
        {
            var account = GetManagedAccountOrThrow(request.accountId);

            if (request.useMemeScraperSource.HasValue)
            {
                account.UseMemeScraperSource = request.useMemeScraperSource.Value;
            }
            if (request.memeNiches != null)
            {
                var normalized = NormalizeNiches(request.memeNiches);
                if (account.UseMemeScraperSource && !normalized.Any())
                {
                    throw new Exception("At least one meme niche is required when MemeScraper source mode is enabled.");
                }
                account.PreferredMemeNiches = normalized;
            }
            if (request.autonomousPostingEnabled.HasValue)
            {
                account.AutonomousPostingEnabled = request.autonomousPostingEnabled.Value;
            }
            if (request.autonomousPostingIntervalMinutes.HasValue && request.autonomousPostingIntervalMinutes.Value > 0)
            {
                account.AutonomousPostingIntervalMinutes = request.autonomousPostingIntervalMinutes.Value;
            }
            if (request.autonomousPostingRandomOffsetMinutes.HasValue && request.autonomousPostingRandomOffsetMinutes.Value >= 0)
            {
                account.AutonomousPostingRandomOffsetMinutes = request.autonomousPostingRandomOffsetMinutes.Value;
            }
            if (request.autonomousCaptionPrompt != null)
            {
                account.AutonomousCaptionPrompt = request.autonomousCaptionPrompt;
            }
            if (!string.IsNullOrWhiteSpace(request.blogName))
            {
                account.BlogName = request.blogName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(request.oauthTokenKey))
            {
                account.OAuthTokenKey = request.oauthTokenKey.Trim();
            }
            if (!string.IsNullOrWhiteSpace(request.oauthTokenSecret))
            {
                account.OAuthTokenSecret = request.oauthTokenSecret.Trim();
            }

            account.UpdatedAtUtc = DateTime.UtcNow;
            await store.SaveAccount(account);
            await LogEvent("Info", "Managed tumblr account settings updated.", account.AccountId, metadata: request);
            await EnsureAutonomousScheduleForAccount(account, "settings_updated");
            return account;
        }

        public async Task<object> GetLiveAccountData(string accountId)
        {
            var account = GetManagedAccountOrThrow(accountId);
            var client = CreateTumblrClient(account);

            var userInfo = await client.GetUserInfoAsync();
            var blogInfo = await client.GetBlogInfoAsync(account.BlogName);
            var likes = await client.GetUserLikesAsync(0, 20);

            await LogEvent("Info", "Fetched live Tumblr account data.", account.AccountId);
            return new
            {
                Account = new
                {
                    account.AccountId,
                    account.Email,
                    account.BlogName,
                    account.Status,
                    account.LastAuthenticatedUtc,
                    account.LastAuthenticationError,
                    account.UseMemeScraperSource,
                    account.PreferredMemeNiches,
                    account.AutonomousPostingEnabled,
                    account.AutonomousPostingIntervalMinutes,
                    account.AutonomousPostingRandomOffsetMinutes
                },
                Tumblr = new
                {
                    UserInfo = userInfo,
                    BlogInfo = blogInfo,
                    Likes = likes
                },
                FetchTimestampUtc = DateTime.UtcNow
            };
        }

        public async Task<object> GetLiveAccountsAnalytics()
        {
            var activeAccounts = store.Accounts.Values.Where(x => x.Status == OmniTumblrAccountStatus.Active).ToList();
            var results = new List<object>();

            foreach (var account in activeAccounts)
            {
                try
                {
                    var client = CreateTumblrClient(account);
                    var blog = await client.GetBlogInfoAsync(account.BlogName);
                    var posts = await client.GetPostsAsync(account.BlogName, 0, 50, PostType.All, false, false, PostFilter.Raw, null);

                    var postList = posts.Result ?? Array.Empty<BasePost>();
                    results.Add(new
                    {
                        account.AccountId,
                        account.Email,
                        account.BlogName,
                        Success = true,
                        BlogTitle = blog.Title,
                        BlogPostCount = blog.PostsCount,
                        LikesCount = blog.LikesCount,
                        SamplePosts = postList.Length,
                        AvgNotes = postList.Any() ? postList.Average(x => x.NotesCount) : 0,
                        TopTags = postList.SelectMany(x => x.Tags ?? Array.Empty<string>())
                            .GroupBy(x => x)
                            .Select(g => new { tag = g.Key, count = g.Count() })
                            .OrderByDescending(x => x.count)
                            .Take(10)
                            .ToList(),
                        PostTypeBreakdown = postList
                            .GroupBy(x => x.Type.ToString())
                            .Select(g => new { type = g.Key, count = g.Count() })
                            .OrderByDescending(x => x.count)
                            .ToList()
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        account.AccountId,
                        account.Email,
                        account.BlogName,
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            await LogEvent("Info", "Fetched live Tumblr analytics for managed accounts.", metadata: new { activeAccounts = activeAccounts.Count });
            return new
            {
                TimestampUtc = DateTime.UtcNow,
                AccountCount = activeAccounts.Count,
                Results = results
            };
        }

        public async Task<bool> DeleteTumblrPost(OmniTumblrDeletePostRequest request)
        {
            var account = GetManagedAccountOrThrow(request.accountId);
            var client = CreateTumblrClient(account);
            await client.DeletePostAsync(account.BlogName, request.postId);
            await LogEvent("Info", "Tumblr post deleted.", account.AccountId, metadata: request);
            return true;
        }

        public async Task<bool> RemoveManagedAccount(OmniTumblrDeleteAccountRequest request)
        {
            var account = GetManagedAccountOrThrow(request.accountId);
            if (request.deleteAssociatedPosts)
            {
                var postsToDelete = store.Posts.Values.Where(x => x.AccountId == account.AccountId).ToList();
                foreach (var post in postsToDelete)
                {
                    await store.DeletePost(post);
                    if (store.Campaigns.TryGetValue(post.CampaignId, out var campaign))
                    {
                        campaign.PlannedPostIds.RemoveAll(x => x == post.PostId);
                        await store.SaveCampaignSafe(campaign);
                    }
                }
            }

            await store.DeleteAccount(account);
            await LogEvent("Info", "Managed tumblr account removed.", account.AccountId, metadata: new { request.deleteAssociatedPosts });
            return true;
        }

        public async Task<OmniTumblrCampaign> SchedulePost(OmniTumblrScheduleRequest request, string createdBy)
        {
            if (request.DispatchMode == OmniTumblrDispatchMode.SingleAccount && string.IsNullOrWhiteSpace(request.AccountId))
            {
                throw new Exception("accountId is required for SingleAccount dispatch.");
            }

            DateTime scheduleAt = request.ScheduledForUtc ?? DateTime.UtcNow.AddSeconds(5);
            if (scheduleAt <= DateTime.UtcNow)
            {
                scheduleAt = DateTime.UtcNow.AddSeconds(5);
            }

            var targets = ResolveTargets(request);
            if (targets.Count == 0)
            {
                throw new Exception("No active tumblr accounts matched dispatch criteria.");
            }

            var campaign = new OmniTumblrCampaign
            {
                CampaignId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = createdBy,
                DispatchMode = request.DispatchMode,
                Status = "Scheduled"
            };

            foreach (var account in targets)
            {
                var post = new OmniTumblrPostPlan
                {
                    PostId = Guid.NewGuid().ToString("N"),
                    CampaignId = campaign.CampaignId,
                    AccountId = account.AccountId,
                    UserCaption = request.UserCaption,
                    AICaptionPrompt = request.AICaptionPrompt,
                    MediaPath = request.MediaPath,
                    ScheduledForUtc = scheduleAt,
                    CreatedAtUtc = DateTime.UtcNow,
                    Status = OmniTumblrPostStatus.Pending
                };

                campaign.PlannedPostIds.Add(post.PostId);
                await store.SavePost(post);
                await ServiceCreateScheduledTask(scheduleAt, "OmniTumblrPost-" + post.PostId, "OmniTumblr", "Scheduled tumblr publish", true, post.PostId);
                await LogEvent("Info", "Tumblr post scheduled.", account.AccountId, post.PostId, new { createdBy, scheduleAt });
            }

            await store.SaveCampaign(campaign);
            return campaign;
        }

        public async Task ProcessPost(string postId)
        {
            await processLock.WaitAsync();
            try
            {
                if (!store.Posts.TryGetValue(postId, out var post))
                {
                    return;
                }
                if (!store.Accounts.TryGetValue(post.AccountId, out var account))
                {
                    post.Status = OmniTumblrPostStatus.Failed;
                    post.LastError = "Account not found";
                    await store.SavePost(post);
                    return;
                }

                post.Status = OmniTumblrPostStatus.Posting;
                post.LastAttemptUtc = DateTime.UtcNow;
                await store.SavePost(post);

                string mediaPath = await ResolveMediaPath(post, account);
                string caption = await ResolveCaption(post, account);

                var publishResult = await PublishWithTumblrApi(account, mediaPath, caption);
                if (publishResult.Success)
                {
                    post.Status = OmniTumblrPostStatus.Posted;
                    post.PostedAtUtc = DateTime.UtcNow;
                    post.ProviderPostId = publishResult.ProviderPostId;
                    post.LastError = null;
                }
                else
                {
                    post.Status = OmniTumblrPostStatus.Failed;
                    post.RetryCount += 1;
                    post.LastError = publishResult.Error;
                }

                await store.SavePost(post);
                await EnsureAutonomousScheduleForAccount(account, "post_processed");
            }
            catch (Exception ex)
            {
                if (store.Posts.TryGetValue(postId, out var post))
                {
                    post.Status = OmniTumblrPostStatus.Failed;
                    post.RetryCount += 1;
                    post.LastError = ex.Message;
                    await store.SavePost(post);
                }
                await ServiceLogError(ex, "OmniTumblr failed processing post " + postId);
            }
            finally
            {
                processLock.Release();
            }
        }

        private async Task<OmniTumblrPublishResult> PublishWithTumblrApi(OmniTumblrAccount account, string mediaPath, string caption)
        {
            try
            {
                var client = CreateTumblrClient(account);
                PostData postData;

                if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
                {
                    string ext = Path.GetExtension(mediaPath).ToLowerInvariant();
                    string mime = GetMimeType(ext);
                    byte[] bytes = await File.ReadAllBytesAsync(mediaPath);
                    var file = new BinaryFile(bytes, Path.GetFileName(mediaPath), mime);

                    if (ext is ".mp4" or ".mov" or ".m4v" or ".webm" or ".avi")
                    {
                        postData = PostData.CreateVideo(file, caption ?? string.Empty, Array.Empty<string>(), PostCreationState.Published);
                    }
                    else
                    {
                        postData = PostData.CreatePhoto(file, caption ?? string.Empty, caption ?? string.Empty, Array.Empty<string>(), PostCreationState.Published);
                    }
                }
                else
                {
                    postData = PostData.CreateText(string.Empty, caption ?? string.Empty, Array.Empty<string>(), PostCreationState.Published);
                }

                var result = await client.CreatePostAsync(account.BlogName, postData);
                return new OmniTumblrPublishResult(true, result.PostId.ToString(), string.Empty);
            }
            catch (Exception ex)
            {
                return new OmniTumblrPublishResult(false, string.Empty, ex.Message);
            }
        }

        private async Task<(bool Success, string? Error)> ValidateTumblrCredentials(OmniTumblrAccount account)
        {
            try
            {
                var client = CreateTumblrClient(account);
                var user = await client.GetUserInfoAsync();
                var blog = await client.GetBlogInfoAsync(account.BlogName);
                bool success = user != null && blog != null;
                return (success, success ? null : "Tumblr authentication check failed.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private TumblrClient CreateTumblrClient(OmniTumblrAccount account)
        {
            string consumerKey = GetStringOmniSetting("OmniTumblrConsumerKey", defaultValue: "").GetAwaiter().GetResult();
            string consumerSecret = GetStringOmniSetting("OmniTumblrConsumerSecret", defaultValue: "").GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(consumerKey) || string.IsNullOrWhiteSpace(consumerSecret))
            {
                throw new Exception("OmniTumblrConsumerKey and OmniTumblrConsumerSecret settings are required.");
            }
            if (string.IsNullOrWhiteSpace(account.OAuthTokenKey) || string.IsNullOrWhiteSpace(account.OAuthTokenSecret))
            {
                throw new Exception("oauthTokenKey and oauthTokenSecret are required for Tumblr API access.");
            }

            var factory = new TumblrClientFactory();
            return factory.Create<TumblrClient>(consumerKey, consumerSecret, new Token(account.OAuthTokenKey, account.OAuthTokenSecret));
        }

        public List<OmniTumblrAccount> GetAccounts()
        {
            return store.Accounts.Values.OrderBy(x => x.Email).ToList();
        }

        public List<OmniTumblrPostPlan> GetRecentPosts(int take = 500)
        {
            return store.Posts.Values.OrderByDescending(x => x.CreatedAtUtc).Take(take).ToList();
        }

        public List<OmniTumblrServiceEvent> GetRecentEvents(int take = 500)
        {
            return store.GetRecentEvents(take);
        }

        public object GetAnalytics(DateTime? fromUtc, DateTime? toUtc)
        {
            IEnumerable<OmniTumblrPostPlan> query = store.Posts.Values;
            if (fromUtc.HasValue) query = query.Where(x => x.CreatedAtUtc >= fromUtc.Value);
            if (toUtc.HasValue) query = query.Where(x => x.CreatedAtUtc <= toUtc.Value);

            var posts = query.ToList();
            int total = posts.Count;
            int posted = posts.Count(x => x.Status == OmniTumblrPostStatus.Posted);
            int failed = posts.Count(x => x.Status == OmniTumblrPostStatus.Failed);

            var byAccount = posts
                .GroupBy(x => x.AccountId)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Email = store.Accounts.TryGetValue(g.Key, out var acc) ? acc.Email : "Unknown",
                    BlogName = store.Accounts.TryGetValue(g.Key, out var acc2) ? acc2.BlogName : "Unknown",
                    Total = g.Count(),
                    Posted = g.Count(x => x.Status == OmniTumblrPostStatus.Posted),
                    Failed = g.Count(x => x.Status == OmniTumblrPostStatus.Failed),
                    AvgRetries = g.Any() ? g.Average(x => x.RetryCount) : 0
                })
                .OrderByDescending(x => x.Total)
                .ToList();

            return new
            {
                RangeStartUtc = fromUtc,
                RangeEndUtc = toUtc,
                TotalPosts = total,
                Posted = posted,
                Failed = failed,
                SuccessRate = total == 0 ? 0 : (double)posted / total * 100,
                AccountsTotal = store.Accounts.Count,
                AccountsActive = store.Accounts.Values.Count(x => x.Status == OmniTumblrAccountStatus.Active),
                ByAccount = byAccount,
                TopFailureReasons = posts
                    .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
                    .GroupBy(x => x.LastError)
                    .Select(g => new { error = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .Take(25)
                    .ToList()
            };
        }

        public async Task<string> SaveUploadedCampaignMedia(string originalFileName, byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length == 0)
            {
                throw new Exception("Uploaded media bytes are empty.");
            }

            string safeFileName = Path.GetFileName(originalFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                safeFileName = "upload.bin";
            }

            string extension = Path.GetExtension(safeFileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".mp4";
            }

            string storedFileName = $"Upload_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}";
            string targetPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrUploadsDirectory), storedFileName);

            await GetDataHandler().WriteBytesToFile(targetPath, fileBytes);
            await LogEvent("Info", "Campaign media uploaded and persisted.", metadata: new { originalFileName, targetPath, fileBytes.Length });
            return targetPath;
        }

        private async Task EnsureAutonomousPostingSchedulesOnBoot()
        {
            foreach (var account in store.Accounts.Values)
            {
                await EnsureAutonomousScheduleForAccount(account, "service_boot");
            }
        }

        private async Task EnsureAutonomousScheduleForAccount(OmniTumblrAccount account, string reason)
        {
            if (account.Status != OmniTumblrAccountStatus.Active
                || !account.AutonomousPostingEnabled
                || !account.UseMemeScraperSource
                || !account.PreferredMemeNiches.Any())
            {
                return;
            }

            bool hasPending = store.Posts.Values.Any(x => x.AccountId == account.AccountId && (x.Status == OmniTumblrPostStatus.Pending || x.Status == OmniTumblrPostStatus.Posting) && x.ScheduledForUtc >= DateTime.UtcNow.AddMinutes(-1));
            if (hasPending)
            {
                return;
            }

            int intervalMinutes = Math.Max(15, account.AutonomousPostingIntervalMinutes);
            int randomOffset = Math.Max(0, account.AutonomousPostingRandomOffsetMinutes);
            int randomDelta = randomOffset > 0 ? random.Next(-randomOffset, randomOffset + 1) : 0;
            DateTime due = DateTime.UtcNow.AddMinutes(intervalMinutes + randomDelta);
            if (due <= DateTime.UtcNow.AddMinutes(1)) due = DateTime.UtcNow.AddMinutes(1);

            await CreateAutonomousPostForAccount(account, due, reason);
        }

        private async Task CreateAutonomousPostForAccount(OmniTumblrAccount account, DateTime dueUtc, string reason)
        {
            var campaign = new OmniTumblrCampaign
            {
                CampaignId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "OmniTumblrAutonomous",
                DispatchMode = OmniTumblrDispatchMode.SingleAccount,
                Status = "Scheduled"
            };

            var post = new OmniTumblrPostPlan
            {
                PostId = Guid.NewGuid().ToString("N"),
                CampaignId = campaign.CampaignId,
                AccountId = account.AccountId,
                AICaptionPrompt = string.IsNullOrWhiteSpace(account.AutonomousCaptionPrompt)
                    ? $"Create a short Tumblr caption for blog {account.BlogName} from a meme reel with hashtags."
                    : account.AutonomousCaptionPrompt,
                ScheduledForUtc = dueUtc,
                CreatedAtUtc = DateTime.UtcNow,
                Status = OmniTumblrPostStatus.Pending
            };

            campaign.PlannedPostIds.Add(post.PostId);
            await store.SaveCampaign(campaign);
            await store.SavePost(post);
            await ServiceCreateScheduledTask(dueUtc, "OmniTumblrPost-" + post.PostId, "OmniTumblr", "Autonomous tumblr publish", true, post.PostId);

            await LogEvent("Info", "Autonomous tumblr post scheduled.", account.AccountId, post.PostId, new { reason, dueUtc });
        }

        private async Task<string> ResolveCaption(OmniTumblrPostPlan post, OmniTumblrAccount account)
        {
            string prompt = post.AICaptionPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = $"Write a short Tumblr caption for blog {account.BlogName}.";
            }

            try
            {
                var aiResponse = await ExecuteServiceMethod<KliveLLM>("QueryLLM", prompt);
                return aiResponse?.ToString() ?? post.UserCaption ?? "";
            }
            catch
            {
                return post.UserCaption ?? "";
            }
        }

        private async Task<string> ResolveMediaPath(OmniTumblrPostPlan post, OmniTumblrAccount account)
        {
            if (!string.IsNullOrWhiteSpace(post.MediaPath))
            {
                return post.MediaPath;
            }

            if (!account.UseMemeScraperSource || !account.PreferredMemeNiches.Any())
            {
                return "";
            }

            var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)(await GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>())[0];
            var accountNiches = NormalizeNiches(account.PreferredMemeNiches);
            var matchingSources = memeScraper.SourceManager.InstagramSources
                .Where(source => source.Niches != null && source.Niches.Any(n => accountNiches.Contains(n.NicheTagName.Trim(), StringComparer.OrdinalIgnoreCase)))
                .ToList();

            var ownerIds = matchingSources.Select(s => s.AccountID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usernames = matchingSources.Select(s => s.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = memeScraper.mediaManager.allScrapedReels
                .Where(x => ownerIds.Contains(x.OwnerID) || usernames.Contains(x.OwnerUsername))
                .OrderByDescending(x => x.DateTimeReelDownloaded)
                .ToList();

            var selected = candidates.FirstOrDefault(x => !account.PostedMemeReelPostIds.Contains(x.PostID)) ?? candidates.FirstOrDefault();
            if (selected == null)
            {
                return "";
            }

            account.PostedMemeReelPostIds.Add(selected.PostID);
            account.UpdatedAtUtc = DateTime.UtcNow;
            await store.SaveAccount(account);

            post.SelectedMemeReelPostId = selected.PostID;
            await store.SavePost(post);

            return selected.GetInstagramReelVideoFilePath();
        }

        private List<OmniTumblrAccount> ResolveTargets(OmniTumblrScheduleRequest request)
        {
            if (request.DispatchMode == OmniTumblrDispatchMode.AllManagedAccounts)
            {
                return store.Accounts.Values.Where(x => x.Status == OmniTumblrAccountStatus.Active).ToList();
            }

            return store.Accounts.Values.Where(x => x.AccountId == request.AccountId && x.Status == OmniTumblrAccountStatus.Active).ToList();
        }

        private OmniTumblrAccount GetManagedAccountOrThrow(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new Exception("accountId is required.");
            }
            if (!store.Accounts.TryGetValue(accountId, out var account))
            {
                throw new Exception("Managed tumblr account not found.");
            }
            return account;
        }

        private static List<string> NormalizeNiches(IEnumerable<string>? niches)
        {
            return niches?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
        }

        private static string GetMimeType(string extension)
        {
            return extension switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".avi" => "video/x-msvideo",
                _ => "application/octet-stream"
            };
        }

        private async Task LogEvent(string level, string message, string? accountId = null, string? postId = null, object? metadata = null)
        {
            var serviceEvent = new OmniTumblrServiceEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Message = message,
                AccountId = accountId,
                PostId = postId,
                MetadataJson = metadata == null ? null : JsonConvert.SerializeObject(metadata)
            };

            await store.SaveEvent(serviceEvent);
        }

        private static string EncryptSensitive(string plainText)
        {
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string DecryptSensitive(string encryptedText)
        {
            byte[] protectedBytes = Convert.FromBase64String(encryptedText);
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
    }
}
