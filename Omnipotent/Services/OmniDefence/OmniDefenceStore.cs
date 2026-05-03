using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Data;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// SQLite-backed persistence for OmniDefence: requests, auth events,
    /// profile actions, IP records and IP events.
    /// All access funnels through a single connection serialized by a
    /// SemaphoreSlim to keep things simple and avoid SQLITE_BUSY.
    /// </summary>
    public class OmniDefenceStore
    {
        private readonly string dbPath;
        private readonly string connectionString;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private SqliteConnection? sharedConnection;

        public OmniDefenceStore(string dbPath)
        {
            this.dbPath = dbPath;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
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

        // ---------- Insert helpers ----------

        public Task InsertRequestAsync(RequestRow row) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO requests
                (utc_ts, ip, method, route, query, status_code, duration_ms, profile_id, profile_name, profile_rank, perm_required, matched_route, body_hash, body_length, user_agent, deny_reason, request_origin, client_page)
                VALUES ($ts,$ip,$method,$route,$query,$status,$dur,$pid,$pname,$prank,$perm,$matched,$bh,$blen,$ua,$deny,$origin,$page)";
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
            await cmd.ExecuteNonQueryAsync();
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
        public Task UpsertIpRecordAsync(IpRecord rec) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO ip_records
                (ip, first_seen, last_seen, total_requests, successful_requests, unauth_attempts, deny_count, threat_score, status, country, asn, city, region, isp, org, latitude, longitude, notes, associated_profile_id, associated_profile_name, associated_profile_rank, associated_profile_last_seen_utc, first_alerted_utc, last_alerted_utc, escalation_level, last_block_reason, last_scanned_utc)
                VALUES ($ip,$fs,$ls,$tot,$succ,$ua,$dc,$ts,$st,$co,$asn,$city,$region,$isp,$org,$lat,$lon,$nt,$apid,$apname,$aprank,$aplast,$fa,$la,$el,$lbr,$lsc)
                ON CONFLICT(ip) DO UPDATE SET
                    last_seen=excluded.last_seen,
                    total_requests=excluded.total_requests,
                    successful_requests=excluded.successful_requests,
                    unauth_attempts=excluded.unauth_attempts,
                    deny_count=excluded.deny_count,
                    threat_score=excluded.threat_score,
                    status=excluded.status,
                    country=COALESCE(excluded.country, ip_records.country),
                    asn=COALESCE(excluded.asn, ip_records.asn),
                    city=COALESCE(excluded.city, ip_records.city),
                    region=COALESCE(excluded.region, ip_records.region),
                    isp=COALESCE(excluded.isp, ip_records.isp),
                    org=COALESCE(excluded.org, ip_records.org),
                    latitude=COALESCE(excluded.latitude, ip_records.latitude),
                    longitude=COALESCE(excluded.longitude, ip_records.longitude),
                    notes=excluded.notes,
                    associated_profile_id=COALESCE(excluded.associated_profile_id, ip_records.associated_profile_id),
                    associated_profile_name=COALESCE(excluded.associated_profile_name, ip_records.associated_profile_name),
                    associated_profile_rank=COALESCE(excluded.associated_profile_rank, ip_records.associated_profile_rank),
                    associated_profile_last_seen_utc=COALESCE(excluded.associated_profile_last_seen_utc, ip_records.associated_profile_last_seen_utc),
                    first_alerted_utc=COALESCE(excluded.first_alerted_utc, ip_records.first_alerted_utc),
                    last_alerted_utc=excluded.last_alerted_utc,
                    escalation_level=excluded.escalation_level,
                    last_block_reason=excluded.last_block_reason,
                    last_scanned_utc=COALESCE(excluded.last_scanned_utc, ip_records.last_scanned_utc)";
            cmd.Parameters.AddWithValue("$ip", rec.Ip);
            cmd.Parameters.AddWithValue("$fs", rec.FirstSeen);
            cmd.Parameters.AddWithValue("$ls", rec.LastSeen);
            cmd.Parameters.AddWithValue("$tot", rec.TotalRequests);
            cmd.Parameters.AddWithValue("$succ", rec.SuccessfulRequests);
            cmd.Parameters.AddWithValue("$ua", rec.UnauthAttempts);
            cmd.Parameters.AddWithValue("$dc", rec.DenyCount);
            cmd.Parameters.AddWithValue("$ts", rec.ThreatScore);
            cmd.Parameters.AddWithValue("$st", rec.Status);
            cmd.Parameters.AddWithValue("$co", (object?)rec.Country ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$asn", (object?)rec.Asn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$city", (object?)rec.City ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$region", (object?)rec.Region ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isp", (object?)rec.Isp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$org", (object?)rec.Org ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lat", (object?)rec.Latitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lon", (object?)rec.Longitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nt", (object?)rec.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$apid", (object?)rec.AssociatedProfileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$apname", (object?)rec.AssociatedProfileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$aprank", (object?)rec.AssociatedProfileRank ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$aplast", (object?)rec.AssociatedProfileLastSeenUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fa", (object?)rec.FirstAlertedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$la", (object?)rec.LastAlertedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$el", rec.EscalationLevel);
            cmd.Parameters.AddWithValue("$lbr", (object?)rec.LastBlockReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lsc", (object?)rec.LastScannedUtc ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });

        public Task<IpRecord?> GetIpRecordAsync(string ip) => WithLockAsync(async conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM ip_records WHERE ip=$ip";
            cmd.Parameters.AddWithValue("$ip", ip);
            using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync()) return ReadIpRecord(rdr);
            return null;
        });

        public Task<List<IpRecord>> ListIpRecordsAsync(string? statusFilter, double minScore, string? query, int limit, int offset)
            => WithLockAsync(async conn =>
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

        public Task<List<IpRecord>> LoadAllIpRecordsAsync() => WithLockAsync(async conn =>
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
            => WithLockAsync(async conn =>
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
            => WithLockAsync(async conn =>
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
        public Task<List<HoneypotRouteRow>> ListHoneypotRoutesAsync() => WithLockAsync(async conn =>
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
        public Task<List<BlockedRegionRow>> ListBlockedRegionsAsync() => WithLockAsync(async conn =>
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
                LastScannedUtc = rdr["last_scanned_utc"] is DBNull ? null : Convert.ToInt64(rdr["last_scanned_utc"])
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
