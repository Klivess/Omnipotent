namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Generic post-backtest validation output, attached to <c>BacktestResult.Validation</c>. Strategy-
    /// agnostic: cost sensitivity, a walk-forward out-of-sample run with an auto-built parameter sweep,
    /// the deflated Sharpe that penalises that sweep, and turnover. Null on runs that didn't request it.
    /// </summary>
    public class ValidationReport
    {
        // Walk-forward (stitched out-of-sample curve).
        public decimal WalkForwardOosPnLPercent { get; set; }
        public decimal WalkForwardOosSharpe { get; set; }
        public decimal WalkForwardOosMaxDrawdownPercent { get; set; }
        public int WalkForwardFolds { get; set; }

        // Deflated Sharpe (selection-bias adjusted; Trials = sweep grid size).
        public int TrialsTested { get; set; }
        public double DeflatedSharpe { get; set; }
        public double ExpectedMaxSharpe { get; set; }

        // Cost sensitivity (re-run at 1×/2×/3× fee+slippage).
        public List<CostSensitivity.Row> CostSensitivity { get; set; } = new();

        // Turnover (× of equity traded).
        public decimal WeeklyTurnover { get; set; }
        public decimal AnnualTurnover { get; set; }

        // Gross vs net on the primary run.
        public decimal GrossPnLPercent { get; set; }
        public decimal NetPnLPercent { get; set; }

        public List<string> Notes { get; set; } = new();
    }
}
