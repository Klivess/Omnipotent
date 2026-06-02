namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// The generic <see cref="ValidationReport"/> plus the momentum-research-only extras that depend on
    /// point-in-time universe data (survivorship audit and capacity). Produced by the opt-in
    /// <see cref="MomentumBacktestRunner"/>; ordinary universe backtests get the base report.
    /// </summary>
    public sealed class MomentumValidationReport : ValidationReport
    {
        // Survivorship audit (needs the point-in-time universe dataset).
        public int UniverseCoins { get; set; }
        public int DelistedCoins { get; set; }
        public bool PointInTimeUniverse { get; set; }
        public List<string> DelistedExamples { get; set; } = new();

        // Capacity (needs per-coin USD volumes).
        public decimal EstimatedCapacityUsd { get; set; }
    }
}
