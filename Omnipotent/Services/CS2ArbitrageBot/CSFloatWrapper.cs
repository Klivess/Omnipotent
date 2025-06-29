using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using static System.Net.Mime.MediaTypeNames;

namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class CSFloatWrapper
    {
        private HttpClient Client;
        public int SentRequests = 0;
        public int RequestsRemaining = 0;
        public CS2ArbitrageBot parent;
        public CSFloatWrapper(CS2ArbitrageBot parent, string CSFloatAPIKey)
        {
            Client = new HttpClient();
            Client.DefaultRequestHeaders.Add("Authorization", CSFloatAPIKey);
            this.parent = parent;
        }
        public struct ItemListing
        {
            public string ItemListingID;
            public string ItemName;
            public string ItemMarketHashName;
            public string PriceText;
            public int PriceInPence;
            public double PriceInPounds;
            public string ListingURL;
            public string ImageURL;
            public int AppraisalBasePriceInPence;
            public double AppraisalBasePriceInPounds;
            public string AppraisalPriceText;
        }

        public async Task<List<ItemListing>> GetBestDealsOnCSFloat(int amountOfListingsToLoad, bool noRepeatedItems = true, float? minimumPriceInPence = null, float? maximumPriceInPence = null, float? minimumQuantityOnSale = null,
            bool normalOnly = false, string csfloatSortBy = "best_deal")
        {
            List<ItemListing> result = new List<ItemListing>();

            List<int> queries = [];
            int loading = amountOfListingsToLoad;
            while (loading > 0)
            {
                if (loading > 50)
                {
                    queries.Add(50);
                    loading -= 50;
                }
                else
                {
                    queries.Add(loading);
                    loading = 0;
                }
            }
            int page = 0;
            int i = 0;
            string cursor = "";
            while (result.Count < amountOfListingsToLoad)
            {
                string url = $"https://csfloat.com/api/v1/listings?limit=50&type=buy_now";
                if (!string.IsNullOrEmpty(cursor))
                {
                    url += $"&cursor={cursor}";
                }
                if (minimumQuantityOnSale != null)
                {
                    url += $"&min_ref_qty={minimumQuantityOnSale}";
                }
                if (minimumPriceInPence != null)
                {
                    //convert to usd
                    url += $"&min_price={Math.Round(Convert.ToDecimal(minimumPriceInPence / parent.ExchangeRate))}";
                }
                if (maximumPriceInPence != null)
                {
                    //convert to usd
                    url += $"&max_price={Math.Round(Convert.ToDecimal(maximumPriceInPence / parent.ExchangeRate))}";
                }
                if (normalOnly)
                {
                    url += "&category=1";
                }
                HttpRequestMessage message = new();
                message.Method = HttpMethod.Get;
                message.RequestUri = new Uri(url);
                var response = Client.SendAsync(message).Result;
                SentRequests++;
                UpdateRequestsRemaining(response);
                string strResponse = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    dynamic json = JsonConvert.DeserializeObject(strResponse);
                    cursor = json.cursor;
                    int duplicateItems = 0;
                    foreach (dynamic jsonItem in json.data)
                    {
                        ItemListing item = ConvertItemListingJSONItemToStruct(jsonItem);
                        if (noRepeatedItems == true && result.Select(k => k.ItemName).Contains(item.ItemName))
                        {
                            duplicateItems++;
                        }
                        else
                        {
                            result.Add(item);
                        }
                    }
                    page++;
                    i++;
                }
                else
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        DateTime dateTimeTillReset;
                        try
                        {
                            var header = response.Headers.Where(k => k.Key == "x-ratelimit-reset");
                            dateTimeTillReset = ConvertEpochToDateTime(Convert.ToUInt32(string.Join("", header.First().Value.Take(50))));
                        }
                        catch (Exception ex)
                        {
                            dateTimeTillReset = DateTime.Now.AddMinutes(30);
                        }
                        float secondsToWait = Convert.ToSingle((dateTimeTillReset - DateTime.UtcNow).TotalSeconds);
                        if (secondsToWait < 0)
                        {
                            secondsToWait = 10;
                        }
                        parent.ServiceLogError($"Too many requests to CSFloat API. RateLimit resets at {dateTimeTillReset.ToString()}, so waiting {secondsToWait / 60} minutes until retrying.");
                        await Task.Delay(TimeSpan.FromSeconds(secondsToWait + 1));
                    }
                    else
                    {
                        parent.ServiceLogError($"Error when trying to get listings from CSFloat API. Status code: {response.StatusCode}. Content: {strResponse}");
                        Environment.Exit(0);
                    }
                }
            }
            return result;
        }

        private ItemListing ConvertItemListingJSONItemToStruct(dynamic jsonItem)
        {
            ItemListing result = new ItemListing();
            result.ItemListingID = jsonItem.id;
            result.ItemName = jsonItem.item.item_name;
            result.ItemMarketHashName = jsonItem.item.market_hash_name;
            result.PriceInPence = jsonItem.price * parent.ExchangeRate;
            result.PriceInPounds = Convert.ToDouble(result.PriceInPence) / 100;
            result.PriceText = "£" + result.PriceInPounds.ToString();
            result.ListingURL = $"https://csfloat.com/item/{result.ItemListingID}";
            result.ImageURL = "https://community.cloudflare.steamstatic.com/economy/image/" + jsonItem.item.icon_url;
            result.AppraisalBasePriceInPence = jsonItem.reference.base_price * parent.ExchangeRate;
            result.AppraisalBasePriceInPounds = Convert.ToDouble(result.AppraisalBasePriceInPence) / 100;
            result.AppraisalPriceText = "£" + result.AppraisalBasePriceInPounds.ToString();
            return result;
        }

        public static DateTime ConvertEpochToDateTime(long epochTime, bool isMilliseconds = false)
        {
            // The epoch time starts from January 1, 1970, UTC
            DateTime epochStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            if (isMilliseconds)
            {
                return epochStart.AddMilliseconds(epochTime);
            }
            else
            {
                return epochStart.AddSeconds(epochTime);
            }
        }
        private void UpdateRequestsRemaining(HttpResponseMessage message)
        {
            var header = message.Headers.Where(k => k.Key == "x-ratelimit-remaining");
        }

        public async IAsyncEnumerable<ItemListing> SnipeBestDealsOnCSFloat(int amountOfListingsToLoad, bool noRepeatedItems = true, float? minimumPriceInPence = null, float? maximumPriceInPence = null, float? minimumQuantityOnSale = null,
    bool normalOnly = false, string csfloatSortBy = "best_deal", bool searchRandomPages = true)
        {
            List<ItemListing> result = new List<ItemListing>();

            List<int> queries = [];
            int loading = amountOfListingsToLoad;
            while (loading > 0)
            {
                if (loading > 50)
                {
                    queries.Add(50);
                    loading -= 50;
                }
                else
                {
                    queries.Add(loading);
                    loading = 0;
                }
            }
            int page = 0;
            int i = 0;
            string cursor = "";
            while (result.Count < amountOfListingsToLoad)
            {
                string url = $"https://csfloat.com/api/v1/listings?limit=50&type=buy_now";
                if (!string.IsNullOrEmpty(cursor))
                {
                    url += $"&cursor={cursor}";
                }
                if (minimumQuantityOnSale != null)
                {
                    url += $"&min_ref_qty={minimumQuantityOnSale}";
                }
                if (minimumPriceInPence != null)
                {
                    //convert to usd
                    url += $"&min_price={Math.Round(Convert.ToDecimal(minimumPriceInPence / parent.ExchangeRate))}";
                }
                if (maximumPriceInPence != null)
                {
                    //convert to usd
                    url += $"&max_price={Math.Round(Convert.ToDecimal(maximumPriceInPence / parent.ExchangeRate))}";
                }
                if (normalOnly)
                {
                    url += "&category=1";
                }
                if (searchRandomPages)
                {
                    Random random = new Random();
                    int randomPage = random.Next(0, 50); // Random page between 0 and 999
                    url += $"&page={randomPage}";
                }
                else
                {
                    url += $"&page={page}"; // Use the current page number
                }
                HttpRequestMessage message = new();
                message.Method = HttpMethod.Get;
                message.RequestUri = new Uri(url);
                var response = Client.SendAsync(message).Result;
                SentRequests++;
                UpdateRequestsRemaining(response);
                string strResponse = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    dynamic json = JsonConvert.DeserializeObject(strResponse);
                    cursor = json.cursor;
                    int duplicateItems = 0;
                    dynamic jsonData = json.data;
                    foreach (dynamic jsonItem in jsonData)
                    {
                        //await Task.Yield(); // Allow async execution

                        ItemListing item = ConvertItemListingJSONItemToStruct(jsonItem);
                        if (noRepeatedItems == true && result.Select(k => k.ItemName).Contains(item.ItemName))
                        {
                            duplicateItems++;
                        }
                        else
                        {
                            result.Add(item);
                            Console.WriteLine(item.ItemMarketHashName);
                            yield return item;
                        }
                    }
                    page++;
                    i++;
                }
                else
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        DateTime dateTimeTillReset;
                        try
                        {
                            var header = response.Headers.Where(k => k.Key == "x-ratelimit-reset");
                            dateTimeTillReset = ConvertEpochToDateTime(Convert.ToUInt32(string.Join("", header.First().Value.Take(50))));
                        }
                        catch (Exception ex)
                        {
                            dateTimeTillReset = DateTime.Now.AddMinutes(30);
                        }
                        float secondsToWait = Convert.ToSingle((dateTimeTillReset - DateTime.UtcNow).TotalSeconds);
                        if (secondsToWait < 0)
                        {
                            secondsToWait = 10;
                        }
                        parent.ServiceLogError($"Too many requests to CSFloat API. RateLimit resets at {dateTimeTillReset.ToString()}, so waiting {secondsToWait / 60} minutes until retrying.");
                        await Task.Delay(TimeSpan.FromSeconds(secondsToWait + 1));
                    }
                    else
                    {
                        parent.ServiceLogError($"Error when trying to get listings from CSFloat API. Status code: {response.StatusCode}. Content: {strResponse}");
                        Environment.Exit(0);
                    }
                }
            }
        }

    }
}
