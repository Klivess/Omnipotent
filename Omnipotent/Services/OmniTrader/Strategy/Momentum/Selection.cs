namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    public sealed class SelectionResult
    {
        public List<string> Longs { get; init; } = new();
        public List<string> Shorts { get; init; } = new();
        /// <summary>True when the universe was below <see cref="MomentumConfig.MinUniverseSize"/> — do not trade.</summary>
        public bool Skip { get; init; }
    }

    /// <summary>
    /// Section 5: rank by score and pick the top/bottom fractions. Handles the edge cases the spec
    /// calls out — too-small universe (skip), no long/short overlap, and (via the caller) force-exit
    /// of names that dropped out of the universe between rebalances.
    /// </summary>
    public static class Selection
    {
        public static SelectionResult Select(IReadOnlyList<(string Symbol, decimal Score)> scored, MomentumConfig cfg)
        {
            if (scored.Count < cfg.MinUniverseSize)
                return new SelectionResult { Skip = true };

            var ranked = scored.OrderByDescending(x => x.Score).Select(x => x.Symbol).ToList();
            int n = ranked.Count;

            int longCount = (int)Math.Round(cfg.TopFraction * n, MidpointRounding.AwayFromZero);
            int shortCount = (int)Math.Round(cfg.BottomFraction * n, MidpointRounding.AwayFromZero);
            longCount = Math.Clamp(longCount, 0, n);
            shortCount = Math.Clamp(shortCount, 0, n);

            // Never let the long and short slices overlap: cap their sum at the universe size,
            // trimming the short book first (longs are the higher-conviction side).
            if (longCount + shortCount > n) shortCount = n - longCount;
            if (shortCount < 0) shortCount = 0;

            var longs = ranked.Take(longCount).ToList();
            var shorts = shortCount > 0 ? ranked.Skip(n - shortCount).ToList() : new List<string>();
            return new SelectionResult { Longs = longs, Shorts = shorts };
        }
    }
}
