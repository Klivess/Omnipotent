using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public sealed class BacktestResult
    {
        /// <summary>Section 11 validation output for momentum (portfolio) backtests; null for single-symbol runs.</summary>
        public MomentumValidationReport? Validation { get; set; }

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
        public List<OHLCCandle> Candles { get; set; } = new();

        // ---- Derived analytics ----
        // Computed from Trades / EquityCurve / Duration, all of which are persisted, so these
        // also enrich previously-stored backtests when re-fetched. Every divisor is guarded.

        private double DurationYears => Duration.TotalDays > 0 ? Duration.TotalDays / 365.0 : 0;

        // Returns
        public decimal AnnualizedReturnPercent
        {
            get
            {
                double years = DurationYears;
                if (years <= 0 || InitialEquity == 0) return 0;
                double growth = (double)(FinalEquity / InitialEquity);
                if (growth <= 0) return 0;
                return (decimal)((Math.Pow(growth, 1.0 / years) - 1.0) * 100.0);
            }
        }
        public decimal AlphaVsBuyAndHoldPercent => TotalPnLPercent - BuyAndHoldPnLPercent;

        // Risk-adjusted
        public decimal SortinoRatio => BacktestMetrics.Sortino(EquityCurve);
        public decimal AnnualizedVolatilityPercent => BacktestMetrics.AnnualizedVolatilityPercent(EquityCurve);
        public decimal CalmarRatio => MaxDrawdownPercent == 0 ? 0 : AnnualizedReturnPercent / MaxDrawdownPercent;
        public decimal RecoveryFactor => MaxDrawdown == 0 ? 0 : TotalPnL / MaxDrawdown;
        public int MaxDrawdownDurationBars => BacktestMetrics.MaxDrawdownDuration(EquityCurve).bars;
        public double MaxDrawdownDurationHours => BacktestMetrics.MaxDrawdownDuration(EquityCurve).span.TotalHours;

        // Trade quality
        public decimal Expectancy => Trades.Count == 0 ? 0 : Trades.Sum(t => t.RealizedPnL) / Trades.Count;
        public decimal ExpectancyPercent => InitialEquity == 0 ? 0 : Expectancy / InitialEquity * 100m;
        public decimal PayoffRatio => AverageLoss == 0 ? 0 : AverageWin / AverageLoss;
        public int MaxConsecutiveWins => BacktestMetrics.MaxConsecutive(Trades).wins;
        public int MaxConsecutiveLosses => BacktestMetrics.MaxConsecutive(Trades).losses;

        // Trade durations / exposure
        public double AverageTradeDurationHours => Trades.Count == 0 ? 0 : Trades.Average(t => (t.ExitTime - t.EntryTime).TotalHours);
        public double MaxTradeDurationHours => Trades.Count == 0 ? 0 : Trades.Max(t => (t.ExitTime - t.EntryTime).TotalHours);
        public double MinTradeDurationHours => Trades.Count == 0 ? 0 : Trades.Min(t => (t.ExitTime - t.EntryTime).TotalHours);
        public decimal ExposurePercent
        {
            get
            {
                if (Duration.TotalHours <= 0 || Trades.Count == 0) return 0;
                double inMarket = Trades.Sum(t => (t.ExitTime - t.EntryTime).TotalHours);
                return (decimal)(inMarket / Duration.TotalHours * 100.0);
            }
        }

        // Long / short breakdown
        public int LongTrades => Trades.Count(t => !t.IsShort);
        public int ShortTrades => Trades.Count(t => t.IsShort);
        public decimal LongWinRate
        {
            get { var l = Trades.Where(t => !t.IsShort).ToList(); return l.Count == 0 ? 0 : (decimal)l.Count(t => t.IsWin) / l.Count * 100m; }
        }
        public decimal ShortWinRate
        {
            get { var s = Trades.Where(t => t.IsShort).ToList(); return s.Count == 0 ? 0 : (decimal)s.Count(t => t.IsWin) / s.Count * 100m; }
        }

        // Per-trade return distribution
        public decimal AverageTradeReturnPercent => Trades.Count == 0 ? 0 : Trades.Average(TradeReturnPercent);
        public decimal BestTradePercent => Trades.Count == 0 ? 0 : Trades.Max(TradeReturnPercent);
        public decimal WorstTradePercent => Trades.Count == 0 ? 0 : Trades.Min(TradeReturnPercent);

        private static decimal TradeReturnPercent(TradeRecord t)
        {
            decimal cost = t.EntryPrice * t.Qty;
            return cost == 0 ? 0 : t.RealizedPnL / cost * 100m;
        }
    }
}
