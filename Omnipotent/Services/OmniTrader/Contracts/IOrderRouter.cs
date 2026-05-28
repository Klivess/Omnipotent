namespace Omnipotent.Services.OmniTrader.Contracts
{
    public interface IOrderRouter
    {
        Task<OrderIntent> PlaceOrderAsync(string deploymentId, OrderRequest request, CancellationToken ct = default);
        Task CancelOrderAsync(OrderIntent intent, CancellationToken ct = default);
    }
}
