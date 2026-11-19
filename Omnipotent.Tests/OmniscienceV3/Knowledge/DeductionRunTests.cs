using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Prompting;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// The extraction run as the server showed it on 19 Nov 2026, two days after launch: 141 calls, 87 of them cut off at
    /// exactly 12,000 output tokens (slices filled the model's 260,000-token window), jobs that read for hours and then
    /// failed on a later slice, over three hundred verified claims and not one fact. Invented people and text only.
    /// </summary>
    public class DeductionRunTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc));
        private const long Minute = 60_000L;

        private static bool IsCritic(string question) => question.Contains("sceptical reviewer", StringComparison.Ordinal);

        /// <summary>A model that finds "I live in X" in the subject's own messages, and a critic that agrees with every fact.</summary>
        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (IsCritic(call.Question))
                    return JsonSerializer.Serialize(new { verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "stated" }) });
                var roster = FakeLlm.Roster(call.Prompt);
                string subject = roster["Dee Marsh"];
                var claims = new List<object>();
                foreach (var (reference, (handle, text)) in FakeLlm.EvidenceLines(call.Prompt))
                {
                    var m = Regex.Match(text, @"I live in (Town\d+)");
                    if (handle != subject || !m.Success) continue;
                    claims.Add(new
                    {
                        id = "c" + (claims.Count + 1).ToString(CultureInfo.InvariantCulture), subject, attribute = "city", value = m.Groups[1].Value, polarity = "affirmed",
                        modality = "literal", source_kind = "self_disclosure", speech_act = "statement", sincerity = "sincere",
                        evidence = new[] { new { @ref = reference, quote = m.Value } }, probability = 0.9,
                    });
                }
                return JsonSerializer.Serialize(new { claims });
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
        }

        /// <summary>One tracked person and a long private chat: a statement about where she lives every fortieth message.</summary>
        private static async Task SeedAsync(KnowledgeHarness h, int messages)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            const string filler = "the weather has been all over the place again this week and nobody on the street can agree what to wear in the morning";
            for (int i = 0; i < messages; i++)
            {
                long at = Now - 400L * 86_400_000 + i * 20 * Minute;
                string text = i % 40 == 5 ? $"by the way I live in Town{i} now, moved last month" : $"{filler} ({i})";
                await corpus.MessageAsync(i % 3 == 0 ? "me" : "dee", "discord:c-dee", at, i % 3 == 0 ? $"right, {filler}" : text);
            }
        }

        private static async Task<List<string>> TurnAsync(KnowledgeHarness h)
        {
            while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
            await h.Runtime.Host.PlanOnceAsync();
            return await InquiryTests.DriveAsync(h);
        }

        private static List<LlmRequest> ExtractAsks(KnowledgeHarness h) =>
            h.Llm.Requests.Where(r => r.Purpose == "deduction.extract" && !IsCritic(r.Turns[^1].Content)).ToList();

        private static int Facts(KnowledgeHarness h) => h.Kit.Assertions.Query(new AssertionQuery
        {
            SubjectIds = new[] { "dee" }, AtMs = h.Clock.NowMs, Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed }, Limit = 500,
        }).Count(v => v.Record.Attribute == "city");

        [Fact]
        public async Task ASlice_IsSizedToWhatCanComeBack_NotToTheModelsWindow()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h, 900);

            var failures = await TurnAsync(h);
            Assert.True(failures.Count == 0, string.Join("\n", failures));

            var asks = ExtractAsks(h);
            // 900 messages are some 30,000 tokens. Filling the 260,000-token window they were one call, whose answer could
            // not fit under the output ceiling; now a job reads them a slice at a time (four slices, then it yields).
            Assert.Equal(4, asks.Count);
            var estimator = new TokenEstimator();
            Assert.All(asks, a => Assert.InRange(estimator.Estimate(a.Turns[^1].Content), 1_000, 9_000));

            // Every call says how hard to think, and its ceiling leaves room for that on top of the answer.
            Assert.All(asks, a => Assert.Equal("low", a.ReasoningEffort));
            Assert.All(asks, a => Assert.Equal(16_000 + 1_500, a.MaxOutputTokens));

            // One reading by default; the critic is asked on the session that already holds the evidence, so its question
            // travels alone and the model still has the messages in front of it.
            var critic = h.Llm.Calls.Where(c => c.Purpose == "deduction.extract" && IsCritic(c.Question)).ToList();
            Assert.NotEmpty(critic);
            Assert.All(critic, c => { Assert.True(c.TurnInSession > 1); Assert.Contains("EVIDENCE (", c.Prompt); Assert.DoesNotContain("EVIDENCE (", c.Question); });

            // More is left to read, and the person is marked for the next job.
            using var intel = h.Stores.Intel.OpenRead();
            Assert.Equal(1L, intel.Scalar<long>(null, "SELECT pending FROM kn_person_state WHERE person_id='dee'"));
            Assert.Contains("\"pending\":true", intel.Scalar<string>(null, "SELECT rejections_json FROM kn_extraction_runs WHERE person_id='dee' AND outcome='ok'"));
        }

        [Fact]
        public async Task ClaimsBecomeFactsAfterEverySlice_SoAJobThatFailsLaterLosesNothing()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h, 900);
            int extractAsks = 0;
            h.Llm.Intercept = request =>
            {
                if (request.Purpose != "deduction.extract" || IsCritic(request.Turns[^1].Content)) return null;
                // The third slice meets an outage: the provider is unavailable.
                return ++extractAsks == 3 ? new LlmResult(false, null, null, LlmErrorKind.Other, "AIRouter request failed with status 502.", null, "fake-model", "fake", 0, 0, 0, 0, 1) : null;
            };

            var failures = await TurnAsync(h);

            // The job failed, and will be tried again...
            Assert.Contains(failures, f => f.StartsWith("knowledge.deduction/extract", StringComparison.Ordinal));
            using var intel = h.Stores.Intel.OpenRead();
            Assert.Equal(1L, intel.Scalar<long>(null, "SELECT COUNT(*) FROM jobs WHERE module_id='knowledge.deduction' AND kind='extract' AND state='queued'"));
            // ...but what the first two slices found is knowledge already. Before, facts were only made when the job
            // returned, so they stayed claims for as long as any later slice kept failing.
            Assert.True(intel.Scalar<long>(null, "SELECT COUNT(*) FROM kn_claims WHERE person_id='dee' AND verdict='verified'") >= 2);
            Assert.True(Facts(h) >= 1, "the slices read before the failure should have produced facts");
            // The run is closed as failed with what it managed, not left "running" for ever.
            var run = intel.Query(null, "SELECT outcome, slices, finished_at_ms FROM kn_extraction_runs WHERE person_id='dee'", r => (Outcome: r.GetString(0), Slices: r.GetInt32(1), Finished: r.GetInt64OrNull(2))).Single();
            Assert.Equal(("failed", 2), (run.Outcome, run.Slices));
            Assert.NotNull(run.Finished);

            // The retry reads on from where it stopped: nothing is read twice.
            h.Llm.Intercept = null;
            int before = ExtractAsks(h).Count;
            h.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Empty(await TurnAsync(h));
            Assert.Equal(4, ExtractAsks(h).Count - before);
            Assert.Equal(1L, intel.Scalar<long>(null, "SELECT COUNT(*) FROM kn_extraction_runs WHERE person_id='dee' AND outcome='failed'"));
        }

        [Fact]
        public async Task AnAnswerCutOffBeforeItBegan_SplitsTheSlice_AndTheJobGoesOn()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h, 300);
            int extractAsks = 0;
            h.Llm.Intercept = request =>
            {
                if (request.Purpose != "deduction.extract" || IsCritic(request.Turns[^1].Content)) return null;
                // The whole output ceiling went on thinking: finish_reason=length and no structured answer at all.
                return ++extractAsks == 1
                    ? new LlmResult(false, "", null, LlmErrorKind.NoStructuredOutput, "The model did not return the requested structured output.", "length", "fake-model", "fake", 9_000, 0, 17_500, 30_000, 1)
                    : null;
            };

            var failures = await TurnAsync(h);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
            var asks = ExtractAsks(h);
            // Not asked again at the same size (it would be cut off again): the two halves are read instead.
            Assert.DoesNotContain(asks, a => a.Turns[^1].Content.Contains("could not be used", StringComparison.Ordinal));
            Assert.True(asks.Count >= 3);
            Assert.True(asks[1].Turns[^1].Content.Length < asks[0].Turns[^1].Content.Length);
            // The session that was cut off was ended, so nothing was appended to a turn with no answer.
            Assert.Contains(asks[0].SessionKey, h.Llm.Ended);
            using var intel = h.Stores.Intel.OpenRead();
            Assert.Contains("\"splits\":1", intel.Scalar<string>(null, "SELECT rejections_json FROM kn_extraction_runs WHERE person_id='dee'"));
            Assert.True(Facts(h) >= 1);
        }

        // ── the gateway's side of it ─────────────────────────────────────────────

        private sealed class ScriptedLlm : ILlmClient
        {
            public readonly List<LlmRequest> Requests = new();
            public readonly List<string> Ended = new();
            public readonly Queue<LlmResult> Answers = new();

            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(Answers.Dequeue());
            }

            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("fake", "fake-model", 260_000, 16_384, "test", null, 0, null));

            public void EndSession(string sessionKey) => Ended.Add(sessionKey);

            public static LlmResult Ok(string json) => new(true, json, json, LlmErrorKind.None, null, "tool_calls", "fake-model", "fake", 100, 0, 20, 400, 1);
            public static LlmResult Fail(LlmErrorKind kind, string? finish) => new(false, "", null, kind, "failed", finish, "fake-model", "fake", 100, 0, 0, 400, 1);
        }

        private sealed record Answer(List<string>? Items);

        private static async Task<(OmniLlm Llm, ScriptedLlm Client, OmniscienceStores Stores, string Root)> GatewayAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "omniv3_llmshape_" + Guid.NewGuid().ToString("N"));
            var clock = new FixedClock(Now);
            var stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            var client = new ScriptedLlm();
            return (new OmniLlm(client, stores.Intel, clock, new TokenEstimator()), client, stores, root);
        }

        private static async Task CloseAsync(OmniscienceStores stores, string root)
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        [Fact]
        public async Task EveryCall_SaysHowHardToThink_AndLeavesRoomForIt()
        {
            var (llm, _, stores, root) = await GatewayAsync();
            try
            {
                LlmRequest Request(int? max, string? effort = null) => new()
                {
                    Purpose = "p", SessionKey = "omniscience-bg-test", Turns = new[] { new LlmTurn("user", "q") }, MaxOutputTokens = max, ReasoningEffort = effort,
                };
                // A label or a line (links, watch rules, concepts, captions): thinking off. Their ceilings were sized for the
                // answer alone and the router's default "medium" thinking spent them: 10 of 10 link-filing calls came back cut off.
                Assert.Equal(("off", (int?)(1_550 + 256)), (llm.Shape(Request(1_550)).ReasoningEffort, llm.Shape(Request(1_550)).MaxOutputTokens));
                Assert.Equal(("off", (int?)(120 + 256)), (llm.Shape(Request(120)).ReasoningEffort, llm.Shape(Request(120)).MaxOutputTokens));
                // Longer answers think a little, with headroom on top of what the caller asked for the answer.
                Assert.Equal(("low", (int?)13_500), (llm.Shape(Request(12_000)).ReasoningEffort, llm.Shape(Request(12_000)).MaxOutputTokens));
                Assert.Equal(("low", (int?)null), (llm.Shape(Request(null)).ReasoningEffort, llm.Shape(Request(null)).MaxOutputTokens));
                // A caller that asks for more gets it, and the room for it.
                Assert.Equal(("medium", (int?)4_700), (llm.Shape(Request(700, "Medium")).ReasoningEffort, llm.Shape(Request(700, "Medium")).MaxOutputTokens));
                Assert.Equal(("high", (int?)14_000), (llm.Shape(Request(6_000, "high")).ReasoningEffort, llm.Shape(Request(6_000, "high")).MaxOutputTokens));
            }
            finally { await CloseAsync(stores, root); }
        }

        [Fact]
        public async Task AStructuredAsk_TreatsACutOffAsTruncation_AndSaysTheRequestAgainWhenItRetries()
        {
            var (llm, client, stores, root) = await GatewayAsync();
            try
            {
                var schema = StructuredLlm.Schema("emit", "items", """{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"}}}}""");
                Func<Answer, string?> validate = a => a.Items == null ? "No items." : null;

                // Cut off before the answer began: one call, reported as truncated (the caller makes the request smaller).
                client.Answers.Enqueue(ScriptedLlm.Fail(LlmErrorKind.NoStructuredOutput, "length"));
                using (var s = llm.OpenSession("m", "p", "omniscience-bg-a", "system", "EVIDENCE one two three", AIRouterWorkClass.Background))
                {
                    var cut = await StructuredLlm.AskAsync(s, "List the items.", schema, 4_000, null, validate, default);
                    Assert.Equal((StructuredOutcome.Truncated, 1), (cut.Outcome, cut.Calls));
                    Assert.False(s.PrefixSent);
                }
                Assert.Contains("omniscience-bg-a", client.Ended);

                // No structured answer at all (not cut off): the session is started again, and the retry carries the
                // evidence and the request itself, not only "answer the same request again".
                client.Requests.Clear();
                client.Answers.Enqueue(ScriptedLlm.Fail(LlmErrorKind.NoStructuredOutput, "stop"));
                client.Answers.Enqueue(ScriptedLlm.Ok("""{"items":["a"]}"""));
                using (var s = llm.OpenSession("m", "p", "omniscience-bg-b", "system", "EVIDENCE one two three", AIRouterWorkClass.Background))
                {
                    var repaired = await StructuredLlm.AskAsync(s, "List the items.", schema, 4_000, null, validate, default);
                    Assert.Equal((StructuredOutcome.Repaired, 2), (repaired.Outcome, repaired.Calls));
                    string retry = client.Requests[1].Turns[^1].Content;
                    Assert.StartsWith("EVIDENCE one two three", retry);
                    Assert.Contains("List the items.", retry);
                    Assert.Contains("could not be used", retry);
                    Assert.True(s.PrefixSent);

                    // A follow-up after a success travels alone: the provider-side session holds the evidence.
                    client.Answers.Enqueue(ScriptedLlm.Ok("""{"items":["b"]}"""));
                    await StructuredLlm.AskAsync(s, "And the rest?", schema, 4_000, null, validate, default);
                    Assert.Equal("And the rest?", client.Requests[2].Turns[^1].Content);
                }
            }
            finally { await CloseAsync(stores, root); }
        }
    }
}
