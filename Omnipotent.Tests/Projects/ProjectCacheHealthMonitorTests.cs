using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The fleet prompt-cache kill switch. These tests carry more weight than usual because the
/// failure they guard against is invisible in every other signal: a cache collapse returns correct
/// answers at normal latency, so nothing except this arithmetic can tell that it is happening —
/// and a false trip here stops the entire agent estate.
/// </summary>
public class ProjectCacheHealthMonitorTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "cache-health-tests", Guid.NewGuid().ToString("N"));

    private DateTime now = Start;

    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
        GC.SuppressFinalize(this);
    }

    private ProjectCacheHealthMonitor Monitor(ProjectCacheHealthOptions? options = null)
        => new(directory, log: _ => { }, clock: () => now, options: options ?? Options());

    private static ProjectCacheHealthOptions Options() => new()
    {
        Enabled = true,
        Window = TimeSpan.FromMinutes(20),
        MinimumWeightedHitRatePct = 80,
        MinimumMeasuredRequests = 20,
        MinimumMeasuredPromptTokens = 250_000,
        MinimumObservationSpan = TimeSpan.FromMinutes(5),
    };

    private ProjectTokenUsageRecord Record(
        long prompt,
        long cached,
        string projectID = "p1",
        string agentID = "commander",
        string model = "qwen3.8",
        bool measurable = true,
        int epochTurn = 2,
        DateTime? occurredAt = null)
        => new()
        {
            RecordKind = "usage",
            ProjectID = projectID,
            AgentID = agentID,
            Source = "commander",
            WakeID = "wake-1",
            CacheSessionID = $"{projectID}:{agentID}",
            CacheEpochID = "epoch-1",
            CacheEpochTurnIndex = epochTurn,
            TurnIndex = epochTurn,
            Model = model,
            Provider = "AIRouter",
            RoutedProvider = "AIRouter",
            PromptTokens = prompt,
            CachedPromptTokens = cached,
            CacheMetricsAvailable = measurable,
            PromptCacheTelemetryVersion = measurable
                ? ProjectPromptCacheTelemetry.CurrentVersion
                : "projects-prefix-v1",
            OccurredAt = occurredAt ?? now,
            UsageID = Guid.NewGuid().ToString("N"),
        };

    /// <summary>Feeds `count` requests spread evenly over `span`, advancing the clock as it goes.</summary>
    private void Feed(
        ProjectCacheHealthMonitor monitor,
        int count,
        long prompt,
        long cached,
        TimeSpan span,
        string projectID = "p1",
        string model = "qwen3.8",
        bool measurable = true)
    {
        for (int index = 0; index < count; index++)
        {
            now = Start + TimeSpan.FromTicks(span.Ticks * index / Math.Max(1, count - 1));
            monitor.Observe(Record(prompt, cached, projectID: projectID, model: model,
                measurable: measurable, epochTurn: index + 2));
        }
    }

    [Fact]
    public void CollapsedCache_HaltsTheFleetAndReportsTheEvidence()
    {
        var monitor = Monitor();
        ProjectCacheHealthVerdict? halted = null;
        IReadOnlyList<ProjectTokenUsageRecord>? window = null;
        monitor.HaltAction = (verdict, samples) =>
        {
            halted = verdict;
            window = samples;
            return Task.CompletedTask;
        };

        // The 2026-08-28 shape: 55K prompts served 30% from cache.
        Feed(monitor, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));

        Assert.True(SpinFor(() => halted != null));
        Assert.True(monitor.IsHalted);
        Assert.Equal(30, halted!.WeightedHitRatePct);
        Assert.Equal(80, halted.ThresholdPct);
        // It halts the instant the evidence is sufficient — the 20th request, not the 25th. Waiting
        // for a rounder sample would be five more full prefills bought for nothing.
        Assert.Equal(20, halted.MeasuredRequests);
        Assert.Equal(1_100_000, halted.PromptTokens);
        Assert.Equal(770_000, halted.UncachedTokens);
        Assert.Equal(20, window!.Count);
        Assert.Contains("below the 80% floor", halted.Summary, StringComparison.Ordinal);

        var state = monitor.GetState();
        Assert.True(state.Engaged);
        Assert.Equal(20, state.WindowMinutes);
        Assert.Equal(30, state.ObservedHitRatePct);
    }

    [Fact]
    public void HaltedFleet_RefusesAdmissionUntilCleared()
    {
        var monitor = Monitor();
        Assert.Null(monitor.AdmissionRefusal());

        Feed(monitor, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));

        string? refusal = monitor.AdmissionRefusal();
        Assert.NotNull(refusal);
        Assert.Contains("halted on prompt-cache health", refusal!, StringComparison.Ordinal);
        Assert.Contains("20 minutes", refusal, StringComparison.Ordinal);

        Assert.True(monitor.Clear("klives"));
        Assert.Null(monitor.AdmissionRefusal());
        Assert.False(monitor.IsHalted);
        Assert.Equal("klives", monitor.GetState().ClearedBy);
    }

    [Fact]
    public void ClearingDropsTheWindow_SoTheFleetDoesNotImmediatelyReHalt()
    {
        // Without this the offending samples are still inside the 20-minute window, the very next
        // request re-trips, and the clear reads to Klives as though it silently failed.
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        Feed(monitor, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));
        Assert.True(SpinFor(() => trips == 1));
        monitor.Clear("klives");

        now += TimeSpan.FromSeconds(30);
        monitor.Observe(Record(55_000, 16_500));

        Assert.False(monitor.IsHalted);
        Assert.Equal(1, trips);
        Assert.Equal(1, monitor.Describe().MeasuredRequests);
    }

    [Fact]
    public void TheTriggerIsTokenWeighted_NotAPerRequestMean()
    {
        // Nineteen tiny fully-cached requests against one huge mostly-uncached one. The per-request
        // mean says 96%; weighted by tokens it is 16%, which is the figure the provider actually
        // bills. Asserted on the window rather than on a halt: the weighted rate being below the
        // floor is necessary to stop the fleet but no longer sufficient, and this one huge request
        // continues nothing, so there is no prefix evidence to convict it with.
        var monitor = Monitor();

        for (int index = 0; index < 19; index++)
        {
            now = Start + TimeSpan.FromMinutes(index * 0.5);
            monitor.Observe(Record(1_000, 1_000, epochTurn: index + 2));
        }
        now = Start + TimeSpan.FromMinutes(11);
        monitor.Observe(Record(5_000_000, 800_000, epochTurn: 40));

        var verdict = monitor.Describe();
        Assert.Equal(16.3, verdict.WeightedHitRatePct, 1);
        Assert.Equal(95.8, verdict.UnweightedHitRatePct, 1);
        Assert.True(verdict.BelowThreshold);
    }

    [Fact]
    public void UnmeasuredRequests_AreExcludedRatherThanCountedAsMisses()
    {
        // A route that reports no cached_tokens reads as a perfect 0%. Counting those would halt the
        // fleet every time a non-reporting provider or a utility one-shot was used, which is the
        // false positive that would get the whole mechanism turned off.
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        Feed(monitor, 40, prompt: 55_000, cached: 0, span: TimeSpan.FromMinutes(12), measurable: false);

        var verdict = monitor.Describe();
        Assert.Equal(0, verdict.MeasuredRequests);
        Assert.Equal(40, verdict.UnmeasuredRequests);
        Assert.False(verdict.BelowThreshold);
        Assert.False(monitor.IsHalted);
        Assert.Equal(0, trips);
    }

    [Fact]
    public void ABurstOfColdStarts_DoesNotHaltTheFleet()
    {
        // Twenty conversations opening at once after a restart clear both volume floors within
        // seconds while being nothing but first turns — genuinely 0%, genuinely healthy.
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        for (int index = 0; index < 25; index++)
        {
            now = Start + TimeSpan.FromSeconds(index);
            monitor.Observe(Record(55_000, 0, epochTurn: 1));
        }

        Assert.False(monitor.IsHalted);
        Assert.Equal(0, trips);
        var verdict = monitor.Describe();
        Assert.True(verdict.BelowThreshold);
        Assert.False(verdict.ShouldHalt);
        Assert.Contains("halt is withheld", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("span only", verdict.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 2026-09-16 false positive, reproduced. The fleet ran for four hours halted on a 78.3%
    /// weighted rate while its prefixes were intact: every continuation resumed inside the cache
    /// lifetime hit, and the entire shortfall came from a handful of turns resumed long after the
    /// provider had dropped the prefix — idle agents and a hung browser call, neither of which
    /// stopping the fleet repairs.
    /// </summary>
    [Fact]
    public void ExpiredPrefixes_DragTheWeightedRateDown_ButDoNotHaltAHealthyFleet()
    {
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        // Twenty turns of ordinary work, 45s apart: warm, and reusing essentially all of the prefix.
        for (int index = 0; index < 20; index++)
        {
            now = Start + TimeSpan.FromSeconds(45 * index);
            monitor.Observe(Record(60_000, 59_500, epochTurn: index + 2, occurredAt: now));
        }

        // A second project whose commander went quiet for eight minutes mid-wake and came back to a
        // prefix the provider had already evicted. Big, and a total miss — as it must be.
        now = Start + TimeSpan.FromSeconds(45 * 19);
        monitor.Observe(Record(300_000, 299_000, projectID: "p2", epochTurn: 2,
            occurredAt: Start + TimeSpan.FromMinutes(6)));
        monitor.Observe(Record(600_000, 0, projectID: "p2", epochTurn: 3,
            occurredAt: Start + TimeSpan.FromMinutes(14)));

        var verdict = monitor.Describe();
        Assert.True(verdict.BelowThreshold);                      // 70.9% — well under the 80% floor
        Assert.False(verdict.ShouldHalt);
        Assert.False(monitor.IsHalted);
        Assert.Equal(0, trips);

        // The expired pair is excluded from the correctness figure and reported on its own.
        Assert.Equal(1, verdict.ExpiredPrefixSamples);
        Assert.Equal(300_000, verdict.ExpiredPrefixTokens);
        Assert.Equal(19, verdict.ReusablePrefixSamples);
        Assert.True(verdict.ReusablePrefixEfficiencyPct > 99);
        Assert.False(verdict.PrefixEfficiencyBelowThreshold);
        Assert.Contains("the prefix is intact", verdict.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same rule: once continuations start missing INSIDE the cache lifetime,
    /// this is the 2026-08-28 collapse and the fleet must still stop.
    /// </summary>
    [Fact]
    public void WarmContinuationsThatMiss_StillHaltTheFleet()
    {
        var monitor = Monitor();
        ProjectCacheHealthVerdict? halted = null;
        monitor.HaltAction = (verdict, _) => { halted = verdict; return Task.CompletedTask; };

        // Same cadence as above — 45 seconds apart, nowhere near expiry — but the provider is
        // serving almost none of the prefix back.
        for (int index = 0; index < 22; index++)
        {
            now = Start + TimeSpan.FromSeconds(45 * index);
            monitor.Observe(Record(60_000, 18_000, epochTurn: index + 2, occurredAt: now));
        }

        Assert.True(SpinFor(() => halted != null));
        Assert.True(halted!.PrefixEfficiencyBelowThreshold);
        Assert.Equal(0, halted.ExpiredPrefixSamples);
        Assert.Contains("Warm prefixes are being lost", halted.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// With too little live evidence the halt is withheld rather than falling back to the weighted
    /// rate, which would put the false positive straight back in the thinnest windows.
    /// </summary>
    [Fact]
    public void TooFewLiveContinuations_WithholdsTheHaltAndSaysWhy()
    {
        var monitor = Monitor();
        // Twenty-five first turns: plenty of volume and span, but nothing continues anything, so
        // there is no prefix to judge.
        for (int index = 0; index < 25; index++)
        {
            now = Start + TimeSpan.FromSeconds(index * 30);
            monitor.Observe(Record(55_000, 0, epochTurn: 1, occurredAt: now));
        }

        var verdict = monitor.Describe();
        Assert.True(verdict.BelowThreshold);
        Assert.False(verdict.ShouldHalt);
        Assert.Contains("live continuations to judge prefix health by",
            verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewMeasuredRequests_WithholdsTheHaltAndSaysWhy()
    {
        var monitor = Monitor();
        Feed(monitor, 8, prompt: 55_000, cached: 0, span: TimeSpan.FromMinutes(12));

        var verdict = monitor.Describe();
        Assert.False(verdict.ShouldHalt);
        Assert.Contains("only 8 of the required 20 measured requests", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewPromptTokens_WithholdsTheHalt()
    {
        var monitor = Monitor();
        // Clears the request and span floors, but 25 × 1,000 tokens says nothing about prefill spend.
        Feed(monitor, 25, prompt: 1_000, cached: 0, span: TimeSpan.FromMinutes(12));

        var verdict = monitor.Describe();
        Assert.False(verdict.ShouldHalt);
        Assert.Contains("measured prompt tokens", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AHealthyFleet_IsLeftAlone()
    {
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        Feed(monitor, 40, prompt: 55_000, cached: 53_000, span: TimeSpan.FromMinutes(15));

        Assert.False(monitor.IsHalted);
        Assert.Equal(0, trips);
        var verdict = monitor.Describe();
        Assert.Equal(96.4, verdict.WeightedHitRatePct, 1);
        Assert.Contains("at or above the 80% floor", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheTrailingWindowCounts()
    {
        // An hour-old collapse that has since recovered must not hold the fleet down; the window is
        // the whole point of measuring a rate rather than a running total.
        var monitor = Monitor();
        Feed(monitor, 25, prompt: 55_000, cached: 0, span: TimeSpan.FromMinutes(12));
        monitor.Clear("klives");

        now = Start + TimeSpan.FromHours(2);
        monitor.Observe(Record(55_000, 54_000));

        var verdict = monitor.Describe();
        Assert.Equal(1, verdict.MeasuredRequests);
        Assert.Equal(98.2, verdict.WeightedHitRatePct, 1);
    }

    [Fact]
    public void TheWindowLengthIsConfigurable()
    {
        // The window is an OmniSetting; a five-minute one must actually forget at five minutes.
        var options = Options();
        options.Window = TimeSpan.FromMinutes(5);
        options.MinimumObservationSpan = TimeSpan.FromMinutes(1);
        var monitor = Monitor(options);

        Feed(monitor, 25, prompt: 55_000, cached: 54_000, span: TimeSpan.FromMinutes(4));
        Assert.Equal(25, monitor.Describe().MeasuredRequests);

        now += TimeSpan.FromMinutes(6);
        Assert.Equal(0, monitor.Describe().MeasuredRequests);
    }

    [Fact]
    public void OnlyOneHaltIsDispatchedPerTrip()
    {
        var monitor = Monitor();
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        Feed(monitor, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));
        Assert.True(SpinFor(() => trips == 1));
        for (int index = 0; index < 10; index++)
        {
            now += TimeSpan.FromSeconds(5);
            monitor.Observe(Record(55_000, 0));
        }

        Assert.Equal(1, trips);
    }

    [Fact]
    public void DisabledByOmniSetting_MeasuresButNeverHalts()
    {
        var options = Options();
        options.Enabled = false;
        var monitor = Monitor(options);
        int trips = 0;
        monitor.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };

        Feed(monitor, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));

        Assert.False(monitor.IsHalted);
        Assert.Equal(0, trips);
        var verdict = monitor.Describe();
        Assert.Equal(30, verdict.WeightedHitRatePct);
        Assert.Contains("disabled", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngagedHaltSurvivesARestart_WithoutRe_alerting()
    {
        var first = Monitor();
        int trips = 0;
        first.HaltAction = (_, _) => { trips++; return Task.CompletedTask; };
        Feed(first, 25, prompt: 55_000, cached: 16_500, span: TimeSpan.FromMinutes(12));
        Assert.True(SpinFor(() => trips == 1));
        first.NoteHaltOutcome(new[] { "p1" }, "report.md", "requests.jsonl", alertDelivered: true, alertError: null);

        // A restart must not let the fleet resume spending on a still-open incident, and must not
        // DM Klives a second copy of an alert he already has.
        var restarted = Monitor();
        Assert.True(restarted.IsHalted);
        Assert.NotNull(restarted.AdmissionRefusal());
        var state = restarted.GetState();
        Assert.Equal(30, state.ObservedHitRatePct);
        Assert.Equal(new[] { "p1" }, state.HaltedProjectIDs);
        Assert.True(state.AlertDelivered);

        Assert.True(restarted.Clear("klives"));
        Assert.False(Monitor().IsHalted);
    }

    [Fact]
    public void ReconciliationRows_AreNotSamples()
    {
        // Cost adjustments re-book an already-counted request and carry no tokens of their own.
        var monitor = Monitor();
        var adjustment = Record(0, 0);
        adjustment.RecordKind = "cost-adjustment";
        monitor.Observe(adjustment);

        Assert.Equal(0, monitor.Describe().MeasuredRequests);
    }

    [Fact]
    public void TheReportNamesTheWorstOffendersAndEveryRequest()
    {
        var monitor = Monitor();
        ProjectCacheHealthVerdict? verdict = null;
        IReadOnlyList<ProjectTokenUsageRecord>? window = null;
        monitor.HaltAction = (v, samples) => { verdict = v; window = samples; return Task.CompletedTask; };

        Feed(monitor, 14, prompt: 60_000, cached: 0, span: TimeSpan.FromMinutes(10),
            projectID: "broken", model: "qwen3.8");
        Feed(monitor, 14, prompt: 60_000, cached: 59_000, span: TimeSpan.FromMinutes(10),
            projectID: "healthy", model: "gpt-oss");

        Assert.True(SpinFor(() => verdict != null));
        string report = ProjectCacheHealthMonitor.BuildReport(
            verdict!, window!, monitor.Options, new[] { "broken", "healthy" });

        Assert.Contains("# Projects fleet halted", report, StringComparison.Ordinal);
        Assert.Contains("broken", report, StringComparison.Ordinal);
        Assert.Contains("healthy", report, StringComparison.Ordinal);
        Assert.Contains("qwen3.8", report, StringComparison.Ordinal);
        Assert.Contains("Largest zero-hit requests", report, StringComparison.Ordinal);
        Assert.Contains("/projects/cache-health/clear", report, StringComparison.Ordinal);
        // Every measured request has to be individually accounted for — that is the "comprehensive
        // request data" the alert promises.
        foreach (var record in window!)
            Assert.Contains(record.UsageID, report, StringComparison.Ordinal);

        string jsonl = ProjectCacheHealthMonitor.BuildRequestLog(window!);
        Assert.Equal(window!.Count, jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        foreach (var record in window!)
            Assert.Contains(record.UsageID, jsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupingRanksByUncachedTokens_NotByTheUgliestPercentage()
    {
        var records = new List<ProjectTokenUsageRecord>();
        // A tiny 0% offender against a large one that is losing far more actual prefill.
        records.Add(Record(1_000, 0, projectID: "tiny"));
        for (int index = 0; index < 10; index++)
            records.Add(Record(100_000, 50_000, projectID: "large", epochTurn: index + 2));

        var groups = ProjectCacheHealthMonitor.Group(records, record => record.ProjectID);

        Assert.Equal("large", groups[0].Key);
        Assert.Equal(500_000, groups[0].UncachedTokens);
        Assert.Equal(50, groups[0].WeightedHitRatePct);
        Assert.Equal("tiny", groups[1].Key);
        Assert.Equal(0, groups[1].WeightedHitRatePct);
    }

    /// <summary>The halt action is dispatched off the observing thread on purpose (it cancels the
    /// very wake that reported the sample), so the assertions have to wait for it.</summary>
    private static bool SpinFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }
}
