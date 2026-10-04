using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class PriceTrendTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>One point per day for 14 days; price(daysAgo) supplied by the caller.</summary>
    private static List<SteamPricePoint> Daily(Func<int, double> priceDaysAgo, int purchases = 50) =>
        Enumerable.Range(0, 14).Select(d => new SteamPricePoint(Now.AddDays(-d).AddHours(-1), priceDaysAgo(d), purchases)).ToList();

    [Fact]
    public void FlatOrRisingPricesAreNeverCountedOn()
    {
        Assert.Equal(1.0, PriceTrend.ProjectedDeclineFactor(Daily(_ => 5.0), Now));
        Assert.Equal(1.0, PriceTrend.ProjectedDeclineFactor(Daily(d => 5.0 * Math.Pow(1.02, -d)), Now)); // rising 2%/day
    }

    [Fact]
    public void ASteadyDeclineIsProjectedOverTheHold()
    {
        // Falling 1% a day → about 0.99^8 ≈ 0.92 by the time trade protection lifts.
        double factor = PriceTrend.ProjectedDeclineFactor(Daily(d => 5.0 * Math.Pow(0.99, -d)), Now);
        Assert.InRange(factor, 0.90, 0.94);
    }

    [Fact]
    public void ThinOrMissingHistoryIsTreatedAsFlat()
    {
        Assert.Equal(1.0, PriceTrend.ProjectedDeclineFactor(null, Now));
        Assert.Equal(1.0, PriceTrend.ProjectedDeclineFactor(Daily(d => 5.0 * Math.Pow(0.9, -d), purchases: 0), Now));
        Assert.Equal(1.0, PriceTrend.ProjectedDeclineFactor(Daily(_ => 5.0).Take(3).ToList(), Now));
    }

    [Fact]
    public void ACrashIsClampedRatherThanExtrapolatedToZero()
    {
        Assert.Equal(0.5, PriceTrend.ProjectedDeclineFactor(Daily(d => 5.0 * Math.Pow(0.7, -d)), Now));
    }

    [Fact]
    public void TheTrendIsPricedIntoBothExits()
    {
        var listing = CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing("1", "Sticker | Monte (Holo) | Cologne 2026", 900, 1100)));
        var settings = new EvaluationSettings { MinimumPriceCents = 1 };
        var flat = OpportunityEvaluator.Evaluate(listing, null, 0.9, 0.75, settings, Now);
        var falling = OpportunityEvaluator.Evaluate(listing, null, 0.9, 0.75, settings, Now, trendFactor: 0.92);
        Assert.True(flat.ShouldBuy);              // ~14% relist ROI on today's price…
        Assert.False(falling.ShouldBuy);          // …but a loss-adjacent trade if the slide continues.
        Assert.Equal(0.92, falling.TrendFactor);
        Assert.True(falling.Relist.Roi < flat.Relist.Roi - 0.07);
    }
}
