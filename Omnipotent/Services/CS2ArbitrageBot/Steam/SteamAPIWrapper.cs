using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using static System.Net.Mime.MediaTypeNames;
using System.Drawing;
using System.Net.Http.Headers;
using System.Net;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.Support.UI;
using Json.More;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    public class SteamAPIWrapper
    {
        public CS2ArbitrageBot parent;
        public SteamAPIProfileWrapper profileWrapper;
        private Dictionary<string, int> CS2NameIDTable;
        public int SentRequests = 0;

        string cs2NameIDTablePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotDirectory), "cs2nameIDtables.json");

        public SteamAPIWrapper(CS2ArbitrageBot parent)
        {
            this.parent = parent;
        }

        public async Task SteamAPIWrapperInitialisation()
        {
            profileWrapper = new SteamAPIProfileWrapper(this);
            if (OmniPaths.CheckIfOnServer() == false)
            {
                await profileWrapper.InitialiseLogin();
            }
            if (!File.Exists(cs2NameIDTablePath))
            {
                await DownloadCS2ItemNameIDTable();
            }
            await LoadCS2ItemNameIDTable();
            parent.serviceManager.timeManager.TaskDue += TimeManager_TaskDue;
        }
        private void TimeManager_TaskDue(object? sender, Service_Manager.TimeManager.ScheduledTask e)
        {

        }
        public enum FloatType
        {
            FactoryNew,
            MinimalWear,
            FieldTested,
            WellWorn,
            BattleScarred
        }
        public struct ItemListing
        {
            public string Name;
            public int CheapestSellOrderPriceInPence;
            public double CheapestSellOrderPriceInPounds;
            public int HighestBuyOrderPriceInPence;
            public double HighestBuyOrderPriceInPounds;
            public BuyAndSellOrders BuyAndSellOrders;
            public string SellListings;
            public string PriceText;
            public string ImageURL;
            public FloatType floatType;
            public Color NameColor;
            public string ListingURL;
        }

        public const string CS2APPID = "730";
        public const string ItemImageURLPrefix = "https://community.fastly.steamstatic.com/economy/image/";

        public async Task DownloadCS2ItemNameIDTable()
        {
            parent.ServiceLog("Downloading CS2 Item Name ID Table...");

            string url = "https://raw.githubusercontent.com/somespecialone/steam-item-name-ids/refs/heads/master/data/cs2.json";
            WebClient wc = new();
            wc.DownloadFile(new Uri(url), cs2NameIDTablePath);

            parent.ServiceLog("CS2 Item Name ID Table downloaded successfully.");

            parent.ServiceCreateScheduledTask(DateTime.Now.AddDays(3), "DownloadCS2ItemIDTables", "SteamAPIWrapper", "To ensure that ItemNameID table is up to date.");
        }
        public async Task AddToNameIDTable(string itemHashName, int itemID)
        {
            parent.ServiceLog("Adding " + itemHashName + "'s item id " + itemID + " to table and saving.");
            //Check if already exists first
            if (CS2NameIDTable.ContainsKey(itemHashName))
            {
                //Update it
                CS2NameIDTable[itemHashName] = itemID;
            }
            else
            {
                //Add it
                CS2NameIDTable.Add(itemHashName, itemID);
            }
            //Save to disk
            string json = JsonConvert.SerializeObject(CS2NameIDTable, Formatting.Indented);
            await parent.GetDataHandler().WriteToFile(cs2NameIDTablePath, json);
        }
        public async Task LoadCS2ItemNameIDTable()
        {
            parent.ServiceLog("Loading CS2 Item Name ID Table from disk...");
            string path = cs2NameIDTablePath;
            if (File.Exists(path))
            {
                string json = await parent.GetDataHandler().ReadDataFromFile(path, true);
                CS2NameIDTable = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                parent.ServiceLog("CS2 Item Name ID Table loaded successfully.");
            }
            else
            {
                parent.ServiceLogError("CS2 Item Name ID Table file not found. Downloading it first.");
                DownloadCS2ItemNameIDTable();
                await LoadCS2ItemNameIDTable();
            }
        }
        public async Task<ItemListing> GetItemOnMarket(string itemHashName)
        {
            ItemListing listing = new();

            string url = $"https://steamcommunity.com/market/listings/{CS2APPID}/{itemHashName}/render?currency=2";
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Get;
            message.RequestUri = new Uri(url);
            var proxy = new WebProxy();
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            //Use Proxy
            HttpClient client = new();
            HttpResponseMessage result = new();
            result = client.Send(message);
            SentRequests++;
            string strResponse = await result.Content.ReadAsStringAsync();
            if (result.IsSuccessStatusCode)
            {
                dynamic json = JsonConvert.DeserializeObject(strResponse);
                listing.Name = itemHashName;
                var jsonObj = JObject.Parse(strResponse);
                JObject listingInfo = json["listinginfo"];
                string firstKey = listingInfo.Properties().First().Name;
                JObject firstListing = (JObject)listingInfo[firstKey];
                listing.CheapestSellOrderPriceInPence = Convert.ToInt32(firstListing["converted_price"]) + Convert.ToInt32(firstListing["converted_fee"]);
                listing.CheapestSellOrderPriceInPounds = Convert.ToDouble(listing.CheapestSellOrderPriceInPence) / 100;
                listing.SellListings = "999";
                listing.ListingURL = $"https://steamcommunity.com/market/listings/730/{itemHashName.Replace(" ", "%20")}";
                try
                {
                    var assets = jsonObj["assets"]
    .Children<JProperty>().First().Value
    .Children<JProperty>().First().Value
    .Children<JProperty>().First().Value;
                    listing.ImageURL = ItemImageURLPrefix + assets["icon_url"];
                    listing.NameColor = ColorTranslator.FromHtml("#" + assets["name_color"]);
                }
                catch (Exception ex)
                {
                    parent.ServiceLogError($"Failed to get image URL for item {itemHashName}. Exception: {ex.Message}");
                }
                if (listing.Name.Contains("Field-Tested"))
                {
                    listing.floatType = FloatType.FieldTested;
                }
                else if (listing.Name.Contains("Minimal"))
                {
                    listing.floatType = FloatType.MinimalWear;
                }
                else if (listing.Name.Contains("Well-Worn"))
                {
                    listing.floatType = FloatType.WellWorn;
                }
                else if (listing.Name.Contains("Battle-Scarred"))
                {
                    listing.floatType = FloatType.BattleScarred;
                }
                else
                {
                    listing.floatType = FloatType.FactoryNew;
                }

                //Get Buy And Sell Orders;
                //Check if itemHashName exists in CS2NameIDTable
                if (!CS2NameIDTable.ContainsKey(itemHashName))
                {
                    parent.ServiceLog("Item not found in CS2NameIDTable. Attempting to get NameID via Selenium.");
                    string nameid = await GetItemNameIDViaSelenium(listing.ListingURL);
                    await AddToNameIDTable(itemHashName, Convert.ToInt32(nameid));
                }
                BuyAndSellOrders buyAndSellOrders = await GetAllBuyOrdersOfItem(CS2NameIDTable[itemHashName].ToString());
                listing.BuyAndSellOrders = buyAndSellOrders;
                listing.HighestBuyOrderPriceInPence = Convert.ToInt32(buyAndSellOrders.BuyOrders.OrderByDescending(k => k.Key).FirstOrDefault().Key * 100);
                listing.HighestBuyOrderPriceInPounds = Convert.ToDouble(listing.HighestBuyOrderPriceInPence) / 100;
                listing.PriceText = "£" + listing.HighestBuyOrderPriceInPounds.ToString();
            }
            else
            {
                if (result.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    parent.ServiceLog("Ratelimited by Steam... Waiting 25 seconds, and trying again.");
                    Task.Delay(TimeSpan.FromSeconds(25)).Wait();
                    return GetItemOnMarket(itemHashName).GetAwaiter().GetResult();
                }
                else
                {
                    parent.ServiceLogError($"Failed to get item from the steam market. \n\n Status Code: {result.StatusCode} Response: {strResponse}");
                }
            }
            return listing;
        }
        public async Task<List<ItemListing>> GetAllMarketListings(int countToLoad, int startPage = 0)
        {
            //Each query can only load 100 items. We need to split countToLoad into multiple queries.
            List<int> queries = [];
            int loading = countToLoad;
            while (loading > 0)
            {
                if (loading > 100)
                {
                    queries.Add(100);
                    loading -= 100;
                }
                else
                {
                    queries.Add(loading);
                    loading = 0;
                }
            }
            List<ItemListing> listings = new();
            parent.ServiceLog($"Getting {countToLoad} listings from the steam market.");
            for (int i = 0; i < 1; i++)
            {
                string url = $"https://steamcommunity.com/market/search/render/?query=&start={queries[i] + i * 100 + startPage}&count={queries[i]}&search_descriptions=0&appid=730&norender=1";
                //Create HTTP Message
                HttpRequestMessage message = new();
                message.Method = HttpMethod.Get;
                message.RequestUri = new Uri(url);
                HttpClient client = new();
                var result = client.Send(message);
                SentRequests++;
                if (result.IsSuccessStatusCode)
                {
                    //Parse Response
                    string response = await result.Content.ReadAsStringAsync();
                    //Parse JSON
                    dynamic json = JsonConvert.DeserializeObject(response);
                    foreach (dynamic item in json.results)
                    {
                        ItemListing listing = new();
                        listing.Name = item.name;
                        //Remove field-tested, well worn etc from name
                        listing.Name = listing.Name.Replace("Field-Tested", "").Replace("Minimal Wear", "").Replace("Well-Worn", "").Replace("Battle-Scarred", "").Replace("Factory New", "").Trim();
                        //Remove parenthesis from name
                        listing.Name = listing.Name.Replace("(", "").Replace(")", "").Trim();
                        listing.CheapestSellOrderPriceInPence = Convert.ToInt32(Math.Round(Convert.ToDouble(item.sell_price) * parent.ExchangeRate));
                        listing.CheapestSellOrderPriceInPounds = Convert.ToDouble(listing.CheapestSellOrderPriceInPence) / 100;
                        listing.SellListings = item.sell_listings;
                        listing.PriceText = "£" + listing.CheapestSellOrderPriceInPounds.ToString();
                        listing.ImageURL = ItemImageURLPrefix + item.asset_description.icon_url;
                        listing.NameColor = ColorTranslator.FromHtml("#" + item.asset_description.name_color);
                        if (listing.Name.Contains("Field-Tested"))
                        {
                            listing.floatType = FloatType.FieldTested;
                        }
                        else if (listing.Name.Contains("Minimal"))
                        {
                            listing.floatType = FloatType.MinimalWear;
                        }
                        else if (listing.Name.Contains("Well-Worn"))
                        {
                            listing.floatType = FloatType.WellWorn;
                        }
                        else if (listing.Name.Contains("Battle-Scarred"))
                        {
                            listing.floatType = FloatType.BattleScarred;
                        }
                        else
                        {
                            listing.floatType = FloatType.FactoryNew;
                        }
                        listings.Add(listing);
                    }
                    parent.ServiceLog($"{listings.Count} listings out of {countToLoad} listings acquired from the steam market.");
                }
                else if (result.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    parent.ServiceLog("Ratelimited by Steam... Waiting 1 second and trying again.");
                    await Task.Delay(1000);
                    i--;
                }
                else
                {
                    parent.ServiceLog("Failed to get listings from the steam market.");
                    throw new Exception("Failed to get listings from the steam market.");
                }
            }
            return listings;
        }
        public struct BuyAndSellOrders
        {
            public Dictionary<double, int> BuyOrders;
            public Dictionary<double, int> SellOrders;
        }
        public async Task<BuyAndSellOrders> GetAllBuyOrdersOfItem(string itemID)
        {
            Dictionary<double, int> buyOrders = new();
            Dictionary<double, int> sellOrders = new();
            string url = $"https://steamcommunity.com/market/itemordershistogram?country=GB&language=english&currency=2&item_nameid={itemID}";
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Get;
            message.RequestUri = new Uri(url);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpClient client = new();
            var result = await client.SendAsync(message);
            SentRequests++;
            if (result.IsSuccessStatusCode)
            {
                string response = await result.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(response);
                foreach (var item in json.buy_order_graph)
                {
                    double price = Convert.ToDouble(item[0]);
                    int amount = Convert.ToInt32(item[1]);
                    buyOrders.Add(price, amount); //Otherwise, add a new entry
                }
                foreach (var item in json.sell_order_graph)
                {
                    double price = Convert.ToDouble(item[0]);
                    int amount = Convert.ToInt32(item[1]);
                    sellOrders.Add(price, amount); //Otherwise, add a new entry
                }
            }
            else
            {
                parent.ServiceLogError($"Failed to get buy orders for item {itemID}. Status Code: {result.StatusCode}");
            }
            BuyAndSellOrders orders = new()
            {
                BuyOrders = buyOrders,
                SellOrders = sellOrders
            };
            return orders;
        }
        public async Task<string> GetItemNameIDViaSelenium(string steamListingUrl)
        {
            try
            {
                var options = new ChromeOptions();
                options.AddArgument("--headless");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--disable-web-security");
                options.AddArgument("--disable-features=VizDisplayCompositor");
                options.AddArgument("--disable-logging");


                ChromeDriver chromeDriver = new ChromeDriver(options);
                parent.ServiceLog("Going to webpage: " + steamListingUrl);

                IDevTools devTools = chromeDriver;
                DevToolsSession session = devTools.GetDevToolsSession();
                await session.Domains.Network.EnableNetwork();
                bool found = false;
                string item_nameid = string.Empty;
                session.DevToolsEventReceived += (sender, e) =>
                {
                    if (e.EventData.ToJsonString().Contains("https://steamcommunity.com/market/itemordershistogram") && found == false)
                    {
                        try
                        {
                            parent.ServiceLog("Found itemordershistorgram request for url: " + steamListingUrl);
                            dynamic json = JsonConvert.DeserializeObject(e.EventData.ToJsonString());
                            string url = json.request.url;
                            //Parse parameters from URL
                            var parameters = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
                            item_nameid = parameters["item_nameid"];
                            parent.ServiceLog("Found nameid " + item_nameid + " for url: " + steamListingUrl);
                            found = true;
                        }
                        catch (Exception ex) { }
                    }
                };

                chromeDriver.Navigate().GoToUrl(steamListingUrl);
                WebDriverWait wait = new WebDriverWait(chromeDriver, TimeSpan.FromSeconds(10));
                while (found == false)
                {
                    await Task.Delay(100);
                }
                chromeDriver.Quit();
                return item_nameid;
                //IWebElement searchBox = wait.Until(d => d.FindElement(By.Name("q")));

            }
            catch (Exception ex)
            {
                LogErrorStatic("Main Thread", ex, "Error in TestTask!");
                return null;
            }
        }
    }
}
