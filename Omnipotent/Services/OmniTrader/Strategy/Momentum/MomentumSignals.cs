using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    public readonly record struct SignalResult(decimal Score, decimal CumReturn, decimal RealizedVol);

    /// <summary>
    /// Section 4: trailing risk-adjusted momentum on point-in-time daily closes.
    /// <para>score = cum_return(lookback, skipping the most recent <c>skip_days</c>) / annualized realized vol.</para>
    /// Pure function of the price series ending at the rebalance bar — no look-ahead.
    /// </summary>
    public static class MomentumSignals
    {
        /// <summary>Annualization factor for daily crypto vol (365 trading days).</summary>
        private static readonly double Sqrt365 = Math.Sqrt(365);

        /// <summary>Returns null if the series is too short to form a signal.</summary>
        public static SignalResult? Compute(IReadOnlyList<OHLCCandle> prices, MomentumConfig cfg)
        {
            int n = prices.Count;
            int end = n - 1 - cfg.SkipDays;            // price at t − skip
            int start = end - cfg.LookbackDays;        // price at t − skip − lookback
            int volStart = end - cfg.VolLookbackDays;  // first index of the vol window
            if (start < 0 || volStart < 1 || end <= 0 || end >= n) return null;

            decimal pStart = prices[start].Close;
            decimal pEnd = prices[end].Close;
            if (pStart <= 0m || pEnd <= 0m) return null;
            decimal cumReturn = pEnd / pStart - 1m;

            // Realized daily vol over the vol window ending at `end`, annualized.
            var rets = new List<double>(cfg.VolLookbackDays);
            for (int i = volStart + 1; i <= end; i++)
            {
                decimal prev = prices[i - 1].Close;
                if (prev <= 0m) continue;
                rets.Add((double)(prices[i].Close / prev - 1m));
            }
            if (rets.Count < 2) return null;

            double mean = rets.Average();
            double variance = rets.Sum(r => (r - mean) * (r - mean)) / rets.Count;
            decimal realizedVol = (decimal)(Math.Sqrt(variance) * Sqrt365);

            decimal score = cfg.UseRiskAdjusted
                ? (realizedVol == 0m ? 0m : cumReturn / realizedVol)
                : cumReturn;
            return new SignalResult(score, cumReturn, realizedVol);
        }
    }
}
