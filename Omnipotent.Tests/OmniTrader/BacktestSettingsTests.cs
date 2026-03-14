using Omnipotent.Services.OmniTrader.Backtesting;

namespace Omnipotent.Tests.OmniTrader
{
    public class BacktestSettingsTests
    {
        [Fact]
        public void DefaultSettings_InitialQuoteBalance_Is10000()
        {
            var settings = new BacktestSettings();
            Assert.Equal(10_000m, settings.InitialQuoteBalance);
        }

        [Fact]
        public void DefaultSettings_InitialBaseBalance_IsZero()
        {
            var settings = new BacktestSettings();
            Assert.Equal(0m, settings.InitialBaseBalance);
        }

        [Fact]
        public void DefaultSettings_FeeFraction_Is0001()
        {
            var settings = new BacktestSettings();
            Assert.Equal(0.001m, settings.FeeFraction);
        }

        [Fact]
        public void DefaultSettings_SlippageFraction_Is00005()
        {
            var settings = new BacktestSettings();
            Assert.Equal(0.0005m, settings.SlippageFraction);
        }

        [Fact]
        public void CustomSettings_OverrideDefaults()
        {
            var settings = new BacktestSettings
            {
                InitialQuoteBalance = 50_000m,
                InitialBaseBalance = 1m,
                FeeFraction = 0.002m,
                SlippageFraction = 0.001m
            };

            Assert.Equal(50_000m, settings.InitialQuoteBalance);
            Assert.Equal(1m, settings.InitialBaseBalance);
            Assert.Equal(0.002m, settings.FeeFraction);
            Assert.Equal(0.001m, settings.SlippageFraction);
        }

        [Fact]
        public void AmountType_HasPercentageAndAbsolute()
        {
            Assert.True(Enum.IsDefined(typeof(AmountType), AmountType.Percentage));
            Assert.True(Enum.IsDefined(typeof(AmountType), AmountType.Absolute));
        }
    }
}
