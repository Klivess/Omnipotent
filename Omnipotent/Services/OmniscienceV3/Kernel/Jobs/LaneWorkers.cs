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

    public sealed record LaneOptions(Lane Lane, int Concurrency, TimeSpan LeaseDuration, TimeSpan IdlePoll);

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
                    job = await queue.LeaseNextAsync(lane.Lane, owner, lane.LeaseDuration, token).ConfigureAwait(false);
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

                await RunJobAsync(job, lane, owner, token).ConfigureAwait(false);
                Interlocked.Increment(ref processed);
            }
        }

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

        private async Task RunJobAsync(JobRecord job, LaneOptions lane, string owner, CancellationToken stopToken)
        {
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            async Task<bool> Beat(string? progress)
            {
                bool ok = await queue.HeartbeatAsync(job.JobId, owner, lane.LeaseDuration, progress).ConfigureAwait(false);
                if (!ok) jobCts.Cancel(); // lease lost or job cancelled from the UI
                return ok;
            }

            var handler = dispatcher.Resolve(job.ModuleId, job.Kind);
            if (handler == null)
            {
                await SafeFail(job, owner, ErrorClass.Permanent, $"No handler registered for {job.ModuleId}/{job.Kind}.").ConfigureAwait(false);
                return;
            }

            var context = new JobContext(job, clock, jobCts.Token, p => Beat(p));
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, lane.LeaseDuration.TotalMilliseconds / 3)));
            var heartbeatLoop = HeartbeatLoopAsync(heartbeatTimer, () => Beat(null), jobCts.Token);
            try
            {
                string? result = await handler(context).ConfigureAwait(false);
                jobCts.Token.ThrowIfCancellationRequested();
                await queue.CompleteAsync(job.JobId, owner, result).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                // Graceful shutdown: give the job back without counting the attempt.
                await SafeRelease(job, owner).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
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
                if (!jobCts.IsCancellationRequested) jobCts.Cancel();
                await heartbeatLoop.ConfigureAwait(false);
            }
        }

        private static async Task HeartbeatLoopAsync(PeriodicTimer timer, Func<Task<bool>> beat, CancellationToken token)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (!await beat().ConfigureAwait(false)) return;
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

    /// <summary>Shared JSON settings for kernel payloads and results.</summary>
    public static class KernelJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };
    }
}
