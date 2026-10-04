using Omnipotent.Services.CS2ArbitrageBot.Engine;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class ArbitrageMathTests
{
    [Fact]
    public void SteamFeesMatchALiveListing()
    {
        // Live listing, Oct 2026: unPrice 282 (seller receives) + unFee 42 = £3.24 shown to buyers.
        Assert.Equal(324, ArbitrageMath.SteamBuyerPays(282));
        Assert.Equal(282, ArbitrageMath.SteamSellerReceives(324));
    }

    [Fact]
    public void SteamFeeMinimumsApplyToCheapItems()
    {
        Assert.Equal(3, ArbitrageMath.SteamBuyerPays(1));   // 1p + 1p Valve + 1p CS2
        Assert.Equal(0, ArbitrageMath.SteamSellerReceives(2));
        Assert.Equal(1, ArbitrageMath.SteamSellerReceives(3));
        Assert.Equal(0, ArbitrageMath.SteamBuyerPays(0));
    }

    [Fact]
    public void SellerReceivesIsTheExactInverseOfBuyerPays()
    {
        for (int seller = 1; seller <= 20000; seller++)
            Assert.Equal(seller, ArbitrageMath.SteamSellerReceives(ArbitrageMath.SteamBuyerPays(seller)));
    }

    [Fact]
    public void SellerReceivesIsTheLargestAmountThatFitsTheBuyerPrice()
    {
        for (int buyer = 3; buyer <= 20000; buyer++)
        {
            int receives = ArbitrageMath.SteamSellerReceives(buyer);
            Assert.True(ArbitrageMath.SteamBuyerPays(receives) <= buyer, $"buyer {buyer}");
            Assert.True(ArbitrageMath.SteamBuyerPays(receives + 1) > buyer, $"buyer {buyer} not maximal");
        }
    }

    [Fact]
    public void OldDivideBy115ShortcutWasWrongForSomePrices()
    {
        // floor(p/1.15) can list a penny above the intended buyer price (the item then sits unsold).
        int mismatches = Enumerable.Range(3, 5000).Count(p => ArbitrageMath.SteamBuyerPays((int)Math.Floor(p / 1.15)) > p);
        Assert.True(mismatches > 0);
    }

    [Theory]
    [InlineData(100, 0.755163, 76, 75)]
    [InlineData(9846, 0.755163, 7436, 7435)]
    [InlineData(0, 0.75, 0, 0)]
    public void CurrencyRoundingNeverFlattersTheTrade(int cents, double fx, int ceil, int floor)
    {
        Assert.Equal(ceil, ArbitrageMath.UsdCentsToPenceCeil(cents, fx));
        Assert.Equal(floor, ArbitrageMath.UsdCentsToPenceFloor(cents, fx));
    }

    [Fact]
    public void CSFloatFeeIsTakenFromTheSale()
    {
        Assert.Equal(980, ArbitrageMath.CSFloatNetProceedsCents(1000, 0.02));
        Assert.Equal(99, ArbitrageMath.CSFloatNetProceedsCents(102, 0.02));
    }
}
