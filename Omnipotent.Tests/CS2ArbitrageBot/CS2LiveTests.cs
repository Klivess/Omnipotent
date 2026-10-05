using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Diagnostics;
using Xunit.Abstractions;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Tests.CS2ArbitrageBot;

/// <summary>Runs only with CS2_LIVE=1: these hit steamcommunity.com, csfloat.com and the bulk price feed.</summary>
public sealed class CS2LiveFactAttribute : FactAttribute
{
    public CS2LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CS2_LIVE") != "1")
            Skip = "Live market test; set CS2_LIVE=1 (needs internet; the engine dry run also needs a CSFloat API key).";
    }
}

/// <summary>Refuses every non-GET request: a live dry run can read the markets but can never buy or list.</summary>
internal sealed class ReadOnlyGuardHandler : DelegatingHandler
{
    public ReadOnlyGuardHandler(HttpMessageHandler inner) : base(inner) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            throw new InvalidOperationException($"Dry run refused {request.Method} {request.RequestUri}");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Live checks of every external dependency — the things that silently broke the old bot — plus a dry run
/// of the real engine. Run after Steam/CSFloat changes:
/// <c>CS2_LIVE=1 DOTNET_ROLL_FORWARD=Major dotnet test -c Release --filter "FullyQualifiedName~CS2LiveTests"</c>
/// The dry run reads the CSFloat key from CS2_CSFLOAT_KEY or the dev SavedData copy; it never spends money.
/// </summary>
public class CS2LiveTests
{
    private readonly ITestOutputHelper output;
    public CS2LiveTests(ITestOutputHelper output) => this.output = output;

    private static Dictionary<string, double>? rates;
    private static async Task<Dictionary<string, double>> Rates() => rates ??= await CSFloatWrapper.GetExchangeRatesAsync(new HttpClient());

    [CS2LiveFact]
    public async Task SteamOrderbookEndpointReturnsARealBook()
    {
        var r = await Rates();
        using var client = new SteamMarketClient(() => r, _ => null, TimeSpan.FromMilliseconds(500));
        var book = await client.GetOrderBookAsync("AK-47 | Slate (Minimal Wear)", TimeSpan.Zero, RequestPriority.High);
        Assert.NotNull(book);
        output.WriteLine($"AK-47 | Slate (MW): buy £{book!.HighestBuyOrderPence / 100.0:F2} ×{book.BuyOrderCount}, ask £{book.LowestSellOrderPence / 100.0:F2} ×{book.SellOrderCount}, currency {book.SourceCurrency}, source {book.Source}");
        Assert.Equal("orderbook", book.Source);
        Assert.True(book.HighestBuyOrderPence > 0 && book.LowestSellOrderPence >= book.HighestBuyOrderPence);
        Assert.True(book.BuyLevels.Count > 5 && book.SellLevels.Count > 5);
        Assert.True(book.PriceAtBuyDepth(3) > 0);
    }

    [CS2LiveFact]
    public async Task SteamPriceHistoryIsAvailableAnonymously()
    {
        var r = await Rates();
        using var client = new SteamMarketClient(() => r, _ => null, TimeSpan.FromMilliseconds(500));
        var history = await client.GetPriceHistoryAsync("Dreams & Nightmares Case", RequestPriority.High);
        Assert.NotNull(history);
        output.WriteLine($"{history!.Count} points, latest £{history[^1].MedianPriceGbp:F2} ×{history[^1].Purchases} at {history[^1].TimeUtc:u}");
        Assert.True(history.Count > 100);
        Assert.True(history[^1].TimeUtc > DateTime.UtcNow.AddDays(-3));
    }

    [CS2LiveFact]
    public async Task BulkFeedsLoad()
    {
        var reference = new SteamReferencePrices();
        Assert.True(await reference.RefreshAsync(SteamReferencePrices.CreateHttpClient()), reference.LastError);
        output.WriteLine($"csgotrader Steam feed: {reference.Count} items");
        Assert.True(reference.Count > 10_000);

        var csfloat = new CSFloatWrapper(null, CSFloatWrapper.CreateHttpClient(null));
        var list = await csfloat.GetPriceListAsync();
        output.WriteLine($"CSFloat price list: {list.Count} items");
        Assert.True(list.Count > 10_000);
    }

    private static string? CSFloatKey()
    {
        string? key = Environment.GetEnvironmentVariable("CS2_CSFLOAT_KEY");
        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
        string dev = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Omnipotent", "bin", "Debug", "net9.0", "SavedData", "CS2ArbitrageBot", "CSFloatAPIKey.txt");
        return File.Exists(dev) ? File.ReadAllText(dev).Trim() : null;
    }

    private sealed class DryRunHost : ISnipeHost
    {
        public required CSFloatWrapper CSFloat { get; init; }
        public required SteamMarketClient SteamMarket { get; init; }
        private ExitSignalsProvider? exitSignals;
        public ExitSignalsProvider ExitSignals => exitSignals ??= new ExitSignalsProvider(CSFloat, SteamMarket);
        public MarketDriftModel MarketModel { get; } = new();
        public ExitCalibrationSnapshot ExitCalibration => ExitCalibrationSnapshot.Neutral;
        public ExitEnvironment ExitEnvironment => ExitEnvironment.Default;
        public string? OwnSteamId => null;
        public required SteamReferencePrices ReferencePrices { get; init; }
        public Scanalytics Analytics { get; } = new(null!);
        public EngineSettings Settings { get; } = new() { PurchasingEnabled = false };
        public double GbpPerUsd { get; init; }
        public double ConversionCoefficient { get; set; } = 0.68;
        public int? BalanceCents { get; set; }
        public int SpentTodayPence => 0;
        public double TargetConversionVolumePence => 10_000;
        public List<(CSFloatListing Listing, OpportunityEvaluation Evaluation, string Why)> WouldBuy { get; } = new();
        public ConversionModelSnapshot? Model;
        public LiquidityPlan? Plan;
        public List<string> Errors { get; } = new();

        public async Task RefreshBalanceAsync(bool force, CancellationToken ct)
        {
            if (BalanceCents == null || force) BalanceCents = (await CSFloat.GetAccountInformation()).BalanceInCents;
        }
        public void DebitBalance(int cents) { }
        public int OpenPositionsFor(string marketHashName) => 0;
        public Task OnPurchasedAsync(CSFloatListing listing, OpportunityEvaluation evaluation, SteamOrderBook? book) =>
            throw new InvalidOperationException("dry run must never purchase");
        public Task OnOpportunityNotBoughtAsync(CSFloatListing listing, OpportunityEvaluation evaluation, string why)
        {
            lock (WouldBuy) WouldBuy.Add((listing, evaluation, why));
            return Task.CompletedTask;
        }
        public Task OnConversionModelComputedAsync(ConversionModelSnapshot snapshot, LiquidityPlan plan)
        {
            Model = snapshot;
            Plan = plan;
            ConversionCoefficient = snapshot.Coefficient;
            return Task.CompletedTask;
        }
        public void Log(string message) { }
        public void LogError(Exception? ex, string message) { lock (Errors) Errors.Add(message + " " + ex?.Message); }
    }

    /// <summary>
    /// The exit model on real data (read-only): for items from a case that sells constantly to a sticker that
    /// has not sold in months, what each exit is worth and how long a relist would take.
    /// </summary>
    [CS2LiveFact]
    public async Task ExitModelAgainstTheLiveMarkets()
    {
        string? key = CSFloatKey();
        Assert.False(string.IsNullOrEmpty(key), "No CSFloat API key (set CS2_CSFLOAT_KEY).");
        var r = await Rates();
        double fx = r["gbp"];
        var http = new HttpClient(new ReadOnlyGuardHandler(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", key);
        var csfloat = new CSFloatWrapper(null, http);
        using var steam = new SteamMarketClient(() => r, _ => null, TimeSpan.FromMilliseconds(1200));
        var signalsProvider = new ExitSignalsProvider(csfloat, steam);
        var market = new MarketDriftModel();
        var plans = new Dictionary<string, ExitPlan>();

        // The locks every decision runs against, measured from the account's own trades.
        var timeline = new TradeTimeline();
        timeline.Measure(await csfloat.GetTradesAsync(100), DateTime.UtcNow);
        var (holdGrowth, holdSigma) = ConversionModel.HoldRisk(null, market, timeline.ConverterHoldDays);
        var environment = new ExitEnvironment { Timeline = timeline, ConverterHoldGrowth = holdGrowth, ConverterHoldSigma = holdSigma };
        output.WriteLine("Trade timeline (measured): " + Newtonsoft.Json.JsonConvert.SerializeObject(timeline.Describe()));
        output.WriteLine($"A purchase now unlocks in {timeline.DaysUntilTradable(DateTime.UtcNow, 0):F2} days (protection ends {timeline.ProtectionEnd(DateTime.UtcNow.AddDays(timeline.HandoverDays)):dd/MM HH:mm} UTC)");
        Assert.InRange(timeline.CSFloatSaleToCashDays, 7, 9);
        Assert.InRange(timeline.SteamSaleToCashDays, 14, 18);

        foreach (string name in new[] { "Recoil Case", "Sticker | Vitality (Holo) | Austin 2025", "Sticker | chrisJ | Cologne 2016", "AK-47 | Slate (Field-Tested)" })
        {
            var signals = await signalsProvider.GetAsync(name, SignalUse.Sale, CancellationToken.None);
            int anchor = signals.ReferenceBaseCents ?? 0;
            var demand = CSFloatDemand.Build(name, anchor, signals.SalesGraph, signals.RecentSales, signals.Competitors, signals.ReferenceQuantity, null, null, DateTime.UtcNow);
            var forecast = PriceForecaster.Forecast(signals.SteamHistory, name, DateTime.UtcNow, market);
            var plan = ExitPlanner.Plan(new ExitContext
            {
                MarketHashName = name,
                CostPence = (int)(anchor * 0.85 * fx),
                AnchorCents = anchor,
                GbpPerUsd = fx,
                ConversionCoefficient = 0.66,
                Book = signals.Book,
                Forecast = forecast,
                Demand = anchor > 0 ? demand : null,
                Environment = environment,
            }, new ExitModelSettings(), DateTime.UtcNow);
            plans[name] = plan;
            output.WriteLine($"── {name}: value ${anchor / 100.0:F2}; {demand.SalesPerDay:0.###} sales/day ({demand.SalesRateBasis}), last sale {demand.DaysSinceLastSale:F1}d ago, buyers pay ~{demand.MedianValueRatio:P0}, {demand.CompetitorRatios.Count} rivals from ${demand.LowestCompetitorCents / 100.0:F2}");
            output.WriteLine($"   Steam history: {forecast.Basis}; σ(8d) {forecast.Sigma8:P1}, exponent {forecast.HorizonExponent:F2}, drift {forecast.DriftPerDay * 30:P1}/30d, {forecast.SteamSalesPerDay:F1} Steam sales/day");
            output.WriteLine($"   → {plan.Rationale}");
            if (signals.Missing.Count > 0) output.WriteLine("   missing: " + string.Join("; ", signals.Missing));
        }

        Assert.All(plans.Values, p => Assert.NotNull(p.Best));
        Assert.True(plans["Recoil Case"].BestRelist!.ExpectedDaysToSell < 1, "a case that sells constantly should relist within a day");
        var dead = plans["Sticker | chrisJ | Cologne 2016"];
        Assert.True(dead.SalesPerDay < 0.1, $"chrisJ sells {dead.SalesPerDay:F3}/day");
        Assert.True(dead.BestRelist == null || dead.BestRelist.SellProbability < 0.5, "an item that has not sold in months must not be valued as if it will");

        // Walk-forward calibration of the forecaster on live histories: (realised − forecast) / σ should be ~N(0,1)-sized.
        var zs = new List<double>();
        foreach (string name in new[] { "Recoil Case", "Dreams & Nightmares Case", "Sticker | Vitality (Holo) | Austin 2025", "AK-47 | Slate (Field-Tested)", "Charm | Lil' Serpent", "Sticker | s1mple (Holo) | Paris 2023" })
        {
            var history = await steam.GetPriceHistoryAsync(name, RequestPriority.Normal);
            if (PriceForecaster.WalkForwardZ(history, name, DateTime.UtcNow) is double z) { zs.Add(z); output.WriteLine($"walk-forward z {z,6:F2}  {name}"); }
        }
        Assert.True(zs.Count >= 4);
        double rms = Math.Sqrt(zs.Average(z => z * z));
        output.WriteLine($"RMS z over {zs.Count} items: {rms:F2} (1 = calibrated)");
        Assert.InRange(rms, 0.2, 3.0);
    }

    [CS2LiveFact]
    public async Task EngineDryRunAgainstTheLiveMarkets()
    {
        string? key = CSFloatKey();
        Assert.False(string.IsNullOrEmpty(key), "No CSFloat API key (set CS2_CSFLOAT_KEY).");
        var r = await Rates();
        var http = new HttpClient(new ReadOnlyGuardHandler(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", key);
        var reference = new SteamReferencePrices();
        await reference.RefreshAsync(SteamReferencePrices.CreateHttpClient());
        var host = new DryRunHost
        {
            CSFloat = new CSFloatWrapper(null, http),
            SteamMarket = new SteamMarketClient(() => r, _ => null, TimeSpan.FromMilliseconds(700)),
            ReferencePrices = reference,
            GbpPerUsd = r["gbp"],
        };
        await host.RefreshBalanceAsync(true, CancellationToken.None);
        var engine = new SnipeEngine(host);
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("CS2_LIVE_SECONDS"), out int s) ? s : 150;
        var clock = Stopwatch.StartNew();

        output.WriteLine($"GBP/USD {host.GbpPerUsd:F4}; CSFloat balance ${host.BalanceCents / 100.0:F2}; bulk Steam prices {reference.Count}");
        Assert.True(await engine.RunConversionModelAsync(CancellationToken.None));
        output.WriteLine($"Conversion model: k = {host.Model!.Coefficient:F3} ({host.Model.Basis}) after {clock.Elapsed.TotalSeconds:F0}s");
        foreach (var c in host.Model.Converters.Take(8))
            output.WriteLine($"  k={c.Coefficient:F3} {(c.VerifiedBySales ? "verified" : "ask-only")} sales7d={c.CSFloatSales7d} cap=£{c.WeeklyCapacityPence / 100:F0} {c.MarketHashName}");

        await engine.RunStructuralScanAsync(CancellationToken.None);
        output.WriteLine($"Structural: {engine.GetStatus().LastStructuralSummary}");
        await engine.RunSweepAsync("highest_discount", CancellationToken.None);
        output.WriteLine($"Setup phases took {clock.Elapsed.TotalSeconds:F0}s; feed phase runs {seconds}s.");
        var feedClock = Stopwatch.StartNew();
        while (feedClock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await engine.RunFeedPollAsync(CancellationToken.None);
            await Task.Delay(host.CSFloat.RateLimits.SuggestedInterval(CSFloatWrapper.ListingsBucket, 15, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)));
        }
        await Task.Delay(TimeSpan.FromSeconds(10)); // let the last feed valuation finish

        var status = engine.GetStatus();
        var aggregate = host.Analytics.SnapshotAggregate();
        output.WriteLine($"Feed polls {status.FeedPolls}, gaps {status.FeedCoverageGaps} (+{status.FeedGapFillPages} fill pages), ~{status.ObservedListingsPerMinute}/min in window");
        output.WriteLine($"Seen {status.ListingsSeen}, evaluated {status.Evaluated}, prefiltered {status.Prefiltered}, Steam lookups {status.SteamLookups}, Steam cache hits {host.SteamMarket.CacheHits}");
        output.WriteLine($"Buckets <0:{aggregate.BucketCounts[0]} 0-5:{aggregate.BucketCounts[1]} 5-10:{aggregate.BucketCounts[2]} 10-20:{aggregate.BucketCounts[3]} >20:{aggregate.BucketCounts[4]}; qualified {aggregate.QualifiedOpportunities}");
        foreach (var cycle in host.Analytics.RecentCycles())
            output.WriteLine($"  {cycle.Strategy,-24} returned {cycle.ListingsReturned,3} new {cycle.NewListings,3} eval {cycle.Evaluated,3} steam {cycle.SteamLookups,3} opp {cycle.Opportunities} {cycle.DurationMs,6:F0}ms best {cycle.BestRoi:P1} {cycle.BestItem} {cycle.Note}");
        foreach (var (listing, evaluation, why) in host.WouldBuy.Take(15))
            output.WriteLine((evaluation.ShouldBuy ? "WOULD BUY " : "SCREEN PASSED, NOT BOUGHT ") +
                $"{listing.MarketHashName} ${listing.PriceCents / 100.0:F2} via {evaluation.BestRoute} ROI {evaluation.BestRoi:P1} (screen {evaluation.ScreenBestRoi:P1}) profit £{evaluation.BestProfitPence / 100.0:F2} ({why}) {listing.ListingUrl}");
        foreach (var e in host.Analytics.RecentNotable(15).OrderByDescending(e => e.BestRoi))
            output.WriteLine($"notable {e.BestRoi,7:P1} {e.BestRoute,-13} {e.MarketHashName} ${e.PriceCents / 100.0:F2} — {e.Reason}");
        foreach (var err in status.RecentErrors.Take(10)) output.WriteLine("error: " + err);
        foreach (var bucket in host.CSFloat.RateLimits.Snapshot()) output.WriteLine($"rate {bucket.Name}: {bucket.Remaining}/{bucket.Limit} until {bucket.ResetUtc:HH:mm:ss}Z (429s: {bucket.TooManyRequestsCount})");

        Assert.True(status.FeedPolls >= 2);
        Assert.True(status.Evaluated > 0);
        Assert.Equal(0, host.CSFloat.RateLimits.Snapshot().Sum(b => b.TooManyRequestsCount));
        Assert.True(host.SteamMarket.Successes > 0);
    }
}
