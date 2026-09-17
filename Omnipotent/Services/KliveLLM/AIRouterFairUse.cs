namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Client-side admission control for AIRouter's published fair-use policy:
    /// 3 parallel requests, 240 requests/minute, 10M tokens/minute.
    ///
    /// Reservations are reconciled to provider-reported usage so pessimistic completion estimates
    /// do not strand capacity. Estimates, external clients and provider policy mean this cannot
    /// guarantee absence of 429s, so provider-directed cool-offs are shared by every caller.
    ///
    /// Beyond enforcing the envelope, this decides the ORDER in which waiting requests are admitted,
    /// because with only three slots the order is what determines cost. A plain queue is oblivious to
    /// prompt-prefix cache warmth: as the agent fleet grows, the wait between two consecutive turns of
    /// the same conversation exceeds the provider's cache lifetime, the next turn re-prefills tens of
    /// thousands of tokens, service time rises, the queue lengthens, and more turns miss — a positive
    /// feedback loop that is bistable and does not recover on its own. See
    /// <see cref="AIRouterResidencyScheduler"/> for the policy and <see cref="PrefixSurvivalMeter"/>
    /// for the measurements it runs on.
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

        /// <summary>
        /// How long a freed slot is held for the conversation that just released it.
        ///
        /// An agent in a tool loop issues its next turn moments after the last one returns. Without
        /// this it re-enters the queue behind everything else on every turn, which both lengthens the
        /// queue for everyone and risks its own prefix going cold mid-wake. Idling a slot for a couple
        /// of seconds against a service time measured in minutes is a low single-digit percentage
        /// tax; re-prefilling a 55K-token prompt is not.
        /// </summary>
        private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(2.5);
        private const int MaxGraceTurns = 8;
        private static readonly TimeSpan MaxGraceWall = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Deliberately never built. A max_tokens:1 request on a resident's prefix to keep it hot is
        /// cheap when it hits and costs a full re-prefill when it misses — and it would only ever fire
        /// when a miss is suspected. Worse, a ping that misses WRITES the prefix again and evicts
        /// somebody else's under LRU pressure, so the mechanism can deepen the problem in exactly the
        /// regime it exists for. Its premise is also unverified: whether this cache is LRU-on-read or
        /// LRU-on-insert is unknown, and if the latter a ping does nothing at all. Finally, the
        /// scheduler sits below prompt assembly and does not own the prompt bytes; the session mutates
        /// them, so a ping built from a retained reference would send a DIFFERENT prefix and pollute
        /// the cache. Residency already guarantees a real dispatch inside the measured lifetime, which
        /// is the same goal without the downside. Left here so the idea is not re-litigated.
        /// </summary>
        internal const bool RefreshPingsEnabled = false;

        private readonly int maxParallel;
        private readonly int requestCeiling;
        private readonly long tokenCeiling;
        private readonly Func<DateTime> nowUtc;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        private readonly object sync = new();
        private readonly LinkedList<WindowEntry> window = new();
        private TaskCompletionSource<bool> stateChanged = NewStateSignal();
        private long windowTokens;
        private DateTime penaltyUntilUtc = DateTime.MinValue;

        private long totalAdmitted;
        private long totalWaitedMs;
        private long totalPenalties;
        private long maxWaitedMs;
        private long parkedAdmissions;
        private long wouldHavePark;
        private int inFlight;

        private readonly List<Waiter> waiters = new();
        private readonly Dictionary<string, GraceState> graces = new(StringComparer.Ordinal);
        private readonly AIRouterResidencyScheduler residency = new();
        private readonly PrefixSurvivalMeter survival;
        private int coldInFlight;
        private int oneShotInFlight;
        private AIRouterSchedulerMode mode = AIRouterSchedulerMode.Fifo;
        private int lastReportedK = -1;
        private DateTime lastCapacityReportUtc = DateTime.MinValue;

        private static readonly TimeSpan CapacityReportInterval = TimeSpan.FromMinutes(10);

        public AIRouterFairUseLimiter(
            int maxParallelRequests = PolicyMaxParallelRequests,
            int maxRequestsPerMinute = PolicyMaxRequestsPerMinute,
            long maxTokensPerMinute = PolicyMaxTokensPerMinute,
            Func<DateTime>? nowUtc = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
            : this(maxParallelRequests, maxRequestsPerMinute, maxTokensPerMinute, nowUtc, delay, null)
        {
        }

        /// <summary>Test seam: a private survival curve, so one test's measurements cannot leak into
        /// another's scheduling decisions through the process-wide meter.</summary>
        internal AIRouterFairUseLimiter(
            int maxParallelRequests,
            int maxRequestsPerMinute,
            long maxTokensPerMinute,
            Func<DateTime>? nowUtc,
            Func<TimeSpan, CancellationToken, Task>? delay,
            PrefixSurvivalMeter? survivalMeter)
        {
            maxParallel = Math.Max(1, maxParallelRequests);
            requestCeiling = Math.Max(1, (int)Math.Floor(Math.Max(1, maxRequestsPerMinute) * RequestHeadroomFraction));
            tokenCeiling = Math.Max(1, (long)Math.Floor(Math.Max(1, maxTokensPerMinute) * TokenHeadroomFraction));
            this.nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            this.delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            survival = survivalMeter ?? PrefixSurvivalMeter.Shared;
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
            long TotalPenalties,
            int Waiting = 0,
            int WarmCohortSize = 0,
            int Residents = 0,
            int TrackedConversations = 0,
            long MaxWaitedMs = 0,
            long ForcedAdmissions = 0,
            long ColdAdmissions = 0,
            long ParkedAdmissions = 0,
            long DeadZoneDispatches = 0,
            double ReusablePrefixEfficiency = 1d,
            double EffectiveLifetimeSeconds = 0d,
            double WarmServiceSeconds = 0d,
            string LifetimeSource = "",
            string Mode = "");

        public Snapshot Describe()
        {
            DateTime now = nowUtc();
            lock (sync)
            {
                Prune(now);
                var curve = survival.Describe();
                return new Snapshot(
                    InFlight: inFlight,
                    RequestsInWindow: window.Count,
                    RequestCeiling: requestCeiling,
                    TokensInWindow: windowTokens,
                    TokenCeiling: tokenCeiling,
                    PenaltyRemaining: penaltyUntilUtc > now ? penaltyUntilUtc - now : TimeSpan.Zero,
                    TotalAdmitted: totalAdmitted,
                    TotalWaitedMs: totalWaitedMs,
                    TotalPenalties: totalPenalties,
                    Waiting: waiters.Count,
                    WarmCohortSize: residency.K,
                    Residents: residency.ResidentCount,
                    TrackedConversations: residency.TrackedPrefixes,
                    MaxWaitedMs: maxWaitedMs,
                    ForcedAdmissions: residency.ForcedAdmissions,
                    ColdAdmissions: residency.ColdAdmissions,
                    ParkedAdmissions: parkedAdmissions,
                    DeadZoneDispatches: curve.DeadZoneDispatches,
                    ReusablePrefixEfficiency: curve.ReusableEfficiency,
                    EffectiveLifetimeSeconds: curve.Lifetime.TotalSeconds,
                    WarmServiceSeconds: curve.WarmServiceTime.TotalSeconds,
                    LifetimeSource: curve.LifetimeSource,
                    Mode: mode.ToString());
            }
        }

        /// <summary>
        /// Is there work for this conversation family currently queued or in flight?
        ///
        /// Exists so a stall detector can tell "waiting for one of three slots" apart from "wedged".
        /// The park ceiling deliberately exceeds KliveLLM.BriefSessionIdleLimit — dispatching onto an
        /// evicted prefix is the one thing the efficiency target forbids — so a parked request can
        /// outlast a watchdog's patience while being exactly as healthy as intended. Without this
        /// signal the scheduler would manufacture false stalls out of its own correct behaviour.
        /// </summary>
        internal bool HasQueuedWork(string sessionIdPrefix)
        {
            if (string.IsNullOrEmpty(sessionIdPrefix)) return false;
            lock (sync)
            {
                foreach (Waiter waiter in waiters)
                    if (waiter.Info.PrefixKey.StartsWith(sessionIdPrefix, StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        internal void SetMode(AIRouterSchedulerMode value) { lock (sync) { mode = value; } }

        internal void SetAlpha(double value) { lock (sync) { residency.SetAlpha(value); } }

        /// <summary>
        /// The one line that tells Klives the truth about capacity: how many conversations can be kept
        /// warm, how many there actually are, and therefore how oversubscribed the key is. Returned at
        /// most once per interval, or whenever the cohort size changes. Null when there is nothing new
        /// to say. The scheduler reports this rather than acting on it — it cannot create slot-seconds,
        /// only spend them well.
        /// </summary>
        internal string? ConsumeCapacityReport()
        {
            lock (sync)
            {
                DateTime now = nowUtc();
                var curve = survival.Describe();
                int k = residency.K;
                bool changed = k != lastReportedK;
                if (!changed && now - lastCapacityReportUtc < CapacityReportInterval) return null;
                if (curve.Samples < PrefixSurvivalMeter.MinSamplesToSchedule) return null;

                lastReportedK = k;
                lastCapacityReportUtc = now;

                int live = residency.TrackedPrefixes;
                double over = k > 0 ? (double)live / k : 0d;
                string oversubscribed = live > k ? $" ({over:0.0}x oversubscribed)" : "";
                long avgWait = totalAdmitted > 0 ? totalWaitedMs / totalAdmitted : 0;

                return $"AIRouter capacity [{mode}]: {maxParallel} slots x {curve.Lifetime.TotalSeconds:0}s measured cache " +
                       $"lifetime ({curve.LifetimeSource}) / {curve.WarmServiceTime.TotalSeconds:0}s warm service time " +
                       $"= {k} conversations can stay warm. {live} seen recently{oversubscribed}; {waiters.Count} waiting. " +
                       (mode == AIRouterSchedulerMode.Observe ? $"Would have parked {wouldHavePark:N0} so far. " : "") +
                       $"Reusable-prefix efficiency {curve.ReusableEfficiency:P1} over {curve.ReusableSamples:N0} continuations; " +
                       $"{curve.DeadZoneDispatches:N0} dead-zone dispatches. Mean wait {avgWait:N0}ms, max {maxWaitedMs:N0}ms. " +
                       "This ceiling is arithmetic: warming more conversations needs more slots, a longer-lived cache or " +
                       "shorter turns. The scheduler cannot create capacity, only spend it well.";
            }
        }

        /// <summary>Queue for permission to send one AIRouter request. Returns only when all published limits have
        /// room; the returned lease holds the parallel slot until it is disposed, which the caller
        /// must do as soon as the HTTP exchange completes (not when it finishes processing the body).</summary>
        /// <param name="estimatedTokens">
        /// Pessimistic size of the request: prompt estimate plus the full completion reserve. Reconciled
        /// to the provider-reported figure via <see cref="AIRouterFairUseLease.ReportActualTokens"/>.
        /// </param>
        public Task<AIRouterFairUseLease> AcquireAsync(long estimatedTokens,
            CancellationToken cancellationToken = default)
            => AcquireAsync(estimatedTokens, AIRouterWorkTicket.Anonymous("unclassified"), cancellationToken);

        /// <summary>
        /// Admission for a request whose conversation the caller can identify. The ticket is what lets
        /// this keep consecutive turns of one conversation inside the measured cache lifetime instead
        /// of interleaving every conversation until they all go cold.
        /// </summary>
        internal async Task<AIRouterFairUseLease> AcquireAsync(long estimatedTokens,
            AIRouterWorkTicket info, CancellationToken cancellationToken = default)
        {
            // A request bigger than the entire per-minute token ceiling could never be admitted; clamp
            // it so an oversized estimate waits for an empty window rather than deadlocking forever.
            long reserved = Math.Clamp(estimatedTokens, 1, tokenCeiling);
            DateTime queuedAt = nowUtc();
            var waiter = new Waiter(info, reserved, queuedAt);

            lock (sync)
            {
                waiters.Add(waiter);
                // Everyone re-evaluates: a newly arrived request may outrank a waiter that is mid-sleep.
                PulseStateChanged();
            }

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    AIRouterFairUseLease? lease;
                    TimeSpan wait;
                    Task recheck;
                    lock (sync)
                    {
                        DateTime now = nowUtc();
                        Prune(now);
                        ExpireGraces(now);
                        // The waiting set is what tells residency which conversations are mid-wake, so
                        // it never expires or evicts one whose next turn is already queued.
                        residency.Maintain(now, survival.EffectiveLifetime(), survival.WarmServiceTime(),
                            survival.NonResidentSlotShare(), maxParallel, WaitingPrefixKeys());
                        recheck = stateChanged.Task;
                        lease = TryAdmitLocked(waiter, now, out wait);
                    }

                    if (lease != null) return lease;

                    // A zero wait means the windows allow it but a slot or the cohort does not; poll on
                    // the signal rather than computing a deadline that another caller's release invalidates.
                    if (wait <= TimeSpan.Zero || wait > MaxSingleWait) wait = MaxSingleWait;
                    await await Task.WhenAny(delay(wait, cancellationToken), recheck.WaitAsync(cancellationToken));
                }
            }
            finally
            {
                // A cancelled or faulted caller must never leave its claim behind. The slot itself was
                // never taken while waiting, so there is nothing else to unwind.
                lock (sync)
                {
                    waiters.Remove(waiter);
                    PulseStateChanged();
                }
            }
        }

        private IEnumerable<string> WaitingPrefixKeys()
        {
            foreach (Waiter waiter in waiters) yield return waiter.Info.PrefixKey;
        }

        private AIRouterFairUseLease? TryAdmitLocked(Waiter self, DateTime now, out TimeSpan wait)
        {
            wait = TimeUntilAdmissible(now, self.Reserved);
            if (wait > TimeSpan.Zero) return null;

            // Never schedule on a guess. Until the survival curve rests on real evidence the ordering
            // degrades to arrival order, which is what the gate did before any of this existed.
            bool informed = survival.HasEnoughEvidence;
            bool enforcing = mode == AIRouterSchedulerMode.Enforce && informed;
            bool observing = mode == AIRouterSchedulerMode.Observe && informed;

            TimeSpan lifetime = survival.EffectiveLifetime();
            TimeSpan warmService = survival.WarmServiceTime();
            bool pastDeadline = now >= residency.Deadline(self.Info, self.EnqueuedAt, lifetime);

            if (!HasCapacityForLocked(self, now, pastDeadline)) { wait = TimeSpan.Zero; return null; }

            if (enforcing || observing)
            {
                bool may = residency.MayDispatch(self.Info, now, pastDeadline, coldInFlight, oneShotInFlight,
                    warmService, out string reason);
                if (!may)
                {
                    if (enforcing)
                    {
                        residency.NotePark(reason);
                        wait = TimeSpan.Zero;
                        return null;
                    }
                    // Observe: record the decision that WOULD have been taken, then dispatch anyway.
                    wouldHavePark++;
                }
            }

            if (!IsBestCandidateLocked(self, now, lifetime, warmService, enforcing)) { wait = TimeSpan.Zero; return null; }

            return AdmitLocked(self, now, pastDeadline);
        }

        private AIRouterFairUseLease AdmitLocked(Waiter self, DateTime now, bool pastDeadline)
        {
            var node = window.AddLast(new WindowEntry(now, self.Reserved));
            windowTokens += self.Reserved;
            inFlight++;
            totalAdmitted++;

            bool wasResident = residency.IsResident(self.Info.PrefixKey);
            TimeSpan? gap = residency.GapSinceLastDispatch(self.Info.PrefixKey, now);
            residency.OnDispatch(self.Info, now, pastDeadline);
            if (!wasResident) coldInFlight++;
            if (self.Info.Class == AIRouterWorkClass.OneShot) oneShotInFlight++;

            ConsumeGraceLocked(self.Info.PrefixKey);

            long queueDurationMs = (long)Math.Max(0, (now - self.EnqueuedAt).TotalMilliseconds);
            totalWaitedMs += queueDurationMs;
            if (queueDurationMs > maxWaitedMs) maxWaitedMs = queueDurationMs;
            if (queueDurationMs > 5_000) parkedAdmissions++;

            // A slot changed hands: waiters that deferred to this one must re-evaluate now rather than
            // sleeping out their poll interval.
            PulseStateChanged();

            return new AIRouterFairUseLease(this, node, queueDurationMs, self.Info, gap, wasResident, now);
        }

        /// <summary>
        /// Would admitting this waiter now jump ahead of a better one that could also go? Deferral only
        /// ever happens to a candidate that is itself fully admissible this instant, so there is no
        /// arrangement in which every waiter defers and nothing runs.
        /// </summary>
        private bool IsBestCandidateLocked(Waiter self, DateTime now, TimeSpan lifetime,
            TimeSpan warmService, bool enforcing)
        {
            foreach (Waiter other in waiters)
            {
                if (ReferenceEquals(other, self)) continue;
                if (Compare(other, self, lifetime, enforcing) >= 0) continue;
                if (TimeUntilAdmissible(now, other.Reserved) > TimeSpan.Zero) continue;

                bool otherPast = now >= residency.Deadline(other.Info, other.EnqueuedAt, lifetime);
                if (!HasCapacityForLocked(other, now, otherPast)) continue;
                if (enforcing && !residency.MayDispatch(other.Info, now, otherPast, coldInFlight,
                        oneShotInFlight, warmService, out _)) continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Ordering. Interactive first because a human is blocked on it; then by cache deadline, so the
        /// request whose prefix is closest to being lost goes first. The inversion that falls out of
        /// this is deliberate: an already-cold request has nothing left to preserve, so it ranks behind
        /// warm ones — and as it waits its own park deadline approaches, which eventually promotes it.
        /// Ageing and de-prioritisation are the same mechanism, which is why there is no separate
        /// anti-starvation rule to keep in sync.
        /// </summary>
        private int Compare(Waiter a, Waiter b, TimeSpan lifetime, bool enforcing)
        {
            if (!enforcing) return a.EnqueuedAt.CompareTo(b.EnqueuedAt);

            int band = AIRouterResidencyScheduler.Band(a.Info.Class)
                .CompareTo(AIRouterResidencyScheduler.Band(b.Info.Class));
            if (band != 0) return band;

            int deadline = residency.Deadline(a.Info, a.EnqueuedAt, lifetime)
                .CompareTo(residency.Deadline(b.Info, b.EnqueuedAt, lifetime));
            if (deadline != 0) return deadline;

            return a.EnqueuedAt.CompareTo(b.EnqueuedAt);
        }

        private bool HasCapacityForLocked(Waiter self, DateTime now, bool pastDeadline)
        {
            int heldForOthers = 0;
            // A human waiting, or a request that has already waited out its park ceiling, takes a
            // reserved slot rather than idling behind it.
            bool preempts = self.Info.Class == AIRouterWorkClass.Interactive || pastDeadline;
            if (!preempts)
            {
                foreach (var kv in graces)
                {
                    if (kv.Value.UntilUtc <= now) continue;
                    if (string.Equals(kv.Key, self.Info.PrefixKey, StringComparison.Ordinal)) continue;
                    heldForOthers++;
                }
            }
            return inFlight + heldForOthers < maxParallel;
        }

        private void ExpireGraces(DateTime now)
        {
            if (graces.Count == 0) return;
            List<string>? drop = null;
            foreach (var kv in graces)
                if (kv.Value.UntilUtc <= now) (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null) foreach (string key in drop) graces.Remove(key);
        }

        private void ConsumeGraceLocked(string prefixKey)
        {
            if (graces.TryGetValue(prefixKey, out GraceState state))
                graces[prefixKey] = state with { UntilUtc = DateTime.MinValue };
        }

        private void OfferGraceLocked(AIRouterFairUseLease lease, DateTime now)
        {
            // Only a conversation that can actually reuse a prefix is worth idling a slot for, and only
            // while it is still in the warm cohort.
            if (lease.Info.Class is not (AIRouterWorkClass.Interactive or AIRouterWorkClass.Agent)) return;
            if (!residency.IsResident(lease.Info.PrefixKey)) return;

            // Never let reservations own every slot. A grace window is a bet that one conversation
            // comes straight back; with three slots and more conversations than that, taking the bet
            // on all of them at once stalls everybody else for the full window on every release — a
            // queue that idles its own capacity waiting for callers who are not coming.
            int active = 0;
            foreach (var kv in graces)
                if (kv.Value.UntilUtc > now && !string.Equals(kv.Key, lease.Info.PrefixKey, StringComparison.Ordinal))
                    active++;
            if (active >= maxParallel - 1) return;

            graces.TryGetValue(lease.Info.PrefixKey, out GraceState existing);
            DateTime startedAt = existing.StartedAt == default ? now : existing.StartedAt;
            // A retry is not progress: it must not spend the allowance that keeps a genuine tool loop
            // holding its slot, or a flapping provider would quietly exhaust it.
            int turns = lease.Info.Intent == AIRouterSlotIntent.Turn ? existing.Turns + 1 : existing.Turns;

            if (turns > MaxGraceTurns || now - startedAt > MaxGraceWall)
            {
                graces.Remove(lease.Info.PrefixKey);
                return;
            }
            graces[lease.Info.PrefixKey] = new GraceState(now + GraceWindow, startedAt, turns);
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
                totalPenalties++;
                PulseStateChanged();
            }
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

        internal void ReleaseSlot(AIRouterFairUseLease lease)
        {
            lock (sync)
            {
                DateTime now = nowUtc();
                inFlight--;
                if (inFlight < 0) inFlight = 0;
                if (!lease.WasResident && coldInFlight > 0) coldInFlight--;
                if (lease.Info.Class == AIRouterWorkClass.OneShot && oneShotInFlight > 0) oneShotInFlight--;
                residency.OnRelease(lease.Info.PrefixKey, now);
                OfferGraceLocked(lease, now);
                PulseStateChanged();
            }
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

        /// <summary>Feed one completed request's cache outcome back into the survival curve. This is
        /// the loop that lets the scheduler learn a lifetime nobody can configure.</summary>
        internal void RecordCacheOutcome(AIRouterFairUseLease lease, long promptTokens, long cachedTokens,
            TimeSpan occupancy)
            => survival.RecordOutcome(lease.Info.PrefixKey, lease.GapSinceLastDispatch, promptTokens,
                cachedTokens, lease.WasResident, lease.Info.Class == AIRouterWorkClass.OneShot,
                occupancy, nowUtc());

        internal DateTime UtcNow() => nowUtc();

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

        private readonly record struct GraceState(DateTime UntilUtc, DateTime StartedAt, int Turns);

        private sealed class Waiter
        {
            internal Waiter(AIRouterWorkTicket info, long reserved, DateTime enqueuedAt)
            {
                Info = info;
                Reserved = reserved;
                EnqueuedAt = enqueuedAt;
            }

            internal AIRouterWorkTicket Info { get; }
            internal long Reserved { get; }
            internal DateTime EnqueuedAt { get; }
        }
    }

    /// <summary>
    /// One admitted AIRouter request. Disposing releases the parallel slot; the window entry lives on
    /// for the rest of the minute so the per-minute limits keep counting it.
    /// </summary>
    public sealed class AIRouterFairUseLease : IDisposable
    {
        private readonly AIRouterFairUseLimiter limiter;
        private readonly LinkedListNode<AIRouterFairUseLimiter.WindowEntry> node;
        private readonly DateTime grantedAtUtc;
        private TimeSpan occupancy;
        private int released;

        internal AIRouterFairUseLease(AIRouterFairUseLimiter limiter,
            LinkedListNode<AIRouterFairUseLimiter.WindowEntry> node, long queueDurationMs,
            AIRouterWorkTicket info, TimeSpan? gapSinceLastDispatch, bool wasResident, DateTime grantedAtUtc)
        {
            this.limiter = limiter;
            this.node = node;
            this.grantedAtUtc = grantedAtUtc;
            QueueDurationMs = queueDurationMs;
            Info = info;
            GapSinceLastDispatch = gapSinceLastDispatch;
            WasResident = wasResident;
        }

        /// <summary>Time spent waiting locally before this request was allowed to reach AIRouter.</summary>
        public long QueueDurationMs { get; }

        internal AIRouterWorkTicket Info { get; }

        /// <summary>How long since this exact prefix was last sent, or null if it has never been sent.
        /// Null means a genuine cold start with no reusable prefix — which must NOT count against
        /// prefix efficiency, because nothing was wasted.</summary>
        internal TimeSpan? GapSinceLastDispatch { get; }

        internal bool WasResident { get; }

        /// <summary>Book the provider's own token count against the window in place of our estimate.</summary>
        public void ReportActualTokens(long totalTokens) => limiter.Reconcile(node, totalTokens);

        /// <summary>
        /// Report what the provider actually served from cache. Called from the usage-reporting path,
        /// which on the buffered route runs AFTER disposal and on the streaming route runs before it —
        /// so the occupancy is taken from the recorded value when it exists and measured live otherwise.
        /// </summary>
        internal void ReportCacheOutcome(long promptTokens, long cachedTokens)
        {
            TimeSpan spent = occupancy > TimeSpan.Zero ? occupancy : limiter.UtcNow() - grantedAtUtc;
            limiter.RecordCacheOutcome(this, promptTokens, cachedTokens, spent);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            occupancy = limiter.UtcNow() - grantedAtUtc;
            limiter.ReleaseSlot(this);
        }
    }
}
