using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    public abstract class TradingStrategy
    {
        protected StrategyContext Ctx { get; private set; } = null!;

        public void Attach(StrategyContext context)
        {
            Ctx = context;
        }

        /// <summary>
        /// What this strategy trades. Single-symbol strategies return one fixed pair (typically from a
        /// [Param] symbol property); cross-sectional strategies return a <see cref="UniverseSpec"/>.
        /// Called after parameters are applied, before the session/backtest subscribes to market data.
        /// </summary>
        public virtual StrategySymbols DeclareSymbols() => StrategySymbols.Of("BTCUSDT");

        public virtual Task OnStart(CancellationToken ct) => Task.CompletedTask;
        public virtual Task OnCandleClose(OHLCCandle candle, CancellationToken ct) => Task.CompletedTask;
        public virtual Task OnOrderFilled(FillEvent fill, CancellationToken ct) => Task.CompletedTask;
        public virtual Task OnStop(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Portfolio (cross-sectional) hook. Called once per synchronized universe bar with point-in-time
        /// histories for every tradable symbol. Single-symbol strategies ignore this; multi-asset
        /// strategies (e.g. cross-sectional momentum) override it instead of <see cref="OnCandleClose"/>.
        /// Default is a no-op so existing strategies are unaffected.
        /// </summary>
        public virtual Task OnUniverseBar(PortfolioBar bar, CancellationToken ct) => Task.CompletedTask;

        protected Task<OrderIntent> SubmitOrder(OrderRequest request, CancellationToken ct = default)
            => Ctx.Host.SubmitOrderAsync(request, ct);

        protected Task CancelOrder(string intentId, CancellationToken ct = default)
            => Ctx.Host.CancelOrderAsync(intentId, ct);

        protected IReadOnlyList<OHLCCandle> History => Ctx.CandleHistory;
        protected Position? Position => Ctx.Host.CurrentPosition;
        protected decimal QuoteBalance => Ctx.Host.QuoteBalance;
        protected decimal BaseBalance => Ctx.Host.BaseBalance;
        protected string Symbol => Ctx.Host.Symbol;
        /// <summary>Account leverage (1 = spot). Read this to size into available margin.</summary>
        protected decimal Leverage => Ctx.Host.Leverage;

        // ── Portfolio (cross-sectional) helpers ──────────────────────────────────
        /// <summary>Net signed quantity held per symbol across the whole book (flat symbols omitted).</summary>
        protected IReadOnlyDictionary<string, decimal> Positions => Ctx.Host.PortfolioPositions;
        /// <summary>Total account equity (cash + marked value of every position).</summary>
        protected decimal Equity => Ctx.Host.Equity;

        /// <summary>Orders accepted but not yet filled (next-open market orders, working stops/limits).</summary>
        protected IReadOnlyList<OrderIntent> WorkingOrders => Ctx.Host.WorkingOrders;

        protected void Log(string msg) => Ctx.Host.Log(msg);
        protected void LogError(string msg, Exception? ex = null) => Ctx.Host.LogError(msg, ex);
    }

    /// <summary>
    /// Optional fast path for strategies whose signals are vectorised. In a backtest the engine hands
    /// the strategy every series in full before the first bar, so indicators are computed once rather
    /// than per bar. This is only sound because every indicator is causal — value[i] depends on bars
    /// ≤ i — and the strategy must still only read index ≤ the current bar. Live and paper sessions
    /// never prime; the strategy computes from its growing history instead.
    /// </summary>
    public interface IPrimedStrategy
    {
        void Prime(IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> series, TimeInterval interval);
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TradingStrategyAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// True for cross-sectional / multi-asset strategies that run only on the portfolio backtest
        /// path (they implement <see cref="TradingStrategy.OnUniverseBar"/>, not <see cref="TradingStrategy.OnCandleClose"/>).
        /// Such strategies cannot run as a single-symbol backtest or paper/live deployment.
        /// </summary>
        public bool RequiresUniverse { get; init; }

        public TradingStrategyAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
