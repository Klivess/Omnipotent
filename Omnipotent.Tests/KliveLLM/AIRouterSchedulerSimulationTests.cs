using Omnipotent.Services.KliveLLM;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// The failure this scheduler exists for cannot be caught by a unit test, because nothing in the
    /// app breaks when it happens: the same responses come back, the same tests pass, and the only
    /// symptom is the provider's prefill bill. It reached Klives once as a suspension email.
    ///
    /// So it is reproduced here instead. A discrete-event model of the real traffic shape — wakes of
    /// many turns seconds apart, separated by keepalive-length idle gaps, three parallel slots, a
    /// service time that grows when a prefix misses — run twice over an identical trace, once with
    /// arrival-order dispatch and once with the scheduler. Deterministic: virtual clock, seeded RNG,
    /// no real waiting.
    /// </summary>
    public class AIRouterSchedulerSimulationTests
    {
        private readonly ITestOutputHelper output;

        public AIRouterSchedulerSimulationTests(ITestOutputHelper output) => this.output = output;

        [Fact]
        public void ArrivalOrderCollapses_TheSchedulerDoesNot()
        {
            var fifo = Simulation.Run(SimulationOptions.Default, useScheduler: false);
            var scheduled = Simulation.Run(SimulationOptions.Default, useScheduler: true);

            output.WriteLine($"arrival-order : {fifo}");
            output.WriteLine($"scheduler     : {scheduled}");
            output.WriteLine($"curve         : {scheduled.Curve}");

            // 1. The collapse is real and reproduced. Turns land in the "dead zone" — past what the
            //    provider kept, short of what would have made our own client rebuild the prompt — so
            //    they are dispatched as continuations onto a prefix that is already gone.
            Assert.True(fifo.ReusableEfficiency < 0.60,
                $"expected arrival-order dispatch to waste warm prefixes, got {fifo.ReusableEfficiency:P1}");
            Assert.True(fifo.DeadZoneDispatches > 0, "the collapse should show up as dead-zone dispatches");

            // 2. The scheduler holds the bar it was built for.
            Assert.True(scheduled.ReusableEfficiency >= 0.99,
                $"expected >= 99% reusable-prefix efficiency, got {scheduled.ReusableEfficiency:P2} " +
                $"({scheduled.ReusableHits}/{scheduled.ReusableSamples})");

            // 3. And it is not buying that by doing less work. This is the counter-intuitive result
            //    that justifies parking anything at all: running FEWER conversations at once completes
            //    MORE turns, because a warm turn is materially cheaper to serve than a cold one.
            Assert.True(scheduled.CompletedTurns >= fifo.CompletedTurns,
                $"scheduler completed {scheduled.CompletedTurns} turns vs arrival-order {fifo.CompletedTurns}");
        }

        [Fact]
        public void EveryConversationGetsAFairShareAndTheCohortSettles()
        {
            var result = Simulation.Run(SimulationOptions.Default, useScheduler: true);

            output.WriteLine($"K={result.FinalK}, least-served conversation {result.LeastServedConversation} turns " +
                $"vs mean {result.MeanTurnsPerConversation:0.0}, longest silence {result.LongestSilence:g}");

            // The real anti-starvation property is not "nobody ever waits long" — at three slots against
            // a fleet wanting several times their capacity, long waits are simply what the arithmetic
            // says. It is that the waiting is SHARED: cold turns are admitted in arrival order, so no
            // conversation can be overtaken indefinitely while others cycle.
            Assert.Equal(0, result.StarvedConversations);
            Assert.True(result.LeastServedConversation > result.MeanTurnsPerConversation * 0.5,
                $"least-served conversation got {result.LeastServedConversation} turns against a mean of " +
                $"{result.MeanTurnsPerConversation:0.0} — the queue is not sharing fairly");

            // Silence is bounded by the park ceiling plus the wake it then has to wait out, not by luck.
            Assert.True(result.LongestSilence < AIRouterResidencyScheduler.HardParkCeiling + TimeSpan.FromMinutes(15),
                $"a conversation went quiet for {result.LongestSilence:g}");

            // The cohort must converge rather than oscillate: a flapping cohort churns residencies, and
            // a churning cohort misses on everything.
            Assert.True(result.FinalK >= 1);
            Assert.True(result.CohortChanges < result.CompletedTurns / 4,
                $"cohort size changed {result.CohortChanges} times over {result.CompletedTurns} turns");
        }

        [Fact]
        public void TheSchedulerStaysOutOfTheWayWhenThereIsNothingToSchedule()
        {
            // The common case must not pay for the uncommon one. One conversation on three slots should
            // behave exactly as it did before any of this existed.
            var options = SimulationOptions.Default with { Conversations = 1, Hours = 2 };
            var solo = Simulation.Run(options, useScheduler: true);

            output.WriteLine($"single conversation: {solo}");
            Assert.Equal(0, solo.ParkedDispatches);
            Assert.Equal(0, solo.DeadZoneDispatches);
            Assert.True(solo.ReusableEfficiency >= 0.99);
        }

        // ── The model ──────────────────────────────────────────────────────────────────────────

        private readonly record struct SimulationOptions(
            int Conversations,
            int Slots,
            int TurnsPerWake,
            double Hours,
            TimeSpan ThinkTime,
            TimeSpan MinIdleGap,
            TimeSpan MaxIdleGap,
            TimeSpan BaseService,
            TimeSpan PrefillPenalty,
            TimeSpan ProviderCacheLifetime)
        {
            /// <summary>
            /// Shaped after the real thing: streaming Commander/worker turns that hold a slot for their
            /// whole generation, wakes of a dozen turns, and keepalive/heartbeat gaps that already
            /// exceed the client-side brief idle limit so a new wake legitimately starts cold.
            /// </summary>
            internal static SimulationOptions Default => new(
                Conversations: 18,
                Slots: AIRouterFairUseLimiter.PolicyMaxParallelRequests,
                TurnsPerWake: 12,
                Hours: 8,
                ThinkTime: TimeSpan.FromSeconds(3),
                MinIdleGap: TimeSpan.FromMinutes(14),
                MaxIdleGap: TimeSpan.FromMinutes(20),
                BaseService: TimeSpan.FromSeconds(45),
                PrefillPenalty: TimeSpan.FromSeconds(35),
                ProviderCacheLifetime: TimeSpan.FromSeconds(240));
        }

        private sealed class Result
        {
            internal int CompletedTurns;
            internal int ReusableSamples;
            internal int ReusableHits;
            internal int DeadZoneDispatches;
            internal int ParkedDispatches;
            internal int StarvedConversations;
            internal int CohortChanges;
            internal int FinalK;
            internal TimeSpan LongestSilence;
            internal int LeastServedConversation;
            internal double MeanTurnsPerConversation;
            internal string Curve = "";

            internal double ReusableEfficiency => ReusableSamples > 0 ? (double)ReusableHits / ReusableSamples : 1d;

            public override string ToString()
                => $"{CompletedTurns} turns, efficiency {ReusableEfficiency:P2} " +
                   $"({ReusableHits}/{ReusableSamples}), dead-zone {DeadZoneDispatches}, parked {ParkedDispatches}";
        }

        private sealed class Conversation
        {
            internal string Id = "";
            internal int Epoch;
            internal DateTime? LastDispatchAt;   // within the current epoch only
            internal DateTime ReadyAt;
            internal DateTime LastCompletedAt;
            internal int TurnsLeft;
            internal bool Queued;
            internal bool InFlight;
            internal int Served;
            internal string Key => $"{Id}#e{Epoch}|qwen";
        }

        private sealed class Pending
        {
            internal Conversation Conversation = null!;
            internal DateTime EnqueuedAt;
            internal bool Reusable;   // fixed at ASSEMBLY time, exactly as the real client fixes it
        }

        private sealed class Slot
        {
            internal Conversation? Occupant;
            internal DateTime FreeAt = DateTime.MinValue;
            internal bool WasResident;
        }

        private static class Simulation
        {
            private static readonly DateTime Start = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

            internal static Result Run(SimulationOptions options, bool useScheduler)
            {
                var rng = new Random(20260914);
                var result = new Result();
                var meter = new PrefixSurvivalMeter();
                var scheduler = new AIRouterResidencyScheduler();

                // The provider's radix cache: when each prefix was last written, and therefore whether
                // it is still there. Nothing tells us this in production, which is the whole reason the
                // real meter has to infer it.
                var providerCache = new Dictionary<string, DateTime>(StringComparer.Ordinal);

                var conversations = new List<Conversation>();
                for (int i = 0; i < options.Conversations; i++)
                    conversations.Add(new Conversation
                    {
                        Id = $"projects-agent-p1-w{i}",
                        ReadyAt = Start.AddSeconds(i * 7),
                        TurnsLeft = options.TurnsPerWake,
                        LastCompletedAt = Start,
                    });

                var slots = new Slot[options.Slots];
                for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
                var queue = new List<Pending>();

                DateTime now = Start;
                DateTime end = Start.AddHours(options.Hours);
                int coldInFlight = 0;
                int lastK = -1;

                while (now < end)
                {
                    // 1. Return finished slots.
                    foreach (Slot slot in slots)
                    {
                        if (slot.Occupant == null || slot.FreeAt > now) continue;
                        Conversation done = slot.Occupant;
                        done.InFlight = false;
                        done.LastCompletedAt = slot.FreeAt;
                        result.CompletedTurns++;
                        done.Served++;
                        if (useScheduler) scheduler.OnRelease(done.Key, slot.FreeAt);
                        if (!slot.WasResident && coldInFlight > 0) coldInFlight--;

                        done.TurnsLeft--;
                        done.ReadyAt = done.TurnsLeft > 0
                            ? slot.FreeAt + options.ThinkTime
                            : slot.FreeAt + RandomGap(rng, options);
                        if (done.TurnsLeft <= 0) done.TurnsLeft = options.TurnsPerWake;

                        slot.Occupant = null;
                    }

                    // 2. Assemble any turn whose conversation is ready. The rotation decision happens
                    //    HERE, not at dispatch — which is exactly why parking a request cannot undo the
                    //    fact that it was already built as a continuation.
                    foreach (Conversation conversation in conversations)
                    {
                        if (conversation.Queued || conversation.InFlight || conversation.ReadyAt > now) continue;
                        if (conversation.LastDispatchAt is { } last
                            && now - last >= Omnipotent.Services.KliveLLM.KliveLLM.BriefSessionIdleLimit)
                        {
                            conversation.Epoch++;              // the client rebuilds the brief
                            conversation.LastDispatchAt = null; // a fresh prefix: nothing to reuse
                        }
                        conversation.Queued = true;
                        queue.Add(new Pending
                        {
                            Conversation = conversation,
                            EnqueuedAt = now,
                            Reusable = conversation.LastDispatchAt.HasValue,
                        });
                    }

                    // 3. Dispatch into free slots.
                    if (useScheduler)
                        scheduler.Maintain(now, meter.EffectiveLifetime(), meter.WarmServiceTime(),
                            meter.NonResidentSlotShare(), options.Slots,
                            queue.Select(q => q.Conversation.Key));

                    while (queue.Count > 0)
                    {
                        Slot? free = Array.Find(slots, s => s.Occupant == null);
                        if (free == null) break;

                        Pending? pick = useScheduler
                            ? PickScheduled(queue, scheduler, meter, now, coldInFlight)
                            : queue[0];
                        if (pick == null) break;

                        queue.Remove(pick);
                        Dispatch(pick, free, scheduler, meter, providerCache, options, now, useScheduler,
                            result, ref coldInFlight);
                    }

                    if (useScheduler)
                    {
                        if (lastK >= 0 && scheduler.K != lastK) result.CohortChanges++;
                        lastK = scheduler.K;
                    }

                    now = NextInstant(now, end, slots, queue, conversations, useScheduler, scheduler, meter);
                }

                result.LeastServedConversation = int.MaxValue;
                foreach (Conversation conversation in conversations)
                {
                    TimeSpan silence = end - conversation.LastCompletedAt;
                    if (silence > result.LongestSilence) result.LongestSilence = silence;
                    if (conversation.LastCompletedAt <= Start) result.StarvedConversations++;
                    if (conversation.Served < result.LeastServedConversation)
                        result.LeastServedConversation = conversation.Served;
                }
                result.MeanTurnsPerConversation = (double)result.CompletedTurns / conversations.Count;
                result.FinalK = useScheduler ? scheduler.K : options.Conversations;
                var curve = meter.Describe();
                result.Curve = $"T_eff={curve.Lifetime.TotalSeconds:0}s ({curve.LifetimeSource}), " +
                    $"S_warm={curve.WarmServiceTime.TotalSeconds:0}s, S_cold={curve.ColdServiceTime.TotalSeconds:0}s, " +
                    $"beta={curve.NonResidentSlotShare:P0}, K={result.FinalK}";
                return result;
            }

            private static Pending? PickScheduled(List<Pending> queue, AIRouterResidencyScheduler scheduler,
                PrefixSurvivalMeter meter, DateTime now, int coldInFlight)
            {
                TimeSpan lifetime = meter.EffectiveLifetime();
                TimeSpan warmService = meter.WarmServiceTime();

                Pending? best = null;
                DateTime bestDeadline = DateTime.MaxValue;
                foreach (Pending candidate in queue)
                {
                    var ticket = new AIRouterWorkTicket(candidate.Conversation.Key,
                        AIRouterWorkClass.Agent, AIRouterSlotIntent.Turn);
                    DateTime deadline = scheduler.Deadline(ticket, candidate.EnqueuedAt, lifetime);
                    bool pastDeadline = now >= deadline;
                    if (!scheduler.MayDispatch(ticket, now, pastDeadline, coldInFlight, 0, warmService, out _))
                        continue;
                    if (best != null && deadline >= bestDeadline) continue;
                    best = candidate;
                    bestDeadline = deadline;
                }
                return best;
            }

            private static void Dispatch(Pending pending, Slot slot, AIRouterResidencyScheduler scheduler,
                PrefixSurvivalMeter meter, Dictionary<string, DateTime> providerCache,
                SimulationOptions options, DateTime now, bool useScheduler, Result result, ref int coldInFlight)
            {
                Conversation conversation = pending.Conversation;
                string key = conversation.Key;
                var ticket = new AIRouterWorkTicket(key, AIRouterWorkClass.Agent, AIRouterSlotIntent.Turn);

                bool wasResident = useScheduler && scheduler.IsResident(key);
                TimeSpan? gap = pending.Reusable && conversation.LastDispatchAt is { } last
                    ? now - last
                    : null;

                bool providerHasIt = providerCache.TryGetValue(key, out DateTime writtenAt)
                    && now - writtenAt <= options.ProviderCacheLifetime;
                bool hit = pending.Reusable && providerHasIt;

                if (pending.Reusable)
                {
                    result.ReusableSamples++;
                    if (hit) result.ReusableHits++;
                    else if (gap is { } g && g < Omnipotent.Services.KliveLLM.KliveLLM.BriefSessionIdleLimit)
                        result.DeadZoneDispatches++;
                }
                if (now - pending.EnqueuedAt > TimeSpan.FromSeconds(5)) result.ParkedDispatches++;

                TimeSpan service = hit ? options.BaseService : options.BaseService + options.PrefillPenalty;

                if (useScheduler)
                {
                    bool pastDeadline = now >= scheduler.Deadline(ticket, pending.EnqueuedAt, meter.EffectiveLifetime());
                    scheduler.OnDispatch(ticket, now, pastDeadline);
                    if (!wasResident) coldInFlight++;
                }
                meter.RecordOutcome(key, gap, promptTokens: 50_000, cachedTokens: hit ? 49_000 : 0,
                    wasResident: wasResident, outsideCohort: false, slotOccupancy: service, nowUtc: now);

                providerCache[key] = now;
                conversation.LastDispatchAt = now;
                conversation.Queued = false;
                conversation.InFlight = true;

                slot.Occupant = conversation;
                slot.WasResident = wasResident;
                slot.FreeAt = now + service;
            }

            private static DateTime NextInstant(DateTime now, DateTime end, Slot[] slots, List<Pending> queue,
                List<Conversation> conversations, bool useScheduler,
                AIRouterResidencyScheduler scheduler, PrefixSurvivalMeter meter)
            {
                DateTime next = end;
                foreach (Slot slot in slots)
                    if (slot.Occupant != null && slot.FreeAt > now && slot.FreeAt < next) next = slot.FreeAt;
                // The moment a conversation next wants a turn. Without this the clock skips straight
                // over every think-time and idle gap, which silently turns the whole model into noise.
                foreach (Conversation conversation in conversations)
                    if (!conversation.Queued && !conversation.InFlight
                        && conversation.ReadyAt > now && conversation.ReadyAt < next) next = conversation.ReadyAt;
                foreach (Pending pending in queue)
                {
                    if (!useScheduler) continue;
                    var ticket = new AIRouterWorkTicket(pending.Conversation.Key,
                        AIRouterWorkClass.Agent, AIRouterSlotIntent.Turn);
                    DateTime deadline = scheduler.Deadline(ticket, pending.EnqueuedAt, meter.EffectiveLifetime());
                    if (deadline > now && deadline < next) next = deadline;
                }
                return next > now ? next : now.AddSeconds(1);
            }

            private static TimeSpan RandomGap(Random rng, SimulationOptions options)
            {
                double span = (options.MaxIdleGap - options.MinIdleGap).TotalSeconds;
                return options.MinIdleGap + TimeSpan.FromSeconds(rng.NextDouble() * span);
            }
        }
    }
}
