using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// Every lock and hold between a decision and spendable CSFloat cash. Platform rules are constants; the rest
    /// is measured from the account's own CSFloat trades (the trade monitor already fetches them) and shrunk
    /// towards the defaults below, so a handful of unusual trades nudges rather than swings it.
    ///
    /// Measured on the account's last 98 trades (Mar–Oct 2026):
    /// <list type="bullet">
    /// <item>Sellers accept within minutes (median 7 min) and the offer is delivered at once; CSFloat gives the
    /// seller 2 hours from accepting to send it.</item>
    /// <item>Valve's trade protection does not end exactly 7 days after delivery: it ends on the first daily
    /// boundary after that (07:00 UTC in 95 of 98 trades), i.e. 7.32 days median, 7.66 days at p90.</item>
    /// <item>CSFloat verifies the trade, and pays the seller, at that boundary (median 1.5 minutes after it).</item>
    /// </list>
    /// Items bought on the Steam Community Market cannot be re-traded for 7 days, freeing at the exact time of
    /// purchase (no daily boundary since Valve's 2026 change).
    /// </summary>
    public sealed class TradeTimeline
    {
        /// <summary>Valve trade protection: a traded item cannot be re-traded or sold on the Market for this long.</summary>
        public const double ProtectionDays = 7;
        /// <summary>Items bought on the Steam Community Market cannot be re-traded for this long.</summary>
        public const double SteamMarketHoldDays = 7;
        /// <summary>CSFloat: after accepting a sale the seller has this long to send the trade offer.</summary>
        public const double SendDeadlineDays = 2 / 24.0;
        /// <summary>Selling 1 hour after protection lifts, so inventories have caught up.</summary>
        public const double UnlockBufferDays = 1 / 24.0;
        /// <summary>The defaults count as much as this many trades (so a few unusual trades nudge, not swing).</summary>
        public const double PriorTrades = 5;
        public const double DefaultHandoverDays = 0.005;
        public const double DefaultVerificationLagDays = 0.001;
        public const double DefaultOwnSendDays = 1 / 24.0;
        public const double DefaultCancelRate = 0.02;

        // ── Measured from the account's trades ──
        /// <summary>The UTC hour Valve's protection ends on (the mode of observed end times).</summary>
        public int ProtectionBoundaryHourUtc { get; set; } = 7;
        /// <summary>Purchase → offer delivered: the seller accepts and sends, the account accepts.</summary>
        public double HandoverDays { get; set; } = DefaultHandoverDays;
        /// <summary>Protection end → CSFloat verifies the trade and credits the seller.</summary>
        public double VerificationLagDays { get; set; } = DefaultVerificationLagDays;
        /// <summary>Share of the account's own CSFloat sales that were cancelled after a buyer bought (missed sends etc.).</summary>
        public double SaleCancelRate { get; set; } = DefaultCancelRate;
        /// <summary>Share of the account's purchases that fell through (seller never sent, or reversed): 2 of 100 measured.</summary>
        public double PurchaseCancelRate { get; set; } = DefaultCancelRate;
        public int PurchasesObserved { get; set; }
        public int SalesObserved { get; set; }
        public DateTime? MeasuredUtc { get; set; }

        // ── Steps a person (or tooling) performs: settings ──
        /// <summary>Confirming a Steam Market listing in the mobile app (the bot prompts on Discord).</summary>
        public double SteamConfirmDays { get; set; } = 0.5;
        /// <summary>Sending the trade offer after a CSFloat buyer purchases (within the 2-hour deadline); measured once the account has sales.</summary>
        public double OwnSendDays { get; set; } = DefaultOwnSendDays;
        /// <summary>Buying converter items on Steam with the wallet.</summary>
        public double ConverterBuyDays { get; set; } = 0.1;
        /// <summary>Selling a liquid converter item on CSFloat once its hold lifts.</summary>
        public double ConverterSaleDays { get; set; } = 0.25;

        /// <summary>When a trade delivered at <paramref name="deliveredUtc"/> stops being protected.</summary>
        public DateTime ProtectionEnd(DateTime deliveredUtc)
        {
            DateTime earliest = deliveredUtc.AddDays(ProtectionDays);
            DateTime boundary = earliest.Date.AddHours(ProtectionBoundaryHourUtc);
            return DateTime.SpecifyKind(boundary >= earliest ? boundary : boundary.AddDays(1), DateTimeKind.Utc);
        }

        /// <summary>Days from <paramref name="nowUtc"/> until a unit bought now from this seller can be resold.</summary>
        public double DaysUntilTradable(DateTime nowUtc, double sellerHandoverDays)
        {
            double handover = Math.Max(HandoverDays, Math.Clamp(sellerHandoverDays, 0, 2));
            return (ProtectionEnd(nowUtc.AddDays(handover)) - nowUtc).TotalDays + UnlockBufferDays;
        }

        /// <summary>
        /// Days from a CSFloat sale (a buyer purchasing) at an unknown future moment to spendable cash: the account
        /// sends the offer, the buyer's protection runs to its daily boundary (half a day of rounding on average),
        /// then CSFloat verifies and credits the account.
        /// </summary>
        [JsonIgnore]
        public double CSFloatSaleToCashDays => OwnSendDays + ProtectionDays + 0.5 + VerificationLagDays;

        /// <summary>
        /// Days from a Steam Market sale to spendable CSFloat cash: confirm the listing, buy converter items with the
        /// wallet, Steam's 7-day hold on them, sell them on CSFloat, then that sale's protection and verification.
        /// </summary>
        [JsonIgnore]
        public double SteamSaleToCashDays => SteamConfirmDays + ConverterBuyDays + SteamMarketHoldDays + ConverterSaleDays + CSFloatSaleToCashDays;

        /// <summary>How long converter items are exposed to price moves before they are sold on CSFloat.</summary>
        [JsonIgnore]
        public double ConverterHoldDays => ConverterBuyDays + SteamMarketHoldDays + ConverterSaleDays;

        /// <summary>
        /// Updates the measured parts from <c>/me/trades</c> (newest first). Each figure is the median of what was
        /// observed, shrunk towards its default with a <see cref="PriorTrades"/>-trade prior. Idempotent: measuring
        /// the same trades again (every trade poll does) gives the same result.
        /// </summary>
        public void Measure(JArray trades, DateTime nowUtc)
        {
            var handovers = new List<double>();
            var lags = new List<double>();
            var boundaryHours = new List<int>();
            var sendTimes = new List<double>();
            int sales = 0, salesCancelled = 0, purchases = 0, purchasesFinished = 0, purchasesCancelled = 0;

            foreach (var trade in trades.OfType<JObject>())
            {
                bool isSale = (trade["contract"] as JObject)?.Value<bool?>("is_seller") == true;
                string state = trade.Value<string>("state")?.ToLowerInvariant() ?? "";
                var offer = trade["steam_offer"] as JObject;
                DateTime? created = TradeProgress.ReadUtc(trade["created_at"]);
                DateTime? accepted = TradeProgress.ReadUtc(trade["accepted_at"]);
                DateTime? sent = TradeProgress.ReadUtc(offer?["sent_at"]);
                DateTime? delivered = offer?.Value<int?>("state") == 3 ? TradeProgress.ReadUtc(offer["updated_at"]) : null;
                DateTime? protectionEnds = TradeProgress.ReadUtc(trade["trade_protection_ends_at"]);
                DateTime? verified = TradeProgress.ReadUtc(trade["verified_at"]);

                if (protectionEnds is DateTime pe) boundaryHours.Add(pe.Hour);
                if (protectionEnds is DateTime end && verified is DateTime v && v >= end) lags.Add((v - end).TotalDays);
                if (isSale)
                {
                    if (state is "verified" or "cancelled" or "failed") sales++;
                    if (state is "cancelled" or "failed") salesCancelled++;
                    if (created is DateTime c && sent is DateTime s && s >= c) sendTimes.Add((s - c).TotalDays);
                }
                else
                {
                    purchases++;
                    if (state is "verified" or "cancelled" or "failed") purchasesFinished++;
                    if (state is "cancelled" or "failed") purchasesCancelled++;
                    if (created is DateTime c && delivered is DateTime d && d >= c) handovers.Add((d - c).TotalDays);
                }
            }

            if (boundaryHours.Count > 0) ProtectionBoundaryHourUtc = boundaryHours.GroupBy(h => h).OrderByDescending(g => g.Count()).First().Key;
            HandoverDays = Shrink(DefaultHandoverDays, handovers);
            VerificationLagDays = Shrink(DefaultVerificationLagDays, lags);
            OwnSendDays = Shrink(DefaultOwnSendDays, sendTimes);
            SaleCancelRate = (PriorTrades * DefaultCancelRate + salesCancelled) / (PriorTrades + sales);
            PurchaseCancelRate = (PriorTrades * DefaultCancelRate + purchasesCancelled) / (PriorTrades + purchasesFinished);
            PurchasesObserved = purchases;
            SalesObserved = sales;
            MeasuredUtc = nowUtc;
        }

        private static double Shrink(double prior, List<double> observed) =>
            observed.Count == 0 ? prior : (PriorTrades * prior + observed.Count * CSFloatDemand.Quantile(observed, 0.5)) / (PriorTrades + observed.Count);

        public TradeTimeline Clone() => (TradeTimeline)MemberwiseClone();

        /// <summary>
        /// The chance a purchase from this seller falls through: their failed and avoided trades, shrunk towards the
        /// account's measured purchase cancel rate with a 20-trade prior (a new seller is judged as average).
        /// </summary>
        public double PurchaseFailureProbability(int sellerTotalTrades, int sellerFailedOrAvoided) =>
            Math.Clamp((20 * PurchaseCancelRate + Math.Max(0, sellerFailedOrAvoided)) / (20.0 + Math.Max(0, sellerTotalTrades)), 0, 1);

        public object Describe() => new
        {
            protectionEndsAtUtcHour = ProtectionBoundaryHourUtc,
            handoverHours = Math.Round(HandoverDays * 24, 2),
            verificationLagHours = Math.Round(VerificationLagDays * 24, 2),
            ownSendHours = Math.Round(OwnSendDays * 24, 2),
            saleCancelRate = Math.Round(SaleCancelRate, 3),
            purchaseCancelRate = Math.Round(PurchaseCancelRate, 3),
            csfloatSaleToCashDays = Math.Round(CSFloatSaleToCashDays, 2),
            steamSaleToCashDays = Math.Round(SteamSaleToCashDays, 2),
            converterHoldDays = Math.Round(ConverterHoldDays, 2),
            purchasesObserved = PurchasesObserved,
            salesObserved = SalesObserved,
            measuredUtc = MeasuredUtc,
        };
    }

    /// <summary>What the exits depend on besides the item itself: locks and holds, the conversion route's risk, account limits.</summary>
    public sealed record ExitEnvironment
    {
        public static readonly ExitEnvironment Default = new();

        public TradeTimeline Timeline { get; init; } = new();
        /// <summary>Expected multiplicative change of the converter items' price over their Steam hold (category drift).</summary>
        public double ConverterHoldGrowth { get; init; } = 1;
        /// <summary>σ of the converter items' price over their Steam hold.</summary>
        public double ConverterHoldSigma { get; init; } = PriceForecaster.DefaultSigma8[ItemCategory.Container];
        /// <summary>Room left in the Steam wallet before its cap, GBP pence; null when unknown.</summary>
        public double? SteamWalletHeadroomPence { get; init; }
        /// <summary>Steam refuses Market listings above this price (≈ $1,800), GBP pence; null when unknown.</summary>
        public double? SteamMaxListingPence { get; init; }
        /// <summary>The CSFloat account is set to away: its listings cannot sell.</summary>
        public bool CSFloatSellingPaused { get; init; }
    }
}
