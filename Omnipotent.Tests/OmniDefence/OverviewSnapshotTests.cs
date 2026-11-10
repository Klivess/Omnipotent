using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence;

public sealed class OverviewSnapshotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "omnidefence-overview-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RefreshPublishesExactBodyAndRestartLoadsItWithoutComputing()
    {
        string path = Path.Combine(directory, "overview.snapshot.json");
        DateTimeOffset now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        int builds = 0;
        const string body = "{\"requests24h\":12,\"topRoutes\":[{\"route\":\"/test\",\"hits\":5}]}";
        var first = new OmniDefenceOverviewSnapshot(path, () =>
        {
            builds++;
            return Task.FromResult(body);
        }, utcNow: () => now);

        Assert.Null(first.CurrentJson);
        Assert.Equal(0, builds);
        await first.RefreshOnceAsync();
        Assert.Equal(body, first.CurrentJson);
        Assert.Equal(body, first.CurrentJson);
        Assert.Equal(1, builds);

        var restarted = new OmniDefenceOverviewSnapshot(path, () =>
        {
            builds++;
            throw new InvalidOperationException("A read must never build the overview.");
        }, utcNow: () => now.AddSeconds(20));
        restarted.LoadFromDisk();
        Assert.Equal(body, restarted.CurrentJson);
        Assert.Equal(1, builds);

        // Past the freshness window the overview is still served, saying how old it is. Withholding it
        // left the live page with nothing at all for as long as builds ran slow.
        now = now.AddMinutes(3);
        var stale = Newtonsoft.Json.Linq.JObject.Parse(first.CurrentJson!);
        Assert.True(stale.Value<bool>("stale"));
        Assert.Equal(180, stale.Value<long>("ageSeconds"));
        Assert.Equal(12, stale.Value<int>("requests24h"));
        Assert.Equal("/test", stale["topRoutes"]![0]!.Value<string>("route"));
    }

    [Fact]
    public async Task ASnapshotFromBeforeARestartIsServedHoweverOldUntilTheFirstBuildLands()
    {
        string path = Path.Combine(directory, "overview.snapshot.json");
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var before = new OmniDefenceOverviewSnapshot(path, () => Task.FromResult("{\"requests24h\":7}"), utcNow: () => now);
        await before.RefreshOnceAsync();

        var gate = new TaskCompletionSource<string>();
        var after = new OmniDefenceOverviewSnapshot(path, () => gate.Task, utcNow: () => now.AddHours(5));
        after.LoadFromDisk();

        var served = Newtonsoft.Json.Linq.JObject.Parse(after.CurrentJson!);
        Assert.True(served.Value<bool>("stale"));
        Assert.Equal(5 * 3600, served.Value<long>("ageSeconds"));
        Assert.Equal(7, served.Value<int>("requests24h"));

        // While a build runs, the status says so and names the step it has reached.
        Task building = after.RefreshOnceAsync();
        after.ReportStep("requests in the last 7 days");
        var status = Newtonsoft.Json.Linq.JObject.FromObject(after.Status());
        Assert.True(status.Value<bool>("building"));
        Assert.Equal("requests in the last 7 days", status.Value<string>("step"));
        Assert.True(status.Value<bool>("stale"));
        now = now.AddSeconds(90);
        Assert.Equal(90, Newtonsoft.Json.Linq.JObject.FromObject(after.Status()).Value<long>("buildRunningSeconds"));

        gate.SetResult("{\"requests24h\":9}");
        await building;
        Assert.Equal("{\"requests24h\":9}", after.CurrentJson);
        Assert.False(Newtonsoft.Json.Linq.JObject.FromObject(after.Status()).Value<bool>("building"));
    }

    [Fact]
    public void ASlowBuildIsReportedAtOnceThenOnlyEveryHalfHourWhileBuildsStaySlow()
    {
        DateTimeOffset first = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(OmniDefenceOverviewSnapshot.ShouldReportSlowBuild(null, first));
        // Builds that keep overrunning arrive every two or three minutes: one line each would be
        // about twenty errors an hour for a condition the status route already shows.
        Assert.False(OmniDefenceOverviewSnapshot.ShouldReportSlowBuild(first, first.AddMinutes(3)));
        Assert.False(OmniDefenceOverviewSnapshot.ShouldReportSlowBuild(first, first.AddMinutes(29)));
        Assert.True(OmniDefenceOverviewSnapshot.ShouldReportSlowBuild(first, first.AddMinutes(30)));
    }

    [Fact]
    public async Task FailedRefreshKeepsLastPublishedSnapshot()
    {
        string path = Path.Combine(directory, "overview.snapshot.json");
        bool fail = false;
        var snapshot = new OmniDefenceOverviewSnapshot(path, () =>
            fail ? throw new InvalidOperationException("temporary database failure")
                 : Task.FromResult("{\"requests24h\":1}"));

        await snapshot.RefreshOnceAsync();
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.RefreshOnceAsync());
        Assert.Equal("{\"requests24h\":1}", snapshot.CurrentJson);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
