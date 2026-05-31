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
        /// <summary>Margin leverage for this order (1 = spot). Live sessions inject the
        /// deployment's leverage; the simulated router uses its State.Leverage instead.</summary>
        public decimal Leverage { get; init; } = 1m;
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
        /// <summary>Symbol the fill is for. Needed by portfolio mode to attribute fills to the right
        /// book; the single-symbol path also sets it. May be empty for legacy/exchange fills.</summary>
        public string Symbol { get; init; } = "";
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

    /// <summary>
    /// A single point-in-time slice of a multi-asset universe, handed to a portfolio strategy's
    /// <c>OnUniverseBar</c>. All series end at <see cref="T"/> — nothing here looks ahead.
    /// <para><see cref="Histories"/> maps symbol → its daily candle history up to and including T
    /// (for the cross-sectional momentum strategy these are daily closes synthesised as OHLC with
    /// <c>Volume</c> = trailing USD quote volume). <see cref="MarketCaps"/> maps symbol → its USD
    /// market cap as of T (used only for ranking/universe cap). A symbol present in the engine's
    /// candle set but absent from <see cref="MarketCaps"/> simply has no cap datum at T.</para>
    /// </summary>
    public sealed class PortfolioBar
    {
        public required DateTime T { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> Histories { get; init; }
        public required IReadOnlyDictionary<string, decimal> MarketCaps { get; init; }

        /// <summary>Latest (as-of-T) close for a symbol, or 0 if it has no history at T.</summary>
        public decimal Mark(string symbol)
            => Histories.TryGetValue(symbol, out var h) && h.Count > 0 ? h[^1].Close : 0m;
    }

    public sealed class RiskCaps
    {
        public decimal MaxPositionQuoteUsd { get; init; } = 100m;
        public decimal MaxDailyLossUsd { get; init; } = 50m;
        public int MaxOrdersPerHour { get; init; } = 30;
        public HashSet<string> AllowedSymbols { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Margin settings shared by deployments and backtests. Leverage = 1 means spot
    /// (margin mode is fully disabled and behaviour is identical to the pre-margin engine).
    /// </summary>
    public sealed class MarginSettings
    {
        /// <summary>Account leverage, 1–10. 1 = spot (no borrowing, no shorts, no liquidation).</summary>
        public decimal Leverage { get; init; } = 1m;
        /// <summary>Liquidate when margin level (equity / posted margin) falls to this. Kraken ≈ 0.40.</summary>
        public decimal LiquidationMarginLevel { get; init; } = 0.40m;
        /// <summary>Annualised borrow/rollover rate charged per bar on borrowed notional.</summary>
        public decimal BorrowAnnualRate { get; init; } = 0.20m;
        /// <summary>Fee fraction charged when opening/increasing a leveraged position (Kraken ≈ 0.0002).</summary>
        public decimal OpeningFeeFraction { get; init; } = 0.0002m;

        public static MarginSettings Spot => new();
        public decimal ClampedLeverage => Math.Clamp(Leverage, 1m, 10m);
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
        public MarginSettings Margin { get; init; } = new();
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
        public MarginSettings Margin { get; init; } = new();

        /// <summary>When set, the job runs the multi-asset (cross-sectional momentum) portfolio path
        /// instead of the single-symbol path. Null for ordinary single-symbol backtests.</summary>
        public MomentumBacktestSettings? Momentum { get; init; }
    }

    /// <summary>
    /// Settings for a cross-sectional momentum (portfolio) backtest. Carries the universe window/fetch
    /// parameters, the strategy parameter block (mirrors <c>MomentumConfig</c> as primitives so this
    /// layer stays free of a Strategy dependency), and the Section 11 validation knobs. The backtest
    /// queue maps it onto the engine.
    /// </summary>
    public sealed class MomentumBacktestSettings
    {
        // ── Universe fetch / window ─────────────────────────────────────────────
        public int UniverseTopN { get; init; } = 100;
        /// <summary>Engine key of the regime/benchmark asset — a Binance pair, e.g. BTCUSDT.</summary>
        public string RegimeSymbol { get; init; } = "BTCUSDT";
        /// <summary>Binance quote asset the universe is built from (USDT by default).</summary>
        public string QuoteAsset { get; init; } = "USDT";
        public DateTime FromUtc { get; init; } = DateTime.UtcNow.AddYears(-2);
        public DateTime ToUtc { get; init; } = DateTime.UtcNow.AddDays(-1);

        // ── Strategy params (defaults = the spec's starting point) ──────────────
        public int LookbackDays { get; init; } = 30;
        public int SkipDays { get; init; } = 1;
        public int RebalanceDays { get; init; } = 7;
        public int VolLookbackDays { get; init; } = 30;
        public double TopFraction { get; init; } = 0.20;
        public double BottomFraction { get; init; } = 0.20;
        public int MinUniverseSize { get; init; } = 20;
        public bool UseRiskAdjusted { get; init; } = true;
        public string SkipAction { get; init; } = "cash";        // cash | hold
        public double TargetPortfolioVol { get; init; } = 0.40;
        public double MaxWeightPerAsset { get; init; } = 0.20;
        public double MaxGrossLeverage { get; init; } = 1.0;
        public int RegimeMaDays { get; init; } = 100;
        public double RiskOffScalar { get; init; } = 0.0;
        public bool KeepShortsWhenRiskOff { get; init; } = true;
        public double DdKillswitch { get; init; } = 0.30;
        public int UniverseCap { get; init; } = 100;
        public double LiquidityFloorUsd { get; init; } = 5_000_000;
        public int LiquidityLookbackDays { get; init; } = 30;
        public double PegVolThreshold { get; init; } = 0.01;
        public double ParticipationCap { get; init; } = 0.05;
        public decimal AnnualFundingRate { get; init; } = 0.10m;

        // ── Validation (Section 11) ─────────────────────────────────────────────
        public bool RunValidation { get; init; } = true;
        public int InSampleDays { get; init; } = 180;
        public int OosDays { get; init; } = 60;
        public int WarmupDays { get; init; } = 62;
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
