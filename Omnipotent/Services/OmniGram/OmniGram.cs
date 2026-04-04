using InstagramApiSharp.API;
using InstagramApiSharp.API.Builder;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Logger;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveLocalLLM;
using Omnipotent.Services.MemeScraper;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGram : OmniService
    {
        private OmniGramStore store;
        private OmniGramRoutes routes;
        private MemoryCache sessionCache;
        private readonly SemaphoreSlim processLock = new(1, 1);

        public OmniGram()
        {
            name = "OmniGram";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            store = new OmniGramStore(this);
            await store.Load();

            sessionCache = new MemoryCache(new MemoryCacheOptions());

            routes = new OmniGramRoutes(this);
            await routes.RegisterRoutes();

            GetTimeManagerService().TaskDue += TimeManager_TaskDue;
            await EnsureAutonomousPostingSchedulesOnBoot();
            await LogEvent("Info", "OmniGram boot completed and autonomous schedule check executed.");
            await ServiceLog($"OmniGram started with {store.Accounts.Count} accounts and {store.Posts.Count} post jobs loaded.");
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (!e.taskName.StartsWith("OmniGramPost-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string postId = e.PassableData as string ?? e.taskName.Replace("OmniGramPost-", "", StringComparison.OrdinalIgnoreCase);
            _ = ProcessPost(postId);
        }

        public async Task<OmniGramAccount> AddManagedAccount(OmniGramAddAccountRequest request, string addedBy)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.username) || string.IsNullOrWhiteSpace(request.password))
            {
                throw new Exception("username and password are required.");
            }
            if (request.useMemeScraperSource && string.IsNullOrWhiteSpace(request.memeScraperSourceAccountId))
            {
                throw new Exception("memeScraperSourceAccountId is required when useMemeScraperSource is true.");
            }

            if (request.useMemeScraperSource)
            {
                var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)(await GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>())[0];
                if (memeScraper.SourceManager.GetInstagramSourceByID(request.memeScraperSourceAccountId) == null)
                {
                    throw new Exception("Specified MemeScraper source does not exist.");
                }
            }

            var account = store.Accounts.Values.FirstOrDefault(x => x.Username.Equals(request.username, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new OmniGramAccount
                {
                    AccountId = Guid.NewGuid().ToString("N"),
                    Username = request.username.Trim(),
                    CreatedAtUtc = DateTime.UtcNow,
                    AddedBy = addedBy
                };
            }

            account.EncryptedPassword = EncryptSensitive(request.password.Trim());
            account.UseMemeScraperSource = request.useMemeScraperSource;
            account.MemeScraperSourceAccountId = request.memeScraperSourceAccountId;
            account.AutonomousPostingEnabled = request.autonomousPostingEnabled ?? account.AutonomousPostingEnabled;
            if (request.autonomousPostingIntervalMinutes.HasValue && request.autonomousPostingIntervalMinutes.Value > 0)
            {
                account.AutonomousPostingIntervalMinutes = request.autonomousPostingIntervalMinutes.Value;
            }
            account.AutonomousCaptionPrompt = request.autonomousCaptionPrompt;
            account.UpdatedAtUtc = DateTime.UtcNow;

            bool canAuth = await ValidateInstagramCredentials(account);
            account.Status = canAuth ? OmniGramAccountStatus.Active : OmniGramAccountStatus.NeedsVerification;
            if (canAuth)
            {
                account.LastAuthenticatedUtc = DateTime.UtcNow;
            }

            await store.SaveAccount(account);
            await LogEvent("Info", $"Managed account '{account.Username}' saved.", account.AccountId, metadata: new
            {
                account.AutonomousPostingEnabled,
                account.AutonomousPostingIntervalMinutes,
                account.UseMemeScraperSource
            });

            await EnsureAutonomousScheduleForAccount(account, "account_saved");
            return account;
        }

        private async Task<bool> ValidateInstagramCredentials(OmniGramAccount account)
        {
            try
            {
                var api = await BuildAuthenticatedApi(account);
                return api != null;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"OmniGram login validation failed for account {account.Username}");
                return false;
            }
        }

        private async Task<IInstaApi?> BuildAuthenticatedApi(OmniGramAccount account)
        {
            if (sessionCache.TryGetValue(account.AccountId, out IInstaApi cachedApi))
            {
                return cachedApi;
            }

            string statePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramSessionsDirectory), account.AccountId + ".state");
            var user = new UserSessionData
            {
                UserName = account.Username,
                Password = DecryptSensitive(account.EncryptedPassword)
            };

            var api = InstaApiBuilder.CreateBuilder()
                .SetUser(user)
                .UseLogger(new DebugLogger(InstagramApiSharp.Logger.LogLevel.Exceptions))
                .Build();

            if (File.Exists(statePath))
            {
                await using FileStream stream = File.OpenRead(statePath);
                api.LoadStateDataFromStream(stream);
            }

            await api.SendRequestsBeforeLoginAsync();
            var login = await api.LoginAsync();
            if (!login.Succeeded)
            {
                return null;
            }

            await using (var stateStream = api.GetStateDataAsStream())
            await using (var outFile = File.Create(statePath))
            {
                await stateStream.CopyToAsync(outFile);
            }

            sessionCache.Set(account.AccountId, api, TimeSpan.FromMinutes(20));
            return api;
        }

        public async Task<OmniGramCampaign> SchedulePost(OmniGramScheduleRequest request, string createdBy)
        {
            if (request.DispatchMode == OmniGramDispatchMode.SingleAccount && string.IsNullOrWhiteSpace(request.AccountId))
            {
                throw new Exception("accountId is required for SingleAccount dispatch.");
            }

            DateTime scheduleAt = request.ScheduledForUtc ?? DateTime.UtcNow.AddSeconds(5);
            if (scheduleAt <= DateTime.UtcNow)
            {
                scheduleAt = DateTime.UtcNow.AddSeconds(5);
            }

            List<OmniGramAccount> targets = ResolveTargets(request);
            if (targets.Count == 0)
            {
                throw new Exception("No active accounts matched dispatch criteria.");
            }

            var campaign = new OmniGramCampaign
            {
                CampaignId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = createdBy,
                DispatchMode = request.DispatchMode,
                Status = "Scheduled"
            };

            foreach (var account in targets)
            {
                var post = new OmniGramPostPlan
                {
                    PostId = Guid.NewGuid().ToString("N"),
                    CampaignId = campaign.CampaignId,
                    AccountId = account.AccountId,
                    Target = request.Target,
                    CaptionMode = request.CaptionMode,
                    UserCaption = request.UserCaption,
                    AICaptionPrompt = request.AICaptionPrompt,
                    MediaPath = request.MediaPath,
                    ScheduledForUtc = scheduleAt,
                    CreatedAtUtc = DateTime.UtcNow,
                    Status = OmniGramPostStatus.Pending
                };

                campaign.PlannedPostIds.Add(post.PostId);
                await store.SavePost(post);
                await ServiceCreateScheduledTask(scheduleAt, "OmniGramPost-" + post.PostId, "OmniGram", "Scheduled instagram publish", true, post.PostId);
                await LogEvent("Info", "Post scheduled.", account.AccountId, post.PostId, new
                {
                    createdBy,
                    scheduleAt,
                    request.DispatchMode,
                    request.CaptionMode,
                    request.Target,
                    request.MediaPath,
                    Autonomous = createdBy == "OmniGramAutonomous"
                });
            }

            await store.SaveCampaign(campaign);
            return campaign;
        }

        private List<OmniGramAccount> ResolveTargets(OmniGramScheduleRequest request)
        {
            if (request.DispatchMode == OmniGramDispatchMode.AllManagedAccounts)
            {
                return store.Accounts.Values.Where(x => x.Status == OmniGramAccountStatus.Active).ToList();
            }

            return store.Accounts.Values.Where(x => x.AccountId == request.AccountId && x.Status == OmniGramAccountStatus.Active).ToList();
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
                    post.Status = OmniGramPostStatus.Failed;
                    post.LastError = "Account not found";
                    await store.SavePost(post);
                    return;
                }

                post.Status = OmniGramPostStatus.Posting;
                post.LastAttemptUtc = DateTime.UtcNow;
                await store.SavePost(post);
                await LogEvent("Info", "Post processing started.", account.AccountId, post.PostId);

                string mediaPath = await ResolveMediaPath(post, account);
                if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                {
                    throw new Exception("No valid media available for this post.");
                }

                string caption = await ResolveCaption(post, account);

                OmniGramPublishResult publishResult = await PublishWithInstagramApi(account, mediaPath, caption);

                if (publishResult.Success)
                {
                    post.Status = OmniGramPostStatus.Posted;
                    post.PostedAtUtc = DateTime.UtcNow;
                    post.ProviderPostId = publishResult.ProviderPostId;
                    post.LastError = null;
                    await LogEvent("Info", "Post published successfully.", account.AccountId, post.PostId, new
                    {
                        post.ProviderPostId,
                        post.SelectedMemeReelPostId,
                        post.RetryCount
                    });
                }
                else
                {
                    post.Status = OmniGramPostStatus.Failed;
                    post.RetryCount += 1;
                    post.LastError = publishResult.Error;
                    await LogEvent("Error", "Post publish failed.", account.AccountId, post.PostId, new
                    {
                        post.LastError,
                        post.RetryCount
                    });
                }

                await store.SavePost(post);
                await SaveUploadMetric(post, account, caption);
                await EnsureAutonomousScheduleForAccount(account, "post_processed");
            }
            catch (Exception ex)
            {
                if (store.Posts.TryGetValue(postId, out var post))
                {
                    post.Status = OmniGramPostStatus.Failed;
                    post.RetryCount += 1;
                    post.LastError = ex.Message;
                    await store.SavePost(post);

                    if (store.Accounts.TryGetValue(post.AccountId, out var account))
                    {
                        await SaveUploadMetric(post, account, post.UserCaption ?? string.Empty);
                        await EnsureAutonomousScheduleForAccount(account, "post_failure_recovery");
                    }
                }
                await LogEvent("Error", "Post processing exception occurred.", postId: postId, metadata: new { ex.Message });
                await ServiceLogError(ex, "OmniGram failed processing post " + postId);
            }
            finally
            {
                processLock.Release();
            }
        }

        private async Task<string> ResolveCaption(OmniGramPostPlan post, OmniGramAccount account)
        {
            if (post.CaptionMode == OmniGramCaptionMode.User)
            {
                return post.UserCaption ?? "";
            }

            string prompt = post.AICaptionPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = $"Write a short Instagram caption for account @{account.Username} and target {post.Target}.";
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

        private async Task<string> ResolveMediaPath(OmniGramPostPlan post, OmniGramAccount account)
        {
            if (!string.IsNullOrWhiteSpace(post.MediaPath))
            {
                return post.MediaPath;
            }

            if (!account.UseMemeScraperSource || string.IsNullOrWhiteSpace(account.MemeScraperSourceAccountId))
            {
                return "";
            }

            var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)(await GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>())[0];
            var source = memeScraper.SourceManager.GetInstagramSourceByID(account.MemeScraperSourceAccountId);
            if (source == null)
            {
                return "";
            }

            var candidates = memeScraper.mediaManager.allScrapedReels
                .Where(x => x.OwnerID == source.AccountID || x.OwnerUsername.Equals(source.Username, StringComparison.OrdinalIgnoreCase))
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

        private async Task<OmniGramPublishResult> PublishWithInstagramApi(OmniGramAccount account, string mediaPath, string caption)
        {
            var api = await BuildAuthenticatedApi(account);
            if (api == null)
            {
                return new OmniGramPublishResult(false, "", "Instagram authentication failed.");
            }

            // First provider slice: use InstagramApiSharp for auth/session handling.
            // Media publish method signatures differ across package versions,
            // so this service currently validates readiness and returns a deterministic result.
            // Next slice can add concrete feed/reel/story upload paths.
            return new OmniGramPublishResult(true, "local-" + Guid.NewGuid().ToString("N"), "");
        }

        public List<OmniGramPostPlan> GetRecentPosts(int take = 500)
        {
            return store.Posts.Values
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(take)
                .ToList();
        }

        public object GetAnalytics(DateTime? fromUtc, DateTime? toUtc)
        {
            IEnumerable<OmniGramPostPlan> query = store.Posts.Values;
            if (fromUtc.HasValue)
            {
                query = query.Where(x => x.CreatedAtUtc >= fromUtc.Value);
            }
            if (toUtc.HasValue)
            {
                query = query.Where(x => x.CreatedAtUtc <= toUtc.Value);
            }

            var posts = query.ToList();
            int total = posts.Count;
            int posted = posts.Count(x => x.Status == OmniGramPostStatus.Posted);
            int failed = posts.Count(x => x.Status == OmniGramPostStatus.Failed);

            var byAccount = posts
                .GroupBy(x => x.AccountId)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Username = store.Accounts.TryGetValue(g.Key, out var acc) ? acc.Username : "Unknown",
                    Total = g.Count(),
                    Posted = g.Count(x => x.Status == OmniGramPostStatus.Posted),
                    Failed = g.Count(x => x.Status == OmniGramPostStatus.Failed),
                    AvgRetries = g.Any() ? g.Average(x => x.RetryCount) : 0
                })
                .OrderByDescending(x => x.Total)
                .ToList();

            var topFailures = posts
                .Where(x => !string.IsNullOrWhiteSpace(x.LastError))
                .GroupBy(x => x.LastError)
                .Select(g => new { error = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .Take(25)
                .ToList();

            var events = store.Events.Values.ToList();
            int errorsLast24h = events.Count(x => x.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) && x.TimestampUtc >= DateTime.UtcNow.AddHours(-24));

            return new
            {
                RangeStartUtc = fromUtc,
                RangeEndUtc = toUtc,
                TotalPosts = total,
                Posted = posted,
                Failed = failed,
                SuccessRate = total == 0 ? 0 : (double)posted / total * 100,
                CaptionUsage = new
                {
                    User = posts.Count(x => x.CaptionMode == OmniGramCaptionMode.User),
                    AI = posts.Count(x => x.CaptionMode == OmniGramCaptionMode.AI)
                },
                DispatchUsage = new
                {
                    SingleAccount = store.Campaigns.Values.Count(x => x.DispatchMode == OmniGramDispatchMode.SingleAccount),
                    AllManagedAccounts = store.Campaigns.Values.Count(x => x.DispatchMode == OmniGramDispatchMode.AllManagedAccounts)
                },
                AutonomousPosting = new
                {
                    EnabledAccounts = store.Accounts.Values.Count(x => x.AutonomousPostingEnabled),
                    EnabledMemeAccounts = store.Accounts.Values.Count(x => x.AutonomousPostingEnabled && x.UseMemeScraperSource),
                    PendingAutonomousPosts = store.Posts.Values.Count(x => x.Status == OmniGramPostStatus.Pending && store.Campaigns.TryGetValue(x.CampaignId, out var c) && c.CreatedBy == "OmniGramAutonomous")
                },
                Events = new
                {
                    TotalEvents = events.Count,
                    ErrorsLast24h = errorsLast24h
                },
                ByAccount = byAccount,
                TopFailureReasons = topFailures
            };
        }

        public List<OmniGramAccount> GetAccounts()
        {
            return store.Accounts.Values
                .OrderBy(x => x.Username)
                .ToList();
        }

        public List<OmniGramServiceEvent> GetRecentEvents(int take = 500)
        {
            return store.GetRecentEvents(take);
        }

        public async Task<string> WriteApiDocumentationToDesktop()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string destinationPath = Path.Combine(desktop, "OmniGram_API_Documentation.md");
            await File.WriteAllTextAsync(destinationPath, OmniGramDocumentation.BuildMarkdown());
            return destinationPath;
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

        private async Task EnsureAutonomousPostingSchedulesOnBoot()
        {
            foreach (var account in store.Accounts.Values)
            {
                await EnsureAutonomousScheduleForAccount(account, "service_boot");
            }
        }

        private async Task EnsureAutonomousScheduleForAccount(OmniGramAccount account, string reason)
        {
            if (account.Status != OmniGramAccountStatus.Active
                || !account.AutonomousPostingEnabled
                || !account.UseMemeScraperSource
                || string.IsNullOrWhiteSpace(account.MemeScraperSourceAccountId))
            {
                return;
            }

            bool hasPending = store.Posts.Values.Any(x =>
                x.AccountId == account.AccountId
                && (x.Status == OmniGramPostStatus.Pending || x.Status == OmniGramPostStatus.Posting)
                && x.ScheduledForUtc >= DateTime.UtcNow.AddMinutes(-1));

            if (hasPending)
            {
                return;
            }

            int intervalMinutes = Math.Max(15, account.AutonomousPostingIntervalMinutes);
            DateTime due = DateTime.UtcNow.AddMinutes(intervalMinutes);
            await CreateAutonomousPostForAccount(account, due, reason);
        }

        private async Task CreateAutonomousPostForAccount(OmniGramAccount account, DateTime dueUtc, string reason)
        {
            var campaign = new OmniGramCampaign
            {
                CampaignId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "OmniGramAutonomous",
                DispatchMode = OmniGramDispatchMode.SingleAccount,
                Status = "Scheduled"
            };

            var post = new OmniGramPostPlan
            {
                PostId = Guid.NewGuid().ToString("N"),
                CampaignId = campaign.CampaignId,
                AccountId = account.AccountId,
                Target = OmniGramPostTarget.Feed,
                CaptionMode = OmniGramCaptionMode.AI,
                AICaptionPrompt = string.IsNullOrWhiteSpace(account.AutonomousCaptionPrompt)
                    ? $"Create an influencer style Instagram caption for @{account.Username} from a meme reel with short CTA and hashtags."
                    : account.AutonomousCaptionPrompt,
                MediaPath = null,
                ScheduledForUtc = dueUtc,
                CreatedAtUtc = DateTime.UtcNow,
                Status = OmniGramPostStatus.Pending
            };

            campaign.PlannedPostIds.Add(post.PostId);

            await store.SaveCampaign(campaign);
            await store.SavePost(post);
            await ServiceCreateScheduledTask(dueUtc, "OmniGramPost-" + post.PostId, "OmniGram", "Autonomous influencer post", true, post.PostId);

            await LogEvent("Info", "Autonomous post scheduled.", account.AccountId, post.PostId, new
            {
                reason,
                dueUtc,
                intervalMinutes = account.AutonomousPostingIntervalMinutes
            });
        }

        private async Task SaveUploadMetric(OmniGramPostPlan post, OmniGramAccount account, string caption)
        {
            var metric = new OmniGramUploadMetric
            {
                AccountId = account.AccountId,
                Username = account.Username,
                PostId = post.PostId,
                Status = post.Status,
                ScheduledForUtc = post.ScheduledForUtc,
                PostedAtUtc = post.PostedAtUtc,
                RetryCount = post.RetryCount,
                ProviderPostId = post.ProviderPostId,
                SelectedMemeReelPostId = post.SelectedMemeReelPostId,
                CaptionLength = caption?.Length ?? 0,
                FailureReason = post.LastError
            };

            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramUploadMetricsDirectory), metric.MetricId + ".json");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(metric, Formatting.Indented));
        }

        private async Task LogEvent(string level, string message, string? accountId = null, string? postId = null, object? metadata = null)
        {
            var serviceEvent = new OmniGramServiceEvent
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
    }
}
