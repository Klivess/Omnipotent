using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Projections;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Synthetic;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The projections that died or stalled on the server (20 Nov 2026): the daily pass over 45,969 people began again at
    /// the first person on every attempt and was dead after its fifth, about 800 people in; the health view, rebuilt every
    /// minute, counted and read the whole jobs table and once ran two hours without finishing.
    /// </summary>
    [Collection(nameof(ProjectionResumeCollection))]
    public class ProjectionResumeTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_projresume_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private readonly int checkpoint = ProjectionModule.DailyCheckpoint;
        private V3Runtime rt = null!;
        private JobDriver driver = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root), Clock = clock, LlmClient = new FakeLlm(), Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new ClassificationModule(), new RollupModule(), new ProjectionModule() },
                Encoder = new HashingTextEncoder(128),
            });
            driver = new JobDriver(rt, clock);
            await SyntheticCorpus.GenerateAsync(rt.Stores, clock, new SyntheticCorpusOptions
            {
                Seed = 7, Messages = 600, People = 30, Guilds = 1, ChannelsPerGuild = 2, DirectChats = 4, WhatsAppGroups = 0, Span = TimeSpan.FromDays(60),
            });
            await rt.Host.Settings.SetAsync(ClassificationModule.PriorityBacklogSetting, 10_000_000, "test");
            await driver.RunUntilIdleAsync(2_000);
            driver.AssertNoFailures();
            ProjectionModule.DailyCheckpoint = 5;
        }

        public async Task DisposeAsync()
        {
            ProjectionModule.DailyCheckpoint = checkpoint;
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private T Scalar<T>(SqliteStore store, string sql, params (string, object?)[] args)
        {
            using var c = store.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        /// <summary>Leases a fresh daily job and runs it; <paramref name="stopAfterReports"/> cancels it after that many progress reports.</summary>
        private async Task<(string? Result, List<JsonElement> Reports)> RunDailyAsync(int? stopAfterReports = null)
        {
            long id = await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = ProjectionModule.ModuleId, Kind = "daily", Lane = Lane.Coordinator, Priority = 900, SubjectKind = "stream", SubjectId = "all" });
            var job = await rt.Host.Jobs.LeaseNextAsync(Lane.Coordinator, "test-driver", TimeSpan.FromMinutes(10));
            Assert.Equal(id, job!.JobId);
            using var cts = new CancellationTokenSource();
            var reports = new List<JsonElement>();
            var ctx = new JobContext(job, clock, cts.Token, p =>
            {
                reports.Add(JsonDocument.Parse(p).RootElement);
                if (reports.Count == stopAfterReports) cts.Cancel();
                return Task.FromResult(true);
            });
            var handler = rt.Host.Resolve(job.ModuleId, job.Kind)!;
            try
            {
                string? result = await handler(ctx);
                await rt.Host.Jobs.CompleteAsync(job.JobId, "test-driver", result);
                return (result, reports);
            }
            catch (OperationCanceledException)
            {
                await rt.Host.Jobs.FailAsync(job.JobId, "test-driver", ErrorClass.Permanent, "stopped by the test");
                return (null, reports);
            }
        }

        [Fact]
        public async Task TheDailyPass_CarriesOnWhereTheLastAttemptStopped_AndStartsAfreshTheNextDay()
        {
            long people = Scalar<long>(rt.Stores.Corpus, "SELECT COUNT(*) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE COALESCE(pc.canonical_id, p.person_id)=p.person_id");
            Assert.True(people > 20);

            // Today's pass ran when the engine started. The next day's first attempt is stopped after its second note of how
            // far it is: ten people in.
            clock.Advance(TimeSpan.FromDays(1));
            long firstAttemptAt = clock.NowMs;
            var (stopped, firstReports) = await RunDailyAsync(stopAfterReports: 2);
            Assert.Null(stopped);
            Assert.Equal(10, firstReports[^1].GetProperty("projected").GetInt32());
            Assert.Equal(10, Scalar<long>(rt.Stores.Intel, "SELECT seq FROM projection_cursors WHERE name=$n", ("$n", ProjectionModule.DailyCursor)) % ProjectionModule.DailyStride);

            // The next attempt does the rest, and only the rest.
            clock.Advance(TimeSpan.FromMinutes(1));
            var (result, reports) = await RunDailyAsync();
            using (var doc = JsonDocument.Parse(result!))
            {
                Assert.Equal(10, doc.RootElement.GetProperty("resumedAt").GetInt32());
                Assert.Equal(people, doc.RootElement.GetProperty("persons").GetInt64());
            }
            Assert.Equal(10, reports[0].GetProperty("resumedAt").GetInt32());
            Assert.Equal(10, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM projection_people WHERE as_of_ms=$t", ("$t", firstAttemptAt)));
            Assert.Equal(people - 10, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM projection_people WHERE as_of_ms=$t", ("$t", clock.NowMs)));

            // A pass that finished is not done twice on the same day's retry, and the next day's pass starts at the first person.
            var (again, _) = await RunDailyAsync();
            using (var doc = JsonDocument.Parse(again!)) Assert.Equal(people, doc.RootElement.GetProperty("resumedAt").GetInt64());
            clock.Advance(TimeSpan.FromDays(1));
            var (nextDay, _) = await RunDailyAsync();
            using (var doc = JsonDocument.Parse(nextDay!))
            {
                Assert.Equal(0, doc.RootElement.GetProperty("resumedAt").GetInt32());
                Assert.Equal(people, doc.RootElement.GetProperty("persons").GetInt64());
            }
            Assert.Equal(people, Scalar<long>(rt.Stores.Intel, "SELECT COUNT(*) FROM projection_people WHERE as_of_ms=$t", ("$t", clock.NowMs)));
        }

        [Fact]
        public async Task TheHealthView_ShowsWhatTheJobsTableHolds_WithoutReadingIt()
        {
            // A job of the projections module that failed for good, so there is something to say about it.
            long doomed = await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = ProjectionModule.ModuleId, Kind = "overview", Lane = Lane.Coordinator, Priority = 900 });
            var leased = await rt.Host.Jobs.LeaseNextAsync(Lane.Coordinator, "test-driver", TimeSpan.FromMinutes(10));
            Assert.Equal(doomed, leased!.JobId);
            await rt.Host.Jobs.FailAsync(doomed, "test-driver", ErrorClass.Permanent, "the disk was full");
            await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = RollupModule.ModuleId, Kind = "waiting", Lane = Lane.Coordinator, NotBeforeMs = clock.NowMs + 86_400_000L });

            await rt.Host.Modules.OfType<ProjectionModule>().Single().ProjectHealthAsync(default);

            using var doc = JsonDocument.Parse(rt.Host.ReadModels.Get(ProjectionModule.Health, "global")!.BodyJson);
            var byState = doc.RootElement.GetProperty("jobsByState");
            using (var c = rt.Stores.Intel.OpenRead())
                foreach (var (state, n) in c.Query(null, "SELECT state, COUNT(*) FROM jobs GROUP BY state", r => (r.GetString(0), r.GetInt64(1))))
                    Assert.Equal(n, byState.GetProperty(state).GetInt64());
            var modules = doc.RootElement.GetProperty("modules").EnumerateArray().ToDictionary(m => m.GetProperty("moduleId").GetString()!);
            Assert.Equal(1, modules[ProjectionModule.ModuleId].GetProperty("dead").GetInt64());
            Assert.Equal("the disk was full", modules[ProjectionModule.ModuleId].GetProperty("lastError").GetString());
            Assert.True(modules[ProjectionModule.ModuleId].GetProperty("lastSucceededMs").GetInt64() > 0);
            Assert.Equal(1, modules[RollupModule.ModuleId].GetProperty("queued").GetInt64());
            Assert.Equal(0, modules[RollupModule.ModuleId].GetProperty("dead").GetInt64());
            Assert.False(modules[RollupModule.ModuleId].TryGetProperty("lastError", out var none) && none.ValueKind == JsonValueKind.String);
        }

        [Fact]
        public void ATableThatOnlyGrows_IsCountedOnce_AndThenOnlyWhatWasAdded()
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            c.Exec(null, "CREATE TABLE messages(message_id TEXT PRIMARY KEY, content TEXT)");
            void Add(int from, int to)
            {
                for (int i = from; i < to; i++) c.Exec(null, "INSERT INTO messages VALUES($i, 'x')", ("$i", "m" + i));
            }
            var count = new ProjectionModule.GrowingCount("messages");
            long t = 1_000_000;

            Assert.Equal(0, count.Read(c, t));
            Add(0, 300);
            Assert.Equal(300, count.Read(c, t));
            Add(300, 420);
            Assert.Equal(420, count.Read(c, t + 60_000));

            // Rows that went away in between (someone forgotten) are caught by the full count, a few hours on.
            c.Exec(null, "DELETE FROM messages WHERE message_id IN ('m5','m6','m7')");
            Assert.Equal(420, count.Read(c, t + 120_000));
            Assert.Equal(417, count.Read(c, t + 7 * 3_600_000L));
            // And at once when the newest rows are the ones that went.
            c.Exec(null, "DELETE FROM messages WHERE message_id IN ('m419','m418')");
            Assert.Equal(415, count.Read(c, t + 7 * 3_600_000L + 1));
        }
    }

    /// <summary>These tests set how often the daily pass notes its place (process-wide): they do not run beside each other.</summary>
    [CollectionDefinition(nameof(ProjectionResumeCollection), DisableParallelization = true)]
    public class ProjectionResumeCollection { }
}
