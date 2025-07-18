using Markdig.Helpers;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Net.Http.Headers;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace Omnipotent.Services.CS2ArbitrageBot.CSFloat
{
    public class CSFloatWrapper
    {
        public HttpClient Client;
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
            public int PriceInCents;
            public int PriceInPence;
            public double PriceInPounds;
            public string ListingURL;
            public string ImageURL;
            public int AppraisalBasePriceInPence;
            public double AppraisalBasePriceInPounds;
            public string AppraisalPriceText;

            public string AssetID;
            public double FloatValue;
            public string ItemID64;

            public DateTime DateTimeListingCreated;
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
                    foreach (object jsonItem in json.data)
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
        public ItemListing ConvertItemListingJSONItemToStruct(dynamic jsonItem, bool hasfloatValue = true)
        {
            ItemListing result = new ItemListing();
            result.ItemListingID = jsonItem.id;
            result.ItemName = jsonItem.item.item_name;
            result.ItemMarketHashName = jsonItem.item.market_hash_name;
            int price = Convert.ToInt32(jsonItem.price);
            result.PriceInCents = price;
            result.PriceInPence = Convert.ToInt32(Math.Ceiling(Convert.ToDecimal(price * parent.ExchangeRate)));
            result.PriceInPounds = Convert.ToDouble(result.PriceInPence) / 100;
            result.PriceText = "£" + result.PriceInPounds.ToString();
            result.ListingURL = $"https://csfloat.com/item/{result.ItemListingID}";
            result.ImageURL = "https://community.cloudflare.steamstatic.com/economy/image/" + jsonItem.item.icon_url;
            result.AppraisalBasePriceInPence = jsonItem.reference.base_price * parent.ExchangeRate;
            result.AppraisalBasePriceInPounds = Convert.ToDouble(result.AppraisalBasePriceInPence) / 100;
            result.AppraisalPriceText = "£" + result.AppraisalBasePriceInPounds.ToString();



            result.DateTimeListingCreated = DateTime.Parse(Convert.ToString(jsonItem.created_at));

            if (hasfloatValue == true)
            {
                result.FloatValue = jsonItem.item.float_value;
                result.ItemID64 = jsonItem.item.d_param;
            }
            result.AssetID = jsonItem.item.asset_id;
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
        public async Task<CSFloatAccountInformation> GetAccountInformation()
        {
            string url = "https://csfloat.com/api/v1/me";
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Get;
            message.RequestUri = new Uri(url);
            var response = await Client.SendAsync(message);
            if (response.IsSuccessStatusCode)
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                CSFloatAccountInformation account = new();
                try
                {
                    dynamic json = JsonConvert.DeserializeObject(responseContent);
                    dynamic user = json.user;

                    account.SteamID = user.steam_id;
                    account.Username = user.username;
                    account.Flags = user.flags;
                    account.Avatar = user.avatar;
                    account.Email = user.email;
                    account.PhoneNumber = user.phone_number;
                    account.BalanceInPence = (user.balance * parent.ExchangeRate);
                    account.BalanceInPounds = (account.BalanceInPence) / 100;
                    account.PendingBalanceInPence = (user.pending_balance * parent.ExchangeRate);
                    account.PendingBalanceInPounds = (account.PendingBalanceInPence) / 100;
                    account.StallPublic = user.stall_public;
                    account.Away = user.away;
                    account.TradeToken = user.trade_token;
                    account.KnowYourCustomer = user.know_your_customer;
                    account.ExtensionSetupAt = DateTime.Parse(user.extension_setup_at.ToString());
                    account.ObfuscatedID = user.obfuscated_id;
                    account.Online = user.online;
                    account.Fee = user.fee;
                    account.WithdrawFee = user.withdraw_fee;
                    account.Subscriptions = user.subscriptions.ToObject<List<string>>();
                    account.Has2FA = user.has_2fa;
                    account.HasAPIKey = user.has_api_key;

                    account.PaymentAccounts = new PaymentAccounts
                    {
                        StripeConnect = user.payment_accounts.stripe_connect,
                        StripeCustomer = user.payment_accounts.stripe_customer
                    };

                    account.Statistics = new Statistics
                    {
                        TotalSales = user.statistics.total_sales,
                        TotalPurchases = user.statistics.total_purchases,
                        MedianTradeTime = user.statistics.median_trade_time,
                        TotalAvoidedTrades = user.statistics.total_avoided_trades,
                        TotalFailedTrades = user.statistics.total_failed_trades,
                        TotalVerifiedTrades = user.statistics.total_verified_trades,
                        TotalTrades = user.statistics.total_trades
                    };

                    account.Preferences = new Preferences
                    {
                        OffersEnabled = user.preferences.offers_enabled,
                        MaxOfferDiscount = user.preferences.max_offer_discount
                    };

                    account.FirebaseMessaging = new FirebaseMessaging
                    {
                        Platform = user.firebase_messaging.platform,
                        LastUpdated = DateTime.Parse(user.firebase_messaging.last_updated.ToString())
                    };

                    account.StripeConnect = new StripeConnect
                    {
                        PayoutsEnabled = user.stripe_connect.payouts_enabled
                    };
                }
                catch (JsonException ex)
                {
                    throw new Exception("Failed to deserialize CSFloat account information.", ex);
                }

                return account;
            }
            else
            {
                throw new Exception($"Failed to get CSFloat account information. Status code: {response.StatusCode}. Content: {await response.Content.ReadAsStringAsync()}");
            }
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
                    foreach (object jsonItem in jsonData)
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

        public async Task<bool> BuyCSFloatListing(int priceincents, string itemlistingID)
        {
            string url = $"https://csfloat.com/api/v1/listings/buy";
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Post;
            message.RequestUri = new Uri(url);

            string payload = "{\"total_price\":" + priceincents + ",\"contract_ids\":[\"" + itemlistingID + "\"]}\r\n".Trim();
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await Client.SendAsync(message);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                var ex = new Exception($"Failed to buy CSFloat listing. Status code: {response.StatusCode}. Content: {await response.Content.ReadAsStringAsync()}");
                throw ex;
                return false;
            }

        }
        public async Task<bool> BuyCSFloatListing(ItemListing listing)
        {
            return await BuyCSFloatListing(listing.PriceInCents, listing.ItemListingID);
        }

        public class CSFloatAccountInformation
        {
            public string SteamID { get; set; }
            public string Username { get; set; }
            public int Flags { get; set; }
            public string Avatar { get; set; }
            public string Email { get; set; }
            public string PhoneNumber { get; set; }
            public int BalanceInPence { get; set; }
            public float BalanceInPounds { get; set; }
            public int PendingBalanceInPence { get; set; }
            public float PendingBalanceInPounds { get; set; }
            public bool StallPublic { get; set; }
            public bool Away { get; set; }
            public string TradeToken { get; set; }
            public PaymentAccounts PaymentAccounts { get; set; }
            public Statistics Statistics { get; set; }
            public Preferences Preferences { get; set; }
            public string KnowYourCustomer { get; set; }
            public DateTime ExtensionSetupAt { get; set; }
            public FirebaseMessaging FirebaseMessaging { get; set; }
            public StripeConnect StripeConnect { get; set; }
            public string ObfuscatedID { get; set; }
            public bool Online { get; set; }
            public double Fee { get; set; }
            public double WithdrawFee { get; set; }
            public List<string> Subscriptions { get; set; }
            public bool Has2FA { get; set; }
            public bool HasAPIKey { get; set; }
        }

        public class PaymentAccounts
        {
            public string StripeConnect { get; set; }
            public string StripeCustomer { get; set; }
        }

        public class Statistics
        {
            public int TotalSales { get; set; }
            public int TotalPurchases { get; set; }
            public int MedianTradeTime { get; set; }
            public int TotalAvoidedTrades { get; set; }
            public int TotalFailedTrades { get; set; }
            public int TotalVerifiedTrades { get; set; }
            public int TotalTrades { get; set; }
        }

        public class Preferences
        {
            public bool OffersEnabled { get; set; }
            public int MaxOfferDiscount { get; set; }
        }

        public class FirebaseMessaging
        {
            public int Platform { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        public class StripeConnect
        {
            public bool PayoutsEnabled { get; set; }
        }

    }
}
