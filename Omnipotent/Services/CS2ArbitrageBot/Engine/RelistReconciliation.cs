using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// What CSFloat says became of a relist, and whether its unit is still in the Steam inventory. Every field is null
    /// when it could not be read (an outage is never evidence).
    /// </summary>
    /// <param name="ListingState">The listing's own state, lower case ("listed", "sold", "delisted", "refunded", …), "missing" for a 404.</param>
    /// <param name="ListingPriceCents">The listing's price as CSFloat reports it.</param>
    /// <param name="UnitInInventory">Whether the position's unit is still in the Steam inventory.</param>
    public sealed record RelistFacts(
        string? ListingState,
        int? ListingPriceCents,
        bool? UnitInInventory)
    {
        public static readonly RelistFacts Unknown = new(null, null, null);
    }

    public enum RelistVerdict
    {
        /// <summary>Nothing could be read: keep the position as it is and look again later.</summary>
        Unknown,
        /// <summary>CSFloat still shows the listing as live.</summary>
        StillListed,
        /// <summary>A buyer bought it and the unit has not left yet: the trade is (or was) waiting to be sent.</summary>
        SoldAwaitingTrade,
        /// <summary>Sold, and the unit has gone: the sale completed even though its trade was never seen.</summary>
        SoldAndDelivered,
        /// <summary>Not live any more and the unit is still ours: sell it again.</summary>
        BackToSale,
        /// <summary>Not live and the unit is gone: sold or moved outside the bot.</summary>
        LeftInventory,
    }

    /// <summary>
    /// Settles a relist the bot can no longer follow. Live on 2026-11-13: a $2.94 sticker relisted on 19 Oct sat as
    /// "waiting for a buyer" for 25 days while CSFloat refused every re-price ("you can only modify listed contracts",
    /// 14 times in 13.7 h). The review only ever said "failed" and tried again an hour later, the trade monitor reads
    /// only the newest 100 trades (all purchases on this account), and nothing asked CSFloat what became of the
    /// listing, so the position could never close. Pure, so every branch is unit-tested.
    /// </summary>
    public static class RelistReconciliation
    {
        /// <summary>CSFloat gives the seller this long to send a sold item; past it the sale is cancelled.</summary>
        public static readonly TimeSpan SendDeadline = TimeSpan.FromHours(2);
        /// <summary>A sale the bot inferred is re-checked this often until the unit leaves or the sale lapses.</summary>
        public static readonly TimeSpan SoldRecheck = TimeSpan.FromHours(6);
        /// <summary>After this, a sold unit still sitting in the inventory means the sale fell through.</summary>
        public static readonly TimeSpan SoldButKeptLapse = TimeSpan.FromDays(2);

        public const string InferredSoldState = "resale:sold";
        public const string InferredDeliveredState = "resale:inferred";

        public static RelistVerdict Decide(RelistFacts facts, PurchasedListing p, DateTime nowUtc)
        {
            string? state = facts.ListingState?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(state)) return RelistVerdict.Unknown;
            if (state == "listed") return RelistVerdict.StillListed;
            if (state == "sold")
            {
                if (facts.UnitInInventory == false) return RelistVerdict.SoldAndDelivered;
                if (facts.UnitInInventory == null) return RelistVerdict.SoldAwaitingTrade;
                // Sold but still ours: inside the send window (or not long after) the trade is pending; long after,
                // the buyer's purchase was cancelled and the unit needs selling again.
                bool lapsed = p.ResaleSoldAtUtc != default && nowUtc - p.ResaleSoldAtUtc >= SoldButKeptLapse;
                return lapsed ? RelistVerdict.BackToSale : RelistVerdict.SoldAwaitingTrade;
            }
            // delisted, refunded, cancelled, missing, or anything new: not live.
            if (facts.UnitInInventory == true) return RelistVerdict.BackToSale;
            if (facts.UnitInInventory == false) return RelistVerdict.LeftInventory;
            return RelistVerdict.Unknown;
        }

        /// <summary>Moves the position on as the verdict says. Returns the note recorded on it (null when nothing changed).</summary>
        public static string? Apply(RelistVerdict verdict, RelistFacts facts, PurchasedListing p, DateTime nowUtc, double gbpPerUsd, double sellerFee)
        {
            string listing = p.CSFloatResaleListingID;
            switch (verdict)
            {
                case RelistVerdict.SoldAwaitingTrade:
                    if (p.ResaleSoldAtUtc == default)
                    {
                        if (nowUtc > p.RelistExposureUpdatedUtc) ExitManager.AccrueExposure(p, nowUtc);
                        p.ResaleSoldAtUtc = nowUtc;
                    }
                    if (facts.ListingPriceCents is > 0) p.CSFloatResalePriceCents = facts.ListingPriceCents.Value;
                    p.LastTradeState = InferredSoldState;
                    p.NextExitReviewUtc = nowUtc + SoldRecheck;
                    return Note(p, nowUtc, $"CSFloat shows relist {listing} as sold; its trade is not among the recent trades");

                case RelistVerdict.SoldAndDelivered:
                {
                    if (p.ResaleSoldAtUtc == default) p.ResaleSoldAtUtc = nowUtc;
                    int priceCents = facts.ListingPriceCents is > 0 ? facts.ListingPriceCents.Value : p.CSFloatResalePriceCents;
                    p.CSFloatResalePriceCents = priceCents;
                    double netPounds = ArbitrageMath.CSFloatNetProceedsCents(priceCents, sellerFee) * gbpPerUsd / 100.0;
                    double costPounds = p.PurchaseCostPence > 0 ? p.PurchaseCostPence / 100.0 : p.comparison?.CSFloatListing.PriceInPounds ?? 0;
                    if (gbpPerUsd > 0)
                    {
                        p.ActualAbsoluteProfitInPounds = (float)(netPounds - costPounds);
                        p.ActualAbsoluteProfitInPence = (float)Math.Round((netPounds - costPounds) * 100);
                        p.ActualProfitPercentage = costPounds > 0 ? (float)((netPounds / costPounds - 1) * 100) : 0;
                    }
                    p.TimeOfCollectedRevenue = nowUtc;
                    p.LastTradeState = InferredDeliveredState;
                    p.CurrentStrategicStage = StrategicStages.StrategyCompleted;
                    return Note(p, nowUtc, $"Closed: CSFloat shows relist {listing} as sold at ${priceCents / 100.0:F2} and the unit has left the inventory (its trade was never seen; profit inferred from the listing price)");
                }

                case RelistVerdict.BackToSale:
                    p.CurrentStrategicStage = StrategicStages.JustRetrieved;
                    p.CSFloatResaleListingID = "";
                    p.ResaleSoldAtUtc = default;
                    p.PredictedTimeToBeResoldOnSteam = nowUtc;
                    p.SaleDeferredUntilUtc = default;
                    p.LastRelistChangeUtc = nowUtc;
                    p.LastTradeState = "resale:" + (facts.ListingState ?? "gone");
                    return Note(p, nowUtc, $"Relist {listing} is {facts.ListingState ?? "gone"} on CSFloat and the unit is still in the inventory; handed back to the sale scheduler");

                case RelistVerdict.LeftInventory:
                    p.CurrentStrategicStage = StrategicStages.LeftInventory;
                    p.LastTradeState = "resale:" + (facts.ListingState ?? "gone");
                    return Note(p, nowUtc, $"Closed: relist {listing} is {facts.ListingState ?? "gone"} on CSFloat and the unit is no longer in the inventory (sold or moved outside the bot)");

                default:
                    return null;
            }
        }

        private static string Note(PurchasedListing p, DateTime nowUtc, string text)
        {
            string line = $"{text} ({nowUtc:u}).";
            p.Notes = string.IsNullOrEmpty(p.Notes) ? line : p.Notes + Environment.NewLine + line;
            return line;
        }
    }
}
