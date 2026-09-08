namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Client-side admission control for AIRouter's published fair-use policy:
    /// 3 parallel requests, 240 requests/minute, 10M tokens/minute.
    ///
    /// Reservations are reconciled to provider-reported usage so pessimistic completion estimates
    /// do not strand capacity. Estimates, external clients and provider policy mean this cannot
    /// guarantee absence of 429s, so provider-directed cool-offs are shared by every caller.
    /// </summary>
    public sealed class AIRouterFairUseLimiter
    {
        // ── The published policy (see https://airouter.ch fair-use dialog) ──
        public const int PolicyMaxParallelRequests = 3;
        public const int PolicyMaxRequestsPerMinute = 240;
        public const long PolicyMaxTokensPerMinute = 10_000_000;

        /// <summary>The rolling window every per-minute limit is measured over.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        // We admit against a slightly tighter envelope than the published one, for two reasons. Our
        // clock, the router's clock and the network are not the same, so a request admitted at
        // exactly 240/min can still land inside the router's previous window. And a few HTTP calls
        // never pass through here at all — the connection warm-up GET most of all — so the slack
        // absorbs them too. This headroom does not account for other processes using the same key.
        private const double RequestHeadroomFraction = 0.95;   // 228 req/min
        private const double TokenHeadroomFraction = 0.95;     // 9.5M tokens/min

        // Longest single wait handed out before re-evaluating. The window drains continuously and a
        // smaller request may become admissible first, so callers re-check instead of sleeping on a
        // stale computation.
        private static readonly TimeSpan MaxSingleWait = TimeSpan.FromSeconds(5);

        private readonly int requestCeiling;
        private readonly long tokenCeiling;
        private readonly Func<DateTime> nowUtc;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly SemaphoreSlim parallelism;

        private readonly object sync = new();
        private readonly LinkedList<WindowEntry> window = new();
        private TaskCompletionSource<bool> stateChanged = NewStateSignal();
        private long windowTokens;
        private DateTime penaltyUntilUtc = DateTime.MinValue;

        private long totalAdmitted;
        private long totalWaitedMs;
        private long totalPenalties;
        private int inFlight;

        public AIRouterFairUseLimiter(
            int maxParallelRequests = PolicyMaxParallelRequests,
            int maxRequestsPerMinute = PolicyMaxRequestsPerMinute,
            long maxTokensPerMinute = PolicyMaxTokensPerMinute,
            Func<DateTime>? nowUtc = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            parallelism = new SemaphoreSlim(Math.Max(1, maxParallelRequests), Math.Max(1, maxParallelRequests));
            requestCeiling = Math.Max(1, (int)Math.Floor(Math.Max(1, maxRequestsPerMinute) * RequestHeadroomFraction));
            tokenCeiling = Math.Max(1, (long)Math.Floor(Math.Max(1, maxTokensPerMinute) * TokenHeadroomFraction));
            this.nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            this.delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        }

        /// <summary>Observable state, for logs and the fair-use diagnostics surface.</summary>
        public readonly record struct Snapshot(
            int InFlight,
            int RequestsInWindow,
            int RequestCeiling,
            long TokensInWindow,
            long TokenCeiling,
            TimeSpan PenaltyRemaining,
            long TotalAdmitted,
            long TotalWaitedMs,
            long TotalPenalties);

        public Snapshot Describe()
        {
            DateTime now = nowUtc();
            lock (sync)
            {
                Prune(now);
                return new Snapshot(
                    InFlight: Volatile.Read(ref inFlight),
                    RequestsInWindow: window.Count,
                    RequestCeiling: requestCeiling,
                    TokensInWindow: windowTokens,
                    TokenCeiling: tokenCeiling,
                    PenaltyRemaining: penaltyUntilUtc > now ? penaltyUntilUtc - now : TimeSpan.Zero,
                    TotalAdmitted: Interlocked.Read(ref totalAdmitted),
                    TotalWaitedMs: Interlocked.Read(ref totalWaitedMs),
                    TotalPenalties: Interlocked.Read(ref totalPenalties));
            }
        }

        /// <summary>
        /// Queue for permission to send one AIRouter request. Returns only when all published limits have
        /// room; the returned lease holds the parallel slot until it is disposed, which the caller
        /// must do as soon as the HTTP exchange completes (not when it finishes processing the body).
        /// </summary>
        /// <param name="estimatedTokens">
        /// Pessimistic size of the request: prompt estimate plus the full completion reserve. Reconciled
        /// to the provider-reported figure via <see cref="AIRouterFairUseLease.ReportActualTokens"/>.
        /// </param>
        public async Task<AIRouterFairUseLease> AcquireAsync(long estimatedTokens,
            CancellationToken cancellationToken = default)
        {
            // A request bigger than the entire per-minute token ceiling could never be admitted; clamp
            // it so an oversized estimate waits for an empty window rather than deadlocking forever.
            long reserved = Math.Clamp(estimatedTokens, 1, tokenCeiling);
            DateTime queuedAt = nowUtc();

            while (true)
            {
                // Never occupy one of AIRouter's three parallel slots while waiting for an RPM/TPM
                // window or provider cool-off. The previous ordering took the slot first and then
                // slept, allowing one blocked prompt to pin a slot and the serialized admission gate
                // to hold every request behind it.
                await parallelism.WaitAsync(cancellationToken);
                bool admitted = false;
                TimeSpan wait;
                Task recheck;
                LinkedListNode<WindowEntry>? node = null;
                try
                {
                    DateTime now = nowUtc();
                    lock (sync)
                    {
                        Prune(now);
                        wait = TimeUntilAdmissible(now, reserved);
                        recheck = stateChanged.Task;
                        if (wait <= TimeSpan.Zero)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            node = window.AddLast(new WindowEntry(now, reserved));
                            windowTokens += reserved;
                            admitted = true;
                        }
                    }
                }
                finally
                {
                    if (!admitted) parallelism.Release();
                }

                if (admitted)
                {
                    long queueDurationMs = (long)Math.Max(0, (nowUtc() - queuedAt).TotalMilliseconds);
                    Interlocked.Increment(ref inFlight);
                    Interlocked.Increment(ref totalAdmitted);
                    Interlocked.Add(ref totalWaitedMs, queueDurationMs);
                    return new AIRouterFairUseLease(this, node!, queueDurationMs);
                }

                if (wait > MaxSingleWait) wait = MaxSingleWait;
                // Provider usage usually reconciles a pessimistic max-completion reservation down
                // long before its minute expires. Wake immediately when that happens instead of
                // adding up to five seconds of polling latency to every newly-admissible request.
                await await Task.WhenAny(delay(wait, cancellationToken), recheck.WaitAsync(cancellationToken));
            }
        }

        /// <summary>
        /// Record that the router rate-limited us anyway (someone else is spending the same key, or a
        /// window boundary landed badly) and hold EVERY queued caller off until the cool-off expires.
        /// The provider may impose a longer restriction than its public per-minute windows.
        /// </summary>
        public void Penalize(TimeSpan coolOff)
        {
            if (coolOff <= TimeSpan.Zero) return;
            DateTime until = nowUtc() + coolOff;
            lock (sync)
            {
                if (until > penaltyUntilUtc) penaltyUntilUtc = until;
            }
            Interlocked.Increment(ref totalPenalties);
        }

        /// <summary>How long the caller must wait before this request fits inside all three limits.</summary>
        private TimeSpan TimeUntilAdmissible(DateTime now, long reserved)
        {
            if (penaltyUntilUtc > now) return penaltyUntilUtc - now;

            TimeSpan wait = TimeSpan.Zero;
            if (window.Count + 1 > requestCeiling && window.First != null)
                wait = Expiry(window.First.Value, now);

            if (windowTokens + reserved > tokenCeiling)
            {
                // Walk the window oldest-first until enough reserved tokens have aged out.
                long mustFree = windowTokens + reserved - tokenCeiling;
                long freed = 0;
                for (var entry = window.First; entry != null; entry = entry.Next)
                {
                    freed += entry.Value.Tokens;
                    if (freed < mustFree) continue;
                    var tokenWait = Expiry(entry.Value, now);
                    if (tokenWait > wait) wait = tokenWait;
                    break;
                }
            }

            return wait;
        }

        private static TimeSpan Expiry(WindowEntry entry, DateTime now)
        {
            var remaining = entry.At + Window - now;
            // Never return zero for an entry we are waiting on: the caller would spin. One tick of
            // slack guarantees the entry is genuinely outside the window when it re-checks.
            return remaining > TimeSpan.Zero ? remaining + TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(1);
        }

        private void Prune(DateTime now)
        {
            DateTime cutoff = now - Window;
            while (window.First is { } first && first.Value.At <= cutoff)
            {
                windowTokens -= first.Value.Tokens;
                window.RemoveFirst();
            }
            if (window.Count == 0) windowTokens = 0; // defensive: reconciliation can never strand a residue
        }

        internal void ReleaseSlot()
        {
            Interlocked.Decrement(ref inFlight);
            parallelism.Release();
        }

        /// <summary>
        /// Replace a request's pessimistic reservation with what the provider actually billed, freeing
        /// the difference for the callers queued behind it. Ignored once the entry has aged out.
        /// </summary>
        internal void Reconcile(LinkedListNode<WindowEntry> node, long actualTokens)
        {
            if (actualTokens < 0) return;
            lock (sync)
            {
                if (node.List != window) return; // already pruned out of the window
                long delta = actualTokens - node.Value.Tokens;
                node.Value = node.Value with { Tokens = actualTokens };
                windowTokens += delta;
                if (windowTokens < 0) windowTokens = 0;
                if (delta < 0) PulseStateChanged();
            }
        }

        private static TaskCompletionSource<bool> NewStateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Complete the current generation and replace it while holding <see cref="sync"/>.</summary>
        private void PulseStateChanged()
        {
            var previous = stateChanged;
            stateChanged = NewStateSignal();
            previous.TrySetResult(true);
        }

        internal readonly record struct WindowEntry(DateTime At, long Tokens);
    }

    /// <summary>
    /// One admitted AIRouter request. Disposing releases the parallel slot; the window entry lives on
    /// for the rest of the minute so the per-minute limits keep counting it.
    /// </summary>
    public sealed class AIRouterFairUseLease : IDisposable
    {
        private readonly AIRouterFairUseLimiter limiter;
        private readonly LinkedListNode<AIRouterFairUseLimiter.WindowEntry> node;
        private int released;

        internal AIRouterFairUseLease(AIRouterFairUseLimiter limiter,
            LinkedListNode<AIRouterFairUseLimiter.WindowEntry> node, long queueDurationMs)
        {
            this.limiter = limiter;
            this.node = node;
            QueueDurationMs = queueDurationMs;
        }

        /// <summary>Time spent waiting locally before this request was allowed to reach AIRouter.</summary>
        public long QueueDurationMs { get; }

        /// <summary>Book the provider's own token count against the window in place of our estimate.</summary>
        public void ReportActualTokens(long totalTokens) => limiter.Reconcile(node, totalTokens);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            limiter.ReleaseSlot();
        }
    }
}
