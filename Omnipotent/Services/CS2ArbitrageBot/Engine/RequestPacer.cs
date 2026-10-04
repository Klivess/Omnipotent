namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    public enum RequestPriority
    {
        /// <summary>Re-checking a book right before a purchase.</summary>
        Critical = 0,
        /// <summary>Valuing a fresh listing from the live feed.</summary>
        High = 1,
        /// <summary>Structural scans and sweeps.</summary>
        Normal = 2,
        /// <summary>Background warming (conversion model, cache refresh).</summary>
        Low = 3,
    }

    /// <summary>
    /// Spaces requests to one host and always serves the most urgent waiter first, so background cache
    /// warming can never delay the lookup that decides a purchase. Backs off on 429s (pause + slower
    /// spacing) and recovers gradually. Replaces the old per-call exponential backoff, which went to
    /// 2^attempt seconds for up to 100 attempts (days of sleeping inside a single scan).
    /// </summary>
    public sealed class RequestPacer : IDisposable
    {
        private readonly object gate = new();
        private readonly Queue<TaskCompletionSource<bool>>[] queues;
        private readonly SemaphoreSlim waiting = new(0, int.MaxValue);
        private readonly CancellationTokenSource lifetime = new();
        private readonly TimeSpan baseInterval;
        private readonly TimeSpan maxInterval;
        private readonly Func<DateTime> utcNow;
        private TimeSpan interval;
        private DateTime nextSlotUtc = DateTime.MinValue;
        private DateTime pausedUntilUtc = DateTime.MinValue;
        private int consecutiveThrottles;

        public long Granted { get; private set; }
        public long Throttles { get; private set; }
        public DateTime PausedUntilUtc { get { lock (gate) return pausedUntilUtc; } }
        public TimeSpan CurrentInterval { get { lock (gate) return interval; } }
        public int QueueLength { get { lock (gate) return queues.Sum(q => q.Count); } }

        public RequestPacer(TimeSpan baseInterval, TimeSpan maxInterval, Func<DateTime>? utcNow = null)
        {
            this.baseInterval = baseInterval;
            this.maxInterval = maxInterval < baseInterval ? baseInterval : maxInterval;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            interval = baseInterval;
            queues = Enumerable.Range(0, Enum.GetValues<RequestPriority>().Length).Select(_ => new Queue<TaskCompletionSource<bool>>()).ToArray();
            _ = Task.Run(DispatchLoopAsync);
        }

        /// <summary>Waits until this caller may send one request.</summary>
        public async Task WaitTurnAsync(RequestPriority priority, CancellationToken ct = default)
        {
            var turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) queues[(int)priority].Enqueue(turn);
            waiting.Release();
            using (ct.Register(() => turn.TrySetCanceled(ct)))
            {
                await turn.Task.ConfigureAwait(false);
            }
        }

        /// <summary>The last request succeeded: drift the spacing back toward the base rate.</summary>
        public void ReportSuccess()
        {
            lock (gate)
            {
                consecutiveThrottles = 0;
                if (interval > baseInterval)
                {
                    var relaxed = TimeSpan.FromTicks((long)(interval.Ticks * 0.9));
                    interval = relaxed < baseInterval ? baseInterval : relaxed;
                }
            }
        }

        /// <summary>The host said 429: pause (honouring Retry-After) and slow down.</summary>
        public void ReportThrottled(TimeSpan? retryAfter = null)
        {
            lock (gate)
            {
                Throttles++;
                consecutiveThrottles++;
                var doubled = TimeSpan.FromTicks(interval.Ticks * 2);
                interval = doubled > maxInterval ? maxInterval : doubled;
                // 30s, 60s, 120s ... capped at 15 minutes.
                TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(consecutiveThrottles - 1, 5))));
                if (retryAfter is { } ra && ra > backoff && ra < TimeSpan.FromHours(1)) backoff = ra;
                DateTime until = utcNow() + backoff;
                if (until > pausedUntilUtc) pausedUntilUtc = until;
            }
        }

        private async Task DispatchLoopAsync()
        {
            CancellationToken ct = lifetime.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await waiting.WaitAsync(ct).ConfigureAwait(false);
                    while (true)
                    {
                        DateTime due;
                        lock (gate) due = nextSlotUtc > pausedUntilUtc ? nextSlotUtc : pausedUntilUtc;
                        TimeSpan wait = due - utcNow();
                        if (wait <= TimeSpan.Zero) break;
                        await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    }

                    TaskCompletionSource<bool>? next = null;
                    lock (gate)
                    {
                        foreach (var queue in queues)
                        {
                            while (queue.Count > 0)
                            {
                                var candidate = queue.Dequeue();
                                if (!candidate.Task.IsCompleted) { next = candidate; break; }
                            }
                            if (next != null) break;
                        }
                        if (next != null)
                        {
                            nextSlotUtc = utcNow() + interval;
                            Granted++;
                        }
                    }
                    next?.TrySetResult(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { /* never let the dispatcher die */ }
            }
        }

        public void Dispose()
        {
            lifetime.Cancel();
            lock (gate)
            {
                foreach (var queue in queues)
                    while (queue.Count > 0) queue.Dequeue().TrySetCanceled();
            }
        }
    }
}
