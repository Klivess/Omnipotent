using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Section 11.3: cost sensitivity. Re-run the net backtest at 1×, 2×, and 3× the assumed
    /// fee + slippage. A strategy that only survives at optimistic costs is not robust.
    /// </summary>
    public static class CostSensitivity
    {
        public readonly record struct Row(double Multiplier, decimal NetPnLPercent, decimal SharpeRatio, decimal MaxDrawdownPercent, decimal TotalFees);

        /// <summary>
        /// <paramref name="sessionFactory"/> must build a fresh <see cref="BacktestSession"/> (fresh
        /// strategy instance + inputs) for the supplied cost-scaled config — strategies are stateful,
        /// so each run needs its own instance.
        /// </summary>
        public static async Task<List<Row>> RunAsync(
            Func<BacktestConfig, BacktestSession> sessionFactory,
            BacktestConfig baseCfg,
            IReadOnlyList<double> multipliers,
            CancellationToken ct = default)
        {
            var rows = new List<Row>(multipliers.Count);
            foreach (var m in multipliers)
            {
                var cfg = ScaleCosts(baseCfg, (decimal)m);
                var res = await sessionFactory(cfg).RunPortfolioAsync(ct);
                rows.Add(new Row(m, res.TotalPnLPercent, res.SharpeRatio, res.MaxDrawdownPercent, res.TotalFeesPaid));
            }
            return rows;
        }

        public static BacktestConfig ScaleCosts(BacktestConfig c, decimal mult) => new()
        {
            StrategyClass = c.StrategyClass,
            Coin = c.Coin,
            Currency = c.Currency,
            Interval = c.Interval,
            CandleCount = c.CandleCount,
            InitialQuoteBalance = c.InitialQuoteBalance,
            InitialBaseBalance = c.InitialBaseBalance,
            FeeFraction = c.FeeFraction * mult,
            SlippageFraction = c.SlippageFraction * mult,
            Margin = c.Margin,
        };
    }
}
