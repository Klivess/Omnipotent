using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    public static class Indicators
    {
        public static decimal SMA(IList<OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period - 1)
                throw new ArgumentException("Not enough candles for SMA calculation.");
            decimal sum = 0;
            for (int i = endIndex - period + 1; i <= endIndex; i++) sum += candles[i].Close;
            return sum / period;
        }

        public static decimal RSI(IList<OHLCCandle> candles, int period, int endIndex)
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
            return 100m - 100m / (1m + avgGain / avgLoss);
        }

        public static decimal ATR(IList<OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period)
                throw new ArgumentException("Not enough candles for ATR calculation.");
            decimal sum = 0;
            for (int i = endIndex - period + 1; i <= endIndex; i++)
            {
                decimal hl = candles[i].High - candles[i].Low;
                decimal hpc = Math.Abs(candles[i].High - candles[i - 1].Close);
                decimal lpc = Math.Abs(candles[i].Low - candles[i - 1].Close);
                sum += Math.Max(hl, Math.Max(hpc, lpc));
            }
            return sum / period;
        }

        public static decimal EMA(IList<OHLCCandle> candles, int period, int endIndex)
        {
            if (endIndex < period - 1)
                throw new ArgumentException("Not enough candles for EMA calculation.");
            decimal multiplier = 2m / (period + 1);
            decimal ema = candles[endIndex - period + 1].Close;
            for (int i = endIndex - period + 2; i <= endIndex; i++)
                ema = (candles[i].Close - ema) * multiplier + ema;
            return ema;
        }

        public static decimal IBSSmoothed(IList<OHLCCandle> candles, int endIndex, int smoothing = 2)
        {
            if (endIndex < smoothing - 1)
                return IBS(candles[endIndex]);
            decimal sum = 0;
            for (int i = endIndex - smoothing + 1; i <= endIndex; i++)
                sum += IBS(candles[i]);
            return sum / smoothing;
        }

        public static decimal IBS(OHLCCandle candle)
        {
            decimal range = candle.High - candle.Low;
            return range == 0 ? 0.5m : (candle.Close - candle.Low) / range;
        }
    }
}
