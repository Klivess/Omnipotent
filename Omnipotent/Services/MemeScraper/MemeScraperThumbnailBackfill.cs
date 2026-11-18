using Omnipotent.Data_Handling;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Makes the thumbnails of reels that have none, slowly, in the background.
    ///
    /// Why: on 13 Nov 2026 about 1,130 of 20,234 reels had a thumbnail (31.7 MB). New downloads get one at once, but
    /// anything older was made with FFmpeg on first view, and the old library asked for 48 at a time: /reelThumb p95
    /// 38 s, max 68 s. The console now only asks for what is on screen, and this fills in the rest so browsing the
    /// library is instant.
    ///
    /// Gentle on a 4-core server: one FFmpeg run at a time (the same slot new downloads use), a pause between runs,
    /// nothing while a scrape runs, newest reels first (the ones most likely to be looked at). The
    /// <see cref="ThumbnailBackfillSetting"/> setting switches it off.
    /// </summary>
    public partial class MemeScraper
    {
        public const string ThumbnailBackfillSetting = "MemeScraperThumbnailBackfill";
        private static readonly TimeSpan BackfillStartDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BackfillGap = TimeSpan.FromSeconds(3);
        private const int BackfillBatch = 200;

        /// <summary>Progress of the backfill, for the health route.</summary>
        public sealed class ThumbnailBackfillState
        {
            /// <summary>waiting, running, paused (a scrape is running), off (setting), done.</summary>
            public string State = "waiting";
            public int Made;
            public int Failed;
            /// <summary>Reels with a video and no thumbnail, as of the last count.</summary>
            public int Remaining;
            public DateTime? LastMadeUtc;
            public DateTime? CountedUtc;
        }

        private readonly ThumbnailBackfillState backfill = new();

        /// <summary>
        /// The reels to make a thumbnail for, newest download first: those with a usable post id, a video path and no
        /// thumbnail file, skipping ones FFmpeg failed on recently.
        /// </summary>
        internal static List<InstagramScrapeUtilities.InstagramReel> ThumbnailBacklog(
            IEnumerable<InstagramScrapeUtilities.InstagramReel> reels, ISet<string> existingThumbnails, Func<string, bool> recentlyFailed)
        {
            return reels
                .Where(r => r != null && !string.IsNullOrEmpty(r.PostID) && SafeFileId.IsMatch(r.PostID)
                            && !string.IsNullOrWhiteSpace(r.InstagramReelVideoFilePath)
                            && !existingThumbnails.Contains(r.PostID + ".jpg")
                            && !recentlyFailed(r.PostID))
                .OrderByDescending(r => r.DateTimeReelDownloaded)
                .ToList();
        }

        private async Task ThumbnailBackfillLoopAsync()
        {
            if (!OmniPaths.CheckIfOnServer())
            {
                backfill.State = "off";
                return; // a development machine's library is not worth the CPU
            }
            await Task.Delay(BackfillStartDelay); // let the start-up storm settle first
            while (true)
            {
                try
                {
                    if (!await GetBoolOmniSetting(ThumbnailBackfillSetting, true))
                    {
                        backfill.State = "off";
                        await Task.Delay(TimeSpan.FromMinutes(30));
                        continue;
                    }
                    var media = mediaManager;
                    if (media == null) { await Task.Delay(TimeSpan.FromMinutes(1)); continue; }

                    string folder = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperThumbnailsDirectory);
                    Directory.CreateDirectory(folder);
                    var existing = new HashSet<string>(new DirectoryInfo(folder).EnumerateFiles("*.jpg").Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    var backlog = ThumbnailBacklog(media.allScrapedReels, existing,
                        id => thumbnailFailures.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromDays(1));
                    backfill.Remaining = backlog.Count;
                    backfill.CountedUtc = DateTime.UtcNow;
                    if (backlog.Count == 0)
                    {
                        backfill.State = "done";
                        await Task.Delay(TimeSpan.FromHours(1));
                        continue;
                    }

                    foreach (var reel in backlog.Take(BackfillBatch))
                    {
                        if (currentScrape != null) { backfill.State = "paused"; break; }
                        backfill.State = "running";
                        string? video = VideoPath(reel);
                        if (video == null || !File.Exists(video))
                        {
                            // No video to take a frame from; leave it out of the next counts for a day.
                            thumbnailFailures[reel.PostID] = DateTime.UtcNow;
                            backfill.Remaining--;
                            continue;
                        }
                        bool made;
                        await backgroundThumbnailSlot.WaitAsync();
                        try { made = await EnsureThumbnailAsync(reel.PostID, video); }
                        finally { backgroundThumbnailSlot.Release(); }
                        backfill.Remaining--;
                        if (made) { backfill.Made++; backfill.LastMadeUtc = DateTime.UtcNow; }
                        else backfill.Failed++;
                        await Task.Delay(BackfillGap);
                    }
                    await Task.Delay(backfill.State == "paused" ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    backfill.State = "waiting";
                    _ = ServiceLogError(ex, "MemeScraper's thumbnail backfill hit an error; it tries again in 10 minutes.", false);
                    await Task.Delay(TimeSpan.FromMinutes(10));
                }
            }
        }
    }
}
