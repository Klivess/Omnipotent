using Newtonsoft.Json;
using Omnipotent.Services.KliveLLM;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveLLM;

public class AIRouterPrefillBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CachedInputRefundsCapacity_WithoutDiscountingTheInitialReservation()
    {
        var clock = new VirtualClock();
        var limiter = Create(clock);
        using var first = await limiter.AcquireAsync(900, 900);
        Assert.Equal(100, limiter.Describe().UncachedTokensAvailable);
        first.ReportPromptTokens(900, 850);
        Assert.Equal(950, limiter.Describe().UncachedTokensAvailable);
        first.ReportPromptTokens(900, 850); // repeated reconciliation cannot mint credit
        Assert.Equal(950, limiter.Describe().UncachedTokensAvailable);
        using var second = await limiter.AcquireAsync(900, 900);
        Assert.Equal(clock.Start, clock.Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(901L)]
    public async Task MissingOrInvalidCacheMetricsChargeFullPrompt(long? cached)
    {
        var clock = new VirtualClock();
        var limiter = Create(clock);
        using (var first = await limiter.AcquireAsync(900, 900)) first.ReportPromptTokens(900, cached);
        using var second = await limiter.AcquireAsync(900, 900);
        Assert.InRange(clock.Now - clock.Start, TimeSpan.FromSeconds(800), TimeSpan.FromSeconds(801));
    }

    [Fact]
    public async Task MissingUsageAndFailedCallsDoNotReclaimReservationsWhenDisposed()
    {
        var clock = new VirtualClock();
        var limiter = Create(clock);
        (await limiter.AcquireAsync(900, 900)).Dispose();
        Assert.Equal(100, limiter.Describe().UncachedTokensAvailable);
        using var retry = await limiter.AcquireAsync(900, 900);
        Assert.True(clock.Now - clock.Start >= TimeSpan.FromSeconds(800));
    }

    [Fact]
    public async Task SlowResponsesReconcileAfterTheMinuteWindowExpired_AndUnderestimatesCreateDebt()
    {
        var clock = new VirtualClock();
        var limiter = Create(clock);
        using var first = await limiter.AcquireAsync(900, 900);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, limiter.Describe().RequestsInWindow);
        first.ReportPromptTokens(1500, 0);
        Assert.Equal(-380, limiter.Describe().UncachedTokensAvailable);
        using var next = await limiter.AcquireAsync(100, 100);
        Assert.InRange(clock.Now - clock.Start, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(601));
    }

    [Fact]
    public async Task CancellationWhileWaitingReleasesSlotsAndDoesNotReserveUnsentInput()
    {
        using var cts = new CancellationTokenSource();
        var limiter = new AIRouterFairUseLimiter(uncachedBurstTokens: 1000, uncachedTokensPerHour: 3600,
            delay: (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        (await limiter.AcquireAsync(900, 900)).Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(900, 900, cts.Token));
        Assert.Equal(0, limiter.Describe().InFlight);
        Assert.Equal(1, limiter.Describe().TotalAdmitted);
        using var next = await limiter.AcquireAsync(50, 50);
        Assert.InRange(limiter.Describe().UncachedTokensAvailable, 50, 55);
    }

    [Fact]
    public async Task RestartRestoresTheBudgetIncludingRequestsWithoutUsage()
    {
        string path = Path.Combine(Path.GetTempPath(), "airouter-prefill-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var clock = new VirtualClock();
            (await Create(clock, path).AcquireAsync(900, 900)).Dispose();
            var restarted = Create(clock, path);
            Assert.Equal(100, restarted.Describe().UncachedTokensAvailable);
            using var next = await restarted.AcquireAsync(900, 900);
            Assert.True(clock.Now - clock.Start >= TimeSpan.FromSeconds(800));
            Assert.DoesNotContain("prompt", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task RestartPreservesProviderRetryAfter()
    {
        string path = Path.Combine(Path.GetTempPath(), "airouter-prefill-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var clock = new VirtualClock();
            Create(clock, path).Penalize(TimeSpan.FromHours(1));
            var restarted = Create(clock, path);
            using var first = await restarted.AcquireAsync(100, 100);
            Assert.True(clock.Now - clock.Start >= TimeSpan.FromHours(1));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task CorruptStateDoesNotGrantAFreshBurst_AndPersistenceFailurePreventsAdmission()
    {
        string path = Path.Combine(Path.GetTempPath(), "airouter-prefill-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "broken");
            var clock = new VirtualClock();
            var limiter = Create(clock, path);
            Assert.Equal(0, limiter.Describe().UncachedTokensAvailable);
            Assert.NotNull(limiter.Describe().PrefillPersistenceError);
            using var first = await limiter.AcquireAsync(100, 100);
            Assert.True(clock.Now - clock.Start >= TimeSpan.FromSeconds(100));
            Assert.Null(limiter.Describe().PrefillPersistenceError);

            // An existing FILE in the parent-directory position makes persistence impossible.
            var unwritable = Create(clock, Path.Combine(path, "state.json"));
            await Assert.ThrowsAnyAsync<IOException>(() => unwritable.AcquireAsync(100, 100));
            Assert.Equal(0, unwritable.Describe().TotalAdmitted);
            Assert.Equal(0, unwritable.Describe().InFlight);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Fact]
    public async Task BurstLimitRejectsOversizedPromptsWithoutClampingTheirComputeReservation()
    {
        var limiter = Create(new VirtualClock());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => limiter.AcquireAsync(1500, 1500));
        Assert.Equal(0, limiter.Describe().TotalAdmitted);
        Assert.Equal(0, limiter.Describe().InFlight);
    }

    [Fact]
    public void ClockRollbackCannotRefillTheBucket()
    {
        var clock = new VirtualClock();
        var budget = new AIRouterPrefillBudget(3600, 1000, null);
        budget.Reserve(900, clock.Now);
        clock.Advance(TimeSpan.FromSeconds(-500));
        Assert.Equal(100, budget.Describe(clock.Now).AvailableTokens);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cached_tokens\":null}")]
    [InlineData("{\"audio_tokens\":12}")]
    public void AbsentOrNullCacheCountsRemainUnknown(string json)
    {
        var details = JsonConvert.DeserializeObject<HFWrapper.HFLLMInferenceResponse.PromptTokensDetails>(json)!;
        Assert.False(details.HasCacheReadMetrics);
    }

    [Fact]
    public async Task IncidentVolumeReplay_BacksOffMissesWhileHealthyReuseKeepsTheRequestedCadence()
    {
        async Task<(int Admitted, long Uncached)> Replay(double hitRate)
        {
            var clock = new VirtualClock();
            var limiter = new AIRouterFairUseLimiter(nowUtc: () => clock.Now, delay: clock.Delay);
            long uncached = 0;
            int admitted = 0;
            for (int i = 0; i < 208; i++)
            {
                var scheduled = clock.Start + TimeSpan.FromSeconds(i * 7200d / 208);
                if (clock.Now < scheduled) clock.Advance(scheduled - clock.Now);
                using var lease = await limiter.AcquireAsync(59000, 58000);
                if (clock.Now >= clock.Start + TimeSpan.FromHours(2)) break;
                long cached = i == 0 ? 0 : (long)(58000 * hitRate);
                lease.ReportActualTokens(59000);
                lease.ReportPromptTokens(58000, cached);
                admitted++;
                uncached += 58000 - cached;
            }
            return (admitted, uncached);
        }
        var incident = await Replay(.322);
        var healthy = await Replay(.9765);
        output.WriteLine($"Two-hour admission simulation at 208 requested calls, 58K prompt tokens, first call cold: " +
            $"32.2% cache -> {incident.Admitted} admitted / {incident.Uncached:N0} uncached; " +
            $"97.65% cache -> {healthy.Admitted} admitted / {healthy.Uncached:N0} uncached.");
        Assert.True(incident.Admitted < 20);
        Assert.True(incident.Uncached <= AIRouterPrefillBudget.DefaultBurstTokens + 2 * AIRouterPrefillBudget.DefaultTokensPerHour);
        Assert.Equal(208, healthy.Admitted);
    }

    private static AIRouterFairUseLimiter Create(VirtualClock clock, string? path = null) => new(
        nowUtc: () => clock.Now, delay: clock.Delay, uncachedTokensPerHour: 3600,
        uncachedBurstTokens: 1000, prefillStatePath: path);

    private sealed class VirtualClock
    {
        internal DateTime Start { get; } = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        internal DateTime Now { get; private set; } = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        internal void Advance(TimeSpan duration) => Now += duration;
        internal Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Advance(duration);
            return Task.CompletedTask;
        }
    }
}
