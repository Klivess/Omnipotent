using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Ledger
{
    /// <summary>What economic event an entry records.</summary>
    public enum LedgerEntryKind
    {
        /// <summary>Cash moved in or out of an account.</summary>
        Cash = 0,
        /// <summary>Owned quantity of an asset changed (spot inventory).</summary>
        Inventory = 1,
        /// <summary>Derivative exposure opened, changed or closed (CFD).</summary>
        Exposure = 2,
        /// <summary>An explicit cost — see <see cref="CostKind"/> on the entry's detail.</summary>
        Cost = 3,
        /// <summary>Profit or loss realized on a closing trade.</summary>
        RealizedPnL = 4,
        /// <summary>A correction posted against an earlier entry. The original is never altered.</summary>
        Adjustment = 5
    }

    public enum CostKind
    {
        None = 0,
        Spread = 1,
        Commission = 2,
        Financing = 3,
        MakerTakerFee = 4,
        FxConversion = 5,
        BrokerAdjustment = 6
    }

    /// <summary>Whether a cost was measured, estimated, or simply not available. Performance reports
    /// must say which — an estimated cost presented as observed is a lie about the P&amp;L.</summary>
    public enum CostQuality { Observed = 0, Estimated = 1, Unavailable = 2 }

    /// <summary>Where an entry came from. Broker-originated activity the platform did not initiate is
    /// imported but permanently marked as external.</summary>
    public enum EntryOrigin { Platform = 0, BrokerReported = 1, ExternalManual = 2, Correction = 3 }

    public enum ReconciliationState { Unreconciled = 0, Matched = 1, Break = 2, Explained = 3 }

    /// <summary>
    /// One immutable accounting consequence of an external or internal event. Entries are appended,
    /// never updated — a mistake is corrected by posting an <see cref="LedgerEntryKind.Adjustment"/>
    /// that references the original, so the audit trail survives every correction.
    /// </summary>
    public sealed class LedgerEntry
    {
        public required string Id { get; init; }
        public required DateTime Ts { get; init; }
        public required string AccountId { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public string? InstrumentId { get; init; }

        public required LedgerEntryKind Kind { get; init; }
        /// <summary>The asset the amount is denominated in (cash currency or crypto ticker).</summary>
        public required string Asset { get; init; }
        /// <summary>Signed value in <see cref="Asset"/> — the debit/credit.</summary>
        public required decimal Amount { get; init; }
        /// <summary>Signed quantity change for inventory/exposure entries.</summary>
        public decimal Quantity { get; init; }
        public decimal? Price { get; init; }

        public CostKind CostKind { get; init; } = CostKind.None;
        public CostQuality CostQuality { get; init; } = CostQuality.Observed;

        /// <summary>What produced this entry: <c>order</c>, <c>fill</c>, <c>activity</c>, <c>reconciliation</c>.</summary>
        public required string SourceType { get; init; }
        public string? SourceId { get; init; }
        public EntryOrigin Origin { get; init; } = EntryOrigin.Platform;
        public ReconciliationState ReconciliationState { get; set; } = ReconciliationState.Unreconciled;
        public string? StrategyId { get; init; }
        /// <summary>For adjustments: the entry being corrected.</summary>
        public string? CorrectsEntryId { get; init; }
        public string? Note { get; init; }

        public static LedgerEntry Cash(string accountId, VenueId venue, TradingEnvironment env, string currency,
            decimal amount, string sourceType, string? sourceId, DateTime ts, EntryOrigin origin = EntryOrigin.Platform,
            string? strategyId = null) => new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = ts,
                AccountId = accountId,
                Venue = venue,
                Environment = env,
                Kind = LedgerEntryKind.Cash,
                Asset = currency,
                Amount = amount,
                SourceType = sourceType,
                SourceId = sourceId,
                Origin = origin,
                StrategyId = strategyId
            };
    }

    /// <summary>A valuation of a native amount into the reporting currency. Native values are never
    /// overwritten — the conversion is recorded alongside them with its rate and source.</summary>
    public sealed class Valuation
    {
        public required string Asset { get; init; }
        public required decimal NativeAmount { get; init; }
        public required decimal Rate { get; init; }
        public required string ReportingCurrency { get; init; }
        public required decimal ReportingAmount { get; init; }
        public required string Source { get; init; }
        public required DateTime AsOfUtc { get; init; }
    }

    /// <summary>A position as the platform believes it to be, with the venue's own view attached so a
    /// disagreement is visible rather than averaged away.</summary>
    public sealed class FirmPosition
    {
        public required string InstrumentId { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required string AccountId { get; init; }
        public required ExposureKind Exposure { get; init; }
        public decimal Quantity { get; set; }
        public decimal AveragePrice { get; set; }
        public decimal? MarkPrice { get; set; }
        public decimal RealizedPnL { get; set; }
        public decimal Fees { get; set; }
        public string? StrategyId { get; set; }
        public DateTime? OpenedUtc { get; set; }

        /// <summary>Broker-reported quantity at the last reconciliation, when known.</summary>
        public decimal? VenueQuantity { get; set; }

        public decimal Notional => Math.Abs(Quantity) * (MarkPrice ?? AveragePrice);
        public decimal SignedNotional => Quantity * (MarkPrice ?? AveragePrice);
        public decimal UnrealizedPnL => MarkPrice is > 0m ? (MarkPrice.Value - AveragePrice) * Quantity : 0m;
        public bool Disagrees => VenueQuantity.HasValue && Math.Abs(VenueQuantity.Value - Quantity) > 0.00000001m;
    }

    // ── reconciliation ────────────────────────────────────────────────────────────

    public enum BreakKind { Position = 0, Order = 1, Balance = 2, Fill = 3 }

    /// <summary>Why internal and broker state differ. Classifying a difference is what turns "the
    /// numbers don't match" into an actionable item.</summary>
    public enum BreakClassification
    {
        /// <summary>A real event we simply have not booked yet — expected to clear on its own.</summary>
        Timing = 0,
        /// <summary>The venue reports something we have no record of receiving.</summary>
        MissingEvent = 1,
        /// <summary>Symbol or account mapping is wrong on our side.</summary>
        MappingError = 2,
        /// <summary>Someone traded this account outside the platform.</summary>
        ExternalManualActivity = 3,
        /// <summary>None of the above — needs a human.</summary>
        Unexplained = 4
    }

    public sealed class ReconciliationBreak
    {
        public required string Id { get; init; }
        public required string RunId { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required BreakKind Kind { get; init; }
        public required BreakClassification Classification { get; set; }
        /// <summary>What the break is about — an instrument id, an order id, or an asset ticker.</summary>
        public required string Subject { get; init; }
        public decimal? InternalValue { get; init; }
        public decimal? VenueValue { get; init; }
        public required string Detail { get; init; }
        public DateTime DetectedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? ResolvedUtc { get; set; }
        public string? Resolution { get; set; }

        public bool Open => ResolvedUtc == null;
        /// <summary>Timing differences are expected to clear; anything else blocks new automated
        /// exposure until it is resolved.</summary>
        public bool Material => Open && Classification != BreakClassification.Timing;
    }

    public sealed class ReconciliationRun
    {
        public required string Id { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        /// <summary>startup | reconnect | ambiguous_order | fill | scheduled | manual</summary>
        public required string Trigger { get; init; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public List<ReconciliationBreak> Breaks { get; init; } = new();
        public int Checked { get; set; }
        public string? Error { get; set; }

        public int MaterialBreaks => Breaks.Count(b => b.Material);
    }
}
