namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Client-side admission control for AIRouter's published fair-use policy:
    /// 3 parallel requests, 240 requests/minute, 10M tokens/minute.
    ///
    /// AIRouter is a FLAT-FEE router — there is no per-token price to economise on, so the only
    /// scarce resource is the fair-use envelope itself. Hitting it produces a 429, and a 429 in an
    /// autonomous Projects wake is expensive in the way that actually matters: it opens the project's
    /// provider circuit and DEFERS the wake for minutes. So rather than react to rate limits, this
    /// limiter makes them unreachable: every AIRouter request queues here first and is admitted only
    /// once all three limits provably have room for it.
    ///
    /// The queue is strictly FIFO (admission is serialised), so a burst of sub-agents can never
    /// stampede the window, and a caller waits exactly as long as the window needs — normally zero.
    /// Nothing is ever rejected: <see cref="AcquireAsync"/> waits, it does not fail. That is the
    /// whole point — a bounded in-process wait replaces an unbounded out-of-process deferral.
    ///
    /// Accounting is deliberately pessimistic up front (prompt estimate + the full completion
    /// reserve) and reconciled DOWN to provider-reported truth once the response lands, so the
    /// window can never be over-committed by a request that turned out smaller than budgeted.
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
        // absorbs them too. The headroom costs ~5% of a ceiling no Omnipotent workload comes close
        // to, and buys the guarantee the whole class exists for.
        private const double RequestHeadroomFraction = 0.95;   // 228 req/min
        private const double TokenHeadroomFraction = 0.95;     // 9.5M tokens/min

        // Longest single wait handed out before re-evaluating. The window drains continuously, so a
        // caller re-checks rather than sleeping on a stale computation.
        private static readonly TimeSpan MaxSingleWait = TimeSpan.FromSeconds(5);

        private readonly int requestCeiling;
        private readonly long tokenCeiling;
        private readonly Func<DateTime> nowUtc;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        // Admission is serialised so waiting callers form a queue in arrival order instead of all
        // waking together and re-racing for the same slot.
        private readonly SemaphoreSlim admission = new(1, 1);
        private readonly SemaphoreSlim parallelism;

        private readonly object sync = new();
        private readonly LinkedList<WindowEntry> window = new();
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
        /// Queue for permission to send one AIRouter request. Returns only when all three limits have
        /// room; the returned lease holds the parallel slot until it is disposed, which the caller
        /// must do as soon as the HTTP exchange completes (not when it finishes processing the body).
        /// </summary>
        /// <param name="estimatedTokens">
        /// Pessimistic size of the request: prompt estimate plus the full completion reserve. Reconciled
        /// to the provider-reported figure via <see cref="AIRouterFairUseLease.ReportActualTokens"/>.
        /// </param>
        public async Task<AIRouterFairUseLease> AcquireAsync(long estimatedTokens, CancellationToken cancellationToken = default)
        {
            // A request bigger than the entire per-minute token ceiling could never be admitted; clamp
            // it so an oversized estimate waits for an empty window rather than deadlocking forever.
            long reserved = Math.Clamp(estimatedTokens, 1, tokenCeiling);
            DateTime queuedAt = nowUtc();

            await admission.WaitAsync(cancellationToken);
            bool slotHeld = false;
            try
            {
                await parallelism.WaitAsync(cancellationToken);
                slotHeld = true;
                Interlocked.Increment(ref inFlight);

                LinkedListNode<WindowEntry> node;
                while (true)
                {
                    DateTime now = nowUtc();
                    TimeSpan wait;
                    lock (sync)
                    {
                        Prune(now);
                        wait = TimeUntilAdmissible(now, reserved);
                        if (wait <= TimeSpan.Zero)
                        {
                            node = window.AddLast(new WindowEntry(now, reserved));
                            windowTokens += reserved;
                            break;
                        }
                    }
                    if (wait > MaxSingleWait) wait = MaxSingleWait;
                    await delay(wait, cancellationToken);
                }

                Interlocked.Increment(ref totalAdmitted);
                Interlocked.Add(ref totalWaitedMs, (long)Math.Max(0, (nowUtc() - queuedAt).TotalMilliseconds));
                return new AIRouterFairUseLease(this, node);
            }
            catch
            {
                if (slotHeld)
                {
                    Interlocked.Decrement(ref inFlight);
                    parallelism.Release();
                }
                throw;
            }
            finally
            {
                admission.Release();
            }
        }

        /// <summary>
        /// Record that the router rate-limited us anyway (someone else is spending the same key, or a
        /// window boundary landed badly) and hold EVERY queued caller off until the cool-off expires.
        /// The per-minute windows guarantee the block clears, so this is a short pause rather than a
        /// reason to abandon the wake.
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
            }
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

        internal AIRouterFairUseLease(AIRouterFairUseLimiter limiter, LinkedListNode<AIRouterFairUseLimiter.WindowEntry> node)
        {
            this.limiter = limiter;
            this.node = node;
        }

        /// <summary>Book the provider's own token count against the window in place of our estimate.</summary>
        public void ReportActualTokens(long totalTokens) => limiter.Reconcile(node, totalTokens);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            limiter.ReleaseSlot();
        }
    }
}
