namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Measures how long a prompt prefix actually survives in the provider's cache, and how long a
    /// request actually occupies one of the three parallel slots.
    ///
    /// Both numbers have to be MEASURED rather than configured. AIRouter is an OpenAI-compatible
    /// endpoint over an implicit radix/prefix cache: we never send a cache TTL, and eviction is LRU
    /// under memory pressure shared with every other tenant on the box. So the effective lifetime is
    /// not a constant we can look up — it drifts with other people's load, and the only observable is
    /// usage.prompt_tokens_details.cached_tokens on our own responses.
    ///
    /// The scheduler's entire capacity calculation rests on these two figures, which is why this is
    /// the first thing to ship and run on its own: until the survival curve has settled, nobody —
    /// including this code — knows what the real numbers are.
    /// </summary>
    internal sealed class PrefixSurvivalMeter
    {
        /// <summary>
        /// Upper edges of the gap buckets, in seconds. Log-spaced because the interesting region spans
        /// two orders of magnitude and we only ever need the knee, not a smooth curve.
        /// </summary>
        private static readonly double[] BucketEdgesSeconds = { 15, 30, 60, 120, 240, 480, 900, double.PositiveInfinity };

        /// <summary>
        /// The survival a bucket must show for us to treat its whole span as "still warm".
        ///
        /// This is 0.99 rather than a comfortable 0.8 BECAUSE the target is 99% prefix efficiency.
        /// T_eff is defined as "the gap at which 99% of prefixes still survive", which makes the
        /// residency invariant and the efficiency bar the same statement. It buys that by producing a
        /// materially shorter lifetime — and therefore a smaller warm cohort — than a looser
        /// threshold would. That cost is the price of the bar, not a tuning regression.
        /// </summary>
        private const double SurvivalThreshold = 0.99;

        /// <summary>Decayed sample weight a bucket needs before its survival figure is trusted.</summary>
        private const double MinBucketWeight = 8d;

        /// <summary>
        /// How much of the REUSABLE PREFIX must come back from cache to call the dispatch a hit.
        ///
        /// This was 0.5 of the whole prompt on the premise that a radix cache is bimodal — a request
        /// matches nearly all of its prefix or nearly none — so the split barely mattered. That premise
        /// is false for this fleet, and the third mode it misses is the one that matters: every agent
        /// prompt opens with the same system block and tool schemas, that block is referenced by every
        /// project on the box and so is never the thing LRU evicts, and it is 27K of a 50K commander
        /// prompt. A continuation whose own conversation was evicted still comes back 52–58% cached on
        /// the shared preamble alone — over the old bar, so it scored as a hit.
        ///
        /// That made the meter structurally incapable of observing an eviction: every band read ~100%
        /// survival, no band ever failed, the lifetime climbed to its ceiling, K sized the warm cohort
        /// to the entire fleet, nothing was ever parked, and the scheduler degraded to the FIFO gate it
        /// was written to replace — while reporting perfect health. Measuring against the reusable
        /// prefix instead of the whole prompt removes the preamble from the denominator; 0.9 then puts
        /// the bar above anything a surviving preamble can reach on its own, and in line with the 99%
        /// efficiency target the rest of this class is written against.
        /// </summary>
        private const double HitFraction = 0.9;

        /// <summary>Bound on the per-prefix prompt sizes kept for the reusable-prefix denominator.</summary>
        private const int MaxTrackedPrefixes = 512;

        /// <summary>Tracks drift in other tenants' load without reacting to individual requests.</summary>
        private static readonly TimeSpan DecayHalfLife = TimeSpan.FromHours(1);

        /// <summary>Before evidence exists, assume little. Small enough to be safe, large enough that
        /// the derived cohort size is still at least two at a realistic service time.</summary>
        internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(120);

        /// <summary>Floor: below this the scheduler could not keep anything warm and would be pure overhead.</summary>
        internal static readonly TimeSpan MinLifetime = TimeSpan.FromSeconds(45);

        /// <summary>Consecutive resident misses that mean the provider is evicting far sooner than the
        /// decayed curve believes. A one-hour half-life cannot react to an eviction storm on its own.</summary>
        private const int CollapseStreak = 6;

        /// <summary>Enough requests that a figure means something. Below this the scheduler must not
        /// act on the curve at all.</summary>
        internal const int MinSamplesToSchedule = 60;

        private const double Alpha = 0.2;

        /// <summary>One meter per process: every service sharing the router key shares the curve.
        /// Declared after the statics it reads: a static field initializer runs in declaration order,
        /// so constructing this any earlier sees a null bucket table.</summary>
        internal static readonly PrefixSurvivalMeter Shared = new();

        private readonly object sync = new();
        private readonly double[] bucketHits = new double[BucketEdgesSeconds.Length];
        private readonly double[] bucketWeight = new double[BucketEdgesSeconds.Length];
        private DateTime decayedAtUtc = DateTime.MinValue;

        /// <summary>The prompt size of each prefix's last dispatch — the denominator for "how much of
        /// what this request COULD have reused actually came back". Without it the only available
        /// denominator is the whole prompt, which the shared preamble alone can satisfy.</summary>
        private readonly Dictionary<string, PriorDispatch> priorDispatch = new(StringComparer.Ordinal);

        private readonly record struct PriorDispatch(long PromptTokens, DateTime AtUtc);

        // Decayed aggregate of the metric this whole class exists to hold up: of the continuations we
        // sent, how many were actually served from cache. Decays alongside the bands so it tracks the
        // provider's current behaviour rather than the whole run's history.
        private double recentReusable;
        private double recentHits;

        private long samples;
        private long reusableSamples;      // dispatches that HAD a prefix worth reusing
        private long reusableHits;
        private long deadZoneDispatches;
        private int residentMissStreak;
        private TimeSpan lifetime = DefaultLifetime;
        private DateTime lifetimeRaisedAtUtc = DateTime.MinValue;
        private int consecutiveRaiseVotes;

        // Slot occupancy, split by whether the request was served from cache. The gap between the two
        // is the prefill penalty, and it is what makes the collapse loop run in both directions.
        private double warmServiceMs = 45_000;
        private double warmDeviationMs = 15_000;
        private double coldServiceMs = 90_000;
        private bool haveWarmSample;
        private bool haveColdSample;
        private double nonResidentShare = 0.2;

        internal readonly record struct SurvivalSnapshot(
            long Samples,
            long ReusableSamples,
            long ReusableHits,
            long DeadZoneDispatches,
            TimeSpan Lifetime,
            string LifetimeSource,
            TimeSpan WarmServiceTime,
            TimeSpan ColdServiceTime,
            double NonResidentSlotShare)
        {
            internal double ReusableEfficiency => ReusableSamples > 0 ? (double)ReusableHits / ReusableSamples : 1d;
        }

        /// <summary>True once the curve rests on enough evidence to schedule against. Until then the
        /// limiter must stay in plain FIFO no matter what mode is configured — scheduling on a guess
        /// is how you build a system that is confidently wrong.</summary>
        internal bool HasEnoughEvidence
        {
            get { lock (sync) { return samples >= MinSamplesToSchedule; } }
        }

        internal TimeSpan EffectiveLifetime()
        {
            lock (sync) { return lifetime; }
        }

        /// <summary>Biased upward from the mean so a variance spike shrinks the cohort rather than
        /// overfilling it. Every estimate in this class errs towards a smaller warm set.</summary>
        internal TimeSpan WarmServiceTime()
        {
            lock (sync) { return TimeSpan.FromMilliseconds(Math.Max(1, warmServiceMs + 0.5 * warmDeviationMs)); }
        }

        internal TimeSpan ColdServiceTime()
        {
            lock (sync) { return TimeSpan.FromMilliseconds(Math.Max(1, coldServiceMs)); }
        }

        /// <summary>
        /// What one live conversation costs a slot RIGHT NOW, warm and cold service time blended by
        /// the rolling share of continuations actually being served from cache.
        ///
        /// <see cref="WarmServiceTime"/> alone is the right input for sizing the warm cohort, and the
        /// wrong one for deciding how many conversations may be live at all. The two service times
        /// differ by more than an order of magnitude on this fleet — a cached turn returns in seconds,
        /// a full re-prefill of the same prompt takes a minute or two — which is exactly what makes
        /// the congestion collapse bistable: once enough conversations go cold, throughput falls far
        /// enough that the rest cannot get a turn inside the cache lifetime either, and the fleet has
        /// no way back. Blending by the measured efficiency gives the one number that shrinks when the
        /// fleet is cold, so admission tightens on its own and the fleet can re-warm. With no evidence
        /// yet this reads as fully warm, which is the optimistic direction; the volume gates on the
        /// consumer side are what stop that being acted on.
        /// </summary>
        internal TimeSpan ExpectedServiceTime()
        {
            lock (sync)
            {
                double efficiency = recentReusable > 0
                    ? Math.Clamp(recentHits / recentReusable, 0d, 1d)
                    : 1d;
                double warm = Math.Max(1d, warmServiceMs + 0.5 * warmDeviationMs);
                double cold = Math.Max(warm, coldServiceMs);
                return TimeSpan.FromMilliseconds(warm * efficiency + cold * (1d - efficiency));
            }
        }

        internal double NonResidentSlotShare()
        {
            lock (sync) { return Math.Clamp(nonResidentShare, 0d, 0.9d); }
        }

        internal SurvivalSnapshot Describe()
        {
            lock (sync)
            {
                string source = samples < MinSamplesToSchedule ? "default"
                    : lifetime >= LifetimeCeiling() ? "capped-by-brief-idle"
                    : "measured";
                return new SurvivalSnapshot(samples, reusableSamples, reusableHits, deadZoneDispatches,
                    lifetime, source,
                    TimeSpan.FromMilliseconds(warmServiceMs), TimeSpan.FromMilliseconds(coldServiceMs),
                    nonResidentShare);
            }
        }

        /// <summary>
        /// T_eff can never exceed the point at which OUR OWN client gives up on the session.
        /// KliveLLM.BriefSessionIdleLimit makes the next assembly rotate the brief and start a fresh
        /// epoch, so believing a prefix is warm past that is believing in something the process has
        /// already thrown away. Expressed against the real constant, never a copy of it.
        /// </summary>
        internal static TimeSpan LifetimeCeiling()
            => KliveLLM.BriefSessionIdleLimit - TimeSpan.FromSeconds(30);

        /// <summary>Record one completed request: how long its prefix had been idle, and how much of
        /// it the provider actually served from cache.</summary>
        /// <param name="outsideCohort">
        /// True for work that can never be a cohort member however big the cohort gets — a one-shot
        /// with no prefix to reuse. This must NOT be "was not resident this time": that is an outcome
        /// of the cohort size, and feeding an outcome back in as an input makes the two chase each
        /// other to the floor. Measured in simulation, that loop drove the cohort to 2 and the share
        /// to 100%, for no reason other than that they were each other's cause.
        /// </param>
        internal void RecordOutcome(string prefixKey, TimeSpan? gapSinceLastDispatch, long promptTokens,
            long cachedTokens, bool wasResident, bool outsideCohort, TimeSpan slotOccupancy, DateTime nowUtc)
        {
            if (promptTokens <= 0) return;
            if (cachedTokens < 0) cachedTokens = 0;
            if (cachedTokens > promptTokens) cachedTokens = promptTokens;

            lock (sync)
            {
                Decay(nowUtc);
                samples++;

                // What this request could have reused: the previous prompt on this exact prefix, which
                // is a prefix of this one by construction. No prior dispatch means nothing was reusable,
                // so there is no hit to claim however much the shared preamble returned.
                long reusable = priorDispatch.TryGetValue(prefixKey, out PriorDispatch prior)
                    ? Math.Min(prior.PromptTokens, promptTokens)
                    : 0;
                RememberDispatch(prefixKey, promptTokens, nowUtc);
                bool hit = reusable > 0 && cachedTokens >= HitFraction * reusable;

                if (slotOccupancy > TimeSpan.Zero)
                {
                    double ms = slotOccupancy.TotalMilliseconds;
                    if (hit)
                    {
                        double deviation = Math.Abs(ms - warmServiceMs);
                        warmServiceMs = haveWarmSample ? warmServiceMs + Alpha * (ms - warmServiceMs) : ms;
                        warmDeviationMs = haveWarmSample ? warmDeviationMs + Alpha * (deviation - warmDeviationMs) : deviation;
                        haveWarmSample = true;
                    }
                    else
                    {
                        coldServiceMs = haveColdSample ? coldServiceMs + Alpha * (ms - coldServiceMs) : ms;
                        haveColdSample = true;
                    }
                }

                nonResidentShare += Alpha * ((outsideCohort ? 1d : 0d) - nonResidentShare);

                // A first-ever dispatch has no prefix to reuse; it is a legitimate cold start and must
                // not be counted against efficiency. Only a CONTINUATION can waste a warm prefix.
                if (gapSinceLastDispatch is not { } gap) return;

                // The scheduler remembers this prefix but we no longer hold what its last prompt cost,
                // so there is no honest denominator. Recording it either way would invent evidence:
                // as a miss it convicts a prefix we cannot size, as a hit it is the old bug. Drop it.
                if (reusable <= 0) return;

                reusableSamples++;
                recentReusable += 1d;
                if (hit) { reusableHits++; recentHits += 1d; }

                int index = BucketFor(gap);
                bucketWeight[index] += 1d;
                if (hit) bucketHits[index] += 1d;

                // The dead zone: past what the provider kept, but short of what would have made our
                // own client rotate the session. Every sample in here is a wasted warm prefix, which
                // is precisely what the 99% target forbids. It is counted separately because it is
                // the single number that says whether the scheduler is doing its job.
                if (gap > lifetime && gap < KliveLLM.BriefSessionIdleLimit) deadZoneDispatches++;

                if (wasResident)
                {
                    residentMissStreak = hit ? 0 : residentMissStreak + 1;
                    if (residentMissStreak >= CollapseStreak)
                    {
                        // Do not wait an hour for the decay to notice. Halve immediately, re-earn upward.
                        lifetime = ClampLifetime(TimeSpan.FromMilliseconds(lifetime.TotalMilliseconds / 2));
                        residentMissStreak = 0;
                        consecutiveRaiseVotes = 0;
                        return;
                    }
                }

                Reestimate(nowUtc);
            }
        }

        private void Reestimate(DateTime nowUtc)
        {
            if (samples < MinSamplesToSchedule) return;

            // Whatever the per-band picture says, the aggregate is the thing we actually promised.
            //
            // Band-level survival is a proxy, and a sparse one: nearly every sample lands in the one or
            // two bands the scheduler's own pacing produces, so the long-gap bands can stay permanently
            // below the weight needed to convict them even while they are quietly missing. Measured in
            // simulation that let the estimate drift to its ceiling — more than twice the real cache
            // lifetime — which inflated the cohort, which lengthened the gaps that caused the misses.
            // So close the loop on the target itself: if the rolling share of continuations being
            // served from cache is under the bar, the lifetime is too optimistic by definition,
            // whatever the bands say. Step it down at once and let it re-earn the ground.
            if (recentReusable >= MinSamplesToSchedule && recentHits / recentReusable < SurvivalThreshold)
            {
                TimeSpan reduced = ClampLifetime(NextBucketDown(lifetime));
                if (reduced < lifetime)
                {
                    lifetime = reduced;
                    consecutiveRaiseVotes = 0;
                    return;
                }
            }


            // Survival falls monotonically with the gap, so walk upward and note two separate things:
            // the longest gap band we have actually VERIFIED as safe, and the first band that shows
            // real evidence of eviction. A band with too little data is skipped, never counted as a
            // failure — traffic clusters at a few characteristic gaps, so most bands are simply
            // unobserved.
            TimeSpan verifiedSafe = MinLifetime;
            TimeSpan? failedAt = null;
            for (int i = 0; i < BucketEdgesSeconds.Length; i++)
            {
                if (bucketWeight[i] < MinBucketWeight) continue;
                double edgeSeconds = BucketEdgesSeconds[i];
                TimeSpan edge = double.IsPositiveInfinity(edgeSeconds)
                    ? LifetimeCeiling() : TimeSpan.FromSeconds(edgeSeconds);
                if (bucketHits[i] / bucketWeight[i] < SurvivalThreshold) { failedAt = edge; break; }
                verifiedSafe = edge;
            }

            // Real evidence of eviction pulls the estimate back at once and ungated: a shorter lifetime
            // only shrinks the cohort, and being wrong in that direction costs latency. Being wrong the
            // other way admits a cohort that cannot be kept warm, which is the original failure.
            if (failedAt.HasValue)
            {
                TimeSpan capped = ClampLifetime(verifiedSafe);
                if (capped < lifetime) { lifetime = capped; consecutiveRaiseVotes = 0; }
                return;
            }

            // Nothing has failed anywhere. Absence of evidence at longer gaps is NOT evidence of
            // eviction — and treating it as such is a trap, because a scheduler that keeps every
            // conversation tightly warm only ever observes short gaps and would pin its own estimate at
            // the floor forever, parking most of the fleet for no reason. So the estimate is allowed to
            // explore one band beyond what it has verified. That is cheap to be wrong about: a run of
            // resident misses halves it immediately.
            TimeSpan target = ClampLifetime(verifiedSafe > lifetime ? verifiedSafe : NextBucketUp(lifetime));
            if (target <= lifetime) { consecutiveRaiseVotes = 0; return; }
            if (++consecutiveRaiseVotes < 3) return;
            if (nowUtc - lifetimeRaisedAtUtc < lifetime) return;
            lifetime = target;
            lifetimeRaisedAtUtc = nowUtc;
            consecutiveRaiseVotes = 0;
        }

        /// <summary>The next band below the current estimate, for pulling an over-optimistic lifetime
        /// back towards what the provider is really doing.</summary>
        private static TimeSpan NextBucketDown(TimeSpan current)
        {
            TimeSpan below = MinLifetime;
            foreach (double edge in BucketEdgesSeconds)
            {
                if (double.IsPositiveInfinity(edge)) break;
                var candidate = TimeSpan.FromSeconds(edge);
                if (candidate >= current) break;
                below = candidate;
            }
            return below;
        }

        private static TimeSpan NextBucketUp(TimeSpan current)
        {
            foreach (double edge in BucketEdgesSeconds)
            {
                if (double.IsPositiveInfinity(edge)) break;
                if (edge > current.TotalSeconds) return TimeSpan.FromSeconds(edge);
            }
            return LifetimeCeiling();
        }

        private static TimeSpan ClampLifetime(TimeSpan value)
        {
            TimeSpan ceiling = LifetimeCeiling();
            if (ceiling < MinLifetime) ceiling = MinLifetime;
            return value < MinLifetime ? MinLifetime : value > ceiling ? ceiling : value;
        }

        /// <summary>
        /// Keep this dispatch's prompt size as the next one's reusable-prefix denominator. Bounded: a
        /// prefix key carries a cache epoch, so every compaction and brief rotation mints a new one and
        /// the map would otherwise grow for the life of the process. Entries past the point where our
        /// own client abandons the session can never be a denominator again, so they go first, and a
        /// clear-out is the fallback if a burst of fresh keys outruns that.
        /// </summary>
        private void RememberDispatch(string prefixKey, long promptTokens, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(prefixKey)) return;
            if (priorDispatch.Count >= MaxTrackedPrefixes && !priorDispatch.ContainsKey(prefixKey))
            {
                DateTime cutoff = nowUtc - KliveLLM.BriefSessionIdleLimit;
                List<string>? stale = null;
                foreach (var kv in priorDispatch)
                    if (kv.Value.AtUtc <= cutoff) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null) foreach (string key in stale) priorDispatch.Remove(key);
                if (priorDispatch.Count >= MaxTrackedPrefixes) priorDispatch.Clear();
            }
            priorDispatch[prefixKey] = new PriorDispatch(promptTokens, nowUtc);
        }

        private static int BucketFor(TimeSpan gap)
        {
            double seconds = gap.TotalSeconds;
            for (int i = 0; i < BucketEdgesSeconds.Length; i++)
                if (seconds <= BucketEdgesSeconds[i]) return i;
            return BucketEdgesSeconds.Length - 1;
        }

        private void Decay(DateTime nowUtc)
        {
            if (decayedAtUtc == DateTime.MinValue) { decayedAtUtc = nowUtc; return; }
            TimeSpan elapsed = nowUtc - decayedAtUtc;
            if (elapsed <= TimeSpan.Zero) return;
            decayedAtUtc = nowUtc;
            double factor = Math.Pow(0.5, elapsed.TotalSeconds / DecayHalfLife.TotalSeconds);
            if (factor >= 1d) return;
            for (int i = 0; i < bucketWeight.Length; i++)
            {
                bucketWeight[i] *= factor;
                bucketHits[i] *= factor;
            }
            recentReusable *= factor;
            recentHits *= factor;
        }
    }
}
