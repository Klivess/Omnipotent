using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Section 11.5: turnover and capacity.
    /// <para><b>Turnover</b> is estimated from completed round-trips: total traded notional divided by
    /// average equity, expressed per week. <b>Capacity</b> is the max deployable capital implied by the
    /// liquidity filter and the participation cap — for a name held at the per-asset weight cap, staying
    /// under <c>participation × daily volume</c> bounds capital at <c>participation × V / maxWeight</c>;
    /// the binding (smallest, taken here as the universe-median volume) name sets the estimate.</para>
    /// </summary>
    public static class TurnoverCapacity
    {
        public readonly record struct Result(decimal WeeklyTurnover, decimal AnnualTurnover, decimal EstimatedCapacityUsd);

        public static Result Compute(BacktestResult res, MomentumConfig cfg, IReadOnlyList<decimal> universeDailyVolumesUsd)
        {
            // Average equity over the run.
            decimal avgEquity = res.EquityCurve.Count > 0
                ? res.EquityCurve.Average(p => p.Equity)
                : res.InitialEquity;

            // Traded notional ≈ Σ entry + exit notional across round-trips.
            decimal tradedNotional = 0m;
            foreach (var t in res.Trades)
                tradedNotional += t.EntryPrice * t.Qty + t.ExitPrice * t.Qty;

            double weeks = res.Duration.TotalDays / 7.0;
            decimal weeklyTurnover = (avgEquity > 0m && weeks > 0)
                ? tradedNotional / avgEquity / (decimal)weeks
                : 0m;

            // Capacity from the participation cap at the per-asset weight cap.
            decimal medianVol = Median(universeDailyVolumesUsd);
            decimal maxWeight = (decimal)Math.Max(1e-6, cfg.MaxWeightPerAsset);
            decimal participation = (decimal)(cfg.ParticipationCap > 0 ? cfg.ParticipationCap : 0.05);
            decimal capacity = medianVol > 0m ? participation * medianVol / maxWeight : 0m;

            return new Result(weeklyTurnover, weeklyTurnover * 52m, capacity);
        }

        private static decimal Median(IReadOnlyList<decimal> xs)
        {
            if (xs.Count == 0) return 0m;
            var s = xs.OrderBy(x => x).ToList();
            int mid = s.Count / 2;
            return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2m;
        }
    }
}
