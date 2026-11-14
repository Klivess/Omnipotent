using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using Omnipotent.Services.CS2ArbitrageBot.Steam;
using System.Net;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;
using Bot = Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBot;

namespace Omnipotent.Tests.CS2ArbitrageBot;

/// <summary>
/// What the rebuilt CS2 Arbitrage page relies on, each reproduced from the live server on 2026-11-13: alerts re-sent
/// after every restart, one item Steam has no book for fetched every sweep, a refused Steam token retried every 10 minutes,
/// "qualified" counted before the exit model rejected it, a 526 KB analytics poll, and trades outside the newest 100.
/// </summary>
public class Cs2ConsoleBackendTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);

    // ── Alerts survive a restart ──

    [Fact]
    public async Task AnAlertSentBeforeARestartIsNotSentAgainAfterIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs2-alerts-{Guid.NewGuid():N}.json");
        try
        {
            var before = new AlertLedger(path);
            before.Load();
            Assert.True(before.TryTake("relist-overdue:1026", TimeSpan.FromHours(12), T0, "CS2 Arbitrage — relist overdue"));
            Assert.False(before.TryTake("relist-overdue:1026", TimeSpan.FromHours(12), T0.AddHours(1), "CS2 Arbitrage — relist overdue"));
            Assert.True(await before.SaveIfChangedAsync((p, json) => File.WriteAllTextAsync(p, json), T0));
            Assert.False(await before.SaveIfChangedAsync((p, json) => File.WriteAllTextAsync(p, json), T0)); // nothing new

            // The process restarts: the in-memory cooldown is gone, the saved one is not.
            var after = new AlertLedger(path);
            after.Load();
            Assert.False(after.TryTake("relist-overdue:1026", TimeSpan.FromHours(12), T0.AddHours(2)));
            // What went out, and when, survives too (the status route lists it).
            var sentBefore = Assert.Single(after.Recent(10));
            Assert.Equal(("relist-overdue:1026", T0, "CS2 Arbitrage — relist overdue"), (sentBefore.Key, sentBefore.AtUtc, sentBefore.Title));
            Assert.True(after.TryTake("relist-overdue:1026", TimeSpan.FromHours(12), T0.AddHours(13), "CS2 Arbitrage — relist overdue"));
            Assert.True(after.TryTake("steam-source", TimeSpan.FromHours(6), T0.AddHours(2), "CS2 Arbitrage — Steam prices unavailable"));
            Assert.Equal(new[] { "relist-overdue:1026", "steam-source" }, after.Recent(10).Select(a => a.Key).ToArray()); // newest first
            Assert.Single(after.Recent(1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ACorruptLedgerStartsEmptyRatherThanFailing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs2-alerts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{not json");
        try
        {
            var ledger = new AlertLedger(path);
            ledger.Load();
            Assert.Equal(0, ledger.Count);
            Assert.True(ledger.TryTake("x", TimeSpan.FromHours(1), T0));
        }
        finally { File.Delete(path); }
    }

    // ── An item Steam has no book for ──

    private const string NoBookItem = "Souvenir PP-Bizon | Wood Block Camo (Battle-Scarred)";

    [Fact]
    public async Task AnItemSteamHasNoBookForIsBackedOffInsteadOfFetchedEverySweep()
    {
        var steam = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{\"data\":{\"success\":false}}"));
        var market = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: steam);

        Assert.Null(await market.GetOrderBookAsync(NoBookItem, TimeSpan.FromMinutes(15), RequestPriority.Low));
        for (int sweep = 0; sweep < 5; sweep++)
            Assert.Null(await market.GetOrderBookAsync(NoBookItem, TimeSpan.FromMinutes(15), RequestPriority.Low));

        Assert.Equal(1, steam.Count(_ => true));    // live: one request per sweep for 13.7 h
        Assert.Equal(1, market.Misses);
        Assert.Equal(5, market.MissSkips);
        Assert.Equal(0, market.ConsecutiveFailures); // Steam answered; the source is fine
        Assert.Equal(0, market.Failures);
        Assert.Equal(1, market.KnownMisses);
        Assert.Contains("no book", market.LastError);

        // A pre-purchase re-check always asks Steam.
        await market.GetOrderBookAsync(NoBookItem, TimeSpan.FromMinutes(1), RequestPriority.Critical);
        Assert.Equal(2, steam.Count(_ => true));
    }

    [Fact]
    public async Task AnOutageIsNotMistakenForAnItemWithoutABook()
    {
        var steam = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{}", HttpStatusCode.BadGateway));
        var market = new SteamMarketClient(() => null, _ => null, TimeSpan.FromMilliseconds(1), handler: steam);
        await market.GetOrderBookAsync("AK-47 | Slate (Field-Tested)", TimeSpan.FromMinutes(15), RequestPriority.Low);
        await market.GetOrderBookAsync("AK-47 | Slate (Field-Tested)", TimeSpan.FromMinutes(15), RequestPriority.Low);
        Assert.Equal(2, steam.Count(_ => true)); // retried: a 502 says nothing about the item
        Assert.Equal(0, market.Misses);
        Assert.Equal(2, market.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(4, 120)]
    [InlineData(7, 720)]
    [InlineData(30, 720)]
    public void TheBackOffDoublesToTwelveHours(int misses, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SteamMarketClient.MissBackoff(misses));

    // ── A refresh token Steam refused ──

    [Fact]
    public void ARefusedTokenIsNotRetriedByBackgroundChecksForSixHours()
    {
        string token = "eyJ.refused.token";
        string? fp = SteamAPIProfileWrapper.TokenFingerprint(token);
        Assert.NotNull(fp);
        Assert.True(SteamAPIProfileWrapper.ShouldSkipRejectedToken(token, fp, T0, T0.AddMinutes(10)));   // the next 10-minute probe
        Assert.True(SteamAPIProfileWrapper.ShouldSkipRejectedToken(token, fp, T0, T0.AddHours(5.9)));
        Assert.False(SteamAPIProfileWrapper.ShouldSkipRejectedToken(token, fp, T0, T0.AddHours(6)));      // tried again after 6 h
        Assert.False(SteamAPIProfileWrapper.ShouldSkipRejectedToken("eyJ.new.token", fp, T0, T0.AddMinutes(10))); // a new login is tried at once
        Assert.False(SteamAPIProfileWrapper.ShouldSkipRejectedToken(token, null, T0, T0.AddMinutes(10)));
        Assert.NotEqual(token, fp); // only a fingerprint is held, never the token
    }

    // ── The funnel: screen → exit model → bought ──

    [Fact]
    public void TheFunnelCountsWhatTheExitModelDidWithScreenPassers()
    {
        var aggregate = new ScanAggregate();
        var passer = new OpportunityEvaluation
        {
            MarketHashName = "Sticker | Luck Skill (Foil)", ShouldBuy = true, BestRoi = 0.12, EvaluatedAtUtc = T0,
            Relist = new ExitEstimate { Available = true, Route = ExitRoute.CSFloatRelist, Roi = 0.12 },
        };
        for (int i = 0; i < 174; i++) aggregate.Record(passer);        // live 13 Nov: 174 "qualified"
        for (int i = 0; i < 173; i++) aggregate.RecordModelVerdict(false, -0.125, T0);
        aggregate.RecordModelVerdict(true, 0.11, T0);
        aggregate.RecordBlocked("purchasing is disabled (PurchaseCSFloatArbitrageOpportunities)", T0);

        var day = aggregate.Daily["2026-11-13"];
        Assert.Equal(174, day.Qualified);
        Assert.Equal(174, day.ModelChecked);
        Assert.Equal(1, day.ModelConfirmed);
        Assert.Equal(1, day.Blocked);
        Assert.Equal(0.11, day.BestModelRoi);
        Assert.Equal(1, aggregate.BlockReasons["buying off"]);
        Assert.Equal(T0, aggregate.FunnelSinceUtc);

        var analytics = new ScannedComparisonAnalytics(aggregate, new List<PurchasedListing>(), 0.86);
        Assert.Equal(174, analytics.ModelChecked);
        Assert.Equal(1, analytics.ModelConfirmed);
        Assert.Equal(1, analytics.Blocked);
        Assert.Equal(1, analytics.BlockReasons["buying off"]);

        // An aggregate saved before the funnel existed still loads (the counters start at zero).
        var old = JsonConvert.DeserializeObject<ScanAggregate>("{\"Version\":2,\"TotalEvaluated\":5,\"Daily\":{\"2026-10-09\":{\"Evaluated\":5,\"Qualified\":2,\"Purchased\":1,\"BestRoi\":0.2}}}")!;
        Assert.Equal(0, old.ModelChecked);
        Assert.Null(old.FunnelSinceUtc);
        Assert.Null(old.Daily["2026-10-09"].BestModelRoi);
    }

    [Theory]
    [InlineData("purchasing is disabled (PurchaseCSFloatArbitrageOpportunities)", "buying off")]
    [InlineData("already holding 3 of this item", "holding the limit")]
    [InlineData("daily spend limit (£20.00) reached", "daily limit")]
    [InlineData("CSFloat balance unknown", "balance unknown")]
    [InlineData("insufficient CSFloat balance ($1.20)", "not enough balance")]
    [InlineData("above the per-item cap (40% of balance = $12.31)", "per-item cap")]
    [InlineData("purchase rejected (409)", "CSFloat refused")]
    [InlineData("", "other")]
    public void BlockReasonsAreCountedUnderShortKeys(string reason, string key) =>
        Assert.Equal(key, ScanAggregate.BlockReasonKey(reason));

    // ── The analytics the page polls ──

    [Fact]
    public void CompactAnalyticsDropsTheOrderBooksAndOldDecisions()
    {
        var book = new JObject();
        for (int i = 0; i < 101; i++) book[(8.01 - i * 0.01).ToString("0.00")] = 30 + i;
        var decisions = new JArray(Enumerable.Range(0, 7).Select(i => new JObject { ["AtUtc"] = T0.AddDays(i), ["Stage"] = "review", ["Action"] = "keep" }));
        var full = new JObject
        {
            ["TotalListingsScanned"] = 1164002,
            ["ModelChecked"] = 46,
            ["TimeTakenToPurchaseAllPurchasedItems"] = new JArray("01:41:36", "1.20:10:50"),
            ["AllPurchasedItems"] = new JArray(new JObject
            {
                ["CSFloatListingID"] = "pos-1",
                ["ItemMarketHashName"] = "AK-47 | Searing Rage (Minimal Wear)",
                ["CurrentStrategicStage"] = 0,
                ["SaleAttempts"] = 0,
                ["Notes"] = "",
                ["PurchasePlan"] = null,
                ["TimeOfSellerToAcceptSale"] = "0001-01-01T00:00:00",
                ["comparison"] = new JObject
                {
                    ["CSFloatURL"] = "https://csfloat.com/item/pos-1",
                    ["CSFloatListing"] = new JObject { ["PriceText"] = "£5.05", ["PriceInPounds"] = 5.05, ["ImageURL"] = "https://img/x.png", ["AssetID"] = "1", ["ItemID64"] = "2" },
                    ["SteamListing"] = new JObject { ["PriceText"] = "£8.01", ["HighestBuyOrderPriceInPounds"] = 8.01, ["BuyAndSellOrders"] = new JObject { ["BuyOrders"] = book, ["SellOrders"] = book } },
                },
                ["ExitHistory"] = decisions,
            }),
        };
        string fullJson = full.ToString(Formatting.None);
        var compact = JObject.Parse(Bot.CompactAnalyticsJson(fullJson));

        Assert.True(Bot.CompactAnalyticsJson(fullJson).Length < fullJson.Length / 2);
        Assert.Null(compact["TimeTakenToPurchaseAllPurchasedItems"]);
        Assert.Equal(1164002, compact.Value<int>("TotalListingsScanned"));
        Assert.Equal(46, compact.Value<int>("ModelChecked"));
        var item = (JObject)compact["AllPurchasedItems"]![0]!;
        Assert.Null(item.SelectToken("comparison.SteamListing.BuyAndSellOrders"));
        Assert.Null(item.SelectToken("comparison.CSFloatListing.AssetID"));
        Assert.Equal("https://img/x.png", (string?)item.SelectToken("comparison.CSFloatListing.ImageURL"));
        Assert.Equal(8.01, (double)item.SelectToken("comparison.SteamListing.HighestBuyOrderPriceInPounds")!);
        Assert.Equal(7, item.Value<int>("ExitHistoryCount"));
        // Unset fields go; zeros stay (stage 0 is "waiting for the seller", not "unknown").
        Assert.Null(item["Notes"]);
        Assert.Null(item["PurchasePlan"]);
        Assert.Null(item["TimeOfSellerToAcceptSale"]);
        Assert.Equal(0, item.Value<int>("CurrentStrategicStage"));
        Assert.Equal(0, item.Value<int>("SaleAttempts"));
        var kept = (JArray)item["ExitHistory"]!;
        Assert.Equal(3, kept.Count);
        Assert.Equal(T0.AddDays(6), kept[2].Value<DateTime>("AtUtc").ToUniversalTime()); // the newest are kept
    }

    // ── Trades beyond the newest 100 ──

    [Fact]
    public void BuyerAndSellerTradesMergeNewestFirstWithoutDuplicates()
    {
        static JObject Trade(string id, string contract, DateTime at) => new() { ["id"] = id, ["contract_id"] = contract, ["created_at"] = at.ToString("o") };
        var purchases = new JArray(Trade("t3", "buy-3", T0.AddHours(-1)), Trade("t2", "buy-2", T0.AddDays(-2)));
        var sales = new JArray(Trade("t9", "relist-9", T0.AddDays(-1)), Trade("t3", "buy-3", T0.AddHours(-1)));

        var merged = Bot.MergeTrades(purchases, sales);
        Assert.Equal(new[] { "t3", "t9", "t2" }, merged.Select(t => (string)t["id"]!).ToArray());
    }

    // ── Closing a position by hand ──

    [Fact]
    public void APositionClosedByHandWithASalePriceRecordsTheResult()
    {
        var p = new PurchasedListing
        {
            CSFloatListingID = "1026293428079692062", ItemMarketHashName = "Sticker | Censored (Foil)",
            CurrentStrategicStage = StrategicStages.WaitingForCSFloatResale, PurchaseCostPence = 179, CSFloatResaleListingID = "1032135882146185730",
        };
        string summary = Bot.ClosePositionByHand(p, 294, "sold it myself", "Klives", T0, 0.7562, 0.02);
        Assert.Equal(StrategicStages.StrategyCompleted, p.CurrentStrategicStage);
        Assert.Equal(294, p.CSFloatResalePriceCents);
        Assert.InRange(p.ActualAbsoluteProfitInPounds, 0.38f, 0.40f); // $2.94 × 0.98 × 0.7562 = £2.18, less £1.79
        Assert.Contains("$2.94", summary);
        Assert.Contains("Closed by hand by Klives", p.Notes);
        Assert.Contains("sold it myself", p.Notes);
    }

    [Fact]
    public void APositionClosedByHandWithoutAPriceLeavesTheBotWithNoResult()
    {
        var p = new PurchasedListing { CSFloatListingID = "x", ItemMarketHashName = "Gamma 2 Case", CurrentStrategicStage = StrategicStages.JustRetrieved, PurchaseCostPence = 197 };
        Bot.ClosePositionByHand(p, null, "", "Klives", T0, 0.75, 0.02);
        Assert.Equal(StrategicStages.LeftInventory, p.CurrentStrategicStage);
        Assert.Equal(0, p.ActualAbsoluteProfitInPounds);
        Assert.Equal("closed:gone", p.LastTradeState);
    }

    // ── Balance records kept in memory ──

    [Fact]
    public void ASecondBalanceRecordOnTheSameDayReplacesTheFirst()
    {
        var records = new List<CSFloatAndSteamBalance>
        {
            new() { DateTimeOfBalanceRecord = new DateTime(2026, 11, 11, 12, 0, 0), CSFloatTotalBalanceInPounds = 20 },
            new() { DateTimeOfBalanceRecord = new DateTime(2026, 11, 12, 12, 0, 0), CSFloatTotalBalanceInPounds = 21 },
        };
        MergeBalanceRecord(records, new CSFloatAndSteamBalance { DateTimeOfBalanceRecord = new DateTime(2026, 11, 12, 18, 0, 0), CSFloatTotalBalanceInPounds = 22 });
        MergeBalanceRecord(records, new CSFloatAndSteamBalance { DateTimeOfBalanceRecord = new DateTime(2026, 11, 13, 12, 0, 0), CSFloatTotalBalanceInPounds = 23 });
        Assert.Equal(new[] { 20.0, 22.0, 23.0 }, records.Select(r => r.CSFloatTotalBalanceInPounds).ToArray());
    }
}
