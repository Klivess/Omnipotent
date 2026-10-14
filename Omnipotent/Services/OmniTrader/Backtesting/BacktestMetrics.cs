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

        /// <summary>Annualised Sharpe of per-bar equity returns (risk-free 0). <paramref name="barsPerYear"/>
        /// comes from the interval and whether the market trades continuously.</summary>
        public static decimal Sharpe(IReadOnlyList<EquityPoint> equityCurve, double barsPerYear = 252)
        {
            var returns = PeriodReturns(equityCurve);
            if (returns.Count < 2) return 0;
            double mean = returns.Average();
            double std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1));
            return std == 0 || !double.IsFinite(std) ? 0 : Clamp(mean / std * Math.Sqrt(barsPerYear));
        }

        private static List<double> PeriodReturns(IReadOnlyList<EquityPoint> equityCurve)
        {
            var returns = new List<double>(Math.Max(0, equityCurve.Count - 1));
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i - 1].Equity == 0) continue;
                returns.Add((double)((equityCurve[i].Equity - equityCurve[i - 1].Equity) / equityCurve[i - 1].Equity));
            }
            return returns;
        }

        private static decimal Clamp(double v) => !double.IsFinite(v) ? 0 : (decimal)Math.Clamp(v, -1e9, 1e9);

        public static decimal ProfitFactor(IReadOnlyList<TradeRecord> trades)
        {
            decimal gross = 0, loss = 0;
            foreach (var t in trades)
            {
                if (t.IsWin) gross += t.RealizedPnL;
                else loss += Math.Abs(t.RealizedPnL);
            }
            // No losing trade: report a large finite value rather than decimal.MaxValue, which
            // serialises as a 29-digit number and breaks every chart it touches.
            if (loss == 0) return gross > 0 ? 999m : 0;
            return Math.Min(999m, gross / loss);
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
        /// Annualised Sortino: mean return over downside deviation, where downside deviation is the
        /// root-mean-square of the negative returns taken over ALL observations (target 0).
        /// </summary>
        public static decimal Sortino(IReadOnlyList<EquityPoint> equityCurve, double barsPerYear = 252)
        {
            var returns = PeriodReturns(equityCurve);
            if (returns.Count < 2) return 0;
            double mean = returns.Average();
            double downsideDev = Math.Sqrt(returns.Sum(r => r < 0 ? r * r : 0) / returns.Count);
            return downsideDev == 0 ? 0 : Clamp(mean / downsideDev * Math.Sqrt(barsPerYear));
        }

        /// <summary>Annualised volatility of per-bar equity returns, as a percentage.</summary>
        public static decimal AnnualizedVolatilityPercent(IReadOnlyList<EquityPoint> equityCurve, double barsPerYear = 252)
        {
            var returns = PeriodReturns(equityCurve);
            if (returns.Count < 2) return 0;
            double mean = returns.Average();
            double std = Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1));
            return Clamp(std * Math.Sqrt(barsPerYear) * 100);
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
