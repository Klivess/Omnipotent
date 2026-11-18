using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Remembers that a router just failed a lane, so the lane's next calls go straight to its fallback
    /// instead of paying the failed router's whole retry chain first, and so a long conversation stays on
    /// one router (and keeps its prompt cache warm) until the failed one is tried again.
    ///
    /// Open for 2 minutes after a failure, doubling with each consecutive one up to 15. When the time is
    /// up one call tries the primary again (half-open); its success closes the circuit. Only consulted for
    /// lanes that have a fallback: a lane with nowhere else to go always tries its router.
    /// </summary>
    public sealed class LlmCircuitBreaker
    {
        public static readonly TimeSpan FirstOpen = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan MaxOpen = TimeSpan.FromMinutes(15);

        public sealed record State(string Lane, string Router, int Failures, DateTime OpenUntilUtc, DateTime LastFailureUtc, string LastError, DateTime? ProbeStartedUtc);

        /// <summary>A test call that never reports back (cancelled, lost) stops holding the circuit after this.</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(3);

        private readonly ConcurrentDictionary<string, State> states = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> now;

        public LlmCircuitBreaker(Func<DateTime>? now = null) => this.now = now ?? (() => DateTime.UtcNow);

        private static string Key(string lane, string router) => lane + "|" + router;

        /// <summary>True when calls should skip this router for now. Once the open time is over, exactly one
        /// call is let through to test it; until that call reports back, the others keep skipping.</summary>
        public bool IsOpen(string lane, string router)
        {
            string key = Key(lane, router);
            while (states.TryGetValue(key, out var s))
            {
                DateTime at = now();
                if (s.OpenUntilUtc > at) return true;
                if (s.ProbeStartedUtc is { } started && at - started < ProbeTimeout) return true;
                if (states.TryUpdate(key, s with { ProbeStartedUtc = at }, s)) return false;
            }
            return false;
        }

        public void RecordFailure(string lane, string router, string error)
        {
            DateTime at = now();
            states.AddOrUpdate(Key(lane, router),
                _ => new State(lane, router, 1, at + FirstOpen, at, Clip(error), null),
                (_, s) =>
                {
                    int failures = s.Failures + 1;
                    double minutes = Math.Min(MaxOpen.TotalMinutes, FirstOpen.TotalMinutes * Math.Pow(2, failures - 1));
                    return new State(lane, router, failures, at + TimeSpan.FromMinutes(minutes), at, Clip(error), null);
                });
        }

        public void RecordSuccess(string lane, string router) => states.TryRemove(Key(lane, router), out _);

        public IReadOnlyList<State> Describe()
        {
            DateTime at = now();
            foreach (var (key, s) in states)
                if (s.OpenUntilUtc < at - TimeSpan.FromHours(6)) states.TryRemove(key, out _);
            return states.Values.OrderByDescending(s => s.LastFailureUtc).ToList();
        }

        public void Reset(string? lane = null, string? router = null)
        {
            foreach (var (key, s) in states)
                if ((lane == null || string.Equals(s.Lane, lane, StringComparison.OrdinalIgnoreCase))
                    && (router == null || string.Equals(s.Router, router, StringComparison.OrdinalIgnoreCase)))
                    states.TryRemove(key, out _);
        }

        private static string Clip(string? text)
        {
            string value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 300 ? value : value[..300];
        }
    }

    /// <summary>
    /// Client-side concurrency and rate limits: per lane (the routing policy's max parallel) and per added
    /// router (its own max parallel and requests a minute). AIRouter is never gated here; its shared
    /// fair-use limiter already is the envelope.
    /// </summary>
    public sealed class LlmGates
    {
        private sealed class Gate
        {
            public Gate(int limit) { Limit = limit; Semaphore = new SemaphoreSlim(limit, limit); }
            public int Limit { get; }
            public SemaphoreSlim Semaphore { get; }
        }

        private readonly ConcurrentDictionary<string, Gate> parallel = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Queue<DateTime>> rate = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Waits for a slot under <paramref name="limit"/> concurrent holders of <paramref name="key"/>.
        /// Null (and no wait) when there is no limit.</summary>
        public async Task<IDisposable?> EnterAsync(string key, int? limit, CancellationToken ct)
        {
            if (limit is not > 0) return null;
            var gate = parallel.AddOrUpdate(key, _ => new Gate(limit.Value), (_, g) => g.Limit == limit.Value ? g : new Gate(limit.Value));
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(gate.Semaphore);
        }

        /// <summary>Waits until one more request fits in the rolling minute.</summary>
        public async Task WaitForRateAsync(string key, int? perMinute, CancellationToken ct)
        {
            if (perMinute is not > 0) return;
            var window = rate.GetOrAdd(key, _ => new Queue<DateTime>());
            while (true)
            {
                TimeSpan wait;
                lock (window)
                {
                    DateTime now = DateTime.UtcNow;
                    while (window.Count > 0 && now - window.Peek() >= TimeSpan.FromMinutes(1)) window.Dequeue();
                    if (window.Count < perMinute.Value) { window.Enqueue(now); return; }
                    wait = TimeSpan.FromMinutes(1) - (now - window.Peek()) + TimeSpan.FromMilliseconds(20);
                }
                await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }

        public int InUse(string key) => parallel.TryGetValue(key, out var g) ? g.Limit - g.Semaphore.CurrentCount : 0;

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? semaphore;
            public Releaser(SemaphoreSlim semaphore) => this.semaphore = semaphore;
            public void Dispose() => Interlocked.Exchange(ref semaphore, null)?.Release();
        }
    }
}
