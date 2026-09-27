using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Tests.OmniTrader;

public sealed class RouteSnapshotTests
{
    [Fact]
    public async Task Snapshot_PersistsAtomically_RejectsStaleAndCorruptData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "route-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "deployments.json");
        DateTime asOf = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            var snapshot = new PrecomputedRouteSnapshot(path, JTokenType.Array, TimeSpan.FromMinutes(2));
            await snapshot.PublishAsync("[{\"Id\":\"first\"}]", asOf, CancellationToken.None);
            Assert.Equal("[{\"Id\":\"first\"}]", snapshot.GetBody(asOf.AddSeconds(90)));
            Assert.Null(snapshot.GetBody(asOf.AddMinutes(3)));

            var restored = new PrecomputedRouteSnapshot(path, JTokenType.Array, TimeSpan.FromMinutes(2));
            Assert.True(restored.Restore(asOf.AddSeconds(90)));
            Assert.Equal("[{\"Id\":\"first\"}]", restored.GetBody(asOf.AddSeconds(90)));
            Assert.False(new PrecomputedRouteSnapshot(path, JTokenType.Array,
                TimeSpan.FromMinutes(2)).Restore(asOf.AddMinutes(3)));

            await snapshot.PublishAsync("[{\"Id\":\"second\"}]", asOf.AddSeconds(91), CancellationToken.None);
            Assert.Contains("second", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            await File.WriteAllTextAsync(path, "{broken");
            Assert.False(new PrecomputedRouteSnapshot(path, JTokenType.Array,
                TimeSpan.FromMinutes(2)).Restore(asOf.AddSeconds(92)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_BuildsOnlyInBackground_AndCoalescesRefreshRequests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "route-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cts = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBody = new TaskCompletionSource<SnapshotBuild>(TaskCreationOptions.RunContinuationsAsynchronously);
        int builds = 0;
        try
        {
            var worker = new RouteSnapshotWorker(
                new PrecomputedRouteSnapshot(Path.Combine(directory, "jobs.json"),
                    JTokenType.Array, TimeSpan.FromMinutes(2)),
                _ =>
                {
                    int build = Interlocked.Increment(ref builds);
                    if (build == 1)
                    {
                        firstStarted.TrySetResult();
                        return firstBody.Task;
                    }
                    return Task.FromResult(new SnapshotBuild("[{\"version\":2}]", DateTime.UtcNow));
                },
                _ => Task.CompletedTask,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            worker.Start(cts.Token);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 100; i++) Assert.Null(worker.GetBody(DateTime.UtcNow));
            Assert.Equal(1, Volatile.Read(ref builds));

            worker.RequestRefresh();
            worker.RequestRefresh();
            firstBody.SetResult(new SnapshotBuild("[{\"version\":1}]", DateTime.UtcNow));
            await WaitUntilAsync(() => worker.GetBody(DateTime.UtcNow)?.Contains("\"version\":2") == true);
            Assert.Equal(2, Volatile.Read(ref builds));
        }
        finally
        {
            cts.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BacktestSummary_MatchesFullJobMetrics_WithoutReturningResultArrays()
    {
        string directory = Path.Combine(Path.GetTempPath(), "backtest-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = new OmniTraderDb(Path.Combine(directory, "jobs.db"));
            await db.InitialiseAsync();
            var repo = new BacktestJobRepository(db);
            var config = new BacktestConfig
            {
                StrategyClass = "TestStrategy", Coin = "BTC", Currency = "USDT",
                Interval = TimeInterval.OneHour, CandleCount = 200
            };
            var result = new BacktestResult
            {
                InitialEquity = 10_000m,
                FinalEquity = 10_850m,
                TotalTrades = 4,
                WinningTrades = 3,
                SharpeRatio = 1.125m,
                MaxDrawdownPercent = 4.75m,
                Candles = Enumerable.Range(0, 200).Select(i => new OHLCCandle(
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                    1m, 2m, 0.5m, 1.5m, 100m)).ToList()
            };
            await repo.InsertAsync(new BacktestJobRow
            {
                Id = "complete", StrategyClass = config.StrategyClass, Config = config,
                Status = BacktestJobStatus.Succeeded, ProgressPct = 100,
                QueuedUtc = DateTime.UtcNow, Result = result
            });
            await repo.InsertAsync(new BacktestJobRow
            {
                Id = "queued", StrategyClass = config.StrategyClass, Config = config,
                Status = BacktestJobStatus.Queued, QueuedUtc = DateTime.UtcNow.AddSeconds(1)
            });

            var summaries = await repo.ListRecentSummariesAsync();
            var full = await repo.GetAsync("complete");
            Assert.NotNull(full?.Result);
            var summary = Assert.Single(summaries, s => s.Id == "complete");
            Assert.Equal(full.Result.TotalPnLPercent, summary.TotalPnLPercent);
            Assert.Equal(full.Result.WinRate, summary.WinRate);
            Assert.Equal(full.Result.SharpeRatio, summary.SharpeRatio);
            Assert.Equal(full.Result.MaxDrawdownPercent, summary.MaxDrawdownPercent);
            Assert.Equal(full.Result.TotalTrades, summary.TotalTrades);
            var queued = Assert.Single(summaries, s => s.Id == "queued");
            Assert.Null(queued.TotalPnLPercent);
            Assert.Null(queued.TotalTrades);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Snapshot did not refresh in time.");
    }
}
