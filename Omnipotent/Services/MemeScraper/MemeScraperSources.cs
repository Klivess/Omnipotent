using JetBrains.Annotations;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V136.Network;
using System.Linq.Expressions;
using System.Net;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;

namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraperSources
    {
        MemeScraper parent;
        public List<InstagramSource> InstagramSources;
        public List<Niche> AllNiches;
        public MemeScraperSources(MemeScraper parent)
        {
            this.parent = parent;
            InstagramSources = new List<InstagramSource>();
            LoadAllInstagramSources().Wait();
        }
        public class Source
        {
            public int MemesCollectedTotal;
            public int VideoMemesCollectedTotal;
            public int ImageMemesCollectedTotal;
            public DateTime DateTimeAdded;
            public DateTime LastScraped;
            public DateTime LastUpdated;
            public List<string> PathsOfAllMemes = new List<string>();
            public List<Niche> Niches;
        }

        public class InstagramSource : Source
        {
            public string Username;
            public int Followers;
            public int AccountID;
            public string FullName;
            public string ProfilePictureUrl;
            public string Bio;
            public bool DownloadReels;
            public bool DownloadPosts;
            public float AverageLikes;
            public float AverageComments;
            public List<AccountTopHashtag> AccountTopHashtags;

            public struct AccountTopHashtag
            {
                public string Hashtag;
                public int Count;
                public string InflactHashtagUrl;
            }
        }

        public class Niche
        {
            public string NicheTagName;
            public DateTime CreatedAt;
            public DateTime LastUpdated;
        }

        public async Task SaveNiche(Niche niche)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperNichesDirectory), "Niche" + niche.NicheTagName + ".json");
            await parent.GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(niche, Formatting.Indented));
        }

        public async Task LoadNiches()
        {
            AllNiches = new List<Niche>();
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperNichesDirectory);
            string[] files = Directory.GetFiles(path, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    Niche niche = JsonConvert.DeserializeObject<Niche>(content);
                    AllNiches.Add(niche);
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError($"Error loading niche from file {file}: {ex.Message}");
                }
            }
        }
        public async Task LoadAllInstagramSources()
        {
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperInstagramSourcesDirectory));
            foreach (var file in files)
            {
                try
                {
                    string content = await parent.GetDataHandler().ReadDataFromFile(file);
                    InstagramSource source = JsonConvert.DeserializeObject<InstagramSource>(content);
                    if (source != null)
                    {
                        InstagramSources.Add(source);
                    }
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError($"Error loading Instagram source from file {file}: {ex.Message}");
                }
            }
        }
        public async Task SaveInstagramSource(InstagramSource source)
        {
            string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.MemeScraperInstagramSourcesDirectory), source.AccountID + ".json");
            await parent.GetDataHandler().WriteToFile(filePath, JsonConvert.SerializeObject(source, Formatting.Indented));
        }

        public async Task UpdateInstagramSource(InstagramSource source)
        {
            //Replace the existing source in the list if it exists
            var existingSource = InstagramSources.FirstOrDefault(s => s.AccountID == source.AccountID);
            if (existingSource != null)
            {
                InstagramSources.Remove(existingSource);
            }
            InstagramSources.Add(source);
            //Save the updated source to file
            await SaveInstagramSource(source);
        }
        public async Task<InstagramSource> ProduceNewInstagramSource(string username, bool DownloadReels, bool DownloadPosts, List<Niche> Niches)
        {
            InstagramSource source = new();
            ChromeOptions options = new ChromeOptions();
            options.AddArgument("--headless"); // Run in headless mode
            var driver = new ChromeDriver(options);


            var devTools = driver as IDevTools;
            var session = devTools.GetDevToolsSession();
            var network = new NetworkAdapter(session);
            await network.Enable(new EnableCommandSettings());

            bool dataAcquired = false;

            network.ResponseReceived += async (sender, e) =>
            {
                try
                {
                    if (e.Response.Url.StartsWith("https://inflact.com/downloader/api/downloader/profile/?lang=en"))
                    {
                        await Task.Delay(5000);
                        var body = await network.GetResponseBody(new GetResponseBodyCommandSettings
                        {
                            RequestId = e.RequestId
                        });

                        string content = body.Body;
                        dynamic jsonData = JsonConvert.DeserializeObject(content);
                        source.Username = jsonData.data.profile.username;
                        source.AccountID = jsonData.data.profile.id;
                        source.Followers = jsonData.data.profile.edge_followed_by.count;
                        source.FullName = jsonData.data.profile.full_name;
                        source.ProfilePictureUrl = jsonData.data.profile.profile_pic_download_url;
                        source.Bio = jsonData.data.profile.biography;
                        source.DownloadReels = DownloadReels;
                        source.DownloadPosts = DownloadPosts;
                        source.AverageLikes = jsonData.data.avg_likes;
                        source.AverageComments = jsonData.data.avg_comments;
                        source.AccountTopHashtags = new List<InstagramSource.AccountTopHashtag>();
                        foreach (var hashtag in jsonData.data.hashtags)
                        {
                            InstagramSource.AccountTopHashtag tag;
                            tag.Hashtag = hashtag.name;
                            tag.Count = hashtag.count;
                            tag.InflactHashtagUrl = hashtag.url;
                            source.AccountTopHashtags.Add(tag);
                        }

                        source.ImageMemesCollectedTotal = 0;
                        source.VideoMemesCollectedTotal = 0;
                        source.MemesCollectedTotal = 0;
                        source.DateTimeAdded = DateTime.Now;
                        source.LastUpdated = DateTime.Now;
                        source.PathsOfAllMemes = new List<string>();
                        source.Niches = Niches;
                        dataAcquired = true;
                    }
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError(ex, $"Error processing response in ProduceNewInstagramSource");
                }
            };


            driver.Navigate().GoToUrl($"https://inflact.com/instagram-downloader/?profile={username}");
            while (dataAcquired == false)
            {
                await Task.Delay(100);
            }
            await SaveInstagramSource(source);
            InstagramSources.Add(source);

            await parent.ServiceCreateScheduledTask(DateTime.Now.AddMinutes(30), "ScrapeAllInstagramPostsFromSource" + source.AccountID, "Meme Scraping", $"Go through all of {source.Username} posts and download them.", false, source.AccountID);

            driver.Quit();
            return source;
        }

        public InstagramSource GetInstagramSourceByID(int id)
        {
            try
            {
                return InstagramSources.Where(k => k.AccountID == id).ToArray()[0];
            }
            catch (Exception ex)
            {
                return null; // Return null if an error occurs
            }
        }
    }
}
