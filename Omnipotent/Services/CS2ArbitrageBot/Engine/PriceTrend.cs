using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// Projects how much an item's price is likely to fall over the 7-day trade-protection hold. A listing that
    /// looks cheap against today's reference is often cheap because the item is sliding (e.g. a Major's
    /// stickers after the event); buying it means selling a week later into a lower market.
    /// </summary>
    public static class PriceTrend
    {
        /// <summary>
        /// Multiplicative factor (≤ 1) for the price <paramref name="horizonDays"/> ahead, extrapolating the
        /// volume-weighted price of the last 3 days against days 4–14. Rising prices are ignored (never
        /// counted on); thin or missing history returns 1.
        /// </summary>
        public static double ProjectedDeclineFactor(IReadOnlyList<SteamPricePoint>? history, DateTime nowUtc, double horizonDays = 8)
        {
            if (history == null || history.Count == 0) return 1;
            var window = history.Where(p => p.TimeUtc > nowUtc.AddDays(-14) && p.TimeUtc <= nowUtc && p.MedianPriceGbp > 0).ToList();
            var recent = window.Where(p => p.TimeUtc > nowUtc.AddDays(-3)).ToList();
            var earlier = window.Where(p => p.TimeUtc <= nowUtc.AddDays(-3)).ToList();
            if (recent.Sum(p => p.Purchases) < 5 || earlier.Sum(p => p.Purchases) < 10) return 1;
            if (earlier.Select(p => p.TimeUtc.Date).Distinct().Count() < 4) return 1;

            double recentPrice = WeightedAverage(recent);
            double earlierPrice = WeightedAverage(earlier);
            if (recentPrice <= 0 || earlierPrice <= 0) return 1;
            double ratio = recentPrice / earlierPrice;
            if (ratio >= 1) return 1;

            // Window centres: ~1.5 days ago vs ~8.5 days ago.
            double dailyRate = Math.Pow(ratio, 1 / 7.0);
            return Math.Clamp(Math.Pow(dailyRate, horizonDays), 0.5, 1.0);
        }

        private static double WeightedAverage(IEnumerable<SteamPricePoint> points)
        {
            double weight = 0, sum = 0;
            foreach (var p in points)
            {
                double w = Math.Max(1, p.Purchases);
                weight += w;
                sum += p.MedianPriceGbp * w;
            }
            return weight > 0 ? sum / weight : 0;
        }
    }
}
