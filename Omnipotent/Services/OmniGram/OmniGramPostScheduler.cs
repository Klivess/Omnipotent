using InstagramApiSharp;
using InstagramApiSharp.API;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Classes.Models;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramPostScheduler
    {
        private readonly OmniGram service;
        private readonly OmniGramAccountManager accountManager;
        private readonly ConcurrentDictionary<string, OmniGramPost> posts = new();
        private readonly HashSet<string> postedSourceIds = new();

        public OmniGramPostScheduler(OmniGram service, OmniGramAccountManager accountManager)
        {
            this.service = service;
            this.accountManager = accountManager;
        }

        public IReadOnlyCollection<OmniGramPost> GetAllPosts() => posts.Values.ToList().AsReadOnly();

        public OmniGramPost GetPostById(string postId)
        {
            posts.TryGetValue(postId, out var post);
            return post;
        }

        public IReadOnlyCollection<OmniGramPost> GetQueuedPosts()
            => posts.Values.Where(p => p.Status == OmniGramPostStatus.Queued)
                          .OrderBy(p => p.ScheduledTime)
                          .ToList().AsReadOnly();

        public async Task InitializeAsync()
        {
            await LoadPostsFromDisk();
            LoadPostedSourceIds();
            await ScheduleQueuedPosts();
        }

        private async Task LoadPostsFromDisk()
        {
            var postFiles = Directory.GetFiles(OmniPaths.GlobalPaths.OmniGramPostsDirectory, "*.json");
            foreach (var file in postFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var post = JsonConvert.DeserializeObject<OmniGramPost>(json);
                    if (post != null && !string.IsNullOrEmpty(post.PostId))
                        posts[post.PostId] = post;
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Failed to load post from {file}");
                }
            }
            await service.ServiceLog($"[OmniGram] Loaded {posts.Count} posts from disk.");
        }

        private void LoadPostedSourceIds()
        {
            foreach (var post in posts.Values.Where(p => p.SourceType == OmniGramContentSource.MemeScraper && !string.IsNullOrEmpty(p.SourceId)))
            {
                postedSourceIds.Add(post.SourceId);
            }
        }

        private async Task ScheduleQueuedPosts()
        {
            foreach (var post in posts.Values.Where(p => p.Status == OmniGramPostStatus.Queued))
            {
                var scheduleTime = post.ScheduledTime > DateTime.Now ? post.ScheduledTime : DateTime.Now.AddMinutes(1);
                await service.ServiceCreateScheduledTask(
                    scheduleTime,
                    $"OmniGramPost_{post.PostId}",
                    "Instagram Posting",
                    $"Post {post.ContentType} to account {post.AccountId}",
                    false,
                    post.PostId);
            }
        }

        public async Task<OmniGramPost> SchedulePostAsync(OmniGramPost post)
        {
            posts[post.PostId] = post;
            await SavePostToDisk(post);

            var scheduleTime = post.ScheduledTime > DateTime.Now ? post.ScheduledTime : DateTime.Now.AddMinutes(1);
            await service.ServiceCreateScheduledTask(
                scheduleTime,
                $"OmniGramPost_{post.PostId}",
                "Instagram Posting",
                $"Post {post.ContentType} to account {post.AccountId}",
                false,
                post.PostId);

            await service.ServiceLog($"[OmniGram] Scheduled {post.ContentType} post {post.PostId} for account {post.AccountId} at {post.ScheduledTime}.");
            return post;
        }

        public async Task<bool> CancelPostAsync(string postId)
        {
            if (!posts.TryGetValue(postId, out var post) || post.Status != OmniGramPostStatus.Queued)
                return false;

            posts.TryRemove(postId, out _);
            var filePath = Path.Combine(OmniPaths.GlobalPaths.OmniGramPostsDirectory, $"{postId}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            await service.ServiceLog($"[OmniGram] Cancelled post {postId}.");
            return true;
        }

        // ── Task Handler (called by TimeManager.TaskDue) ──

        public async Task HandleScheduledPost(string postId)
        {
            if (!posts.TryGetValue(postId, out var post))
            {
                await service.ServiceLog($"[OmniGram] Post {postId} not found for scheduled publishing.");
                return;
            }

            if (post.Status != OmniGramPostStatus.Queued)
            {
                await service.ServiceLog($"[OmniGram] Post {postId} is no longer queued (status: {post.Status}). Skipping.");
                return;
            }

            var account = accountManager.GetAccountById(post.AccountId);
            if (account == null)
            {
                await service.ServiceLogError($"[OmniGram] Account {post.AccountId} not found for post {postId}.");
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Account not found";
                await SavePostToDisk(post);
                return;
            }

            if (account.IsPaused || account.LoginStatus != OmniGramLoginStatus.LoggedIn)
            {
                await service.ServiceLog($"[OmniGram] Account {account.Username} not available. Rescheduling post {postId} in 30 minutes.");
                post.ScheduledTime = DateTime.Now.AddMinutes(30);
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniGramPost_{post.PostId}",
                    "Instagram Posting", $"Retry post to {account.Username}", false, post.PostId);
                return;
            }

            if (!accountManager.CanPerformAction(account.AccountId))
            {
                var delay = service.GetActionDelaySeconds();
                await service.ServiceLog($"[OmniGram] Rate limit active for {account.Username}. Rescheduling post in {delay} seconds.");
                post.ScheduledTime = DateTime.Now.AddSeconds(delay);
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniGramPost_{post.PostId}",
                    "Instagram Posting", $"Rate-delayed post to {account.Username}", false, post.PostId);
                return;
            }

            // Check daily post limit
            var maxPosts = await service.GetIntOmniSetting("OmniGram_MaxPostsPerDayPerAccount", defaultValue: 4);
            var todayPostCount = posts.Values.Count(p =>
                p.AccountId == account.AccountId &&
                p.Status == OmniGramPostStatus.Posted &&
                p.PostedTime.HasValue &&
                p.PostedTime.Value.Date == DateTime.UtcNow.Date);

            if (todayPostCount >= maxPosts)
            {
                await service.ServiceLog($"[OmniGram] Daily limit ({maxPosts}) reached for {account.Username}. Rescheduling to tomorrow.");
                post.ScheduledTime = DateTime.Today.AddDays(1).AddHours(9).AddMinutes(new Random().Next(0, 120));
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniGramPost_{post.PostId}",
                    "Instagram Posting", $"Next-day post to {account.Username}", false, post.PostId);
                return;
            }

            // Publish
            await PublishPostAsync(account, post);
        }

        private async Task PublishPostAsync(OmniGramAccount account, OmniGramPost post)
        {
            var instaApi = accountManager.GetApiInstance(account.AccountId);
            if (instaApi == null)
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "No API instance available";
                await SavePostToDisk(post);
                return;
            }

            post.Status = OmniGramPostStatus.Posting;
            await SavePostToDisk(post);
            await service.ServiceLog($"[OmniGram] Publishing {post.ContentType} post {post.PostId} to {account.Username}...");

            try
            {
                var caption = post.GetCaptionWithHashtags();
                IResult<InstaMedia> result = null;

                switch (post.ContentType)
                {
                    case OmniGramContentType.Photo:
                        result = await PublishPhotoAsync(instaApi, account, post, caption);
                        break;
                    case OmniGramContentType.Reel:
                        result = await PublishReelAsync(instaApi, account, post, caption);
                        break;
                    case OmniGramContentType.Story:
                        await PublishStoryAsync(instaApi, account, post);
                        break;
                    case OmniGramContentType.Carousel:
                        result = await PublishCarouselAsync(instaApi, account, post, caption);
                        break;
                }

                if (post.ContentType != OmniGramContentType.Story)
                {
                    if (result != null && result.Succeeded)
                    {
                        post.Status = OmniGramPostStatus.Posted;
                        post.PostedTime = DateTime.UtcNow;
                        post.InstagramMediaId = result.Value?.Pk;
                        account.LastPostTime = DateTime.UtcNow;
                        accountManager.RecordAction(account.AccountId);
                        await service.ServiceLog($"[OmniGram] Successfully posted {post.ContentType} to {account.Username} (MediaId: {post.InstagramMediaId}).");
                    }
                    else if (result != null)
                    {
                        await HandlePublishFailure(account, post, instaApi, result.Info?.Message ?? "Unknown error");
                    }
                }

                await SavePostToDisk(post);
                await accountManager.SaveAccountToDisk(account);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Exception publishing post {post.PostId} to {account.Username}");
                await HandlePublishFailure(account, post, instaApi, ex.Message);
            }
        }

        private async Task<IResult<InstaMedia>> PublishPhotoAsync(IInstaApi instaApi, OmniGramAccount account, OmniGramPost post, string caption)
        {
            var mediaPath = post.MediaPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Media file not found";
                return null;
            }

            var imageUpload = new InstaImageUpload
            {
                Uri = mediaPath
            };

            return await accountManager.loginHandler.ExecuteWithChallengeHandlingAsync(
                instaApi, account,
                () => instaApi.MediaProcessor.UploadPhotoAsync(imageUpload, caption));
        }

        private async Task<IResult<InstaMedia>> PublishReelAsync(IInstaApi instaApi, OmniGramAccount account, OmniGramPost post, string caption)
        {
            var mediaPath = post.MediaPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Media file not found";
                return null;
            }

            var videoUpload = new InstaVideoUpload
            {
                Video = new InstaVideo(mediaPath, 0, 0)
            };

            return await accountManager.loginHandler.ExecuteWithChallengeHandlingAsync(
                instaApi, account,
                () => instaApi.MediaProcessor.UploadVideoAsync(videoUpload, caption));
        }

        private async Task PublishStoryAsync(IInstaApi instaApi, OmniGramAccount account, OmniGramPost post)
        {
            var mediaPath = post.MediaPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Media file not found";
                await SavePostToDisk(post);
                return;
            }

            var ext = Path.GetExtension(mediaPath).ToLowerInvariant();
            bool isVideo = ext == ".mp4" || ext == ".mov";

            if (isVideo)
            {
                var videoUpload = new InstaVideoUpload
                {
                    Video = new InstaVideo(mediaPath, 0, 0)
                };
                var result = await accountManager.loginHandler.ExecuteWithChallengeHandlingAsync(
                    instaApi, account,
                    () => instaApi.StoryProcessor.UploadStoryVideoAsync(videoUpload, post.Caption ?? ""));

                if (result.Succeeded)
                {
                    post.Status = OmniGramPostStatus.Posted;
                    post.PostedTime = DateTime.UtcNow;
                    account.LastPostTime = DateTime.UtcNow;
                    accountManager.RecordAction(account.AccountId);
                    await service.ServiceLog($"[OmniGram] Story video posted to {account.Username}.");
                }
                else
                {
                    await HandlePublishFailure(account, post, instaApi, result.Info?.Message ?? "Story upload failed");
                }
            }
            else
            {
                var imageUpload = new InstaImage { Uri = mediaPath };
                var result = await accountManager.loginHandler.ExecuteWithChallengeHandlingAsync(
                    instaApi, account,
                    () => instaApi.StoryProcessor.UploadStoryPhotoAsync(imageUpload, post.Caption ?? ""));

                if (result.Succeeded)
                {
                    post.Status = OmniGramPostStatus.Posted;
                    post.PostedTime = DateTime.UtcNow;
                    account.LastPostTime = DateTime.UtcNow;
                    accountManager.RecordAction(account.AccountId);
                    await service.ServiceLog($"[OmniGram] Story photo posted to {account.Username}.");
                }
                else
                {
                    await HandlePublishFailure(account, post, instaApi, result.Info?.Message ?? "Story upload failed");
                }
            }
        }

        private async Task<IResult<InstaMedia>> PublishCarouselAsync(IInstaApi instaApi, OmniGramAccount account, OmniGramPost post, string caption)
        {
            if (post.MediaPaths == null || post.MediaPaths.Count < 2)
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Carousel requires at least 2 media items";
                return null;
            }

            var images = post.MediaPaths
                .Where(File.Exists)
                .Select(path => new InstaImageUpload { Uri = path })
                .ToArray();

            if (images.Length < 2)
            {
                post.Status = OmniGramPostStatus.Failed;
                post.ErrorMessage = "Not enough valid media files for carousel";
                return null;
            }

            return await accountManager.loginHandler.ExecuteWithChallengeHandlingAsync(
                instaApi, account,
                () => instaApi.MediaProcessor.UploadAlbumAsync(images, null, caption));
        }

        private async Task HandlePublishFailure(OmniGramAccount account, OmniGramPost post, IInstaApi instaApi, string errorMessage)
        {
            post.Status = OmniGramPostStatus.Failed;
            post.ErrorMessage = errorMessage;
            await SavePostToDisk(post);
            await service.ServiceLogError($"[OmniGram] Post {post.PostId} failed for {account.Username}: {errorMessage}");

            // Schedule single retry in 30 minutes
            var retryTaskName = $"OmniGramPostRetry_{post.PostId}";
            post.Status = OmniGramPostStatus.Queued;
            post.ScheduledTime = DateTime.Now.AddMinutes(30);
            post.ErrorMessage = $"Retrying after failure: {errorMessage}";
            await SavePostToDisk(post);

            await service.ServiceCreateScheduledTask(
                post.ScheduledTime,
                retryTaskName,
                "Instagram Posting",
                $"Retry failed post to {account.Username}",
                false,
                post.PostId);
        }

        // ── MemeScraper Integration ──

        public async Task PullFromMemeScraperAsync()
        {
            try
            {
                var memeScraperServices = await service.GetServicesByType<Omnipotent.Services.MemeScraper.MemeScraper>();
                if (memeScraperServices == null || memeScraperServices.Length == 0) return;

                var memeScraper = (Omnipotent.Services.MemeScraper.MemeScraper)memeScraperServices[0];
                if (memeScraper.mediaManager?.allScrapedReels == null) return;

                var eligibleAccounts = accountManager.GetAllAccounts()
                    .Where(a => a.IsActive && !a.IsPaused
                        && a.LoginStatus == OmniGramLoginStatus.LoggedIn
                        && a.ContentConfig.ContentSource == OmniGramContentSource.MemeScraper)
                    .ToList();

                if (eligibleAccounts.Count == 0) return;

                int newPosts = 0;

                foreach (var account in eligibleAccounts)
                {
                    var config = account.ContentConfig;
                    var nicheFilter = config.MemeScraperNicheFilter;

                    // How many more posts can this account have today?
                    var todayPosts = posts.Values.Count(p =>
                        p.AccountId == account.AccountId
                        && (p.Status == OmniGramPostStatus.Queued || p.Status == OmniGramPostStatus.Posted)
                        && p.ScheduledTime.Date == DateTime.UtcNow.Date);
                    var slotsRemaining = config.PostsPerDay - todayPosts;
                    if (slotsRemaining <= 0) continue;

                    var reels = memeScraper.mediaManager.allScrapedReels
                        .Where(r => !postedSourceIds.Contains(r.PostID)
                            && !string.IsNullOrEmpty(r.VideoDownloadURL)
                            && File.Exists(r.InstagramReelVideoFilePath))
                        .Take(slotsRemaining);

                    int slotIndex = 0;
                    foreach (var reel in reels)
                    {
                        var scheduledTime = GetNextScheduledTime(account, slotIndex);

                        var caption = config.UseAICaptionsForMemeScraper
                            ? (reel.Description ?? "")
                            : GenerateCaptionForAccount(config);

                        var hashtags = SelectHashtagsForAccount(config);

                        var post = new OmniGramPost
                        {
                            AccountId = account.AccountId,
                            ContentType = OmniGramContentType.Reel,
                            MediaPaths = new List<string> { reel.InstagramReelVideoFilePath },
                            Caption = caption,
                            Hashtags = hashtags,
                            ScheduledTime = scheduledTime,
                            SourceType = OmniGramContentSource.MemeScraper,
                            SourceId = reel.PostID
                        };

                        await SchedulePostAsync(post);
                        postedSourceIds.Add(reel.PostID);
                        newPosts++;
                        slotIndex++;
                    }
                }

                if (newPosts > 0)
                    await service.ServiceLog($"[OmniGram] Pulled {newPosts} reels from MemeScraper into post queue.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniGram] Failed to pull from MemeScraper");
            }
        }

        // ── Content Folder Integration ──

        public async Task PullFromContentFoldersAsync()
        {
            try
            {
                var eligibleAccounts = accountManager.GetAllAccounts()
                    .Where(a => a.IsActive && !a.IsPaused
                        && a.LoginStatus == OmniGramLoginStatus.LoggedIn
                        && a.ContentConfig.ContentSource == OmniGramContentSource.ContentFolder
                        && !string.IsNullOrEmpty(a.ContentConfig.ContentFolderPath))
                    .ToList();

                if (eligibleAccounts.Count == 0) return;

                int newPosts = 0;

                foreach (var account in eligibleAccounts)
                {
                    var config = account.ContentConfig;
                    if (!Directory.Exists(config.ContentFolderPath)) continue;

                    var todayPosts = posts.Values.Count(p =>
                        p.AccountId == account.AccountId
                        && (p.Status == OmniGramPostStatus.Queued || p.Status == OmniGramPostStatus.Posted)
                        && p.ScheduledTime.Date == DateTime.UtcNow.Date);
                    var slotsRemaining = config.PostsPerDay - todayPosts;
                    if (slotsRemaining <= 0) continue;

                    var allFiles = Directory.GetFiles(config.ContentFolderPath)
                        .Where(f => service.MediaManager.IsSupported(f)
                            && !config.UsedContentPaths.Contains(f))
                        .ToList();

                    if (allFiles.Count == 0)
                    {
                        await service.ServiceLog($"[OmniGram] No unused content in folder for {account.Username}.");
                        continue;
                    }

                    // Filter by allowed content types
                    var filteredFiles = allFiles.Where(f =>
                    {
                        var inferredType = service.MediaManager.InferContentType(f);
                        return config.AllowedContentTypes.Contains(inferredType);
                    }).ToList();

                    if (filteredFiles.Count == 0) continue;

                    // Select files based on selection mode
                    List<string> selectedFiles;
                    if (config.SelectionMode == OmniGramContentSelectionMode.Random)
                    {
                        var rng = new Random();
                        selectedFiles = filteredFiles.OrderBy(_ => rng.Next()).Take(slotsRemaining).ToList();
                    }
                    else
                    {
                        selectedFiles = filteredFiles.Take(slotsRemaining).ToList();
                    }

                    int slotIndex = 0;
                    foreach (var file in selectedFiles)
                    {
                        var contentType = service.MediaManager.InferContentType(file);
                        var scheduledTime = GetNextScheduledTime(account, slotIndex);
                        var caption = GenerateCaptionForAccount(config);
                        var hashtags = SelectHashtagsForAccount(config);

                        // Store media in OmniGram media directory
                        var storedPath = await service.MediaManager.StoreUploadedMedia(file, account.AccountId);

                        var post = new OmniGramPost
                        {
                            AccountId = account.AccountId,
                            ContentType = contentType,
                            MediaPaths = new List<string> { storedPath },
                            Caption = caption,
                            Hashtags = hashtags,
                            ScheduledTime = scheduledTime,
                            SourceType = OmniGramContentSource.ContentFolder,
                            SourceId = Path.GetFileName(file)
                        };

                        await SchedulePostAsync(post);
                        config.UsedContentPaths.Add(file);
                        newPosts++;
                        slotIndex++;
                    }

                    await accountManager.SaveAccountToDisk(account);
                }

                if (newPosts > 0)
                    await service.ServiceLog($"[OmniGram] Pulled {newPosts} items from content folders into post queue.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniGram] Failed to pull from content folders");
            }
        }

        // ── Per-Account Caption & Hashtag Generation ──

        public static string GenerateCaptionForAccount(OmniGramAccountContentConfig config)
        {
            return config.CaptionMode switch
            {
                OmniGramCaptionMode.Static => config.StaticCaption ?? "",
                OmniGramCaptionMode.RandomFromList when config.CandidateCaptions.Count > 0
                    => config.CandidateCaptions[new Random().Next(config.CandidateCaptions.Count)],
                OmniGramCaptionMode.AIGenerated => config.AICaptionPrompt ?? "",
                _ => ""
            };
        }

        public static List<string> SelectHashtagsForAccount(OmniGramAccountContentConfig config)
        {
            if (config.Hashtags == null || config.Hashtags.Count == 0)
                return new List<string>();

            if (!config.RotateHashtags || config.Hashtags.Count <= config.MaxHashtagsPerPost)
                return config.Hashtags.Take(config.MaxHashtagsPerPost).ToList();

            // Rotate: pick random subset
            var rng = new Random();
            return config.Hashtags.OrderBy(_ => rng.Next()).Take(config.MaxHashtagsPerPost).ToList();
        }

        private DateTime GetNextScheduledTime(OmniGramAccount account, int slotOffset)
        {
            var config = account.ContentConfig;
            var now = DateTime.UtcNow;
            var today = now.Date;

            // Find next available preferred hour
            var preferredHours = config.PreferredPostHoursUTC.OrderBy(h => h).ToList();
            if (preferredHours.Count == 0) preferredHours = new List<int> { 9, 13, 18 };

            // Find next slot
            var candidateTimes = new List<DateTime>();
            for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
            {
                var day = today.AddDays(dayOffset);
                if (!config.ActiveDaysOfWeek.Contains((int)day.DayOfWeek)) continue;

                foreach (var hour in preferredHours)
                {
                    var candidate = day.AddHours(hour).AddMinutes(new Random().Next(0, 30));
                    if (candidate > now)
                        candidateTimes.Add(candidate);
                }
            }

            if (candidateTimes.Count == 0)
                return now.AddMinutes(config.MinIntervalMinutes * (slotOffset + 1));

            return slotOffset < candidateTimes.Count
                ? candidateTimes[slotOffset]
                : candidateTimes.Last().AddMinutes(config.MinIntervalMinutes * (slotOffset - candidateTimes.Count + 1));
        }

        // ── Persistence ──

        private async Task SavePostToDisk(OmniGramPost post)
        {
            try
            {
                var filePath = Path.Combine(OmniPaths.GlobalPaths.OmniGramPostsDirectory, $"{post.PostId}.json");
                var json = JsonConvert.SerializeObject(post, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Failed to save post {post.PostId}");
            }
        }

        // ── Statistics ──

        public OmniGramDashboardStats GetDashboardStats()
        {
            return service.AnalyticsTracker.GetFleetDashboardStats();
        }
    }
}
