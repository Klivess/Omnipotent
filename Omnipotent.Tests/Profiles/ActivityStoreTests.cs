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
    public void Counts_AreServedFromMemory_AndAgreeWithTheDatabase()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new List<object>();
        for (int i = 0; i < 40; i++)
            batch.Add(new ActivityRequest { ProfileId = i % 2 == 0 ? "p1" : "p2", TsMs = Ms(now.AddMinutes(-i * 30)), Route = "/a", Status = i % 5 == 0 ? 403 : 200 });
        // Older than the 24 h window: in the database, not in the directory's counters.
        batch.Add(new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddDays(-3)), Route = "/old", Status = 200 });
        store.WriteBatch(batch);

        long since = Ms(now.AddHours(-24));
        var fromMemory = store.GetCountsSince(since);
        var fromDatabase = store.CountsFromDatabase(since);
        Assert.Equal(fromDatabase.OrderBy(k => k.Key), fromMemory.OrderBy(k => k.Key));

        // A look-back beyond the window still works (it reads the database).
        Assert.Equal(21L, store.GetCountsSince(Ms(now.AddDays(-4)))["p1"].Requests);
        Assert.Equal(Ms(now), store.GetLastSeen()["p1"]);
    }

    [Fact]
    public void Counts_SurviveARestart_WithoutCountingAnythingTwice()
    {
        var now = DateTimeOffset.UtcNow;
        store.WriteBatch(new object[]
        {
            new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-90)), Route = "/a", Status = 200 },
            new ActivityRequest { ProfileId = "p1", TsMs = Ms(now.AddMinutes(-20)), Route = "/b", Status = 403 },
            new ActivityRequest { ProfileId = "p2", TsMs = Ms(now.AddMinutes(-5)), Route = "/c", Status = 200 },
        });
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new ProfileActivityStore(Path.Combine(dir, "activity.db"), _ => { });
        reopened.Start();
        // Written after the restart: counted as it is written, never again by the seed.
        reopened.WriteBatch(new object[] { new ActivityRequest { ProfileId = "p1", TsMs = Ms(now), Route = "/d", Status = 200 } });
        Assert.True(reopened.WaitUntilSeeded(TimeSpan.FromSeconds(10)));

        var counts = reopened.GetCountsSince(Ms(now.AddHours(-24)));
        Assert.Equal((3L, 1L), counts["p1"]);
        Assert.Equal((1L, 0L), counts["p2"]);
        Assert.Equal(Ms(now), reopened.GetLastSeen()["p1"]);
        Assert.Equal(Ms(now.AddMinutes(-5)), reopened.GetLastSeen()["p2"]);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Summary_SinglePass_MatchesAnIndependentTally()
    {
        var now = DateTimeOffset.UtcNow;
        var random = new Random(7);
        string[] services = { "OmniTrader", "KliveCloud", "Account" };
        string[] perms = { "a.b.read", "a.b.write", "c.d.read", "" };
        string?[] ips = { "1.1.1.1", "2.2.2.2", "", null };
        var requests = Enumerable.Range(0, 400).Select(_ => new ActivityRequest
        {
            ProfileId = "p1",
            TsMs = Ms(now.AddMinutes(-random.Next(0, 6 * 24 * 60))),
            Route = "/r" + random.Next(0, 12),
            PermKey = perms[random.Next(perms.Length)],
            Service = services[random.Next(services.Length)],
            Status = new[] { 200, 200, 200, 403, 401, 500 }[random.Next(6)],
            Ip = ips[random.Next(ips.Length)],
        }).ToList();
        store.WriteBatch(requests.Cast<object>().ToList());

        var summary = store.GetSummary("p1", TimeSpan.FromDays(7), now.AddMinutes(1));
        Assert.Equal(requests.Count, summary.Requests);
        Assert.Equal(requests.Count(r => r.Status is 401 or 403), summary.Denied);
        Assert.Equal(requests.Count(r => r.Status >= 500), summary.Errors);
        Assert.Equal(requests.Select(r => r.Route).Distinct().Count(), summary.DistinctRoutes);
        Assert.Equal((long)requests.Count, summary.Buckets.Sum(b => b.ByService.Values.Sum()));
        Assert.Equal(services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), summary.Services);

        var expectedTop = requests.GroupBy(r => r.PermKey ?? "").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(15).ToList();
        Assert.Equal(expectedTop.Select(g => (g.Key, (long)g.Count(), (long)g.Count(r => r.Status is 401 or 403), g.Max(r => r.TsMs))),
            summary.TopPermissions.Select(k => (k.Key, k.Count, k.Denied, k.LastMs)));

        var expectedIps = requests.Where(r => !string.IsNullOrEmpty(r.Ip)).GroupBy(r => r.Ip!).OrderByDescending(g => g.Max(r => r.TsMs)).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedIps.Select(g => (g.Key, (long)g.Count(), g.Min(r => r.TsMs), g.Max(r => r.TsMs))),
            summary.Ips.Select(i => (i.Ip, i.Count, i.FirstMs, i.LastMs)));
    }

    [Fact]
    public async Task SummaryAsync_ReusesTheComputedSummary()
    {
        store.WriteBatch(new object[] { new ActivityRequest { ProfileId = "p1", TsMs = Ms(DateTimeOffset.UtcNow), Route = "/a", Status = 200 } });
        var first = await store.GetSummaryAsync("p1", TimeSpan.FromHours(24));
        Assert.Equal(1, first.Requests);

        store.WriteBatch(new object[] { new ActivityRequest { ProfileId = "p1", TsMs = Ms(DateTimeOffset.UtcNow), Route = "/b", Status = 200 } });
        var second = await store.GetSummaryAsync("p1", TimeSpan.FromHours(24));
        Assert.Same(first, second); // still fresh: not recomputed
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
