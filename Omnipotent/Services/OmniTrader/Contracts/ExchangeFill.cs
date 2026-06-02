namespace Omnipotent.Services.OmniTrader.Contracts
{
    /// <summary>
    /// A snapshot of an order's execution state pulled back from a real exchange (e.g. Kraken
    /// QueryOrders). Quantities/fees are CUMULATIVE for the order; the live session diffs them
    /// against what it has already booked to derive incremental fills.
    /// </summary>
    public sealed class ExchangeFill
    {
        public required string ExchangeOrderId { get; init; }
        public required OrderSide Side { get; init; }
        /// <summary>Cumulative executed base volume.</summary>
        public required decimal CumulativeQty { get; init; }
        /// <summary>Average execution price (cost / executed volume).</summary>
        public required decimal AvgPrice { get; init; }
        /// <summary>Cumulative fee paid on the order.</summary>
        public required decimal CumulativeFee { get; init; }
        /// <summary>True once the order is terminal (closed/canceled/expired) and no more fills will arrive.</summary>
        public required bool Closed { get; init; }
        public string Symbol { get; init; } = "";
    }
}
