namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// FreeOpenRouterModelID = "nothing" on the live server was sent to OpenRouter verbatim, so every free-model
    /// caller (Omniscience profiles, extraction, detective, search, replica) failed with 400 "nothing is not a
    /// valid model ID". A switched-off value must be recognised as switched off, never as a model.
    /// </summary>
    public class FreeModelSwitchTests
    {
        [Theory]
        [InlineData("nothing")]
        [InlineData("Nothing")]
        [InlineData(" none ")]
        [InlineData("off")]
        [InlineData("disabled")]
        [InlineData("")]
        [InlineData(null)]
        public void SwitchedOffValues_AreNotModelIDs(string? value) =>
            Assert.True(Omnipotent.Services.KliveLLM.KliveLLM.IsSwitchedOffModelID(value));

        [Theory]
        [InlineData("openrouter/free")]
        [InlineData("deepseek/deepseek-v4-pro")]
        [InlineData("Qwen3.8")]
        [InlineData("meta-llama/llama-3.3-70b-instruct:free")]
        public void RealModelIDs_AreKept(string value) =>
            Assert.False(Omnipotent.Services.KliveLLM.KliveLLM.IsSwitchedOffModelID(value));
    }
}
