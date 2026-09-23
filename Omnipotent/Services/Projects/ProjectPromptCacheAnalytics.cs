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
    /// <summary>Continuations resumed after the assumed prefix lifetime, so excluded from the
    /// efficiency ratio. The prefill they re-paid is real money and the main lever on the raw hit
    /// rate, but it is idle time being billed, not a prefix-assembly fault.</summary>
    public int ExpiredPrefixSamples { get; set; }
    public long ExpiredPrefixTokens { get; set; }
    public double TargetReusablePrefixEfficiencyPct { get; set; } = 99.7;
    public bool MeetsReusablePrefixTarget { get; set; }
    public double ZeroHitRatePct { get; set; }
    public double TelemetryCoveragePct { get; set; }
    public long AveragePromptTokens { get; set; }
    public long AverageUncachedTokens { get; set; }
    public long AverageRequestDurationMs { get; set; }
    public long TotalRequestDurationMs { get; set; }
    public int LatencyBreakdownRequests { get; set; }
    public long AverageQueueDurationMs { get; set; }
    public long TotalQueueDurationMs { get; set; }
    /// <summary>The queue-wait tail. Parking is a deliberate trade, so the mean is the wrong place to
    /// look: these are what say whether the scheduler is spending patience it should not be.</summary>
    public long MaxQueueDurationMs { get; set; }
    public int ParkedRequests { get; set; }
    public long AverageProviderDurationMs { get; set; }
    public long TotalProviderDurationMs { get; set; }
    public double LatencyBreakdownCoveragePct { get; set; }
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
    public int LatencyBreakdownRequests { get; set; }
    public long AverageQueueDurationMs { get; set; }
    public long TotalQueueDurationMs { get; set; }
    public long AverageProviderDurationMs { get; set; }
    public long TotalProviderDurationMs { get; set; }
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
    public int LatencyBreakdownRequests { get; set; }
    public long AverageQueueDurationMs { get; set; }
    public long TotalQueueDurationMs { get; set; }
    public long AverageProviderDurationMs { get; set; }
    public long TotalProviderDurationMs { get; set; }
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
    public long QueueDurationMs { get; set; }
    public long ProviderDurationMs { get; set; }
    public bool LatencyBreakdownAvailable { get; set; }
    public bool ContextWasCompacted { get; set; }
    public string? ResponseCacheStatus { get; set; }
}

internal static class ProjectPromptCacheAnalytics
{
    private const int MinimumReadyRequests = 20;

    /// <summary>Above this a request was held by a scheduling decision rather than by ordinary
    /// contention, so counting it separately is what distinguishes deliberate parking from a stall.</summary>
    private const long ParkedRequestThresholdMs = 5_000;
    private const long MinimumReadyPromptTokens = 1_000_000;
    private const int MinimumReusablePrefixSamples = 10;
    internal const double TargetCacheHitRatePct = 99.7;

    internal static AnalyticsPromptCacheSnapshot Build(
        IEnumerable<ProjectTokenUsageRecord> usage,
        AnalyticsRange range,
        string telemetryVersion = ProjectPromptCacheTelemetry.CurrentVersion)
    {
        var eligible = usage
            .Where(record => IsEligible(record, telemetryVersion))
            .OrderBy(record => record.OccurredAt)
            .ThenBy(record => record.Sequence)
            .ToList();
        var measured = eligible.Where(record => record.CacheMetricsAvailable).ToList();
        var result = new AnalyticsPromptCacheSnapshot
        {
            TelemetryVersion = telemetryVersion,
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
            if (record.LatencyBreakdownAvailable)
            {
                result.LatencyBreakdownRequests++;
                long queued = Math.Max(0, record.QueueDurationMs);
                result.TotalQueueDurationMs += queued;
                if (queued > result.MaxQueueDurationMs) result.MaxQueueDurationMs = queued;
                // Anything held this long was a scheduling decision, not contention noise.
                if (queued > ParkedRequestThresholdMs) result.ParkedRequests++;
                result.TotalProviderDurationMs += Math.Max(0, record.ProviderDurationMs);
            }
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
                if (record.LatencyBreakdownAvailable)
                {
                    point.LatencyBreakdownRequests++;
                    point.TotalQueueDurationMs += Math.Max(0, record.QueueDurationMs);
                    point.TotalProviderDurationMs += Math.Max(0, record.ProviderDurationMs);
                }
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
            ExpiredPrefixSamples = snapshots.Sum(item => item.ExpiredPrefixSamples),
            ExpiredPrefixTokens = snapshots.Sum(item => item.ExpiredPrefixTokens),
            TotalRequestDurationMs = snapshots.Sum(item => item.TotalRequestDurationMs),
            LatencyBreakdownRequests = snapshots.Sum(item => item.LatencyBreakdownRequests),
            TotalQueueDurationMs = snapshots.Sum(item => item.TotalQueueDurationMs),
            MaxQueueDurationMs = snapshots.Count == 0 ? 0 : snapshots.Max(item => item.MaxQueueDurationMs),
            ParkedRequests = snapshots.Sum(item => item.ParkedRequests),
            TotalProviderDurationMs = snapshots.Sum(item => item.TotalProviderDurationMs),
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
            target.LatencyBreakdownRequests += point.LatencyBreakdownRequests;
            target.TotalQueueDurationMs += point.TotalQueueDurationMs;
            target.TotalProviderDurationMs += point.TotalProviderDurationMs;
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
                    LatencyBreakdownRequests = group.Sum(item => item.LatencyBreakdownRequests),
                    TotalQueueDurationMs = group.Sum(item => item.TotalQueueDurationMs),
                    TotalProviderDurationMs = group.Sum(item => item.TotalProviderDurationMs),
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
        => IsEligible(record, ProjectPromptCacheTelemetry.CurrentVersion);

    private static bool IsEligible(ProjectTokenUsageRecord record, string telemetryVersion)
        => !string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)
            && record.PromptTokens > 0
            && !string.IsNullOrWhiteSpace(record.Provider)
            && string.Equals(record.PromptCacheTelemetryVersion, telemetryVersion, StringComparison.Ordinal);

    /// <summary>
    /// Whether a journal row carries a provider cache measurement this build is allowed to believe.
    /// Shared with <see cref="ProjectCacheHealthMonitor"/> on purpose: the rate that halts the fleet
    /// and the rate on the analytics page have to be computed over the same population, or the
    /// dashboard will contradict the kill switch at exactly the moment someone is reading both.
    /// </summary>
    internal static bool IsMeasuredSample(ProjectTokenUsageRecord record)
        => IsEligible(record) && record.CacheMetricsAvailable;

    /// <summary>
    /// The known-reusable prefix and how much of it the provider actually served.
    ///
    /// <paramref name="ExpiredSamples"/>/<paramref name="ExpiredTokens"/> are the continuations left
    /// OUT of the ratio because the agent was away longer than the cache could plausibly hold the
    /// prefix. They are reported rather than discarded: a rise in expiries is a real (and expensive)
    /// fact about how the fleet is spending its time, it is just not evidence that prefix assembly
    /// is broken, which is the only thing this ratio is allowed to claim.
    /// </summary>
    internal readonly record struct ReusablePrefixMeasurement(
        int Samples,
        long ReusableTokens,
        long ReusedTokens,
        int ExpiredSamples = 0,
        long ExpiredTokens = 0,
        /// <summary>Of <see cref="ExpiredSamples"/>, those that were ready inside the prefix lifetime
        /// and spent it waiting for an AIRouter slot. A fleet fault, but a capacity one.</summary>
        int QueueExpiredSamples = 0,
        long QueueExpiredTokens = 0);

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

    /// <summary>
    /// How long a prefix is assumed to survive at the provider, for the purpose of deciding which
    /// continuations were ever capable of hitting. Shared with
    /// <see cref="ProjectCacheHealthOptions.AssumedPrefixLifetime"/>'s default so the dashboard and
    /// the kill switch cannot report different efficiencies for the same window.
    /// </summary>
    internal static readonly TimeSpan DefaultAssumedPrefixLifetime = TimeSpan.FromMinutes(5);

    private static void AddReusablePrefixMeasurements(
        AnalyticsPromptCacheSnapshot result,
        IReadOnlyList<ProjectTokenUsageRecord> records)
    {
        var measurement = MeasureReusablePrefix(records, DefaultAssumedPrefixLifetime);
        result.ReusablePrefixSamples += measurement.Samples;
        result.ReusablePrefixTokens += measurement.ReusableTokens;
        result.ReusedPrefixTokens += measurement.ReusedTokens;
        result.ExpiredPrefixSamples += measurement.ExpiredSamples;
        result.ExpiredPrefixTokens += measurement.ExpiredTokens;
    }

    /// <summary>
    /// The correctness figure behind the raw hit rate. The raw cached/prompt ratio has an
    /// unavoidable ceiling — every new assistant/tool suffix is being seen for the first time — so
    /// this compares the provider's cache read against the preceding request that should be an
    /// exact prefix of this continuation instead.
    ///
    /// "Should" is a claim about TIME as well as bytes. A provider-side prefix cache has a finite
    /// lifetime, so a continuation sent half an hour after its predecessor was never going to hit
    /// however perfect the prefix is — the entry is gone. Counting that as a lost prefix is what
    /// made this figure agree with the raw rate exactly when the two needed to disagree, and it is
    /// what halted the fleet on 2026-09-16: measured over the live journal, continuations resumed
    /// within three minutes reused 99.96% of their prefix and never missed outright, while the
    /// whole of the loss sat in the 5.8% of turns that resumed 10–30 minutes later.
    ///
    /// So pairs separated by more than <paramref name="prefixLifetime"/> are counted as EXPIRED and
    /// excluded from the ratio. Passing null keeps the old time-blind behaviour.
    /// </summary>
    internal static ReusablePrefixMeasurement MeasureReusablePrefix(
        IEnumerable<ProjectTokenUsageRecord> records,
        TimeSpan? prefixLifetime = null)
    {
        int samples = 0;
        long reusableTokens = 0;
        long reusedTokens = 0;
        int expiredSamples = 0;
        long expiredTokens = 0;
        int queueExpiredSamples = 0;
        long queueExpiredTokens = 0;
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
                        if (prefixLifetime is { } lifetime && ProviderIdleGap(previous, record) > lifetime)
                        {
                            expiredSamples++;
                            expiredTokens += reusable;
                            // Expired, but not because anyone was idle: this one was ready inside the
                            // lifetime and spent it queued. Counted apart because the two have opposite
                            // remedies — nothing, versus admission control.
                            if (EnqueueIdleGap(previous, record) <= lifetime)
                            {
                                queueExpiredSamples++;
                                queueExpiredTokens += reusable;
                            }
                        }
                        else
                        {
                            samples++;
                            reusableTokens += reusable;
                            reusedTokens += Math.Min(cached, reusable);
                        }
                    }
                }
                previous = record;
            }
        }
        return new ReusablePrefixMeasurement(
            samples, reusableTokens, reusedTokens, expiredSamples, expiredTokens,
            queueExpiredSamples, queueExpiredTokens);
    }

    /// <summary>
    /// How long the prefix sat untouched between two consecutive turns, as the PROVIDER experienced
    /// it: from the predecessor's write to the moment this request actually reached the provider.
    ///
    /// The distinction is the whole measurement. A prefix cache expires in the provider's wall clock,
    /// which keeps running while a request sits in our own admission queue — so the gap that decides
    /// whether a prefix was still there is (completed → arrived at provider), and the local wait is
    /// part of it, not an exemption from it.
    ///
    /// Subtracting <c>RequestDurationMs</c> instead — as this did until 2026-09-17 — deducts the queue
    /// wait as well, which is the one term that makes the gap long in the first place. Under load that
    /// wait is minutes: on 2026-09-16 every continuation in the halting window was enqueued 3–163s
    /// after its predecessor but only reached the provider 267–440s after it, so eight pairs whose
    /// prefixes had genuinely expired were scored as live prefixes the assembly had lost, and the
    /// fleet was halted for a fault that was not in the prompt at all.
    ///
    /// A row with no latency breakdown gets no correction and so reads LONGER than it was, which can
    /// only classify a live pair as expired — it drops evidence, it can never manufacture health.
    /// That is the safe direction for a number whose job is to hold up a kill switch.
    /// </summary>
    private static TimeSpan ProviderIdleGap(ProjectTokenUsageRecord previous, ProjectTokenUsageRecord record)
        => GapTo(previous, record.LatencyBreakdownAvailable ? record.ProviderDurationMs : 0, record);

    /// <summary>
    /// The same gap measured to the moment the request ENTERED the queue rather than the moment it
    /// left it. Only ever compared against <see cref="ProviderIdleGap"/>: a pair that is live by this
    /// measure and expired by that one lost its prefix to our own queue, which is a capacity fault and
    /// the one kind of expiry the fleet can actually do something about.
    /// </summary>
    private static TimeSpan EnqueueIdleGap(ProjectTokenUsageRecord previous, ProjectTokenUsageRecord record)
        => GapTo(previous, record.LatencyBreakdownAvailable ? record.RequestDurationMs : 0, record);

    private static TimeSpan GapTo(ProjectTokenUsageRecord previous, long rewindMs, ProjectTokenUsageRecord record)
    {
        DateTime wrote = previous.OccurredAt.ToUniversalTime();
        DateTime reached = record.OccurredAt.ToUniversalTime()
            - TimeSpan.FromMilliseconds(Math.Max(0, rewindMs));
        return reached > wrote ? reached - wrote : TimeSpan.Zero;
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
            if (record.LatencyBreakdownAvailable)
            {
                result.LatencyBreakdownRequests++;
                result.TotalQueueDurationMs += Math.Max(0, record.QueueDurationMs);
                result.TotalProviderDurationMs += Math.Max(0, record.ProviderDurationMs);
            }
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
        result.AverageQueueDurationMs = result.LatencyBreakdownRequests > 0
            ? result.TotalQueueDurationMs / result.LatencyBreakdownRequests
            : 0;
        result.AverageProviderDurationMs = result.LatencyBreakdownRequests > 0
            ? result.TotalProviderDurationMs / result.LatencyBreakdownRequests
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
            QueueDurationMs = Math.Max(0, record.QueueDurationMs),
            ProviderDurationMs = Math.Max(0, record.ProviderDurationMs),
            LatencyBreakdownAvailable = record.LatencyBreakdownAvailable,
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
        result.AverageQueueDurationMs = result.LatencyBreakdownRequests > 0
            ? result.TotalQueueDurationMs / result.LatencyBreakdownRequests
            : 0;
        result.AverageProviderDurationMs = result.LatencyBreakdownRequests > 0
            ? result.TotalProviderDurationMs / result.LatencyBreakdownRequests
            : 0;
        result.LatencyBreakdownCoveragePct = Percent(
            result.LatencyBreakdownRequests, result.MeasuredRequests);

        foreach (var point in result.Series)
        {
            point.CacheHitRatePct = Percent(point.CachedTokens, point.PromptTokens);
            point.AverageRequestDurationMs = point.Requests > 0
                ? point.TotalRequestDurationMs / point.Requests
                : 0;
            point.AverageQueueDurationMs = point.LatencyBreakdownRequests > 0
                ? point.TotalQueueDurationMs / point.LatencyBreakdownRequests
                : 0;
            point.AverageProviderDurationMs = point.LatencyBreakdownRequests > 0
                ? point.TotalProviderDurationMs / point.LatencyBreakdownRequests
                : 0;
        }

        if (result.Requests == 0)
        {
            result.Status = "no-data";
            result.Verdict = "No requests from this telemetry version are in this range yet.";
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
            if (result.Count > ProjectAnalyticsRange.MaxBuckets)
                throw new ArgumentException("Too many analytics chart points.");
            cursor = ProjectAnalyticsRange.Next(cursor, range.Bucket);
        }
        return result;
    }

    private static DateTime BucketStart(DateTime timestamp, string bucket) => ProjectAnalyticsRange.Start(timestamp, bucket);

    private static string BucketKey(DateTime timestamp, string bucket)
        => ProjectAnalyticsRange.Key(timestamp, bucket);

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
