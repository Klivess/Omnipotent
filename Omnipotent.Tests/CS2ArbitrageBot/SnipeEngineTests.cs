using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Net;
using System.Web;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Tests.CS2ArbitrageBot;

/// <summary>An in-memory host: real engine, real clients, fake HTTP on both markets.</summary>
internal sealed class FakeSnipeHost : ISnipeHost
{
    public FakeSnipeHost(FakeHttpHandler csfloat, FakeHttpHandler steam, string? referenceJson = null)
    {
        CSFloat = new CSFloatWrapper(null, new HttpClient(csfloat));
        SteamMarket = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: steam);
        ReferencePrices = new SteamReferencePrices();
        if (referenceJson != null)
            Assert.True(ReferencePrices.RefreshAsync(new HttpClient(new FakeHttpHandler((_, _) => FakeHttpHandler.Json(referenceJson))), default, minimumItems: 1).GetAwaiter().GetResult());
        Analytics = new Scanalytics(null!);
    }

    public CSFloatWrapper CSFloat { get; }
    public SteamMarketClient SteamMarket { get; }
    public SteamReferencePrices ReferencePrices { get; }
    public Scanalytics Analytics { get; }
    public EngineSettings Settings { get; set; } = new() { Evaluation = new EvaluationSettings { MinimumPriceCents = 1 } };
    public double GbpPerUsd { get; set; } = 0.75;
    public double ConversionCoefficient { get; set; } = 0.9;
    public int? BalanceCents { get; set; } = 100_000;
    public Dictionary<string, int> Holdings { get; } = new();
    public int SpentTodayPence { get; set; }
    public double TargetConversionVolumePence => 2500;
    public List<(CSFloatListing Listing, OpportunityEvaluation Evaluation)> Purchased { get; } = new();
    public List<(CSFloatListing Listing, string Why)> NotBought { get; } = new();
    public List<string> Errors { get; } = new();
    public ConversionModelSnapshot? Model { get; private set; }
    public LiquidityPlan? Plan { get; private set; }

    public Task RefreshBalanceAsync(bool force, CancellationToken ct) => Task.CompletedTask;
    public void DebitBalance(int cents) => BalanceCents -= cents;
    public int OpenPositionsFor(string marketHashName) => Holdings.TryGetValue(marketHashName, out int n) ? n : 0;

    public Task OnPurchasedAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book)
    {
        Purchased.Add((listing, evaluation));
        Holdings[listing.MarketHashName] = OpenPositionsFor(listing.MarketHashName) + 1;
        return Task.CompletedTask;
    }

    public Task OnOpportunityNotBoughtAsync(CSFloatListing listing, OpportunityEvaluation evaluation, string why)
    {
        NotBought.Add((listing, why));
        return Task.CompletedTask;
    }

    public Task OnConversionModelComputedAsync(ConversionModelSnapshot snapshot, LiquidityPlan plan)
    {
        Model = snapshot;
        Plan = plan;
        return Task.CompletedTask;
    }

    public void Log(string message) { }
    public void LogError(Exception? ex, string message) => Errors.Add(message + " " + ex?.Message);
}

public class SnipeEngineTests
{
    private static CSFloatListing L(string id, string name, int price, int? basePrice, int refQty = 500) =>
        CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing(id, name, price, basePrice, null, refQty)));

    private static string QpName(HttpRequestMessage request) =>
        (string)JArray.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["qp"]!)[1]!;

    private static FakeHttpHandler Steam(Dictionary<string, (int Buy, int Sell)> books) => new((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/market/orderbook" && books.TryGetValue(QpName(request), out var b))
            return FakeHttpHandler.Json(CS2Fixtures.SteamBook(b.Buy, b.Sell));
        if (request.RequestUri.AbsolutePath == "/market/actions")
            return FakeHttpHandler.Json("{\"data\":{\"ecurrency\":2,\"prices\":[{\"time\":1791093600,\"price_median\":1.1,\"purchases\":400}]}}");
        return FakeHttpHandler.Json("{\"data\":{\"success\":false}}");
    });

    private static FakeHttpHandler CSFloat(Func<HttpRequestMessage, string?, HttpResponseMessage?>? extra = null, bool buySucceeds = true) => new((request, body) =>
    {
        var custom = extra?.Invoke(request, body);
        if (custom != null) return custom;
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/listings/buy")
            return buySucceeds ? FakeHttpHandler.Json("{\"message\":\"purchased\"}") : FakeHttpHandler.Json("{\"message\":\"listing already sold\"}", HttpStatusCode.BadRequest);
        return FakeHttpHandler.Json("{\"message\":\"unexpected\"}", HttpStatusCode.NotFound);
    });

    private static List<(string Id, int Price)> Buys(FakeHttpHandler csfloat) => csfloat.Requests
        .Where(r => r.Method == HttpMethod.Post && r.Url.EndsWith("/listings/buy"))
        .Select(r => { var j = JObject.Parse(r.Body!); return ((string)j["contract_ids"]![0]!, (int)j["total_price"]!); })
        .ToList();

    [Fact]
    public async Task BuysAProfitableRelistWithoutSpendingASteamRequestWhenBulkPricesRuleSteamOut()
    {
        var csfloat = CSFloat();
        var steam = Steam(new());
        // Bulk feed: Steam sells this for ~$9, far too low for the Steam exit at a $10 cost.
        var host = new FakeSnipeHost(csfloat, steam, "{\"AK-47 | Redline (Field-Tested)\":{\"last_24h\":9.0,\"last_7d\":9.0,\"last_30d\":9.0,\"last_90d\":9.0}}");
        var engine = new SnipeEngine(host);
        var cycle = new ScanCycleSummary();
        await engine.ProcessListingsAsync(new[] { L("1", "AK-47 | Redline (Field-Tested)", 1000, 1500) }, "feed", cycle, CancellationToken.None);

        // No order book was needed; the only Steam call is the pre-purchase price-trend check.
        Assert.DoesNotContain(steam.Requests, r => r.Url.Contains("/market/orderbook"));
        Assert.Single(steam.Requests, r => r.Url.Contains("QueryPriceHistory"));
        Assert.Equal(1, cycle.Prefiltered);
        Assert.Equal(new[] { ("1", 1000) }, Buys(csfloat));
        var (_, evaluation) = Assert.Single(host.Purchased);
        Assert.Equal(ExitRoute.CSFloatRelist, evaluation.BestRoute);
        Assert.Equal(99_000, host.BalanceCents);
        Assert.Equal(1, cycle.Purchased);
    }

    [Fact]
    public async Task LooksUpSteamOnlyWhenItCouldPayAndBuysTheSteamExit()
    {
        var csfloat = CSFloat();
        var steam = Steam(new() { ["Good"] = (1200, 1250), ["Bad"] = (900, 950) });
        var host = new FakeSnipeHost(csfloat, steam);
        var engine = new SnipeEngine(host);
        var cycle = new ScanCycleSummary();
        await engine.ProcessListingsAsync(new[] { L("b", "Bad", 1000, null), L("g", "Good", 1000, null) }, "feed", cycle, CancellationToken.None);

        Assert.Equal(2, cycle.SteamLookups);
        Assert.Equal(new[] { ("g", 1000) }, Buys(csfloat));
        Assert.Equal(ExitRoute.SteamMarket, host.Purchased.Single().Evaluation.BestRoute);
        Assert.Equal(2, host.Analytics.SnapshotAggregate().TotalEvaluated);
    }

    [Fact]
    public async Task PurchaseGuardsBlockWithoutCallingCSFloat()
    {
        var csfloat = CSFloat();
        var host = new FakeSnipeHost(csfloat, Steam(new()));
        var engine = new SnipeEngine(host);
        var listing = L("1", "AK-47 | Redline (Field-Tested)", 1000, 1500);
        var evaluation = OpportunityEvaluator.Evaluate(listing, null, 0.9, 0.75, host.Settings.Evaluation, DateTime.UtcNow, "test");
        Assert.True(evaluation.ShouldBuy);

        host.Settings.PurchasingEnabled = false;
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("disabled", host.NotBought[^1].Why);

        host.Settings.PurchasingEnabled = true;
        host.Holdings[listing.MarketHashName] = 3;
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("already holding", host.NotBought[^1].Why);

        host.Holdings.Clear();
        host.BalanceCents = 500;
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("insufficient", host.NotBought[^1].Why);

        host.BalanceCents = 2000; // 40% cap = $8 < $10
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("per-item cap", host.NotBought[^1].Why);

        host.BalanceCents = 100_000;
        host.Settings.DailySpendLimitPence = 1000;
        host.SpentTodayPence = 900;
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("daily spend limit", host.NotBought[^1].Why);

        Assert.Empty(Buys(csfloat));
    }

    [Fact]
    public async Task ARejectedPurchaseIsReportedAndCounted()
    {
        var csfloat = CSFloat(buySucceeds: false);
        var host = new FakeSnipeHost(csfloat, Steam(new()));
        var engine = new SnipeEngine(host);
        var listing = L("1", "AK-47 | Redline (Field-Tested)", 1000, 1500);
        var evaluation = OpportunityEvaluator.Evaluate(listing, null, 0.9, 0.75, host.Settings.Evaluation, DateTime.UtcNow, "test");
        Assert.False(await engine.TryBuyAsync(listing, evaluation, null, CancellationToken.None));
        Assert.Contains("listing already sold", host.NotBought.Single().Why);
        Assert.Empty(host.Purchased);
        var aggregate = host.Analytics.SnapshotAggregate();
        Assert.Equal(1, aggregate.PurchaseAttempts);
        Assert.Equal(0, aggregate.Purchases);
        Assert.Equal(100_000, host.BalanceCents);
    }

    [Fact]
    public async Task AStaleSteamValuationIsRecheckedAndAbandonedIfThePriceMoved()
    {
        var csfloat = CSFloat();
        var steam = Steam(new() { ["Good"] = (700, 750) }); // the market has moved since the old book
        var host = new FakeSnipeHost(csfloat, steam);
        var engine = new SnipeEngine(host);
        var listing = L("g", "Good", 1000, null);
        var oldBook = SteamOrderBook.ParseOrderbookResponse(CS2Fixtures.SteamBook(1200, 1250), "Good", (m, c) => SteamCurrency.ToGbpPence(m, c, null), DateTime.UtcNow.AddMinutes(-10))!;
        var evaluation = OpportunityEvaluator.Evaluate(listing, oldBook, 0.9, 0.75, host.Settings.Evaluation, DateTime.UtcNow, "test");
        Assert.True(evaluation.ShouldBuy);

        Assert.False(await engine.TryBuyAsync(listing, evaluation, oldBook, CancellationToken.None));
        Assert.Single(steam.Requests);
        Assert.Empty(Buys(csfloat));
    }

    [Fact]
    public void CoverageGapsAreDetected()
    {
        var t0 = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        List<CSFloatListing> Page(int count, DateTime oldest) => Enumerable.Range(0, count)
            .Select(i => { var l = L(i.ToString(), "x", 100, 100); l.CreatedAtUtc = oldest.AddSeconds(i); return l; }).ToList();

        Assert.False(SnipeEngine.DetectCoverageGap(Page(50, t0), null, 50));                 // first poll
        Assert.False(SnipeEngine.DetectCoverageGap(Page(50, t0), t0.AddSeconds(10), 50));    // overlaps the last poll
        Assert.True(SnipeEngine.DetectCoverageGap(Page(50, t0.AddMinutes(1)), t0, 50));      // everything is newer: we missed some
        Assert.False(SnipeEngine.DetectCoverageGap(Page(20, t0.AddMinutes(1)), t0, 50));     // a short page is complete
    }

    [Fact]
    public void FeedPriceBoundFollowsTheBalance()
    {
        var settings = new EngineSettings { MaxSpendPerItemFraction = 0.4, Evaluation = new EvaluationSettings { MinimumPriceCents = 100 } };
        Assert.Equal(4000, SnipeEngine.FeedMaxPriceCents(10_000, settings));
        Assert.Null(SnipeEngine.FeedMaxPriceCents(1_000, settings)); // tiny balance: still look (and report)
        Assert.Null(SnipeEngine.FeedMaxPriceCents(null, settings));
        settings.Evaluation.MaximumPriceCents = 2500;
        Assert.Equal(2500, SnipeEngine.FeedMaxPriceCents(10_000, settings));
        Assert.Equal(2500, SnipeEngine.FeedMaxPriceCents(null, settings));
    }

    [Fact]
    public async Task FeedPollAsksForTheNewestListingsWithTheRightFilters()
    {
        string page = CS2Fixtures.ListingPage(CS2Fixtures.Listing("n1", "Nothing", 500, 500));
        var csfloat = CSFloat((request, _) => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/listings"
            ? FakeHttpHandler.Json(page, headers: new() { ["x-ratelimit-limit"] = "200", ["x-ratelimit-remaining"] = "150", ["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString() })
            : null);
        var host = new FakeSnipeHost(csfloat, Steam(new()), "{\"Nothing\":{\"last_24h\":1.0,\"last_7d\":1.0,\"last_30d\":1.0,\"last_90d\":1.0}}");
        host.Settings.Evaluation.MinimumPriceCents = 150;
        var engine = new SnipeEngine(host);
        await engine.RunFeedPollAsync(CancellationToken.None);

        var query = HttpUtility.ParseQueryString(new Uri(csfloat.Requests.Single().Url).Query);
        Assert.Equal("most_recent", query["sort_by"]);
        Assert.Equal("150", query["min_price"]);
        Assert.Equal("40000", query["max_price"]); // 40% of the $1000 balance
        Assert.Equal("buy_now", query["type"]);
        Assert.Equal(150, host.CSFloat.RateLimits.Get(CSFloatWrapper.ListingsBucket).Remaining);
        Assert.Equal(1, engine.GetStatus().FeedPolls);
    }

    [Fact]
    public async Task StructuralScanFindsAStructurallyCheapItemAndBuysItsCheapestListing()
    {
        // An item that is never a "deal" on CSFloat (listed at its market value) but carries a Steam premium.
        string priceList = "[{\"market_hash_name\":\"Kilowatt Case\",\"quantity\":2740,\"min_price\":500},{\"market_hash_name\":\"Overpriced\",\"quantity\":50,\"min_price\":5000}]";
        string targeted = CS2Fixtures.ListingPage(CS2Fixtures.Listing("k1", "Kilowatt Case", 500, 510, refQty: 9000, floatValue: null));
        var csfloat = CSFloat((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/listings/price-list") return FakeHttpHandler.Json(priceList);
            if (path == "/api/v1/listings" && request.Method == HttpMethod.Get)
            {
                Assert.Equal("Kilowatt Case", HttpUtility.ParseQueryString(request.RequestUri.Query)["market_hash_name"]);
                return FakeHttpHandler.Json(targeted);
            }
            return null;
        });
        var steam = Steam(new() { ["Kilowatt Case"] = (560, 570), ["Overpriced"] = (3000, 3100) });
        var host = new FakeSnipeHost(csfloat, steam,
            "{\"Kilowatt Case\":{\"last_24h\":7.4,\"last_7d\":7.4,\"last_30d\":7.2,\"last_90d\":7.0},\"Overpriced\":{\"last_24h\":40,\"last_7d\":40,\"last_30d\":40,\"last_90d\":40}}");
        host.ConversionCoefficient = 0.95;
        var engine = new SnipeEngine(host);
        await engine.RunStructuralScanAsync(CancellationToken.None);

        Assert.Equal(new[] { ("k1", 500) }, Buys(csfloat));
        Assert.Equal(ExitRoute.SteamMarket, host.Purchased.Single().Evaluation.BestRoute);
        Assert.DoesNotContain(steam.Requests, r => r.Url.Contains("Overpriced"));
        Assert.Contains("1 confirmed", engine.GetStatus().LastStructuralSummary);
    }

    [Fact]
    public async Task ConversionModelRunVerifiesConvertersAgainstCSFloatSales()
    {
        string priceList = "[{\"market_hash_name\":\"Dreams & Nightmares Case\",\"quantity\":31997,\"min_price\":99},{\"market_hash_name\":\"Sticker | WARNING\",\"quantity\":300,\"min_price\":190}]";
        string Graph(double avg, int count) => "[" + string.Join(",", Enumerable.Range(0, 7).Select(d => $"{{\"count\":{count},\"day\":\"{DateTime.UtcNow.Date.AddDays(-d):yyyy-MM-ddT00:00:00Z}\",\"avg_price\":{avg}}}")) + "]";
        var csfloat = CSFloat((request, _) =>
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == "/api/v1/listings/price-list") return FakeHttpHandler.Json(priceList);
            if (path == "/api/v1/history/Dreams & Nightmares Case/graph") return FakeHttpHandler.Json(Graph(97.5, 1000));
            if (path == "/api/v1/history/Sticker | WARNING/graph") return FakeHttpHandler.Json(Graph(191, 24));
            return null;
        });
        var steam = Steam(new() { ["Dreams & Nightmares Case"] = (103, 107), ["Sticker | WARNING"] = (130, 141) });
        var host = new FakeSnipeHost(csfloat, steam,
            "{\"Dreams & Nightmares Case\":{\"last_24h\":1.4,\"last_7d\":1.4,\"last_30d\":1.4,\"last_90d\":1.4},\"Sticker | WARNING\":{\"last_24h\":1.9,\"last_7d\":1.9,\"last_30d\":1.9,\"last_90d\":1.9}}");
        var engine = new SnipeEngine(host);
        Assert.True(await engine.RunConversionModelAsync(CancellationToken.None));

        var model = host.Model!;
        var warning = model.Converters.Single(c => c.MarketHashName == "Sticker | WARNING");
        var dn = model.Converters.Single(c => c.MarketHashName == "Dreams & Nightmares Case");
        Assert.True(warning.VerifiedBySales && dn.VerifiedBySales, string.Join(" | ", engine.GetStatus().RecentErrors) + " || " + string.Join(" | ", csfloat.Requests.Select(r => r.Url)));
        Assert.True(warning.Coefficient > dn.Coefficient);
        Assert.InRange(dn.Coefficient, 0.66, 0.70);
        // Capacity-weighted and haircut: never above the best verified converter.
        Assert.InRange(model.Coefficient, 0.6, warning.Coefficient);
        Assert.NotEmpty(host.Plan!.Top10Gaps);
        Assert.Contains("conversion coefficient", host.Plan.LiquidityPlanDescription);
    }
}
