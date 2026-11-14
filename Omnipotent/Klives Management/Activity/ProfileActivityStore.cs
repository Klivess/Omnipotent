using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Profiles.Activity
{
    /// <summary>One API request made by a profile (batch items are recorded individually).</summary>
    public sealed class ActivityRequest
    {
        public string ProfileId { get; set; } = "";
        public long TsMs { get; set; }
        public string? SessionId { get; set; }
        public string? Method { get; set; }
        public string? Route { get; set; }
        public string? PermKey { get; set; }
        public string? Service { get; set; }
        public int Status { get; set; }
        public double DurationMs { get; set; }
        public string? Ip { get; set; }
        public string? Page { get; set; }
        public string? DenyReason { get; set; }
        public bool ViaBatch { get; set; }
    }

    /// <summary>A page the profile had open on the website, with how long it stayed there.</summary>
    public sealed class ActivityPageView
    {
        public string ProfileId { get; set; } = "";
        public string? SessionId { get; set; }
        public long TsMs { get; set; }
        public string? Path { get; set; }
        public string? Title { get; set; }
        public long DwellMs { get; set; }
    }

    /// <summary>
    /// Something that happened to (or was done by) a profile: logins, sign-outs, access changes,
    /// suspensions. <see cref="ActorId"/> is who did it when that is not the profile itself.
    /// </summary>
    public sealed class ActivityEvent
    {
        public string ProfileId { get; set; } = "";
        public long TsMs { get; set; }
        public string Kind { get; set; } = "";
        public string? ActorId { get; set; }
        public string? ActorName { get; set; }
        public string? Ip { get; set; }
        public string? DetailJson { get; set; }
    }

    /// <summary>
    /// What each profile did, for the profile console: its own SQLite database so it never contends
    /// with OmniDefence's shared write lock. Writes are fire-and-forget through a bounded queue and
    /// committed in batches; nothing on a request path ever waits on this store.
    ///
    /// The profile directory (list / get) asks for "requests in the last 24 h" and "last seen" per
    /// profile on every call. Those are kept in memory, fed by <see cref="WriteBatch"/> and seeded
    /// once from the database in the background, so the directory never touches the database.
    ///
    /// Summaries read hourly rollups, not raw rows. On the server a page read is slow and the
    /// owner's rows fill most pages, so a quieter profile's rows sit one to a page: a 30-day summary
    /// read tens of thousands of pages (p95 22.9 s live, max 48.7 s) and "refused only" walked every
    /// row of the profile (11.2 s live). The rollups are clustered by profile and hour
    /// (WITHOUT ROWID), so a summary reads a few hundred rows that sit together. They are written
    /// with every batch; rows from before they existed are rolled up once, in the background,
    /// resuming where a restart left off.
    /// </summary>
    public sealed class ProfileActivityStore : IDisposable
    {
        private const int MaxQueue = 50_000;
        private const int BatchSize = 256;
        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);

        private const long MinuteMs = 60_000;
        private const long HourMs = 3_600_000;
        private static readonly TimeSpan TallyWindow = TimeSpan.FromHours(24);
        /// <summary>More minutes than the window holds, so two minutes inside the window never share a slot.</summary>
        private const int TallySlots = 1500;
        private static readonly TimeSpan SummaryFresh = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan SummaryStale = TimeSpan.FromMinutes(5);
        /// <summary>Raw rows rolled up per transaction by the backfill (keeps the write lock short).</summary>
        internal int BackfillChunk { get; set; } = 5_000;
        /// <summary>Off in tests that drive the index build and backfill by hand.</summary>
        internal bool AutoUpgrade { get; set; } = true;

        private readonly ConcurrentDictionary<string, RequestTally> tallies = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> lastSeen = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Profile, TimeSpan Range), SummaryEntry> summaries = new();
        private Task? seeding;
        private Task? upgrading;

        private readonly string dbPath;
        private readonly Action<string> log;
        private readonly string connectionString;
        private readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(MaxQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        private readonly CancellationTokenSource stop = new();
        private Task? writer;
        private Task? retention;
        private volatile bool ready;
        private volatile bool indexesReady;
        private volatile bool rollupsReady;
        private long rollupUptoId;
        private long rollupThroughId;
        private long rollupsSinceMs;

        /// <summary>Raw rows are kept this long; daily rollups and events are kept longer.</summary>
        public int RetentionDays { get; set; } = 90;
        public int EventRetentionDays { get; set; } = 400;

        /// <summary>Raised synchronously when a request is enqueued (feeds the live view).</summary>
        public event Action<ActivityRequest>? RequestRecorded;
        public event Action<ActivityEvent>? EventRecorded;

        public ProfileActivityStore(string dbPath, Action<string> log)
        {
            this.dbPath = dbPath;
            this.log = log;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();
        }

        public bool IsReady => ready;

        /// <summary>The indexes behind "refused only" and the service filter exist.</summary>
        public bool IndexesReady => indexesReady;

        /// <summary>Every raw row is in the rollups (summaries no longer read raw rows).</summary>
        public bool RollupsReady => rollupsReady;

        /// <summary>How far the one-off backfill has got: rows rolled up so far out of the rows that predate the rollups.</summary>
        public (long Through, long Upto) RollupProgress => (Interlocked.Read(ref rollupThroughId), Interlocked.Read(ref rollupUptoId));

        public void Start()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
                long seedUpToId;
                using (var conn = Open())
                {
                    Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
                    // Tables and indexes on empty tables only: anything that has to read the request
                    // log runs in the background, so a big log never delays the profile routes.
                    foreach (string ddl in Schema) Exec(conn, ddl);
                    seedUpToId = Scalar<long>(conn, "SELECT COALESCE(MAX(id), 0) FROM requests;");
                    // The first start with rollups: everything up to here predates them and is
                    // rolled up by the backfill; every later row is rolled up as it is written.
                    Exec(conn, "INSERT OR IGNORE INTO meta (k, v) VALUES ('rollup_upto_id', $m);", ("$m", seedUpToId));
                    Exec(conn, "INSERT OR IGNORE INTO meta (k, v) VALUES ('rollup_since_ms', $t);", ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    Exec(conn, "INSERT OR IGNORE INTO meta (k, v) VALUES ('rollup_through_id', 0);");
                    rollupUptoId = Scalar<long>(conn, "SELECT v FROM meta WHERE k = 'rollup_upto_id';");
                    rollupThroughId = Scalar<long>(conn, "SELECT v FROM meta WHERE k = 'rollup_through_id';");
                    rollupsSinceMs = Scalar<long>(conn, "SELECT v FROM meta WHERE k = 'rollup_since_ms';");
                    rollupsReady = rollupThroughId >= rollupUptoId;
                    indexesReady = SlowIndexes.All(ix => Scalar<long>(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $n;", ("$n", ix.Name)) > 0);
                }
                ready = true;
                // Rows written from here on (ids above seedUpToId) are counted live by WriteBatch;
                // the seed counts only what was already there, so nothing is counted twice.
                seeding = Task.Run(() => Seed(seedUpToId));
                if (AutoUpgrade) upgrading = Task.Run(Upgrade);
                writer = Task.Run(WriterLoop);
                retention = Task.Run(RetentionLoop);
            }
            catch (Exception ex)
            {
                log("KMProfiles: activity store unavailable, activity will not be recorded: " + ex.Message);
            }
        }

        private static readonly string[] Schema =
        {
            @"CREATE TABLE IF NOT EXISTS requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                ts_ms INTEGER NOT NULL,
                session_id TEXT,
                method TEXT,
                route TEXT,
                perm_key TEXT,
                service TEXT,
                status INTEGER,
                duration_ms REAL,
                ip TEXT,
                page TEXT,
                deny_reason TEXT,
                via_batch INTEGER NOT NULL DEFAULT 0
            );",
            "CREATE INDEX IF NOT EXISTS ix_req_profile_ts ON requests(profile_id, ts_ms DESC);",
            "CREATE INDEX IF NOT EXISTS ix_req_ts ON requests(ts_ms);",
            @"CREATE TABLE IF NOT EXISTS page_views (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                session_id TEXT,
                ts_ms INTEGER NOT NULL,
                path TEXT,
                title TEXT,
                dwell_ms INTEGER NOT NULL DEFAULT 0
            );",
            "CREATE INDEX IF NOT EXISTS ix_pv_profile_ts ON page_views(profile_id, ts_ms DESC);",
            @"CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                ts_ms INTEGER NOT NULL,
                kind TEXT NOT NULL,
                actor_id TEXT,
                actor_name TEXT,
                ip TEXT,
                detail_json TEXT
            );",
            "CREATE INDEX IF NOT EXISTS ix_ev_profile_ts ON events(profile_id, ts_ms DESC);",
            @"CREATE TABLE IF NOT EXISTS daily_rollup (
                profile_id TEXT NOT NULL,
                day TEXT NOT NULL,
                perm_key TEXT NOT NULL,
                count INTEGER NOT NULL,
                denied INTEGER NOT NULL,
                last_ts_ms INTEGER NOT NULL,
                PRIMARY KEY (profile_id, day, perm_key)
            );",
            // One row per profile, hour, route, method and credential kind (password = no session).
            // Service and permission follow from the route and method; the latest seen wins.
            @"CREATE TABLE IF NOT EXISTS activity_hourly (
                profile_id TEXT NOT NULL,
                hour_ms INTEGER NOT NULL,
                route TEXT NOT NULL,
                method TEXT NOT NULL,
                password INTEGER NOT NULL,
                service TEXT NOT NULL,
                perm_key TEXT NOT NULL,
                count INTEGER NOT NULL,
                denied INTEGER NOT NULL,
                errors INTEGER NOT NULL,
                last_ts_ms INTEGER NOT NULL,
                PRIMARY KEY (profile_id, hour_ms, route, method, password)
            ) WITHOUT ROWID;",
            @"CREATE TABLE IF NOT EXISTS activity_hourly_ips (
                profile_id TEXT NOT NULL,
                hour_ms INTEGER NOT NULL,
                ip TEXT NOT NULL,
                count INTEGER NOT NULL,
                first_ts_ms INTEGER NOT NULL,
                last_ts_ms INTEGER NOT NULL,
                PRIMARY KEY (profile_id, hour_ms, ip)
            ) WITHOUT ROWID;",
            "CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v INTEGER NOT NULL);",
        };

        /// <summary>
        /// Indexes over the request log (and the daily rollup) that a long log takes a while to build,
        /// so they are built after start-up. Until they exist, reads fall back to the plain queries.
        /// </summary>
        internal static readonly (string Name, string Ddl)[] SlowIndexes =
        {
            // "Refused only" and the summary's recent refusals: a small partial index, so a profile
            // that is rarely refused costs a handful of rows instead of every row it ever made.
            ("ix_req_denied", "CREATE INDEX IF NOT EXISTS ix_req_denied ON requests(profile_id, ts_ms DESC) WHERE status IN (401, 403);"),
            ("ix_req_profile_service_ts", "CREATE INDEX IF NOT EXISTS ix_req_profile_service_ts ON requests(profile_id, service, ts_ms DESC);"),
            // Usage reads (count, denied, last) per permission: covering, in the order it groups by.
            ("ix_ru_profile_perm", "CREATE INDEX IF NOT EXISTS ix_ru_profile_perm ON daily_rollup(profile_id, perm_key, last_ts_ms, count, denied);"),
        };

        // ───────────────────────────── writes ─────────────────────────────

        public void EnqueueRequest(ActivityRequest r)
        {
            if (!ready || string.IsNullOrEmpty(r.ProfileId)) return;
            queue.Writer.TryWrite(r);
            try { RequestRecorded?.Invoke(r); } catch { }
        }

        public void EnqueuePageView(ActivityPageView v)
        {
            if (!ready || string.IsNullOrEmpty(v.ProfileId)) return;
            queue.Writer.TryWrite(v);
        }

        public void EnqueueEvent(ActivityEvent e)
        {
            if (!ready || string.IsNullOrEmpty(e.ProfileId)) return;
            if (e.TsMs == 0) e.TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            queue.Writer.TryWrite(e);
            try { EventRecorded?.Invoke(e); } catch { }
        }

        private async Task WriterLoop()
        {
            var batch = new List<object>(BatchSize);
            var reader = queue.Reader;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!await reader.WaitToReadAsync(stop.Token)) break;
                    var deadline = DateTime.UtcNow + BatchWindow;
                    while (batch.Count < BatchSize)
                    {
                        while (batch.Count < BatchSize && reader.TryRead(out var item)) batch.Add(item);
                        if (batch.Count >= BatchSize) break;
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        wait.CancelAfter(remaining);
                        try { if (!await reader.WaitToReadAsync(wait.Token)) break; }
                        catch (OperationCanceledException) { break; }
                    }
                    if (batch.Count > 0) await WriteBatchWhenFreeAsync(batch);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    log("KMProfiles: failed to write profile activity (batch dropped): " + ex.Message);
                }
                finally { batch.Clear(); }
            }
        }

        /// <summary>
        /// Writes a batch, waiting out a busy database (the one-off index build after an upgrade can
        /// hold the write lock for longer than the 30 s busy timeout) instead of dropping it.
        /// </summary>
        private async Task WriteBatchWhenFreeAsync(IReadOnlyList<object> items)
        {
            int[] waitsSeconds = { 5, 15, 40, 90 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    WriteBatch(items);
                    return;
                }
                catch (SqliteException ex) when ((ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) && attempt < waitsSeconds.Length)
                {
                    await Task.Delay(TimeSpan.FromSeconds(waitsSeconds[attempt]), stop.Token);
                }
            }
        }

        internal void WriteBatch(IReadOnlyList<object> items)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var reqCmd = conn.CreateCommand();
            reqCmd.Transaction = tx;
            reqCmd.CommandText = @"INSERT INTO requests (profile_id, ts_ms, session_id, method, route, perm_key, service, status, duration_ms, ip, page, deny_reason, via_batch)
                VALUES ($p, $t, $s, $m, $r, $k, $svc, $st, $d, $ip, $pg, $dr, $vb);";
            var rp = AddParams(reqCmd, "$p", "$t", "$s", "$m", "$r", "$k", "$svc", "$st", "$d", "$ip", "$pg", "$dr", "$vb");

            using var pvCmd = conn.CreateCommand();
            pvCmd.Transaction = tx;
            pvCmd.CommandText = "INSERT INTO page_views (profile_id, session_id, ts_ms, path, title, dwell_ms) VALUES ($p, $s, $t, $pa, $ti, $dw);";
            var pp = AddParams(pvCmd, "$p", "$s", "$t", "$pa", "$ti", "$dw");

            using var evCmd = conn.CreateCommand();
            evCmd.Transaction = tx;
            evCmd.CommandText = "INSERT INTO events (profile_id, ts_ms, kind, actor_id, actor_name, ip, detail_json) VALUES ($p, $t, $k, $a, $an, $ip, $d);";
            var ep = AddParams(evCmd, "$p", "$t", "$k", "$a", "$an", "$ip", "$d");

            var rollup = new Dictionary<(string, string, string), (int Count, int Denied, long Last)>();
            var hourly = new HourlyAggregate();
            foreach (var item in items)
            {
                switch (item)
                {
                    case ActivityRequest r:
                        Set(rp, r.ProfileId, r.TsMs, r.SessionId, r.Method, r.Route, r.PermKey, r.Service, r.Status, r.DurationMs,
                            r.Ip, Trim(r.Page, 512), r.DenyReason, r.ViaBatch ? 1 : 0);
                        reqCmd.ExecuteNonQuery();
                        string day = DayOf(r.TsMs);
                        var key = (r.ProfileId, day, r.PermKey ?? "");
                        rollup.TryGetValue(key, out var agg);
                        rollup[key] = (agg.Count + 1, agg.Denied + (IsDenied(r.Status) ? 1 : 0), Math.Max(agg.Last, r.TsMs));
                        hourly.Add(r.ProfileId, r.TsMs, r.Route, r.Method, r.SessionId == null, r.Service, r.PermKey, r.Status, r.Ip);
                        break;
                    case ActivityPageView v:
                        Set(pp, v.ProfileId, v.SessionId, v.TsMs, Trim(v.Path, 512), Trim(v.Title, 256), v.DwellMs);
                        pvCmd.ExecuteNonQuery();
                        break;
                    case ActivityEvent e:
                        Set(ep, e.ProfileId, e.TsMs, e.Kind, e.ActorId, e.ActorName, e.Ip, Trim(e.DetailJson, 8192));
                        evCmd.ExecuteNonQuery();
                        break;
                }
            }

            if (rollup.Count > 0)
            {
                using var ruCmd = conn.CreateCommand();
                ruCmd.Transaction = tx;
                ruCmd.CommandText = @"INSERT INTO daily_rollup (profile_id, day, perm_key, count, denied, last_ts_ms) VALUES ($p, $d, $k, $c, $dn, $l)
                    ON CONFLICT(profile_id, day, perm_key) DO UPDATE SET count = count + excluded.count, denied = denied + excluded.denied,
                    last_ts_ms = MAX(last_ts_ms, excluded.last_ts_ms);";
                var up = AddParams(ruCmd, "$p", "$d", "$k", "$c", "$dn", "$l");
                foreach (var ((profile, day, permKey), agg) in rollup)
                {
                    Set(up, profile, day, permKey, agg.Count, agg.Denied, agg.Last);
                    ruCmd.ExecuteNonQuery();
                }
            }
            hourly.Write(conn, tx);
            tx.Commit();

            foreach (var item in items)
            {
                if (item is ActivityRequest r) NoteRequest(r);
            }
        }

        /// <summary>Requests folded into the hourly rollups in memory, then upserted once per key.</summary>
        private sealed class HourlyAggregate
        {
            private readonly Dictionary<(string Profile, long Hour, string Route, string Method, int Password), (string Service, string Perm, long Count, long Denied, long Errors, long Last)> routes = new();
            private readonly Dictionary<(string Profile, long Hour, string Ip), (long Count, long First, long Last)> ips = new();

            public int Count => routes.Count;

            public void Add(string profileId, long tsMs, string? route, string? method, bool password, string? service, string? permKey, int status, string? ip)
            {
                long hour = tsMs / HourMs * HourMs;
                var key = (profileId, hour, route ?? "", method ?? "", password ? 1 : 0);
                routes.TryGetValue(key, out var r);
                routes[key] = (service ?? "Other", permKey ?? "", r.Count + 1, r.Denied + (IsDenied(status) ? 1 : 0),
                    r.Errors + (status >= 500 ? 1 : 0), Math.Max(r.Last, tsMs));
                if (string.IsNullOrEmpty(ip)) return;
                var ipKey = (profileId, hour, ip);
                if (ips.TryGetValue(ipKey, out var u)) ips[ipKey] = (u.Count + 1, Math.Min(u.First, tsMs), Math.Max(u.Last, tsMs));
                else ips[ipKey] = (1, tsMs, tsMs);
            }

            public void Write(SqliteConnection conn, SqliteTransaction tx)
            {
                if (routes.Count > 0)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO activity_hourly (profile_id, hour_ms, route, method, password, service, perm_key, count, denied, errors, last_ts_ms)
                        VALUES ($p, $h, $r, $m, $pw, $s, $k, $c, $d, $e, $l)
                        ON CONFLICT(profile_id, hour_ms, route, method, password) DO UPDATE SET count = count + excluded.count,
                        denied = denied + excluded.denied, errors = errors + excluded.errors,
                        service = CASE WHEN excluded.last_ts_ms >= last_ts_ms THEN excluded.service ELSE service END,
                        perm_key = CASE WHEN excluded.last_ts_ms >= last_ts_ms THEN excluded.perm_key ELSE perm_key END,
                        last_ts_ms = MAX(last_ts_ms, excluded.last_ts_ms);";
                    var ps = AddParams(cmd, "$p", "$h", "$r", "$m", "$pw", "$s", "$k", "$c", "$d", "$e", "$l");
                    foreach (var ((profile, hour, route, method, password), v) in routes)
                    {
                        Set(ps, profile, hour, route, method, password, v.Service, v.Perm, v.Count, v.Denied, v.Errors, v.Last);
                        cmd.ExecuteNonQuery();
                    }
                }
                if (ips.Count > 0)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO activity_hourly_ips (profile_id, hour_ms, ip, count, first_ts_ms, last_ts_ms) VALUES ($p, $h, $ip, $c, $f, $l)
                        ON CONFLICT(profile_id, hour_ms, ip) DO UPDATE SET count = count + excluded.count,
                        first_ts_ms = MIN(first_ts_ms, excluded.first_ts_ms), last_ts_ms = MAX(last_ts_ms, excluded.last_ts_ms);";
                    var ps = AddParams(cmd, "$p", "$h", "$ip", "$c", "$f", "$l");
                    foreach (var ((profile, hour, ip), v) in ips)
                    {
                        Set(ps, profile, hour, ip, v.Count, v.First, v.Last);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static bool IsDenied(int? status) => status == 401 || status == 403;

        private void NoteRequest(ActivityRequest r)
        {
            tallies.GetOrAdd(r.ProfileId, _ => new RequestTally()).Add(r.TsMs, 1, IsDenied(r.Status) ? 1 : 0);
            NoteSeen(r.ProfileId, r.TsMs);
        }

        private void NoteSeen(string profileId, long tsMs)
            => lastSeen.AddOrUpdate(profileId, tsMs, (_, current) => Math.Max(current, tsMs));

        /// <summary>
        /// Loads the counters from what is already in the database. Runs once, in the background;
        /// until it finishes the directory shows what has been counted so far.
        /// </summary>
        private void Seed(long upToRequestId)
        {
            try
            {
                using var conn = Open();
                if (upToRequestId > 0)
                {
                    long from = DateTimeOffset.UtcNow.Subtract(TallyWindow).ToUnixTimeMilliseconds() - MinuteMs;
                    // INDEXED BY: left alone SQLite would scan the whole table in profile order to
                    // dodge a sort — every row for 90 days instead of the last 24 hours.
                    using var cmd = Command(conn, @"SELECT profile_id, ts_ms / 60000, COUNT(*), SUM(CASE WHEN status IN (401, 403) THEN 1 ELSE 0 END)
                        FROM requests INDEXED BY ix_req_ts WHERE ts_ms >= $s AND id <= $m GROUP BY profile_id, ts_ms / 60000;",
                        ("$s", from), ("$m", upToRequestId));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        tallies.GetOrAdd(r.GetString(0), _ => new RequestTally()).Add(r.GetInt64(1) * MinuteMs, r.GetInt64(2), r.GetInt64(3));
                }
                using var seen = Command(conn, "SELECT profile_id, MAX(last_ts_ms) FROM daily_rollup GROUP BY profile_id;");
                using var rs = seen.ExecuteReader();
                while (rs.Read()) NoteSeen(rs.GetString(0), rs.GetInt64(1));
            }
            catch (Exception ex)
            {
                log("KMProfiles: could not load the activity counters (they will build up from now on): " + ex.Message);
            }
        }

        /// <summary>Blocks until the background seed has finished (tests, diagnostics).</summary>
        internal bool WaitUntilSeeded(TimeSpan timeout) => seeding?.Wait(timeout) ?? true;

        /// <summary>Blocks until the background index build and rollup backfill have finished (tests).</summary>
        internal bool WaitUntilUpgraded(TimeSpan timeout) => upgrading?.Wait(timeout) ?? true;

        /// <summary>
        /// The background half of start-up: builds the indexes a long log takes a while to index,
        /// then rolls up the rows that predate the rollups, a chunk per transaction, recording how
        /// far it got so a restart carries on instead of starting again.
        /// </summary>
        private void Upgrade()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!indexesReady)
                {
                    using var conn = Open();
                    foreach (var (_, ddl) in SlowIndexes)
                    {
                        if (stop.IsCancellationRequested) return;
                        Exec(conn, ddl);
                    }
                    indexesReady = true;
                    log($"KMProfiles: activity indexes built in {clock.Elapsed.TotalSeconds:0.0} s.");
                }
                if (!rollupsReady) Backfill();
            }
            catch (Exception ex)
            {
                log("KMProfiles: could not finish preparing the activity store (summaries read raw rows until the next start): " + ex.Message);
            }
        }

        /// <summary>Builds the indexes a long log takes a while to index (tests call it directly).</summary>
        internal void BuildSlowIndexes()
        {
            using var conn = Open();
            foreach (var (_, ddl) in SlowIndexes) Exec(conn, ddl);
            indexesReady = true;
        }

        /// <summary>Rolls up raw rows with ids in (rollup_through_id, rollup_upto_id], at most <paramref name="maxChunks"/> chunks.</summary>
        internal void Backfill(int maxChunks = int.MaxValue)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long upto = Interlocked.Read(ref rollupUptoId);
            long rows = 0;
            for (int chunk = 0; chunk < maxChunks && !stop.IsCancellationRequested; chunk++)
            {
                long through = Interlocked.Read(ref rollupThroughId);
                if (through >= upto) break;
                long next = Math.Min(upto, through + BackfillChunk);
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                var hourly = new HourlyAggregate();
                using (var cmd = Command(conn, "SELECT profile_id, ts_ms, route, session_id IS NULL, service, perm_key, status, ip, method FROM requests WHERE id > $a AND id <= $b;",
                    ("$a", through), ("$b", next)))
                {
                    cmd.Transaction = tx;
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        hourly.Add(r.GetString(0), r.GetInt64(1), Str(r, 2), Str(r, 8), r.GetInt64(3) == 1, Str(r, 4), Str(r, 5), r.IsDBNull(6) ? 0 : r.GetInt32(6), Str(r, 7));
                        rows++;
                    }
                }
                hourly.Write(conn, tx);
                using (var mark = Command(conn, "UPDATE meta SET v = $v WHERE k = 'rollup_through_id';", ("$v", next)))
                {
                    mark.Transaction = tx;
                    mark.ExecuteNonQuery();
                }
                tx.Commit();
                Interlocked.Exchange(ref rollupThroughId, next);
            }
            if (Interlocked.Read(ref rollupThroughId) >= upto)
            {
                rollupsReady = true;
                foreach (var entry in summaries.Values) lock (entry.Gate) entry.ComputedUtc = DateTime.MinValue;
                if (rows > 0) log($"KMProfiles: rolled up {rows:N0} earlier request(s) in {clock.Elapsed.TotalSeconds:0.0} s; summaries now read the rollups.");
            }
        }

        /// <summary>Requests and denials per minute for one profile, over a ring that covers the last 24 hours.</summary>
        private sealed class RequestTally
        {
            private readonly object gate = new();
            private readonly long[] minutes = new long[TallySlots];
            private readonly long[] requests = new long[TallySlots];
            private readonly long[] denied = new long[TallySlots];

            public void Add(long tsMs, long requestCount, long deniedCount)
            {
                long minute = tsMs / MinuteMs;
                long newest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / MinuteMs;
                // Older than the window can't be shown; mustn't overwrite a slot that now belongs to a newer minute.
                if (minute <= newest - (long)TallyWindow.TotalMinutes) return;
                int slot = (int)(minute % TallySlots);
                lock (gate)
                {
                    if (minutes[slot] > minute) return;
                    if (minutes[slot] != minute)
                    {
                        minutes[slot] = minute;
                        requests[slot] = 0;
                        denied[slot] = 0;
                    }
                    requests[slot] += requestCount;
                    denied[slot] += deniedCount;
                }
            }

            /// <summary>Counts from the minute containing <paramref name="sinceMs"/> onwards.</summary>
            public (long Requests, long Denied) Since(long sinceMs)
            {
                long from = sinceMs / MinuteMs;
                long total = 0, deniedTotal = 0;
                lock (gate)
                {
                    for (int i = 0; i < TallySlots; i++)
                    {
                        if (requests[i] == 0 || minutes[i] < from) continue;
                        total += requests[i];
                        deniedTotal += denied[i];
                    }
                }
                return (total, deniedTotal);
            }

            /// <summary>Requests per hour for the <paramref name="hours"/> hours ending with the one containing <paramref name="nowMs"/>, oldest first.</summary>
            public long[] Hourly(long nowMs, int hours)
            {
                var result = new long[hours];
                long lastHour = nowMs / HourMs;
                long firstHour = lastHour - hours + 1;
                lock (gate)
                {
                    for (int i = 0; i < TallySlots; i++)
                    {
                        if (requests[i] == 0) continue;
                        long hour = minutes[i] * MinuteMs / HourMs;
                        if (hour < firstHour || hour > lastHour) continue;
                        result[hour - firstHour] += requests[i];
                    }
                }
                return result;
            }
        }

        private async Task RetentionLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), stop.Token);
                    Prune(DateTimeOffset.UtcNow);
                    await Task.Delay(TimeSpan.FromHours(6), stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { log("KMProfiles: activity retention failed: " + ex.Message); }
            }
        }

        internal void Prune(DateTimeOffset now)
        {
            long rawCutoff = now.AddDays(-Math.Max(1, RetentionDays)).ToUnixTimeMilliseconds();
            long eventCutoff = now.AddDays(-Math.Max(RetentionDays, EventRetentionDays)).ToUnixTimeMilliseconds();
            using var conn = Open();
            Exec(conn, "DELETE FROM requests WHERE ts_ms < $c;", ("$c", rawCutoff));
            Exec(conn, "DELETE FROM page_views WHERE ts_ms < $c;", ("$c", rawCutoff));
            Exec(conn, "DELETE FROM events WHERE ts_ms < $c;", ("$c", eventCutoff));
            // The hourly rollups only ever answer windows of the last 30 days; keep them as long as raw rows.
            Exec(conn, "DELETE FROM activity_hourly WHERE hour_ms < $c;", ("$c", rawCutoff));
            Exec(conn, "DELETE FROM activity_hourly_ips WHERE hour_ms < $c;", ("$c", rawCutoff));
        }

        // ───────────────────────────── reads ─────────────────────────────

        public sealed class TimelineItem
        {
            public string Type { get; set; } = "";     // request | page | event
            public long TsMs { get; set; }
            public string? Kind { get; set; }           // event kind
            public string? Method { get; set; }
            public string? Route { get; set; }
            public string? PermKey { get; set; }
            public string? Service { get; set; }
            public int? Status { get; set; }
            public double? DurationMs { get; set; }
            public string? Ip { get; set; }
            public string? Page { get; set; }
            public string? Title { get; set; }
            public long? DwellMs { get; set; }
            public string? DenyReason { get; set; }
            public bool ViaBatch { get; set; }
            public string? ActorId { get; set; }
            public string? ActorName { get; set; }
            public string? DetailJson { get; set; }
            public string? SessionId { get; set; }
        }

        /// <summary>The timeline's request query: the index matching the filter, once it exists.</summary>
        internal string TimelineRequestSql(bool service, bool deniedOnly)
        {
            string index = !indexesReady ? "" : deniedOnly ? " INDEXED BY ix_req_denied" : service ? " INDEXED BY ix_req_profile_service_ts" : "";
            return $@"SELECT ts_ms, method, route, perm_key, service, status, duration_ms, ip, page, deny_reason, via_batch, session_id
                    FROM requests{index} WHERE profile_id = $p AND ts_ms < $b AND ts_ms >= $s"
                + (service ? " AND service = $svc" : "")
                // Written exactly as the partial index's WHERE, which is how SQLite knows it applies.
                + (deniedOnly ? " AND status IN (401, 403)" : "")
                + " ORDER BY ts_ms DESC LIMIT $l;";
        }

        /// <param name="types">Any of request, page, event (null = all).</param>
        /// <param name="beforeMs">Cursor: only items strictly older than this.</param>
        public List<TimelineItem> GetTimeline(string profileId, long? beforeMs, long? sinceMs, int limit,
            ISet<string>? types = null, string? service = null, bool deniedOnly = false)
        {
            limit = Math.Clamp(limit, 1, 500);
            long before = beforeMs ?? long.MaxValue;
            long since = sinceMs ?? 0;
            var items = new List<TimelineItem>();
            if (!ready) return items;
            using var conn = Open();

            if (types == null || types.Contains("request"))
            {
                using var span = RequestTrace.AmbientSpan(deniedOnly ? "activity-refused" : service != null ? "activity-service" : "activity-requests");
                var args = new List<(string, object?)> { ("$p", profileId), ("$b", before), ("$s", since), ("$l", limit) };
                if (service != null) args.Add(("$svc", service));
                using var cmd = Command(conn, TimelineRequestSql(service != null, deniedOnly), args.ToArray());
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    items.Add(new TimelineItem
                    {
                        Type = "request", TsMs = r.GetInt64(0), Method = Str(r, 1), Route = Str(r, 2), PermKey = Str(r, 3),
                        Service = Str(r, 4), Status = r.IsDBNull(5) ? null : r.GetInt32(5), DurationMs = r.IsDBNull(6) ? null : r.GetDouble(6),
                        Ip = Str(r, 7), Page = Str(r, 8), DenyReason = Str(r, 9), ViaBatch = !r.IsDBNull(10) && r.GetInt32(10) == 1,
                        SessionId = Str(r, 11),
                    });
                }
            }
            if ((types == null || types.Contains("page")) && service == null && !deniedOnly)
            {
                using var span = RequestTrace.AmbientSpan("activity-pages");
                using var cmd = Command(conn, @"SELECT ts_ms, path, title, dwell_ms, session_id FROM page_views
                    WHERE profile_id = $p AND ts_ms < $b AND ts_ms >= $s ORDER BY ts_ms DESC LIMIT $l;",
                    ("$p", profileId), ("$b", before), ("$s", since), ("$l", limit));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    items.Add(new TimelineItem { Type = "page", TsMs = r.GetInt64(0), Page = Str(r, 1), Title = Str(r, 2), DwellMs = r.GetInt64(3), SessionId = Str(r, 4) });
                }
            }
            if ((types == null || types.Contains("event")) && service == null && !deniedOnly)
            {
                using var span = RequestTrace.AmbientSpan("activity-events");
                using var cmd = Command(conn, @"SELECT ts_ms, kind, actor_id, actor_name, ip, detail_json FROM events
                    WHERE profile_id = $p AND ts_ms < $b AND ts_ms >= $s ORDER BY ts_ms DESC LIMIT $l;",
                    ("$p", profileId), ("$b", before), ("$s", since), ("$l", limit));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    items.Add(new TimelineItem
                    {
                        Type = "event", TsMs = r.GetInt64(0), Kind = Str(r, 1), ActorId = Str(r, 2), ActorName = Str(r, 3),
                        Ip = Str(r, 4), DetailJson = Str(r, 5),
                    });
                }
            }
            return items.OrderByDescending(i => i.TsMs).Take(limit).ToList();
        }

        public sealed class ActivitySummary
        {
            public long FromMs { get; set; }
            public long ToMs { get; set; }
            public long BucketMs { get; set; }
            /// <summary>"rollup" (hourly rollups, window starts on the hour) or "rows" (raw rows, while the backfill runs).</summary>
            public string Source { get; set; } = "rows";
            public List<string> Services { get; set; } = new();
            /// <summary>One entry per bucket: start time and request count per service.</summary>
            public List<SummaryBucket> Buckets { get; set; } = new();
            public long Requests { get; set; }
            public long Denied { get; set; }
            public long Errors { get; set; }
            /// <summary>Requests made with a password instead of a session (scripts, devices, the old site).</summary>
            public long PasswordRequests { get; set; }
            public int DistinctRoutes { get; set; }
            public long PageViews { get; set; }
            public int ActiveDays { get; set; }
            public List<KeyCount> TopPermissions { get; set; } = new();
            public List<KeyCount> TopPages { get; set; } = new();
            public List<KeyCount> TopRoutes { get; set; } = new();
            public List<IpUse> Ips { get; set; } = new();
            public List<TimelineItem> RecentDenials { get; set; } = new();
            public List<TimelineItem> Logins { get; set; } = new();
        }

        public sealed class SummaryBucket
        {
            public long T { get; set; }
            public Dictionary<string, long> ByService { get; set; } = new();
            public long Denied { get; set; }
        }

        public sealed class KeyCount
        {
            public string Key { get; set; } = "";
            public long Count { get; set; }
            public long Denied { get; set; }
            public long LastMs { get; set; }
        }

        public sealed class IpUse
        {
            public string Ip { get; set; } = "";
            public long Count { get; set; }
            public long FirstMs { get; set; }
            public long LastMs { get; set; }
        }

        /// <summary>
        /// A profile's activity over <paramref name="range"/>: from the hourly rollups once every raw
        /// row is in them (the window then starts on the hour), from raw rows until then.
        /// </summary>
        public ActivitySummary GetSummary(string profileId, TimeSpan range, DateTimeOffset? nowUtc = null)
            => rollupsReady ? GetSummaryFromRollups(profileId, range, nowUtc) : GetSummaryFromRows(profileId, range, nowUtc);

        /// <summary>Fills one summary from a stream of (ts, route, password, service, perm, count, denied, errors, last) facts.</summary>
        private sealed class SummaryBuilder
        {
            private readonly ActivitySummary summary;
            private readonly SortedDictionary<long, SummaryBucket> buckets = new();
            private readonly HashSet<string> services = new(StringComparer.Ordinal);
            private readonly HashSet<string> paths = new(StringComparer.Ordinal);
            private readonly Dictionary<string, long> routes = new(StringComparer.Ordinal);
            private readonly Dictionary<string, KeyCount> permissions = new(StringComparer.Ordinal);

            public SummaryBuilder(ActivitySummary summary)
            {
                this.summary = summary;
                for (long t = summary.FromMs / summary.BucketMs * summary.BucketMs; t <= summary.ToMs; t += summary.BucketMs) buckets[t] = new SummaryBucket { T = t };
            }

            public void Add(long ts, string? route, string? method, bool password, string service, string permKey, long count, long denied, long errors, long lastMs)
            {
                long b = ts / summary.BucketMs * summary.BucketMs;
                if (!buckets.TryGetValue(b, out var sb)) buckets[b] = sb = new SummaryBucket { T = b };
                sb.ByService[service] = sb.ByService.GetValueOrDefault(service) + count;
                sb.Denied += denied;
                services.Add(service);
                summary.Requests += count;
                summary.Denied += denied;
                summary.Errors += errors;
                if (password) summary.PasswordRequests += count;
                if (route != null)
                {
                    paths.Add(route);
                    string label = string.IsNullOrEmpty(method) ? route : method + " " + route;
                    routes[label] = routes.GetValueOrDefault(label) + count;
                }

                if (!permissions.TryGetValue(permKey, out var pc)) permissions[permKey] = pc = new KeyCount { Key = permKey };
                pc.Count += count;
                pc.Denied += denied;
                if (lastMs > pc.LastMs) pc.LastMs = lastMs;
            }

            public void Finish()
            {
                summary.Buckets = buckets.Values.ToList();
                summary.Services = services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                summary.DistinctRoutes = paths.Count;
                summary.TopRoutes = routes.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Take(10)
                    .Select(r => new KeyCount { Key = r.Key, Count = r.Value }).ToList();
                summary.TopPermissions = permissions.Values.OrderByDescending(k => k.Count).ThenBy(k => k.Key, StringComparer.Ordinal).Take(15).ToList();
            }
        }

        private static long BucketFor(TimeSpan range) => range <= TimeSpan.FromDays(7) ? HourMs : 86_400_000L;

        /// <summary>The summary from raw rows: one pass over every request of the window.</summary>
        internal ActivitySummary GetSummaryFromRows(string profileId, TimeSpan range, DateTimeOffset? nowUtc = null)
        {
            DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
            long to = now.ToUnixTimeMilliseconds();
            long from = now.Subtract(range).ToUnixTimeMilliseconds();
            var summary = new ActivitySummary { FromMs = from, ToMs = to, BucketMs = BucketFor(range), Source = "rows" };
            if (!ready) return summary;

            using var conn = Open();
            var builder = new SummaryBuilder(summary);
            var ips = new Dictionary<string, IpUse>(StringComparer.Ordinal);
            using (RequestTrace.AmbientSpan("summary-rows"))
            using (var cmd = Command(conn, "SELECT ts_ms, service, status, route, perm_key, ip, session_id IS NULL, method FROM requests WHERE profile_id = $p AND ts_ms >= $f;",
                ("$p", profileId), ("$f", from)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    long ts = r.GetInt64(0);
                    int status = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    builder.Add(ts, Str(r, 3), Str(r, 7), r.GetInt64(6) == 1, r.IsDBNull(1) ? "Other" : r.GetString(1), r.IsDBNull(4) ? "" : r.GetString(4),
                        1, IsDenied(status) ? 1 : 0, status >= 500 ? 1 : 0, ts);
                    if (!r.IsDBNull(5))
                    {
                        string ip = r.GetString(5);
                        if (ip.Length > 0)
                        {
                            if (!ips.TryGetValue(ip, out var use)) ips[ip] = use = new IpUse { Ip = ip, FirstMs = ts, LastMs = ts };
                            use.Count++;
                            if (ts < use.FirstMs) use.FirstMs = ts;
                            if (ts > use.LastMs) use.LastMs = ts;
                        }
                    }
                }
            }
            builder.Finish();
            summary.Ips = ips.Values.OrderByDescending(i => i.LastMs).ThenBy(i => i.Ip, StringComparer.Ordinal).Take(20).ToList();
            FillSummaryExtras(conn, profileId, summary);
            return summary;
        }

        /// <summary>The summary from the hourly rollups: a few hundred rows that sit together.</summary>
        internal ActivitySummary GetSummaryFromRollups(string profileId, TimeSpan range, DateTimeOffset? nowUtc = null)
        {
            DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
            long to = now.ToUnixTimeMilliseconds();
            long from = now.Subtract(range).ToUnixTimeMilliseconds() / HourMs * HourMs;
            var summary = new ActivitySummary { FromMs = from, ToMs = to, BucketMs = BucketFor(range), Source = "rollup" };
            if (!ready) return summary;

            using var conn = Open();
            var builder = new SummaryBuilder(summary);
            using (RequestTrace.AmbientSpan("summary-rollup"))
            {
                using (var cmd = Command(conn, @"SELECT hour_ms, route, password, service, perm_key, count, denied, errors, last_ts_ms, method
                    FROM activity_hourly WHERE profile_id = $p AND hour_ms >= $f;", ("$p", profileId), ("$f", from)))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        string route = r.GetString(1);
                        builder.Add(r.GetInt64(0), route.Length == 0 ? null : route, r.GetString(9), r.GetInt64(2) == 1, r.GetString(3), r.GetString(4),
                            r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8));
                    }
                }
                using (var cmd = Command(conn, @"SELECT ip, SUM(count), MIN(first_ts_ms), MAX(last_ts_ms) FROM activity_hourly_ips
                    WHERE profile_id = $p AND hour_ms >= $f GROUP BY ip;", ("$p", profileId), ("$f", from)))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) summary.Ips.Add(new IpUse { Ip = r.GetString(0), Count = r.GetInt64(1), FirstMs = r.GetInt64(2), LastMs = r.GetInt64(3) });
                }
            }
            builder.Finish();
            summary.Ips = summary.Ips.OrderByDescending(i => i.LastMs).ThenBy(i => i.Ip, StringComparer.Ordinal).Take(20).ToList();
            FillSummaryExtras(conn, profileId, summary);
            return summary;
        }

        /// <summary>Page views, active days, recent refusals and sign-ins: small reads both summaries share.</summary>
        private void FillSummaryExtras(SqliteConnection conn, string profileId, ActivitySummary summary)
        {
            long from = summary.FromMs;
            using (RequestTrace.AmbientSpan("summary-pages"))
            {
                summary.PageViews = Scalar<long>(conn, "SELECT COUNT(*) FROM page_views WHERE profile_id = $p AND ts_ms >= $f;",
                    ("$p", profileId), ("$f", from));
                using var cmd = Command(conn, @"SELECT COALESCE(path, ''), COUNT(*), SUM(dwell_ms), MAX(ts_ms) FROM page_views
                        WHERE profile_id = $p AND ts_ms >= $f GROUP BY 1 ORDER BY 3 DESC LIMIT 10;",
                    ("$p", profileId), ("$f", from));
                using var r = cmd.ExecuteReader();
                while (r.Read()) summary.TopPages.Add(new KeyCount { Key = r.GetString(0), Count = r.GetInt64(1), Denied = r.IsDBNull(2) ? 0 : r.GetInt64(2), LastMs = r.GetInt64(3) });
            }
            using (RequestTrace.AmbientSpan("summary-days"))
            {
                summary.ActiveDays = Scalar<int>(conn, "SELECT COUNT(DISTINCT day) FROM daily_rollup WHERE profile_id = $p AND day >= $d;",
                    ("$p", profileId), ("$d", DayOf(from)));
            }
            using (RequestTrace.AmbientSpan("summary-refused"))
            {
                summary.RecentDenials = GetTimeline(profileId, null, from, 20, new HashSet<string> { "request" }, null, deniedOnly: true);
            }
            summary.Logins = GetTimeline(profileId, null, from, 40, new HashSet<string> { "event" })
                .Where(e => e.Kind != null && (e.Kind.StartsWith("login", StringComparison.Ordinal) || e.Kind.StartsWith("session", StringComparison.Ordinal)))
                .Take(20).ToList();
        }

        private sealed class SummaryEntry
        {
            public readonly object Gate = new();
            public ActivitySummary? Value;
            public DateTime ComputedUtc;
            public Task<ActivitySummary>? Refresh;
        }

        /// <summary>
        /// <see cref="GetSummary"/> for the profile console, off the caller's thread. The result is
        /// kept: younger than 15 s is served as is, up to 5 minutes old is served at once while one
        /// background pass refreshes it, and only the first look (or one after a long gap) waits.
        /// </summary>
        public Task<ActivitySummary> GetSummaryAsync(string profileId, TimeSpan range)
        {
            var entry = summaries.GetOrAdd((profileId, range), _ => new SummaryEntry());
            lock (entry.Gate)
            {
                TimeSpan age = DateTime.UtcNow - entry.ComputedUtc;
                if (entry.Value != null && age < SummaryFresh) return Task.FromResult(entry.Value);
                entry.Refresh ??= Task.Run(() =>
                {
                    try
                    {
                        var value = GetSummary(profileId, range);
                        lock (entry.Gate)
                        {
                            entry.Value = value;
                            entry.ComputedUtc = DateTime.UtcNow;
                        }
                        return value;
                    }
                    catch (Exception ex)
                    {
                        log("KMProfiles: could not summarise profile activity: " + ex.Message);
                        throw;
                    }
                    finally
                    {
                        lock (entry.Gate) entry.Refresh = null;
                    }
                });
                return entry.Value != null && age < SummaryStale ? Task.FromResult(entry.Value) : entry.Refresh;
            }
        }

        /// <summary>The usage query: covering index, in the order it groups by, once it exists.</summary>
        internal string UsageSql => $@"SELECT perm_key, SUM(count), SUM(denied), MAX(last_ts_ms) FROM daily_rollup{(indexesReady ? " INDEXED BY ix_ru_profile_perm" : "")}
                WHERE profile_id = $p AND perm_key <> '' GROUP BY perm_key;";

        /// <summary>For each key the profile has used: how often and when last (all retained history).</summary>
        public List<KeyCount> GetPermissionUsage(string profileId)
        {
            var list = new List<KeyCount>();
            if (!ready) return list;
            using var span = RequestTrace.AmbientSpan("usage");
            using var conn = Open();
            using var cmd = Command(conn, UsageSql, ("$p", profileId));
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new KeyCount { Key = r.GetString(0), Count = r.GetInt64(1), Denied = r.GetInt64(2), LastMs = r.GetInt64(3) });
            return list;
        }

        /// <summary>Latest request time per profile (for "last seen" in the directory).</summary>
        public Dictionary<string, long> GetLastSeen() => new(lastSeen, StringComparer.Ordinal);

        /// <summary>
        /// Requests and denials per profile since <paramref name="sinceMs"/>. Answered from memory
        /// for anything inside the last 24 hours (the directory's case); a longer look-back falls
        /// through to the database.
        /// </summary>
        public Dictionary<string, (long Requests, long Denied)> GetCountsSince(long sinceMs)
        {
            var map = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            if (!ready) return map;
            long windowStart = DateTimeOffset.UtcNow.Subtract(TallyWindow).ToUnixTimeMilliseconds();
            if (sinceMs < windowStart - MinuteMs) return CountsFromDatabase(sinceMs);
            foreach (var (profileId, tally) in tallies)
            {
                var (requests, denied) = tally.Since(sinceMs);
                if (requests > 0) map[profileId] = (requests, denied);
            }
            return map;
        }

        internal Dictionary<string, (long Requests, long Denied)> CountsFromDatabase(long sinceMs)
        {
            var map = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            using var conn = Open();
            using var cmd = Command(conn, @"SELECT profile_id, COUNT(*), SUM(CASE WHEN status IN (401, 403) THEN 1 ELSE 0 END)
                FROM requests INDEXED BY ix_req_ts WHERE ts_ms >= $s GROUP BY profile_id;", ("$s", sinceMs));
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            return map;
        }

        // ───────────────────────────── insights (the directory's review) ─────────────────────────────

        /// <summary>Requests per hour over the last <paramref name="hours"/> hours (oldest first), from memory.</summary>
        public long[] GetHourly(string profileId, int hours = 24, long? nowMs = null)
        {
            hours = Math.Clamp(hours, 1, 24);
            return tallies.TryGetValue(profileId, out var tally)
                ? tally.Hourly(nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hours)
                : new long[hours];
        }

        public sealed class AuthMix
        {
            public long Session { get; set; }
            public long Password { get; set; }
            public long? LastPasswordMs { get; set; }
            public long? LastSessionMs { get; set; }
        }

        /// <summary>
        /// How a profile's requests were signed since <paramref name="sinceMs"/>: with a session, or
        /// with the password itself. Read from the rollups, so it reaches back to when they started
        /// (<see cref="RollupsSinceMs"/>) until the backfill finishes, then over the whole window.
        /// </summary>
        public AuthMix GetAuthMix(string profileId, long sinceMs)
        {
            var mix = new AuthMix();
            if (!ready) return mix;
            using var conn = Open();
            using var cmd = Command(conn, "SELECT password, SUM(count), MAX(last_ts_ms) FROM activity_hourly WHERE profile_id = $p AND hour_ms >= $f GROUP BY password;",
                ("$p", profileId), ("$f", sinceMs / HourMs * HourMs));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetInt64(0) == 1) { mix.Password = r.GetInt64(1); mix.LastPasswordMs = r.GetInt64(2); }
                else { mix.Session = r.GetInt64(1); mix.LastSessionMs = r.GetInt64(2); }
            }
            return mix;
        }

        /// <summary>When the rollups (and so the credential split) began, before the backfill finishes.</summary>
        public long RollupsSinceMs => Interlocked.Read(ref rollupsSinceMs);

        /// <summary>Permission keys the profile used (successfully or not) since <paramref name="sinceMs"/>, with when last.</summary>
        public Dictionary<string, long> GetKeysUsedSince(string profileId, long sinceMs)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!ready) return map;
            using var conn = Open();
            using var cmd = Command(conn, $@"SELECT perm_key, MAX(last_ts_ms) FROM daily_rollup{(indexesReady ? " INDEXED BY ix_ru_profile_perm" : "")}
                WHERE profile_id = $p AND perm_key <> '' AND last_ts_ms >= $f GROUP BY perm_key;", ("$p", profileId), ("$f", sinceMs));
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
            return map;
        }

        /// <summary>How many of each event kind each profile has had since <paramref name="sinceMs"/> (the events table is small).</summary>
        public Dictionary<string, Dictionary<string, long>> GetEventCountsSince(long sinceMs, IReadOnlyCollection<string> kinds)
        {
            var map = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            if (!ready || kinds.Count == 0) return map;
            using var conn = Open();
            var names = kinds.Select((_, i) => "$k" + i).ToList();
            var args = new List<(string, object?)> { ("$f", sinceMs) };
            args.AddRange(kinds.Select((k, i) => ("$k" + i, (object?)k)));
            using var cmd = Command(conn, $"SELECT profile_id, kind, COUNT(*) FROM events WHERE ts_ms >= $f AND kind IN ({string.Join(", ", names)}) GROUP BY profile_id, kind;", args.ToArray());
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!map.TryGetValue(r.GetString(0), out var byKind)) map[r.GetString(0)] = byKind = new Dictionary<string, long>(StringComparer.Ordinal);
                byKind[r.GetString(1)] = r.GetInt64(2);
            }
            return map;
        }

        /// <summary>The oldest request still kept: "last seen" means nothing before it.</summary>
        public long? GetTrackingSinceMs()
        {
            if (!ready) return null;
            using var conn = Open();
            long ms = Scalar<long>(conn, "SELECT COALESCE(MIN(ts_ms), 0) FROM requests;");
            return ms > 0 ? ms : null;
        }

        // ───────────────────────────── plumbing ─────────────────────────────

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        }

        private static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
        {
            using var cmd = Command(conn, sql, args);
            cmd.ExecuteNonQuery();
        }

        private static T Scalar<T>(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
        {
            using var cmd = Command(conn, sql, args);
            object? v = cmd.ExecuteScalar();
            if (v == null || v is DBNull) return default!;
            return (T)Convert.ChangeType(v, typeof(T));
        }

        private static SqliteCommand Command(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        private static SqliteParameter[] AddParams(SqliteCommand cmd, params string[] names)
            => names.Select(n => cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value))).ToArray();

        private static void Set(SqliteParameter[] ps, params object?[] values)
        {
            for (int i = 0; i < ps.Length; i++) ps[i].Value = values[i] ?? DBNull.Value;
        }

        private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        private static string? Trim(string? s, int max) => s == null || s.Length <= max ? s : s[..max];

        private static string DayOf(long tsMs) => DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime.ToString("yyyy-MM-dd");

        public void Dispose()
        {
            try { stop.Cancel(); } catch { }
            try { queue.Writer.TryComplete(); } catch { }
            try { writer?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { seeding?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { upgrading?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        }
    }
}
