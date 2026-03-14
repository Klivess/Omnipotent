namespace Omnipotent.Services.OmniTrader.Data
{
    /// <summary>
    /// Static helpers that compute common technical indicators over a list of OHLC candles.
    /// All methods expect the list to be ordered oldest → newest.
    /// </summary>
    public static class TechnicalIndicators
    {
        /// <summary>
        /// Simple Moving Average of Close prices over the last <paramref name="period"/> candles
        /// ending at <paramref name="endIndex"/> (inclusive).
        /// </summary>
        public static decimal SMA(IList<RequestKlineData.OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period - 1)
                throw new ArgumentException("Not enough candles for SMA calculation.");

            decimal sum = 0;
            for (int i = endIndex - period + 1; i <= endIndex; i++)
                sum += candles[i].Close;
            return sum / period;
        }

        /// <summary>
        /// Relative Strength Index over the last <paramref name="period"/> candles
        /// ending at <paramref name="endIndex"/> (inclusive).
        /// </summary>
        public static decimal RSI(IList<RequestKlineData.OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period)
                throw new ArgumentException("Not enough candles for RSI calculation.");

            decimal gainSum = 0, lossSum = 0;
            for (int i = endIndex - period + 1; i <= endIndex; i++)
            {
                decimal change = candles[i].Close - candles[i - 1].Close;
                if (change > 0) gainSum += change;
                else lossSum += Math.Abs(change);
            }

            if (lossSum == 0) return 100m;
            if (gainSum == 0) return 0m;

            decimal avgGain = gainSum / period;
            decimal avgLoss = lossSum / period;
            decimal rs = avgGain / avgLoss;
            return 100m - 100m / (1m + rs);
        }

        /// <summary>
        /// Average True Range over the last <paramref name="period"/> candles
        /// ending at <paramref name="endIndex"/> (inclusive).
        /// </summary>
        public static decimal ATR(IList<RequestKlineData.OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period)
                throw new ArgumentException("Not enough candles for ATR calculation.");

            decimal sum = 0;
            for (int i = endIndex - period + 1; i <= endIndex; i++)
            {
                decimal highLow = candles[i].High - candles[i].Low;
                decimal highPrevClose = Math.Abs(candles[i].High - candles[i - 1].Close);
                decimal lowPrevClose = Math.Abs(candles[i].Low - candles[i - 1].Close);
                sum += Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
            }
            return sum / period;
        }

        /// <summary>
        /// Ratio of the volume at <paramref name="index"/> to the average volume
        /// over the preceding <paramref name="lookback"/> candles.
        /// </summary>
        public static decimal VolumeRatio(IList<RequestKlineData.OHLCCandle> candles, int lookback, int index)
        {
            if (index < lookback)
                throw new ArgumentException("Not enough candles for volume ratio.");

            decimal sum = 0;
            for (int i = index - lookback; i < index; i++)
                sum += candles[i].Volume;
            decimal avg = sum / lookback;
            return avg == 0 ? 1m : candles[index].Volume / avg;
        }

        /// <summary>
        /// Internal Bar Strength: (Close - Low) / (High - Low).
        /// Returns 0.5 when the bar has no range.
        /// </summary>
        public static decimal IBS(RequestKlineData.OHLCCandle candle)
        {
            decimal range = candle.High - candle.Low;
            return range == 0 ? 0.5m : (candle.Close - candle.Low) / range;
        }
    }
}
