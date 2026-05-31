using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>
    /// Adapter from IStrategyHost to a concrete session — routes orders through the session's pipeline
    /// so the session can record idempotency, apply RiskGate (for live), and persist.
    /// </summary>
    public sealed class StrategyHost : IStrategyHost
    {
        public string DeploymentId { get; }
        public SessionMode Mode { get; }
        public string Symbol { get; }
        public TimeInterval Interval { get; }
        public decimal Leverage { get; }

        private readonly Func<OrderRequest, CancellationToken, Task<OrderIntent>> submit;
        private readonly Func<string, CancellationToken, Task> cancel;
        private readonly Func<Position?> positionFunc;
        private readonly Func<decimal> quoteFunc;
        private readonly Func<decimal> baseFunc;
        private readonly Func<IReadOnlyDictionary<string, decimal>>? portfolioPositionsFunc;
        private readonly Func<decimal>? equityFunc;
        private readonly Action<string> logAction;
        private readonly Action<string, Exception?> errAction;

        public StrategyHost(
            string deploymentId, SessionMode mode, string symbol, TimeInterval interval,
            Func<OrderRequest, CancellationToken, Task<OrderIntent>> submit,
            Func<string, CancellationToken, Task> cancel,
            Func<Position?> positionFunc,
            Func<decimal> quoteFunc,
            Func<decimal> baseFunc,
            Action<string> log,
            Action<string, Exception?> err,
            decimal leverage = 1m,
            Func<IReadOnlyDictionary<string, decimal>>? portfolioPositionsFunc = null,
            Func<decimal>? equityFunc = null)
        {
            DeploymentId = deploymentId;
            Mode = mode;
            Symbol = symbol;
            Interval = interval;
            Leverage = leverage;
            this.submit = submit;
            this.cancel = cancel;
            this.positionFunc = positionFunc;
            this.quoteFunc = quoteFunc;
            this.baseFunc = baseFunc;
            this.portfolioPositionsFunc = portfolioPositionsFunc;
            this.equityFunc = equityFunc;
            this.logAction = log;
            this.errAction = err;
        }

        public Position? CurrentPosition => positionFunc();
        public decimal QuoteBalance => quoteFunc();
        public decimal BaseBalance => baseFunc();

        public IReadOnlyDictionary<string, decimal> PortfolioPositions
        {
            get
            {
                if (portfolioPositionsFunc != null) return portfolioPositionsFunc();
                // Single-symbol default: surface the lone position (if any) as a one-entry map.
                var pos = positionFunc();
                return pos != null && pos.Qty != 0m
                    ? new Dictionary<string, decimal> { [pos.Symbol] = pos.Qty }
                    : new Dictionary<string, decimal>();
            }
        }

        // Single-symbol default falls back to cash (no mark available here); portfolio mode injects
        // the authoritative router equity via equityFunc.
        public decimal Equity => equityFunc?.Invoke() ?? quoteFunc();
        public Task<OrderIntent> SubmitOrderAsync(OrderRequest req, CancellationToken ct = default) => submit(req, ct);
        public Task CancelOrderAsync(string intentId, CancellationToken ct = default) => cancel(intentId, ct);
        public void Log(string msg) => logAction(msg);
        public void LogError(string msg, Exception? ex = null) => errAction(msg, ex);
    }
}
