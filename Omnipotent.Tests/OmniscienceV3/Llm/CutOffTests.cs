using System.Collections.Concurrent;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Llm
{
    /// <summary>
    /// An answer that ends at its output ceiling. On the server a digest was asked for three times with the identical
    /// request and came back cut off at 2,026 tokens each time (the second in 187 ms, from the router's cache), and the
    /// classifier's teacher was cut off at 6,700 tokens on all 18 of its calls, reported as "no structured output"
    /// (20 Nov 2026). A call that says so is asked again, once, with twice the room.
    /// </summary>
    public class CutOffTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_cutoff_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)));
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
            public Func<LlmRequest, int, LlmResult> Respond { get; set; } = (_, _) => Ok();
            public int ModelMaxOutput { get; set; } = 16_384;
            private int n;

            public static LlmResult Ok() => new(true, null, "{}", LlmErrorKind.None, null, "stop", "Qwen3.8", "AIRouter", 1000, 0, 50, 4000, 10);

            public static LlmResult Failed(LlmErrorKind kind, string? finish, int completion) =>
                new(false, null, null, kind, kind.ToString(), finish, "Qwen3.8", "AIRouter", 16_177, 0, completion, 38_018, 10);

            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
            {
                Requests.Enqueue(request);
                return Task.FromResult(Respond(request, Interlocked.Increment(ref n)));
            }

            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("AIRouter", "Qwen3.8", 260_000, ModelMaxOutput, "operator-stated", null, 0, null));

            public void EndSession(string sessionKey) { }
        }

        private static LlmCall Call(int maxOutput, bool longer = true) => new()
        {
            ModuleId = "briefs", Purpose = "briefs.digest", LongerWhenCutOff = longer,
            Request = new LlmRequest
            {
                Purpose = "briefs.digest", SessionKey = "omniscience-bg-test", Turns = new[] { new LlmTurn("user", "summarise") }, WorkClass = AIRouterWorkClass.Background,
                MaxOutputTokens = maxOutput, ReasoningEffort = "off",
            },
        };

        [Fact]
        public async Task ACutOffAnswer_IsAskedForAgainOnce_WithTwiceTheRoom()
        {
            var fake = new FakeClient { Respond = (r, i) => i == 1 ? FakeClient.Failed(LlmErrorKind.Truncated, "length", r.MaxOutputTokens!.Value) : FakeClient.Ok() };
            var llm = new OmniLlm(fake, stores.Intel, clock, new TokenEstimator());

            var result = await llm.CallAsync(Call(1_770), CancellationToken.None);

            Assert.True(result.Success);
            var sent = fake.Requests.Select(r => r.MaxOutputTokens).ToArray();
            Assert.Equal(new int?[] { 2_026, 4_052 }, sent); // 1,770 asked for + 256 of headroom with thinking off, then double
            using var c = stores.Intel.OpenRead();
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls WHERE outcome='Truncated'"));
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM llm_calls WHERE outcome='ok'"));
        }

        [Fact]
        public async Task AnAnswerMissingItsStructure_IsACutOffOnlyWhenItUsedAllItsRoom()
        {
            // The teacher's failures: "no structured output", with exactly the ceiling's worth of tokens written.
            var atCeiling = new FakeClient { Respond = (r, i) => i == 1 ? FakeClient.Failed(LlmErrorKind.NoStructuredOutput, null, r.MaxOutputTokens!.Value) : FakeClient.Ok() };
            Assert.True((await new OmniLlm(atCeiling, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(5_200), CancellationToken.None)).Success);
            Assert.Equal(2, atCeiling.Requests.Count);

            // A model that simply did not follow the schema, well inside its room, is not asked again here.
            var inside = new FakeClient { Respond = (_, _) => FakeClient.Failed(LlmErrorKind.NoStructuredOutput, "stop", 300) };
            var result = await new OmniLlm(inside, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(5_200), CancellationToken.None);
            Assert.False(result.Success);
            Assert.Single(inside.Requests);
            Assert.False(OmniLlm.CutOff(result, 5_456));
        }

        [Fact]
        public async Task StillCutOffWithTwiceTheRoom_ItIsReturnedAsCutOff_AndNeverAskedAThirdTime()
        {
            var fake = new FakeClient { Respond = (r, _) => FakeClient.Failed(LlmErrorKind.Truncated, "length", r.MaxOutputTokens!.Value) };
            var result = await new OmniLlm(fake, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(1_770), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(2, fake.Requests.Count);
            Assert.True(OmniLlm.CutOff(result, null));
        }

        [Fact]
        public async Task TheRoomNeverGoesPastWhatTheModelGives_AndACallThatDidNotAskIsLeftAlone()
        {
            var small = new FakeClient { ModelMaxOutput = 3_000, Respond = (r, i) => i == 1 ? FakeClient.Failed(LlmErrorKind.Truncated, "length", r.MaxOutputTokens!.Value) : FakeClient.Ok() };
            await new OmniLlm(small, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(1_770), CancellationToken.None);
            Assert.Equal(new int?[] { 2_026, 3_000 }, small.Requests.Select(r => r.MaxOutputTokens).ToArray());

            var atLimit = new FakeClient { ModelMaxOutput = 2_000, Respond = (r, _) => FakeClient.Failed(LlmErrorKind.Truncated, "length", r.MaxOutputTokens!.Value) };
            await new OmniLlm(atLimit, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(1_770), CancellationToken.None);
            Assert.Single(atLimit.Requests);

            // Extraction splits its slice when it is cut off; it must not be given a longer answer instead.
            var plain = new FakeClient { Respond = (r, _) => FakeClient.Failed(LlmErrorKind.Truncated, "length", r.MaxOutputTokens!.Value) };
            await new OmniLlm(plain, stores.Intel, clock, new TokenEstimator()).CallAsync(Call(1_770, longer: false), CancellationToken.None);
            Assert.Single(plain.Requests);
        }
    }
}
