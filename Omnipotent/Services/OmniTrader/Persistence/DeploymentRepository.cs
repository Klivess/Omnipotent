using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class DeploymentRow
    {
        public required string Id { get; init; }
        public required string StrategyClass { get; init; }
        public required DeploymentConfig Config { get; init; }
        public required SessionMode Mode { get; set; }
        public required DeploymentStatus Status { get; set; }
        public required DateTime CreatedUtc { get; init; }
        public DateTime? ArmedLiveUtc { get; set; }
        public DateTime? PausedUtc { get; set; }
        public required decimal EquityInitial { get; init; }
        public decimal EquityCurrent { get; set; }
        public string? Error { get; set; }
    }

    public sealed class DeploymentRepository
    {
        private const string CacheKey = "omnitrader:deployments";
        private readonly OmniTraderDb db;

        public DeploymentRepository(OmniTraderDb db) { this.db = db; }

        public Task InsertAsync(DeploymentRow row, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO deployments
                (id, strategy_class, config_json, mode, status, created_utc, armed_live_utc, paused_utc, equity_initial, equity_current, error)
                VALUES ($id,$sc,$cj,$m,$s,$c,$al,$p,$ei,$ec,$e)";
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$sc", row.StrategyClass);
            cmd.Parameters.AddWithValue("$cj", JsonConvert.SerializeObject(row.Config));
            cmd.Parameters.AddWithValue("$m", ModeToString(row.Mode));
            cmd.Parameters.AddWithValue("$s", StatusToString(row.Status));
            cmd.Parameters.AddWithValue("$c", row.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$al", (object?)row.ArmedLiveUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)row.PausedUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ei", (double)row.EquityInitial);
            cmd.Parameters.AddWithValue("$ec", (double)row.EquityCurrent);
            cmd.Parameters.AddWithValue("$e", (object?)row.Error ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task UpdateStatusAsync(string id, DeploymentStatus status, string? error = null, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE deployments SET status=$s, error=$e WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", StatusToString(status));
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task UpdateEquityAsync(string id, decimal equity, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE deployments SET equity_current=$e WHERE id=$id";
            cmd.Parameters.AddWithValue("$e", (double)equity);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task SetArmedLiveAsync(string id, DateTime utc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE deployments SET armed_live_utc=$t, status='running' WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", utc.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task SetPausedAsync(string id, DateTime utc, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE deployments SET paused_utc=$t, status='paused' WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", utc.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public Task DeleteAsync(string id, CancellationToken ct = default) => db.WithWriteLockAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM deployments WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            CacheDeps.Bump(CacheKey);
        }, ct);

        public async Task<DeploymentRow?> GetAsync(string id, CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM deployments WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return Map(reader);
        }

        public async Task<List<DeploymentRow>> ListAllAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM deployments ORDER BY created_utc DESC";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<DeploymentRow>();
            while (await reader.ReadAsync(ct))
                output.Add(Map(reader));
            return output;
        }

        public async Task<List<DeploymentRow>> ListRunnableAsync(CancellationToken ct = default)
        {
            CacheDeps.NoteRead(CacheKey);
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM deployments WHERE status IN ('running','paused')";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = new List<DeploymentRow>();
            while (await reader.ReadAsync(ct))
                output.Add(Map(reader));
            return output;
        }

        private static DeploymentRow Map(SqliteDataReader r)
        {
            string configJson = r.GetString(r.GetOrdinal("config_json"));
            var config = JsonConvert.DeserializeObject<DeploymentConfig>(configJson)
                ?? throw new InvalidOperationException("Bad config_json in deployments row");
            return new DeploymentRow
            {
                Id = r.GetString(r.GetOrdinal("id")),
                StrategyClass = r.GetString(r.GetOrdinal("strategy_class")),
                Config = config,
                Mode = ParseMode(r.GetString(r.GetOrdinal("mode"))),
                Status = ParseStatus(r.GetString(r.GetOrdinal("status"))),
                CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc"))).ToUniversalTime(),
                ArmedLiveUtc = r.IsDBNull(r.GetOrdinal("armed_live_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("armed_live_utc"))).ToUniversalTime(),
                PausedUtc = r.IsDBNull(r.GetOrdinal("paused_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("paused_utc"))).ToUniversalTime(),
                EquityInitial = (decimal)r.GetDouble(r.GetOrdinal("equity_initial")),
                EquityCurrent = (decimal)r.GetDouble(r.GetOrdinal("equity_current")),
                Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error"))
            };
        }

        public static string ModeToString(SessionMode m) => m switch
        {
            SessionMode.Paper => "paper",
            SessionMode.Live => "live",
            _ => throw new ArgumentOutOfRangeException(nameof(m))
        };

        public static SessionMode ParseMode(string s) => s switch
        {
            "paper" => SessionMode.Paper,
            "live" => SessionMode.Live,
            _ => throw new ArgumentOutOfRangeException(nameof(s))
        };

        public static string StatusToString(DeploymentStatus s) => s switch
        {
            DeploymentStatus.Running => "running",
            DeploymentStatus.Paused => "paused",
            DeploymentStatus.Stopped => "stopped",
            DeploymentStatus.Errored => "errored",
            _ => throw new ArgumentOutOfRangeException(nameof(s))
        };

        public static DeploymentStatus ParseStatus(string s) => s switch
        {
            "running" => DeploymentStatus.Running,
            "paused" => DeploymentStatus.Paused,
            "stopped" => DeploymentStatus.Stopped,
            "errored" => DeploymentStatus.Errored,
            _ => throw new ArgumentOutOfRangeException(nameof(s))
        };
    }
}
