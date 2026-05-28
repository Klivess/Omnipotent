using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public sealed class BacktestResult
    {
        public decimal InitialEquity { get; set; }
        public decimal FinalEquity { get; set; }
        public decimal FinalQuoteBalance { get; set; }
        public decimal FinalBaseBalance { get; set; }

        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public decimal WinRate => TotalTrades == 0 ? 0 : (decimal)WinningTrades / TotalTrades * 100m;
        public decimal TotalPnL => FinalEquity - InitialEquity;
        public decimal TotalPnLPercent => InitialEquity == 0 ? 0 : TotalPnL / InitialEquity * 100m;
        public decimal TotalFeesPaid { get; set; }
        public decimal AverageWin { get; set; }
        public decimal AverageLoss { get; set; }
        public decimal LargestWin { get; set; }
        public decimal LargestLoss { get; set; }
        public decimal ProfitFactor { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal MaxDrawdownPercent { get; set; }
        public decimal SharpeRatio { get; set; }
        public decimal BuyAndHoldPnLPercent { get; set; }
        public bool BeatsBuyAndHold => TotalPnLPercent > BuyAndHoldPnLPercent;
        public int TotalCandles { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public List<TradeRecord> Trades { get; set; } = new();
        public List<EquityPoint> EquityCurve { get; set; } = new();
    }
}
