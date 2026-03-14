using Omnipotent.Services.OmniTrader.Backtesting;

namespace Omnipotent.Tests.OmniTrader
{
    public class OmniBacktestResultTests
    {
        #region WinRate

        [Fact]
        public void WinRate_AllWins_Returns100()
        {
            var result = new OmniBacktestResult
            {
                TotalTrades = 5,
                WinningTrades = 5,
                LosingTrades = 0,
            };

            Assert.Equal(100m, result.WinRate);
        }

        [Fact]
        public void WinRate_AllLosses_Returns0()
        {
            var result = new OmniBacktestResult
            {
                TotalTrades = 5,
                WinningTrades = 0,
                LosingTrades = 5,
            };

            Assert.Equal(0m, result.WinRate);
        }

        [Fact]
        public void WinRate_HalfAndHalf_Returns50()
        {
            var result = new OmniBacktestResult
            {
                TotalTrades = 10,
                WinningTrades = 5,
                LosingTrades = 5,
            };

            Assert.Equal(50m, result.WinRate);
        }

        [Fact]
        public void WinRate_NoTrades_Returns0()
        {
            var result = new OmniBacktestResult
            {
                TotalTrades = 0,
                WinningTrades = 0,
                LosingTrades = 0,
            };

            Assert.Equal(0m, result.WinRate);
        }

        #endregion

        #region TotalPnL

        [Fact]
        public void TotalPnL_Profit_ReturnsPositive()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 12_000m,
            };

            Assert.Equal(2_000m, result.TotalPnL);
        }

        [Fact]
        public void TotalPnL_Loss_ReturnsNegative()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 8_000m,
            };

            Assert.Equal(-2_000m, result.TotalPnL);
        }

        [Fact]
        public void TotalPnLPercent_ReturnsCorrectPercentage()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 15_000m,
            };

            Assert.Equal(50m, result.TotalPnLPercent);
        }

        [Fact]
        public void TotalPnLPercent_ZeroInitial_Returns0()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 0m,
                FinalEquity = 1000m,
            };

            Assert.Equal(0m, result.TotalPnLPercent);
        }

        #endregion

        #region BeatsBuyAndHold

        [Fact]
        public void BeatsBuyAndHold_StrategyBetter_ReturnsTrue()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 15_000m,
                BuyAndHoldPnLPercent = 30m,
            };

            // TotalPnLPercent = 50%, BuyAndHold = 30%
            Assert.True(result.BeatsBuyAndHold);
        }

        [Fact]
        public void BeatsBuyAndHold_BuyAndHoldBetter_ReturnsFalse()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 11_000m,
                BuyAndHoldPnLPercent = 20m,
            };

            // TotalPnLPercent = 10%, BuyAndHold = 20%
            Assert.False(result.BeatsBuyAndHold);
        }

        [Fact]
        public void BeatsBuyAndHold_Equal_ReturnsFalse()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 12_000m,
                BuyAndHoldPnLPercent = 20m,
            };

            // TotalPnLPercent = 20%, BuyAndHold = 20%
            Assert.False(result.BeatsBuyAndHold);
        }

        #endregion

        #region ToString

        [Fact]
        public void ToString_ContainsKeyMetrics()
        {
            var result = new OmniBacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 12_000m,
                TotalTrades = 5,
                WinningTrades = 3,
                LosingTrades = 2,
                TotalFeesPaid = 50m,
                TotalCandles = 100,
                BacktestDuration = TimeSpan.FromDays(10),
            };

            string output = result.ToString();
            Assert.Contains("Backtest Result", output);
            Assert.Contains("Initial Equity", output);
            Assert.Contains("Final Equity", output);
            Assert.Contains("Total Trades", output);
            Assert.Contains("Win Rate", output);
        }

        #endregion

        #region Trades list

        [Fact]
        public void Trades_DefaultIsEmpty()
        {
            var result = new OmniBacktestResult();
            Assert.NotNull(result.Trades);
            Assert.Empty(result.Trades);
        }

        #endregion
    }
}
