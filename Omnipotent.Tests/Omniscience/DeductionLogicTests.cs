using Omnipotent.Services.Omniscience.Deduction;

namespace Omnipotent.Tests.Omniscience
{
    public class DeductionLogicTests
    {
        [Fact]
        public void IsSelfDisclosure_DenseMessage_True()
        {
            Assert.True(ExtractionJob.IsSelfDisclosure("i'm 17 btw, my brother jake is at uni and i live in leeds"));
        }

        [Theory]
        [InlineData("lol yeah")]
        [InlineData("💀")]
        [InlineData("ok")]
        [InlineData("")]
        [InlineData(null)]
        public void IsSelfDisclosure_LowSignal_False(string? content)
        {
            Assert.False(ExtractionJob.IsSelfDisclosure(content));
        }

        [Fact]
        public void NameSimilarity_ExactMatch_One()
        {
            Assert.Equal(1.0, IdentityLinkEngine.NameSimilarity("james", "James"));
        }

        [Fact]
        public void NameSimilarity_Containment_High()
        {
            Assert.True(IdentityLinkEngine.NameSimilarity("sarah", "sarah_x") >= 0.85);
        }

        [Fact]
        public void NameSimilarity_Unrelated_Low()
        {
            Assert.True(IdentityLinkEngine.NameSimilarity("james", "katherine") < 0.4);
        }

        [Fact]
        public void NameSimilarity_TypoVariant_Moderate()
        {
            double sim = IdentityLinkEngine.NameSimilarity("jonathan", "johnathan");
            Assert.True(sim is > 0.6 and < 1.0, $"expected moderate similarity, got {sim}");
        }
    }
}
