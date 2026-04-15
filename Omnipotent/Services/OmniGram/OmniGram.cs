using Omnipotent.Service_Manager;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGram : OmniService
    {
        public OmniGramAccountManager AccountManager { get; private set; }
        public OmniGramPostScheduler PostScheduler { get; private set; }
        public OmniGramMediaManager MediaManager { get; private set; }
        public OmniGramAnalyticsTracker AnalyticsTracker { get; private set; }

        public OmniGram()
        {
            name = "OmniGram";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            var enabled = await GetBoolOmniSetting("OmniGram_Enabled", defaultValue: true);
            if (!enabled)
            {
                await ServiceLog("[OmniGram] Service is disabled via OmniSettings. Exiting.");
                return;
            }

            await ServiceLog("[OmniGram] Initializing...");

            // Initialize components
            AccountManager = new OmniGramAccountManager(this);
            PostScheduler = new OmniGramPostScheduler(this, AccountManager);
            MediaManager = new OmniGramMediaManager(this);
            AnalyticsTracker = new OmniGramAnalyticsTracker(this, AccountManager);

            MediaManager.EnsureDirectories();

            // Register routes
            var routes = new OmniGramRoutes(this);
            await routes.RegisterRoutes();
            await ServiceLog("[OmniGram] API routes registered.");

            // Load data
            await AccountManager.InitializeAsync();
            await PostScheduler.InitializeAsync();
            await AnalyticsTracker.InitializeAsync();

            // Subscribe to scheduled tasks
            GetTimeManagerService().TaskDue += OnTaskDue;

            // Schedule recurring tasks
            await ScheduleRecurringTasks();

            await ServiceLog($"[OmniGram] Initialized with {AccountManager.GetAllAccounts().Count} accounts, {PostScheduler.GetAllPosts().Count} posts.");
        }

        private async void OnTaskDue(object sender, TimeManager.ScheduledTask task)
        {
            try
            {
                if (task.taskName.StartsWith("OmniGramPost_") && !task.taskName.StartsWith("OmniGramPostRetry_"))
                {
                    var postId = task.PassableData?.ToString();
                    if (!string.IsNullOrEmpty(postId))
                        await PostScheduler.HandleScheduledPost(postId);
                }
                else if (task.taskName.StartsWith("OmniGramPostRetry_"))
                {
                    var postId = task.PassableData?.ToString();
                    if (!string.IsNullOrEmpty(postId))
                        await PostScheduler.HandleScheduledPost(postId);
                }
                else if (task.taskName.StartsWith("OmniGramRetryLogin_"))
                {
                    var accountId = task.PassableData?.ToString();
                    if (!string.IsNullOrEmpty(accountId))
                    {
                        var account = AccountManager.GetAccountById(accountId);
                        var api = AccountManager.GetApiInstance(accountId);
                        if (account != null && api != null)
                        {
                            await AccountManager.loginHandler.HandleLoginAsync(api, account);
                            await AccountManager.SaveAccountToDisk(account);
                        }
                    }
                }
                else if (task.taskName == "OmniGram_SessionHealthCheck")
                {
                    await AccountManager.RunSessionHealthCheck();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(4),
                        "OmniGram_SessionHealthCheck", "OmniGram", "Periodic session health check", false);
                }
                else if (task.taskName == "OmniGram_DailyAnalytics")
                {
                    await AnalyticsTracker.TakeDailySnapshots();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(24),
                        "OmniGram_DailyAnalytics", "OmniGram", "Daily analytics snapshots", false);
                }
                else if (task.taskName == "OmniGram_MemeScraperPull")
                {
                    var autoPull = await GetBoolOmniSetting("OmniGram_AutoPullFromMemeScraper", defaultValue: false);
                    if (autoPull)
                        await PostScheduler.PullFromMemeScraperAsync();

                    await PostScheduler.PullFromContentFoldersAsync();

                    var pullInterval = await GetIntOmniSetting("OmniGram_MemeScraperPullIntervalHours", defaultValue: 6);
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(pullInterval),
                        "OmniGram_MemeScraperPull", "OmniGram", "Pull content from MemeScraper and folders", false);
                }
                else if (task.taskName == "OmniGram_MediaCleanup")
                {
                    await MediaManager.CleanupOldMedia();
                    await ServiceCreateScheduledTask(DateTime.Now.AddDays(1),
                        "OmniGram_MediaCleanup", "OmniGram", "Clean up old media files", false);
                }
                else if (task.taskName == "OmniGram_AutoSchedule")
                {
                    await PostScheduler.AutoScheduleForAllAccounts();
                    await PostScheduler.PullFromMemeScraperAsync();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(6),
                        "OmniGram_AutoSchedule", "OmniGram", "Auto-schedule posts for all accounts", false);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"[OmniGram] Error handling task {task.taskName}");
            }
        }

        private async Task ScheduleRecurringTasks()
        {
            // Session health check every 4 hours
            await ServiceCreateScheduledTask(DateTime.Now.AddHours(4),
                "OmniGram_SessionHealthCheck", "OmniGram", "Periodic session health check", false);

            // Daily analytics at 2 AM
            var nextAnalytics = DateTime.Today.AddDays(1).AddHours(2);
            await ServiceCreateScheduledTask(nextAnalytics,
                "OmniGram_DailyAnalytics", "OmniGram", "Daily analytics snapshots", false);

            // Content pull (MemeScraper + Content Folders)
            var pullInterval = await GetIntOmniSetting("OmniGram_MemeScraperPullIntervalHours", defaultValue: 6);
            await ServiceCreateScheduledTask(DateTime.Now.AddHours(pullInterval),
                "OmniGram_MemeScraperPull", "OmniGram", "Pull content from MemeScraper and folders", false);

            // Media cleanup daily
            await ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(3),
                "OmniGram_MediaCleanup", "OmniGram", "Clean up old media files", false);

            // Auto-schedule: perpetual posting even if commander doesn't intervene
            await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(10),
                "OmniGram_AutoSchedule", "OmniGram", "Auto-schedule posts for all accounts", false);

            await ServiceLog("[OmniGram] Recurring tasks scheduled.");
        }

        public int GetActionDelaySeconds()
        {
            return GetIntOmniSetting("OmniGram_ActionDelaySeconds", defaultValue: 30).Result;
        }
    }
}
