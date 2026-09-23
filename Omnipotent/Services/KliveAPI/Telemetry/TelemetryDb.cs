using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// SQLite store for KliveAPI telemetry. Deliberately its own file + connections —
    /// never OmniDefence's shared connection (see the OmniDefence hot-path lock lesson).
    /// WAL + a single serialised writer (<see cref="WriteLock"/>); readers open pooled
    /// connections. Only the telemetry writer thread and the trace-list routes touch it,
    /// never the request pipeline.
    /// </summary>
    public sealed class TelemetryDb
    {
        public string DbPath { get; }
        public string ConnectionString { get; }
        public readonly SemaphoreSlim WriteLock = new(1, 1);

        public TelemetryDb(string dbPath)
        {
            DbPath = dbPath;
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        public static string BucketTable(string kind, TelemetryTier tier) => $"{kind}_{TelemetryTiers.Name(tier)}";

        /// <summary>Kinds of bucket tables: request series, RUM series, runtime gauges.</summary>
        public const string KindRequests = "req";
        public const string KindRum = "rum";
        public const string KindRuntime = "rt";
        private static readonly string[] Kinds = { KindRequests, KindRum, KindRuntime };
        private static readonly TelemetryTier[] PersistedTiers = { TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 };

        public void Migrate()
        {
            using var conn = Open();
            using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }

            int version;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                version = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (version >= 1) return;

            using var tx = conn.BeginTransaction();
            var sql = new System.Text.StringBuilder();
            foreach (string kind in Kinds)
            {
                foreach (TelemetryTier tier in PersistedTiers)
                {
                    string t = BucketTable(kind, tier);
                    sql.Append($"CREATE TABLE IF NOT EXISTS {t} (ts INTEGER NOT NULL, series TEXT NOT NULL, payload BLOB NOT NULL, PRIMARY KEY (ts, series)) WITHOUT ROWID;");
                    sql.Append($"CREATE INDEX IF NOT EXISTS ix_{t}_series ON {t}(series, ts);");
                }
            }
            sql.Append(@"
CREATE TABLE IF NOT EXISTS traces (
    id INTEGER PRIMARY KEY,
    ts INTEGER NOT NULL,
    route TEXT NOT NULL,
    method TEXT NOT NULL,
    status INTEGER NOT NULL,
    total_us INTEGER NOT NULL,
    cache INTEGER NOT NULL,
    origin TEXT,
    profile TEXT,
    bytes_in INTEGER NOT NULL,
    bytes_out INTEGER NOT NULL,
    bytes_raw INTEGER NOT NULL,
    encoding TEXT,
    flags INTEGER NOT NULL,
    reason TEXT,
    stages BLOB NOT NULL,
    spans TEXT,
    client BLOB
);
CREATE INDEX IF NOT EXISTS ix_traces_ts ON traces(ts);
CREATE INDEX IF NOT EXISTS ix_traces_route_ts ON traces(route, ts);
CREATE INDEX IF NOT EXISTS ix_traces_total ON traces(total_us);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
PRAGMA user_version = 1;");
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql.ToString();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // ── buckets ──

        public readonly record struct BucketRow(string Kind, TelemetryTier Tier, long Ts, string Series, byte[] Payload);

        public void UpsertBuckets(IReadOnlyList<BucketRow> rows)
        {
            if (rows.Count == 0) return;
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                var commands = new Dictionary<string, SqliteCommand>();
                try
                {
                    foreach (BucketRow row in rows)
                    {
                        string table = BucketTable(row.Kind, row.Tier);
                        if (!commands.TryGetValue(table, out SqliteCommand? cmd))
                        {
                            cmd = conn.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = $"INSERT OR REPLACE INTO {table}(ts, series, payload) VALUES ($ts, $series, $payload);";
                            cmd.Parameters.Add("$ts", SqliteType.Integer);
                            cmd.Parameters.Add("$series", SqliteType.Text);
                            cmd.Parameters.Add("$payload", SqliteType.Blob);
                            commands[table] = cmd;
                        }
                        cmd.Parameters["$ts"].Value = row.Ts;
                        cmd.Parameters["$series"].Value = row.Series;
                        cmd.Parameters["$payload"].Value = row.Payload;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                finally
                {
                    foreach (var c in commands.Values) c.Dispose();
                }
            }
            finally
            {
                WriteLock.Release();
            }
        }

        /// <summary>Streams persisted buckets of one tier in [fromMs, toMs), optionally for one series.</summary>
        public IEnumerable<(long Ts, string Series, byte[] Payload)> ReadBuckets(string kind, TelemetryTier tier, long fromMs, long toMs, string? series = null)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            string table = BucketTable(kind, tier);
            cmd.CommandText = series == null
                ? $"SELECT ts, series, payload FROM {table} WHERE ts >= $from AND ts < $to ORDER BY ts;"
                : $"SELECT ts, series, payload FROM {table} WHERE series = $series AND ts >= $from AND ts < $to ORDER BY ts;";
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            if (series != null) cmd.Parameters.AddWithValue("$series", series);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                yield return (reader.GetInt64(0), reader.GetString(1), (byte[])reader.GetValue(2));
            }
        }

        public long? EarliestBucket(string kind, TelemetryTier tier, string series)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT MIN(ts) FROM {BucketTable(kind, tier)} WHERE series = $s;";
            cmd.Parameters.AddWithValue("$s", series);
            object? v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : Convert.ToInt64(v);
        }

        public int PruneBuckets(string kind, TelemetryTier tier, long olderThanMs)
        {
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {BucketTable(kind, tier)} WHERE ts < $cut;";
                cmd.Parameters.AddWithValue("$cut", olderThanMs);
                return cmd.ExecuteNonQuery();
            }
            finally { WriteLock.Release(); }
        }

        // ── traces ──

        public sealed class TraceRow
        {
            public long Id;
            public long Ts;
            public string Route = "/";
            public string Method = "GET";
            public int Status;
            public long TotalMicros;
            public int Cache;
            public string? Origin;
            public string? Profile;
            public long BytesIn;
            public long BytesOut;
            public long BytesRaw;
            public string? Encoding;
            public int Flags;
            public string? Reason;
            public byte[] Stages = Array.Empty<byte>();
            public string? Spans;
            public byte[]? Client;
        }

        public const int FlagSlow = 1, FlagError = 2, FlagDisconnect = 4, FlagSample = 8, FlagBatchItem = 16, FlagException = 32, FlagNotModified = 64;

        public void InsertTraces(IReadOnlyList<TraceRow> rows)
        {
            if (rows.Count == 0) return;
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO traces
(id, ts, route, method, status, total_us, cache, origin, profile, bytes_in, bytes_out, bytes_raw, encoding, flags, reason, stages, spans, client)
VALUES ($id, $ts, $route, $method, $status, $total, $cache, $origin, $profile, $bin, $bout, $braw, $enc, $flags, $reason, $stages, $spans, $client);";
                string[] names = { "$id", "$ts", "$route", "$method", "$status", "$total", "$cache", "$origin", "$profile", "$bin", "$bout", "$braw", "$enc", "$flags", "$reason", "$stages", "$spans", "$client" };
                foreach (string n in names) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
                foreach (TraceRow r in rows)
                {
                    cmd.Parameters["$id"].Value = r.Id;
                    cmd.Parameters["$ts"].Value = r.Ts;
                    cmd.Parameters["$route"].Value = r.Route;
                    cmd.Parameters["$method"].Value = r.Method;
                    cmd.Parameters["$status"].Value = r.Status;
                    cmd.Parameters["$total"].Value = r.TotalMicros;
                    cmd.Parameters["$cache"].Value = r.Cache;
                    cmd.Parameters["$origin"].Value = (object?)r.Origin ?? DBNull.Value;
                    cmd.Parameters["$profile"].Value = (object?)r.Profile ?? DBNull.Value;
                    cmd.Parameters["$bin"].Value = r.BytesIn;
                    cmd.Parameters["$bout"].Value = r.BytesOut;
                    cmd.Parameters["$braw"].Value = r.BytesRaw;
                    cmd.Parameters["$enc"].Value = (object?)r.Encoding ?? DBNull.Value;
                    cmd.Parameters["$flags"].Value = r.Flags;
                    cmd.Parameters["$reason"].Value = (object?)r.Reason ?? DBNull.Value;
                    cmd.Parameters["$stages"].Value = r.Stages;
                    cmd.Parameters["$spans"].Value = (object?)r.Spans ?? DBNull.Value;
                    cmd.Parameters["$client"].Value = (object?)r.Client ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { WriteLock.Release(); }
        }

        public void UpdateTraceClient(IReadOnlyList<(long Id, byte[] Client)> updates)
        {
            if (updates.Count == 0) return;
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE traces SET client = $c WHERE id = $id;";
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "$c" });
                cmd.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
                foreach (var (id, client) in updates)
                {
                    cmd.Parameters["$c"].Value = client;
                    cmd.Parameters["$id"].Value = id;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { WriteLock.Release(); }
        }

        public List<TraceRow> QueryTraces(string? route, string? method, long? minMicros, int? statusMin, long fromMs, long toMs, string sort, int limit)
        {
            var result = new List<TraceRow>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var where = new List<string> { "ts >= $from", "ts < $to" };
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            if (!string.IsNullOrEmpty(route)) { where.Add("route = $route"); cmd.Parameters.AddWithValue("$route", route); }
            if (!string.IsNullOrEmpty(method)) { where.Add("method = $method"); cmd.Parameters.AddWithValue("$method", method); }
            if (minMicros.HasValue) { where.Add("total_us >= $min"); cmd.Parameters.AddWithValue("$min", minMicros.Value); }
            if (statusMin.HasValue) { where.Add("status >= $smin"); cmd.Parameters.AddWithValue("$smin", statusMin.Value); }
            string order = sort == "slowest" ? "total_us DESC" : "ts DESC";
            cmd.CommandText = $"SELECT id, ts, route, method, status, total_us, cache, origin, profile, bytes_in, bytes_out, bytes_raw, encoding, flags, reason, stages, spans, client FROM traces WHERE {string.Join(" AND ", where)} ORDER BY {order} LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadTrace(reader));
            return result;
        }

        public TraceRow? GetTrace(long id)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts, route, method, status, total_us, cache, origin, profile, bytes_in, bytes_out, bytes_raw, encoding, flags, reason, stages, spans, client FROM traces WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTrace(reader) : null;
        }

        private static TraceRow ReadTrace(SqliteDataReader r) => new()
        {
            Id = r.GetInt64(0),
            Ts = r.GetInt64(1),
            Route = r.GetString(2),
            Method = r.GetString(3),
            Status = r.GetInt32(4),
            TotalMicros = r.GetInt64(5),
            Cache = r.GetInt32(6),
            Origin = r.IsDBNull(7) ? null : r.GetString(7),
            Profile = r.IsDBNull(8) ? null : r.GetString(8),
            BytesIn = r.GetInt64(9),
            BytesOut = r.GetInt64(10),
            BytesRaw = r.GetInt64(11),
            Encoding = r.IsDBNull(12) ? null : r.GetString(12),
            Flags = r.GetInt32(13),
            Reason = r.IsDBNull(14) ? null : r.GetString(14),
            Stages = (byte[])r.GetValue(15),
            Spans = r.IsDBNull(16) ? null : r.GetString(16),
            Client = r.IsDBNull(17) ? null : (byte[])r.GetValue(17),
        };

        public int PruneTraces(long olderThanMs)
        {
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM traces WHERE ts < $cut;";
                cmd.Parameters.AddWithValue("$cut", olderThanMs);
                return cmd.ExecuteNonQuery();
            }
            finally { WriteLock.Release(); }
        }

        // ── meta ──

        public string? GetMeta(string key)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }

        public void SetMeta(string key, string value)
        {
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ($k, $v);";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            finally { WriteLock.Release(); }
        }

        public long FileBytes
        {
            get
            {
                long n = 0;
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                {
                    try { var fi = new FileInfo(DbPath + suffix); if (fi.Exists) n += fi.Length; } catch { }
                }
                return n;
            }
        }
    }
}
