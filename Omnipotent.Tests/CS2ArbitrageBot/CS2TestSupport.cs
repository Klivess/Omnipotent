using System.Net;
using System.Text;

namespace Omnipotent.Tests.CS2ArbitrageBot;

/// <summary>Records requests and answers them from a routing function — no network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> route;
    public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

    public FakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> route)
    {
        this.route = route;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
        return route(request, body);
    }

    public int Count(Func<string, bool> urlMatches)
    {
        lock (Requests) return Requests.Count(r => urlMatches(r.Url));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK, Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (headers != null)
            foreach (var (k, v) in headers) response.Headers.TryAddWithoutValidation(k, v);
        return response;
    }
}

internal static class CS2Fixtures
{
    /// <summary>A CSFloat listing in the live API's shape (Oct 2026); seller ids are fake.</summary>
    public static string Listing(string id, string name, int priceCents, int? basePrice, int? predicted = null, int refQty = 500,
        double? floatValue = 0.15, bool sellerAway = false, int sellerTrades = 300, int sellerFailed = 0, int medianTradeSeconds = 600,
        string createdAt = "2026-10-04T09:23:07.031914Z", string state = "listed")
    {
        string reference = basePrice == null ? "null" :
            $"{{\"base_price\":{basePrice},\"float_factor\":0.97,\"predicted_price\":{predicted ?? basePrice},\"quantity\":{refQty},\"last_updated\":\"2026-10-04T09:23:07Z\"}}";
        string floatJson = floatValue == null ? "" : $"\"float_value\":{floatValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},";
        return $@"{{""id"":""{id}"",""created_at"":""{createdAt}"",""type"":""buy_now"",""price"":{priceCents},""state"":""{state}"",
""seller"":{{""avatar"":"""",""away"":{(sellerAway ? "true" : "false")},""flags"":0,""online"":true,""stall_public"":true,
""statistics"":{{""median_trade_time"":{medianTradeSeconds},""total_avoided_trades"":0,""total_failed_trades"":{sellerFailed},""total_trades"":{sellerTrades},""total_verified_trades"":{sellerTrades - sellerFailed}}},
""steam_id"":""76561190000000001"",""username"":""seller""}},
""reference"":{reference},
""item"":{{""asset_id"":""5390742374{id.Length}"",""def_index"":7,""paint_index"":44,""paint_seed"":661,{floatJson}""icon_url"":""i0CoZ81Ui0m"",
""is_stattrak"":false,""is_souvenir"":false,""rarity"":5,""quality"":4,""market_hash_name"":""{name}"",""tradable"":0,
""is_commodity"":false,""type"":""skin"",""item_name"":""{name}"",""wear_name"":""Field-Tested"",""d_param"":""123""}},
""is_seller"":false,""watchers"":0}}";
    }

    public static string ListingPage(params string[] listings) => "{\"data\":[" + string.Join(",", listings) + "],\"cursor\":\"next-cursor\"}";

    /// <summary>The new Steam SPA orderbook query-action response.</summary>
    public static string SteamBook(int maxBuy, int minSell, int buyOrders = 5000, int sellOrders = 300, int currency = 2, string? compactBuys = null, string? compactSells = null) =>
        $"{{\"data\":{{\"success\":true,\"data\":{{\"amtMaxBuyOrder\":{maxBuy},\"amtMinSellOrder\":{minSell},\"eCurrency\":{currency},\"cBuyOrders\":{buyOrders},\"cSellOrders\":{sellOrders}," +
        $"\"rgCompactBuyOrders\":[{compactBuys ?? $"{maxBuy},10,{maxBuy - 1},40"}],\"rgCompactSellOrders\":[{compactSells ?? $"{minSell},5,{minSell + 1},20"}]}}}}}}";

    /// <summary>CSFloat's daily sales graph in the live shape: only days with sales appear, newest first.</summary>
    public static string SalesGraph(DateTime todayUtc, Func<int, int> salesDaysAgo, double averagePriceCents, int days = 60) =>
        "[" + string.Join(",", Enumerable.Range(0, days).Select(d => (Day: d, Count: salesDaysAgo(d))).Where(x => x.Count > 0)
            .Select(x => $"{{\"count\":{x.Count},\"day\":\"{todayUtc.Date.AddDays(-x.Day):yyyy-MM-ddT00:00:00Z}\",\"avg_price\":{averagePriceCents.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")) + "]";

    /// <summary>CSFloat's per-sale history in the live shape (newest first); each sale's reference is its value at sale time.</summary>
    public static string RecentSales(DateTime nowUtc, IEnumerable<(int Price, int Value, double HoursListed, double SoldHoursAgo)> sales, Func<int, int>? stickersOfSale = null) =>
        "[" + string.Join(",", sales.OrderBy(s => s.SoldHoursAgo).Select((s, i) =>
        {
            DateTime sold = nowUtc.AddHours(-s.SoldHoursAgo), listed = sold.AddHours(-s.HoursListed);
            string stickers = string.Join(",", Enumerable.Repeat("{\"slot\":0}", stickersOfSale?.Invoke(i) ?? 0));
            return $"{{\"id\":\"s{i}\",\"created_at\":\"{listed:yyyy-MM-ddTHH:mm:ss.ffffffZ}\",\"type\":\"buy_now\",\"price\":{s.Price},\"state\":\"sold\"," +
                   $"\"reference\":{{\"base_price\":{s.Value},\"float_factor\":1,\"predicted_price\":{s.Value},\"quantity\":500,\"last_updated\":\"{sold:yyyy-MM-ddTHH:mm:ssZ}\"}}," +
                   $"\"item\":{{\"asset_id\":\"1{i}\",\"market_hash_name\":\"x\",\"float_value\":0.2,\"stickers\":[{stickers}]}},\"sold_at\":\"{sold:yyyy-MM-ddTHH:mm:ss.ffffffZ}\"}}";
        })) + "]";

    /// <summary>Steam's QueryPriceHistory response: one point per day at noon, price(daysAgo) in GBP.</summary>
    public static string SteamHistory(DateTime nowUtc, Func<int, double> priceDaysAgo, int days = 120, int purchasesPerDay = 40) =>
        "{\"data\":{\"ecurrency\":2,\"prices\":[" + string.Join(",", Enumerable.Range(0, days).Reverse().Select(d =>
            $"{{\"time\":{new DateTimeOffset(nowUtc.Date.AddDays(-d).AddHours(12), TimeSpan.Zero).ToUnixTimeSeconds()},\"price_median\":{priceDaysAgo(d).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"purchases\":{purchasesPerDay}}}")) + "]}}";

    public const string Account ="{\"user\":{\"steam_id\":\"76561190000000002\",\"username\":\"bot\",\"flags\":48,\"avatar\":\"\",\"email\":\"x@example.com\",\"phone_number\":\"\",\"balance\":9846,\"pending_balance\":120," +
        "\"stall_public\":true,\"away\":false,\"trade_token\":\"t\",\"payment_accounts\":{\"stripe_connect\":\"\",\"stripe_customer\":\"\"}," +
        "\"statistics\":{\"total_sales\":51465,\"total_purchases\":50452,\"median_trade_time\":68,\"total_avoided_trades\":1,\"total_failed_trades\":0,\"total_verified_trades\":2273,\"total_trades\":2273}," +
        "\"preferences\":{\"offers_enabled\":true,\"max_offer_discount\":10},\"know_your_customer\":\"verified\",\"extension_setup_at\":\"2024-01-01T00:00:00Z\"," +
        "\"firebase_messaging\":{\"platform\":1,\"last_updated\":\"2025-01-01T00:00:00Z\"},\"stripe_connect\":{\"payouts_enabled\":true},\"obfuscated_id\":\"1\",\"online\":true," +
        "\"fee\":0.02,\"withdraw_fee\":0.025,\"subscriptions\":[],\"has_2fa\":true,\"has_api_key\":true}}";
}
