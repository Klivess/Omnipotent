using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Kernel
{
    /// <summary>
    /// What the first two days on the server showed (19 Nov 2026): 650,000 queued media jobs outranked every other module
    /// in their lanes, four workers sat for four hours on jobs that had hung, and the status page recounted a million rows
    /// per request. These tests hold the queue to: modules take turns, one module cannot occupy every worker, a job that
    /// makes no progress is stopped, and the counts a page shows come from a small table that stays exact.
    /// </summary>
    public class KernelFairnessTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_fair_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private JobQueue queue = null!;
        private readonly ConcurrentQueue<string> logs = new();

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            queue = new JobQueue(stores.Intel, clock);
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private void Log(string m, Exception? e = null) => logs.Enqueue(e == null ? m : m + " " + e.Message);

        private static JobRequest Req(string module, string kind = "k", int priority = 100, Lane lane = Lane.Cpu, int maxAttempts = 3) =>
            new() { ModuleId = module, Kind = kind, Priority = priority, Lane = lane, MaxAttempts = maxAttempts };

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(20);
            }
        }

        // ── leasing ──────────────────────────────────────────────────────────────

        /// <summary>Queues <paramref name="count"/> jobs of one module in one transaction (a backlog, as an import leaves one).</summary>
        private Task BacklogAsync(string module, int count, int priority, Lane lane = Lane.Cpu) =>
            stores.Intel.WriteAsync((c, tx) =>
            {
                for (int i = 0; i < count; i++) queue.EnqueueCore(c, tx, Req(module, "bulk", priority, lane));
            });

        private async Task<List<JobRecord>> LeaseAsync(int n, Lane lane = Lane.Cpu)
        {
            var leased = new List<JobRecord>();
            for (int i = 0; i < n; i++)
                if (await queue.LeaseNextAsync(lane, "w", TimeSpan.FromMinutes(1)) is { } job) leased.Add(job);
            return leased;
        }

        [Fact]
        public async Task ABacklogInOneModule_DoesNotMakeTheOthersWait()
        {
            // The media backlog as it stood: far more jobs than a module normally queues, each at a higher priority than
            // anything the other modules ask for.
            await BacklogAsync("media.store", JobQueue.BacklogJobs + 200, priority: 214);
            long sweep = await queue.EnqueueAsync(Req("knowledge.deduction", "sweep", priority: 50));
            long project = await queue.EnqueueAsync(Req("projections", "people", priority: 90));

            var first = await LeaseAsync(4);

            // By priority alone both would have run after all 1,200 probes. Now they run first, the higher priority before
            // the lower, and the backlog has the lane whenever nobody else is ready.
            Assert.Equal(new[] { project, sweep }, first.Take(2).Select(j => j.JobId).ToArray());
            Assert.All(first.Skip(2), j => Assert.Equal("media.store", j.ModuleId));
        }

        [Fact]
        public async Task ABacklog_StillGetsItsShare_WhileOthersAreBusy_AndBacklogsTakeTurns()
        {
            await BacklogAsync("media.store", JobQueue.BacklogJobs + 50, priority: 110);
            await BacklogAsync("media.visual", JobQueue.BacklogJobs + 50, priority: 100);
            for (int i = 0; i < 30; i++) await queue.EnqueueAsync(Req("rollups", "drain", priority: 150));

            var modules = (await LeaseAsync(12)).Select(j => j.ModuleId).ToList();

            // One lease in four goes to a backlog while ordinary work is waiting; the two backlogs alternate.
            Assert.Equal(9, modules.Count(m => m == "rollups"));
            Assert.Equal(new[] { "media.store", "media.visual", "media.store" }, modules.Where(m => m != "rollups").ToArray());
            Assert.Equal("rollups", modules[0]);
        }

        [Fact]
        public async Task AcrossOrdinaryModules_PriorityDecides_AndWaitingCounts()
        {
            long old = await queue.EnqueueAsync(Req("insights", "refresh", priority: 50));
            clock.Advance(TimeSpan.FromHours(10)); // sixty ten-minute steps: it now counts as 110
            long high = await queue.EnqueueAsync(Req("rollups", "drain", priority: 150));
            long mid = await queue.EnqueueAsync(Req("projections", "people", priority: 100));

            Assert.Equal(new[] { high, old, mid }, (await LeaseAsync(3)).Select(j => j.JobId).ToArray());

            // Nothing was written to make that so: the stored priority is what the module asked for.
            Assert.Equal(50, queue.Get(old)!.Priority);
        }

        [Fact]
        public async Task WithinAModule_PriorityThenAgeStillDecide()
        {
            long low = await queue.EnqueueAsync(Req("m", "low", priority: 10));
            long high = await queue.EnqueueAsync(Req("m", "high", priority: 200));
            long alsoHigh = await queue.EnqueueAsync(Req("m", "also-high", priority: 200));
            Assert.Equal(high, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Equal(alsoHigh, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Equal(low, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Null(await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)));
        }

        [Fact]
        public async Task AModuleWhoseJobsAreBackingOff_IsPassedOver()
        {
            long a = await queue.EnqueueAsync(Req("a"));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(JobStates.Queued, await queue.FailAsync(a, "w", ErrorClass.Transient, "later"));
            long b = await queue.EnqueueAsync(Req("b"));
            Assert.Equal(b, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Null(await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(a, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
        }

        [Fact]
        public async Task OneModule_CannotHoldMoreThanItsShareOfALane()
        {
            for (int i = 0; i < 10; i++) await queue.EnqueueAsync(Req("media.text", lane: Lane.Io));
            // Four workers, three at most for one module: the fourth stays free.
            for (int i = 0; i < 3; i++) Assert.NotNull(await queue.LeaseNextAsync(Lane.Io, "w" + i, TimeSpan.FromMinutes(1), maxPerModule: 3));
            Assert.Null(await queue.LeaseNextAsync(Lane.Io, "w3", TimeSpan.FromMinutes(1), maxPerModule: 3));

            // ...for the module that had been waiting behind it: the v2 tail, which is how v3 gets its messages.
            long tail = await queue.EnqueueAsync(Req("ingest.legacy", "sync", priority: 50, lane: Lane.Io));
            Assert.Equal(tail, (await queue.LeaseNextAsync(Lane.Io, "w3", TimeSpan.FromMinutes(1), maxPerModule: 3))!.JobId);
        }

        [Fact]
        public void LaneDefaults_GiveAFourCoreHostTwoCpuWorkers_AndEveryLaneAStallLimit()
        {
            var lanes = KernelOptions.DefaultLanes(4);
            Assert.Equal(2, lanes.Single(l => l.Lane == Lane.Cpu).Concurrency); // was 1: Math.Max(1, Math.Min(4, 4 - 4))
            Assert.Equal(4, KernelOptions.DefaultLanes(16).Single(l => l.Lane == Lane.Cpu).Concurrency);
            Assert.All(lanes, l => Assert.True(l.StallAfter > TimeSpan.Zero));
            Assert.Equal(1, lanes.Single(l => l.Lane == Lane.Cpu).ModuleLimit);
            Assert.Equal(3, lanes.Single(l => l.Lane == Lane.Io).ModuleLimit);
            Assert.Equal(2, lanes.Single(l => l.Lane == Lane.LlmInteractive).ModuleLimit);
            Assert.Equal(TimeSpan.Zero, new KernelOptions().PriorityAgeAfter); // no rewrite of every queued row every ten minutes
        }

        // ── counters ─────────────────────────────────────────────────────────────

        private Dictionary<string, long> Counted()
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, "SELECT lane || '|' || module_id || '|' || state, n FROM job_counts WHERE n <> 0", r => (r.GetString(0), r.GetInt64(1)))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        }

        private Dictionary<string, long> Recounted()
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, "SELECT lane || '|' || module_id || '|' || state, COUNT(*) FROM jobs GROUP BY lane, module_id, state", r => (r.GetString(0), r.GetInt64(1)))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        }

        [Fact]
        public async Task Counters_StayExactThroughEveryTransition()
        {
            var ids = new List<long>();
            for (int i = 0; i < 12; i++) ids.Add(await queue.EnqueueAsync(Req(i % 2 == 0 ? "a" : "b", maxAttempts: 2)));
            await queue.EnqueueAsync(Req("c", lane: Lane.Io));
            Assert.Equal(Recounted(), Counted());

            var j1 = (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!;
            var j2 = (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!;
            var j3 = (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!;
            var j4 = (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromSeconds(1)))!;
            await queue.CompleteAsync(j1.JobId, "w", "{}");
            await queue.FailAsync(j2.JobId, "w", ErrorClass.Permanent, "bad input");
            await queue.FailAsync(j3.JobId, "w", ErrorClass.Transient, "later");
            Assert.Equal(Recounted(), Counted());

            clock.Advance(TimeSpan.FromMinutes(1));
            await queue.ReclaimExpiredAsync();           // j4's lease ran out
            await queue.CancelAsync(ids[^1]);
            await queue.RetryDeadAsync(j2.JobId);
            await queue.CancelModuleAsync("b");
            await queue.ReclaimOrphanedAsync();
            Assert.Equal(Recounted(), Counted());

            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM jobs WHERE state IN ('succeeded','cancelled')"));
            Assert.Equal(Recounted(), Counted());

            // What the pages read.
            var lanes = queue.Counts();
            Assert.Equal(Recounted().Where(kv => kv.Key.StartsWith("Cpu|") && kv.Key.EndsWith("|queued")).Sum(kv => kv.Value),
                lanes.Where(l => l.Lane == Lane.Cpu && l.State == JobStates.Queued).Sum(l => l.Count));
            Assert.Equal(1, lanes.Single(l => l.Lane == Lane.Io).Count);
        }

        [Fact]
        public async Task ModuleStats_RememberTheLastSuccessAndTheLastDeadJob_WithoutReadingTheJobsTable()
        {
            long ok = await queue.EnqueueAsync(Req("replica", "train.index"));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            clock.Advance(TimeSpan.FromSeconds(5));
            long okAt = clock.NowMs;
            await queue.CompleteAsync(ok, "w", null);

            long bad = await queue.EnqueueAsync(Req("replica", "train.index"));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            clock.Advance(TimeSpan.FromSeconds(5));
            await queue.FailAsync(bad, "w", ErrorClass.Permanent, "ArgumentException: Found a low surrogate char");
            await queue.EnqueueAsync(Req("replica", "eval"));

            var stats = queue.ModuleStats()["replica"];
            Assert.Equal(1, stats.Queued);
            Assert.Equal(1, stats.Dead);
            Assert.Equal(1, stats.Succeeded);
            Assert.Equal(okAt, stats.LastSucceededMs);
            Assert.Equal(clock.NowMs, stats.LastFailedMs);
            Assert.StartsWith("ArgumentException", stats.LastError);

            // The dead job is pruned or retried later: the module still says when it last failed.
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM jobs WHERE state='dead'"));
            Assert.Equal(0, queue.ModuleStats()["replica"].Dead);
            Assert.NotNull(queue.ModuleStats()["replica"].LastFailedMs);
        }

        // ── the stall watch ──────────────────────────────────────────────────────

        private sealed class Dispatcher : IJobDispatcher
        {
            public Func<JobContext, Task<string?>> Handler { get; set; } = _ => Task.FromResult<string?>(null);
            public JobHandler? Resolve(string moduleId, string kind) => ctx => Handler(ctx);
        }

        private LaneWorkers Workers(Dispatcher d, int concurrency, TimeSpan stallAfter) =>
            new(queue, d, clock, new[] { new LaneOptions(Lane.Io, concurrency, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20), stallAfter, MaxPerModule: concurrency) }, Log);

        [Fact]
        public async Task AJobThatMakesNoProgress_IsStopped_AndItsWorkerGoesOn()
        {
            var d = new Dispatcher
            {
                Handler = async ctx =>
                {
                    if (ctx.Job.Kind == "hang") await Task.Delay(Timeout.Infinite, ctx.Cancellation); // honours cancellation
                    return "{}";
                },
            };
            long hang = await queue.EnqueueAsync(Req("media.text", "hang", lane: Lane.Io, maxAttempts: 1));
            long next = await queue.EnqueueAsync(Req("ingest.legacy", "sync", lane: Lane.Io));
            await using var w = Workers(d, 1, TimeSpan.FromMilliseconds(300));
            w.Start();
            await WaitUntil(() => queue.Get(next)!.State == JobStates.Succeeded && queue.Get(hang)!.State == JobStates.Dead);
            Assert.Contains("Made no progress", queue.Get(hang)!.ErrorMessage);
            Assert.Equal(1, w.Stalled);
        }

        [Fact]
        public async Task AJobThatWillNotStop_IsLeftBehind_AndTheLaneKeepsMoving()
        {
            var release = new ManualResetEventSlim(false);
            var previousGrace = LaneWorkers.StallGrace;
            LaneWorkers.StallGrace = TimeSpan.FromMilliseconds(200);
            try
            {
                var d = new Dispatcher
                {
                    Handler = ctx =>
                    {
                        // A native call that never looks at the token: what held four Io workers for four hours.
                        if (ctx.Job.Kind == "wedged") release.Wait();
                        return Task.FromResult<string?>("{}");
                    },
                };
                long wedged = await queue.EnqueueAsync(Req("media.text", "wedged", lane: Lane.Io, maxAttempts: 1));
                long next = await queue.EnqueueAsync(Req("ingest.legacy", "sync", lane: Lane.Io));
                await using var w = Workers(d, 1, TimeSpan.FromMilliseconds(300));
                w.Start();
                await WaitUntil(() => queue.Get(next)!.State == JobStates.Succeeded && queue.Get(wedged)!.State == JobStates.Dead);
                Assert.Contains("did not stop when asked", queue.Get(wedged)!.ErrorMessage);
                Assert.Contains(logs, l => l.Contains("its worker has moved on"));
            }
            finally
            {
                release.Set();
                LaneWorkers.StallGrace = previousGrace;
            }
        }

        [Fact]
        public async Task AJobThatReportsProgress_IsNotStopped()
        {
            var d = new Dispatcher
            {
                Handler = async ctx =>
                {
                    // Runs four times as long as the limit, and says so as it goes (an import, a training run).
                    for (int i = 0; i < 12; i++)
                    {
                        await Task.Delay(100, ctx.Cancellation);
                        await ctx.ReportProgressAsync(new { step = i });
                    }
                    return "{}";
                },
            };
            long id = await queue.EnqueueAsync(Req("ingest.legacy", "sync", lane: Lane.Io, maxAttempts: 1));
            await using var w = Workers(d, 1, TimeSpan.FromMilliseconds(300));
            w.Start();
            await WaitUntil(() => JobStates.IsTerminal(queue.Get(id)!.State));
            Assert.Equal(JobStates.Succeeded, queue.Get(id)!.State);
            Assert.Equal(0, w.Stalled);
        }
    }
}
