using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy
{
    public sealed class StrategyContext
    {
        public required IStrategyHost Host { get; init; }
        public List<OHLCCandle> CandleHistory { get; } = new();
    }
}
