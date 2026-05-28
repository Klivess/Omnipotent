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
    }
}
