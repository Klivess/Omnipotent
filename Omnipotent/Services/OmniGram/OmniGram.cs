using InstagramApiSharp.API;
using InstagramApiSharp.API.Builder;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Logger;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveLLM;
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
        private readonly Random random = new();
        private static readonly InstagramApiSharp.Classes.Android.DeviceInfo.AndroidDevice FixedInstaDevice =
            InstagramApiSharp.Classes.Android.DeviceInfo.AndroidDeviceGenerator.GetByName(
                InstagramApiSharp.Classes.Android.DeviceInfo.AndroidDevices.GALAXY_S7_EDGE);

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

            var auth = await ValidateInstagramCredentials(account);
            account.Status = auth.Success ? OmniGramAccountStatus.Active : OmniGramAccountStatus.NeedsVerification;
            account.CheckpointRequired = auth.CheckpointRequired;
            account.LastAuthenticationError = auth.Error;
            account.LastAuthenticationGuidance = auth.Guidance;
            if (auth.Success)
            {
                account.LastAuthenticatedUtc = DateTime.UtcNow;
            }

            await store.SaveAccount(account);
            await LogEvent("Info", $"Managed account '{account.Username}' saved.", account.AccountId, metadata: new
            {
                account.AutonomousPostingEnabled,
                account.AutonomousPostingIntervalMinutes,
                account.AutonomousPostingRandomOffsetMinutes,
                account.PreferredMemeNiches,
                account.UseMemeScraperSource
            });

            await EnsureAutonomousScheduleForAccount(account, "account_saved");
            return account;
        }

        public async Task<OmniGramAccount> UpdateManagedAccountSettings(OmniGramUpdateAccountSettingsRequest request)
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

            account.UpdatedAtUtc = DateTime.UtcNow;
            await store.SaveAccount(account);
            await LogEvent("Info", "Managed account settings updated.", account.AccountId, metadata: request);
            await EnsureAutonomousScheduleForAccount(account, "settings_updated");
            return account;
        }

        public async Task<object> GetLiveAccountData(string accountId)
        {
            var account = GetManagedAccountOrThrow(accountId);
            var api = await BuildAuthenticatedApi(account);
            if (api == null)
            {
                throw new Exception("Instagram authentication failed for managed account.");
            }

            var userInfoResult = await api.UserProcessor.GetUserInfoByUsernameAsync(account.Username);
            if (!userInfoResult.Succeeded)
            {
                throw new Exception(userInfoResult.Info?.Message ?? "Failed to fetch live account data from Instagram.");
            }

            var loggedUser = api.GetLoggedUser();
            await LogEvent("Info", "Fetched live Instagram account data.", account.AccountId);

            return new
            {
                Account = new
                {
                    account.AccountId,
                    account.Username,
                    account.Status,
                    account.LastAuthenticatedUtc,
                    account.UseMemeScraperSource,
                    account.PreferredMemeNiches,
                    account.AutonomousPostingEnabled,
                    account.AutonomousPostingIntervalMinutes,
                    account.AutonomousPostingRandomOffsetMinutes,
                    account.CheckpointRequired,
                    account.LastAuthenticationError,
                    account.LastAuthenticationGuidance
                },
                Verification = new
                {
                    IsLoggedIn = true,
                    LoggedInUsername = loggedUser?.UserName,
                    FetchTimestampUtc = DateTime.UtcNow
                },
                Instagram = userInfoResult.Value
            };
        }

        public async Task<object> GetLiveAccountsAnalytics()
        {
            var activeAccounts = store.Accounts.Values.Where(x => x.Status == OmniGramAccountStatus.Active).ToList();
            var results = new List<object>();

            foreach (var account in activeAccounts)
            {
                try
                {
                    var api = await BuildAuthenticatedApi(account);
                    if (api == null)
                    {
                        results.Add(new
                        {
                            account.AccountId,
                            account.Username,
                            Success = false,
                            Error = "Authentication failed"
                        });
                        continue;
                    }

                    var userInfoResult = await api.UserProcessor.GetUserInfoByUsernameAsync(account.Username);
                    if (!userInfoResult.Succeeded)
                    {
                        results.Add(new
                        {
                            account.AccountId,
                            account.Username,
                            Success = false,
                            Error = userInfoResult.Info?.Message ?? "Instagram user info request failed"
                        });
                        continue;
                    }

                    results.Add(new
                    {
                        account.AccountId,
                        account.Username,
                        Success = true,
                        Instagram = userInfoResult.Value
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        account.AccountId,
                        account.Username,
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            await LogEvent("Info", "Fetched live Instagram analytics for managed accounts.", metadata: new { ActiveAccounts = activeAccounts.Count, Returned = results.Count });
            return new
            {
                TimestampUtc = DateTime.UtcNow,
                AccountCount = activeAccounts.Count,
                Results = results
            };
        }

        public async Task<object> UpdateManagedAccountProfile(OmniGramUpdateProfileRequest request, byte[]? profilePictureBytes)
        {
            var account = GetManagedAccountOrThrow(request.accountId);
            var api = await BuildAuthenticatedApi(account);
            if (api == null)
            {
                throw new Exception("Instagram authentication failed for managed account.");
            }

            string? biography = !string.IsNullOrWhiteSpace(request.biography) ? request.biography : request.bio;
            string? externalUrl = !string.IsNullOrWhiteSpace(request.externalUrl) ? request.externalUrl : request.website;

            var profileChanges = new List<string>();

            if (!string.IsNullOrWhiteSpace(biography))
            {
                var bioResult = await api.AccountProcessor.SetBiographyAsync(biography);
                if (!bioResult.Succeeded)
                {
                    throw new Exception(bioResult.Info?.Message ?? "Failed to set biography.");
                }
                profileChanges.Add("biography");
            }

            if (profilePictureBytes != null && profilePictureBytes.Length > 0)
            {
                var pfpResult = await api.AccountProcessor.ChangeProfilePictureAsync(profilePictureBytes);
                if (!pfpResult.Succeeded)
                {
                    throw new Exception(pfpResult.Info?.Message ?? "Failed to update profile picture.");
                }
                profileChanges.Add("profilePicture");
            }

            bool shouldEditProfile =
                !string.IsNullOrWhiteSpace(request.displayName)
                || !string.IsNullOrWhiteSpace(externalUrl)
                || !string.IsNullOrWhiteSpace(request.email)
                || !string.IsNullOrWhiteSpace(request.phoneNumber)
                || request.gender.HasValue
                || !string.IsNullOrWhiteSpace(request.username);

            if (shouldEditProfile)
            {
                InstagramApiSharp.Enums.InstaGenderType? gender = null;
                if (request.gender.HasValue && Enum.IsDefined(typeof(InstagramApiSharp.Enums.InstaGenderType), request.gender.Value))
                {
                    gender = (InstagramApiSharp.Enums.InstaGenderType)request.gender.Value;
                }

                var editResult = await api.AccountProcessor.EditProfileAsync(
                    request.displayName ?? string.Empty,
                    biography ?? string.Empty,
                    externalUrl ?? string.Empty,
                    request.email ?? string.Empty,
                    request.phoneNumber ?? string.Empty,
                    gender,
                    request.username ?? account.Username);

                if (!editResult.Succeeded)
                {
                    throw new Exception(editResult.Info?.Message ?? "Failed to edit Instagram profile.");
                }
                profileChanges.Add("profileFields");

                if (!string.IsNullOrWhiteSpace(request.username) && !request.username.Equals(account.Username, StringComparison.OrdinalIgnoreCase))
                {
                    account.Username = request.username;
                    account.UpdatedAtUtc = DateTime.UtcNow;
                    await store.SaveAccount(account);
                }
            }

            await LogEvent("Info", "Managed account Instagram profile updated.", account.AccountId, metadata: new { profileChanges });
            return await GetLiveAccountData(account.AccountId);
        }

        public async Task<bool> DeleteInstagramPost(OmniGramDeletePostRequest request)
        {
            var account = GetManagedAccountOrThrow(request.accountId);
            var api = await BuildAuthenticatedApi(account);
            if (api == null)
            {
                throw new Exception("Instagram authentication failed for managed account.");
            }

            string mediaId = !string.IsNullOrWhiteSpace(request.mediaId)
                ? request.mediaId
                : request.instagramMediaId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(mediaId))
            {
                throw new Exception("mediaId (or instagramMediaId) is required.");
            }

            var mediaTypesToTry = new List<InstagramApiSharp.Classes.Models.InstaMediaType>();
            if (request.mediaType.HasValue)
            {
                if (!Enum.IsDefined(typeof(InstagramApiSharp.Classes.Models.InstaMediaType), request.mediaType.Value))
                {
                    throw new Exception("Invalid mediaType value.");
                }

                mediaTypesToTry.Add((InstagramApiSharp.Classes.Models.InstaMediaType)request.mediaType.Value);
            }
            else
            {
                foreach (InstagramApiSharp.Classes.Models.InstaMediaType mediaType in Enum.GetValues(typeof(InstagramApiSharp.Classes.Models.InstaMediaType)))
                {
                    if (!mediaTypesToTry.Contains(mediaType))
                    {
                        mediaTypesToTry.Add(mediaType);
                    }
                }
            }

            var failedTypes = new List<string>();
            foreach (var mediaType in mediaTypesToTry)
            {
                var deleteResult = await api.MediaProcessor.DeleteMediaAsync(mediaId, mediaType);
                if (deleteResult.Succeeded)
                {
                    await LogEvent("Info", "Instagram media deleted.", account.AccountId, metadata: new { mediaId, mediaType });
                    return true;
                }

                failedTypes.Add($"{mediaType}: {deleteResult.Info?.Message ?? "Unknown"}");
            }

            throw new Exception("Failed to delete media from Instagram. " + string.Join(" | ", failedTypes));
        }

        public async Task<bool> RemoveManagedAccount(OmniGramDeleteAccountRequest request)
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

            sessionCache.Remove(account.AccountId);
            string statePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramSessionsDirectory), account.AccountId + ".state");
            await GetDataHandler().DeleteFile(statePath);

            await store.DeleteAccount(account);
            await LogEvent("Info", "Managed account removed.", account.AccountId, metadata: new { request.deleteAssociatedPosts });
            return true;
        }

        private async Task<(bool Success, bool CheckpointRequired, string? Error, string? Guidance)> ValidateInstagramCredentials(OmniGramAccount account)
        {
            try
            {
                var auth = await BuildAuthenticatedApiDetailed(account);
                return (auth.Api != null, auth.CheckpointRequired, auth.Error, auth.Guidance);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"OmniGram login validation failed for account {account.Username}");
                return (false, false, ex.Message, "Instagram auth failed due to a network or provider issue. Retry in a few minutes, then check /omnigram/accounts/live for detailed diagnostics.");
            }
        }

        private async Task<IInstaApi?> BuildAuthenticatedApi(OmniGramAccount account)
        {
            var auth = await BuildAuthenticatedApiDetailed(account);
            return auth.Api;
        }

        private async Task<(IInstaApi? Api, bool CheckpointRequired, string? Error, string? Guidance)> BuildAuthenticatedApiDetailed(OmniGramAccount account)
        {
            if (sessionCache.TryGetValue(account.AccountId, out IInstaApi cachedApi))
            {
                return (cachedApi, false, null, null);
            }

            string statePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramSessionsDirectory), account.AccountId + ".state");
            var user = new UserSessionData
            {
                UserName = account.Username,
                Password = DecryptSensitive(account.EncryptedPassword)
            };

            var api = InstaApiBuilder.CreateBuilder()
                .SetUser(user)
                .SetDevice(FixedInstaDevice)
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
                string loginError = login.Info?.Message ?? "Instagram login failed.";
                bool checkpointRequired = loginError.Contains("checkpoint_required", StringComparison.OrdinalIgnoreCase)
                    || loginError.Contains("challenge_required", StringComparison.OrdinalIgnoreCase);
                string guidance = BuildInstagramAuthGuidance(loginError, checkpointRequired);

                return (null, checkpointRequired, loginError, guidance);
            }

            await using (var stateStream = api.GetStateDataAsStream())
            await using (var outFile = File.Create(statePath))
            {
                await stateStream.CopyToAsync(outFile);
            }

            sessionCache.Set(account.AccountId, api, TimeSpan.FromMinutes(20));
            return (api, false, null, null);
        }

        private static string BuildInstagramAuthGuidance(string loginError, bool checkpointRequired)
        {
            if (checkpointRequired)
            {
                return "Instagram requires a 'This was me' confirmation in the Instagram app. Confirm the login attempt in-app, then retry verification from /omnigram/accounts/live.";
            }

            if (loginError.Contains("bad_password", StringComparison.OrdinalIgnoreCase)
                || loginError.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                return "Credentials were rejected by Instagram. Re-enter the account username/password and retry onboarding.";
            }

            if (loginError.Contains("login_required", StringComparison.OrdinalIgnoreCase)
                || loginError.Contains("session", StringComparison.OrdinalIgnoreCase))
            {
                return "Instagram session appears expired. Retry verification; if it persists, remove and re-add the managed account.";
            }

            if (loginError.Contains("wait", StringComparison.OrdinalIgnoreCase)
                || loginError.Contains("rate", StringComparison.OrdinalIgnoreCase)
                || loginError.Contains("throttle", StringComparison.OrdinalIgnoreCase))
            {
                return "Instagram is rate-limiting this login. Wait 5-15 minutes before retrying verification.";
            }

            return "Instagram returned an authentication error. Retry /omnigram/accounts/live and inspect LastAuthenticationError for details.";
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
                var aiResponse = await ExecuteServiceMethod<KliveLLM.KliveLLM>("QueryLLM", prompt);
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

            if (!account.UseMemeScraperSource || !account.PreferredMemeNiches.Any())
            {
                return "";
            }

            var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)(await GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>())[0];
            var accountNiches = NormalizeNiches(account.PreferredMemeNiches);
            if (!accountNiches.Any())
            {
                return "";
            }

            var matchingSources = memeScraper.SourceManager.InstagramSources
                .Where(source => source.Niches != null && source.Niches.Any(n => accountNiches.Contains(n.NicheTagName.Trim(), StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (!matchingSources.Any())
            {
                return "";
            }

            var matchingSourceOwnerIds = matchingSources.Select(s => s.AccountID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchingSourceUsernames = matchingSources.Select(s => s.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = memeScraper.mediaManager.allScrapedReels
                .Where(x => matchingSourceOwnerIds.Contains(x.OwnerID) || matchingSourceUsernames.Contains(x.OwnerUsername))
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
            if (!File.Exists(mediaPath))
            {
                return new OmniGramPublishResult(false, "", "Media file does not exist on disk.");
            }

            string extension = Path.GetExtension(mediaPath).ToLowerInvariant();
            bool isVideo = extension is ".mp4" or ".mov" or ".m4v" or ".avi" or ".webm";
            bool isImage = extension is ".jpg" or ".jpeg" or ".png";

            try
            {
                if (isVideo)
                {
                    var videoUpload = new InstagramApiSharp.Classes.Models.InstaVideoUpload
                    {
                        Video = new InstagramApiSharp.Classes.Models.InstaVideo
                        {
                            Uri = mediaPath
                        }
                    };

                    var uploadResult = await api.MediaProcessor.UploadVideoAsync(videoUpload, caption ?? string.Empty, null);
                    if (uploadResult.Succeeded && uploadResult.Value != null)
                    {
                        return new OmniGramPublishResult(true, uploadResult.Value.Pk.ToString(), "");
                    }

                    string error = uploadResult.Info?.Message ?? "Instagram video upload failed.";
                    return new OmniGramPublishResult(false, "", error);
                }

                if (isImage)
                {
                    var imageUpload = new InstagramApiSharp.Classes.Models.InstaImageUpload
                    {
                        Uri = mediaPath
                    };

                    var uploadResult = await api.MediaProcessor.UploadPhotoAsync(imageUpload, caption ?? string.Empty, null);
                    if (uploadResult.Succeeded && uploadResult.Value != null)
                    {
                        return new OmniGramPublishResult(true, uploadResult.Value.Pk.ToString(), "");
                    }

                    string error = uploadResult.Info?.Message ?? "Instagram image upload failed.";
                    return new OmniGramPublishResult(false, "", error);
                }

                return new OmniGramPublishResult(false, "", $"Unsupported media extension '{extension}'.");
            }
            catch (Exception ex)
            {
                return new OmniGramPublishResult(false, "", ex.Message);
            }
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
            string targetPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramUploadsDirectory), storedFileName);

            await GetDataHandler().WriteBytesToFile(targetPath, fileBytes);
            await LogEvent("Info", "Campaign media uploaded and persisted.", metadata: new { originalFileName, targetPath, fileBytes.Length });

            return targetPath;
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
                || !account.PreferredMemeNiches.Any())
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
            int randomOffset = Math.Max(0, account.AutonomousPostingRandomOffsetMinutes);
            int randomDelta = randomOffset > 0 ? random.Next(-randomOffset, randomOffset + 1) : 0;
            DateTime due = DateTime.UtcNow.AddMinutes(intervalMinutes + randomDelta);
            if (due <= DateTime.UtcNow.AddMinutes(1))
            {
                due = DateTime.UtcNow.AddMinutes(1);
            }
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
                intervalMinutes = account.AutonomousPostingIntervalMinutes,
                randomOffsetMinutes = account.AutonomousPostingRandomOffsetMinutes,
                account.PreferredMemeNiches
            });
        }

        private OmniGramAccount GetManagedAccountOrThrow(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new Exception("accountId is required.");
            }

            if (!store.Accounts.TryGetValue(accountId, out var account))
            {
                throw new Exception("Managed account not found.");
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
