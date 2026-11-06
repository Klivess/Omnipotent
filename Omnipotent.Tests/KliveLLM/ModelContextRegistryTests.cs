using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>Dynamic context-window resolution (Omniscience v3 plan: "max context, fetched dynamically").
    /// No network: catalogues are injected.</summary>
    public class ModelContextRegistryTests
    {
        private static ModelContextRegistry Registry(
            IReadOnlyList<ModelContextRegistry.CatalogEntry>? openRouter = null,
            IReadOnlyList<ModelContextRegistry.CatalogEntry>? provider = null,
            Dictionary<string, int>? overrides = null,
            int? stated = null,
            string? persist = null)
        {
            return new ModelContextRegistry(
                _ => Task.FromResult(openRouter),
                (_, _) => Task.FromResult(provider),
                (_, model) => overrides != null && overrides.TryGetValue(model, out int v) ? v : null,
                p => p == "AIRouter" ? stated : null,
                persist);
        }

        private static readonly ModelContextRegistry.CatalogEntry[] OpenRouterCatalog =
        {
            new("qwen/qwen3.8", "qwen/qwen3.8-2026", 262_144, 32_768),
            new("qwen/qwen3.8-instruct", null, 131_072, 16_384),
            new("deepseek/deepseek-v4-flash", null, 1_000_000, 65_536),
            new("anthropic/claude-x", null, 200_000, 8_192),
        };

        [Theory]
        [InlineData("This model's maximum context length is 262144 tokens. However, you requested 300000 tokens", 262144)]
        [InlineData("context length of 131,072 exceeded", 131072)]
        [InlineData("Input exceeds the maximum of 32768 tokens", 32768)]
        [InlineData("max_model_len (65536) exceeded", 65536)]
        [InlineData("rate limited, retry later", null)]
        [InlineData(null, null)]
        public void ParsesProviderStatedLimits(string? message, int? expected) =>
            Assert.Equal(expected, ModelContextRegistry.ParseStatedLimit(message));

        [Theory]
        [InlineData("This model's maximum context length is 8192 tokens", true)]
        [InlineData("Prompt is too long", true)]
        [InlineData("context_length_exceeded", true)]
        [InlineData("429 Too Many Requests", false)]
        public void RecognisesOverflowErrors(string message, bool expected) =>
            Assert.Equal(expected, ModelContextRegistry.LooksLikeContextOverflow(message));

        [Fact]
        public void FamilyMatch_PrefersExactThenSmallestPrefixedWindow()
        {
            Assert.Equal("qwen/qwen3.8", ModelContextRegistry.MatchFamily("Qwen3.8", OpenRouterCatalog)!.Id);
            Assert.Equal("deepseek/deepseek-v4-flash", ModelContextRegistry.MatchFamily("DeepSeek-V4-Flash", OpenRouterCatalog)!.Id);
            var prefixed = new[] { new ModelContextRegistry.CatalogEntry("x/gpt-q-large", null, 400_000, null), new ModelContextRegistry.CatalogEntry("x/gpt-q-mini", null, 100_000, null) };
            Assert.Equal("x/gpt-q-mini", ModelContextRegistry.MatchFamily("gpt-q", prefixed)!.Id);
            Assert.Null(ModelContextRegistry.MatchFamily("unknown-model", OpenRouterCatalog));
        }

        [Fact]
        public void ParseCatalog_ReadsTheUsualFieldsAndTheSmallerTopProviderWindow()
        {
            const string json = """
            {"data":[
              {"id":"a/one","context_length":200000,"top_provider":{"context_length":150000,"max_completion_tokens":8000}},
              {"id":"b/two","max_model_len":32768},
              {"id":"c/three"}
            ]}
            """;
            var c = ModelContextRegistry.ParseCatalog(json);
            Assert.Equal(150_000, c[0].ContextWindowTokens);
            Assert.Equal(8_000, c[0].MaxOutputTokens);
            Assert.Equal(32_768, c[1].ContextWindowTokens);
            Assert.Equal(0, c[2].ContextWindowTokens);
        }

        [Fact]
        public async Task Resolution_OperatorStatedCapsTheOpenRouterUpperBoundForRouters()
        {
            var r = Registry(openRouter: OpenRouterCatalog, stated: 260_000);
            var info = await r.ResolveAsync("AIRouter", "Qwen3.8");
            Assert.Equal(260_000, info.ContextWindowTokens);
            Assert.Equal("operator-stated", info.Source);
        }

        [Fact]
        public async Task Resolution_FallsBackThroughTheChain()
        {
            Assert.Equal("openrouter-family", (await Registry(openRouter: OpenRouterCatalog).ResolveAsync("AIRouter", "Qwen3.8")).Source);
            Assert.Equal("operator-stated", (await Registry(stated: 260_000).ResolveAsync("AIRouter", "Qwen3.8")).Source);
            var dflt = await Registry().ResolveAsync("AIRouter", "Qwen3.8");
            Assert.Equal("default", dflt.Source);
            Assert.Equal(ModelContextRegistry.DefaultContextWindowTokens, dflt.ContextWindowTokens);

            var own = new[] { new ModelContextRegistry.CatalogEntry("Qwen3.8", null, 250_000, 20_000) };
            var fromProvider = await Registry(openRouter: OpenRouterCatalog, provider: own, stated: 260_000).ResolveAsync("AIRouter", "Qwen3.8");
            Assert.Equal(("provider-catalog", 250_000, 20_000), (fromProvider.Source, fromProvider.ContextWindowTokens, fromProvider.MaxOutputTokens));

            var overridden = await Registry(openRouter: OpenRouterCatalog, overrides: new() { ["Qwen3.8"] = 100_000 }).ResolveAsync("AIRouter", "Qwen3.8");
            Assert.Equal(("override", 100_000), (overridden.Source, overridden.ContextWindowTokens));
        }

        [Fact]
        public async Task LearnedProviderLimit_WinsOverCataloguesAndIsNeverRaisedByLaterErrors()
        {
            var r = Registry(openRouter: OpenRouterCatalog, stated: 260_000);
            Assert.Equal(200_000, r.RecordOverflow("AIRouter", "Qwen3.8", "maximum context length is 200000 tokens"));
            r.RecordOverflow("AIRouter", "Qwen3.8", "maximum context length is 240000 tokens");
            var info = await r.ResolveAsync("AIRouter", "Qwen3.8");
            Assert.Equal(("learned:provider-stated", 200_000), (info.Source, info.ContextWindowTokens));
            Assert.Null(r.RecordOverflow("AIRouter", "Qwen3.8", "something unrelated"));
        }

        [Fact]
        public async Task WindowIsNeverBelowAPromptThatAlreadySucceeded()
        {
            var r = Registry(); // default 32K
            r.RecordSuccess("AIRouter", "m", promptTokens: 90_000, requestBytes: 400_000);
            var info = await r.ResolveAsync("AIRouter", "m");
            Assert.True(info.ContextWindowTokens >= 90_000 + info.MaxOutputTokens);
            Assert.Equal(90_000, info.LargestSuccessfulPromptTokens);
        }

        [Fact]
        public async Task RequestBodyCeiling_IsLearnedFromRefusals()
        {
            var r = Registry(stated: 260_000);
            Assert.Null((await r.ResolveAsync("AIRouter", "m")).RequestByteLimit);
            r.RecordSuccess("AIRouter", "m", 1000, 3_900_000);
            r.RecordRequestTooLarge("AIRouter", "m", 4_900_000);
            Assert.Equal(4_410_000, (await r.ResolveAsync("AIRouter", "m")).RequestByteLimit);
        }

        [Fact]
        public async Task LearnedLimits_PersistAcrossInstances()
        {
            string path = Path.Combine(Path.GetTempPath(), "model-limits-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Registry(persist: path).RecordOverflow("AIRouter", "m", "maximum context length is 123456 tokens");
                var info = await Registry(persist: path).ResolveAsync("AIRouter", "m");
                Assert.Equal(123_456, info.ContextWindowTokens);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void GatewayHelpers_ExtractJsonAndClassifyErrors()
        {
            Assert.Equal("{\"a\":{\"b\":\"}\"}}", Services.KliveLLM.KliveLLM.ExtractJsonObject("Sure! {\"a\":{\"b\":\"}\"}} trailing"));
            Assert.Null(Services.KliveLLM.KliveLLM.ExtractJsonObject("no json here"));
            Assert.Equal(LlmErrorKind.ContextOverflow, Services.KliveLLM.KliveLLM.ClassifyGatewayError("maximum context length is 8192 tokens"));
            Assert.Equal(LlmErrorKind.RequestTooLarge, Services.KliveLLM.KliveLLM.ClassifyGatewayError("HTTP 413 Request Entity Too Large"));
            Assert.Equal(LlmErrorKind.RateLimited, Services.KliveLLM.KliveLLM.ClassifyGatewayError("429 rate limit"));
            Assert.Equal(LlmErrorKind.Unauthorized, Services.KliveLLM.KliveLLM.ClassifyGatewayError("401 unauthorized"));
        }
    }
}
