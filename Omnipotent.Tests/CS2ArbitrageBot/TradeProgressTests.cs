using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class TradeProgressTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static PurchasedListing Purchase(StrategicStages stage = StrategicStages.WaitingForCSFloatSellerToAcceptSale) => new()
    {
        CSFloatListingID = "c1",
        ItemMarketHashName = "AK-47 | Slate (Minimal Wear)",
        CurrentStrategicStage = stage,
        PurchaseCostPence = 500,
        comparison = new ScannedComparison { CSFloatListing = new CSFloatWrapper.ItemListing { PriceInPounds = 5, PriceText = "£5.00" } },
    };

    [Fact]
    public void QueuedTradeChangesNothing()
    {
        var p = Purchase();
        Assert.Equal(TradeTransition.None, TradeProgress.ApplyPurchaseTrade(p, JObject.Parse("{\"state\":\"queued\",\"contract_id\":\"c1\"}"), Now));
        Assert.Equal(StrategicStages.WaitingForCSFloatSellerToAcceptSale, p.CurrentStrategicStage);
        Assert.Equal("queued", p.LastTradeState);
    }

    [Fact]
    public void FollowsAcceptSendAndReceive()
    {
        var p = Purchase();
        Assert.Equal(TradeTransition.SellerAccepted, TradeProgress.ApplyPurchaseTrade(p,
            JObject.Parse("{\"state\":\"pending\",\"accepted_at\":\"2026-10-04T09:00:00Z\",\"steam_offer\":{\"is_from_seller\":true}}"), Now));
        Assert.Equal(StrategicStages.WaitingForCSFloatTradeToBeSent, p.CurrentStrategicStage);

        Assert.Equal(TradeTransition.TradeOfferSent, TradeProgress.ApplyPurchaseTrade(p,
            JObject.Parse("{\"state\":\"pending\",\"accepted_at\":\"2026-10-04T09:00:00Z\",\"steam_offer\":{\"id\":\"8907142679\",\"state\":2,\"sent_at\":\"2026-10-04T09:05:00Z\"}}"), Now));
        Assert.Equal(StrategicStages.WaitingForCSFloatTradeToBeAccepted, p.CurrentStrategicStage);
        Assert.Equal("https://steamcommunity.com/tradeoffer/8907142679/", p.CSFloatToSteamTradeOfferLink);

        Assert.Equal(TradeTransition.Retrieved, TradeProgress.ApplyPurchaseTrade(p, JObject.Parse(
            "{\"state\":\"pending\",\"accepted_at\":\"2026-10-04T09:00:00Z\",\"verification_mode\":\"escrow\",\"steam_offer\":{\"id\":\"8907142679\",\"state\":3,\"sent_at\":\"2026-10-04T09:05:00Z\",\"updated_at\":\"2026-10-04T09:20:00Z\"}," +
            "\"trade_protection_ends_at\":\"2026-10-11T09:20:00Z\",\"verify_sale_at\":\"2026-10-12T07:00:00Z\"}"), Now));
        Assert.Equal(StrategicStages.JustRetrieved, p.CurrentStrategicStage);
        Assert.Equal(new DateTime(2026, 10, 11, 10, 20, 0, DateTimeKind.Utc), p.PredictedTimeToBeResoldOnSteam);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 20, 0, DateTimeKind.Utc), p.TimeOfItemRetrieval);
    }

    [Fact]
    public void CancelledTradesStopBeingTracked()
    {
        // The old monitor had no branch for this: a cancelled purchase stayed "pending" forever.
        var p = Purchase();
        Assert.Equal(TradeTransition.Cancelled, TradeProgress.ApplyPurchaseTrade(p, JObject.Parse("{\"state\":\"cancelled\"}"), Now));
        Assert.Equal(StrategicStages.TradeCancelled, p.CurrentStrategicStage);
        Assert.Equal(TradeTransition.None, TradeProgress.ApplyPurchaseTrade(p, JObject.Parse("{\"state\":\"cancelled\"}"), Now));
    }

    [Fact]
    public void DeadSteamOffersDoNotAdvanceTheStage()
    {
        var p = Purchase(StrategicStages.WaitingForCSFloatTradeToBeSent);
        Assert.Equal(TradeTransition.None, TradeProgress.ApplyPurchaseTrade(p,
            JObject.Parse("{\"state\":\"pending\",\"steam_offer\":{\"id\":\"1\",\"state\":7,\"sent_at\":\"2026-10-04T09:05:00Z\"}}"), Now));
        Assert.Equal(StrategicStages.WaitingForCSFloatTradeToBeSent, p.CurrentStrategicStage);
    }

    [Fact]
    public void ResaleSaleIsAnnouncedOnceThenCompletesWithProfit()
    {
        var p = Purchase(StrategicStages.WaitingForCSFloatResale);
        p.CSFloatResalePriceCents = 900;
        var pending = JObject.Parse("{\"state\":\"pending\",\"contract\":{\"price\":900,\"state\":\"sold\"}}");
        Assert.Equal(TradeTransition.ResaleSold, TradeProgress.ApplyResaleTrade(p, pending, Now, 0.75));
        Assert.Equal(TradeTransition.None, TradeProgress.ApplyResaleTrade(p, pending, Now, 0.75));
        Assert.Equal(TradeTransition.ResaleCompleted, TradeProgress.ApplyResaleTrade(p, JObject.Parse("{\"state\":\"verified\",\"contract\":{\"price\":900}}"), Now, 0.75));
        Assert.Equal(StrategicStages.StrategyCompleted, p.CurrentStrategicStage);
        // 900c − 2% = 882c = £6.615 against £5.00 paid.
        Assert.Equal(1.615f, p.ActualAbsoluteProfitInPounds, 3);
    }

    [Fact]
    public void AResaleClosesTheListingsEvidenceWindowAtTheMomentItSold()
    {
        // Listed 4 days ago at a price expected to sell in 2 days; a buyer bought it a day ago.
        var p = Purchase(StrategicStages.WaitingForCSFloatResale);
        p.CSFloatResalePriceCents = 900;
        p.RelistBaseDaysToSell = 2;
        p.RelistExposureUpdatedUtc = Now.AddDays(-4);
        var pending = JObject.Parse("{\"state\":\"pending\",\"created_at\":\"2026-10-03T10:00:00Z\",\"contract\":{\"price\":900,\"state\":\"sold\"}}");
        Assert.Equal(TradeTransition.ResaleSold, TradeProgress.ApplyResaleTrade(p, pending, Now, 0.75));
        Assert.Equal(Now.AddDays(-1), p.ResaleSoldAtUtc);
        Assert.Equal(1.5, p.RelistTotalExposure, 9); // 3 days ÷ 2 expected
        Assert.Equal(1.5, p.RelistModelExposure, 9);

        // The account's real fee is used for the realised profit.
        Assert.Equal(TradeTransition.ResaleCompleted, TradeProgress.ApplyResaleTrade(p, JObject.Parse("{\"state\":\"verified\",\"contract\":{\"price\":900}}"), Now, 0.75, sellerFee: 0.05));
        Assert.Equal((855 * 0.75 / 100.0) - 5, p.ActualAbsoluteProfitInPounds, 2);
    }

    [Fact]
    public void StaleLegacyPositionsAndRepeatedFailuresAreNotAutoSold()
    {
        var fresh = Purchase(StrategicStages.JustRetrieved);
        fresh.PredictedTimeToBeResoldOnSteam = Now.AddHours(-2);
        Assert.True(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.IsAutoSellable(fresh, Now));

        // A pre-rewrite record (no cost recorded) whose sale date passed months ago was handled by hand.
        var legacy = Purchase(StrategicStages.JustRetrieved);
        legacy.PurchaseCostPence = 0;
        legacy.PredictedTimeToBeResoldOnSteam = Now.AddDays(-90);
        Assert.False(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.IsAutoSellable(legacy, Now));

        var failing = Purchase(StrategicStages.JustRetrieved);
        failing.SaleAttempts = global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.MaxSaleAttempts;
        Assert.False(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.IsAutoSellable(failing, Now));
    }

    private static CSFloatWrapper.InventoryItem Unit(string name, double? floatValue = null) =>
        new() { AssetId = Guid.NewGuid().ToString("N"), MarketHashName = name, Tradable = true, FloatValue = floatValue };

    private static PurchasedListing Held(string name, float floatValue = 0) => new()
    {
        CSFloatListingID = Guid.NewGuid().ToString("N"),
        ItemMarketHashName = name,
        ItemFloatValue = floatValue,
        CurrentStrategicStage = StrategicStages.JustRetrieved,
        PurchaseCostPence = 300,
        PredictedTimeToBeResoldOnSteam = Now.AddDays(-20),
    };

    [Fact]
    public void OnlyAShortfallOfUnitsMarksPositionsAsGone()
    {
        // Live, Nov 10 2026: positions sold by hand stayed "in inventory", failed every sale and were reported on every restart.
        var gamma = new[] { Held("Gamma 2 Case"), Held("Gamma 2 Case"), Held("Gamma 2 Case") };
        var macTen = Held("Souvenir MAC-10 | Amber Fade (Field-Tested)", 0.2512f);
        var spectrum = Held("Spectrum 2 Case");
        var inventory = new List<CSFloatWrapper.InventoryItem>
        {
            Unit("Gamma 2 Case"),                                              // one of the three is left
            Unit("Souvenir MAC-10 | Amber Fade (Field-Tested)", 0.31),         // another MAC-10 (Klives's own?): a unit all the same
            Unit("Spectrum 2 Case"), Unit("Spectrum 2 Case"),                  // more units than positions
        };

        var missing = global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.PositionsWithoutAUnit(gamma.Append(macTen).Append(spectrum), inventory);

        Assert.Equal(2, missing.Count);
        Assert.All(missing, m => Assert.Equal("Gamma 2 Case", m.ItemMarketHashName));
        Assert.DoesNotContain(macTen, missing);   // conservative: any unit of the item keeps a position open
        Assert.DoesNotContain(spectrum, missing);
    }

    [Fact]
    public void ASkinsUnitGoesToThePositionWithItsFloat()
    {
        var sold = Held("AK-47 | Slate (Minimal Wear)", 0.1001f);
        var kept = Held("AK-47 | Slate (Minimal Wear)", 0.1402f);
        var missing = global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.PositionsWithoutAUnit(
            new[] { sold, kept }, new[] { Unit("AK-47 | Slate (Minimal Wear)", 0.14020000123) });
        Assert.Equal(new[] { sold }, missing);
    }

    [Fact]
    public void OnlyUnlockedPositionsWaitingToBeSoldAreCheckedAgainstTheInventory()
    {
        var unlocked = Held("Gamma 2 Case");
        Assert.True(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.ShouldBeInInventory(unlocked, Now));

        var stillLocked = Held("Gamma 2 Case");
        stillLocked.PredictedTimeToBeResoldOnSteam = Now.AddDays(2);
        Assert.False(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.ShouldBeInInventory(stillLocked, Now));

        var listedOnSteam = Held("Gamma 2 Case");
        listedOnSteam.CurrentStrategicStage = StrategicStages.WaitingForMarketSaleOnSteam;
        Assert.False(global::Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot.ShouldBeInInventory(listedOnSteam, Now));
    }

    [Fact]
    public void CancelledResaleEitherStaysListedOrGoesBackForResale()
    {
        var stillListed = Purchase(StrategicStages.WaitingForCSFloatResale);
        Assert.Equal(TradeTransition.ResaleCancelled, TradeProgress.ApplyResaleTrade(stillListed, JObject.Parse("{\"state\":\"cancelled\",\"contract\":{\"state\":\"listed\"}}"), Now, 0.75));
        Assert.Equal(StrategicStages.WaitingForCSFloatResale, stillListed.CurrentStrategicStage);

        var delisted = Purchase(StrategicStages.WaitingForCSFloatResale);
        delisted.CSFloatResaleListingID = "r1";
        Assert.Equal(TradeTransition.ResaleCancelled, TradeProgress.ApplyResaleTrade(delisted, JObject.Parse("{\"state\":\"cancelled\",\"contract\":{\"state\":\"delisted\"}}"), Now, 0.75));
        Assert.Equal(StrategicStages.JustRetrieved, delisted.CurrentStrategicStage);
        Assert.Equal("", delisted.CSFloatResaleListingID);
    }
}
