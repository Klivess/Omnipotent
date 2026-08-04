using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Instruments
{
    /// <summary>
    /// How a canonical instrument is dealt at one venue. Everything needed to validate a price, a
    /// quantity and a schedule before an order is built lives here.
    /// </summary>
    public sealed class VenueMapping
    {
        public required VenueId Venue { get; init; }
        public required string VenueSymbol { get; init; }
        public decimal TickSize { get; init; }
        public decimal QuantityStep { get; init; }
        public decimal MinQuantity { get; init; }
        public decimal? MaxQuantity { get; init; }
        public decimal ContractMultiplier { get; init; } = 1m;
        public decimal? MarginFactor { get; init; }
        public bool Tradeable { get; init; } = true;
        public string? TradingStatus { get; init; }
        public string? TradingHours { get; init; }

        /// <summary>Round a quantity down to the venue's step. Rounding down never creates size the
        /// user did not ask for, so an oversize order can't slip through a rounding artefact.</summary>
        public decimal RoundQuantity(decimal qty)
        {
            if (QuantityStep <= 0m) return qty;
            decimal steps = Math.Floor(Math.Abs(qty) / QuantityStep);
            return Math.Sign(qty) * steps * QuantityStep;
        }

        public decimal RoundPrice(decimal price)
        {
            if (TickSize <= 0m) return price;
            return Math.Round(price / TickSize, MidpointRounding.ToZero) * TickSize;
        }
    }

    /// <summary>
    /// A tradeable thing with an identity independent of any broker's symbol. Strategies and analytics
    /// reference only <see cref="Id"/>; venue symbols exist solely inside adapters and mappings.
    /// </summary>
    public sealed class Instrument
    {
        /// <summary>Internal identity, e.g. <c>crypto:BTC/USD</c> or <c>index:UK100</c>.</summary>
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required AssetClass AssetClass { get; init; }

        // ── economic definition ───────────────────────────────────────────────────
        public string BaseAsset { get; init; } = "";
        public string QuoteCurrency { get; init; } = "USD";
        public decimal ContractMultiplier { get; init; } = 1m;
        /// <summary>How a position in this instrument is valued: owned inventory or derivative notional.</summary>
        public ExposureKind Exposure { get; init; } = ExposureKind.Inventory;

        public List<VenueMapping> Venues { get; init; } = new();

        // ── data state ────────────────────────────────────────────────────────────
        public DateTime? LastUpdatedUtc { get; set; }
        public string? DataSource { get; set; }
        /// <summary>How old a price may be before automated actions on this instrument are blocked.</summary>
        public TimeSpan FreshnessThreshold { get; init; } = TimeSpan.FromMinutes(15);

        public VenueMapping? MappingFor(VenueId venue) => Venues.FirstOrDefault(v => v.Venue == venue);
        public bool TradableOn(VenueId venue) => MappingFor(venue) is { Tradeable: true };

        /// <summary>Build a canonical id from an asset class and a human pair name.</summary>
        public static string MakeId(AssetClass assetClass, string baseAsset, string quoteCurrency)
            => $"{assetClass.ToString().ToLowerInvariant()}:{baseAsset.ToUpperInvariant()}/{quoteCurrency.ToUpperInvariant()}";
    }

    /// <summary>Freshness verdict for one instrument's price data, used by the risk engine's
    /// data-integrity layer and shown on every operational screen.</summary>
    public sealed class DataFreshness
    {
        public required string InstrumentId { get; init; }
        public DateTime? LastUpdateUtc { get; init; }

        /// <summary>How old the newest data itself is. On a bar series this is at least one bar by
        /// definition, so it is only meaningful next to <see cref="Cadence"/>.</summary>
        public required TimeSpan Age { get; init; }
        /// <summary>How long since the feed last answered at all — the platform-health half of
        /// freshness, and the only half that is a fault on its own.</summary>
        public TimeSpan ObservationAge { get; init; }
        /// <summary>How often new data is expected, when the caller knew. Null for live ticks.</summary>
        public TimeSpan? Cadence { get; init; }

        public required bool Stale { get; init; }
        /// <summary>The feed is healthy but has stopped producing bars on a market that keeps
        /// session hours. Reported instead of staleness, because a shut exchange is not a fault.</summary>
        public bool MarketLikelyClosed { get; init; }

        public string? Source { get; init; }
        public string? Issue { get; init; }
    }
}
