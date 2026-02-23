using LangChain.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V138.Network;
using OpenQA.Selenium.Support.UI;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
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

            public string? InstagramReelInfoFilePath;
            public string? InstagramReelVideoFilePath;

            public DateTime DateTimeReelDownloaded;

            public string GetInstagramReelInfoFilePath()
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstagramReelInfoFilePath);
            }

            public string GetInstagramReelVideoFilePath()
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstagramReelVideoFilePath);
            }

            public void SetInstagramReelInfoFilePath(string path)
            {
                InstagramReelInfoFilePath = path;
            }

            public void SetInstagramReelVideoFilePath(string path)
            {
                InstagramReelVideoFilePath = path;
            }
        }

        public async Task<List<InstagramReel>> ScrapeAllInstagramProfileReelDownloadsLinksAsync(string username)
        {
            ConcurrentBag<string> reqIDs = new();
            ConcurrentBag<InstagramReel> reels = new();
            try
            {
                var seleniumObject = (await parent.GetSeleniumManager()).CreateSeleniumObject("ScrapeAllInstagramProfileReelDownloadsLinks");
                seleniumObject.AddArgumentToOptions("--headless"); // Run in headless mode  
                var driver = seleniumObject.UseChromeDriver();
                driver.Navigate().GoToUrl($"https://inflact.com/instagram-downloader?profile={username}/");

                var devTools = driver as IDevTools;
                var session = devTools.GetDevToolsSession();
                var network = new NetworkAdapter(session);
                await network.Enable(new EnableCommandSettings());

                Stopwatch st = Stopwatch.StartNew();
                int counter = 0;
                network.ResponseReceived += async (sender, e) =>
                {
                    if (e.Response.Url.Contains("reels"))
                    {
                        try
                        {
                            InstagramReel reel = new();
                            await Task.Delay(2500);
                            var body = await network.GetResponseBody(new GetResponseBodyCommandSettings
                            {
                                RequestId = e.RequestId
                            });
                            string content = body.Body;
                            dynamic jsonData = JsonConvert.DeserializeObject(content);
                            foreach (var item in jsonData.data.reels)
                            {
                                counter++;
                                try
                                {
                                    reel.PostID = item.post_id;
                                    reel.OwnerUsername = item.owner.username;
                                    reel.OwnerID = item.owner.id;
                                    reel.ViewCount = item.videoViewCount;
                                    reel.CreatedAt = OmniPaths.EpochSToDateTime(Convert.ToString(item.created_at));
                                    reel.ShortCode = item.shortCode;
                                    reel.ShortURL = $"https://www.instagram.com/reels/{reel.ShortCode}/";
                                    reel.VideoDownloadURL = item.url;
                                    reel.CommentCount = item.comment_count;
                                    reel.Description = item.description;
                                    string url = item.url;
                                    st.Restart();
                                    reels.Add(reel);
                                }
                                catch (Exception g)
                                {
                                    parent.ServiceLogError(g, "Error deserialising reel info", false);
                                }
                            }
                        }
                        catch (Exception t)
                        {
                            parent.ServiceLogError(t, "Error processing AllInstagramProfileReelDownloadsLinksAsync response.");

                        }
                    }
                };
                await Task.Delay(5000);
                var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(120));
                while (true)
                {
                    await Task.Delay(1000); // Wait and retry
                    var viewAllButton = wait.Until(d =>
                    {
                        try
                        {
                            return d.FindElement(By.CssSelector("div.StyledBtn-sc-1ygbkhl.kYFPxn.Btn-sc-1knupfx.gOCeBO[data-test-id='profile-reels-view-all']"));
                        }
                        catch (NoSuchElementException)
                        {
                            return null; // Retry until the element is found
                        }
                    });
                    if (viewAllButton != null)
                    {
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", viewAllButton);
                        break;
                    }
                }
                st.Restart();
                while (true)
                {
                    try
                    {
                        var viewMoreButton = wait.Until(d =>
                        {
                            var button = d.FindElements(By.CssSelector("div.StyledBtn-sc-1ygbkhl.hzXvLs.Btn-sc-1knupfx.gOCeBO")).FirstOrDefault();
                            var loadingSvg = button?.FindElements(By.CssSelector("svg.is-spin")).FirstOrDefault();

                            // Return the button only if it's not in a loading state  
                            return loadingSvg == null ? button : null;
                        });

                        if (viewMoreButton == null)
                        {
                            // Break out if the button no longer exists or is not found  
                            break;
                        }

                            // Click the button once it's ready  
                            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", viewMoreButton);
                        await Task.Delay(1000);
                    }
                    catch (WebDriverTimeoutException)
                    {
                        // Break out if the button no longer exists  
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


            //remove duplicates
            List<InstagramReel> uniqueReels = new();
            foreach (var reel in reels)
            {
                if (!uniqueReels.Any(r => r.ShortCode == reel.ShortCode))
                {
                    uniqueReels.Add(reel);
                }
            }

            return uniqueReels;
        }
    }
}
