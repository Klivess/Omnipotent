namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// Fee and currency arithmetic shared by the evaluator, the sale flow and the conversion model.
    /// Everything works in integer minor units (GBP pence / USD cents) so rounding matches the markets.
    /// </summary>
    public static class ArbitrageMath
    {
        public const double SteamWalletFeePercent = 0.05;
        public const double Cs2PublisherFeePercent = 0.10;
        public const int SteamFeeMinimumPence = 1;

        /// <summary>
        /// What a buyer pays for a Steam listing whose seller receives <paramref name="sellerReceives"/>
        /// (Steam's CalculateAmountToSendForDesiredReceivedAmount: 5% Valve + 10% CS2, each at least 1).
        /// </summary>
        public static int SteamBuyerPays(int sellerReceives)
        {
            if (sellerReceives <= 0) return 0;
            int steamFee = (int)Math.Floor(Math.Max(sellerReceives * SteamWalletFeePercent, SteamFeeMinimumPence));
            int publisherFee = (int)Math.Floor(Math.Max(sellerReceives * Cs2PublisherFeePercent, 1));
            return sellerReceives + steamFee + publisherFee;
        }

        /// <summary>
        /// What the seller receives when a buyer pays <paramref name="buyerPays"/> — the largest amount whose
        /// fee-inclusive price does not exceed it (any remainder is absorbed by Steam's fee, as Steam does).
        /// This is also the "price" field Steam's sellitem endpoint expects for a listing at that buyer price.
        /// </summary>
        public static int SteamSellerReceives(int buyerPays)
        {
            if (buyerPays < 3) return 0; // 1p to the seller already costs 1p + 1p in fees.
            int estimate = (int)(buyerPays / (1 + SteamWalletFeePercent + Cs2PublisherFeePercent)) + 2;
            while (estimate > 0 && SteamBuyerPays(estimate) > buyerPays) estimate--;
            return Math.Max(0, estimate);
        }

        /// <summary>USD cents → GBP pence, rounded up (costs are never understated).</summary>
        public static int UsdCentsToPenceCeil(long cents, double gbpPerUsd) =>
            (int)Math.Ceiling(cents * gbpPerUsd - 1e-9);

        /// <summary>USD cents → GBP pence, rounded down (proceeds are never overstated).</summary>
        public static int UsdCentsToPenceFloor(long cents, double gbpPerUsd) =>
            (int)Math.Floor(cents * gbpPerUsd + 1e-9);

        /// <summary>GBP pence → USD cents, rounded down (a price cap never exceeds the budget).</summary>
        public static int PenceToUsdCentsFloor(long pence, double gbpPerUsd) =>
            gbpPerUsd <= 0 ? 0 : (int)Math.Floor(pence / gbpPerUsd + 1e-9);

        /// <summary>Net CSFloat proceeds (USD cents) from a sale at <paramref name="priceCents"/> after the seller fee.</summary>
        public static int CSFloatNetProceedsCents(int priceCents, double sellerFee) =>
            (int)Math.Floor(priceCents * (1 - Math.Clamp(sellerFee, 0, 0.5)) + 1e-9);
    }
}
