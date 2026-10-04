using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs
{
    /// <summary>
    /// Data shapes and price-history helpers for the Steam→CSFloat "liquidity plan".
    ///
    /// The daily container-only search that used to live here is replaced by Engine/ConversionModel, which
    /// ranks every commodity item, verifies converters against real CSFloat sales, and runs every 3 hours.
    /// (The old search fetched price history from /market/pricehistory, which needs a login and returned
    /// 400 for this anonymous client — so every candidate was filtered out and the bot silently fell back
    /// to a hard-coded 0.75 coefficient.) These types are kept because the KM website's plan view and the
    /// Discord command consume them.
    /// </summary>
    public class CS2LiquidityFinder
    {
        public enum ContainerType
        {
            WeaponCase,
            StickerCapsule,
            AutographCapsule,
            SouvenirPackage
        }

        public struct Container
        {
            public string MarketHashName;
            public int PriceInCents;
            public int PriceInPence;
            public double PriceInPounds;
            public string ImageURL;
            public ContainerType containerType;
        }

        public struct ContainerGap
        {
            public Container csfloatContainer;
            public SteamAPIWrapper.ItemListing steamListing;
            public double ReturnCoefficientFromSteamtoCSFloat;
            public double ReturnCoefficientFromSteamToCSFloatTaxIncluded;

            public List<SteamPriceHistoryDataPoint> priceHistory;

            public int IdealCSFloatSellPriceInCents;
            public double IdealCSFloatSellPriceInPounds;
            public int IdealCSFloatSellPriceInPence;

            public double IdealPriceToPurchaseOnSteamInPounds;
            public double IdealReturnCoefficientFromSteamtoCSFloat;
            public double IdealReturnCoefficientFromSteamToCSFloatTaxIncluded;
        }

        public struct SteamPriceHistoryDataPoint
        {
            public DateTime DateTimeRecorded;
            public double PriceInPounds;
            public double PriceInPence => Convert.ToDouble(Math.Ceiling(PriceInPounds * 100));
            public int QuantitySold;
        }

        /// <summary>
        /// Local minima of a price series. <paramref name="window"/> points on each side must not be lower;
        /// <paramref name="minProminence"/> is how far the price must rise on both sides to count.
        /// </summary>
        public static IEnumerable<SteamPriceHistoryDataPoint> GetPriceBottoms(
            IEnumerable<SteamPriceHistoryDataPoint>? rawPoints,
            int window = 3,
            double minProminence = 0.0)
        {
            if (rawPoints == null) yield break;
            if (window < 1) window = 1;
            var points = rawPoints.OrderBy(p => p.DateTimeRecorded).ToList();
            if (points.Count < window * 2 + 1) yield break;

            for (int i = window; i < points.Count - window; i++)
            {
                double cur = points[i].PriceInPounds;
                double leftMin = double.MaxValue, rightMin = double.MaxValue;
                double leftMax = double.MinValue, rightMax = double.MinValue;
                for (int j = i - window; j < i; j++)
                {
                    double v = points[j].PriceInPounds;
                    if (v < leftMin) leftMin = v;
                    if (v > leftMax) leftMax = v;
                }
                for (int j = i + 1; j <= i + window; j++)
                {
                    double v = points[j].PriceInPounds;
                    if (v < rightMin) rightMin = v;
                    if (v > rightMax) rightMax = v;
                }
                bool isLocalMin = cur <= leftMin && cur <= rightMin &&
                                  (points[i - 1].PriceInPounds > cur || points[i + 1].PriceInPounds > cur);
                if (!isLocalMin) continue;
                double prominence = Math.Min(leftMax - cur, rightMax - cur);
                if (prominence + 1e-12 < minProminence) continue;
                yield return points[i];
            }
        }

        /// <summary>
        /// The lowest price that traded meaningful volume (≥5% of the last 5 days' sales in one point), capped
        /// at the latest price — a buy-order price likely to fill. 0 when there is no history.
        /// (The previous version started from 0 and only ever lowered it, so it always returned 0.)
        /// </summary>
        public static double FindIdealPriceToPlaceBuyOrder(List<SteamPriceHistoryDataPoint> dataPoints)
        {
            if (dataPoints == null || dataPoints.Count == 0) return 0;
            DateTime cutoff = dataPoints.Max(p => p.DateTimeRecorded).AddDays(-5);
            var recent = dataPoints.Where(p => p.DateTimeRecorded >= cutoff).ToList();
            double volume = recent.Sum(p => p.QuantitySold);
            double latest = dataPoints.OrderByDescending(p => p.DateTimeRecorded).First().PriceInPounds;
            var meaningful = recent.Where(p => p.QuantitySold >= 0.05 * volume && p.PriceInPounds > 0).ToList();
            double lowest = meaningful.Count > 0 ? meaningful.Min(p => p.PriceInPounds) : latest;
            return Math.Min(lowest, latest);
        }
    }
}
