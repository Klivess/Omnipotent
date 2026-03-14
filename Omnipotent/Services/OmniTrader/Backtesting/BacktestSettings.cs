namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public enum AmountType
    {
        Percentage,
        Absolute
    }

    public class BacktestSettings
    {
        /// <summary>Starting quote balance (e.g. USD).</summary>
        public decimal InitialQuoteBalance { get; set; } = 10_000m;

        /// <summary>Starting base balance (e.g. BTC). Typically 0 for a clean backtest.</summary>
        public decimal InitialBaseBalance { get; set; } = 0m;

        /// <summary>Trading fee as a fraction (0.001 = 0.1%).</summary>
        public decimal FeeFraction { get; set; } = 0.001m;

        /// <summary>Slippage as a fraction of price (0.0005 = 0.05%). Buys execute slightly above close, sells slightly below.</summary>
        public decimal SlippageFraction { get; set; } = 0.0005m;
    }
}
