using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Contracts
{
    public interface IStrategyHost
    {
        string DeploymentId { get; }
        SessionMode Mode { get; }
        string Symbol { get; }
        TimeInterval Interval { get; }
        /// <summary>Account leverage available to this deployment (1 = spot). Strategies may
        /// read this to size into the available margin (e.g. set max weight to ±Leverage).</summary>
        decimal Leverage { get; }

        Task<OrderIntent> SubmitOrderAsync(OrderRequest request, CancellationToken ct = default);
        Task CancelOrderAsync(string intentId, CancellationToken ct = default);

        Position? CurrentPosition { get; }
        decimal QuoteBalance { get; }
        decimal BaseBalance { get; }

        /// <summary>Net signed quantity held per symbol (portfolio mode). In single-symbol mode this
        /// is just the one current position, if any. Symbols that are flat are omitted.</summary>
        IReadOnlyDictionary<string, decimal> PortfolioPositions { get; }

        /// <summary>Total account equity (cash + marked value of every position). Portfolio strategies
        /// size target notionals off this; single-symbol callers can keep using QuoteBalance/BaseBalance.</summary>
        decimal Equity { get; }

        void Log(string message);
        void LogError(string message, Exception? ex = null);
    }
}
