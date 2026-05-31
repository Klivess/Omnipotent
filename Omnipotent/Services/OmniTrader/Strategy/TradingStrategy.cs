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

        protected void Log(string msg) => Ctx.Host.Log(msg);
        protected void LogError(string msg, Exception? ex = null) => Ctx.Host.LogError(msg, ex);
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
