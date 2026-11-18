using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Security.Cryptography;
using Stamp = Omnipotent.Services.MemeScraper.MemeScraperReelIndex.Stamp;

namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraperMedia
    {
        MemeScraper? parent;
        private readonly object gate = new();
        private List<InstagramScrapeUtilities.InstagramReel> reels = new();
        private readonly HashSet<string> knownPostIds = new();
        private readonly HashSet<string> knownShortCodes = new();
        private readonly Dictionary<string, InstagramScrapeUtilities.InstagramReel> byPostId = new(StringComparer.Ordinal);

        // The file each reel was read from (or saved to), as the directory listing describes it. Keyed by the reel
        // object: the index writes these, and the start-up check reads only files whose stamp no longer matches.
        private readonly Dictionary<InstagramScrapeUtilities.InstagramReel, Stamp> stamps = new(ReferenceEqualityComparer.Instance);
        private readonly MemeScraperReelIndex index;
        private readonly TimeSpan writeDelay;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private int writeScheduled;
        private Task reconciled = Task.CompletedTask;

        /// <summary>How the reels were loaded at start-up (index or every file), with timings.</summary>
        public MemeScraperReelIndex.LoadReport? LastLoad { get; private set; }
        /// <summary>The background check of the index against the reel directory, once it has finished.</summary>
        public MemeScraperReelIndex.ReconcileReport? LastReconcile { get; private set; }
        public MemeScraperReelIndex.WriteReport? LastIndexWrite { get; private set; }
        public string? LastIndexWriteError { get; private set; }
        /// <summary>Completes when the index has been checked against the directory (at once after a full read).</summary>
        public Task Reconciled => reconciled;
        /// <summary>Files read so far / to read, while a full read is running.</summary>
        public (int Done, int Total) LoadProgress => index.Progress;

        /// <summary>Cheap: call <see cref="LoadAllScrapedInstagramReels"/> to populate.</summary>
        public MemeScraperMedia(MemeScraper parent)
            : this(parent, new MemeScraperReelIndex(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory),
                OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelIndexFile)), TimeSpan.FromSeconds(30))
        {
        }

        /// <summary>Test seam: any reel directory and index file, no running service.</summary>
        internal MemeScraperMedia(MemeScraper? parent, MemeScraperReelIndex index, TimeSpan writeDelay)
        {
            this.parent = parent;
            this.index = index;
            this.writeDelay = writeDelay;
        }

        /// <summary>
        /// Snapshot of every scraped reel. A copy, so OmniGram/OmniTumblr/OmniTube can enumerate it
        /// while a scrape is adding reels (the old shared List threw "collection was modified").
        /// Mutate through <see cref="TryAddReel"/> / <see cref="RemoveReels"/>, not this list.
        /// </summary>
        public List<InstagramScrapeUtilities.InstagramReel> allScrapedReels
        {
            get { lock (gate) return new List<InstagramScrapeUtilities.InstagramReel>(reels); }
        }

        public int Count
        {
            get { lock (gate) return reels.Count; }
        }

        public InstagramScrapeUtilities.InstagramReel? FindReel(string? postId)
        {
            if (string.IsNullOrEmpty(postId)) return null;
            lock (gate) return byPostId.TryGetValue(postId, out var reel) ? reel : null;
        }

        public bool IsKnown(string? postId, string? shortCode)
        {
            lock (gate)
            {
                return (!string.IsNullOrEmpty(postId) && knownPostIds.Contains(postId))
                    || (!string.IsNullOrEmpty(shortCode) && knownShortCodes.Contains(shortCode));
            }
        }

        /// <summary>Adds the reel unless one with the same post id or shortcode is already held.</summary>
        public bool TryAddReel(InstagramScrapeUtilities.InstagramReel reel)
        {
            lock (gate)
            {
                if (IsKnownLocked(reel))
                {
                    stamps.Remove(reel);
                    return false;
                }
                reels.Add(reel);
                Index(reel);
            }
            MarkIndexDirty();
            return true;
        }

        public List<InstagramScrapeUtilities.InstagramReel> RemoveReels(Func<InstagramScrapeUtilities.InstagramReel, bool> predicate)
        {
            List<InstagramScrapeUtilities.InstagramReel> removed;
            lock (gate)
            {
                removed = reels.Where(predicate).ToList();
                if (removed.Count == 0) return removed;
                var gone = new HashSet<InstagramScrapeUtilities.InstagramReel>(removed, ReferenceEqualityComparer.Instance);
                reels = reels.Where(r => !gone.Contains(r)).ToList();
                foreach (var reel in removed) stamps.Remove(reel);
                RebuildIndexLocked();
            }
            MarkIndexDirty();
            return removed;
        }

        private bool IsKnownLocked(InstagramScrapeUtilities.InstagramReel reel) =>
            (!string.IsNullOrEmpty(reel.PostID) && knownPostIds.Contains(reel.PostID))
            || (!string.IsNullOrEmpty(reel.ShortCode) && knownShortCodes.Contains(reel.ShortCode));

        private void Index(InstagramScrapeUtilities.InstagramReel reel)
        {
            if (!string.IsNullOrEmpty(reel.PostID))
            {
                knownPostIds.Add(reel.PostID);
                byPostId.TryAdd(reel.PostID, reel);
            }
            if (!string.IsNullOrEmpty(reel.ShortCode)) knownShortCodes.Add(reel.ShortCode);
        }

        private void RebuildIndexLocked()
        {
            knownPostIds.Clear();
            knownShortCodes.Clear();
            byPostId.Clear();
            foreach (var reel in reels) Index(reel);
        }

        /// <summary>
        /// Loads every reel: from the reel index when it has one (one file), otherwise from every reel file. After an
        /// index load, call <see cref="StartReconcile"/> once the reels are published, to pick up what changed on disk.
        /// </summary>
        public async Task LoadAllScrapedInstagramReels()
        {
            var (items, report) = await index.LoadAsync();
            lock (gate)
            {
                reels = items.Select(i => i.Reel).ToList();
                stamps.Clear();
                foreach (var (reel, stamp) in items) stamps[reel] = stamp;
                RebuildIndexLocked();
            }
            LastLoad = report;
            if (report.Failures > 0)
            {
                _ = parent?.ServiceLogError($"Couldn't load {report.Failures} reel file(s), e.g. {report.FirstError}");
            }
            if (report.Via == "files")
            {
                // Every file was just read, so nothing is left to check; write the index the next start will use.
                reconciled = Task.CompletedTask;
                MarkIndexDirty(TimeSpan.Zero);
            }
        }

        /// <summary>Checks the loaded index against the reel directory in the background (no-op after a full read).</summary>
        public Task StartReconcile()
        {
            if (LastLoad?.Via != "index") return reconciled;
            reconciled = Task.Run(ReconcileCoreAsync);
            return reconciled;
        }

        private async Task ReconcileCoreAsync()
        {
            // Only what the index described is checked: a reel downloaded while this runs has its own fresh stamp and
            // must not be "removed" because the listing was taken before its file was written.
            var known = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
            var byFile = new Dictionary<string, InstagramScrapeUtilities.InstagramReel>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                foreach (var reel in reels)
                {
                    if (!stamps.TryGetValue(reel, out var stamp) || !known.TryAdd(stamp.File, stamp)) continue;
                    byFile[stamp.File] = reel;
                }
            }
            MemeScraperReelIndex.ReconcileResult result;
            try
            {
                result = await index.ReconcileAsync(known);
            }
            catch (Exception ex)
            {
                _ = parent?.ServiceLogError(ex, "MemeScraper couldn't check its reel index against the reel files; the index's reels stay loaded.");
                return;
            }

            bool changed = false;
            lock (gate)
            {
                foreach (var file in result.Removed)
                {
                    var reel = byFile[file];
                    // A source deletion may already have taken it out.
                    if (stamps.Remove(reel))
                    {
                        reels.Remove(reel);
                        changed = true;
                    }
                }
                foreach (var (file, fresh, stamp) in result.Changed)
                {
                    var old = byFile[file];
                    int at = reels.IndexOf(old);
                    if (at < 0) continue;
                    reels[at] = fresh;
                    stamps.Remove(old);
                    stamps[fresh] = stamp;
                    changed = true;
                }
                if (result.Added.Count > 0)
                {
                    var held = new HashSet<string>(stamps.Values.Select(s => s.File), StringComparer.OrdinalIgnoreCase);
                    foreach (var (fresh, stamp) in result.Added)
                    {
                        // Already in memory: downloaded (and stamped) after the listing began.
                        if (!held.Add(stamp.File)) continue;
                        reels.Add(fresh);
                        stamps[fresh] = stamp;
                        changed = true;
                    }
                }
                if (changed) RebuildIndexLocked();
            }
            LastReconcile = result.Report;
            if (result.Report.Failures > 0)
            {
                _ = parent?.ServiceLogError($"Couldn't read {result.Report.Failures} changed reel file(s) while checking the reel index, e.g. {result.Report.FirstError}");
            }
            if (changed) MarkIndexDirty(TimeSpan.Zero);
        }

        /// <summary>Rewrites the reel index soon (several changes in a row are written once).</summary>
        public void MarkIndexDirty(TimeSpan? delay = null)
        {
            if (Interlocked.Exchange(ref writeScheduled, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var wait = delay ?? writeDelay;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                }
                finally
                {
                    Interlocked.Exchange(ref writeScheduled, 0);
                }
                await WriteIndexAsync();
            });
        }

        /// <summary>Writes the reel index now. Never throws: the per-reel files stay the source of truth.</summary>
        public async Task WriteIndexAsync()
        {
            await writeLock.WaitAsync();
            try
            {
                List<MemeScraperReelIndex.Entry> entries;
                lock (gate)
                {
                    entries = reels.Select(reel => stamps.TryGetValue(reel, out var stamp)
                        ? new MemeScraperReelIndex.Entry { File = stamp.File, Length = stamp.Length, WriteTicks = stamp.WriteTicks, Reel = reel }
                        // No stamp (should not happen): a length of -1 never matches, so the next start reads that file.
                        : new MemeScraperReelIndex.Entry { File = FileNameFor(reel), Length = -1, WriteTicks = 0, Reel = reel }).ToList();
                }
                LastIndexWrite = await index.WriteAsync(entries);
                LastIndexWriteError = null;
            }
            catch (Exception ex)
            {
                LastIndexWriteError = ex.Message;
                _ = parent?.ServiceLogError(ex, "MemeScraper couldn't write its reel index; the next start reads every reel file instead.", false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private static string FileNameFor(InstagramScrapeUtilities.InstagramReel reel) =>
            !string.IsNullOrWhiteSpace(reel.InstagramReelInfoFilePath) ? Path.GetFileName(reel.InstagramReelInfoFilePath) : $"Reel{reel.PostID}.json";

        public async Task SaveInstagramReel(InstagramScrapeUtilities.InstagramReel reel)
        {
            try
            {
                string path = index.ReelsDirectory;
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                string filePath = Path.Combine(path, $"Reel{reel.PostID}.json");
                string json = JsonConvert.SerializeObject(reel, Formatting.Indented);
                // Temp + move: a crash mid-write must not leave a half-written JSON that fails to load.
                string temp = filePath + ".tmp";
                await File.WriteAllTextAsync(temp, json);
                File.Move(temp, filePath, overwrite: true);
                var stamp = Stamp.Of(new FileInfo(filePath));
                lock (gate) stamps[reel] = stamp;
            }
            catch (Exception ex)
            {
                _ = parent?.ServiceLogError(ex, "Error saving Instagram reel");
                throw;
            }
        }

        public async Task RemoveDuplicateReelsByVideoContentAsync()
        {
            var path = index.ReelsDirectory;
            var hashToReel = new Dictionary<string, InstagramScrapeUtilities.InstagramReel>();
            var duplicateReels = new List<InstagramScrapeUtilities.InstagramReel>();

            foreach (var reel in allScrapedReels)
            {
                string? videoFilePath = reel.InstagramReelVideoFilePath ?? reel.GetInstagramReelVideoFilePath();
                if (string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
                    continue;

                string hash;
                try
                {
                    using (var stream = File.OpenRead(videoFilePath))
                    using (var sha = SHA256.Create())
                    {
                        var hashBytes = await sha.ComputeHashAsync(stream);
                        hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    }
                }
                catch (Exception ex)
                {
                    _ = parent?.ServiceLogError(ex, $"Error hashing video file: {videoFilePath}");
                    continue;
                }

                if (!hashToReel.ContainsKey(hash))
                {
                    hashToReel[hash] = reel;
                }
                else
                {
                    duplicateReels.Add(reel);
                }
            }

            // Remove duplicates from memory
            var duplicates = new HashSet<InstagramScrapeUtilities.InstagramReel>(duplicateReels);
            RemoveReels(duplicates.Contains);

            // Delete duplicate JSON files
            foreach (var reel in duplicateReels)
            {
                try
                {
                    string filePath = Path.Combine(path, $"Reel{reel.PostID}.json");
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    _ = parent?.ServiceLogError(ex, $"Error deleting duplicate reel file for PostID: {reel.PostID}");
                }
            }
        }
    }
}
