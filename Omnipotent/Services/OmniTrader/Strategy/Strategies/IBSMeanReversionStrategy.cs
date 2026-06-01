using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Params;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    [TradingStrategy(
        "IBS Mean Reversion",
        "Mean reversion using smoothed IBS with 200-EMA trend filter, ATR stop-loss, and EMA-touch exit. " +
        "Long only when close > EMA(200). Entry: smoothed IBS(2) < 0.1. Stop: entry - 1.5*ATR(14). Exit: close > prev high OR close > EMA(20).")]
    public sealed class IBSMeanReversionStrategy : TradingStrategy
    {
        [Param("Symbol", Group = "Market", IsSymbol = true)] public string TradeSymbol { get; set; } = "BTCUSDT";
        public override StrategySymbols DeclareSymbols() => StrategySymbols.Of(TradeSymbol);

        [Param("High Lookback", Group = "Entry", Min = 2, Max = 50)] public int HighLookback { get; set; } = 10;
        [Param("Avg Range Lookback", Group = "Entry", Min = 5, Max = 100)] public int AvgRangeLookback { get; set; } = 25;
        [Param("Range Multiplier", Group = "Entry", Min = 0.5, Max = 5, Step = 0.1)] public decimal RangeMultiplier { get; set; } = 2.5m;
        [Param("IBS Smoothing", Group = "Entry", Min = 1, Max = 10)] public int IBSSmoothing { get; set; } = 2;
        [Param("IBS Threshold", Group = "Entry", Min = 0.01, Max = 0.5, Step = 0.01)] public decimal IBSThreshold { get; set; } = 0.1m;
        [Param("Trend EMA Period", Group = "Trend", Min = 20, Max = 400)] public int TrendEmaPeriod { get; set; } = 200;
        [Param("Exit EMA Period", Group = "Exit", Min = 5, Max = 100)] public int ExitEmaPeriod { get; set; } = 20;
        [Param("ATR Period", Group = "Risk", Min = 2, Max = 50)] public int AtrPeriod { get; set; } = 14;
        [Param("ATR Stop Multiplier", Group = "Risk", Min = 0.5, Max = 5, Step = 0.1)] public decimal AtrStopMultiplier { get; set; } = 1.5m;
        [Param("Take Profit ATR Mult", Group = "Risk", Min = 0, Max = 10, Step = 0.5, Help = "0 = use mean-revert exit only")] public decimal TakeProfitAtrMultiplier { get; set; } = 0m;
        [Param("Position Fraction", Group = "Sizing", Min = 0.01, Max = 1, Step = 0.01)] public decimal PositionFraction { get; set; } = 0.10m;
        [Param("Min Hold Bars", Group = "Risk", Min = 0, Max = 50, Help = "Don't exit before this many bars after entry")] public int MinHoldBars { get; set; } = 1;

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
                // Lower barrier (ATR stop) is now a protective bracket order attached at entry — it fills
                // intrabar via the engine, so no manual close-price check here.

                // Min-hold guard: don't flip straight back out the bar(s) right after entering.
                if (_entryBarIndex >= 0 && last - _entryBarIndex < MinHoldBars) return Task.CompletedTask;

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

            // Enter WITH a protective bracket: the ATR stop becomes a real stop-loss order and (optionally)
            // a take-profit, so the position is protected intrabar instead of waiting for the next close.
            decimal? takeProfit = TakeProfitAtrMultiplier > 0m ? candle.Close + TakeProfitAtrMultiplier * atr : (decimal?)null;

            return SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty,
                StopLossPrice = _stopPrice,
                TakeProfitPrice = takeProfit
            }, ct);
        }

        // Estimate the strategy's natural holding period from history (no look-ahead):
        // after each oversold dip below the 20-EMA in an uptrend, count the bars until
        // price closes back above its 20-EMA. The median of those is the time barrier.
        // Model-free — deliberately avoids imposing an OU half-life on a trending series.
        private int EstimateMaxHoldBars(IList<OHLCCandle> h)
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
