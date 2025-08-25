using Omnipotent.Service_Manager;
using System.Net;
using System.Collections.Concurrent;
using System.Management.Automation;
using Omnipotent.Data_Handling;
using Newtonsoft.Json;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using static Omnipotent.Profiles.KMProfileManager;
using OpenQA.Selenium.DevTools.V136.Network;
using Org.BouncyCastle.Asn1.X500;
using Omnipotent.Services.MemeScraper.MemeScraper_Labs;


namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraper : OmniService
    {
        public MemeScraperSources SourceManager;
        public InstagramScrapeUtilities instagramScrapeUtilities;
        public MemeScraperMedia mediaManager;
        public MemeScraperLabs memeScraperLabs;
        public MemeScraper()
        {
            name = "MemeScraper";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            SourceManager = new MemeScraperSources(this);
            instagramScrapeUtilities = new InstagramScrapeUtilities(this);
            mediaManager = new MemeScraperMedia(this);
            memeScraperLabs = new(this);

            if (!OmniPaths.CheckIfOnServer())
            {
                //var list = await instagramScrapeUtilities.ScrapeAllInstagramProfileReelDownloadsLinksAsync("tgpu");
                //ServiceLog($"Found {list.Count} reels for tgpu.", true);

                //await DownloadVideosInParallel(list.Select(k => k.VideoDownloadURL).ToList(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MemeScraper2"));
            }

            //CheckForBrokenFilePaths();

            CreateRoutes();

            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;

            foreach (var item in SourceManager.InstagramSources)
            {
                if (await serviceManager.timeManager.GetTask("ScrapeAllInstagramPostsFromSource-" + item.Username) == null)
                {
                    Random rnd = new();
                    ServiceCreateScheduledTask(DateTime.Now.AddMinutes(rnd.Next(0, 1000)), "ScrapeAllInstagramPostsFromSource" + item.Username,
    "Meme Scraping", $"Go through all of {item.Username} posts and download them.", false, item.AccountID);
                }
            }
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (e.taskName.StartsWith("ScrapeAllInstagramPostsFromSource"))
            {
                ScrapeInstagramAccount(SourceManager.GetInstagramSourceByID((string)e.PassableData));
            }
        }

        public async Task ScrapeInstagramAccount(MemeScraperSources.InstagramSource source)
        {
            Random rnd = new();
            try
            {
                if (source.DownloadReels)
                {
                    ServiceLog($"Starting to scrape Instagram account {source.Username} for reels.");
                    List<InstagramScrapeUtilities.InstagramReel> reels = await instagramScrapeUtilities.ScrapeAllInstagramProfileReelDownloadsLinksAsync(source.Username);
                    ServiceLog($"Found {reels.Count} reels to download from {source.Username}.", true);
                    await Parallel.ForEachAsync(reels, async (reel, token) =>
                    {
                        try
                        {
                            WebClient wc = new();
                            // Parse filetype from reel.VideoDownloadURL url  
                            string fileExtension = Path.GetExtension(new Uri(reel.VideoDownloadURL).AbsolutePath);

                            if (!mediaManager.allScrapedReels.Any(k => k.PostID == reel.PostID))
                            {
                                //Save Reel Video
                                string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsVideoDirectory), $"ReelMedia{reel.PostID}{fileExtension}");
                                await wc.DownloadFileTaskAsync(new Uri(reel.VideoDownloadURL), path);
                                reel.DateTimeReelDownloaded = DateTime.Now;

                                string dataPath = Path.Combine(OmniPaths.GlobalPaths.MemeScraperReelsDataDirectory, $"Reel{reel.PostID}.json");
                                try
                                {
                                    //Update Source
                                    source.LastScraped = DateTime.Now;

                                    //Save Reel Data
                                    reel.SetInstagramReelInfoFilePath(dataPath);
                                    string videoPath = Path.Combine(OmniPaths.GlobalPaths.MemeScraperReelsVideoDirectory, $"ReelMedia{reel.PostID}{fileExtension}");
                                    reel.SetInstagramReelVideoFilePath(videoPath);
                                    mediaManager.allScrapedReels.Add(reel);
                                    await mediaManager.SaveInstagramReel(reel);

                                    //Save Source Data
                                    SourceManager.UpdateInstagramSource(source);
                                }
                                catch (Exception e)
                                {
                                    ServiceLogError(e, $"Error saving reel {reel.PostID} for {source.Username}.", true);
                                    reel.DateTimeReelDownloaded = DateTime.MinValue; // Reset the download time
                                    File.Delete(path); // Delete the file if saving fails
                                    File.Delete(dataPath);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            await ServiceLogError(e, $"Error processing reel {reel.PostID} for {source.Username}.", true);
                        }
                    });
                }
                ServiceLog($"Finished scraping Instagram account {source.Username} for reels.", true);
                ServiceCreateScheduledTask(DateTime.Now.AddDays(3).AddMinutes(rnd.Next(0, 500)), "ScrapeAllInstagramPostsFromSource" + source.Username,
                    "Meme Scraping", $"Go through all of {source.Username} posts and download them.", false, source.AccountID);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error scraping Instagram account", true);
                ServiceCreateScheduledTask(DateTime.Now.AddDays(1).AddMinutes(rnd.Next(0, 500)), "ScrapeAllInstagramPostsFromSource" + source.Username, "Meme Scraping",
                    $"Go through all of {source.Username} posts and download them after an error.", false, source.AccountID);
            }
        }


        private async Task CreateRoutes()
        {
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/addInstagramSource", async (request) =>
            {
                try
                {
                    string content = request.userMessageContent;
                    if (string.IsNullOrEmpty(content))
                    {
                        await request.ReturnResponse("No username provided.", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    dynamic jsonData = JsonConvert.DeserializeObject(content);
                    string username = jsonData.username;
                    bool DownloadReels = jsonData.downloadReels ?? false;
                    bool DownloadPosts = jsonData.downloadPosts ?? false;
                    List<string> niches = new();
                    foreach (var item in jsonData.niches)
                    {
                        niches.Add(item.ToString());
                    }
                    List<MemeScraperSources.Niche> nichesList = new();
                    foreach (var niche in niches)
                    {
                        if (SourceManager.AllNiches.Select(k => k.NicheTagName).Contains(niche))
                        {
                            nichesList.Add(SourceManager.AllNiches.Where(k => k.NicheTagName == niche).ToList()[0]);
                        }
                        else
                        {
                            var nichey = new MemeScraperSources.Niche
                            {
                                NicheTagName = niche,
                                CreatedAt = DateTime.Now,
                                LastUpdated = DateTime.Now
                            };
                            await SourceManager.SaveNiche(nichey);
                            SourceManager.AllNiches.Add(nichey);
                            nichesList.Add(nichey);
                        }
                    }
                    SourceManager.ProduceNewInstagramSource(username, DownloadReels, DownloadPosts, nichesList);
                    await request.ReturnResponse("Instagram source being added.", code: HttpStatusCode.OK);

                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Post, KMPermissions.Manager);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/getAllInstagramSources", async (request) =>
            {
                try
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(SourceManager.InstagramSources), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/memeScraperAnalytics", async (request) =>
            {
                try
                {
                    var analytics = new MemeScraperLabs.MemeScraperAnalytics(SourceManager.InstagramSources, mediaManager.allScrapedReels);
                    await request.ReturnResponse(JsonConvert.SerializeObject(analytics), code: HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Get, KMPermissions.Guest);
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/deleteInstagramSource", async (request) =>
            {
                try
                {
                    string id = request.userParameters["sourceAccountID"];
                    bool deleteAssociatedMemes = request.userParameters["deleteAssociatedMemes"] == "true";

                    var source = SourceManager.GetInstagramSourceByID(id);
                    SourceManager.DeleteInstagramSource(source, deleteAssociatedMemes);

                    await request.ReturnResponse("OK");
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Get, KMPermissions.Manager);

        }

        public async Task DownloadVideosInParallel(List<string> videoDownloadLinks, string targetDirectory)
        {
            try
            {
                Directory.CreateDirectory(targetDirectory); // Ensure the target directory exists  

                await Parallel.ForEachAsync(videoDownloadLinks, async (videoUrl, token) =>
                {
                    WebClient wc = new();
                    string fileExtension = Path.GetExtension(new Uri(videoUrl).AbsolutePath);
                    string fileName = $"Video_{Guid.NewGuid()}{fileExtension}";
                    string filePath = Path.Combine(targetDirectory, fileName);

                    await wc.DownloadFileTaskAsync(new Uri(videoUrl), filePath);
                });
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error downloading videos in parallel.", true);
            }
        }
    }
}
