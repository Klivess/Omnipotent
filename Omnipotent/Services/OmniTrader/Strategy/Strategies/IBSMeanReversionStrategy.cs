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

        private decimal _stopPrice;

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            var history = History;
            int minBars = Math.Max(TrendEmaPeriod, Math.Max(AvgRangeLookback, AtrPeriod + 1));
            if (history.Count < minBars) return Task.CompletedTask;

            int last = history.Count - 1;
            bool inPosition = Position != null && Position.IsLong;

            if (inPosition)
            {
                // ATR stop-loss
                if (candle.Close <= _stopPrice)
                {
                    Log($"ATR stop triggered at {candle.Close:F2} (stop={_stopPrice:F2})");
                    return SubmitOrder(new OrderRequest
                    {
                        IntentId = Guid.NewGuid().ToString("N"),
                        Side = OrderSide.Sell,
                        Type = OrderType.Market,
                        Symbol = Symbol,
                        Qty = Position!.Qty
                    }, ct);
                }

                // Original exit: close > previous bar high OR close > EMA(20)
                decimal exitEma = Indicators.EMA(history as IList<OHLCCandle> ?? history.ToList(), ExitEmaPeriod, last);
                var prev = history[^2];
                if (candle.Close > prev.High || candle.Close > exitEma)
                {
                    return SubmitOrder(new OrderRequest
                    {
                        IntentId = Guid.NewGuid().ToString("N"),
                        Side = OrderSide.Sell,
                        Type = OrderType.Market,
                        Symbol = Symbol,
                        Qty = Position!.Qty
                    }, ct);
                }
                return Task.CompletedTask;
            }

            // Trend filter: only long when price is above the 200-EMA
            var histList = history as IList<OHLCCandle> ?? history.ToList();
            decimal trendEma = Indicators.EMA(histList, TrendEmaPeriod, last);
            if (candle.Close <= trendEma) return Task.CompletedTask;

            // Entry: smoothed IBS(2) < threshold AND original price envelope
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

            return SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Symbol = Symbol,
                Qty = qty
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
