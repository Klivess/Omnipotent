using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.MemeScraper.Analytics;
using Omnipotent.Services.OmniTumblr.Engine;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text.RegularExpressions;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Routes behind the MemeScraper website pages (overview, sources, library, health): date-windowed
    /// analytics, the reel library with previews, scrape history, scheduler settings and source edits.
    /// Every GET here reads MemeScraper's in-memory stores, which the KliveAPI response cache cannot
    /// version, so each one opts out of caching rather than risk serving a stale library.
    /// </summary>
    public partial class MemeScraper
    {
        public const string PausedSetting = "ScrapeInstagramAccounts";
        public const string IntervalSetting = "MemeScraperScrapeIntervalHours";
        public const string MaxPagesSetting = "MemeScraperMaxPagesPerScrape";
        private const long MaxVideoBytes = 512L * 1024 * 1024;
        private static readonly Regex SafeFileId = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.Compiled);

        public sealed class ScraperSettings
        {
            /// <summary>True when the ScrapeInstagramAccounts setting has switched scheduled scrapes off.</summary>
            public bool Paused;
            public int IntervalHours = 24;
            public int MaxPagesPerScrape = 40;
            public DateTime? ReadUtc;
        }

        /// <summary>A background refresh of one source's profile (followers, name, picture, bio).</summary>
        public sealed class ProfileRefreshRun
        {
            public string Username = "";
            public string AccountID = "";
            public string RequestedBy = "";
            public string State = "queued"; // queued, running, complete, failed
            public DateTime QueuedUtc;
            public DateTime? FinishedUtc;
            public string? Error;
            public int? FollowersBefore;
            public int? FollowersAfter;
        }

        public sealed class DiskUsage
        {
            public long Bytes;
            public int Files;
            /// <summary>Reels whose video file is gone.</summary>
            public int MissingVideos;
            /// <summary>Video files no reel points at.</summary>
            public int OrphanFiles;
            public long ThumbnailBytes;
            public DateTime MeasuredUtc;
            public long DurationMs;
        }

        private volatile ScraperSettings settingsCache = new();
        private volatile ProfileRefreshRun? lastProfileRefresh;
        private volatile DiskUsage? diskUsage;
        private int diskScanRunning;
        private readonly SemaphoreSlim thumbnailSlots = new(3, 3);
        private readonly ConcurrentDictionary<string, DateTime> thumbnailFailures = new();
        private OmniTumblrMediaTools? mediaTools;

        private static string? Param(UserRequest request, string key) => request.userParameters?[key];

        private static Task Json(UserRequest request, object body, HttpStatusCode code = HttpStatusCode.OK) =>
            request.ReturnResponse(JsonConvert.SerializeObject(body), code: code);

        private static Task Error(UserRequest request, string message, HttpStatusCode code) =>
            Json(request, new { error = message }, code);

        private async Task LoadScrapeLogAsync()
        {
            int unreadable = await scrapeLog.LoadAsync();
            if (unreadable > 0) _ = ServiceLogError($"MemeScraper: skipped {unreadable} unreadable line(s) in the scrape history.");
        }

        /// <summary>Reads the three scheduler settings (creating them with defaults) and caches them for health.</summary>
        private async Task<ScraperSettings> RefreshSettingsAsync()
        {
            var settings = new ScraperSettings
            {
                Paused = !await GetBoolOmniSetting(PausedSetting, true),
                IntervalHours = Math.Clamp(await GetIntOmniSetting(IntervalSetting, 24), 1, 24 * 14),
                MaxPagesPerScrape = Math.Clamp(await GetIntOmniSetting(MaxPagesSetting, 40), 1, 500),
                ReadUtc = DateTime.UtcNow,
            };
            settingsCache = settings;
            scrapeIntervalHoursCache = settings.IntervalHours;
            return settings;
        }

        // ───────────────────────────── Analytics ─────────────────────────────

        private async Task SummaryRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live summary");
            var summary = MemeScraperInsights.BuildSummary(SourceManager.InstagramSources, mediaManager.allScrapedReels, Param(request, "tz"), DateTime.UtcNow);
            await Json(request, summary);
        }

        private async Task DashboardRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live analytics");
            var query = InsightsQuery.Parse(key => Param(request, key));
            var view = MemeScraperInsights.BuildDashboard(SourceManager.InstagramSources, mediaManager.allScrapedReels, scrapeLog.Snapshot(),
                query, DateTime.UtcNow, settingsCache.IntervalHours);
            await Json(request, view);
        }

        private async Task ScrapeHistoryRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live scrape history");
            var query = ScrapeQuery.Parse(key => Param(request, key));
            await Json(request, MemeScraperInsights.QueryScrapes(SourceManager.InstagramSources, scrapeLog.Snapshot(), query, DateTime.UtcNow));
        }

        // ───────────────────────────── Library ─────────────────────────────

        private async Task ReelsRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live library");
            var query = ReelQuery.Parse(key => Param(request, key));
            if (string.Equals(Param(request, "format"), "csv", StringComparison.OrdinalIgnoreCase))
            {
                string csv = MemeScraperInsights.ReelsCsv(SourceManager.InstagramSources, mediaManager.allScrapedReels, query, DateTime.UtcNow);
                var headers = new NameValueCollection { { "Content-Disposition", $"attachment; filename=\"memescraper-reels-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv\"" } };
                await request.ReturnResponse(csv, "text/csv; charset=utf-8", headers);
                return;
            }
            var page = MemeScraperInsights.QueryReels(SourceManager.InstagramSources, mediaManager.allScrapedReels, query, DateTime.UtcNow, VideoFileInfo);
            await Json(request, page);
        }

        private async Task ReelRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live reel");
            var reel = mediaManager.FindReel(Param(request, "postId"));
            if (reel == null)
            {
                await Error(request, "No reel with that post id.", HttpStatusCode.NotFound);
                return;
            }
            var row = MemeScraperInsights.ToRow(reel, SourceManager.InstagramSources);
            var (exists, bytes) = VideoFileInfo(reel);
            row.VideoExists = exists;
            row.Bytes = exists ? bytes : null;
            // Where it ranks within its own source, by views: "top 5% of @x's reels".
            var siblings = mediaManager.allScrapedReels
                .Where(r => string.Equals(r.OwnerUsername, reel.OwnerUsername, StringComparison.OrdinalIgnoreCase))
                .Select(r => (long)r.ViewCount).ToList();
            int better = siblings.Count(v => v > reel.ViewCount);
            await Json(request, new
            {
                Reel = row,
                OwnerReels = siblings.Count,
                OwnerRank = better + 1,
                OwnerMedianViews = MemeScraperInsights.Median(siblings.Select(v => (double)v)),
                Thumbnail = SafeFileId.IsMatch(reel.PostID ?? "") && File.Exists(ThumbnailPath(reel.PostID!)),
            });
        }

        private (bool Exists, long Bytes) VideoFileInfo(InstagramScrapeUtilities.InstagramReel reel)
        {
            string? path = VideoPath(reel);
            if (path == null) return (false, 0);
            var info = new FileInfo(path);
            return info.Exists ? (true, info.Length) : (false, 0);
        }

        private static string? VideoPath(InstagramScrapeUtilities.InstagramReel reel) =>
            string.IsNullOrWhiteSpace(reel.InstagramReelVideoFilePath) ? null : reel.GetInstagramReelVideoFilePath();

        private static string ThumbnailPath(string postId) =>
            Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperThumbnailsDirectory), postId + ".jpg");

        /// <summary>
        /// A JPEG frame from about a second in, made with FFmpeg on first request and kept on disk.
        /// Bounded to three concurrent FFmpeg runs: a library page asks for dozens at once.
        /// </summary>
        private async Task ReelThumbRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper reel thumbnail");
            var reel = mediaManager.FindReel(Param(request, "postId"));
            if (reel == null || !SafeFileId.IsMatch(reel.PostID ?? ""))
            {
                await Error(request, "No reel with that post id.", HttpStatusCode.NotFound);
                return;
            }
            string postId = reel.PostID!;
            string thumb = ThumbnailPath(postId);
            if (!File.Exists(thumb))
            {
                string? video = VideoPath(reel);
                if (video == null || !File.Exists(video))
                {
                    await Error(request, "The reel's video file is missing.", HttpStatusCode.NotFound);
                    return;
                }
                if (thumbnailFailures.TryGetValue(postId, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromHours(1))
                {
                    await Error(request, "A preview could not be made (is FFmpeg installed?).", HttpStatusCode.NotFound);
                    return;
                }
                await thumbnailSlots.WaitAsync();
                try
                {
                    if (!File.Exists(thumb))
                    {
                        mediaTools ??= new OmniTumblrMediaTools(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory));
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        // A nominal 10 s length seeks ~1 s in, past the black first frame most reels open on;
                        // a clip shorter than that falls back to its first frame.
                        bool made = await mediaTools.CreateThumbnailAsync(video, thumb, 10, timeout.Token)
                                    || await mediaTools.CreateThumbnailAsync(video, thumb, null, timeout.Token);
                        if (!made)
                        {
                            thumbnailFailures[postId] = DateTime.UtcNow;
                            await Error(request, "A preview could not be made (is FFmpeg installed?).", HttpStatusCode.NotFound);
                            return;
                        }
                    }
                }
                finally
                {
                    thumbnailSlots.Release();
                }
            }
            await request.ReturnBinaryResponse(await File.ReadAllBytesAsync(thumb), "image/jpeg");
        }

        /// <summary>Streams the downloaded video (the website fetches it as a blob for the preview player).</summary>
        private async Task ReelVideoRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper reel video");
            var reel = mediaManager.FindReel(Param(request, "postId"));
            string? path = reel == null ? null : VideoPath(reel);
            if (path == null || !File.Exists(path))
            {
                await Error(request, reel == null ? "No reel with that post id." : "The reel's video file is missing.", HttpStatusCode.NotFound);
                return;
            }
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > MaxVideoBytes)
            {
                await Error(request, "The video is too large to preview.", HttpStatusCode.RequestEntityTooLarge);
                return;
            }
            string contentType = OmniTumblrMediaTools.MimeFor(path);
            var headers = new NameValueCollection { { "Content-Disposition", $"inline; filename=\"{Path.GetFileName(path)}\"" }, { "X-Content-Type-Options", "nosniff" } };
            await using var destination = request.PrepareStreamResponse(contentType, source.Length, HttpStatusCode.OK, headers);
            await source.CopyToAsync(destination);
        }

        // ───────────────────────────── Settings ─────────────────────────────

        private async Task SettingsRoute(UserRequest request)
        {
            CacheDeps.MarkUncacheable("memescraper live settings");
            var settings = await RefreshSettingsAsync();
            await Json(request, new { settings.Paused, settings.IntervalHours, settings.MaxPagesPerScrape, OnServer = OmniPaths.CheckIfOnServer() });
        }

        /// <summary>POST { paused?, intervalHours?, maxPagesPerScrape? }: any subset.</summary>
        private async Task SaveSettingsRoute(UserRequest request)
        {
            if (!TryParseBody(request, out var body, out string? problem))
            {
                await Error(request, problem!, HttpStatusCode.BadRequest);
                return;
            }
            bool? paused = body.Value<bool?>("paused");
            int? interval = body.Value<int?>("intervalHours");
            int? pages = body.Value<int?>("maxPagesPerScrape");
            if (interval is < 1 or > 24 * 14)
            {
                await Error(request, "The scrape interval must be between 1 and 336 hours.", HttpStatusCode.BadRequest);
                return;
            }
            if (pages is < 1 or > 500)
            {
                await Error(request, "Pages per scrape must be between 1 and 500.", HttpStatusCode.BadRequest);
                return;
            }
            var manager = await GetOmniGlobalSettingsManager();
            if (paused.HasValue) await manager.SetBoolOmniSetting(PausedSetting, !paused.Value, serviceID, name);
            if (interval.HasValue) await manager.SetIntOmniSetting(IntervalSetting, interval.Value, serviceID, name);
            if (pages.HasValue) await manager.SetIntOmniSetting(MaxPagesSetting, pages.Value, serviceID, name);
            var settings = await RefreshSettingsAsync();
            if (settings.Paused) schedulerState = "paused by the ScrapeInstagramAccounts setting";
            WakeScheduler();
            await ServiceLog($"{request.user?.Name ?? "someone"} changed MemeScraper settings: paused={settings.Paused}, every {settings.IntervalHours}h, {settings.MaxPagesPerScrape} pages per scrape.");
            await Json(request, new { settings.Paused, settings.IntervalHours, settings.MaxPagesPerScrape, OnServer = OmniPaths.CheckIfOnServer() });
        }

        // ───────────────────────────── Sources ─────────────────────────────

        /// <summary>POST { accountId, downloadReels?, downloadPosts?, niches?: string[], scrapeSoon? }.</summary>
        private async Task UpdateInstagramSourceRoute(UserRequest request)
        {
            if (!TryParseBody(request, out var body, out string? problem))
            {
                await Error(request, problem!, HttpStatusCode.BadRequest);
                return;
            }
            var source = SourceManager.GetInstagramSourceByID(body.Value<string>("accountId") ?? "");
            if (source == null)
            {
                await Error(request, "No source with that account id.", HttpStatusCode.NotFound);
                return;
            }
            bool? downloadReels = body.Value<bool?>("downloadReels");
            bool? downloadPosts = body.Value<bool?>("downloadPosts");
            List<MemeScraperSources.Niche>? niches = null;
            if (body["niches"] is JArray list)
            {
                try
                {
                    niches = await ResolveNichesAsync(list.Select(t => t.ToString()));
                }
                catch (ArgumentException ex)
                {
                    await Error(request, ex.Message, HttpStatusCode.BadRequest);
                    return;
                }
            }
            bool scrapeSoon = body.Value<bool?>("scrapeSoon") ?? false;
            bool resumed;
            lock (source)
            {
                resumed = downloadReels == true && !source.DownloadReels;
                if (downloadReels.HasValue) source.DownloadReels = downloadReels.Value;
                if (downloadPosts.HasValue) source.DownloadPosts = downloadPosts.Value;
                if (niches != null) source.Niches = niches;
                // A source switched back on is due now, not whenever its old schedule said.
                if (resumed || scrapeSoon) source.NextScrapeDueUtc = DateTime.UtcNow.AddMinutes(1);
                source.LastUpdated = DateTime.Now;
            }
            await SourceManager.SaveInstagramSource(source);
            if (resumed || scrapeSoon) WakeScheduler();
            await ServiceLog($"{request.user?.Name ?? "someone"} updated @{source.Username}: reels {(source.DownloadReels ? "on" : "off")}, niches [{string.Join(", ", (source.Niches ?? new()).Select(n => n.NicheTagName))}].");
            await Json(request, SourceSnapshot(source));
        }

        /// <summary>POST { accountId, deleteReels }: removes a source, optionally with every reel it produced.</summary>
        private async Task RemoveInstagramSourceRoute(UserRequest request)
        {
            if (!TryParseBody(request, out var body, out string? problem))
            {
                await Error(request, problem!, HttpStatusCode.BadRequest);
                return;
            }
            var source = SourceManager.GetInstagramSourceByID(body.Value<string>("accountId") ?? "");
            if (source == null)
            {
                await Error(request, "No source with that account id.", HttpStatusCode.NotFound);
                return;
            }
            bool deleteReels = body.Value<bool?>("deleteReels") ?? false;
            int reels = mediaManager.allScrapedReels.Count(r => string.Equals(r.OwnerUsername, source.Username, StringComparison.OrdinalIgnoreCase));
            await SourceManager.DeleteInstagramSource(source, deleteReels);
            await ServiceLog($"{request.user?.Name ?? "someone"} removed source @{source.Username}{(deleteReels ? $" and its {reels} reel(s)" : $", keeping its {reels} reel(s)")}.");
            await Json(request, new { removed = source.Username, reelsDeleted = deleteReels ? reels : 0 });
        }

        /// <summary>
        /// Re-reads a source's profile (followers, name, picture, bio, hashtags), which is otherwise only
        /// fetched once when the source is added. Runs in the background behind any scrape: one Chrome at a time.
        /// </summary>
        private async Task RefreshSourceProfileRoute(UserRequest request)
        {
            string id = Param(request, "sourceAccountID") ?? "";
            if (string.IsNullOrEmpty(id) && TryParseBody(request, out var body, out _)) id = body.Value<string>("accountId") ?? "";
            var source = SourceManager.GetInstagramSourceByID(id);
            if (source == null)
            {
                await Error(request, "No source with that account id.", HttpStatusCode.NotFound);
                return;
            }
            var running = lastProfileRefresh;
            if (running != null && running.State is "queued" or "running")
            {
                await Json(request, new { error = $"A profile refresh of @{running.Username} is already in progress.", refresh = running }, HttpStatusCode.Conflict);
                return;
            }
            var run = new ProfileRefreshRun
            {
                Username = source.Username,
                AccountID = source.AccountID,
                RequestedBy = request.user?.Name ?? "unknown",
                QueuedUtc = DateTime.UtcNow,
                FollowersBefore = source.Followers,
            };
            lastProfileRefresh = run;
            _ = Task.Run(() => RunProfileRefreshAsync(source, run));
            await Json(request, run, HttpStatusCode.Accepted);
        }

        private async Task RunProfileRefreshAsync(MemeScraperSources.InstagramSource source, ProfileRefreshRun run)
        {
            await scrapeLock.WaitAsync();
            try
            {
                run.State = "running";
                currentScrape = $"profile refresh of @{source.Username} since {DateTime.UtcNow:u}";
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                var profile = await instagramScrapeUtilities.FetchProfileAsync(source.Username, timeout.Token);
                lock (source)
                {
                    if (profile.Followers > 0) source.Followers = profile.Followers;
                    if (!string.IsNullOrWhiteSpace(profile.FullName)) source.FullName = profile.FullName;
                    if (!string.IsNullOrWhiteSpace(profile.ProfilePictureUrl)) source.ProfilePictureUrl = profile.ProfilePictureUrl;
                    if (!string.IsNullOrWhiteSpace(profile.Bio)) source.Bio = profile.Bio;
                    if (profile.AverageLikes > 0) source.AverageLikes = profile.AverageLikes;
                    if (profile.AverageComments > 0) source.AverageComments = profile.AverageComments;
                    if (profile.Hashtags.Count > 0)
                    {
                        source.AccountTopHashtags = profile.Hashtags.Select(h => new MemeScraperSources.InstagramSource.AccountTopHashtag
                        {
                            Hashtag = h.Name,
                            Count = h.Count,
                            InflactHashtagUrl = h.Url,
                        }).ToList();
                    }
                    source.LastUpdated = DateTime.Now;
                }
                if (SourceManager.IsRegistered(source)) await SourceManager.SaveInstagramSource(source);
                run.FollowersAfter = source.Followers;
                run.State = "complete";
                await ServiceLog($"Refreshed @{source.Username}'s profile via {profile.Provider}: {run.FollowersBefore} → {run.FollowersAfter} followers.", false);
            }
            catch (Exception ex)
            {
                run.State = "failed";
                run.Error = ex is OperationCanceledException ? "timed out after 6 min" : ex.Message;
                await ServiceLogError(ex, $"Couldn't refresh @{source.Username}'s profile.");
            }
            finally
            {
                run.FinishedUtc = DateTime.UtcNow;
                currentScrape = null;
                scrapeLock.Release();
            }
        }

        private async Task<List<MemeScraperSources.Niche>> ResolveNichesAsync(IEnumerable<string> names)
        {
            var result = new List<MemeScraperSources.Niche>();
            foreach (var name in names.Select(n => n?.Trim() ?? "").Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length > 60 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException($"“{name}” is not a usable niche name (60 characters at most, no path characters).");
                var existing = SourceManager.AllNiches.FirstOrDefault(n => string.Equals(n.NicheTagName, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    result.Add(existing);
                    continue;
                }
                var created = new MemeScraperSources.Niche { NicheTagName = name, CreatedAt = DateTime.Now, LastUpdated = DateTime.Now };
                await SourceManager.SaveNiche(created);
                SourceManager.AllNiches.Add(created);
                result.Add(created);
            }
            return result;
        }

        private static object SourceSnapshot(MemeScraperSources.InstagramSource source)
        {
            lock (source)
            {
                return new
                {
                    source.Username,
                    source.AccountID,
                    source.DownloadReels,
                    source.DownloadPosts,
                    Niches = (source.Niches ?? new()).Select(n => n.NicheTagName).ToList(),
                    source.NextScrapeDueUtc,
                };
            }
        }

        private static bool TryParseBody(UserRequest request, out JObject body, out string? problem)
        {
            body = new JObject();
            problem = null;
            string content = request.userMessageContent ?? "";
            if (string.IsNullOrWhiteSpace(content)) return true;
            try
            {
                if (JToken.Parse(content) is JObject parsed)
                {
                    body = parsed;
                    return true;
                }
                problem = "The request body must be a JSON object.";
            }
            catch (JsonException)
            {
                problem = "The request body is not valid JSON.";
            }
            return false;
        }

        // ───────────────────────────── Disk ─────────────────────────────

        /// <summary>The last measurement (at most 10 min old); a stale or missing one is re-measured in the background.</summary>
        private DiskUsage? GetDiskUsage()
        {
            var current = diskUsage;
            if (mediaManager != null
                && (current == null || DateTime.UtcNow - current.MeasuredUtc > TimeSpan.FromMinutes(10))
                && Interlocked.CompareExchange(ref diskScanRunning, 1, 0) == 0)
            {
                _ = Task.Run(() =>
                {
                    try { diskUsage = MeasureDisk(); }
                    catch (Exception ex) { _ = ServiceLogError(ex, "MemeScraper couldn't measure its disk usage."); }
                    finally { Interlocked.Exchange(ref diskScanRunning, 0); }
                });
            }
            return current;
        }

        private DiskUsage MeasureDisk()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var usage = new DiskUsage();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string videos = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsVideoDirectory);
            if (Directory.Exists(videos))
            {
                foreach (var file in new DirectoryInfo(videos).EnumerateFiles())
                {
                    if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    usage.Files++;
                    usage.Bytes += file.Length;
                    names.Add(file.Name);
                }
            }
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reel in mediaManager.allScrapedReels)
            {
                string name = Path.GetFileName(reel.InstagramReelVideoFilePath ?? "");
                if (name.Length > 0 && names.Contains(name)) referenced.Add(name);
                else usage.MissingVideos++;
            }
            usage.OrphanFiles = names.Count - referenced.Count;
            string thumbs = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperThumbnailsDirectory);
            if (Directory.Exists(thumbs)) usage.ThumbnailBytes = new DirectoryInfo(thumbs).EnumerateFiles().Sum(f => f.Length);
            usage.MeasuredUtc = DateTime.UtcNow;
            usage.DurationMs = clock.ElapsedMilliseconds;
            return usage;
        }
    }
}
