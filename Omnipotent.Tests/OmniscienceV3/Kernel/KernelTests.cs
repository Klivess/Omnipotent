using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Kernel
{
    public class KernelTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_kernel_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2024, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
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

        private static JobRequest Req(string kind = "k", int priority = 100, string? key = null, Lane lane = Lane.Cpu, int maxAttempts = 3, long notBefore = 0) => new()
        {
            ModuleId = "test", Kind = kind, Priority = priority, DedupeKey = key, Lane = lane, MaxAttempts = maxAttempts, NotBeforeMs = notBefore,
        };

        // ── queue ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Enqueue_DedupesLiveJobsButNotFinishedOnes()
        {
            long a = await queue.EnqueueAsync(Req(key: "x"));
            long b = await queue.EnqueueAsync(Req(key: "x"));
            Assert.Equal(a, b);
            var leased = await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(a, leased!.JobId);
            Assert.Equal(a, await queue.EnqueueAsync(Req(key: "x"))); // still live (leased)
            Assert.True(await queue.CompleteAsync(a, "w", "{}"));
            long c = await queue.EnqueueAsync(Req(key: "x"));
            Assert.NotEqual(a, c);
        }

        [Fact]
        public async Task Lease_HonoursPriorityNotBeforeAndLane()
        {
            long low = await queue.EnqueueAsync(Req("low", priority: 10));
            long high = await queue.EnqueueAsync(Req("high", priority: 200));
            long future = await queue.EnqueueAsync(Req("future", priority: 999, notBefore: clock.NowMs + 60_000));
            await queue.EnqueueAsync(Req("io", priority: 999, lane: Lane.Io));

            Assert.Equal(high, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Equal(low, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
            Assert.Null(await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(future, (await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1)))!.JobId);
        }

        [Fact]
        public async Task TransientFailure_BacksOffThenDeadLettersWhenExhausted()
        {
            long id = await queue.EnqueueAsync(Req(maxAttempts: 2));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(JobStates.Queued, await queue.FailAsync(id, "w", ErrorClass.Transient, "boom"));
            var j = queue.Get(id)!;
            Assert.True(j.NotBeforeMs > clock.NowMs);
            Assert.Null(await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1))); // backing off
            clock.Advance(TimeSpan.FromMinutes(1));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(JobStates.Dead, await queue.FailAsync(id, "w", ErrorClass.Transient, "boom again"));
            Assert.Equal("boom again", queue.Get(id)!.ErrorMessage);
        }

        [Fact]
        public async Task PermanentFailure_DeadLettersImmediatelyAndCanBeRetried()
        {
            long id = await queue.EnqueueAsync(Req(maxAttempts: 5));
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(JobStates.Dead, await queue.FailAsync(id, "w", ErrorClass.Permanent, "bad input"));
            Assert.True(await queue.RetryDeadAsync(id));
            var j = queue.Get(id)!;
            Assert.Equal(JobStates.Queued, j.State);
            Assert.Equal(0, j.Attempts);
        }

        [Fact]
        public async Task ExpiredLeases_AreReclaimedAndFinalAttemptsDeadLettered()
        {
            long a = await queue.EnqueueAsync(Req("a", maxAttempts: 3));
            long b = await queue.EnqueueAsync(Req("b", maxAttempts: 1));
            await queue.LeaseNextAsync(Lane.Cpu, "crashed", TimeSpan.FromSeconds(30));
            await queue.LeaseNextAsync(Lane.Cpu, "crashed", TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(2, await queue.ReclaimExpiredAsync());
            Assert.Equal(JobStates.Queued, queue.Get(a)!.State);
            Assert.Equal(JobStates.Dead, queue.Get(b)!.State);
            // The crashed owner can no longer complete it.
            Assert.False(await queue.CompleteAsync(a, "crashed", null));
        }

        [Fact]
        public async Task Release_DoesNotCountTheAttempt()
        {
            long id = await queue.EnqueueAsync(Req());
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.True(await queue.ReleaseAsync(id, "w"));
            var j = queue.Get(id)!;
            Assert.Equal(JobStates.Queued, j.State);
            Assert.Equal(0, j.Attempts);
        }

        [Fact]
        public async Task CancelledJob_LosesItsHeartbeat()
        {
            long id = await queue.EnqueueAsync(Req());
            await queue.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.True(await queue.CancelAsync(id));
            Assert.False(await queue.HeartbeatAsync(id, "w", TimeSpan.FromMinutes(1)));
            Assert.False(await queue.CompleteAsync(id, "w", null));
            Assert.Equal(JobStates.Cancelled, queue.Get(id)!.State);
        }

        [Fact]
        public async Task OldQueuedJobs_GainPriority()
        {
            long id = await queue.EnqueueAsync(Req(priority: 5));
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(1, await queue.AgePriorities(TimeSpan.FromMinutes(10), cap: 6));
            Assert.Equal(0, await queue.AgePriorities(TimeSpan.FromMinutes(10), cap: 6));
            Assert.Equal(6, queue.Get(id)!.Priority);
        }

        // ── workers ──────────────────────────────────────────────────────────────

        private sealed class Dispatcher : IJobDispatcher
        {
            public Func<JobContext, Task<string?>> Handler { get; set; } = _ => Task.FromResult<string?>(null);
            public JobHandler? Resolve(string moduleId, string kind) => moduleId == "test" ? ctx => Handler(ctx) : null;
        }

        private LaneWorkers Workers(Dispatcher d, int concurrency = 2, TimeSpan? lease = null) =>
            new(queue, d, clock, new[] { new LaneOptions(Lane.Cpu, concurrency, lease ?? TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)) }, Log);

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task Workers_RunRetryAndDeadLetter()
        {
            var attempts = new ConcurrentDictionary<long, int>();
            var d = new Dispatcher
            {
                Handler = ctx =>
                {
                    int n = attempts.AddOrUpdate(ctx.Job.JobId, 1, (_, v) => v + 1);
                    return ctx.Job.Kind switch
                    {
                        "ok" => Task.FromResult<string?>("{\"ok\":true}"),
                        "flaky" when n < 2 => throw new TimeoutException("transient"),
                        "flaky" => Task.FromResult<string?>("{}"),
                        "bad" => throw new JobFailureException(ErrorClass.Permanent, "cannot work"),
                        _ => Task.FromResult<string?>(null),
                    };
                },
            };
            long ok = await queue.EnqueueAsync(Req("ok"));
            long flaky = await queue.EnqueueAsync(Req("flaky", maxAttempts: 3));
            long bad = await queue.EnqueueAsync(Req("bad"));
            long orphan = await queue.EnqueueAsync(new JobRequest { ModuleId = "nobody", Kind = "x", Lane = Lane.Cpu });

            await using (var w = Workers(d))
            {
                w.Start();
                await WaitUntil(() => queue.Get(ok)!.State == JobStates.Succeeded && queue.Get(bad)!.State == JobStates.Dead
                                      && queue.Get(orphan)!.State == JobStates.Dead
                                      && attempts.ContainsKey(flaky) && queue.Get(flaky)!.State == JobStates.Queued);
                // The flaky job failed once and is backing off on the fixed clock; make it eligible again.
                clock.Advance(TimeSpan.FromMinutes(1));
                await WaitUntil(() => queue.Get(flaky)!.State == JobStates.Succeeded);
            }
            Assert.Equal("cannot work", queue.Get(bad)!.ErrorMessage);
            Assert.Contains("No handler registered", queue.Get(orphan)!.ErrorMessage);
            Assert.Equal(2, attempts[flaky]);
        }

        [Fact]
        public async Task Workers_CancelRunningJobWhenCancelledInUi()
        {
            var started = new TaskCompletionSource();
            var observedCancel = new TaskCompletionSource();
            var d = new Dispatcher
            {
                Handler = async ctx =>
                {
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ctx.Cancellation); }
                    catch (OperationCanceledException) { observedCancel.TrySetResult(); throw; }
                    return null;
                },
            };
            long id = await queue.EnqueueAsync(Req());
            // Short lease so the heartbeat (every lease/3) notices the cancellation quickly.
            await using var w = Workers(d, 1, TimeSpan.FromMilliseconds(300));
            w.Start();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await queue.CancelAsync(id);
            await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(JobStates.Cancelled, queue.Get(id)!.State);
        }

        [Fact]
        public async Task Shutdown_ReleasesRunningJobsWithoutCountingTheAttempt()
        {
            var started = new TaskCompletionSource();
            var d = new Dispatcher
            {
                Handler = async ctx =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ctx.Cancellation);
                    return null;
                },
            };
            long id = await queue.EnqueueAsync(Req());
            var w = Workers(d, 1);
            w.Start();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await w.DisposeAsync();
            var j = queue.Get(id)!;
            Assert.Equal(JobStates.Queued, j.State);
            Assert.Equal(0, j.Attempts);
        }

        [Fact]
        public async Task Chaos_RepeatedCrashesNeverLoseOrDuplicateWork()
        {
            const int jobCount = 60;
            var completed = new ConcurrentDictionary<long, int>();
            var rng = new Random(1234);
            var rngLock = new object();
            var d = new Dispatcher
            {
                Handler = async ctx =>
                {
                    int roll;
                    lock (rngLock) roll = rng.Next(100);
                    await Task.Delay(roll % 7, ctx.Cancellation);
                    if (roll < 20) throw new TimeoutException("random transient failure");
                    completed.AddOrUpdate(ctx.Job.JobId, 1, (_, v) => v + 1);
                    return null;
                },
            };
            var ids = new List<long>();
            for (int i = 0; i < jobCount; i++) ids.Add(await queue.EnqueueAsync(Req("c" + i, maxAttempts: 50, key: "chaos-" + i)));

            for (int round = 0; round < 25 && ids.Any(id => queue.Get(id)!.State != JobStates.Succeeded); round++)
            {
                // A "process" runs briefly, then dies: sometimes gracefully, sometimes by leaving leases behind.
                var w = Workers(d, 3);
                w.Start();
                await Task.Delay(30);
                await w.DisposeAsync();
                if (round % 3 == 0)
                {
                    // Simulated hard crash: a ghost worker grabs jobs and never returns them.
                    for (int g = 0; g < 3; g++) await queue.LeaseNextAsync(Lane.Cpu, "ghost", TimeSpan.FromSeconds(5));
                }
                clock.Advance(TimeSpan.FromMinutes(20)); // expire leases and backoffs
                await queue.ReclaimExpiredAsync();
            }
            var finalWorkers = Workers(d, 3);
            finalWorkers.Start();
            await WaitUntil(() =>
            {
                clock.Advance(TimeSpan.FromMinutes(20));
                return ids.All(id => queue.Get(id)!.State == JobStates.Succeeded);
            }, 30_000);
            await finalWorkers.DisposeAsync();

            Assert.All(ids, id => Assert.Equal(JobStates.Succeeded, queue.Get(id)!.State));
            Assert.Equal(jobCount, completed.Count);
            Assert.DoesNotContain(queue.List(JobStates.Leased, null, 1000), _ => true);
        }

        // ── module host ──────────────────────────────────────────────────────────

        private sealed class PersonModule : IntelModuleBase
        {
            public readonly ConcurrentBag<string> Ran = new();
            public readonly List<PlanContext> Plans = new();
            public int Version { get; init; } = 1;
            public IReadOnlyList<string> Deps { get; init; } = Array.Empty<string>();
            public string ModuleIdOverride { get; init; } = "test.person";
            public IReadOnlyList<ScheduleTrigger> Triggers { get; init; } = Array.Empty<ScheduleTrigger>();

            public override ModuleManifest Manifest => new()
            {
                Id = ModuleIdOverride, Version = Version, Title = "Person test", DependsOn = Deps,
                SubscribedChangeKinds = new[] { ChangeKinds.MessageInsert }, Schedules = Triggers,
            };

            public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
                changes.Where(c => c.PersonId != null).Select(c => new DirtySubject("person", c.PersonId!));

            public override IEnumerable<JobRequest> Plan(PlanContext ctx)
            {
                lock (Plans) Plans.Add(ctx);
                foreach (var d in ctx.Dirty) yield return Job("recompute", d.Kind, d.Id);
                foreach (var s in ctx.DueSchedules) yield return Job("scheduled:" + s.Name);
            }

            public override Task<string?> ExecuteAsync(JobContext job)
            {
                Ran.Add(job.Job.Kind + ":" + job.Job.SubjectId);
                return Task.FromResult<string?>(null);
            }
        }

        [Fact]
        public void DependencyCycles_AndUnknownDependencies_AreRejected()
        {
            var a = new PersonModule { ModuleIdOverride = "a", Deps = new[] { "b" } };
            var b = new PersonModule { ModuleIdOverride = "b", Deps = new[] { "a" } };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ModuleHost.OrderByDependencies(new Dictionary<string, IIntelModule> { ["a"] = a, ["b"] = b }));
            Assert.Contains("cycle", ex.Message);

            var orphan = new PersonModule { ModuleIdOverride = "c", Deps = new[] { "missing" } };
            Assert.Throws<InvalidOperationException>(() =>
                ModuleHost.OrderByDependencies(new Dictionary<string, IIntelModule> { ["c"] = orphan }));

            var first = new PersonModule { ModuleIdOverride = "first" };
            var second = new PersonModule { ModuleIdOverride = "second", Deps = new[] { "first" } };
            var order = ModuleHost.OrderByDependencies(new Dictionary<string, IIntelModule> { ["second"] = second, ["first"] = first });
            Assert.Equal(new[] { "first", "second" }, order.Select(m => m.Manifest.Id));
        }

        // No background loop: tests drive pump/plan passes explicitly so results are deterministic.
        private ModuleHost Host(bool workers = false) =>
            new(stores, clock, new KernelOptions { StartWorkers = workers, StartLoop = false }, m => logs.Enqueue(m), Log);

        private Task<long> AppendChange(string person, string kind = ChangeKinds.MessageInsert) =>
            stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, clock.NowMs, kind, "m:" + Guid.NewGuid().ToString("N"), person, "conv", SourceKinds.Live));

        [Fact]
        public async Task ChangesBecomeDirtySubjectsThenSingleFlightJobs()
        {
            var module = new PersonModule();
            await using var host = Host();
            host.Register(module);
            await host.StartAsync();

            await AppendChange("p1");
            await AppendChange("p1");
            await AppendChange("p2");
            await AppendChange("p3", ChangeKinds.ReactionChange); // not subscribed

            Assert.Equal(3, await host.PumpChangesOnceAsync());
            Assert.Equal(2, await host.PlanOnceAsync());
            var queued = host.Jobs.List(JobStates.Queued, "test.person", 100);
            Assert.Equal(new[] { "p1", "p2" }, queued.Select(j => j.SubjectId).OrderBy(x => x));

            // New change for p1 while its job is still queued: re-dirtied, re-planned, deduped onto the live job.
            await AppendChange("p1");
            await host.PumpChangesOnceAsync();
            await host.PlanOnceAsync();
            Assert.Equal(2, host.Jobs.List(JobStates.Queued, "test.person", 100).Count);

            var health = host.Health().Single();
            Assert.Equal(0, health.Backlog);
            Assert.Equal(0, health.DirtySubjects);
            Assert.Equal(2, health.Queued);
        }

        [Fact]
        public async Task MappingFailure_HoldsTheWatermark()
        {
            var module = new ThrowingMapModule();
            await using var host = Host();
            host.Register(module);
            await host.StartAsync();
            await AppendChange("p1");
            await host.PumpChangesOnceAsync();
            Assert.Equal(1, host.Health().Single().Backlog);
            module.Throw = false;
            await host.PumpChangesOnceAsync();
            Assert.Equal(0, host.Health().Single().Backlog);
        }

        private sealed class ThrowingMapModule : IntelModuleBase
        {
            public bool Throw { get; set; } = true;
            public override ModuleManifest Manifest => new() { Id = "test.throw", Version = 1, Title = "t", SubscribedChangeKinds = new[] { ChangeKinds.MessageInsert } };
            public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
                Throw ? throw new InvalidOperationException("mapping bug") : changes.Select(c => new DirtySubject("person", c.PersonId ?? "?"));
            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);
        }

        [Fact]
        public async Task DailySchedule_FiresOnceAndLateAfterDowntime()
        {
            var module = new PersonModule { Triggers = new[] { new ScheduleTrigger("nightly", DailyAtLocal: new TimeOnly(3, 30)) } };
            await using var host = Host();
            host.Register(module);
            await host.StartAsync();

            await host.PlanOnceAsync(); // arms the schedule (next: 2024-11-06 03:30 London = 03:30Z)
            Assert.Empty(host.Jobs.List(JobStates.Queued, null, 10));

            clock.Set(Instant.FromUtc(new DateTime(2024, 11, 9, 10, 0, 0, DateTimeKind.Utc))); // three nights missed
            await host.PlanOnceAsync();
            var jobs = host.Jobs.List(JobStates.Queued, null, 10);
            Assert.Single(jobs, j => j.Kind == "scheduled:nightly");
            var due = module.Plans.Last().DueSchedules.Single();
            Assert.True(due.Lateness > TimeSpan.FromDays(3));

            await host.PlanOnceAsync(); // nothing more until tomorrow 03:30
            Assert.Single(host.Jobs.List(JobStates.Queued, null, 10));
        }

        [Fact]
        public async Task VersionBump_TriggersOneFullReplan()
        {
            await using (var host = Host())
            {
                host.Register(new PersonModule { Version = 1 });
                await host.StartAsync();
                await host.PlanOnceAsync();
            }
            var v2 = new PersonModule { Version = 2 };
            await using (var host = Host())
            {
                host.Register(v2);
                await host.StartAsync();
                await host.PlanOnceAsync();
                await host.PlanOnceAsync();
            }
            Assert.Single(v2.Plans);
            Assert.True(v2.Plans[0].VersionChanged);
        }

        [Fact]
        public async Task EndToEnd_WorkersExecutePlannedJobs()
        {
            var module = new PersonModule();
            await using var host = new ModuleHost(stores, clock, new KernelOptions
            {
                LoopInterval = TimeSpan.FromMilliseconds(50),
                Lanes = new[] { new LaneOptions(Lane.Cpu, 2, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)) },
            }, m => logs.Enqueue(m), Log);
            host.Register(module);
            await host.StartAsync();
            await AppendChange("p9");
            host.Kick();
            await WaitUntil(() => module.Ran.Contains("recompute:p9"));
        }

        [Fact]
        public async Task Settings_DefaultsLiveInCodeUntilSet()
        {
            await using var host = Host();
            await host.StartAsync();
            Assert.Equal("Europe/London", host.Settings.Get(KernelSettings.KlivesTimeZoneKey, Zones.DefaultKlivesZone));
            await host.Settings.SetAsync(KernelSettings.KlivesTimeZoneKey, "America/New_York", "test");
            Assert.Equal("America/New_York", host.Settings.Get(KernelSettings.KlivesTimeZoneKey, Zones.DefaultKlivesZone));
            Assert.Equal(-5, host.Settings.KlivesZone.BaseUtcOffset.TotalHours);
            using var c = stores.Operator.OpenRead();
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM audit_log WHERE action='setting.set'"));
        }
    }

}
