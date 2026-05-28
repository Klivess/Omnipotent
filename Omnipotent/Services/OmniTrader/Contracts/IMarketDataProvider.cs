namespace Omnipotent.Services.OmniTrader.Contracts
{
    public interface IMarketDataProvider
    {
        string Name { get; }
        Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default);
        IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, CancellationToken ct = default);
    }
}
