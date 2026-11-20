using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Kernel
{
    /// <summary>
    /// What 18 dead jobs on the server came down to (20 Nov 2026). A job stopped for making no progress "did not stop when
    /// asked" 64 times in sixteen hours, because its handler sat in a database read that knows nothing of cancellation; the
    /// handler was left running and the queue started the same job again beside it (ten copies of a 15 GB backup); and
    /// every running job lost its lease in the same second, four times, because heartbeats could not run while the thread
    /// pool was backed up. These tests hold the kernel to: a read stops with its job, a job is never run twice at once, and
    /// a lease is not taken from a handler that is running here.
    /// </summary>
    [Collection(nameof(KernelStuckWorkCollection))]
    public class KernelStuckWorkTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_stuck_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private JobQueue queue = null!;
        private readonly ConcurrentQueue<string> logs = new();
        private readonly TimeSpan grace = LaneWorkers.StallGrace, retryDelay = LaneWorkers.LeftBehindRetryDelay;
        private readonly int maxLeftBehind = LaneWorkers.MaxLeftBehind;

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            queue = new JobQueue(stores.Intel, clock);
            LaneWorkers.StallGrace = TimeSpan.FromMilliseconds(250);
            LaneWorkers.LeftBehindRetryDelay = TimeSpan.FromMinutes(5);
        }

        public async Task DisposeAsync()
        {
            LaneWorkers.StallGrace = grace;
            LaneWorkers.LeftBehindRetryDelay = retryDelay;
            LaneWorkers.MaxLeftBehind = maxLeftBehind;
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private void Log(string m, Exception? e = null) => logs.Enqueue(e == null ? m : m + " " + e.Message);

        private static JobRequest Req(string module, string kind, int maxAttempts = 3) =>
            new() { ModuleId = module, Kind = kind, Priority = 100, Lane = Lane.Io, MaxAttempts = maxAttempts };

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(20);
            }
        }

        private sealed class Dispatcher : IJobDispatcher
        {
            public Func<JobContext, Task<string?>> Handler { get; set; } = _ => Task.FromResult<string?>(null);
            public JobHandler? Resolve(string moduleId, string kind) => ctx => Handler(ctx);
        }

        private LaneWorkers Workers(Dispatcher d, int concurrency = 1) =>
            new(queue, d, clock, new[] { new LaneOptions(Lane.Io, concurrency, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(300), MaxPerModule: concurrency) }, Log);

        /// <summary>A statement that never ends by itself.</summary>
        private const string Endless = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT COUNT(*) FROM n";

        [Fact]
        public async Task AReadInsideAJob_StopsWhenTheJobIsToldToStop()
        {
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    // A handler that never looks at its token: it only reads, as the evidence gathering and the sweeps do.
                    using var c = stores.Corpus.OpenRead();
                    c.Scalar<long>(null, Endless);
                    return Task.FromResult<string?>("{}");
                },
            };
            long id = await queue.EnqueueAsync(Req("knowledge.deduction", "extract", maxAttempts: 1));
            await using var w = Workers(d);
            w.Start();

            await WaitUntil(() => queue.Get(id)!.State == JobStates.Dead);

            // Stopped, not left behind: nothing of it is still running.
            Assert.Equal("Made no progress for 0.3 s; stopped.", queue.Get(id)!.ErrorMessage);
            Assert.Empty(w.LeftBehind);
            Assert.DoesNotContain(logs, l => l.Contains("did not stop when asked"));
        }

        [Fact]
        public async Task OutsideAJob_AReadIsNeverInterrupted_AndACancelledJobStartsNoNewRead()
        {
            using (var c = stores.Corpus.OpenRead()) Assert.Equal(1, c.Scalar<long>(null, "SELECT 1"));

            using var cts = new CancellationTokenSource();
            await Task.Run(() =>
            {
                AmbientCancellation.Set(cts.Token);
                using (var c = stores.Corpus.OpenRead())
                {
                    Assert.Equal(1, c.Scalar<long>(null, "SELECT 1"));
                    cts.Cancel();
                    // Between statements an interrupt has nothing to stop; the next read must not begin.
                    Assert.Throws<OperationCanceledException>(() => c.Query(null, "SELECT 1", r => r.GetInt64(0)));
                }
                Assert.Throws<OperationCanceledException>(() => stores.Corpus.OpenRead());
            });

            // The token belonged to that task alone, and the connection it used went back to the pool clean.
            using (var c = stores.Corpus.OpenRead()) Assert.Equal(1, c.Scalar<long>(null, "SELECT 1"));
        }

        [Fact]
        public async Task AJobLeftBehind_IsNotStartedAgain_UntilItsFirstAttemptHasEnded()
        {
            var release = new ManualResetEventSlim(false);
            int starts = 0;
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    Interlocked.Increment(ref starts);
                    release.Wait(); // a native call: the copy of a database, an OCR engine
                    return Task.FromResult<string?>("{}");
                },
            };
            long id = await queue.EnqueueAsync(Req("datacontrols", "backup"));
            await using var w = Workers(d);
            w.Start();
            try
            {
                await WaitUntil(() => w.LeftBehind.Count == 1 && queue.Get(id)!.State == JobStates.Queued);
                Assert.Equal(id, w.LeftBehind[0].JobId);
                Assert.Equal(1, queue.Get(id)!.Attempts);

                // Its retry comes due while the first attempt is still running: it is put back, and the attempt is not counted.
                clock.Advance(TimeSpan.FromMinutes(1));
                await WaitUntil(() => queue.Get(id)!.ErrorMessage?.StartsWith("Waiting for an earlier attempt") == true);
                Assert.Equal(1, Volatile.Read(ref starts));
                Assert.Equal(JobStates.Queued, queue.Get(id)!.State);
                Assert.Equal(1, queue.Get(id)!.Attempts);

                // The first attempt ends by itself; from then on the job may run again, and does.
                release.Set();
                await WaitUntil(() => w.LeftBehind.Count == 0);
                clock.Advance(TimeSpan.FromMinutes(10));
                await WaitUntil(() => queue.Get(id)!.State == JobStates.Succeeded);
                Assert.Equal(2, Volatile.Read(ref starts));
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task AJobWhoseEarlierAttemptNeverComesBack_IsFailedForGood_AndNeverStartedBesideIt()
        {
            var release = new ManualResetEventSlim(false);
            int starts = 0;
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    Interlocked.Increment(ref starts);
                    release.Wait();
                    return Task.FromResult<string?>("{}");
                },
            };
            long id = await queue.EnqueueAsync(Req("media.text", "extract"));
            await using var w = Workers(d);
            w.Start();
            try
            {
                await WaitUntil(() => w.LeftBehind.Count == 1 && queue.Get(id)!.State == JobStates.Queued);

                // Hours later the first attempt has still not come back. A job that only ever waits would look healthy:
                // it is failed for good and says why, with attempts to spare.
                clock.Advance(LaneWorkers.LeftBehindGiveUp + TimeSpan.FromHours(1));
                await WaitUntil(() => queue.Get(id)!.State == JobStates.Dead);
                var job = queue.Get(id)!;
                Assert.Contains("is still running, so it was not started again beside it", job.ErrorMessage);
                Assert.Equal("Permanent", job.ErrorClass);
                Assert.True(job.Attempts < job.MaxAttempts + 1);
                Assert.Equal(1, Volatile.Read(ref starts));
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task OnlySoManyHandlersAreLeftBehind_ThenAWorkerStaysWithItsJob()
        {
            LaneWorkers.MaxLeftBehind = 1;
            var release = new ManualResetEventSlim(false);
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    release.Wait();
                    return Task.FromResult<string?>("{}");
                },
            };
            long a = await queue.EnqueueAsync(Req("media.text", "extract", maxAttempts: 1));
            long b = await queue.EnqueueAsync(Req("media.caption", "sweep", maxAttempts: 1));
            await using var w = Workers(d, concurrency: 2);
            w.Start();
            try
            {
                await WaitUntil(() => w.LeftBehind.Count == 1 && w.Stalled == 2);
                await Task.Delay(600); // well past the grace: the second one is still not abandoned
                Assert.Single(w.LeftBehind);
                var states = new[] { queue.Get(a)!.State, queue.Get(b)!.State };
                Assert.Contains(JobStates.Dead, states);
                Assert.Contains(JobStates.Leased, states);

                release.Set();
                await WaitUntil(() => queue.Get(a)!.State == JobStates.Dead && queue.Get(b)!.State == JobStates.Dead && w.LeftBehind.Count == 0);
                Assert.Contains(new[] { queue.Get(a)!.ErrorMessage, queue.Get(b)!.ErrorMessage }, m => m!.EndsWith("; stopped."));
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task ALeaseIsNotTakenFromAJobThatIsRunningHere()
        {
            long running = await queue.EnqueueAsync(Req("projections", "daily"));
            long gone = await queue.EnqueueAsync(Req("media.store", "sweep"));
            var first = await queue.LeaseNextAsync(Lane.Io, "w1", TimeSpan.FromMinutes(2));
            var second = await queue.LeaseNextAsync(Lane.Io, "w2", TimeSpan.FromMinutes(2));
            Assert.Equal(new[] { running, gone }.OrderBy(x => x), new[] { first!.JobId, second!.JobId }.OrderBy(x => x));

            // No heartbeat could run for five minutes (the process was held up); the handler of one of them is still running here.
            clock.Advance(TimeSpan.FromMinutes(5));
            int reclaimed = await queue.ReclaimExpiredAsync(new[] { running });

            Assert.Equal(1, reclaimed);
            Assert.Equal(JobStates.Leased, queue.Get(running)!.State);
            Assert.Equal(JobStates.Queued, queue.Get(gone)!.State);
            // Its next heartbeat still finds the lease its own.
            Assert.True(await queue.HeartbeatAsync(running, running == first.JobId ? "w1" : "w2", TimeSpan.FromMinutes(2)));
            // With nobody running it, the same lease is reclaimed as before.
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, await queue.ReclaimExpiredAsync());
            Assert.Equal(JobStates.Queued, queue.Get(running)!.State);
        }

        [Fact]
        public async Task AWorkersLeasesSurviveAStretchWithoutHeartbeats()
        {
            // The whole path: a job is running in a worker; its lease runs out on the clock; maintenance reclaims with what
            // the workers say is running. The job finishes as if nothing had happened.
            var release = new ManualResetEventSlim(false);
            var d = new Dispatcher { Handler = async ctx => { await Task.Run(() => release.Wait(ctx.Cancellation)); return "{}"; } };
            long id = await queue.EnqueueAsync(Req("projections", "daily", maxAttempts: 1));
            await using var w = new LaneWorkers(queue, d, clock, new[] { new LaneOptions(Lane.Io, 1, TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(20)) }, Log);
            w.Start();
            await WaitUntil(() => w.RunningJobIds.Contains(id));

            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(0, await queue.ReclaimExpiredAsync(w.RunningJobIds));
            release.Set();

            await WaitUntil(() => JobStates.IsTerminal(queue.Get(id)!.State));
            Assert.Equal(JobStates.Succeeded, queue.Get(id)!.State);
            Assert.Empty(w.RunningJobIds);
        }

        /// <summary>Queues a job, runs it out of attempts, and leaves it dead.</summary>
        private async Task<long> DeadAsync(string module, string kind, string? key)
        {
            // Ahead of anything already waiting, so it is the one leased next.
            long id = await queue.EnqueueAsync(Req(module, kind, maxAttempts: 1) with { DedupeKey = key, Priority = 10_000 });
            var leased = await queue.LeaseNextAsync(Lane.Io, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(id, leased!.JobId);
            Assert.Equal(JobStates.Dead, await queue.FailAsync(id, "w", ErrorClass.Transient, "Made no progress for 30 min and did not stop when asked."));
            return id;
        }

        [Fact]
        public async Task DeadJobs_AreRetriedTogether_AndOneWhoseWorkIsAlreadyQueuedAgainIsClosed()
        {
            // As on the server: two dead sweeps of the same recurring work, a dead backup whose next run is already queued,
            // and two jobs that stand alone.
            long olderSweep = await DeadAsync("media.store", "sweep", "media.store|sweep");
            long newerSweep = await DeadAsync("media.store", "sweep", "media.store|sweep");
            long extract = await DeadAsync("knowledge.deduction", "extract", null);
            long digest = await DeadAsync("briefs", "digest", "briefs|digest|d1");
            long backup = await DeadAsync("datacontrols", "backup", "datacontrols|backup|all");
            long tonight = await queue.EnqueueAsync(Req("datacontrols", "backup") with { DedupeKey = "datacontrols|backup|all" });

            // One module first: only its jobs are touched. Of the two sweeps the newer is retried and the older closed.
            Assert.Equal((1, 1), await queue.RetryAllDeadAsync("media.store"));
            Assert.Equal((JobStates.Queued, 0), (queue.Get(newerSweep)!.State, queue.Get(newerSweep)!.Attempts));
            Assert.Equal(JobStates.Cancelled, queue.Get(olderSweep)!.State);
            Assert.StartsWith("Closed: the same work was queued again. It had failed with: Made no progress", queue.Get(olderSweep)!.ErrorMessage);
            Assert.Equal(JobStates.Dead, queue.Get(extract)!.State);

            // Then all of them. The backup's next run is already waiting, so the dead one is closed, not queued beside it
            // (the database refuses two live jobs for the same work, and retrying it used to fail on that).
            Assert.Equal((2, 1), await queue.RetryAllDeadAsync(null));
            Assert.Equal(new[] { JobStates.Queued, JobStates.Queued, JobStates.Cancelled, JobStates.Queued },
                new[] { extract, digest, backup, tonight }.Select(id => queue.Get(id)!.State));
            Assert.Equal(0, queue.Counts().Where(c => c.State == JobStates.Dead).Sum(c => c.Count));
            Assert.Equal((0, 0), await queue.RetryAllDeadAsync(null));

            // One at a time behaves the same way.
            long again = await DeadAsync("links", "categorise", "links|categorise");
            long twin = await queue.EnqueueAsync(Req("links", "categorise") with { DedupeKey = "links|categorise" });
            Assert.True(await queue.RetryDeadAsync(again));
            Assert.Equal((JobStates.Cancelled, JobStates.Queued), (queue.Get(again)!.State, queue.Get(twin)!.State));
            Assert.False(await queue.RetryDeadAsync(again));
        }

        [Fact]
        public async Task JobsOfOneModule_AreListedNewestFirst_ByModule_ByKind_AndByState()
        {
            long sweep1 = await queue.EnqueueAsync(Req("media.store", "sweep"));
            long probe = await queue.EnqueueAsync(Req("media.store", "probe"));
            long other = await queue.EnqueueAsync(Req("projections", "daily"));
            long sweep2 = await queue.EnqueueAsync(Req("media.store", "sweep"));
            var leased = await queue.LeaseNextAsync(Lane.Io, "w", TimeSpan.FromMinutes(1));
            await queue.FailAsync(leased!.JobId, "w", ErrorClass.Permanent, "x");

            Assert.Equal(new[] { sweep2, probe, sweep1 }, queue.Query(new JobQuery { ModuleId = "media.store" }).Items.Select(j => j.JobId));
            Assert.Equal(new[] { sweep2, sweep1 }, queue.Query(new JobQuery { ModuleId = "media.store", Kind = "sweep" }).Items.Select(j => j.JobId));
            Assert.Equal(new[] { other }, queue.Query(new JobQuery { ModuleId = "projections", State = queue.Get(other)!.State }).Items.Select(j => j.JobId));
            Assert.Single(queue.Query(new JobQuery { ModuleId = "media.store", State = JobStates.Dead }).Items);
            // A page of one, and the cursor to the next.
            var page = queue.Query(new JobQuery { ModuleId = "media.store", Limit = 1 });
            Assert.Equal(sweep2, page.Items.Single().JobId);
            Assert.Equal(new[] { probe }, queue.Query(new JobQuery { ModuleId = "media.store", Limit = 1, BeforeJobId = page.NextCursor }).Items.Select(j => j.JobId));

            // The index that serves each listing hands the rows back in job order: nothing is sorted.
            using var c = stores.Intel.OpenRead();
            foreach (var (index, where) in new[] { ("ix_jobs_module", "module_id='m'"), ("ix_jobs_module_kind", "module_id='m' AND kind='k'"), ("ix_jobs_module_state", "module_id='m' AND state='queued'") })
            {
                var plan = c.Query(null, $"EXPLAIN QUERY PLAN SELECT job_id FROM jobs INDEXED BY {index} WHERE {where} ORDER BY job_id DESC LIMIT 5", r => r.GetString(3));
                Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE"));
            }
        }
    }

    /// <summary>These tests set the kernel's process-wide timings (the grace, the retry delay): they do not run beside each other.</summary>
    [CollectionDefinition(nameof(KernelStuckWorkCollection), DisableParallelization = true)]
    public class KernelStuckWorkCollection { }
}
