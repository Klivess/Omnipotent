using InstagramApiSharp;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramAnalyticsTracker
    {
        private readonly OmniGram service;
        private readonly OmniGramAccountManager accountManager;
        private readonly ConcurrentDictionary<string, List<OmniGramAnalyticsSnapshot>> snapshots = new();
        private readonly ConcurrentBag<OmniGramEvent> events = new();

        public OmniGramAnalyticsTracker(OmniGram service, OmniGramAccountManager accountManager)
        {
            this.service = service;
            this.accountManager = accountManager;
        }

        public async Task InitializeAsync()
        {
            await LoadSnapshotsFromDisk();
            await LoadEventsFromDisk();
        }

        // ── Event Logging ──

        public async Task LogEvent(string accountId, string eventType, string message, string severity = "Info")
        {
            var evt = new OmniGramEvent
            {
                AccountId = accountId,
                EventType = eventType,
                Message = message,
                Severity = severity
            };
            events.Add(evt);
            await SaveEventToDisk(evt);
        }

        public List<OmniGramEvent> GetRecentEvents(int count = 100, string accountId = null)
        {
            var query = events.AsEnumerable();
            if (!string.IsNullOrEmpty(accountId))
                query = query.Where(e => e.AccountId == accountId);
            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        // ── Snapshot Collection ──

        public async Task TakeDailySnapshots()
        {
            await service.ServiceLog("[OmniGram] Taking daily analytics snapshots...");

            foreach (var account in accountManager.GetAllAccounts()
                .Where(a => a.IsActive && a.LoginStatus == OmniGramLoginStatus.LoggedIn))
            {
                try
                {
                    var instaApi = accountManager.GetApiInstance(account.AccountId);
                    if (instaApi == null) continue;

                    var currentUser = await instaApi.GetCurrentUserAsync();
                    if (!currentUser.Succeeded) continue;

                    var userInfoResult = await instaApi.UserProcessor.GetUserInfoByIdAsync(currentUser.Value.Pk);
                    if (!userInfoResult.Succeeded) continue;

                    var userInfo = userInfoResult.Value;

                    var snapshot = new OmniGramAnalyticsSnapshot
                    {
                        AccountId = account.AccountId,
                        Timestamp = DateTime.UtcNow,
                        FollowerCount = (int)userInfo.FollowerCount,
                        FollowingCount = (int)userInfo.FollowingCount,
                        MediaCount = (int)userInfo.MediaCount
                    };

                    // Calculate changes from previous snapshot
                    if (snapshots.TryGetValue(account.AccountId, out var history) && history.Count > 0)
                    {
                        var lastSnapshot = history.OrderByDescending(s => s.Timestamp).First();
                        snapshot.FollowerChange = snapshot.FollowerCount - lastSnapshot.FollowerCount;
                        snapshot.FollowingChange = snapshot.FollowingCount - lastSnapshot.FollowingCount;
                        snapshot.MediaChange = snapshot.MediaCount - lastSnapshot.MediaCount;
                    }

                    // Count posts today for this account
                    snapshot.PostsToday = service.PostScheduler.GetAllPosts()
                        .Count(p => p.AccountId == account.AccountId
                            && p.Status == OmniGramPostStatus.Posted
                            && p.PostedTime.HasValue
                            && p.PostedTime.Value.Date == DateTime.UtcNow.Date);

                    // Update account profile info
                    account.FollowerCount = snapshot.FollowerCount;
                    account.FollowingCount = snapshot.FollowingCount;
                    account.MediaCount = snapshot.MediaCount;
                    account.Biography = currentUser.Value.Biography;
                    account.ProfilePicUrl = currentUser.Value.ProfilePicture;
                    await accountManager.SaveAccountToDisk(account);

                    // Calculate engagement rate from recent media
                    try
                    {
                        var mediaResult = await instaApi.UserProcessor.GetUserMediaAsync(
                            account.Username, PaginationParameters.MaxPagesToLoad(1));
                        if (mediaResult.Succeeded && mediaResult.Value.Count > 0)
                        {
                            double totalLikes = 0;
                            double totalComments = 0;
                            int mediaCount = 0;
                            foreach (var media in mediaResult.Value.Take(12))
                            {
                                totalLikes += media.LikesCount;
                                totalComments += long.TryParse(media.CommentsCount?.ToString(), out var cc) ? cc : 0;
                                mediaCount++;
                            }

                            if (mediaCount > 0)
                            {
                                snapshot.AvgLikes = Math.Round(totalLikes / mediaCount, 1);
                                snapshot.AvgComments = Math.Round(totalComments / mediaCount, 1);
                                if (snapshot.FollowerCount > 0)
                                {
                                    var avgEngagement = (totalLikes + totalComments) / mediaCount;
                                    snapshot.EngagementRate = Math.Round(avgEngagement / snapshot.FollowerCount * 100, 2);
                                }
                            }

                            // Estimate reach based on engagement
                            if (snapshot.FollowerCount > 0 && snapshot.EngagementRate > 0)
                            {
                                snapshot.ReachEstimate = Math.Round(snapshot.FollowerCount * (snapshot.EngagementRate / 100) * 3, 0);
                            }
                        }
                    }
                    catch { /* engagement rate is supplemental */ }

                    // Store snapshot
                    if (!snapshots.ContainsKey(account.AccountId))
                        snapshots[account.AccountId] = new List<OmniGramAnalyticsSnapshot>();
                    snapshots[account.AccountId].Add(snapshot);

                    await SaveSnapshotToDisk(account.AccountId, snapshot);
                    await LogEvent(account.AccountId, "AnalyticsSnapshot",
                        $"Followers: {snapshot.FollowerCount} ({(snapshot.FollowerChange >= 0 ? "+" : "")}{snapshot.FollowerChange}), Engagement: {snapshot.EngagementRate}%");
                    await service.ServiceLog($"[OmniGram] Snapshot for {account.Username}: {snapshot.FollowerCount} followers ({(snapshot.FollowerChange >= 0 ? "+" : "")}{snapshot.FollowerChange}), {snapshot.EngagementRate}% engagement.");
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Failed to take snapshot for {account.Username}");
                    await LogEvent(account.AccountId, "AnalyticsError", ex.Message, "Error");
                }
            }
        }

        // ── Snapshot Queries ──

        public List<OmniGramAnalyticsSnapshot> GetSnapshots(string accountId, int days = 30)
        {
            if (!snapshots.TryGetValue(accountId, out var history))
                return new List<OmniGramAnalyticsSnapshot>();

            var cutoff = DateTime.UtcNow.AddDays(-days);
            return history.Where(s => s.Timestamp >= cutoff).OrderBy(s => s.Timestamp).ToList();
        }

        public OmniGramAccountAnalyticsSummary GetAccountAnalyticsSummary(string accountId)
        {
            var account = accountManager.GetAccountById(accountId);
            var allHistory = GetSnapshots(accountId, 90);
            var history30 = allHistory.Where(s => s.Timestamp >= DateTime.UtcNow.AddDays(-30)).ToList();
            var history7 = allHistory.Where(s => s.Timestamp >= DateTime.UtcNow.AddDays(-7)).ToList();
            var history1 = allHistory.Where(s => s.Timestamp >= DateTime.UtcNow.AddDays(-1)).ToList();

            var allPosts = service.PostScheduler.GetAllPosts()
                .Where(p => p.AccountId == accountId && p.Status == OmniGramPostStatus.Posted).ToList();

            var summary = new OmniGramAccountAnalyticsSummary
            {
                AccountId = accountId,
                Username = account?.Username ?? "Unknown",
                CurrentFollowers = account?.FollowerCount ?? 0,
                CurrentFollowing = account?.FollowingCount ?? 0,
                CurrentMediaCount = account?.MediaCount ?? 0
            };

            if (allHistory.Count > 0)
            {
                summary.LatestEngagementRate = allHistory.Last().EngagementRate;
                summary.PeakFollowerCount = allHistory.Max(s => s.FollowerCount);
                summary.BestEngagementRate = allHistory.Max(s => s.EngagementRate);
            }

            // Follower changes over periods
            if (history1.Count >= 2)
                summary.FollowerChange1d = history1.Last().FollowerCount - history1.First().FollowerCount;
            if (history7.Count >= 2)
                summary.FollowerChange7d = history7.Last().FollowerCount - history7.First().FollowerCount;
            if (history30.Count >= 2)
                summary.FollowerChange30d = history30.Last().FollowerCount - history30.First().FollowerCount;

            // Average engagement over 7 days
            if (history7.Count > 0)
                summary.AvgEngagement7d = Math.Round(history7.Average(s => s.EngagementRate), 2);

            // Post counts
            summary.TotalPostsLast7d = allPosts.Count(p => p.PostedTime >= DateTime.UtcNow.AddDays(-7));
            summary.TotalPostsLast30d = allPosts.Count(p => p.PostedTime >= DateTime.UtcNow.AddDays(-30));

            // Build time-series data for charts
            summary.FollowerHistory = allHistory.Select(s => new OmniGramAnalyticsDataPoint
            {
                Timestamp = s.Timestamp,
                Value = s.FollowerCount
            }).ToList();

            summary.EngagementHistory = allHistory.Select(s => new OmniGramAnalyticsDataPoint
            {
                Timestamp = s.Timestamp,
                Value = s.EngagementRate
            }).ToList();

            summary.MediaHistory = allHistory.Select(s => new OmniGramAnalyticsDataPoint
            {
                Timestamp = s.Timestamp,
                Value = s.MediaCount
            }).ToList();

            return summary;
        }

        public OmniGramDashboardStats GetFleetDashboardStats()
        {
            var allAccounts = accountManager.GetAllAccounts();
            var allPosts = service.PostScheduler.GetAllPosts();
            var posted = allPosts.Count(p => p.Status == OmniGramPostStatus.Posted);
            var failed = allPosts.Count(p => p.Status == OmniGramPostStatus.Failed);
            var total = posted + failed;
            var now = DateTime.UtcNow;

            // Aggregate follower gain today across all accounts
            int totalFollowerGain = 0;
            double totalEngagement = 0;
            int engagementCount = 0;
            foreach (var account in allAccounts.Where(a => a.IsActive))
            {
                var todaySnapshots = GetSnapshots(account.AccountId, 1);
                if (todaySnapshots.Count >= 2)
                    totalFollowerGain += todaySnapshots.Last().FollowerCount - todaySnapshots.First().FollowerCount;
                if (todaySnapshots.Count > 0)
                {
                    totalEngagement += todaySnapshots.Last().EngagementRate;
                    engagementCount++;
                }
            }

            return new OmniGramDashboardStats
            {
                TotalAccounts = allAccounts.Count,
                ActiveAccounts = allAccounts.Count(a => a.IsActive && !a.IsPaused && a.LoginStatus == OmniGramLoginStatus.LoggedIn),
                PausedAccounts = allAccounts.Count(a => a.IsPaused),
                ErrorAccounts = allAccounts.Count(a => a.LoginStatus == OmniGramLoginStatus.Error
                    || a.LoginStatus == OmniGramLoginStatus.CredentialsInvalid
                    || a.LoginStatus == OmniGramLoginStatus.AccountDisabled),
                TotalPosts = allPosts.Count,
                PostedCount = posted,
                SuccessRate = total > 0 ? Math.Round((double)posted / total * 100, 1) : 0,
                PendingCount = allPosts.Count(p => p.Status == OmniGramPostStatus.Queued),
                FailedCount = failed,
                TotalFollowers = allAccounts.Sum(a => a.FollowerCount),
                FollowerGainToday = totalFollowerGain,
                AvgEngagementRate = engagementCount > 0 ? Math.Round(totalEngagement / engagementCount, 2) : 0,
                PostsToday = allPosts.Count(p => p.Status == OmniGramPostStatus.Posted
                    && p.PostedTime.HasValue && p.PostedTime.Value.Date == now.Date),
                PostsThisWeek = allPosts.Count(p => p.Status == OmniGramPostStatus.Posted
                    && p.PostedTime.HasValue && p.PostedTime.Value >= now.AddDays(-7))
            };
        }

        // ── Persistence ──

        private async Task LoadSnapshotsFromDisk()
        {
            var analyticsDir = OmniPaths.GlobalPaths.OmniGramAnalyticsDirectory;
            if (!Directory.Exists(analyticsDir)) return;

            foreach (var accountDir in Directory.GetDirectories(analyticsDir))
            {
                var accountId = Path.GetFileName(accountDir);
                var snapshotFiles = Directory.GetFiles(accountDir, "*.json");
                var accountSnapshots = new List<OmniGramAnalyticsSnapshot>();

                foreach (var file in snapshotFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var snapshot = JsonConvert.DeserializeObject<OmniGramAnalyticsSnapshot>(json);
                        if (snapshot != null)
                            accountSnapshots.Add(snapshot);
                    }
                    catch { /* skip corrupt files */ }
                }

                if (accountSnapshots.Count > 0)
                    snapshots[accountId] = accountSnapshots.OrderBy(s => s.Timestamp).ToList();
            }

            int totalSnapshots = snapshots.Values.Sum(s => s.Count);
            await service.ServiceLog($"[OmniGram] Loaded {totalSnapshots} analytics snapshots for {snapshots.Count} accounts.");
        }

        private async Task SaveSnapshotToDisk(string accountId, OmniGramAnalyticsSnapshot snapshot)
        {
            try
            {
                var dir = Path.Combine(OmniPaths.GlobalPaths.OmniGramAnalyticsDirectory, accountId);
                Directory.CreateDirectory(dir);

                var fileName = $"{snapshot.Timestamp:yyyy-MM-dd_HH-mm-ss}.json";
                var filePath = Path.Combine(dir, fileName);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Failed to save analytics snapshot for {accountId}");
            }
        }

        private async Task LoadEventsFromDisk()
        {
            var eventsDir = OmniPaths.GlobalPaths.OmniGramEventsDirectory;
            if (!Directory.Exists(eventsDir)) return;

            // Only load last 7 days of events
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.GetFiles(eventsDir, "*.json"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTimeUtc < cutoff) continue;

                    var json = await File.ReadAllTextAsync(file);
                    var evt = JsonConvert.DeserializeObject<OmniGramEvent>(json);
                    if (evt != null) events.Add(evt);
                }
                catch { /* skip corrupt files */ }
            }

            await service.ServiceLog($"[OmniGram] Loaded {events.Count} recent events.");
        }

        private async Task SaveEventToDisk(OmniGramEvent evt)
        {
            try
            {
                var dir = OmniPaths.GlobalPaths.OmniGramEventsDirectory;
                Directory.CreateDirectory(dir);

                var fileName = $"{evt.Timestamp:yyyy-MM-dd_HH-mm-ss}_{evt.EventType}.json";
                var filePath = Path.Combine(dir, fileName);
                var json = JsonConvert.SerializeObject(evt, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch { /* event logging is best-effort */ }
        }
    }
}
