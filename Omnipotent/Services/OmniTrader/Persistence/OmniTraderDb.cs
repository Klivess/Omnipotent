using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class OmniTraderDb : IDisposable
    {
        private readonly string connectionString;
        private readonly SemaphoreSlim writeLock = new(1, 1);

        public string DbPath { get; }

        public OmniTraderDb()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTraderDirectory);
            Directory.CreateDirectory(dir);
            DbPath = Path.Combine(dir, "omnitrader.db");

            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();
        }

        public async Task InitialiseAsync(CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;", ct);
            await ExecuteAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
            await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct);
            await ApplyMigrationsAsync(conn, ct);
        }

        public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
        {
            var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct);
            return conn;
        }

        public async Task<T> WithWriteLockAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct = default)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                return await action(conn);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public async Task WithWriteLockAsync(Func<SqliteConnection, Task> action, CancellationToken ct = default)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                await action(conn);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct = default)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task ApplyMigrationsAsync(SqliteConnection conn, CancellationToken ct)
        {
            await ExecuteAsync(conn, @"CREATE TABLE IF NOT EXISTS schema_versions (
                version INTEGER PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );", ct);

            var applied = new HashSet<int>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version FROM schema_versions";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    applied.Add(reader.GetInt32(0));
            }

            foreach (var (version, sql) in Schema.OmniTraderSchema.Migrations)
            {
                if (applied.Contains(version)) continue;
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var stamp = conn.CreateCommand())
                {
                    stamp.Transaction = tx;
                    stamp.CommandText = "INSERT INTO schema_versions(version, applied_utc) VALUES($v, $t)";
                    stamp.Parameters.AddWithValue("$v", version);
                    stamp.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                    await stamp.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }

        public void Dispose()
        {
            writeLock.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }
}
