using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>
    /// Section 10: the cost model the backtest must apply, and how it maps onto OmniTrader's engine.
    /// <list type="bullet">
    /// <item><b>Fee</b> (taker/maker) → <see cref="DeploymentConfig.FeeFraction"/> / <see cref="BacktestConfig.FeeFraction"/>,
    /// charged per side by the <c>SimulatedOrderRouter</c>.</item>
    /// <item><b>Slippage</b> → <see cref="BacktestConfig.SlippageFraction"/>, applied to every fill price.
    /// The participation cap (Section 8) bounds order size so the backtest can't assume unrealistic fills.</item>
    /// <item><b>Funding</b> (shorts / leveraged longs) → the router's per-bar borrow/rollover rate
    /// (<see cref="MarginSettings.BorrowAnnualRate"/>). Set it to borrow + perp funding; for a short the
    /// borrowed-notional carry IS the funding cost. <see cref="MomentumConfig.AnnualFundingRate"/> records
    /// the assumed funding component so it surfaces in reports.</item>
    /// </list>
    /// Results are reported both gross and net (<see cref="GrossNet"/>); if the edge only exists gross it
    /// does not exist.
    /// </summary>
    public static class CostModel
    {
        /// <summary>
        /// Translate momentum cost assumptions into the engine's margin/borrow settings so a short's
        /// funding cost is actually charged per bar. Combines the supplied borrow rate with the
        /// configured perp funding rate.
        /// </summary>
        public static MarginSettings ToMarginSettings(MomentumConfig cfg, MarginSettings baseSettings)
            => new()
            {
                Leverage = baseSettings.Leverage,
                LiquidationMarginLevel = baseSettings.LiquidationMarginLevel,
                OpeningFeeFraction = baseSettings.OpeningFeeFraction,
                BorrowAnnualRate = baseSettings.BorrowAnnualRate + cfg.AnnualFundingRate,
            };

        /// <summary>Gross- vs net-of-cost view of a finished run. Gross adds the realised fees back.</summary>
        public readonly record struct GrossNet(decimal NetPnL, decimal GrossPnL, decimal TotalCosts)
        {
            public decimal NetPnLPercent(decimal initialEquity) => initialEquity == 0m ? 0m : NetPnL / initialEquity * 100m;
            public decimal GrossPnLPercent(decimal initialEquity) => initialEquity == 0m ? 0m : GrossPnL / initialEquity * 100m;
        }

        /// <summary>
        /// Gross = net + all realised fees/funding (slippage stays embedded in fill prices; the
        /// 1×/2×/3× cost-sensitivity re-runs in Section 11 capture slippage sensitivity end-to-end).
        /// </summary>
        public static GrossNet Split(decimal initialEquity, decimal finalEquity, decimal totalFees)
        {
            decimal net = finalEquity - initialEquity;
            return new GrossNet(net, net + totalFees, totalFees);
        }
    }
}
