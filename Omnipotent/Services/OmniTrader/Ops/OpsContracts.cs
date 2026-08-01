namespace Omnipotent.Services.OmniTrader.Ops
{
    /// <summary>
    /// Alert severity, matching the operating model: Critical alerts require acknowledgement and stay
    /// open until the underlying state is actually resolved — acknowledging is not fixing.
    /// </summary>
    public enum AlertSeverity
    {
        Informational = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    public sealed class Alert
    {
        public required string Id { get; init; }
        public required AlertSeverity Severity { get; init; }
        /// <summary>Coarse grouping: connectivity, data, orders, risk, reconciliation, security, strategy.</summary>
        public required string Category { get; init; }
        public required string Title { get; init; }
        public required string Message { get; set; }
        /// <summary>Identity of the *condition*, not the occurrence. A condition that is already open
        /// updates its alert instead of raising a duplicate.</summary>
        public required string DedupeKey { get; init; }

        public DateTime RaisedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? AcknowledgedUtc { get; set; }
        public string? AcknowledgedBy { get; set; }
        public DateTime? ResolvedUtc { get; set; }
        public int OccurrenceCount { get; set; } = 1;

        /// <summary>The venue/account/strategy the alert concerns, for filtering and for telling an
        /// operator where to look.</summary>
        public string? Venue { get; init; }
        public string? Environment { get; init; }
        public string? StrategyId { get; init; }
        public string? RecoveryHint { get; init; }

        public bool Open => ResolvedUtc == null;
        public bool NeedsAcknowledgement => Open && Severity == AlertSeverity.Critical && AcknowledgedUtc == null;
    }

    /// <summary>One operational area's health, as shown on the Systems page.</summary>
    public sealed class HealthArea
    {
        public required string Area { get; init; }
        public required bool Healthy { get; init; }
        public string? Detail { get; init; }
        public List<HealthSignal> Signals { get; init; } = new();
    }

    public sealed class HealthSignal
    {
        public required string Name { get; init; }
        public required bool Ok { get; init; }
        public string? Value { get; init; }
        public string? Detail { get; init; }
    }

    /// <summary>The firm-wide health verdict. Trading authority requires healthy market, account, risk
    /// and order services; analytics may degrade independently without blocking anything.</summary>
    public sealed class FirmHealth
    {
        public required bool TradingPermitted { get; init; }
        public required string Summary { get; init; }
        public List<HealthArea> Areas { get; init; } = new();
        public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;
        public List<string> Blockers { get; init; } = new();
    }
}
