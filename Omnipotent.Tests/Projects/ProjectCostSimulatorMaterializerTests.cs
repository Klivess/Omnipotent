using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectCostSimulatorMaterializerTests
{
    [Fact]
    public async Task CustomRead_QueuesBoundedWorkAndNeverBuildsOnCallingThread()
    {
        int builds = 0;
        using var cts = new CancellationTokenSource();
        var materializer = NewMaterializer((range, _) =>
        {
            Interlocked.Increment(ref builds);
            return Snapshot(range);
        });
        string from = "2026-09-01T00:00:00Z";
        string to = "2026-09-02T00:00:00Z";

        var cold = materializer.Read("custom", from, to, includeArchived: true);
        Assert.True(cold.Pending);
        Assert.Equal(0, Volatile.Read(ref builds));

        materializer.Start(cts.Token);
        CostSimulatorReadResult result = cold;
        for (int attempt = 0; attempt < 100 && result.Json == null; attempt++)
        {
            await Task.Delay(20);
            result = materializer.Read("custom", from, to, includeArchived: true);
        }
        cts.Cancel();
        Assert.NotNull(result.Json);
        Assert.False(result.Pending);
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.Contains("\"scope\":\"cost-simulator\"", result.Json);
    }

    [Fact]
    public async Task PersistedDefaultSnapshot_IsReturnedWithoutCallingBuilder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cost-simulator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var range = ProjectAnalyticsCalculator.ResolveRange("30d",
                DateTime.UtcNow.AddDays(-60), DateTime.UtcNow);
            string json = ProjectsRoutes.Json(Snapshot(range));
            await File.WriteAllTextAsync(Path.Combine(directory, "cost-simulator-30d-all.json"), json);
            int builds = 0;
            var materializer = new ProjectCostSimulatorMaterializer(
                () => { Interlocked.Increment(ref builds); throw new Exception("default builder called"); },
                (_, _) => throw new Exception("custom builder called"), snapshotDirectory: directory);

            await materializer.LoadAsync();
            var result = materializer.Read("30d", null, null, includeArchived: true);

            Assert.NotNull(result.Json);
            Assert.False(result.Pending);
            Assert.Equal(0, builds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StartupRejectsAnExpiredDefaultSnapshot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cost-simulator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var range = ProjectAnalyticsCalculator.ResolveRange("30d",
                DateTime.UtcNow.AddDays(-60), DateTime.UtcNow);
            var old = Snapshot(range);
            old.GeneratedAt = DateTime.UtcNow.AddMinutes(-3);
            await File.WriteAllTextAsync(Path.Combine(directory, "cost-simulator-30d-all.json"),
                ProjectsRoutes.Json(old));
            var materializer = new ProjectCostSimulatorMaterializer(
                () => throw new Exception("not called"), (_, _) => throw new Exception("not called"),
                snapshotDirectory: directory);
            await materializer.LoadAsync();
            Assert.True(materializer.Read("30d", null, null, includeArchived: true).Pending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CustomQueue_RejectsExcessDistinctWindowsWithoutScanningJournals()
    {
        int builds = 0;
        var materializer = NewMaterializer((range, _) =>
        {
            Interlocked.Increment(ref builds);
            return Snapshot(range);
        });
        for (int day = 1; day <= 8; day++)
        {
            var result = materializer.Read("custom",
                $"2026-01-{day:00}T00:00:00Z", $"2026-01-{day:00}T01:00:00Z", true);
            Assert.True(result.Pending);
            Assert.False(result.Busy);
        }

        var excess = materializer.Read("custom",
            "2026-01-09T00:00:00Z", "2026-01-09T01:00:00Z", true);
        Assert.True(excess.Busy);
        Assert.Equal(0, builds);
    }

    [Fact]
    public void CustomRead_ReturnsStableBoundsWhenEndIsClippedToNow()
    {
        var materializer = NewMaterializer((range, _) => Snapshot(range));
        var start = DateTime.UtcNow.AddHours(-1).ToString("O");
        var end = DateTime.UtcNow.AddSeconds(30).ToString("O");

        var pending = materializer.Read("custom", start, end, true);

        Assert.True(pending.Pending);
        Assert.NotNull(pending.FromUtc);
        Assert.NotNull(pending.ToUtc);
        Assert.True(DateTime.Parse(pending.ToUtc!).ToUniversalTime() < DateTime.Parse(end).ToUniversalTime());
        Assert.True(materializer.Read("custom", pending.FromUtc, pending.ToUtc, true).Pending);
    }

    private static ProjectCostSimulatorMaterializer NewMaterializer(
        Func<AnalyticsRange, bool, CostSimulationSnapshot> custom)
        => new(() => new Dictionary<string, CostSimulationSnapshot>(), custom,
            snapshotDirectory: Path.Combine(Path.GetTempPath(),
                "cost-simulator-tests-" + Guid.NewGuid().ToString("N")));

    private static CostSimulationSnapshot Snapshot(AnalyticsRange range) => new()
    {
        GeneratedAt = DateTime.UtcNow,
        Range = range,
        Totals = new CostSimulationBuckets { TotalTokens = 42 },
    };
}
