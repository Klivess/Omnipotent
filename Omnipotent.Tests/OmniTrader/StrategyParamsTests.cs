using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>The declarative [Param] schema + value injection used by the dynamic UI.</summary>
    public class StrategyParamsTests
    {
        [Fact]
        public void Schema_Reflects_Annotated_Properties_With_Defaults()
        {
            var schema = StrategyParams.For(typeof(IBSMeanReversionStrategy));
            var trend = schema.FirstOrDefault(p => p.Name == "TrendEmaPeriod");
            Assert.NotNull(trend);
            Assert.Equal("int", trend!.Type);
            Assert.Equal(200, Convert.ToInt32(trend.Default));
            Assert.Equal("Trend", trend.Group);
            Assert.Equal(20, trend.Min);

            // ATR stop multiplier is a decimal param.
            var atr = schema.FirstOrDefault(p => p.Name == "AtrStopMultiplier");
            Assert.NotNull(atr);
            Assert.Equal("decimal", atr!.Type);
        }

        [Fact]
        public void Apply_Writes_Typed_Values_From_Jsonish_Dict()
        {
            var s = new IBSMeanReversionStrategy();
            Assert.Equal(200, s.TrendEmaPeriod);
            Assert.Equal(1.5m, s.AtrStopMultiplier);

            // Values arrive JSON-boxed (long for ints, double for decimals) — Apply must convert.
            StrategyParams.Apply(s, new Dictionary<string, object?>
            {
                ["TrendEmaPeriod"] = 100L,
                ["AtrStopMultiplier"] = 2.0,
                ["PositionFraction"] = 0.25,
                ["NotAParam"] = 999,         // ignored
            });

            Assert.Equal(100, s.TrendEmaPeriod);
            Assert.Equal(2.0m, s.AtrStopMultiplier);
            Assert.Equal(0.25m, s.PositionFraction);
        }

        [Fact]
        public void Apply_Is_CaseInsensitive_And_Ignores_Bad_Values()
        {
            var s = new TCNSignalStrategy();
            StrategyParams.Apply(s, new Dictionary<string, object?>
            {
                ["tau"] = 0.5,               // case-insensitive key
                ["SigmaStarAnn"] = "not-a-number", // bad → ignored, no throw
            });
            Assert.Equal(0.5, s.Tau);
            Assert.Equal(0.12, s.SigmaStarAnn); // unchanged
        }
    }
}
