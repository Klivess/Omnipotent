using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using tryAGI.OpenAI;
using System.Security.Cryptography;

namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraperMedia
    {
        MemeScraper parent;
        public List<InstagramScrapeUtilities.InstagramReel> allScrapedReels;
        public MemeScraperMedia(MemeScraper parent)
        {
            this.parent = parent;
            // Constructor logic if needed
            allScrapedReels = new List<InstagramScrapeUtilities.InstagramReel>();
            LoadAllScrapedInstagramReels().Wait();

        }

        private async Task LoadAllScrapedInstagramReels()
        {
            allScrapedReels = new List<InstagramScrapeUtilities.InstagramReel>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory);
            var files = Directory.GetFiles(path, "*.json");
            foreach (var item in files)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(item);
                    var reel = JsonConvert.DeserializeObject<InstagramScrapeUtilities.InstagramReel>(json);
                    if (reel != null)
                    {
                        allScrapedReels.Add(reel);
                    }
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError(ex, "Error loading AllScrapedInstagramReel json");
                }
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
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                parent.ServiceLogError(ex, "Error saving Instagram reel");
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
            allScrapedReels = hashToReel.Values.ToList();

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
