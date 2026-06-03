using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Params;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    [TradingStrategy(
        "IBS Mean Reversion",
        "Buys oversold dips in an uptrend (smoothed IBS < threshold, below the 20-EMA, above the 200-EMA) " +
        "and exits ONLY via a protective bracket: a take-profit at the mean (or an ATR multiple) and an " +
        "ATR stop-loss. No manual close — the engine fills whichever bracket the price reaches first (OCO).")]
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
        [Param("Mean EMA Period", Group = "Exit", Min = 5, Max = 100)] public int ExitEmaPeriod { get; set; } = 20;
        [Param("ATR Period", Group = "Risk", Min = 2, Max = 50)] public int AtrPeriod { get; set; } = 14;
        [Param("ATR Stop Multiplier", Group = "Risk", Min = 0.5, Max = 5, Step = 0.1)] public decimal AtrStopMultiplier { get; set; } = 1.5m;
        [Param("Take Profit ATR Mult", Group = "Risk", Min = 0, Max = 10, Step = 0.5, Help = "0 = take-profit at the mean (EMA); >0 = entry + mult*ATR")] public decimal TakeProfitAtrMultiplier { get; set; } = 0m;
        [Param("Position Fraction", Group = "Sizing", Min = 0.01, Max = 1, Step = 0.01)] public decimal PositionFraction { get; set; } = 0.10m;

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            var history = History;
            int minBars = Math.Max(TrendEmaPeriod, Math.Max(AvgRangeLookback, AtrPeriod + 1));
            if (history.Count < minBars) return Task.CompletedTask;

            // While a position is open, the protective bracket (TP at the mean, SL at the ATR stop) is the
            // ONLY exit — the engine fills it intrabar. The strategy never closes manually.
            if (Position != null && !Position.IsFlat) return Task.CompletedTask;

            int last = history.Count - 1;
            var histList = history as IList<OHLCCandle> ?? history.ToList();

            // ── Entry conditions ────────────────────────────────────────────────────
            // Trend filter: only long when price is above the 200-EMA.
            decimal trendEma = Indicators.EMA(histList, TrendEmaPeriod, last);
            if (candle.Close <= trendEma) return Task.CompletedTask;

            // Room to revert: enter only BELOW the mean (20-EMA), so the take-profit at the mean is a
            // genuine target rather than already satisfied.
            decimal meanEma = Indicators.EMA(histList, ExitEmaPeriod, last);
            if (candle.Close >= meanEma) return Task.CompletedTask;

            // Oversold: smoothed IBS(2) < threshold AND price envelope (a real dip vs recent range).
            decimal smoothedIbs = Indicators.IBSSmoothed(histList, last, IBSSmoothing);
            if (smoothedIbs >= IBSThreshold) return Task.CompletedTask;

            decimal highestHigh = HighestHigh(history, HighLookback);
            decimal avgHigh = AverageHigh(history, AvgRangeLookback);
            decimal avgLow = AverageLow(history, AvgRangeLookback);
            decimal entryThreshold = highestHigh - RangeMultiplier * (avgHigh - avgLow);
            if (candle.Close >= entryThreshold) return Task.CompletedTask;

            decimal qty = QuoteBalance * PositionFraction / candle.Close;
            if (qty <= 0) return Task.CompletedTask;

            decimal atr = Indicators.ATR(histList, AtrPeriod, last);
            decimal stop = candle.Close - AtrStopMultiplier * atr;
            // Take-profit: at the mean (the reversion target) by default, or an ATR multiple if configured.
            decimal takeProfit = TakeProfitAtrMultiplier > 0m ? candle.Close + TakeProfitAtrMultiplier * atr : meanEma;
            if (takeProfit <= candle.Close) takeProfit = candle.Close + Math.Max(atr, candle.Close * 0.002m); // ensure TP is above entry

            Log($"IBS entry at {candle.Close:F2} (TP {takeProfit:F2}, SL {stop:F2})");
            return SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty,
                StopLossPrice = stop,
                TakeProfitPrice = takeProfit,
            }, ct);
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
