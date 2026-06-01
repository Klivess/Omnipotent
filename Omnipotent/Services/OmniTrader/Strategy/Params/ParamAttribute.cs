namespace Omnipotent.Services.OmniTrader.Strategy.Params
{
    /// <summary>
    /// Marks a public settable strategy property as a user-configurable parameter. The registry reflects
    /// these into a schema the frontend renders dynamically; <see cref="StrategyParams.Apply"/> writes the
    /// chosen values back onto the instance before the strategy runs (backtest, paper, or live).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ParamAttribute : Attribute
    {
        public string Label { get; }
        public string Group { get; init; } = "General";
        public double Min { get; init; } = double.NaN;
        public double Max { get; init; } = double.NaN;
        public double Step { get; init; } = double.NaN;
        public string? Help { get; init; }
        /// <summary>Render this string parameter as a trading-symbol picker (e.g. BTCUSDT).</summary>
        public bool IsSymbol { get; init; }

        public ParamAttribute(string label) { Label = label; }
    }
}
