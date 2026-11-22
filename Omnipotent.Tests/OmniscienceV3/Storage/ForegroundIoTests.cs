using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Storage
{
    /// <summary>
    /// Whoever is waiting for an answer is read first. On the server the engine's stores share one slow drive with seven
    /// jobs' reads, so a list of nine jobs took one to two seconds, the status the page asks for every ten seconds had a
    /// median of 1.2 s, and nearly every read reached the four-second limit at its 95th percentile (21 Nov 2026). These
    /// tests hold the stores to: while a request is being answered a job's reads wait (between statements and in the
    /// middle of one), a request's own reads never do, and neither side can hold the other back for long.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class ForegroundIoTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_foreground_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc)));
        private readonly (bool Enabled, TimeSpan Hold, TimeSpan Yield, TimeSpan Run) before = (ForegroundIo.Enabled, ForegroundIo.MaxHold, ForegroundIo.MaxYield, ForegroundIo.MinRun);
        private OmniscienceStores stores = null!;

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            // Long enough that a test which is not about a limit never meets one.
            (ForegroundIo.Enabled, ForegroundIo.MaxHold, ForegroundIo.MaxYield, ForegroundIo.MinRun) = (true, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.Zero);
        }

        public async Task DisposeAsync()
        {
            (ForegroundIo.Enabled, ForegroundIo.MaxHold, ForegroundIo.MaxYield, ForegroundIo.MinRun) = before;
            await stores.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(10);
            }
        }

        /// <summary>A statement that never ends by itself.</summary>
        private const string Endless = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT COUNT(*) FROM n";

        /// <summary>One read as a job makes it: on a task that carries the job's cancellation.</summary>
        private Task<long> JobRead(CancellationToken job, string sql = "SELECT 1", bool foreground = false) => Task.Run(() =>
        {
            AmbientCancellation.Set(job, foreground);
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<long>(null, sql);
        });

        private sealed class Dispatcher : IJobDispatcher
        {
            public Func<JobContext, Task<string?>> Handler { get; set; } = _ => Task.FromResult<string?>(null);
            public JobHandler? Resolve(string moduleId, string kind) => ctx => Handler(ctx);
        }

        [Fact]
        public async Task AJobSomebodyIsWaitingFor_IsARequest_AndNeverStandsBack()
        {
            using var job = new CancellationTokenSource();
            using var request = ForegroundIo.Enter(); // the request that is waiting for that very job, for instance
            long waits = ForegroundIo.Yields;
            Assert.Equal(1, await JobRead(job.Token, foreground: true).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(waits, ForegroundIo.Yields);

            // The lanes say which jobs those are: the interactive lane's (a tactic run by hand, a brief asked for on a page).
            var queue = new JobQueue(stores.Intel, clock);
            long read = 0;
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    using var c = stores.Corpus.OpenRead();
                    read = c.Scalar<long>(null, "SELECT 41 + 1");
                    return Task.FromResult<string?>("{}");
                },
            };
            var lanes = new[]
            {
                new LaneOptions(Lane.LlmInteractive, 1, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)),
                new LaneOptions(Lane.Cpu, 1, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)),
            };
            await using var w = new LaneWorkers(queue, d, clock, lanes, (_, _) => { });
            w.Start();
            long asked = await queue.EnqueueAsync(new JobRequest { ModuleId = "core.tactics", Kind = "run", Lane = Lane.LlmInteractive });
            await WaitUntil(() => queue.Get(asked)!.State == JobStates.Succeeded);
            Assert.Equal(42, Volatile.Read(ref read));
            Assert.Equal(waits, ForegroundIo.Yields);

            // The same handler as a background job waits for the request, as every other job does.
            long background = await queue.EnqueueAsync(new JobRequest { ModuleId = "explore", Kind = "mirror", Lane = Lane.Cpu });
            await WaitUntil(() => ForegroundIo.Yielding == 1);
            Assert.Equal(JobStates.Leased, queue.Get(background)!.State);
            request.Dispose();
            await WaitUntil(() => queue.Get(background)!.State == JobStates.Succeeded);
        }

        [Fact]
        public async Task AJobsRead_WaitsWhileARequestIsBeingAnswered_AndIsMadeTheMomentItIs()
        {
            using var job = new CancellationTokenSource();
            long waits = ForegroundIo.Yields;
            Assert.False(ForegroundIo.Busy);
            Assert.Equal(1, await JobRead(job.Token).WaitAsync(TimeSpan.FromSeconds(10))); // nobody is asking: nothing to wait for
            Assert.Equal(waits, ForegroundIo.Yields);

            var request = ForegroundIo.Enter();
            Assert.True(ForegroundIo.Busy);
            var read = JobRead(job.Token);
            await WaitUntil(() => ForegroundIo.Yielding == 1);
            await Task.Delay(150);
            Assert.False(read.IsCompleted);

            // The request's own reads are not held up by anything, the waiting job included.
            using (var c = stores.Corpus.OpenRead()) Assert.Equal(1, c.Scalar<long>(null, "SELECT 1"));
            using (var c = stores.Intel.OpenRead()) Assert.Equal(1, c.Scalar<long>(null, "SELECT 1"));
            Assert.False(read.IsCompleted);

            request.Dispose();
            request.Dispose(); // answered once, however often it is said
            Assert.False(ForegroundIo.Busy);
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, ForegroundIo.Yielding);
            Assert.True(ForegroundIo.Yields > waits);
            Assert.True(ForegroundIo.Yielded >= TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task AStatementThatRunsForMinutes_GivesWayInTheMiddle_AndStillStopsWhenItsJobIsToldTo()
        {
            using var job = new CancellationTokenSource();
            var read = JobRead(job.Token, Endless);
            await Task.Delay(100);
            Assert.False(read.IsCompleted);
            Assert.Equal(0, ForegroundIo.Yielding); // reading, with nobody to wait for

            using (ForegroundIo.Enter())
            {
                // It is inside one statement and will be for ever: only the statement itself can give way.
                await WaitUntil(() => ForegroundIo.Yielding == 1);

                // A job that is told to stop while it stands back stops at once: it does not wait for the request first.
                job.Cancel();
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(AmbientCancellation.IsStop(ex, job.Token), ex.ToString());
                Assert.Equal(0, ForegroundIo.Yielding);
            }

            // A request that reads next (on a connection from the same pool) reads straight through a long statement of its
            // own while another request is being answered.
            using (ForegroundIo.Enter())
            using (var c = stores.Corpus.OpenRead())
                Assert.Equal(200_000, c.Scalar<long>(null, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 200000) SELECT COUNT(*) FROM n"));
            Assert.Equal(0, ForegroundIo.Yielding);
        }

        [Fact]
        public async Task ARequestThatRunsLong_HoldsTheJobsBackOnlySoLong()
        {
            using var job = new CancellationTokenSource();
            ForegroundIo.MaxHold = TimeSpan.FromMilliseconds(300);
            using var export = ForegroundIo.Enter(); // an export, a stream: it is not answered for as long as this test runs
            Assert.True(ForegroundIo.Busy);
            var read = JobRead(job.Token);

            // The job's wait is a minute long (MaxYield); it reads because the request stopped counting, not because it gave up.
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ForegroundIo.Busy);

            // A request that arrives now counts afresh.
            ForegroundIo.MaxHold = TimeSpan.FromMinutes(1);
            using var next = ForegroundIo.Enter();
            Assert.True(ForegroundIo.Busy);
        }

        [Fact]
        public async Task WithRequestsArrivingWithoutABreak_AJobStillMoves()
        {
            using var job = new CancellationTokenSource();
            ForegroundIo.MaxYield = TimeSpan.FromMilliseconds(300);
            using var request = ForegroundIo.Enter();
            var read = JobRead(job.Token);
            Assert.Equal(1, await read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(ForegroundIo.Busy); // the request is still being answered: the job read because its own wait has a limit
        }

        [Fact]
        public async Task AfterWaitingItsLimit_AJobReadsForAWhile_BeforeItCanBeMadeToWaitAgain()
        {
            using var job = new CancellationTokenSource();
            (ForegroundIo.MaxYield, ForegroundIo.MinRun) = (TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(1));
            using var request = ForegroundIo.Enter(); // being answered for as long as this test runs
            long waits = ForegroundIo.Yields;

            // A statement of millions of steps looks for a request every few thousand: waiting its limit at each look, it
            // would stand back several hundred times and take minutes. It waits once and then reads.
            var read = JobRead(job.Token, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000000) SELECT COUNT(*) FROM n");
            Assert.Equal(1_000_000, await read.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.InRange(ForegroundIo.Yields - waits, 1, 2);
            Assert.True(ForegroundIo.Busy);
        }

        [Fact]
        public async Task SwitchedOff_NoJobWaits()
        {
            using var job = new CancellationTokenSource();
            ForegroundIo.Enabled = false;
            using var request = ForegroundIo.Enter();
            long waits = ForegroundIo.Yields;
            Assert.Equal(1, await JobRead(job.Token).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(waits, ForegroundIo.Yields);
        }

        private sealed class QuietModule : IntelModuleBase
        {
            public override ModuleManifest Manifest { get; } = new() { Id = "quiet", Version = 1, Title = "Quiet module" };

            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);
        }

        [Fact]
        public async Task OnceAnHour_TheKernelSaysHowOftenJobsStoodBack_AndNothingWhenNoneDid()
        {
            var lines = new ConcurrentQueue<string>();
            await using var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "engine")), Clock = clock, LlmClient = new WsC.FakeLlm(), Log = lines.Enqueue, LogError = (m, _) => lines.Enqueue(m),
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new QuietModule() },
            });
            await rt.Host.RunMaintenanceOnceAsync(); // the first look: counting starts here
            clock.Advance(ModuleHost.PacingReportEvery);
            await rt.Host.RunMaintenanceOnceAsync();
            Assert.DoesNotContain(lines, l => l.Contains("pacing:")); // an hour in which nothing waited

            using var job = new CancellationTokenSource();
            var request = ForegroundIo.Enter();
            var read = JobRead(job.Token);
            await WaitUntil(() => ForegroundIo.Yielding == 1);
            request.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(ModuleHost.PacingReportEvery);
            await rt.Host.RunMaintenanceOnceAsync();
            Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"pacing: jobs stood back for requests [12] time\(s\), \d+ s in all"));
        }

        [Fact]
        public async Task AJobThatReportsItsProgress_StandsBackThereToo()
        {
            // Work that reads files or another database never passes through a store's read: its progress report is where it gives way.
            using var job = new CancellationTokenSource();
            var request = ForegroundIo.Enter();
            var reported = Task.Run(async () =>
            {
                AmbientCancellation.Set(job.Token);
                await ForegroundIo.YieldIfBackgroundAsync();
            });
            await WaitUntil(() => ForegroundIo.Yielding == 1);
            await Task.Delay(100);
            Assert.False(reported.IsCompleted);
            request.Dispose();
            await reported.WaitAsync(TimeSpan.FromSeconds(10));

            // Outside a job the same call returns at once, whoever is asking.
            using (ForegroundIo.Enter())
            {
                Assert.True(ForegroundIo.YieldIfBackgroundAsync().IsCompleted);
                Assert.False(ForegroundIo.InBackground);
            }
        }
    }
}
