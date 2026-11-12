using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
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
        public async Task CacheShapedSession_SendsThePrefixOnceAndEndsTheSession()
        {
            var fake = new FakeClient();
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            using (var s = llm.OpenSession("m", "extract", "omniscience-bg-x", "system", "BIG EVIDENCE PREFIX", AIRouterWorkClass.Background))
            {
                await s.AskAsync("question 1", null, 100, null, CancellationToken.None);
                await s.AskAsync("question 2", null, 100, null, CancellationToken.None);
                Assert.Equal(2, s.Passes);
            }
            var reqs = fake.Requests.ToArray();
            Assert.StartsWith("BIG EVIDENCE PREFIX", reqs[0].Turns[0].Content);
            Assert.Equal("question 2", reqs[1].Turns[0].Content);
            Assert.All(reqs, r => Assert.True(r.KeepSession));
            Assert.All(reqs, r => Assert.Equal(LlmStamp.None, r.Stamp));
            Assert.Equal(new[] { "omniscience-bg-x" }, fake.Ended.ToArray());
        }

        [Fact]
        public async Task CacheEfficiency_IsMeasuredPerWorkClassAndGatesBackgroundWork()
        {
            var fake = new FakeClient { Respond = (_, _) => FakeClient.Ok("x", prompt: 10_000, cached: 1_000) };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());
            for (int i = 0; i < 25; i++) await llm.CallAsync(new LlmCall { ModuleId = "m", Purpose = "p", Request = Req() }, CancellationToken.None);
            var (eff, n) = llm.CacheEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString());
            Assert.Equal(25, n);
            Assert.Equal(0.1, eff, 3);
            Assert.True(new LlmBackgroundGate(llm).ShouldPause());
            Assert.Equal(0, llm.CacheEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Interactive.ToString()).Samples);
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
