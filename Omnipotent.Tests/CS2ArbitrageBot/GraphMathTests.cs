using Omnipotent.Services.CS2ArbitrageBot;

namespace Omnipotent.Tests.CS2ArbitrageBot
{
    public class GraphMathTests
    {
        #region CalculateLatestEMA - Valid Inputs

        [Fact]
        public void CalculateLatestEMA_SingleValue_ReturnsThatValue()
        {
            var values = new List<float> { 10f };
            float result = GraphMath.CalculateLatestEMA(values, 5);
            Assert.Equal(10f, result);
        }

        [Fact]
        public void CalculateLatestEMA_ConstantValues_ReturnsConstant()
        {
            var values = new List<float> { 5f, 5f, 5f, 5f, 5f };
            float result = GraphMath.CalculateLatestEMA(values, 3);
            Assert.Equal(5f, result, precision: 4);
        }

        [Fact]
        public void CalculateLatestEMA_IncreasingValues_EMATrendsUp()
        {
            var values = new List<float> { 1f, 2f, 3f, 4f, 5f };
            float result = GraphMath.CalculateLatestEMA(values, 3);
            // EMA should be between the mean and the latest value (trending up)
            Assert.True(result > 3f);
            Assert.True(result <= 5f);
        }

        [Fact]
        public void CalculateLatestEMA_DecreasingValues_EMATrendsDown()
        {
            var values = new List<float> { 5f, 4f, 3f, 2f, 1f };
            float result = GraphMath.CalculateLatestEMA(values, 3);
            Assert.True(result < 3f);
            Assert.True(result >= 1f);
        }

        [Fact]
        public void CalculateLatestEMA_Period1_ReturnsLastValue()
        {
            var values = new List<float> { 10f, 20f, 30f };
            float result = GraphMath.CalculateLatestEMA(values, 1);
            // With period=1, multiplier=1, so EMA converges to the latest value
            Assert.Equal(30f, result, precision: 4);
        }

        [Fact]
        public void CalculateLatestEMA_KnownCalculation_ReturnsExpectedResult()
        {
            // Manual EMA calculation: period=3, multiplier=2/(3+1)=0.5
            // values: [2, 4, 6, 8, 10]
            // EMA[0] = 2
            // EMA[1] = (4-2)*0.5 + 2 = 3
            // EMA[2] = (6-3)*0.5 + 3 = 4.5
            // EMA[3] = (8-4.5)*0.5 + 4.5 = 6.25
            // EMA[4] = (10-6.25)*0.5 + 6.25 = 8.125
            var values = new List<float> { 2f, 4f, 6f, 8f, 10f };
            float result = GraphMath.CalculateLatestEMA(values, 3);
            Assert.Equal(8.125f, result, precision: 3);
        }

        #endregion

        #region CalculateLatestEMA - Invalid Inputs

        [Fact]
        public void CalculateLatestEMA_NullValues_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => GraphMath.CalculateLatestEMA(null!, 5));
        }

        [Fact]
        public void CalculateLatestEMA_EmptyValues_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => GraphMath.CalculateLatestEMA([], 5));
        }

        [Fact]
        public void CalculateLatestEMA_ZeroPeriod_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => GraphMath.CalculateLatestEMA([1f, 2f], 0));
        }

        [Fact]
        public void CalculateLatestEMA_NegativePeriod_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => GraphMath.CalculateLatestEMA([1f, 2f], -1));
        }

        #endregion
    }
}
