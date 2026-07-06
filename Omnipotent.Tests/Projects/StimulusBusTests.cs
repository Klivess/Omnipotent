using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Stimulus;

namespace Omnipotent.Tests.Projects
{
    public class StimulusQueueTests
    {
        private static string NewPid() => "test_" + Guid.NewGuid().ToString("N");

        private static StimulusEnvelope Env(string pid, string hookID, string payload,
            StimulusDurability dur = StimulusDurability.Standard, string supKey = "", TimeSpan? ttl = null) => new()
        {
            ProjectID = pid,
            HookID = hookID,
            SourceKind = "test",
            Durability = dur,
            SupersessionKey = supKey,
            Payload = payload,
            ExpiresAt = ttl.HasValue ? DateTime.UtcNow + ttl.Value : null,
        };

        [Fact]
        public async Task Enqueue_DeliversToDestinationAgent()
        {
            var q = new StimulusQueue(_ => { });
            var delivered = new List<StimulusEnvelope>();
            var done = new TaskCompletionSource();
            q.OnDeliver = e => { delivered.Add(e); done.TrySetResult(); return Task.CompletedTask; };
            string pid = NewPid();
            await q.EnqueueAsync(Env(pid, "hook1", "hello"), "commander");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(delivered);
            Assert.Equal("hello", delivered[0].Payload);
        }

        [Fact]
        public async Task PerAgentChannels_FanOutIndependently()
        {
            var q = new StimulusQueue(_ => { });
            var byAgent = new System.Collections.Concurrent.ConcurrentBag<string>();
            int count = 0;
            var done = new TaskCompletionSource();
            q.OnDeliver = e => { byAgent.Add(e.Payload); if (Interlocked.Increment(ref count) == 2) done.TrySetResult(); return Task.CompletedTask; };
            string pid = NewPid();
            await q.EnqueueAsync(Env(pid, "h1", "for-A"), "agentA");
            await q.EnqueueAsync(Env(pid, "h2", "for-B"), "agentB");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("for-A", byAgent);
            Assert.Contains("for-B", byAgent);
        }

        [Fact]
        public async Task ExpiredSuperseding_IsDroppedNotDelivered()
        {
            var q = new StimulusQueue(_ => { });
            int delivered = 0;
            q.OnDeliver = _ => { Interlocked.Increment(ref delivered); return Task.CompletedTask; };
            string pid = NewPid();
            // Already-expired TTL → the reader drops it.
            await q.EnqueueAsync(Env(pid, "screen", "stale frame", StimulusDurability.SupersedingByKey, "region1", TimeSpan.FromMilliseconds(-1)), "commander");
            await Task.Delay(200);
            Assert.Equal(0, delivered);
        }

        [Fact]
        public async Task Replay_RedeliversUndeliveredAfterRestart()
        {
            string pid = NewPid();
            // First queue instance: delivery always fails, so the envelope stays undelivered on disk.
            var q1 = new StimulusQueue(_ => { });
            q1.OnDeliver = _ => throw new Exception("simulated delivery failure");
            await q1.EnqueueAsync(Env(pid, "hookR", "durable-payload"), "commander");
            await Task.Delay(150);

            // Second instance (a "restart"): replay must re-dispatch the undelivered envelope.
            var q2 = new StimulusQueue(_ => { });
            var redelivered = new TaskCompletionSource<string>();
            q2.OnDeliver = e => { redelivered.TrySetResult(e.Payload); return Task.CompletedTask; };
            q2.ReplayUndelivered();
            string got = await redelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("durable-payload", got);
        }

        [Fact]
        public async Task DeliveredEnvelope_IsNotReplayed()
        {
            string pid = NewPid();
            var q1 = new StimulusQueue(_ => { });
            var done = new TaskCompletionSource();
            q1.OnDeliver = _ => { done.TrySetResult(); return Task.CompletedTask; };
            await q1.EnqueueAsync(Env(pid, "hookD", "one-shot"), "commander");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100); // let MarkDelivered persist

            var q2 = new StimulusQueue(_ => { });
            int replayed = 0;
            q2.OnDeliver = _ => { Interlocked.Increment(ref replayed); return Task.CompletedTask; };
            q2.ReplayUndelivered();
            await Task.Delay(200);
            Assert.Equal(0, replayed); // already delivered, nothing to replay
        }
    }

    public class StimulusAgentTests
    {
        private static StimulusAgent Agent(Func<string, string, Task<string?>> query) =>
            new(query, _ => ("free-model", "fallback-model"), _ => { });

        private static StimulusEnvelope Env(string payload) => new() { SourceKind = "email", Payload = payload };

        [Fact]
        public async Task Confirm_IsParsed()
        {
            var agent = Agent((_, _) => Task.FromResult<string?>("CONFIRM: supplier sent a quote"));
            var r = await agent.EvaluateAsync(Env("quote attached"), "a supplier price quote");
            Assert.True(r.Confirmed);
            Assert.Equal("supplier sent a quote", r.Verdict);
        }

        [Fact]
        public async Task Reject_IsParsed()
        {
            var agent = Agent((_, _) => Task.FromResult<string?>("REJECT: just a newsletter"));
            var r = await agent.EvaluateAsync(Env("50% off sale"), "a supplier price quote");
            Assert.False(r.Confirmed);
        }

        [Fact]
        public async Task EmptyCriterion_AlwaysConfirms_WithoutCallingModel()
        {
            bool called = false;
            var agent = Agent((_, _) => { called = true; return Task.FromResult<string?>("REJECT: x"); });
            var r = await agent.EvaluateAsync(Env("anything"), "");
            Assert.True(r.Confirmed);
            Assert.False(called);
        }

        [Fact]
        public async Task FreeModelThrottled_FallsBackToPaid()
        {
            int calls = 0;
            var agent = Agent((_, model) =>
            {
                calls++;
                if (model == "free-model") throw new Exception("429 throttled");
                return Task.FromResult<string?>("CONFIRM: fallback handled it");
            });
            var r = await agent.EvaluateAsync(Env("x"), "some criterion");
            Assert.True(r.Confirmed);
            Assert.Equal(2, calls); // free failed, fallback succeeded
        }

        [Fact]
        public async Task BothModelsFail_FailsOpen()
        {
            var agent = Agent((_, _) => throw new Exception("down"));
            var r = await agent.EvaluateAsync(Env("x"), "criterion");
            Assert.True(r.Confirmed); // over-deliver rather than drop a real event
        }
    }

    public class StimulusHookStoreTests
    {
        [Fact]
        public void CRUD_RoundTripsAndLogsChanges()
        {
            var log = new ProjectEventLogStore(_ => { });
            var store = new StimulusHookStore(log);
            string pid = "test_" + Guid.NewGuid().ToString("N");

            var hook = store.Create(new StimulusHookRecord { ProjectID = pid, SourceKind = "timer", RecognitionCriterion = "always" });
            Assert.NotEmpty(hook.HookID);
            Assert.Single(store.List(pid));
            Assert.True(store.Delete(pid, hook.HookID));
            Assert.Empty(store.List(pid));
            // Each mutation logged a HookChanged event.
            Assert.Equal(2, log.ReadSince(pid, 0).Count(e => e.Type == ProjectEventTypes.HookChanged));
        }
    }
}
