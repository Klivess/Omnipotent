using Newtonsoft.Json.Linq;
using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Engine;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Tests.CS2ArbitrageBot;

public class TradeTimelineTests
{
    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void ProtectionEndsOnTheFirstDailyBoundaryAfterSevenDays()
    {
        var timeline = new TradeTimeline();
        // A live trade (Mar 2026): delivered 23:17, protection ended at 07:00 UTC a week and a bit later.
        Assert.Equal(Utc(3, 14, 7), timeline.ProtectionEnd(Utc(3, 6, 23, 17)));
        Assert.Equal(Utc(3, 13, 7), timeline.ProtectionEnd(Utc(3, 6, 6, 0)));  // 06:00 + 7d is before that day's boundary
        Assert.Equal(Utc(3, 13, 7), timeline.ProtectionEnd(Utc(3, 6, 7, 0)));  // exactly on it
        Assert.Equal(Utc(3, 14, 7), timeline.ProtectionEnd(Utc(3, 6, 7, 1)));  // just after it: the next day's
    }

    [Fact]
    public void WhenAPurchaseUnlocksDependsOnTheHourItIsMade()
    {
        var timeline = new TradeTimeline();
        // Delivered within minutes either way; the 06:00 purchase catches 07:00 a week later, the 08:00 one doesn't.
        double early = timeline.DaysUntilTradable(Utc(10, 5, 6), 0);
        double late = timeline.DaysUntilTradable(Utc(10, 5, 8), 0);
        Assert.InRange(early, 7.04, 7.09);
        Assert.InRange(late, 7.95, 8.01);
        // A slow seller (median 36 hours to send) delays the unlock by their handover.
        Assert.True(timeline.DaysUntilTradable(Utc(10, 5, 6), 1.5) > early + 1.4);
    }

    private static JObject Trade(bool sale, string state, DateTime created, double deliveredAfterMinutes, DateTime? protectionEnds, double verifiedAfterProtectionMinutes, double sentAfterMinutes = 0)
    {
        var offer = state == "cancelled" ? null : new JObject
        {
            ["state"] = 3,
            ["sent_at"] = created.AddMinutes(sentAfterMinutes).ToString("o"),
            ["updated_at"] = created.AddMinutes(deliveredAfterMinutes).ToString("o"),
        };
        return new JObject
        {
            ["state"] = state,
            ["created_at"] = created.ToString("o"),
            ["accepted_at"] = created.AddMinutes(1).ToString("o"),
            ["steam_offer"] = offer,
            ["trade_protection_ends_at"] = protectionEnds?.ToString("o"),
            ["verified_at"] = state == "verified" && protectionEnds is DateTime p ? p.AddMinutes(verifiedAfterProtectionMinutes).ToString("o") : null,
            ["contract"] = new JObject { ["is_seller"] = sale, ["price"] = 500 },
        };
    }

    [Fact]
    public void TheTimelineIsMeasuredFromTheAccountsOwnTrades()
    {
        var trades = new JArray();
        for (int i = 0; i < 20; i++)
        {
            DateTime created = Utc(9, 1 + i, 10);
            trades.Add(Trade(sale: false, "verified", created, deliveredAfterMinutes: 30, Utc(9, 9 + i, 7), verifiedAfterProtectionMinutes: 2));
        }
        trades.Add(Trade(sale: false, "cancelled", Utc(9, 25, 10), 0, null, 0));
        for (int i = 0; i < 10; i++)
            trades.Add(Trade(sale: true, i < 2 ? "cancelled" : "verified", Utc(8, 1 + i, 12), 50, Utc(8, 9 + i, 7), 1, sentAfterMinutes: 45));

        var timeline = new TradeTimeline();
        timeline.Measure(trades, Utc(10, 5, 12));
        // 20 deliveries at 30 min, shrunk towards the default with a 5-trade prior.
        Assert.Equal((5 * TradeTimeline.DefaultHandoverDays + 20 * (30 / 1440.0)) / 25, timeline.HandoverDays, 9);
        Assert.InRange(timeline.VerificationLagDays * 1440, 1.0, 2.0);
        // 8 sends at 45 minutes against the 1-hour default (cancelled sales never sent).
        Assert.Equal((5 * TradeTimeline.DefaultOwnSendDays + 8 * (45 / 1440.0)) / 13, timeline.OwnSendDays, 9);
        Assert.Equal((5 * 0.02 + 2) / 15, timeline.SaleCancelRate, 9);
        Assert.Equal((5 * 0.02 + 1) / 26, timeline.PurchaseCancelRate, 9);
        Assert.Equal(7, timeline.ProtectionBoundaryHourUtc);
        Assert.Equal(21, timeline.PurchasesObserved);
        Assert.Equal(10, timeline.SalesObserved);

        // Every trade poll re-measures the same window: that must not wash the prior out.
        double handover = timeline.HandoverDays;
        timeline.Measure(trades, Utc(10, 5, 13));
        Assert.Equal(handover, timeline.HandoverDays, 12);
    }

    [Fact]
    public void ARelistSaleBecomesCashOnlyAfterTheBuyersProtection()
    {
        var timeline = new TradeTimeline();
        Assert.InRange(timeline.CSFloatSaleToCashDays, 7.5, 7.6);
        // The Steam route also waits out Steam's 7-day hold on the converters before their own CSFloat sale.
        Assert.Equal(timeline.SteamConfirmDays + timeline.ConverterHoldDays + timeline.CSFloatSaleToCashDays, timeline.SteamSaleToCashDays, 9);
        Assert.True(timeline.SteamSaleToCashDays > timeline.CSFloatSaleToCashDays + TradeTimeline.SteamMarketHoldDays);
    }

    [Fact]
    public void ASellersOwnRecordDecidesHowLikelyTheirTradeFallsThrough()
    {
        var timeline = new TradeTimeline { PurchaseCancelRate = 0.02 };
        Assert.Equal(0.02, timeline.PurchaseFailureProbability(0, 0), 9);                    // unknown seller: the account's average
        Assert.Equal((20 * 0.02 + 10) / 220.0, timeline.PurchaseFailureProbability(200, 10), 9); // 5% failures over 200 trades
        Assert.True(timeline.PurchaseFailureProbability(1000, 0) < 0.001);
    }

    [Fact]
    public void WithoutAReportedProtectionEndTheUnlockIsComputedNotGuessed()
    {
        var p = new PurchasedListing { CSFloatListingID = "c1", CurrentStrategicStage = StrategicStages.WaitingForCSFloatTradeToBeAccepted };
        var received = JObject.Parse("{\"state\":\"pending\",\"steam_offer\":{\"id\":\"1\",\"state\":3,\"sent_at\":\"2026-03-06T23:10:00Z\",\"updated_at\":\"2026-03-06T23:17:00Z\"}}");
        Assert.Equal(TradeTransition.Retrieved, TradeProgress.ApplyPurchaseTrade(p, received, Utc(3, 6, 23, 20)));
        // Not "+7 days" (that would be 23:17 on the 13th, while the item is still locked) and not verify_sale_at (a day late).
        Assert.Equal(Utc(3, 14, 8), p.PredictedTimeToBeResoldOnSteam);
    }

    [Fact]
    public void ConvertersAreValuedAtTheirPriceAfterSteamsHold()
    {
        var options = new ConversionModel.Options();
        ConverterCandidate Converter(string name, double k) => new() { MarketHashName = name, Coefficient = k, WeeklyCapacityPence = 10_000, VerifiedBySales = true };
        var candidates = new[] { Converter("Sticker | Test | Cologne 2026", 0.80), Converter("Recoil Case", 0.70) };
        var now = Utc(10, 5, 12);

        var ignoringHold = ConversionModel.Combine(candidates, 5000, 0.68, options, now);
        // Stickers expected to lose 15% over the hold, cases nothing: 0.80 → 0.68 after the hold, so the case wins.
        var afterHold = ConversionModel.Combine(candidates, 5000, 0.68, options, now, name => name.StartsWith("Sticker") ? 0.85 : 1.0);
        Assert.Equal(0.80 * options.SafetyHaircut, ignoringHold.Coefficient, 9);
        Assert.Equal(0.70 * options.SafetyHaircut, afterHold.Coefficient, 9);
        Assert.Contains("7-day hold", afterHold.Basis);

        var (growth, sigma) = ConversionModel.HoldRisk(null, new MarketDriftModel(), 7.35);
        Assert.Equal(1.0, growth, 9); // cases: no drift
        Assert.InRange(sigma, 0.08, 0.09);
    }
}
