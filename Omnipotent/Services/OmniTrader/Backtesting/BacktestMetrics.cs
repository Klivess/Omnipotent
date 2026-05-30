using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public static class BacktestMetrics
    {
        public static (decimal maxDD, decimal maxDDPct) MaxDrawdown(IReadOnlyList<EquityPoint> equityCurve)
        {
            if (equityCurve.Count == 0) return (0, 0);
            decimal peak = equityCurve[0].Equity;
            decimal maxDD = 0;
            decimal maxDDPct = 0;
            foreach (var p in equityCurve)
            {
                if (p.Equity > peak) peak = p.Equity;
                decimal dd = peak - p.Equity;
                if (dd > maxDD) maxDD = dd;
                if (peak > 0)
                {
                    decimal ddPct = dd / peak * 100m;
                    if (ddPct > maxDDPct) maxDDPct = ddPct;
                }
            }
            return (maxDD, maxDDPct);
        }

        public static decimal Sharpe(IReadOnlyList<EquityPoint> equityCurve)
        {
            if (equityCurve.Count < 2) return 0;
            var returns = new List<decimal>(equityCurve.Count - 1);
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i - 1].Equity == 0) continue;
                returns.Add((equityCurve[i].Equity - equityCurve[i - 1].Equity) / equityCurve[i - 1].Equity);
            }
            if (returns.Count == 0) return 0;
            decimal mean = returns.Sum() / returns.Count;
            decimal variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;
            double std = Math.Sqrt((double)variance);
            return std == 0 ? 0 : (decimal)(((double)mean) / std * Math.Sqrt(252));
        }

        public static decimal ProfitFactor(IReadOnlyList<TradeRecord> trades)
        {
            decimal gross = 0, loss = 0;
            foreach (var t in trades)
            {
                if (t.IsWin) gross += t.RealizedPnL;
                else loss += Math.Abs(t.RealizedPnL);
            }
            if (loss == 0) return gross > 0 ? decimal.MaxValue : 0;
            return gross / loss;
        }

        public static decimal BuyAndHoldPnLPercent(IReadOnlyList<OHLCCandle> candles)
        {
            if (candles.Count < 2) return 0;
            decimal first = candles[0].Close;
            decimal last = candles[^1].Close;
            if (first == 0) return 0;
            return (last - first) / first * 100m;
        }

        /// <summary>
        /// Downside-deviation-adjusted return (annualised, √252). Only negative period
        /// returns contribute to the denominator. Mirrors <see cref="Sharpe"/>.
        /// </summary>
        public static decimal Sortino(IReadOnlyList<EquityPoint> equityCurve)
        {
            if (equityCurve.Count < 2) return 0;
            var returns = new List<double>(equityCurve.Count - 1);
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i - 1].Equity == 0) continue;
                returns.Add((double)((equityCurve[i].Equity - equityCurve[i - 1].Equity) / equityCurve[i - 1].Equity));
            }
            if (returns.Count == 0) return 0;
            double mean = returns.Average();
            double downsideSumSq = 0;
            int downsideCount = 0;
            foreach (double r in returns)
            {
                if (r < 0) { downsideSumSq += r * r; downsideCount++; }
            }
            if (downsideCount == 0) return 0;
            double downsideDev = Math.Sqrt(downsideSumSq / downsideCount);
            return downsideDev == 0 ? 0 : (decimal)(mean / downsideDev * Math.Sqrt(252));
        }

        /// <summary>
        /// Annualised volatility of period-over-period equity returns, as a percentage (√252 scaling).
        /// </summary>
        public static decimal AnnualizedVolatilityPercent(IReadOnlyList<EquityPoint> equityCurve)
        {
            if (equityCurve.Count < 2) return 0;
            var returns = new List<double>(equityCurve.Count - 1);
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i - 1].Equity == 0) continue;
                returns.Add((double)((equityCurve[i].Equity - equityCurve[i - 1].Equity) / equityCurve[i - 1].Equity));
            }
            if (returns.Count == 0) return 0;
            double mean = returns.Average();
            double variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;
            double std = Math.Sqrt(variance);
            return (decimal)(std * Math.Sqrt(252) * 100);
        }

        /// <summary>
        /// Longest run, in bars and wall-clock time, from an equity peak until equity recovers to that peak.
        /// A still-underwater curve reports the span from the last peak to the final point.
        /// </summary>
        public static (int bars, TimeSpan span) MaxDrawdownDuration(IReadOnlyList<EquityPoint> equityCurve)
        {
            if (equityCurve.Count < 2) return (0, TimeSpan.Zero);
            decimal peak = equityCurve[0].Equity;
            int peakIndex = 0;
            int maxBars = 0;
            TimeSpan maxSpan = TimeSpan.Zero;
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i].Equity >= peak)
                {
                    int bars = i - peakIndex;
                    TimeSpan span = equityCurve[i].Ts - equityCurve[peakIndex].Ts;
                    if (bars > maxBars) maxBars = bars;
                    if (span > maxSpan) maxSpan = span;
                    peak = equityCurve[i].Equity;
                    peakIndex = i;
                }
            }
            // Account for a drawdown that never recovered before the curve ended.
            int tailBars = (equityCurve.Count - 1) - peakIndex;
            TimeSpan tailSpan = equityCurve[^1].Ts - equityCurve[peakIndex].Ts;
            if (tailBars > maxBars) maxBars = tailBars;
            if (tailSpan > maxSpan) maxSpan = tailSpan;
            return (maxBars, maxSpan);
        }

        /// <summary>
        /// Longest streaks of consecutive winning and losing trades.
        /// </summary>
        public static (int wins, int losses) MaxConsecutive(IReadOnlyList<TradeRecord> trades)
        {
            int maxWins = 0, maxLosses = 0, runWins = 0, runLosses = 0;
            foreach (var t in trades)
            {
                if (t.IsWin)
                {
                    runWins++;
                    runLosses = 0;
                    if (runWins > maxWins) maxWins = runWins;
                }
                else
                {
                    runLosses++;
                    runWins = 0;
                    if (runLosses > maxLosses) maxLosses = runLosses;
                }
            }
            return (maxWins, maxLosses);
        }
    }
}
