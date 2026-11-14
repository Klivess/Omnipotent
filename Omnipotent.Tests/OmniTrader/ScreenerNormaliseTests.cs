using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Screening;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// A screen run failed in 0.6 s on 9 Nov 2026 with "Object reference not set to an instance of an
    /// object." (3 of 5 runs failed in 30 days). Request bodies can carry nulls the defaults never see.
    /// </summary>
    public class ScreenerNormaliseTests
    {
        private const string Body = "{\"Name\":null,\"Universe\":null,\"Symbols\":[\"AAPL\",null,\" msft \"],\"Filters\":null,"
            + "\"Columns\":[{\"Key\":\"rsi\",\"Expr\":null},{\"Key\":null,\"Expr\":\"rsi14\"},null],\"Inputs\":null}";

        private static ScreenDefinition Parse() => JsonConvert.DeserializeObject<ScreenDefinition>(Body, TerminalRoutes.JsonSettings)!;

        [Fact]
        public void A_body_with_nulls_used_to_throw_before_anything_ran()
        {
            Assert.ThrowsAny<NullReferenceException>(() => ScreenerEngine.Validate(Parse()));
        }

        [Fact]
        public void Normalised_it_validates_and_keeps_what_was_meant()
        {
            var def = ScreenerEngine.Normalise(Parse());

            Assert.Empty(ScreenerEngine.Validate(def));
            Assert.Empty(def.Filters);
            Assert.Single(def.Columns);
            Assert.Equal("rsi14", def.Columns[0].Key);
            Assert.Equal(new[] { "AAPL", "msft" }, def.Symbols);
            Assert.NotNull(def.Inputs);
            Assert.Equal("", def.Universe);                 // symbols given: no universe needed
            Assert.Equal("Untitled screen", def.Name);
        }

        [Fact]
        public void An_empty_universe_falls_back_to_the_default_list()
        {
            var def = ScreenerEngine.Normalise(new ScreenDefinition { Universe = " ", Symbols = null! });
            Assert.Equal("megacaps", def.Universe);
            Assert.Empty(def.Symbols);
        }
    }
}
