using Omnipotent.Services.OmniTrader.Backtesting;

namespace Omnipotent.Tests.OmniTrader
{
    public class TradeRecordTests
    {
        [Fact]
        public void RealizedPnL_WinningTrade_ReturnsPositive()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 1200m,
            };

            Assert.Equal(200m, trade.RealizedPnL);
        }

        [Fact]
        public void RealizedPnL_LosingTrade_ReturnsNegative()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 800m,
            };

            Assert.Equal(-200m, trade.RealizedPnL);
        }

        [Fact]
        public void RealizedPnL_BreakEven_ReturnsZero()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 1000m,
            };

            Assert.Equal(0m, trade.RealizedPnL);
        }

        [Fact]
        public void RealizedPnLPercent_WinningTrade_ReturnsCorrectPercentage()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 1200m,
            };

            Assert.Equal(20m, trade.RealizedPnLPercent);
        }

        [Fact]
        public void RealizedPnLPercent_ZeroCost_ReturnsZero()
        {
            var trade = new TradeRecord
            {
                EntryCost = 0m,
                ExitProceeds = 500m,
            };

            Assert.Equal(0m, trade.RealizedPnLPercent);
        }

        [Fact]
        public void IsWin_Profit_ReturnsTrue()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 1500m,
            };

            Assert.True(trade.IsWin);
        }

        [Fact]
        public void IsWin_Loss_ReturnsFalse()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 500m,
            };

            Assert.False(trade.IsWin);
        }

        [Fact]
        public void IsWin_BreakEven_ReturnsFalse()
        {
            var trade = new TradeRecord
            {
                EntryCost = 1000m,
                ExitProceeds = 1000m,
            };

            Assert.False(trade.IsWin);
        }

        [Fact]
        public void TradeRecord_StoresEntryAndExitTimes()
        {
            var entryTime = new DateTime(2024, 1, 1);
            var exitTime = new DateTime(2024, 1, 5);

            var trade = new TradeRecord
            {
                EntryTime = entryTime,
                ExitTime = exitTime,
                EntryPrice = 100m,
                ExitPrice = 110m,
                EntryQuantity = 10m,
                EntryCost = 1000m,
                EntryFee = 1m,
                ExitProceeds = 1099m,
                ExitFee = 1m,
            };

            Assert.Equal(entryTime, trade.EntryTime);
            Assert.Equal(exitTime, trade.ExitTime);
            Assert.Equal(100m, trade.EntryPrice);
            Assert.Equal(110m, trade.ExitPrice);
        }
    }
}
