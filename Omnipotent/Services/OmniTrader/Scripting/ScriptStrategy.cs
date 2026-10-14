using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;
using System.Diagnostics;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Scripting
{
    /// <summary>Thrown when a script exceeds its per-callback work or time budget, or recurses too deep.</summary>
    public sealed class ScriptBudgetException : Exception
    {
        public ScriptBudgetException(string message) : base(message) { }
    }

    /// <summary>
    /// Work and depth limits for compiled scripts. The script compiler injects <see cref="Tick"/> into
    /// every loop body and <see cref="Enter"/> into every synchronous method body, so an infinite loop
    /// fails the callback and deep recursion fails before it can overflow the stack.
    /// </summary>
    public static class ScriptGuard
    {
        [ThreadStatic] private static long ticks;
        [ThreadStatic] private static long budget;
        [ThreadStatic] private static long deadline;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static bool active;

        public const long DefaultTicks = 20_000_000;
        public static readonly TimeSpan DefaultTime = TimeSpan.FromSeconds(5);
        public const int MaxDepth = 200;

        public static void Begin(long maxTicks, TimeSpan maxTime)
        {
            ticks = 0;
            budget = maxTicks;
            deadline = Stopwatch.GetTimestamp() + (long)(maxTime.TotalSeconds * Stopwatch.Frequency);
            depth = 0;
            active = true;
        }

        public static void End() => active = false;

        public static void Tick()
        {
            if (!active) Begin(DefaultTicks * 5, TimeSpan.FromSeconds(30));
            if (++ticks > budget) throw new ScriptBudgetException($"Script exceeded {budget:N0} loop iterations in one callback (infinite loop?).");
            if ((ticks & 4095) == 0 && Stopwatch.GetTimestamp() > deadline)
                throw new ScriptBudgetException("Script exceeded its time budget in one callback.");
        }

        public static DepthScope Enter()
        {
            if (++depth > MaxDepth)
            {
                depth = 0;
                throw new ScriptBudgetException($"Script recursion deeper than {MaxDepth} calls.");
            }
            return new DepthScope();
        }

        public readonly struct DepthScope : IDisposable
        {
            public void Dispose() { if (depth > 0) depth--; }
        }

        /// <summary>Run a script callback under a fresh budget.</summary>
        public static T Run<T>(Func<T> f, long maxTicks = DefaultTicks, TimeSpan? maxTime = null)
        {
            Begin(maxTicks, maxTime ?? DefaultTime);
            try { return f(); }
            finally { End(); }
        }

        public static void Run(Action f, long maxTicks = DefaultTicks, TimeSpan? maxTime = null)
        {
            Begin(maxTicks, maxTime ?? DefaultTime);
            try { f(); }
            finally { End(); }
        }
    }

    /// <summary>Per-bar values recorded by <c>Plot()</c>, returned with the backtest for charting.</summary>
    public interface IReportsPlots
    {
        IReadOnlyDictionary<string, List<(DateTime T, double V)>> Plots { get; }
    }

    /// <summary>
    /// Base class for single-symbol script strategies. Override <see cref="OnBar"/>; read prices with
    /// <see cref="Close"/>/<see cref="C"/>, indicators with the typed helpers or <see cref="F"/> (any
    /// formula), and place orders with <see cref="Buy"/>, <see cref="BuyPercent"/>, <see cref="ExitLong"/>…
    /// Orders placed during OnBar are submitted when it returns. Everything is causal: helpers read
    /// the current bar or earlier, never later.
    /// </summary>
    public abstract class ScriptStrategy : TradingStrategy, IPrimedStrategy, IReportsPlots
    {
        [Param("Symbol", Group = "Universe", IsSymbol = true)]
        public string TradeSymbol { get; set; } = "BTCUSDT";

        public override StrategySymbols DeclareSymbols() => StrategySymbols.Of(TradeSymbol);

        /// <summary>Called once before the first bar.</summary>
        public virtual void Initialize() { }
        /// <summary>Called on every closed bar.</summary>
        public abstract void OnBar();
        /// <summary>Called when an order fills.</summary>
        public virtual void OnFill(FillEvent fill) { }

        // ── state ─────────────────────────────────────────────────────────────────

        private BarSeries? primedBars;
        private Dictionary<DateTime, int>? primedIndex;
        private BarSeries? bars;
        private int i;
        private readonly Dictionary<string, double[]> cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledFormula> formulas = new(StringComparer.Ordinal);
        private readonly List<OrderRequest> pending = new();
        private readonly Dictionary<string, List<(DateTime, double)>> plots = new(StringComparer.Ordinal);
        private int entryBar = -1;
        private double? nextStop, nextTarget;
        private TimeInterval interval = TimeInterval.OneDay;

        public IReadOnlyDictionary<string, List<(DateTime T, double V)>> Plots => plots;

        public void Prime(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> series, TimeInterval primedInterval)
        {
            interval = primedInterval;
            if (!series.TryGetValue(TradeSymbol, out var candles)) return;
            primedBars = BarSeries.From(candles, CandleClock.BarsPerYear(interval, !MarketDataRouter.UsesEquityFeed(TradeSymbol, Venues.AssetClass.Unknown)));
            primedIndex = new Dictionary<DateTime, int>(candles.Count);
            for (int k = 0; k < candles.Count; k++) primedIndex[candles[k].Timestamp] = k;
            cache.Clear();
        }

        public sealed override Task OnStart(CancellationToken ct)
        {
            ScriptGuard.Run(Initialize);
            return Task.CompletedTask;
        }

        public sealed override async Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            interval = Ctx.Host.Interval;
            if (primedBars != null && primedIndex != null && primedIndex.TryGetValue(candle.Timestamp, out var idx))
            {
                if (!ReferenceEquals(bars, primedBars)) { bars = primedBars; cache.Clear(); }
                i = idx;
            }
            else
            {
                bars = BarSeries.From(History, CandleClock.BarsPerYear(interval, !MarketDataRouter.UsesEquityFeed(TradeSymbol, Venues.AssetClass.Unknown)));
                cache.Clear();
                i = bars.Count - 1;
            }
            if (IsFlat) entryBar = -1;
            else if (entryBar < 0) entryBar = i;

            pending.Clear();
            ScriptGuard.Run(OnBar);
            foreach (var order in pending.ToList())
            {
                var intent = await SubmitOrder(order, ct);
                if (intent.Status != OrderStatus.Rejected && order.Type == OrderType.Market && IsEntry(order)) entryBar = i;
            }
            pending.Clear();
        }

        public sealed override Task OnOrderFilled(FillEvent fill, CancellationToken ct)
        {
            ScriptGuard.Run(() => OnFill(fill));
            return Task.CompletedTask;
        }

        private bool IsEntry(OrderRequest o) => (o.Side == OrderSide.Buy && PositionSize <= 0) || (o.Side == OrderSide.Sell && PositionSize >= 0);

        // ── prices ────────────────────────────────────────────────────────────────

        private BarSeries B => bars ?? throw new InvalidOperationException("No bars yet.");
        private double Ago(double[] s, int ago) => ago < 0 ? throw new ArgumentException("ago must be ≥ 0") : i - ago >= 0 ? s[i - ago] : double.NaN;

        protected int CurrentBar => i;
        protected int BarCount => i + 1;
        protected DateTime Time => B.Time[i];
        protected double Open => B.Open[i];
        protected double High => B.High[i];
        protected double Low => B.Low[i];
        protected double Close => B.Close[i];
        protected double Volume => B.Volume[i];
        protected double O(int ago) => Ago(B.Open, ago);
        protected double H(int ago) => Ago(B.High, ago);
        protected double L(int ago) => Ago(B.Low, ago);
        protected double C(int ago) => Ago(B.Close, ago);
        protected double V(int ago) => Ago(B.Volume, ago);

        // ── indicators ────────────────────────────────────────────────────────────

        /// <summary>Any formula in the indicator language, evaluated at the current bar (or <paramref name="ago"/> bars back).</summary>
        protected double F(string formula, int ago = 0)
        {
            if (!cache.TryGetValue(formula, out var series))
            {
                if (!formulas.TryGetValue(formula, out var compiled))
                    formulas[formula] = compiled = FormulaCompiler.Compile(formula, FormulaScope.With());
                series = compiled.Evaluate(new FormulaContext { Bars = B });
                cache[formula] = series;
            }
            return Ago(series, ago);
        }

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        protected double Sma(int length, int ago = 0) => F($"sma(close, {length})", ago);
        protected double Ema(int length, int ago = 0) => F($"ema(close, {length})", ago);
        protected double Wma(int length, int ago = 0) => F($"wma(close, {length})", ago);
        protected double Hma(int length, int ago = 0) => F($"hma(close, {length})", ago);
        protected double Rsi(int length = 14, int ago = 0) => F($"rsi(close, {length})", ago);
        protected double Atr(int length = 14, int ago = 0) => F($"atr({length})", ago);
        protected double Natr(int length = 14, int ago = 0) => F($"natr({length})", ago);
        protected double Adx(int length = 14, int ago = 0) => F($"adx({length})", ago);
        protected double Cci(int length = 20, int ago = 0) => F($"cci(hlc3, {length})", ago);
        protected double Mfi(int length = 14, int ago = 0) => F($"mfi({length})", ago);
        protected double WilliamsR(int length = 14, int ago = 0) => F($"willr({length})", ago);
        protected double Roc(int length, int ago = 0) => F($"roc(close, {length})", ago);
        protected double StdDev(int length, int ago = 0) => F($"stdev(close, {length})", ago);
        protected double ZScore(int length, int ago = 0) => F($"zscore(close, {length})", ago);
        protected double PercentRank(int length, int ago = 0) => F($"percentrank(close, {length})", ago);
        protected double Highest(int length, int ago = 0) => F($"highest(high, {length})", ago);
        protected double Lowest(int length, int ago = 0) => F($"lowest(low, {length})", ago);
        protected double Vwap(int ago = 0) => F("vwap()", ago);
        protected double Obv(int ago = 0) => F("obv()", ago);
        protected double HistVol(int length = 20, int ago = 0) => F($"hv({length})", ago);
        protected double Ibs(int ago = 0) => F("ibs()", ago);

        protected (double Line, double Signal, double Hist) Macd(int fast = 12, int slow = 26, int signal = 9, int ago = 0)
            => (F($"macd(close, {fast}, {slow}, {signal})", ago), F($"macd(close, {fast}, {slow}, {signal}).signal", ago), F($"macd(close, {fast}, {slow}, {signal}).hist", ago));

        protected (double Upper, double Mid, double Lower) Bollinger(int length = 20, double mult = 2, int ago = 0)
            => (F($"bb(close, {length}, {N(mult)}).upper", ago), F($"bb(close, {length}, {N(mult)})", ago), F($"bb(close, {length}, {N(mult)}).lower", ago));

        protected (double Upper, double Mid, double Lower) Keltner(int length = 20, double mult = 2, int ago = 0)
            => (F($"kc({length}, {N(mult)}).upper", ago), F($"kc({length}, {N(mult)})", ago), F($"kc({length}, {N(mult)}).lower", ago));

        protected (double Upper, double Lower) Donchian(int length = 20, int ago = 0)
            => (F($"dc({length})", ago), F($"dc({length}).lower", ago));

        protected (double K, double D) Stoch(int length = 14, int smoothK = 3, int d = 3, int ago = 0)
            => (F($"stoch({length}, {smoothK}, {d})", ago), F($"stoch({length}, {smoothK}, {d}).d", ago));

        protected (double Line, int Direction) Supertrend(int length = 10, double mult = 3, int ago = 0)
            => (F($"supertrend({length}, {N(mult)})", ago), (int)F($"supertrend({length}, {N(mult)}).dir", ago));

        protected bool CrossAbove(string a, string b) => F($"crossover({a}, {b})") == 1;
        protected bool CrossBelow(string a, string b) => F($"crossunder({a}, {b})") == 1;

        // ── position ──────────────────────────────────────────────────────────────

        protected double PositionSize => (double)(Position?.Qty ?? 0m);
        protected bool IsLong => PositionSize > 0;
        protected bool IsShort => PositionSize < 0;
        protected bool IsFlat => PositionSize == 0;
        protected double EntryPrice => (double)(Position?.AveragePrice ?? 0m);
        protected int BarsInTrade => IsFlat || entryBar < 0 ? 0 : i - entryBar;
        protected double OpenProfitPct => IsFlat || EntryPrice <= 0 ? 0 : (Close / EntryPrice - 1) * 100 * Math.Sign(PositionSize);
        protected double Cash => (double)QuoteBalance;
        protected double AccountEquity => (double)Equity;
        protected double MaxLeverage => (double)Leverage;
        protected bool HasPendingOrders => WorkingOrders.Any(o => o.Status == OrderStatus.Pending);

        // ── orders ────────────────────────────────────────────────────────────────

        /// <summary>Attach a protective stop to the next entry (absolute price).</summary>
        protected void SetStopLoss(double price) => nextStop = price;
        /// <summary>Attach a take-profit to the next entry (absolute price).</summary>
        protected void SetTakeProfit(double price) => nextTarget = price;

        private void Queue(OrderSide side, double qty, OrderType type = OrderType.Market, double? limit = null, double? stop = null,
            double? stopLoss = null, double? takeProfit = null)
        {
            if (!(qty > 0) || !double.IsFinite(qty)) return;
            decimal q = Math.Floor((decimal)qty * 100_000_000m) / 100_000_000m;
            if (q <= 0) return;
            stopLoss ??= nextStop;
            takeProfit ??= nextTarget;
            nextStop = nextTarget = null;
            pending.Add(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side = side,
                Type = type,
                Symbol = TradeSymbol,
                Qty = q,
                LimitPrice = limit.HasValue ? (decimal)limit.Value : null,
                StopPrice = stop.HasValue ? (decimal)stop.Value : null,
                StopLossPrice = stopLoss is > 0 ? (decimal)stopLoss.Value : null,
                TakeProfitPrice = takeProfit is > 0 ? (decimal)takeProfit.Value : null,
            });
        }

        protected void Buy(double qty, double? stopLoss = null, double? takeProfit = null) => Queue(OrderSide.Buy, qty, stopLoss: stopLoss, takeProfit: takeProfit);
        protected void Sell(double qty, double? stopLoss = null, double? takeProfit = null) => Queue(OrderSide.Sell, qty, stopLoss: stopLoss, takeProfit: takeProfit);
        protected void LimitBuy(double qty, double price) => Queue(OrderSide.Buy, qty, OrderType.Limit, limit: price);
        protected void LimitSell(double qty, double price) => Queue(OrderSide.Sell, qty, OrderType.Limit, limit: price);
        protected void StopBuy(double qty, double stop) => Queue(OrderSide.Buy, qty, OrderType.StopLoss, stop: stop);
        protected void StopSell(double qty, double stop) => Queue(OrderSide.Sell, qty, OrderType.StopLoss, stop: stop);

        /// <summary>Go long with <paramref name="pct"/>% of equity (capped by cash on spot, by leverage on margin).</summary>
        protected void BuyPercent(double pct, double? stopLoss = null, double? takeProfit = null)
        {
            if (IsLong || HasPendingOrders) return;
            if (IsShort) Buy(Math.Abs(PositionSize));
            double notional = Math.Min(AccountEquity * pct / 100, MaxNotional());
            Buy(notional / Close, stopLoss, takeProfit);
        }

        /// <summary>Go short with <paramref name="pct"/>% of equity. Ignored on spot (leverage 1).</summary>
        protected void ShortPercent(double pct, double? stopLoss = null, double? takeProfit = null)
        {
            if (IsShort || HasPendingOrders || MaxLeverage <= 1) return;
            if (IsLong) Sell(PositionSize);
            double notional = Math.Min(AccountEquity * pct / 100, MaxNotional());
            Sell(notional / Close, stopLoss, takeProfit);
        }

        /// <summary>Move to a signed target position, as % of equity (−100 = fully short, 100 = fully long).</summary>
        protected void SetTargetPercent(double pct)
        {
            if (HasPendingOrders || Close <= 0) return;
            if (pct < 0 && MaxLeverage <= 1) pct = 0;
            double targetQty = Math.Sign(pct) * Math.Min(AccountEquity * Math.Abs(pct) / 100, MaxNotional()) / Close;
            double delta = targetQty - PositionSize;
            if (Math.Abs(delta) * Close < AccountEquity * 0.001) return;
            if (delta > 0) Buy(delta); else Sell(-delta);
        }

        protected void ExitLong() { if (IsLong && !HasPendingOrders) Sell(PositionSize); }
        protected void ExitShort() { if (IsShort && !HasPendingOrders) Buy(-PositionSize); }
        protected void Exit() { ExitLong(); ExitShort(); }

        private double MaxNotional()
        {
            double lev = Math.Max(1, MaxLeverage);
            double cap = AccountEquity * lev * 0.995;
            return lev <= 1 ? Math.Min(cap, Cash * 0.995 + Math.Max(0, PositionSize) * Close) : cap;
        }

        // ── diagnostics ───────────────────────────────────────────────────────────

        /// <summary>Record a value on this bar; shown under the backtest chart.</summary>
        protected void Plot(string name, double value)
        {
            if (!plots.TryGetValue(name, out var list)) plots[name] = list = new List<(DateTime, double)>();
            if (double.IsFinite(value)) list.Add((Time, value));
        }

        protected void Print(string message) => Log(message);
    }

    /// <summary>A read-only view of the universe at the current bar for <see cref="ScriptUniverseStrategy"/>.</summary>
    public sealed class UniverseView
    {
        private readonly ScriptUniverseStrategy owner;
        internal UniverseView(ScriptUniverseStrategy owner, DateTime time, IReadOnlyList<string> symbols)
        {
            this.owner = owner;
            Time = time;
            Symbols = symbols;
        }

        public DateTime Time { get; }
        public IReadOnlyList<string> Symbols { get; }
        public double Close(string symbol, int ago = 0) => owner.ValueAt(symbol, "close", ago);
        public double Volume(string symbol, int ago = 0) => owner.ValueAt(symbol, "volume", ago);
        /// <summary>Any formula evaluated on one symbol's series.</summary>
        public double F(string symbol, string formula, int ago = 0) => owner.ValueAt(symbol, formula, ago);
        public int Bars(string symbol) => owner.BarsFor(symbol);
    }

    /// <summary>
    /// Base class for multi-asset script strategies. Override <see cref="OnBar(UniverseView)"/> and
    /// call <see cref="SetWeights"/> with target weights (signed fractions of equity); the base class
    /// trades the differences, reductions first.
    /// </summary>
    public abstract class ScriptUniverseStrategy : TradingStrategy, IPrimedStrategy, IReportsPlots
    {
        [Param("Universe size", Group = "Universe", Min = 2, Max = 300, Step = 1)]
        public int UniverseSize { get; set; } = 30;

        [Param("Regime / benchmark", Group = "Universe", IsSymbol = true)]
        public string RegimeSymbol { get; set; } = "BTCUSDT";

        /// <summary>Fixed basket; null for a dynamic top-N-by-volume crypto universe.</summary>
        protected virtual string[]? Symbols => null;

        public override StrategySymbols DeclareSymbols() => Symbols is { Length: > 0 } fixedSymbols
            ? StrategySymbols.Of(fixedSymbols.Append(RegimeSymbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            : StrategySymbols.FromUniverse(new UniverseSpec { TopN = UniverseSize, RegimeSymbol = RegimeSymbol });

        public virtual void Initialize() { }
        public abstract void OnBar(UniverseView universe);

        private readonly Dictionary<string, (BarSeries Bars, Dictionary<DateTime, int> Index)> primed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (BarSeries Bars, int Index)> current = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double[]> cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledFormula> formulas = new(StringComparer.Ordinal);
        private Dictionary<string, double>? targets;
        private readonly Dictionary<string, List<(DateTime, double)>> plots = new(StringComparer.Ordinal);
        private DateTime now;

        public IReadOnlyDictionary<string, List<(DateTime T, double V)>> Plots => plots;
        protected int BarNumber { get; private set; }

        public void Prime(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> series, TimeInterval interval)
        {
            primed.Clear();
            foreach (var (symbol, candles) in series)
            {
                var idx = new Dictionary<DateTime, int>(candles.Count);
                for (int k = 0; k < candles.Count; k++) idx[candles[k].Timestamp] = k;
                primed[symbol] = (BarSeries.From(candles, CandleClock.BarsPerYear(interval, !MarketDataRouter.UsesEquityFeed(symbol, Venues.AssetClass.Unknown))), idx);
            }
            cache.Clear();
        }

        public sealed override Task OnStart(CancellationToken ct)
        {
            ScriptGuard.Run(Initialize);
            return Task.CompletedTask;
        }

        public sealed override async Task OnUniverseBar(PortfolioBar bar, CancellationToken ct)
        {
            now = bar.T;
            BarNumber++;
            current.Clear();
            foreach (var (symbol, history) in bar.Histories)
            {
                if (history.Count == 0) continue;
                if (primed.TryGetValue(symbol, out var p) && p.Index.TryGetValue(history[^1].Timestamp, out var idx))
                    current[symbol] = (p.Bars, idx);
                else
                {
                    var live = BarSeries.From(history);
                    current[symbol] = (live, live.Count - 1);
                    cache.Remove(symbol + "\u0001");
                    foreach (var key in cache.Keys.Where(k => k.StartsWith(symbol + "\u0001", StringComparison.Ordinal)).ToList()) cache.Remove(key);
                }
            }
            targets = null;
            var view = new UniverseView(this, bar.T, current.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList());
            ScriptGuard.Run(() => OnBar(view));
            if (targets != null) await RebalanceAsync(bar, targets, ct);
        }

        internal int BarsFor(string symbol) => current.TryGetValue(symbol, out var c) ? c.Index + 1 : 0;

        internal double ValueAt(string symbol, string formula, int ago)
        {
            if (ago < 0) throw new ArgumentException("ago must be ≥ 0");
            if (!current.TryGetValue(symbol, out var c)) return double.NaN;
            string key = symbol + "\u0001" + formula;
            if (!cache.TryGetValue(key, out var series))
            {
                if (c.Bars.Source(formula) is { } field) series = field;
                else
                {
                    if (!formulas.TryGetValue(formula, out var compiled))
                        formulas[formula] = compiled = FormulaCompiler.Compile(formula, FormulaScope.With());
                    series = compiled.Evaluate(new FormulaContext { Bars = c.Bars });
                }
                cache[key] = series;
            }
            int k = c.Index - ago;
            return k >= 0 && k < series.Length ? series[k] : double.NaN;
        }

        /// <summary>Target weights (signed fractions of equity). Symbols left out are closed.</summary>
        protected void SetWeights(IDictionary<string, double> weights)
            => targets = weights.Where(w => double.IsFinite(w.Value)).ToDictionary(w => w.Key.ToUpperInvariant(), w => w.Value, StringComparer.OrdinalIgnoreCase);

        protected IReadOnlyDictionary<string, decimal> Holdings => Positions;
        protected double AccountEquity => (double)Equity;
        protected double MaxLeverage => (double)Leverage;

        protected void Plot(string name, double value)
        {
            if (!plots.TryGetValue(name, out var list)) plots[name] = list = new List<(DateTime, double)>();
            if (double.IsFinite(value)) list.Add((now, value));
        }

        private async Task RebalanceAsync(PortfolioBar bar, Dictionary<string, double> weights, CancellationToken ct)
        {
            if (WorkingOrders.Any(o => o.Status == OrderStatus.Pending)) return;
            double equity = (double)Equity;
            if (equity <= 0) return;
            double gross = weights.Values.Sum(Math.Abs);
            double lev = Math.Max(1, (double)Leverage);
            double scale = gross > lev ? lev / gross : 1;
            var orders = new List<(OrderRequest Order, bool Reduce)>();
            var held = Positions;
            foreach (var symbol in held.Keys.Union(weights.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                double price = (double)bar.Mark(symbol);
                if (price <= 0) continue;
                double w = weights.TryGetValue(symbol, out var x) ? x * scale : 0;
                if (w < 0 && lev <= 1) w = 0;
                double target = equity * w * 0.995 / price;
                double have = held.TryGetValue(symbol, out var q) ? (double)q : 0;
                double delta = target - have;
                if (Math.Abs(delta) * price < Math.Max(equity * 0.002, Math.Abs(target) * price * 0.05) && target != 0) continue;
                if (Math.Abs(delta) * price < 1e-6) continue;
                decimal qty = Math.Floor((decimal)Math.Abs(target == 0 ? have : delta) * 100_000_000m) / 100_000_000m;
                if (qty <= 0) continue;
                bool reduce = (have > 0 && delta < 0) || (have < 0 && delta > 0);
                orders.Add((new OrderRequest
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Side = delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                    Type = OrderType.Market,
                    Symbol = symbol,
                    Qty = qty
                }, reduce));
            }
            foreach (var (order, _) in orders.OrderBy(o => o.Reduce ? 0 : 1))
                await SubmitOrder(order, ct);
        }
    }
}
