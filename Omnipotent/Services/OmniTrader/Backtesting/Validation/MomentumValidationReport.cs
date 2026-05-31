namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Aggregated Section 11 validation output, stored alongside a momentum backtest's result so the
    /// stitched out-of-sample curve, deflated Sharpe, cost sensitivity, survivorship audit, and
    /// turnover/capacity are all reported together. Every field is optional/serialisation-friendly so
    /// single-symbol results are unaffected.
    /// </summary>
    public sealed class MomentumValidationReport
    {
        // 11.1 Walk-forward (stitched OOS).
        public decimal WalkForwardOosPnLPercent { get; set; }
        public decimal WalkForwardOosSharpe { get; set; }
        public decimal WalkForwardOosMaxDrawdownPercent { get; set; }
        public int WalkForwardFolds { get; set; }

        // 11.2 Deflated Sharpe.
        public int TrialsTested { get; set; }
        public double DeflatedSharpe { get; set; }
        public double ExpectedMaxSharpe { get; set; }

        // 11.3 Cost sensitivity (1×/2×/3×).
        public List<CostSensitivity.Row> CostSensitivity { get; set; } = new();

        // 11.4 Survivorship audit.
        public int UniverseCoins { get; set; }
        public int DelistedCoins { get; set; }
        public bool PointInTimeUniverse { get; set; }
        public List<string> DelistedExamples { get; set; } = new();

        // 11.5 Turnover & capacity.
        public decimal WeeklyTurnover { get; set; }
        public decimal AnnualTurnover { get; set; }
        public decimal EstimatedCapacityUsd { get; set; }

        // Gross vs net (Section 10) on the primary run.
        public decimal GrossPnLPercent { get; set; }
        public decimal NetPnLPercent { get; set; }

        public List<string> Notes { get; set; } = new();
    }
}
