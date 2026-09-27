using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Tests.KliveAPI.Telemetry;

public sealed class TelemetrySnapshotTests
{
    [Fact]
    public void CustomAggregate_IsPublishedOnlyByBackgroundPump_AndRefreshes()
    {
        long now = 1_760_000_430_000;
        var tel = new ApiTelemetry { NowMs = () => now };
        tel.AdvanceClock(now);
        tel.Fold(TraceHelpers.Make("/snapshot"));
        var query = new TelemetryQuery
        {
            Kind = TelemetryQueryKind.Route,
            Series = "GET /snapshot",
            RangeKey = "24h",
            IncludeDenied = true,
        };

        Assert.Null(tel.TryGetQueryView(query));
        Assert.Equal(1, tel.PendingSnapshotBuilds);
        tel.Pump();
        var first = tel.TryGetQueryView(query);
        Assert.NotNull(first);
        Assert.Equal(0, tel.PendingSnapshotBuilds);
        using (var doc = JsonDocument.Parse(first!.RawBody))
            Assert.Equal(1, doc.RootElement.GetProperty("kpi").GetProperty("count").GetInt64());

        now += 11_000;
        tel.Fold(TraceHelpers.Make("/snapshot"));
        tel.Pump();
        var second = tel.TryGetQueryView(query);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        using var refreshed = JsonDocument.Parse(second!.RawBody);
        Assert.Equal(2, refreshed.RootElement.GetProperty("kpi").GetProperty("count").GetInt64());
    }

    [Fact]
    public void DistinctCustomQueries_HaveABoundedBackgroundQueue()
    {
        long now = 1_760_000_400_000;
        var tel = new ApiTelemetry { NowMs = () => now };
        for (int i = 0; i < 200; i++)
        {
            var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route, Series = $"GET /route{i}", RangeKey = "24h" };
            Assert.Null(tel.TryGetQueryView(q));
        }
        Assert.InRange(tel.PendingSnapshotBuilds, 1, 64);
    }

    [Fact]
    public void ConcurrentColdQueries_CannotExceedTheQueueLimit()
    {
        var tel = new ApiTelemetry { NowMs = () => 1_760_000_400_000 };
        Parallel.For(0, 512, i =>
        {
            var q = new TelemetryQuery { Kind = TelemetryQueryKind.Route,
                Series = $"GET /concurrent{i}", RangeKey = "24h" };
            tel.TryGetQueryView(q);
        });
        Assert.InRange(tel.PendingSnapshotBuilds, 1, 64);
    }

    [Fact]
    public void LongPresetViews_RefreshWithinOneMinute()
    {
        long now = 1_760_000_430_000;
        var tel = new ApiTelemetry { NowMs = () => now };
        tel.AdvanceClock(now);
        tel.RebuildAllViewsForTest();
        using var first = JsonDocument.Parse(tel.TryGetView("overview|30d")!.RawBody);
        long firstAsOf = first.RootElement.GetProperty("asOfUtc").GetInt64();

        now += 60_000;
        tel.Pump();
        using var second = JsonDocument.Parse(tel.TryGetView("overview|30d")!.RawBody);
        Assert.Equal(now, second.RootElement.GetProperty("asOfUtc").GetInt64());
        Assert.Equal(60_000, now - firstAsOf);
    }

    [Fact]
    public void ExpiredViews_AreNotServedIfBackgroundRefreshStops()
    {
        long now = 1_760_000_430_000;
        var tel = new ApiTelemetry { NowMs = () => now };
        tel.AdvanceClock(now);
        tel.RebuildAllViewsForTest();
        Assert.NotNull(tel.TryGetView("overview|24h"));

        var custom = new TelemetryQuery
        {
            Kind = TelemetryQueryKind.Route,
            Series = "GET /stale",
            RangeKey = "24h",
            IncludeDenied = true
        };
        Assert.Null(tel.TryGetQueryView(custom));
        tel.Pump();
        Assert.NotNull(tel.TryGetQueryView(custom));

        now += 121_000;
        Assert.Null(tel.TryGetView("overview|24h"));
        Assert.Null(tel.TryGetQueryView(custom));
        Assert.Equal(1, tel.PendingSnapshotBuilds);
    }

    [Fact]
    public void TraceSqlAndJson_AreMaterializedOffTheRequestThread()
    {
        string path = Path.Combine(Path.GetTempPath(), $"trace-view-{Guid.NewGuid():N}.db");
        try
        {
            var db = new TelemetryDb(path);
            db.Migrate();
            long now = 1_760_000_400_000;
            using var views = new TelemetryTraceViews(db, () => now);
            var query = new TelemetryTraceViews.ListQuery("24h", null, null, null, null, null, null, "recent", 100);

            // The first lookup only schedules work. Every subsequent lookup reads
            // an immutable entry; no SQLite connection is opened by this thread.
            var first = views.TryGetList(query);
            Assert.Null(first);
            Assert.True(SpinWait.SpinUntil(() => views.TryGetList(query) != null, 5_000));
            var entry = views.TryGetList(query)!;
            Assert.Equal(200, entry.StatusCode);
            using (var doc = JsonDocument.Parse(entry.RawBody))
                Assert.True(doc.RootElement.GetProperty("persisted").GetBoolean());

            Assert.Null(views.TryGetDetail(0x1234));
            Assert.True(SpinWait.SpinUntil(() => views.TryGetDetail(0x1234) != null, 5_000));
            Assert.Equal(404, views.TryGetDetail(0x1234)!.StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch { }
        }
    }
}
