using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class ExitPlannerTests
{
    private const double Fx = 0.75;
    private const string Sticker = "Sticker | Test (Holo) | Cologne 2026";
    private static readonly DateTime Now = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    private static SteamOrderBook Book(int bid, int ask, int buyOrders = 5000) =>
        SteamOrderBook.ParseOrderbookResponse(CS2Fixtures.SteamBook(bid, ask, buyOrders), Sticker, (m, c) => SteamCurrency.ToGbpPence(m, c, null), Now)!;

    private static PriceForecast Forecast(double sigma8 = 0.10, double drift = -0.0015) => new()
    {
        Category = ItemCategory.Sticker, HasHistory = true, LevelLog = Math.Log(3.0), DriftPerDay = drift, Sigma8 = sigma8, HorizonExponent = 0.3,
    };

    /// <summary>Demand with recent sales spread ±3% around <paramref name="medianRatio"/> of the $4 value.</summary>
    private static CSFloatDemand Demand(double salesPerDay, double medianRatio = 0.96, double daysOnMarket = 0.5, double[]? rivals = null, int sales = 30)
    {
        var demand = new CSFloatDemand
        {
            MarketHashName = Sticker, Interchangeable = true, AnchorCents = 400, SalesPerDay = salesPerDay,
            EvidenceDays = 30, SalesRateBasis = "test", DaysSinceLastSale = 0.2,
        };
        for (int i = 0; i < sales; i++)
        {
            double r = medianRatio + 0.03 * ((i % 7) - 3) / 3.0;
            demand.ValueRatios.Add(r);
            demand.Durations.Add(new CSFloatDemand.SaleDuration(r, daysOnMarket));
        }
        demand.CompetitorRatios.AddRange(rivals ?? new[] { 0.97, 0.98, 0.99, 1.0, 1.02 });
        return demand;
    }

    private static ExitContext Context(CSFloatDemand? demand, SteamOrderBook? book, double daysUntilTradable = 0, int? listed = null,
        double exposure = 0, bool relist = true, ExitCalibrationSnapshot? calibration = null) => new()
    {
        MarketHashName = Sticker,
        CostPence = 250,
        DaysUntilTradable = daysUntilTradable,
        AnchorCents = 400,
        GbpPerUsd = Fx,
        ConversionCoefficient = 0.66,
        Book = book,
        Forecast = Forecast(),
        Demand = demand,
        AllowRelist = relist,
        ListedPriceCents = listed,
        ListedHazardExposure = exposure,
        Calibration = calibration,
    };

    [Fact]
    public void ALiquidItemIsRelistedNearWhatBuyersActuallyPay()
    {
        var plan = ExitPlanner.Plan(Context(Demand(6), Book(260, 290)), new ExitModelSettings(), Now);
        var best = plan.Best!;
        Assert.Equal(ExitRoute.CSFloatRelist, best.Route);
        Assert.InRange(best.Price, 360, 412);
        Assert.True(best.SellProbability > 0.9, $"P(sell) {best.SellProbability:P0}");
        Assert.True(best.ExpectedDaysToSell < 7, $"{best.ExpectedDaysToSell:F1} days");
        Assert.True(plan.BestSteam!.CertaintyEquivalentPence < best.CertaintyEquivalentPence);
        Assert.Contains("CSFloat at", plan.Rationale);
    }

    [Fact]
    public void AnItemThatDoesNotSellGoesToSteamEvenWhenCSFloatValuesItHigher()
    {
        // CSFloat's reference says $4 (≈£2.94 net), Steam pays far less after fees and conversion (≈£1.72),
        // but nobody has bought this on CSFloat for months: the "better" exit is a listing that sits.
        var plan = ExitPlanner.Plan(Context(Demand(0.002, daysOnMarket: 90), Book(300, 330)), new ExitModelSettings(), Now);
        Assert.Equal(ExitRoute.SteamMarket, plan.Best!.Route);
        Assert.True(plan.BestRelist!.SellProbability < 0.2, $"P(sell) {plan.BestRelist.SellProbability:P0}");
        Assert.True(plan.BestRelist.NetIfSoldPence > plan.BestSteam!.NetIfSoldPence);
    }

    [Fact]
    public void TheSteamExitPaysForItsRoundTripBackToCSFloat()
    {
        var plan = ExitPlanner.Plan(Context(null, Book(300, 330)), new ExitModelSettings(), Now);
        var steam = plan.Best!;
        Assert.Equal(ExitRoute.SteamMarket, steam.Route);
        Assert.Equal(300, steam.Price);
        // Back on CSFloat only after confirming the sale, buying converters, Steam's 7-day hold on them, selling
        // them, then that sale's 7-day protection to its daily boundary and verification.
        var timeline = ExitEnvironment.Default.Timeline;
        Assert.Equal(ArbitrageMath.SteamSellerReceives(300) * 0.66 * ExitEnvironment.Default.ConverterHoldGrowth, steam.NetIfSoldPence, 9);
        Assert.Equal(timeline.SteamSaleToCashDays, steam.ExpectedDaysToCash, 9);
        Assert.InRange(steam.ExpectedDaysToCash, 15, 16.5);
        Assert.True(steam.CertaintyEquivalentPence < steam.PresentValuePence && steam.PresentValuePence < steam.NetIfSoldPence);
    }

    [Fact]
    public void AtPurchaseTheHoldCostsTimeAndAddsPriceRisk()
    {
        var demand = Demand(6);
        var now = ExitPlanner.Plan(Context(demand, Book(260, 290)), new ExitModelSettings(), Now);
        var atPurchase = ExitPlanner.Plan(Context(demand, Book(260, 290), daysUntilTradable: 8), new ExitModelSettings(), Now);
        Assert.True(atPurchase.Best!.CertaintyEquivalentPence < now.Best!.CertaintyEquivalentPence);
        Assert.True(atPurchase.Best.StdDevPence > now.Best.StdDevPence);
        Assert.True(atPurchase.AnchorAtTradableCents < 400); // stickers drift down over the hold
    }

    [Fact]
    public void AFreshListingIsKeptButOneThatSatUnsoldIsRepricedOrWithdrawn()
    {
        var demand = Demand(1.5);
        var book = Book(260, 290);
        int chosen = ExitPlanner.Plan(Context(demand, book), new ExitModelSettings(), Now).Best!.Price;

        var fresh = ExitPlanner.Plan(Context(demand, book, listed: chosen, exposure: 0.2), new ExitModelSettings(), Now);
        Assert.True(fresh.Best!.IsCurrentListing, fresh.Rationale);

        // It should have sold ~8 times over by now and hasn't: demand is marked down to 2/(2+8) = 20%.
        var stale = ExitPlanner.Plan(Context(demand, book, listed: chosen, exposure: 8), new ExitModelSettings(), Now);
        Assert.Equal(0.2, stale.DemandMultiplier, 9);
        Assert.False(stale.Best!.IsCurrentListing, stale.Rationale);
        Assert.True(stale.Best.Route == ExitRoute.SteamMarket || stale.Best.Price < chosen, stale.Rationale);
    }

    [Fact]
    public void ALiveListingChangesOnlyWhenTheGainClearsTheHysteresis()
    {
        var demand = Demand(6);
        int best = ExitPlanner.Plan(Context(demand, Book(260, 290)), new ExitModelSettings(), Now).Best!.Price;
        var offBest = Context(demand, Book(260, 290), listed: best + 30); // listed 7.5% too high

        var strict = ExitPlanner.Plan(offBest, new ExitModelSettings { MinimumImprovementFraction = 0, MinimumImprovementPence = 0 }, Now);
        Assert.False(strict.Best!.IsCurrentListing, strict.Rationale);

        var lax = ExitPlanner.Plan(offBest, new ExitModelSettings { MinimumImprovementFraction = 1, MinimumImprovementPence = 1000 }, Now);
        Assert.True(lax.Best!.IsCurrentListing);
        Assert.StartsWith("keep the listing", lax.Rationale);
    }

    [Fact]
    public void UncertainDemandMakesTheWaitSlowerThanAKnownRate()
    {
        // A known rate h gives an exponential wait: E[e^{−rτ}; τ<H] = h/(h+r)(1 − e^{−(h+r)H}). With demand only
        // known to within a Gamma(2) spread the wait is Gamma–Poisson: same mean rate, fatter tail.
        double h = 0.3, r = 0.01, horizon = 21;
        double exponential = h / (h + r) * (1 - Math.Exp(-(h + r) * horizon));
        double beta = 2.0; // α = 2: heavier tail, so less probability of selling in time
        double lomax = ExitPlanner.DiscountedSaleProbability(h, beta, horizon, r);
        Assert.True(lomax < exponential);
        Assert.Equal(1 - Math.Pow(beta / (beta + h * horizon), 2), ExitPlanner.DiscountedSaleProbability(h, beta, horizon, 0), 9);
        Assert.InRange(lomax, 0.75, 0.95);
    }

    [Fact]
    public void ExpensiveCapitalPrefersQuickerSales()
    {
        var demand = Demand(1.0);
        var patient = ExitPlanner.Plan(Context(demand, Book(260, 290)), new ExitModelSettings { CapitalCostPerDay = 0 }, Now).BestRelist!;
        var hurried = ExitPlanner.Plan(Context(demand, Book(260, 290)), new ExitModelSettings { CapitalCostPerDay = 0.03 }, Now).BestRelist!;
        Assert.True(hurried.Price <= patient.Price, $"{hurried.Price} vs {patient.Price}");
        Assert.True(hurried.ExpectedDaysToSell <= patient.ExpectedDaysToSell);
    }

    [Fact]
    public void TheBotsOwnSlowRelistsMarkDemandDown()
    {
        var demand = Demand(2);
        var neutral = ExitPlanner.Plan(Context(demand, Book(260, 290)), new ExitModelSettings(), Now).BestRelist!;
        var slow = ExitPlanner.Plan(Context(demand, Book(260, 290), calibration: new ExitCalibrationSnapshot { RelistTimeMultiplier = 3 }), new ExitModelSettings(), Now);
        Assert.Equal(1 / 3.0, slow.DemandMultiplier, 9);
        Assert.True(slow.BestRelist!.CertaintyEquivalentPence < neutral.CertaintyEquivalentPence);
    }

    [Fact]
    public void TheConvertersSteamHoldMakesTheSteamExitWorthLessAndRiskier()
    {
        // The wallet sits in converter items for Steam's 7-day hold before they can go to a CSFloat buyer.
        var calm = Context(null, Book(300, 330));
        calm.Environment = ExitEnvironment.Default with { ConverterHoldGrowth = 1, ConverterHoldSigma = 0 };
        var held = Context(null, Book(300, 330));
        held.Environment = ExitEnvironment.Default with { ConverterHoldGrowth = 0.97, ConverterHoldSigma = 0.15 };
        var a = ExitPlanner.Plan(calm, new ExitModelSettings(), Now).Best!;
        var b = ExitPlanner.Plan(held, new ExitModelSettings(), Now).Best!;
        Assert.Equal(a.NetIfSoldPence * 0.97, b.NetIfSoldPence, 9);
        Assert.True(b.StdDevPence > a.StdDevPence * 2);
        Assert.True(b.CertaintyEquivalentPence < a.CertaintyEquivalentPence * 0.96);
    }

    [Fact]
    public void AFullSteamWalletOrSteamsListingLimitRulesTheSteamExitOut()
    {
        var ctx = Context(Demand(3), Book(300, 330));
        ctx.Environment = ExitEnvironment.Default with { SteamWalletHeadroomPence = 100 }; // Steam would refuse the listing
        var plan = ExitPlanner.Plan(ctx, new ExitModelSettings(), Now);
        Assert.Null(plan.BestSteam);
        Assert.Contains("wallet", plan.Rationale);

        ctx.Environment = ExitEnvironment.Default with { SteamMaxListingPence = 250 };
        Assert.Null(ExitPlanner.Plan(ctx, new ExitModelSettings(), Now).BestSteam);
    }

    [Fact]
    public void WhileTheCSFloatAccountIsAwayNothingIsRelisted()
    {
        var ctx = Context(Demand(6), Book(260, 290));
        ctx.Environment = ExitEnvironment.Default with { CSFloatSellingPaused = true };
        var plan = ExitPlanner.Plan(ctx, new ExitModelSettings(), Now);
        Assert.Null(plan.BestRelist);
        Assert.Equal(ExitRoute.SteamMarket, plan.Best!.Route);
        Assert.Contains("away", plan.Rationale);
    }

    [Fact]
    public void AtPurchaseTheChanceTheTradeFallsThroughIsPriced()
    {
        var safePlan = ExitPlanner.Plan(Context(Demand(6), Book(260, 290), daysUntilTradable: 8), new ExitModelSettings(), Now);
        var risky = Context(Demand(6), Book(260, 290), daysUntilTradable: 8);
        risky.TradeFailureProbability = 0.2;
        var riskyPlan = ExitPlanner.Plan(risky, new ExitModelSettings(), Now);
        var safe = safePlan.Best!;
        var same = riskyPlan.Options.Single(o => o.Route == safe.Route && o.Price == safe.Price);
        // One time in five the seller never sends (or reverses inside protection): the 250p comes back, no profit.
        Assert.Equal(0.8 * safe.PresentValuePence + 0.2 * 250 * Math.Exp(-0.002), same.PresentValuePence, 6);
        Assert.True(same.CertaintyEquivalentPence < safe.CertaintyEquivalentPence);
        Assert.True(riskyPlan.Roi < safePlan.Roi);

        // Once the unit is tradable that risk is behind it.
        var tradable = Context(Demand(6), Book(260, 290));
        tradable.TradeFailureProbability = 0.2;
        Assert.Equal(ExitPlanner.Plan(Context(Demand(6), Book(260, 290)), new ExitModelSettings(), Now).Best!.CertaintyEquivalentPence,
            ExitPlanner.Plan(tradable, new ExitModelSettings(), Now).Best!.CertaintyEquivalentPence, 9);
    }

    [Fact]
    public void WithoutSteamTheRelistStandsAloneAndWithNeitherThereIsNoPlan()
    {
        var relistOnly = ExitPlanner.Plan(Context(Demand(3), null), new ExitModelSettings(), Now);
        Assert.Equal(ExitRoute.CSFloatRelist, relistOnly.Best!.Route);
        Assert.Contains("Steam unavailable", relistOnly.Rationale);

        var none = ExitPlanner.Plan(Context(Demand(3), null, relist: false), new ExitModelSettings(), Now);
        Assert.Null(none.Best);
        Assert.Contains("no exit can be valued", none.Rationale);
    }
}

public class ExitCalibrationTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WithoutATrackRecordTheModelIsTrusted()
    {
        var snapshot = ExitCalibration.Compute(Array.Empty<Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.PurchasedListing>(), Now);
        Assert.Equal(1.0, snapshot.RelistTimeMultiplier);
        Assert.Equal(0.0, snapshot.SteamForecastBiasLog);
        Assert.Null(snapshot.RealisedVsExpected);
    }

    [Fact]
    public void RelistTimingCountsUnsoldListingsAsEvidenceToo()
    {
        // One sold after 3× the predicted time; one still unsold after 2× → (3 prior + 3 + 2) / (3 prior + 1 sale) = 2.
        var sold = new Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.PurchasedListing { RelistTotalExposure = 3, ResaleSoldAtUtc = Now };
        var unsold = new Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.PurchasedListing { RelistTotalExposure = 2 };
        var snapshot = ExitCalibration.Compute(new[] { sold, unsold }, Now);
        Assert.Equal(2.0, snapshot.RelistTimeMultiplier, 9);
        Assert.Equal(2, snapshot.RelistEpisodes);
        Assert.Equal(1, snapshot.RelistSales);
    }

    [Fact]
    public void ForecastBiasComparesThePurchasePlanWithWhatTheSaleSaw()
    {
        var p = new Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.PurchasedListing
        {
            PurchasePlan = new ExitDecision { Stage = "purchase", SteamBidAtTradablePence = 100, AnchorAtTradableCents = 400 },
        };
        p.RecordExitDecision(new ExitDecision { Stage = "sale", ObservedSteamDepthBidPence = 90, ObservedAnchorCents = 440 });
        var snapshot = ExitCalibration.Compute(new[] { p }, Now);
        Assert.Equal(Math.Log(0.9) / 4, snapshot.SteamForecastBiasLog, 9);
        Assert.Equal(Math.Log(1.1) / 4, snapshot.CSFloatForecastBiasLog, 9);
        Assert.Equal(1, snapshot.ForecastChecks);
    }

    [Fact]
    public void RealisedResultsAreComparedWithWhatWasExpected()
    {
        var done = new Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.PurchasedListing
        {
            CurrentStrategicStage = Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.StrategicStages.StrategyCompleted,
            PurchaseCostPence = 500, ExpectedNetCashPence = 600, ActualAbsoluteProfitInPence = 50,
        };
        var snapshot = ExitCalibration.Compute(new[] { done }, Now);
        Assert.Equal(550 / 600.0, snapshot.RealisedVsExpected!.Value, 9);
        Assert.Equal(1, snapshot.CompletedPositions);
    }
}
