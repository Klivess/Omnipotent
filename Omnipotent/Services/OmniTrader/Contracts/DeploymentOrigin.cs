namespace Omnipotent.Services.OmniTrader.Contracts
{
    /// <summary>
    /// Provenance and expectations stored with a deployment when it is made from a deploy plan: which
    /// Alpha Lab candidate (or Studio strategy) it came from, the exact version that was tested, and the
    /// evaluation it was deployed on the strength of. The Deploy page compares live behaviour against it,
    /// and the health check uses <see cref="BarsPerTrade"/> to tell "quiet" from "not working".
    /// </summary>
    public sealed class DeploymentOrigin
    {
        /// <summary>alpha | studio | builtin | manual</summary>
        public string Source { get; init; } = "manual";
        public string? CandidateId { get; init; }
        public string? CandidateStatus { get; init; }
        public double? Score { get; init; }
        public string? StrategyId { get; init; }
        public int? StrategyVersion { get; init; }
        public string? StrategyName { get; init; }
        /// <summary>single | basket | universe</summary>
        public string? Shape { get; init; }
        public string? Label { get; init; }
        public DeploymentEvidence? Evaluated { get; init; }
        /// <summary>The backtest's average bars between trades; null when it never traded.</summary>
        public double? BarsPerTrade { get; init; }
        /// <summary>Longest lookback the strategy's inputs imply, in bars (a heuristic; null when unknown).</summary>
        public int? WarmupBars { get; init; }
        /// <summary>Plan checks at warn level that the person deployed past.</summary>
        public List<string> AcceptedWarnings { get; init; } = new();
        /// <summary>Strategy inputs the person changed from the tested defaults under Advanced.</summary>
        public List<string> ChangedInputs { get; init; } = new();
        public DateTime PlannedUtc { get; init; } = DateTime.UtcNow;
    }

    /// <summary>The headline numbers a deployment was made on (the Alpha Lab's primary run).</summary>
    public sealed class DeploymentEvidence
    {
        public TimeInterval Interval { get; init; }
        public List<string> Symbols { get; init; } = new();
        public int? Bars { get; init; }
        public double? ReturnPct { get; init; }
        public double? CagrPct { get; init; }
        public double? Sharpe { get; init; }
        public double? MaxDrawdownPct { get; init; }
        public int? Trades { get; init; }
        public double? WinRatePct { get; init; }
        public double? ExposurePct { get; init; }
        public double? OosSharpe { get; init; }
        public double? StressedReturnPct { get; init; }
        public double? DeflatedSharpe { get; init; }
        public string? CostPreset { get; init; }
        public decimal? FeeFraction { get; init; }
        public decimal? SlippageFraction { get; init; }
        public DateTime? EvaluatedUtc { get; init; }
    }
}
