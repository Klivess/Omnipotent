using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectPromptCacheAnalyticsTests
{
    private static readonly DateTime Now =
        new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);

    private static AnalyticsRange Range => new()
    {
        Key = "7d",
        Label = "Last 7 days",
        FromUtc = Now.AddDays(-6).Date,
        ToUtc = Now,
        Bucket = "day",
    };

    [Fact]
    public void HealthyVerdict_RequiresAndUsesOnlyCurrentVersionEvidence()
    {
        // Every continuation reuses the entire preceding request. The raw ratio is lower because
        // the newly appended 1,500-token suffix cannot have been cached before its first use.
        var records = SequentialWake();
        var legacy = Record(100, 55_000, 0);
        legacy.PromptCacheTelemetryVersion = "projects-prefix-v1";
        records.Add(legacy);
        var legacyRouter = Record(101, 55_000, 0, provider: "AIRouter");
        legacyRouter.PromptCacheTelemetryVersion = "projects-prefix-v2";
        records.Add(legacyRouter);

        var result = ProjectPromptCacheAnalytics.Build(records, Range);

        Assert.Equal("healthy", result.Status);
        Assert.Equal(20, result.Requests);
        Assert.Equal(20, result.MeasuredRequests);
        Assert.Equal(1_385_000, result.PromptTokens);
        Assert.Equal(1_301_500, result.CachedTokens);
        Assert.Equal(94, result.CacheHitRatePct);
        Assert.False(result.MeetsCacheHitTarget);
        Assert.Equal(19, result.ReusablePrefixSamples);
        Assert.Equal(100, result.ReusablePrefixEfficiencyPct);
        Assert.True(result.MeetsReusablePrefixTarget);
        Assert.Equal(1, result.ZeroHitRequests);
        Assert.Equal(100, result.ProviderStabilityPct);
        Assert.Equal(0, result.ResponseCacheHits);
        Assert.NotEmpty(result.Series);
        Assert.Single(result.Breakdown);
        Assert.Equal(20, result.Recent.Count);
    }

    [Fact]
    public void ThirtyPercentPlateau_IsExplicitlyDegraded()
    {
        var records = Enumerable.Range(1, 20)
            .Select(index => Record(index, prompt: 55_000, cached: 16_500))
            .ToList();

        var result = ProjectPromptCacheAnalytics.Build(records, Range);

        Assert.Equal("degraded", result.Status);
        Assert.Equal(30, result.CacheHitRatePct);
        Assert.Contains("Uncached input remains above target", result.Verdict, StringComparison.Ordinal);
        Assert.Equal(770_000, result.UncachedTokens);
        Assert.Equal(30, result.ReusablePrefixEfficiencyPct);
        Assert.False(result.MeetsReusablePrefixTarget);
    }

    [Fact]
    public void ReleaseGate_UsesUnroundedReusablePrefixRatio()
    {
        var records = Enumerable.Range(1, 20)
            .Select(index => Record(index, prompt: 55_000, cached: 54_808))
            .ToList();

        var result = ProjectPromptCacheAnalytics.Build(records, Range);

        // 54,808 / 55,000 = 99.6509%, displayed as 99.7 but still below the real 99.7 gate.
        Assert.Equal(99.7, result.ReusablePrefixEfficiencyPct);
        Assert.False(result.MeetsReusablePrefixTarget);
        Assert.Equal("degraded", result.Status);
    }

    [Fact]
    public void MissingPromptTokenDetails_NeverMasqueradeAsZeroHitEvidence()
    {
        var records = Enumerable.Range(1, 20)
            .Select(index => Record(index, prompt: 55_000, cached: 0, metricsAvailable: false))
            .ToList();

        var result = ProjectPromptCacheAnalytics.Build(records, Range);

        Assert.Equal("warming", result.Status);
        Assert.Equal(20, result.Requests);
        Assert.Equal(0, result.MeasuredRequests);
        Assert.Equal(0, result.ZeroHitRequests);
        Assert.Equal(0, result.TelemetryCoveragePct);
    }

    [Fact]
    public void AIRouter_ReportsMeasuredHitsWithoutOpenRouterRoutingMetadata()
    {
        var records = SequentialWake();
        foreach (var record in records)
        {
            record.Provider = "AIRouter";
            record.RoutedProvider = null;
        }
        var result = ProjectPromptCacheAnalytics.Build(records, Range);
        Assert.Equal(20, result.MeasuredRequests);
        Assert.Equal(94, result.CacheHitRatePct);
        Assert.Equal("healthy", result.Status);
        Assert.Equal("AIRouter", Assert.Single(result.Breakdown).Provider);
    }

    [Fact]
    public void CacheEpochs_CompareAcrossWakesButNeverAcrossRebases()
    {
        var records = SequentialWake();
        foreach (var record in records)
        {
            record.WakeID = "wake-" + record.Sequence;
            record.CacheEpochID = record.Sequence <= 10 ? "epoch-1" : "epoch-2";
            record.CacheEpochTurnIndex = (int)((record.Sequence - 1) % 10) + 1;
            record.TurnIndex = 1;
        }
        var result = ProjectPromptCacheAnalytics.Build(records, Range);
        Assert.Equal(18, result.ReusablePrefixSamples);
        Assert.Equal(2, result.FirstTurnRequests);
        Assert.Equal(18, result.ContinuationRequests);
    }

    private static ProjectTokenUsageRecord Record(
        int index,
        long prompt,
        long cached,
        string provider = "OpenRouter",
        bool metricsAvailable = true)
        => new()
        {
            Sequence = index,
            UsageID = $"usage-{index}",
            ProjectID = "project-1",
            OccurredAt = Now.AddMinutes(-21 + index),
            WakeID = "wake-1",
            AgentID = "commander",
            Source = "commander",
            Operation = "wake-model-turn",
            Model = "qwen/qwen3.8",
            Provider = provider,
            RoutedProvider = "Together",
            CacheSessionID = "projects-commander-project-1",
            PromptCacheTelemetryVersion = ProjectPromptCacheTelemetry.CurrentVersion,
            CacheMetricsAvailable = metricsAvailable,
            TurnIndex = index,
            PromptTokens = prompt,
            CachedPromptTokens = cached,
            CompletionTokens = 100,
            RequestDurationMs = 2_000,
            GenerationID = $"gen-{index}",
        };

    private static List<ProjectTokenUsageRecord> SequentialWake()
    {
        var result = new List<ProjectTokenUsageRecord>();
        long previousPrompt = 0;
        for (int index = 1; index <= 20; index++)
        {
            long prompt = 55_000 + (index - 1) * 1_500;
            result.Add(Record(index, prompt, index == 1 ? 0 : previousPrompt));
            previousPrompt = prompt;
        }
        return result;
    }
}
