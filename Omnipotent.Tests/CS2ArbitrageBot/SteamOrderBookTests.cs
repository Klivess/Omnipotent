using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class SteamOrderBookTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
    private static int? Gbp(int minor, int currency) => SteamCurrency.ToGbpPence(minor, currency, null);

    // Trimmed from a live response (AK-47 | Slate (Minimal Wear), Oct 2026).
    private const string LiveShape = "{\"data\":{\"success\":true,\"data\":{\"amtMaxBuyOrder\":579,\"amtMinSellOrder\":594,\"eCurrency\":2," +
        "\"cBuyOrders\":158839,\"cSellOrders\":1539,\"rgCompactBuyOrders\":[579,2,577,50,572,33],\"rgCompactSellOrders\":[594,3,595,10,599]}}}";

    [Fact]
    public void ParsesTheNewSpaOrderbookResponse()
    {
        var book = SteamOrderBook.ParseOrderbookResponse(LiveShape, "AK-47 | Slate (Minimal Wear)", Gbp, Now)!;
        Assert.Equal(579, book.HighestBuyOrderPence);
        Assert.Equal(594, book.LowestSellOrderPence);
        Assert.Equal(158839, book.BuyOrderCount);
        Assert.Equal(1539, book.SellOrderCount);
        Assert.Equal(new[] { new SteamPriceLevel(579, 2), new SteamPriceLevel(577, 50), new SteamPriceLevel(572, 33) }, book.BuyLevels);
        // The trailing unpaired value is dropped, as the SPA does.
        Assert.Equal(new[] { new SteamPriceLevel(594, 3), new SteamPriceLevel(595, 10) }, book.SellLevels);
        Assert.Equal("orderbook", book.Source);
        Assert.Equal(Now, book.FetchedAtUtc);
    }

    [Fact]
    public void DepthPriceLooksPastALoneHighOrder()
    {
        var book = SteamOrderBook.ParseOrderbookResponse(LiveShape, "x", Gbp, Now)!;
        Assert.Equal(579, book.PriceAtBuyDepth(1));
        Assert.Equal(577, book.PriceAtBuyDepth(3));
        Assert.Equal(572, book.PriceAtBuyDepth(60));
        // Beyond the truncated levels but within the order count: deepest level shown.
        Assert.Equal(572, book.PriceAtBuyDepth(1000));
    }

    [Fact]
    public void DepthPriceIsZeroWhenTheBookIsTooThin()
    {
        var book = SteamOrderBook.ParseOrderbookResponse(
            "{\"amtMaxBuyOrder\":100,\"amtMinSellOrder\":120,\"eCurrency\":2,\"cBuyOrders\":2,\"cSellOrders\":5,\"rgCompactBuyOrders\":[100,2],\"rgCompactSellOrders\":[120,5]}",
            "x", Gbp, Now)!;
        Assert.Equal(0, book.PriceAtBuyDepth(3));
    }

    [Fact]
    public void CostToBuyUnitsWalksTheSellSide()
    {
        var book = SteamOrderBook.ParseOrderbookResponse(LiveShape, "x", Gbp, Now)!;
        Assert.Equal(3 * 594 + 2 * 595, book.CostToBuyUnits(5));
        Assert.Equal(-1, book.CostToBuyUnits(100)); // not enough shown
    }

    [Fact]
    public void FailedResponsesParseToNull()
    {
        Assert.Null(SteamOrderBook.ParseOrderbookResponse("{\"data\":{\"success\":false}}", "x", Gbp, Now));
        Assert.Null(SteamOrderBook.ParseOrderbookResponse("", "x", Gbp, Now));
    }

    [Fact]
    public void NonGbpBooksAreConvertedOrRejected()
    {
        string usd = "{\"amtMaxBuyOrder\":1000,\"amtMinSellOrder\":1100,\"eCurrency\":1,\"cBuyOrders\":10,\"cSellOrders\":10,\"rgCompactBuyOrders\":[1000,10],\"rgCompactSellOrders\":[1100,10]}";
        var rates = new Dictionary<string, double> { ["usd"] = 1, ["gbp"] = 0.75 };
        var converted = SteamOrderBook.ParseOrderbookResponse(usd, "x", (m, c) => SteamCurrency.ToGbpPence(m, c, rates), Now)!;
        Assert.Equal(750, converted.HighestBuyOrderPence);
        Assert.Equal(825, converted.LowestSellOrderPence);
        Assert.Equal(750, converted.BuyLevels[0].PricePence);
        Assert.Null(SteamOrderBook.ParseOrderbookResponse(usd, "x", Gbp, Now)); // no rates → unusable, not "£0"
    }

    [Fact]
    public void ParsesTheLegacyHistogramAndDeAccumulatesItsGraphs()
    {
        string legacy = "{\"success\":1,\"highest_buy_order\":\"579\",\"lowest_sell_order\":\"594\"," +
            "\"buy_order_graph\":[[5.79,2,\"2 buy orders at £5.79 or higher\"],[5.77,52,\"\"]]," +
            "\"sell_order_graph\":[[5.94,3,\"\"],[5.95,13,\"\"]]," +
            "\"buy_order_summary\":\"<span class=\\\"market_commodity_orders_header_promote\\\">158839</span> requests to buy at <span class=\\\"market_commodity_orders_header_promote\\\">£5.79</span> or lower\"," +
            "\"sell_order_summary\":\"<span class=\\\"market_commodity_orders_header_promote\\\">1539</span> for sale\"}";
        var book = SteamOrderBook.ParseHistogramResponse(legacy, "x", SteamCurrency.GBP, Gbp, Now)!;
        Assert.Equal(579, book.HighestBuyOrderPence);
        Assert.Equal(594, book.LowestSellOrderPence);
        Assert.Equal(new[] { new SteamPriceLevel(579, 2), new SteamPriceLevel(577, 50) }, book.BuyLevels);
        Assert.Equal(new[] { new SteamPriceLevel(594, 3), new SteamPriceLevel(595, 10) }, book.SellLevels);
        Assert.Equal(158839, book.BuyOrderCount);
        Assert.Equal(1539, book.SellOrderCount);
        Assert.Equal("histogram", book.Source);
    }

    [Fact]
    public void PriceHistoryParsesTheQueryActionShape()
    {
        string json = "{\"data\":{\"ecurrency\":2,\"prices\":[{\"time\":1791093600,\"price_median\":1.1122,\"purchases\":4166},{\"time\":0,\"price_median\":1,\"purchases\":1}]}}";
        var points = SteamMarketClient.ParsePriceHistory(json, Gbp);
        var point = Assert.Single(points);
        Assert.Equal(1.11, point.MedianPriceGbp, 2);
        Assert.Equal(4166, point.Purchases);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791093600).UtcDateTime, point.TimeUtc);
    }

    [Fact]
    public async Task MarketClientUsesTheQueryActionHeaderAndCachesBooks()
    {
        var handler = new FakeHttpHandler((request, _) =>
        {
            Assert.True(request.Headers.TryGetValues("x-valve-request-type", out var v) && v.Single() == "queryAction");
            return FakeHttpHandler.Json(CS2Fixtures.SteamBook(500, 520));
        });
        using var client = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: handler);
        var first = await client.GetOrderBookAsync("AK-47 | Slate (Minimal Wear)", TimeSpan.FromMinutes(5), Omnipotent.Services.CS2ArbitrageBot.Engine.RequestPriority.High);
        var second = await client.GetOrderBookAsync("AK-47 | Slate (Minimal Wear)", TimeSpan.FromMinutes(5), Omnipotent.Services.CS2ArbitrageBot.Engine.RequestPriority.High);
        Assert.Equal(500, first!.HighestBuyOrderPence);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
        Assert.Contains("/market/orderbook?q=Load&qp=", handler.Requests[0].Url);
        Assert.Contains(Uri.EscapeDataString("AK-47 | Slate (Minimal Wear)"), handler.Requests[0].Url);
    }

    [Fact]
    public async Task MarketClientFallsBackToTheHistogramAndTreatsRedirectsAsFailures()
    {
        var handler = new FakeHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("orderbook"))
            {
                var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://steamcommunity.com/market/listings/730/G1807208B083004");
                return redirect;
            }
            return FakeHttpHandler.Json("{\"success\":1,\"highest_buy_order\":\"400\",\"lowest_sell_order\":\"410\",\"buy_order_graph\":[[4.0,30,\"\"]],\"sell_order_graph\":[[4.1,5,\"\"]]}");
        });
        using var client = new SteamMarketClient(() => null, name => 176241022, TimeSpan.FromMilliseconds(1), handler: handler);
        var book = await client.GetOrderBookAsync("AK-47 | Slate (Minimal Wear)", TimeSpan.FromMinutes(5), Omnipotent.Services.CS2ArbitrageBot.Engine.RequestPriority.High);
        Assert.Equal(400, book!.HighestBuyOrderPence);
        Assert.Equal("histogram", book.Source);
        Assert.Equal(1, client.FallbackUses);
        Assert.Contains("redirected", client.LastError);
    }

    [Fact]
    public async Task MarketClientBacksOffOn429AndServesTheStaleBook()
    {
        int call = 0;
        var handler = new FakeHttpHandler((_, _) => ++call == 1
            ? FakeHttpHandler.Json(CS2Fixtures.SteamBook(300, 310))
            : new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));
        using var client = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: handler);
        var fresh = await client.GetOrderBookAsync("x", TimeSpan.FromMinutes(5), Omnipotent.Services.CS2ArbitrageBot.Engine.RequestPriority.High);
        var stale = await client.GetOrderBookAsync("x", TimeSpan.Zero, Omnipotent.Services.CS2ArbitrageBot.Engine.RequestPriority.High);
        Assert.Same(fresh, stale);
        Assert.True(client.Pacer.PausedUntilUtc > DateTime.UtcNow.AddSeconds(20));
        Assert.Equal(1, client.Pacer.Throttles);
    }
}
