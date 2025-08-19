using Omnipotent.Service_Manager;
using System.Net;
using System.Collections.Concurrent;
using System.Management.Automation;
using Omnipotent.Data_Handling;
using Newtonsoft.Json;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using static Omnipotent.Profiles.KMProfileManager;
using OpenQA.Selenium.DevTools.V136.Network;


namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraper : OmniService
    {
        MemeScraperSources SourceManager;
        InstagramScrapeUtilities instagramScrapeUtilities;
        MemeScraperMedia mediaManager;
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

            CreateRoutes();

            serviceManager.timeManager.TaskDue += TimeManager_TaskDue;

            foreach (var item in SourceManager.InstagramSources)
            {
                if (await serviceManager.timeManager.GetTask("ScrapeAllInstagramPostsFromSource" + item.AccountID) == null)
                {
                    ScrapeInstagramAccount(item);
                }
            }
        }

        private void TimeManager_TaskDue(object? sender, TimeManager.ScheduledTask e)
        {
            if (e.taskName.StartsWith("ScrapeAllInstagramPostsFromSource"))
            {
                ScrapeInstagramAccount(SourceManager.GetInstagramSourceByID((int)e.PassableData));
            }
        }

        public async Task ScrapeInstagramAccount(MemeScraperSources.InstagramSource source)
        {
            try
            {
                if (source.DownloadReels)
                {
                    List<InstagramScrapeUtilities.InstagramReel> reels = await instagramScrapeUtilities.AllInstagramProfileReelDownloadsLinksAsync(source.Username);
                    reels = reels.Where(k => mediaManager.allScrapedReels.Select(x => x.PostID).Contains(k.PostID)).ToList();
                    foreach (var reel in reels)
                    {
                        WebClient wc = new();
                        // Parse filetype from reel.VideoDownloadURL url  
                        string fileExtension = Path.GetExtension(new Uri(reel.VideoDownloadURL).AbsolutePath);

                        //Save Reel Video
                        string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperReelsVideoDirectory), $"ReelMedia{reel.PostID}{fileExtension}");
                        await wc.DownloadFileTaskAsync(new Uri(reel.VideoDownloadURL), path);
                        reel.DateTimeReelDownloaded = DateTime.Now;
                        reel.InstagramReelFilePath = $"ReelMedia{reel.PostID}{fileExtension}";

                        //Update Source
                        source.PathsOfAllMemes.Add(reel.InstagramReelFilePath);
                        source.MemesCollectedTotal++;
                        source.VideoMemesCollectedTotal++;
                        source.LastScraped = DateTime.Now;

                        //Save Reel Data
                        mediaManager.allScrapedReels.Add(reel);
                        await mediaManager.SaveInstagramReel(reel);

                        //Save Source Data
                        SourceManager.UpdateInstagramSource(source);
                    }
                }
                ServiceCreateScheduledTask(DateTime.Now.AddDays(1), "ScrapeAllInstagramPostsFromSource" + source.AccountID, "Meme Scraping", $"Go through all of {source.Username} posts and download them.", false, source.AccountID);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error scraping Instagram account", true);
                ServiceCreateScheduledTask(DateTime.Now.AddDays(1), "ScrapeAllInstagramPostsFromSource" + source.AccountID, "Meme Scraping", $"Go through all of {source.Username} posts and download them after an error.", false, source.AccountID);
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
                        niches.Add(item);
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
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/getAllSources", async (request) =>
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
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/memescraper/deleteSource", async (request) =>
            {
                try
                {
                    string id = request.userParameters["accountID"];
                    bool deleteAssociatedMemes = request.userParameters["deleteAssociatedMemes"] == "true";
                    await request.ReturnResponse("OK");
                }
                catch (Exception e)
                {
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { error = e.Message }), code: HttpStatusCode.InternalServerError);
                    ServiceLogError(e, $"Error in {request.route} route.");
                }
            }, HttpMethod.Post, KMPermissions.Guest);

        }
    }
}
