using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Security.Cryptography;

namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraperMedia
    {
        MemeScraper parent;
        private readonly object gate = new();
        private List<InstagramScrapeUtilities.InstagramReel> reels = new();
        private readonly HashSet<string> knownPostIds = new();
        private readonly HashSet<string> knownShortCodes = new();

        public MemeScraperMedia(MemeScraper parent)
        {
            this.parent = parent;
            LoadAllScrapedInstagramReels().Wait();
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
                if (IsKnownLocked(reel)) return false;
                reels.Add(reel);
                Index(reel);
                return true;
            }
        }

        public List<InstagramScrapeUtilities.InstagramReel> RemoveReels(Func<InstagramScrapeUtilities.InstagramReel, bool> predicate)
        {
            lock (gate)
            {
                var removed = reels.Where(predicate).ToList();
                if (removed.Count == 0) return removed;
                reels = reels.Except(removed).ToList();
                RebuildIndexLocked();
                return removed;
            }
        }

        private bool IsKnownLocked(InstagramScrapeUtilities.InstagramReel reel) =>
            (!string.IsNullOrEmpty(reel.PostID) && knownPostIds.Contains(reel.PostID))
            || (!string.IsNullOrEmpty(reel.ShortCode) && knownShortCodes.Contains(reel.ShortCode));

        private void Index(InstagramScrapeUtilities.InstagramReel reel)
        {
            if (!string.IsNullOrEmpty(reel.PostID)) knownPostIds.Add(reel.PostID);
            if (!string.IsNullOrEmpty(reel.ShortCode)) knownShortCodes.Add(reel.ShortCode);
        }

        private void RebuildIndexLocked()
        {
            knownPostIds.Clear();
            knownShortCodes.Clear();
            foreach (var reel in reels) Index(reel);
        }

        private async Task LoadAllScrapedInstagramReels()
        {
            var loaded = new List<InstagramScrapeUtilities.InstagramReel>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory);
            Directory.CreateDirectory(path);
            var files = Directory.GetFiles(path, "*.json");
            foreach (var item in files)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(item);
                    var reel = JsonConvert.DeserializeObject<InstagramScrapeUtilities.InstagramReel>(json);
                    if (reel != null)
                    {
                        loaded.Add(reel);
                    }
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError(ex, "Error loading AllScrapedInstagramReel json");
                }
            }
            lock (gate)
            {
                reels = loaded;
                RebuildIndexLocked();
            }
        }

        public async Task SaveInstagramReel(InstagramScrapeUtilities.InstagramReel reel)
        {
            try
            {
                string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory);
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
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, "Error saving Instagram reel");
                throw;
            }
        }

        public async Task RemoveDuplicateReelsByVideoContentAsync()
        {
            var path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory);
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
                    parent.ServiceLogError(ex, $"Error hashing video file: {videoFilePath}");
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
                    parent.ServiceLogError(ex, $"Error deleting duplicate reel file for PostID: {reel.PostID}");
                }
            }
        }
    }
}
