using FFMpegCore;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.OmniTube.Video_Factory;
using System.Collections.Concurrent;

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
                var reelsUnder10Seconds = new ConcurrentBag<InstagramScrapeUtilities.InstagramReel>();
                int error = 0;
                List<Exception> exceptions = new();
                await Task.WhenAll(reels.Select(async item =>
                {
                    try
                    {
                        var analysis = await FFProbe.AnalyseAsync(item.GetInstagramReelVideoFilePath());
                        if (analysis.Duration.TotalSeconds < 5)
                        {
                            reelsUnder10Seconds.Add(item);
                        }
                    }
                    catch (Exception e)
                    {
                        error++;
                        exceptions.Add(e);
                    }
                }));
                var selection = reelsUnder10Seconds.OrderBy(x => Guid.NewGuid()).Take(50).ToList();
                string outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MemeCompilation.mp4");
                //var success = await videoFactory.ProduceMemeCompilation(selection, outputPath);
                ServiceLog("Meme compilation video created at: " + outputPath);
            }
        }
    }
}
