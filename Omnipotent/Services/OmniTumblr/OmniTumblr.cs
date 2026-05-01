using Omnipotent.Service_Manager;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblr : OmniService
    {
        public OmniTumblrAccountManager AccountManager { get; private set; }
        public OmniTumblrPostScheduler PostScheduler { get; private set; }
        public OmniTumblrMediaManager MediaManager { get; private set; }
        public OmniTumblrAnalyticsTracker AnalyticsTracker { get; private set; }

        /// <summary>
        /// The callback URL registered in the Tumblr app. Must match exactly.
        /// Configurable via OmniSetting 'OmniTumblr_OAuthCallbackUrl'.
        /// </summary>
        public string OAuthCallbackUrl { get; private set; } = "https://klive.dev/omnitumblr/oauth/callback";

        public OmniTumblr()
        {
            name = "OmniTumblr";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            var enabled = await GetBoolOmniSetting("OmniTumblr_Enabled", defaultValue: true);
            OAuthCallbackUrl = await GetStringOmniSetting("OmniTumblr_OAuthCallbackUrl", defaultValue: "https://klive.dev/omnitumblr/oauth/callback");
            if (!enabled)
            {
                await ServiceLog("[OmniTumblr] Service is disabled via OmniSettings. Exiting.");
                return;
            }

            await ServiceLog("[OmniTumblr] Initializing...");

            // Initialize components
            MediaManager = new OmniTumblrMediaManager(this);
            AccountManager = new OmniTumblrAccountManager(this);
            PostScheduler = new OmniTumblrPostScheduler(this, AccountManager);
            AnalyticsTracker = new OmniTumblrAnalyticsTracker(this, AccountManager, PostScheduler);

            // Register routes
            var routes = new OmniTumblrRoutes(this);
            await routes.RegisterRoutes();
            await ServiceLog("[OmniTumblr] API routes registered.");

            // Load data
            await MediaManager.InitializeAsync();
            await AccountManager.InitializeAsync();
            await PostScheduler.InitializeAsync();
            await AnalyticsTracker.InitializeAsync();

            // Subscribe to scheduled tasks
            GetTimeManagerService().TaskDue += OnTaskDue;

            // Schedule recurring tasks
            await ScheduleRecurringTasks();

            await ServiceLog($"[OmniTumblr] Initialized with {AccountManager.GetAllAccounts().Count} accounts, {PostScheduler.GetAllPosts().Count} posts.");
        }

        private async void OnTaskDue(object sender, TimeManager.ScheduledTask task)
        {
            try
            {
                if (task.taskName.StartsWith("OmniTumblrPost_") || task.taskName.StartsWith("OmniTumblrPostRetry_"))
                {
                    var postId = task.PassableData?.ToString();
                    if (!string.IsNullOrEmpty(postId))
                        await PostScheduler.HandleScheduledPost(postId);
                }
                else if (task.taskName == "OmniTumblr_ConnectionHealthCheck")
                {
                    await AccountManager.RunConnectionHealthCheck();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(4),
                        "OmniTumblr_ConnectionHealthCheck", "OmniTumblr", "Periodic connection health check", false);
                }
                else if (task.taskName == "OmniTumblr_DailyAnalytics")
                {
                    await AnalyticsTracker.TakeDailySnapshots();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(24),
                        "OmniTumblr_DailyAnalytics", "OmniTumblr", "Daily analytics snapshots", false);
                }
                else if (task.taskName == "OmniTumblr_MediaCleanup")
                {
                    await MediaManager.CleanupOldMedia();
                    await ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(3),
                        "OmniTumblr_MediaCleanup", "OmniTumblr", "Clean up old media files", false);
                }
                else if (task.taskName == "OmniTumblr_AutoSchedule")
                {
                    await PostScheduler.AutoScheduleForAllAccounts();
                    await PostScheduler.PullFromContentFoldersAsync();
                    await ServiceCreateScheduledTask(DateTime.Now.AddHours(6),
                        "OmniTumblr_AutoSchedule", "OmniTumblr", "Auto-schedule posts for all accounts", false);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"[OmniTumblr] Error handling task {task.taskName}");
            }
        }

        private async Task ScheduleRecurringTasks()
        {
            // Connection health check every 4 hours
            await ServiceCreateScheduledTask(DateTime.Now.AddHours(4),
                "OmniTumblr_ConnectionHealthCheck", "OmniTumblr", "Periodic connection health check", false);

            // Daily analytics at 2 AM
            var nextAnalytics = DateTime.Today.AddDays(1).AddHours(2);
            await ServiceCreateScheduledTask(nextAnalytics,
                "OmniTumblr_DailyAnalytics", "OmniTumblr", "Daily analytics snapshots", false);

            // Media cleanup daily at 3 AM
            await ServiceCreateScheduledTask(DateTime.Today.AddDays(1).AddHours(3),
                "OmniTumblr_MediaCleanup", "OmniTumblr", "Clean up old media files", false);

            // Auto-schedule: first run in 10 minutes, then every 6 hours
            await ServiceCreateScheduledTask(DateTime.Now.AddMinutes(10),
                "OmniTumblr_AutoSchedule", "OmniTumblr", "Auto-schedule posts for all accounts", false);

            await ServiceLog("[OmniTumblr] Recurring tasks scheduled.");
        }

        public int GetActionDelaySeconds()
        {
            return GetIntOmniSetting("OmniTumblr_ActionDelaySeconds", defaultValue: 30).Result;
        }
    }
}
