using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// A request Klives is waiting on goes ahead of the fleet's background turns in every scheduler mode.
    /// Fifo switches off the cache-aware machinery; it must not also turn a reply to him into the back of
    /// a queue that dozens of agent turns joined first.
    /// </summary>
    public class AIRouterHumanFirstTests
    {
        [Fact]
        public async Task InFifoMode_AnInteractiveRequestTakesTheNextFreeSlotAheadOfEarlierAgentTurns()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            Assert.Equal("Fifo", limiter.Describe().Mode);
            var held = new List<AIRouterFairUseLease>();
            for (int i = 0; i < 3; i++)
                held.Add(await limiter.AcquireAsync(10, Ticket($"projects-agent-{i}", AIRouterWorkClass.Agent)));

            var earlierAgent = limiter.AcquireAsync(10, Ticket("projects-agent-x", AIRouterWorkClass.Agent));
            await Task.Delay(50);
            var klives = limiter.AcquireAsync(10, Ticket("projects-commander-p#1", AIRouterWorkClass.Interactive));
            await Task.Delay(50);

            held[0].Dispose();
            var admitted = await klives.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(earlierAgent.IsCompleted); // arrival order did not win over a waiting human

            admitted.Dispose();
            (await earlierAgent.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            held[1].Dispose();
            held[2].Dispose();
        }

        [Fact]
        public async Task InFifoMode_BackgroundWorkKeepsArrivalOrderAmongItself()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 1);
            var holder = await limiter.AcquireAsync(10, Ticket("projects-agent-0", AIRouterWorkClass.Agent));
            var first = limiter.AcquireAsync(10, Ticket("projects-agent-1", AIRouterWorkClass.Agent));
            await Task.Delay(50);
            var second = limiter.AcquireAsync(10, Ticket("projects-agent-2", AIRouterWorkClass.OneShot));
            await Task.Delay(50);

            holder.Dispose();
            var admitted = await first.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(second.IsCompleted);
            admitted.Dispose();
            (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }

        private static AIRouterWorkTicket Ticket(string key, AIRouterWorkClass cls) => new(key, cls, AIRouterSlotIntent.Turn);
    }
}
