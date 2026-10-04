using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class OpportunityEvaluatorTests
{
    private const double Fx = 0.75; // GBP per USD, round for readable expectations
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static CSFloatListing Listing(int price, int? basePrice = 1000, int? predicted = null, int refQty = 500, bool away = false, int trades = 300, int failed = 0, int medianTrade = 600) =>
        CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing("42", "AK-47 | Slate (Minimal Wear)", price, basePrice, predicted, refQty,
            sellerAway: away, sellerTrades: trades, sellerFailed: failed, medianTradeSeconds: medianTrade)));

    private static SteamOrderBook Book(int maxBuy, int minSell, int buyOrders = 5000) =>
        SteamOrderBook.ParseOrderbookResponse(CS2Fixtures.SteamBook(maxBuy, minSell, buyOrders), "AK-47 | Slate (Minimal Wear)",
            (m, c) => SteamCurrency.ToGbpPence(m, c, null), Now)!;

    private static EvaluationSettings Settings(bool relist = true) => new() { AllowRelistExit = relist, MinimumPriceCents = 1 };

    [Fact]
    public void SteamExitMathIsExact()
    {
        // $10 listing = 750p. Book: 1200p ×10 then 1199p ×40 → depth-3 price 1200; 3% haircut → 1164.
        var e = OpportunityEvaluator.Evaluate(Listing(1000, basePrice: null), Book(1200, 1250), 0.9, Fx, Settings(), Now);
        int receives = ArbitrageMath.SteamSellerReceives(1164);
        Assert.Equal(750, e.CostPence);
        Assert.Equal(1164, e.Steam.ExpectedSalePrice);
        Assert.Equal((int)Math.Floor(receives * 0.9), e.Steam.NetCashPence);
        Assert.Equal(e.Steam.NetCashPence - 750, e.Steam.ProfitPence);
        Assert.True(e.ShouldBuy);
        Assert.Equal(ExitRoute.SteamMarket, e.BestRoute);
    }

    [Fact]
    public void TheSameListingIsALossAtTheRealCaseConversionRate()
    {
        // At k = 0.68 (what cases actually convert at) the same trade loses money — the old model's blind spot.
        var e = OpportunityEvaluator.Evaluate(Listing(1000, basePrice: null), Book(1200, 1250), 0.68, Fx, Settings(), Now);
        Assert.True(e.Steam.Roi < 0.10);
        Assert.False(e.ShouldBuy);
    }

    [Fact]
    public void ThinOrCrossedSteamBooksAreNotSellable()
    {
        var thin = OpportunityEvaluator.Evaluate(Listing(500, basePrice: null), Book(1200, 1250, buyOrders: 3), 0.9, Fx, Settings(), Now);
        Assert.False(thin.Steam.Available);
        Assert.Contains("buy orders", thin.Steam.UnavailableReason);

        var crossed = OpportunityEvaluator.Evaluate(Listing(500, basePrice: null), Book(1500, 1000), 0.9, Fx, Settings(), Now);
        Assert.False(crossed.Steam.Available);
        Assert.Contains("crossed", crossed.Steam.UnavailableReason);
    }

    [Fact]
    public void RelistExitValuesAtTheLowerOfBaseAndFloatAdjustedPrice()
    {
        // base $15, float-adjusted $14 → sell at 14×0.95 = $13.30, net of 2% = 1303c = 977p vs cost 750p.
        var e = OpportunityEvaluator.Evaluate(Listing(1000, basePrice: 1500, predicted: 1400), null, 0.9, Fx, Settings(), Now);
        Assert.True(e.Relist.Available);
        Assert.Equal(1330, e.Relist.ExpectedSalePrice);
        Assert.Equal(977, e.Relist.NetCashPence);
        Assert.True(e.ShouldBuy);
        Assert.Equal(ExitRoute.CSFloatRelist, e.BestRoute);

        // A float premium (predicted above base) is never counted.
        var premium = OpportunityEvaluator.Evaluate(Listing(1000, basePrice: 1100, predicted: 3000), null, 0.9, Fx, Settings(), Now);
        Assert.Equal((int)Math.Floor(1100 * 0.95), premium.Relist.ExpectedSalePrice);
    }

    [Fact]
    public void RelistExitNeedsCSFloatLiquidityAndCanBeDisabled()
    {
        Assert.False(OpportunityEvaluator.Evaluate(Listing(1000, 1500, refQty: 5), null, 0.9, Fx, Settings(), Now).Relist.Available);
        var disabled = OpportunityEvaluator.Evaluate(Listing(1000, 1500), null, 0.9, Fx, Settings(relist: false), Now);
        Assert.False(disabled.Relist.Available);
        Assert.False(disabled.ShouldBuy);
    }

    [Fact]
    public void ExtremeDiscountsMustBeCorroboratedBySteam()
    {
        // $5 for an item CSFloat values at $14: a fat-finger or a broken reference.
        var unverified = OpportunityEvaluator.Evaluate(Listing(500, 1400), null, 0.9, Fx, Settings(), Now);
        Assert.False(unverified.Relist.Available);

        var contradicted = OpportunityEvaluator.Evaluate(Listing(500, 1400), Book(200, 210), 0.9, Fx, Settings(), Now);
        Assert.False(contradicted.Relist.Available);

        var corroborated = OpportunityEvaluator.Evaluate(Listing(500, 1400), Book(1400, 1450), 0.9, Fx, Settings(), Now);
        Assert.True(corroborated.Relist.Available);
        Assert.True(corroborated.ShouldBuy);
    }

    [Fact]
    public void UnreliableSellersAreRejectedEvenWhenProfitable()
    {
        var away = OpportunityEvaluator.Evaluate(Listing(1000, 1500, away: true), null, 0.9, Fx, Settings(), Now);
        Assert.False(away.ShouldBuy);
        Assert.Contains("away", away.Reason);

        var flaky = OpportunityEvaluator.Evaluate(Listing(1000, 1500, trades: 100, failed: 20), null, 0.9, Fx, Settings(), Now);
        Assert.False(flaky.ShouldBuy);
        Assert.Contains("failure rate", flaky.Reason);

        var slow = OpportunityEvaluator.Evaluate(Listing(1000, 1500, medianTrade: 2 * 24 * 3600), null, 0.9, Fx, Settings(), Now);
        Assert.False(slow.ShouldBuy);

        // A brand-new seller with a couple of failures is not judged on a tiny sample.
        Assert.True(OpportunityEvaluator.Evaluate(Listing(1000, 1500, trades: 5, failed: 2), null, 0.9, Fx, Settings(), Now).ShouldBuy);
    }

    [Fact]
    public void TheMoreProfitableExitWinsWhenBothQualify()
    {
        var e = OpportunityEvaluator.Evaluate(Listing(1000, 1500, predicted: 1500), Book(1600, 1650), 0.95, Fx, Settings(), Now);
        Assert.True(e.Steam.MeetsThreshold);
        Assert.True(e.Relist.MeetsThreshold);
        var expected = e.Steam.ProfitPence >= e.Relist.ProfitPence ? ExitRoute.SteamMarket : ExitRoute.CSFloatRelist;
        Assert.Equal(expected, e.BestRoute);
        Assert.Equal(Math.Max(e.Steam.ProfitPence, e.Relist.ProfitPence), e.BestProfitPence);
    }

    [Fact]
    public void MinimumAbsoluteProfitFiltersPennyTrades()
    {
        var settings = Settings();
        settings.MinimumProfitPence = 40;
        // 35% ROI on a 26c item is still only a few pence.
        var e = OpportunityEvaluator.Evaluate(Listing(26, 40), null, 0.9, Fx, settings, Now);
        Assert.True(e.Relist.Roi > 0.3);
        Assert.False(e.ShouldBuy);
    }

    [Fact]
    public void OptimisticBoundIsInfiniteWhenUnknown()
    {
        Assert.Equal(double.PositiveInfinity, OpportunityEvaluator.OptimisticSteamRoi(100, 0, 0.9));
        double bound = OpportunityEvaluator.OptimisticSteamRoi(1000, 1150, 0.9);
        Assert.Equal(ArbitrageMath.SteamSellerReceives(1150) * 0.9 / 1000 - 1, bound, 9);
    }
}
