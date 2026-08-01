using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Execution venues the firm connects to. A venue is an *external* counterparty; the internal
    /// paper simulator is <see cref="Internal"/> so paper fills still get a venue identity and can
    /// never be confused with broker truth.
    /// </summary>
    public enum VenueId
    {
        Internal = 0,
        Kraken = 1,
        IG = 2,
        Binance = 3
    }

    /// <summary>
    /// Which reality an account, order or strategy deployment lives in. These are separate
    /// permissions with independent credentials, ledgers and audit scopes — nothing crosses.
    /// </summary>
    public enum TradingEnvironment
    {
        Historical = 0,
        Paper = 1,
        Demo = 2,
        Live = 3
    }

    /// <summary>How much authority a deployment/account has been granted (progressive authority).</summary>
    public enum ExecutionAuthority
    {
        /// <summary>Signals recorded, nothing submitted anywhere.</summary>
        Observe = 0,
        /// <summary>Fills simulated internally against live data.</summary>
        Paper = 1,
        /// <summary>Broker-supported simulation (IG demo).</summary>
        Demo = 2,
        /// <summary>Real money, but every order queues for a human decision.</summary>
        ApprovalRequired = 3,
        /// <summary>Real money, automated, under a restricted risk budget.</summary>
        Automated = 4
    }

    /// <summary>The economic nature of what a venue lets you hold. Kept explicit because a leveraged
    /// CFD position must never be added to owned spot inventory as though both were assets.</summary>
    public enum ExposureKind
    {
        /// <summary>Owned inventory (Kraken spot crypto, cash).</summary>
        Inventory = 0,
        /// <summary>Leveraged derivative exposure (IG CFD) — notional, not an owned asset.</summary>
        Derivative = 1
    }

    public enum AssetClass
    {
        Unknown = 0,
        Crypto = 1,
        Equity = 2,
        Index = 3,
        Forex = 4,
        Commodity = 5
    }

    /// <summary>
    /// What a venue can actually do. The order ticket, risk engine and strategy runtime all read
    /// this instead of hard-coding venue behaviour — unsupported features are disabled with a
    /// stated reason rather than silently simulated.
    /// </summary>
    public sealed class VenueCapabilities
    {
        public required VenueId Venue { get; init; }
        public required string DisplayName { get; init; }
        public required ExposureKind Exposure { get; init; }
        public required AssetClass[] AssetClasses { get; init; }

        public bool SupportsShort { get; init; }
        public bool SupportsLeverage { get; init; }
        public decimal MaxLeverage { get; init; } = 1m;
        public bool SupportsAttachedProtection { get; init; }
        public bool SupportsStreamingPrices { get; init; }
        public bool SupportsStreamingAccount { get; init; }
        public bool SupportsHistoricalData { get; init; }
        public bool SupportsDemoEnvironment { get; init; }
        public OrderType[] OrderTypes { get; init; } = Array.Empty<OrderType>();

        /// <summary>Human-readable reasons a capability is missing, keyed by capability name. The UI
        /// shows these on disabled controls so a limitation is never invisible.</summary>
        public Dictionary<string, string> Limitations { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Supports(OrderType type) => OrderTypes.Contains(type);

        public string? WhyNot(string capability)
            => Limitations.TryGetValue(capability, out var why) ? why : null;
    }

    /// <summary>A venue's own description of a tradeable thing, before it is folded into the
    /// canonical instrument master.</summary>
    public sealed class VenueInstrumentDescriptor
    {
        public required VenueId Venue { get; init; }
        /// <summary>The venue's identifier — Kraken pair name, IG epic, Binance symbol.</summary>
        public required string VenueSymbol { get; init; }
        public required string DisplayName { get; init; }
        public AssetClass AssetClass { get; init; } = AssetClass.Unknown;
        public string BaseAsset { get; init; } = "";
        public string QuoteCurrency { get; init; } = "";
        public decimal TickSize { get; init; }
        public decimal QuantityStep { get; init; }
        public decimal MinQuantity { get; init; }
        public decimal? MaxQuantity { get; init; }
        public decimal ContractMultiplier { get; init; } = 1m;
        public decimal? MarginFactor { get; init; }
        public bool Tradeable { get; init; } = true;
        public string? TradingStatus { get; init; }
        public string? TradingHours { get; init; }
    }

    /// <summary>Broker-reported account state. Fields that do not apply to a venue stay null rather
    /// than being fabricated (Kraken spot has no margin/equity concept in this baseline).</summary>
    public sealed class VenueAccountSnapshot
    {
        public required VenueId Venue { get; init; }
        public required string AccountId { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required DateTime AsOfUtc { get; init; }
        public string BaseCurrency { get; init; } = "USD";

        public decimal? Balance { get; init; }
        public decimal? Equity { get; init; }
        public decimal? AvailableFunds { get; init; }
        public decimal? MarginUsed { get; init; }
        public decimal? UnrealizedPnL { get; init; }

        /// <summary>Owned inventory by asset (Kraken spot). Empty for pure derivative venues.</summary>
        public Dictionary<string, decimal> Inventory { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Inventory reserved against open orders — a sell can never exceed free inventory.</summary>
        public Dictionary<string, decimal> Reserved { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class VenuePositionSnapshot
    {
        public required VenueId Venue { get; init; }
        public required string VenueSymbol { get; init; }
        public required decimal Quantity { get; init; }
        public required ExposureKind Exposure { get; init; }
        public decimal? AveragePrice { get; init; }
        public decimal? MarkPrice { get; init; }
        public decimal? UnrealizedPnL { get; init; }
        public decimal? Leverage { get; init; }
        public string? VenuePositionId { get; init; }
    }

    public sealed class VenueOrderSnapshot
    {
        public required VenueId Venue { get; init; }
        public required string VenueOrderId { get; init; }
        public required string VenueSymbol { get; init; }
        public required OrderSide Side { get; init; }
        public required decimal Quantity { get; init; }
        public decimal FilledQuantity { get; init; }
        public decimal? AverageFillPrice { get; init; }
        public decimal Fee { get; init; }
        public string FeeCurrency { get; init; } = "";
        public required OrderStatus Status { get; init; }
        public string? ClientReference { get; init; }
        public DateTime? CreatedUtc { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>The resolved outcome of a submission. <see cref="Unknown"/> is a first-class result:
    /// it means the platform cannot prove whether the venue accepted the order, so automation must
    /// not retry until reconciliation settles it.</summary>
    public enum SubmissionOutcome { Accepted, Rejected, Unknown }

    public sealed class VenueSubmissionResult
    {
        public required SubmissionOutcome Outcome { get; init; }
        public string? VenueOrderId { get; init; }
        public string? ClientReference { get; init; }
        public string? Reason { get; init; }
        public DateTime AcknowledgedUtc { get; init; } = DateTime.UtcNow;

        public static VenueSubmissionResult Accepted(string venueOrderId, string? clientRef = null)
            => new() { Outcome = SubmissionOutcome.Accepted, VenueOrderId = venueOrderId, ClientReference = clientRef };
        public static VenueSubmissionResult Rejected(string reason)
            => new() { Outcome = SubmissionOutcome.Rejected, Reason = reason };
        public static VenueSubmissionResult Unknown(string reason, string? clientRef = null)
            => new() { Outcome = SubmissionOutcome.Unknown, Reason = reason, ClientReference = clientRef };
    }

    /// <summary>Health of one named channel of a venue connection. REST and streaming are tracked
    /// separately — losing a price stream does not imply losing order access.</summary>
    public sealed class ChannelHealth
    {
        public required string Channel { get; init; }
        public bool Connected { get; set; }
        public DateTime? LastOkUtc { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public string? LastError { get; set; }
        public int ConsecutiveFailures { get; set; }
        /// <summary>Remaining request budget as a 0–1 fraction where the venue exposes quotas.</summary>
        public double? QuotaRemaining { get; set; }

        public bool Degraded => !Connected || ConsecutiveFailures > 0;
    }

    public sealed class VenueHealthSnapshot
    {
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public bool Configured { get; init; }
        public List<ChannelHealth> Channels { get; init; } = new();
        public DateTime AsOfUtc { get; init; } = DateTime.UtcNow;

        /// <summary>A venue is tradeable only when every channel required for order management is up.</summary>
        public bool OrderPathHealthy =>
            Configured && Channels.Where(c => c.Channel.Contains("rest", StringComparison.OrdinalIgnoreCase)
                                           || c.Channel.Contains("order", StringComparison.OrdinalIgnoreCase))
                                  .All(c => c.Connected);
    }
}
