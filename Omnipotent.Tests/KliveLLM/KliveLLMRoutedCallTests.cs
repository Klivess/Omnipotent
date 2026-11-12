using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.KliveLLM.Telemetry;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// End to end on the test seam: a caller's call is planned from the routing document, sent to its
    /// router, falls back when that router fails on its own side, and leaves a ledger row that the
    /// queries read back. And the request bytes a call sends do not change when routing is set to the
    /// router it would have used anyway (the prefix cache depends on that).
    /// </summary>
    public class KliveLLMRoutedCallTests : IAsyncLifetime
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "klivellm-routed-" + Guid.NewGuid().ToString("N"));
        private LlmTelemetry ledger = null!;
        private readonly List<(string Host, string Body)> requests = new();
        private readonly HashSet<string> failingHosts = new(StringComparer.OrdinalIgnoreCase);

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(dir);
            ledger = new LlmTelemetry(new LlmTelemetryDb(Path.Combine(dir, "klivellm.db")),
                new LlmTelemetryOptions { FlushInterval = TimeSpan.FromMilliseconds(40) });
            ledger.Start();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await ledger.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string, HttpResponseMessage> answer;
            public Handler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) => this.answer = answer;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                return answer(request, body);
            }
        }

        private LlmService Service(LlmRoutingDocument doc, LlmService.LLMProvider provider = LlmService.LLMProvider.OpenRouter)
        {
            var http = new HttpClient(new Handler((request, body) =>
            {
                string host = request.RequestUri!.Host;
                lock (requests) requests.Add((host, body));
                if (failingHosts.Contains(host))
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"down\"}}") };
                string model = Newtonsoft.Json.Linq.JObject.Parse(body).Value<string>("model") ?? "";
                string reply = "{\"id\":\"gen-1\",\"model\":\"" + model + "\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],"
                    + "\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":30,\"total_tokens\":1230,\"prompt_tokens_details\":{\"cached_tokens\":1000}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
            }));
            var service = new LlmService(http)
            {
                Routing = LlmRoutingStore.InMemory(doc),
                ActiveProviderOverride = provider,
                Ledger = ledger,
            };
            service.Queries = new LlmQueryEngine(ledger.Db, () => service.Prices, service.BillingOf, service.RouterNameOf);
            service.RouterConfigurationOverride = router => router.IsLocal ? null : new LlmService.RemoteLLMProviderConfiguration(
                router.Kind == "openrouter" ? LlmService.LLMProvider.OpenRouter : LlmService.LLMProvider.CustomOpenAI,
                router.Name, $"https://{router.Id}.test/v1/chat/completions", "key-" + router.Id,
                router.DefaultModel ?? router.Id + "-default");
            return service;
        }

        private static LlmRouterDefinition Router(string id, LlmBilling billing) => new()
        {
            Id = id, Name = id, BaseUrl = $"https://{id}.test/v1", DefaultModel = id + "-model", Billing = billing,
        };

        private static void Seed(LlmService service, string session, LlmCaller caller)
        {
            service.StartToolSession(session, "SYSTEM", caller);
            // No receipt stamp, so two runs of the same conversation send the same bytes.
            service.AppendCacheStableWakeSeedToToolSession(session, "Do the thing.");
        }

        [Fact]
        public async Task AFailingRouter_FallsBack_ThenIsSkippedUntilTriedAgain_AndTheLedgerSaysSo()
        {
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(Router("alpha", LlmBilling.PerToken));
            doc.Routers.Add(Router("beta", LlmBilling.PerToken));
            doc.Lanes["Stratum/engineer"] = new LlmRoutePolicy { Router = "alpha", Fallbacks = new() { new() { Router = "beta" } } };
            failingHosts.Add("alpha.test");
            var service = Service(doc);

            Seed(service, "stratum-engineer-p-1", LlmConsumerCatalog.Callers.StratumEngineer);
            var first = await service.QueryToolSessionAsync("stratum-engineer-p-1", new List<HFWrapper.HFTool>(), maxTokensOverride: 50);
            Assert.True(first.Success);
            Assert.Equal("beta", first.RouterId);
            Assert.Equal("alpha", first.FallbackFrom);
            Assert.Equal(4, first.Attempts);
            Assert.Equal(3, requests.Count(r => r.Host == "alpha.test"));
            Assert.Equal("beta-model", first.Model);

            // The circuit now sends the lane straight to its fallback: no retry chain on the dead router.
            Seed(service, "stratum-engineer-p-2", LlmConsumerCatalog.Callers.StratumEngineer);
            var second = await service.QueryToolSessionAsync("stratum-engineer-p-2", new List<HFWrapper.HFTool>(), maxTokensOverride: 50);
            Assert.Equal("beta", second.RouterId);
            Assert.Equal(3, requests.Count(r => r.Host == "alpha.test"));

            await ledger.FlushAsync();
            var calls = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = 0, ToMs = long.MaxValue, Services = new[] { "Stratum" } }, out _);
            Assert.Equal(2, calls.Count);
            var fallback = calls.Single(c => c.SessionId == "stratum-engineer-p-1");
            Assert.True(fallback.Has(LlmCallFlags.Fallback));
            Assert.Equal("alpha", fallback.FallbackFrom);
            Assert.Equal("beta", fallback.Router);
            Assert.Equal(1200, fallback.PromptTokens);
            Assert.Equal(1000, fallback.CachedTokens);
            Assert.Equal(new[] { "alpha", "alpha", "alpha", "beta" }, fallback.AttemptLog!.Select(a => a.Router));
            Assert.Equal(3, fallback.AttemptLog!.Count(a => a.Outcome == LlmOutcome.Unavailable));
        }

        [Fact]
        public async Task AFlatFeeLane_NeverFallsOntoAPaidRouter()
        {
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(Router("flat", LlmBilling.Flat));
            doc.Routers.Add(Router("paid", LlmBilling.PerToken));
            doc.Lanes["Stratum/engineer"] = new LlmRoutePolicy { Router = "flat", Fallbacks = new() { new() { Router = "paid" } } };
            failingHosts.Add("flat.test");
            var service = Service(doc);
            Seed(service, "stratum-engineer-p-3", LlmConsumerCatalog.Callers.StratumEngineer);

            var error = await Assert.ThrowsAsync<RemoteLLMException>(() =>
                service.QueryToolSessionAsync("stratum-engineer-p-3", new List<HFWrapper.HFTool>(), maxTokensOverride: 50));
            Assert.Equal(RemoteLLMFailureKind.ProviderUnavailable, error.Kind);
            Assert.DoesNotContain(requests, r => r.Host == "paid.test");
        }

        [Fact]
        public async Task APausedOrNeverSpendingLane_IsRefusedBeforeAnythingIsSent()
        {
            var doc = new LlmRoutingDocument();
            doc.Services["Stratum"] = new LlmRoutePolicy { Paused = true, PausedReason = "maintenance" };
            var service = Service(doc);
            Seed(service, "stratum-engineer-p-4", LlmConsumerCatalog.Callers.StratumEngineer);
            var paused = await Assert.ThrowsAsync<RemoteLLMException>(() =>
                service.QueryToolSessionAsync("stratum-engineer-p-4", new List<HFWrapper.HFTool>(), maxTokensOverride: 50));
            Assert.Equal(RemoteLLMFailureKind.Paused, paused.Kind);
            Assert.False(paused.IsRetryable);

            // OmniBlox may never spend per token: on OpenRouter it is refused, with the reason.
            var (ok, reason) = await service.CheckRouteAsync(LlmConsumerCatalog.Callers.OmniBloxAgents);
            Assert.False(ok);
            Assert.Contains("per token", reason);
            Assert.Empty(requests);

            await ledger.FlushAsync();
            var blocked = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = 0, ToMs = long.MaxValue, Outcomes = new[] { LlmOutcome.Blocked } }, out _);
            Assert.Single(blocked);
        }

        [Fact]
        public async Task RoutingALaneToTheRouterItAlreadyUses_SendsTheSameBytes_AndAPinChangesOnlyTheModel()
        {
            async Task<string> Run(LlmRoutingDocument doc, string? modelOverride)
            {
                requests.Clear();
                var service = Service(doc);
                Seed(service, "projects-commander-p", LlmConsumerCatalog.Callers.ProjectsCommander.For("p"));
                var response = await service.QueryToolSessionAsync("projects-commander-p", new List<HFWrapper.HFTool>(), maxTokensOverride: 64,
                    modelOverride: modelOverride, modelRoutes: new[] { "anthropic/claude-sonnet-4.5", "openai/gpt-5" });
                Assert.True(response.Success);
                return Assert.Single(requests).Body;
            }

            string unrouted = await Run(new LlmRoutingDocument(), null);
            var explicitDoc = new LlmRoutingDocument();
            explicitDoc.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter };
            Assert.Equal(unrouted, await Run(explicitDoc, null));
            Assert.Contains("\"model\":\"anthropic/claude-sonnet-4.5\"", unrouted);
            Assert.Contains("\"models\":[\"openai/gpt-5\"]", unrouted);

            var pinned = new LlmRoutingDocument();
            pinned.Lanes["Projects/commander"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter, Model = "openai/gpt-5-mini", Pin = true };
            string pinnedBody = await Run(pinned, null);
            Assert.Contains("\"model\":\"openai/gpt-5-mini\"", pinnedBody);
            Assert.DoesNotContain("\"models\"", pinnedBody);
            Assert.Equal(unrouted.Replace("\"model\":\"anthropic/claude-sonnet-4.5\"", "\"model\":\"openai/gpt-5-mini\"").Replace(",\"models\":[\"openai/gpt-5\"]", ""), pinnedBody);
        }

        [Fact]
        public async Task ACallersOwnModel_ThatItsRouterDoesNotServe_IsRedirected()
        {
            var doc = new LlmRoutingDocument();
            doc.Services["Stratum"] = new LlmRoutePolicy { Router = LlmRouterIds.OpenRouter, Model = "openai/gpt-5" };
            var service = Service(doc);
            Seed(service, "stratum-engineer-p-5", LlmConsumerCatalog.Callers.StratumEngineer);
            // "Qwen3.8" is AIRouter's name for a model; OpenRouter would refuse it.
            var response = await service.QueryToolSessionAsync("stratum-engineer-p-5", new List<HFWrapper.HFTool>(), maxTokensOverride: 50, modelOverride: "Qwen3.8");
            Assert.True(response.Success);
            Assert.Contains("\"model\":\"openai/gpt-5\"", Assert.Single(requests).Body);
            await ledger.FlushAsync();
            var call = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = 0, ToMs = long.MaxValue }, out _).Single();
            Assert.True(call.Has(LlmCallFlags.Redirected));
            Assert.Equal("Qwen3.8", call.ModelRequested);
        }
    }
}
