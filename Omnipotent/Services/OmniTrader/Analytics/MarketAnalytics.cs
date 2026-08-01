using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Services.OmniTrader.Analytics
{
    public enum MarketRegime { Unknown = 0, TrendingUp = 1, TrendingDown = 2, RangeBound = 3, Volatile = 4 }

    /// <summary>
    /// Shared analytical features. These are *decision support*, available identically to user-defined
    /// strategies and to a human reading the Markets page — they produce transparent, timestamped
    /// numbers and never place an order.
    ///
    /// Every function is pure and causal: it reads a candle series and returns a measurement of it.
    /// </summary>
    public static class MarketAnalytics
    {
        /// <summary><see cref="Indicators"/> takes an <see cref="IList{T}"/>; the engine hands these
        /// series around as read-only. Avoid a copy in the common case where it already is a list.</summary>
        private static IList<OHLCCandle> AsList(IReadOnlyList<OHLCCandle> candles)
            => candles as IList<OHLCCandle> ?? candles.ToList();

        // ── regime ────────────────────────────────────────────────────────────────

        /// <summary>Classify the regime from trend slope versus realized volatility. A market that is
        /// moving but whose move is small relative to its noise is range-bound, not trending.</summary>
        public static RegimeReading ClassifyRegime(IReadOnlyList<OHLCCandle> candles, int lookback = 100)
        {
            if (candles.Count < 20)
                return new RegimeReading { Regime = MarketRegime.Unknown, Confidence = 0, Detail = "insufficient history" };

            int n = Math.Min(lookback, candles.Count);
            int start = candles.Count - n;
            decimal first = candles[start].Close, last = candles[^1].Close;
            if (first <= 0m)
                return new RegimeReading { Regime = MarketRegime.Unknown, Confidence = 0, Detail = "invalid prices" };

            decimal change = (last - first) / first;
            double vol = RealizedVolatility(candles, n);
            decimal ma = Indicators.SMA(AsList(candles), Math.Min(50, n - 1), candles.Count - 1);
            bool aboveMa = last > ma;

            // Signal-to-noise: the move measured against what the market normally does over the window.
            double noise = Math.Max(vol * Math.Sqrt(n / 252.0), 1e-9);
            double ratio = Math.Abs((double)change) / noise;

            MarketRegime regime;
            if (vol > 1.2) regime = MarketRegime.Volatile;
            else if (ratio < 0.75) regime = MarketRegime.RangeBound;
            else regime = change > 0 && aboveMa ? MarketRegime.TrendingUp
                        : change < 0 && !aboveMa ? MarketRegime.TrendingDown
                        : MarketRegime.RangeBound;

            return new RegimeReading
            {
                Regime = regime,
                Confidence = Math.Clamp(ratio / 3.0, 0, 1),
                ChangePercent = change * 100m,
                AnnualizedVolatility = (decimal)vol * 100m,
                AboveMovingAverage = aboveMa,
                Detail = $"{n}-bar move {change * 100m:F2}%, annualised vol {vol * 100:F1}%, S/N {ratio:F2}"
            };
        }

        /// <summary>Annualised realized volatility from close-to-close log returns.</summary>
        public static double RealizedVolatility(IReadOnlyList<OHLCCandle> candles, int lookback = 30)
        {
            int n = Math.Min(lookback, candles.Count - 1);
            if (n < 2) return 0;
            var returns = new double[n];
            int start = candles.Count - n;
            for (int i = 0; i < n; i++)
            {
                double prev = (double)candles[start + i - 1].Close;
                double current = (double)candles[start + i].Close;
                returns[i] = prev > 0 ? Math.Log(current / prev) : 0;
            }
            double mean = returns.Average();
            double variance = returns.Sum(r => (r - mean) * (r - mean)) / Math.Max(1, n - 1);
            return Math.Sqrt(variance) * Math.Sqrt(365);
        }

        // ── momentum ──────────────────────────────────────────────────────────────

        /// <summary>Risk-adjusted momentum: the lookback return with an optional recency skip,
        /// divided by realized volatility so instruments of different volatility rank comparably.</summary>
        public static decimal MomentumScore(IReadOnlyList<OHLCCandle> candles, int lookback = 30, int skip = 1, bool riskAdjusted = true)
        {
            if (candles.Count < lookback + skip + 1) return 0m;
            int endIndex = candles.Count - 1 - skip;
            int startIndex = endIndex - lookback;
            if (startIndex < 0) return 0m;

            decimal from = candles[startIndex].Close, to = candles[endIndex].Close;
            if (from <= 0m) return 0m;
            decimal raw = (to - from) / from;
            if (!riskAdjusted) return raw * 100m;

            double vol = RealizedVolatility(candles, lookback);
            return vol <= 0 ? raw * 100m : raw * 100m / (decimal)Math.Max(vol, 0.01);
        }

        /// <summary>Rank instruments by momentum. Returns descending, so index 0 is the strongest.</summary>
        public static List<(string InstrumentId, decimal Score)> RankByMomentum(
            IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> series, int lookback = 30, int skip = 1)
            => series.Select(kv => (kv.Key, MomentumScore(kv.Value, lookback, skip)))
                     .OrderByDescending(x => x.Item2)
                     .ToList();

        /// <summary>Relative strength of one instrument against a benchmark over the same window.</summary>
        public static decimal RelativeStrength(IReadOnlyList<OHLCCandle> instrument, IReadOnlyList<OHLCCandle> benchmark, int lookback = 30)
        {
            decimal a = MomentumScore(instrument, lookback, 0, riskAdjusted: false);
            decimal b = MomentumScore(benchmark, lookback, 0, riskAdjusted: false);
            return a - b;
        }

        // ── breakout quality ──────────────────────────────────────────────────────

        /// <summary>
        /// How convincing the latest move through its range is. A breakout on no volume, into a wide
        /// range, that closes back inside, is a low-quality breakout — the score says so instead of
        /// simply reporting "broke out: true".
        /// </summary>
        public static BreakoutReading AnalyseBreakout(IReadOnlyList<OHLCCandle> candles, int lookback = 20)
        {
            if (candles.Count < lookback + 2)
                return new BreakoutReading { Direction = 0, Quality = 0, Detail = "insufficient history" };

            int start = candles.Count - 1 - lookback;
            decimal high = decimal.MinValue, low = decimal.MaxValue, volumeSum = 0m;
            for (int i = start; i < candles.Count - 1; i++)
            {
                high = Math.Max(high, candles[i].High);
                low = Math.Min(low, candles[i].Low);
                volumeSum += candles[i].Volume;
            }

            var latest = candles[^1];
            decimal averageVolume = volumeSum / lookback;
            decimal range = high - low;
            if (range <= 0m) return new BreakoutReading { Direction = 0, Quality = 0, Detail = "degenerate range" };

            int direction = latest.Close > high ? 1 : latest.Close < low ? -1 : 0;
            if (direction == 0)
                return new BreakoutReading
                {
                    Direction = 0,
                    Quality = 0,
                    RangeHigh = high,
                    RangeLow = low,
                    Detail = "price is inside the prior range"
                };

            // Penetration depth relative to the range, capped so a gap does not dominate the score.
            decimal penetration = direction > 0 ? (latest.Close - high) / range : (low - latest.Close) / range;
            double depthScore = Math.Clamp((double)penetration / 0.05, 0, 1);

            // Volume confirmation.
            double volumeScore = averageVolume > 0m ? Math.Clamp((double)(latest.Volume / averageVolume) / 2.0, 0, 1) : 0.5;

            // Close strength within the breakout bar: a close near the extreme is conviction.
            decimal barRange = latest.High - latest.Low;
            double closeScore = barRange > 0m
                ? Math.Clamp((double)(direction > 0 ? (latest.Close - latest.Low) / barRange : (latest.High - latest.Close) / barRange), 0, 1)
                : 0.5;

            double quality = 0.4 * depthScore + 0.3 * volumeScore + 0.3 * closeScore;
            return new BreakoutReading
            {
                Direction = direction,
                Quality = quality,
                RangeHigh = high,
                RangeLow = low,
                VolumeRatio = averageVolume > 0m ? latest.Volume / averageVolume : 0m,
                Detail = $"{(direction > 0 ? "upside" : "downside")} break, depth {penetration * 100m:F2}% of range, "
                       + $"volume {(averageVolume > 0m ? latest.Volume / averageVolume : 0m):F2}x, close strength {closeScore:F2}"
            };
        }

        // ── multi-timeframe alignment ─────────────────────────────────────────────

        /// <summary>Agreement across timeframes: +1 when every supplied series trends the same way,
        /// 0 when they cancel out. Series are keyed by their timeframe label.</summary>
        public static AlignmentReading AnalyseAlignment(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> byTimeframe, int lookback = 50)
        {
            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (label, candles) in byTimeframe)
            {
                var regime = ClassifyRegime(candles, lookback);
                votes[label] = regime.Regime switch
                {
                    MarketRegime.TrendingUp => 1,
                    MarketRegime.TrendingDown => -1,
                    _ => 0
                };
            }
            if (votes.Count == 0) return new AlignmentReading { Score = 0, Votes = votes, Detail = "no series supplied" };

            double score = votes.Values.Sum() / (double)votes.Count;
            string detail = string.Join(", ", votes.Select(v => $"{v.Key}:{(v.Value > 0 ? "up" : v.Value < 0 ? "down" : "flat")}"));
            return new AlignmentReading { Score = score, Votes = votes, Detail = detail };
        }

        // ── liquidity ─────────────────────────────────────────────────────────────

        /// <summary>Traded value and its stability. Thin or erratic liquidity is what turns a good
        /// signal into an unfillable one, so it is measured before sizing, not after a bad fill.</summary>
        public static LiquidityReading AnalyseLiquidity(IReadOnlyList<OHLCCandle> candles, int lookback = 30)
        {
            if (candles.Count < 2) return new LiquidityReading { Detail = "insufficient history" };
            int n = Math.Min(lookback, candles.Count);
            int start = candles.Count - n;

            decimal totalValue = 0m;
            var values = new List<decimal>(n);
            for (int i = start; i < candles.Count; i++)
            {
                decimal value = candles[i].Volume * candles[i].Close;
                values.Add(value);
                totalValue += value;
            }
            decimal average = totalValue / n;
            decimal median = values.OrderBy(v => v).ElementAt(n / 2);

            // Average intrabar range as a spread proxy where no book is available.
            decimal rangeSum = 0m;
            for (int i = start; i < candles.Count; i++)
                if (candles[i].Close > 0m) rangeSum += (candles[i].High - candles[i].Low) / candles[i].Close;
            decimal averageRangePct = rangeSum / n * 100m;

            return new LiquidityReading
            {
                AverageQuoteVolume = average,
                MedianQuoteVolume = median,
                EstimatedSpreadPercent = averageRangePct / 4m,
                Detail = $"avg {average:N0} / median {median:N0} quote volume over {n} bars, "
                       + $"typical bar range {averageRangePct:F2}%"
            };
        }

        // ── breadth ───────────────────────────────────────────────────────────────

        /// <summary>Participation across a universe: what fraction is advancing and above its trend.
        /// A rally where only a handful of names participate is a different market to one where most do.</summary>
        public static BreadthReading AnalyseBreadth(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> universe, int lookback = 50)
        {
            int total = 0, advancing = 0, aboveMa = 0;
            foreach (var (_, candles) in universe)
            {
                if (candles.Count < 3) continue;
                total++;
                if (candles[^1].Close > candles[^2].Close) advancing++;
                int period = Math.Min(lookback, candles.Count - 1);
                if (period >= 2 && candles[^1].Close > Indicators.SMA(AsList(candles), period, candles.Count - 1)) aboveMa++;
            }
            if (total == 0) return new BreadthReading { Detail = "empty universe" };

            return new BreadthReading
            {
                Members = total,
                AdvancingPercent = advancing * 100m / total,
                AboveTrendPercent = aboveMa * 100m / total,
                Detail = $"{advancing}/{total} advancing, {aboveMa}/{total} above their {lookback}-bar average"
            };
        }
    }

    // ── reading records ───────────────────────────────────────────────────────────

    public sealed class RegimeReading
    {
        public required MarketRegime Regime { get; init; }
        public required double Confidence { get; init; }
        public decimal ChangePercent { get; init; }
        public decimal AnnualizedVolatility { get; init; }
        public bool AboveMovingAverage { get; init; }
        public string Detail { get; init; } = "";
        public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class BreakoutReading
    {
        /// <summary>+1 upside, -1 downside, 0 no break.</summary>
        public required int Direction { get; init; }
        /// <summary>0–1 conviction score combining depth, volume and close strength.</summary>
        public required double Quality { get; init; }
        public decimal RangeHigh { get; init; }
        public decimal RangeLow { get; init; }
        public decimal VolumeRatio { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class AlignmentReading
    {
        public required double Score { get; init; }
        public Dictionary<string, int> Votes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Detail { get; init; } = "";
    }

    public sealed class LiquidityReading
    {
        public decimal AverageQuoteVolume { get; init; }
        public decimal MedianQuoteVolume { get; init; }
        public decimal EstimatedSpreadPercent { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class BreadthReading
    {
        public int Members { get; init; }
        public decimal AdvancingPercent { get; init; }
        public decimal AboveTrendPercent { get; init; }
        public string Detail { get; init; } = "";
    }
}
