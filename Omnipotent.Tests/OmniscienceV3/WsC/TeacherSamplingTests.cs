using System.Text.Json;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The job that draws the classifier's teacher sample. On the server it took 40 to 68 minutes to draw a thousand
    /// messages (each probe is a handful of cold reads), kept them in memory until the end and reported nothing: one
    /// run was taken for stuck at two hours and stopped, five times over, each time losing all it had drawn
    /// (22 Nov 2026). Synthetic data only.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class TeacherSamplingTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_sampling_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private readonly TimeSpan budget = ClassificationModule.TeacherSampleBudget;
        private readonly int probes = ClassificationModule.TeacherSampleProbes;
        private V3Runtime rt = null!;
        private JobDriver driver = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root),
                Clock = clock,
                // The teacher labels what is queued as it goes; a labelled message stays in the queue's count.
                LlmClient = new FakeLlm(),
                Log = _ => { },
                LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new ClassificationModule() },
                Encoder = new HashingTextEncoder(256),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 3, Messages = 2_500, People = 40, Guilds = 2, ChannelsPerGuild = 3, DirectChats = 10, WhatsAppGroups = 1, Span = TimeSpan.FromDays(120),
            });
            // No sample is wanted until the test asks for one: the first jobs only embed.
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherTargetSetting, 0, "test");
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES('synth-p1','tracked',0)"));
        }

        public async Task DisposeAsync()
        {
            ClassificationModule.TeacherSampleBudget = budget;
            ClassificationModule.TeacherSampleProbes = probes;
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private long Queued()
        {
            using var c = rt.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM classify_teacher_queue WHERE reason='sample'");
        }

        /// <summary>Ten minutes on: the next sampling job, run here so that what it reports on the way is kept (the driver
        /// runs every other job as usual).</summary>
        private async Task<(JsonElement Result, List<JsonElement> Said)> NextSampleAsync()
        {
            clock.Advance(TimeSpan.FromMinutes(11));
            while (await rt.Host.PumpChangesOnceAsync() > 0) { }
            await rt.Host.PlanOnceAsync();
            var said = new List<JsonElement>();
            JsonElement? result = null;
            while (await rt.Host.Jobs.LeaseNextAsync(Lane.Cpu, "test-driver", TimeSpan.FromMinutes(10)) is { } job)
            {
                if (job.Kind != "teacher-sample") { await driver.RunAsync(job); continue; }
                var handler = rt.Host.Resolve(job.ModuleId, job.Kind)!;
                string? json = await handler(new JobContext(job, clock, CancellationToken.None, partial =>
                {
                    said.Add(JsonDocument.Parse(partial).RootElement);
                    return Task.FromResult(true);
                }));
                await rt.Host.Jobs.CompleteAsync(job.JobId, "test-driver", json);
                result = JsonDocument.Parse(json!).RootElement;
            }
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            Assert.NotNull(result);
            return (result!.Value, said);
        }

        [Fact]
        public async Task ASamplingJob_QueuesEachBatchAsItIsDrawn_SaysSo_AndEndsWhenItsTimeIsUp()
        {
            // Everything is embedded first (a message without a vector is never sampled).
            await driver.RunUntilIdleAsync();
            driver.AssertNoFailures();
            Assert.Equal(0, Queued());

            // No time at all: a job still draws one batch, queues what it found, says where it stands and ends.
            await rt.Host.Settings.SetAsync(ClassificationModule.TeacherTargetSetting, 700, "test");
            ClassificationModule.TeacherSampleBudget = TimeSpan.Zero;
            ClassificationModule.TeacherSampleProbes = 24;
            long before = Queued();
            var (first, said) = await NextSampleAsync();
            int added = first.GetProperty("added").GetInt32();
            Assert.InRange(added, 1, 24);
            Assert.True(first.GetProperty("more").GetBoolean());
            Assert.Equal(before, first.GetProperty("have").GetInt64());
            // What it drew is in the queue, not in the job's memory: a job stopped after this point loses nothing.
            Assert.Equal(before + added, Queued());
            // One batch, one report of where the sample stands (what tells the lane's watch the job is alive).
            Assert.Equal(before + added, Assert.Single(said).GetProperty("have").GetInt64());
            Assert.Equal(added, first.GetProperty("byTier").EnumerateObject().Sum(t => t.Value.GetInt32()));

            // The next one carries on from what is queued.
            var (second, _) = await NextSampleAsync();
            Assert.Equal(before + added, second.GetProperty("have").GetInt64());
            Assert.True(Queued() > before + added);

            // Given its time again, sampling reaches the target as before.
            ClassificationModule.TeacherSampleBudget = TimeSpan.FromMinutes(4);
            ClassificationModule.TeacherSampleProbes = 64;
            for (int i = 0; i < 6 && Queued() < 500; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(11));
                await driver.RunUntilIdleAsync();
            }
            driver.AssertNoFailures();
            Assert.True(Queued() >= 500, $"queued {Queued()} of a target of 700");
        }
    }
}
