using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class ConversionModelTests
{
    private const double Fx = 0.755163;
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Dreams & Nightmares Case", true)]
    [InlineData("Sticker | WARNING", true)]
    [InlineData("AK-47 | Slate (Minimal Wear)", false)]
    [InlineData("★ Karambit", false)]
    [InlineData("Charm | Die-cast AK", true)]
    public void OnlyFloatlessItemsAreConverters(string name, bool expected) =>
        Assert.Equal(expected, ConversionModel.IsCommodityLike(name));

    [Fact]
    public void RankingFiltersAndOrdersByPrior()
    {
        var list = new List<CSFloatWrapper.PriceListEntry>
        {
            new() { MarketHashName = "Sticker | WARNING", MinPriceCents = 190, Quantity = 300 },
            new() { MarketHashName = "Dreams & Nightmares Case", MinPriceCents = 99, Quantity = 31997 },
            new() { MarketHashName = "AK-47 | Slate (Minimal Wear)", MinPriceCents = 595, Quantity = 1326 }, // has wear
            new() { MarketHashName = "Sticker | Rare", MinPriceCents = 190, Quantity = 3 },                   // illiquid
            new() { MarketHashName = "Sticker | Stale Ask", MinPriceCents = 5600, Quantity = 300 },           // too expensive
        };
        var ranked = ConversionModel.RankCandidates(list, null, Fx, new ConversionModel.Options());
        Assert.Equal(new[] { "Dreams & Nightmares Case", "Sticker | WARNING" }, ranked.Select(c => c.MarketHashName)); // by listing count without a prior
    }

    private static SteamOrderBook Book(string sells) => SteamOrderBook.ParseOrderbookResponse(
        $"{{\"amtMaxBuyOrder\":100,\"amtMinSellOrder\":107,\"eCurrency\":2,\"cBuyOrders\":9000,\"cSellOrders\":9000,\"rgCompactBuyOrders\":[100,50],\"rgCompactSellOrders\":[{sells}]}}",
        "x", (m, c) => SteamCurrency.ToGbpPence(m, c, null), Now)!;

    [Fact]
    public void SteamSidePricesSeveralUnitsNotJustTheTopAsk()
    {
        var candidate = new ConverterCandidate { MarketHashName = "Dreams & Nightmares Case", CSFloatMinAskCents = 99 };
        ConversionModel.ApplySteamBook(candidate, Book("107,2,110,10"), Fx, units: 5);
        Assert.Equal((2 * 107 + 3 * 110) / 5.0, candidate.SteamUnitCostPence);
        double expected = ArbitrageMath.CSFloatNetProceedsCents(98, 0.02) * Fx / candidate.SteamUnitCostPence;
        Assert.Equal(expected, candidate.Coefficient, 6);
    }

    [Fact]
    public void SalesHistoryOverridesAnOptimisticAsk()
    {
        // Measured Oct 2026: D&N sells for ~$0.975 on CSFloat against a £1.07 Steam ask → ~0.68.
        var candidate = new ConverterCandidate { MarketHashName = "Dreams & Nightmares Case", CSFloatMinAskCents = 120 };
        ConversionModel.ApplySteamBook(candidate, Book("107,100"), Fx, units: 5);
        double askBased = candidate.Coefficient;
        var graph = Enumerable.Range(0, 7).Select(d => new CSFloatWrapper.SalesGraphPoint { DayUtc = Now.Date.AddDays(-d), Count = 1000, AveragePriceCents = 97.5 }).ToList();
        ConversionModel.ApplySalesGraph(candidate, graph, Fx, new ConversionModel.Options());
        Assert.True(candidate.VerifiedBySales);
        Assert.Equal(7000, candidate.CSFloatSales7d);
        Assert.InRange(candidate.Coefficient, 0.66, 0.69);
        Assert.True(candidate.Coefficient < askBased);
        Assert.Equal(7000 * 0.15 * 107, candidate.WeeklyCapacityPence, 6);
    }

    [Fact]
    public void ItemsThatNeverSellOnCSFloatAreUseless()
    {
        var candidate = new ConverterCandidate { MarketHashName = "Sticker | Nobody Buys", CSFloatMinAskCents = 56 };
        ConversionModel.ApplySteamBook(candidate, Book("7,100"), Fx, units: 5);
        Assert.True(candidate.Coefficient > 4); // the ask says "8× return"…
        ConversionModel.ApplySalesGraph(candidate, new List<CSFloatWrapper.SalesGraphPoint>(), Fx, new ConversionModel.Options());
        Assert.Equal(0, candidate.Coefficient); // …but nothing ever sells.
    }

    [Fact]
    public void CombineWeightsByCapacityAndPricesTheRemainderAtTheFallback()
    {
        var options = new ConversionModel.Options();
        var candidates = new[]
        {
            new ConverterCandidate { MarketHashName = "Good", Coefficient = 0.95, WeeklyCapacityPence = 1000, VerifiedBySales = true },
            new ConverterCandidate { MarketHashName = "Okay", Coefficient = 0.80, WeeklyCapacityPence = 1000, VerifiedBySales = true },
            new ConverterCandidate { MarketHashName = "Unverified", Coefficient = 0.99, WeeklyCapacityPence = 0, VerifiedBySales = false },
        };
        var small = ConversionModel.Combine(candidates, 1000, 0.68, options, Now);
        Assert.Equal(0.95 * 0.97, small.Coefficient, 6);

        var large = ConversionModel.Combine(candidates, 4000, 0.68, options, Now);
        double raw = (0.95 * 1000 + 0.80 * 1000 + 0.68 * 2000) / 4000;
        Assert.Equal(raw * 0.97, large.Coefficient, 6);
        Assert.Equal(2000, large.CapacityCoveredPence);
        Assert.Contains("remainder", large.Basis);
    }

    [Fact]
    public void CombineFallsBackAndClamps()
    {
        var options = new ConversionModel.Options();
        var none = ConversionModel.Combine(Array.Empty<ConverterCandidate>(), 1000, 0.68, options, Now);
        Assert.Equal(0.68, none.Coefficient);
        Assert.Contains("default", none.Basis);

        var absurd = ConversionModel.Combine(new[] { new ConverterCandidate { MarketHashName = "x", Coefficient = 1.25, WeeklyCapacityPence = 5000, VerifiedBySales = true } }, 1000, 0.68, options, Now);
        Assert.Equal(options.MaximumCoefficient, absurd.Coefficient);
    }
}
