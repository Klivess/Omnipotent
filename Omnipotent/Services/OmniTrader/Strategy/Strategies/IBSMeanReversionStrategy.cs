using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    [TradingStrategy(
        "IBS Mean Reversion",
        "Mean reversion using Internal Bar Strength and 10/25-bar price envelope. Entry: close < 10-bar high - 2.5*(25-bar range) AND IBS < 0.3. Exit: close > previous bar high.")]
    public sealed class IBSMeanReversionStrategy : TradingStrategy
    {
        private const int HighLookback = 10;
        private const int AvgRangeLookback = 25;
        private const decimal RangeMultiplier = 2.5m;
        private const decimal IBSThreshold = 0.3m;
        private const decimal PositionFraction = 0.10m; // 10% of quote balance per entry

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            var history = History;
            if (history.Count < AvgRangeLookback) return Task.CompletedTask;

            bool inPosition = Position != null && Position.IsLong;

            if (inPosition)
            {
                var prev = history[^2];
                if (candle.Close > prev.High)
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

            decimal highestHigh10 = HighestHigh(history, HighLookback);
            decimal avgHigh25 = AverageHigh(history, AvgRangeLookback);
            decimal avgLow25 = AverageLow(history, AvgRangeLookback);
            decimal entryThreshold = highestHigh10 - RangeMultiplier * (avgHigh25 - avgLow25);
            decimal ibs = Indicators.IBS(candle);

            if (candle.Close < entryThreshold && ibs < IBSThreshold)
            {
                decimal qty = QuoteBalance * PositionFraction / candle.Close;
                if (qty <= 0) return Task.CompletedTask;
                return SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Symbol = Symbol,
                    Qty = qty
                }, ct);
            }
            return Task.CompletedTask;
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
