using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.KliveLLM.Telemetry;
using Omnipotent.Services.Omniscience;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.Omniscience
{
    /// <summary>
    /// Omniscience used to give each call its own small max_tokens, and every profiling call in the
    /// KliveLLM ledger stopped at exactly 2,048. These pin the replacement: one high ceiling, the
    /// ledger lane still named from the real call site, and a reply cut off by the ceiling reported
    /// as failed instead of saved as finished.
    /// </summary>
    public class OmniscienceLlmTests : IAsyncLifetime
    {
        private const string ProfilerFile = @"C:\src\Omnipotent\Services\Omniscience\Profiling\PersonalityProfiler.cs";

        private readonly string dir = Path.Combine(Path.GetTempPath(), "omniscience-llm-" + Guid.NewGuid().ToString("N"));
        private LlmTelemetry ledger = null!;
        private readonly List<string> bodies = new();
        private string finishReason = "stop";

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
            private readonly Func<string, HttpResponseMessage> answer;
            public Handler(Func<string, HttpResponseMessage> answer) => this.answer = answer;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => answer(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        }

        private LlmService Service()
        {
            var http = new HttpClient(new Handler(body =>
            {
                lock (bodies) bodies.Add(body);
                string reply = "{\"id\":\"gen-1\",\"model\":\"Qwen3.8\",\"choices\":[{\"index\":0,\"finish_reason\":\"" + finishReason + "\","
                    + "\"message\":{\"role\":\"assistant\",\"content\":\"## Communication Style\\n- terse\"}}],"
                    + "\"usage\":{\"prompt_tokens\":80000,\"completion_tokens\":2048,\"total_tokens\":82048}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
            }));
            var doc = new LlmRoutingDocument();
            doc.Routers.Add(new LlmRouterDefinition
            {
                Id = "flat", Name = "flat", BaseUrl = "https://flat.test/v1", DefaultModel = "Qwen3.8", Billing = LlmBilling.Flat,
            });
            doc.Services["Omniscience"] = new LlmRoutePolicy { Router = "flat" };
            var service = new LlmService(http)
            {
                Routing = LlmRoutingStore.InMemory(doc),
                ActiveProviderOverride = LlmService.LLMProvider.CustomOpenAI,
                Ledger = ledger,
            };
            service.Queries = new LlmQueryEngine(ledger.Db, () => service.Prices, service.BillingOf, service.RouterNameOf);
            service.RouterConfigurationOverride = router => router.IsLocal ? null : new LlmService.RemoteLLMProviderConfiguration(
                LlmService.LLMProvider.CustomOpenAI, router.Name, $"https://{router.Id}.test/v1/chat/completions", "key-" + router.Id,
                router.DefaultModel ?? router.Id + "-default");
            return service;
        }

        [Fact]
        public async Task AFinishedReply_AsksForTheCeiling_AndKeepsTheCallersLane()
        {
            var response = await OmniscienceLlm.AskAsync(Service(), "profile this person", "You are an analyst.",
                useFreeModel: true, callerFile: ProfilerFile);

            Assert.True(response.Success, response.ErrorMessage);
            Assert.Equal("stop", response.FinishReason);
            Assert.Equal(OmniscienceLlm.OutputCeiling, JObject.Parse(Assert.Single(bodies)).Value<int>("max_tokens"));

            await ledger.FlushAsync();
            var call = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = 0, ToMs = long.MaxValue }, out _).Single();
            Assert.Equal("Omniscience", call.Service);
            Assert.Equal("profiling", call.Lane);
            Assert.False(call.Has(LlmCallFlags.Truncated));
        }

        [Fact]
        public async Task AReplyCutOffByTheCeiling_IsAFailure_NotAFinishedDossier()
        {
            finishReason = "length";
            var response = await OmniscienceLlm.AskAsync(Service(), "profile this person", "You are an analyst.",
                useFreeModel: true, callerFile: ProfilerFile);

            Assert.False(response.Success);
            Assert.Contains("cut off", response.ErrorMessage);
            Assert.True(OmniscienceLlm.WasCutOff(response));

            await ledger.FlushAsync();
            Assert.True(ledger.Db.QueryCalls(new LlmCallFilter { FromMs = 0, ToMs = long.MaxValue }, out _).Single().Has(LlmCallFlags.Truncated));
        }

        /// <summary>A call site that sets its own max_tokens again brings the 2,048-token cut-off back.</summary>
        [Fact]
        public void NoOmniscienceCallSite_SetsItsOwnOutputLimit()
        {
            string folder = Path.Combine(RepoRoot(), "Omnipotent", "Services", "Omniscience");
            var direct = new Regex(@"\.QueryLLM\(|maxTokensOverride\s*:");
            var offenders = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("OmniscienceLlm.cs", StringComparison.OrdinalIgnoreCase))
                .Where(f => direct.IsMatch(File.ReadAllText(f)))
                .Select(f => Path.GetRelativePath(folder, f))
                .ToList();
            Assert.True(Directory.Exists(folder), "Omniscience source folder not found: " + folder);
            Assert.True(offenders.Count == 0, "Call KliveLLM through OmniscienceLlm.AskAsync instead: " + string.Join(", ", offenders));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
