using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Risk
{
    /// <summary>The layers a proposal passes through. Every layer runs; a hard failure in any one of
    /// them blocks, but the decision record keeps every rule's verdict so a rejection is explainable.</summary>
    public enum RiskLayer
    {
        DataIntegrity = 0,
        OrderValidity = 1,
        TradeRisk = 2,
        StrategyRisk = 3,
        VenueRisk = 4,
        PortfolioRisk = 5,
        OperationalRisk = 6
    }

    /// <summary>Hard controls block execution. Soft controls require acknowledgement or approval but
    /// do not by themselves stop a human-approved order.</summary>
    public enum RiskSeverity { Pass = 0, Soft = 1, Hard = 2 }

    public sealed class RiskRuleResult
    {
        public required RiskLayer Layer { get; init; }
        public required string Rule { get; init; }
        public required RiskSeverity Severity { get; init; }
        public string? Detail { get; init; }
        /// <summary>The observed value and the limit it was measured against, for the UI.</summary>
        public decimal? Observed { get; init; }
        public decimal? Limit { get; init; }

        public static RiskRuleResult Pass(RiskLayer layer, string rule, decimal? observed = null, decimal? limit = null)
            => new() { Layer = layer, Rule = rule, Severity = RiskSeverity.Pass, Observed = observed, Limit = limit };
        public static RiskRuleResult Soft(RiskLayer layer, string rule, string detail, decimal? observed = null, decimal? limit = null)
            => new() { Layer = layer, Rule = rule, Severity = RiskSeverity.Soft, Detail = detail, Observed = observed, Limit = limit };
        public static RiskRuleResult Hard(RiskLayer layer, string rule, string detail, decimal? observed = null, decimal? limit = null)
            => new() { Layer = layer, Rule = rule, Severity = RiskSeverity.Hard, Detail = detail, Observed = observed, Limit = limit };
    }

    public enum RiskVerdict
    {
        /// <summary>Cleared for submission under the current authority.</summary>
        Approved = 0,
        /// <summary>Cleared on the rules, but a soft control or the deployment's authority requires a
        /// human decision before submission.</summary>
        RequiresApproval = 1,
        /// <summary>A hard control failed. Nothing is submitted.</summary>
        Rejected = 2
    }

    /// <summary>An immutable, persisted record of one pre-trade decision with rule-level reasons.
    /// No broker order may exist without one of these pointing at it.</summary>
    public sealed class RiskDecision
    {
        public required string Id { get; init; }
        public required string ProposalId { get; init; }
        public required RiskVerdict Verdict { get; init; }
        public required DateTime DecidedUtc { get; init; }
        public List<RiskRuleResult> Rules { get; init; } = new();

        /// <summary>Portfolio state that would exist if this order executed — the engine evaluates the
        /// after-picture, not just the order in isolation.</summary>
        public decimal ProjectedGrossExposure { get; init; }
        public decimal ProjectedNetExposure { get; init; }
        public decimal ProjectedVenueExposure { get; init; }

        public IEnumerable<RiskRuleResult> Failures => Rules.Where(r => r.Severity != RiskSeverity.Pass);
        public string Summary => Verdict == RiskVerdict.Approved
            ? "approved"
            : string.Join("; ", Failures.Select(f => $"{f.Rule}: {f.Detail}"));
    }

    /// <summary>
    /// The firm's risk budget. Hard limits block; soft limits escalate to approval. Values are in the
    /// reporting currency unless named otherwise.
    /// </summary>
    public sealed class RiskLimits
    {
        // ── trade level ───────────────────────────────────────────────────────────
        public decimal MaxOrderNotional { get; set; } = 500m;
        public decimal SoftOrderNotional { get; set; } = 250m;
        public decimal MaxSlippageToleranceBps { get; set; } = 50m;

        // ── strategy level ────────────────────────────────────────────────────────
        public decimal MaxStrategyDailyLoss { get; set; } = 100m;
        public int MaxOrdersPerHourPerStrategy { get; set; } = 30;
        public int MaxConcurrentPositionsPerStrategy { get; set; } = 10;

        // ── portfolio level ───────────────────────────────────────────────────────
        public decimal MaxGrossExposure { get; set; } = 5_000m;
        public decimal MaxNetExposure { get; set; } = 3_000m;
        public decimal MaxSingleInstrumentExposure { get; set; } = 1_000m;
        public decimal MaxVenueExposure { get; set; } = 4_000m;
        public decimal MaxFirmDailyLoss { get; set; } = 250m;
        public decimal MaxDrawdownPercent { get; set; } = 15m;

        // ── operational ───────────────────────────────────────────────────────────
        public TimeSpan MaxPriceAge { get; set; } = TimeSpan.FromMinutes(15);
        public int MaxUnresolvedUnknownOrders { get; set; } = 0;
        public int MaxUnreconciledBreaks { get; set; } = 0;
        public int RepeatedRejectionThreshold { get; set; } = 5;

        public static RiskLimits Conservative => new();
    }

    /// <summary>An order the strategy runtime or a manual ticket wants to place. It carries everything
    /// the risk engine needs and is the only thing the order service accepts.</summary>
    public sealed class TradeProposal
    {
        public required string Id { get; init; }
        public required string InstrumentId { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required string AccountId { get; init; }
        public required OrderSide Side { get; init; }
        public required OrderType Type { get; init; }
        public required decimal Quantity { get; init; }
        public decimal? LimitPrice { get; init; }
        public decimal? StopPrice { get; init; }
        public decimal? StopLossPrice { get; init; }
        public decimal? TakeProfitPrice { get; init; }

        /// <summary>Which strategy version proposed this, or null for a manual ticket.</summary>
        public string? StrategyId { get; init; }
        public string? StrategyVersion { get; init; }
        public string? DeploymentId { get; init; }
        public required ExecutionAuthority Authority { get; init; }

        /// <summary>The mark the decision was taken at, and when that mark was observed. Slippage is
        /// measured against this, and a stale timestamp fails the data-integrity layer.</summary>
        public decimal DecisionPrice { get; init; }
        public DateTime DataTimestampUtc { get; init; } = DateTime.UtcNow;
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>A proposal that has expired is not executed on stale intent.</summary>
        public DateTime? ExpiresUtc { get; init; }
        public string? Rationale { get; init; }

        public decimal Notional => Quantity * (LimitPrice ?? DecisionPrice);
        public bool Expired => ExpiresUtc.HasValue && DateTime.UtcNow > ExpiresUtc.Value;
    }

    /// <summary>The live portfolio picture the risk engine measures a proposal against.</summary>
    public sealed class RiskPortfolioState
    {
        public decimal GrossExposure { get; init; }
        public decimal NetExposure { get; init; }
        public decimal Equity { get; init; }
        public decimal PeakEquity { get; init; }
        public decimal DailyRealizedPnL { get; init; }
        public Dictionary<string, decimal> ExposureByInstrument { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<VenueId, decimal> ExposureByVenue { get; init; } = new();
        public Dictionary<string, decimal> DailyPnLByStrategy { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> OpenPositionsByStrategy { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Free inventory per asset on the target venue — a spot sell can never exceed it.</summary>
        public Dictionary<string, decimal> FreeInventory { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public decimal? AvailableFunds { get; init; }

        public decimal DrawdownPercent => PeakEquity > 0m ? (PeakEquity - Equity) / PeakEquity * 100m : 0m;
    }

    /// <summary>Operational facts that gate automation independently of any single order.</summary>
    public sealed class RiskOperationalState
    {
        public int UnknownOrders { get; init; }
        public int UnreconciledBreaks { get; init; }
        public int RecentRejections { get; init; }
        public bool VenueOrderPathHealthy { get; init; } = true;
        public bool SafeModeActive { get; init; }
        public string? SafeModeReason { get; init; }
    }
}
