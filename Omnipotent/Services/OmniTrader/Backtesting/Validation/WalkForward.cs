using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Section 11.1: walk-forward validation. For each rolling fold, sweep the parameter grid on the
    /// in-sample window, pick the best by Sharpe, evaluate THOSE params on the immediately following
    /// out-of-sample window, then roll forward. Only the stitched out-of-sample curve is reported — the
    /// in-sample fits are never shown as results. The number of trials feeds the deflated Sharpe (11.2).
    /// </summary>
    public static class WalkForward
    {
        public sealed record FoldResult(DateTime OosStart, DateTime OosEnd, decimal OosPnLPercent, decimal OosSharpe, object? ChosenParams);

        public sealed class Result
        {
            public List<EquityPoint> StitchedOosEquity { get; init; } = new();
            public decimal OosPnLPercent { get; init; }
            public decimal OosSharpe { get; init; }
            public decimal OosMaxDrawdownPercent { get; init; }
            public int Folds { get; init; }
            public int TrialsPerFold { get; init; }
            public double DeflatedSharpe { get; init; }
            public double ExpectedMaxSharpe { get; init; }
            public List<FoldResult> FoldResults { get; init; } = new();
        }

        /// <summary>
        /// <paramref name="sessionFactory"/> builds a fresh session for (params, slicedInput) using the
        /// caller's cost config. <paramref name="inSampleDays"/>/<paramref name="oosDays"/> are window
        /// lengths in days; <paramref name="warmupDays"/> is the history each run needs before it can trade.
        /// </summary>
        public static async Task<Result> RunAsync<TParam>(
            PortfolioInput input,
            IReadOnlyList<TParam> paramGrid,
            Func<TParam, PortfolioInput, BacktestSession> sessionFactory,
            decimal initialEquity,
            int inSampleDays,
            int oosDays,
            int warmupDays,
            CancellationToken ct = default)
        {
            var dates = MasterDates(input);
            if (paramGrid.Count == 0 || dates.Count < warmupDays + inSampleDays + oosDays)
                return new Result();

            var stitched = new List<EquityPoint> { new() { Ts = dates[0], MarkPrice = 0m, QuoteBalance = initialEquity, BaseBalance = 0m, Equity = initialEquity } };
            decimal running = initialEquity;
            var folds = new List<FoldResult>();
            var pooledInSampleSharpes = new List<double>();

            int cursor = warmupDays;
            while (cursor + inSampleDays + oosDays <= dates.Count)
            {
                DateTime inStart = dates[cursor];
                DateTime inEnd = dates[cursor + inSampleDays - 1];
                DateTime oosStart = dates[cursor + inSampleDays];
                DateTime oosEnd = dates[cursor + inSampleDays + oosDays - 1];
                // OOS run needs warmup history that lives inside the in-sample window.
                DateTime oosRunStart = dates[Math.Max(0, cursor + inSampleDays - warmupDays)];

                // ── In-sample sweep ────────────────────────────────────────────────
                var inSlice = Slice(input, inStart, inEnd);
                TParam best = paramGrid[0];
                decimal bestSharpe = decimal.MinValue;
                foreach (var p in paramGrid)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await sessionFactory(p, inSlice).RunPortfolioAsync(ct);
                    pooledInSampleSharpes.Add((double)r.SharpeRatio);
                    if (r.SharpeRatio > bestSharpe) { bestSharpe = r.SharpeRatio; best = p; }
                }

                // ── Out-of-sample evaluation of the chosen params ──────────────────
                var oosSlice = Slice(input, oosRunStart, oosEnd);
                var oosRes = await sessionFactory(best, oosSlice).RunPortfolioAsync(ct);

                // Stitch only the OOS-period returns onto the running equity.
                decimal foldStartEquity = running;
                var oosPeriod = oosRes.EquityCurve.Where(p => p.Ts >= oosStart).OrderBy(p => p.Ts).ToList();
                for (int i = 1; i < oosPeriod.Count; i++)
                {
                    decimal prev = oosPeriod[i - 1].Equity;
                    if (prev == 0m) continue;
                    decimal ret = (oosPeriod[i].Equity - prev) / prev;
                    running *= (1m + ret);
                    stitched.Add(new EquityPoint { Ts = oosPeriod[i].Ts, MarkPrice = 0m, QuoteBalance = running, BaseBalance = 0m, Equity = running });
                }

                decimal foldPnLPct = foldStartEquity == 0m ? 0m : (running - foldStartEquity) / foldStartEquity * 100m;
                folds.Add(new FoldResult(oosStart, oosEnd, foldPnLPct, oosRes.SharpeRatio, best));

                cursor += oosDays; // roll forward by the OOS length (non-overlapping OOS)
            }

            var (maxDD, maxDDPct) = BacktestMetrics.MaxDrawdown(stitched);
            decimal stitchedSharpe = BacktestMetrics.Sharpe(stitched);
            decimal pnlPct = initialEquity == 0m ? 0m : (running - initialEquity) / initialEquity * 100m;

            double trialStd = StdDev(pooledInSampleSharpes);
            var dsr = DeflatedSharpe.Compute(DeflatedSharpe.PeriodReturns(stitched), paramGrid.Count, trialStd);

            return new Result
            {
                StitchedOosEquity = stitched,
                OosPnLPercent = pnlPct,
                OosSharpe = stitchedSharpe,
                OosMaxDrawdownPercent = maxDDPct,
                Folds = folds.Count,
                TrialsPerFold = paramGrid.Count,
                DeflatedSharpe = dsr.Dsr,
                ExpectedMaxSharpe = dsr.ExpectedMaxSharpe,
                FoldResults = folds,
            };
        }

        /// <summary>A small, sane default grid over the spec's sweep ranges (keeps runtime bounded).</summary>
        public static List<MomentumConfig> DefaultGrid(MomentumConfig baseline)
        {
            var grid = new List<MomentumConfig>();
            foreach (int lookback in new[] { 20, 30 })
                foreach (double top in new[] { 0.20, 0.30 })
                    foreach (int regimeMa in new[] { 50, 100 })
                    {
                        var c = baseline.Clone();
                        c.LookbackDays = lookback;
                        c.TopFraction = top;
                        c.RegimeMaDays = regimeMa;
                        grid.Add(c);
                    }
            return grid;
        }

        private static List<DateTime> MasterDates(PortfolioInput input)
        {
            var set = new SortedSet<DateTime>();
            foreach (var series in input.Candles.Values)
                foreach (var c in series) set.Add(c.Timestamp);
            return set.ToList();
        }

        /// <summary>Filter every symbol's candle/market-cap series to [from, to] (inclusive).</summary>
        public static PortfolioInput Slice(PortfolioInput input, DateTime from, DateTime to)
        {
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, series) in input.Candles)
            {
                var sliced = series.Where(c => c.Timestamp >= from && c.Timestamp <= to).ToList();
                if (sliced.Count > 0) candles[sym] = sliced;
            }
            var caps = new Dictionary<string, IReadOnlyList<MarketCapPoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, series) in input.MarketCaps)
            {
                var sliced = series.Where(c => c.Date >= from && c.Date <= to).ToList();
                if (sliced.Count > 0) caps[sym] = sliced;
            }
            return new PortfolioInput { Candles = candles, MarketCaps = caps, RegimeSymbol = input.RegimeSymbol };
        }

        private static double StdDev(IReadOnlyList<double> xs)
        {
            if (xs.Count < 2) return 0;
            double mean = xs.Average();
            return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
        }
    }
}
