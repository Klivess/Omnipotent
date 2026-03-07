using Omnipotent.Services.OmniTrader.Data;
using static Omnipotent.Services.OmniTrader.Data.RequestKlineData;

namespace Omnipotent.Tests.OmniTrader
{
    public class TechnicalIndicatorsTests
    {
        private static OHLCCandle MakeCandle(decimal open, decimal high, decimal low, decimal close, decimal volume = 100m)
        {
            return new OHLCCandle
            {
                Timestamp = DateTime.UtcNow,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                VWAP = (high + low + close) / 3,
                TradeCount = 10
            };
        }

        #region SMA

        [Fact]
        public void SMA_ConstantPrices_ReturnsThatPrice()
        {
            var candles = Enumerable.Range(0, 5)
                .Select(_ => MakeCandle(10m, 10m, 10m, 10m))
                .ToList();

            decimal sma = TechnicalIndicators.SMA(candles, 5, 4);
            Assert.Equal(10m, sma);
        }

        [Fact]
        public void SMA_KnownValues_ReturnsCorrectAverage()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 10),
                MakeCandle(0, 0, 0, 20),
                MakeCandle(0, 0, 0, 30),
                MakeCandle(0, 0, 0, 40),
                MakeCandle(0, 0, 0, 50),
            };

            // SMA(3) at index 4 = (30+40+50)/3 = 40
            decimal sma = TechnicalIndicators.SMA(candles, 3, 4);
            Assert.Equal(40m, sma);
        }

        [Fact]
        public void SMA_PeriodEqualsCount_UsesAllCandles()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 2),
                MakeCandle(0, 0, 0, 4),
                MakeCandle(0, 0, 0, 6),
            };

            decimal sma = TechnicalIndicators.SMA(candles, 3, 2);
            Assert.Equal(4m, sma);
        }

        [Fact]
        public void SMA_NotEnoughCandles_ThrowsArgumentException()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 10),
                MakeCandle(0, 0, 0, 20),
            };

            Assert.Throws<ArgumentException>(() => TechnicalIndicators.SMA(candles, 5, 1));
        }

        #endregion

        #region RSI

        [Fact]
        public void RSI_AllGains_Returns100()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 10),
                MakeCandle(0, 0, 0, 20),
                MakeCandle(0, 0, 0, 30),
                MakeCandle(0, 0, 0, 40),
                MakeCandle(0, 0, 0, 50),
            };

            decimal rsi = TechnicalIndicators.RSI(candles, 4, 4);
            Assert.Equal(100m, rsi);
        }

        [Fact]
        public void RSI_AllLosses_Returns0()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 50),
                MakeCandle(0, 0, 0, 40),
                MakeCandle(0, 0, 0, 30),
                MakeCandle(0, 0, 0, 20),
                MakeCandle(0, 0, 0, 10),
            };

            decimal rsi = TechnicalIndicators.RSI(candles, 4, 4);
            Assert.Equal(0m, rsi);
        }

        [Fact]
        public void RSI_EqualGainsAndLosses_Returns50()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 100),
                MakeCandle(0, 0, 0, 110),  // +10
                MakeCandle(0, 0, 0, 100),  // -10
                MakeCandle(0, 0, 0, 110),  // +10
                MakeCandle(0, 0, 0, 100),  // -10
            };

            decimal rsi = TechnicalIndicators.RSI(candles, 4, 4);
            Assert.Equal(50m, rsi);
        }

        [Fact]
        public void RSI_NotEnoughCandles_ThrowsArgumentException()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 10),
                MakeCandle(0, 0, 0, 20),
            };

            Assert.Throws<ArgumentException>(() => TechnicalIndicators.RSI(candles, 5, 1));
        }

        #endregion

        #region ATR

        [Fact]
        public void ATR_KnownValues_ReturnsCorrectResult()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(100, 105, 95, 102),   // Previous candle
                MakeCandle(102, 110, 98, 108),    // TR = max(12, 8, 4) = 12
                MakeCandle(108, 115, 100, 112),   // TR = max(15, 7, 8) = 15
            };

            decimal atr = TechnicalIndicators.ATR(candles, 2, 2);
            Assert.Equal(13.5m, atr);
        }

        [Fact]
        public void ATR_FlatCandles_ReturnsZero()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(100, 100, 100, 100),
                MakeCandle(100, 100, 100, 100),
                MakeCandle(100, 100, 100, 100),
            };

            decimal atr = TechnicalIndicators.ATR(candles, 2, 2);
            Assert.Equal(0m, atr);
        }

        [Fact]
        public void ATR_NotEnoughCandles_ThrowsArgumentException()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(100, 110, 90, 105),
            };

            Assert.Throws<ArgumentException>(() => TechnicalIndicators.ATR(candles, 5, 0));
        }

        #endregion

        #region VolumeRatio

        [Fact]
        public void VolumeRatio_DoubleAverage_Returns2()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 0, volume: 100),
                MakeCandle(0, 0, 0, 0, volume: 100),
                MakeCandle(0, 0, 0, 0, volume: 200),
            };

            decimal ratio = TechnicalIndicators.VolumeRatio(candles, 2, 2);
            Assert.Equal(2m, ratio);
        }

        [Fact]
        public void VolumeRatio_SameVolume_Returns1()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 0, volume: 50),
                MakeCandle(0, 0, 0, 0, volume: 50),
                MakeCandle(0, 0, 0, 0, volume: 50),
            };

            decimal ratio = TechnicalIndicators.VolumeRatio(candles, 2, 2);
            Assert.Equal(1m, ratio);
        }

        [Fact]
        public void VolumeRatio_ZeroAverageVolume_Returns1()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 0, volume: 0),
                MakeCandle(0, 0, 0, 0, volume: 0),
                MakeCandle(0, 0, 0, 0, volume: 100),
            };

            decimal ratio = TechnicalIndicators.VolumeRatio(candles, 2, 2);
            Assert.Equal(1m, ratio);
        }

        [Fact]
        public void VolumeRatio_NotEnoughCandles_ThrowsArgumentException()
        {
            var candles = new List<OHLCCandle>
            {
                MakeCandle(0, 0, 0, 0, volume: 100),
            };

            Assert.Throws<ArgumentException>(() => TechnicalIndicators.VolumeRatio(candles, 5, 0));
        }

        #endregion

        #region IBS

        [Fact]
        public void IBS_CloseAtHigh_Returns1()
        {
            var candle = MakeCandle(95, 100, 90, 100);
            decimal ibs = TechnicalIndicators.IBS(candle);
            Assert.Equal(1m, ibs);
        }

        [Fact]
        public void IBS_CloseAtLow_Returns0()
        {
            var candle = MakeCandle(95, 100, 90, 90);
            decimal ibs = TechnicalIndicators.IBS(candle);
            Assert.Equal(0m, ibs);
        }

        [Fact]
        public void IBS_CloseAtMidpoint_Returns05()
        {
            var candle = MakeCandle(95, 100, 90, 95);
            decimal ibs = TechnicalIndicators.IBS(candle);
            Assert.Equal(0.5m, ibs);
        }

        [Fact]
        public void IBS_NoRange_Returns05()
        {
            var candle = MakeCandle(100, 100, 100, 100);
            decimal ibs = TechnicalIndicators.IBS(candle);
            Assert.Equal(0.5m, ibs);
        }

        [Fact]
        public void IBS_ValueBetween0And1()
        {
            var candle = MakeCandle(95, 110, 80, 100);
            decimal ibs = TechnicalIndicators.IBS(candle);
            Assert.InRange(ibs, 0m, 1m);
        }

        #endregion
    }
}
