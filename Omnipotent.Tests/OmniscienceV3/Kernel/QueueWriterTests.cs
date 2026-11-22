using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Kernel
{
    /// <summary>
    /// The queue's database has one writer, and everything a job does to the queue (being leased, its heartbeat, its
    /// completion) goes through it. On the server a clean-up statement that ran at every start and every six hours read
    /// the row of every finished job inside that writer (some 400,000, to delete none): for about twenty minutes after
    /// each start no job was leased or completed on any lane, 27 writes stood waiting, and the only sign was that nothing
    /// finished (22 Nov 2026). These tests hold the kernel to a clean-up that reads a little, outside the writer, and to
    /// saying so when any one write holds a writer for long.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class QueueWriterTests : IAsyncLifetime
    {
        private const long Day = 86_400_000L;
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_writer_" + Guid.NewGuid().ToString("N"));
        private readonly long today = Instant.FromUtc(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        private readonly FixedClock clock;
        private readonly (int Batch, int Batches, TimeSpan Slow) before = (JobQueue.PruneBatch, JobQueue.PruneBatches, WriterQueue.SlowOperation);
        private OmniscienceStores stores = null!;
        private JobQueue queue = null!;

        public QueueWriterTests() => clock = new FixedClock(today);

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            queue = new JobQueue(stores.Intel, clock);
        }

        public async Task DisposeAsync()
        {
            (JobQueue.PruneBatch, JobQueue.PruneBatches, WriterQueue.SlowOperation) = before;
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private long Count(string state)
        {
            using var c = stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM jobs WHERE state=$s", ("$s", state));
        }

        private long Counted(string state)
        {
            using var c = stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT SUM(n) FROM job_counts WHERE state=$s", ("$s", state)) ?? 0;
        }

        /// <summary>Jobs of one module that ended <paramref name="daysAgo"/> days ago in <paramref name="state"/>.</summary>
        private async Task<List<long>> EndedAsync(int count, int daysAgo, string state, string module = "media.store")
        {
            var ids = new List<long>();
            clock.Set(today - daysAgo * Day);
            for (int i = 0; i < count; i++)
            {
                long id = await queue.EnqueueAsync(new JobRequest { ModuleId = module, Kind = "store", SubjectKind = "attachment", SubjectId = Guid.NewGuid().ToString("N"), Lane = Lane.Io, MaxAttempts = 1 });
                ids.Add(id);
                if (state == JobStates.Queued) continue;
                if (state == JobStates.Cancelled) { await queue.CancelAsync(id); continue; }
                var job = await queue.LeaseNextAsync(Lane.Io, "w", TimeSpan.FromMinutes(1));
                Assert.Equal(id, job!.JobId);
                if (state == JobStates.Succeeded) await queue.CompleteAsync(id, "w", "{}");
                else await queue.FailAsync(id, "w", ErrorClass.Permanent, "gone");
            }
            clock.Set(today);
            return ids;
        }

        [Fact]
        public async Task FinishedJobsPastTheirTime_GoAFewAtATime_OldestFirst_AndTheLookStopsWhereNothingIsOldEnough()
        {
            (JobQueue.PruneBatch, JobQueue.PruneBatches) = (50, 2);
            var old = await EndedAsync(130, daysAgo: 20, JobStates.Succeeded);
            var recent = await EndedAsync(70, daysAgo: 1, JobStates.Succeeded);
            var cancelledOld = await EndedAsync(5, daysAgo: 20, JobStates.Cancelled);
            var deadOld = await EndedAsync(4, daysAgo: 100, JobStates.Dead);
            var deadKept = await EndedAsync(6, daysAgo: 20, JobStates.Dead); // dead jobs are kept ninety days
            var waiting = await EndedAsync(8, daysAgo: 30, JobStates.Queued); // never finished: never removed

            // The look reads its rows in job order from the (state, job) index: no sort, no walk of the table.
            using (var c = stores.Intel.OpenRead())
            {
                var plan = c.Query(null, "EXPLAIN QUERY PLAN " + JobQueue.PruneLookSql, r => r.GetString(3), ("$s", JobStates.Succeeded), ("$after", 0L), ("$n", 50));
                Assert.Contains(plan, p => p.Contains("ix_jobs_state"));
                Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE") || p.StartsWith("SCAN", StringComparison.Ordinal));
            }

            // One round: two looks of fifty for the finished ones, and it says there is more.
            long looked = JobQueue.PruneLooked;
            var first = await queue.PruneFinishedAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(90));
            Assert.Equal((100 + 5 + 4, true), first);
            Assert.Equal(30 + 70, Count(JobStates.Succeeded));
            Assert.InRange(JobQueue.PruneLooked - looked, 1, 100 + 5 + 10);
            Assert.Equal(old.Skip(100).First(), stores.Intel.OpenReadScalar("SELECT MIN(job_id) FROM jobs WHERE state='succeeded'")); // the oldest went first

            // The next round takes the thirty that are left and stops at the jobs of yesterday.
            looked = JobQueue.PruneLooked;
            var second = await queue.PruneFinishedAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(90));
            Assert.Equal((30, false), second);
            Assert.Equal(70, Count(JobStates.Succeeded));
            Assert.Equal(0, Count(JobStates.Cancelled));
            Assert.Equal(6, Count(JobStates.Dead));
            Assert.Equal(8, Count(JobStates.Queued));

            // With nothing old enough a round is one look per state, however many finished jobs there are.
            looked = JobQueue.PruneLooked;
            Assert.Equal((0, false), await queue.PruneFinishedAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(90)));
            Assert.InRange(JobQueue.PruneLooked - looked, 1, 50 + 6);

            // The counters the pages read stayed exact through the deletes.
            foreach (string state in new[] { JobStates.Succeeded, JobStates.Cancelled, JobStates.Dead, JobStates.Queued })
                Assert.Equal(Count(state), Counted(state));
            Assert.Equal(recent.Count, Count(JobStates.Succeeded));
            Assert.True(deadKept.Count == 6 && waiting.Count == 8 && cancelledOld.Count == 5 && deadOld.Count == 4);
        }

        [Fact]
        public async Task AJobMadeLongAgoThatEndedLately_StaysAndHidesNothingOfItsBatch()
        {
            (JobQueue.PruneBatch, JobQueue.PruneBatches) = (20, 4);
            // The lowest job id of all: made thirty days ago, finished an hour ago.
            clock.Set(today - 30 * Day);
            long late = await queue.EnqueueAsync(new JobRequest { ModuleId = "explore", Kind = "mirror", Lane = Lane.Cpu });
            clock.Set(today - 3_600_000L);
            Assert.Equal(late, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            await queue.CompleteAsync(late, "w", "{}");
            await EndedAsync(30, daysAgo: 20, JobStates.Succeeded);

            Assert.Equal((30, false), await queue.PruneFinishedAsync(TimeSpan.FromDays(14), TimeSpan.FromDays(90)));
            Assert.Equal(JobStates.Succeeded, queue.Get(late)!.State);
            Assert.Equal(1, Count(JobStates.Succeeded));
        }

        private sealed class QuietModule : IntelModuleBase
        {
            public override ModuleManifest Manifest { get; } = new() { Id = "quiet", Version = 1, Title = "Quiet module" };

            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);
        }

        [Fact]
        public async Task TheFirstMaintenanceAfterAStart_ReadsAHandfulOfFinishedJobs_NotAllOfThem()
        {
            (JobQueue.PruneBatch, JobQueue.PruneBatches) = (25, 2);
            string home = Path.Combine(root, "engine");
            await using var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(home), Clock = clock, LlmClient = new WsC.FakeLlm(), Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new QuietModule() },
            });
            var jobs = rt.Host.Jobs;
            for (int i = 0; i < 300; i++)
            {
                long id = await jobs.EnqueueAsync(new JobRequest { ModuleId = "quiet", Kind = "k", SubjectKind = "n", SubjectId = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Lane = Lane.Cpu });
                Assert.Equal(id, (await jobs.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
                await jobs.CompleteAsync(id, "w", "{}");
            }

            // What every start does first. Three hundred finished jobs, none old enough: one look of twenty-five, and the
            // writer is asked for nothing on their account.
            long looked = JobQueue.PruneLooked;
            await rt.Host.RunMaintenanceOnceAsync();
            Assert.InRange(JobQueue.PruneLooked - looked, 1, 25);
            using var c = rt.Stores.Intel.OpenRead();
            Assert.Equal(300, c.Scalar<long>(null, "SELECT COUNT(*) FROM jobs WHERE state='succeeded'"));
        }

        [Fact]
        public async Task AWriteThatHoldsAWriterForLong_IsSaid_WithWhoAskedForIt_AndAnOrdinaryOneIsNot()
        {
            WriterQueue.SlowOperation = TimeSpan.FromMilliseconds(80);
            var lines = new ConcurrentQueue<string>();
            stores.Intel.SlowWriteLog = lines.Enqueue;

            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE jobs SET priority = priority WHERE job_id = -1"));
            Assert.Empty(lines);

            var slow = stores.Intel.WriteAsync((c, tx) => Thread.Sleep(250));
            var behind = stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE jobs SET priority = priority WHERE job_id = -1"));
            await Task.WhenAll(slow, behind);

            string line = Assert.Single(lines);
            Assert.Contains("intel.db: one write (QueueWriterTests.AWriteThatHoldsAWriterForLong_IsSaid_WithWhoAskedForIt_AndAnOrdinaryOneIsNot) held the writer for", line);
            Assert.Contains("other write(s) waited behind it", line);
        }
    }

    internal static class QueueWriterTestReads
    {
        /// <summary>One number read from a store on a read connection.</summary>
        public static long OpenReadScalar(this SqliteStore store, string sql)
        {
            using var c = store.OpenRead();
            return c.Scalar<long>(null, sql);
        }
    }
}
