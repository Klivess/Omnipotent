using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    /// <summary>
    /// Mean reversion strategy based on Internal Bar Strength (IBS).
    /// Entry: close &lt; (10-bar highest high - 2.5 * (25-bar avg high - 25-bar avg low)) AND IBS &lt; 0.3
    /// Exit:  close &gt; previous bar's high
    /// </summary>
    public class IBSMeanReversionStrategy : OmniTraderStrategy
    {
        private const int HighLookback = 10;
        private const int AvgRangeLookback = 25;
        private const decimal RangeMultiplier = 2.5m;
        private const decimal IBSThreshold = 0.3m;

        private bool _inPosition;

        public IBSMeanReversionStrategy()
        {
            Name = "IBS Mean Reversion Strategy";
            Description = "A mean reversion strategy using Internal Bar Strength (IBS) and a 10/25-bar price envelope for entry, exiting when close exceeds the previous bar's high.";
        }

        protected override Task OnLoad()
        {
            _inPosition = false;
            return Task.CompletedTask;
        }

        protected override Task OnCandleClose(OmniTraderFinanceData.OHLCCandle current)
        {

            // Need at least AvgRangeLookback bars of history to evaluate signals
            if (candleHistory.Count < AvgRangeLookback)
                return Task.CompletedTask;

            if (_inPosition)
            {
                // Exit: close > yesterday's high
                var previousBar = candleHistory[^2];
                if (current.Close > previousBar.High)
                {
                    RaiseSell(AmountType.Percentage, 100);
                    _inPosition = false;
                }
            }
            else
            {
                // Entry condition
                decimal highestHigh10 = GetHighestHigh(HighLookback);
                decimal avgHigh25 = GetAverageHigh(AvgRangeLookback);
                decimal avgLow25 = GetAverageLow(AvgRangeLookback);
                decimal entryThreshold = highestHigh10 - RangeMultiplier * (avgHigh25 - avgLow25);

                decimal ibs = CalculateIBS(current);

                if (current.Close < entryThreshold && ibs < IBSThreshold)
                {
                    RaiseLong(AmountType.Percentage, 100);
                    _inPosition = true;
                }
            }

            return Task.CompletedTask;
        }

        private static decimal CalculateIBS(OmniTraderFinanceData.OHLCCandle candle)
        {
            decimal range = candle.High - candle.Low;
            if (range == 0)
                return 0.5m; // Neutral when there is no range
            return (candle.Close - candle.Low) / range;
        }

        private decimal GetHighestHigh(int lookback)
        {
            decimal highest = decimal.MinValue;
            int start = candleHistory.Count - lookback;
            for (int i = start; i < candleHistory.Count; i++)
            {
                if (candleHistory[i].High > highest)
                    highest = candleHistory[i].High;
            }
            return highest;
        }

        private decimal GetAverageHigh(int lookback)
        {
            decimal sum = 0;
            int start = candleHistory.Count - lookback;
            for (int i = start; i < candleHistory.Count; i++)
                sum += candleHistory[i].High;
            return sum / lookback;
        }

        private decimal GetAverageLow(int lookback)
        {
            decimal sum = 0;
            int start = candleHistory.Count - lookback;
            for (int i = start; i < candleHistory.Count; i++)
                sum += candleHistory[i].Low;
            return sum / lookback;
        }


    }
}
