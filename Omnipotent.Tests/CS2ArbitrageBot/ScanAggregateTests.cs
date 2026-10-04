using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class ScanAggregateTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static OpportunityEvaluation Eval(double roi, bool buy = false, string name = "x", int cost = 500, double? fl = 0.2, int steamBo = 600) => new()
    {
        MarketHashName = name,
        BestRoi = roi,
        CostPence = cost,
        FloatValue = fl,
        SteamHighestBuyOrderPence = steamBo,
        EvaluatedAtUtc = Now,
        ShouldBuy = buy,
        BestRoute = ExitRoute.SteamMarket,
        Steam = new ExitEstimate { Available = true, Route = ExitRoute.SteamMarket, Roi = roi },
    };

    [Fact]
    public void AggregateBucketsMatchTheKmPageContract()
    {
        var aggregate = new Scanalytics.ScanAggregate();
        foreach (var roi in new[] { -0.3, -0.01, 0.02, 0.07, 0.15, 0.25 }) aggregate.Record(Eval(roi, buy: roi >= 0.1, name: "item" + roi));
        var analytics = new Scanalytics.ScannedComparisonAnalytics(aggregate, new List<Scanalytics.PurchasedListing>(), 0.8);

        Assert.Equal(6, analytics.TotalListingsScanned);
        Assert.Equal(2, analytics.NumberOfListingsBelow0PercentGain);
        Assert.Equal(1, analytics.NumberOfListingsBetween0And5PercentGain);
        Assert.Equal(1, analytics.NumberOfListingsBetween5And10PercentGain);
        Assert.Equal(1, analytics.NumberOfListingsBetween10And20PercentGain);
        Assert.Equal(1, analytics.NumberOfListingsAbove20PercentGain);
        Assert.Equal(6.0, analytics.MeanPriceOfListingsBelow0PercentGain);
        Assert.Equal(1.25, analytics.HighestPredictedGainFoundSoFar, 9);
        Assert.Equal("item0.25", analytics.NameOfItemWithHighestPredictedGain);
        Assert.Equal(4, analytics.CountListingsWithPositiveGain);
        Assert.Equal(2, analytics.CountListingsWithNegativeGain);
        Assert.Equal(4 * 100.0 / 6, analytics.PercentageChanceOfFindingPositiveGainListing, 9);
        Assert.Equal(2, analytics.QualifiedOpportunities);
        Assert.Equal((1.15 * 1.25 - 1) * 100, analytics.TotalExpectedProfitPercent, 3);
        Assert.Equal(0.8, analytics.CurrentExpectedReturnCoefficientOfSteamToCSFloat);
        Assert.Equal(6, analytics.Daily["2026-10-04"].Evaluated);
    }

    [Fact]
    public void AggregateSurvivesAJsonRoundTrip()
    {
        var aggregate = new Scanalytics.ScanAggregate();
        aggregate.Record(Eval(0.12, buy: true));
        var copy = JsonConvert.DeserializeObject<Scanalytics.ScanAggregate>(JsonConvert.SerializeObject(aggregate))!;
        Assert.Equal(1, copy.TotalEvaluated);
        Assert.Equal(0.12, copy.HighestRoi);
        Assert.Equal(1, copy.BucketCounts[3]);
        // And the KM DTO serialises (no infinities, no cycles).
        var json = JObject.FromObject(new Scanalytics.ScannedComparisonAnalytics(copy, new(), 0.7));
        Assert.Equal(1, (int?)json["TotalListingsScanned"]);
    }

    [Fact]
    public void OnlyNotableEvaluationsAreKeptInFull()
    {
        var analytics = new Scanalytics(null!);
        var settings = new EvaluationSettings { MinimumSteamRoiPercent = 10, MinimumRelistRoiPercent = 12 };
        analytics.RecordEvaluation(Eval(-0.40), settings);
        analytics.RecordEvaluation(Eval(0.05), settings); // within 10 points of the 10% threshold
        analytics.RecordEvaluation(Eval(0.30, buy: true), settings);
        var notable = analytics.RecentNotable();
        Assert.Equal(2, notable.Count);
        Assert.Equal(0.30, notable[0].BestRoi);
        Assert.Equal(3, analytics.SnapshotAggregate().TotalEvaluated);
    }

    [Fact]
    public void ScanCyclesAreBounded()
    {
        var analytics = new Scanalytics(null!);
        for (int i = 0; i < Scanalytics.RecentCycleCapacity + 50; i++) analytics.RecordCycle(new Scanalytics.ScanCycleSummary { Strategy = "feed", Evaluated = i });
        var cycles = analytics.RecentCycles();
        Assert.Equal(Scanalytics.RecentCycleCapacity, cycles.Count);
        Assert.Equal(Scanalytics.RecentCycleCapacity + 49, cycles[^1].Evaluated);
    }

    [Fact]
    public void LegacyComparisonUsesExactSteamFees()
    {
        var csfloat = new CSFloatWrapper.ItemListing { PriceInPounds = 5.00, PriceInPence = 500 };
        var steam = new SteamAPIWrapper.ItemListing { HighestBuyOrderPriceInPence = 690, HighestBuyOrderPriceInPounds = 6.90 };
        var comparison = new Scanalytics.ScannedComparison(csfloat, steam, Now, 0.8);
        double receives = ArbitrageMath.SteamSellerReceives(690) / 100.0;
        Assert.Equal(6.90 / 5.00, comparison.RawArbitrageGain, 9);
        Assert.Equal(receives / 5.00, comparison.ArbitrageGainAfterSteamTax, 9);
        Assert.Equal(receives * 0.8 / 5.00, comparison.PredictedOverallArbitrageGain, 9);
    }

    [Fact]
    public void OldPurchaseFilesStillDeserialise()
    {
        // Shape of a pre-rewrite PurchasedItems file (no v2 fields, stage as an int).
        string legacy = "{\"comparison\":{\"ItemMarketHashName\":\"Sticker | Graviti | Shanghai 2024\",\"CSFloatListing\":{\"ItemListingID\":\"1\",\"PriceText\":\"£0.10\",\"PriceInPounds\":0.1}," +
            "\"SteamListing\":{\"Name\":\"Sticker | Graviti | Shanghai 2024\",\"HighestBuyOrderPriceInPounds\":0.2,\"NameColor\":\"210, 210, 210\"},\"LastUpdate\":\"2025-07-17T20:53:33\"}," +
            "\"CSFloatListingID\":\"1\",\"ExpectedProfitPercentage\":-100.0,\"TimeOfPurchase\":\"2025-07-17T20:53:33.9895357+01:00\",\"ItemMarketHashName\":\"Sticker | Graviti | Shanghai 2024\",\"CurrentStrategicStage\":3}";
        var p = JsonConvert.DeserializeObject<Scanalytics.PurchasedListing>(legacy)!;
        Assert.Equal(Scanalytics.StrategicStages.JustRetrieved, p.CurrentStrategicStage);
        Assert.Equal("SteamMarket", p.PlannedExit);
        Assert.Equal("£0.10", p.comparison.CSFloatListing.PriceText);
    }
}
