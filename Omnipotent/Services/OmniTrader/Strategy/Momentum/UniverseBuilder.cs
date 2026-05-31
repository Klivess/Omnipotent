using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>
    /// Section 3: point-in-time universe construction. Every filter uses only data available at the
    /// rebalance timestamp (the snapshots are already sliced to t by the caller), so a universe built
    /// here is survivorship-free as long as the snapshot set itself includes coins that later delisted.
    /// </summary>
    public static class UniverseBuilder
    {
        /// <summary>A common explicit denylist of stablecoins / wrapped / pegged tickers.</summary>
        public static readonly HashSet<string> DefaultDenylist = new(StringComparer.OrdinalIgnoreCase)
        {
            "USDT", "USDC", "DAI", "BUSD", "TUSD", "USDP", "USDD", "GUSD", "FRAX", "LUSD", "FDUSD",
            "PYUSD", "USDE", "EUROC", "EURT", "USTC", "UST", "WBTC", "WETH", "WBETH", "STETH",
            "WSTETH", "CBETH", "RETH", "SUSD", "USDS",
        };

        /// <summary>
        /// Build the tradable universe at t. <paramref name="denylist"/> defaults to
        /// <see cref="DefaultDenylist"/>. Returns snapshots ordered by market cap (desc), capped to
        /// <see cref="MomentumConfig.UniverseCap"/>.
        /// </summary>
        public static List<AssetSnapshot> Build(
            IEnumerable<AssetSnapshot> candidates,
            MomentumConfig cfg,
            ISet<string>? denylist = null)
        {
            denylist ??= DefaultDenylist;
            bool needShortable = cfg.BottomFraction > 0;
            var survivors = new List<AssetSnapshot>();

            foreach (var a in candidates)
            {
                // 1. Stablecoins / wrapped / pegged: explicit denylist + peg vol check.
                string ticker = !string.IsNullOrEmpty(a.Ticker) ? a.Ticker.ToUpperInvariant() : BaseTicker(a.Symbol);
                if (denylist.Contains(ticker)) continue;
                if (IsPegged(a.History, cfg)) continue;

                // 2. Enough history to form a signal.
                if (a.History.Count < cfg.MinHistoryDays) continue;

                // 3. Liquidity floor: trailing N-day average USD quote volume.
                if (TrailingAvgQuoteVolume(a.History, cfg.LiquidityLookbackDays) < (decimal)cfg.LiquidityFloorUsd)
                    continue;

                // 4. Shortable intersection when a short book is enabled.
                if (needShortable && !a.Shortable) continue;

                survivors.Add(a);
            }

            // 5. Rank by market cap, falling back to trailing quote volume when cap is unavailable
            //    (e.g. the Binance universe has no market cap). The spec permits ranking by volume.
            decimal RankKey(AssetSnapshot a) => a.MarketCap > 0m
                ? a.MarketCap
                : TrailingAvgQuoteVolume(a.History, cfg.LiquidityLookbackDays);
            survivors.Sort((x, y) => RankKey(y).CompareTo(RankKey(x)));
            if (survivors.Count > cfg.UniverseCap) survivors.RemoveRange(cfg.UniverseCap, survivors.Count - cfg.UniverseCap);
            return survivors;
        }

        /// <summary>30-day realized vol of daily returns below the peg threshold ⇒ treat as a stable/peg.</summary>
        public static bool IsPegged(IReadOnlyList<OHLCCandle> history, MomentumConfig cfg)
        {
            int window = 30;
            if (history.Count < window + 1) return false; // not enough data to judge — don't exclude on this basis
            var rets = new List<double>(window);
            for (int i = history.Count - window; i < history.Count; i++)
            {
                decimal prev = history[i - 1].Close;
                if (prev <= 0m) continue;
                rets.Add((double)(history[i].Close / prev - 1m));
            }
            if (rets.Count < 2) return false;
            double mean = rets.Average();
            double std = Math.Sqrt(rets.Sum(r => (r - mean) * (r - mean)) / rets.Count);
            return std < cfg.PegVolThreshold;
        }

        public static decimal TrailingAvgQuoteVolume(IReadOnlyList<OHLCCandle> history, int lookback)
        {
            if (history.Count == 0) return 0m;
            int n = Math.Min(lookback, history.Count);
            decimal sum = 0m;
            for (int i = history.Count - n; i < history.Count; i++) sum += history[i].Volume;
            return sum / n;
        }

        /// <summary>Strip a quote suffix (BTCUSD → BTC, ETH/USD → ETH) for denylist matching.</summary>
        public static string BaseTicker(string symbol)
        {
            string s = symbol.Replace("/", "").ToUpperInvariant();
            foreach (var quote in new[] { "USDT", "USDC", "USD", "EUR", "GBP" })
                if (s.Length > quote.Length && s.EndsWith(quote)) return s[..^quote.Length];
            return s;
        }
    }
}
