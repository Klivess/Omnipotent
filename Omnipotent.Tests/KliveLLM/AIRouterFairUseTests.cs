using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// The limiter's contract is "a queued wait, never a rejection and never a 429". These tests pin
    /// the three published limits independently, plus the two things that make the queue usable in an
    /// agent fleet: reconciliation (a pessimistic reservation must not hold capacity it didn't use)
    /// and the shared penalty (one caller's 429 must back everyone off, not just itself).
    /// </summary>
    public class AIRouterFairUseTests
    {
        [Fact]
        public void PublishedPolicy_IsPinnedToWhatAIRouterPublishes()
        {
            // If AIRouter ever changes its fair-use dialog, this is the test that should fail first.
            Assert.Equal(3, AIRouterFairUseLimiter.PolicyMaxParallelRequests);
            Assert.Equal(240, AIRouterFairUseLimiter.PolicyMaxRequestsPerMinute);
            Assert.Equal(10_000_000, AIRouterFairUseLimiter.PolicyMaxTokensPerMinute);
            Assert.Equal(TimeSpan.FromMinutes(1), AIRouterFairUseLimiter.Window);
        }

        [Fact]
        public void DefaultCeilings_SitUnderThePublishedLimits()
        {
            // Admitting at exactly the published number can still land inside the router's previous
            // window, so the effective ceilings must be strictly lower.
            var snapshot = new AIRouterFairUseLimiter().Describe();
            Assert.True(snapshot.RequestCeiling < AIRouterFairUseLimiter.PolicyMaxRequestsPerMinute);
            Assert.True(snapshot.TokenCeiling < AIRouterFairUseLimiter.PolicyMaxTokensPerMinute);
        }

        [Fact]
        public async Task ParallelCeiling_QueuesTheFourthRequestUntilASlotIsReturned()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 3);
            var held = new List<AIRouterFairUseLease>();
            for (int i = 0; i < 3; i++) held.Add(await limiter.AcquireAsync(10));
            Assert.Equal(3, limiter.Describe().InFlight);

            var fourth = limiter.AcquireAsync(10);
            await Task.Delay(100);
            Assert.False(fourth.IsCompleted); // queued, not rejected

            held[0].Dispose();
            var admitted = await fourth.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, limiter.Describe().InFlight);

            admitted.Dispose();
            held[1].Dispose();
            held[2].Dispose();
        }

        [Fact]
        public async Task RequestWindow_HoldsTheNextCallerUntilTheMinuteRollsOver()
        {
            var clock = new VirtualClock();
            // 4 published → a ceiling of 3 after headroom, so the fourth call must wait out the window.
            var limiter = new AIRouterFairUseLimiter(maxRequestsPerMinute: 4,
                nowUtc: clock.Now, delay: clock.DelayAsync);
            Assert.Equal(3, limiter.Describe().RequestCeiling);

            // Three requests ten seconds apart, filling the ceiling: t=0, t=10, t=20.
            DateTime start = clock.UtcNow;
            for (int i = 0; i < 3; i++)
            {
                (await limiter.AcquireAsync(10, CancellationToken.None)).Dispose();
                clock.Advance(TimeSpan.FromSeconds(10));
            }
            Assert.Equal(TimeSpan.FromSeconds(30), clock.UtcNow - start); // none of them waited

            (await limiter.AcquireAsync(10, CancellationToken.None)).Dispose();
            // It waits for the t=0 entry to leave the window — until t=60 — and no longer.
            Assert.InRange(clock.UtcNow - start, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(70));
            // The t=0 entry aged out; the t=10 and t=20 entries plus this one remain.
            Assert.Equal(3, limiter.Describe().RequestsInWindow);
        }

        [Fact]
        public async Task TokenWindow_HoldsACallerThatWouldOvercommitTheMinute()
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(maxTokensPerMinute: 1_000,
                nowUtc: clock.Now, delay: clock.DelayAsync);

            DateTime start = clock.UtcNow;
            (await limiter.AcquireAsync(900, CancellationToken.None)).Dispose();
            Assert.Equal(start, clock.UtcNow);

            // 900 + 900 exceeds the 950 ceiling, so this one waits for the first to expire.
            (await limiter.AcquireAsync(900, CancellationToken.None)).Dispose();
            Assert.True(clock.UtcNow - start >= AIRouterFairUseLimiter.Window);
        }

        [Fact]
        public async Task Reconciliation_ReleasesCapacityTheRequestDidNotActuallyUse()
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(maxTokensPerMinute: 1_000,
                nowUtc: clock.Now, delay: clock.DelayAsync);

            DateTime start = clock.UtcNow;
            var first = await limiter.AcquireAsync(900, CancellationToken.None);
            // The reservation was prompt + the whole completion reserve; the model used a fraction.
            first.ReportActualTokens(50);
            first.Dispose();
            Assert.Equal(50, limiter.Describe().TokensInWindow);

            // Without reconciliation this would have waited a full minute (see the test above).
            (await limiter.AcquireAsync(900, CancellationToken.None)).Dispose();
            Assert.Equal(start, clock.UtcNow);
        }

        [Fact]
        public async Task Penalty_BacksEveryQueuedCallerOffForTheCoolOff()
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(nowUtc: clock.Now, delay: clock.DelayAsync);

            DateTime start = clock.UtcNow;
            limiter.Penalize(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(30), limiter.Describe().PenaltyRemaining);

            (await limiter.AcquireAsync(10, CancellationToken.None)).Dispose();
            Assert.True(clock.UtcNow - start >= TimeSpan.FromSeconds(30),
                "a 429 cool-off must hold admission, not just the caller that hit it");
            Assert.Equal(TimeSpan.Zero, limiter.Describe().PenaltyRemaining);
        }

        [Fact]
        public async Task AnOverSizedEstimateIsClampedRatherThanWaitingForever()
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(maxTokensPerMinute: 1_000,
                nowUtc: clock.Now, delay: clock.DelayAsync);

            // Larger than the whole per-minute ceiling: it must still be admitted against an empty
            // window instead of becoming permanently inadmissible.
            var lease = await limiter.AcquireAsync(50_000, CancellationToken.None);
            Assert.NotNull(lease);
            Assert.Equal(950, limiter.Describe().TokensInWindow);
            lease.Dispose();
        }

        [Fact]
        public async Task ACancelledCallerDoesNotStrandTheParallelSlotItWasWaitingOn()
        {
            var limiter = new AIRouterFairUseLimiter(maxParallelRequests: 1);
            var held = await limiter.AcquireAsync(10);

            using var cts = new CancellationTokenSource();
            var queued = limiter.AcquireAsync(10, cts.Token);
            await Task.Delay(50);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

            held.Dispose();
            // The slot is genuinely free again — a cancellation must not leak the semaphore.
            var next = await limiter.AcquireAsync(10).WaitAsync(TimeSpan.FromSeconds(5));
            next.Dispose();
        }

        /// <summary>Advances instantly instead of sleeping, so window arithmetic is tested in
        /// milliseconds rather than minutes.</summary>
        private sealed class VirtualClock
        {
            public DateTime UtcNow { get; private set; } = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

            public Func<DateTime> Now => () => UtcNow;

            public void Advance(TimeSpan span) => UtcNow += span;

            public Task DelayAsync(TimeSpan span, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (span > TimeSpan.Zero) UtcNow += span;
                return Task.CompletedTask;
            }
        }
    }
}
