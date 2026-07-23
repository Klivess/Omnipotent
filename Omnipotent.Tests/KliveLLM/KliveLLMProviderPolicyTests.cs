using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    public class KliveLLMProviderPolicyTests
    {
        [Fact]
        public void AffordableLimit_ParsesStructuredOpenRouterMessageBeforeNestedRouteErrors()
        {
            string body = """
                {
                  "error": {
                    "message": "This request requires more credits, or fewer max_tokens. You requested up to 65536 tokens, but can only afford 32949.",
                    "code": 402,
                    "metadata": {
                      "previous_errors": [
                        { "message": "You requested up to 65536 tokens, but can only afford 16474." }
                      ]
                    }
                  }
                }
                """;

            Assert.Equal(32_949, LlmService.ParseAffordableMaxTokens(body));
            Assert.Equal(29_654, LlmService.CalculateAffordableRetryMaxTokens(body, requestedMaxTokens: null));
        }

        [Fact]
        public void AffordableLimit_ParsesNamedFieldAndPlainText()
        {
            Assert.Equal(12_345, LlmService.ParseAffordableMaxTokens("{\"error\":{\"affordable_max_tokens\":12345}}"));
            Assert.Equal(31_532, LlmService.ParseAffordableMaxTokens(
                "Payment required: requested too much; can only afford 31,532 tokens."));
        }

        [Fact]
        public void AffordableRetry_MustActuallyReduceAnExplicitRequest()
        {
            const string body = "can only afford 5000";
            Assert.Equal(4_500, LlmService.CalculateAffordableRetryMaxTokens(body, requestedMaxTokens: 8_192));
            Assert.Null(LlmService.CalculateAffordableRetryMaxTokens(body, requestedMaxTokens: 4_000));
            Assert.Null(LlmService.CalculateAffordableRetryMaxTokens("unrelated payment failure", requestedMaxTokens: null));
        }

        [Fact]
        public async Task OpenRouter402_RetriesOnceWithNinetyPercentAffordableLimit()
        {
            var handler = new RecordingHandler(
                _ => JsonResponse(HttpStatusCode.PaymentRequired, PaymentBody(16_474)),
                _ => JsonResponse(HttpStatusCode.OK, SuccessBody));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            var response = await service.SendPayloadWithRetryAsync(OpenRouter(), Payload(maxTokens: null));

            Assert.Single(response.choices);
            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Null(JObject.Parse(handler.RequestBodies[0])["max_tokens"]);
            Assert.Equal(14_826, JObject.Parse(handler.RequestBodies[1])["max_tokens"]!.Value<int>());
        }

        [Fact]
        public async Task OpenRouter402_SecondFailureDoesNotLoopAndIsTyped()
        {
            var handler = new RecordingHandler(
                _ => JsonResponse(HttpStatusCode.PaymentRequired, PaymentBody(10_000)),
                _ => JsonResponse(HttpStatusCode.PaymentRequired, PaymentBody(8_000)),
                _ => JsonResponse(HttpStatusCode.OK, SuccessBody));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            var error = await Assert.ThrowsAsync<RemoteLLMException>(
                () => service.SendPayloadWithRetryAsync(OpenRouter(), Payload(maxTokens: null)));

            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Equal(RemoteLLMFailureKind.InsufficientProviderCredit, error.Kind);
            Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
            Assert.Equal(9_000, error.RequestedMaxTokens);
            Assert.Equal(8_000, error.AffordableMaxTokens);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public async Task NonOpenRouter402_FailsFastWithoutAffordableRetry()
        {
            var handler = new RecordingHandler(
                _ => JsonResponse(HttpStatusCode.PaymentRequired, PaymentBody(10_000)),
                _ => JsonResponse(HttpStatusCode.OK, SuccessBody));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);
            var provider = new LlmService.RemoteLLMProviderConfiguration(
                LlmService.LLMProvider.HuggingFace,
                "HuggingFace",
                "https://provider.test/v1/chat/completions",
                "test-token",
                "test/model");

            var error = await Assert.ThrowsAsync<RemoteLLMException>(
                () => service.SendPayloadWithRetryAsync(provider, Payload(maxTokens: null)));

            Assert.Single(handler.RequestBodies);
            Assert.Equal(RemoteLLMFailureKind.InsufficientProviderCredit, error.Kind);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, RemoteLLMFailureKind.Authentication)]
        [InlineData(HttpStatusCode.Forbidden, RemoteLLMFailureKind.Authentication)]
        [InlineData(HttpStatusCode.NotFound, RemoteLLMFailureKind.ModelUnavailable)]
        [InlineData(HttpStatusCode.RequestTimeout, RemoteLLMFailureKind.Timeout)]
        [InlineData(HttpStatusCode.TooManyRequests, RemoteLLMFailureKind.RateLimited)]
        [InlineData(HttpStatusCode.BadGateway, RemoteLLMFailureKind.ProviderUnavailable)]
        [InlineData(HttpStatusCode.BadRequest, RemoteLLMFailureKind.InvalidRequest)]
        public void HttpFailures_AreClassified(HttpStatusCode status, RemoteLLMFailureKind expected)
        {
            Assert.Equal(expected, LlmService.ClassifyRemoteFailure(status, "ordinary provider error"));
        }

        [Fact]
        public void WrappedUpstreamRateLimit_IsClassifiedFromBody()
        {
            Assert.Equal(RemoteLLMFailureKind.RateLimited,
                LlmService.ClassifyRemoteFailure(HttpStatusCode.BadRequest,
                    "{\"error\":{\"code\":429,\"message\":\"temporarily rate-limited upstream\"}}"));
        }

        [Fact]
        public void FallbackHistoryRateLimit_DoesNotOverrideFinalStructuredError()
        {
            const string body = """
                { "error": {
                  "code": 400,
                  "message": "Tool schema is invalid",
                  "metadata": {
                    "error_type": "invalid_request",
                    "previous_errors": [{ "code": 429, "message": "temporarily rate limited upstream" }]
                  }
                }}
                """;

            Assert.Equal(RemoteLLMFailureKind.InvalidRequest,
                LlmService.ClassifyRemoteFailure(HttpStatusCode.BadRequest, body));
        }

        [Fact]
        public async Task LongRateLimitRetryAfter_DefersWithoutReplayingTheFallbackChain()
        {
            var handler = new RecordingHandler(_ =>
            {
                var response = JsonResponse(HttpStatusCode.TooManyRequests,
                    "{\"error\":{\"code\":429,\"message\":\"rate limited\"}}");
                response.Headers.TryAddWithoutValidation("Retry-After", "60");
                return response;
            });
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            var error = await Assert.ThrowsAsync<RemoteLLMException>(
                () => service.SendPayloadWithRetryAsync(OpenRouter(), Payload(maxTokens: null)));

            Assert.Single(handler.RequestBodies);
            Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter!.Value);
        }

        [Fact]
        public void RetryAfter_IsCappedToBoundedInWakePolicy()
        {
            Assert.Equal(TimeSpan.FromSeconds(7), LlmService.BoundProviderRetryAfter(TimeSpan.FromSeconds(7)));
            Assert.Equal(TimeSpan.FromSeconds(20), LlmService.BoundProviderRetryAfter(TimeSpan.FromMinutes(10)));
            Assert.Equal(TimeSpan.Zero, LlmService.BoundProviderRetryAfter(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void ModelFallback_SendsOnlyBackupRoutesAfterThePrimaryModel()
        {
            // Multiple routes → the whole ordered list becomes OpenRouter's native `models` fallback set.
            var multi = Payload(null);
            multi.model = "anthropic/claude-sonnet-4.5";
            LlmService.ApplyModelFallback(ref multi, OpenRouter(), new[] { "anthropic/claude-sonnet-4.5", "openai/gpt-4.1" });
            Assert.Equal(new[] { "openai/gpt-4.1" }, multi.models);

            // A single route needs no fallback — `models` stays unset so the plain `model` field is used.
            var single = Payload(null);
            single.model = "anthropic/claude-sonnet-4.5";
            LlmService.ApplyModelFallback(ref single, OpenRouter(), new[] { "anthropic/claude-sonnet-4.5" });
            Assert.Null(single.models);

            // No routes supplied at all.
            var none = Payload(null);
            LlmService.ApplyModelFallback(ref none, OpenRouter(), null);
            Assert.Null(none.models);
        }

        [Fact]
        public void ModelFallback_DedupesAndTrims_AndIgnoresNonOpenRouterProviders()
        {
            var deduped = Payload(null);
            deduped.model = "a/b";
            LlmService.ApplyModelFallback(ref deduped, OpenRouter(),
                new[] { " a/b ", "a/b", "", "c/d", "A/B" });
            Assert.Equal(new[] { "c/d" }, deduped.models);

            // Only OpenRouter understands the parameter; other providers never receive it.
            var hf = Payload(null);
            LlmService.ApplyModelFallback(ref hf, HuggingFace(), new[] { "a/b", "c/d" });
            Assert.Null(hf.models);

            // AgentRouter is a plain OpenAI-compatible router: the Projects route list still selects the
            // primary model, but the backup entries must not be sent as an unknown parameter.
            var agentRouter = Payload(null);
            LlmService.ApplyModelFallback(ref agentRouter, AgentRouter(), new[] { "a/b", "c/d" });
            Assert.Null(agentRouter.models);
        }

        [Fact]
        public void ProviderDropdown_SelectsTheRouterEveryRemoteCallSiteUses()
        {
            Assert.Equal(LlmService.LLMProvider.Local, LlmService.ParseProvider("Local"));
            Assert.Equal(LlmService.LLMProvider.OpenRouter, LlmService.ParseProvider("OpenRouter"));
            Assert.Equal(LlmService.LLMProvider.AgentRouter, LlmService.ParseProvider("AgentRouter"));
            Assert.Equal(LlmService.LLMProvider.AgentRouter, LlmService.ParseProvider("agentrouter"));
            Assert.Equal(LlmService.LLMProvider.HuggingFace, LlmService.ParseProvider("HuggingFace"));
            // Unknown/absent settings keep the historical HuggingFace default.
            Assert.Equal(LlmService.LLMProvider.HuggingFace, LlmService.ParseProvider("SomethingElse"));
            Assert.Equal(LlmService.LLMProvider.HuggingFace, LlmService.ParseProvider(null));
        }

        [Fact]
        public async Task AgentRouter402_FailsFastWithoutTheOpenRouterAffordableRetry()
        {
            var handler = new RecordingHandler(
                _ => JsonResponse(HttpStatusCode.PaymentRequired, PaymentBody(10_000)),
                _ => JsonResponse(HttpStatusCode.OK, SuccessBody));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            var error = await Assert.ThrowsAsync<RemoteLLMException>(
                () => service.SendPayloadWithRetryAsync(AgentRouter(), Payload(maxTokens: null)));

            Assert.Single(handler.RequestBodies);
            Assert.Equal(RemoteLLMFailureKind.InsufficientProviderCredit, error.Kind);
            Assert.Equal("AgentRouter", error.Provider);
        }

        [Fact]
        public async Task AgentRouter_PostsToItsOwnEndpointWithItsOwnKey()
        {
            var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, SuccessBody));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            await service.SendPayloadWithRetryAsync(AgentRouter(), Payload(maxTokens: null));

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://agentrouter.org/v1/chat/completions", request.Uri);
            Assert.Equal("agent-router-token", request.AuthParameter);
            // OpenRouter-only attribution headers must not leak to a different router.
            Assert.DoesNotContain("HTTP-Referer", request.HeaderNames);
            Assert.DoesNotContain("X-Title", request.HeaderNames);
        }

        [Fact]
        public void ModelFallback_RespectsOpenRouterThreeBackupMaximum()
        {
            var payload = Payload(maxTokens: null);
            payload.model = "a/one";
            LlmService.ApplyModelFallback(ref payload, OpenRouter(),
                new[] { "a/one", "b/two", "c/three", "d/four", "e/five" });

            Assert.Equal(new[] { "b/two", "c/three", "d/four" }, payload.models);
        }

        [Fact]
        public void ModelFallback_RotatesBackupsAfterAPinnedSuccessfulRoute()
        {
            var payload = Payload(maxTokens: null);
            payload.model = "b/two";
            LlmService.ApplyModelFallback(ref payload, OpenRouter(),
                new[] { "a/one", "b/two", "c/three" });

            Assert.Equal(new[] { "c/three", "a/one" }, payload.models);
        }

        private static LlmService.RemoteLLMProviderConfiguration OpenRouter() => new(
            LlmService.LLMProvider.OpenRouter,
            "OpenRouter",
            "https://provider.test/v1/chat/completions",
            "test-token",
            "test/model");

        private static LlmService.RemoteLLMProviderConfiguration HuggingFace() => new(
            LlmService.LLMProvider.HuggingFace,
            "HuggingFace",
            "https://provider.test/v1/chat/completions",
            "test-token",
            "test/model");

        private static LlmService.RemoteLLMProviderConfiguration AgentRouter() => new(
            LlmService.LLMProvider.AgentRouter,
            "AgentRouter",
            "https://agentrouter.org/v1/chat/completions",
            "agent-router-token",
            "gpt-4.1-mini");

        private static HFWrapper.HFLLMInferenceRequest Payload(int? maxTokens) => new()
        {
            model = "test/model",
            stream = false,
            max_tokens = maxTokens,
            messages = new[] { new HFWrapper.HFMessage { role = "user", content = "hello" } },
        };

        private static string PaymentBody(int affordable) =>
            "{\"error\":{\"message\":\"This request requires more credits, or fewer max_tokens. " +
            $"You requested up to 65536 tokens, but can only afford {affordable}.\",\"code\":402}}";

        private const string SuccessBody =
            "{\"id\":\"generation-1\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private sealed record RequestSnapshot(string Uri, string? AuthParameter, HashSet<string> HeaderNames);

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses;

            public RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                this.responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
            }

            public List<string> RequestBodies { get; } = new();

            /// <summary>Snapshot of each outbound request, captured before the caller disposes it.</summary>
            public List<RequestSnapshot> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestBodies.Add(request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(new RequestSnapshot(
                    request.RequestUri?.ToString() ?? "",
                    request.Headers.Authorization?.Parameter,
                    request.Headers.Select(h => h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)));
                if (responses.Count == 0) throw new InvalidOperationException("Unexpected provider request.");
                return responses.Dequeue()(request);
            }
        }
    }
}
