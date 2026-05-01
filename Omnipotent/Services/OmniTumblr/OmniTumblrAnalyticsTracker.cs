using DontPanic.TumblrSharp.Client;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblrAnalyticsTracker
    {
        private readonly OmniTumblr service;
        private readonly OmniTumblrAccountManager accountManager;
        private readonly OmniTumblrPostScheduler postScheduler;

        private readonly List<OmniTumblrEvent> recentEvents = new();
        private const int MaxRecentEvents = 500;

        public OmniTumblrAnalyticsTracker(OmniTumblr service, OmniTumblrAccountManager accountManager, OmniTumblrPostScheduler postScheduler)
        {
            this.service = service;
            this.accountManager = accountManager;
            this.postScheduler = postScheduler;
        }

        public async Task InitializeAsync()
        {
            await LoadRecentEvents();
            await service.ServiceLog("[OmniTumblr] AnalyticsTracker initialised.");
        }

        // ── Fleet Dashboard ──

        public OmniTumblrDashboardStats GetFleetDashboardStats()
        {
            var allAccounts = accountManager.GetAllAccounts().ToList();
            var allPosts = postScheduler.GetAllPosts().ToList();
            var today = DateTime.UtcNow.Date;

            int errorAccounts = allAccounts.Count(a => a.ConnectionStatus == OmniTumblrConnectionStatus.Error);
            int totalPosts = allPosts.Count;
            int postedCount = allPosts.Count(p => p.Status == OmniTumblrPostStatus.Posted);
            int pendingCount = allPosts.Count(p => p.Status == OmniTumblrPostStatus.Queued);
            int failedCount = allPosts.Count(p => p.Status == OmniTumblrPostStatus.Failed);
            double successRate = totalPosts > 0 ? (double)postedCount / totalPosts * 100 : 0;

            int postsToday = allPosts.Count(p =>
                p.Status == OmniTumblrPostStatus.Posted &&
                p.PostedTime.HasValue && p.PostedTime.Value.Date == today);

            int postsThisWeek = allPosts.Count(p =>
                p.Status == OmniTumblrPostStatus.Posted &&
                p.PostedTime.HasValue && p.PostedTime.Value.Date >= today.AddDays(-7));

            long totalFollowers = allAccounts.Sum(a => a.FollowerCount);
            long followerGainToday = CalculateFollowerGainToday(allAccounts);
            double avgEngagement = allAccounts.Count > 0 ? 0 : 0; // Will be updated after snapshots

            return new OmniTumblrDashboardStats
            {
                TotalAccounts = allAccounts.Count,
                ActiveAccounts = allAccounts.Count(a => a.IsActive && !a.IsPaused),
                PausedAccounts = allAccounts.Count(a => a.IsPaused),
                ErrorAccounts = errorAccounts,
                TotalPosts = totalPosts,
                PostedCount = postedCount,
                SuccessRate = Math.Round(successRate, 2),
                PendingCount = pendingCount,
                FailedCount = failedCount,
                TotalFollowers = totalFollowers,
                FollowerGainToday = followerGainToday,
                AvgEngagementRate = Math.Round(avgEngagement, 2),
                PostsToday = postsToday,
                PostsThisWeek = postsThisWeek
            };
        }

        private long CalculateFollowerGainToday(IEnumerable<OmniTumblrAccount> accounts)
        {
            long gain = 0;
            var today = DateTime.UtcNow.Date;

            foreach (var account in accounts)
            {
                var accountDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAnalyticsDirectory, account.AccountId);
                if (!Directory.Exists(accountDir)) continue;

                var todayFile = Path.Combine(accountDir, $"{today:yyyy-MM-dd}.json");
                var yesterdayFile = Path.Combine(accountDir, $"{today.AddDays(-1):yyyy-MM-dd}.json");

                if (!File.Exists(todayFile) || !File.Exists(yesterdayFile)) continue;

                try
                {
                    var todaySnap = JsonConvert.DeserializeObject<OmniTumblrAnalyticsSnapshot>(File.ReadAllText(todayFile));
                    var yesterdaySnap = JsonConvert.DeserializeObject<OmniTumblrAnalyticsSnapshot>(File.ReadAllText(yesterdayFile));
                    if (todaySnap != null && yesterdaySnap != null)
                        gain += todaySnap.FollowerCount - yesterdaySnap.FollowerCount;
                }
                catch { }
            }

            return gain;
        }

        // ── Daily Snapshots ──

        public async Task TakeDailySnapshots()
        {
            await service.ServiceLog("[OmniTumblr] Taking daily analytics snapshots...");

            foreach (var account in accountManager.GetAllAccounts()
                .Where(a => a.IsActive && a.ConnectionStatus == OmniTumblrConnectionStatus.Connected))
            {
                try
                {
                    var client = accountManager.GetApiInstance(account.AccountId);
                    if (client == null) continue;

                    var blogInfo = await client.GetBlogInfoAsync(account.BlogName);
                    if (blogInfo == null) continue;

                    var now = DateTime.UtcNow;
                    var recentPosts = await client.GetPostsAsync(account.BlogName, 0, 20);
                    double avgNotes = 0;
                    if (recentPosts != null && recentPosts.Result?.Length > 0)
                    {
                        avgNotes = recentPosts.Result.Average(p => (double)p.NotesCount);
                    }

                    long followers = 0;
                    try
                    {
                        var followersInfo = await client.GetFollowersAsync(account.BlogName, 0, 1);
                        if (followersInfo != null) followers = followersInfo.Count;
                    }
                    catch { /* followers unavailable for some blogs */ }
                    double engagementRate = followers > 0 ? avgNotes / followers * 100 : 0;

                    var snapshot = new OmniTumblrAnalyticsSnapshot
                    {
                        AccountId = account.AccountId,
                        Timestamp = now,
                        FollowerCount = followers,
                        PostCount = blogInfo.PostsCount,
                        AvgNotesPerPost = Math.Round(avgNotes, 2),
                        EngagementRate = Math.Round(engagementRate, 4),
                        PostsToday = postScheduler.GetAllPosts().Count(p =>
                            p.AccountId == account.AccountId &&
                            p.Status == OmniTumblrPostStatus.Posted &&
                            p.PostedTime.HasValue &&
                            p.PostedTime.Value.Date == now.Date)
                    };

                    // Calculate change vs yesterday
                    var yesterday = DateTime.UtcNow.Date.AddDays(-1);
                    var yesterdaySnap = await LoadSnapshotForDate(account.AccountId, yesterday);
                    if (yesterdaySnap != null)
                    {
                        snapshot.FollowerChange = snapshot.FollowerCount - yesterdaySnap.FollowerCount;
                        snapshot.PostChange = snapshot.PostCount - yesterdaySnap.PostCount;
                    }

                    await SaveSnapshot(snapshot);

                    // Update account live stats
                    account.FollowerCount = followers;
                    account.PostCount = blogInfo.PostsCount;
                    account.Description = blogInfo.Description;
                    await accountManager.SaveAccountToDisk(account);

                    await service.ServiceLog($"[OmniTumblr] Snapshot for '{account.BlogName}': followers={followers}, engagement={engagementRate:F2}%");
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] Snapshot failed for '{account.BlogName}'");
                }
            }
        }

        // ── Account Summary ──

        public async Task<OmniTumblrAccountAnalyticsSummary> GetAccountAnalyticsSummary(string accountId)
        {
            var account = accountManager.GetAccountById(accountId);
            if (account == null) return null;

            var snapshots = await LoadAllSnapshots(accountId);
            snapshots = snapshots.OrderBy(s => s.Timestamp).ToList();

            var today = DateTime.UtcNow;
            var summary = new OmniTumblrAccountAnalyticsSummary
            {
                AccountId = accountId,
                BlogName = account.BlogName,
                CurrentFollowers = account.FollowerCount,
                CurrentPostCount = account.PostCount,
                CurrentLikes = account.LikesCount
            };

            if (snapshots.Count > 0)
            {
                summary.LatestEngagementRate = snapshots.Last().EngagementRate;
                summary.BestEngagementRate = snapshots.Max(s => s.EngagementRate);
                summary.PeakFollowerCount = snapshots.Max(s => s.FollowerCount);

                var dayAgoSnap = snapshots.LastOrDefault(s => s.Timestamp <= today.AddDays(-1));
                if (dayAgoSnap != null) summary.FollowerChange1d = account.FollowerCount - dayAgoSnap.FollowerCount;

                var weekAgoSnap = snapshots.LastOrDefault(s => s.Timestamp <= today.AddDays(-7));
                if (weekAgoSnap != null) summary.FollowerChange7d = account.FollowerCount - weekAgoSnap.FollowerCount;

                var monthAgoSnap = snapshots.LastOrDefault(s => s.Timestamp <= today.AddDays(-30));
                if (monthAgoSnap != null) summary.FollowerChange30d = account.FollowerCount - monthAgoSnap.FollowerCount;

                var last7dSnaps = snapshots.Where(s => s.Timestamp >= today.AddDays(-7)).ToList();
                summary.AvgEngagement7d = last7dSnaps.Count > 0
                    ? Math.Round(last7dSnaps.Average(s => s.EngagementRate), 4) : 0;

                summary.TotalPostsLast7d = last7dSnaps.Sum(s => s.PostsToday);
                summary.TotalPostsLast30d = snapshots.Where(s => s.Timestamp >= today.AddDays(-30)).Sum(s => s.PostsToday);

                summary.FollowerHistory = snapshots
                    .Select(s => new OmniTumblrAnalyticsDataPoint { Timestamp = s.Timestamp, Value = s.FollowerCount })
                    .ToList();
                summary.EngagementHistory = snapshots
                    .Select(s => new OmniTumblrAnalyticsDataPoint { Timestamp = s.Timestamp, Value = s.EngagementRate })
                    .ToList();
                summary.PostHistory = snapshots
                    .Select(s => new OmniTumblrAnalyticsDataPoint { Timestamp = s.Timestamp, Value = s.PostCount })
                    .ToList();
            }

            return summary;
        }

        // ── Events ──

        public async Task LogEvent(string accountId, string eventType, string message, string severity = "Info")
        {
            var ev = new OmniTumblrEvent
            {
                AccountId = accountId,
                EventType = eventType,
                Message = message,
                Severity = severity,
                Timestamp = DateTime.UtcNow
            };

            lock (recentEvents)
            {
                recentEvents.Add(ev);
                if (recentEvents.Count > MaxRecentEvents)
                    recentEvents.RemoveAt(0);
            }

            try
            {
                var evFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{accountId}_{eventType}.json";
                var evPath = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrEventsDirectory, evFileName);
                await File.WriteAllTextAsync(evPath, JsonConvert.SerializeObject(ev, Formatting.Indented));
            }
            catch { /* Non-critical */ }
        }

        public List<OmniTumblrEvent> GetRecentEvents(int count = 50)
        {
            lock (recentEvents)
            {
                return recentEvents.TakeLast(count).ToList();
            }
        }

        private async Task LoadRecentEvents()
        {
            try
            {
                var evFiles = Directory.GetFiles(OmniPaths.GlobalPaths.OmniTumblrEventsDirectory, "*.json")
                    .OrderByDescending(f => f)
                    .Take(MaxRecentEvents)
                    .ToList();

                foreach (var file in evFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var ev = JsonConvert.DeserializeObject<OmniTumblrEvent>(json);
                        if (ev != null) recentEvents.Add(ev);
                    }
                    catch { }
                }

                recentEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniTumblr] Failed to load recent events");
            }
        }

        // ── Snapshot Persistence ──

        private async Task SaveSnapshot(OmniTumblrAnalyticsSnapshot snapshot)
        {
            var accountDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAnalyticsDirectory, snapshot.AccountId);
            Directory.CreateDirectory(accountDir);

            var fileName = $"{snapshot.Timestamp:yyyy-MM-dd}.json";
            var filePath = Path.Combine(accountDir, fileName);
            await File.WriteAllTextAsync(filePath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        }

        private async Task<OmniTumblrAnalyticsSnapshot> LoadSnapshotForDate(string accountId, DateTime date)
        {
            var accountDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAnalyticsDirectory, accountId);
            var filePath = Path.Combine(accountDir, $"{date:yyyy-MM-dd}.json");

            if (!File.Exists(filePath)) return null;
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                return JsonConvert.DeserializeObject<OmniTumblrAnalyticsSnapshot>(json);
            }
            catch { return null; }
        }

        private async Task<List<OmniTumblrAnalyticsSnapshot>> LoadAllSnapshots(string accountId)
        {
            var accountDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAnalyticsDirectory, accountId);
            if (!Directory.Exists(accountDir)) return new();

            var snapshots = new List<OmniTumblrAnalyticsSnapshot>();
            foreach (var file in Directory.GetFiles(accountDir, "*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var snap = JsonConvert.DeserializeObject<OmniTumblrAnalyticsSnapshot>(json);
                    if (snap != null) snapshots.Add(snap);
                }
                catch { }
            }

            return snapshots;
        }
    }
}
