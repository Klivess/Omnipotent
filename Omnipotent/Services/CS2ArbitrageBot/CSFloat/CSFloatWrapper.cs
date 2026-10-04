using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using System.Globalization;
using System.Net;
using System.Text;

namespace Omnipotent.Services.CS2ArbitrageBot.CSFloat
{
    /// <summary>Thrown when CSFloat rejects the API key (401/403). Retrying cannot help.</summary>
    public sealed class CSFloatAuthException : Exception
    {
        public CSFloatAuthException(string message) : base(message) { }
    }

    /// <summary>Thrown when a CSFloat bucket is exhausted; <see cref="RetryAtUtc"/> says when it resets.</summary>
    public sealed class CSFloatRateLimitedException : Exception
    {
        public string Bucket { get; }
        public DateTime RetryAtUtc { get; }
        public CSFloatRateLimitedException(string bucket, DateTime retryAtUtc)
            : base($"CSFloat '{bucket}' rate limit exhausted until {retryAtUtc:HH:mm:ss} UTC.")
        {
            Bucket = bucket;
            RetryAtUtc = retryAtUtc;
        }
    }

    /// <summary>
    /// CSFloat REST client. One pooled HttpClient (the old code created clients ad hoc and blocked on
    /// <c>.Result</c>), header-driven rate-limit budgets, typed parsing, and no <c>Environment.Exit</c>:
    /// the previous version terminated the entire Omnipotent process on any unexpected listings error.
    /// </summary>
    public class CSFloatWrapper
    {
        public const string BaseUrl = "https://csfloat.com/api/v1";

        // Rate-limit bucket names (one per server-side window).
        public const string ListingsBucket = "listings";
        public const string MeBucket = "me";
        public const string TradesBucket = "trades";
        public const string BuyBucket = "buy";
        public const string PriceListBucket = "price-list";
        public const string HistoryBucket = "history";
        public const string CreateListingBucket = "create-listing";
        public const string SingleListingBucket = "single-listing";
        public const string InventoryBucket = "inventory";

        public HttpClient Client;
        public readonly RateLimitBudget RateLimits = new();
        public CS2ArbitrageBot parent;
        public int SentRequests => (int)Math.Min(int.MaxValue, Interlocked.Read(ref sentRequests));
        private long sentRequests;

        public CSFloatWrapper(CS2ArbitrageBot parent, string CSFloatAPIKey)
        {
            this.parent = parent;
            Client = CreateHttpClient(CSFloatAPIKey);
        }

        /// <summary>For tests and tools: an explicit client (e.g. over a fake handler) and no owning service.</summary>
        public CSFloatWrapper(CS2ArbitrageBot? parent, HttpClient client)
        {
            this.parent = parent!;
            Client = client;
        }

        internal static HttpClient CreateHttpClient(string? apiKey)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey.Trim());
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Omnipotent-CS2ArbitrageBot/2.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        // ───────────────────────────── Legacy shapes (persisted JSON) ─────────────────────────────

        /// <summary>Legacy listing shape. Kept because purchased-item files on disk embed it.</summary>
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

        public static ItemListing ToLegacyItemListing(CSFloatListing listing, double gbpPerUsd)
        {
            int pence = ArbitrageMath.UsdCentsToPenceCeil(listing.PriceCents, gbpPerUsd);
            int basePence = listing.BasePriceCents is > 0 ? ArbitrageMath.UsdCentsToPenceFloor(listing.BasePriceCents.Value, gbpPerUsd) : 0;
            return new ItemListing
            {
                ItemListingID = listing.Id,
                ItemName = listing.ItemName,
                ItemMarketHashName = listing.MarketHashName,
                PriceInCents = listing.PriceCents,
                PriceInPence = pence,
                PriceInPounds = pence / 100.0,
                PriceText = "£" + (pence / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                ListingURL = listing.ListingUrl,
                ImageURL = listing.ImageUrl,
                AppraisalBasePriceInPence = basePence,
                AppraisalBasePriceInPounds = basePence / 100.0,
                AppraisalPriceText = "£" + (basePence / 100.0).ToString("F2", CultureInfo.InvariantCulture),
                AssetID = listing.AssetId,
                FloatValue = listing.FloatValue ?? 0,
                ItemID64 = listing.DParam,
                DateTimeListingCreated = listing.CreatedAtUtc,
            };
        }

        /// <summary>Back-compat for callers holding raw listing JSON (e.g. manual purchase by id).</summary>
        public ItemListing ConvertItemListingJSONItemToStruct(dynamic jsonItem, bool hasfloatValue = true)
        {
            var listing = CSFloatListing.FromJson(jsonItem is JToken token ? token : JToken.FromObject(jsonItem));
            return ToLegacyItemListing(listing, parent?.ExchangeRate ?? 0);
        }

        // ───────────────────────────── Listings ─────────────────────────────

        public sealed class ListingQuery
        {
            /// <summary>most_recent, lowest_price, highest_discount, best_deal (default), expires_soon, ...</summary>
            public string SortBy { get; set; } = "most_recent";
            public int Limit { get; set; } = 50;
            public string? Cursor { get; set; }
            public int? MinPriceCents { get; set; }
            public int? MaxPriceCents { get; set; }
            /// <summary>1 normal, 2 StatTrak, 3 souvenir (comma-separated multi-value supported); null = all.</summary>
            public string? Categories { get; set; }
            public string? MarketHashName { get; set; }
            public int? MinReferenceQuantity { get; set; }
            public string Type { get; set; } = "buy_now";

            public string ToQueryString()
            {
                var parts = new List<string>
                {
                    "limit=" + Math.Clamp(Limit, 1, 50).ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(Type)) parts.Add("type=" + Uri.EscapeDataString(Type));
                if (!string.IsNullOrEmpty(SortBy) && SortBy != "best_deal") parts.Add("sort_by=" + Uri.EscapeDataString(SortBy));
                if (!string.IsNullOrEmpty(Cursor)) parts.Add("cursor=" + Uri.EscapeDataString(Cursor));
                if (MinPriceCents is > 0) parts.Add("min_price=" + MinPriceCents.Value.ToString(CultureInfo.InvariantCulture));
                if (MaxPriceCents is > 0) parts.Add("max_price=" + MaxPriceCents.Value.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(Categories)) parts.Add("category=" + Uri.EscapeDataString(Categories));
                if (!string.IsNullOrEmpty(MarketHashName)) parts.Add("market_hash_name=" + Uri.EscapeDataString(MarketHashName));
                if (MinReferenceQuantity is > 0) parts.Add("min_ref_qty=" + MinReferenceQuantity.Value.ToString(CultureInfo.InvariantCulture));
                return string.Join("&", parts);
            }
        }

        public sealed class ListingPage
        {
            public List<CSFloatListing> Listings { get; set; } = new();
            public string? Cursor { get; set; }
            public int ParseFailures { get; set; }
        }

        public async Task<ListingPage> SearchListingsAsync(ListingQuery query, CancellationToken ct = default)
        {
            string body = await SendAsync(ListingsBucket, HttpMethod.Get, BaseUrl + "/listings?" + query.ToQueryString(), null, ct);
            return ParseListingPage(body);
        }

        public static ListingPage ParseListingPage(string body)
        {
            var page = new ListingPage();
            JToken root = JToken.Parse(body);
            JArray? data = root as JArray ?? root["data"] as JArray;
            if (root is JObject obj) page.Cursor = obj.Value<string>("cursor");
            if (data == null) return page;
            foreach (var token in data)
            {
                try { page.Listings.Add(CSFloatListing.FromJson(token)); }
                catch (FormatException) { page.ParseFailures++; }
            }
            return page;
        }

        public async Task<CSFloatListing?> GetListingAsync(string listingId, CancellationToken ct = default)
        {
            try
            {
                string body = await SendAsync(SingleListingBucket, HttpMethod.Get, BaseUrl + "/listings/" + Uri.EscapeDataString(listingId), null, ct);
                return CSFloatListing.FromJson(JToken.Parse(body));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public sealed class PriceListEntry
        {
            public string MarketHashName { get; set; } = "";
            public int Quantity { get; set; }
            public int MinPriceCents { get; set; }
        }

        /// <summary>
        /// Every item's lowest buy-now ask on CSFloat in one request (~28k items). It is a CDN-cached
        /// snapshot refreshed about hourly, so it gives full-market coverage, not real-time sniping.
        /// </summary>
        public async Task<List<PriceListEntry>> GetPriceListAsync(CancellationToken ct = default)
        {
            string body = await SendAsync(PriceListBucket, HttpMethod.Get, BaseUrl + "/listings/price-list", null, ct);
            return ParsePriceList(body);
        }

        public static List<PriceListEntry> ParsePriceList(string body)
        {
            var result = new List<PriceListEntry>();
            if (JToken.Parse(body) is not JArray array) return result;
            foreach (var token in array.OfType<JObject>())
            {
                string? name = token.Value<string>("market_hash_name");
                int? price = CSFloatListing.ReadInt(token["min_price"]);
                if (string.IsNullOrEmpty(name) || price is not > 0) continue;
                result.Add(new PriceListEntry { MarketHashName = name, MinPriceCents = price.Value, Quantity = CSFloatListing.ReadInt(token["quantity"]) ?? 0 });
            }
            return result;
        }

        public sealed class SalesGraphPoint
        {
            public DateTime DayUtc { get; set; }
            public int Count { get; set; }
            public double AveragePriceCents { get; set; }
        }

        /// <summary>Daily CSFloat sale counts and average prices for an item (500 requests/day budget).</summary>
        public async Task<List<SalesGraphPoint>> GetSalesGraphAsync(string marketHashName, CancellationToken ct = default)
        {
            string body = await SendAsync(HistoryBucket, HttpMethod.Get, BaseUrl + "/history/" + Uri.EscapeDataString(marketHashName) + "/graph", null, ct);
            var result = new List<SalesGraphPoint>();
            if (JToken.Parse(body) is not JArray array) return result;
            foreach (var token in array.OfType<JObject>())
            {
                if (!DateTime.TryParse(token.Value<string>("day"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)) continue;
                result.Add(new SalesGraphPoint
                {
                    DayUtc = day,
                    Count = CSFloatListing.ReadInt(token["count"]) ?? 0,
                    AveragePriceCents = token.Value<double?>("avg_price") ?? 0,
                });
            }
            return result.OrderByDescending(p => p.DayUtc).ToList();
        }

        // ───────────────────────────── Buying / selling ─────────────────────────────

        public sealed class BuyResult
        {
            public bool Success { get; set; }
            public HttpStatusCode StatusCode { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>
        /// Buys one listing. Expected rejections (already sold, price changed, insufficient balance) come
        /// back as a failed <see cref="BuyResult"/> rather than an exception.
        /// </summary>
        public async Task<BuyResult> BuyListingAsync(string listingId, int priceCents, CancellationToken ct = default)
        {
            string payload = JsonConvert.SerializeObject(new { total_price = priceCents, contract_ids = new[] { listingId } });
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/listings/buy")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await Client.SendAsync(request, ct);
            Interlocked.Increment(ref sentRequests);
            RateLimits.Observe(BuyBucket, response);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && LooksLikeAuthFailure(body))
                throw new CSFloatAuthException($"CSFloat rejected the API key while buying ({(int)response.StatusCode}): {Truncate(body)}");
            return new BuyResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = response.StatusCode,
                Message = response.IsSuccessStatusCode ? "purchased" : ExtractMessage(body),
            };
        }

        /// <summary>Back-compat wrapper used by the manual purchase path; throws on failure like the old API.</summary>
        public async Task<bool> BuyCSFloatListing(int priceincents, string itemlistingID)
        {
            var result = await BuyListingAsync(itemlistingID, priceincents);
            if (!result.Success) throw new Exception($"Failed to buy CSFloat listing. Status code: {result.StatusCode}. Message: {result.Message}");
            return true;
        }

        public async Task<bool> BuyCSFloatListing(ItemListing listing) => await BuyCSFloatListing(listing.PriceInCents, listing.ItemListingID);

        /// <summary>Creates a buy-now listing for an item in the account's Steam inventory. Returns the new listing id.</summary>
        public async Task<string?> CreateListingAsync(string assetId, int priceCents, CancellationToken ct = default)
        {
            string payload = JsonConvert.SerializeObject(new { asset_id = assetId, type = "buy_now", price = priceCents });
            string body = await SendAsync(CreateListingBucket, HttpMethod.Post, BaseUrl + "/listings", payload, ct);
            return JToken.Parse(body).Value<string>("id");
        }

        public sealed class InventoryItem
        {
            public string AssetId { get; set; } = "";
            public string MarketHashName { get; set; } = "";
            public bool Tradable { get; set; }
            public double? FloatValue { get; set; }
        }

        /// <summary>The account's Steam inventory as CSFloat sees it (asset ids + whether trade protection has lifted).</summary>
        public async Task<List<InventoryItem>> GetInventoryAsync(CancellationToken ct = default)
        {
            string body = await SendAsync(InventoryBucket, HttpMethod.Get, BaseUrl + "/me/inventory", null, ct);
            return ParseInventory(body);
        }

        public static List<InventoryItem> ParseInventory(string body)
        {
            var result = new List<InventoryItem>();
            JToken root = JToken.Parse(body);
            JArray? items = root as JArray ?? root["data"] as JArray ?? root["items"] as JArray;
            if (items == null) return result;
            foreach (var o in items.OfType<JObject>())
            {
                string? assetId = o.Value<string>("asset_id");
                string? name = o.Value<string>("market_hash_name");
                if (string.IsNullOrEmpty(assetId) || string.IsNullOrEmpty(name)) continue;
                var tradable = o["tradable"];
                result.Add(new InventoryItem
                {
                    AssetId = assetId,
                    MarketHashName = name,
                    Tradable = tradable != null && (tradable.Type == JTokenType.Boolean ? tradable.Value<bool>() : tradable.ToString() == "1"),
                    FloatValue = o["float_value"]?.Type is JTokenType.Float or JTokenType.Integer ? o.Value<double>("float_value") : null,
                });
            }
            return result;
        }

        /// <summary>Recent trades (buys and sells) for the account, newest first.</summary>
        public async Task<JArray> GetTradesAsync(int limit = 100, CancellationToken ct = default)
        {
            string body = await SendAsync(TradesBucket, HttpMethod.Get, BaseUrl + "/me/trades?page=0&limit=" + Math.Clamp(limit, 1, 1000).ToString(CultureInfo.InvariantCulture), null, ct);
            JToken root = JToken.Parse(body);
            return root as JArray ?? root["trades"] as JArray ?? new JArray();
        }

        // ───────────────────────────── Account ─────────────────────────────

        public async Task<CSFloatAccountInformation> GetAccountInformation()
        {
            string body = await SendAsync(MeBucket, HttpMethod.Get, BaseUrl + "/me", null, default);
            return ParseAccountInformation(body, parent?.ExchangeRate ?? 0);
        }

        public static CSFloatAccountInformation ParseAccountInformation(string body, double gbpPerUsd)
        {
            JObject root = JObject.Parse(body);
            JObject user = root["user"] as JObject ?? root;
            var stats = user["statistics"] as JObject;
            var preferences = user["preferences"] as JObject;
            var firebase = user["firebase_messaging"] as JObject;
            var payments = user["payment_accounts"] as JObject;
            var stripe = user["stripe_connect"] as JObject;
            int balanceCents = CSFloatListing.ReadInt(user["balance"]) ?? 0;
            int pendingCents = CSFloatListing.ReadInt(user["pending_balance"]) ?? 0;
            var account = new CSFloatAccountInformation
            {
                SteamID = user.Value<string>("steam_id") ?? "",
                Username = user.Value<string>("username") ?? "",
                Flags = CSFloatListing.ReadInt(user["flags"]) ?? 0,
                Avatar = user.Value<string>("avatar") ?? "",
                Email = user.Value<string>("email") ?? "",
                PhoneNumber = user.Value<string>("phone_number") ?? "",
                BalanceInCents = balanceCents,
                PendingBalanceInCents = pendingCents,
                BalanceInPence = ArbitrageMath.UsdCentsToPenceFloor(balanceCents, gbpPerUsd),
                PendingBalanceInPence = ArbitrageMath.UsdCentsToPenceFloor(pendingCents, gbpPerUsd),
                StallPublic = user.Value<bool?>("stall_public") ?? false,
                Away = user.Value<bool?>("away") ?? false,
                TradeToken = user.Value<string>("trade_token") ?? "",
                KnowYourCustomer = user.Value<string>("know_your_customer") ?? "",
                ObfuscatedID = user.Value<string>("obfuscated_id") ?? "",
                Online = user.Value<bool?>("online") ?? false,
                Fee = user.Value<double?>("fee") ?? 0.02,
                WithdrawFee = user.Value<double?>("withdraw_fee") ?? 0,
                Subscriptions = (user["subscriptions"] as JArray)?.Select(s => s.ToString()).ToList() ?? new List<string>(),
                Has2FA = user.Value<bool?>("has_2fa") ?? false,
                HasAPIKey = user.Value<bool?>("has_api_key") ?? false,
                PaymentAccounts = new PaymentAccounts
                {
                    StripeConnect = payments?.Value<string>("stripe_connect") ?? "",
                    StripeCustomer = payments?.Value<string>("stripe_customer") ?? "",
                },
                Statistics = new Statistics
                {
                    TotalSales = CSFloatListing.ReadInt(stats?["total_sales"]) ?? 0,
                    TotalPurchases = CSFloatListing.ReadInt(stats?["total_purchases"]) ?? 0,
                    MedianTradeTime = CSFloatListing.ReadInt(stats?["median_trade_time"]) ?? 0,
                    TotalAvoidedTrades = CSFloatListing.ReadInt(stats?["total_avoided_trades"]) ?? 0,
                    TotalFailedTrades = CSFloatListing.ReadInt(stats?["total_failed_trades"]) ?? 0,
                    TotalVerifiedTrades = CSFloatListing.ReadInt(stats?["total_verified_trades"]) ?? 0,
                    TotalTrades = CSFloatListing.ReadInt(stats?["total_trades"]) ?? 0,
                },
                Preferences = new Preferences
                {
                    OffersEnabled = preferences?.Value<bool?>("offers_enabled") ?? false,
                    MaxOfferDiscount = CSFloatListing.ReadInt(preferences?["max_offer_discount"]) ?? 0,
                },
                FirebaseMessaging = new FirebaseMessaging
                {
                    Platform = CSFloatListing.ReadInt(firebase?["platform"]) ?? 0,
                    LastUpdated = DateTime.TryParse(firebase?.Value<string>("last_updated"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var lu) ? lu : default,
                },
                StripeConnect = new StripeConnect { PayoutsEnabled = stripe?.Value<bool?>("payouts_enabled") ?? false },
            };
            account.BalanceInPounds = account.BalanceInPence / 100f;
            account.PendingBalanceInPounds = account.PendingBalanceInPence / 100f;
            if (DateTime.TryParse(user.Value<string>("extension_setup_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var setupAt))
                account.ExtensionSetupAt = setupAt;
            return account;
        }

        /// <summary>USD exchange rates keyed by lowercase ISO code ("gbp" = GBP per 1 USD). No API key needed.</summary>
        public static async Task<Dictionary<string, double>> GetExchangeRatesAsync(HttpClient http, CancellationToken ct = default)
        {
            using var response = await http.GetAsync(BaseUrl + "/meta/exchange-rates", ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"CSFloat exchange rates returned {(int)response.StatusCode}: {Truncate(body)}", null, response.StatusCode);
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (JObject.Parse(body)["data"] is JObject data)
            {
                foreach (var property in data.Properties())
                {
                    if (property.Value.Type is JTokenType.Float or JTokenType.Integer)
                    {
                        double rate = property.Value.Value<double>();
                        if (rate > 0) rates[property.Name] = rate;
                    }
                }
            }
            if (!rates.TryGetValue("gbp", out double gbp) || gbp <= 0) throw new FormatException("CSFloat exchange rates had no GBP rate.");
            return rates;
        }

        // ───────────────────────────── Transport ─────────────────────────────

        private async Task<string> SendAsync(string bucket, HttpMethod method, string url, string? jsonBody, CancellationToken ct)
        {
            if (!RateLimits.CanSpend(bucket))
                throw new CSFloatRateLimitedException(bucket, RateLimits.NextAvailableUtc(bucket));

            using var request = new HttpRequestMessage(method, url);
            if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, ct);
            Interlocked.Increment(ref sentRequests);
            RateLimits.Observe(bucket, response);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new CSFloatRateLimitedException(bucket, RateLimits.NextAvailableUtc(bucket));
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && LooksLikeAuthFailure(body))
                throw new CSFloatAuthException($"CSFloat rejected the API key on {bucket} ({(int)response.StatusCode}): {Truncate(body)}");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"CSFloat {bucket} returned {(int)response.StatusCode}: {Truncate(ExtractMessage(body))}", null, response.StatusCode);
            return body;
        }

        private static bool LooksLikeAuthFailure(string body)
        {
            // 403 is also used for "you need to be logged in" (no/invalid key) and for Cloudflare blocks;
            // only the former means the key is bad.
            string lower = body.ToLowerInvariant();
            return lower.Contains("logged in") || lower.Contains("api key") || lower.Contains("unauthorized") || lower.Contains("invalid token");
        }

        internal static string ExtractMessage(string body)
        {
            try
            {
                var token = JToken.Parse(body);
                string? message = token.Value<string>("message") ?? token.Value<string>("error");
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            catch { }
            return Truncate(body);
        }

        private static string Truncate(string text, int max = 300) =>
            string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";

        // ───────────────────────────── Account DTOs (persisted / served) ─────────────────────────────

        public class CSFloatAccountInformation
        {
            public string SteamID { get; set; } = "";
            public string Username { get; set; } = "";
            public int Flags { get; set; }
            public string Avatar { get; set; } = "";
            public string Email { get; set; } = "";
            public string PhoneNumber { get; set; } = "";
            public int BalanceInCents { get; set; }
            public int PendingBalanceInCents { get; set; }
            public int BalanceInPence { get; set; }
            public float BalanceInPounds { get; set; }
            public int PendingBalanceInPence { get; set; }
            public float PendingBalanceInPounds { get; set; }
            public bool StallPublic { get; set; }
            public bool Away { get; set; }
            public string TradeToken { get; set; } = "";
            public PaymentAccounts PaymentAccounts { get; set; } = new();
            public Statistics Statistics { get; set; } = new();
            public Preferences Preferences { get; set; } = new();
            public string KnowYourCustomer { get; set; } = "";
            public DateTime ExtensionSetupAt { get; set; }
            public FirebaseMessaging FirebaseMessaging { get; set; } = new();
            public StripeConnect StripeConnect { get; set; } = new();
            public string ObfuscatedID { get; set; } = "";
            public bool Online { get; set; }
            public double Fee { get; set; }
            public double WithdrawFee { get; set; }
            public List<string> Subscriptions { get; set; } = new();
            public bool Has2FA { get; set; }
            public bool HasAPIKey { get; set; }
        }

        public class PaymentAccounts
        {
            public string StripeConnect { get; set; } = "";
            public string StripeCustomer { get; set; } = "";
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
