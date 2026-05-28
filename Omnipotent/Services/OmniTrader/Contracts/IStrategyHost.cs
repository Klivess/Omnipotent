using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Contracts
{
    public interface IStrategyHost
    {
        string DeploymentId { get; }
        SessionMode Mode { get; }
        string Symbol { get; }
        TimeInterval Interval { get; }

        Task<OrderIntent> SubmitOrderAsync(OrderRequest request, CancellationToken ct = default);
        Task CancelOrderAsync(string intentId, CancellationToken ct = default);

        Position? CurrentPosition { get; }
        decimal QuoteBalance { get; }
        decimal BaseBalance { get; }

        void Log(string message);
        void LogError(string message, Exception? ex = null);
    }
}
