using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using System.Net;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class CSFloatClientTests
{
    [Fact]
    public void ParsesAListingInTheLiveShape()
    {
        var listing = CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing("1026786536324205863", "AK-47 | Redline (Field-Tested)", 4500, 4210, 4344,
            refQty: 2719, floatValue: 0.2285, sellerTrades: 100, sellerFailed: 10)));
        Assert.Equal("1026786536324205863", listing.Id);
        Assert.Equal(4500, listing.PriceCents);
        Assert.Equal(4210, listing.BasePriceCents);
        Assert.Equal(4344, listing.PredictedPriceCents);
        Assert.Equal(2719, listing.ReferenceQuantity);
        Assert.Equal(0.2285, listing.FloatValue);
        Assert.Equal("AK-47 | Redline (Field-Tested)", listing.MarketHashName);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 23, 7, DateTimeKind.Utc), listing.CreatedAtUtc.AddTicks(-(listing.CreatedAtUtc.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(0.10, listing.SellerFailureRate, 3);
        Assert.Equal("https://csfloat.com/item/1026786536324205863", listing.ListingUrl);
        Assert.StartsWith("https://community.cloudflare.steamstatic.com/economy/image/", listing.ImageUrl);
    }

    [Fact]
    public void ListingWithoutReferenceStillParses()
    {
        var listing = CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing("1", "Sticker | WARNING", 190, null, null, floatValue: null)));
        Assert.Null(listing.BasePriceCents);
        Assert.Null(listing.FloatValue);
    }

    [Fact]
    public void AMalformedListingIsCountedNotFatal()
    {
        string page = "{\"data\":[" + CS2Fixtures.Listing("1", "A", 100, 100, 100) + ",{\"id\":\"2\",\"price\":5},{\"price\":9}],\"cursor\":\"abc\"}";
        var parsed = CSFloatWrapper.ParseListingPage(page);
        Assert.Single(parsed.Listings);
        Assert.Equal(2, parsed.ParseFailures);
        Assert.Equal("abc", parsed.Cursor);
    }

    [Fact]
    public void QueryStringSendsSortByAndEscapesNames()
    {
        // The old client accepted a sort argument and never put it in the URL.
        var q = new CSFloatWrapper.ListingQuery { SortBy = "most_recent", MinPriceCents = 100, MaxPriceCents = 3000, Categories = "1,2", MarketHashName = "StatTrak™ AK-47 | Redline (Field-Tested)" };
        string qs = q.ToQueryString();
        Assert.Contains("sort_by=most_recent", qs);
        Assert.Contains("min_price=100", qs);
        Assert.Contains("max_price=3000", qs);
        Assert.Contains("category=1%2C2", qs);
        Assert.Contains("type=buy_now", qs);
        Assert.Contains("market_hash_name=" + Uri.EscapeDataString("StatTrak™ AK-47 | Redline (Field-Tested)"), qs);
        Assert.DoesNotContain("page=", qs);
        Assert.DoesNotContain("sort_by", new CSFloatWrapper.ListingQuery { SortBy = "best_deal" }.ToQueryString());
        Assert.Contains("limit=50", new CSFloatWrapper.ListingQuery { Limit = 500 }.ToQueryString());
    }

    [Fact]
    public void ParsesThePriceList()
    {
        var list = CSFloatWrapper.ParsePriceList("[{\"market_hash_name\":\"Prisma 2 Case\",\"quantity\":27740,\"min_price\":102},{\"market_hash_name\":\"Bad\",\"min_price\":0},{\"quantity\":5}]");
        var entry = Assert.Single(list);
        Assert.Equal("Prisma 2 Case", entry.MarketHashName);
        Assert.Equal(102, entry.MinPriceCents);
        Assert.Equal(27740, entry.Quantity);
    }

    [Fact]
    public void ParsesAccountBalancesInBothCurrencies()
    {
        var account = CSFloatWrapper.ParseAccountInformation(CS2Fixtures.Account, 0.755163);
        Assert.Equal(9846, account.BalanceInCents);
        Assert.Equal(7435, account.BalanceInPence);
        Assert.Equal(74.35f, account.BalanceInPounds, 2);
        Assert.Equal(120, account.PendingBalanceInCents);
        Assert.Equal(0.02, account.Fee);
        Assert.Equal(2273, account.Statistics.TotalTrades);
    }

    [Fact]
    public void ParsesTheInventory()
    {
        var items = CSFloatWrapper.ParseInventory("[{\"asset_id\":\"52593528294\",\"market_hash_name\":\"MP9 | Pine (Minimal Wear)\",\"tradable\":1,\"float_value\":0.12},{\"asset_id\":\"2\",\"market_hash_name\":\"X\",\"tradable\":0}]");
        Assert.Equal(2, items.Count);
        Assert.True(items[0].Tradable);
        Assert.Equal(0.12, items[0].FloatValue);
        Assert.False(items[1].Tradable);
    }

    [Fact]
    public async Task BuySendsTheContractAndPriceAndReportsRejectionsWithoutThrowing()
    {
        var handler = new FakeHttpHandler((request, body) =>
            body!.Contains("\"sold\"") ? FakeHttpHandler.Json("{}") : FakeHttpHandler.Json("{\"code\":4,\"message\":\"listing is no longer available\"}", HttpStatusCode.BadRequest));
        var client = new CSFloatWrapper(null, new HttpClient(handler));
        var result = await client.BuyListingAsync("123", 4500);
        Assert.False(result.Success);
        Assert.Equal("listing is no longer available", result.Message);
        var (method, url, payload) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://csfloat.com/api/v1/listings/buy", url);
        var json = JObject.Parse(payload!);
        Assert.Equal(4500, (int)json["total_price"]!);
        Assert.Equal("123", (string)json["contract_ids"]![0]!);
    }

    [Fact]
    public async Task RateLimitedSearchThrowsATypedExceptionAndBlocksFurtherSpend()
    {
        long reset = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{\"message\":\"slow down\"}", HttpStatusCode.TooManyRequests,
            new() { ["x-ratelimit-limit"] = "200", ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset.ToString() }));
        var client = new CSFloatWrapper(null, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<CSFloatRateLimitedException>(() => client.SearchListingsAsync(new CSFloatWrapper.ListingQuery()));
        Assert.True(ex.RetryAtUtc > DateTime.UtcNow.AddMinutes(19));
        await Assert.ThrowsAsync<CSFloatRateLimitedException>(() => client.SearchListingsAsync(new CSFloatWrapper.ListingQuery()));
        Assert.Single(handler.Requests); // the second call never left the process
    }

    [Fact]
    public async Task ServerErrorsThrowHttpRequestExceptionNotProcessExit()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{\"message\":\"internal\"}", HttpStatusCode.InternalServerError));
        var client = new CSFloatWrapper(null, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchListingsAsync(new CSFloatWrapper.ListingQuery()));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public void ParsesRecentSalesWithTimeOnMarketAndValueAtSaleTime()
    {
        // Trimmed from a live /history/{name}/sales response (Oct 2026).
        string body = "[{\"id\":\"1021205477226315826\",\"created_at\":\"2026-09-18T23:45:58.852003Z\",\"type\":\"buy_now\",\"price\":383,\"state\":\"sold\"," +
                      "\"reference\":{\"base_price\":377,\"float_factor\":0.998091,\"predicted_price\":376,\"quantity\":10132,\"last_updated\":\"2026-10-04T17:57:14.496848Z\",\"sticker_overpay\":{}}," +
                      "\"item\":{\"asset_id\":\"34531654000\",\"float_value\":0.3400673270225525,\"market_hash_name\":\"AK-47 | Slate (Field-Tested)\",\"stickers\":[{},{}]},\"sold_at\":\"2026-10-04T21:15:29.919442Z\"}," +
                      "{\"id\":\"2\",\"price\":0,\"sold_at\":\"2026-10-04T21:00:00Z\"},{\"id\":\"3\",\"price\":400}]";
        var sale = Assert.Single(CSFloatWrapper.ParseRecentSales(body));
        Assert.Equal(383, sale.PriceCents);
        Assert.Equal(376, sale.PredictedPriceCents);
        Assert.Equal(383 / 376.0, sale.ValueRatio!.Value, 9);
        Assert.InRange(sale.DaysOnMarket, 15.8, 15.9);
        Assert.Equal(2, sale.StickerCount);
        Assert.Equal(0.3400673270225525, sale.FloatValue);
    }

    [Fact]
    public void TheDailyGraphKeepsOnlyRealDays()
    {
        var graph = CSFloatWrapper.ParseSalesGraph("[{\"count\":62,\"day\":\"2026-10-04T00:00:00Z\",\"avg_price\":476.2},{\"count\":84,\"day\":\"2026-10-03T00:00:00Z\",\"avg_price\":541.6},{\"count\":3}]");
        Assert.Equal(2, graph.Count);
        Assert.Equal(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), graph[0].DayUtc);
        Assert.Equal(62, graph[0].Count);
    }

    [Fact]
    public async Task RepricingAndDelistingUseTheBulkEndpoints()
    {
        var handler = new FakeHttpHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("bulk-modify")
            ? FakeHttpHandler.Json("{\"data\":[{\"id\":\"777\",\"price\":412,\"state\":\"listed\"}]}")
            : FakeHttpHandler.Json("{\"message\":\"delisted\"}"));
        var client = new CSFloatWrapper(null, new HttpClient(handler));

        Assert.Equal(412, await client.UpdateListingPriceAsync("777", 412));
        await client.DelistAsync("777");

        var (method, url, body) = handler.Requests[0];
        Assert.Equal(HttpMethod.Patch, method);
        Assert.Equal("https://csfloat.com/api/v1/listings/bulk-modify", url);
        var modification = JObject.Parse(body!)["modifications"]![0]!;
        Assert.Equal("777", (string)modification["contract_id"]!);
        Assert.Equal(412, (int)modification["price"]!);

        (method, url, body) = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, method);
        Assert.Equal("https://csfloat.com/api/v1/listings/bulk-delist", url);
        Assert.Equal("777", (string)JObject.Parse(body!)["contract_ids"]![0]!);
    }

    [Fact]
    public async Task PerSaleHistoryHasItsOwnRateWindow()
    {
        // Measured: /sales and /graph report different remaining counts and resets, i.e. separate windows.
        long reset = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("[]", headers: new() { ["x-ratelimit-limit"] = "500", ["x-ratelimit-remaining"] = "3", ["x-ratelimit-reset"] = reset.ToString() }));
        var client = new CSFloatWrapper(null, new HttpClient(handler));
        await client.GetRecentSalesAsync("Recoil Case");
        Assert.Equal(3, client.RateLimits.Get(CSFloatWrapper.HistorySalesBucket).Remaining);
        Assert.True(client.RateLimits.CanSpend(CSFloatWrapper.HistoryBucket, 100));
        Assert.EndsWith("/history/Recoil%20Case/sales", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task ExchangeRatesExposeGbpPerUsd()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{\"data\":{\"gbp\":0.755163,\"eur\":0.888662,\"usd\":1}}"));
        var rates = await CSFloatWrapper.GetExchangeRatesAsync(new HttpClient(handler));
        Assert.Equal(0.755163, rates["gbp"]);
        Assert.Equal(0.888662, rates["EUR"]);
    }
}
