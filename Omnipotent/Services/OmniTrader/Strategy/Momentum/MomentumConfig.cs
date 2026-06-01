using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>When the universe is too small to trade, either move to cash or keep the prior book.</summary>
    public enum SkipAction { Cash, Hold }

    /// <summary>
    /// Section 2 of the spec: the whole parameter block in one place. Defaults are the
    /// literature-supported starting point; the <c>Sweep*</c> ranges drive walk-forward validation
    /// (Section 11) and must NOT be optimized on a single in-sample window.
    /// </summary>
    public sealed class MomentumConfig
    {
        // ── Signal / schedule ───────────────────────────────────────────────────
        public int LookbackDays { get; set; } = 30;          // sweep 15..35
        public int SkipDays { get; set; } = 1;               // sweep 0..2
        public int RebalanceDays { get; set; } = 7;          // fixed weekly
        public int VolLookbackDays { get; set; } = 30;       // sweep 20..60

        // ── Selection ───────────────────────────────────────────────────────────
        public double TopFraction { get; set; } = 0.20;      // sweep 0.10..0.30
        public double BottomFraction { get; set; } = 0.20;   // 0 = long-only; sweep 0..0.30
        public int MinUniverseSize { get; set; } = 20;
        public SkipAction SkipAction { get; set; } = SkipAction.Cash;
        public bool UseRiskAdjusted { get; set; } = true;    // score = cumret/vol vs raw cumret (A/B)

        // ── Sizing / leverage ───────────────────────────────────────────────────
        public double TargetPortfolioVol { get; set; } = 0.40;  // annualized; sweep 0.20..0.60
        public double MaxWeightPerAsset { get; set; } = 0.20;
        public double MaxGrossLeverage { get; set; } = 1.0;     // sweep 1.0..2.0

        // ── Regime filter ───────────────────────────────────────────────────────
        public int RegimeMaDays { get; set; } = 100;            // sweep 50..200
        public string RegimeAsset { get; set; } = "BTC";
        public double RiskOffScalar { get; set; } = 0.0;        // 0 = full cash when off; e.g. 0.25 partial
        public bool KeepShortsWhenRiskOff { get; set; } = true; // long-short: drop longs, keep shorts

        // ── Risk controls ───────────────────────────────────────────────────────
        public double DdKillswitch { get; set; } = 0.30;

        // ── Universe construction (Section 3) ───────────────────────────────────
        public int UniverseCap { get; set; } = 100;
        public double LiquidityFloorUsd { get; set; } = 5_000_000;
        public int LiquidityLookbackDays { get; set; } = 30;
        public double PegVolThreshold { get; set; } = 0.01;     // 30d vol < 1% ⇒ peg/stable, excluded

        // ── Cost model (Section 10) ─────────────────────────────────────────────
        public double ParticipationCap { get; set; } = 0.05;    // ≤5% of the bar's quote volume per order
        public decimal AnnualFundingRate { get; set; } = 0.10m; // perp funding on short notional (modeled via borrow rate)

        // ── Protective brackets (overlay on top of the weekly re-rank; 0 = off) ──
        public double StopLossPct { get; set; } = 0.0;          // e.g. 0.15 = exit a name if it drops 15%
        public double TakeProfitPct { get; set; } = 0.0;        // e.g. 0.30 = bank a name up 30%

        /// <summary>Minimum history a name needs to produce a signal: lookback + vol window + skip.</summary>
        public int MinHistoryDays => LookbackDays + VolLookbackDays + SkipDays + 1;

        public MomentumConfig Clone() => (MomentumConfig)MemberwiseClone();

        /// <summary>Map the persisted backtest settings DTO onto the strategy parameter block.</summary>
        public static MomentumConfig FromSettings(Contracts.MomentumBacktestSettings s) => new()
        {
            LookbackDays = s.LookbackDays,
            SkipDays = s.SkipDays,
            RebalanceDays = s.RebalanceDays,
            VolLookbackDays = s.VolLookbackDays,
            TopFraction = s.TopFraction,
            BottomFraction = s.BottomFraction,
            MinUniverseSize = s.MinUniverseSize,
            UseRiskAdjusted = s.UseRiskAdjusted,
            SkipAction = s.SkipAction.Equals("hold", StringComparison.OrdinalIgnoreCase) ? SkipAction.Hold : SkipAction.Cash,
            TargetPortfolioVol = s.TargetPortfolioVol,
            MaxWeightPerAsset = s.MaxWeightPerAsset,
            MaxGrossLeverage = s.MaxGrossLeverage,
            RegimeMaDays = s.RegimeMaDays,
            RiskOffScalar = s.RiskOffScalar,
            KeepShortsWhenRiskOff = s.KeepShortsWhenRiskOff,
            DdKillswitch = s.DdKillswitch,
            UniverseCap = s.UniverseCap,
            LiquidityFloorUsd = s.LiquidityFloorUsd,
            LiquidityLookbackDays = s.LiquidityLookbackDays,
            PegVolThreshold = s.PegVolThreshold,
            ParticipationCap = s.ParticipationCap,
            AnnualFundingRate = s.AnnualFundingRate,
            StopLossPct = s.StopLossPct,
            TakeProfitPct = s.TakeProfitPct,
        };
    }

    /// <summary>A coin's point-in-time snapshot at a rebalance timestamp. All series end at <c>t</c>.</summary>
    public sealed class AssetSnapshot
    {
        /// <summary>Engine key — unique per coin (provider id or e.g. BTCUSD).</summary>
        public required string Symbol { get; init; }
        /// <summary>Base ticker (BTC, ETH) used for denylist matching. Falls back to a parse of
        /// <see cref="Symbol"/> when empty.</summary>
        public string Ticker { get; init; } = "";
        /// <summary>Daily candle history up to and including t. <c>Volume</c> is trailing USD quote volume.</summary>
        public required IReadOnlyList<OHLCCandle> History { get; init; }
        /// <summary>USD market cap as of t (0 if unknown — such names rank last).</summary>
        public decimal MarketCap { get; init; }
        /// <summary>Whether the venue has a perp/futures market for this coin (required to short it).</summary>
        public bool Shortable { get; init; } = true;
    }
}
