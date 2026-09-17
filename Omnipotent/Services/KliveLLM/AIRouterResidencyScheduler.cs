namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// What a request is FOR, which is the only thing that justifies it jumping a queue.
    /// Derived from the session id by default; the four sites whose intent a string cannot express
    /// pass it explicitly (see <see cref="AIRouterWorkClassifier"/>).
    /// </summary>
    public enum AIRouterWorkClass
    {
        /// <summary>Klives is sitting there waiting. Never parked.</summary>
        Interactive = 0,
        /// <summary>An autonomous agent turn inside a multi-turn tool session.</summary>
        Agent = 1,
        /// <summary>A council: a short burst of related turns that can partially reuse a prefix.</summary>
        Burst = 2,
        /// <summary>A single discarded query — digest rebuild, triage, summary. Can never hit a cache,
        /// and its prefill EVICTS prefixes that could have.</summary>
        OneShot = 3,
    }

    /// <summary>How much of the scheduler is actually allowed to affect dispatch.</summary>
    public enum AIRouterSchedulerMode
    {
        /// <summary>Ordering by arrival, no residency, no parking, no slot reservations. Every
        /// cache-aware mechanism is off, leaving the published rate envelope and the provider
        /// cool-off — behaviourally the gate that existed before any of this.</summary>
        Fifo,
        /// <summary>Compute every decision and record what it WOULD have done, then dispatch FIFO.
        /// Lets the policy be validated against production traffic before it can affect anything.</summary>
        Observe,
        /// <summary>The scheduler decides.</summary>
        Enforce,
    }

    /// <summary>Which acquisition this is within one logical turn. A retry is not progress, so it must
    /// not consume the turn allowance that keeps a tool loop holding its slot.</summary>
    internal enum AIRouterSlotIntent
    {
        Turn,
        Retry,
        StreamFallback,
    }

    /// <summary>Everything the scheduler needs to know about one request.</summary>
    internal readonly record struct AIRouterWorkTicket(
        string PrefixKey,
        AIRouterWorkClass Class,
        AIRouterSlotIntent Intent)
    {
        internal static AIRouterWorkTicket Anonymous(string reason)
            => new($"anon:{reason}:{Guid.NewGuid():N}", AIRouterWorkClass.OneShot, AIRouterSlotIntent.Turn);
    }

    /// <summary>
    /// Falls back to inferring a work class from the session id.
    ///
    /// This is deliberately the FALLBACK and not the mechanism. Inferring from a string is
    /// order-sensitive and it misfires on real session ids in both directions: a
    /// kliveagent-{conversationId} can itself end in a GUID, and stratum-engineer-summary-{runID} is a
    /// single discarded query while stratum-engineer-{projectID}-{runID} beside it is a real
    /// multi-turn session. No ordering of the arms satisfies both, so the sites that know pass their
    /// class explicitly and this exists only so an unknown or future subsystem still gets something
    /// sane rather than being silently granted a residency it can never use.
    /// </summary>
    internal static class AIRouterWorkClassifier
    {
        internal static AIRouterWorkClass Classify(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return AIRouterWorkClass.OneShot;
            const StringComparison ord = StringComparison.Ordinal;

            if (sessionId.StartsWith("kliveagent-", ord)) return AIRouterWorkClass.Interactive;
            if (sessionId.StartsWith("projects-council-", ord)) return AIRouterWorkClass.Burst;
            // Ahead of the general stratum- arm: a summary is one query, then the session is dropped.
            if (sessionId.StartsWith("stratum-engineer-summary-", ord)) return AIRouterWorkClass.OneShot;
            if (sessionId.StartsWith("stratum-", ord)) return AIRouterWorkClass.Agent;
            if (sessionId.StartsWith("projects-commander-", ord)) return AIRouterWorkClass.Agent;
            if (sessionId.StartsWith("projects-agent-", ord)) return AIRouterWorkClass.Agent;
            // projects-{operation}-{Guid:N}: the utility routes, which are all one-shot.
            if (EndsWithGuidN(sessionId)) return AIRouterWorkClass.OneShot;
            return AIRouterWorkClass.Agent;
        }

        private static bool EndsWithGuidN(string sessionId)
        {
            int dash = sessionId.LastIndexOf('-');
            if (dash < 0 || sessionId.Length - dash - 1 != 32) return false;
            for (int i = dash + 1; i < sessionId.Length; i++)
            {
                char c = sessionId[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Decides WHICH conversations get to stay cache-warm, given that only a few can.
    ///
    /// The arithmetic is not negotiable: with C parallel slots, a mean service time S and an effective
    /// cache lifetime T_eff, the number of conversations whose consecutive turns can stay inside
    /// T_eff is K = C * T_eff / S. Past that it is physically impossible to keep everything warm, and
    /// the old gate's answer — let everyone degrade equally — is the worst available allocation
    /// because it maximises total re-prefill. This class instead keeps a bounded set warm and parks
    /// the rest, which completes MORE work in total because a warm turn is materially cheaper to
    /// serve than a cold one.
    ///
    /// A residency lasts exactly as long as the prefix it protects could still be alive, which is what
    /// makes a 99% efficiency target reachable rather than merely aspirational. The turns inside one
    /// wake are seconds apart and are the ones with a prefix worth reusing; consecutive wakes are
    /// already separated by idle gaps (14 min keepalive, 20 min worker heartbeat) that exceed
    /// KliveLLM.BriefSessionIdleLimit and therefore cold-start whatever anyone does. So a conversation
    /// is either dispatched while genuinely warm or has nothing left to lose, and the expensive middle
    /// case — a continuation sent to a prefix the provider already dropped — is designed out rather
    /// than tuned against.
    ///
    /// NOT thread-safe by design. The limiter owns the one lock that covers the window, the slots and
    /// this; giving it a second lock would buy nothing and risk an ordering bug.
    /// </summary>
    internal sealed class AIRouterResidencyScheduler
    {
        /// <summary>
        /// The longest a COLD first turn may be parked before it is admitted regardless.
        ///
        /// It only ever applies to a request with no reusable prefix, because continuations are never
        /// parked at all. That distinction was worth learning the hard way: an earlier version of this
        /// tried to park continuations past KliveLLM.BriefSessionIdleLimit on the theory that the
        /// client would rotate the brief and turn them into honest cold starts. It does not. The epoch
        /// is baked into the prefix key at ASSEMBLY time, long before the request reaches this queue,
        /// so a parked continuation stays a continuation — it simply misses, and waiting longer only
        /// makes it miss later. Simulation put that at 37% efficiency against a 99% target.
        ///
        /// So the ceiling is a backstop, not a routine trigger, and it is deliberately long. Ordinary
        /// turnover is what admits parked work: cold candidates are ordered by arrival, so the
        /// longest-waiting one takes the next residency a finished wake releases, and nothing can be
        /// overtaken indefinitely. Forcing admission sooner than that is actively harmful — a cohort
        /// of three cannot absorb fifteen forced entrants, and simulation shows the attempt collapsing
        /// the measured lifetime and taking efficiency down with it.
        ///
        /// A wait this long is not the scheduler being unfair; it is what three slots against a fleet
        /// several times their capacity actually means. The capacity report says so in as many words
        /// rather than hiding it in latency. Safe only because ProjectWatchdog is taught to read a
        /// parked request as waiting rather than wedged.
        /// </summary>
        internal static readonly TimeSpan HardParkCeiling = TimeSpan.FromMinutes(45);

        /// <summary>
        /// At most one cold prefill in flight across the whole fleet.
        ///
        /// The single most important constraint in this class. Without it a cohort rotation admits K
        /// cold prefills at once; they all miss, the measured service time spikes, K recomputes
        /// downward, and the congestion collapse this scheduler exists to prevent reruns inside the
        /// scheduler itself.
        /// </summary>
        internal const int MaxConcurrentColdAdmissions = 1;

        /// <summary>One council must not be able to evict the entire agent cohort.</summary>
        private const int MaxBurstResidents = 2;

        /// <summary>EDF feasibility margin. Service times here are non-preemptible and heavy-tailed, so
        /// the cohort is sized below the theoretical bound rather than at it.</summary>
        internal const double DefaultAlpha = 0.70;

        private const int MaxTrackedPrefixes = 512;

        private sealed class Resident
        {
            internal string PrefixKey = "";
            internal AIRouterWorkClass Class;
            internal DateTime AdmittedAt;
            internal DateTime LastDispatchAt;
            internal DateTime LastReleaseAt;   // diagnostics only: expiry is driven by LastDispatchAt
            internal int InFlight;
            internal int Dispatches;
        }

        private readonly Dictionary<string, Resident> residents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> lastDispatch = new(StringComparer.Ordinal);
        private readonly HashSet<string> pending = new(StringComparer.Ordinal);

        private double alpha = DefaultAlpha;
        private int currentK = 1;
        private bool seeded;
        private int raiseVotes;
        private DateTime raisedAtUtc = DateTime.MinValue;

        private long forcedAdmissions;
        private long coldAdmissions;
        private long parkedDispatches;
        private long evictions;

        internal int K => currentK;
        internal int ResidentCount => residents.Count;
        internal int TrackedPrefixes => lastDispatch.Count;
        internal long ForcedAdmissions => forcedAdmissions;
        internal long ColdAdmissions => coldAdmissions;
        internal long ParkedDispatches => parkedDispatches;
        internal long Evictions => evictions;

        internal void SetAlpha(double value) => alpha = Math.Clamp(value, 0.05d, 1.0d);

        internal bool IsResident(string prefixKey) => residents.ContainsKey(prefixKey);

        /// <summary>The gap since this prefix was last sent, or null if we have never sent it — which
        /// is the difference between "a continuation that may have wasted a warm prefix" and "an
        /// honest cold start", and therefore the difference between counting against the efficiency
        /// target and being excluded from it.</summary>
        internal TimeSpan? GapSinceLastDispatch(string prefixKey, DateTime now)
            => lastDispatch.TryGetValue(prefixKey, out DateTime at) && now > at ? now - at : null;

        /// <summary>Expire finished wakes, drop prefixes the provider must have evicted, and resize the
        /// cohort. Called under the limiter's lock before every admission decision.</summary>
        internal void Maintain(DateTime now, TimeSpan effectiveLifetime, TimeSpan warmServiceTime,
            double nonResidentShare, int parallelSlots, IEnumerable<string>? pendingKeys = null)
        {
            pending.Clear();
            if (pendingKeys != null) foreach (string key in pendingKeys) pending.Add(key);

            RecomputeK(now, effectiveLifetime, warmServiceTime, nonResidentShare, parallelSlots);

            // A residency lasts exactly as long as the prefix it protects could still be alive.
            //
            // Releasing it any sooner is the subtle way to lose the efficiency target: our client goes
            // on treating the session as continuable for KliveLLM.BriefSessionIdleLimit, so a
            // conversation that returned from a long tool call after losing its residency would be
            // parked, and its already-assembled CONTINUATION would eventually be dispatched onto a
            // prefix the provider had dropped. Holding residency for the full lifetime means a
            // conversation is either dispatched while genuinely warm or has nothing left to lose.
            List<string>? drop = null;
            foreach (var kv in residents)
            {
                Resident r = kv.Value;
                if (r.InFlight > 0 || pending.Contains(kv.Key)) continue;
                if (now - r.LastDispatchAt <= effectiveLifetime) continue;
                (drop ??= new List<string>()).Add(kv.Key);
            }
            if (drop != null)
                foreach (string key in drop) { residents.Remove(key); evictions++; }

            // Shrinking the cohort never touches a request in flight, and among the rest it takes the
            // one closest to losing its cache anyway — the cheapest possible thing to give up. If every
            // resident is mid-request the cohort sits above target until one finishes: paying a
            // reusable miss to enforce a number a moment sooner is the trade this exists to refuse.
            while (residents.Count > currentK)
            {
                Resident? victim = null;
                foreach (Resident r in residents.Values)
                {
                    if (r.InFlight > 0 || pending.Contains(r.PrefixKey)) continue;
                    if (victim == null || r.Class > victim.Class
                        || (r.Class == victim.Class && r.LastDispatchAt < victim.LastDispatchAt))
                        victim = r;
                }
                if (victim == null) break;
                residents.Remove(victim.PrefixKey);
                evictions++;
            }
        }

        private void RecomputeK(DateTime now, TimeSpan effectiveLifetime, TimeSpan warmServiceTime,
            double nonResidentShare, int parallelSlots)
        {
            double service = Math.Max(1d, warmServiceTime.TotalSeconds);
            double usable = Math.Clamp(1d - nonResidentShare, 0.1d, 1d);
            int derived = (int)Math.Floor(alpha * usable * parallelSlots * effectiveLifetime.TotalSeconds / service);
            derived = Math.Clamp(derived, 1, 32);

            // The first estimate is not a flap, it is the starting point. Growing into it one step at a
            // time would park the whole fleet for no reason every time the process restarts.
            if (!seeded)
            {
                seeded = true;
                currentK = derived;
                return;
            }

            if (derived <= currentK)
            {
                // Down is the safe direction and takes effect at once.
                currentK = derived;
                raiseVotes = 0;
                return;
            }

            // Up is gated three ways. A flapping K churns the cohort, and a churning cohort misses on
            // everything — the failure mode would look exactly like the one being fixed.
            if (++raiseVotes < 3) return;
            if (now - raisedAtUtc < effectiveLifetime) return;
            currentK++;
            raisedAtUtc = now;
            raiseVotes = 0;
        }

        /// <summary>Ordering key. One number does three jobs — sort order, park bound and residency
        /// expiry — which is why this stays small enough to reason about.</summary>
        internal DateTime Deadline(in AIRouterWorkTicket info, DateTime enqueuedAt, TimeSpan effectiveLifetime)
            => residents.TryGetValue(info.PrefixKey, out Resident? r)
                ? r.LastDispatchAt + effectiveLifetime
                : enqueuedAt + HardParkCeiling;

        /// <summary>Coarse priority band. Interactive always first; one-shots always last because they
        /// cannot hit a cache and their prefill evicts prefixes that could.</summary>
        internal static int Band(AIRouterWorkClass cls) => cls switch
        {
            AIRouterWorkClass.Interactive => 0,
            AIRouterWorkClass.OneShot => 2,
            _ => 1,
        };

        /// <summary>
        /// May this request be dispatched right now, given the cohort? Assumes the caller has already
        /// confirmed a free slot and that the rate/token windows allow it.
        /// </summary>
        internal bool MayDispatch(in AIRouterWorkTicket info, DateTime now, bool pastDeadline,
            int coldInFlight, int oneShotInFlight, TimeSpan warmServiceTime, out string reason)
        {
            reason = "";

            // A human is waiting and the volume is negligible. Its slot-seconds still show up in the
            // measured non-resident share, so the cohort shrinks to accommodate it rather than being
            // destabilised by it.
            if (info.Class == AIRouterWorkClass.Interactive) return true;

            // The invariant itself: a resident is always dispatched, which is what keeps its
            // consecutive turns inside the measured lifetime.
            if (residents.ContainsKey(info.PrefixKey)) return true;

            // A CONTINUATION is never parked, even if its residency has lapsed.
            //
            // This is the rule that actually delivers the efficiency target, and it is worth being
            // precise about why, because the obvious alternative is wrong. A request is assembled
            // before it ever reaches this queue, so by the time it is here its prompt is already
            // either a continuation or a fresh brief — parking cannot change which. Parking a
            // continuation past the cache lifetime therefore guarantees a wasted warm prefix, and
            // waiting even longer does NOT launder it into an honest cold start: the epoch is baked
            // into the key, so it stays a continuation that missed. The only admission control that
            // costs nothing is at the WAKE BOUNDARY, where the prompt is cold anyway.
            if (GapSinceLastDispatch(info.PrefixKey, now) != null) return true;

            // Anti-starvation. Nothing waits forever, whatever the cohort looks like. This is only
            // ever reached by a cold first turn, which is why it is affordable.
            if (pastDeadline) return true;

            if (coldInFlight >= MaxConcurrentColdAdmissions)
            {
                reason = "cold-stagger";
                return false;
            }

            if (info.Class == AIRouterWorkClass.OneShot)
            {
                if (oneShotInFlight >= 1) { reason = "oneshot-serialised"; return false; }
                // Only genuine slack. A one-shot that lands just before a resident's prefix expires
                // costs that resident its cache, and the one-shot could never have used one.
                foreach (Resident r in residents.Values)
                    if (r.InFlight == 0 && r.LastDispatchAt + warmServiceTime >= now)
                    {
                        reason = "oneshot-yields-to-warm";
                        return false;
                    }
                return true;
            }

            if (info.Class == AIRouterWorkClass.Burst)
            {
                int bursts = 0;
                foreach (Resident r in residents.Values)
                    if (r.Class == AIRouterWorkClass.Burst) bursts++;
                if (bursts >= MaxBurstResidents) { reason = "burst-cap"; return false; }
            }

            if (residents.Count >= currentK) { reason = "cohort-full"; return false; }
            return true;
        }

        /// <summary>Book a dispatch: refresh the prefix clock and, when there is cohort room, grant or
        /// renew residency.</summary>
        internal bool OnDispatch(in AIRouterWorkTicket info, DateTime now, bool pastDeadline)
        {
            bool wasResident = residents.TryGetValue(info.PrefixKey, out Resident? r);
            if (!wasResident && !lastDispatch.ContainsKey(info.PrefixKey)) { /* first ever: a true cold start */ }

            if (pastDeadline) forcedAdmissions++;
            if (!wasResident) coldAdmissions++;

            TrackDispatch(info.PrefixKey, now);

            if (wasResident)
            {
                r!.LastDispatchAt = now;
                r.InFlight++;
                r.Dispatches++;
                return true;
            }

            // One-shots never take cohort space: they cannot reuse a prefix, so a residency would only
            // displace something that can.
            if (info.Class == AIRouterWorkClass.OneShot) return false;

            // Admission and residency are the SAME decision, and this is the single most important
            // line in the class after the cold stagger.
            //
            // A cold prefill is an investment: it only pays off if this conversation gets to keep
            // using the prefix it just paid to build. Dispatching a cold turn WITHOUT granting
            // residency is the worst of both worlds — we pay for the prefill, and then its follow-up
            // turns are parked until that same prefix is dead, which manufactures exactly the wasted
            // warm prefixes the whole design exists to prevent. So a dispatch that got this far takes
            // a residency, making room for itself if the cohort is full. MayDispatch is what keeps
            // that rare: it refuses cold newcomers while the cohort is full, so the only way here is a
            // free seat or a request that has waited out its park ceiling.
            if (residents.Count >= currentK) EvictCheapest(now);

            residents[info.PrefixKey] = new Resident
            {
                PrefixKey = info.PrefixKey,
                Class = info.Class,
                AdmittedAt = now,
                LastDispatchAt = now,
                LastReleaseAt = now,
                InFlight = 1,
                Dispatches = 1,
            };
            return true;
        }

        /// <summary>Give up whichever residency is worth least: never one mid-request, and among the
        /// rest the one whose cache was closest to dying anyway.</summary>
        private void EvictCheapest(DateTime now)
        {
            Resident? victim = null;
            foreach (Resident r in residents.Values)
            {
                // Never take a residency away from a conversation that is mid-wake — in flight, or
                // with its next turn already queued. Evicting one of those parks work that was about
                // to run warm, which is the exact waste this is all built to avoid.
                if (r.InFlight > 0 || pending.Contains(r.PrefixKey)) continue;
                if (victim == null || r.Class > victim.Class
                    || (r.Class == victim.Class && r.LastDispatchAt < victim.LastDispatchAt))
                    victim = r;
            }
            // Everyone is mid-wake: sit above target rather than evict one of them.
            // Maintain trims the cohort as soon as one of them finishes.
            if (victim == null) return;
            residents.Remove(victim.PrefixKey);
            evictions++;
        }

        internal void OnRelease(string prefixKey, DateTime now)
        {
            if (!residents.TryGetValue(prefixKey, out Resident? r)) return;
            if (r.InFlight > 0) r.InFlight--;
            r.LastReleaseAt = now;
        }

        internal void NotePark(string reason) { if (reason.Length > 0) parkedDispatches++; }

        private void TrackDispatch(string prefixKey, DateTime now)
        {
            lastDispatch[prefixKey] = now;
            if (lastDispatch.Count <= MaxTrackedPrefixes) return;
            // Bounded memory: the oldest entries can only ever produce "cold" verdicts anyway.
            string? oldest = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var kv in lastDispatch)
                if (kv.Value < oldestAt) { oldestAt = kv.Value; oldest = kv.Key; }
            if (oldest != null) lastDispatch.Remove(oldest);
        }
    }
}
