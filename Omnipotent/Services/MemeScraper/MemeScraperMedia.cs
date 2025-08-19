using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using tryAGI.OpenAI;

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
            foreach (var item in Directory.GetFiles(path, "*.json"))
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
    }
}
