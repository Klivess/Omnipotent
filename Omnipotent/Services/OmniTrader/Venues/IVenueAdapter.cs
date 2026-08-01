using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Translates one broker's authentication, instruments, account state and order handling into
    /// the platform's internal contracts. Adapters make **no** portfolio, risk or strategy
    /// decisions — they are pure translation plus connection ownership.
    ///
    /// Every method may throw; callers treat a throw on submission as
    /// <see cref="SubmissionOutcome.Unknown"/> unless the adapter proves otherwise.
    /// </summary>
    public interface IVenueAdapter
    {
        VenueId Venue { get; }
        TradingEnvironment Environment { get; }
        VenueCapabilities Capabilities { get; }

        /// <summary>True once credentials are present and a session has been established.</summary>
        bool IsConfigured { get; }

        VenueHealthSnapshot Health { get; }

        /// <summary>Establish or refresh an authenticated session. Safe to call repeatedly.</summary>
        Task<bool> ConnectAsync(CancellationToken ct = default);

        /// <summary>The venue's tradeable instruments (optionally filtered by a search term).</summary>
        Task<IReadOnlyList<VenueInstrumentDescriptor>> GetInstrumentsAsync(string? search = null, CancellationToken ct = default);

        Task<VenueAccountSnapshot> GetAccountAsync(CancellationToken ct = default);

        Task<IReadOnlyList<VenuePositionSnapshot>> GetPositionsAsync(CancellationToken ct = default);

        Task<IReadOnlyList<VenueOrderSnapshot>> GetWorkingOrdersAsync(CancellationToken ct = default);

        /// <summary>Broker-truth state of specific orders — the basis of reconciliation and the only
        /// legitimate way to resolve an <see cref="SubmissionOutcome.Unknown"/> submission.</summary>
        Task<IReadOnlyList<VenueOrderSnapshot>> QueryOrdersAsync(IEnumerable<string> venueOrderIds, CancellationToken ct = default);

        /// <summary>Submit an order. <paramref name="clientReference"/> is the platform's idempotency
        /// key and must be persisted by the adapter onto the venue order where the venue supports it.</summary>
        Task<VenueSubmissionResult> SubmitOrderAsync(OrderRequest request, string clientReference, CancellationToken ct = default);

        Task<bool> CancelOrderAsync(string venueOrderId, CancellationToken ct = default);

        /// <summary>Latest traded/mid price for a venue symbol, or 0 when unavailable.</summary>
        Task<decimal> GetLatestPriceAsync(string venueSymbol, CancellationToken ct = default);

        /// <summary>Historical bars in the venue's own terms, normalized to <see cref="OHLCCandle"/>.</summary>
        Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string venueSymbol, TimeInterval interval, int count, CancellationToken ct = default);
    }
}
