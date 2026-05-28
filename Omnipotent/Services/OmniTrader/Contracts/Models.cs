namespace Omnipotent.Services.OmniTrader.Contracts
{
    public enum TimeInterval
    {
        OneMinute = 1,
        FiveMinute = 5,
        FifteenMinute = 15,
        ThirtyMinute = 30,
        OneHour = 60,
        FourHour = 240,
        OneDay = 1440,
        OneWeek = 10080
    }

    public enum SessionMode { Backtest, Paper, Live }

    public enum DeploymentStatus { Running, Paused, Stopped, Errored }

    public enum OrderSide { Buy, Sell }

    public enum OrderType { Market, Limit, StopLoss, TakeProfit }

    public enum OrderStatus { Pending, Open, PartiallyFilled, Filled, Cancelled, Rejected }

    public readonly record struct OHLCCandle(
        DateTime Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume);

    public sealed class OrderRequest
    {
        public required string IntentId { get; init; }
        public required OrderSide Side { get; init; }
        public required OrderType Type { get; init; }
        public required string Symbol { get; init; }
        public required decimal Qty { get; init; }
        public decimal? LimitPrice { get; init; }
        public decimal? StopPrice { get; init; }
    }

    public sealed class OrderIntent
    {
        public required string Id { get; init; }
        public required string IntentId { get; init; }
        public required string DeploymentId { get; init; }
        public required OrderRequest Request { get; init; }
        public required OrderStatus Status { get; set; }
        public required DateTime PlacedUtc { get; init; }
        public string? ExchangeOrderId { get; set; }
        public string? Error { get; set; }
    }

    public sealed class FillEvent
    {
        public required string OrderId { get; init; }
        public required string IntentId { get; init; }
        public required decimal Qty { get; init; }
        public required decimal Price { get; init; }
        public required decimal Fee { get; init; }
        public required string FeeCurrency { get; init; }
        public required DateTime FilledUtc { get; init; }
    }

    public sealed class Position
    {
        public required string Symbol { get; init; }
        public decimal Qty { get; set; }
        public decimal AveragePrice { get; set; }
        public DateTime OpenedUtc { get; set; }

        public bool IsFlat => Qty == 0;
        public bool IsLong => Qty > 0;
        public bool IsShort => Qty < 0;
    }

    public sealed class RiskCaps
    {
        public decimal MaxPositionQuoteUsd { get; init; } = 100m;
        public decimal MaxDailyLossUsd { get; init; } = 50m;
        public int MaxOrdersPerHour { get; init; } = 30;
        public HashSet<string> AllowedSymbols { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class DeploymentConfig
    {
        public required string StrategyClass { get; init; }
        public required string Symbol { get; init; }
        public required TimeInterval Interval { get; init; }
        public required SessionMode Mode { get; init; }
        public decimal InitialQuoteBalance { get; init; } = 10_000m;
        public decimal InitialBaseBalance { get; init; } = 0m;
        public decimal FeeFraction { get; init; } = 0.001m;
        public decimal SlippageFraction { get; init; } = 0.0005m;
        public RiskCaps? Caps { get; init; }
    }

    public sealed class BacktestConfig
    {
        public required string StrategyClass { get; init; }
        public required string Coin { get; init; }
        public required string Currency { get; init; }
        public required TimeInterval Interval { get; init; }
        public required int CandleCount { get; init; }
        public decimal InitialQuoteBalance { get; init; } = 10_000m;
        public decimal InitialBaseBalance { get; init; } = 0m;
        public decimal FeeFraction { get; init; } = 0.001m;
        public decimal SlippageFraction { get; init; } = 0.0005m;
    }

    public sealed class TradeRecord
    {
        public required DateTime EntryTime { get; init; }
        public required DateTime ExitTime { get; init; }
        public required decimal EntryPrice { get; init; }
        public required decimal ExitPrice { get; init; }
        public required decimal Qty { get; init; }
        public required bool IsShort { get; init; }
        public required decimal Fees { get; init; }
        public decimal RealizedPnL => IsShort
            ? (EntryPrice - ExitPrice) * Qty - Fees
            : (ExitPrice - EntryPrice) * Qty - Fees;
        public bool IsWin => RealizedPnL > 0;
    }

    public sealed class EquityPoint
    {
        public required DateTime Ts { get; init; }
        public required decimal MarkPrice { get; init; }
        public required decimal QuoteBalance { get; init; }
        public required decimal BaseBalance { get; init; }
        public required decimal Equity { get; init; }
    }
}
