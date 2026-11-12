using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Text;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    public enum LlmTier { M1 = 0, H1 = 1, D1 = 2 }

    public static class LlmTiers
    {
        public static readonly LlmTier[] All = { LlmTier.M1, LlmTier.H1, LlmTier.D1 };
        public static long WidthMs(LlmTier tier) => tier switch { LlmTier.M1 => 60_000, LlmTier.H1 => 3_600_000, _ => 86_400_000 };
        public static string Table(LlmTier tier) => tier switch { LlmTier.M1 => "agg_1m", LlmTier.H1 => "agg_1h", _ => "agg_1d" };
        public static string Name(LlmTier tier) => tier switch { LlmTier.M1 => "1m", LlmTier.H1 => "1h", _ => "1d" };
        public static long Align(long ms, LlmTier tier) => Align(ms, WidthMs(tier));
        public static long Align(long ms, long width) => ms - (((ms % width) + width) % width);
    }

    /// <summary>The dimensions every aggregate row is kept by.</summary>
    public readonly record struct LlmAggKey(LlmTier Tier, long Ts, string Service, string Lane, string Router, string Provider, string Model, string Kind);

    public readonly record struct LlmSubjectKey(long Ts, string Service, string Lane, string Subject);

    public sealed class LlmAggRow
    {
        public long Ts;
        public string Service = "", Lane = "", Router = "", Provider = "", Model = "", Kind = "";
        public LlmAggregate Agg = new();

        public string Dimension(string name) => name switch
        {
            "service" => Service,
            "lane" => Service + "/" + Lane,
            "router" => Router,
            "provider" => Provider,
            "model" => Model,
            "kind" => Kind,
            _ => "",
        };
    }

    public sealed class LlmCallFilter
    {
        public long FromMs;
        public long ToMs = long.MaxValue;
        public string[]? Services;
        /// <summary>"Service/lane" keys.</summary>
        public string[]? Lanes;
        public string[]? Routers;
        public string[]? Models;
        public string[]? Outcomes;
        public string[]? Kinds;
        public string? Subject;
        public string? Session;
        public bool FailedOnly;
        public bool FallbackOnly;
        public bool ContentOnly;
        public long? MinTotalMs;
        /// <summary>newest, oldest, slowest, largest, costliest or ttft.</summary>
        public string Sort = "newest";
        /// <summary>Keyset cursor for newest-first paging: the last row's end time and id.</summary>
        public long? BeforeMs;
        public long? BeforeId;
        public int Offset;
        public int Limit = 100;
    }

    /// <summary>
    /// The ledger's SQLite file. Its own file and connections (never another service's), WAL, one
    /// serialised writer. Every query against <c>calls</c> is bounded by time and driven by an index
    /// named in the query, so a long history can never turn a page load into a table walk.
    /// </summary>
    public sealed class LlmTelemetryDb
    {
        public string Path { get; }
        private readonly string connectionString;
        public readonly SemaphoreSlim WriteLock = new(1, 1);

        public LlmTelemetryDb(string path)
        {
            Path = path;
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private const string CallColumns =
            "id, started_ms, ended_ms, service, lane, subject, operation, session, kind, work_class, router, router_name, provider, " +
            "model_requested, model, stream, outcome, http_status, finish, attempts, prompt, cached, cache_write, completion, reasoning, " +
            "cache_metrics, cost, upstream_cost, est_cost, billing, queue_ms, provider_ms, total_ms, ttft_ms, request_bytes, tool_calls, " +
            "flags, fallback_from, epoch, epoch_turn, revision, error, attempt_log";

        public void Migrate()
        {
            using var conn = Open();
            using (var wal = conn.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; wal.ExecuteNonQuery(); }
            int version;
            using (var cmd = conn.CreateCommand()) { cmd.CommandText = "PRAGMA user_version;"; version = Convert.ToInt32(cmd.ExecuteScalar()); }
            if (version >= 1) return;

            var sql = new StringBuilder(@"
CREATE TABLE IF NOT EXISTS calls (
    id INTEGER PRIMARY KEY,
    started_ms INTEGER NOT NULL, ended_ms INTEGER NOT NULL,
    service TEXT NOT NULL, lane TEXT NOT NULL, subject TEXT, operation TEXT, session TEXT,
    kind TEXT NOT NULL, work_class TEXT,
    router TEXT NOT NULL, router_name TEXT, provider TEXT, model_requested TEXT, model TEXT,
    stream INTEGER NOT NULL, outcome TEXT NOT NULL, http_status INTEGER, finish TEXT, attempts INTEGER NOT NULL,
    prompt INTEGER NOT NULL, cached INTEGER NOT NULL, cache_write INTEGER NOT NULL, completion INTEGER NOT NULL, reasoning INTEGER NOT NULL,
    cache_metrics INTEGER NOT NULL, cost REAL, upstream_cost REAL, est_cost REAL, billing TEXT NOT NULL,
    queue_ms INTEGER NOT NULL, provider_ms INTEGER NOT NULL, total_ms INTEGER NOT NULL, ttft_ms INTEGER,
    request_bytes INTEGER NOT NULL, tool_calls INTEGER NOT NULL, flags INTEGER NOT NULL,
    fallback_from TEXT, epoch TEXT, epoch_turn INTEGER NOT NULL, revision INTEGER NOT NULL,
    error TEXT, attempt_log TEXT
);
CREATE INDEX IF NOT EXISTS ix_calls_ended ON calls(ended_ms, id);
CREATE INDEX IF NOT EXISTS ix_calls_service ON calls(service, ended_ms, id);
CREATE INDEX IF NOT EXISTS ix_calls_router ON calls(router, ended_ms, id);
CREATE INDEX IF NOT EXISTS ix_calls_subject ON calls(subject, ended_ms, id);
CREATE INDEX IF NOT EXISTS ix_calls_session ON calls(session, ended_ms, id);
CREATE TABLE IF NOT EXISTS subject_1d (ts INTEGER NOT NULL, service TEXT NOT NULL, lane TEXT NOT NULL, subject TEXT NOT NULL, payload BLOB NOT NULL,
    PRIMARY KEY (ts, service, lane, subject)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS router_1m (ts INTEGER NOT NULL, router TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (ts, router)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS router_1h (ts INTEGER NOT NULL, router TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (ts, router)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS marks (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, detail TEXT);
CREATE INDEX IF NOT EXISTS ix_marks_ts ON marks(ts);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
");
            foreach (var tier in LlmTiers.All)
                sql.Append($"CREATE TABLE IF NOT EXISTS {LlmTiers.Table(tier)} (ts INTEGER NOT NULL, service TEXT NOT NULL, lane TEXT NOT NULL, router TEXT NOT NULL, " +
                           "provider TEXT NOT NULL, model TEXT NOT NULL, kind TEXT NOT NULL, payload BLOB NOT NULL, " +
                           "PRIMARY KEY (ts, service, lane, router, provider, model, kind)) WITHOUT ROWID;\n");
            sql.Append("PRAGMA user_version = 1;");

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql.ToString();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // ── writes ──

        public sealed class Batch
        {
            public List<LlmCallRecord> Calls { get; } = new();
            public List<(LlmAggKey Key, LlmAggregate Agg)> Aggregates { get; } = new();
            public List<(LlmSubjectKey Key, LlmAggregate Agg)> Subjects { get; } = new();
            public List<(string Table, long Ts, string Router, string Json)> Routers { get; } = new();
            public List<(long Ts, string Kind, string Title, string? Detail)> Marks { get; } = new();
            public bool IsEmpty => Calls.Count == 0 && Aggregates.Count == 0 && Subjects.Count == 0 && Routers.Count == 0 && Marks.Count == 0;
        }

        public void Write(Batch batch)
        {
            if (batch.IsEmpty) return;
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                if (batch.Calls.Count > 0) InsertCalls(conn, tx, batch.Calls);
                if (batch.Aggregates.Count > 0)
                {
                    var commands = new Dictionary<string, SqliteCommand>();
                    try
                    {
                        foreach (var (key, agg) in batch.Aggregates)
                        {
                            string table = LlmTiers.Table(key.Tier);
                            if (!commands.TryGetValue(table, out var cmd))
                            {
                                cmd = conn.CreateCommand();
                                cmd.Transaction = tx;
                                cmd.CommandText = $"INSERT OR REPLACE INTO {table}(ts, service, lane, router, provider, model, kind, payload) VALUES ($ts, $s, $l, $r, $p, $m, $k, $payload);";
                                foreach (string n in new[] { "$ts", "$s", "$l", "$r", "$p", "$m", "$k", "$payload" }) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
                                commands[table] = cmd;
                            }
                            cmd.Parameters["$ts"].Value = key.Ts;
                            cmd.Parameters["$s"].Value = key.Service;
                            cmd.Parameters["$l"].Value = key.Lane;
                            cmd.Parameters["$r"].Value = key.Router;
                            cmd.Parameters["$p"].Value = key.Provider;
                            cmd.Parameters["$m"].Value = key.Model;
                            cmd.Parameters["$k"].Value = key.Kind;
                            cmd.Parameters["$payload"].Value = agg.ToBytes();
                            cmd.ExecuteNonQuery();
                        }
                    }
                    finally { foreach (var c in commands.Values) c.Dispose(); }
                }
                if (batch.Subjects.Count > 0)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO subject_1d(ts, service, lane, subject, payload) VALUES ($ts, $s, $l, $sub, $payload);";
                    foreach (string n in new[] { "$ts", "$s", "$l", "$sub", "$payload" }) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
                    foreach (var (key, agg) in batch.Subjects)
                    {
                        cmd.Parameters["$ts"].Value = key.Ts;
                        cmd.Parameters["$s"].Value = key.Service;
                        cmd.Parameters["$l"].Value = key.Lane;
                        cmd.Parameters["$sub"].Value = key.Subject;
                        cmd.Parameters["$payload"].Value = agg.ToBytes();
                        cmd.ExecuteNonQuery();
                    }
                }
                foreach (var (table, ts, router, json) in batch.Routers)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"INSERT OR REPLACE INTO {(table == "router_1h" ? "router_1h" : "router_1m")}(ts, router, payload) VALUES ($ts, $r, $p);";
                    cmd.Parameters.AddWithValue("$ts", ts);
                    cmd.Parameters.AddWithValue("$r", router);
                    cmd.Parameters.AddWithValue("$p", json);
                    cmd.ExecuteNonQuery();
                }
                foreach (var (ts, kind, title, detail) in batch.Marks)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO marks(ts, kind, title, detail) VALUES ($ts, $k, $t, $d);";
                    cmd.Parameters.AddWithValue("$ts", ts);
                    cmd.Parameters.AddWithValue("$k", kind);
                    cmd.Parameters.AddWithValue("$t", title);
                    cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            finally { WriteLock.Release(); }
        }

        private static readonly JsonSerializerSettings CompactJson = new() { NullValueHandling = NullValueHandling.Ignore };

        private static void InsertCalls(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<LlmCallRecord> calls)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            string[] names = CallColumns.Split(',').Select(c => "$" + c.Trim()).ToArray();
            cmd.CommandText = $"INSERT OR REPLACE INTO calls({CallColumns}) VALUES ({string.Join(", ", names)});";
            foreach (string n in names) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
            object V(object? value) => value ?? DBNull.Value;
            foreach (var r in calls)
            {
                var p = cmd.Parameters;
                p["$id"].Value = r.Id;
                p["$started_ms"].Value = r.StartedMs;
                p["$ended_ms"].Value = r.EndedMs;
                p["$service"].Value = r.Service;
                p["$lane"].Value = r.Lane;
                p["$subject"].Value = V(r.Subject);
                p["$operation"].Value = V(r.Operation);
                p["$session"].Value = V(r.SessionId);
                p["$kind"].Value = r.Kind;
                p["$work_class"].Value = V(r.WorkClass);
                p["$router"].Value = r.Router ?? "";
                p["$router_name"].Value = V(r.RouterName);
                p["$provider"].Value = V(r.Provider);
                p["$model_requested"].Value = V(r.ModelRequested);
                p["$model"].Value = V(r.ModelServed);
                p["$stream"].Value = r.Stream ? 1 : 0;
                p["$outcome"].Value = r.Outcome;
                p["$http_status"].Value = V(r.HttpStatus);
                p["$finish"].Value = V(r.FinishReason);
                p["$attempts"].Value = r.Attempts;
                p["$prompt"].Value = r.PromptTokens;
                p["$cached"].Value = r.CachedTokens;
                p["$cache_write"].Value = r.CacheWriteTokens;
                p["$completion"].Value = r.CompletionTokens;
                p["$reasoning"].Value = r.ReasoningTokens;
                p["$cache_metrics"].Value = r.CacheMetrics ? 1 : 0;
                p["$cost"].Value = V(r.CostUsd);
                p["$upstream_cost"].Value = V(r.UpstreamCostUsd);
                p["$est_cost"].Value = V(r.EstimatedCostUsd);
                p["$billing"].Value = r.Billing;
                p["$queue_ms"].Value = r.QueueMs;
                p["$provider_ms"].Value = r.ProviderMs;
                p["$total_ms"].Value = r.TotalMs;
                p["$ttft_ms"].Value = V(r.TtftMs);
                p["$request_bytes"].Value = r.RequestBytes;
                p["$tool_calls"].Value = r.ToolCalls;
                p["$flags"].Value = (int)r.Flags;
                p["$fallback_from"].Value = V(r.FallbackFrom);
                p["$epoch"].Value = V(r.CacheEpoch);
                p["$epoch_turn"].Value = r.CacheEpochTurn;
                p["$revision"].Value = r.RoutingRevision;
                p["$error"].Value = V(r.Error);
                p["$attempt_log"].Value = r.AttemptLog is { Count: > 0 } log ? JsonConvert.SerializeObject(log, CompactJson) : DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        // ── reads: aggregates ──

        public LlmAggregate? ReadAggregate(LlmAggKey key)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT payload FROM {LlmTiers.Table(key.Tier)} WHERE ts = $ts AND service = $s AND lane = $l AND router = $r AND provider = $p AND model = $m AND kind = $k;";
            cmd.Parameters.AddWithValue("$ts", key.Ts);
            cmd.Parameters.AddWithValue("$s", key.Service);
            cmd.Parameters.AddWithValue("$l", key.Lane);
            cmd.Parameters.AddWithValue("$r", key.Router);
            cmd.Parameters.AddWithValue("$p", key.Provider);
            cmd.Parameters.AddWithValue("$m", key.Model);
            cmd.Parameters.AddWithValue("$k", key.Kind);
            return cmd.ExecuteScalar() is byte[] blob ? LlmAggregate.FromBytes(blob) : null;
        }

        public LlmAggregate? ReadSubject(LlmSubjectKey key)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM subject_1d WHERE ts = $ts AND service = $s AND lane = $l AND subject = $sub;";
            cmd.Parameters.AddWithValue("$ts", key.Ts);
            cmd.Parameters.AddWithValue("$s", key.Service);
            cmd.Parameters.AddWithValue("$l", key.Lane);
            cmd.Parameters.AddWithValue("$sub", key.Subject);
            return cmd.ExecuteScalar() is byte[] blob ? LlmAggregate.FromBytes(blob) : null;
        }

        /// <summary>Aggregate rows in [from, to), filtered by any of the dimensions (values are OR'd within one
        /// dimension and AND'd across them). The primary key starts with ts, so the range is an index seek.</summary>
        public List<LlmAggRow> ReadAggregates(LlmTier tier, long fromMs, long toMs, IReadOnlyDictionary<string, string[]>? filters = null)
        {
            var rows = new List<LlmAggRow>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var where = new StringBuilder("ts >= $from AND ts < $to");
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            AppendFilters(cmd, where, filters);
            cmd.CommandText = $"SELECT ts, service, lane, router, provider, model, kind, payload FROM {LlmTiers.Table(tier)} WHERE {where} ORDER BY ts;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new LlmAggRow
                {
                    Ts = reader.GetInt64(0),
                    Service = reader.GetString(1),
                    Lane = reader.GetString(2),
                    Router = reader.GetString(3),
                    Provider = reader.GetString(4),
                    Model = reader.GetString(5),
                    Kind = reader.GetString(6),
                    Agg = LlmAggregate.FromBytes((byte[])reader.GetValue(7)),
                });
            }
            return rows;
        }

        private static readonly Dictionary<string, string> FilterColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = "service",
            ["lane"] = "(service || '/' || lane)",
            ["router"] = "router",
            ["provider"] = "provider",
            ["model"] = "model",
            ["kind"] = "kind",
        };

        private static void AppendFilters(SqliteCommand cmd, StringBuilder where, IReadOnlyDictionary<string, string[]>? filters)
        {
            if (filters == null) return;
            int n = 0;
            foreach (var (dimension, values) in filters)
            {
                if (values == null || values.Length == 0 || !FilterColumns.TryGetValue(dimension, out string? column)) continue;
                var names = new List<string>();
                foreach (string value in values.Take(50))
                {
                    string name = "$f" + n++;
                    names.Add(name);
                    cmd.Parameters.AddWithValue(name, value ?? "");
                }
                where.Append($" AND {column} IN ({string.Join(", ", names)})");
            }
        }

        public List<(long Ts, string Service, string Lane, string Subject, LlmAggregate Agg)> ReadSubjects(long fromMs, long toMs, string? service = null, string? lane = null, string? subject = null)
        {
            var rows = new List<(long, string, string, string, LlmAggregate)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var where = new StringBuilder("ts >= $from AND ts < $to");
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            if (service != null) { where.Append(" AND service = $s"); cmd.Parameters.AddWithValue("$s", service); }
            if (lane != null) { where.Append(" AND lane = $l"); cmd.Parameters.AddWithValue("$l", lane); }
            if (subject != null) { where.Append(" AND subject = $sub"); cmd.Parameters.AddWithValue("$sub", subject); }
            cmd.CommandText = $"SELECT ts, service, lane, subject, payload FROM subject_1d WHERE {where};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), LlmAggregate.FromBytes((byte[])reader.GetValue(4))));
            return rows;
        }

        public string? ReadRouterRow(string table, long ts, string router)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT payload FROM {(table == "router_1h" ? "router_1h" : "router_1m")} WHERE ts = $ts AND router = $r;";
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$r", router);
            return cmd.ExecuteScalar() as string;
        }

        public List<(long Ts, string Router, string Json)> ReadRouterRows(string table, long fromMs, long toMs, string? router = null)
        {
            var rows = new List<(long, string, string)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            string t = table == "router_1h" ? "router_1h" : "router_1m";
            cmd.CommandText = router == null
                ? $"SELECT ts, router, payload FROM {t} WHERE ts >= $from AND ts < $to ORDER BY ts;"
                : $"SELECT ts, router, payload FROM {t} WHERE ts >= $from AND ts < $to AND router = $r ORDER BY ts;";
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            if (router != null) cmd.Parameters.AddWithValue("$r", router);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }

        // ── reads: calls ──

        /// <summary>The longest window a sort other than newest/oldest may cover: it reads every row in range.</summary>
        public const long MaxSortedWindowMs = 31L * 86_400_000;

        public List<LlmCallRecord> QueryCalls(LlmCallFilter f, out bool capped)
        {
            capped = false;
            string sort = (f.Sort ?? "newest").ToLowerInvariant();
            bool keyset = sort is "newest" or "oldest";
            long from = f.FromMs, to = f.ToMs;
            if (!keyset && to - from > MaxSortedWindowMs)
            {
                from = to - MaxSortedWindowMs;
                capped = true;
            }

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var where = new StringBuilder("ended_ms >= $from AND ended_ms < $to");
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            string index = "ix_calls_ended";
            if (!string.IsNullOrWhiteSpace(f.Session)) { index = "ix_calls_session"; where.Append(" AND session = $session"); cmd.Parameters.AddWithValue("$session", f.Session); }
            else if (!string.IsNullOrWhiteSpace(f.Subject)) { index = "ix_calls_subject"; where.Append(" AND subject = $subject"); cmd.Parameters.AddWithValue("$subject", f.Subject); }
            else if (f.Services is { Length: 1 }) index = "ix_calls_service";
            else if (f.Routers is { Length: 1 }) index = "ix_calls_router";
            if (!string.IsNullOrWhiteSpace(f.Session) && !string.IsNullOrWhiteSpace(f.Subject)) { where.Append(" AND subject = $subject"); cmd.Parameters.AddWithValue("$subject", f.Subject); }

            int n = 0;
            void In(string column, string[]? values)
            {
                if (values == null || values.Length == 0) return;
                var names = new List<string>();
                foreach (string v in values.Take(50)) { string name = "$v" + n++; names.Add(name); cmd.Parameters.AddWithValue(name, v ?? ""); }
                where.Append($" AND {column} IN ({string.Join(", ", names)})");
            }
            In("service", f.Services);
            In("(service || '/' || lane)", f.Lanes);
            In("router", f.Routers);
            In("model", f.Models);
            In("outcome", f.Outcomes);
            In("kind", f.Kinds);
            if (f.FailedOnly) where.Append(" AND outcome NOT IN ('ok', 'cancelled')");
            if (f.FallbackOnly) where.Append($" AND (flags & {(int)LlmCallFlags.Fallback}) <> 0");
            if (f.ContentOnly) where.Append($" AND (flags & {(int)LlmCallFlags.ContentKept}) <> 0");
            if (f.MinTotalMs is > 0) { where.Append(" AND total_ms >= $min"); cmd.Parameters.AddWithValue("$min", f.MinTotalMs.Value); }
            if (keyset && f.BeforeMs.HasValue)
            {
                where.Append(sort == "newest"
                    ? " AND (ended_ms < $bms OR (ended_ms = $bms AND id < $bid))"
                    : " AND (ended_ms > $bms OR (ended_ms = $bms AND id > $bid))");
                cmd.Parameters.AddWithValue("$bms", f.BeforeMs.Value);
                cmd.Parameters.AddWithValue("$bid", f.BeforeId ?? (sort == "newest" ? long.MaxValue : long.MinValue));
            }
            string order = sort switch
            {
                "oldest" => "ended_ms ASC, id ASC",
                "slowest" => "total_ms DESC, id DESC",
                "largest" => "(prompt + completion) DESC, id DESC",
                "costliest" => "COALESCE(cost, est_cost, 0) DESC, id DESC",
                "ttft" => "COALESCE(ttft_ms, 0) DESC, id DESC",
                _ => "ended_ms DESC, id DESC",
            };
            // The service/router indexes lead with that column; give SQLite the equality so it can seek.
            if (index == "ix_calls_service") { where.Append(" AND service = $only"); cmd.Parameters.AddWithValue("$only", f.Services![0]); }
            if (index == "ix_calls_router") { where.Append(" AND router = $only"); cmd.Parameters.AddWithValue("$only", f.Routers![0]); }
            cmd.CommandText = $"SELECT {CallColumns} FROM calls INDEXED BY {index} WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset;";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(f.Limit, 1, 1000));
            cmd.Parameters.AddWithValue("$offset", keyset ? 0 : Math.Clamp(f.Offset, 0, 100_000));
            var result = new List<LlmCallRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadCall(reader));
            return result;
        }

        public LlmCallRecord? GetCall(long id)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {CallColumns} FROM calls WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadCall(reader) : null;
        }

        private static LlmCallRecord ReadCall(SqliteDataReader r)
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
            double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            var record = new LlmCallRecord
            {
                Id = r.GetInt64(0),
                StartedMs = r.GetInt64(1),
                EndedMs = r.GetInt64(2),
                Service = r.GetString(3),
                Lane = r.GetString(4),
                Subject = S(5),
                Operation = S(6),
                SessionId = S(7),
                Kind = r.GetString(8),
                WorkClass = S(9),
                Router = r.GetString(10),
                RouterName = S(11),
                Provider = S(12),
                ModelRequested = S(13),
                ModelServed = S(14),
                Stream = r.GetInt64(15) != 0,
                Outcome = r.GetString(16),
                HttpStatus = (int?)L(17),
                FinishReason = S(18),
                Attempts = (int)r.GetInt64(19),
                PromptTokens = r.GetInt64(20),
                CachedTokens = r.GetInt64(21),
                CacheWriteTokens = r.GetInt64(22),
                CompletionTokens = r.GetInt64(23),
                ReasoningTokens = r.GetInt64(24),
                CacheMetrics = r.GetInt64(25) != 0,
                CostUsd = D(26),
                UpstreamCostUsd = D(27),
                EstimatedCostUsd = D(28),
                Billing = r.GetString(29),
                QueueMs = r.GetInt64(30),
                ProviderMs = r.GetInt64(31),
                TotalMs = r.GetInt64(32),
                TtftMs = L(33),
                RequestBytes = r.GetInt64(34),
                ToolCalls = (int)r.GetInt64(35),
                Flags = (LlmCallFlags)r.GetInt64(36),
                FallbackFrom = S(37),
                CacheEpoch = S(38),
                CacheEpochTurn = (int)r.GetInt64(39),
                RoutingRevision = r.GetInt64(40),
                Error = S(41),
            };
            string? log = S(42);
            if (log != null)
            {
                try { record.AttemptLog = JsonConvert.DeserializeObject<List<LlmAttempt>>(log); } catch { }
            }
            return record;
        }

        public long MaxCallId()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(id) FROM calls;";
            object? v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
        }

        public long? EarliestMs(string table)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = table == "calls" ? "SELECT MIN(ended_ms) FROM calls INDEXED BY ix_calls_ended;" : $"SELECT MIN(ts) FROM {table};";
            object? v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : Convert.ToInt64(v);
        }

        public List<(long Id, long Ts, string Kind, string Title, string? Detail)> ReadMarks(long fromMs, long toMs, int limit = 500)
        {
            var rows = new List<(long, long, string, string, string?)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts, kind, title, detail FROM marks WHERE ts >= $from AND ts < $to ORDER BY ts DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 5000));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            return rows;
        }

        // ── maintenance ──

        public int Prune(string table, string column, long olderThanMs)
        {
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cut;";
                cmd.Parameters.AddWithValue("$cut", olderThanMs);
                return cmd.ExecuteNonQuery();
            }
            finally { WriteLock.Release(); }
        }

        /// <summary>Refreshes planner statistics, bounded so it stays cheap on a large file.</summary>
        public void Optimize()
        {
            WriteLock.Wait();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA analysis_limit=2000; ANALYZE; PRAGMA optimize;";
                cmd.ExecuteNonQuery();
            }
            finally { WriteLock.Release(); }
        }

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
                    try { var fi = new FileInfo(Path + suffix); if (fi.Exists) n += fi.Length; } catch { }
                }
                return n;
            }
        }
    }
}
