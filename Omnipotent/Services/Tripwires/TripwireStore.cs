using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Tripwires
{
    public sealed class TripwireStore : IDisposable
    {
        private readonly string connectionString;
        private readonly SemaphoreSlim writeLock = new(1, 1);

        public string DbPath { get; }

        public TripwireStore(string? dbPath = null)
        {
            DbPath = dbPath ?? OmniPaths.GetPath(OmniPaths.GlobalPaths.TripwireDbFile);
            string? directory = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString();
        }

        public async Task InitialiseAsync(CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;", ct);
            await ExecuteAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
            await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct);
            await ExecuteAsync(conn, @"
CREATE TABLE IF NOT EXISTS tripwires (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    created_by TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    last_tripped_utc TEXT NULL,
    last_discord_utc TEXT NULL,
    total_trips INTEGER NOT NULL DEFAULT 0,
    settings_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tripwire_targets (
    id TEXT PRIMARY KEY,
    tripwire_id TEXT NOT NULL REFERENCES tripwires(id) ON DELETE CASCADE,
    token TEXT NOT NULL UNIQUE,
    label TEXT NOT NULL,
    destination_url TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    archived INTEGER NOT NULL DEFAULT 0,
    sort_order INTEGER NOT NULL DEFAULT 0,
    trip_count INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tripwire_events (
    id TEXT PRIMARY KEY,
    tripwire_id TEXT NOT NULL REFERENCES tripwires(id) ON DELETE CASCADE,
    target_id TEXT NOT NULL,
    tripped_utc TEXT NOT NULL,
    ip_address TEXT NULL,
    visitor_hash TEXT NULL,
    country_code TEXT NULL,
    country TEXT NULL,
    region TEXT NULL,
    city TEXT NULL,
    latitude REAL NULL,
    longitude REAL NULL,
    timezone TEXT NULL,
    user_agent TEXT NULL,
    browser TEXT NULL,
    operating_system TEXT NULL,
    device_type TEXT NULL,
    referrer TEXT NULL,
    language TEXT NULL,
    query_parameters_json TEXT NULL,
    do_not_track INTEGER NOT NULL DEFAULT 0,
    is_bot INTEGER NOT NULL DEFAULT 0,
    is_unique INTEGER NOT NULL DEFAULT 1,
    discord_notified INTEGER NOT NULL DEFAULT 0,
    notification_error TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_tripwire_targets_tripwire ON tripwire_targets(tripwire_id, sort_order);
CREATE UNIQUE INDEX IF NOT EXISTS idx_tripwire_targets_token ON tripwire_targets(token);
CREATE INDEX IF NOT EXISTS idx_tripwire_events_tripwire_time ON tripwire_events(tripwire_id, tripped_utc DESC);
CREATE INDEX IF NOT EXISTS idx_tripwire_events_target_time ON tripwire_events(target_id, tripped_utc DESC);
CREATE INDEX IF NOT EXISTS idx_tripwire_events_visitor_time ON tripwire_events(tripwire_id, visitor_hash, tripped_utc DESC);
", ct);
            if (!await ColumnExistsAsync(conn, "tripwire_targets", "archived", ct))
                await ExecuteAsync(conn, "ALTER TABLE tripwire_targets ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;", ct);
        }

        public async Task<List<TripwireRecord>> ListAsync(CancellationToken ct = default)
        {
            var items = new List<TripwireRecord>();
            await using var conn = await OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, name, created_by, created_utc, updated_utc,
                    last_tripped_utc, last_discord_utc, total_trips, settings_json
                    FROM tripwires ORDER BY created_utc DESC";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadTripwire(reader));
            }
            await LoadTargetsAsync(conn, items, ct);
            return items;
        }

        public async Task<TripwireRecord?> GetAsync(string id, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            TripwireRecord? item = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, name, created_by, created_utc, updated_utc,
                    last_tripped_utc, last_discord_utc, total_trips, settings_json
                    FROM tripwires WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) item = ReadTripwire(reader);
            }
            if (item == null) return null;
            await LoadTargetsAsync(conn, new List<TripwireRecord> { item }, ct);
            return item;
        }

        public async Task<(TripwireRecord Tripwire, TripwireTarget Target)?> ResolveTokenAsync(string token, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            TripwireRecord? tripwire = null;
            TripwireTarget? target = null;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT w.id, w.name, w.created_by, w.created_utc, w.updated_utc,
                w.last_tripped_utc, w.last_discord_utc, w.total_trips, w.settings_json,
                t.id, t.tripwire_id, t.token, t.label, t.destination_url, t.enabled,
                t.sort_order, t.trip_count, t.created_utc
                FROM tripwire_targets t JOIN tripwires w ON w.id=t.tripwire_id
                WHERE t.token=$token AND t.archived=0 LIMIT 1";
            cmd.Parameters.AddWithValue("$token", token);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                tripwire = ReadTripwire(reader);
                target = ReadTarget(reader, 9);
                tripwire.Targets.Add(target);
            }
            return tripwire == null || target == null ? null : (tripwire, target);
        }

        public async Task<TripwireRecord> CreateAsync(
            string name,
            string createdBy,
            TripwireSettings settings,
            IReadOnlyList<TripwireTarget> targets,
            CancellationToken ct = default)
        {
            settings.Normalize();
            var now = DateTimeOffset.UtcNow;
            var tripwire = new TripwireRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CreatedBy = createdBy,
                CreatedUtc = now,
                UpdatedUtc = now,
                Settings = settings,
            };

            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO tripwires
                        (id,name,created_by,created_utc,updated_utc,total_trips,settings_json)
                        VALUES($id,$name,$by,$created,$updated,0,$settings)";
                    cmd.Parameters.AddWithValue("$id", tripwire.Id);
                    cmd.Parameters.AddWithValue("$name", tripwire.Name);
                    cmd.Parameters.AddWithValue("$by", tripwire.CreatedBy);
                    cmd.Parameters.AddWithValue("$created", Iso(now));
                    cmd.Parameters.AddWithValue("$updated", Iso(now));
                    cmd.Parameters.AddWithValue("$settings", JsonConvert.SerializeObject(settings));
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    target.Id = Guid.NewGuid().ToString("N");
                    target.TripwireId = tripwire.Id;
                    target.Token = NewToken();
                    target.SortOrder = i;
                    target.CreatedUtc = now;
                    target.TripCount = 0;
                    await InsertTargetAsync(conn, tx, target, ct);
                    tripwire.Targets.Add(target);
                }

                await tx.CommitAsync(ct);
                return tripwire;
            }
            finally { writeLock.Release(); }
        }

        public async Task<TripwireRecord?> UpdateAsync(
            string id,
            string name,
            TripwireSettings settings,
            IReadOnlyList<TripwireTarget>? requestedTargets,
            CancellationToken ct = default)
        {
            settings.Normalize();
            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE tripwires SET name=$name, settings_json=$settings,
                        updated_utc=$updated WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$settings", JsonConvert.SerializeObject(settings));
                    cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
                    if (await cmd.ExecuteNonQueryAsync(ct) == 0) return null;
                }

                if (requestedTargets != null)
                {
                    var retainedIds = new List<string>();
                    for (int i = 0; i < requestedTargets.Count; i++)
                    {
                        var target = requestedTargets[i];
                        if (!string.IsNullOrWhiteSpace(target.Id))
                        {
                            await using var update = conn.CreateCommand();
                            update.Transaction = tx;
                            update.CommandText = @"UPDATE tripwire_targets SET label=$label,
                                destination_url=$url, enabled=$enabled, archived=0, sort_order=$sort
                                WHERE id=$target AND tripwire_id=$tripwire";
                            update.Parameters.AddWithValue("$label", target.Label);
                            update.Parameters.AddWithValue("$url", target.DestinationUrl);
                            update.Parameters.AddWithValue("$enabled", target.Enabled ? 1 : 0);
                            update.Parameters.AddWithValue("$sort", i);
                            update.Parameters.AddWithValue("$target", target.Id);
                            update.Parameters.AddWithValue("$tripwire", id);
                            if (await update.ExecuteNonQueryAsync(ct) > 0) retainedIds.Add(target.Id);
                        }
                        else
                        {
                            target.Id = Guid.NewGuid().ToString("N");
                            target.TripwireId = id;
                            target.Token = NewToken();
                            target.SortOrder = i;
                            target.CreatedUtc = DateTimeOffset.UtcNow;
                            await InsertTargetAsync(conn, tx, target, ct);
                            retainedIds.Add(target.Id);
                        }
                    }

                    await using var disable = conn.CreateCommand();
                    disable.Transaction = tx;
                    if (retainedIds.Count == 0)
                    {
                        disable.CommandText = "UPDATE tripwire_targets SET enabled=0,archived=1 WHERE tripwire_id=$id";
                    }
                    else
                    {
                        var placeholders = retainedIds.Select((_, i) => "$keep" + i).ToList();
                        disable.CommandText = $"UPDATE tripwire_targets SET enabled=0,archived=1 WHERE tripwire_id=$id AND id NOT IN ({string.Join(',', placeholders)})";
                        for (int i = 0; i < retainedIds.Count; i++) disable.Parameters.AddWithValue("$keep" + i, retainedIds[i]);
                    }
                    disable.Parameters.AddWithValue("$id", id);
                    await disable.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            finally { writeLock.Release(); }
            return await GetAsync(id, ct);
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            return await WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM tripwires WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                return await cmd.ExecuteNonQueryAsync(ct) > 0;
            }, ct);
        }

        public async Task<bool> ClearEventsAsync(string id, CancellationToken ct = default)
        {
            return await WithWriteLockAsync(async conn =>
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                await using (var delete = conn.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM tripwire_events WHERE tripwire_id=$id";
                    delete.Parameters.AddWithValue("$id", id);
                    await delete.ExecuteNonQueryAsync(ct);
                }
                await using (var resetTargets = conn.CreateCommand())
                {
                    resetTargets.Transaction = tx;
                    resetTargets.CommandText = "UPDATE tripwire_targets SET trip_count=0 WHERE tripwire_id=$id";
                    resetTargets.Parameters.AddWithValue("$id", id);
                    await resetTargets.ExecuteNonQueryAsync(ct);
                }
                await using var reset = conn.CreateCommand();
                reset.Transaction = tx;
                reset.CommandText = "UPDATE tripwires SET total_trips=0,last_tripped_utc=NULL WHERE id=$id";
                reset.Parameters.AddWithValue("$id", id);
                bool found = await reset.ExecuteNonQueryAsync(ct) > 0;
                await tx.CommitAsync(ct);
                return found;
            }, ct);
        }

        public async Task<TripwireRecordResult> RecordEventAsync(
            TripwireRecord tripwire,
            TripwireTarget target,
            TripwireEvent item,
            int deduplicateWindowMinutes,
            CancellationToken ct = default)
        {
            return await WithWriteLockAsync(async conn =>
            {
                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

                long totalTrips;
                await using (var current = conn.CreateCommand())
                {
                    current.Transaction = tx;
                    current.CommandText = "SELECT total_trips FROM tripwires WHERE id=$id";
                    current.Parameters.AddWithValue("$id", tripwire.Id);
                    object? value = await current.ExecuteScalarAsync(ct);
                    if (value == null || value == DBNull.Value) return new TripwireRecordResult();
                    totalTrips = Convert.ToInt64(value);
                }

                if (tripwire.Settings.MaxTrips.HasValue && totalTrips >= tripwire.Settings.MaxTrips.Value)
                    return new TripwireRecordResult { LimitReached = true };

                item.IsUnique = true;
                if (deduplicateWindowMinutes > 0 && !string.IsNullOrWhiteSpace(item.VisitorHash))
                {
                    await using var unique = conn.CreateCommand();
                    unique.Transaction = tx;
                    unique.CommandText = @"SELECT 1 FROM tripwire_events
                        WHERE tripwire_id=$tripwire AND visitor_hash=$visitor
                        AND tripped_utc >= $since LIMIT 1";
                    unique.Parameters.AddWithValue("$tripwire", tripwire.Id);
                    unique.Parameters.AddWithValue("$visitor", item.VisitorHash);
                    unique.Parameters.AddWithValue("$since", Iso(item.TrippedUtc.AddMinutes(-deduplicateWindowMinutes)));
                    item.IsUnique = await unique.ExecuteScalarAsync(ct) == null;
                }

                await using (var insert = conn.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = @"INSERT INTO tripwire_events
                        (id,tripwire_id,target_id,tripped_utc,ip_address,visitor_hash,country_code,country,
                         region,city,latitude,longitude,timezone,user_agent,browser,operating_system,device_type,
                         referrer,language,query_parameters_json,do_not_track,is_bot,is_unique,discord_notified,notification_error)
                        VALUES($id,$tripwire,$target,$time,$ip,$visitor,$countryCode,$country,$region,$city,$lat,$lon,
                         $timezone,$ua,$browser,$os,$device,$referrer,$language,$query,$dnt,$bot,$unique,0,NULL)";
                    AddEventParameters(insert, item);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await using (var updateTarget = conn.CreateCommand())
                {
                    updateTarget.Transaction = tx;
                    updateTarget.CommandText = "UPDATE tripwire_targets SET trip_count=trip_count+1 WHERE id=$id";
                    updateTarget.Parameters.AddWithValue("$id", target.Id);
                    await updateTarget.ExecuteNonQueryAsync(ct);
                }
                await using (var updateTripwire = conn.CreateCommand())
                {
                    updateTripwire.Transaction = tx;
                    updateTripwire.CommandText = @"UPDATE tripwires SET total_trips=total_trips+1,
                        last_tripped_utc=$time WHERE id=$id";
                    updateTripwire.Parameters.AddWithValue("$id", tripwire.Id);
                    updateTripwire.Parameters.AddWithValue("$time", Iso(item.TrippedUtc));
                    await updateTripwire.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                return new TripwireRecordResult { Accepted = true, Event = item };
            }, ct);
        }

        public async Task UpdateGeoAsync(string eventId, TripwireGeoResult geo, CancellationToken ct = default)
        {
            await WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE tripwire_events SET country_code=$code,country=$country,
                    region=$region,city=$city,latitude=$lat,longitude=$lon,timezone=$timezone WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", eventId);
                cmd.Parameters.AddWithValue("$code", Db(geo.CountryCode));
                cmd.Parameters.AddWithValue("$country", Db(geo.Country));
                cmd.Parameters.AddWithValue("$region", Db(geo.Region));
                cmd.Parameters.AddWithValue("$city", Db(geo.City));
                cmd.Parameters.AddWithValue("$lat", Db(geo.Latitude));
                cmd.Parameters.AddWithValue("$lon", Db(geo.Longitude));
                cmd.Parameters.AddWithValue("$timezone", Db(geo.Timezone));
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
        }

        public async Task<bool> TryClaimDiscordNotificationAsync(string tripwireId, int cooldownSeconds, DateTimeOffset now, CancellationToken ct = default)
        {
            return await WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE tripwires SET last_discord_utc=$now
                    WHERE id=$id AND (last_discord_utc IS NULL OR last_discord_utc <= $cutoff)";
                cmd.Parameters.AddWithValue("$id", tripwireId);
                cmd.Parameters.AddWithValue("$now", Iso(now));
                cmd.Parameters.AddWithValue("$cutoff", Iso(now.AddSeconds(-Math.Max(0, cooldownSeconds))));
                return await cmd.ExecuteNonQueryAsync(ct) > 0;
            }, ct);
        }

        public async Task MarkNotificationAsync(string eventId, bool sent, string? error, CancellationToken ct = default)
        {
            await WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE tripwire_events SET discord_notified=$sent,
                    notification_error=$error WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", eventId);
                cmd.Parameters.AddWithValue("$sent", sent ? 1 : 0);
                cmd.Parameters.AddWithValue("$error", Db(error));
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
        }

        public async Task<TripwireEventPage> GetEventsAsync(
            string tripwireId,
            string? targetId,
            int limit,
            int offset,
            CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 500);
            offset = Math.Max(0, offset);
            await using var conn = await OpenAsync(ct);
            var page = new TripwireEventPage { Limit = limit, Offset = offset };
            await using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM tripwire_events WHERE tripwire_id=$id"
                    + (string.IsNullOrWhiteSpace(targetId) ? "" : " AND target_id=$target");
                count.Parameters.AddWithValue("$id", tripwireId);
                if (!string.IsNullOrWhiteSpace(targetId)) count.Parameters.AddWithValue("$target", targetId);
                page.Total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT e.id,e.tripwire_id,e.target_id,t.label,t.destination_url,e.tripped_utc,
                    e.ip_address,e.visitor_hash,e.country_code,e.country,e.region,e.city,e.latitude,e.longitude,
                    e.timezone,e.user_agent,e.browser,e.operating_system,e.device_type,e.referrer,e.language,
                    e.query_parameters_json,e.do_not_track,e.is_bot,e.is_unique,e.discord_notified,e.notification_error
                    FROM tripwire_events e LEFT JOIN tripwire_targets t ON t.id=e.target_id
                    WHERE e.tripwire_id=$id" + (string.IsNullOrWhiteSpace(targetId) ? "" : " AND e.target_id=$target")
                    + " ORDER BY e.tripped_utc DESC LIMIT $limit OFFSET $offset";
                cmd.Parameters.AddWithValue("$id", tripwireId);
                if (!string.IsNullOrWhiteSpace(targetId)) cmd.Parameters.AddWithValue("$target", targetId);
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", offset);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) page.Items.Add(ReadEvent(reader));
            }
            return page;
        }

        public async Task<TripwireSummary> GetSummaryAsync(string tripwireId, CancellationToken ct = default)
        {
            await using var conn = await OpenAsync(ct);
            var result = new TripwireSummary();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT w.total_trips, COALESCE(SUM(e.is_unique),0),
                    COALESCE(SUM(CASE WHEN e.tripped_utc >= $day THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN e.tripped_utc >= $week THEN 1 ELSE 0 END),0),
                    w.last_tripped_utc FROM tripwires w LEFT JOIN tripwire_events e ON e.tripwire_id=w.id
                    WHERE w.id=$id GROUP BY w.id";
                cmd.Parameters.AddWithValue("$id", tripwireId);
                cmd.Parameters.AddWithValue("$day", Iso(DateTimeOffset.UtcNow.AddHours(-24)));
                cmd.Parameters.AddWithValue("$week", Iso(DateTimeOffset.UtcNow.AddDays(-7)));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    result.TotalTrips = reader.GetInt64(0);
                    result.UniqueTrips = reader.GetInt64(1);
                    result.TripsLast24Hours = reader.GetInt64(2);
                    result.TripsLast7Days = reader.GetInt64(3);
                    result.LastTrippedUtc = NullableDate(reader, 4);
                }
            }
            await using (var daily = conn.CreateCommand())
            {
                daily.CommandText = @"SELECT substr(tripped_utc,1,10),COUNT(*) FROM tripwire_events
                    WHERE tripwire_id=$id AND tripped_utc >= $since
                    GROUP BY substr(tripped_utc,1,10) ORDER BY 1";
                daily.Parameters.AddWithValue("$id", tripwireId);
                daily.Parameters.AddWithValue("$since", Iso(DateTimeOffset.UtcNow.AddDays(-30)));
                await using var reader = await daily.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) result.Daily.Add(new TripwireDailyCount { Date = reader.GetString(0), Count = reader.GetInt64(1) });
            }
            result.Countries = await ReadValueCountsAsync(conn, tripwireId, "COALESCE(NULLIF(country,''),NULLIF(country_code,''),'Unknown')", ct);
            result.Devices = await ReadValueCountsAsync(conn, tripwireId, "COALESCE(NULLIF(device_type,''),'Unknown')", ct);
            return result;
        }

        public async Task CleanupExpiredEventsAsync(CancellationToken ct = default)
        {
            var tripwires = await ListAsync(ct);
            foreach (var tripwire in tripwires)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-tripwire.Settings.RetentionDays);
                await WithWriteLockAsync(async conn =>
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM tripwire_events WHERE tripwire_id=$id AND tripped_utc < $cutoff";
                    cmd.Parameters.AddWithValue("$id", tripwire.Id);
                    cmd.Parameters.AddWithValue("$cutoff", Iso(cutoff));
                    await cmd.ExecuteNonQueryAsync(ct);
                }, ct);
            }
        }

        private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
        {
            var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct);
            return conn;
        }

        private async Task<T> WithWriteLockAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                return await action(conn);
            }
            finally { writeLock.Release(); }
        }

        private async Task WithWriteLockAsync(Func<SqliteConnection, Task> action, CancellationToken ct)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await using var conn = await OpenAsync(ct);
                await action(conn);
            }
            finally { writeLock.Release(); }
        }

        private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static async Task InsertTargetAsync(SqliteConnection conn, SqliteTransaction tx, TripwireTarget target, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO tripwire_targets
                (id,tripwire_id,token,label,destination_url,enabled,archived,sort_order,trip_count,created_utc)
                VALUES($id,$tripwire,$token,$label,$url,$enabled,0,$sort,0,$created)";
            cmd.Parameters.AddWithValue("$id", target.Id);
            cmd.Parameters.AddWithValue("$tripwire", target.TripwireId);
            cmd.Parameters.AddWithValue("$token", target.Token);
            cmd.Parameters.AddWithValue("$label", target.Label);
            cmd.Parameters.AddWithValue("$url", target.DestinationUrl);
            cmd.Parameters.AddWithValue("$enabled", target.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$sort", target.SortOrder);
            cmd.Parameters.AddWithValue("$created", Iso(target.CreatedUtc));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task LoadTargetsAsync(SqliteConnection conn, List<TripwireRecord> items, CancellationToken ct)
        {
            if (items.Count == 0) return;
            var byId = items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var placeholders = items.Select((_, i) => "$id" + i).ToList();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT id,tripwire_id,token,label,destination_url,enabled,sort_order,trip_count,created_utc
                FROM tripwire_targets WHERE archived=0 AND tripwire_id IN ({string.Join(',', placeholders)}) ORDER BY sort_order";
            for (int i = 0; i < items.Count; i++) cmd.Parameters.AddWithValue("$id" + i, items[i].Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var target = ReadTarget(reader);
                if (byId.TryGetValue(target.TripwireId, out var owner)) owner.Targets.Add(target);
            }
        }

        private static TripwireRecord ReadTripwire(SqliteDataReader reader, int offset = 0)
        {
            TripwireSettings settings;
            try { settings = JsonConvert.DeserializeObject<TripwireSettings>(reader.GetString(offset + 8)) ?? new(); }
            catch { settings = new(); }
            settings.Normalize();
            return new TripwireRecord
            {
                Id = reader.GetString(offset),
                Name = reader.GetString(offset + 1),
                CreatedBy = reader.GetString(offset + 2),
                CreatedUtc = Date(reader.GetString(offset + 3)),
                UpdatedUtc = Date(reader.GetString(offset + 4)),
                LastTrippedUtc = NullableDate(reader, offset + 5),
                LastDiscordUtc = NullableDate(reader, offset + 6),
                TotalTrips = reader.GetInt64(offset + 7),
                Settings = settings,
            };
        }

        private static TripwireTarget ReadTarget(SqliteDataReader reader, int offset = 0) => new()
        {
            Id = reader.GetString(offset),
            TripwireId = reader.GetString(offset + 1),
            Token = reader.GetString(offset + 2),
            Label = reader.GetString(offset + 3),
            DestinationUrl = reader.GetString(offset + 4),
            Enabled = reader.GetInt64(offset + 5) != 0,
            SortOrder = reader.GetInt32(offset + 6),
            TripCount = reader.GetInt64(offset + 7),
            CreatedUtc = Date(reader.GetString(offset + 8)),
        };

        private static TripwireEvent ReadEvent(SqliteDataReader r) => new()
        {
            Id = r.GetString(0), TripwireId = r.GetString(1), TargetId = r.GetString(2),
            TargetLabel = Text(r, 3) ?? "", DestinationUrl = Text(r, 4) ?? "", TrippedUtc = Date(r.GetString(5)),
            IpAddress = Text(r, 6), VisitorHash = Text(r, 7), CountryCode = Text(r, 8), Country = Text(r, 9),
            Region = Text(r, 10), City = Text(r, 11), Latitude = Number(r, 12), Longitude = Number(r, 13),
            Timezone = Text(r, 14), UserAgent = Text(r, 15), Browser = Text(r, 16), OperatingSystem = Text(r, 17),
            DeviceType = Text(r, 18), Referrer = Text(r, 19), Language = Text(r, 20), QueryParametersJson = Text(r, 21),
            DoNotTrack = r.GetInt64(22) != 0, IsBot = r.GetInt64(23) != 0, IsUnique = r.GetInt64(24) != 0,
            DiscordNotified = r.GetInt64(25) != 0, NotificationError = Text(r, 26),
        };

        private static void AddEventParameters(SqliteCommand cmd, TripwireEvent item)
        {
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$tripwire", item.TripwireId);
            cmd.Parameters.AddWithValue("$target", item.TargetId);
            cmd.Parameters.AddWithValue("$time", Iso(item.TrippedUtc));
            cmd.Parameters.AddWithValue("$ip", Db(item.IpAddress));
            cmd.Parameters.AddWithValue("$visitor", Db(item.VisitorHash));
            cmd.Parameters.AddWithValue("$countryCode", Db(item.CountryCode));
            cmd.Parameters.AddWithValue("$country", Db(item.Country));
            cmd.Parameters.AddWithValue("$region", Db(item.Region));
            cmd.Parameters.AddWithValue("$city", Db(item.City));
            cmd.Parameters.AddWithValue("$lat", Db(item.Latitude));
            cmd.Parameters.AddWithValue("$lon", Db(item.Longitude));
            cmd.Parameters.AddWithValue("$timezone", Db(item.Timezone));
            cmd.Parameters.AddWithValue("$ua", Db(item.UserAgent));
            cmd.Parameters.AddWithValue("$browser", Db(item.Browser));
            cmd.Parameters.AddWithValue("$os", Db(item.OperatingSystem));
            cmd.Parameters.AddWithValue("$device", Db(item.DeviceType));
            cmd.Parameters.AddWithValue("$referrer", Db(item.Referrer));
            cmd.Parameters.AddWithValue("$language", Db(item.Language));
            cmd.Parameters.AddWithValue("$query", Db(item.QueryParametersJson));
            cmd.Parameters.AddWithValue("$dnt", item.DoNotTrack ? 1 : 0);
            cmd.Parameters.AddWithValue("$bot", item.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("$unique", item.IsUnique ? 1 : 0);
        }

        private static async Task<List<TripwireValueCount>> ReadValueCountsAsync(
            SqliteConnection conn, string tripwireId, string expression, CancellationToken ct)
        {
            var values = new List<TripwireValueCount>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT {expression} value, COUNT(*) count FROM tripwire_events
                WHERE tripwire_id=$id GROUP BY value ORDER BY count DESC LIMIT 8";
            cmd.Parameters.AddWithValue("$id", tripwireId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) values.Add(new TripwireValueCount { Value = reader.GetString(0), Count = reader.GetInt64(1) });
            return values;
        }

        private static string NewToken()
        {
            Span<byte> bytes = stackalloc byte[18];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
        private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value).ToUniversalTime();
        private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? null : Date(reader.GetString(ordinal));
        private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        private static double? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
        private static object Db(object? value) => value ?? DBNull.Value;

        public void Dispose()
        {
            writeLock.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }
}
