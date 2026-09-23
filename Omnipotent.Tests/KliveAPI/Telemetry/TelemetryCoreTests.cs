using System.Diagnostics;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Tests.KliveAPI.Telemetry;

public sealed class TelemetryCoreTests
{
    // ── histogram ──

    [Fact]
    public void Histogram_Percentiles_WithinNinePercentOfExact()
    {
        var rng = new Random(1234);
        var h = new LogHistogram();
        var values = new List<long>();
        for (int i = 0; i < 50_000; i++)
        {
            // log-normal-ish latencies centred around ~5 ms with a long tail
            double v = Math.Exp(8.5 + rng.NextDouble() * 2.5 + (rng.NextDouble() < 0.02 ? 3 : 0));
            long us = (long)v;
            values.Add(us);
            h.Add(us);
        }
        values.Sort();
        foreach (double p in new[] { 0.5, 0.9, 0.95, 0.99 })
        {
            long exact = values[(int)Math.Ceiling(p * values.Count) - 1];
            double approx = h.Percentile(p);
            Assert.InRange(approx / exact, 1 / 1.1, 1.1);
        }
        Assert.Equal(values.Count, h.Count);
        Assert.Equal(values.Max(), h.Max);
        Assert.Equal(values.Sum(), h.Sum);
    }

    [Fact]
    public void Histogram_MergeIsExact_AndSurvivesFreezeAndSerialization()
    {
        var rng = new Random(7);
        var a = new LogHistogram();
        var b = new LogHistogram();
        var direct = new LogHistogram();
        for (int i = 0; i < 5000; i++)
        {
            long v = rng.Next(0, 2_000_000);
            (i % 2 == 0 ? a : b).Add(v);
            direct.Add(v);
        }
        a.Freeze();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) b.Write(w);
        ms.Position = 0;
        var bRoundTrip = LogHistogram.Read(new BinaryReader(ms));

        var merged = new LogHistogram();
        merged.Merge(a);
        merged.Merge(bRoundTrip);

        Assert.Equal(direct.Count, merged.Count);
        Assert.Equal(direct.Sum, merged.Sum);
        Assert.Equal(direct.Max, merged.Max);
        for (int i = 0; i < LogHistogram.BucketCount; i++) Assert.Equal(direct.CountAt(i), merged.CountAt(i));
        Assert.Equal(direct.Percentile(0.95), merged.Percentile(0.95));
    }

    [Fact]
    public void Histogram_Subtract_UndoesMerge()
    {
        var a = new LogHistogram();
        var b = new LogHistogram();
        for (int i = 1; i < 1000; i++) { a.Add(i * 37); b.Add(i * 11); }
        var acc = a.Clone();
        acc.Merge(b);
        acc.Subtract(b);
        for (int i = 0; i < LogHistogram.BucketCount; i++) Assert.Equal(a.CountAt(i), acc.CountAt(i));
        Assert.Equal(a.Count, acc.Count);
    }

    // ── trace ──

    [Fact]
    public void Trace_StagesPartitionTheLifetime()
    {
        var trace = RequestTrace.StartNow(TelemetryStage.DispatchQueue);
        SpinFor(0.3);
        trace.Enter(TelemetryStage.Auth);
        SpinFor(0.3);
        trace.Enter(TelemetryStage.Handler);
        SpinFor(0.6);
        trace.Enter(TelemetryStage.ResponseWrite);
        SpinFor(0.2);
        trace.Enter(TelemetryStage.Teardown);
        trace.Complete();

        long sum = trace.StageTicks.Sum();
        Assert.Equal(trace.TotalTicks, sum);
        // Lower bounds only: a preempted spin can make any stage longer, never shorter.
        Assert.True(RequestTrace.TicksToMs(trace.StageTicks[(int)TelemetryStage.Handler]) >= 0.6);
        Assert.True(RequestTrace.TicksToMs(trace.StageTicks[(int)TelemetryStage.ResponseWrite]) >= 0.2);
        // Teardown is excluded from latency.
        Assert.Equal(sum - trace.StageTicks[(int)TelemetryStage.Teardown], trace.LatencyTicks);
    }

    [Fact]
    public void Trace_DefenceDelay_IsExcludedFromLatencyAndServerTimingApp()
    {
        var trace = RequestTrace.StartNow(TelemetryStage.Prologue);
        trace.Enter(TelemetryStage.DefenceDelay);
        SpinFor(2);
        trace.Enter(TelemetryStage.Handler);
        trace.Complete();
        Assert.True(RequestTrace.TicksToMs(trace.LatencyTicks) < 1.5);
        string header = trace.BuildServerTimingHeader();
        Assert.Contains("delay;dur=", header);
        Assert.Contains("app;dur=", header);
        string appPart = header[(header.LastIndexOf("app;dur=", StringComparison.Ordinal) + 8)..];
        double app = double.Parse(appPart[..appPart.IndexOf(',')], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(app < 1.5, header);
    }

    [Fact]
    public void Trace_ServerTiming_ListsNonZeroStagesThenApp()
    {
        var trace = RequestTrace.StartNow(TelemetryStage.DispatchQueue);
        SpinFor(0.2);
        trace.Enter(TelemetryStage.Handler);
        SpinFor(0.5);
        string header = trace.BuildServerTimingHeader();
        Assert.StartsWith("queue;dur=", header);
        Assert.Contains("handler;dur=", header);
        Assert.Matches(@"app;dur=[0-9.]+, trace;desc=[0-9a-f]{16}$", header);
        Assert.DoesNotContain("etag;", header); // zero stages omitted
    }

    [Fact]
    public void Trace_Spans_AccumulateByName_AndCap()
    {
        var trace = RequestTrace.StartNow();
        using (trace.Span("gate-wait")) SpinFor(0.1);
        using (trace.Span("gate-wait")) SpinFor(0.1);
        for (int i = 0; i < 20; i++) using (trace.Span("s" + i)) { }
        Assert.Equal(RequestTrace.MaxSpans, trace.SpanCount);
        Assert.Equal("gate-wait", trace.SpanName(0));
        Assert.True(RequestTrace.TicksToMs(trace.SpanTicks(0)) >= 0.19);
    }

    // ── bucket ──

    [Fact]
    public void Bucket_SerializeRoundTrip_PreservesEverything()
    {
        var b = new TelemetryBucket();
        foreach (int status in new[] { 200, 200, 304, 404, 500, 401 })
        {
            b.Fold(TraceHelpers.Make("/x", status: status, handlerMs: 12, bytes: 4096, cache: TelemetryCacheStatus.Hit, origin: "WebsiteProfile", user: "Klives", span: "gate-wait"));
        }
        b.Freeze();
        var round = TelemetryBucket.Deserialize(b.Serialize());
        Assert.Equal(b.Count, round.Count);
        Assert.Equal(b.Status, round.Status);
        Assert.Equal(b.CacheHit, round.CacheHit);
        Assert.Equal(b.BytesOut, round.BytesOut);
        Assert.Equal(b.Latency.Percentile(0.5), round.Latency.Percentile(0.5));
        Assert.Equal(b.Stages[(int)TelemetryStage.Handler].Sum, round.Stages[(int)TelemetryStage.Handler].Sum);
        Assert.Equal(6, round.Origins!["WebsiteProfile"]);
        Assert.Equal(6, round.Users!["Klives"]);
        Assert.Equal(6, round.Spans!["gate-wait"].Count);
        Assert.Equal(1, round.ServerErrors);
        Assert.Equal(2, round.ClientErrors);
    }

    [Fact]
    public void Bucket_UserSketch_IsBounded()
    {
        var b = new TelemetryBucket();
        for (int i = 0; i < 500; i++) b.Fold(TraceHelpers.Make("/x", user: "u" + i));
        Assert.True(b.Users!.Count <= TelemetryBucket.MaxUsers);
        Assert.Equal(500, b.Count);
    }

    // ── tiers ──

    [Fact]
    public void Tiers_RollupThroughTiersEqualsDirectAggregation()
    {
        var m1 = new TierStore<TelemetryBucket>(TelemetryTier.M1, () => new TelemetryBucket(), (a, b) => a.Merge(b), b => b.Freeze());
        var direct = new TelemetryBucket();
        long t0 = 1_700_000_000_000 - (1_700_000_000_000 % 3_600_000);
        var rng = new Random(3);
        m1.Advance(t0);
        for (int minute = 0; minute < 120; minute++)
        {
            for (int k = 0; k < 20; k++)
            {
                var t = TraceHelpers.Make("/r", handlerMs: rng.Next(1, 400));
                m1.GetOrCreateOpen("GET /r").Fold(t);
                direct.Fold(t);
            }
            m1.Advance(t0 + (minute + 1) * 60_000L);
        }
        var hourA = m1.MergeRange("GET /r", t0, t0 + 3_600_000);
        var hourB = m1.MergeRange("GET /r", t0 + 3_600_000, t0 + 7_200_000);
        var rolled = new TelemetryBucket();
        rolled.Merge(hourA);
        rolled.Merge(hourB);

        Assert.Equal(direct.Count, rolled.Count);
        Assert.Equal(direct.Latency.Sum, rolled.Latency.Sum);
        Assert.Equal(direct.Latency.Percentile(0.99), rolled.Latency.Percentile(0.99));
        Assert.Equal(direct.Stages[(int)TelemetryStage.Handler].Percentile(0.5), rolled.Stages[(int)TelemetryStage.Handler].Percentile(0.5));
    }

    [Fact]
    public void Tiers_PruneKeepsLongLivedSeriesLonger()
    {
        var store = new TierStore<TelemetryBucket>(TelemetryTier.M1, () => new TelemetryBucket(), (a, b) => a.Merge(b), b => b.Freeze());
        long t0 = 1_700_000_000_000 - (1_700_000_000_000 % 60_000);
        store.Advance(t0);
        store.GetOrCreateOpen("*").Fold(TraceHelpers.Make("/a"));
        store.GetOrCreateOpen("GET /a").Fold(TraceHelpers.Make("/a"));
        store.Advance(t0 + 60_000);
        store.Prune(t0 + 10 * 60_000, routeRetentionMs: 5 * 60_000, longRetentionMs: 60 * 60_000, ApiTelemetryTestAccess.IsLongLived);
        Assert.NotNull(store.Get(t0, "*"));
        Assert.Null(store.Get(t0, "GET /a"));
    }

    [Theory]
    [InlineData("10s", 10_000)]
    [InlineData("5m", 300_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("2d", 172_800_000)]
    public void Tiers_ParseWidth(string text, long ms)
    {
        Assert.True(TelemetryTiers.TryParseWidth(text, out long parsed));
        Assert.Equal(ms, parsed);
        Assert.Equal(text, TelemetryTiers.FormatWidth(ms));
    }

    [Fact]
    public void Tiers_TierForPicksCoarsestDividingTier()
    {
        Assert.Equal(TelemetryTier.S10, TelemetryTiers.TierFor(30_000));
        Assert.Equal(TelemetryTier.M1, TelemetryTiers.TierFor(600_000));
        Assert.Equal(TelemetryTier.H1, TelemetryTiers.TierFor(10_800_000));
        Assert.Equal(TelemetryTier.D1, TelemetryTiers.TierFor(172_800_000));
    }

    private static void SpinFor(double ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) { }
    }
}

/// <summary>Builds completed traces with chosen stage durations.</summary>
internal static class TraceHelpers
{
    public static RequestTrace Make(string route, int status = 200, double handlerMs = 5, long bytes = 1000,
        TelemetryCacheStatus cache = TelemetryCacheStatus.None, string? origin = null, string? user = null,
        string? span = null, string method = "GET", bool viaBatch = false, bool denied = false, bool matched = true,
        double queueMs = 0.05, double writeMs = 0.2, string? seriesOverride = null)
    {
        var t = RequestTrace.StartNow(TelemetryStage.Handler);
        if (span != null) t.AddSpan(span, t.StartTimestamp, ToTicks(1));
        t.Complete();
        Array.Clear(t.StageTicks);
        t.StageTicks[(int)TelemetryStage.DispatchQueue] = ToTicks(queueMs);
        t.StageTicks[(int)TelemetryStage.Handler] = ToTicks(handlerMs);
        t.StageTicks[(int)TelemetryStage.ResponseWrite] = ToTicks(writeMs);
        t.Route = route;
        t.Method = method;
        t.Matched = matched;
        t.StatusCode = status;
        t.ResponseBytes = bytes;
        t.ResponseRawBytes = bytes * 3;
        t.Encoding = "br";
        t.Cache = cache;
        t.Origin = origin;
        t.ProfileName = user;
        t.ViaBatch = viaBatch;
        t.Denied = denied;
        t.SeriesOverride = seriesOverride;
        return t;
    }

    public static long ToTicks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);
}

internal static class ApiTelemetryTestAccess
{
    public static bool IsLongLived(string s) => s.Length > 0 && (s[0] == '*' || s[0] == '(');
}
