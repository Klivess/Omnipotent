using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>How a caller's pinned sampling parameters reach (or are withheld from) the wire.</summary>
    public class KliveLLMSamplingParameterTests
    {
        [Fact]
        public void OpenRouter_ReceivesEveryPinnedParameterIncludingItsOwnExtensions()
        {
            var payload = Payload();
            LlmService.ApplySamplingParameters(ref payload, OpenRouter(), new ModelSamplingParameters(
                Temperature: 0.25, TopP: 0.8, TopK: 40, FrequencyPenalty: 0.5, PresencePenalty: -0.25,
                RepetitionPenalty: 1.1, MinP: 0.05, TopA: 0.2, Seed: 7));

            var json = Serialize(payload);
            Assert.Equal(0.25, json["temperature"]!.Value<double>());
            Assert.Equal(0.8, json["top_p"]!.Value<double>());
            Assert.Equal(40, json["top_k"]!.Value<int>());
            Assert.Equal(0.5, json["frequency_penalty"]!.Value<double>());
            Assert.Equal(-0.25, json["presence_penalty"]!.Value<double>());
            Assert.Equal(1.1, json["repetition_penalty"]!.Value<double>());
            Assert.Equal(0.05, json["min_p"]!.Value<double>());
            Assert.Equal(0.2, json["top_a"]!.Value<double>());
            Assert.Equal(7, json["seed"]!.Value<int>());
        }

        [Fact]
        public void HuggingFace_GetsVanillaFieldsOnlySoAStrictEndpointCannotReject() =>
            AssertOnlyVanillaFieldsSent(LlmService.LLMProvider.HuggingFace);

        [Fact]
        public void CustomOpenAIEndpoint_GetsVanillaFieldsOnlySoAStrictEndpointCannotReject() =>
            AssertOnlyVanillaFieldsSent(LlmService.LLMProvider.CustomOpenAI);

        private static void AssertOnlyVanillaFieldsSent(LlmService.LLMProvider provider)
        {
            var payload = Payload();
            LlmService.ApplySamplingParameters(ref payload, Provider(provider), new ModelSamplingParameters(
                Temperature: 0.4, TopK: 40, RepetitionPenalty: 1.1, MinP: 0.05, TopA: 0.2, Seed: 3));

            var json = Serialize(payload);
            Assert.Equal(0.4, json["temperature"]!.Value<double>());
            Assert.Equal(3, json["seed"]!.Value<int>());
            foreach (string extension in new[] { "top_k", "repetition_penalty", "min_p", "top_a" })
                Assert.Null(json[extension]);
        }

        [Fact]
        public void UnpinnedParametersAreAbsentSoTheProviderDefaultApplies()
        {
            var payload = Payload();
            LlmService.ApplySamplingParameters(ref payload, OpenRouter(),
                new ModelSamplingParameters(Temperature: 0.7));

            var json = Serialize(payload);
            Assert.Equal(0.7, json["temperature"]!.Value<double>());
            foreach (string absent in new[]
                     { "top_p", "top_k", "frequency_penalty", "presence_penalty", "repetition_penalty", "min_p", "top_a", "seed" })
                Assert.Null(json[absent]);
        }

        [Fact]
        public void NoParametersProducesTheIdenticalPayloadItDidBeforeTheFeature()
        {
            var baseline = Serialize(Payload()).ToString(Formatting.None);

            var nullPayload = Payload();
            LlmService.ApplySamplingParameters(ref nullPayload, OpenRouter(), null);
            Assert.Equal(baseline, Serialize(nullPayload).ToString(Formatting.None));

            var emptyPayload = Payload();
            LlmService.ApplySamplingParameters(ref emptyPayload, OpenRouter(), new ModelSamplingParameters());
            Assert.Equal(baseline, Serialize(emptyPayload).ToString(Formatting.None));
        }

        private static JObject Serialize(HFWrapper.HFLLMInferenceRequest payload) =>
            JObject.Parse(JsonConvert.SerializeObject(payload));

        private static LlmService.RemoteLLMProviderConfiguration OpenRouter() =>
            Provider(LlmService.LLMProvider.OpenRouter);

        private static LlmService.RemoteLLMProviderConfiguration Provider(LlmService.LLMProvider provider) =>
            new(provider, provider.ToString(), "https://provider.test/v1/chat/completions", "test-token", "test/model");

        private static HFWrapper.HFLLMInferenceRequest Payload() => new()
        {
            model = "test/model",
            stream = false,
            messages = new[] { new HFWrapper.HFMessage { role = "user", content = "hello" } },
        };
    }
}
