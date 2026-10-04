using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>Opens a browser session for one scrape. Production uses SeleniumManager; tests can supply their own.</summary>
    public interface IScraperBrowserFactory
    {
        Task<ScraperBrowser> OpenAsync(string purpose, CancellationToken ct);
    }

    /// <summary>
    /// A headless Chrome session that looks like a normal desktop browser and is always torn down.
    ///
    /// Scrapers here talk to sites through the page's own JavaScript (in-page fetch, embedded JSON)
    /// rather than clicking styled-component CSS classes. The old scraper waited on generated class
    /// names like <c>StyledBtn-sc-1ygbkhl.kYFPxn</c>; one inflact redeploy removed the button and every
    /// scrape timed out. Nothing in this class or its callers depends on page styling.
    /// </summary>
    public sealed class ScraperBrowser : IDisposable
    {
        private readonly ChromeDriver driver;
        private readonly Action release;
        private int disposed;

        public ScraperBrowser(ChromeDriver driver, Action release)
        {
            this.driver = driver;
            this.release = release;
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(75);
            driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(90);
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
            NormalizeUserAgent();
        }

        /// <summary>Options that make headless Chrome indistinguishable enough for inflact/Instagram's logged-out pages.</summary>
        public static void ApplyOptions(ChromeOptions options, bool headless = true)
        {
            if (headless) options.AddArgument("--headless=new");
            options.AddArgument("--disable-blink-features=AutomationControlled");
            options.AddArgument("--window-size=1366,900");
            options.AddArgument("--lang=en-US");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--mute-audio");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddExcludedArgument("enable-automation");
            options.AddUserProfilePreference("intl.accept_languages", "en-US,en");
            // DOMContentLoaded is enough: both sites render from embedded JSON / XHR, and waiting for
            // every ad/analytics beacon to finish is where page loads used to hang.
            options.PageLoadStrategy = PageLoadStrategy.Eager;
        }

        /// <summary>
        /// Headless Chrome advertises "HeadlessChrome/x" in its UA, which bot filters key on. Swap in the
        /// same version as a regular Chrome UA so the UA still matches the real engine.
        /// </summary>
        private void NormalizeUserAgent()
        {
            try
            {
                var ua = Execute("return navigator.userAgent;") as string;
                if (string.IsNullOrEmpty(ua) || !ua.Contains("Headless", StringComparison.OrdinalIgnoreCase)) return;
                driver.ExecuteCdpCommand("Emulation.setUserAgentOverride", new Dictionary<string, object>
                {
                    ["userAgent"] = ua.Replace("HeadlessChrome", "Chrome"),
                    ["acceptLanguage"] = "en-US,en;q=0.9",
                    ["platform"] = "Windows",
                });
            }
            catch
            {
                // Best effort; a scrape can still succeed with the headless UA.
            }
        }

        /// <summary>Runs <paramref name="source"/> before any page script on every subsequent navigation.</summary>
        public void AddInitScript(string source)
        {
            driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { ["source"] = source });
        }

        public void Navigate(string url)
        {
            try
            {
                driver.Navigate().GoToUrl(url);
            }
            catch (WebDriverTimeoutException)
            {
                // Eager load strategy rarely times out, and when it does the document is usually
                // usable anyway; callers verify what they need instead of trusting the load event.
            }
        }

        public object? Execute(string script, params object[] args) => ((IJavaScriptExecutor)driver).ExecuteScript(script, args);

        /// <summary>Runs an async script; it must call <c>arguments[arguments.length - 1](result)</c>.</summary>
        public object? ExecuteAsync(string script, params object[] args) => ((IJavaScriptExecutor)driver).ExecuteAsyncScript(script, args);

        /// <summary>Polls <paramref name="probe"/> until it returns non-null or the timeout passes.</summary>
        public async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct, int pollMs = 500) where T : class
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                T? value = null;
                try { value = probe(); } catch (WebDriverException) when (DateTime.UtcNow < deadline) { }
                if (value != null) return value;
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(pollMs, ct);
            }
        }

        public string CurrentUrl
        {
            get { try { return driver.Url; } catch { return ""; } }
        }

        /// <summary>Title + start of the visible text, for failure messages ("Just a moment…", "limit reached", login walls).</summary>
        public string DescribePage(int maxChars = 300)
        {
            try
            {
                var text = Execute("return (document.title || '') + ' | ' + ((document.body && document.body.innerText) || '').replace(/\\s+/g, ' ').slice(0, 600);") as string ?? "";
                return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "…";
            }
            catch (Exception ex)
            {
                return "(page unreadable: " + ex.GetType().Name + ")";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            try { release(); } catch { }
        }
    }

    /// <summary>Production factory: borrows a Chrome from SeleniumManager and always hands it back.</summary>
    internal sealed class SeleniumManagerBrowserFactory : IScraperBrowserFactory
    {
        private readonly MemeScraper parent;

        public SeleniumManagerBrowserFactory(MemeScraper parent)
        {
            this.parent = parent;
        }

        public async Task<ScraperBrowser> OpenAsync(string purpose, CancellationToken ct)
        {
            var manager = await parent.GetSeleniumManager();
            // The inactivity watchdog is a backstop for a leaked session; real sessions end via Dispose.
            var seleniumObject = manager.CreateSeleniumObject(purpose, TimeSpan.FromMinutes(90));
            try
            {
                seleniumObject.ConfigureOptions(o => ScraperBrowser.ApplyOptions(o));
                var driver = await Task.Run(seleniumObject.UseChromeDriver, ct);
                return new ScraperBrowser(driver, () => manager.StopUsingSeleniumObject(seleniumObject));
            }
            catch (Exception ex)
            {
                manager.StopUsingSeleniumObject(seleniumObject);
                // Usually a Chrome/ChromeDriver problem on the host, not a site problem; say so plainly.
                throw new ReelProviderException("browser", "could not start Chrome: " + ex.Message, ex);
            }
        }
    }
}
