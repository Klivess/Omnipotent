using System.Diagnostics;
using Omnipotent.Services.Omniscience;

namespace Omnipotent.Tests.Omniscience;

public sealed class OmniscienceBackgroundSnapshotsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "omnipotent-omniscience-snapshot-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ColdRead_DoesNotInvokeTheExpensiveBuilder()
    {
        using var snapshots = new OmniscienceBackgroundSnapshots(directory);
        int builds = 0;
        snapshots.Register("stats/overview", "overview.json", TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(3), () => { Interlocked.Increment(ref builds); return "{\"messages\":7}"; },
            startWorker: false);

        var stopwatch = Stopwatch.StartNew();
        Assert.Null(snapshots.TryGet("stats/overview"));
        stopwatch.Stop();
        Assert.Equal(0, builds);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));

        await snapshots.RefreshNowAsync("stats/overview");
        Assert.Equal("{\"messages\":7}", snapshots.TryGet("stats/overview"));
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task SavedPayloadSurvivesRestart_ThenExpiresWithoutRecomputingOnRead()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (var first = new OmniscienceBackgroundSnapshots(directory, utcNow: () => now))
        {
            first.Register("briefing/preview", "preview.json", TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(15), () => "{\"markdown\":\"Today\"}", startWorker: false);
            await first.RefreshNowAsync("briefing/preview");
        }

        int unexpectedBuilds = 0;
        using var reopened = new OmniscienceBackgroundSnapshots(directory, utcNow: () => now);
        reopened.Register("briefing/preview", "preview.json", TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(15), () => { Interlocked.Increment(ref unexpectedBuilds); return "{}"; },
            startWorker: false);
        Assert.Equal("{\"markdown\":\"Today\"}", reopened.TryGet("briefing/preview"));

        now = now.AddMinutes(16);
        Assert.Null(reopened.TryGet("briefing/preview"));
        Assert.Equal(0, unexpectedBuilds);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheLastGoodSnapshot()
    {
        using var snapshots = new OmniscienceBackgroundSnapshots(directory);
        string payload = "{\"messages\":1}";
        snapshots.Register("stats/overview", "overview.json", TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(3), () => payload, startWorker: false);
        await snapshots.RefreshNowAsync("stats/overview");

        payload = "invalid JSON";
        await Assert.ThrowsAnyAsync<Exception>(() => snapshots.RefreshNowAsync("stats/overview"));
        Assert.Equal("{\"messages\":1}", snapshots.TryGet("stats/overview"));
    }

    [Fact]
    public async Task WorkerBuildsSnapshotWithoutARequest()
    {
        using var snapshots = new OmniscienceBackgroundSnapshots(directory);
        snapshots.Register("stats/overview", "overview.json", TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(3), () => "{\"messages\":12}");

        string? payload = null;
        for (int i = 0; i < 100 && payload == null; i++)
        {
            await Task.Delay(20);
            payload = snapshots.TryGet("stats/overview");
        }
        Assert.Equal("{\"messages\":12}", payload);
        string savedPath = Path.Combine(directory, "overview.json");
        for (int i = 0; i < 100 && !File.Exists(savedPath); i++) await Task.Delay(20);
        Assert.True(File.Exists(savedPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}
