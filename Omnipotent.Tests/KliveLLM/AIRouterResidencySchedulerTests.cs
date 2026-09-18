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

        // ── What the meter is allowed to call a hit ───────────────────────────────────────────

        /// <summary>
        /// The blind spot that let the scheduler switch itself off on 2026-09-16.
        ///
        /// Every agent prompt opens with the same system block and tool schemas. That block is
        /// referenced by every project on the box, so it is never what LRU evicts, and it is 27K of a
        /// 50K commander prompt. A continuation whose own conversation had been dropped therefore came
        /// back 54% cached on the preamble alone — over a 50%-of-the-whole-prompt bar, so it scored as
        /// a hit. No band could ever fail, the lifetime climbed to its ceiling, the warm cohort was
        /// sized to the entire fleet, nothing was parked, and the gate degraded to FIFO while
        /// reporting perfect health.
        /// </summary>
        [Fact]
        public void SurvivingSharedPreamble_IsNotAHit()
        {
            var meter = new PrefixSurvivalMeter();
            const string key = "projects-commander-p1#e1|qwen";
            TimeSpan lifetimeBefore = meter.EffectiveLifetime();

            // Enough evidence to schedule on, every sample a continuation that got the preamble back
            // and nothing else.
            for (int i = 0; i < 200; i++)
                meter.RecordOutcome(key, TimeSpan.FromSeconds(300), promptTokens: 50_000,
                    cachedTokens: 27_200, wasResident: true, outsideCohort: false,
                    slotOccupancy: TimeSpan.FromSeconds(60), nowUtc: T0.AddSeconds(i * 300));

            var snapshot = meter.Describe();
            Assert.True(snapshot.ReusableSamples > 0, "these are continuations and must be judged as such");
            Assert.Equal(0, snapshot.ReusableHits);
            Assert.True(meter.EffectiveLifetime() < lifetimeBefore,
                "a prefix that is never actually served back must pull the estimated lifetime down");
        }

        /// <summary>The other direction: a real continuation that gets its own conversation back is a
        /// hit, and the estimate must not be so strict that honest reuse cannot clear it.</summary>
        [Fact]
        public void ReusedConversationPrefix_IsAHit()
        {
            var meter = new PrefixSurvivalMeter();
            const string key = "projects-commander-p2#e1|qwen";

            for (int i = 0; i < 200; i++)
                meter.RecordOutcome(key, TimeSpan.FromSeconds(20), promptTokens: 50_000 + i,
                    cachedTokens: 49_900, wasResident: true, outsideCohort: false,
                    slotOccupancy: TimeSpan.FromSeconds(30), nowUtc: T0.AddSeconds(i * 20));

            var snapshot = meter.Describe();
            Assert.True(snapshot.ReusableEfficiency > 0.99, $"efficiency was {snapshot.ReusableEfficiency:P1}");
            Assert.True(meter.EffectiveLifetime() >= PrefixSurvivalMeter.MinLifetime);
        }

        // ── The wake-boundary budget this layer publishes but cannot enforce ──────────────────

        /// <summary>
        /// Nothing may act on the budget until it rests on evidence. A fresh process reads 0, and the
        /// consumer is required to fall back to its own ramp rather than treat that as "no limit".
        /// </summary>
        [Fact]
        public void ConversationBudget_IsZeroUntilTheCurveHasEvidence()
        {
            var limiter = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, new PrefixSurvivalMeter());
            Assert.Equal(0, limiter.ConversationBudget());
        }

        /// <summary>
        /// The whole point of costing the budget at the BLENDED service time rather than the warm one.
        ///
        /// A cached turn and a re-prefilled turn differ by more than an order of magnitude in slot
        /// occupancy, which is what makes the congestion collapse bistable: once enough conversations
        /// go cold, throughput drops far enough that the rest cannot get a turn inside the cache
        /// lifetime either. Sizing the live fleet off the WARM service time in that state would keep
        /// admitting conversations into a fleet that can no longer serve the ones it has. The budget
        /// therefore has to fall as the fleet goes cold — that is what lets it re-warm.
        /// </summary>
        [Fact]
        public void ConversationBudget_ShrinksWhenTheFleetGoesCold()
        {
            var warm = new PrefixSurvivalMeter();
            for (int i = 0; i < 200; i++)
                warm.RecordOutcome($"projects-commander-warm{i % 4}#e1|qwen", TimeSpan.FromSeconds(20),
                    promptTokens: 50_000 + i, cachedTokens: 49_900, wasResident: true, outsideCohort: false,
                    slotOccupancy: TimeSpan.FromSeconds(15), nowUtc: T0.AddSeconds(i * 20));
            int warmBudget = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, warm).ConversationBudget();

            var cold = new PrefixSurvivalMeter();
            for (int i = 0; i < 200; i++)
                cold.RecordOutcome($"projects-commander-cold{i % 4}#e1|qwen", TimeSpan.FromSeconds(400),
                    promptTokens: 50_000 + i, cachedTokens: 27_200, wasResident: true, outsideCohort: false,
                    slotOccupancy: TimeSpan.FromSeconds(120), nowUtc: T0.AddSeconds(i * 400));
            int coldBudget = new AIRouterFairUseLimiter(3, 240, 10_000_000, null, null, cold).ConversationBudget();

            Assert.True(warmBudget > 0 && coldBudget > 0, "the budget is a positive number of conversations");
            Assert.True(coldBudget < warmBudget,
                $"a collapsed fleet must be allowed FEWER live conversations, not the same number " +
                $"(warm {warmBudget}, cold {coldBudget})");
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
                meter.RecordOutcome("projects-commander-solo#e1|qwen", TimeSpan.FromSeconds(20), 50_000, 49_000,
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
        public async Task Fifo_TurnsOffSlotReservationsAndNotJustOrdering()
        {
            // The mode dropdown has to mean what it says. Ordering and parking were already gated on
            // it, but the slot reservation was not — and that is a cache-aware mechanism too: it idles
            // a just-freed slot for the conversation that released it, which is exactly the queue
            // jumping Fifo exists to switch off. Left ungated, "Fifo" was the smart queue with its
            // ordering removed, so anyone reaching for the plain gate under load did not get one.
            static async Task<long> NewcomerWaitMsAsync(AIRouterSchedulerMode mode)
            {
                var clock = new VirtualClock();
                var limiter = new AIRouterFairUseLimiter(2, 240, 10_000_000, clock.Now, clock.DelayAsync,
                    new PrefixSurvivalMeter());
                limiter.SetMode(mode);

                // Two slots, both taken, then one released: the released slot is the contested one.
                var releasing = await limiter.AcquireAsync(1_000, Ticket("toolloop#e1|qwen"));
                var holding = await limiter.AcquireAsync(1_000, Ticket("filler#e1|qwen"));
                releasing.Dispose();

                var newcomer = await limiter.AcquireAsync(1_000, Ticket("newcomer#e1|qwen"))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                newcomer.Dispose();
                holding.Dispose();
                return newcomer.QueueDurationMs;
            }

            Assert.Equal(0, await NewcomerWaitMsAsync(AIRouterSchedulerMode.Fifo));
            // Observe's contract is that it decides without any of it reaching dispatch, so a
            // reservation is not its to grant either.
            Assert.Equal(0, await NewcomerWaitMsAsync(AIRouterSchedulerMode.Observe));
            // And the mechanism is still there when it was asked for.
            Assert.True(await NewcomerWaitMsAsync(AIRouterSchedulerMode.Enforce) > 0,
                "Enforce must still hold a freed slot for the conversation that released it");
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

        /// <summary>Advances instantly instead of sleeping, so a reservation window is tested in
        /// milliseconds rather than in the seconds it actually spans.</summary>
        private sealed class VirtualClock
        {
            public DateTime UtcNow { get; private set; } = T0;

            public Func<DateTime> Now => () => UtcNow;

            public Task DelayAsync(TimeSpan span, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (span > TimeSpan.Zero) UtcNow += span;
                return Task.CompletedTask;
            }
        }
    }
}
