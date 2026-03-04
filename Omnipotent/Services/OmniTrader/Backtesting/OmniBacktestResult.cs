namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public class TradeRecord
    {
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal EntryQuantity { get; set; }
        public decimal EntryCost { get; set; }
        public decimal EntryFee { get; set; }

        public DateTime ExitTime { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal ExitProceeds { get; set; }
        public decimal ExitFee { get; set; }

        public decimal RealizedPnL => ExitProceeds - EntryCost;
        public decimal RealizedPnLPercent => EntryCost == 0 ? 0 : RealizedPnL / EntryCost * 100;
        public bool IsWin => RealizedPnL > 0;
    }

    public class OmniBacktestResult
    {
        // Balances
        public decimal InitialEquity { get; set; }
        public decimal FinalEquity { get; set; }
        public decimal FinalQuoteBalance { get; set; }
        public decimal FinalBaseBalance { get; set; }

        // Trade metrics
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public decimal WinRate => TotalTrades == 0 ? 0 : (decimal)WinningTrades / TotalTrades * 100;

        // P&L
        public decimal TotalPnL => FinalEquity - InitialEquity;
        public decimal TotalPnLPercent => InitialEquity == 0 ? 0 : TotalPnL / InitialEquity * 100;
        public decimal TotalFeesPaid { get; set; }

        // Per-trade averages
        public decimal AverageWin { get; set; }
        public decimal AverageLoss { get; set; }
        public decimal LargestWin { get; set; }
        public decimal LargestLoss { get; set; }

        // Risk metrics
        public decimal ProfitFactor { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal MaxDrawdownPercent { get; set; }
        public decimal SharpeRatio { get; set; }

        // Buy & hold comparison
        public decimal BuyAndHoldPnLPercent { get; set; }
        public bool BeatsBuyAndHold => TotalPnLPercent > BuyAndHoldPnLPercent;

        // Durations
        public int TotalCandles { get; set; }
        public TimeSpan BacktestDuration { get; set; }

        // Individual trades
        public List<TradeRecord> Trades { get; set; } = [];

        public override string ToString()
        {
            return $"""
                === Backtest Result ===
                Period:             {TotalCandles} candles ({BacktestDuration.TotalDays:F1} days)
                Initial Equity:     {InitialEquity:F2}
                Final Equity:       {FinalEquity:F2}
                Total P&L:          {TotalPnL:F2} ({TotalPnLPercent:F2}%)
                Total Fees Paid:    {TotalFeesPaid:F2}
                ---
                Total Trades:       {TotalTrades}
                Win Rate:           {WinRate:F2}%  ({WinningTrades}W / {LosingTrades}L)
                Avg Win:            {AverageWin:F2}
                Avg Loss:           {AverageLoss:F2}
                Largest Win:        {LargestWin:F2}
                Largest Loss:       {LargestLoss:F2}
                Profit Factor:      {ProfitFactor:F2}
                ---
                Max Drawdown:       {MaxDrawdown:F2} ({MaxDrawdownPercent:F2}%)
                Sharpe Ratio:       {SharpeRatio:F4}
                ---
                Buy & Hold P&L:     {BuyAndHoldPnLPercent:F2}%
                Beats Buy & Hold:   {BeatsBuyAndHold}
                """;
        }
    }
}
