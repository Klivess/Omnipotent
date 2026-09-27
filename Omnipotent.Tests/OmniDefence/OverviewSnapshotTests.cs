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

        now = now.AddMinutes(3);
        Assert.Null(first.CurrentJson);
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
