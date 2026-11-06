using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// The Background work class added for Omniscience v3 (Klives: background work yields to interactive).
    /// It must never cost Projects/KliveAgent a slot or a warm prefix: lowest band, cold starts share the
    /// one "slack" permit with one-shots, and it takes only a free residency seat, never evicting.
    /// </summary>
    public class AIRouterBackgroundClassTests
    {
        private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        private static AIRouterWorkTicket Ticket(string key, AIRouterWorkClass cls) => new(key, cls, AIRouterSlotIntent.Turn);

        [Theory]
        [InlineData("omniscience-bg-extract-p1-s3", AIRouterWorkClass.Background)]
        [InlineData("omniscience-ia-ask-123", AIRouterWorkClass.Interactive)]
        public void OmniscienceSessionIds_Classify(string id, AIRouterWorkClass expected) =>
            Assert.Equal(expected, AIRouterWorkClassifier.Classify(id));

        [Fact]
        public void Background_IsTheLowestBandAndSlackOnly()
        {
            foreach (var cls in new[] { AIRouterWorkClass.Interactive, AIRouterWorkClass.Agent, AIRouterWorkClass.Burst, AIRouterWorkClass.OneShot })
                Assert.True(AIRouterResidencyScheduler.Band(AIRouterWorkClass.Background) > AIRouterResidencyScheduler.Band(cls));
            Assert.True(AIRouterResidencyScheduler.IsSlackOnly(AIRouterWorkClass.Background));
            Assert.True(AIRouterResidencyScheduler.IsSlackOnly(AIRouterWorkClass.OneShot));
            Assert.False(AIRouterResidencyScheduler.IsSlackOnly(AIRouterWorkClass.Agent));
            Assert.False(AIRouterResidencyScheduler.IsLive(AIRouterWorkClass.Background));
        }

        [Fact]
        public void BackgroundColdStart_IsSerialisedWithOneShotsAndYieldsToWarmResidents()
        {
            var s = new AIRouterResidencyScheduler();
            s.Maintain(T0, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30), 0.2, 3);
            var bg = Ticket("omniscience-bg-a#e|qwen", AIRouterWorkClass.Background);

            Assert.False(s.MayDispatch(bg, T0, pastDeadline: false, coldInFlight: 0, oneShotInFlight: 1, TimeSpan.FromSeconds(30), out string reason));
            Assert.Equal("oneshot-serialised", reason);

            // A warm, idle resident whose next turn is imminent: background must not land and evict its prefix.
            s.OnDispatch(Ticket("projects-commander-p#e|qwen", AIRouterWorkClass.Agent), T0, false);
            s.OnRelease("projects-commander-p#e|qwen", T0);
            Assert.False(s.MayDispatch(bg, T0.AddSeconds(5), false, 0, 0, TimeSpan.FromSeconds(30), out reason));
            Assert.Equal("oneshot-yields-to-warm", reason);
        }

        [Fact]
        public void BackgroundContinuation_IsNeverParked()
        {
            var s = new AIRouterResidencyScheduler();
            s.Maintain(T0, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30), 0.2, 3);
            var bg = Ticket("omniscience-bg-a#e|qwen", AIRouterWorkClass.Background);
            s.OnDispatch(bg, T0, false);
            s.OnRelease(bg.PrefixKey, T0.AddSeconds(20));
            // Second pass over the same prefix ("prefill once, ask many") goes straight through.
            Assert.True(s.MayDispatch(bg, T0.AddSeconds(21), false, 0, 1, TimeSpan.FromSeconds(30), out _));
        }

        [Fact]
        public void Background_TakesOnlyAFreeSeatAndNeverEvicts()
        {
            var s = new AIRouterResidencyScheduler();
            // Slow cold turns shrink the cohort to its floor.
            s.Maintain(T0, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180), 0.2, 3);
            int k = s.K;
            var agents = Enumerable.Range(0, k).Select(i => $"projects-agent-p{i}#e|qwen").ToList();
            foreach (var a in agents) s.OnDispatch(Ticket(a, AIRouterWorkClass.Agent), T0, false);
            Assert.All(agents, a => Assert.True(s.IsResident(a)));

            var bg = Ticket("omniscience-bg-full#e|qwen", AIRouterWorkClass.Background);
            Assert.False(s.OnDispatch(bg, T0.AddSeconds(1), pastDeadline: true));
            Assert.False(s.IsResident(bg.PrefixKey));
            Assert.All(agents, a => Assert.True(s.IsResident(a)));
        }
    }
}
