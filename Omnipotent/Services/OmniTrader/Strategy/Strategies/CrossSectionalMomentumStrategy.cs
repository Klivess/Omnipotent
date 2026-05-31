using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    /// <summary>
    /// Cross-sectional crypto momentum (weekly-rebalanced, vol-targeted) — the spec end-to-end.
    /// Runs in the engine's portfolio mode: on each rebalance bar it builds a point-in-time universe
    /// (Section 3), scores names by risk-adjusted momentum (4), selects top/bottom fractions (5),
    /// applies the BTC regime filter (6), sizes by inverse-vol scaled to a target portfolio vol (7),
    /// diffs targets against the current book into orders (8), and enforces the drawdown killswitch (9).
    /// Costs (10) are charged by the simulated router (fees, slippage, and short funding via the borrow rate).
    /// </summary>
    [TradingStrategy(
        "Cross-Sectional Momentum",
        "Weekly-rebalanced cross-sectional crypto momentum. Ranks a point-in-time universe by trailing " +
        "risk-adjusted momentum, goes long the top fraction (optionally short the bottom), sizes by " +
        "inverse-vol scaled to a target portfolio vol, gated by a BTC trend regime filter and a drawdown " +
        "killswitch. Requires portfolio-mode backtests (multi-asset universe data).",
        RequiresUniverse = true)]
    public sealed class CrossSectionalMomentumStrategy : TradingStrategy
    {
        /// <summary>Strategy parameters. The backtest queue injects this from the job config before running.</summary>
        public MomentumConfig Config { get; set; } = new();

        /// <summary>Engine key of the regime asset (e.g. the coin id for BTC). Injected by the queue.</summary>
        public string? RegimeSymbol { get; set; }

        /// <summary>Optional symbol → base-ticker map (for denylist matching) and shortable set. Injected by the queue.</summary>
        public IReadOnlyDictionary<string, string>? Tickers { get; set; }
        public IReadOnlySet<string>? Shortable { get; set; }

        private readonly KillswitchState _killswitch = new();
        private DateTime? _lastRebalance;

        public override Task OnStart(CancellationToken ct)
        {
            _lastRebalance = null;
            return Task.CompletedTask;
        }

        public override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
        {
            var cfg = Config;

            // Drawdown killswitch (Section 9): if tripped, flatten the book and pause new entries.
            bool paused = _killswitch.Update(Equity, cfg);
            if (paused)
            {
                await FlattenAllAsync(bar, ct);
                return;
            }

            // Rebalance only on schedule (every RebalanceDays). Aligned to the data's day grid.
            if (!IsRebalanceBar(bar.T)) return;
            _lastRebalance = bar.T.Date;

            // ── Build the point-in-time universe (Section 3) ────────────────────────
            var snapshots = new List<AssetSnapshot>(bar.Histories.Count);
            foreach (var (sym, hist) in bar.Histories)
            {
                snapshots.Add(new AssetSnapshot
                {
                    Symbol = sym,
                    Ticker = Tickers != null && Tickers.TryGetValue(sym, out var tk) ? tk : "",
                    History = hist,
                    MarketCap = bar.MarketCaps.TryGetValue(sym, out var mc) ? mc : 0m,
                    Shortable = Shortable == null || Shortable.Contains(sym),
                });
            }
            var universe = UniverseBuilder.Build(snapshots, cfg);

            // ── Signals (Section 4) ─────────────────────────────────────────────────
            var scored = new List<(string Symbol, decimal Score)>(universe.Count);
            var vols = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in universe)
            {
                var sig = MomentumSignals.Compute(a.History, cfg);
                if (sig == null) continue;
                scored.Add((a.Symbol, sig.Value.Score));
                vols[a.Symbol] = sig.Value.RealizedVol;
            }

            // ── Selection (Section 5) ───────────────────────────────────────────────
            var selection = Selection.Select(scored, cfg);
            if (selection.Skip)
            {
                if (cfg.SkipAction == SkipAction.Cash) await FlattenAllAsync(bar, ct);
                return; // SkipAction.Hold: leave the prior book untouched.
            }

            var longs = selection.Longs;
            var shorts = selection.Shorts;
            decimal postScale = 1m;

            // ── Regime filter (Section 6) ───────────────────────────────────────────
            bool regimeOn = RegimeSymbol != null && bar.Histories.TryGetValue(RegimeSymbol, out var regimeHist)
                ? RegimeFilter.RegimeOn(regimeHist, cfg.RegimeMaDays)
                : true;
            if (!regimeOn)
            {
                if (cfg.BottomFraction > 0)
                {
                    // Long-short: drop the long book, optionally keep shorts.
                    longs = new List<string>();
                    if (!cfg.KeepShortsWhenRiskOff) shorts = new List<string>();
                }
                else
                {
                    // Long-only: go to cash, or hold partial exposure via the risk-off scalar.
                    if (cfg.RiskOffScalar > 0) postScale = (decimal)cfg.RiskOffScalar;
                    else longs = new List<string>();
                }
            }

            // ── Sizing (Section 7) ──────────────────────────────────────────────────
            var weights = Sizing.TargetWeights(longs, shorts, vols, cfg);
            if (postScale != 1m)
                foreach (var k in weights.Keys.ToList()) weights[k] *= postScale;

            // ── Rebalance → orders (Section 8) ──────────────────────────────────────
            var marks = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var barVol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sym, hist) in bar.Histories)
            {
                if (hist.Count == 0) continue;
                marks[sym] = hist[^1].Close;
                barVol[sym] = hist[^1].Volume;
            }

            var orders = Rebalance.BuildOrders(weights, Positions, marks, barVol, Equity, cfg);
            foreach (var o in orders)
            {
                await SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = o.Side,
                    Type = OrderType.Market,
                    Symbol = o.Symbol,
                    Qty = o.Qty,
                    Leverage = Leverage,
                }, ct);
            }
        }

        private bool IsRebalanceBar(DateTime t)
        {
            if (_lastRebalance == null) return true;
            return (t.Date - _lastRebalance.Value).TotalDays >= Config.RebalanceDays;
        }

        /// <summary>Exit every open position at market (used by the killswitch and the cash skip action).</summary>
        private async Task FlattenAllAsync(PortfolioBar bar, CancellationToken ct)
        {
            foreach (var (sym, qty) in Positions)
            {
                if (qty == 0m) continue;
                await SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = qty > 0m ? OrderSide.Sell : OrderSide.Buy,
                    Type = OrderType.Market,
                    Symbol = sym,
                    Qty = Math.Abs(qty),
                    Leverage = Leverage,
                }, ct);
            }
        }
    }
}
