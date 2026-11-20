using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Jobs
{
    /// <summary>What a job handler sees while it runs.</summary>
    public sealed class JobContext
    {
        private readonly Func<string, Task<bool>> heartbeat;

        internal JobContext(JobRecord job, IClock clock, CancellationToken cancellation, Func<string, Task<bool>> heartbeat)
        {
            Job = job;
            Clock = clock;
            Cancellation = cancellation;
            this.heartbeat = heartbeat;
        }

        public JobRecord Job { get; }
        public IClock Clock { get; }
        /// <summary>Signalled on shutdown, on UI cancellation and when the lease is lost. Handlers must honour it.</summary>
        public CancellationToken Cancellation { get; }

        public T? Payload<T>() => Job.PayloadJson == null ? default : JsonSerializer.Deserialize<T>(Job.PayloadJson, KernelJson.Options);

        /// <summary>Records progress (shown live in the UI) and extends the lease.</summary>
        public Task ReportProgressAsync(object progress) => heartbeat(JsonSerializer.Serialize(progress, KernelJson.Options));
    }

    /// <summary>A job handler returns its result as JSON (or null).</summary>
    public delegate Task<string?> JobHandler(JobContext context);

    public interface IJobDispatcher
    {
        JobHandler? Resolve(string moduleId, string kind);
    }

    /// <summary>Admission control per lane: CPU throttling under host load, and the LLM background lane
    /// yielding to interactive work (plan R1). The default lets everything through.</summary>
    public interface ILaneGate
    {
        Task WaitAsync(Lane lane, CancellationToken ct);
    }

    public sealed class OpenLaneGate : ILaneGate
    {
        public static readonly OpenLaneGate Instance = new();
        public Task WaitAsync(Lane lane, CancellationToken ct) => Task.CompletedTask;
    }

    /// <param name="StallAfter">How long a job may run without reporting progress before it is told to stop (and, if it
    /// does not, left behind so the worker can move on). Null: never. A heartbeat only proves the worker is alive; without
    /// this a handler stuck inside a native call or a hung request kept its lease, and its worker, for ever.</param>
    /// <param name="MaxPerModule">How many jobs one module may run in this lane at once. Null: one less than the lane's
    /// workers (at least one), so a module whose jobs are slow or stuck always leaves a worker for the others.</param>
    public sealed record LaneOptions(Lane Lane, int Concurrency, TimeSpan LeaseDuration, TimeSpan IdlePoll, TimeSpan? StallAfter = null, int? MaxPerModule = null)
    {
        public int ModuleLimit => Math.Max(1, MaxPerModule ?? (Concurrency > 1 ? Concurrency - 1 : 1));
    }

    /// <summary>
    /// Worker pools, one per lane. Each worker leases a job, runs its handler with a heartbeat that extends
    /// the lease (and cancels the handler if the lease is lost), then completes or fails it with a
    /// classified error. On shutdown, running jobs are cancelled and released without counting the attempt.
    /// </summary>
    public sealed class LaneWorkers : IAsyncDisposable
    {
        private readonly JobQueue queue;
        private readonly IJobDispatcher dispatcher;
        private readonly IClock clock;
        private readonly ILaneGate gate;
        private readonly Action<string, Exception?> log;
        private readonly IReadOnlyList<LaneOptions> lanes;
        private readonly string ownerPrefix;
        private readonly CancellationTokenSource stopping = new();
        private readonly Dictionary<Lane, SemaphoreSlim> wake = new();
        private readonly List<Task> loops = new();
        private long processed;

        public LaneWorkers(JobQueue queue, IJobDispatcher dispatcher, IClock clock, IEnumerable<LaneOptions> lanes,
            Action<string, Exception?> log, ILaneGate? gate = null, string? ownerPrefix = null)
        {
            this.queue = queue;
            this.dispatcher = dispatcher;
            this.clock = clock;
            this.lanes = lanes.ToList();
            this.log = log;
            this.gate = gate ?? OpenLaneGate.Instance;
            this.ownerPrefix = ownerPrefix ?? $"{Environment.MachineName}:{Environment.ProcessId}";
            foreach (var l in this.lanes) wake[l.Lane] = new SemaphoreSlim(0, int.MaxValue);
            queue.JobEnqueued += OnEnqueued;
        }

        public long Processed => Interlocked.Read(ref processed);

        public void Start()
        {
            if (loops.Count > 0) throw new InvalidOperationException("Workers already started.");
            foreach (var lane in lanes)
            {
                for (int i = 0; i < Math.Max(1, lane.Concurrency); i++)
                {
                    string owner = $"{ownerPrefix}:{lane.Lane}:{i}";
                    var options = lane;
                    // Owned long-running loops, awaited in DisposeAsync.
                    loops.Add(Task.Factory.StartNew(() => RunLoopAsync(options, owner), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());
                }
            }
        }

        private void OnEnqueued(Lane lane)
        {
            if (wake.TryGetValue(lane, out var sem) && sem.CurrentCount < 64) sem.Release();
        }

        private async Task RunLoopAsync(LaneOptions lane, string owner)
        {
            var token = stopping.Token;
            while (!token.IsCancellationRequested)
            {
                JobRecord? job;
                try
                {
                    await gate.WaitAsync(lane.Lane, token).ConfigureAwait(false);
                    job = await queue.LeaseNextAsync(lane.Lane, owner, lane.LeaseDuration, token, lane.ModuleLimit).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log($"[OmniscienceV3] lane {lane.Lane} lease failed; backing off.", ex);
                    await DelayQuietly(lane.IdlePoll, token).ConfigureAwait(false);
                    continue;
                }

                if (job == null)
                {
                    await WaitForWorkAsync(lane, token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    // An earlier attempt of this very job is still running here (it would not stop when asked): two copies
                    // of the same work side by side is what the retry must not become. It goes back and waits its turn.
                    if (leftBehind.TryGetValue(job.JobId, out var behind))
                    {
                        // Not for ever: a handler that has still not come back after hours is not going to, and a job that
                        // only ever waits looks healthy. It is failed for good, with the reason; a second copy is still
                        // not started beside the first.
                        long waited = clock.NowMs - behind.SinceMs;
                        if (waited >= (long)LeftBehindGiveUp.TotalMilliseconds)
                            await SafeFail(job, owner, ErrorClass.Permanent,
                                $"An earlier attempt of this job was told to stop {Describe(TimeSpan.FromMilliseconds(waited))} ago and is still running, so it was not started again beside it. Restarting the service clears the stuck attempt.").ConfigureAwait(false);
                        else
                            await queue.DeferAsync(job.JobId, owner, LeftBehindRetryDelay,
                                "Waiting for an earlier attempt of this job that did not stop when asked and is still running.").ConfigureAwait(false);
                        continue;
                    }
                    await RunJobAsync(job, lane, owner, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Nothing a job does may end its worker: the lane would run one worker short until the next start.
                    log($"[OmniscienceV3] lane {lane.Lane} worker hit an error around job {job.JobId} ({job.ModuleId}/{job.Kind}); it carries on.", ex);
                }
                Interlocked.Increment(ref processed);
            }
        }

        /// <summary>A handler that was told to stop and did not: what it is, and since when it has been running on its own.</summary>
        public sealed record LeftBehindJob(long JobId, string ModuleId, string Kind, long SinceMs);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> running = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, LeftBehindJob> leftBehind = new();

        /// <summary>Jobs whose handler is running in this process right now. Their lease is theirs whatever the clock says:
        /// see <see cref="JobQueue.ReclaimExpiredAsync"/>.</summary>
        public IReadOnlyCollection<long> RunningJobIds => running.Keys.ToArray();

        /// <summary>Handlers that did not stop when asked and are still running. Each holds a thread, and whatever it was
        /// reading or writing, until it ends by itself.</summary>
        public IReadOnlyList<LeftBehindJob> LeftBehind => leftBehind.Values.OrderBy(j => j.SinceMs).ToList();

        /// <summary>How long a retry waits when the attempt before it is still running.</summary>
        internal static TimeSpan LeftBehindRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>How long a job goes on waiting for an earlier attempt that will not stop before it is failed for good.</summary>
        internal static TimeSpan LeftBehindGiveUp { get; set; } = TimeSpan.FromHours(6);

        /// <summary>
        /// The most handlers that may be running on their own at once. Past it a worker stays with a handler that will not
        /// stop instead of moving on: a lane that is short of workers is a smaller harm than an unbounded pile of abandoned
        /// work (on the server ten copies of a 15 GB backup and seven hour-long evidence reads ran beside the live jobs).
        /// </summary>
        internal static int MaxLeftBehind { get; set; } = 6;

        private async Task WaitForWorkAsync(LaneOptions lane, CancellationToken token)
        {
            try { await wake[lane.Lane].WaitAsync(lane.IdlePoll, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* stopping: the loop condition exits */ return; }
        }

        private static async Task DelayQuietly(TimeSpan delay, CancellationToken token)
        {
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* stopping */ return; }
        }

        /// <summary>How long a job told to stop for making no progress gets to stop before its worker leaves it behind.</summary>
        internal static TimeSpan StallGrace { get; set; } = TimeSpan.FromSeconds(30);

        private long stalled;

        /// <summary>Jobs stopped because they made no progress for their lane's <see cref="LaneOptions.StallAfter"/>.</summary>
        public long Stalled => Interlocked.Read(ref stalled);

        private async Task RunJobAsync(JobRecord job, LaneOptions lane, string owner, CancellationToken stopToken)
        {
            // Not in a using: a handler that is left behind (see below) still holds this token.
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            long lastSign = System.Diagnostics.Stopwatch.GetTimestamp();
            async Task<bool> Beat(string? progress)
            {
                if (progress != null) Volatile.Write(ref lastSign, System.Diagnostics.Stopwatch.GetTimestamp());
                bool ok = await queue.HeartbeatAsync(job.JobId, owner, lane.LeaseDuration, progress).ConfigureAwait(false);
                if (!ok) jobCts.Cancel(); // lease lost or job cancelled from the UI
                return ok;
            }

            var handler = dispatcher.Resolve(job.ModuleId, job.Kind);
            if (handler == null)
            {
                await SafeFail(job, owner, ErrorClass.Permanent, $"No handler registered for {job.ModuleId}/{job.Kind}.").ConfigureAwait(false);
                jobCts.Dispose();
                return;
            }

            var context = new JobContext(job, clock, jobCts.Token, p => Beat(p));
            var beatEvery = TimeSpan.FromMilliseconds(Math.Max(50, lane.LeaseDuration.TotalMilliseconds / 3));
            using var heartbeatTimer = new PeriodicTimer(beatEvery);
            var heartbeatLoop = HeartbeatLoopAsync(job, heartbeatTimer, beatEvery, () => Beat(null), jobCts.Token);
            running[job.JobId] = 0;
            // On the pool, so a handler that blocks before its first await cannot keep the watch below from running. The
            // job's cancellation travels with the handler: every read it makes stops with it (AmbientCancellation).
            var jobToken = jobCts.Token;
            var run = Task.Run(() =>
            {
                Storage.Sqlite.AmbientCancellation.Set(jobToken);
                return handler(context);
            });
            bool stopped = false, abandoned = false;
            try
            {
                if (lane.StallAfter is { } stall && stall > TimeSpan.Zero)
                {
                    while (!run.IsCompleted)
                    {
                        var quiet = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref lastSign));
                        if (quiet >= stall) break;
                        var wait = stall - quiet;
                        await Task.WhenAny(run, Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    }
                    if (!run.IsCompleted && !jobCts.IsCancellationRequested)
                    {
                        stopped = true;
                        Interlocked.Increment(ref stalled);
                        jobCts.Cancel();
                        if (await Task.WhenAny(run, Task.Delay(StallGrace)).ConfigureAwait(false) != run)
                        {
                            // Counted and added in one step, so two workers giving up at the same moment cannot both
                            // take the last place.
                            lock (leftBehind)
                            {
                                if (leftBehind.Count < MaxLeftBehind)
                                {
                                    leftBehind[job.JobId] = new LeftBehindJob(job.JobId, job.ModuleId, job.Kind, clock.NowMs);
                                    abandoned = true;
                                }
                            }
                        }
                        if (abandoned)
                        {
                            // It did not stop when asked (a native call, a request with no timeout). The worker goes on to
                            // the next job; the handler is left to finish, or not, on its own. Until it has, this job is
                            // not started again here: the retry waits (see RunLoopAsync).
                            _ = run.ContinueWith(t =>
                            {
                                _ = t.Exception;
                                leftBehind.TryRemove(job.JobId, out _);
                                running.TryRemove(job.JobId, out _);
                                jobCts.Dispose();
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            log($"[OmniscienceV3] job {job.JobId} ({job.ModuleId}/{job.Kind}) made no progress for {Describe(stall)} and did not stop when asked; its worker has moved on.", null);
                            await SafeFail(job, owner, ErrorClass.Transient,
                                $"Made no progress for {Describe(stall)} and did not stop when asked. The worker moved on; the stuck work was left behind.").ConfigureAwait(false);
                            return;
                        }
                        // Either it stopped, or enough handlers are already running on their own: this worker stays with it.
                    }
                }

                string? result = await run.ConfigureAwait(false);
                jobCts.Token.ThrowIfCancellationRequested();
                await queue.CompleteAsync(job.JobId, owner, result).ConfigureAwait(false);
            }
            catch (Exception ex) when (stopToken.IsCancellationRequested && Storage.Sqlite.AmbientCancellation.IsStop(ex, stopToken))
            {
                // Graceful shutdown: give the job back without counting the attempt.
                await SafeRelease(job, owner).ConfigureAwait(false);
            }
            catch (Exception ex) when (stopped && Storage.Sqlite.AmbientCancellation.IsStop(ex, jobToken))
            {
                log($"[OmniscienceV3] job {job.JobId} ({job.ModuleId}/{job.Kind}) made no progress for {Describe(lane.StallAfter!.Value)}; stopped.", null);
                await SafeFail(job, owner, ErrorClass.Transient, $"Made no progress for {Describe(lane.StallAfter!.Value)}; stopped.").ConfigureAwait(false);
            }
            catch (Exception ex) when (Storage.Sqlite.AmbientCancellation.IsStop(ex, jobToken))
            {
                // Lease lost or cancelled in the UI: the queue already holds the authoritative state.
                log($"[OmniscienceV3] job {job.JobId} ({job.ModuleId}/{job.Kind}) stopped: lease lost or cancelled.", null);
            }
            catch (JobFailureException jf)
            {
                await SafeFail(job, owner, jf.Class, jf.Message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var cls = Classify(ex);
                log($"[OmniscienceV3] job {job.JobId} ({job.ModuleId}/{job.Kind}) failed ({cls}).", ex);
                await SafeFail(job, owner, cls, $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            }
            finally
            {
                if (!abandoned)
                {
                    running.TryRemove(job.JobId, out _);
                    if (!jobCts.IsCancellationRequested) jobCts.Cancel();
                }
                await heartbeatLoop.ConfigureAwait(false);
                if (!abandoned) jobCts.Dispose();
            }
        }

        private static string Describe(TimeSpan t) =>
            t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalSeconds:0.#} s";

        private long lateBeatLoggedTicks;
        private long lateBeats;

        /// <summary>Heartbeats that ran more than a whole interval late since the start: a sign that the process, not the job,
        /// was stuck (a starved thread pool, a long pause).</summary>
        public long LateHeartbeats => Interlocked.Read(ref lateBeats);

        /// <summary>
        /// Extends the lease on a timer. It never ends in an error: a write that fails (a full disk, a store that is busy) is
        /// tried again on the next tick, where it used to end the heartbeat for good and, awaited in the worker's cleanup,
        /// the worker with it. A beat that runs late says so, with what the thread pool had queued: on the server every
        /// running job lost its lease in the same second four times in a morning, each time with about 46,000 items waiting
        /// for a pool thread.
        /// </summary>
        private async Task HeartbeatLoopAsync(JobRecord job, PeriodicTimer timer, TimeSpan every, Func<Task<bool>> beat, CancellationToken token)
        {
            long last = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var gap = System.Diagnostics.Stopwatch.GetElapsedTime(last);
                    last = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (gap > every + every)
                    {
                        Interlocked.Increment(ref lateBeats);
                        long now = Environment.TickCount64, logged = Interlocked.Read(ref lateBeatLoggedTicks);
                        if (now - logged > 600_000 && Interlocked.CompareExchange(ref lateBeatLoggedTicks, now, logged) == logged)
                            log($"[OmniscienceV3] the heartbeat of job {job.JobId} ({job.ModuleId}/{job.Kind}) ran {gap.TotalSeconds:0} s after the one before it (due every {every.TotalSeconds:0} s); "
                                + $"the thread pool has {ThreadPool.PendingWorkItemCount} items waiting and {ThreadPool.ThreadCount} threads. The process is being held up, not the job.", null);
                    }
                    try
                    {
                        if (!await beat().ConfigureAwait(false)) return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        log($"[OmniscienceV3] could not extend the lease of job {job.JobId} ({job.ModuleId}/{job.Kind}); trying again on the next beat.", ex);
                    }
                }
            }
            catch (OperationCanceledException) { /* job finished or cancelled */ return; }
        }

        /// <summary>Unknown exceptions are transient (retried, then dead-lettered); argument/format/not-supported
        /// errors cannot be fixed by retrying.</summary>
        internal static ErrorClass Classify(Exception ex) => ex switch
        {
            ArgumentException or FormatException or NotSupportedException or JsonException => ErrorClass.Permanent,
            _ => ErrorClass.Transient,
        };

        private async Task SafeFail(JobRecord job, string owner, ErrorClass cls, string message)
        {
            try { await queue.FailAsync(job.JobId, owner, cls, message).ConfigureAwait(false); }
            catch (Exception ex) { log($"[OmniscienceV3] could not record failure of job {job.JobId}; its lease will expire and be reclaimed.", ex); }
        }

        private async Task SafeRelease(JobRecord job, string owner)
        {
            try { await queue.ReleaseAsync(job.JobId, owner).ConfigureAwait(false); }
            catch (Exception ex) { log($"[OmniscienceV3] could not release job {job.JobId}; its lease will expire and be reclaimed.", ex); }
        }

        public async ValueTask DisposeAsync()
        {
            queue.JobEnqueued -= OnEnqueued;
            if (!stopping.IsCancellationRequested) stopping.Cancel();
            try { await Task.WhenAll(loops).ConfigureAwait(false); }
            catch (Exception ex) { log("[OmniscienceV3] a worker loop ended with an error during shutdown.", ex); }
            stopping.Dispose();
            foreach (var s in wake.Values) s.Dispose();
        }
    }

    /// <summary>Shared JSON settings for kernel payloads, results and stored documents. Enums are written by name, so
    /// stored data survives enum reordering and reads the same as the API (numbers are still accepted on read).</summary>
    public static class KernelJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
    }
}
