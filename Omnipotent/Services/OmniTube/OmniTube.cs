using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.OmniTube.Video_Factory;

namespace Omnipotent.Services.OmniTube
{
    public class OmniTube : OmniService
    {
        VideoFactory videoFactory;
        public OmniTube()
        {
            name = "OmniTube";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            videoFactory = new(this);
            if (OmniPaths.CheckIfOnServer() == false)
            {
                List<InstagramScrapeUtilities.InstagramReel> reels = new();
                var memeScraper = (MemeScraper.MemeScraper)(await serviceManager.GetServiceByClassType<MemeScraper.MemeScraper>())[0];
                while (memeScraper.mediaManager == null)
                {
                    await Task.Delay(100);
                }
                //Copy the reels from memeScraper to avoid threading issues
                reels.AddRange(memeScraper.mediaManager.allScrapedReels);
                //Pick 50 random reels
                reels = reels.OrderBy(x => Guid.NewGuid()).Take(50).ToList();
                string outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MemeCompilation.mp4");
                await videoFactory.ProduceMemeCompilation(reels, outputPath);
                ServiceLog("Meme compilation video created at: " + outputPath);
            }
        }
    }
}
