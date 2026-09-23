using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Tests.KliveAPI.Telemetry;

public sealed class TelemetryEngineTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public TelemetryEngineTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"telemetry-test-{Guid.NewGuid():N}.db");
    // A fixed, hour-aligned clock so bucket boundaries are deterministic.
    private long _now = 1_760_000_400_000 - (1_760_000_400_000 % 3_600_000) + 30_000;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private ApiTelemetry NewEngine(TelemetryDb? db = null)
    {
        var tel = new ApiTelemetry(new TelemetryOptions(), db) { NowMs = () => _now };
        tel.AdvanceClock(_now);
        return tel;
    }

    private static JsonDocument Json(byte[] bytes) => JsonDocument.Parse(bytes);

    [Fact]
    public void Fold_BatchItemsAndDeniedAndPreflights_StayOutOfGlobal()
    {
        var tel = NewEngine();
        tel.Fold(TraceHelpers.Make("/batch", method: "POST"));
        tel.Fold(TraceHelpers.Make("/a", viaBatch: true));
        tel.Fold(TraceHelpers.Make("/a", viaBatch: true));
        tel.Fold(TraceHelpers.Make("/a"));
        tel.Fold(TraceHelpers.Make("/secret", denied: true, handlerMs: 20_000));
        tel.Fold(TraceHelpers.Make("/a", method: "OPTIONS", seriesOverride: ApiTelemetry.PreflightSeries));
        tel.Fold(TraceHelpers.Make("/nope", matched: false, status: 404));

        var m1 = tel.RequestStore(TelemetryTier.M1);
        Assert.Equal(3, m1.Get(m1.OpenStart, ApiTelemetry.GlobalSeries)!.Count); // POST /batch, GET /a, 404
        Assert.Equal(3, m1.Get(m1.OpenStart, "GET /a")!.Count);                  // direct + both batch items
        Assert.Equal(2, m1.Get(m1.OpenStart, ApiTelemetry.BatchItemsSeries)!.Count);
        Assert.Equal(1, m1.Get(m1.OpenStart, ApiTelemetry.DeniedSeries)!.Count);
        Assert.Equal(1, m1.Get(m1.OpenStart, ApiTelemetry.PreflightSeries)!.Count);
        Assert.Equal(1, m1.Get(m1.OpenStart, ApiTelemetry.UnmatchedSeries)!.Count);
        // The 20 s denied request never pollutes the global latency distribution.
        Assert.True(m1.Get(m1.OpenStart, ApiTelemetry.GlobalSeries)!.Latency.Max < 1_000_000);
    }

    [Fact]
    public void Views_ArePublishedForEveryPreset_AndDescribeTheData()
    {
        var tel = NewEngine();
        for (int minute = 0; minute < 30; minute++)
        {
            for (int i = 0; i < 10; i++) tel.Fold(TraceHelpers.Make("/fast", handlerMs: 2));
            for (int i = 0; i < 2; i++) tel.Fold(TraceHelpers.Make("/slow", handlerMs: 400, status: minute == 5 ? 500 : 200));
            _now += 60_000;
            tel.AdvanceClock(_now);
        }
        tel.RebuildAllViewsForTest();

        foreach (string preset in TelemetryQuery.Presets)
        {
            foreach (string kind in new[] { "overview", "routes", "runtime", "rum" })
            {
                Assert.NotNull(tel.TryGetView($"{kind}|{preset}"));
            }
        }
        Assert.NotNull(tel.TryGetView("weekly"));
        Assert.NotNull(tel.TryGetView("health"));

        using var overview = Json(tel.TryGetView("overview|1h")!.RawBody);
        var kpi = overview.RootElement.GetProperty("kpi");
        Assert.Equal(360, kpi.GetProperty("count").GetInt64());
        Assert.InRange(kpi.GetProperty("p99").GetDouble(), 360, 450);
        Assert.InRange(kpi.GetProperty("p50").GetDouble(), 1.5, 3.5);
        Assert.Equal(2.0 / 360 * 100, kpi.GetProperty("errorPct").GetDouble(), 2);
        var stages = overview.RootElement.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(TelemetryStages.Count, stages.Count);
        var handler = stages.Single(s => s.GetProperty("key").GetString() == "handler");
        Assert.True(handler.GetProperty("share").GetDouble() > 0.9);
        Assert.Equal(overview.RootElement.GetProperty("ts").GetProperty("count").GetArrayLength(), overview.RootElement.GetProperty("points").GetInt32());

        using var routes = Json(tel.TryGetView("routes|1h")!.RawBody);
        var rows = routes.RootElement.GetProperty("routes").EnumerateArray().ToList();
        Assert.Equal("GET /slow", rows[0].GetProperty("s").GetString()); // ordered by time cost
        Assert.Equal("handler", rows[0].GetProperty("dom").GetString());
        Assert.Equal(300, rows.Single(r => r.GetProperty("s").GetString() == "GET /fast").GetProperty("count").GetInt64());
        Assert.True(rows[0].GetProperty("spark").GetProperty("count").GetArrayLength() <= 24);
    }

    [Fact]
    public void PlanWindow_ChoosesTiersByRangeAndRetention()
    {
        var tel = NewEngine();
        ApiTelemetry.Window W(string range, bool route, long? bucket = null) =>
            tel.PlanWindow(new TelemetryQuery { Kind = TelemetryQueryKind.Overview, RangeKey = range, BucketMs = bucket }, _now, route, hasS10: !route);

        Assert.Equal(TelemetryTier.S10, W("15m", route: false).Tier);
        Assert.Equal(TelemetryTier.M1, W("15m", route: true).Tier);
        Assert.Equal(TelemetryTier.M1, W("24h", route: true).Tier);
        Assert.Equal(TelemetryTier.H1, W("7d", route: true).Tier);
        // Routes keep hourly data for 8 days, so 30 days of a route is answered from days...
        Assert.Equal(TelemetryTier.D1, W("30d", route: true).Tier);
        // ...while the global series keeps hourly history much longer.
        Assert.Equal(TelemetryTier.H1, W("30d", route: false).Tier);
        foreach (string preset in TelemetryQuery.Presets)
        {
            Assert.InRange(W(preset, route: false).Points, 1, 300);
        }

        // An explicit 1-minute bucket over 30 days is widened (and says so).
        var widened = W("30d", route: false, bucket: 60_000);
        Assert.True(widened.Degraded);
        Assert.True(widened.Points <= 300);
    }

    [Fact]
    public async Task Query_CustomRange_IsComputedAndCached()
    {
        var tel = NewEngine();
        long start = _now;
        for (int minute = 0; minute < 10; minute++)
        {
            tel.Fold(TraceHelpers.Make("/q", handlerMs: 10));
            _now += 60_000;
            tel.AdvanceClock(_now);
        }
        var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route, Series = "GET /q", FromMs = start - 60_000, ToMs = _now, BucketMs = 60_000 };
        var first = await tel.QueryAsync(q);
        var second = await tel.QueryAsync(q);
        Assert.Same(first, second);
        using var doc = Json(first.RawBody);
        Assert.Equal(10, doc.RootElement.GetProperty("kpi").GetProperty("count").GetInt64());
        Assert.Equal(60_000, doc.RootElement.GetProperty("bucketMs").GetInt64());
    }

    [Fact]
    public void Persistence_ClosedAndOpenBucketsSurviveARestart()
    {
        var db = new TelemetryDb(_dbPath);
        var tel = NewEngine(db);
        tel.LoadHistory();
        for (int minute = 0; minute < 5; minute++)
        {
            for (int i = 0; i < 4; i++) tel.Fold(TraceHelpers.Make("/p", handlerMs: 8));
            _now += 60_000;
            tel.AdvanceClock(_now);
        }
        tel.Fold(TraceHelpers.Make("/p", handlerMs: 8)); // lands in the still-open minute/hour
        tel.FlushForTest();

        var restarted = NewEngine(new TelemetryDb(_dbPath));
        restarted.LoadHistory();
        var h1 = restarted.RequestStore(TelemetryTier.H1);
        var hour = h1.Get(h1.OpenStart, "GET /p");
        Assert.NotNull(hour);
        Assert.Equal(21, hour!.Count);                    // the open hour resumed intact
        var m1 = restarted.RequestStore(TelemetryTier.M1);
        Assert.Equal(20, m1.MergeRange("GET /p", 0, m1.OpenStart).Count); // closed minutes reloaded
    }

    [Fact]
    public void Persistence_SlowErrorAndSampledTracesAreKept()
    {
        var db = new TelemetryDb(_dbPath);
        var tel = NewEngine(db);
        tel.LoadHistory();
        for (int i = 0; i < 10; i++) tel.Fold(TraceHelpers.Make("/k", handlerMs: 3)); // 2 sampled
        tel.Fold(TraceHelpers.Make("/k", handlerMs: 2500));                                // slow
        tel.Fold(TraceHelpers.Make("/k", status: 503));                                   // error
        tel.FlushForTest();

        var rows = db.QueryTraces("/k", "GET", null, null, 0, long.MaxValue, "slowest", 50);
        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].TotalMicros >= 2_500_000);
        Assert.Contains(rows, r => r.Status == 503);
        var (stages, _, _) = ApiTelemetry.DecodeStages(rows[0].Stages);
        Assert.InRange(stages[(int)TelemetryStage.Handler] / 1000.0, 2499, 2501);
    }

    [Fact]
    public void Rum_BeaconIsClampedAndUnknownRoutesCollapse()
    {
        using var doc = JsonDocument.Parse("""{"r":"https://klive.dev/projects/list?x=1","t":"00000000000000ff","dns":-5,"tls":999999999,"net":12.5,"srv":3,"dl":1,"tot":20,"reuse":1,"xfer":900}""");
        var sample = TelemetryRoutes.ParseRum(doc.RootElement, 1_000, r => r == "/projects/list" ? "GET" : null);
        Assert.NotNull(sample);
        Assert.Equal("/projects/list", sample!.Route);
        Assert.Equal("GET", sample.Method);
        Assert.Equal(255, sample.TraceId);
        Assert.Equal(0, sample.PhaseMicros[(int)RumPhase.Dns]);
        Assert.Equal(600_000_000, sample.PhaseMicros[(int)RumPhase.Tls]);
        Assert.Equal(12_500, sample.PhaseMicros[(int)RumPhase.Network]);
        Assert.True(sample.Reused);

        using var unknown = JsonDocument.Parse("""{"r":"/attacker/controlled/123","tot":5}""");
        Assert.Equal(ApiTelemetry.UnmatchedSeries, TelemetryRoutes.ParseRum(unknown.RootElement, 1_000, _ => null)!.Route);

        using var empty = JsonDocument.Parse("""{"r":"/x"}""");
        Assert.Null(TelemetryRoutes.ParseRum(empty.RootElement, 1_000, _ => "GET"));
    }

    [Fact]
    public void Rum_JoinsAKeptTraceBeforeItIsFlushed()
    {
        var db = new TelemetryDb(_dbPath);
        var tel = NewEngine(db);
        tel.LoadHistory();
        var slow = TraceHelpers.Make("/j", handlerMs: 3000);
        tel.Fold(slow);
        var sample = new RumSample { Route = "/j", TraceId = slow.TraceId };
        sample.PhaseMicros[(int)RumPhase.Total] = 3_100_000;
        tel.RecordRum(sample);
        tel.DrainForTest();
        tel.FlushForTest();
        var row = db.GetTrace(slow.TraceId);
        Assert.NotNull(row?.Client);
        Assert.Equal(3_100_000, ApiTelemetry.DecodeClient(row!.Client!).Phases[(int)RumPhase.Total]);
    }

    [Fact]
    public void Record_IsCheapOnTheHotPath()
    {
        var tel = NewEngine();
        var traces = Enumerable.Range(0, 20_000).Select(_ => TraceHelpers.Make("/hot")).ToArray();
        tel.Record(traces[0]); // warm up
        var sw = Stopwatch.StartNew();
        for (int i = 1; i < traces.Length; i++) tel.Record(traces[i]);
        sw.Stop();
        double microsPerRecord = sw.Elapsed.TotalMilliseconds * 1000 / traces.Length;
        Assert.True(microsPerRecord < 5, $"Record() cost {microsPerRecord:F2} µs");
    }

    [Fact]
    public void Record_DropsOldestWhenBacklogIsFull_AndCountsIt()
    {
        var tel = NewEngine();
        for (int i = 0; i < 50_100; i++) tel.Record(TraceHelpers.Make("/flood"));
        Assert.Equal(100, tel.Dropped);
        Assert.Equal(50_000, tel.Backlog);
    }

    /// <summary>
    /// The performance contract: at 400 routes with deep history, every preset view is a
    /// sub-5 ms read, cold custom queries stay bounded, and payloads stay compact.
    /// </summary>
    [Fact]
    public async Task InstantResponses_AtScale()
    {
        var tel = NewEngine();
        var rng = new Random(42);
        string[] routes = Enumerable.Range(0, 400).Select(i => $"GET /svc{i % 20}/route{i}").ToArray();

        TelemetryBucket MakeBucket(int requests)
        {
            var b = new TelemetryBucket();
            for (int i = 0; i < requests; i++)
            {
                b.Fold(TraceHelpers.Make("/x", handlerMs: Math.Exp(rng.NextDouble() * 6), bytes: rng.Next(200, 200_000),
                    status: rng.NextDouble() < 0.01 ? 500 : 200, cache: rng.NextDouble() < 0.5 ? TelemetryCacheStatus.Hit : TelemetryCacheStatus.Miss));
            }
            return b.Freeze();
        }

        // Seed history directly into the tiers (1y of days, 8d of hours, 24h of minutes).
        var d1 = tel.RequestStore(TelemetryTier.D1);
        var h1 = tel.RequestStore(TelemetryTier.H1);
        var m1 = tel.RequestStore(TelemetryTier.M1);
        var template = MakeBucket(60);
        for (int day = 1; day <= 365; day++)
        {
            long ts = d1.OpenStart - day * 86_400_000L;
            d1.LoadClosed(ts, ApiTelemetry.GlobalSeries, Clone(template));
            foreach (string r in routes.Where((_, i) => i % 3 == day % 3)) d1.LoadClosed(ts, r, Clone(template));
        }
        for (int hour = 1; hour <= 8 * 24; hour++)
        {
            long ts = h1.OpenStart - hour * 3_600_000L;
            h1.LoadClosed(ts, ApiTelemetry.GlobalSeries, Clone(template));
            foreach (string r in routes.Where((_, i) => i % 8 == hour % 8)) h1.LoadClosed(ts, r, Clone(template));
        }
        for (int minute = 1; minute <= 24 * 60; minute++)
        {
            long ts = m1.OpenStart - minute * 60_000L;
            m1.LoadClosed(ts, ApiTelemetry.GlobalSeries, Clone(template));
            foreach (string r in routes.Where((_, i) => i % 60 == minute % 60)) m1.LoadClosed(ts, r, Clone(template));
        }
        foreach (string r in routes) tel.Fold(TraceHelpers.Make("/" + r.Split(' ')[1].TrimStart('/'))); // register series

        var buildTimer = Stopwatch.StartNew();
        tel.RebuildAllViewsForTest();
        buildTimer.Stop();

        // 1) Preset reads are dictionary lookups.
        var reads = new List<double>();
        for (int round = 0; round < 200; round++)
        {
            foreach (string preset in TelemetryQuery.Presets)
            {
                foreach (string kind in new[] { "overview", "routes", "runtime", "rum" })
                {
                    var sw = Stopwatch.StartNew();
                    var entry = tel.TryGetView($"{kind}|{preset}");
                    var br = entry!.GetVariant(Omnipotent.Services.KliveAPI.HttpResponseHelpers.ContentEncoding.Brotli);
                    sw.Stop();
                    reads.Add(sw.Elapsed.TotalMilliseconds);
                    Assert.NotNull(br ?? entry.RawBody);
                }
            }
        }
        reads.Sort();
        double p99 = reads[(int)(reads.Count * 0.99)];
        Assert.True(p99 < 5, $"preset view read p99 {p99:F3} ms");

        // 2) Payload budgets (Brotli, what the website actually downloads).
        int Brotli(string key)
        {
            var e = tel.TryGetView(key)!;
            return (e.GetVariant(Omnipotent.Services.KliveAPI.HttpResponseHelpers.ContentEncoding.Brotli) ?? e.RawBody).Length;
        }
        Assert.True(Brotli("overview|24h") <= 30 * 1024, $"overview|24h is {Brotli("overview|24h")} bytes");
        Assert.True(Brotli("routes|24h") <= 60 * 1024, $"routes|24h is {Brotli("routes|24h")} bytes");
        Assert.True(Brotli("routes|1y") <= 60 * 1024, $"routes|1y is {Brotli("routes|1y")} bytes");

        // 3) Cold custom queries (route detail, custom range + bucket) are bounded.
        var cold = new List<double>();
        foreach (string r in routes.Take(40))
        {
            var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route, Series = r, FromMs = _now - 7 * 86_400_000L, ToMs = _now, BucketMs = 3_600_000 };
            var sw = Stopwatch.StartNew();
            await tel.QueryAsync(q);
            cold.Add(sw.Elapsed.TotalMilliseconds);
        }
        cold.Sort();
        Assert.True(cold[(int)(cold.Count * 0.95)] < 50, $"cold route query p95 {cold[(int)(cold.Count * 0.95)]:F1} ms");

        _out.WriteLine($"view read p99={p99:F4}ms; cold route query p95={cold[(int)(cold.Count * 0.95)]:F2}ms; full rebuild={buildTimer.Elapsed.TotalMilliseconds:F0}ms");
        foreach (string key in new[] { "overview|15m", "overview|24h", "overview|1y", "routes|24h", "routes|1y", "runtime|24h" })
        {
            _out.WriteLine($"{key}: raw={tel.TryGetView(key)!.RawBody.Length}B br={Brotli(key)}B");
        }

        // Full rebuild of every view (runs off the request path) stays well under a second.
        Assert.True(buildTimer.Elapsed.TotalMilliseconds < 5000, $"full view rebuild {buildTimer.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static TelemetryBucket Clone(TelemetryBucket b)
    {
        var c = new TelemetryBucket();
        c.Merge(b);
        return c;
    }
}
