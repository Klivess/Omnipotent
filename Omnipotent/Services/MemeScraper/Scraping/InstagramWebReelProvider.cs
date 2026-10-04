using System.Collections;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>
    /// Lists reels straight from Instagram's logged-out web pages — no third party involved.
    ///
    /// Logged-out Instagram (Oct 2026) refuses its JSON APIs (<c>/api/v1/users/web_profile_info</c> → 401
    /// require_login, 429 from plain HTTP) but still server-renders page data into
    /// <c>&lt;script type="application/json"&gt;</c> blocks for a real browser:
    /// <c>/{user}/reels/</c> carries the 12 newest clips, and each <c>/reel/{code}/</c> carries
    /// <c>video_versions</c> with directly downloadable CDN URLs. So this provider sees only the newest
    /// ~12 reels per profile, which is enough when scraping daily and is exactly what's needed to
    /// refresh an expired download URL.
    /// </summary>
    public sealed class InstagramWebReelProvider : IReelProvider, IReelResolver
    {
        public const string ProviderName = "instagram";
        private const string Origin = "https://www.instagram.com";

        // Returns only the embedded JSON blocks that can matter, plus a few fallbacks; all parsing is C#.
        private const string CollectScript = @"var needle = arguments[0];
var scripts = Array.prototype.slice.call(document.querySelectorAll('script[type=""application/json""]'))
  .map(function (s) { return s.textContent || ''; })
  .filter(function (t) { return t.indexOf(needle) >= 0; });
var anchors = Array.prototype.slice.call(document.querySelectorAll('a[href]'))
  .map(function (a) { return a.getAttribute('href') || ''; })
  .filter(function (h) { return /\/(reel|reels|p)\//.test(h); });
var meta = function (p) { var m = document.querySelector('meta[property=""' + p + '""]'); return m ? (m.getAttribute('content') || '') : ''; };
return { scripts: scripts, anchors: anchors, ogVideo: meta('og:video') || meta('og:video:secure_url'), ogDescription: meta('og:description') };";

        private readonly IScraperBrowserFactory browsers;
        private readonly Action<string> log;
        private readonly Random jitter = new();

        public InstagramWebReelProvider(IScraperBrowserFactory browsers, Action<string> log)
        {
            this.browsers = browsers;
            this.log = log;
        }

        public string Name => ProviderName;

        /// <summary>Upper bound on per-reel page loads in one run, to stay under Instagram's logged-out rate limits.</summary>
        public int MaxReelPagesPerRun { get; set; } = 15;

        public async Task<ReelDiscoveryResult> DiscoverAsync(ReelDiscoveryRequest request, CancellationToken ct)
        {
            using var browser = await browsers.OpenAsync("MemeScraper-instagram", ct);
            var listed = await ListProfileReelsAsync(browser, request.Username, ct);
            var result = new ReelDiscoveryResult { Provider = Name, PagesFetched = 1, Listed = listed.Count };

            int cap = Math.Max(0, request.MaxDetailPages ?? MaxReelPagesPerRun);
            var toResolve = listed.Where(r => !request.IsKnown(r)).ToList();
            if (toResolve.Count > cap)
            {
                result.TruncatedReason = $"{toResolve.Count} new reels, resolved the newest {cap} this run";
                toResolve = toResolve.Take(cap).ToList();
            }

            var resolved = await ResolveInBrowserAsync(browser, toResolve.Select(r => r.ShortCode).ToList(), ct);
            foreach (var reel in listed)
            {
                if (resolved.TryGetValue(reel.ShortCode, out var full))
                {
                    full.MergeFrom(reel); // keeps play_count from the grid, which the reel page omits
                    if (string.IsNullOrEmpty(full.OwnerUsername)) full.OwnerUsername = request.Username;
                    result.Reels.Add(full);
                }
                else if (request.IsKnown(reel))
                {
                    result.Reels.Add(reel);
                }
            }
            // Logged-out only ever shows the newest page, so this is complete only if it reached a known reel.
            result.CoveredAllNewReels = listed.Any(request.IsKnown) && result.TruncatedReason == null;
            if (toResolve.Count > 0 && resolved.Count == 0)
            {
                throw new ReelProviderException(Name, $"listed {listed.Count} reels but could not read video URLs for any of {toResolve.Count} new ones. Page: {browser.DescribePage()}");
            }
            return result;
        }

        public async Task<Dictionary<string, DiscoveredReel>> ResolveAsync(IReadOnlyCollection<string> shortCodes, CancellationToken ct)
        {
            if (shortCodes.Count == 0) return new();
            using var browser = await browsers.OpenAsync("MemeScraper-instagram-resolve", ct);
            return await ResolveInBrowserAsync(browser, shortCodes.Take(MaxReelPagesPerRun * 2).ToList(), ct);
        }

        public async Task<DiscoveredProfile> FetchProfileAsync(string username, CancellationToken ct)
        {
            using var browser = await browsers.OpenAsync("MemeScraper-instagram-profile", ct);
            browser.Navigate($"{Origin}/{Uri.EscapeDataString(username)}/reels/");
            var page = await CollectAsync(browser, "\"username\"", ct);
            var profile = InstagramWebParser.ExtractProfile(page.Scripts, username, page.OgDescription);
            if (profile == null)
            {
                throw new ReelProviderException(Name, "profile page has no account data. Page: " + browser.DescribePage());
            }
            return profile;
        }

        private async Task<List<DiscoveredReel>> ListProfileReelsAsync(ScraperBrowser browser, string username, CancellationToken ct)
        {
            browser.Navigate($"{Origin}/{Uri.EscapeDataString(username)}/reels/");
            var page = await CollectAsync(browser, "\"code\"", ct);
            var reels = InstagramWebParser.ExtractProfileReels(page.Scripts, page.Anchors, username);
            if (reels.Count == 0)
            {
                string described = browser.DescribePage();
                // A profile with zero reels renders normally with no clips; a block/login wall doesn't render the profile at all.
                bool profileRendered = described.Contains("@" + username, StringComparison.OrdinalIgnoreCase)
                                       || described.Contains(username, StringComparison.OrdinalIgnoreCase);
                if (!profileRendered || described.Contains("Page Not Found", StringComparison.OrdinalIgnoreCase)
                    || browser.CurrentUrl.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReelProviderException(Name, "profile reels page did not render (login wall, rate limit or missing profile). Page: " + described);
                }
            }
            return reels;
        }

        private async Task<Dictionary<string, DiscoveredReel>> ResolveInBrowserAsync(ScraperBrowser browser, IReadOnlyList<string> shortCodes, CancellationToken ct)
        {
            var resolved = new Dictionary<string, DiscoveredReel>();
            int consecutiveMisses = 0;
            foreach (var code in shortCodes)
            {
                ct.ThrowIfCancellationRequested();
                if (!ScrapeJson.IsShortCode(code)) continue;
                browser.Navigate($"{Origin}/reel/{code}/");
                var page = await CollectAsync(browser, "video_versions", ct);
                var reel = InstagramWebParser.ExtractReelFromPostPage(page.Scripts, code, page.OgVideo);
                if (reel != null)
                {
                    resolved[code] = reel;
                    consecutiveMisses = 0;
                }
                else if (++consecutiveMisses >= 3)
                {
                    // Three blank reel pages in a row is a block, not three odd reels; stop burning requests.
                    log($"instagram: 3 reel pages in a row had no video data, stopping early. Page: {browser.DescribePage()}");
                    break;
                }
                await Task.Delay(jitter.Next(1500, 3500), ct);
            }
            return resolved;
        }

        private sealed record CollectedPage(List<string> Scripts, List<string> Anchors, string OgVideo, string OgDescription);

        private static async Task<CollectedPage> CollectAsync(ScraperBrowser browser, string needle, CancellationToken ct)
        {
            CollectedPage? last = null;
            // Embedded JSON lands with the document, but hydration can add more; take the first read
            // that contains the needle, retrying briefly in case the page was still arriving.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (browser.Execute(CollectScript, needle) is IDictionary dict)
                    {
                        last = new CollectedPage(
                            Strings(dict["scripts"]),
                            Strings(dict["anchors"]),
                            dict["ogVideo"] as string ?? "",
                            dict["ogDescription"] as string ?? "");
                        if (last.Scripts.Count > 0) return last;
                    }
                }
                catch (OpenQA.Selenium.WebDriverException) { }
                await Task.Delay(1000, ct);
            }
            return last ?? new CollectedPage(new(), new(), "", "");
        }

        private static List<string> Strings(object? value) =>
            value is IEnumerable items and not string ? items.OfType<string>().ToList() : new List<string>();
    }
}
