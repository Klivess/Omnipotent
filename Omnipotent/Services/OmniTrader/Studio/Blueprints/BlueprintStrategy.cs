using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Studio.Blueprints
{
    /// <summary>
    /// Executes a compiled blueprint. Signals are vectorised formulas: in a backtest they are computed
    /// once over the primed series (sound because every formula is causal), in paper/live they are
    /// recomputed over the growing history each bar. Positions are managed per symbol: signal exits,
    /// bracket stop/target, trailing stop, time exit, cooldown, filter.
    /// </summary>
    [StudioHost]
    public sealed class BlueprintStrategy : TradingStrategy, IPrimedStrategy, IDynamicParameters
    {
        private readonly CompiledBlueprint compiled;
        private readonly Blueprint bp;
        private readonly Dictionary<string, double> inputs = new(StringComparer.OrdinalIgnoreCase);
        private string tradeSymbol;

        /// <summary>Fetches another symbol's bars (for ext()) when they are not part of the run's own series.</summary>
        public Func<string, TimeInterval, int, IReadOnlyList<OHLCCandle>>? ExternalBars { get; set; }

        private sealed class Signals
        {
            public double[]? EntryLong, ExitLong, EntryShort, ExitShort, Filter, Score, Atr, Vol, DollarVol;
            public Dictionary<DateTime, int> Index = new();
            public int Count;
        }

        private sealed class TradeState
        {
            public int EntryIndex = -1;
            public double Extreme;       // highest high (long) / lowest low (short) since entry
            public int CooldownUntil = -1;
        }

        private readonly Dictionary<string, Signals> primed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TradeState> states = new(StringComparer.OrdinalIgnoreCase);
        private TimeInterval interval = TimeInterval.OneDay;
        private int universeBars;

        public BlueprintStrategy(CompiledBlueprint compiled)
        {
            this.compiled = compiled;
            bp = compiled.Source;
            tradeSymbol = bp.Universe.Mode == "single" ? bp.Universe.Symbol : bp.Universe.RegimeSymbol;
            foreach (var input in bp.Inputs) inputs[input.Name] = input.Default;
        }

        public void ApplyParameters(IReadOnlyDictionary<string, object?> values)
        {
            foreach (var (key, raw) in values)
            {
                object? v = raw is Newtonsoft.Json.Linq.JValue jv ? jv.Value : raw;
                if (string.Equals(key, "TradeSymbol", StringComparison.OrdinalIgnoreCase))
                {
                    if (v is string s && !string.IsNullOrWhiteSpace(s)) tradeSymbol = s.Trim().ToUpperInvariant();
                    continue;
                }
                var input = bp.Inputs.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));
                if (input == null || v == null) continue;
                try
                {
                    double d = v is bool b ? (b ? 1 : 0) : Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    if (input.Min.HasValue) d = Math.Max(input.Min.Value, d);
                    if (input.Max.HasValue) d = Math.Min(input.Max.Value, d);
                    if (input.Type == "int") d = Math.Round(d);
                    inputs[input.Name] = d;
                }
                catch { }
            }
        }

        private IEnumerable<string> Tradeable => bp.Universe.Mode == "basket"
            ? bp.Universe.Symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0)
            : Array.Empty<string>();

        public override StrategySymbols DeclareSymbols() => bp.Universe.Mode switch
        {
            "basket" => StrategySymbols.Of(Tradeable.Append(bp.Universe.RegimeSymbol.ToUpperInvariant()).Distinct().ToArray()),
            "universe" => StrategySymbols.FromUniverse(new UniverseSpec
            {
                TopN = bp.Universe.TopN, QuoteAsset = bp.Universe.QuoteAsset, RegimeSymbol = bp.Universe.RegimeSymbol.ToUpperInvariant()
            }),
            _ => StrategySymbols.Of(tradeSymbol)
        };

        // ── signal computation ────────────────────────────────────────────────────

        public void Prime(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> series, TimeInterval primedInterval)
        {
            interval = primedInterval;
            primed.Clear();
            foreach (var (symbol, candles) in series)
                primed[symbol] = Compute(symbol, candles, series);
        }

        private Signals Compute(string symbol, IReadOnlyList<OHLCCandle> candles, IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>>? universe)
        {
            bool continuous = !MarketDataRouter.UsesEquityFeed(symbol, Venues.AssetClass.Unknown);
            var bars = BarSeries.From(candles, CandleClock.BarsPerYear(interval, continuous));
            var ctx = new FormulaContext
            {
                Bars = bars,
                Inputs = inputs,
                External = sym =>
                {
                    if (universe != null && universe.TryGetValue(sym, out var other)) return BarSeries.From(other, bars.BarsPerYear);
                    var fetched = ExternalBars?.Invoke(sym, interval, Math.Max(candles.Count + 5, 50));
                    return fetched is { Count: > 0 } ? BarSeries.From(fetched, bars.BarsPerYear) : null;
                }
            };
            var s = new Signals { Count = candles.Count };
            for (int i = 0; i < candles.Count; i++) s.Index[candles[i].Timestamp] = i;
            s.EntryLong = compiled.EntryLong?.Evaluate(ctx);
            s.ExitLong = compiled.ExitLong?.Evaluate(ctx);
            s.EntryShort = compiled.EntryShort?.Evaluate(ctx);
            s.ExitShort = compiled.ExitShort?.Evaluate(ctx);
            s.Filter = compiled.Filter?.Evaluate(ctx);
            s.Score = compiled.Score?.Evaluate(ctx);
            int atrLen = new[] { bp.Risk.StopLoss, bp.Risk.TakeProfit, bp.Risk.Trailing }.Where(l => l.Mode == "atr").Select(l => l.AtrLength).DefaultIfEmpty(14).First();
            s.Atr = TA.Atr(bars.High, bars.Low, bars.Close, Math.Max(1, atrLen));
            s.Vol = TA.HistoricalVolatility(bars.Close, 20, bars.BarsPerYear);
            s.DollarVol = TA.DollarVolume(bars.Close, bars.Volume, 20);
            return s;
        }

        private (Signals Signals, int Index)? SignalsAt(string symbol, IReadOnlyList<OHLCCandle> history,
            IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>>? universe)
        {
            if (history.Count == 0) return null;
            var t = history[^1].Timestamp;
            if (primed.TryGetValue(symbol, out var p) && p.Index.TryGetValue(t, out var idx)) return (p, idx);
            // Live/paper (or an unprimed symbol): compute over the history we have.
            var fresh = Compute(symbol, history, universe);
            return (fresh, history.Count - 1);
        }

        private static bool On(double[]? s, int i) => s != null && i >= 0 && i < s.Length && TA.IsNum(s[i]) && s[i] != 0;
        private static double At(double[]? s, int i) => s != null && i >= 0 && i < s.Length ? s[i] : double.NaN;

        // ── single symbol ─────────────────────────────────────────────────────────

        public override async Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            if (bp.Universe.Mode != "single") return;
            interval = Ctx.Host.Interval;
            var hit = SignalsAt(tradeSymbol, History, null);
            if (hit == null) return;
            var (sig, i) = hit.Value;
            await ManageSymbolAsync(tradeSymbol, candle, sig, i, PositionQty(tradeSymbol), filterOn: sig.Filter == null || On(sig.Filter, i),
                maxNewPositions: 1, ct);
        }

        private decimal PositionQty(string symbol)
        {
            if (bp.Universe.Mode == "single" && Position != null && string.Equals(Position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                return Position.Qty;
            return Positions.TryGetValue(symbol, out var q) ? q : 0m;
        }

        private bool HasPending(string symbol) => WorkingOrders.Any(o =>
            string.Equals(o.Request.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
            && o.Request.Type == OrderType.Market && o.Status == OrderStatus.Pending);

        /// <summary>One symbol's per-bar decision. Returns true when a new position was opened.</summary>
        private async Task<bool> ManageSymbolAsync(string symbol, OHLCCandle candle, Signals sig, int i, decimal qty, bool filterOn,
            int maxNewPositions, CancellationToken ct)
        {
            if (HasPending(symbol)) return false;
            if (!states.TryGetValue(symbol, out var st)) states[symbol] = st = new TradeState();
            double price = (double)candle.Close;
            if (price <= 0) return false;

            if (qty != 0)
            {
                bool isLong = qty > 0;
                if (st.EntryIndex < 0) { st.EntryIndex = i; st.Extreme = isLong ? (double)candle.High : (double)candle.Low; }
                st.Extreme = isLong ? Math.Max(st.Extreme, (double)candle.High) : Math.Min(st.Extreme, (double)candle.Low);
                int held = i - st.EntryIndex;

                bool exit = isLong ? On(sig.ExitLong, i) : On(sig.ExitShort, i);
                if (!exit && bp.Risk.MaxBarsInTrade > 0 && held >= bp.Risk.MaxBarsInTrade) exit = true;
                if (!exit && bp.Risk.ExitOnFilterOff && !filterOn) exit = true;
                if (!exit && bp.Risk.Trailing.Mode != "none")
                {
                    double dist = bp.Risk.Trailing.Mode == "atr" ? bp.Risk.Trailing.Value * At(sig.Atr, i) : st.Extreme * bp.Risk.Trailing.Value / 100;
                    if (TA.IsNum(dist) && dist > 0)
                        exit = isLong ? price <= st.Extreme - dist : price >= st.Extreme + dist;
                }
                if (exit)
                {
                    await SubmitOrder(new OrderRequest
                    {
                        IntentId = Guid.NewGuid().ToString("N"),
                        Side = isLong ? OrderSide.Sell : OrderSide.Buy,
                        Type = OrderType.Market,
                        Symbol = symbol,
                        Qty = Math.Abs(qty),
                    }, ct);
                    st.EntryIndex = -1;
                    st.CooldownUntil = i + Math.Max(0, bp.Risk.CooldownBars);
                }
                return false;
            }

            // Flat: the position closed (signal, bracket or liquidation) — reset trade state.
            if (st.EntryIndex >= 0) { st.EntryIndex = -1; st.CooldownUntil = i + Math.Max(0, bp.Risk.CooldownBars); }
            if (maxNewPositions <= 0 || i < st.CooldownUntil || !filterOn) return false;

            bool goLong = compiled.AllowsLong && On(sig.EntryLong, i);
            bool goShort = !goLong && compiled.AllowsShort && Leverage > 1m && On(sig.EntryShort, i);
            if (!goLong && !goShort) return false;

            double atr = At(sig.Atr, i);
            double stopDist = bp.Risk.StopLoss.Mode switch
            {
                "percent" => price * bp.Risk.StopLoss.Value / 100,
                "atr" => TA.IsNum(atr) ? atr * bp.Risk.StopLoss.Value : double.NaN,
                _ => double.NaN
            };
            double targetDist = bp.Risk.TakeProfit.Mode switch
            {
                "percent" => price * bp.Risk.TakeProfit.Value / 100,
                "atr" => TA.IsNum(atr) ? atr * bp.Risk.TakeProfit.Value : double.NaN,
                "r" => TA.IsNum(stopDist) ? stopDist * bp.Risk.TakeProfit.Value : double.NaN,
                _ => double.NaN
            };

            double notional = TargetNotional(price, stopDist, At(sig.Vol, i), Math.Max(1, MaxPositions()));
            if (!(notional > 0)) return false;
            decimal orderQty = Floor((decimal)(notional / price));
            if (orderQty <= 0) return false;

            decimal? sl = TA.IsNum(stopDist) && stopDist > 0 ? Round(goLong ? price - stopDist : price + stopDist) : null;
            decimal? tp = TA.IsNum(targetDist) && targetDist > 0 ? Round(goLong ? price + targetDist : price - targetDist) : null;
            if (sl is <= 0) sl = null;
            if (tp is <= 0) tp = null;

            var intent = await SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = goLong ? OrderSide.Buy : OrderSide.Sell,
                Type = OrderType.Market,
                Symbol = symbol,
                Qty = orderQty,
                StopLossPrice = sl,
                TakeProfitPrice = tp,
            }, ct);
            if (intent.Status is OrderStatus.Rejected) return false;
            st.EntryIndex = i;
            st.Extreme = goLong ? (double)candle.High : (double)candle.Low;
            return true;
        }

        private int MaxPositions() => bp.Universe.Mode == "single" ? 1 : Math.Max(1, bp.Sizing.MaxPositions);

        /// <summary>Notional for a new position under the blueprint's sizing, capped by leverage and the per-position limit.</summary>
        private double TargetNotional(double price, double stopDist, double volPct, int slots)
        {
            double equity = (double)Equity;
            if (equity <= 0) return 0;
            double lev = (double)Math.Max(1m, Leverage);
            double cap = equity * lev * Math.Min(1, bp.Sizing.MaxPositionPct / 100) * 0.995;
            if (bp.Universe.Mode != "single") cap = Math.Min(cap, equity * lev / slots * 0.995);
            double notional = bp.Sizing.Mode switch
            {
                "quote" => bp.Sizing.Value,
                "risk" => TA.IsNum(stopDist) && stopDist > 0 ? equity * bp.Sizing.Value / 100 / stopDist * price : 0,
                "voltarget" => TA.IsNum(volPct) && volPct > 0 ? equity * bp.Sizing.Value / volPct : 0,
                _ => equity * bp.Sizing.Value / 100
            };
            // Spot: never commit more cash than is free.
            if (Leverage <= 1m) cap = Math.Min(cap, (double)QuoteBalance * 0.995);
            return Math.Max(0, Math.Min(notional, cap));
        }

        private static decimal Floor(decimal qty) => Math.Floor(qty * 100_000_000m) / 100_000_000m;
        private static decimal Round(double price) => Math.Round((decimal)price, price >= 100 ? 2 : price >= 1 ? 4 : 8);

        // ── basket / universe ─────────────────────────────────────────────────────

        public override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
        {
            if (bp.Universe.Mode == "single") return;
            interval = Ctx.Host.Interval;
            universeBars++;
            var histories = bar.Histories;
            string regime = bp.Universe.RegimeSymbol.ToUpperInvariant();

            bool filterOn = true;
            if (compiled.Filter != null)
            {
                filterOn = false;
                if (histories.TryGetValue(regime, out var rh) && SignalsAt(regime, rh, histories) is { } rs)
                    filterOn = On(rs.Signals.Filter, rs.Index);
            }

            var symbols = bp.Universe.Mode == "basket"
                ? Tradeable.Where(histories.ContainsKey).ToList()
                : histories.Keys.ToList();

            if (bp.Ranking != null)
            {
                await RebalanceAsync(bar, symbols, filterOn, ct);
                return;
            }

            // Independent signals per symbol, capped at MaxPositions concurrent positions.
            int open = Positions.Count(p => p.Value != 0);
            int slots = Math.Max(0, MaxPositions() - open);
            var ordered = symbols.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var symbol in ordered)
            {
                var h = histories[symbol];
                if (h.Count == 0 || SignalsAt(symbol, h, histories) is not { } hit) continue;
                decimal qty = PositionQty(symbol);
                if (qty == 0 && slots <= 0) continue;
                bool opened = await ManageSymbolAsync(symbol, h[^1], hit.Signals, hit.Index, qty, filterOn, slots, ct);
                if (opened) slots--;
            }
        }

        private async Task RebalanceAsync(PortfolioBar bar, List<string> symbols, bool filterOn, CancellationToken ct)
        {
            var ranking = bp.Ranking!;
            if ((universeBars - 1) % Math.Max(1, ranking.RebalanceBars) != 0) return;
            if (WorkingOrders.Any(o => o.Request.Type == OrderType.Market && o.Status == OrderStatus.Pending)) return;

            var scored = new List<(string Symbol, double Score, double Vol, double Price)>();
            foreach (var symbol in symbols)
            {
                var h = bar.Histories[symbol];
                if (h.Count < Math.Max(2, compiled.Lookback / 2) || SignalsAt(symbol, h, bar.Histories) is not { } hit) continue;
                int i = hit.Index;
                double score = At(hit.Signals.Score, i);
                if (!TA.IsNum(score)) continue;
                if (compiled.EntryLong != null && !On(hit.Signals.EntryLong, i) && compiled.EntryShort == null) continue;
                if (ranking.MinDollarVolume > 0 && !(At(hit.Signals.DollarVol, i) >= ranking.MinDollarVolume)) continue;
                double price = (double)h[^1].Close;
                if (price <= 0) continue;
                scored.Add((symbol, score, At(hit.Signals.Vol, i), price));
            }

            var targets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (filterOn && scored.Count > 0)
            {
                var desc = scored.OrderByDescending(s => s.Score).ToList();
                var longs = compiled.AllowsLong ? desc.Take(ranking.Top).ToList() : new();
                var shorts = compiled.AllowsShort && Leverage > 1m && ranking.Bottom > 0
                    ? desc.AsEnumerable().Reverse().Where(s => !longs.Contains(s)).Take(ranking.Bottom).ToList() : new();
                var picks = longs.Select(s => (s, 1)).Concat(shorts.Select(s => (s, -1))).ToList();
                if (picks.Count > 0)
                {
                    double gross = Math.Min(ranking.GrossExposure, (double)Math.Max(1m, Leverage));
                    var raw = picks.Select(p => (p.s.Symbol, Sign: p.Item2, W: ranking.Weighting switch
                    {
                        "inverse-vol" => TA.IsNum(p.s.Vol) && p.s.Vol > 0 ? 1 / p.s.Vol : 0,
                        "score" => Math.Max(0, Math.Abs(p.s.Score)),
                        _ => 1.0
                    })).ToList();
                    double sum = raw.Sum(r => r.W);
                    if (sum <= 0) raw = raw.Select(r => (r.Symbol, r.Sign, W: 1.0)).ToList();
                    sum = raw.Sum(r => r.W);
                    foreach (var r in raw) targets[r.Symbol] = r.Sign * gross * r.W / sum;
                }
            }

            double equity = (double)Equity;
            if (equity <= 0) return;
            var current = Positions;
            var orders = new List<OrderRequest>();
            foreach (var symbol in current.Keys.Union(targets.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                double price = (double)bar.Mark(symbol);
                if (price <= 0) continue;
                double targetQty = targets.TryGetValue(symbol, out var w) ? equity * w / price : 0;
                double currentQty = current.TryGetValue(symbol, out var q) ? (double)q : 0;
                double delta = targetQty - currentQty;
                // Skip churn: changes under 10% of the target position (or tiny absolute amounts) are ignored.
                if (Math.Abs(delta) * price < Math.Max(equity * 0.002, Math.Abs(targetQty) * price * 0.10) && targetQty != 0) continue;
                if (Math.Abs(delta) * price < 1e-6) continue;
                orders.Add(new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                    Type = OrderType.Market,
                    Symbol = symbol,
                    Qty = Floor((decimal)Math.Abs(targetQty == 0 ? currentQty : delta)),
                });
            }
            // Reductions first so the cash they free funds the additions.
            foreach (var order in orders.OrderBy(o => IsReduction(o, current) ? 0 : 1))
                if (order.Qty > 0) await SubmitOrder(order, ct);
        }

        private static bool IsReduction(OrderRequest o, IReadOnlyDictionary<string, decimal> current)
        {
            decimal q = current.TryGetValue(o.Symbol, out var v) ? v : 0;
            return (q > 0 && o.Side == OrderSide.Sell) || (q < 0 && o.Side == OrderSide.Buy);
        }
    }
}
