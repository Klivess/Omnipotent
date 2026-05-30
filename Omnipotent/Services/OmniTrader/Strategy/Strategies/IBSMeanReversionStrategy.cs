using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    [TradingStrategy(
        "IBS Mean Reversion",
        "Mean reversion using smoothed IBS with 200-EMA trend filter, ATR stop-loss, and EMA-touch exit. " +
        "Long only when close > EMA(200). Entry: smoothed IBS(2) < 0.1. Stop: entry - 1.5*ATR(14). Exit: close > prev high OR close > EMA(20).")]
    public sealed class IBSMeanReversionStrategy : TradingStrategy
    {
        private const int HighLookback = 10;
        private const int AvgRangeLookback = 25;
        private const decimal RangeMultiplier = 2.5m;
        private const int IBSSmoothing = 2;
        private const decimal IBSThreshold = 0.1m;
        private const int TrendEmaPeriod = 200;
        private const int ExitEmaPeriod = 20;
        private const int AtrPeriod = 14;
        private const decimal AtrStopMultiplier = 1.5m;
        private const decimal PositionFraction = 0.10m;

        // Vertical (time) barrier — the measured reversion horizon (see EstimateMaxHoldBars).
        private const int MaxHoldFallback = 8;   // used until the horizon is measured
        private const int MaxHoldClampLo  = 2;
        private const int MaxHoldClampHi  = 24;

        private decimal _stopPrice;
        private int _entryBarIndex = -1;
        private int _maxHoldBars   = -1;         // lazily measured from history, then cached

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            var history = History;
            int minBars = Math.Max(TrendEmaPeriod, Math.Max(AvgRangeLookback, AtrPeriod + 1));
            if (history.Count < minBars) return Task.CompletedTask;

            int last = history.Count - 1;
            var histList = history as IList<OHLCCandle> ?? history.ToList();

            // Measure the strategy's natural holding period once, after enough history exists.
            if (_maxHoldBars < 0 && history.Count >= TrendEmaPeriod + 200)
            {
                _maxHoldBars = EstimateMaxHoldBars(histList);
                Log($"IBS: measured reversion horizon -> max hold {_maxHoldBars} bars");
            }
            int maxHold = _maxHoldBars > 0 ? _maxHoldBars : MaxHoldFallback;

            Task Exit(string reason)
            {
                _entryBarIndex = -1;
                Log($"IBS exit ({reason}) at {candle.Close:F2}");
                return SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = OrderSide.Sell,
                    Type = OrderType.Market,
                    Symbol = Symbol,
                    Qty = Position!.Qty
                }, ct);
            }

            bool inPosition = Position != null && Position.IsLong;

            if (inPosition)
            {
                // ── Triple-barrier exit ────────────────────────────────────────────
                // Lower barrier: ATR stop-loss
                if (candle.Close <= _stopPrice) return Exit("ATR stop");

                // Upper barrier: price reverted up to the mean (20-EMA)
                decimal exitEma = Indicators.EMA(histList, ExitEmaPeriod, last);
                if (candle.Close >= exitEma) return Exit("reverted to mean");

                // Vertical barrier: held longer than the measured reversion horizon
                if (_entryBarIndex >= 0 && last - _entryBarIndex >= maxHold)
                    return Exit($"time barrier {maxHold} bars");

                return Task.CompletedTask;
            }

            // ── Entry ──────────────────────────────────────────────────────────────
            // Trend filter: only long when price is above the 200-EMA
            decimal trendEma = Indicators.EMA(histList, TrendEmaPeriod, last);
            if (candle.Close <= trendEma) return Task.CompletedTask;

            // Room to revert: enter only BELOW the 20-EMA, so the upper barrier
            // (close >= EMA20) is a genuine target rather than already satisfied.
            decimal entryEma20 = Indicators.EMA(histList, ExitEmaPeriod, last);
            if (candle.Close >= entryEma20) return Task.CompletedTask;

            // Oversold: smoothed IBS(2) < threshold AND price envelope
            decimal smoothedIbs = Indicators.IBSSmoothed(histList, last, IBSSmoothing);
            if (smoothedIbs >= IBSThreshold) return Task.CompletedTask;

            decimal highestHigh10 = HighestHigh(history, HighLookback);
            decimal avgHigh25 = AverageHigh(history, AvgRangeLookback);
            decimal avgLow25 = AverageLow(history, AvgRangeLookback);
            decimal entryThreshold = highestHigh10 - RangeMultiplier * (avgHigh25 - avgLow25);
            if (candle.Close >= entryThreshold) return Task.CompletedTask;

            decimal qty = QuoteBalance * PositionFraction / candle.Close;
            if (qty <= 0) return Task.CompletedTask;

            decimal atr = Indicators.ATR(histList, AtrPeriod, last);
            _stopPrice = candle.Close - AtrStopMultiplier * atr;
            _entryBarIndex = last;

            return SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty
            }, ct);
        }

        // Estimate the strategy's natural holding period from history (no look-ahead):
        // after each oversold dip below the 20-EMA in an uptrend, count the bars until
        // price closes back above its 20-EMA. The median of those is the time barrier.
        // Model-free — deliberately avoids imposing an OU half-life on a trending series.
        private static int EstimateMaxHoldBars(IList<OHLCCandle> h)
        {
            int last = h.Count - 1;
            var durations = new List<int>();
            for (int i = TrendEmaPeriod; i < last; i++)
            {
                decimal ema20  = Indicators.EMA(h, ExitEmaPeriod, i);
                decimal ema200 = Indicators.EMA(h, TrendEmaPeriod, i);
                decimal ibs    = Indicators.IBSSmoothed(h, i, IBSSmoothing);
                bool entryLike = h[i].Close > ema200 && h[i].Close < ema20 && ibs < IBSThreshold;
                if (!entryLike) continue;

                for (int j = i + 1; j <= last; j++)
                {
                    if (h[j].Close >= Indicators.EMA(h, ExitEmaPeriod, j)) { durations.Add(j - i); break; }
                }
            }
            if (durations.Count < 5) return MaxHoldFallback;
            durations.Sort();
            return Math.Clamp(durations[durations.Count / 2], MaxHoldClampLo, MaxHoldClampHi);
        }

        private static decimal HighestHigh(IReadOnlyList<OHLCCandle> history, int lookback)
        {
            decimal best = decimal.MinValue;
            for (int i = history.Count - lookback; i < history.Count; i++)
                if (history[i].High > best) best = history[i].High;
            return best;
        }

        private static decimal AverageHigh(IReadOnlyList<OHLCCandle> history, int lookback)
        {
            decimal sum = 0;
            for (int i = history.Count - lookback; i < history.Count; i++) sum += history[i].High;
            return sum / lookback;
        }

        private static decimal AverageLow(IReadOnlyList<OHLCCandle> history, int lookback)
        {
            decimal sum = 0;
            for (int i = history.Count - lookback; i < history.Count; i++) sum += history[i].Low;
            return sum / lookback;
        }
    }
}
