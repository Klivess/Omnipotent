using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public enum BacktestJobStatus { Queued, Running, Succeeded, Failed, Cancelled }

    public sealed class BacktestJobRow
    {
        public required string Id { get; init; }
        public required string StrategyClass { get; init; }
        public required BacktestConfig Config { get; init; }
        public BacktestJobStatus Status { get; set; }
        public double ProgressPct { get; set; }
        public int? CandlesTotal { get; set; }
        public int? CandlesDone { get; set; }
        public BacktestResult? Result { get; set; }
        public string? Error { get; set; }
        public DateTime QueuedUtc { get; init; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public bool CancellationRequested { get; set; }
    }

    public sealed class BacktestJobRepository
    {
        private readonly OmniTraderDb db;

        public BacktestJobRepository(OmniTraderDb db) { this.db = db; }

        public Task InsertAsync(BacktestJobRow row, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO backtest_jobs
                (id, strategy_class, config_json, status, progress_pct, candles_total, candles_done, result_json, error, queued_utc, started_utc, finished_utc, cancellation_requested)
                VALUES ($id,$sc,$cj,$s,$p,$ct,$cd,$rj,$e,$q,$st,$f,$cr)";
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$sc", row.StrategyClass);
            cmd.Parameters.AddWithValue("$cj", JsonConvert.SerializeObject(row.Config));
            cmd.Parameters.AddWithValue("$s", row.Status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$p", row.ProgressPct);
            cmd.Parameters.AddWithValue("$ct", (object?)row.CandlesTotal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cd", (object?)row.CandlesDone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rj", (object?)(row.Result == null ? null : JsonConvert.SerializeObject(row.Result)) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)row.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", row.QueuedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$st", (object?)row.StartedUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", (object?)row.FinishedUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cr", row.CancellationRequested ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task UpdateStatusAsync(string id, BacktestJobStatus status, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET status=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task UpdateProgressAsync(string id, double progressPct, int candlesDone, int candlesTotal, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET progress_pct=$p, candles_done=$cd, candles_total=$ct WHERE id=$id";
            cmd.Parameters.AddWithValue("$p", progressPct);
            cmd.Parameters.AddWithValue("$cd", candlesDone);
            cmd.Parameters.AddWithValue("$ct", candlesTotal);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task StartAsync(string id, DateTime startedUtc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET status='running', started_utc=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", startedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task CompleteAsync(string id, BacktestJobStatus status, BacktestResult? result, string? error, DateTime finishedUtc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE backtest_jobs
                SET status=$s, result_json=$r, error=$e, finished_utc=$f, progress_pct=CASE WHEN $s='succeeded' THEN 100 ELSE progress_pct END
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$r", (object?)(result == null ? null : JsonConvert.SerializeObject(result)) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", finishedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public Task RequestCancelAsync(string id, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET cancellation_requested=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public async Task<bool> IsCancellationRequestedAsync(string id, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT cancellation_requested FROM backtest_jobs WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l && l != 0;
        }

        public Task MarkOrphansFailedAsync(string error, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET status='failed', error=$e, finished_utc=$f WHERE status='running'";
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$f", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        public async Task<BacktestJobRow?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM backtest_jobs WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return Map(reader);
        }

        public async Task<List<BacktestJobRow>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM backtest_jobs ORDER BY queued_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<BacktestJobRow>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        public async Task<List<BacktestJobRow>> ListQueuedAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM backtest_jobs WHERE status='queued' ORDER BY queued_utc ASC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<BacktestJobRow>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        private static BacktestJobRow Map(SqliteDataReader r)
        {
            string configJson = r.GetString(r.GetOrdinal("config_json"));
            var config = JsonConvert.DeserializeObject<BacktestConfig>(configJson)
                ?? throw new InvalidOperationException("Bad config_json in backtest_jobs row");
            BacktestResult? result = null;
            int rjIdx = r.GetOrdinal("result_json");
            if (!r.IsDBNull(rjIdx))
                result = JsonConvert.DeserializeObject<BacktestResult>(r.GetString(rjIdx));

            return new BacktestJobRow
            {
                Id = r.GetString(r.GetOrdinal("id")),
                StrategyClass = r.GetString(r.GetOrdinal("strategy_class")),
                Config = config,
                Status = ParseStatus(r.GetString(r.GetOrdinal("status"))),
                ProgressPct = r.GetDouble(r.GetOrdinal("progress_pct")),
                CandlesTotal = r.IsDBNull(r.GetOrdinal("candles_total")) ? null : r.GetInt32(r.GetOrdinal("candles_total")),
                CandlesDone = r.IsDBNull(r.GetOrdinal("candles_done")) ? null : r.GetInt32(r.GetOrdinal("candles_done")),
                Result = result,
                Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
                QueuedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("queued_utc"))).ToUniversalTime(),
                StartedUtc = r.IsDBNull(r.GetOrdinal("started_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("started_utc"))).ToUniversalTime(),
                FinishedUtc = r.IsDBNull(r.GetOrdinal("finished_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("finished_utc"))).ToUniversalTime(),
                CancellationRequested = r.GetInt32(r.GetOrdinal("cancellation_requested")) != 0
            };
        }

        public static BacktestJobStatus ParseStatus(string s) => s switch
        {
            "queued" => BacktestJobStatus.Queued,
            "running" => BacktestJobStatus.Running,
            "succeeded" => BacktestJobStatus.Succeeded,
            "failed" => BacktestJobStatus.Failed,
            "cancelled" => BacktestJobStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(s))
        };
    }
}
