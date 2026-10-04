using Newtonsoft.Json.Linq;
using System.Globalization;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    public enum TradeTransition
    {
        None,
        SellerAccepted,
        TradeOfferSent,
        Retrieved,
        Cancelled,
        ResaleSold,
        ResaleCompleted,
        ResaleCancelled,
    }

    /// <summary>
    /// Applies a CSFloat <c>/me/trades</c> entry to a purchase. Pure, so the state machine that drives
    /// selling is unit-tested. CSFloat trade states: queued → pending → verified, or cancelled/failed.
    /// The old monitor had no cancelled branch: one cancelled purchase stayed "pending" forever and kept
    /// the trades endpoint (100 requests/window) polled every 3 seconds.
    /// </summary>
    public static class TradeProgress
    {
        /// <summary>Steam trade offer states that mean the offer is dead.</summary>
        private static readonly HashSet<int> DeadOfferStates = new() { 5, 6, 7, 8, 10 };

        public static TradeTransition ApplyPurchaseTrade(PurchasedListing purchase, JObject trade, DateTime nowUtc)
        {
            string state = trade.Value<string>("state")?.ToLowerInvariant() ?? "";
            purchase.LastTradeState = state;
            var offer = trade["steam_offer"] as JObject;

            if (state is "cancelled" or "failed")
            {
                if (purchase.CurrentStrategicStage == StrategicStages.TradeCancelled) return TradeTransition.None;
                purchase.CurrentStrategicStage = StrategicStages.TradeCancelled;
                purchase.Notes = AppendNote(purchase.Notes, $"CSFloat trade {state} at {nowUtc:u}" +
                    (purchase.TimeOfItemRetrieval != default ? " after retrieval (reversed?)" : ""));
                return TradeTransition.Cancelled;
            }

            if (purchase.CurrentStrategicStage >= StrategicStages.JustRetrieved) return TradeTransition.None;

            DateTime? acceptedAt = ReadUtc(trade["accepted_at"]);
            DateTime? sentAt = ReadUtc(offer?["sent_at"]);
            int offerState = offer?.Value<int?>("state") ?? 0;
            DateTime? protectionEnds = ReadUtc(trade["trade_protection_ends_at"]);
            DateTime? verifySaleAt = ReadUtc(trade["verify_sale_at"]);
            bool received = offerState == 3 || protectionEnds != null || verifySaleAt != null || state == "verified";

            if (received)
            {
                purchase.TimeOfSellerToAcceptSale = purchase.TimeOfSellerToAcceptSale == default && acceptedAt is { } a ? a : purchase.TimeOfSellerToAcceptSale;
                purchase.TimeOfSellerToSendTradeOffer = purchase.TimeOfSellerToSendTradeOffer == default && sentAt is { } s ? s : purchase.TimeOfSellerToSendTradeOffer;
                purchase.TimeOfItemRetrieval = ReadUtc(offer?["updated_at"]) ?? nowUtc;
                // Valve's trade protection blocks market sales for 7 days; sell an hour after it lifts.
                DateTime resellAt = (protectionEnds ?? verifySaleAt ?? purchase.TimeOfItemRetrieval.AddDays(7)).AddHours(1);
                purchase.PredictedTimeToBeResoldOnSteam = resellAt;
                purchase.CurrentStrategicStage = StrategicStages.JustRetrieved;
                return TradeTransition.Retrieved;
            }

            if (sentAt != null && purchase.CurrentStrategicStage < StrategicStages.WaitingForCSFloatTradeToBeAccepted && !DeadOfferStates.Contains(offerState))
            {
                purchase.TimeOfSellerToSendTradeOffer = sentAt.Value;
                if (purchase.TimeOfSellerToAcceptSale == default && acceptedAt is { } a) purchase.TimeOfSellerToAcceptSale = a;
                string? offerId = offer?.Value<string>("id");
                if (!string.IsNullOrEmpty(offerId)) purchase.CSFloatToSteamTradeOfferLink = $"https://steamcommunity.com/tradeoffer/{offerId}/";
                purchase.CurrentStrategicStage = StrategicStages.WaitingForCSFloatTradeToBeAccepted;
                return TradeTransition.TradeOfferSent;
            }

            if (acceptedAt != null && purchase.CurrentStrategicStage < StrategicStages.WaitingForCSFloatTradeToBeSent)
            {
                purchase.TimeOfSellerToAcceptSale = acceptedAt.Value;
                purchase.CurrentStrategicStage = StrategicStages.WaitingForCSFloatTradeToBeSent;
                return TradeTransition.SellerAccepted;
            }
            return TradeTransition.None;
        }

        /// <summary>Applies the trade for an item the bot relisted on CSFloat (the account is the seller).</summary>
        public static TradeTransition ApplyResaleTrade(PurchasedListing purchase, JObject trade, DateTime nowUtc, double gbpPerUsd)
        {
            string state = trade.Value<string>("state")?.ToLowerInvariant() ?? "";
            if (purchase.CurrentStrategicStage != StrategicStages.WaitingForCSFloatResale) return TradeTransition.None;
            string previous = purchase.LastTradeState;
            purchase.LastTradeState = "resale:" + state;
            if (state is "cancelled" or "failed")
            {
                if (previous == purchase.LastTradeState) return TradeTransition.None;
                // A cancelled sale normally puts the listing back up ("listed"); if CSFloat delisted it
                // instead, hand the item back to the sale scheduler to decide again.
                string contractState = (trade["contract"] as JObject)?.Value<string>("state")?.ToLowerInvariant() ?? "";
                if (contractState != "listed")
                {
                    purchase.CurrentStrategicStage = StrategicStages.JustRetrieved;
                    purchase.PredictedTimeToBeResoldOnSteam = nowUtc;
                    purchase.CSFloatResaleListingID = "";
                }
                return TradeTransition.ResaleCancelled;
            }
            if (state == "verified")
            {
                int priceCents = (trade["contract"] as JObject)?.Value<int?>("price") ?? purchase.CSFloatResalePriceCents;
                double netPounds = ArbitrageMath.CSFloatNetProceedsCents(priceCents, 0.02) * gbpPerUsd / 100.0;
                double costPounds = purchase.PurchaseCostPence > 0 ? purchase.PurchaseCostPence / 100.0 : purchase.comparison?.CSFloatListing.PriceInPounds ?? 0;
                purchase.ActualAbsoluteProfitInPounds = (float)(netPounds - costPounds);
                purchase.ActualAbsoluteProfitInPence = (float)Math.Round((netPounds - costPounds) * 100);
                purchase.ActualProfitPercentage = costPounds > 0 ? (float)((netPounds / costPounds - 1) * 100) : 0;
                purchase.TimeOfCollectedRevenue = nowUtc;
                purchase.CurrentStrategicStage = StrategicStages.StrategyCompleted;
                return TradeTransition.ResaleCompleted;
            }
            // queued/pending: a buyer exists and the account must send the trade offer.
            return previous == purchase.LastTradeState ? TradeTransition.None : TradeTransition.ResaleSold;
        }

        private static string AppendNote(string notes, string note) => string.IsNullOrEmpty(notes) ? note : notes + "\n" + note;

        internal static DateTime? ReadUtc(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>().ToUniversalTime();
            string text = token.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        }
    }
}
