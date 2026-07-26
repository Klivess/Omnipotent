using Newtonsoft.Json;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// What the SSE transport must be able to read out of a streamed chunk. The streaming path
    /// synthesizes the same response shape the buffered path returns, and callers meter cost and pin
    /// OpenRouter fallback routes off the generation id and the SERVED model — so losing either field
    /// silently degrades billing attribution and route selection for every streaming caller.
    /// </summary>
    public class KliveLLMStreamChunkTests
    {
        // A real OpenRouter content delta: it repeats id/model on every chunk.
        private const string ContentChunk = """
            {"id":"gen-abc123","provider":"Anthropic","model":"anthropic/claude-sonnet-4.5",
             "object":"chat.completion.chunk",
             "choices":[{"index":0,"delta":{"role":"assistant","content":"Checking the "},"finish_reason":null}]}
            """;

        // The final chunk when usage accounting is enabled: usage (with the real cost) and no choices.
        private const string UsageChunk = """
            {"id":"gen-abc123","model":"anthropic/claude-sonnet-4.5","object":"chat.completion.chunk",
             "choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":80,"total_tokens":1280,"cost":0.0042}}
            """;

        [Fact]
        public void ContentChunk_CarriesGenerationIdAndServedModel()
        {
            var chunk = JsonConvert.DeserializeObject<HFWrapper.HFLLMStreamChunk>(ContentChunk)!;

            Assert.Equal("gen-abc123", chunk.id);
            Assert.Equal("anthropic/claude-sonnet-4.5", chunk.model);
            Assert.Equal("Checking the ", chunk.choices[0].delta.content);
        }

        [Fact]
        public void FinalUsageChunk_CarriesTokensAndRealCost()
        {
            var chunk = JsonConvert.DeserializeObject<HFWrapper.HFLLMStreamChunk>(UsageChunk)!;

            Assert.Equal(1200, chunk.usage.prompt_tokens);
            Assert.Equal(80, chunk.usage.completion_tokens);
            Assert.Equal(0.0042, chunk.usage.cost);
            // Identity still travels on the usage chunk, so a stream that only reads the last chunk
            // still knows which generation it paid for.
            Assert.Equal("gen-abc123", chunk.id);
            Assert.Equal("anthropic/claude-sonnet-4.5", chunk.model);
        }

        [Fact]
        public void ChunkWithoutIdentityFields_DeserializesWithoutThrowing()
        {
            // Not every provider repeats id/model; the reader keeps the last non-empty value it saw,
            // so an anonymous keep-alive-shaped chunk must simply parse to nulls.
            var chunk = JsonConvert.DeserializeObject<HFWrapper.HFLLMStreamChunk>(
                """{"choices":[{"index":0,"delta":{"content":"x"}}]}""")!;

            Assert.Null(chunk.id);
            Assert.Null(chunk.model);
            Assert.Equal("x", chunk.choices[0].delta.content);
        }
    }
}
