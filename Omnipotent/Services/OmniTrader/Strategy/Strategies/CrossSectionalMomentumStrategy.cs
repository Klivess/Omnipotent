using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;
using Omnipotent.Services.OmniTrader.Strategy.Params;

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

        /// <summary>Engine key of the regime asset (e.g. BTCUSDT). A live/paper deploy uses this param;
        /// the backtest runner overrides it from the request's universe settings.</summary>
        [Param("Regime Symbol", Group = "Universe", IsSymbol = true)]
        public string RegimeSymbol { get; set; } = "BTCUSDT";

        // ── Configurable parameters (views over Config), so the dynamic UI can tune a momentum deploy. ──
        [Param("Universe Cap", Group = "Universe", Min = 10, Max = 400)] public int UniverseCap { get => Config.UniverseCap; set => Config.UniverseCap = value; }
        [Param("Min Universe", Group = "Universe", Min = 2, Max = 100)] public int MinUniverseSize { get => Config.MinUniverseSize; set => Config.MinUniverseSize = value; }
        [Param("Top Fraction", Group = "Selection", Min = 0.05, Max = 0.5, Step = 0.05)] public double TopFraction { get => Config.TopFraction; set => Config.TopFraction = value; }
        [Param("Bottom Fraction", Group = "Selection", Min = 0, Max = 0.5, Step = 0.05, Help = "0 = long-only; >0 needs leverage > 1")] public double BottomFraction { get => Config.BottomFraction; set => Config.BottomFraction = value; }
        [Param("Lookback Days", Group = "Signal", Min = 5, Max = 90)] public int LookbackDays { get => Config.LookbackDays; set => Config.LookbackDays = value; }
        [Param("Skip Days", Group = "Signal", Min = 0, Max = 5)] public int SkipDays { get => Config.SkipDays; set => Config.SkipDays = value; }
        [Param("Rebalance Days", Group = "Signal", Min = 1, Max = 30)] public int RebalanceDays { get => Config.RebalanceDays; set => Config.RebalanceDays = value; }
        [Param("Vol Lookback Days", Group = "Signal", Min = 5, Max = 90)] public int VolLookbackDays { get => Config.VolLookbackDays; set => Config.VolLookbackDays = value; }
        [Param("Target Portfolio Vol", Group = "Sizing", Min = 0.05, Max = 1.5, Step = 0.05)] public double TargetPortfolioVol { get => Config.TargetPortfolioVol; set => Config.TargetPortfolioVol = value; }
        [Param("Max Weight / Asset", Group = "Sizing", Min = 0.02, Max = 1, Step = 0.02)] public double MaxWeightPerAsset { get => Config.MaxWeightPerAsset; set => Config.MaxWeightPerAsset = value; }
        [Param("Max Gross Leverage", Group = "Sizing", Min = 1, Max = 3, Step = 0.5)] public double MaxGrossLeverage { get => Config.MaxGrossLeverage; set => Config.MaxGrossLeverage = value; }
        [Param("Regime MA Days", Group = "Regime", Min = 10, Max = 250)] public int RegimeMaDays { get => Config.RegimeMaDays; set => Config.RegimeMaDays = value; }
        [Param("Drawdown Killswitch", Group = "Risk", Min = 0.05, Max = 1, Step = 0.05)] public double DdKillswitch { get => Config.DdKillswitch; set => Config.DdKillswitch = value; }
        [Param("Stop Loss %", Group = "Risk", Min = 0, Max = 0.9, Step = 0.05, Help = "0 = exit only at re-rank")] public double StopLossPct { get => Config.StopLossPct; set => Config.StopLossPct = value; }
        [Param("Take Profit %", Group = "Risk", Min = 0, Max = 2, Step = 0.05)] public double TakeProfitPct { get => Config.TakeProfitPct; set => Config.TakeProfitPct = value; }

        /// <summary>Optional symbol → base-ticker map (for denylist matching) and shortable set. Injected by the queue.</summary>
        public IReadOnlyDictionary<string, string>? Tickers { get; set; }
        public IReadOnlySet<string>? Shortable { get; set; }

        private readonly KillswitchState _killswitch = new();
        private DateTime? _lastRebalance;

        public override StrategySymbols DeclareSymbols() => StrategySymbols.FromUniverse(new UniverseSpec
        {
            TopN = Config.UniverseCap,
            RegimeSymbol = RegimeSymbol ?? "BTCUSDT",
        });

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
                // Attach a protective bracket to entries/increases (not to exits), so a name is held with
                // a stop/target between rebalances instead of only being exited at the weekly re-rank.
                decimal targetWeight = weights.TryGetValue(o.Symbol, out var w) ? w : 0m;
                bool entering = (o.Side == OrderSide.Buy && targetWeight > 0m) || (o.Side == OrderSide.Sell && targetWeight < 0m);
                decimal? sl = null, tp = null;
                if (entering && (cfg.StopLossPct > 0 || cfg.TakeProfitPct > 0) && marks.TryGetValue(o.Symbol, out var px) && px > 0m)
                {
                    bool isLong = targetWeight > 0m;
                    if (cfg.StopLossPct > 0) sl = isLong ? px * (1m - (decimal)cfg.StopLossPct) : px * (1m + (decimal)cfg.StopLossPct);
                    if (cfg.TakeProfitPct > 0) tp = isLong ? px * (1m + (decimal)cfg.TakeProfitPct) : px * (1m - (decimal)cfg.TakeProfitPct);
                }
                await SubmitOrder(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = o.Side,
                    Type = OrderType.Market,
                    Symbol = o.Symbol,
                    Qty = o.Qty,
                    Leverage = Leverage,
                    StopLossPrice = sl,
                    TakeProfitPrice = tp,
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
