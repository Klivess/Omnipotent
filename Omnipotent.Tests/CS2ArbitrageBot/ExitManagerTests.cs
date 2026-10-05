using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Net;
using System.Web;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Tests.CS2ArbitrageBot;

/// <summary>Records what the exit manager does instead of trading.</summary>
internal sealed class FakeExitHost : IExitHost
{
    public FakeExitHost(FakeHttpHandler csfloat, FakeHttpHandler steam)
    {
        var client = new CSFloatWrapper(null, new HttpClient(csfloat));
        var market = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: steam);
        ExitSignals = new ExitSignalsProvider(client, market);
    }

    public ExitSignalsProvider ExitSignals { get; }
    public EngineSettings Settings { get; } = new();
    public double GbpPerUsd { get; set; } = 0.75;
    public double ConversionCoefficient { get; set; } = 0.66;
    public MarketDriftModel MarketModel { get; } = new();
    public ExitCalibrationSnapshot ExitCalibration { get; set; } = ExitCalibrationSnapshot.Neutral;
    public ExitEnvironment ExitEnvironment { get; set; } = ExitEnvironment.Default;
    public string? OwnSteamId { get; set; } = "76561190000000002";
    public bool Tradable { get; set; } = true;
    public List<(string Action, int Price)> Actions { get; } = new();
    public List<string> Notifications { get; } = new();
    public List<string> Alerts { get; } = new();
    public int Saves { get; private set; }

    public Task<bool> SellOnSteamAsync(PurchasedListing position, int pricePence)
    {
        Actions.Add(("steam", pricePence));
        position.CurrentStrategicStage = StrategicStages.WaitingForMarketSaleOnSteam;
        return Task.FromResult(true);
    }

    public Task<string?> ListOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct)
    {
        if (!Tradable) return Task.FromResult<string?>(null);
        Actions.Add(("list", priceCents));
        return Task.FromResult<string?>("relist-1");
    }

    public Task<bool> RepriceOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct)
    {
        Actions.Add(("reprice", priceCents));
        return Task.FromResult(true);
    }

    public Task DelistFromCSFloatAsync(PurchasedListing position, CancellationToken ct)
    {
        Actions.Add(("delist", 0));
        return Task.CompletedTask;
    }

    public Task SaveAsync(PurchasedListing position) { Saves++; return Task.CompletedTask; }
    public Task NotifyAsync(PurchasedListing position, string title, string message) { Notifications.Add(title + ": " + message); return Task.CompletedTask; }
    public Task AlertAsync(string key, string title, string message) { Alerts.Add(title + ": " + message); return Task.CompletedTask; }
    public void Log(string message) { }
}

public class ExitManagerTests
{
    private const string Item = "Sticker | Test (Holo) | Cologne 2026";
    private static readonly DateTime T0 = DateTime.UtcNow;

    private static string QpName(HttpRequestMessage request) =>
        (string)Newtonsoft.Json.Linq.JArray.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["qp"]!)[1]!;

    /// <summary>A liquid sticker worth $4 on CSFloat: ~3 sales a day at 0.93–0.99 of value, rivals from $3.88.</summary>
    private static FakeHttpHandler CSFloat(bool historyAvailable = true) => new((request, _) =>
    {
        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (!historyAvailable && (path.StartsWith("/api/v1/history/") || path == "/api/v1/listings"))
            return FakeHttpHandler.Json("{\"message\":\"internal\"}", HttpStatusCode.InternalServerError);
        if (path == $"/api/v1/history/{Item}/graph") return FakeHttpHandler.Json(CS2Fixtures.SalesGraph(T0, _ => 3, 384));
        if (path == $"/api/v1/history/{Item}/sales")
            return FakeHttpHandler.Json(CS2Fixtures.RecentSales(T0, Enumerable.Range(0, 30).Select(i => (372 + 4 * (i % 7), 400, 14.0, i * 8.0))));
        if (path == "/api/v1/listings" && request.Method == HttpMethod.Get)
        {
            Assert.Equal(Item, HttpUtility.ParseQueryString(request.RequestUri.Query)["market_hash_name"]);
            return FakeHttpHandler.Json(CS2Fixtures.ListingPage(
                CS2Fixtures.Listing("r1", Item, 388, 400, 400, floatValue: null),
                CS2Fixtures.Listing("r2", Item, 396, 400, 400, floatValue: null),
                CS2Fixtures.Listing("r3", Item, 404, 400, 400, floatValue: null)));
        }
        return FakeHttpHandler.Json("{\"message\":\"unexpected\"}", HttpStatusCode.NotFound);
    });

    /// <summary>Steam pays far less for it: £2.60 buy order (≈£1.49 back on CSFloat at k = 0.66).</summary>
    private static FakeHttpHandler Steam(int bid = 260) => new((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/market/orderbook") return FakeHttpHandler.Json(CS2Fixtures.SteamBook(bid, bid + 30));
        if (request.RequestUri.AbsolutePath == "/market/actions") return FakeHttpHandler.Json(CS2Fixtures.SteamHistory(T0, _ => 2.9));
        return FakeHttpHandler.Json("{\"data\":{\"success\":false}}");
    });

    private static PurchasedListing Position() => new()
    {
        CSFloatListingID = "bought-1",
        ItemMarketHashName = Item,
        CurrentStrategicStage = StrategicStages.JustRetrieved,
        PurchaseCostPence = 250,
        AnchorCentsAtPurchase = 400,
        AnchorFloatFactor = 1,
        PredictedTimeToBeResoldOnSteam = T0.AddHours(-1),
    };

    [Fact]
    public async Task MissingCSFloatEvidencePostponesTheSaleInsteadOfDumpingItOnSteam()
    {
        var host = new FakeExitHost(CSFloat(historyAvailable: false), Steam());
        var manager = new ExitManager(host, () => T0);
        var p = Position();

        Assert.Equal("deferred", await manager.RunSaleAsync(p, CancellationToken.None));
        Assert.Empty(host.Actions);
        Assert.Equal(1, p.SaleDeferrals);
        Assert.True(p.SaleDeferredUntilUtc > T0);
        Assert.Equal(0, p.SaleAttempts);

        // After a day of failed attempts it decides with what it has rather than never.
        p.SaleDeferrals = ExitManager.MaxSaleDeferrals;
        string outcome = await manager.RunSaleAsync(p, CancellationToken.None);
        Assert.NotEqual("deferred", outcome);
        Assert.Single(host.Actions);
    }

    [Fact]
    public async Task ItRelistsAtTheModelsPriceAndOpensAnEvidenceWindow()
    {
        var host = new FakeExitHost(CSFloat(), Steam());
        var manager = new ExitManager(host, () => T0);
        var p = Position();

        Assert.Equal("relist", await manager.RunSaleAsync(p, CancellationToken.None));
        var (action, price) = Assert.Single(host.Actions);
        Assert.Equal("list", action);
        Assert.InRange(price, 360, 412);
        Assert.Equal(StrategicStages.WaitingForCSFloatResale, p.CurrentStrategicStage);
        Assert.Equal("relist-1", p.CSFloatResaleListingID);
        Assert.Equal(price, p.CSFloatResalePriceCents);
        Assert.True(p.RelistBaseDaysToSell > 0);
        Assert.Equal(T0, p.ListedOnCSFloatAtUtc);
        Assert.InRange(p.NextExitReviewUtc, T0.AddHours(2), T0.AddHours(12));
        var decision = Assert.Single(p.ExitHistory);
        Assert.Equal("sale", decision.Stage);
        Assert.Equal("relist", decision.Action);
        Assert.Equal(400, decision.ObservedAnchorCents);
        Assert.True(decision.SalesPerDay > 2);
        Assert.Contains("listed at", host.Notifications.Single());
    }

    [Fact]
    public async Task AnItemStillTradeLockedIsRetriedSoonWithoutBurningASaleAttempt()
    {
        var host = new FakeExitHost(CSFloat(), Steam()) { Tradable = false };
        var p = Position(); // protection lifted an hour ago, but CSFloat's inventory still says not tradable
        Assert.Equal("not tradable", await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None));
        Assert.Equal(StrategicStages.JustRetrieved, p.CurrentStrategicStage);
        Assert.Empty(p.ExitHistory);
        Assert.Equal(0, p.SaleAttempts);
        Assert.Equal(T0 + ExitManager.NotTradableRetry, p.SaleDeferredUntilUtc);
        Assert.Empty(host.Alerts);

        // Two days after it should have unlocked something else is locking it: a person should look.
        Assert.Equal("not tradable", await new ExitManager(host, () => T0.AddDays(2)).RunSaleAsync(p, CancellationToken.None));
        Assert.Contains(host.Alerts, a => a.Contains("still trade-locked"));
        Assert.Equal(0, p.SaleAttempts);
    }

    [Fact]
    public async Task WhileAwayTheSaleWaitsForTheBetterRelistAndReviewsNeitherActNorCountTime()
    {
        var host = new FakeExitHost(CSFloat(), Steam()) { ExitEnvironment = ExitEnvironment.Default with { CSFloatSellingPaused = true } };
        var p = Position();
        // Relisting is the better exit but cannot sell while away: wait instead of dumping on Steam.
        Assert.Equal("paused", await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None));
        Assert.Empty(host.Actions);
        Assert.Equal(T0 + ExitManager.PausedRetry, p.SaleDeferredUntilUtc);

        host.ExitEnvironment = ExitEnvironment.Default;
        Assert.Equal("relist", await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None));

        host.ExitEnvironment = ExitEnvironment.Default with { CSFloatSellingPaused = true };
        Assert.Equal("paused", await new ExitManager(host, () => T0.AddDays(10)).ReviewListingAsync(p, CancellationToken.None));
        Assert.Single(host.Actions);                // only the listing itself
        Assert.Equal(0, p.RelistTotalExposure);     // ten hidden days are no evidence against the price
        Assert.Equal(T0.AddDays(10), p.RelistExposureUpdatedUtc);
    }

    [Fact]
    public async Task AFreshListingIsKeptAndAStaleOneIsActedOn()
    {
        var host = new FakeExitHost(CSFloat(), Steam());
        var p = Position();
        await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None);
        int listed = p.CSFloatResalePriceCents;

        Assert.Equal("keep", await new ExitManager(host, () => T0.AddHours(3)).ReviewListingAsync(p, CancellationToken.None));
        Assert.Single(host.Actions); // only the original listing

        // Ten days without a sale on an item that sells several times a day.
        string outcome = await new ExitManager(host, () => T0.AddDays(10)).ReviewListingAsync(p, CancellationToken.None);
        Assert.Contains(outcome, new[] { "reprice", "steam" });
        Assert.True(p.RelistTotalExposure > 5);
        if (outcome == "reprice")
        {
            Assert.True(p.CSFloatResalePriceCents < listed);
            Assert.Equal(("reprice", p.CSFloatResalePriceCents), host.Actions[^1]);
            Assert.True(p.RelistModelExposure < p.RelistTotalExposure); // only the evidence that bears on the new price carries over
        }
        else
        {
            Assert.Equal(("delist", 0), host.Actions[^2]);
            Assert.Equal("steam", host.Actions[^1].Action);
        }
    }

    [Fact]
    public async Task InAdviceOnlyModeAStaleListingRaisesAnAlertButIsNotTouched()
    {
        var host = new FakeExitHost(CSFloat(), Steam());
        var p = Position();
        await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None);
        host.Settings.AutoManageRelists = false;

        Assert.Equal("keep", await new ExitManager(host, () => T0.AddDays(10)).ReviewListingAsync(p, CancellationToken.None));
        Assert.Single(host.Actions);
        Assert.Contains(host.Alerts, a => a.Contains("relist advice"));
        // Ten days on an item that sells several times a day: someone should check the listing itself.
        Assert.Contains(host.Alerts, a => a.Contains("relist overdue"));
    }

    [Fact]
    public async Task AReviewWithoutCSFloatEvidenceNeverTouchesTheListing()
    {
        var p = Position();
        await new ExitManager(new FakeExitHost(CSFloat(), Steam()), () => T0).RunSaleAsync(p, CancellationToken.None);

        // Ten days later the CSFloat history is unavailable: the pessimistic proxies must not trigger a delist.
        var blind = new FakeExitHost(CSFloat(historyAvailable: false), Steam(bid: 380));
        Assert.Equal("deferred", await new ExitManager(blind, () => T0.AddDays(10)).ReviewListingAsync(p, CancellationToken.None));
        Assert.Empty(blind.Actions);
        Assert.Equal(StrategicStages.WaitingForCSFloatResale, p.CurrentStrategicStage);
        Assert.Equal(T0.AddDays(10).AddHours(1), p.NextExitReviewUtc);
    }

    [Fact]
    public async Task AListingABuyerHasAlreadyBoughtIsLeftAlone()
    {
        var host = new FakeExitHost(CSFloat(), Steam());
        var p = Position();
        await new ExitManager(host, () => T0).RunSaleAsync(p, CancellationToken.None);
        p.LastTradeState = "resale:pending";
        Assert.Equal("skip", await new ExitManager(host, () => T0.AddDays(10)).ReviewListingAsync(p, CancellationToken.None));
        Assert.Single(host.Actions);
    }
}
