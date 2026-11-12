using System.Collections.Specialized;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.KliveLLM.Telemetry;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>The call ledger: aggregates that merge exactly, a writer that never loses a call, queries
    /// that answer from the right tier, costs computed from the price book, and router health.</summary>
    public class KliveLLMLedgerTests : IAsyncLifetime
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "klivellm-ledger-" + Guid.NewGuid().ToString("N"));
        private LlmTelemetry ledger = null!;

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

        private static LlmCallRecord Call(long endedMs, string service = "Projects", string lane = "commander", string router = LlmRouterIds.AIRouter,
            string model = "Qwen3.8", string outcome = LlmOutcome.Ok, long prompt = 10_000, long cached = 8_000, long completion = 500,
            long providerMs = 2_000, long? ttftMs = 900, string billing = "flat", double? cost = null, string? subject = "p1") => new()
        {
            StartedMs = endedMs - providerMs - 100,
            EndedMs = endedMs,
            Service = service,
            Lane = lane,
            Subject = subject,
            Kind = "tool",
            Router = router,
            ModelRequested = model,
            ModelServed = model,
            Outcome = outcome,
            Attempts = 1,
            PromptTokens = prompt,
            CachedTokens = cached,
            CompletionTokens = completion,
            CacheMetrics = true,
            CostUsd = cost,
            Billing = billing,
            QueueMs = 100,
            ProviderMs = providerMs,
            TotalMs = providerMs + 100,
            TtftMs = ttftMs,
            Stream = ttftMs != null,
            CacheEpochTurn = 2,
            AttemptLog = new List<LlmAttempt> { new(router, outcome, outcome == LlmOutcome.Ok ? 200 : 503, 100, providerMs) },
        };

        [Fact]
        public void Aggregates_MergeExactly_AndSurviveTheirBinaryForm()
        {
            var a = new LlmAggregate();
            var b = new LlmAggregate();
            for (int i = 0; i < 50; i++) a.Add(Call(1_000 + i, providerMs: 1_000 + i * 10));
            for (int i = 0; i < 30; i++) b.Add(Call(2_000 + i, outcome: LlmOutcome.RateLimited, prompt: 0, cached: 0, completion: 0, ttftMs: null));
            var merged = new LlmAggregate();
            merged.Merge(a);
            merged.Merge(b);
            var copy = LlmAggregate.FromBytes(merged.ToBytes());

            Assert.Equal(80, copy.Calls);
            Assert.Equal(50, copy.Ok);
            Assert.Equal(30, copy.Metric("rateLimited"));
            Assert.Equal(500_000, copy.PromptTokens);
            Assert.Equal(80.0, copy.Metric("cacheHitPct")!.Value, 3);
            Assert.Equal(37.5, copy.Metric("errorPct")!.Value, 3);
            // Percentiles come from the merged histogram: within its ±9% bucket resolution.
            double p50 = copy.Metric("p50ProviderMs")!.Value;
            Assert.InRange(p50, 1_000 * 0.9, 1_490 * 1.1);
            Assert.Equal(merged.ProviderHist.Count, copy.ProviderHist.Count);
            Assert.Null(new LlmAggregate().Metric("p95TotalMs"));
        }

        [Fact]
        public async Task TheWriter_KeepsEveryCall_AndQueriesAnswerFromTheAggregates()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < 20; i++) ledger.Enqueue(Call(now - i * 60_000));
            ledger.Enqueue(Call(now - 5_000, service: "Stratum", lane: "engineer", router: LlmRouterIds.OpenRouter, model: "anthropic/claude-sonnet-4.5",
                billing: "per_token", cost: 0.25, subject: null));
            ledger.Enqueue(Call(now - 4_000, outcome: LlmOutcome.Timeout, prompt: 0, cached: 0, completion: 0));
            await ledger.FlushAsync();

            var calls = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = now - 86_400_000, ToMs = now + 60_000, Limit = 100 }, out bool capped);
            Assert.False(capped);
            Assert.Equal(22, calls.Count);
            Assert.True(calls.Zip(calls.Skip(1)).All(pair => pair.First.EndedMs >= pair.Second.EndedMs));
            var failed = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = now - 86_400_000, ToMs = now + 60_000, FailedOnly = true }, out _);
            Assert.Single(failed);
            var stratum = ledger.Db.QueryCalls(new LlmCallFilter { FromMs = now - 86_400_000, ToMs = now + 60_000, Services = new[] { "Stratum" } }, out _);
            Assert.Equal(0.25, Assert.Single(stratum).CostUsd);

            var prices = LlmPriceBook.InMemory();
            prices.Save(new[] { new LlmPriceEntry { Router = LlmRouterIds.AIRouter, Model = "Qwen3.8", InputPerMTok = 1, CachedInputPerMTok = 0.1, OutputPerMTok = 4 } });
            var engine = new LlmQueryEngine(ledger.Db, () => prices,
                id => id == LlmRouterIds.AIRouter ? LlmBilling.Flat : LlmBilling.PerToken, id => id);
            Assert.True(LlmQuerySpec.TryParse(new NameValueCollection { ["range"] = "1h", ["groupBy"] = "service", ["metrics"] = "calls,estCostUsd,listCostUsd,failed" },
                now, 0, ledger.Options, out var spec, out string? error), error);
            var json = Newtonsoft.Json.Linq.JObject.FromObject(engine.Series(spec));
            Assert.Equal(22, json["total"]!["calls"]!.ToObject<double>());
            Assert.Equal(1, json["total"]!["failed"]!.ToObject<double>());
            // AIRouter is flat: it costs nothing, but its list price is what the same tokens would cost per token.
            Assert.Equal(0.25, json["total"]!["estCostUsd"]!.ToObject<double>(), 6);
            double listPerCall = (2_000 * 1 + 8_000 * 0.1 + 500 * 4) / 1_000_000.0;
            Assert.Equal(0.25 + 20 * listPerCall, json["total"]!["listCostUsd"]!.ToObject<double>(), 6);
            var groups = json["groups"]!.Select(g => g["key"]!.ToString()).ToList();
            Assert.Equal(new[] { "Projects", "Stratum" }, groups);

            Assert.True(LlmQuerySpec.TryParse(new NameValueCollection { ["range"] = "24h", ["groupBy"] = "subject" }, now, 0, ledger.Options, out var subjects, out _));
            var subjectJson = Newtonsoft.Json.Linq.JObject.FromObject(engine.Breakdown(subjects));
            Assert.Equal("p1", subjectJson["groups"]![0]!["subject"]!.ToString());

            Assert.True(ledger.Usage("Projects").DayTokens > 0);
            Assert.Equal(0.25, ledger.Usage("Stratum/engineer").DayCostUsd, 6);
        }

        [Fact]
        public void Specs_PickTheirTierAndBucket()
        {
            long now = 1_762_862_400_000;
            var options = new LlmTelemetryOptions();
            Assert.True(LlmQuerySpec.TryParse(new NameValueCollection { ["range"] = "24h" }, now, 0, options, out var day, out _));
            Assert.Equal(600_000, day.BucketMs);
            Assert.Equal(LlmTier.M1, day.Tier);
            Assert.True(LlmQuerySpec.TryParse(new NameValueCollection { ["range"] = "90d" }, now, 0, options, out var quarter, out _));
            Assert.Equal(LlmTier.D1, quarter.Tier);
            // Minute rows are kept two weeks: a window reaching further back is answered by the hour.
            Assert.True(LlmQuerySpec.TryParse(new NameValueCollection { ["range"] = "30d", ["bucket"] = "5m" }, now, 0, options, out var month, out string? err), err);
            Assert.Equal(3_600_000, month.BucketMs);
            Assert.False(LlmQuerySpec.TryParse(new NameValueCollection { ["metrics"] = "nonsense" }, now, 0, options, out _, out _));
        }

        [Fact]
        public void Prices_SplitCacheReadsAndWritesTheWayProvidersBill()
        {
            var price = new LlmPriceEntry { InputPerMTok = 1, CachedInputPerMTok = 0.1, CacheWritePerMTok = 1.25, OutputPerMTok = 4 };
            // 1000 prompt: 400 read from cache, 600 fresh of which 100 written to it; 200 out.
            Assert.Equal((500 * 1 + 100 * 1.25 + 400 * 0.1 + 200 * 4) / 1e6, price.Cost(1000, 400, 100, 200), 12);
            // Blank cache prices bill as input.
            Assert.Equal((1000 * 1 + 200 * 4) / 1e6, new LlmPriceEntry { InputPerMTok = 1, OutputPerMTok = 4 }.Cost(1000, 400, 100, 200), 12);

            var book = LlmPriceBook.InMemory();
            book.ImportOpenRouterCatalogue(Newtonsoft.Json.Linq.JArray.Parse(
                "[{\"id\":\"openai/gpt-5\",\"pricing\":{\"prompt\":\"0.00000125\",\"completion\":\"0.00001\",\"input_cache_read\":\"0.000000125\"}}]"));
            var entry = book.Find(LlmRouterIds.OpenRouter, "openai/gpt-5");
            Assert.Equal(1.25, entry!.InputPerMTok!.Value, 6);
            Assert.Equal(0.125, entry.CachedInputPerMTok!.Value, 6);
            Assert.NotNull(book.Find(LlmRouterIds.OpenRouter, "openai/gpt-5-20250807"));
            book.Save(new[] { new LlmPriceEntry { Router = "*", Model = "Qwen3.8", InputPerMTok = 0.5, OutputPerMTok = 2 } });
            Assert.Equal(0.5, book.Find(LlmRouterIds.AIRouter, "Qwen3.8")!.InputPerMTok);
        }

        [Fact]
        public void RouterHealth_JudgesMinutesByWhoFailed()
        {
            long[] attempts(params (string Outcome, long N)[] counts)
            {
                var a = new long[LlmOutcome.All.Length];
                foreach (var (o, n) in counts) a[LlmOutcome.IndexOf(o)] += n;
                return a;
            }
            Assert.Equal(LlmRouterHealth.Up, LlmRouterHealth.Judge(attempts((LlmOutcome.Ok, 10)), 0, 0));
            Assert.Equal(LlmRouterHealth.Degraded, LlmRouterHealth.Judge(attempts((LlmOutcome.Ok, 9), (LlmOutcome.RateLimited, 1)), 0, 0));
            Assert.Equal(LlmRouterHealth.Down, LlmRouterHealth.Judge(attempts((LlmOutcome.Ok, 1), (LlmOutcome.Unavailable, 1)), 0, 0));
            // Our own bad request says nothing about the router.
            Assert.Equal(LlmRouterHealth.Up, LlmRouterHealth.Judge(attempts((LlmOutcome.Ok, 1), (LlmOutcome.Invalid, 5)), 0, 0));
            Assert.Equal(LlmRouterHealth.Down, LlmRouterHealth.Judge(attempts(), 0, 2));
            Assert.Equal(LlmRouterHealth.NoData, LlmRouterHealth.Judge(attempts(), 0, 0));

            var health = new LlmRouterHealth();
            long t = LlmTiers.Align(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), LlmTier.M1);
            health.Roll(t, (_, _) => null);
            var record = Call(t + 1_000);
            record.AttemptLog = new List<LlmAttempt> { new("alpha", LlmOutcome.Unavailable, 503, 0, 100), new("beta", LlmOutcome.Ok, 200, 0, 900) };
            health.NoteAttempts(record);
            var rows = health.Roll(t + 61_000, (_, _) => null);
            Assert.Contains(rows, r => r.Table == "router_1m" && r.Router == "alpha" && r.Json.Contains("\"State\":\"down\""));
            Assert.Contains(rows, r => r.Table == "router_1h" && r.Router == "beta");
            Assert.Equal(LlmRouterHealth.Up, health.Live().Single(l => l.Router == "beta").State);
        }

        [Fact]
        public void KeptText_DropsPicturesAndAudio()
        {
            string image = "data:image/png;base64," + new string('A', 4000);
            string scrubbed = LlmContentStore.Scrub($"{{\"url\":\"{image}\",\"input_audio\":{{\"data\":\"{new string('B', 4000)}\"}}}}")!;
            Assert.DoesNotContain("AAAA", scrubbed);
            Assert.DoesNotContain("BBBB", scrubbed);
            Assert.Contains("image/png removed", scrubbed);

            var store = new LlmContentStore(Path.Combine(dir, "content.db"));
            var record = Call(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            record.Id = 42;
            record.CapturedRequest = "{\"messages\":[\"hello " + image + "\"]}";
            record.CapturedResponse = "{\"content\":\"hi\"}";
            store.Write(record);
            var kept = store.Read(42);
            Assert.NotNull(kept);
            Assert.Contains("hello", kept!.Value.Request);
            Assert.DoesNotContain("AAAA", kept.Value.Request);
            Assert.Null(record.CapturedRequest);
        }

        [Fact]
        public void ProjectsJournalTurns_ImportAsCommanderAndWorkerCalls()
        {
            var turn = new ProjectTokenUsageRecord
            {
                ProjectID = "p9", Source = "subagent", Provider = "AIRouter", Model = "Qwen3.8", PromptTokens = 50_000, CachedPromptTokens = 45_000,
                CompletionTokens = 900, CacheMetricsAvailable = true, CostUsd = 0, CostBasis = "actual", RequestDurationMs = 4_000,
                QueueDurationMs = 500, ProviderDurationMs = 3_400, CacheEpochTurnIndex = 3,
            };
            var call = LlmProjectsImport.ToCall(turn, 1_700_000_000_000);
            Assert.Equal("Projects/subagent", call.Service + "/" + call.Lane);
            Assert.Equal("p9", call.Subject);
            Assert.Equal(LlmRouterIds.AIRouter, call.Router);
            Assert.Equal("flat", call.Billing);
            Assert.Null(call.CostUsd);
            Assert.True(call.Has(LlmCallFlags.Imported));

            var provisional = LlmProjectsImport.ToCall(new ProjectTokenUsageRecord { Source = "commander", Provider = "OpenRouter", CostUsd = 0.4, CostBasis = "provisional" }, 1);
            Assert.Equal("commander", provisional.Lane);
            Assert.Null(provisional.CostUsd);
        }
    }
}
