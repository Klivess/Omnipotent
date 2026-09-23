using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Data;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// SQLite-backed persistence for OmniDefence: requests, auth events,
    /// profile actions, IP records and IP events.
    ///
    /// Writes funnel through a single connection serialized by <c>writeLock</c> to keep
    /// things simple and avoid SQLITE_BUSY. Reads deliberately do NOT take that lock:
    /// every API request queues an audit row here, so the write lock is fed at the rate
    /// of total site traffic. A reader sharing it waits behind the whole backlog — which
    /// is what made <c>/omnidefence/ip</c> (four separate acquisitions) one of the
    /// slowest routes on the server despite being fully indexed. WAL permits concurrent
    /// readers, so reads run on their own connections against a small semaphore instead.
    /// </summary>
    public class OmniDefenceStore
    {
        private readonly string dbPath;
        private readonly string connectionString;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private SqliteConnection? sharedConnection;

        // ── read path ──
        // Separate connections so a SELECT never queues behind the audit-write backlog.
        // Bounded because unbounded readers would just move thread-pool pressure around.
        private static readonly int ReadPoolSize = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        private readonly SemaphoreSlim readGate = new(ReadPoolSize, ReadPoolSize);
        private readonly string readConnectionString;

        public OmniDefenceStore(string dbPath)
        {
            this.dbPath = dbPath;
            // Single shared connection serialised via writeLock; SqliteCacheMode.Shared
            // would needlessly add SQLite's process-global shared-cache mutex on top of
            // that, which historically contributed to thread-pool starvation when several
            // request finalisers wrote here concurrently with Omniscience reads.
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();

            // ReadWrite rather than ReadOnly: the file always exists by the time reads
            // start (InitializeAsync creates it), and a ReadOnly handle cannot create the
            // -shm file if it is ever missing, which would turn a cold read into a hard
            // failure. Pooling makes acquiring one of these effectively free.
            readConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();
        }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            sharedConnection = new SqliteConnection(connectionString);
            await sharedConnection.OpenAsync();

            using var pragma = sharedConnection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();

            string[] schema = new[]
            {
                @"CREATE TABLE IF NOT EXISTS requests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    utc_ts INTEGER NOT NULL,
                    ip TEXT,
                    method TEXT,
                    route TEXT,
                    query TEXT,
                    status_code INTEGER,
                    duration_ms REAL,
                    profile_id TEXT,
                    profile_name TEXT,
                    profile_rank INTEGER,
                    perm_required INTEGER,
                    matched_route INTEGER,
                    body_hash TEXT,
                    body_length INTEGER,
                    user_agent TEXT,
                    deny_reason TEXT,
                    request_origin TEXT,
                    client_page TEXT
                );",
                "CREATE INDEX IF NOT EXISTS ix_requests_ts ON requests(utc_ts);",
                "CREATE INDEX IF NOT EXISTS ix_requests_ip ON requests(ip);",
                "CREATE INDEX IF NOT EXISTS ix_requests_profile ON requests(profile_id);",
                "CREATE INDEX IF NOT EXISTS ix_requests_route ON requests(route);",

                @"CREATE TABLE IF NOT EXISTS auth_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    utc_ts INTEGER NOT NULL,
                    ip TEXT,
                    type TEXT,
                    profile_id TEXT,
                    profile_name TEXT,
                    route TEXT,
                    user_agent TEXT,
                    detail TEXT
                );",
                "CREATE INDEX IF NOT EXISTS ix_auth_ts ON auth_events(utc_ts);",
                "CREATE INDEX IF NOT EXISTS ix_auth_ip ON auth_events(ip);",
                "CREATE INDEX IF NOT EXISTS ix_auth_type ON auth_events(type);",

                @"CREATE TABLE IF NOT EXISTS profile_actions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    utc_ts INTEGER NOT NULL,
                    profile_id TEXT,
                    profile_name TEXT,
                    ip TEXT,
                    category TEXT,
                    action TEXT,
                    detail_json TEXT
                );",
                "CREATE INDEX IF NOT EXISTS ix_pa_ts ON profile_actions(utc_ts);",
                "CREATE INDEX IF NOT EXISTS ix_pa_profile ON profile_actions(profile_id);",
                "CREATE INDEX IF NOT EXISTS ix_pa_category ON profile_actions(category);",

                @"CREATE TABLE IF NOT EXISTS ip_records (
                    ip TEXT PRIMARY KEY,
                    first_seen INTEGER,
                    last_seen INTEGER,
                    total_requests INTEGER DEFAULT 0,
                    successful_requests INTEGER DEFAULT 0,
                    unauth_attempts INTEGER DEFAULT 0,
                    deny_count INTEGER DEFAULT 0,
                    threat_score REAL DEFAULT 0,
                    status TEXT DEFAULT 'Normal',
                    country TEXT,
                    asn TEXT,
                    city TEXT,
                    region TEXT,
                    isp TEXT,
                    org TEXT,
                    latitude REAL,
                    longitude REAL,
                    notes TEXT,
                    associated_profile_id TEXT,
                    associated_profile_name TEXT,
                    associated_profile_rank INTEGER,
                    associated_profile_last_seen_utc INTEGER,
                    first_alerted_utc INTEGER,
                    last_alerted_utc INTEGER,
                    escalation_level INTEGER DEFAULT 0,
                    last_block_reason TEXT,
                    last_scanned_utc INTEGER
                );",
                "CREATE INDEX IF NOT EXISTS ix_ipr_status ON ip_records(status);",
                "CREATE INDEX IF NOT EXISTS ix_ipr_score ON ip_records(threat_score);",

                @"CREATE TABLE IF NOT EXISTS ip_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    utc_ts INTEGER NOT NULL,
                    ip TEXT,
                    kind TEXT,
                    actor_profile_id TEXT,
                    actor_profile_name TEXT,
                    detail TEXT
                );",
                "CREATE INDEX IF NOT EXISTS ix_ipe_ts ON ip_events(utc_ts);",
                "CREATE INDEX IF NOT EXISTS ix_ipe_ip ON ip_events(ip);",

                @"CREATE TABLE IF NOT EXISTS honeypot_routes (
                    route TEXT PRIMARY KEY,
                    created_utc INTEGER,
                    response_kind TEXT,
                    note TEXT
                );",

                @"CREATE TABLE IF NOT EXISTS blocked_regions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    lat_min REAL NOT NULL,
                    lat_max REAL NOT NULL,
                    lon_min REAL NOT NULL,
                    lon_max REAL NOT NULL,
                    reason TEXT,
                    created_utc INTEGER NOT NULL,
                    created_by TEXT
                );",

                // Fingerprinting: full per-IP aggregate as JSON; class/confidence broken out for queries.
                @"CREATE TABLE IF NOT EXISTS ip_fingerprints (
                    ip TEXT PRIMARY KEY,
                    updated_utc INTEGER NOT NULL,
                    class TEXT,
                    confidence REAL,
                    json TEXT NOT NULL
                );",
                "CREATE INDEX IF NOT EXISTS ix_ipf_class ON ip_fingerprints(class);",

                // Keyed/slow intel lookups (AbuseIPDB, GreyNoise, rDNS), cached per provider.
                @"CREATE TABLE IF NOT EXISTS ip_intel (
                    ip TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    fetched_utc INTEGER NOT NULL,
                    json TEXT,
                    PRIMARY KEY (ip, provider)
                );",

                // Browser-beacon devices: one row per (device, ip) pairing.
                @"CREATE TABLE IF NOT EXISTS fp_devices (
                    device_id TEXT NOT NULL,
                    ip TEXT NOT NULL,
                    first_seen INTEGER NOT NULL,
                    last_seen INTEGER NOT NULL,
                    beacons INTEGER DEFAULT 1,
                    beacon_json TEXT,
                    PRIMARY KEY (device_id, ip)
                );",
                "CREATE INDEX IF NOT EXISTS ix_fpd_ip ON fp_devices(ip);",

                // Durable OmniDefence settings (key/value, JSON values).
                @"CREATE TABLE IF NOT EXISTS od_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );"
            };

            foreach (var stmt in schema)
            {
                using var cmd = sharedConnection.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync();
            }

            await EnsureColumnAsync("requests", "request_origin", "TEXT");
            await EnsureColumnAsync("requests", "client_page", "TEXT");
            await EnsureColumnAsync("requests", "profile_rank", "INTEGER");
            await EnsureColumnAsync("requests", "body_text", "TEXT");
            await EnsureColumnAsync("requests", "body_truncated", "INTEGER");
            await EnsureColumnAsync("requests", "headers_json", "TEXT");
            await EnsureColumnAsync("ip_records", "latitude", "REAL");
            await EnsureColumnAsync("ip_records", "longitude", "REAL");
            await EnsureColumnAsync("ip_records", "city", "TEXT");
            await EnsureColumnAsync("ip_records", "region", "TEXT");
            await EnsureColumnAsync("ip_records", "isp", "TEXT");
            await EnsureColumnAsync("ip_records", "org", "TEXT");
            await EnsureColumnAsync("ip_records", "associated_profile_id", "TEXT");
            await EnsureColumnAsync("ip_records", "associated_profile_name", "TEXT");
            await EnsureColumnAsync("ip_records", "associated_profile_rank", "INTEGER");
            await EnsureColumnAsync("ip_records", "associated_profile_last_seen_utc", "INTEGER");
            await EnsureColumnAsync("ip_records", "classification", "TEXT");
            await EnsureColumnAsync("ip_records", "class_confidence", "REAL");
            await EnsureColumnAsync("ip_records", "class_tags", "TEXT");
            await EnsureColumnAsync("ip_records", "class_updated_utc", "INTEGER");
            await EnsureColumnAsync("ip_records", "is_hosting", "INTEGER");
            await EnsureColumnAsync("ip_records", "is_proxy", "INTEGER");
            await EnsureColumnAsync("ip_records", "is_mobile", "INTEGER");
            await EnsureColumnAsync("ip_records", "reverse_dns", "TEXT");
            await EnsureColumnAsync("ip_records", "timezone", "TEXT");
            await EnsureColumnAsync("ip_records", "as_name", "TEXT");

            StartAuditFlusher();
        }

        /// <summary>
        /// Stops the flusher and commits whatever is still queued, so a clean shutdown
        /// does not discard the tail of the audit log.
        /// </summary>
        public async Task ShutdownAsync()
        {
            try { flushCts?.Cancel(); } catch { }
            if (flushLoop != null)
            {
                try { await flushLoop; } catch { }
            }
            try { await FlushRequestsAsync(); } catch { }
        }

        private async Task EnsureColumnAsync(string table, string column, string type)
        {
            using var check = Connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table})";
            using var rdr = await check.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (string.Equals(rdr["name"] as string, column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = Connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            await alter.ExecuteNonQueryAsync();
        }

        public SqliteConnection Connection => sharedConnection ?? throw new InvalidOperationException("OmniDefenceStore not initialized.");

        public async Task<T> WithLockAsync<T>(Func<SqliteConnection, Task<T>> work)
        {
            await writeLock.WaitAsync();
            try
            {
                return await work(Connection);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public async Task WithLockAsync(Func<SqliteConnection, Task> work)
        {
            await writeLock.WaitAsync();
            try
            {
                await work(Connection);
            }
            finally
            {
                writeLock.Release();
            }
        }

        /// <summary>
        /// Runs a read on its own connection, never touching <c>writeLock</c>. Use this
        /// for every SELECT: it is what keeps read latency independent of how much audit
        /// traffic is queued behind the writer.
        /// </summary>
        public async Task<T> WithReadAsync<T>(Func<SqliteConnection, Task<T>> work)
        {
            await readGate.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(readConnectionString);
                await conn.OpenAsync();
                return await work(conn);
            }
            finally
            {
                readGate.Release();
            }
        }

        // ---------- Request audit queue ----------
        // Every API request records one row here. Inserting them one at a time meant one
        // lock acquisition, transaction and WAL fsync per request, feeding the write lock
        // at the rate of total site traffic and starving readers behind it. Rows are
        // queued instead and committed in batches: the same durability for the audit log
        // (it is telemetry, not a ledger), a fraction of the lock pressure.

        private readonly ConcurrentQueue<RequestRow> pendingRequests = new();
        private readonly SemaphoreSlim flushSignal = new(0);
        private CancellationTokenSource? flushCts;
        private Task? flushLoop;
        private long droppedAuditRows;

        /// <summary>Cap on unflushed rows; beyond this the oldest are dropped rather than
        /// letting a stalled writer grow the queue without bound.</summary>
        private const int MaxPendingRequests = 20_000;

        /// <summary>Rows committed per transaction.</summary>
        private const int FlushBatchSize = 256;

        /// <summary>How long a row may sit unflushed when traffic is light.</summary>
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Audit rows discarded because the queue was saturated.</summary>
        public long DroppedAuditRows => Interlocked.Read(ref droppedAuditRows);

        /// <summary>Rows waiting to be committed.</summary>
        public int PendingAuditRows => pendingRequests.Count;

        /// <summary>
        /// Queues an audit row. Returns immediately — never touches the write lock, so a
        /// slow or backed-up writer cannot show up as request latency.
        /// </summary>
        public void EnqueueRequest(RequestRow row)
        {
            if (row == null) return;
            if (pendingRequests.Count >= MaxPendingRequests)
            {
                if (pendingRequests.TryDequeue(out _)) Interlocked.Increment(ref droppedAuditRows);
            }
            pendingRequests.Enqueue(row);
            try { flushSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private void StartAuditFlusher()
        {
            flushCts = new CancellationTokenSource();
            CancellationToken ct = flushCts.Token;
            flushLoop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Wake on either a queued row or the idle interval, so a lone row
                        // during quiet traffic still lands promptly.
                        await flushSignal.WaitAsync(FlushInterval, ct);
                        await FlushRequestsAsync();
                    }
                    catch (OperationCanceledException) { return; }
                    catch { /* a failed batch must never kill the flusher */ }
                }
            }, ct);
        }

        /// <summary>Drains the queue into batched transactions. Safe to call directly (tests).</summary>
        public async Task FlushRequestsAsync()
        {
            while (!pendingRequests.IsEmpty)
            {
                var batch = new List<RequestRow>(FlushBatchSize);
                while (batch.Count < FlushBatchSize && pendingRequests.TryDequeue(out RequestRow? queued))
                {
                    batch.Add(queued);
                }
                if (batch.Count == 0) return;
                await InsertRequestsAsync(batch);
            }
        }

        /// <summary>Commits a batch of audit rows in one transaction on one prepared command.</summary>
        public Task InsertRequestsAsync(IReadOnlyCollection<RequestRow> rows) => WithLockAsync(async conn =>
        {
            if (rows == null || rows.Count == 0) return;

            using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = RequestInsertSql;
            BindRequestParameters(cmd, rows.First());
            foreach (var row in rows)
            {
                SetRequestParameters(cmd, row);
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        });

        private const string RequestInsertSql = @"INSERT INTO requests
                (utc_ts, ip, method, route, query, status_code, duration_ms, profile_id, profile_name, profile_rank, perm_required, matched_route, body_hash, body_length, user_agent, deny_reason, request_origin, client_page, body_text, body_truncated, headers_json)
                VALUES ($ts,$ip,$method,$route,$query,$status,$dur,$pid,$pname,$prank,$perm,$matched,$bh,$blen,$ua,$deny,$origin,$page,$btext,$btrunc,$hdrs)";

        private static void BindRequestParameters(SqliteCommand cmd, RequestRow row)
        {
            cmd.Parameters.AddWithValue("$ts", row.UtcTimestamp);
            cmd.Parameters.AddWithValue("$ip", (object?)row.Ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", (object?)row.Method ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$route", (object?)row.Route ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$query", (object?)row.Query ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", row.StatusCode);
            cmd.Parameters.AddWithValue("$dur", row.DurationMs);
            cmd.Parameters.AddWithValue("$pid", (object?)row.ProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pname", (object?)row.ProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prank", (object?)row.ProfileRank ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$perm", row.PermRequired);
            cmd.Parameters.AddWithValue("$matched", row.MatchedRoute ? 1 : 0);
            cmd.Parameters.AddWithValue("$bh", (object?)row.BodyHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$blen", row.BodyLength);
            cmd.Parameters.AddWithValue("$ua", (object?)row.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deny", (object?)row.DenyReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", (object?)row.RequestOrigin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$page", (object?)row.ClientPage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$btext", (object?)row.BodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$btrunc", row.BodyTruncated ? 1 : 0);
            cmd.Parameters.AddWithValue("$hdrs", (object?)row.HeadersJson ?? DBNull.Value);
        }

        private static void SetRequestParameters(SqliteCommand cmd, RequestRow row)
        {
            cmd.Parameters["$ts"].Value = row.UtcTimestamp;
            cmd.Parameters["$ip"].Value = (object?)row.Ip ?? DBNull.Value;
            cmd.Parameters["$method"].Value = (object?)row.Method ?? DBNull.Value;
            cmd.Parameters["$route"].Value = (object?)row.Route ?? DBNull.Value;
            cmd.Parameters["$query"].Value = (object?)row.Query ?? DBNull.Value;
            cmd.Parameters["$status"].Value = row.StatusCode;
            cmd.Parameters["$dur"].Value = row.DurationMs;
            cmd.Parameters["$pid"].Value = (object?)row.ProfileId ?? DBNull.Value;
            cmd.Parameters["$pname"].Value = (object?)row.ProfileName ?? DBNull.Value;
            cmd.Parameters["$prank"].Value = (object?)row.ProfileRank ?? DBNull.Value;
            cmd.Parameters["$perm"].Value = row.PermRequired;
            cmd.Parameters["$matched"].Value = row.MatchedRoute ? 1 : 0;
            cmd.Parameters["$bh"].Value = (object?)row.BodyHash ?? DBNull.Value;
            cmd.Parameters["$blen"].Value = row.BodyLength;
            cmd.Parameters["$ua"].Value = (object?)row.UserAgent ?? DBNull.Value;
            cmd.Parameters["$deny"].Value = (object?)row.DenyReason ?? DBNull.Value;
            cmd.Parameters["$origin"].Value = (object?)row.RequestOrigin ?? DBNull.Value;
            cmd.Parameters["$page"].Value = (object?)row.ClientPage ?? DBNull.Value;
            cmd.Parameters["$btext"].Value = (object?)row.BodyText ?? DBNull.Value;
            cmd.Parameters["$btrunc"].Value = row.BodyTruncated ? 1 : 0;
            cmd.Parameters["$hdrs"].Value = (object?)row.HeadersJson ?? DBNull.Value;
        }

        // ---------- Insert helpers ----------

        public Task InsertRequestAsync(RequestRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = RequestInsertSql;
            cmd.Parameters.AddWithValue("$ts", row.UtcTimestamp);
            cmd.Parameters.AddWithValue("$ip", (object?)row.Ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", (object?)row.Method ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$route", (object?)row.Route ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$query", (object?)row.Query ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", row.StatusCode);
            cmd.Parameters.AddWithValue("$dur", row.DurationMs);
            cmd.Parameters.AddWithValue("$pid", (object?)row.ProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pname", (object?)row.ProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prank", (object?)row.ProfileRank ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$perm", row.PermRequired);
            cmd.Parameters.AddWithValue("$matched", row.MatchedRoute ? 1 : 0);
            cmd.Parameters.AddWithValue("$bh", (object?)row.BodyHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$blen", row.BodyLength);
            cmd.Parameters.AddWithValue("$ua", (object?)row.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deny", (object?)row.DenyReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", (object?)row.RequestOrigin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$page", (object?)row.ClientPage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$btext", (object?)row.BodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$btrunc", row.BodyTruncated ? 1 : 0);
            cmd.Parameters.AddWithValue("$hdrs", (object?)row.HeadersJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task<Dictionary<string, object?>?> GetRequestByIdAsync(long id) => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM requests WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return null;
            var row = new Dictionary<string, object?>(rdr.FieldCount);
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var v = rdr.GetValue(i);
                row[rdr.GetName(i)] = v is DBNull ? null : v;
            }
            return row;
        });

        public Task InsertAuthEventAsync(AuthEventRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO auth_events
                (utc_ts, ip, type, profile_id, profile_name, route, user_agent, detail)
                VALUES ($ts,$ip,$type,$pid,$pname,$route,$ua,$detail)";
            cmd.Parameters.AddWithValue("$ts", row.UtcTimestamp);
            cmd.Parameters.AddWithValue("$ip", (object?)row.Ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", row.Type);
            cmd.Parameters.AddWithValue("$pid", (object?)row.ProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pname", (object?)row.ProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$route", (object?)row.Route ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ua", (object?)row.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)row.Detail ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task InsertProfileActionAsync(ProfileActionRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO profile_actions
                (utc_ts, profile_id, profile_name, ip, category, action, detail_json)
                VALUES ($ts,$pid,$pname,$ip,$cat,$act,$detail)";
            cmd.Parameters.AddWithValue("$ts", row.UtcTimestamp);
            cmd.Parameters.AddWithValue("$pid", (object?)row.ProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pname", (object?)row.ProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ip", (object?)row.Ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cat", row.Category);
            cmd.Parameters.AddWithValue("$act", row.Action);
            cmd.Parameters.AddWithValue("$detail", (object?)row.DetailJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task InsertIpEventAsync(IpEventRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ip_events
                (utc_ts, ip, kind, actor_profile_id, actor_profile_name, detail)
                VALUES ($ts,$ip,$kind,$apid,$apname,$detail)";
            cmd.Parameters.AddWithValue("$ts", row.UtcTimestamp);
            cmd.Parameters.AddWithValue("$ip", (object?)row.Ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", row.Kind);
            cmd.Parameters.AddWithValue("$apid", (object?)row.ActorProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$apname", (object?)row.ActorProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)row.Detail ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        // ---------- IP record upsert ----------
        public Task UpsertIpRecordAsync(IpRecord rec) => UpsertIpRecordsAsync(new[] { rec });

        // Geo/intel columns COALESCE so a flush racing an enrichment never erases it;
        // classification columns overwrite because "no class" is a real state.
        private static readonly (string Column, string Param, SqliteType Type, bool Coalesce, Func<IpRecord, object?> Get)[] IpRecordColumns =
        {
            ("ip", "$ip", SqliteType.Text, false, r => r.Ip),
            ("first_seen", "$fs", SqliteType.Integer, false, r => r.FirstSeen),
            ("last_seen", "$ls", SqliteType.Integer, false, r => r.LastSeen),
            ("total_requests", "$tot", SqliteType.Integer, false, r => r.TotalRequests),
            ("successful_requests", "$succ", SqliteType.Integer, false, r => r.SuccessfulRequests),
            ("unauth_attempts", "$ua", SqliteType.Integer, false, r => r.UnauthAttempts),
            ("deny_count", "$dc", SqliteType.Integer, false, r => r.DenyCount),
            ("threat_score", "$ts", SqliteType.Real, false, r => r.ThreatScore),
            ("status", "$st", SqliteType.Text, false, r => r.Status),
            ("country", "$co", SqliteType.Text, true, r => r.Country),
            ("asn", "$asn", SqliteType.Text, true, r => r.Asn),
            ("city", "$city", SqliteType.Text, true, r => r.City),
            ("region", "$region", SqliteType.Text, true, r => r.Region),
            ("isp", "$isp", SqliteType.Text, true, r => r.Isp),
            ("org", "$org", SqliteType.Text, true, r => r.Org),
            ("latitude", "$lat", SqliteType.Real, true, r => r.Latitude),
            ("longitude", "$lon", SqliteType.Real, true, r => r.Longitude),
            ("notes", "$nt", SqliteType.Text, false, r => r.Notes),
            ("associated_profile_id", "$apid", SqliteType.Text, true, r => r.AssociatedProfileId),
            ("associated_profile_name", "$apname", SqliteType.Text, true, r => r.AssociatedProfileName),
            ("associated_profile_rank", "$aprank", SqliteType.Integer, true, r => r.AssociatedProfileRank),
            ("associated_profile_last_seen_utc", "$aplast", SqliteType.Integer, true, r => r.AssociatedProfileLastSeenUtc),
            ("first_alerted_utc", "$fa", SqliteType.Integer, true, r => r.FirstAlertedUtc),
            ("last_alerted_utc", "$la", SqliteType.Integer, false, r => r.LastAlertedUtc),
            ("escalation_level", "$el", SqliteType.Integer, false, r => r.EscalationLevel),
            ("last_block_reason", "$lbr", SqliteType.Text, false, r => r.LastBlockReason),
            ("last_scanned_utc", "$lsc", SqliteType.Integer, true, r => r.LastScannedUtc),
            ("classification", "$cls", SqliteType.Text, false, r => r.Classification),
            ("class_confidence", "$clsc", SqliteType.Real, false, r => r.ClassConfidence),
            ("class_tags", "$clst", SqliteType.Text, false, r => r.ClassTags),
            ("class_updated_utc", "$clsu", SqliteType.Integer, false, r => r.ClassUpdatedUtc),
            ("is_hosting", "$host", SqliteType.Integer, true, r => r.IsHosting.HasValue ? (r.IsHosting.Value ? 1 : 0) : null),
            ("is_proxy", "$prox", SqliteType.Integer, true, r => r.IsProxy.HasValue ? (r.IsProxy.Value ? 1 : 0) : null),
            ("is_mobile", "$mob", SqliteType.Integer, true, r => r.IsMobile.HasValue ? (r.IsMobile.Value ? 1 : 0) : null),
            ("reverse_dns", "$rdns", SqliteType.Text, true, r => r.ReverseDns),
            ("timezone", "$tz", SqliteType.Text, true, r => r.Timezone),
            ("as_name", "$asname", SqliteType.Text, true, r => r.AsName),
        };

        private static readonly string IpRecordUpsertSql = BuildIpRecordUpsertSql();

        private static string BuildIpRecordUpsertSql()
        {
            string cols = string.Join(", ", IpRecordColumns.Select(c => c.Column));
            string vals = string.Join(",", IpRecordColumns.Select(c => c.Param));
            string sets = string.Join(",\n                    ", IpRecordColumns
                .Where(c => c.Column != "ip" && c.Column != "first_seen")
                .Select(c => c.Coalesce
                    ? $"{c.Column}=COALESCE(excluded.{c.Column}, ip_records.{c.Column})"
                    : $"{c.Column}=excluded.{c.Column}"));
            return "INSERT INTO ip_records (" + cols + ")\n                VALUES (" + vals + ")\n                ON CONFLICT(ip) DO UPDATE SET\n                    " + sets;
        }

        /// <summary>
        /// Upserts many IP records inside a single transaction, reusing one prepared
        /// command. This is dramatically faster than one transaction per record — each of
        /// those is its own WAL fsync, so persisting a large IP cache one row at a time took
        /// many minutes and starved every other writer of the shared connection lock
        /// (notably login auditing on startup).
        /// </summary>
        public Task UpsertIpRecordsAsync(IReadOnlyCollection<IpRecord> records) => WithLockAsync(async conn =>
        {
            if (records == null || records.Count == 0) return;

            using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = IpRecordUpsertSql;
            var parameters = IpRecordColumns.Select(c => cmd.Parameters.Add(c.Param, c.Type)).ToArray();

            foreach (var rec in records)
            {
                if (rec == null || string.IsNullOrEmpty(rec.Ip)) continue;
                lock (rec)
                {
                    for (int i = 0; i < IpRecordColumns.Length; i++)
                    {
                        parameters[i].Value = IpRecordColumns[i].Get(rec) ?? DBNull.Value;
                    }
                }
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        });

        // ---------- Fingerprints ----------

        public sealed class FingerprintRow
        {
            public string Ip = "";
            public long UpdatedUtc;
            public string? Class;
            public double Confidence;
            public string Json = "";
        }

        public Task UpsertFingerprintsAsync(IReadOnlyCollection<FingerprintRow> rows) => WithLockAsync(async conn =>
        {
            if (rows == null || rows.Count == 0) return;
            using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"INSERT INTO ip_fingerprints (ip, updated_utc, class, confidence, json)
                VALUES ($ip,$u,$c,$conf,$j)
                ON CONFLICT(ip) DO UPDATE SET updated_utc=excluded.updated_utc, class=excluded.class, confidence=excluded.confidence, json=excluded.json";
            var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
            var pU = cmd.Parameters.Add("$u", SqliteType.Integer);
            var pC = cmd.Parameters.Add("$c", SqliteType.Text);
            var pConf = cmd.Parameters.Add("$conf", SqliteType.Real);
            var pJ = cmd.Parameters.Add("$j", SqliteType.Text);
            foreach (var row in rows)
            {
                pIp.Value = row.Ip;
                pU.Value = row.UpdatedUtc;
                pC.Value = (object?)row.Class ?? DBNull.Value;
                pConf.Value = row.Confidence;
                pJ.Value = row.Json;
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        });

        public Task<List<FingerprintRow>> LoadFingerprintsAsync() => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ip, updated_utc, class, confidence, json FROM ip_fingerprints";
            var list = new List<FingerprintRow>();
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add(new FingerprintRow
                {
                    Ip = rdr.GetString(0),
                    UpdatedUtc = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                    Class = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Confidence = rdr.IsDBNull(3) ? 0 : rdr.GetDouble(3),
                    Json = rdr.IsDBNull(4) ? "{}" : rdr.GetString(4)
                });
            }
            return list;
        });

        public sealed class AuditRowForBackfill
        {
            public long Id;
            public long UtcTs;
            public string Ip = "";
            public string? Method;
            public string? Route;
            public string? Query;
            public int Status;
            public string? UserAgent;
            public string? Origin;
            public string? ProfileId;
            public int? ProfileRank;
            public bool Matched;
            public string? DenyReason;
            public string? HeadersJson;
        }

        /// <summary>Max audit-row id, for sizing the fingerprint backfill window.</summary>
        public Task<long> MaxRequestIdAsync() => ScalarLongAsync("SELECT COALESCE(MAX(id),0) FROM requests", new());

        /// <summary>One chunk of audit rows in id order (fingerprint backfill). Each chunk is its own read.</summary>
        public Task<List<AuditRowForBackfill>> ReadRequestChunkAsync(long afterId, int limit) => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, utc_ts, ip, method, route, query, status_code, user_agent, request_origin, profile_id, profile_rank, matched_route, deny_reason, headers_json
                FROM requests WHERE id > $after AND ip IS NOT NULL AND ip <> '' ORDER BY id LIMIT $lim";
            cmd.Parameters.AddWithValue("$after", afterId);
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<AuditRowForBackfill>(limit);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add(new AuditRowForBackfill
                {
                    Id = rdr.GetInt64(0),
                    UtcTs = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                    Ip = rdr.GetString(2),
                    Method = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    Route = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    Query = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    Status = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                    UserAgent = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    Origin = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    ProfileId = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                    ProfileRank = rdr.IsDBNull(10) ? null : rdr.GetInt32(10),
                    Matched = !rdr.IsDBNull(11) && rdr.GetInt64(11) != 0,
                    DenyReason = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                    HeadersJson = rdr.IsDBNull(13) ? null : rdr.GetString(13)
                });
            }
            return list;
        });

        // ---------- Intel cache ----------

        public Task UpsertIntelAsync(string ip, string provider, long fetchedUtc, string? json) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ip_intel (ip, provider, fetched_utc, json) VALUES ($ip,$p,$f,$j)
                ON CONFLICT(ip, provider) DO UPDATE SET fetched_utc=excluded.fetched_utc, json=excluded.json";
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$f", fetchedUtc);
            cmd.Parameters.AddWithValue("$j", (object?)json ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task<List<(string Ip, string Provider, long FetchedUtc, string? Json)>> LoadIntelAsync(long minFetchedUtc) => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ip, provider, fetched_utc, json FROM ip_intel WHERE fetched_utc >= $min";
            cmd.Parameters.AddWithValue("$min", minFetchedUtc);
            var list = new List<(string, string, long, string?)>();
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetInt64(2), rdr.IsDBNull(3) ? null : rdr.GetString(3)));
            }
            return list;
        });

        // ---------- Beacon devices ----------

        public Task UpsertDeviceAsync(string deviceId, string ip, long seenUtc, string? beaconJson) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO fp_devices (device_id, ip, first_seen, last_seen, beacons, beacon_json) VALUES ($d,$ip,$t,$t,1,$j)
                ON CONFLICT(device_id, ip) DO UPDATE SET last_seen=excluded.last_seen, beacons=fp_devices.beacons+1, beacon_json=excluded.beacon_json";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.Parameters.AddWithValue("$t", seenUtc);
            cmd.Parameters.AddWithValue("$j", (object?)beaconJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task<List<Dictionary<string, object?>>> DevicesForIpAsync(string ip)
            => QueryAsync("SELECT device_id, ip, first_seen, last_seen, beacons, beacon_json FROM fp_devices WHERE ip=$ip ORDER BY last_seen DESC LIMIT 50", new() { ["$ip"] = ip });

        public Task<List<Dictionary<string, object?>>> IpsForDevicesAsync(IEnumerable<string> deviceIds)
        {
            var ids = deviceIds.Take(20).ToList();
            if (ids.Count == 0) return Task.FromResult(new List<Dictionary<string, object?>>());
            var parameters = new Dictionary<string, object?>();
            var names = new List<string>();
            for (int i = 0; i < ids.Count; i++) { names.Add("$d" + i); parameters["$d" + i] = ids[i]; }
            return QueryAsync($"SELECT device_id, ip, first_seen, last_seen, beacons FROM fp_devices WHERE device_id IN ({string.Join(",", names)}) ORDER BY last_seen DESC LIMIT 200", parameters);
        }

        // ---------- Settings ----------

        public Task<Dictionary<string, string>> LoadSettingsAsync() => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM od_settings";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (!rdr.IsDBNull(1)) map[rdr.GetString(0)] = rdr.GetString(1);
            }
            return map;
        });

        public Task SetSettingAsync(string key, string? value) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO od_settings (key, value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task<IpRecord?> GetIpRecordAsync(string ip) => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ip_records WHERE ip=$ip";
            cmd.Parameters.AddWithValue("$ip", ip);
            using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync()) return ReadIpRecord(rdr);
            return null;
        });

        public Task<List<IpRecord>> ListIpRecordsAsync(string? statusFilter, double minScore, string? query, int limit, int offset)
            => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            var sb = new System.Text.StringBuilder("SELECT * FROM ip_records WHERE threat_score >= $min");
            cmd.Parameters.AddWithValue("$min", minScore);
            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                sb.Append(" AND status=$st");
                cmd.Parameters.AddWithValue("$st", statusFilter);
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                sb.Append(" AND (ip LIKE $q OR country LIKE $q OR city LIKE $q OR region LIKE $q OR asn LIKE $q OR isp LIKE $q OR org LIKE $q OR notes LIKE $q OR associated_profile_name LIKE $q OR associated_profile_id LIKE $q)");
                cmd.Parameters.AddWithValue("$q", "%" + query + "%");
            }
            sb.Append(" ORDER BY threat_score DESC, last_seen DESC LIMIT $lim OFFSET $off");
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);
            cmd.CommandText = sb.ToString();
            var list = new List<IpRecord>();
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) list.Add(ReadIpRecord(rdr));
            return list;
        });

        public Task<List<IpRecord>> LoadAllIpRecordsAsync() => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ip_records";
            var list = new List<IpRecord>();
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) list.Add(ReadIpRecord(rdr));
            return list;
        });

        // ---------- Generic filtered selects ----------
        public Task<List<Dictionary<string, object?>>> QueryAsync(string sql, Dictionary<string, object?> parameters)
            => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var kv in parameters)
            {
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }
            var list = new List<Dictionary<string, object?>>();
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var row = new Dictionary<string, object?>(rdr.FieldCount);
                for (int i = 0; i < rdr.FieldCount; i++)
                {
                    var v = rdr.GetValue(i);
                    row[rdr.GetName(i)] = v is DBNull ? null : v;
                }
                list.Add(row);
            }
            return list;
        });

        public Task<long> ScalarLongAsync(string sql, Dictionary<string, object?> parameters)
            => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var kv in parameters)
            {
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }
            var res = await cmd.ExecuteScalarAsync();
            if (res == null || res is DBNull) return 0;
            return Convert.ToInt64(res);
        });

        // ---------- Honeypot routes ----------
        public Task<List<HoneypotRouteRow>> ListHoneypotRoutesAsync() => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT route, created_utc, response_kind, note FROM honeypot_routes";
            using var rdr = await cmd.ExecuteReaderAsync();
            var list = new List<HoneypotRouteRow>();
            while (await rdr.ReadAsync())
            {
                list.Add(new HoneypotRouteRow
                {
                    Route = rdr.GetString(0),
                    CreatedUtc = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                    ResponseKind = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                    Note = rdr.IsDBNull(3) ? null : rdr.GetString(3)
                });
            }
            return list;
        });

        public Task UpsertHoneypotRouteAsync(HoneypotRouteRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO honeypot_routes (route, created_utc, response_kind, note)
                VALUES ($r,$c,$k,$n)
                ON CONFLICT(route) DO UPDATE SET response_kind=excluded.response_kind, note=excluded.note";
            cmd.Parameters.AddWithValue("$r", row.Route);
            cmd.Parameters.AddWithValue("$c", row.CreatedUtc);
            cmd.Parameters.AddWithValue("$k", row.ResponseKind);
            cmd.Parameters.AddWithValue("$n", (object?)row.Note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task DeleteHoneypotRouteAsync(string route) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM honeypot_routes WHERE route=$r";
            cmd.Parameters.AddWithValue("$r", route);
            await cmd.ExecuteNonQueryAsync();
        });

        // ---------- Blocked regions ----------
        public Task<List<BlockedRegionRow>> ListBlockedRegionsAsync() => WithReadAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, lat_min, lat_max, lon_min, lon_max, reason, created_utc, created_by FROM blocked_regions ORDER BY id DESC";
            using var rdr = await cmd.ExecuteReaderAsync();
            var list = new List<BlockedRegionRow>();
            while (await rdr.ReadAsync())
            {
                list.Add(new BlockedRegionRow
                {
                    Id = rdr.GetInt64(0),
                    LatMin = rdr.GetDouble(1),
                    LatMax = rdr.GetDouble(2),
                    LonMin = rdr.GetDouble(3),
                    LonMax = rdr.GetDouble(4),
                    Reason = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    CreatedUtc = rdr.IsDBNull(6) ? 0 : rdr.GetInt64(6),
                    CreatedBy = rdr.IsDBNull(7) ? null : rdr.GetString(7)
                });
            }
            return list;
        });

        public Task<long> InsertBlockedRegionAsync(BlockedRegionRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO blocked_regions (lat_min, lat_max, lon_min, lon_max, reason, created_utc, created_by)
                VALUES ($lmin,$lmax,$omin,$omax,$reason,$ts,$by);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$lmin", row.LatMin);
            cmd.Parameters.AddWithValue("$lmax", row.LatMax);
            cmd.Parameters.AddWithValue("$omin", row.LonMin);
            cmd.Parameters.AddWithValue("$omax", row.LonMax);
            cmd.Parameters.AddWithValue("$reason", (object?)row.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", row.CreatedUtc);
            cmd.Parameters.AddWithValue("$by", (object?)row.CreatedBy ?? DBNull.Value);
            var res = await cmd.ExecuteScalarAsync();
            return res == null || res is DBNull ? 0L : Convert.ToInt64(res);
        });

        public Task DeleteBlockedRegionAsync(long id) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM blocked_regions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        });

        private static IpRecord ReadIpRecord(SqliteDataReader rdr)
        {
            return new IpRecord
            {
                Ip = rdr["ip"] as string ?? string.Empty,
                FirstSeen = rdr["first_seen"] is long fs ? fs : Convert.ToInt64(rdr["first_seen"] ?? 0L),
                LastSeen = rdr["last_seen"] is long ls ? ls : Convert.ToInt64(rdr["last_seen"] ?? 0L),
                TotalRequests = Convert.ToInt64(rdr["total_requests"] ?? 0L),
                SuccessfulRequests = Convert.ToInt64(rdr["successful_requests"] ?? 0L),
                UnauthAttempts = Convert.ToInt64(rdr["unauth_attempts"] ?? 0L),
                DenyCount = Convert.ToInt64(rdr["deny_count"] ?? 0L),
                ThreatScore = Convert.ToDouble(rdr["threat_score"] ?? 0d),
                Status = rdr["status"] as string ?? "Normal",
                Country = rdr["country"] as string,
                Asn = rdr["asn"] as string,
                City = rdr["city"] as string,
                Region = rdr["region"] as string,
                Isp = rdr["isp"] as string,
                Org = rdr["org"] as string,
                Latitude = rdr["latitude"] is DBNull ? null : Convert.ToDouble(rdr["latitude"]),
                Longitude = rdr["longitude"] is DBNull ? null : Convert.ToDouble(rdr["longitude"]),
                Notes = rdr["notes"] as string,
                AssociatedProfileId = rdr["associated_profile_id"] as string,
                AssociatedProfileName = rdr["associated_profile_name"] as string,
                AssociatedProfileRank = rdr["associated_profile_rank"] is DBNull ? null : Convert.ToInt32(rdr["associated_profile_rank"]),
                AssociatedProfileLastSeenUtc = rdr["associated_profile_last_seen_utc"] is DBNull ? null : Convert.ToInt64(rdr["associated_profile_last_seen_utc"]),
                FirstAlertedUtc = rdr["first_alerted_utc"] is DBNull ? null : Convert.ToInt64(rdr["first_alerted_utc"]),
                LastAlertedUtc = rdr["last_alerted_utc"] is DBNull ? null : Convert.ToInt64(rdr["last_alerted_utc"]),
                EscalationLevel = Convert.ToInt32(rdr["escalation_level"] ?? 0),
                LastBlockReason = rdr["last_block_reason"] as string,
                LastScannedUtc = rdr["last_scanned_utc"] is DBNull ? null : Convert.ToInt64(rdr["last_scanned_utc"]),
                Classification = rdr["classification"] as string,
                ClassConfidence = rdr["class_confidence"] is DBNull ? null : Convert.ToDouble(rdr["class_confidence"]),
                ClassTags = rdr["class_tags"] as string,
                ClassUpdatedUtc = rdr["class_updated_utc"] is DBNull ? null : Convert.ToInt64(rdr["class_updated_utc"]),
                IsHosting = rdr["is_hosting"] is DBNull ? null : Convert.ToInt64(rdr["is_hosting"]) != 0,
                IsProxy = rdr["is_proxy"] is DBNull ? null : Convert.ToInt64(rdr["is_proxy"]) != 0,
                IsMobile = rdr["is_mobile"] is DBNull ? null : Convert.ToInt64(rdr["is_mobile"]) != 0,
                ReverseDns = rdr["reverse_dns"] as string,
                Timezone = rdr["timezone"] as string,
                AsName = rdr["as_name"] as string
            };
        }
    }

    public class RequestRow
    {
        public long UtcTimestamp;
        public string? Ip;
        public string? Method;
        public string? Route;
        public string? Query;
        public int StatusCode;
        public double DurationMs;
        public string? ProfileId;
        public string? ProfileName;
        public int? ProfileRank;
        public int PermRequired;
        public bool MatchedRoute;
        public string? BodyHash;
        public long BodyLength;
        public string? UserAgent;
        public string? DenyReason;
        public string? RequestOrigin;
        public string? ClientPage;
        public string? BodyText;
        public bool BodyTruncated;
        public string? HeadersJson;
    }

    public class AuthEventRow
    {
        public long UtcTimestamp;
        public string? Ip;
        public string Type = "";
        public string? ProfileId;
        public string? ProfileName;
        public string? Route;
        public string? UserAgent;
        public string? Detail;
    }

    public class ProfileActionRow
    {
        public long UtcTimestamp;
        public string? ProfileId;
        public string? ProfileName;
        public string? Ip;
        public string Category = "";
        public string Action = "";
        public string? DetailJson;
    }

    public class IpEventRow
    {
        public long UtcTimestamp;
        public string? Ip;
        public string Kind = "";
        public string? ActorProfileId;
        public string? ActorProfileName;
        public string? Detail;
    }

    public class IpRecord
    {
        public string Ip = "";
        public long FirstSeen;
        public long LastSeen;
        public long TotalRequests;
        public long SuccessfulRequests;
        public long UnauthAttempts;
        public long DenyCount;
        public double ThreatScore;
        public string Status = "Normal"; // Normal, Watch, Blocked, Tarpit, Honeypot
        public string? Country;
        public string? Asn;
        public string? City;
        public string? Region;
        public string? Isp;
        public string? Org;
        public double? Latitude;
        public double? Longitude;
        public string? Notes;
        public string? AssociatedProfileId;
        public string? AssociatedProfileName;
        public int? AssociatedProfileRank;
        public long? AssociatedProfileLastSeenUtc;
        public long? FirstAlertedUtc;
        public long? LastAlertedUtc;
        public int EscalationLevel;
        public string? LastBlockReason;
        public long? LastScannedUtc;

        // Fingerprint classification (written by FingerprintEngine).
        public string? Classification;
        public double? ClassConfidence;
        /// <summary>Comma-separated tags, e.g. "Datacenter:AWS,SpoofedUA".</summary>
        public string? ClassTags;
        public long? ClassUpdatedUtc;

        // Network intel from ip-api (hosting/proxy/mobile flags, reverse DNS, tz).
        public bool? IsHosting;
        public bool? IsProxy;
        public bool? IsMobile;
        public string? ReverseDns;
        public string? Timezone;
        public string? AsName;
    }

    public class HoneypotRouteRow
    {
        public string Route = "";
        public long CreatedUtc;
        public string ResponseKind = "JunkJson";
        public string? Note;
    }

    public class BlockedRegionRow
    {
        public long Id;
        public double LatMin;
        public double LatMax;
        public double LonMin;
        public double LonMax;
        public string? Reason;
        public long CreatedUtc;
        public string? CreatedBy;
    }
}
