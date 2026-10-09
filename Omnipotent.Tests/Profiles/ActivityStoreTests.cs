using Omnipotent.Profiles.Activity;

namespace Omnipotent.Tests.Profiles;

public sealed class ActivityStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "km-activity-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileActivityStore store;

    public ActivityStoreTests()
    {
        Directory.CreateDirectory(dir);
        store = new ProfileActivityStore(Path.Combine(dir, "activity.db"), _ => { });
        store.Start();
    }

    public void Dispose()
    {
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    [Fact]
    public void Timeline_MergesRequestsPagesAndEvents_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-3)), Route = "/a", PermKey = "omnitrader.status.view", Service = "OmniTrader", Status = 200 },
            new ActivityPageView { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-2)), Path = "/omnitrader", DwellMs = 60_000 },
            new ActivityEvent { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-1)), Kind = "login" },
            new ActivityRequest { ProfileId = "p2", TsMs = Ms(now), Route = "/other", Status = 200 },
        });

        var items = store.GetTimeline("p1", null, null, 10);
        Assert.Equal(new[] { "event", "page", "request" }, items.Select(i => i.Type));
        Assert.Equal("/a", items[2].Route);

        var requestsOnly = store.GetTimeline("p1", null, null, 10, new HashSet<string> { "request" });
        Assert.Single(requestsOnly);

        var older = store.GetTimeline("p1", items[0].TsMs, null, 10);
        Assert.Equal(2, older.Count); // cursor pagination
    }

    [Fact]
    public void Summary_CountsByServiceDenialsAndUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new List<object>();
        for (int i = 0; i < 5; i++)
            batch.Add(new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-i)), Route = "/a", PermKey = "omnitrader.status.view", Service = "OmniTrader", Status = 200, Ip = "1.1.1.1" });
        batch.Add(new ActivityRequest { ProfileId = "p1", TsMs = Ms(now), Route = "/b", PermKey = "omnitrader.orders.place", Service = "OmniTrader", Status = 403, DenyReason = "MissingPermission", Ip = "2.2.2.2" });
        store.WriteBatch(batch);

        var summary = store.GetSummary("p1", TimeSpan.FromHours(24), now.AddMinutes(1));
        Assert.Equal(6, summary.Requests);
        Assert.Equal(1, summary.Denied);
        Assert.Contains("OmniTrader", summary.Services);
        Assert.Equal(2, summary.Ips.Count);
        Assert.Equal("omnitrader.status.view", summary.TopPermissions[0].Key);
        Assert.Single(summary.RecentDenials);

        var usage = store.GetPermissionUsage("p1");
        Assert.Equal(5, usage.Single(u => u.Key == "omnitrader.status.view").Count);
        Assert.Equal(1, usage.Single(u => u.Key == "omnitrader.orders.place").Denied);
        Assert.True(store.GetLastSeen()["p1"] > 0);
        Assert.Equal((6L, 1L), store.GetCountsSince(Ms(now.AddHours(-1)))["p1"]);
    }

    [Fact]
    public void Prune_DropsOldRawRows_ButKeepsUsageHistory()
    {
        var now = DateTimeOffset.UtcNow;
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddDays(-200)), Route = "/old", PermKey = "x.y.z", Status = 200 },
            new ActivityRequest { ProfileId = "p1", TsMs = Ms(now), Route = "/new", PermKey = "x.y.z", Status = 200 },
        });
        store.Prune(now);
        Assert.Single(store.GetTimeline("p1", null, null, 10));
        Assert.Equal(2, store.GetPermissionUsage("p1").Single().Count);
    }

    [Fact]
    public void Enqueue_RaisesTheLiveFeed()
    {
        ActivityRequest? seen = null;
        store.RequestRecorded += r => seen = r;
        store.EnqueueRequest(new ActivityRequest { ProfileId = "p9", TsMs = Ms(DateTimeOffset.UtcNow), Route = "/live" });
        Assert.Equal("/live", seen?.Route);
    }
}
