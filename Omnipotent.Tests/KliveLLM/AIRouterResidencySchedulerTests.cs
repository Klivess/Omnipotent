using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// The scheduler's job is not "be fair" — it is "never waste a prefix that was still warm", which
    /// is what a 99% prefix-efficiency target actually asks for. These pin the decisions that claim
    /// costs money when they are wrong: what counts as one conversation, how long a prefix is assumed
    /// to live, how many conversations may be kept warm at once, and what happens to everything else.
    /// </summary>
    public class AIRouterResidencySchedulerTests
    {
        private static readonly DateTime T0 = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        private static AIRouterWorkTicket Ticket(string key, AIRouterWorkClass cls = AIRouterWorkClass.Agent)
            => new(key, cls, AIRouterSlotIntent.Turn);

        // ── Identity ───────────────────────────────────────────────────────────────────────────

        [Theory]
        // Klives is waiting on these, and a conversation id can itself end in a GUID — so a
        // GUID-suffix rule placed any earlier would silently demote real interactive chat.
        [InlineData("kliveagent-7f1c2a3b4d5e6f708192a3b4c5d6e7f8", AIRouterWorkClass.Interactive)]
        [InlineData("kliveagent-42", AIRouterWorkClass.Interactive)]
        [InlineData("projects-commander-proj1", AIRouterWorkClass.Agent)]
        [InlineData("projects-agent-proj1-worker7", AIRouterWorkClass.Agent)]
        [InlineData("projects-council-c1-chair", AIRouterWorkClass.Burst)]
        // The trap that motivated making the string rule a FALLBACK: these two sit in the same
        // id-space, one is a real multi-turn session and the other is a single discarded query.
        [InlineData("stratum-engineer-proj1-run9", AIRouterWorkClass.Agent)]
        [InlineData("stratum-engineer-summary-run9", AIRouterWorkClass.OneShot)]
        [InlineData("projects-digest-rebuild-7f1c2a3b4d5e6f708192a3b4c5d6e7f8", AIRouterWorkClass.OneShot)]
        [InlineData(null, AIRouterWorkClass.OneShot)]
        [InlineData("", AIRouterWorkClass.OneShot)]
        public void Classification_PinsTheKnownSessionIdShapes(string? sessionId, AIRouterWorkClass expected)
            => Assert.Equal(expected, AIRouterWorkClassifier.Classify(sessionId));

        [Fact]
        public void ADifferentCacheEpochIsADifferentConversation()
        {
            // A compaction or brief rotation mints a new epoch. The prefix it produces shares nothing
            // with the old one, so residency must NOT carry over — otherwise the scheduler keeps
            // believing a prefix that no longer exists is still warm and stops protecting anything.
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30), 0.2, 3);

            scheduler.OnDispatch(Ticket("projects-commander-p1#epochA|qwen"), T0, false);
            Assert.True(scheduler.IsResident("projects-commander-p1#epochA|qwen"));
            Assert.False(scheduler.IsResident("projects-commander-p1#epochB|qwen"));
            Assert.Null(scheduler.GapSinceLastDispatch("projects-commander-p1#epochB|qwen", T0));
        }

        [Fact]
        public void ADifferentModelIsADifferentCache()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30), 0.2, 3);
            scheduler.OnDispatch(Ticket("s#e|qwen"), T0, false);
            Assert.Null(scheduler.GapSinceLastDispatch("s#e|deepseek", T0));
        }

        // ── Cohort sizing ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void WarmCohortShrinksImmediatelyWhenTurnsGetSlower()
        {
            var scheduler = new AIRouterResidencyScheduler();
            // 3 slots x 480s lifetime / 40s turns, times the 0.7 margin and a 0.8 usable share = 20.
            scheduler.Maintain(T0, TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(40), 0.2, 3);
            int roomy = scheduler.K;
            Assert.True(roomy > 4, $"expected a workable cohort at fast turns, got {roomy}");

            // Streaming turns holding a slot for three minutes: the same lifetime now supports far less.
            scheduler.Maintain(T0, TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(180), 0.2, 3);
            Assert.True(scheduler.K < roomy);

            // The regime that actually matters: one fully-staffed project is 13 conversations, and the
            // arithmetic says only a handful can be kept warm. Oversubscription is normal, not an edge.
            scheduler.Maintain(T0, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180), 0.2, 3);
            Assert.True(scheduler.K <= 2, $"expected a tiny cohort under slow cold turns, got {scheduler.K}");
            Assert.True(scheduler.K >= 1, "the cohort may never reach zero or nothing would ever run");
        }

        [Fact]
        public void WarmCohortGrowsOnlySlowlyAndOnlyOnRepeatedEvidence()
        {
            // A flapping cohort churns residencies, and a churning cohort misses on everything — the
            // failure would look exactly like the one being fixed. So growth is gated three ways.
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180), 0.2, 3);
            int start = scheduler.K;

            var lifetime = TimeSpan.FromSeconds(900);
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.Equal(start, scheduler.K); // one vote is not evidence
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.Equal(start, scheduler.K); // nor two

            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.Equal(start + 1, scheduler.K); // and then by exactly one

            // Further growth has to wait out a full lifetime, however loudly the maths votes.
            for (int i = 0; i < 10; i++) scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.Equal(start + 1, scheduler.K);

            for (int i = 0; i < 3; i++)
                scheduler.Maintain(T0 + lifetime + TimeSpan.FromSeconds(1), lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.Equal(start + 2, scheduler.K);
        }

        // ── Residency and rotation ─────────────────────────────────────────────────────────────

        [Fact]
        public void OnlyTheCohortGetsResidency_TheRestArePark()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180), 0.2, 3);
            int k = scheduler.K;

            for (int i = 0; i < k; i++)
            {
                var t = Ticket($"s{i}#e|m");
                Assert.True(scheduler.MayDispatch(t, T0, false, 0, 0, TimeSpan.FromSeconds(30), out _));
                scheduler.OnDispatch(t, T0, false);
                scheduler.OnRelease(t.PrefixKey, T0);
            }
            Assert.Equal(k, scheduler.ResidentCount);

            var overflow = Ticket("overflow#e|m");
            Assert.False(scheduler.MayDispatch(overflow, T0, false, 0, 0, TimeSpan.FromSeconds(30), out string reason));
            Assert.Equal("cohort-full", reason);
        }

        [Fact]
        public void AResidentIsAlwaysDispatched_ThatIsTheWholeInvariant()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180), 0.2, 3);

            var mine = Ticket("resident#e|m");
            scheduler.OnDispatch(mine, T0, false);
            scheduler.OnRelease(mine.PrefixKey, T0);

            // Even with the cold-admission stagger fully occupied, a resident's next turn goes now:
            // delaying it past the lifetime is precisely the waste the whole design exists to avoid.
            Assert.True(scheduler.MayDispatch(mine, T0.AddSeconds(5), false, coldInFlight: 5, oneShotInFlight: 3,
                TimeSpan.FromSeconds(30), out _));
        }

        [Fact]
        public void ShrinkingTheCohortNeverEvictsARequestInFlight()
        {
            // A request in flight has, by definition, a live prefix and a caller waiting on it. Throwing
            // it out of the cohort to hit a number a moment sooner is the trade this refuses to make:
            // the cohort sits above target until something finishes instead.
            var scheduler = new AIRouterResidencyScheduler();
            var lifetime = TimeSpan.FromSeconds(480);
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);

            var a = Ticket("busy-a#e|m");
            var b = Ticket("busy-b#e|m");
            scheduler.OnDispatch(a, T0, false);   // both still generating
            scheduler.OnDispatch(b, T0, false);
            Assert.Equal(2, scheduler.ResidentCount);

            scheduler.Maintain(T0.AddSeconds(10), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 0.2, 3);
            Assert.Equal(1, scheduler.K);
            Assert.Equal(2, scheduler.ResidentCount);
            Assert.True(scheduler.IsResident(a.PrefixKey));
            Assert.True(scheduler.IsResident(b.PrefixKey));
        }

        [Fact]
        public void ShrinkingTheCohortGivesUpWhicheverCacheWasDyingSoonest()
        {
            var scheduler = new AIRouterResidencyScheduler();
            var lifetime = TimeSpan.FromSeconds(480);
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);

            var old = Ticket("dispatched-long-ago#e|m");
            var fresh = Ticket("dispatched-just-now#e|m");
            scheduler.OnDispatch(old, T0, false);
            scheduler.OnRelease(old.PrefixKey, T0);
            scheduler.OnDispatch(fresh, T0.AddSeconds(200), false);
            scheduler.OnRelease(fresh.PrefixKey, T0.AddSeconds(200));

            scheduler.Maintain(T0.AddSeconds(210), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 0.2, 3);
            Assert.Equal(1, scheduler.K);
            // The one closest to losing its cache anyway is the cheapest thing to give up.
            Assert.False(scheduler.IsResident(old.PrefixKey));
            Assert.True(scheduler.IsResident(fresh.PrefixKey));
        }

        [Fact]
        public void AResidencyLastsExactlyAsLongAsThePrefixCould()
        {
            var scheduler = new AIRouterResidencyScheduler();
            var lifetime = TimeSpan.FromSeconds(480);
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);

            var done = Ticket("finished#e|m");
            scheduler.OnDispatch(done, T0, false);
            scheduler.OnRelease(done.PrefixKey, T0);
            Assert.True(scheduler.IsResident(done.PrefixKey));

            // Held for the whole lifetime, not a shorter grace: while the prefix could still be alive
            // our client would still treat the session as continuable, and releasing early is what
            // would later strand an assembled continuation on a prefix the provider had dropped.
            scheduler.Maintain(T0 + lifetime - TimeSpan.FromSeconds(1), lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.True(scheduler.IsResident(done.PrefixKey));

            // Past it there is nothing left to protect, so the cohort space goes to somebody who can
            // use it. Its next wake was always going to cold-start anyway.
            scheduler.Maintain(T0 + lifetime + TimeSpan.FromSeconds(1), lifetime, TimeSpan.FromSeconds(20), 0.2, 3);
            Assert.False(scheduler.IsResident(done.PrefixKey));
        }

        [Fact]
        public void OnlyOneColdPrefillIsAdmittedAtATime()
        {
            // Without this a rotation admits a whole cohort of cold prefills at once; they all miss,
            // measured service time spikes, the cohort recomputes downward, and the collapse reruns
            // inside the scheduler built to prevent it.
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(20), 0.2, 3);

            Assert.True(scheduler.MayDispatch(Ticket("cold-1#e|m"), T0, false, 0, 0, TimeSpan.FromSeconds(30), out _));
            Assert.False(scheduler.MayDispatch(Ticket("cold-2#e|m"), T0, false,
                coldInFlight: AIRouterResidencyScheduler.MaxConcurrentColdAdmissions, 0,
                TimeSpan.FromSeconds(30), out string reason));
            Assert.Equal("cold-stagger", reason);
        }

        // ── Classes ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void InteractiveWorkIsNeverParked()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 0.2, 3);
            for (int i = 0; i < 8; i++)
            {
                var t = Ticket($"agent{i}#e|m");
                scheduler.OnDispatch(t, T0, false);
            }

            var klives = Ticket("kliveagent-chat#e|m", AIRouterWorkClass.Interactive);
            Assert.True(scheduler.MayDispatch(klives, T0, false, coldInFlight: 4, oneShotInFlight: 2,
                TimeSpan.FromSeconds(30), out _));
            Assert.Equal(0, AIRouterResidencyScheduler.Band(AIRouterWorkClass.Interactive));
        }

        [Fact]
        public void OneShotsYieldToWarmWorkAndNeverTakeCohortSpace()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(20), 0.2, 3);

            var resident = Ticket("commander#e|m");
            scheduler.OnDispatch(resident, T0, false);
            scheduler.OnRelease(resident.PrefixKey, T0);

            // A digest rebuild can never reuse a prefix, and its prefill evicts one that could. While a
            // warm conversation is due, it waits.
            var digest = Ticket("projects-digest-rebuild-abc#e|m", AIRouterWorkClass.OneShot);
            Assert.False(scheduler.MayDispatch(digest, T0.AddSeconds(1), false, 0, 0,
                TimeSpan.FromSeconds(60), out string reason));
            Assert.Equal("oneshot-yields-to-warm", reason);

            // Given genuine slack it runs, but it is never granted residency.
            bool granted = scheduler.OnDispatch(digest, T0.AddSeconds(1), false);
            Assert.False(granted);
            Assert.False(scheduler.IsResident(digest.PrefixKey));
            Assert.Equal(2, AIRouterResidencyScheduler.Band(AIRouterWorkClass.OneShot));
        }

        [Fact]
        public void OneCouncilCannotEvictTheWholeAgentCohort()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(20), 0.2, 3);

            for (int i = 0; i < 3; i++)
            {
                var seat = Ticket($"projects-council-c1-seat{i}#e|m", AIRouterWorkClass.Burst);
                bool may = scheduler.MayDispatch(seat, T0, false, 0, 0, TimeSpan.FromSeconds(30), out string reason);
                if (i < 2) { Assert.True(may); scheduler.OnDispatch(seat, T0, false); }
                else { Assert.False(may); Assert.Equal("burst-cap", reason); }
            }
        }

        // ── Starvation ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void NothingWaitsForeverAndTheParkCeilingClearsTheDeadZone()
        {
            var scheduler = new AIRouterResidencyScheduler();
            scheduler.Maintain(T0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 0.2, 3);
            for (int i = 0; i < 6; i++) scheduler.OnDispatch(Ticket($"hog{i}#e|m"), T0, false);

            var waiting = Ticket("unlucky#e|m");
            Assert.False(scheduler.MayDispatch(waiting, T0, false, 0, 0, TimeSpan.FromSeconds(30), out _));

            DateTime deadline = scheduler.Deadline(waiting, T0, TimeSpan.FromSeconds(60));
            Assert.True(scheduler.MayDispatch(waiting, deadline, true, 0, 0, TimeSpan.FromSeconds(30), out _));

            // The ceiling sits PAST the client-side brief idle limit on purpose. Ending a park inside
            // the dead zone would dispatch a continuation onto a prefix the provider has already
            // evicted — a wasted warm prefix, which is the one thing the efficiency target forbids.
            // Past the limit, the next assembly rotates the brief and it returns as an honest cold
            // start with nothing to waste. Parking longer is better here than parking medium.
            Assert.True(AIRouterResidencyScheduler.HardParkCeiling > Omnipotent.Services.KliveLLM.KliveLLM.BriefSessionIdleLimit,
                "a park that ends inside the dead zone defeats the entire point of the scheduler");
        }

        // ── End to end, through the real limiter ──────────────────────────────────────────────

        [Fact]
        public async Task ASingleConversationNeverWaitsForAnything()
        {
            // The most important test here. Oversubscription is the case this was built for, but the
            // quiet case is the one it must not make worse — if one conversation on three slots ever
            // pays for this machinery, the whole thing is a regression however well it scales.
            var meter = new PrefixSurvivalMeter();
            for (int i = 0; i < 120; i++)
                meter.RecordOutcome(TimeSpan.FromSeconds(20), 50_000, 49_000,
                    wasResident: true, outsideCohort: false, TimeSpan.FromSeconds(30), T0.AddSeconds(i * 30));
            Assert.True(meter.HasEnoughEvidence, "the scheduler must actually be in charge for this to mean anything");

            var limiter = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, meter);
            limiter.SetMode(AIRouterSchedulerMode.Enforce);
            var ticket = Ticket("projects-commander-solo#e1|qwen");

            for (int turn = 0; turn < 40; turn++)
            {
                var lease = await limiter.AcquireAsync(60_000, ticket).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(lease.QueueDurationMs < 250, $"turn {turn} waited {lease.QueueDurationMs}ms");
                lease.Dispose();
            }

            var snapshot = limiter.Describe();
            Assert.Equal(0, snapshot.ParkedAdmissions);
            Assert.Equal(0, snapshot.ForcedAdmissions);
            Assert.Equal(40, snapshot.TotalAdmitted);
        }

        [Fact]
        public async Task SlotReservationsCanNeverOwnEverySlot()
        {
            // Regression. Holding a just-freed slot for the conversation that released it pays for
            // itself in a tool loop, but it is a BET that the caller comes straight back. Taken on
            // every slot at once it stalls everyone else for the full window on every release, and a
            // queue that idles its own scarce capacity waiting for callers who are not coming is worse
            // than no reservation at all. At least one slot always stays open to whoever is waiting.
            var limiter = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, new PrefixSurvivalMeter());
            limiter.SetMode(AIRouterSchedulerMode.Enforce);

            // Distinct conversations, each releasing its slot and never returning.
            for (int i = 0; i < 12; i++)
                (await limiter.AcquireAsync(1_000, Ticket($"drive-by{i}#e1|qwen"))
                    .WaitAsync(TimeSpan.FromSeconds(2))).Dispose();

            // A newcomer must still get in promptly rather than waiting out somebody else's window.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lease = await limiter.AcquireAsync(1_000, Ticket("newcomer#e1|qwen"))
                .WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();
            lease.Dispose();
            Assert.True(stopwatch.ElapsedMilliseconds < 500,
                $"a newcomer waited {stopwatch.ElapsedMilliseconds}ms behind stale slot reservations");
        }

        [Fact]
        public async Task TheSchedulerNeverManufacturesTrafficOfItsOwn()
        {
            // Pins the decision NOT to build cache-refresh pings. Every entry in the fair-use window
            // must correspond to a request a caller actually asked for — if that ever stops being true,
            // the scheduler has started spending the envelope it exists to protect.
            var limiter = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, new PrefixSurvivalMeter());
            limiter.SetMode(AIRouterSchedulerMode.Enforce);

            for (int i = 0; i < 25; i++)
                (await limiter.AcquireAsync(1_000, Ticket($"conversation{i}#e1|qwen"))
                    .WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

            var snapshot = limiter.Describe();
            Assert.Equal(25, snapshot.TotalAdmitted);
            Assert.Equal(25, snapshot.RequestsInWindow);
            Assert.False(AIRouterFairUseLimiter.RefreshPingsEnabled);
        }

        [Fact]
        public void AResidentsDeadlineIsWhenItsCacheDies_NotWhenItArrived()
        {
            var scheduler = new AIRouterResidencyScheduler();
            var lifetime = TimeSpan.FromSeconds(300);
            scheduler.Maintain(T0, lifetime, TimeSpan.FromSeconds(20), 0.2, 3);

            var resident = Ticket("warm#e|m");
            scheduler.OnDispatch(resident, T0, false);
            scheduler.OnRelease(resident.PrefixKey, T0);

            // A resident about to lose its cache outranks a cold newcomer: dispatching it PRESERVES
            // value, while the newcomer has none to preserve. As the newcomer ages its own deadline
            // approaches, which is what eventually promotes it — ageing and de-prioritisation are the
            // same mechanism, so there is no separate anti-starvation rule to keep in sync.
            DateTime residentDeadline = scheduler.Deadline(resident, T0, lifetime);
            DateTime newcomerDeadline = scheduler.Deadline(Ticket("cold#e|m"), T0, lifetime);
            Assert.Equal(T0 + lifetime, residentDeadline);
            Assert.True(residentDeadline < newcomerDeadline);
        }
    }
}
