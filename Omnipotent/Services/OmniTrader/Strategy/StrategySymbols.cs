namespace Omnipotent.Services.OmniTrader.Strategy
{
    /// <summary>A dynamic universe (top-N by 24h quote volume on a venue) that a strategy resolves to
    /// concrete symbols at run time.</summary>
    public sealed class UniverseSpec
    {
        public int TopN { get; init; } = 100;
        public string QuoteAsset { get; init; } = "USDT";
        public string RegimeSymbol { get; init; } = "BTCUSDT";
    }

    /// <summary>
    /// What a strategy trades, declared by the strategy itself (so the frontend no longer asks for a
    /// coin/currency). Either a fixed set of pairs (one for single-symbol strategies, many for a fixed
    /// basket) or a dynamic <see cref="UniverseSpec"/> resolved at deploy/run time.
    /// </summary>
    public sealed class StrategySymbols
    {
        public IReadOnlyList<string>? Fixed { get; init; }
        public UniverseSpec? Universe { get; init; }

        public bool IsUniverse => Universe != null;

        public static StrategySymbols Of(params string[] symbols) => new() { Fixed = symbols };
        public static StrategySymbols FromUniverse(UniverseSpec u) => new() { Universe = u };

        /// <summary>The single declared symbol (first fixed), or a sensible fallback.</summary>
        public string Primary => Fixed != null && Fixed.Count > 0 ? Fixed[0] : Universe?.RegimeSymbol ?? "BTCUSDT";
    }
}
