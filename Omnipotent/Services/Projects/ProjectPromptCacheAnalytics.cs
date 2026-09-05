using System.Globalization;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Provider-reported prompt-cache measurements. Only rows written by the current
/// prefix contract are included, so old records with an ambiguous zero cannot make a fixed build
/// look healthy or broken.
/// </summary>
public sealed class AnalyticsPromptCacheSnapshot
{
    public string TelemetryVersion { get; set; } = ProjectPromptCacheTelemetry.CurrentVersion;
    public string Status { get; set; } = "no-data";
    public string Verdict { get; set; } = "No post-fix requests have been measured yet.";
    public DateTime? MeasurementStartedAt { get; set; }
    public DateTime? LastMeasuredAt { get; set; }
    public int Requests { get; set; }
    public int MeasuredRequests { get; set; }
    public int HitRequests { get; set; }
    public int ZeroHitRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public double CacheHitRatePct { get; set; }
    public double TargetCacheHitRatePct { get; set; } = 99.7;
    public bool MeetsCacheHitTarget { get; set; }
    public long TargetUncachedTokenBudget { get; set; }
    public long ExcessUncachedTokens { get; set; }
    public int ReusablePrefixSamples { get; set; }
    public long ReusablePrefixTokens { get; set; }
    public long ReusedPrefixTokens { get; set; }
    public double ReusablePrefixEfficiencyPct { get; set; }
    public double TargetReusablePrefixEfficiencyPct { get; set; } = 99.7;
    public bool MeetsReusablePrefixTarget { get; set; }
    public double ZeroHitRatePct { get; set; }
    public double TelemetryCoveragePct { get; set; }
    public long AveragePromptTokens { get; set; }
    public long AverageUncachedTokens { get; set; }
    public long AverageRequestDurationMs { get; set; }
    public long TotalRequestDurationMs { get; set; }
    public int FirstTurnRequests { get; set; }
    public long FirstTurnPromptTokens { get; set; }
    public long FirstTurnCachedTokens { get; set; }
    public double FirstTurnHitRatePct { get; set; }
    public int ContinuationRequests { get; set; }
    public long ContinuationPromptTokens { get; set; }
    public long ContinuationCachedTokens { get; set; }
    public double ContinuationHitRatePct { get; set; }
    public int CompactedRequests { get; set; }
    public double CompactionRatePct { get; set; }
    public int RoutedProviderSamples { get; set; }
    public double RoutedProviderCoveragePct { get; set; }
    public int ProviderComparisons { get; set; }
    public int ProviderSwitches { get; set; }
    public double ProviderStabilityPct { get; set; }
    public int ResponseCacheHits { get; set; }
    public List<AnalyticsPromptCacheSeriesPoint> Series { get; set; } = new();
    public List<AnalyticsPromptCacheBreakdown> Breakdown { get; set; } = new();
    public List<AnalyticsPromptCacheSample> Recent { get; set; } = new();
}

public sealed class AnalyticsPromptCacheSeriesPoint
{
    public string Date { get; set; } = "";
    public int Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public double CacheHitRatePct { get; set; }
    public long AverageRequestDurationMs { get; set; }
    public long TotalRequestDurationMs { get; set; }
}

public sealed class AnalyticsPromptCacheBreakdown
{
    public string Key { get; set; } = "";
    public string Provider { get; set; } = "unknown";
    public string Model { get; set; } = "unknown";
    public string Source { get; set; } = "unknown";
    public int Requests { get; set; }
    public int ZeroHitRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public double CacheHitRatePct { get; set; }
    public double ZeroHitRatePct { get; set; }
    public long AverageRequestDurationMs { get; set; }
    public long TotalRequestDurationMs { get; set; }
}

public sealed class AnalyticsPromptCacheSample
{
    public DateTime OccurredAt { get; set; }
    public string ProjectID { get; set; } = "";
    public string? WakeID { get; set; }
    public string AgentID { get; set; } = "system";
    public string Source { get; set; } = "unknown";
    public int TurnIndex { get; set; }
    public int CacheEpochTurnIndex { get; set; }
    public string? PromptAssemblyStatus { get; set; }
    public int AppendedBriefTokens { get; set; }
    public int FullBriefTokens { get; set; }
    public string Model { get; set; } = "unknown";
    public string Provider { get; set; } = "unknown";
    public string? RoutedProvider { get; set; }
    public string? RouterStrategy { get; set; }
    public int? RouterAttempt { get; set; }
    public string? GenerationID { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public double CacheHitRatePct { get; set; }
    public long RequestDurationMs { get; set; }
    public bool ContextWasCompacted { get; set; }
    public string? ResponseCacheStatus { get; set; }
}

internal static class ProjectPromptCacheAnalytics
{
    private const int MinimumReadyRequests = 20;
    private const long MinimumReadyPromptTokens = 1_000_000;
    private const int MinimumReusablePrefixSamples = 10;
    internal const double TargetCacheHitRatePct = 99.7;

    internal static AnalyticsPromptCacheSnapshot Build(
        IEnumerable<ProjectTokenUsageRecord> usage,
        AnalyticsRange range)
    {
        var eligible = usage
            .Where(IsEligible)
            .OrderBy(record => record.OccurredAt)
            .ThenBy(record => record.Sequence)
            .ToList();
        var measured = eligible.Where(record => record.CacheMetricsAvailable).ToList();
        var result = new AnalyticsPromptCacheSnapshot
        {
            Requests = eligible.Count,
            MeasuredRequests = measured.Count,
            MeasurementStartedAt = eligible.Select(record => (DateTime?)record.OccurredAt.ToUniversalTime()).FirstOrDefault(),
            LastMeasuredAt = eligible.Select(record => (DateTime?)record.OccurredAt.ToUniversalTime()).LastOrDefault(),
            Series = CreateSeries(range),
        };
        var seriesByDate = result.Series.ToDictionary(point => point.Date, StringComparer.Ordinal);

        foreach (var record in measured)
        {
            long prompt = Math.Max(0, record.PromptTokens);
            long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
            long uncached = prompt - cached;
            result.PromptTokens += prompt;
            result.CachedTokens += cached;
            result.UncachedTokens += uncached;
            result.CacheWriteTokens += Math.Max(0, record.CacheWritePromptTokens);
            result.TotalRequestDurationMs += Math.Max(0, record.RequestDurationMs);
            if (cached > 0) result.HitRequests++;
            else result.ZeroHitRequests++;
            if (record.ContextWasCompacted) result.CompactedRequests++;
            if (!string.IsNullOrWhiteSpace(RouteProvider(record))) result.RoutedProviderSamples++;
            if (string.Equals(record.ResponseCacheStatus, "HIT", StringComparison.OrdinalIgnoreCase))
                result.ResponseCacheHits++;

            if (EpochTurn(record) <= 1)
            {
                result.FirstTurnRequests++;
                result.FirstTurnPromptTokens += prompt;
                result.FirstTurnCachedTokens += cached;
            }
            else
            {
                result.ContinuationRequests++;
                result.ContinuationPromptTokens += prompt;
                result.ContinuationCachedTokens += cached;
            }

            string key = BucketKey(record.OccurredAt, range.Bucket);
            if (seriesByDate.TryGetValue(key, out var point))
            {
                point.Requests++;
                point.PromptTokens += prompt;
                point.CachedTokens += cached;
                point.UncachedTokens += uncached;
                point.TotalRequestDurationMs += Math.Max(0, record.RequestDurationMs);
            }
        }

        AddProviderTransitions(result, measured);
        AddReusablePrefixMeasurements(result, measured);
        result.Breakdown = measured
            .GroupBy(record => new
            {
                Provider = Clean(RouteProvider(record), "unknown"),
                Model = Clean(record.Model, "unknown"),
                Source = Clean(record.Source, "unknown"),
            })
            .Select(group => BuildBreakdown(group.Key.Provider, group.Key.Model, group.Key.Source, group))
            .OrderByDescending(item => item.PromptTokens)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
        result.Recent = measured
            .OrderByDescending(record => record.OccurredAt)
            .ThenByDescending(record => record.Sequence)
            .Take(50)
            .Select(ToSample)
            .ToList();

        Finish(result);
        return result;
    }

    internal static AnalyticsPromptCacheSnapshot Aggregate(
        IEnumerable<AnalyticsPromptCacheSnapshot> source,
        AnalyticsRange range)
    {
        var snapshots = source.ToList();
        var result = new AnalyticsPromptCacheSnapshot
        {
            Requests = snapshots.Sum(item => item.Requests),
            MeasuredRequests = snapshots.Sum(item => item.MeasuredRequests),
            HitRequests = snapshots.Sum(item => item.HitRequests),
            ZeroHitRequests = snapshots.Sum(item => item.ZeroHitRequests),
            PromptTokens = snapshots.Sum(item => item.PromptTokens),
            CachedTokens = snapshots.Sum(item => item.CachedTokens),
            UncachedTokens = snapshots.Sum(item => item.UncachedTokens),
            CacheWriteTokens = snapshots.Sum(item => item.CacheWriteTokens),
            ReusablePrefixSamples = snapshots.Sum(item => item.ReusablePrefixSamples),
            ReusablePrefixTokens = snapshots.Sum(item => item.ReusablePrefixTokens),
            ReusedPrefixTokens = snapshots.Sum(item => item.ReusedPrefixTokens),
            TotalRequestDurationMs = snapshots.Sum(item => item.TotalRequestDurationMs),
            FirstTurnRequests = snapshots.Sum(item => item.FirstTurnRequests),
            FirstTurnPromptTokens = snapshots.Sum(item => item.FirstTurnPromptTokens),
            FirstTurnCachedTokens = snapshots.Sum(item => item.FirstTurnCachedTokens),
            ContinuationRequests = snapshots.Sum(item => item.ContinuationRequests),
            ContinuationPromptTokens = snapshots.Sum(item => item.ContinuationPromptTokens),
            ContinuationCachedTokens = snapshots.Sum(item => item.ContinuationCachedTokens),
            CompactedRequests = snapshots.Sum(item => item.CompactedRequests),
            RoutedProviderSamples = snapshots.Sum(item => item.RoutedProviderSamples),
            ProviderComparisons = snapshots.Sum(item => item.ProviderComparisons),
            ProviderSwitches = snapshots.Sum(item => item.ProviderSwitches),
            ResponseCacheHits = snapshots.Sum(item => item.ResponseCacheHits),
            MeasurementStartedAt = snapshots.Select(item => item.MeasurementStartedAt)
                .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Min() is var min && min != default ? min : null,
            LastMeasuredAt = snapshots.Select(item => item.LastMeasuredAt)
                .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max() is var max && max != default ? max : null,
            Series = CreateSeries(range),
        };

        var seriesByDate = result.Series.ToDictionary(item => item.Date, StringComparer.Ordinal);
        foreach (var point in snapshots.SelectMany(item => item.Series))
        {
            if (!seriesByDate.TryGetValue(point.Date, out var target)) continue;
            target.Requests += point.Requests;
            target.PromptTokens += point.PromptTokens;
            target.CachedTokens += point.CachedTokens;
            target.UncachedTokens += point.UncachedTokens;
            target.TotalRequestDurationMs += point.TotalRequestDurationMs;
        }

        result.Breakdown = snapshots.SelectMany(item => item.Breakdown)
            .GroupBy(item => new { item.Provider, item.Model, item.Source })
            .Select(group =>
            {
                var merged = new AnalyticsPromptCacheBreakdown
                {
                    Provider = group.Key.Provider,
                    Model = group.Key.Model,
                    Source = group.Key.Source,
                    Requests = group.Sum(item => item.Requests),
                    ZeroHitRequests = group.Sum(item => item.ZeroHitRequests),
                    PromptTokens = group.Sum(item => item.PromptTokens),
                    CachedTokens = group.Sum(item => item.CachedTokens),
                    UncachedTokens = group.Sum(item => item.UncachedTokens),
                    TotalRequestDurationMs = group.Sum(item => item.TotalRequestDurationMs),
                };
                FinishBreakdown(merged);
                return merged;
            })
            .OrderByDescending(item => item.PromptTokens)
            .ToList();
        result.Recent = snapshots.SelectMany(item => item.Recent)
            .OrderByDescending(item => item.OccurredAt)
            .Take(50)
            .ToList();

        Finish(result);
        return result;
    }

    private static int EpochTurn(ProjectTokenUsageRecord record) =>
        record.CacheEpochTurnIndex > 0 ? record.CacheEpochTurnIndex : record.TurnIndex;

    // Direct endpoints do not expose OpenRouter's routing metadata. Their configured provider is
    // the endpoint identity; missing OpenRouter metadata must still fail the routing coverage gate.
    private static string? RouteProvider(ProjectTokenUsageRecord record) =>
        !string.IsNullOrWhiteSpace(record.RoutedProvider) ? record.RoutedProvider
        : string.Equals(record.Provider, "OpenRouter", StringComparison.OrdinalIgnoreCase) ? null : record.Provider;

    private static bool IsEligible(ProjectTokenUsageRecord record)
        => !string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)
            && record.PromptTokens > 0
            && !string.IsNullOrWhiteSpace(record.Provider)
            && string.Equals(record.PromptCacheTelemetryVersion,
                ProjectPromptCacheTelemetry.CurrentVersion, StringComparison.Ordinal);

    private static void AddProviderTransitions(
        AnalyticsPromptCacheSnapshot result,
        IReadOnlyList<ProjectTokenUsageRecord> records)
    {
        foreach (var session in records
            .Where(record => !string.IsNullOrWhiteSpace(record.CacheSessionID))
            .GroupBy(record => record.CacheSessionID!, StringComparer.Ordinal))
        {
            string? previous = null;
            foreach (var record in session.OrderBy(item => item.OccurredAt).ThenBy(item => item.Sequence))
            {
                if (string.IsNullOrWhiteSpace(RouteProvider(record))) continue;
                if (previous != null)
                {
                    result.ProviderComparisons++;
                    if (!string.Equals(previous, RouteProvider(record), StringComparison.OrdinalIgnoreCase))
                        result.ProviderSwitches++;
                }
                previous = RouteProvider(record);
            }
        }
    }

    private static void AddReusablePrefixMeasurements(
        AnalyticsPromptCacheSnapshot result,
        IReadOnlyList<ProjectTokenUsageRecord> records)
    {
        // The raw cached/prompt ratio has an unavoidable ceiling: every new assistant/tool suffix
        // is being seen for the first time. For a correctness test, compare the provider's cache read
        // against the preceding request that should be an exact prefix of this continuation.
        foreach (var wake in records
            .Where(record => !string.IsNullOrWhiteSpace(record.CacheSessionID)
                && !string.IsNullOrWhiteSpace(record.WakeID))
            .GroupBy(record => new
            {
                record.ProjectID,
                record.CacheSessionID,
                Epoch = record.CacheEpochID ?? record.WakeID,
                record.AgentID,
                record.Model,
                record.Provider,
            }))
        {
            ProjectTokenUsageRecord? previous = null;
            foreach (var record in wake.OrderBy(EpochTurn)
                .ThenBy(item => item.OccurredAt).ThenBy(item => item.Sequence))
            {
                if (previous != null
                    && EpochTurn(record) > 1
                    && EpochTurn(record) == EpochTurn(previous) + 1
                    && !record.ContextWasCompacted)
                {
                    long currentPrompt = Math.Max(0, record.PromptTokens);
                    long reusable = Math.Min(Math.Max(0, previous.PromptTokens), currentPrompt);
                    if (reusable > 0)
                    {
                        long cached = Math.Clamp(record.CachedPromptTokens, 0, currentPrompt);
                        result.ReusablePrefixSamples++;
                        result.ReusablePrefixTokens += reusable;
                        result.ReusedPrefixTokens += Math.Min(cached, reusable);
                    }
                }
                previous = record;
            }
        }
    }

    private static AnalyticsPromptCacheBreakdown BuildBreakdown(
        string provider,
        string model,
        string source,
        IEnumerable<ProjectTokenUsageRecord> records)
    {
        var result = new AnalyticsPromptCacheBreakdown
        {
            Provider = provider,
            Model = model,
            Source = source,
        };
        foreach (var record in records)
        {
            long prompt = Math.Max(0, record.PromptTokens);
            long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
            result.Requests++;
            if (cached == 0) result.ZeroHitRequests++;
            result.PromptTokens += prompt;
            result.CachedTokens += cached;
            result.UncachedTokens += prompt - cached;
            result.TotalRequestDurationMs += Math.Max(0, record.RequestDurationMs);
        }
        FinishBreakdown(result);
        return result;
    }

    private static void FinishBreakdown(AnalyticsPromptCacheBreakdown result)
    {
        result.Key = $"{result.Provider}|{result.Model}|{result.Source}";
        result.CacheHitRatePct = Percent(result.CachedTokens, result.PromptTokens);
        result.ZeroHitRatePct = Percent(result.ZeroHitRequests, result.Requests);
        result.AverageRequestDurationMs = result.Requests > 0
            ? result.TotalRequestDurationMs / result.Requests
            : 0;
    }

    private static AnalyticsPromptCacheSample ToSample(ProjectTokenUsageRecord record)
    {
        long prompt = Math.Max(0, record.PromptTokens);
        long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
        return new AnalyticsPromptCacheSample
        {
            OccurredAt = record.OccurredAt.ToUniversalTime(),
            ProjectID = record.ProjectID,
            WakeID = record.WakeID,
            AgentID = record.AgentID,
            Source = record.Source,
            TurnIndex = record.TurnIndex,
            CacheEpochTurnIndex = record.CacheEpochTurnIndex,
            PromptAssemblyStatus = record.PromptAssemblyStatus,
            AppendedBriefTokens = record.AppendedBriefTokens,
            FullBriefTokens = record.FullBriefTokens,
            Model = record.Model,
            Provider = record.Provider ?? "unknown",
            RoutedProvider = record.RoutedProvider,
            RouterStrategy = record.RouterStrategy,
            RouterAttempt = record.RouterAttempt,
            GenerationID = record.GenerationID,
            PromptTokens = prompt,
            CachedTokens = cached,
            UncachedTokens = prompt - cached,
            CacheWriteTokens = Math.Max(0, record.CacheWritePromptTokens),
            CacheHitRatePct = Percent(cached, prompt),
            RequestDurationMs = Math.Max(0, record.RequestDurationMs),
            ContextWasCompacted = record.ContextWasCompacted,
            ResponseCacheStatus = record.ResponseCacheStatus,
        };
    }

    private static void Finish(AnalyticsPromptCacheSnapshot result)
    {
        result.CacheHitRatePct = Percent(result.CachedTokens, result.PromptTokens);
        result.TargetCacheHitRatePct = TargetCacheHitRatePct;
        result.TargetUncachedTokenBudget = TargetUncachedTokenBudget(result.PromptTokens);
        result.ExcessUncachedTokens = Math.Max(
            0, result.UncachedTokens - result.TargetUncachedTokenBudget);
        result.MeetsCacheHitTarget = MeetsCacheHitTarget(
            result.CachedTokens, result.PromptTokens);
        result.TargetReusablePrefixEfficiencyPct = TargetCacheHitRatePct;
        result.ReusablePrefixEfficiencyPct = Percent(
            result.ReusedPrefixTokens, result.ReusablePrefixTokens);
        result.MeetsReusablePrefixTarget = MeetsCacheHitTarget(
            result.ReusedPrefixTokens, result.ReusablePrefixTokens);
        result.ZeroHitRatePct = Percent(result.ZeroHitRequests, result.MeasuredRequests);
        result.TelemetryCoveragePct = Percent(result.MeasuredRequests, result.Requests);
        result.FirstTurnHitRatePct = Percent(result.FirstTurnCachedTokens, result.FirstTurnPromptTokens);
        result.ContinuationHitRatePct = Percent(result.ContinuationCachedTokens, result.ContinuationPromptTokens);
        result.CompactionRatePct = Percent(result.CompactedRequests, result.MeasuredRequests);
        result.RoutedProviderCoveragePct = Percent(result.RoutedProviderSamples, result.MeasuredRequests);
        result.ProviderStabilityPct = result.ProviderComparisons > 0
            ? Percent(result.ProviderComparisons - result.ProviderSwitches, result.ProviderComparisons)
            : result.RoutedProviderSamples > 0 ? 100 : 0;
        result.AveragePromptTokens = result.MeasuredRequests > 0
            ? result.PromptTokens / result.MeasuredRequests
            : 0;
        result.AverageUncachedTokens = result.MeasuredRequests > 0
            ? result.UncachedTokens / result.MeasuredRequests
            : 0;
        result.AverageRequestDurationMs = result.MeasuredRequests > 0
            ? result.TotalRequestDurationMs / result.MeasuredRequests
            : 0;

        foreach (var point in result.Series)
        {
            point.CacheHitRatePct = Percent(point.CachedTokens, point.PromptTokens);
            point.AverageRequestDurationMs = point.Requests > 0
                ? point.TotalRequestDurationMs / point.Requests
                : 0;
        }

        if (result.Requests == 0)
        {
            result.Status = "no-data";
            result.Verdict = "No post-fix Projects requests are in this range yet.";
            return;
        }
        if (result.MeasuredRequests < MinimumReadyRequests
            || result.PromptTokens < MinimumReadyPromptTokens
            || result.ReusablePrefixSamples < MinimumReusablePrefixSamples)
        {
            result.Status = "warming";
            result.Verdict = $"Collecting a clean sample: {result.MeasuredRequests}/{MinimumReadyRequests} measured requests, {result.PromptTokens:N0}/{MinimumReadyPromptTokens:N0} prompt tokens, and {result.ReusablePrefixSamples}/{MinimumReusablePrefixSamples} comparable continuation prefixes.";
            return;
        }

        bool providerHealthy = result.RoutedProviderCoveragePct >= 95
            && result.ProviderStabilityPct >= 95;
        bool healthy = result.TelemetryCoveragePct >= 95
            && result.MeetsReusablePrefixTarget
            && providerHealthy
            && result.ResponseCacheHits == 0;
        result.Status = healthy ? "healthy" : "degraded";
        result.Verdict = healthy
            ? $"The provider served {result.ReusablePrefixEfficiencyPct:0.0}% of the known reusable prefix (99.7% target). The raw whole-prompt hit is {result.CacheHitRatePct:0.0}% because each turn's new suffix cannot be cached on first use."
            : $"Uncached input remains above target: reusable-prefix efficiency is {result.ReusablePrefixEfficiencyPct:0.0}% against the 99.7% target, or another telemetry/routing gate is below target.";
    }

    private static List<AnalyticsPromptCacheSeriesPoint> CreateSeries(AnalyticsRange range)
    {
        var result = new List<AnalyticsPromptCacheSeriesPoint>();
        DateTime cursor = BucketStart(range.FromUtc, range.Bucket);
        DateTime end = BucketStart(range.ToUtc, range.Bucket);
        while (cursor <= end)
        {
            result.Add(new AnalyticsPromptCacheSeriesPoint { Date = BucketKey(cursor, range.Bucket) });
            cursor = range.Bucket switch
            {
                "month" => cursor.AddMonths(1),
                "week" => cursor.AddDays(7),
                _ => cursor.AddDays(1),
            };
        }
        return result;
    }

    private static DateTime BucketStart(DateTime timestamp, string bucket)
    {
        DateTime date = timestamp.ToUniversalTime().Date;
        if (bucket == "month")
            return new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (bucket == "week") return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return date;
    }

    private static string BucketKey(DateTime timestamp, string bucket)
        => BucketStart(timestamp, bucket).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double Percent(long numerator, long denominator)
        => denominator > 0 ? Math.Round(numerator * 100.0 / denominator, 1) : 0;

    private static bool MeetsCacheHitTarget(long cachedTokens, long promptTokens)
        // Use decimal and the unrounded counters for the release decision. The displayed one-decimal
        // percentage must never round a sub-target result up into a false pass.
        => promptTokens > 0
            && (decimal)Math.Clamp(cachedTokens, 0, promptTokens) * 1000m
                >= (decimal)promptTokens * 997m;

    private static long TargetUncachedTokenBudget(long promptTokens)
        // 99.7% cached leaves at most 0.3% uncached. Floor so the budget itself can never relax
        // the target through integer rounding.
        => promptTokens > 0 ? (long)Math.Floor((decimal)promptTokens * 3m / 1000m) : 0;

    private static string Clean(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
