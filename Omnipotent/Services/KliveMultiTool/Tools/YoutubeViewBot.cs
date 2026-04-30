using Omnipotent.Services.SeleniumManager;
using OpenQA.Selenium;
using static Omnipotent.Profiles.KMProfileManager;
using SeleniumMgr = Omnipotent.Services.SeleniumManager.SeleniumManager;

namespace Omnipotent.Services.KliveMultiTool.Tools
{
    public class YoutubeViewBot : KliveTool
    {
        public YoutubeViewBot()
        {
            Name = "YoutubeViewBot";
            Description = "Opens parallel headless Chrome instances to watch a YouTube video (muted) for 2 minutes each.";
        }

        public override KMPermissions RequiredPermission => KMPermissions.Klives;

        [KliveObservable("Active Instances")]
        public int ActiveInstances { get; private set; }

        [KliveObservable("Completed Views")]
        public int CompletedViews { get; private set; }

        [KliveObservable("Failed Views")]
        public int FailedViews { get; private set; }

        [KliveObservable("Status")]
        public string Status { get; private set; } = "Idle";

        [KliveFunction("Run View Bot", "Launches N headless Chrome instances to watch the given YouTube URL for 2 minutes each (muted).")]
        public async Task<KliveToolResult> RunViewBot(
            [KliveParam(Description = "Full YouTube video URL (e.g. https://www.youtube.com/watch?v=xxxxx)", Required = true)] string url,
            [KliveParam(Description = "Number of views to generate", Type = KliveToolParameterType.Int, Min = 1, Required = true)] int viewCount,
            [KliveParam(Description = "Max parallel instances running at once", Type = KliveToolParameterType.Slider, Min = 1, Max = 20, Step = 1, Required = false, DefaultValue = "5")] int concurrency = 5)
        {
            if (string.IsNullOrWhiteSpace(url) || (!url.Contains("youtube.com") && !url.Contains("youtu.be")))
                return KliveToolResult.Fail("Invalid YouTube URL.");

            if (viewCount < 1)
                return KliveToolResult.Fail("viewCount must be at least 1.");

            concurrency = Math.Clamp(concurrency, 1, 20);

            _activeInstances = 0;
            _completedViews = 0;
            _failedViews = 0;
            _totalViews = viewCount;
            ActiveInstances = 0;
            CompletedViews = 0;
            FailedViews = 0;
            Status = $"Running — 0/{viewCount} complete";

            await Log($"YoutubeViewBot starting: {viewCount} views, concurrency={concurrency}, url={url}");

            var seleniumManager = await Parent.GetSeleniumManager();
            var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = Enumerable.Range(0, viewCount).Select(i => RunSingleView(url, i, semaphore, (SeleniumMgr)seleniumManager));

            await Task.WhenAll(tasks);

            Status = $"Done — {CompletedViews} succeeded, {FailedViews} failed";
            await Log(Status);
            return KliveToolResult.Ok(Status);
        }

        private async Task RunSingleView(string url, int index, SemaphoreSlim semaphore, SeleniumMgr seleniumManager)
        {
            await semaphore.WaitAsync();
            Interlocked.Increment(ref _activeInstances);
            ActiveInstances = _activeInstances;

            SeleniumMgr.SeleniumObject? seleniumObject = null;
            try
            {
                seleniumObject = seleniumManager.CreateSeleniumObject($"YoutubeViewBot-{index}", TimeSpan.FromMinutes(5));
                seleniumObject.AddArgumentToOptions("--mute-audio");
                seleniumObject.AddArgumentToOptions("--autoplay-policy=no-user-gesture-required");
                seleniumObject.AddArgumentToOptions("--disable-dev-shm-usage");
                seleniumObject.AddArgumentToOptions("--disable-gpu");
                seleniumObject.AddArgumentToOptions("--window-size=1280,720");
                seleniumObject.AddArgumentToOptions("--disable-blink-features=AutomationControlled");

                var driver = seleniumObject.UseChromeDriver();
                driver.Navigate().GoToUrl(url);

                // Wait for page load then dismiss consent dialogs (EU region)
                await Task.Delay(3000);
                TryDismissConsent(driver);

                // Send mute key to player as a fallback (--mute-audio handles audio at OS level)
                await Task.Delay(2000);
                TryMute(driver);

                // Watch for 2 minutes
                await Task.Delay(TimeSpan.FromMinutes(2));

                Interlocked.Increment(ref _completedViews);
                CompletedViews = _completedViews;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedViews);
                FailedViews = _failedViews;
                await LogError(ex, $"View instance {index} failed");
            }
            finally
            {
                if (seleniumObject != null)
                    seleniumManager.StopUsingSeleniumObject(seleniumObject);

                Interlocked.Decrement(ref _activeInstances);
                ActiveInstances = _activeInstances;
                Status = $"Running — {_completedViews + _failedViews}/{_totalViews} complete";
                semaphore.Release();
            }
        }

        private static void TryDismissConsent(IWebDriver driver)
        {
            try
            {
                var btn = driver.FindElement(By.CssSelector("button[aria-label*='Accept'], button[aria-label*='Agree'], form[action*='consent'] button"));
                btn.Click();
            }
            catch { /* not present */ }
        }

        private static void TryMute(IWebDriver driver)
        {
            try
            {
                var player = driver.FindElement(By.CssSelector("#movie_player, .html5-video-player"));
                player.SendKeys("m");
            }
            catch { /* player not ready */ }
        }

        // Thread-safe backing fields for observable properties
        private int _activeInstances;
        private int _completedViews;
        private int _failedViews;
        private int _totalViews;
    }
}
