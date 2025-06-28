using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using static System.Net.Mime.MediaTypeNames;
using System.Drawing;
using System.Net.Http.Headers;
using System.Net;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class SteamAPIWrapper
    {
        private CS2ArbitrageBot parent;
        public int SentRequests = 0;
        public SteamAPIWrapper(CS2ArbitrageBot parent)
        {
            this.parent = parent;
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
            public int PriceInPence;
            public double PriceInPounds;
            public string SellListings;
            public string PriceText;
            public string ImageURL;
            public FloatType floatType;
            public Color NameColor;
            public string ListingURL;
        }

        public const string CS2APPID = "730";
        public const string ItemImageURLPrefix = "https://community.fastly.steamstatic.com/economy/image/";

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
                listing.PriceInPence = Convert.ToInt32(firstListing["converted_price"]) + Convert.ToInt32(firstListing["converted_fee"]);
                listing.PriceInPounds = Convert.ToDouble(listing.PriceInPence) / 100;
                listing.PriceText = "£" + listing.PriceInPounds.ToString();
                listing.SellListings = "999";
                listing.ListingURL = $"https://steamcommunity.com/market/listings/730/{itemHashName.Replace(" ", "+")}";
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
                string url = $"https://steamcommunity.com/market/search/render/?query=&start={queries[i] + (i * 100) + startPage}&count={queries[i]}&search_descriptions=0&appid=730&norender=1";
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
                        listing.PriceInPence = Convert.ToInt32(Math.Round(Convert.ToDouble(item.sell_price) * parent.ExchangeRate));
                        listing.PriceInPounds = Convert.ToDouble(listing.PriceInPence) / 100;
                        listing.SellListings = item.sell_listings;
                        listing.PriceText = "£" + listing.PriceInPounds.ToString();
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
    }
}
