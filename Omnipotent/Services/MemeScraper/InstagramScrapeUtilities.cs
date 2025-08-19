using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V138.Network;
using OpenQA.Selenium.Support.UI;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Omnipotent.Services.MemeScraper
{
    public class InstagramScrapeUtilities
    {
        MemeScraper parent;

        public InstagramScrapeUtilities(MemeScraper parent)
        {
            this.parent = parent;
            // Constructor logic if needed
        }

        public class InstagramReel
        {
            public string PostID;
            public string OwnerUsername;
            public string OwnerID;
            public int ViewCount;
            public DateTime CreatedAt;
            public string ShortURL;
            public string VideoDownloadURL;
            public int CommentCount;
            public string Description;
            public string ShortCode;

            public string? InstagramReelFilePath;
            public DateTime DateTimeReelDownloaded;
        }

        public async Task<List<InstagramReel>> AllInstagramProfileReelDownloadsLinksAsync(string username)
        {
            List<InstagramReel> reels = new();
            try
            {
                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--headless"); // Run in headless mode
                var driver = new ChromeDriver(options);
                driver.Navigate().GoToUrl($"https://inflact.com/instagram-downloader?profile={username}/");

                var devTools = driver as IDevTools;
                var session = devTools.GetDevToolsSession();
                var network = new NetworkAdapter(session);
                await network.Enable(new EnableCommandSettings());

                Stopwatch st = Stopwatch.StartNew();

                network.ResponseReceived += async (sender, e) =>
                {
                    if (e.Response.Url.Contains("https://inflact.com/downloader/api/downloader/reels/"))
                    {
                        try
                        {
                            InstagramReel reel = new();
                            await Task.Delay(2500);
                            var body = await network.GetResponseBody(new GetResponseBodyCommandSettings
                            {
                                RequestId = e.RequestId
                            });
                            try
                            {
                                string content = body.Body;
                                dynamic jsonData = JsonConvert.DeserializeObject(content);
                                foreach (var item in jsonData.data.reels)
                                {
                                    string se = JsonConvert.SerializeObject(item);
                                    reel.PostID = item.post_id;
                                    reel.OwnerUsername = item.owner.username;
                                    reel.OwnerID = item.owner.id;
                                    reel.ViewCount = item.videoViewCount;
                                    reel.CreatedAt = OmniPaths.EpochMsToDateTime(item.created_at);
                                    reel.ShortCode = item.shortCode;
                                    reel.ShortURL = $"https://www.instagram.com/reels/{reel.ShortCode}/";
                                    reel.VideoDownloadURL = item.url;
                                    reel.CommentCount = item.comment_count;
                                    reel.Description = item.description;
                                    string url = item.url;
                                    if (reels.Select(k => k.PostID).Contains(reel.PostID))
                                    {
                                        continue; // Skip if the link already exists
                                    }
                                    reels.Add(reel);
                                    st.Restart();
                                }
                            }
                            catch (Exception ex)
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            //parent.ServiceLogError($"Error processing GetReelLinks response: {ex.Message}");
                        }
                    }
                };

                var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                wait.Until(d => d.FindElement(By.ClassName("input-main")));
                await Task.Delay(5000);


                var viewAllButton = driver.FindElement(By.CssSelector("div.StyledBtn-sc-1ygbkhl.kYFPxn.Btn-sc-1knupfx.gOCeBO[data-test-id='profile-reels-view-all']"));
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", viewAllButton);
                await Task.Delay(2000);
                st.Restart();
                while (true)
                {
                    try
                    {
                        var viewMoreButton = driver.FindElement(By.CssSelector("div.StyledBtn-sc-1ygbkhl.hzXvLs.Btn-sc-1knupfx.gOCeBO"));
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", viewMoreButton);
                        await Task.Delay(5000);
                    }
                    catch (NoSuchElementException e)
                    {
                        break;
                    }
                }

                while (st.Elapsed.TotalSeconds < 20)
                {
                    await Task.Delay(1000);
                }

                driver.Quit();
            }
            catch (Exception ex)
            {
                parent.ServiceLogError($"Error in AllInstagramProfileReelDownloadsLinksAsync: {ex.Message}");
            }

            return reels;
        }
    }
}
