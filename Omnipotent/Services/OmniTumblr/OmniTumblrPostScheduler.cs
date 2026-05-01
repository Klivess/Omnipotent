using DontPanic.TumblrSharp;
using DontPanic.TumblrSharp.Client;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblrPostScheduler
    {
        private readonly OmniTumblr service;
        private readonly OmniTumblrAccountManager accountManager;
        private readonly ConcurrentDictionary<string, OmniTumblrPost> posts = new();

        public OmniTumblrPostScheduler(OmniTumblr service, OmniTumblrAccountManager accountManager)
        {
            this.service = service;
            this.accountManager = accountManager;
        }

        public IReadOnlyCollection<OmniTumblrPost> GetAllPosts() => posts.Values.ToList().AsReadOnly();

        public OmniTumblrPost GetPostById(string postId)
        {
            posts.TryGetValue(postId, out var post);
            return post;
        }

        public IReadOnlyCollection<OmniTumblrPost> GetQueuedPosts()
            => posts.Values.Where(p => p.Status == OmniTumblrPostStatus.Queued)
                          .OrderBy(p => p.ScheduledTime)
                          .ToList().AsReadOnly();

        public async Task InitializeAsync()
        {
            await LoadPostsFromDisk();
            await ScheduleQueuedPosts();
        }

        private async Task LoadPostsFromDisk()
        {
            var postFiles = Directory.GetFiles(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory, "*.json");
            foreach (var file in postFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var post = JsonConvert.DeserializeObject<OmniTumblrPost>(json);
                    if (post != null && !string.IsNullOrEmpty(post.PostId))
                        posts[post.PostId] = post;
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] Failed to load post from {file}");
                }
            }
            await service.ServiceLog($"[OmniTumblr] Loaded {posts.Count} posts from disk.");
        }

        private async Task ScheduleQueuedPosts()
        {
            foreach (var post in posts.Values.Where(p => p.Status == OmniTumblrPostStatus.Queued))
            {
                var scheduleTime = post.ScheduledTime > DateTime.Now ? post.ScheduledTime : DateTime.Now.AddMinutes(1);
                await service.ServiceCreateScheduledTask(
                    scheduleTime,
                    $"OmniTumblrPost_{post.PostId}",
                    "Tumblr Posting",
                    $"Post {post.PostType} to blog {post.AccountId}",
                    false,
                    post.PostId);
            }
        }

        public async Task<OmniTumblrPost> SchedulePostAsync(OmniTumblrPost post)
        {
            posts[post.PostId] = post;
            await SavePostToDisk(post);

            var scheduleTime = post.ScheduledTime > DateTime.Now ? post.ScheduledTime : DateTime.Now.AddMinutes(1);
            await service.ServiceCreateScheduledTask(
                scheduleTime,
                $"OmniTumblrPost_{post.PostId}",
                "Tumblr Posting",
                $"Post {post.PostType} to blog {post.AccountId}",
                false,
                post.PostId);

            await service.ServiceLog($"[OmniTumblr] Scheduled {post.PostType} post {post.PostId} for account {post.AccountId} at {post.ScheduledTime}.");
            return post;
        }

        public async Task<bool> CancelPostAsync(string postId)
        {
            if (!posts.TryGetValue(postId, out var post) || post.Status != OmniTumblrPostStatus.Queued)
                return false;

            posts.TryRemove(postId, out _);
            var filePath = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory, $"{postId}.json");
            if (File.Exists(filePath)) File.Delete(filePath);

            await service.ServiceLog($"[OmniTumblr] Cancelled post {postId}.");
            return true;
        }

        // ── Task Handler ──

        public async Task HandleScheduledPost(string postId)
        {
            if (!posts.TryGetValue(postId, out var post))
            {
                await service.ServiceLog($"[OmniTumblr] Post {postId} not found for scheduled publishing.");
                return;
            }

            if (post.Status != OmniTumblrPostStatus.Queued)
            {
                await service.ServiceLog($"[OmniTumblr] Post {postId} is no longer queued (status: {post.Status}). Skipping.");
                return;
            }

            var account = accountManager.GetAccountById(post.AccountId);
            if (account == null)
            {
                await service.ServiceLogError($"[OmniTumblr] Account {post.AccountId} not found for post {postId}.");
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "Account not found";
                await SavePostToDisk(post);
                return;
            }

            if (account.IsPaused || account.ConnectionStatus != OmniTumblrConnectionStatus.Connected)
            {
                await service.ServiceLog($"[OmniTumblr] Account '{account.BlogName}' not available. Rescheduling post {postId} in 30 minutes.");
                post.ScheduledTime = DateTime.Now.AddMinutes(30);
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniTumblrPost_{post.PostId}",
                    "Tumblr Posting", $"Retry post to {account.BlogName}", false, post.PostId);
                return;
            }

            if (!accountManager.CanPerformAction(account.AccountId))
            {
                var delay = service.GetActionDelaySeconds();
                post.ScheduledTime = DateTime.Now.AddSeconds(delay);
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniTumblrPost_{post.PostId}",
                    "Tumblr Posting", $"Rate-delayed post to {account.BlogName}", false, post.PostId);
                return;
            }

            var maxPosts = await service.GetIntOmniSetting("OmniTumblr_MaxPostsPerDayPerAccount", defaultValue: 10);
            var todayPostCount = posts.Values.Count(p =>
                p.AccountId == account.AccountId &&
                p.Status == OmniTumblrPostStatus.Posted &&
                p.PostedTime.HasValue &&
                p.PostedTime.Value.Date == DateTime.UtcNow.Date);

            if (todayPostCount >= maxPosts)
            {
                await service.ServiceLog($"[OmniTumblr] Daily limit ({maxPosts}) reached for '{account.BlogName}'. Rescheduling to tomorrow.");
                post.ScheduledTime = DateTime.Today.AddDays(1).AddHours(9).AddMinutes(new Random().Next(0, 120));
                await SavePostToDisk(post);
                await service.ServiceCreateScheduledTask(post.ScheduledTime, $"OmniTumblrPost_{post.PostId}",
                    "Tumblr Posting", $"Next-day post to {account.BlogName}", false, post.PostId);
                return;
            }

            await PublishPostAsync(account, post);
        }

        private async Task PublishPostAsync(OmniTumblrAccount account, OmniTumblrPost post)
        {
            var client = accountManager.GetApiInstance(account.AccountId);
            if (client == null)
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "No API client available";
                await SavePostToDisk(post);
                return;
            }

            post.Status = OmniTumblrPostStatus.Posting;
            await SavePostToDisk(post);
            await service.ServiceLog($"[OmniTumblr] Publishing {post.PostType} post {post.PostId} to '{account.BlogName}'...");

            try
            {
                var tags = post.GetNormalisedTags();
                PostCreationInfo result = null;

                switch (post.PostType)
                {
                    case OmniTumblrPostType.Photo:
                        result = await PublishPhotoAsync(client, account, post, tags);
                        break;
                    case OmniTumblrPostType.PhotoSet:
                        result = await PublishPhotoSetAsync(client, account, post, tags);
                        break;
                    case OmniTumblrPostType.Video:
                        result = await PublishVideoAsync(client, account, post, tags);
                        break;
                    case OmniTumblrPostType.Text:
                        result = await PublishTextAsync(client, account, post, tags);
                        break;
                    case OmniTumblrPostType.Quote:
                        result = await PublishQuoteAsync(client, account, post, tags);
                        break;
                    case OmniTumblrPostType.Link:
                        result = await PublishLinkAsync(client, account, post, tags);
                        break;
                }

                if (result != null)
                {
                    post.Status = OmniTumblrPostStatus.Posted;
                    post.PostedTime = DateTime.UtcNow;
                    post.TumblrPostId = result.PostId;
                    account.LastPostTime = DateTime.UtcNow;
                    accountManager.RecordAction(account.AccountId);
                    await service.ServiceLog($"[OmniTumblr] Successfully posted {post.PostType} to '{account.BlogName}' (TumblrPostId: {post.TumblrPostId}).");
                }
                else
                {
                    await HandlePublishFailure(account, post, "Publish returned null result");
                }

                await SavePostToDisk(post);
                await accountManager.SaveAccountToDisk(account);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniTumblr] Exception publishing post {post.PostId} to '{account.BlogName}'");
                await HandlePublishFailure(account, post, ex.Message);
            }
        }

        private async Task<PostCreationInfo> PublishPhotoAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            var mediaPath = post.MediaPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "Media file not found";
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(mediaPath);
            var binaryFile = new BinaryFile(bytes, Path.GetFileName(mediaPath));
            var postData = PostData.CreatePhoto(binaryFile, post.Caption, null, tags);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task<PostCreationInfo> PublishPhotoSetAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            if (post.MediaPaths == null || post.MediaPaths.Count < 2)
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "PhotoSet requires at least 2 media items";
                return null;
            }

            var binaryFiles = new List<BinaryFile>();
            foreach (var path in post.MediaPaths.Where(File.Exists))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                binaryFiles.Add(new BinaryFile(bytes, Path.GetFileName(path)));
            }

            if (binaryFiles.Count < 2)
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "Not enough valid media files for PhotoSet";
                return null;
            }

            var postData = PostData.CreatePhoto(binaryFiles, post.Caption, null, tags);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task<PostCreationInfo> PublishVideoAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            var mediaPath = post.MediaPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "Media file not found";
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(mediaPath);
            var binaryFile = new BinaryFile(bytes, Path.GetFileName(mediaPath));
            var postData = PostData.CreateVideo(binaryFile, post.Caption, tags);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task<PostCreationInfo> PublishTextAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            var postData = PostData.CreateText(post.Caption, post.Title, tags);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task<PostCreationInfo> PublishQuoteAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            var postData = PostData.CreateQuote(post.Caption, post.QuoteSource, tags);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task<PostCreationInfo> PublishLinkAsync(TumblrClient client, OmniTumblrAccount account, OmniTumblrPost post, IEnumerable<string> tags)
        {
            if (string.IsNullOrEmpty(post.SourceUrl))
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = "Link post requires a source URL";
                return null;
            }
            var postData = PostData.CreateLink(post.SourceUrl, null, post.Caption, null, null, null, tags, PostCreationState.Published);
            return await client.CreatePostAsync(account.BlogName, postData);
        }

        private async Task HandlePublishFailure(OmniTumblrAccount account, OmniTumblrPost post, string errorMessage)
        {
            post.RetryCount++;
            if (post.RetryCount >= post.MaxRetries)
            {
                post.Status = OmniTumblrPostStatus.Failed;
                post.ErrorMessage = $"Max retries ({post.MaxRetries}) exceeded. Last error: {errorMessage}";
                await SavePostToDisk(post);
                await service.ServiceLogError($"[OmniTumblr] Post {post.PostId} permanently failed for '{account.BlogName}': {errorMessage}");
                return;
            }

            post.Status = OmniTumblrPostStatus.Queued;
            post.ScheduledTime = DateTime.Now.AddMinutes(30);
            post.ErrorMessage = $"Retry {post.RetryCount}/{post.MaxRetries}: {errorMessage}";
            await SavePostToDisk(post);

            await service.ServiceCreateScheduledTask(
                post.ScheduledTime,
                $"OmniTumblrPostRetry_{post.PostId}",
                "Tumblr Posting",
                $"Retry failed post to '{account.BlogName}'",
                false,
                post.PostId);
        }

        // ── Content Folder Integration ──

        public async Task PullFromContentFoldersAsync()
        {
            try
            {
                var eligibleAccounts = accountManager.GetAllAccounts()
                    .Where(a => a.IsActive && !a.IsPaused
                        && a.ConnectionStatus == OmniTumblrConnectionStatus.Connected
                        && a.ContentConfig.ContentSource == OmniTumblrContentSource.ContentFolder
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
                        && (p.Status == OmniTumblrPostStatus.Queued || p.Status == OmniTumblrPostStatus.Posted)
                        && p.ScheduledTime.Date == DateTime.UtcNow.Date);
                    var slotsRemaining = config.PostsPerDay - todayPosts;
                    if (slotsRemaining <= 0) continue;

                    var allFiles = Directory.GetFiles(config.ContentFolderPath)
                        .Where(f => service.MediaManager.IsSupported(f) && !config.UsedContentPaths.Contains(f))
                        .ToList();

                    var filteredFiles = allFiles.Where(f =>
                    {
                        var inferredType = service.MediaManager.InferPostType(f);
                        return config.AllowedPostTypes.Contains(inferredType);
                    }).ToList();

                    if (filteredFiles.Count == 0) continue;

                    var rng = new Random();
                    var selectedFiles = config.SelectionMode == OmniTumblrContentSelectionMode.Random
                        ? filteredFiles.OrderBy(_ => rng.Next()).Take(slotsRemaining).ToList()
                        : filteredFiles.Take(slotsRemaining).ToList();

                    int slotIndex = 0;
                    foreach (var file in selectedFiles)
                    {
                        var postType = service.MediaManager.InferPostType(file);
                        var scheduledTime = GetNextScheduledTime(account, slotIndex);
                        var caption = GenerateCaptionForAccount(config);
                        var tags = SelectTagsForAccount(config);
                        var storedPath = await service.MediaManager.StoreUploadedMedia(file, account.AccountId);

                        var post = new OmniTumblrPost
                        {
                            AccountId = account.AccountId,
                            PostType = postType,
                            MediaPaths = new List<string> { storedPath },
                            Caption = caption,
                            Tags = tags,
                            ScheduledTime = scheduledTime,
                            SourceType = OmniTumblrContentSource.ContentFolder,
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
                    await service.ServiceLog($"[OmniTumblr] Pulled {newPosts} items from content folders into post queue.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniTumblr] Failed to pull from content folders");
            }
        }

        // ── Auto-Schedule ──

        public async Task AutoScheduleForAllAccounts()
        {
            try
            {
                var eligibleAccounts = accountManager.GetAllAccounts()
                    .Where(a => a.IsActive && !a.IsPaused && a.ConnectionStatus == OmniTumblrConnectionStatus.Connected)
                    .ToList();

                int totalScheduled = 0;

                foreach (var account in eligibleAccounts)
                {
                    var config = account.ContentConfig;
                    if (config.ContentSource != OmniTumblrContentSource.ContentFolder
                        || string.IsNullOrEmpty(config.ContentFolderPath)
                        || !Directory.Exists(config.ContentFolderPath)) continue;

                    var upcomingPosts = posts.Values.Count(p =>
                        p.AccountId == account.AccountId
                        && p.Status == OmniTumblrPostStatus.Queued
                        && p.ScheduledTime >= DateTime.UtcNow
                        && p.ScheduledTime <= DateTime.UtcNow.AddDays(2));

                    var targetQueueSize = config.PostsPerDay * 2;
                    var slotsToFill = targetQueueSize - upcomingPosts;
                    if (slotsToFill <= 0) continue;

                    var allFiles = Directory.GetFiles(config.ContentFolderPath)
                        .Where(f => service.MediaManager.IsSupported(f) && !config.UsedContentPaths.Contains(f))
                        .ToList();

                    var filteredFiles = allFiles.Where(f =>
                    {
                        var inferredType = service.MediaManager.InferPostType(f);
                        return config.AllowedPostTypes.Contains(inferredType);
                    }).ToList();

                    if (filteredFiles.Count == 0) continue;

                    var rng = new Random();
                    var selectedFiles = config.SelectionMode == OmniTumblrContentSelectionMode.Random
                        ? filteredFiles.OrderBy(_ => rng.Next()).Take(slotsToFill).ToList()
                        : filteredFiles.Take(slotsToFill).ToList();

                    int slotIndex = upcomingPosts;
                    foreach (var file in selectedFiles)
                    {
                        var postType = service.MediaManager.InferPostType(file);
                        var scheduledTime = GetNextScheduledTime(account, slotIndex);
                        var caption = GenerateCaptionForAccount(config);
                        var tags = SelectTagsForAccount(config);
                        var storedPath = await service.MediaManager.StoreUploadedMedia(file, account.AccountId);

                        var post = new OmniTumblrPost
                        {
                            AccountId = account.AccountId,
                            PostType = postType,
                            MediaPaths = new List<string> { storedPath },
                            Caption = caption,
                            Tags = tags,
                            ScheduledTime = scheduledTime,
                            SourceType = OmniTumblrContentSource.ContentFolder,
                            SourceId = Path.GetFileName(file)
                        };

                        await SchedulePostAsync(post);
                        config.UsedContentPaths.Add(file);
                        totalScheduled++;
                        slotIndex++;
                    }

                    await accountManager.SaveAccountToDisk(account);
                }

                if (totalScheduled > 0)
                    await service.ServiceLog($"[OmniTumblr] Auto-scheduled {totalScheduled} posts across fleet.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniTumblr] Failed during auto-scheduling");
            }
        }

        // ── Caption & Tag Generation ──

        public static string GenerateCaptionForAccount(OmniTumblrAccountContentConfig config)
        {
            return config.CaptionMode switch
            {
                OmniTumblrCaptionMode.Static => config.StaticCaption ?? "",
                OmniTumblrCaptionMode.RandomFromList when config.CandidateCaptions.Count > 0
                    => config.CandidateCaptions[new Random().Next(config.CandidateCaptions.Count)],
                OmniTumblrCaptionMode.AIGenerated => config.AICaptionPrompt ?? "",
                _ => ""
            };
        }

        public static List<string> SelectTagsForAccount(OmniTumblrAccountContentConfig config)
        {
            if (config.Tags == null || config.Tags.Count == 0)
                return new List<string>();

            if (!config.RotateTags || config.Tags.Count <= config.MaxTagsPerPost)
                return config.Tags.Take(config.MaxTagsPerPost).ToList();

            var rng = new Random();
            return config.Tags.OrderBy(_ => rng.Next()).Take(config.MaxTagsPerPost).ToList();
        }

        private DateTime GetNextScheduledTime(OmniTumblrAccount account, int slotOffset)
        {
            var config = account.ContentConfig;
            var now = DateTime.UtcNow;
            var today = now.Date;
            var rng = new Random();

            var preferredHours = config.PreferredPostHoursUTC.OrderBy(h => h).ToList();
            if (preferredHours.Count == 0) preferredHours = new List<int> { 9, 13, 18 };

            var offsetRange = Math.Max(0, config.ScheduleRandomOffsetMinutes);

            var candidateTimes = new List<DateTime>();
            for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
            {
                var day = today.AddDays(dayOffset);
                if (!config.ActiveDaysOfWeek.Contains((int)day.DayOfWeek)) continue;

                foreach (var hour in preferredHours)
                {
                    var baseTime = day.AddHours(hour);
                    var randomOffset = offsetRange > 0 ? rng.Next(-offsetRange, offsetRange + 1) : 0;
                    var candidate = baseTime.AddMinutes(randomOffset);
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

        private async Task SavePostToDisk(OmniTumblrPost post)
        {
            try
            {
                var filePath = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory, $"{post.PostId}.json");
                var json = JsonConvert.SerializeObject(post, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniTumblr] Failed to save post {post.PostId}");
            }
        }
    }
}
