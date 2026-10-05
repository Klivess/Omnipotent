using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class CSFloatDemandTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);
    private const string Sticker = "Sticker | Test (Holo) | Cologne 2026";

    private static List<CSFloatWrapper.SalesGraphPoint> Graph(Func<int, int> salesDaysAgo, int days = 60) =>
        CSFloatWrapper.ParseSalesGraph(CS2Fixtures.SalesGraph(Now, salesDaysAgo, 400, days));

    private static List<CSFloatWrapper.RecentSale> Sales(params (int Price, int Value, double HoursListed, double SoldHoursAgo)[] sales) =>
        CSFloatWrapper.ParseRecentSales(CS2Fixtures.RecentSales(Now, sales));

    private static CSFloatListing Rival(string id, int price, int value = 400, string seller = "76561190000000001") =>
        CSFloatListing.FromJson(JToken.Parse(CS2Fixtures.Listing(id, Sticker, price, value, value, floatValue: null).Replace("76561190000000001", seller)));

    [Fact]
    public void TheSalesRateCountsDaysWithoutSales()
    {
        // Three days with 10 sales each in the last month. Averaging the graph's entries says 10/day; the
        // calendar says ~1/day (the measured trap: one sticker read 1.3/day that way and really sold 0.04/day).
        var demand = CSFloatDemand.Build(Sticker, 400, Graph(d => d is 2 or 9 or 20 ? 10 : 0), null, null, null, null, null, Now);
        Assert.InRange(demand.SalesPerDay, 0.9, 1.6);
        Assert.Equal(30, demand.EvidenceDays);
        Assert.InRange(demand.DaysSinceLastSale, 1.0, 2.5);
    }

    [Fact]
    public void WithoutAGraphARecentSalesPageGivesTheRate()
    {
        var sales = Enumerable.Range(0, 40).Select(i => (400, 400, 2.0, i * 2.4)).ToArray(); // 40 sales over ~4 days
        var demand = CSFloatDemand.Build(Sticker, 400, null, Sales(sales), null, null, null, null, Now);
        Assert.InRange(demand.SalesPerDay, 7, 9);
        Assert.Contains("recent sales", demand.SalesRateBasis);
    }

    [Fact]
    public void WithoutAnyHistoryTheReferenceQuantityIsAConservativeProxy()
    {
        var demand = CSFloatDemand.Build(Sticker, 400, null, null, null, referenceQuantity: 550, null, null, Now);
        Assert.Equal(7.0, demand.SalesPerDay, 9);
        Assert.False(demand.HasSalesData);
        Assert.Contains("proxy", demand.SalesRateBasis);
    }

    [Fact]
    public void BuyersAcceptanceFallsAsThePriceRises()
    {
        // Recent buyers paid 0.91–0.97 of CSFloat's value.
        var sales = Enumerable.Range(0, 20).Select(i => (364 + 4 * (i % 7), 400, 12.0, i * 6.0)).ToArray();
        var demand = CSFloatDemand.Build(Sticker, 400, null, Sales(sales), null, null, null, null, Now);
        Assert.True(demand.AcceptanceProbability(0.88) > 0.85);
        Assert.InRange(demand.AcceptanceProbability(0.94), 0.35, 0.65);
        Assert.True(demand.AcceptanceProbability(1.02) < 0.15);
        Assert.InRange(demand.MedianValueRatio, 0.93, 0.95);
    }

    [Fact]
    public void OnlyBetterPricedRivalsAreAheadAndTheAccountsOwnListingsAreNot()
    {
        var rivals = new List<CSFloatListing>
        {
            Rival("a", 380), Rival("b", 390), Rival("c", 400), Rival("d", 420),
            Rival("mine", 350, seller: "76561190000000002"), // the account's own listing
            Rival("x", 300),                                  // the listing being valued (excluded by id)
        };
        var demand = CSFloatDemand.Build(Sticker, 400, null, null, rivals, null, "76561190000000002", "x", Now);
        Assert.Equal(4, demand.CompetitorRatios.Count);
        Assert.Equal(380, demand.LowestCompetitorCents);
        Assert.Equal(2, demand.CompetitorsAhead(0.99));
        Assert.Equal(2.5, demand.CompetitorsAhead(1.0)); // a tie counts half
        Assert.Equal(0, demand.CompetitorsAhead(0.9));

        // Skin buyers also choose by float, so the same queue is softer.
        var skin = CSFloatDemand.Build("AK-47 | Slate (Field-Tested)", 400, null, null, rivals, null, "76561190000000002", "x", Now);
        Assert.Equal(2 * CSFloatDemand.SoftQueueWeight, skin.CompetitorsAhead(0.99), 9);
    }

    [Fact]
    public void SkinsArePricedAgainstPlainSalesNotStickerCrafts()
    {
        // Live AK-47 | Slate (FT), Oct 2026: stickered units sold at 1.1–2.4× value, plain ones at ~1.00×.
        var sales = Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? 400 + i % 3 : 520 + 20 * (i % 5), 400, 30.0, i * 3.0)).ToArray();
        var parsed = CSFloatWrapper.ParseRecentSales(CS2Fixtures.RecentSales(Now, sales, i => i % 2 == 0 ? 0 : 4));
        var skin = CSFloatDemand.Build("AK-47 | Slate (Field-Tested)", 400, null, parsed, null, null, null, null, Now);
        Assert.Equal(10, skin.ValueRatios.Count);
        Assert.InRange(skin.MedianValueRatio, 0.99, 1.01);
        Assert.True(skin.AcceptanceProbability(1.10) < 0.1);

        // Too few plain sales to go on: the premium is capped rather than believed.
        var mostlyStickered = CSFloatWrapper.ParseRecentSales(CS2Fixtures.RecentSales(Now, sales, i => i < 3 ? 0 : 4));
        var capped = CSFloatDemand.Build("AK-47 | Slate (Field-Tested)", 400, null, mostlyStickered, null, null, null, null, Now);
        Assert.All(capped.ValueRatios, r => Assert.True(r <= 1.15));
    }

    [Fact]
    public void AnItemNobodyBuysNeverSellsWhateverItsReferenceSays()
    {
        var demand = CSFloatDemand.Build(Sticker, 400, Graph(d => d is 110 or 130 ? 1 : 0, days: 150),
            Sales((400, 400, 900, 110 * 24), (395, 400, 700, 130 * 24)), null, null, null, null, Now);
        Assert.True(demand.SalesPerDay < 0.02, $"rate {demand.SalesPerDay}");
        Assert.True(demand.DaysSinceLastSale > 100);
        Assert.True(demand.ExpectedDaysToSell(0.9) > 150);
    }

    [Fact]
    public void TimeToSellRisesWithPriceAndWithWeakerDemand()
    {
        var sales = Enumerable.Range(0, 20).Select(i => (376 + 2 * (i % 7), 400, 20.0, i * 8.0)).ToArray();
        var rivals = new List<CSFloatListing> { Rival("a", 388), Rival("b", 396), Rival("c", 404) };
        var demand = CSFloatDemand.Build(Sticker, 400, Graph(_ => 3), Sales(sales), rivals, null, null, null, Now);
        double cheap = demand.ExpectedDaysToSell(0.92), market = demand.ExpectedDaysToSell(0.98), dear = demand.ExpectedDaysToSell(1.06);
        Assert.True(cheap < market && market < dear, $"{cheap:F2} / {market:F2} / {dear:F2}");
        Assert.Equal(2 * demand.ExpectedDaysToSell(0.95), demand.ExpectedDaysToSell(0.95, demandMultiplier: 0.5), 6);
    }
}
