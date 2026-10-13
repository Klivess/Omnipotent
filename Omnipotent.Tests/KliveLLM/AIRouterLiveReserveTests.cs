using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// While Klives is talking, one of AIRouter's three slots is kept free for him: background work may
    /// fill the other two, live work (Interactive, Audio) may use all three, nothing in flight is ever
    /// pre-empted, and the reserve lapses on its own when the conversation goes quiet.
    /// </summary>
    public class AIRouterLiveReserveTests
    {
        [Fact]
        public async Task Reserve_HoldsTheLastSlotBackFromBackgroundWork_ButNotFromKlives()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            limiter.EngageInteractiveReserve("talk:test", TimeSpan.FromMinutes(5));
            Assert.Equal(1, limiter.Describe().ReservedSlots);

            var a = await limiter.AcquireAsync(10, Ticket("projects-agent-a", AIRouterWorkClass.Agent));
            var b = await limiter.AcquireAsync(10, Ticket("projects-agent-b", AIRouterWorkClass.Agent));
            var c = limiter.AcquireAsync(10, Ticket("projects-agent-c", AIRouterWorkClass.Agent));
            await Task.Delay(150);
            Assert.False(c.IsCompleted); // the fleet waits; it does not take the reserved slot

            var live = await limiter.AcquireAsync(10, Ticket("kliveagent-conv", AIRouterWorkClass.Interactive))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, limiter.Describe().InFlight);
            live.Dispose();
            await Task.Delay(150);
            Assert.False(c.IsCompleted); // the free slot is still the reserved one

            limiter.ReleaseInteractiveReserve("talk:test");
            var third = await c.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, limiter.Describe().ReservedSlots);

            third.Dispose();
            a.Dispose();
            b.Dispose();
        }

        [Fact]
        public async Task BackgroundWork_CanStillFillTheOtherSlots_AlongsideALiveRequest()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            limiter.EngageInteractiveReserve("talk:test", TimeSpan.FromMinutes(5));

            var live = await limiter.AcquireAsync(10, Ticket("kliveagent-conv", AIRouterWorkClass.Interactive));
            var a = await limiter.AcquireAsync(10, Ticket("projects-agent-a", AIRouterWorkClass.Agent)).WaitAsync(TimeSpan.FromSeconds(2));
            var b = await limiter.AcquireAsync(10, Ticket("projects-agent-b", AIRouterWorkClass.Agent)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, limiter.Describe().InFlight);

            live.Dispose();
            a.Dispose();
            b.Dispose();
        }

        [Fact]
        public async Task SpeechCalls_AreLiveWork_AndMayUseTheReservedSlot()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            limiter.EngageInteractiveReserve("talk:test", TimeSpan.FromMinutes(5));
            var a = await limiter.AcquireAsync(10, Ticket("projects-agent-a", AIRouterWorkClass.Agent));
            var b = await limiter.AcquireAsync(10, Ticket("projects-agent-b", AIRouterWorkClass.Agent));

            var speech = await limiter.AcquireAsync(1, Ticket("audio:tts", AIRouterWorkClass.Audio)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, limiter.Describe().InFlight);

            speech.Dispose();
            a.Dispose();
            b.Dispose();
            Assert.Equal(0, limiter.Describe().InFlight);
        }

        [Fact]
        public async Task Reserve_NeverPreemptsWorkAlreadyInFlight()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            var held = new List<AIRouterFairUseLease>();
            for (int i = 0; i < 3; i++)
                held.Add(await limiter.AcquireAsync(10, Ticket($"projects-agent-{i}", AIRouterWorkClass.Agent)));

            limiter.EngageInteractiveReserve("talk:test", TimeSpan.FromMinutes(5));
            Assert.Equal(3, limiter.Describe().InFlight); // nobody was evicted

            var live = limiter.AcquireAsync(10, Ticket("kliveagent-conv", AIRouterWorkClass.Interactive));
            var background = limiter.AcquireAsync(10, Ticket("projects-agent-x", AIRouterWorkClass.Agent));
            held[0].Dispose();
            var admitted = await live.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(background.IsCompleted); // the freed slot went to Klives, and the fleet stays at two

            admitted.Dispose();
            limiter.ReleaseInteractiveReserve("talk:test");
            (await background.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            held[1].Dispose();
            held[2].Dispose();
        }

        [Fact]
        public async Task Reserve_LapsesOnItsOwn_WhenTheConversationGoesQuiet()
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3, nowUtc: clock.Now, delay: clock.DelayAsync);
            limiter.EngageInteractiveReserve("talk:test", TimeSpan.FromMinutes(1));
            DateTime start = clock.UtcNow;

            var a = await limiter.AcquireAsync(10, Ticket("projects-agent-a", AIRouterWorkClass.Agent));
            var b = await limiter.AcquireAsync(10, Ticket("projects-agent-b", AIRouterWorkClass.Agent));
            var c = await limiter.AcquireAsync(10, Ticket("projects-agent-c", AIRouterWorkClass.Agent)).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(clock.UtcNow - start >= TimeSpan.FromMinutes(1));
            Assert.Equal(0, limiter.Describe().ReservedSlots);
            a.Dispose();
            b.Dispose();
            c.Dispose();
        }

        [Fact]
        public void Reserve_IsOneSlotHoweverManyTabsHoldIt_AndNeverTheOnlySlot()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            limiter.EngageInteractiveReserve("talk:one", TimeSpan.FromMinutes(5));
            limiter.EngageInteractiveReserve("talk:two", TimeSpan.FromMinutes(5));
            Assert.Equal(1, limiter.Describe().ReservedSlots);
            limiter.ReleaseInteractiveReserve("talk:one");
            Assert.Equal(1, limiter.Describe().ReservedSlots);
            limiter.ReleaseInteractiveReserve("talk:two");
            Assert.Equal(0, limiter.Describe().ReservedSlots);

            var single = new AIRouterFairUseLimiter(maxParallelRequests: 1);
            single.EngageInteractiveReserve("talk:one", TimeSpan.FromMinutes(5));
            Assert.Equal(0, single.Describe().ReservedSlots);
        }

        private static AIRouterWorkTicket Ticket(string key, AIRouterWorkClass cls) => new(key, cls, AIRouterSlotIntent.Turn);

        private sealed class VirtualClock
        {
            public DateTime UtcNow { get; private set; } = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
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
