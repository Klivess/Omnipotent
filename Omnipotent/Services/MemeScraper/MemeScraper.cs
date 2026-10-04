using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.MemeScraper.MemeScraper_Labs;
using Omnipotent.Services.MemeScraper.Scraping;
using System.Collections.Concurrent;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;


namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Downloads reels from registered Instagram meme accounts.
    ///
    /// Scheduling: one in-process loop owns the cadence (each source persists <c>NextScrapeDueUtc</c>)
    /// and scrapes run one at a time. This replaced per-source TimeManager task chains, which
    /// multiplied: the startup check looked up "ScrapeAllInstagramPostsFromSource-{user}" while tasks
    /// were created as "ScrapeAllInstagramPostsFromSource{user}", so every restart added another chain
    /// per source, and TimeManager doesn't cancel a replaced task's timer — so N restarts meant N
    /// concurrent headless Chromes per account hammering inflact's quota. Any legacy tasks still on
    /// disk fire once into a no-op and disappear.
    ///
    /// Health: every scrape records what each provider said, failures back off and alert Klives on
    /// Discord, and <c>/memescraper/scraperHealth</c> shows it all, so the scraper can no longer die
    /// silently.
    /// </summary>
    public class MemeScraper : OmniService
    {
        public MemeScraperSources SourceManager;
        public InstagramScrapeUtilities instagramScrapeUtilities;
        public MemeScraperMedia mediaManager;
        public MemeScraperLabs memeScraperLabs;
        private ReelDownloader downloader;

        private readonly SemaphoreSlim scrapeLock = new(1, 1);
        private readonly SemaphoreSlim schedulerWake = new(0, int.MaxValue);
        private readonly ConcurrentDictionary<string, DateTime> lastAlertUtc = new();
        private readonly ConcurrentQueue<ScrapeReport> recentReports = new();
        private volatile string? currentScrape;
        private volatile ProviderCheckRun? lastProviderCheck;
        private int queuedScrapes;
        private DateTime? lastSchedulerTickUtc;
        private string schedulerState = "starting";
        // Completes once sources, niches and reels are loaded; data-backed routes wait on it briefly.
        private readonly TaskCompletionSource dataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Diagnostics.Stopwatch startupClock = new();
        private static readonly TimeSpan DataWaitTimeout = TimeSpan.FromSeconds(20);

        public static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(5);
        public const int FailuresBeforeAlert = 3;
        private const int DownloadParallelism = 3;
        private const string LegacyTaskPrefix = "ScrapeAllInstagramPostsFromSource";

        public MemeScraper() : this(null) { }

        /// <summary>
        /// Program.cs hands over the KliveAPI instance (as it does for Projects) so route registration
        /// never has to find it in the service list while other services are still being added.
        /// </summary>
        public MemeScraper(Omnipotent.Services.KliveAPI.KliveAPI? kliveApi)
        {
            name = "MemeScraper";
            threadAnteriority = ThreadAnteriority.Standard;
            routeApi = kliveApi;
        }

        public sealed class ScrapeReport
        {
            public string Username = "";
            public string Trigger = "";
            public DateTime StartedUtc;
            public DateTime? FinishedUtc;
            public string? ProviderSummary;
            public int ReelsFound;
            public int NewReels;
            public int Downloaded;
            public int DownloadFailures;
            public string? Error;
            public List<string> Notes = new();
        }

        /// <summary>One provider's result in an on-demand provider check.</summary>
        public sealed class ProviderCheck
        {
            public string Provider = "";
            public bool Ok;
            public int Listed;
            public int Reels;
            public int Pages;
            public long DurationMs;
            public string? Error;
            public string? Note;
            public bool? DownloadOk;
            public long DownloadBytes;
            public string? DownloadHost;
            public string? DownloadError;
        }

        /// <summary>
        /// The website's equivalent of MemeScraperLiveTests: runs each provider on its own against one
        /// account and test-downloads a reel, so a breakage names the provider (and the reason).
        /// </summary>
        public sealed class ProviderCheckRun
        {
            public string Username = "";
            public string RequestedBy = "";
            public string State = "queued"; // queued, running, complete
            public DateTime QueuedUtc;
            public DateTime? StartedUtc;
            public DateTime? FinishedUtc;
            public List<ProviderCheck> Results = new();
        }

        protected override async void ServiceMain()
        {
            startupClock.Start();
            // Routes first. Registering one is a dictionary insert, but they used to be created only
            // after every source/niche/reel file had been read one at a time through DataUtil's queue,
            // so /memescraper/* didn't exist for the whole load. Data-backed routes now hold a request
            // until loading finishes (bounded by DataWaitTimeout) instead.
            instagramScrapeUtilities = new InstagramScrapeUtilities(this);
            downloader = new ReelDownloader();
            _ = CreateRoutes();

            try
            {
                schedulerState = "loading data";
                var sources = new MemeScraperSources(this);
                var media = new MemeScraperMedia(this);
                await Task.WhenAll(sources.LoadAllInstagramSources(), sources.LoadNiches(), media.LoadAllScrapedInstagramReels());
                // Published only once loaded: OmniGram/OmniTumblr/OmniTube treat a non-null mediaManager as ready.
                SourceManager = sources;
                mediaManager = media;
                memeScraperLabs = new(this);
                dataReady.TrySetResult();
            }
            catch (Exception ex)
            {
                schedulerState = "failed to start: " + ex.Message;
                dataReady.TrySetException(ex);
                await ServiceLogError(ex, "MemeScraper failed to load its data; scraping is disabled until restart.", true);
                return;
            }

            GetTimeManagerService().TaskDue += TimeManager_TaskDue;
            _ = Task.Run(SchedulerLoopAsync);
            await ServiceLog($"MemeScraper data loaded in {startupClock.ElapsedMilliseconds} ms: {SourceManager.InstagramSources.Count} Instagram sources, {SourceManager.AllNiches.Count} niches, {mediaManager.Count} reels.");
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            // Legacy per-source chains (see class remarks). The scheduler loop owns cadence now, so these
            // are left to lapse; firing one just nudges the loop.
            if (e?.taskName != null && e.taskName.StartsWith(LegacyTaskPrefix, StringComparison.Ordinal))
            {
                WakeScheduler();
            }
        }

        /// <summary>Makes the scheduler re-check due sources now instead of at its next poll.</summary>
        public void WakeScheduler()
        {
            try { schedulerWake.Release(); } catch (SemaphoreFullException) { }
        }

        // ───────────────────────────── Scheduling ─────────────────────────────

        private async Task SchedulerLoopAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(90)); // let Selenium/Discord/settings services come up
            while (true)
            {
                try
                {
                    lastSchedulerTickUtc = DateTime.UtcNow;
                    await RunDueScrapesAsync();
                }
                catch (Exception ex)
                {
                    schedulerState = "tick failed: " + ex.Message;
                    await ServiceLogError(ex, "MemeScraper scheduler tick failed.");
                }
                await schedulerWake.WaitAsync(SchedulerPollInterval);
                while (schedulerWake.CurrentCount > 0) schedulerWake.Wait(0); // coalesce bursts of wakes
            }
        }

        private async Task RunDueScrapesAsync()
        {
            if (!OmniPaths.CheckIfOnServer())
            {
                schedulerState = "idle (scheduled scrapes only run on the server; use /memescraper/scrapeNow to scrape here)";
                return;
            }
            if (!await GetBoolOmniSetting("ScrapeInstagramAccounts", true))
            {
                schedulerState = "paused by the ScrapeInstagramAccounts setting";
                return;
            }

            var interval = await GetScrapeIntervalAsync();
            var now = DateTime.UtcNow;
            var rng = new Random();
            foreach (var source in SourceManager.InstagramSources.Where(s => s.NextScrapeDueUtc == null))
            {
                // First run under this scheduler (or a source from before it existed): spread the backlog
                // over the next couple of hours rather than scraping every account at once.
                lock (source) source.NextScrapeDueUtc = now.AddMinutes(rng.Next(1, 120));
                if (SourceManager.IsRegistered(source)) await SourceManager.SaveInstagramSource(source);
            }

            var due = SourceManager.InstagramSources
                .Where(s => s.DownloadReels && s.NextScrapeDueUtc <= now)
                .OrderBy(s => s.NextScrapeDueUtc)
                .ToList();
            schedulerState = due.Count == 0 ? "idle" : $"scraping {due.Count} due source(s)";
            foreach (var source in due)
            {
                // Re-check: a manual scrape may have just handled it.
                if (source.NextScrapeDueUtc > DateTime.UtcNow) continue;
                await ScrapeSourceAsync(source, "schedule", CancellationToken.None);
            }
            schedulerState = "idle";
        }

        private int scrapeIntervalHoursCache = 24;

        private async Task<TimeSpan> GetScrapeIntervalAsync()
        {
            int hours = Math.Clamp(await GetIntOmniSetting("MemeScraperScrapeIntervalHours", 24), 1, 24 * 14);
            scrapeIntervalHoursCache = hours;
            return TimeSpan.FromHours(hours);
        }

        // ───────────────────────────── Scraping ─────────────────────────────

        /// <summary>Kept for callers of the old API (KliveAgent scripts, ExecuteServiceMethod).</summary>
        public Task ScrapeInstagramAccount(MemeScraperSources.InstagramSource source) =>
            source == null ? Task.CompletedTask : ScrapeSourceAsync(source, "legacy-call", CancellationToken.None);

        /// <summary>
        /// Discovers, downloads and records new reels for one source, then updates its health and next
        /// due time. Never throws for scrape failures — they land in the report and the source's health.
        /// Scrapes are serialized: one headless Chrome at a time from this service.
        /// </summary>
        public async Task<ScrapeReport> ScrapeSourceAsync(MemeScraperSources.InstagramSource source, string trigger, CancellationToken ct)
        {
            var report = new ScrapeReport { Username = source.Username, Trigger = trigger };
            await scrapeLock.WaitAsync(ct);
            report.StartedUtc = DateTime.UtcNow;
            currentScrape = $"@{source.Username} ({trigger}) since {report.StartedUtc:u}";
            var interval = TimeSpan.FromHours(24);
            try
            {
                interval = await GetScrapeIntervalAsync();
                lock (source) source.LastScrapeAttemptUtc = report.StartedUtc;
                await ServiceLog($"Scraping @{source.Username} for reels ({trigger}).", false);

                int maxPages = Math.Clamp(await GetIntOmniSetting("MemeScraperMaxPagesPerScrape", 40), 1, 500);
                var discovery = await instagramScrapeUtilities.DiscoverReelsAsync(
                    source.Username, r => mediaManager.IsKnown(r.PostID, r.ShortCode), maxPages, ct);
                report.ProviderSummary = discovery.Summary;
                report.ReelsFound = discovery.Reels.Count;

                var fresh = discovery.Reels.Where(r => !mediaManager.IsKnown(r.PostID, r.ShortCode)).ToList();
                report.NewReels = fresh.Count;
                var failed = await DownloadReelsAsync(source, fresh, report, ct);

                if (failed.Count > 0)
                {
                    // Signed CDN URLs expire and mirrors refuse; ask Instagram for fresh URLs and retry once.
                    var codes = failed.Select(f => f.Reel.ShortCode).Where(ScrapeJson.IsShortCode).Distinct().ToList();
                    if (codes.Count > 0)
                    {
                        try
                        {
                            var refreshed = await instagramScrapeUtilities.ResolveFreshVideoUrlsAsync(codes, ct);
                            var retry = new List<DiscoveredReel>();
                            var stillFailed = new List<(DiscoveredReel Reel, string Error)>();
                            foreach (var f in failed)
                            {
                                if (refreshed.TryGetValue(f.Reel.ShortCode, out var update))
                                {
                                    update.MergeFrom(f.Reel); // fresh URLs first, then the stale ones
                                    retry.Add(update);
                                }
                                else stillFailed.Add(f);
                            }
                            report.Notes.Add($"refreshed URLs for {refreshed.Count}/{codes.Count} failed reels");
                            failed = stillFailed.Concat(await DownloadReelsAsync(source, retry, report, ct)).ToList();
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            report.Notes.Add("URL refresh failed: " + ex.Message);
                        }
                    }
                }
                report.DownloadFailures = failed.Count;
                if (failed.Count > 0)
                {
                    report.Notes.Add($"{failed.Count} download(s) failed, e.g. {failed[0].Reel.ShortCode}: {failed[0].Error}");
                }
                if (fresh.Count > 0 && report.Downloaded == 0)
                {
                    // Discovery works but nothing lands (CDN blocking the host, every URL expired): that is a
                    // broken scraper, not a quiet day, so it goes down the failure/alert path.
                    throw new InvalidOperationException($"found {fresh.Count} new reels but none downloaded. {report.Notes.Last()}");
                }

                bool wasAlerting = source.ConsecutiveScrapeFailures >= FailuresBeforeAlert;
                lock (source)
                {
                    source.LastScraped = DateTime.Now;
                    source.LastSuccessfulScrapeUtc = DateTime.UtcNow;
                    source.ConsecutiveScrapeFailures = 0;
                    source.LastScrapeError = failed.Count > 0 ? report.Notes.Last() : null;
                    source.LastScrapeSummary = discovery.Summary;
                    source.LastScrapeReelsFound = report.ReelsFound;
                    source.LastScrapeReelsDownloaded = report.Downloaded;
                    source.LastScrapeDownloadFailures = failed.Count;
                    source.TotalReelsDownloaded += report.Downloaded;
                    // Failed downloads stay unknown, so the next scrape retries them; come back sooner if any.
                    source.NextScrapeDueUtc = DateTime.UtcNow + (failed.Count > 0 ? TimeSpan.FromTicks(Math.Min(interval.Ticks, TimeSpan.FromHours(6).Ticks)) : interval) + Jitter(interval);
                }
                await ServiceLog($"Finished @{source.Username}: {report.Downloaded} new reels downloaded, {report.ReelsFound} seen, {failed.Count} failed. {discovery.Summary}", report.Downloaded > 0 || failed.Count > 0);
                if (wasAlerting)
                {
                    await AlertKlivesAsync("recovered:" + source.AccountID, $"MemeScraper: @{source.Username} is scraping again ({discovery.SucceededVia}). Downloaded {report.Downloaded} new reels.", TimeSpan.Zero);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                report.Error = "cancelled";
                throw;
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                int failures;
                lock (source)
                {
                    failures = ++source.ConsecutiveScrapeFailures;
                    source.LastScrapeError = ex.Message;
                    source.LastScrapeSummary = report.ProviderSummary ?? ex.Message;
                    source.LastScrapeReelsFound = report.ReelsFound;
                    source.LastScrapeReelsDownloaded = report.Downloaded;
                    source.LastScrapeDownloadFailures = report.DownloadFailures;
                    // 1h, 2h, 4h, ... capped at the normal interval.
                    var backoff = TimeSpan.FromHours(Math.Min(Math.Pow(2, failures - 1), interval.TotalHours));
                    source.NextScrapeDueUtc = DateTime.UtcNow + backoff + Jitter(backoff);
                }
                await ServiceLogError(ex, $"Scrape of @{source.Username} failed ({failures} in a row).", true);
                if (failures >= FailuresBeforeAlert)
                {
                    await AlertKlivesAsync("failing:" + source.AccountID,
                        $"MemeScraper: @{source.Username} has failed {failures} scrapes in a row and is downloading nothing. Last error: {Truncate(ex.Message, 900)}",
                        TimeSpan.FromHours(24));
                }
            }
            finally
            {
                try
                {
                    report.FinishedUtc = DateTime.UtcNow;
                    recentReports.Enqueue(report);
                    while (recentReports.Count > 50) recentReports.TryDequeue(out _);
                    currentScrape = null;
                    // A source deleted mid-scrape must stay deleted; saving it would resurrect its file.
                    if (SourceManager.IsRegistered(source)) await SourceManager.SaveInstagramSource(source);
                }
                catch (Exception ex)
                {
                    _ = ServiceLogError(ex, $"Couldn't save health for @{source.Username}.");
                }
                finally
                {
                    scrapeLock.Release();
                }
            }
            return report;
        }

        private async Task<List<(DiscoveredReel Reel, string Error)>> DownloadReelsAsync(
            MemeScraperSources.InstagramSource source, List<DiscoveredReel> reels, ScrapeReport report, CancellationToken ct)
        {
            var failed = new ConcurrentBag<(DiscoveredReel Reel, string Error)>();
            int downloaded = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = ct };
            await Parallel.ForEachAsync(reels, options, async (reel, token) =>
            {
                if (string.IsNullOrEmpty(reel.PostID))
                {
                    failed.Add((reel, "no post id"));
                    return;
                }
                if (mediaManager.IsKnown(reel.PostID, reel.ShortCode)) return;
                if (reel.VideoUrls.Count == 0)
                {
                    failed.Add((reel, "no video URL"));
                    return;
                }

                string extension = PickVideoExtension(reel.VideoUrls);
                string relativeVideoPath = Path.Combine(OmniPaths.GlobalPaths.MemeScraperReelsVideoDirectory, $"ReelMedia{reel.PostID}{extension}");
                string absoluteVideoPath = OmniPaths.GetPath(relativeVideoPath);
                var outcome = await downloader.DownloadAsync(reel.VideoUrls, absoluteVideoPath, token);
                if (!outcome.Success)
                {
                    failed.Add((reel, outcome.Error));
                    return;
                }

                var instagramReel = new InstagramScrapeUtilities.InstagramReel
                {
                    PostID = reel.PostID,
                    ShortCode = reel.ShortCode,
                    OwnerUsername = string.IsNullOrEmpty(reel.OwnerUsername) ? source.Username : reel.OwnerUsername,
                    OwnerID = string.IsNullOrEmpty(reel.OwnerID) ? source.AccountID : reel.OwnerID,
                    ViewCount = ScrapeJson.ClampInt(reel.ViewCount),
                    LikeCount = reel.LikeCount,
                    CommentCount = reel.CommentCount,
                    CreatedAt = reel.CreatedAtUtc,
                    Description = reel.Description,
                    ShortURL = string.IsNullOrEmpty(reel.ShortCode) ? "" : $"https://www.instagram.com/reels/{reel.ShortCode}/",
                    VideoDownloadURL = outcome.Url!,
                    DiscoveredVia = reel.Provider,
                    DateTimeReelDownloaded = DateTime.Now,
                };
                instagramReel.SetInstagramReelInfoFilePath(Path.Combine(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory, $"Reel{reel.PostID}.json"));
                instagramReel.SetInstagramReelVideoFilePath(relativeVideoPath);
                try
                {
                    await mediaManager.SaveInstagramReel(instagramReel);
                    if (mediaManager.TryAddReel(instagramReel)) Interlocked.Increment(ref downloaded);
                }
                catch (Exception ex)
                {
                    try { File.Delete(absoluteVideoPath); } catch { }
                    failed.Add((reel, "saving reel data failed: " + ex.Message));
                }
            });
            report.Downloaded += downloaded;
            return failed.ToList();
        }

        private static string PickVideoExtension(IEnumerable<string> urls)
        {
            foreach (var url in urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (ext is ".mp4" or ".mov" or ".webm" or ".m4v") return ext;
            }
            return ".mp4";
        }

        private static TimeSpan Jitter(TimeSpan basis) =>
            TimeSpan.FromMinutes(Random.Shared.NextDouble() * Math.Min(60, basis.TotalMinutes * 0.1));

        private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "…";

        private async Task AlertKlivesAsync(string key, string message, TimeSpan minInterval)
        {
            if (minInterval > TimeSpan.Zero && lastAlertUtc.TryGetValue(key, out var at) && DateTime.UtcNow - at < minInterval) return;
            lastAlertUtc[key] = DateTime.UtcNow;
            try
            {
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", message);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "MemeScraper couldn't send a Discord alert: " + message, false);
            }
        }

        // ───────────────────────────── Routes ─────────────────────────────

        private Omnipotent.Services.KliveAPI.KliveAPI? routeApi;

        /// <summary>
        /// Registers against the typed KliveAPI (bounded wait, failures named in the log) instead of the
        /// reflection-based CreateAPIRoute, which can fail silently and leave a partial route table.
        /// </summary>
        private async Task RegisterRouteAsync(string path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, HttpMethod method, KMPermissions permission)
        {
            if (routeApi == null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while ((routeApi = FindKliveApi()) == null)
                {
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("KliveAPI did not appear within 90s; MemeScraper routes are unavailable.");
                    await Task.Delay(100);
                }
            }
            await routeApi.CreateRoute(path, handler, method, permission);
        }

        /// <summary>
        /// Fallback lookup when no instance was injected. The service list is a plain List that
        /// Program.cs is still appending to during startup, so enumeration can throw; treat that as
        /// "not found yet" and let the caller retry.
        /// </summary>
        private Omnipotent.Services.KliveAPI.KliveAPI? FindKliveApi()
        {
            try { return GetActiveServices().ToArray().OfType<Omnipotent.Services.KliveAPI.KliveAPI>().FirstOrDefault(); }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private async Task CreateRoutes()
        {
            var routes = new (string Path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> Handler, HttpMethod Method, KMPermissions Permission)[]
            {
                ("/memescraper/addInstagramSource", AddInstagramSourceRoute, HttpMethod.Post, KMPermissions.Manager),
                ("/memescraper/getAllInstagramSources", async request =>
                {
                    CacheDeps.MarkUncacheable("memescraper live source health");
                    await request.ReturnResponse(JsonConvert.SerializeObject(SourceManager.InstagramSources), code: HttpStatusCode.OK);
                }, HttpMethod.Get, KMPermissions.Guest),
                ("/memescraper/getAllSavedNiches", async request =>
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(SourceManager.AllNiches), code: HttpStatusCode.OK);
                }, HttpMethod.Get, KMPermissions.Guest),
                ("/memescraper/memeScraperAnalytics", async request =>
                {
                    CacheDeps.MarkUncacheable("memescraper live analytics");
                    var analytics = new MemeScraperLabs.MemeScraperAnalytics(SourceManager.InstagramSources, mediaManager.allScrapedReels);
                    await request.ReturnResponse(JsonConvert.SerializeObject(analytics), code: HttpStatusCode.OK);
                }, HttpMethod.Get, KMPermissions.Guest),
                ("/memescraper/deleteInstagramSource", async request =>
                {
                    string id = request.userParameters["sourceAccountID"] ?? "";
                    bool deleteAssociatedMemes = request.userParameters["deleteAssociatedMemes"] == "true";
                    var source = SourceManager.GetInstagramSourceByID(id);
                    if (source == null)
                    {
                        await request.ReturnResponse(JsonConvert.SerializeObject(new { error = $"No source with account id '{id}'." }), code: HttpStatusCode.NotFound);
                        return;
                    }
                    await SourceManager.DeleteInstagramSource(source, deleteAssociatedMemes);
                    await request.ReturnResponse("OK");
                }, HttpMethod.Get, KMPermissions.Manager),
                ("/memescraper/scraperHealth", async request =>
                {
                    CacheDeps.MarkUncacheable("memescraper live health");
                    await request.ReturnResponse(JsonConvert.SerializeObject(BuildHealthSnapshot()), code: HttpStatusCode.OK);
                }, HttpMethod.Get, KMPermissions.Guest),
                ("/memescraper/scrapeNow", ScrapeNowRoute, HttpMethod.Post, KMPermissions.Manager),
                ("/memescraper/diagnoseProviders", DiagnoseProvidersRoute, HttpMethod.Post, KMPermissions.Manager),
            };

            int registered = 0;
            foreach (var (path, handler, method, permission) in routes)
            {
                try
                {
                    // Health must answer during startup (it reports the loading state); the rest need data.
                    await RegisterRouteAsync(path, Guard(path, handler, requiresData: path != "/memescraper/scraperHealth"), method, permission);
                    registered++;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"MemeScraper: failed to register route {path}");
                }
            }
            await ServiceLog($"MemeScraper: {registered}/{routes.Length} HTTP routes registered {startupClock.ElapsedMilliseconds} ms after start.", false);
        }

        /// <summary>Uniform error envelope so one route's bug never takes the request down unanswered.</summary>
        private Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> Guard(string path, Func<Omnipotent.Services.KliveAPI.KliveAPI.UserRequest, Task> handler, bool requiresData = true) => async request =>
        {
            try
            {
                if (requiresData && !dataReady.Task.IsCompleted)
                {
                    // Loading takes seconds; hold the request rather than fail a page that opened mid-startup.
                    await Task.WhenAny(dataReady.Task, Task.Delay(DataWaitTimeout));
                }
                if (requiresData && !dataReady.Task.IsCompletedSuccessfully)
                {
                    string why = dataReady.Task.IsFaulted
                        ? "MemeScraper failed to load its data: " + dataReady.Task.Exception?.GetBaseException().Message
                        : "MemeScraper is still loading its data; try again in a moment.";
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = why }), code: HttpStatusCode.ServiceUnavailable);
                    return;
                }
                await handler(request);
            }
            catch (Exception e)
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                await ServiceLogError(e, $"Error in {path} route.");
            }
        };

        private async Task AddInstagramSourceRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            string content = request.userMessageContent;
            if (string.IsNullOrEmpty(content))
            {
                await request.ReturnResponse("No username provided.", code: HttpStatusCode.BadRequest);
                return;
            }
            dynamic jsonData = JsonConvert.DeserializeObject(content);
            string username = MemeScraperSources.NormalizeUsername((string)jsonData.username);
            if (string.IsNullOrEmpty(username))
            {
                await request.ReturnResponse("No username provided.", code: HttpStatusCode.BadRequest);
                return;
            }
            bool downloadReels = jsonData.downloadReels ?? false;
            bool downloadPosts = jsonData.downloadPosts ?? false;
            List<string> niches = new();
            if (jsonData.niches != null)
            {
                foreach (var item in jsonData.niches)
                {
                    niches.Add(item.ToString());
                }
            }
            List<MemeScraperSources.Niche> nichesList = new();
            foreach (var niche in niches.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                var existing = SourceManager.AllNiches.FirstOrDefault(k => k.NicheTagName == niche);
                if (existing != null)
                {
                    nichesList.Add(existing);
                    continue;
                }
                var created = new MemeScraperSources.Niche
                {
                    NicheTagName = niche,
                    CreatedAt = DateTime.Now,
                    LastUpdated = DateTime.Now
                };
                await SourceManager.SaveNiche(created);
                SourceManager.AllNiches.Add(created);
                nichesList.Add(created);
            }

            string requester = request.user?.Name ?? "someone";
            // Looking the account up drives a browser and can take a few minutes, so answer now and
            // report the outcome to the log and Discord.
            _ = Task.Run(async () =>
            {
                try
                {
                    var source = await SourceManager.ProduceNewInstagramSource(username, downloadReels, downloadPosts, nichesList);
                    await ServiceLog($"{requester} added Instagram source @{source.Username} (account {source.AccountID}).");
                    await AlertKlivesAsync("added:" + source.AccountID, $"{requester} uploaded a new instagram source: '{source.Username}'.", TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Couldn't add Instagram source @{username} for {requester}.", true);
                    await AlertKlivesAsync("addfail:" + username, $"MemeScraper: couldn't add @{username} (requested by {requester}): {Truncate(ex.Message, 900)}", TimeSpan.Zero);
                }
            });
            await request.ReturnResponse("Instagram source being added.", code: HttpStatusCode.OK);
        }

        /// <summary>
        /// POST /memescraper/scrapeNow?sourceAccountID=… (or ?username=…); no parameter scrapes every
        /// reel source. Runs in the background (serialized with scheduled scrapes) and works off-server
        /// too, which makes it the way to verify the scraper after a deploy.
        /// </summary>
        private async Task ScrapeNowRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            string? id = request.userParameters?["sourceAccountID"];
            string? username = MemeScraperSources.NormalizeUsername(request.userParameters?["username"]);
            var all = SourceManager.InstagramSources;
            List<MemeScraperSources.InstagramSource> targets =
                !string.IsNullOrEmpty(id) ? all.Where(s => s.AccountID == id).ToList()
                : !string.IsNullOrEmpty(username) ? all.Where(s => string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase)).ToList()
                : all.Where(s => s.DownloadReels).ToList();
            if (targets.Count == 0)
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(new { error = "No matching source." }), code: HttpStatusCode.NotFound);
                return;
            }
            string trigger = "manual:" + (request.user?.Name ?? "unknown");
            Interlocked.Add(ref queuedScrapes, targets.Count);
            _ = Task.Run(async () =>
            {
                foreach (var source in targets)
                {
                    try { await ScrapeSourceAsync(source, trigger, CancellationToken.None); }
                    catch (Exception ex) { await ServiceLogError(ex, $"Manual scrape of @{source.Username} crashed."); }
                    finally { Interlocked.Decrement(ref queuedScrapes); }
                }
            });
            await request.ReturnResponse(JsonConvert.SerializeObject(new
            {
                queued = targets.Select(s => s.Username).ToList(),
                note = "Scrapes run one at a time; watch /memescraper/scraperHealth for results.",
            }), code: HttpStatusCode.Accepted);
        }

        /// <summary>
        /// POST /memescraper/diagnoseProviders?username=... (defaults to a registered reel source). Starts a
        /// provider check in the background; poll /memescraper/scraperHealth (ProviderCheck) for results.
        /// </summary>
        private async Task DiagnoseProvidersRoute(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            var running = lastProviderCheck;
            if (running != null && running.State != "complete")
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(new { error = "A provider check is already running.", check = running }), code: HttpStatusCode.Conflict);
                return;
            }
            string username = MemeScraperSources.NormalizeUsername(request.userParameters?["username"]);
            if (string.IsNullOrEmpty(username))
            {
                username = SourceManager.InstagramSources
                    .Where(s => s.DownloadReels)
                    .OrderByDescending(s => s.LastSuccessfulScrapeUtc ?? DateTime.MinValue)
                    .Select(s => s.Username)
                    .FirstOrDefault() ?? "";
            }
            if (string.IsNullOrEmpty(username))
            {
                await request.ReturnResponse(JsonConvert.SerializeObject(new { error = "Give a username to test against (no reel sources are registered)." }), code: HttpStatusCode.BadRequest);
                return;
            }
            var run = new ProviderCheckRun { Username = username, RequestedBy = request.user?.Name ?? "unknown", QueuedUtc = DateTime.UtcNow };
            lastProviderCheck = run;
            _ = Task.Run(() => RunProviderCheckAsync(run));
            await request.ReturnResponse(JsonConvert.SerializeObject(run), code: HttpStatusCode.Accepted);
        }

        private async Task RunProviderCheckAsync(ProviderCheckRun run)
        {
            // Same one-Chrome-at-a-time rule as scrapes; a check queued behind a scrape waits its turn.
            await scrapeLock.WaitAsync();
            try
            {
                run.StartedUtc = DateTime.UtcNow;
                run.State = "running";
                currentScrape = $"provider check on @{run.Username} since {run.StartedUtc:u}";
                var inflact = instagramScrapeUtilities.Inflact!;
                var instagram = instagramScrapeUtilities.Instagram!;
                var checks = new (string Name, Func<CancellationToken, Task<ReelDiscoveryResult>> Discover)[]
                {
                    (inflact.Name, ct => inflact.DiscoverAsync(new ReelDiscoveryRequest { Username = run.Username, MaxPages = 2, StopWhenCaughtUp = false }, ct)),
                    (instagram.Name, ct => instagram.DiscoverAsync(new ReelDiscoveryRequest { Username = run.Username, MaxDetailPages = 1 }, ct)),
                };
                foreach (var (name, discover) in checks)
                {
                    var check = new ProviderCheck { Provider = name };
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    try
                    {
                        var result = await discover(timeout.Token);
                        check.Listed = result.Listed;
                        check.Reels = result.Reels.Count;
                        check.Pages = result.PagesFetched;
                        check.Note = result.TruncatedReason;
                        var sample = result.Reels.FirstOrDefault(r => r.VideoUrls.Count > 0);
                        if (sample != null)
                        {
                            string temp = Path.Combine(Path.GetTempPath(), $"memescraper-check-{Guid.NewGuid():N}.mp4");
                            try
                            {
                                var download = await downloader.DownloadAsync(sample.VideoUrls, temp, timeout.Token);
                                check.DownloadOk = download.Success;
                                check.DownloadBytes = download.Bytes;
                                check.DownloadHost = download.Url != null && Uri.TryCreate(download.Url, UriKind.Absolute, out var u) ? u.Host : null;
                                check.DownloadError = download.Success ? null : download.Error;
                            }
                            finally
                            {
                                try { File.Delete(temp); } catch { }
                            }
                        }
                        // A check exists to prove the whole pipeline, so it needs at least one reel that downloads.
                        check.Ok = check.Reels > 0 && check.DownloadOk == true;
                        if (!check.Ok)
                        {
                            check.Error = check.Reels == 0
                                ? "no reels returned (the account has none, or the provider is silently failing)"
                                : "reels listed but the test download failed";
                        }
                        if (check.Ok) instagramScrapeUtilities.NoteDiagnosticSuccess(name);
                    }
                    catch (Exception ex)
                    {
                        check.Error = ex is OperationCanceledException ? "timed out after 5 min" : ex.Message;
                    }
                    check.DurationMs = stopwatch.ElapsedMilliseconds;
                    // Copy-on-write: the health route may be serializing this list concurrently.
                    run.Results = run.Results.Append(check).ToList();
                }
                await ServiceLog($"Provider check on @{run.Username}: " + string.Join("; ", run.Results.Select(r => $"{r.Provider} {(r.Ok ? "OK" : "FAILED: " + r.Error)}")), true);
            }
            catch (Exception ex)
            {
                run.Results = run.Results.Append(new ProviderCheck { Provider = "check", Error = ex.Message }).ToList();
                await ServiceLogError(ex, "Provider check crashed.");
            }
            finally
            {
                run.FinishedUtc = DateTime.UtcNow;
                run.State = "complete";
                currentScrape = null;
                scrapeLock.Release();
            }
        }

        public object BuildHealthSnapshot()
        {
            // Answers during startup too, so every manager may still be null here.
            var sources = SourceManager?.InstagramSources ?? new List<MemeScraperSources.InstagramSource>();
            return new
            {
                GeneratedUtc = DateTime.UtcNow,
                DataLoaded = dataReady.Task.IsCompletedSuccessfully,
                OnServer = OmniPaths.CheckIfOnServer(),
                SchedulerState = schedulerState,
                LastSchedulerTickUtc = lastSchedulerTickUtc,
                NextScrapeDueUtc = sources.Where(s => s.DownloadReels).Min(s => s.NextScrapeDueUtc),
                CurrentScrape = currentScrape,
                ReelsOnDisk = mediaManager?.Count ?? 0,
                Providers = instagramScrapeUtilities?.GetProviderHealth() ?? new List<InstagramScrapeUtilities.ProviderHealth>(),
                FailingSources = sources.Count(s => s.ConsecutiveScrapeFailures > 0),
                Sources = sources.OrderBy(s => s.NextScrapeDueUtc).Select(s => new
                {
                    s.Username,
                    s.AccountID,
                    s.DownloadReels,
                    s.NextScrapeDueUtc,
                    s.LastScrapeAttemptUtc,
                    s.LastSuccessfulScrapeUtc,
                    s.ConsecutiveScrapeFailures,
                    s.LastScrapeError,
                    s.LastScrapeSummary,
                    s.LastScrapeReelsFound,
                    s.LastScrapeReelsDownloaded,
                    s.LastScrapeDownloadFailures,
                    s.TotalReelsDownloaded,
                }).ToList(),
                RecentScrapes = recentReports.Reverse().ToList(),
                ProviderCheck = lastProviderCheck,
                ScrapeIntervalHours = scrapeIntervalHoursCache,
                ScrapeQueued = Math.Max(0, queuedScrapes),
            };
        }
    }
}
