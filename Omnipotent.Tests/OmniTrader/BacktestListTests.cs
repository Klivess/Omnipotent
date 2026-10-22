using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Tests.OmniTrader;

/// <summary>
/// The lab's job list reads backtest_job_list (schema v7), never backtest_jobs' result blobs: the old query
/// sorted every job ever run by a column stored behind its multi-megabyte result and parsed 100 results
/// twice per request (3-4 s per list). Every write path must keep the two tables in step.
/// </summary>
public sealed class BacktestListTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "backtest-list-" + Guid.NewGuid().ToString("N"));
    private readonly OmniTraderDb db;
    private readonly BacktestJobRepository repo;

    public BacktestListTests()
    {
        Directory.CreateDirectory(directory);
        db = new OmniTraderDb(Path.Combine(directory, "jobs.db"));
        db.InitialiseAsync().GetAwaiter().GetResult();
        repo = new BacktestJobRepository(db);
    }

    public void Dispose()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    private static BacktestConfig Config(string coin = "BTC") => new()
    {
        StrategyClass = "TestStrategy", Coin = coin, Currency = "USDT", Interval = TimeInterval.OneHour, CandleCount = 200
    };

    private static BacktestResult Result() => new()
    {
        InitialEquity = 10_000m,
        FinalEquity = 11_000m,
        TotalTrades = 5,
        WinningTrades = 4,
        SharpeRatio = 1.5m,
        MaxDrawdownPercent = 3.25m,
        StrategyErrors = 2,
        Analysis = new BacktestAnalysis { CagrPct = 12.5, Sortino = 2.25, Calmar = 1.75 },
    };

    private Task InsertQueuedAsync(string id, DateTime queuedUtc, string coin = "BTC") => repo.InsertAsync(new BacktestJobRow
    {
        Id = id, StrategyClass = "TestStrategy", Config = Config(coin), Status = BacktestJobStatus.Queued, QueuedUtc = queuedUtc
    });

    [Fact]
    public async Task A_Job_Moves_Through_The_List_From_Queued_To_Finished_With_Its_Metrics()
    {
        DateTime queued = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await InsertQueuedAsync("job", queued);
        var (rows, total) = await repo.QuerySummariesAsync(new BacktestJobFilter());
        var row = Assert.Single(rows);
        Assert.Equal(1, total);
        Assert.Equal(BacktestJobStatus.Queued, row.Status);
        Assert.Null(row.StartedUtc);
        Assert.Null(row.TotalTrades);

        await repo.StartAsync("job", queued.AddMinutes(1));
        await repo.CompleteAsync("job", BacktestJobStatus.Succeeded, Result(), null, queued.AddMinutes(2));

        (rows, _) = await repo.QuerySummariesAsync(new BacktestJobFilter());
        row = Assert.Single(rows);
        Assert.Equal(BacktestJobStatus.Succeeded, row.Status);
        Assert.Equal(queued.AddMinutes(1), row.StartedUtc);
        Assert.Equal(queued.AddMinutes(2), row.FinishedUtc);
        Assert.Equal(10m, row.TotalPnLPercent);
        Assert.Equal(80m, row.WinRate);
        Assert.Equal(1.5m, row.SharpeRatio);
        Assert.Equal(3.25m, row.MaxDrawdownPercent);
        Assert.Equal(5, row.TotalTrades);
        Assert.Equal(12.5, row.CagrPct);
        Assert.Equal(2.25, row.Sortino);
        Assert.Equal(1.75, row.Calmar);
        Assert.Equal(2, row.StrategyErrors);

        var recent = Assert.Single(await repo.ListRecentSummariesAsync());
        Assert.Equal(row.TotalPnLPercent, recent.TotalPnLPercent);
        Assert.Equal(row.FinishedUtc, recent.FinishedUtc);
    }

    [Fact]
    public async Task Orphaned_Running_Jobs_Show_As_Failed_With_The_Reason()
    {
        DateTime queued = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await InsertQueuedAsync("running", queued);
        await repo.StartAsync("running", queued.AddMinutes(1));
        await InsertQueuedAsync("waiting", queued.AddMinutes(2));

        await repo.MarkOrphansFailedAsync("Server restarted mid-run.");

        var (rows, _) = await repo.QuerySummariesAsync(new BacktestJobFilter());
        var failed = Assert.Single(rows, r => r.Id == "running");
        Assert.Equal(BacktestJobStatus.Failed, failed.Status);
        Assert.Equal("Server restarted mid-run.", failed.Error);
        Assert.NotNull(failed.FinishedUtc);
        var waiting = Assert.Single(rows, r => r.Id == "waiting");
        Assert.Equal(BacktestJobStatus.Queued, waiting.Status);
        Assert.Null(waiting.Error);
    }

    [Fact]
    public async Task Filters_Order_And_Paging_Still_Apply()
    {
        DateTime t0 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++) await InsertQueuedAsync("j" + i, t0.AddMinutes(i), i % 2 == 0 ? "BTC" : "ETH");
        await repo.StartAsync("j4", t0.AddMinutes(10));

        var (newest, total) = await repo.QuerySummariesAsync(new BacktestJobFilter { Limit = 2 });
        Assert.Equal(5, total);
        Assert.Equal(new[] { "j4", "j3" }, newest.Select(r => r.Id));
        var (page2, _) = await repo.QuerySummariesAsync(new BacktestJobFilter { Limit = 2, Offset = 2 });
        Assert.Equal(new[] { "j2", "j1" }, page2.Select(r => r.Id));

        var (eth, ethTotal) = await repo.QuerySummariesAsync(new BacktestJobFilter { Symbol = "eth" });
        Assert.Equal(2, ethTotal);
        Assert.All(eth, r => Assert.Equal("ETH", r.Config.Coin));

        var (running, runningTotal) = await repo.QuerySummariesAsync(new BacktestJobFilter { Status = BacktestJobStatus.Running });
        Assert.Equal(1, runningTotal);
        Assert.Equal("j4", Assert.Single(running).Id);

        var (prefixed, _) = await repo.QuerySummariesAsync(new BacktestJobFilter { StrategyClass = "Test*" });
        Assert.Equal(5, prefixed.Count);
    }

    [Fact]
    public async Task Upgrading_Fills_The_List_From_Jobs_Stored_Before_It_Existed()
    {
        DateTime queued = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await repo.InsertAsync(new BacktestJobRow
        {
            Id = "old", StrategyClass = "TestStrategy", Config = Config(), Status = BacktestJobStatus.Succeeded,
            ProgressPct = 100, QueuedUtc = queued, StartedUtc = queued.AddMinutes(1), FinishedUtc = queued.AddMinutes(2), Result = Result()
        });
        await InsertQueuedAsync("pending", queued.AddMinutes(3));

        // Roll the database back to v6: no list table, as on a server that has not run this build yet.
        await using (var conn = await db.OpenAsync())
        {
            await OmniTraderDb.ExecuteAsync(conn, "DROP TABLE backtest_job_list; DELETE FROM schema_versions WHERE version = 7;");
        }
        await db.InitialiseAsync();

        var (rows, total) = await repo.QuerySummariesAsync(new BacktestJobFilter());
        Assert.Equal(2, total);
        Assert.Equal(new[] { "pending", "old" }, rows.Select(r => r.Id));
        var old = rows[1];
        Assert.Equal(10m, old.TotalPnLPercent);
        Assert.Equal(12.5, old.CagrPct);
        Assert.Equal(queued.AddMinutes(2), old.FinishedUtc);
        Assert.Null(rows[0].TotalPnLPercent);
    }
}
