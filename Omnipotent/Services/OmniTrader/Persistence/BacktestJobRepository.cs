using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;

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

    /// <summary>The fields shown in the jobs list, without the potentially enormous result arrays.</summary>
    public sealed class BacktestJobSummaryRow
    {
        public required string Id { get; init; }
        public required string StrategyClass { get; init; }
        public required BacktestConfig Config { get; init; }
        public BacktestJobStatus Status { get; init; }
        public double ProgressPct { get; init; }
        public int? CandlesTotal { get; init; }
        public int? CandlesDone { get; init; }
        public DateTime QueuedUtc { get; init; }
        public DateTime? StartedUtc { get; init; }
        public DateTime? FinishedUtc { get; init; }
        public string? Error { get; init; }
        public decimal? TotalPnLPercent { get; init; }
        public decimal? WinRate { get; init; }
        public decimal? SharpeRatio { get; init; }
        public decimal? MaxDrawdownPercent { get; init; }
        public int? TotalTrades { get; init; }
        public double? CagrPct { get; init; }
        public double? Sortino { get; init; }
        public double? Calmar { get; init; }
        public double? ProfitFactor { get; init; }
        public double? ExposurePct { get; init; }
        public double? BenchmarkReturnPct { get; init; }
        public double? Alpha { get; init; }
        public int? StrategyErrors { get; init; }
    }

    public sealed class BacktestJobFilter
    {
        /// <summary>Exact class, or a prefix ending in '*' (e.g. "studio:st_ab12*" for every version).</summary>
        public string? StrategyClass { get; set; }
        public string? Origin { get; set; }
        public BacktestJobStatus? Status { get; set; }
        public string? Symbol { get; set; }
        public string? Text { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; }
    }

    /// <summary>
    /// Backtest jobs. Every write keeps backtest_job_list (schema v7) in step with backtest_jobs: the
    /// list reads its timestamps, error and headline metrics from there, because in backtest_jobs those
    /// columns sit behind the multi-megabyte result_json and cost a walk of its overflow pages per row.
    /// </summary>
    public sealed class BacktestJobRepository
    {
        private const string CacheKey = "omnitrader:backtests";
        private readonly OmniTraderDb db;

        public BacktestJobRepository(OmniTraderDb db) { this.db = db; }

        /// <summary>The list's headline numbers out of a serialized result (NULL in, NULL out).</summary>
        private static string MetricsOf(string json) =>
            $"json_extract({json}, '$.InitialEquity', '$.FinalEquity', '$.WinningTrades', '$.TotalTrades', '$.SharpeRatio', '$.MaxDrawdownPercent')";

        private static string AnalysisOf(string json) =>
            $"json_extract({json}, '$.Analysis.CagrPct', '$.Analysis.Sortino', '$.Analysis.Calmar', '$.Analysis.ProfitFactor', " +
            "'$.Analysis.ExposurePct', '$.Analysis.BenchmarkReturnPct', '$.Analysis.Alpha', '$.StrategyErrors')";

        /// <summary>Runs one multi-statement command atomically (both tables or neither).</summary>
        private static async Task ExecuteInTransactionAsync(SqliteConnection conn, string sql, Action<SqliteParameterCollection> bind, CancellationToken ct)
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                bind(cmd.Parameters);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        public Task InsertAsync(BacktestJobRow row, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO backtest_jobs
                (id, strategy_class, config_json, status, progress_pct, candles_total, candles_done, result_json, error, queued_utc, started_utc, finished_utc, cancellation_requested)
                VALUES ($id,$sc,$cj,$s,$p,$ct,$cd,$rj,$e,$q,$st,$f,$cr);
                INSERT INTO backtest_job_list (id, queued_utc, started_utc, finished_utc, error, metrics_json, analysis_json)
                VALUES ($id,$q,$st,$f,$e," + MetricsOf("$rj") + "," + AnalysisOf("$rj") + ")";
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
            await tx.CommitAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task UpdateStatusAsync(string id, BacktestJobStatus status, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET status=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
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
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task StartAsync(string id, DateTime startedUtc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await ExecuteInTransactionAsync(conn, @"
                UPDATE backtest_jobs SET status='running', started_utc=$t WHERE id=$id;
                UPDATE backtest_job_list SET started_utc=$t WHERE id=$id;", p =>
            {
                p.AddWithValue("$t", startedUtc.ToString("o"));
                p.AddWithValue("$id", id);
            }, ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task CompleteAsync(string id, BacktestJobStatus status, BacktestResult? result, string? error, DateTime finishedUtc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await ExecuteInTransactionAsync(conn, @"
                UPDATE backtest_jobs
                SET status=$s, result_json=$r, error=$e, finished_utc=$f, progress_pct=CASE WHEN $s='succeeded' THEN 100 ELSE progress_pct END
                WHERE id=$id;
                UPDATE backtest_job_list
                SET error=$e, finished_utc=$f, metrics_json=" + MetricsOf("$r") + ", analysis_json=" + AnalysisOf("$r") + @"
                WHERE id=$id;", p =>
            {
                p.AddWithValue("$s", status.ToString().ToLowerInvariant());
                p.AddWithValue("$r", (object?)(result == null ? null : JsonConvert.SerializeObject(result)) ?? DBNull.Value);
                p.AddWithValue("$e", (object?)error ?? DBNull.Value);
                p.AddWithValue("$f", finishedUtc.ToString("o"));
                p.AddWithValue("$id", id);
            }, ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task RequestCancelAsync(string id, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE backtest_jobs SET cancellation_requested=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
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
            // The list rows first: the second statement is what stops these jobs counting as running.
            await ExecuteInTransactionAsync(conn, @"
                UPDATE backtest_job_list SET error=$e, finished_utc=$f WHERE id IN (SELECT id FROM backtest_jobs WHERE status='running');
                UPDATE backtest_jobs SET status='failed', error=$e, finished_utc=$f WHERE status='running';", p =>
            {
                p.AddWithValue("$e", error);
                p.AddWithValue("$f", DateTime.UtcNow.ToString("o"));
            }, ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<BacktestJobRow?> GetAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
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
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM backtest_jobs ORDER BY queued_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<BacktestJobRow>();
            while (await reader.ReadAsync(ct)) output.Add(Map(reader));
            return output;
        }

        // Everything the list shows. Every backtest_jobs column read here precedes result_json, and the
        // metrics were extracted when the result was written (any stored result yields a metrics array),
        // so no result is touched. "l" drives the join (CROSS JOIN fixes the loop order) so ORDER BY +
        // LIMIT walks idx_backtest_job_list_queued.
        private const string SummarySelect = @"SELECT j.id, j.strategy_class, j.config_json, j.status, j.progress_pct,
                       j.candles_total, j.candles_done, l.queued_utc, l.started_utc, l.finished_utc, l.error,
                       l.metrics_json IS NOT NULL AS has_result, l.metrics_json, l.analysis_json
                FROM backtest_job_list l CROSS JOIN backtest_jobs j ON j.id = l.id";

        /// <summary>
        /// The dashboard's recent jobs with their headline metrics. Derived metrics are calculated here
        /// so older stored results work too; the full result stays available through GetAsync.
        /// </summary>
        public async Task<List<BacktestJobSummaryRow>> ListRecentSummariesAsync(int limit = 50,
            CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SummarySelect + " ORDER BY l.queued_utc DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            return await ReadSummariesAsync(cmd, ct);
        }

        /// <summary>Filtered job summaries for the lab, with the extended analysis headline
        /// (CAGR, Sortino, Calmar, profit factor, exposure, alpha).</summary>
        public async Task<(List<BacktestJobSummaryRow> Rows, int Total)> QuerySummariesAsync(BacktestJobFilter filter, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            var where = new List<string>();
            await using var cmd = conn.CreateCommand();
            if (!string.IsNullOrWhiteSpace(filter.StrategyClass))
            {
                if (filter.StrategyClass!.EndsWith('*'))
                {
                    where.Add("j.strategy_class LIKE $cls ESCAPE '\\'");
                    cmd.Parameters.AddWithValue("$cls", filter.StrategyClass[..^1].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
                }
                else
                {
                    where.Add("j.strategy_class = $cls");
                    cmd.Parameters.AddWithValue("$cls", filter.StrategyClass);
                }
            }
            if (!string.IsNullOrWhiteSpace(filter.Origin))
            {
                where.Add("json_extract(j.config_json, '$.Origin') = $origin");
                cmd.Parameters.AddWithValue("$origin", filter.Origin);
            }
            if (filter.Status is { } st)
            {
                where.Add("j.status = $status");
                cmd.Parameters.AddWithValue("$status", st.ToString().ToLowerInvariant());
            }
            if (!string.IsNullOrWhiteSpace(filter.Symbol))
            {
                where.Add("upper(json_extract(j.config_json, '$.Coin')) = $sym");
                cmd.Parameters.AddWithValue("$sym", filter.Symbol!.ToUpperInvariant());
            }
            if (!string.IsNullOrWhiteSpace(filter.Text))
            {
                where.Add("(j.strategy_class LIKE $text OR json_extract(j.config_json, '$.Label') LIKE $text OR json_extract(j.config_json, '$.Coin') LIKE $text)");
                cmd.Parameters.AddWithValue("$text", "%" + filter.Text + "%");
            }
            string clause = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

            await using (var count = conn.CreateCommand())
            {
                count.CommandText = $"SELECT COUNT(*) FROM backtest_jobs j {clause}";
                foreach (SqliteParameter prm in cmd.Parameters) count.Parameters.AddWithValue(prm.ParameterName, prm.Value);
                filter.Limit = Math.Clamp(filter.Limit, 1, 1000);
                var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
                cmd.CommandText = $"{SummarySelect} {clause} ORDER BY l.queued_utc DESC LIMIT $l OFFSET $o";
                cmd.Parameters.AddWithValue("$l", filter.Limit);
                cmd.Parameters.AddWithValue("$o", Math.Max(0, filter.Offset));
                return (await ReadSummariesAsync(cmd, ct), total);
            }
        }

        private static async Task<List<BacktestJobSummaryRow>> ReadSummariesAsync(SqliteCommand cmd, CancellationToken ct)
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<BacktestJobSummaryRow>();
            while (await reader.ReadAsync(ct))
            {
                var config = JsonConvert.DeserializeObject<BacktestConfig>(reader.GetString(2))
                    ?? throw new InvalidOperationException("Bad config_json in backtest_jobs row");
                bool hasResult = reader.GetBoolean(11);
                decimal?[] metrics = hasResult && !reader.IsDBNull(12)
                    ? JsonConvert.DeserializeObject<decimal?[]>(reader.GetString(12)) ?? new decimal?[6]
                    : new decimal?[6];
                double?[] a = hasResult && !reader.IsDBNull(13)
                    ? JsonConvert.DeserializeObject<double?[]>(reader.GetString(13)) ?? new double?[8]
                    : new double?[8];
                if (metrics.Length != 6) metrics = new decimal?[6];
                if (a.Length != 8) a = new double?[8];
                decimal initialEquity = metrics[0] ?? 0m;
                decimal finalEquity = metrics[1] ?? 0m;
                int winningTrades = (int)(metrics[2] ?? 0m);
                int totalTrades = (int)(metrics[3] ?? 0m);
                output.Add(new BacktestJobSummaryRow
                {
                    Id = reader.GetString(0),
                    StrategyClass = reader.GetString(1),
                    Config = config,
                    Status = ParseStatus(reader.GetString(3)),
                    ProgressPct = reader.GetDouble(4),
                    CandlesTotal = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    CandlesDone = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    QueuedUtc = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture).ToUniversalTime(),
                    StartedUtc = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture).ToUniversalTime(),
                    FinishedUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture).ToUniversalTime(),
                    Error = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TotalPnLPercent = hasResult ? initialEquity == 0m ? 0m : (finalEquity - initialEquity) / initialEquity * 100m : null,
                    WinRate = hasResult ? totalTrades == 0 ? 0m : (decimal)winningTrades / totalTrades * 100m : null,
                    SharpeRatio = hasResult ? metrics[4] ?? 0m : null,
                    MaxDrawdownPercent = hasResult ? metrics[5] ?? 0m : null,
                    TotalTrades = hasResult ? totalTrades : null,
                    CagrPct = a[0], Sortino = a[1], Calmar = a[2], ProfitFactor = a[3], ExposurePct = a[4],
                    BenchmarkReturnPct = a[5], Alpha = a[6], StrategyErrors = a[7] is { } se ? (int)se : null
                });
            }
            return output;
        }

        public async Task<List<BacktestJobRow>> ListQueuedAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
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
