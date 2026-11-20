using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Llm
{
    /// <summary>Fake-provider suite for the Omniscience LLM layer, plus the policy rules.</summary>
    public class LlmLayerTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_llm_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;

        public async Task InitializeAsync() => stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* unique temp dir */ }
        }

        private sealed class FakeClient : ILlmClient
        {
            public readonly ConcurrentQueue<LlmRequest> Requests = new();
            public readonly ConcurrentQueue<string> Ended = new();
            public Func<LlmRequest, int, LlmResult> Respond { get; set; } = (_, _) => Ok("{}");
            private int n;

            public static LlmResult Ok(string text, int prompt = 1000, int cached = 0) =>
                new(true, text, null, LlmErrorKind.None, null, "stop", "Qwen3.8", "AIRouter", prompt, cached, 50, 4000, 10);

            public static LlmResult Error(LlmErrorKind kind, string msg) =>
                new(false, null, null, kind, msg, null, "Qwen3.8", "AIRouter", 0, 0, 0, 0, 5);

            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
            {
                Requests.Enqueue(request);
                return Task.FromResult(Respond(request, Interlocked.Increment(ref n)));
            }

            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("AIRouter", "Qwen3.8", 260_000, 16_384, "operator-stated", null, 0, null));

            public void EndSession(string sessionKey) => Ended.Enqueue(sessionKey);
        }

        private static LlmRequest Req(string text = "hello", AIRouterWorkClass cls = AIRouterWorkClass.Background) => new()
        {
            Purpose = "test", SessionKey = "omniscience-bg-test", Turns = new[] { new LlmTurn("user", text) }, WorkClass = cls,
        };

        [Fact]
        public async Task Overflow_IsRepackedAndRetriedOnceThenLogged()
        {
            var fake = new FakeClient
            {
                Respond = (r, i) => i == 1 ? FakeClient.Error(LlmErrorKind.ContextOverflow, "maximum context length is 1000 tokens") : FakeClient.Ok("done"),
            };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            double? shrinkUsed = null;
            var result = await llm.CallAsync(new LlmCall
            {
                ModuleId = "m", Purpose = "p", Request = Req(new string('x', 5000)),
                Repack = f => { shrinkUsed = f; return Req("smaller"); },
            }, CancellationToken.None);
            Assert.True(result.Success);
            Assert.Equal(0.85, shrinkUsed);
            Assert.Equal(2, fake.Requests.Count);
            using var c = stores.Intel.OpenRead();
            Assert.Equal(2L, c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls"));
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls WHERE outcome='ContextOverflow'"));
        }

        [Fact]
        public async Task Unauthorized_OpensTheCircuitAndPausesBackgroundWork()
        {
            var fake = new FakeClient { Respond = (_, _) => FakeClient.Error(LlmErrorKind.Unauthorized, "401") };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            var ex = await Assert.ThrowsAsync<JobFailureException>(() => llm.CallAsync(new LlmCall { ModuleId = "m", Purpose = "p", Request = Req() }, CancellationToken.None));
            Assert.Equal(ErrorClass.Permanent, ex.Class);
            Assert.True(llm.CircuitOpen);
            Assert.True(new LlmBackgroundGate(llm).ShouldPause());
            clock.Advance(TimeSpan.FromMinutes(16));
            Assert.False(llm.CircuitOpen);
        }

        [Fact]
        public async Task RateLimits_BecomeTransientJobFailures_RefusalsAreReturned()
        {
            var limited = new OmniLlm(new FakeClient { Respond = (_, _) => FakeClient.Error(LlmErrorKind.RateLimited, "429") }, stores.Intel, clock, new TokenEstimator());
            var ex = await Assert.ThrowsAsync<JobFailureException>(() => limited.CallAsync(new LlmCall { ModuleId = "m", Purpose = "p", Request = Req() }, CancellationToken.None));
            Assert.Equal(ErrorClass.Transient, ex.Class);

            var refusing = new OmniLlm(new FakeClient { Respond = (_, _) => FakeClient.Error(LlmErrorKind.Refused, "I can't help with that") }, stores.Intel, clock, new TokenEstimator());
            var r = await refusing.CallAsync(new LlmCall { ModuleId = "m", Purpose = "p", Request = Req() }, CancellationToken.None);
            Assert.Equal(LlmErrorKind.Refused, r.ErrorKind);
        }

        [Fact]
        public async Task CacheShapedSession_AsksEachQuestionOverThePrefix_AndAFollowUpOnTopOfTheAnswers()
        {
            var fake = new FakeClient();
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            var schemas = new[] { new LlmOutputSchema("emit_a", "a", new JObject()), new LlmOutputSchema("emit_b", "b", new JObject()) };
            using (var s = llm.OpenSession("m", "extract", "omniscience-bg-x", "system", Evidence, AIRouterWorkClass.Background, schemas: schemas))
            {
                await s.AskAsync("question 1", schemas[0], 100, null, CancellationToken.None);
                await s.AskAsync("question 2", schemas[1], 100, null, CancellationToken.None);
                await s.FollowUpAsync("and why?", schemas[1], 100, null, CancellationToken.None);
                Assert.Equal(3, s.Passes);
            }
            var reqs = fake.Requests.ToArray();
            // A question of its own is the prefix and the question, asked from the session's system prompt again: what the
            // provider reads begins with exactly what it read for the question before, and no answer is sent back.
            Assert.Equal(Evidence + "\n\nquestion 1", reqs[0].Turns[0].Content);
            Assert.Equal(Evidence + "\n\nquestion 2", reqs[1].Turns[0].Content);
            Assert.True(reqs[0].Rewind && reqs[1].Rewind);
            // A follow-up travels alone, on top of the question before it and its answer.
            Assert.Equal("and why?", reqs[2].Turns[0].Content);
            Assert.False(reqs[2].Rewind);
            // Every question carries every schema of the session, whichever one it must answer with: the tool definitions
            // come before the prefix in what the provider reads, and one that changed would end the match there.
            Assert.All(reqs, r => Assert.Same(schemas, r.SchemaFamily));
            Assert.Equal(new[] { "emit_a", "emit_b", "emit_b" }, reqs.Select(r => r.OutputSchema!.Name).ToArray());
            Assert.All(reqs, r => Assert.True(r.KeepSession));
            Assert.All(reqs, r => Assert.Equal(LlmStamp.None, r.Stamp));
            Assert.Equal(new[] { "omniscience-bg-x" }, fake.Ended.ToArray());
            // What each call sent a second time is kept with it: nothing for the first, the request before it for the rest.
            using var c = stores.Intel.OpenRead();
            Assert.Equal(new long[] { 0, 1000, 1000 }, c.Query(null, "SELECT resent_tokens FROM llm_calls ORDER BY call_id", r => r.GetInt64(0)).ToArray());
        }

        /// <summary>A prefix the size of a small slice of evidence (a question is a line or two beside it).</summary>
        private static readonly string Evidence = "BIG EVIDENCE PREFIX " + string.Concat(Enumerable.Repeat("[m:1] p3fa2: nothing much happened today. ", 60));

        [Fact]
        public async Task TwoLongQuestionsInARow_AreNotPassedOffAsOneContinuingTheOther()
        {
            var fake = new FakeClient();
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            string adjudicate = "Decide each disputed claim. " + new string('d', Evidence.Length / 4);
            string criticise = "Is each fact proven? " + new string('f', Evidence.Length / 4);
            using (var s = llm.OpenSession("m", "extract", "omniscience-bg-long", "system", Evidence, AIRouterWorkClass.Background))
            {
                await s.AskAsync("Extract every claim.", null, 100, null, CancellationToken.None);
                await s.AskAsync(adjudicate, null, 100, null, CancellationToken.None);
                // A short question before a long one: the long one begins with nearly all of the short one's request.
                Assert.Empty(fake.Ended);
                await s.AskAsync(criticise, null, 100, null, CancellationToken.None);
                // Two long ones share the evidence and nothing after it. The scheduler would measure the second against the
                // whole of the first and count a warm prefix as lost, so the second is sent under a cache identity of its
                // own (the provider still serves the evidence from cache; the scheduler sees a first call and scores nothing).
                Assert.Equal(new[] { "omniscience-bg-long" }, fake.Ended.ToArray());
                // After a follow-up the session's last request is the whole conversation: the same goes for whatever is asked next.
                await s.FollowUpAsync("Why?", null, 100, null, CancellationToken.None);
                await s.AskAsync("Extract every claim.", null, 100, null, CancellationToken.None);
                Assert.Single(fake.Ended);
                await s.FollowUpAsync("Why?", null, 100, null, CancellationToken.None);
                await s.AskAsync(criticise, null, 100, null, CancellationToken.None);
                Assert.Equal(2, fake.Ended.Count);
            }
            // Every one of them carried the evidence, whichever identity it went under.
            Assert.All(fake.Requests.Where(r => r.Rewind), r => Assert.StartsWith(Evidence, r.Turns[0].Content));
        }

        [Fact]
        public async Task AQuestionThatFailed_IsNotFollowedUpWithoutItsEvidence()
        {
            int n = 0;
            var fake = new FakeClient { Respond = (_, _) => ++n == 2 ? new LlmResult(false, "", null, LlmErrorKind.Truncated, "cut off", "length", "Qwen3.8", "AIRouter", 900, 0, 0, 0, 5) : FakeClient.Ok("{}") };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            using var s = llm.OpenSession("m", "research", "omniscience-ia-x", "system", "QUESTION AND PEOPLE", AIRouterWorkClass.Interactive);
            await s.FollowUpAsync("Begin.", null, 100, null, CancellationToken.None);
            Assert.True(s.PrefixSent);
            await s.FollowUpAsync("SEARCH RESULTS …", null, 100, null, CancellationToken.None);
            Assert.False(s.PrefixSent);
            await s.FollowUpAsync("SEARCH RESULTS again", null, 100, null, CancellationToken.None);
            var reqs = fake.Requests.ToArray();
            // Nothing to follow up on yet: the first one is asked over the prefix.
            Assert.Equal(("QUESTION AND PEOPLE\n\nBegin.", true), (reqs[0].Turns[0].Content, reqs[0].Rewind));
            Assert.Equal(("SEARCH RESULTS …", false), (reqs[1].Turns[0].Content, reqs[1].Rewind));
            // The one after a failure starts from the prefix again, instead of being appended to a turn with no answer.
            Assert.Equal(("QUESTION AND PEOPLE\n\nSEARCH RESULTS again", true), (reqs[2].Turns[0].Content, reqs[2].Rewind));
        }

        [Fact]
        public async Task TheEstimator_LearnsOnlyFromRequestsThatHoldTheirWholePrompt()
        {
            string evidence = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400));
            string followUp = string.Concat(Enumerable.Repeat("Is each of these facts proven by the messages it cites? ", 40));
            var estimator = new TokenEstimator();
            // The provider counts what it read: the request's own text for a question over the prefix, and the whole
            // conversation (prefix, first question, its answer) for a follow-up whose request holds one question.
            var fake = new FakeClient
            {
                Respond = (r, _) => FakeClient.Ok("{}", prompt: (int)TokenEstimator.Raw(r.Rewind ? OmniLlm.PromptText(r) : evidence + OmniLlm.PromptText(r))),
            };
            var llm = new OmniLlm(fake, stores.Intel, clock, estimator);
            using var s = llm.OpenSession("m", "extract", "omniscience-bg-est", "system", evidence, AIRouterWorkClass.Background);
            await s.AskAsync("Extract every claim.", null, 100, null, CancellationToken.None);
            double learned = estimator.Factor;
            Assert.True(learned < 1.15, "an exact count should bring the starting factor down");
            for (int i = 0; i < 5; i++) await s.FollowUpAsync(followUp, null, 100, null, CancellationToken.None);
            // Learned from, the follow-ups read as text more than ten times denser than it is, and every prompt packed
            // afterwards held less than half of what it was sized for.
            Assert.Equal(learned, estimator.Factor);
        }

        [Fact]
        public async Task TheBackgroundGate_JudgesWhatWasSentASecondTime_NotEvidenceReadForTheFirstTime()
        {
            var fake = new FakeClient { Respond = (_, _) => FakeClient.Ok("x", prompt: 10_000, cached: 1_000) };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            for (int i = 0; i < 25; i++) await llm.CallAsync(new LlmCall { ModuleId = "m", Purpose = "p", Request = Req() }, CancellationToken.None);
            var (eff, n) = llm.CacheEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString());
            Assert.Equal(25, n);
            Assert.Equal(0.1, eff, 3);
            Assert.Equal(0, llm.CacheEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Interactive.ToString()).Samples);
            // Twenty-five calls a tenth cached, none of which sent anything a second time: new evidence has to be prefilled
            // once, and pausing the lane would change nothing about that. (Until 20 Nov 2026 this paused it.)
            Assert.Equal(0, llm.ResentEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString()).Samples);
            Assert.False(new LlmBackgroundGate(llm).ShouldPause());

            // Later questions over a prefix the provider has been sent, and it serves a tenth of them: something in the
            // prompt stopped matching, or the questions arrive after the prefix is gone. That is what the gate is for.
            using (var s = llm.OpenSession("m", "p", "omniscience-bg-cold", "system", Evidence, AIRouterWorkClass.Background))
                for (int i = 0; i < 21; i++) await s.AskAsync("q" + i, null, 100, null, CancellationToken.None);
            var (resent, samples) = llm.ResentEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString());
            Assert.Equal(20, samples);
            Assert.Equal(0.1, resent, 3);
            Assert.True(new LlmBackgroundGate(llm).ShouldPause());

            // An hour on, the same questions come back served from cache: the lane runs.
            clock.Advance(TimeSpan.FromHours(2));
            fake.Respond = (_, _) => FakeClient.Ok("x", prompt: 10_000, cached: 9_700);
            using (var s = llm.OpenSession("m", "p", "omniscience-bg-warm", "system", Evidence, AIRouterWorkClass.Background))
                for (int i = 0; i < 21; i++) await s.AskAsync("q" + i, null, 100, null, CancellationToken.None);
            Assert.Equal(0.97, llm.ResentEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString()).Efficiency, 3);
            Assert.False(new LlmBackgroundGate(llm).ShouldPause());
        }

        [Fact]
        public void TokenEstimator_CalibratesTowardsProviderCounts()
        {
            var est = new TokenEstimator();
            string text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 100));
            int before = est.Estimate(text);
            est.Calibrate(text, actualPromptTokens: before * 2); // provider counts far more: estimator rises fast
            Assert.True(est.Estimate(text) > before);
            Assert.True(est.Estimate("日本語のテキスト") > est.Estimate("abcdefgh") / 2);
        }

        [Fact]
        public void ContextPacker_HonoursPriorityStrategyAndOrder()
        {
            var est = new TokenEstimator();
            var history = Enumerable.Range(1, 50).Select(i => $"message {i:D2} " + new string('w', 200)).ToList();
            var sections = new[]
            {
                new PackSection("system", 100, new[] { "SYSTEM RULES" }, PackStrategy.Required),
                new PackSection("history", 50, history, PackStrategy.KeepNewest, Header: "HISTORY"),
                new PackSection("question", 90, new[] { "THE QUESTION" }, PackStrategy.Required),
            };
            var r = ContextPacker.Pack(sections, tokenBudget: 1_200, est);
            Assert.False(r.RequiredDidNotFit);
            Assert.True(r.Coverage["history"].Included < 50 && r.Coverage["history"].Included > 0);
            Assert.Contains("message 50", r.Text);               // newest kept
            Assert.DoesNotContain("message 01", r.Text);         // oldest dropped
            Assert.True(r.Text.IndexOf("SYSTEM RULES", StringComparison.Ordinal) < r.Text.IndexOf("HISTORY", StringComparison.Ordinal));
            Assert.True(r.Text.IndexOf("HISTORY", StringComparison.Ordinal) < r.Text.IndexOf("THE QUESTION", StringComparison.Ordinal));
            Assert.True(r.EstimatedTokens <= 1_200);

            var tooBig = ContextPacker.Pack(new[] { new PackSection("req", 1, new[] { new string('z', 50_000) }, PackStrategy.Required) }, 100, est);
            Assert.True(tooBig.RequiredDidNotFit);
        }

        [Fact]
        public void Budget_SubtractsOutputMarginAndOverhead()
        {
            var info = new ModelContextInfo("AIRouter", "Qwen3.8", 260_000, 16_384, "operator-stated", null, 0, null);
            int budget = ContextPacker.Budget(info, reservedOutputTokens: 8_000);
            Assert.Equal(260_000 - 8_000 - 20_800 - 512, budget);
            Assert.Equal(ContextPacker.DefaultByteBudget, ContextPacker.ByteBudget(info));
        }

        // ── policy ───────────────────────────────────────────────────────────────

        private static long Utc(int y, int m, int d) => Instant.FromUtc(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc));

        [Fact]
        public void MinorGuard_BandsFollowTimeAdjustedEvidence()
        {
            var birth = LifeMath.BirthFromAge(15, Utc(2024, 1, 1));
            Assert.Equal(AgeBand.Minor, MinorGuard.Evaluate(birth, false, Utc(2025, 1, 1)));
            Assert.Equal(AgeBand.Adult, MinorGuard.Evaluate(birth, false, Utc(2030, 1, 1)));       // grew up
            Assert.Equal(AgeBand.Unknown, MinorGuard.Evaluate(null, false, Utc(2025, 1, 1)));
            Assert.Equal(AgeBand.Minor, MinorGuard.Evaluate(null, true, Utc(2025, 1, 1)));         // Klives marked
            // Straddling 18 with a midpoint under 18 is protected.
            var straddle = new TimeInterval(Utc(2007, 6, 1), Utc(2009, 6, 1));
            Assert.Equal(AgeBand.LikelyMinor, MinorGuard.Evaluate(straddle, false, Utc(2026, 1, 1)));
            Assert.False(MinorGuard.Allows(RestrictedCapability.FaceLabelling, AgeBand.LikelyMinor));
            Assert.True(MinorGuard.Allows(RestrictedCapability.Replica, AgeBand.Adult));
            Assert.False(MinorGuard.AllowsAttribute("school", AgeBand.Minor));
            Assert.True(MinorGuard.AllowsAttribute("favourite_game", AgeBand.Minor));
        }

        [Theory]
        [InlineData("religion", "self_disclosure", "literal", true)]
        [InlineData("religion", "inferred", "literal", false)]
        [InlineData("religion", "third_party", "literal", false)]
        [InlineData("religion", "self_disclosure", "joke", false)]
        [InlineData("favourite_food", "inferred", "literal", true)]
        public void SensitiveTraits_OnlyLiteralSelfStatements(string attribute, string source, string modality, bool allowed) =>
            Assert.Equal(allowed, SensitiveTraits.AllowsAssertion(attribute, source, modality));

        [Fact]
        public void OutboundPolicy_TokensAreSingleUseBoundAndExpiring()
        {
            var policy = new OutboundPolicy(clock, TimeSpan.FromMinutes(5));
            Assert.True(policy.Confirm(null, OutboundKind.NotifyKlives, "klives", "hi")); // self-notification
            string token = policy.Preview(OutboundKind.KliveBotPost, "channel:1", "hello");
            Assert.False(policy.Confirm(token, OutboundKind.KliveBotPost, "channel:1", "hello EDITED")); // consumed + mismatch
            string t2 = policy.Preview(OutboundKind.KliveBotPost, "channel:1", "hello");
            Assert.True(policy.Confirm(t2, OutboundKind.KliveBotPost, "channel:1", "hello"));
            Assert.False(policy.Confirm(t2, OutboundKind.KliveBotPost, "channel:1", "hello")); // single use
            string t3 = policy.Preview(OutboundKind.KliveBotPost, "channel:1", "hello");
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.False(policy.Confirm(t3, OutboundKind.KliveBotPost, "channel:1", "hello")); // expired
        }
    }
}
