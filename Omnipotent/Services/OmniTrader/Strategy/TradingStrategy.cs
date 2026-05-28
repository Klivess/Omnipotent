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

        protected Task<OrderIntent> SubmitOrder(OrderRequest request, CancellationToken ct = default)
            => Ctx.Host.SubmitOrderAsync(request, ct);

        protected Task CancelOrder(string intentId, CancellationToken ct = default)
            => Ctx.Host.CancelOrderAsync(intentId, ct);

        protected IReadOnlyList<OHLCCandle> History => Ctx.CandleHistory;
        protected Position? Position => Ctx.Host.CurrentPosition;
        protected decimal QuoteBalance => Ctx.Host.QuoteBalance;
        protected decimal BaseBalance => Ctx.Host.BaseBalance;
        protected string Symbol => Ctx.Host.Symbol;

        protected void Log(string msg) => Ctx.Host.Log(msg);
        protected void LogError(string msg, Exception? ex = null) => Ctx.Host.LogError(msg, ex);
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TradingStrategyAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        public TradingStrategyAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }
}
