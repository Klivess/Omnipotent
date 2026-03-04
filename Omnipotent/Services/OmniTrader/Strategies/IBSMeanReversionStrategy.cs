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

        private readonly List<RequestKlineData.OHLCCandle> _history = [];
        private bool _inPosition;

        public IBSMeanReversionStrategy()
        {
            Name = "IBS Mean Reversion Strategy";
            Description = "A mean reversion strategy using Internal Bar Strength (IBS) and a 10/25-bar price envelope for entry, exiting when close exceeds the previous bar's high.";
        }

        protected override Task OnLoad()
        {
            _history.Clear();
            _inPosition = false;
            return Task.CompletedTask;
        }

        protected override Task OnTick(RequestKlineData.OHLCCandlesData candlesData)
        {
            var current = candlesData.candles.Last();
            _history.Add(current);

            // Need at least AvgRangeLookback bars of history to evaluate signals
            if (_history.Count < AvgRangeLookback)
                return Task.CompletedTask;

            if (_inPosition)
            {
                // Exit: close > yesterday's high
                var previousBar = _history[^2];
                if (current.Close > previousBar.High)
                {
                    RaiseSell(AmountType.Percentage, 100);
                    _inPosition = false;
                    StrategyLog($"EXIT  | Close {current.Close:F2} > Prev High {previousBar.High:F2}");
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
                    RaiseBuy(AmountType.Percentage, 100);
                    _inPosition = true;
                    StrategyLog($"ENTRY | Close {current.Close:F2} < Threshold {entryThreshold:F2} | IBS {ibs:F4}");
                }
            }

            return Task.CompletedTask;
        }

        private static decimal CalculateIBS(RequestKlineData.OHLCCandle candle)
        {
            decimal range = candle.High - candle.Low;
            if (range == 0)
                return 0.5m; // Neutral when there is no range
            return (candle.Close - candle.Low) / range;
        }

        private decimal GetHighestHigh(int lookback)
        {
            decimal highest = decimal.MinValue;
            int start = _history.Count - lookback;
            for (int i = start; i < _history.Count; i++)
            {
                if (_history[i].High > highest)
                    highest = _history[i].High;
            }
            return highest;
        }

        private decimal GetAverageHigh(int lookback)
        {
            decimal sum = 0;
            int start = _history.Count - lookback;
            for (int i = start; i < _history.Count; i++)
                sum += _history[i].High;
            return sum / lookback;
        }

        private decimal GetAverageLow(int lookback)
        {
            decimal sum = 0;
            int start = _history.Count - lookback;
            for (int i = start; i < _history.Count; i++)
                sum += _history[i].Low;
            return sum / lookback;
        }
    }
}
