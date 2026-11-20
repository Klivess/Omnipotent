using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The classifier's teacher on the server (20 Nov 2026): 18 calls, 18 answers cut off at the ceiling (6,700 tokens for
    /// 120 messages), each reported as "no structured output". Every third failure parked the batch as failed, so nothing
    /// was ever labelled and the classifier never trained. A cut-off answer is the batch's size, not the messages' fault.
    /// </summary>
    public class TeacherCutOffTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_teachercut_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private readonly FakeLlm llm = new();
        private V3Runtime rt = null!;
        private JobDriver driver = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root), Clock = clock, LlmClient = llm, Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new ClassificationModule() },
                Encoder = new HashingTextEncoder(256),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 5, Messages = 900, People = 20, Guilds = 1, ChannelsPerGuild = 2, DirectChats = 6, WhatsAppGroups = 0, Span = TimeSpan.FromDays(60),
            });
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherTargetSetting, 300, "test");
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherBatchSetting, 120, "test");
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private T Scalar<T>(string sql, params (string, object?)[] args)
        {
            using var c = rt.Stores.Intel.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private static int Targets(LlmRequest r) => Regex.Matches(r.Turns[^1].Content, "target=\"yes\"").Count;

        private List<LlmRequest> TeacherRequests()
        {
            lock (llm.Requests) return llm.Requests.Where(r => r.Purpose == "classify.teacher").ToList();
        }

        private async Task RoundAsync()
        {
            clock.Advance(TimeSpan.FromMinutes(11));
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
        }

        [Fact]
        public async Task ACutOffAnswer_HalvesTheNextBatch_CostsTheMessagesNothing_AndWhatWasParkedForItIsPutBack()
        {
            // The teacher as it behaved on the server: it writes until its ceiling and the answer is unusable.
            llm.Override = r => r.Purpose != "classify.teacher" ? null
                : new LlmResult(false, null, null, LlmErrorKind.NoStructuredOutput, "The model did not return the requested structured output.", null, "fake-model", "fake", 30_000, 0,
                    r.MaxOutputTokens!.Value, 100_000, 5);
            await driver.RunUntilIdleAsync();
            await RoundAsync();

            var asked = TeacherRequests();
            Assert.True(asked.Count >= 4, $"{asked.Count} teacher requests");
            // The answer gets room for what it has to say (not 40 tokens a message), and no thinking is billed on top.
            Assert.All(asked, r => Assert.Equal("off", r.ReasoningEffort));
            Assert.Equal(120, Targets(asked[0]));
            Assert.Equal(ClassificationModule.TeacherAnswerBase + ClassificationModule.TeacherAnswerPerTarget * 120 + 256, asked[0].MaxOutputTokens);
            // Cut off: asked once more with more room (as far as the model goes), then with half the messages.
            Assert.Equal(120, Targets(asked[1]));
            Assert.True(asked[1].MaxOutputTokens > asked[0].MaxOutputTokens);
            Assert.Equal(60, Targets(asked[2]));
            Assert.True(Targets(asked[^1]) < 60);

            // None of it counted against the messages: nothing parked, no attempt used.
            Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM classify_teacher_queue WHERE state='failed' OR attempts > 0"));
            long queued = Scalar<long>("SELECT COUNT(*) FROM classify_teacher_queue WHERE state='queued'");
            Assert.True(queued >= 120);

            // What the old behaviour had parked is put back; a message given up on for another reason is left alone.
            await rt.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    UPDATE classify_teacher_queue SET state='failed', attempts=3, done_at_ms=1, note='NoStructuredOutput: The model did not return the requested structured output.'
                    WHERE message_id IN (SELECT message_id FROM classify_teacher_queue WHERE state='queued' ORDER BY message_id LIMIT 40)
                    """);
                c.Exec(tx, """
                    UPDATE classify_teacher_queue SET state='failed', attempts=3, done_at_ms=1, note='teacher call failed (Other): something else'
                    WHERE message_id IN (SELECT message_id FROM classify_teacher_queue WHERE state='queued' ORDER BY message_id DESC LIMIT 5)
                    """);
            });

            // The teacher answers again: messages are labelled, the batch grows back, and only the five stay given up on.
            llm.Override = null;
            int before = TeacherRequests().Count;
            await RoundAsync();
            await RoundAsync();
            Assert.Equal(5, Scalar<long>("SELECT COUNT(*) FROM classify_teacher_queue WHERE state='failed'"));
            Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM classify_teacher_queue WHERE note LIKE 'NoStructuredOutput%'"));
            Assert.True(Scalar<long>("SELECT COUNT(DISTINCT item_id) FROM labels WHERE model_version=$v", ("$v", ClassificationModule.TeacherVersion)) >= 60);
            var after = TeacherRequests().Skip(before).ToList();
            Assert.NotEmpty(after);
            Assert.True(after.Max(Targets) > Targets(after[0]) || Targets(after[0]) == 120, "the batch grows back once answers arrive");
        }
    }
}
