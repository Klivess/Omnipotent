using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Projects analytics reads (13 Nov 2026). Live over 30 days the fleet report took p50 77 s and p95
/// 732 s, and 16 of 17 requests were abandoned by the browser: every visit after a five-minute
/// boundary rebuilt every project's snapshot on the request. A read now never waits for a rebuild
/// once a report exists, runs one build at a time per question, and tells a cold caller it is building.
/// </summary>
public class AnalyticsReadTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private sealed class Report { public int Version; }

    [Fact]
    public async Task ColdQuestion_WaitsBriefly_ThenSaysItIsBuilding_ThenAnswers()
    {
        var reader = new StaleWhileRebuilding<Report>(_ => { });
        var gate = new TaskCompletionSource();
        Report Build() { gate.Task.Wait(); return new Report { Version = 1 }; }

        var first = await reader.ReadAsync("30d", Build, force: false, Short);
        Assert.Null(first.Value);
        Assert.True(first.Building);

        // Asking again while it builds starts nothing new.
        var again = await reader.ReadAsync("30d", Build, force: false, Short);
        Assert.True(again.Building);
        Assert.Equal(1, reader.Running);

        gate.SetResult();
        await WaitUntil(() => reader.Running == 0);
        var done = await reader.ReadAsync("30d", Build, force: false, Short);
        Assert.Equal(1, done.Value!.Version);
        Assert.False(done.Stale);
        Assert.False(done.Building);
    }

    [Fact]
    public async Task AQuickBuild_IsAnsweredInTheSameRequest()
    {
        var reader = new StaleWhileRebuilding<Report>(_ => { });
        var read = await reader.ReadAsync("7d", () => new Report { Version = 3 }, force: false, TimeSpan.FromSeconds(5));
        Assert.Equal(3, read.Value!.Version);
        Assert.False(read.Building);
    }

    [Fact]
    public async Task AnOldReport_IsServedAtOnce_WhileOneBuildReplacesIt()
    {
        DateTime now = new(2026, 11, 13, 12, 1, 0, DateTimeKind.Utc);
        var reader = new StaleWhileRebuilding<Report>(_ => { }, () => now);
        int version = 0;
        var gate = new TaskCompletionSource();
        Report Build() { if (version > 0) gate.Task.Wait(); return new Report { Version = ++version }; }

        Assert.Equal(1, (await reader.ReadAsync("30d", Build, false, TimeSpan.FromSeconds(5))).Value!.Version);
        // Same five-minute bucket: fresh, no build.
        now = now.AddMinutes(2);
        var fresh = await reader.ReadAsync("30d", Build, false, Short);
        Assert.False(fresh.Stale);
        Assert.Equal(0, reader.Running);

        // Next bucket: the old report at once, flagged, and exactly one rebuild however often it is asked.
        now = now.AddMinutes(5);
        var stale = await reader.ReadAsync("30d", Build, false, Short);
        await reader.ReadAsync("30d", Build, false, Short);
        Assert.Equal(1, stale.Value!.Version);
        Assert.True(stale.Stale);
        Assert.True(stale.Building);
        Assert.Equal(1, reader.Running);

        gate.SetResult();
        await WaitUntil(() => reader.Running == 0);
        var replaced = await reader.ReadAsync("30d", Build, false, Short);
        Assert.Equal(2, replaced.Value!.Version);
        Assert.False(replaced.Stale);
    }

    [Fact]
    public async Task Refresh_ServesTheLastReport_AndRebuilds()
    {
        var reader = new StaleWhileRebuilding<Report>(_ => { });
        int version = 0;
        Report Build() => new() { Version = ++version };
        await reader.ReadAsync("30d", Build, false, TimeSpan.FromSeconds(5));

        var forced = await reader.ReadAsync("30d", Build, force: true, Short);
        Assert.True(forced.Stale);
        await WaitUntil(() => reader.Running == 0);
        Assert.Equal(2, (await reader.ReadAsync("30d", Build, false, Short)).Value!.Version);
    }

    [Fact]
    public async Task AFailedFirstBuild_ReachesTheCaller_AndTheNextReadTriesAgain()
    {
        var logged = new List<string>();
        var reader = new StaleWhileRebuilding<Report>(logged.Add);
        bool fail = true;
        Report Build() => fail ? throw new ArgumentException("bad range") : new Report { Version = 7 };

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("custom", Build, false, TimeSpan.FromSeconds(5)));
        Assert.Single(logged);
        fail = false;
        Assert.Equal(7, (await reader.ReadAsync("custom", Build, false, TimeSpan.FromSeconds(5))).Value!.Version);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
