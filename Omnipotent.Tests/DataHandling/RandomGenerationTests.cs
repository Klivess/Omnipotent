using Omnipotent.Data_Handling;

namespace Omnipotent.Tests.DataHandling
{
    public class RandomGenerationTests
    {
        [Fact]
        public void GenerateRandomLengthOfNumbers_ReturnsCorrectLength()
        {
            string result = RandomGeneration.GenerateRandomLengthOfNumbers(10);
            Assert.Equal(10, result.Length);
        }

        [Fact]
        public void GenerateRandomLengthOfNumbers_LengthOne_ReturnsSingleDigit()
        {
            string result = RandomGeneration.GenerateRandomLengthOfNumbers(1);
            Assert.Equal(1, result.Length);
            Assert.True(char.IsDigit(result[0]));
        }

        [Fact]
        public void GenerateRandomLengthOfNumbers_LengthZero_ReturnsEmpty()
        {
            string result = RandomGeneration.GenerateRandomLengthOfNumbers(0);
            Assert.Empty(result);
        }

        [Fact]
        public void GenerateRandomLengthOfNumbers_ContainsOnlyDigits()
        {
            string result = RandomGeneration.GenerateRandomLengthOfNumbers(50);
            Assert.All(result, c => Assert.True(char.IsDigit(c)));
        }

        [Fact]
        public void GenerateRandomLengthOfNumbers_LargeLength_ReturnsCorrectLength()
        {
            string result = RandomGeneration.GenerateRandomLengthOfNumbers(1000);
            Assert.Equal(1000, result.Length);
        }

        [Fact]
        public void GenerateRandomLengthOfNumbers_TwoCallsProduceDifferentResults()
        {
            // With high probability, two calls with large length produce different results
            string result1 = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            string result2 = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            // This could theoretically fail but the probability is astronomically low
            Assert.NotEqual(result1, result2);
        }
    }
}
