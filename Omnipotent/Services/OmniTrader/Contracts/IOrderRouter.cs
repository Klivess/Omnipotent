namespace Omnipotent.Services.OmniTrader.Contracts
{
    public interface IOrderRouter
    {
        Task<OrderIntent> PlaceOrderAsync(string deploymentId, OrderRequest request, CancellationToken ct = default);
        Task CancelOrderAsync(OrderIntent intent, CancellationToken ct = default);

        /// <summary>
        /// Poll the exchange for the execution state of the given orders so the session can book
        /// fills. Routers that fill synchronously (the simulator) have nothing to report.
        /// </summary>
        Task<IReadOnlyList<ExchangeFill>> QueryFillsAsync(IEnumerable<string> exchangeOrderIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExchangeFill>>(Array.Empty<ExchangeFill>());
    }
}
