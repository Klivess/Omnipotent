using System.Text.Json;
using KliveLink.Contracts;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.KliveLink;

public sealed class KliveLinkV2AgentRecord
{
    public string AgentId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";
    public string GroupName { get; set; } = "";
    public string CollectionStatus { get; set; } = "none";
    public string HealthJson { get; set; } = "{}";
    public string TokenHash { get; set; } = "";
    public bool IsConnected { get; set; }
}

public sealed class KliveLinkV2SnapshotRecord
{
    public string SnapshotId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Tier { get; set; } = "T0";
    public string Profile { get; set; } = "t0-fast";
    public string Status { get; set; } = "complete";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string ManifestJson { get; set; } = "{}";
    [Newtonsoft.Json.JsonIgnore]
    public string BundlePath { get; set; } = "";
}

public sealed class KliveLinkV2RecordView
{
    public string RecordId { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Collector { get; set; } = "";
    public string Category { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string Sha256 { get; set; } = "";
}

public sealed class KliveLinkV2DataSummary
{
    public string AgentId { get; set; } = "";
    public int Commands { get; set; }
    public int Snapshots { get; set; }
    public int Records { get; set; }
    public long Bytes { get; set; }
    public int AuditEntries { get; set; }
}

public sealed class KliveLinkV2Store : IDisposable
{
    private readonly string connectionString;
    private readonly KliveLinkV2SecretBox secretBox;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public string DbPath { get; }
    private const string CacheKey = "klivelink.v2";

    public string CollectionDirectory { get; }

    public KliveLinkV2Store(string dbPath, string keyPath, string collectionDirectory)
    {
        DbPath = dbPath;
        CollectionDirectory = collectionDirectory;
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        Directory.CreateDirectory(collectionDirectory);
        secretBox = new KliveLinkV2SecretBox(keyPath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA secure_delete=ON;
            CREATE TABLE IF NOT EXISTS agents (
                agent_id TEXT PRIMARY KEY,
                machine_name TEXT NOT NULL DEFAULT '',
                os_version TEXT NOT NULL DEFAULT '',
                agent_version TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                connected_at TEXT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '[]',
                tags_json TEXT NOT NULL DEFAULT '[]',
                group_name TEXT NOT NULL DEFAULT '',
                collection_status TEXT NOT NULL DEFAULT 'none',
                health_json TEXT NOT NULL DEFAULT '{}',
                token_hash TEXT NOT NULL DEFAULT '',
                is_connected INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS commands (
                id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                actor TEXT NOT NULL DEFAULT '',
                operation TEXT NOT NULL,
                request_json TEXT NOT NULL DEFAULT '{}',
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                result_json TEXT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_commands_agent ON commands(agent_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                actor TEXT NOT NULL DEFAULT '',
                action TEXT NOT NULL,
                agent_id TEXT NOT NULL DEFAULT '',
                detail_json TEXT NOT NULL DEFAULT '{}',
                ip TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_audit_agent ON audit(agent_id, ts DESC);
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                tier TEXT NOT NULL,
                profile TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                bytes INTEGER NOT NULL DEFAULT 0,
                sha256 TEXT NOT NULL DEFAULT '',
                manifest_json TEXT NOT NULL DEFAULT '{}',
                bundle_path TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_snapshots_agent ON snapshots(agent_id, started_at DESC);
            CREATE TABLE IF NOT EXISTS records (
                id TEXT PRIMARY KEY,
                snapshot_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                collector TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '',
                source_path TEXT NOT NULL DEFAULT '',
                name TEXT NOT NULL DEFAULT '',
                username TEXT NULL,
                secret_cipher TEXT NULL,
                url TEXT NULL,
                expires_at TEXT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                sha256 TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_records_agent ON records(agent_id, category, name);
            CREATE INDEX IF NOT EXISTS ix_records_snapshot ON records(snapshot_id);
            CREATE TABLE IF NOT EXISTS purge_jobs (
                id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                mode TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                detail_json TEXT NOT NULL DEFAULT '{}'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public KliveLinkV2AgentRecord? GetAgent(string agentId)
    {
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agents WHERE agent_id=$id";
        cmd.Parameters.AddWithValue("$id", agentId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgent(reader) : null;
    }

    public List<KliveLinkV2AgentRecord> ListAgents()
    {
        var list = new List<KliveLinkV2AgentRecord>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agents ORDER BY is_connected DESC, machine_name COLLATE NOCASE, agent_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadAgent(reader));
        return list;
    }

    public void UpsertAgent(KliveLinkV2AgentRecord record)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agents (agent_id, machine_name, os_version, agent_version, first_seen, last_seen, connected_at,
                    capabilities_json, tags_json, group_name, collection_status, health_json, token_hash, is_connected)
                VALUES ($id, $machine, $os, $version, $first, $last, $connected, $caps, $tags, $group, $collection, $health, $token, $connectedFlag)
                ON CONFLICT(agent_id) DO UPDATE SET
                    machine_name=excluded.machine_name,
                    os_version=excluded.os_version,
                    agent_version=excluded.agent_version,
                    last_seen=excluded.last_seen,
                    connected_at=excluded.connected_at,
                    capabilities_json=excluded.capabilities_json,
                    token_hash=excluded.token_hash,
                    is_connected=excluded.is_connected
                """;
            AddAgentParameters(cmd, record);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void SetConnected(string agentId, bool connected, DateTimeOffset? when = null)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET is_connected=$connected, last_seen=$last, connected_at=CASE WHEN $connected=1 THEN COALESCE(connected_at,$last) ELSE connected_at END WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$connected", connected ? 1 : 0);
            cmd.Parameters.AddWithValue("$last", (when ?? DateTimeOffset.UtcNow).ToString("O"));
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void TouchAgent(string agentId)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET last_seen=$last, is_connected=1 WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$last", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void ResetConnections()
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET is_connected=0";
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void SetCollectionStatus(string agentId, string status)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET collection_status=$status, last_seen=$last WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$last", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void RecordCommand(string id, string agentId, string actor, string operation, string requestJson, string status, DateTimeOffset created)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO commands (id, agent_id, actor, operation, request_json, status, created_at) VALUES ($id,$agent,$actor,$op,$request,$status,$created)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$agent", agentId);
            cmd.Parameters.AddWithValue("$actor", actor ?? "");
            cmd.Parameters.AddWithValue("$op", operation);
            cmd.Parameters.AddWithValue("$request", requestJson ?? "{}");
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$created", created.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void CompleteCommand(string id, string status, string? resultJson, string? error)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE commands SET status=$status, completed_at=$completed, result_json=$result, error=$error WHERE id=$id";
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public void Audit(string actor, string action, string agentId, object? detail, string ip = "")
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO audit (ts, actor, action, agent_id, detail_json, ip) VALUES ($ts,$actor,$action,$agent,$detail,$ip)";
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$actor", actor ?? "");
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$agent", agentId ?? "");
            cmd.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(detail ?? new { }, V2Json.Options));
            cmd.Parameters.AddWithValue("$ip", ip ?? "");
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public List<object> ListAudit(string? agentId, int limit = 200)
    {
        var list = new List<object>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = agentId == null
            ? "SELECT * FROM audit ORDER BY id DESC LIMIT $limit"
            : "SELECT * FROM audit WHERE agent_id=$agent ORDER BY id DESC LIMIT $limit";
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                Id = reader.GetInt64(0),
                Timestamp = reader.GetString(1),
                Actor = reader.GetString(2),
                Action = reader.GetString(3),
                AgentId = reader.GetString(4),
                Detail = SafeJson(reader.GetString(5)),
                Ip = reader.GetString(6),
            });
        }
        return list;
    }

    public void CreateSnapshot(KliveLinkV2SnapshotRecord snapshot, IEnumerable<V2CollectionRecord> records, string bundlePath)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO snapshots (id, agent_id, tier, profile, status, started_at, completed_at, bytes, sha256, manifest_json, bundle_path)
                    VALUES ($id,$agent,$tier,$profile,$status,$started,$completed,$bytes,$sha,$manifest,$bundle)
                    """;
                cmd.Parameters.AddWithValue("$id", snapshot.SnapshotId);
                cmd.Parameters.AddWithValue("$agent", snapshot.AgentId);
                cmd.Parameters.AddWithValue("$tier", snapshot.Tier);
                cmd.Parameters.AddWithValue("$profile", snapshot.Profile);
                cmd.Parameters.AddWithValue("$status", snapshot.Status);
                cmd.Parameters.AddWithValue("$started", snapshot.StartedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$completed", (object?)snapshot.CompletedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bytes", snapshot.Bytes);
                cmd.Parameters.AddWithValue("$sha", snapshot.Sha256);
                cmd.Parameters.AddWithValue("$manifest", snapshot.ManifestJson);
                cmd.Parameters.AddWithValue("$bundle", bundlePath);
                cmd.ExecuteNonQuery();
            }

            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM records WHERE snapshot_id=$id";
                delete.Parameters.AddWithValue("$id", snapshot.SnapshotId);
                delete.ExecuteNonQuery();
            }

            int index = 0;
            foreach (V2CollectionRecord record in records)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO records (id, snapshot_id, agent_id, collector, category, source_path, name, username, secret_cipher, url, expires_at, metadata_json, sha256)
                    VALUES ($id,$snapshot,$agent,$collector,$category,$source,$name,$user,$secret,$url,$expires,$meta,$sha)
                    """;
                cmd.Parameters.AddWithValue("$id", $"{snapshot.SnapshotId}-{index++:D6}");
                cmd.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId);
                cmd.Parameters.AddWithValue("$agent", snapshot.AgentId);
                cmd.Parameters.AddWithValue("$collector", record.Collector ?? "");
                cmd.Parameters.AddWithValue("$category", record.Category ?? "");
                cmd.Parameters.AddWithValue("$source", record.SourcePath ?? "");
                cmd.Parameters.AddWithValue("$name", record.Name ?? "");
                cmd.Parameters.AddWithValue("$user", (object?)record.Username ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$secret", string.IsNullOrEmpty(record.Secret) ? DBNull.Value : secretBox.Protect(record.Secret));
                cmd.Parameters.AddWithValue("$url", (object?)record.Url ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$expires", record.ExpiresAt.HasValue ? record.ExpiresAt.Value.ToString("O") : DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", record.Metadata == null ? "{}" : JsonSerializer.Serialize(record.Metadata, V2Json.Options));
                cmd.Parameters.AddWithValue("$sha", record.Sha256 ?? "");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
        SetCollectionStatus(snapshot.AgentId, snapshot.Tier);
    }

    public List<KliveLinkV2SnapshotRecord> ListSnapshots(string? agentId, int limit = 100)
    {
        var list = new List<KliveLinkV2SnapshotRecord>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = agentId == null
            ? "SELECT * FROM snapshots ORDER BY started_at DESC LIMIT $limit"
            : "SELECT * FROM snapshots WHERE agent_id=$agent ORDER BY started_at DESC LIMIT $limit";
        if (agentId != null) cmd.Parameters.AddWithValue("$agent", agentId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new KliveLinkV2SnapshotRecord
            {
                SnapshotId = reader.GetString(0),
                AgentId = reader.GetString(1),
                Tier = reader.GetString(2),
                Profile = reader.GetString(3),
                Status = reader.GetString(4),
                StartedAt = DateTimeOffset.Parse(reader.GetString(5)),
                CompletedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                Bytes = reader.GetInt64(7),
                Sha256 = reader.GetString(8),
                ManifestJson = reader.GetString(9),
                BundlePath = reader.GetString(10),
            });
        }
        return list;
    }

    public List<KliveLinkV2RecordView> SearchRecords(string? agentId, string? category, string? query, int limit = 500)
    {
        var list = new List<KliveLinkV2RecordView>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(agentId)) { where.Add("agent_id=$agent"); cmd.Parameters.AddWithValue("$agent", agentId); }
        if (!string.IsNullOrWhiteSpace(category)) { where.Add("category=$category"); cmd.Parameters.AddWithValue("$category", category); }
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(name LIKE $q OR source_path LIKE $q OR username LIKE $q OR url LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        }
        cmd.CommandText = $"SELECT * FROM records {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY name COLLATE NOCASE LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new KliveLinkV2RecordView
            {
                RecordId = reader.GetString(0),
                SnapshotId = reader.GetString(1),
                AgentId = reader.GetString(2),
                Collector = reader.GetString(3),
                Category = reader.GetString(4),
                SourcePath = reader.GetString(5),
                Name = reader.GetString(6),
                Username = reader.IsDBNull(7) ? null : reader.GetString(7),
                Secret = null,
                Url = reader.IsDBNull(9) ? null : reader.GetString(9),
                ExpiresAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                MetadataJson = reader.GetString(11),
                Sha256 = reader.GetString(12),
            });
        }
        return list;
    }

    public KliveLinkV2RecordView? GetRecord(string recordId, bool revealSecret)
    {
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM records WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", recordId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new KliveLinkV2RecordView
        {
            RecordId = reader.GetString(0),
            SnapshotId = reader.GetString(1),
            AgentId = reader.GetString(2),
            Collector = reader.GetString(3),
            Category = reader.GetString(4),
            SourcePath = reader.GetString(5),
            Name = reader.GetString(6),
            Username = reader.IsDBNull(7) ? null : reader.GetString(7),
            Secret = revealSecret && !reader.IsDBNull(8) ? secretBox.Unprotect(reader.GetString(8)) : null,
            Url = reader.IsDBNull(9) ? null : reader.GetString(9),
            ExpiresAt = reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
            MetadataJson = reader.GetString(11),
            Sha256 = reader.GetString(12),
        };
    }

    public string EncryptBundle(string path)
    {
        try
        {
            if (!File.Exists(path)) return path;
            byte[] data = File.ReadAllBytes(path);
            byte[] encrypted = secretBox.ProtectBytes(data);
            string encryptedPath = path + ".enc";
            File.WriteAllBytes(encryptedPath, encrypted);
            try { File.Delete(path); } catch { }
            return encryptedPath;
        }
        catch
        {
            return path;
        }
    }

    public byte[] ReadBundle(string path)
    {
        if (!File.Exists(path)) return Array.Empty<byte>();
        byte[] data = File.ReadAllBytes(path);
        return path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
            ? secretBox.UnprotectBytes(data)
            : data;
    }

    public KliveLinkV2DataSummary GetDataSummary(string agentId)    {
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM commands WHERE agent_id=$id),
              (SELECT COUNT(*) FROM snapshots WHERE agent_id=$id),
              (SELECT COUNT(*) FROM records WHERE agent_id=$id),
              (SELECT COALESCE(SUM(bytes),0) FROM snapshots WHERE agent_id=$id),
              (SELECT COUNT(*) FROM audit WHERE agent_id=$id)
            """;
        cmd.Parameters.AddWithValue("$id", agentId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new KliveLinkV2DataSummary
        {
            AgentId = agentId,
            Commands = reader.GetInt32(0),
            Snapshots = reader.GetInt32(1),
            Records = reader.GetInt32(2),
            Bytes = reader.GetInt64(3),
            AuditEntries = reader.GetInt32(4),
        };
    }

    public V2PurgeJob CreatePurgeJob(string agentId, string mode, string detailJson)
    {
        var job = new V2PurgeJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            State = "queued",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DetailJson = detailJson,
        };
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO purge_jobs (id, agent_id, mode, state, created_at, updated_at, detail_json) VALUES ($id,$agent,$mode,$state,$created,$updated,$detail)";
            cmd.Parameters.AddWithValue("$id", job.JobId);
            cmd.Parameters.AddWithValue("$agent", job.AgentId);
            cmd.Parameters.AddWithValue("$mode", mode);
            cmd.Parameters.AddWithValue("$state", job.State);
            cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$detail", job.DetailJson);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
        return job;
    }

    public void UpdatePurgeJob(string jobId, string state, string detailJson)
    {
        writeLock.Wait();
        try
        {
            using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE purge_jobs SET state=$state, updated_at=$updated, detail_json=$detail WHERE id=$id";
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$detail", detailJson);
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
        finally { writeLock.Release(); CacheDeps.Bump(CacheKey); }
    }

    public V2PurgeJob? GetPurgeJob(string jobId)
    {
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM purge_jobs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", jobId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new V2PurgeJob
        {
            JobId = reader.GetString(0),
            AgentId = reader.GetString(1),
            State = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            DetailJson = reader.GetString(6),
        };
    }

    public (KliveLinkV2DataSummary Summary, List<string> BundlePaths) PurgeAgent(string agentId, bool keepAudit)
    {
        var summary = GetDataSummary(agentId);
        var bundles = new List<string>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT bundle_path FROM snapshots WHERE agent_id=$id AND bundle_path <> ''";
            cmd.Parameters.AddWithValue("$id", agentId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) bundles.Add(reader.GetString(0));
        }
        foreach (string table in new[] { "records", "snapshots", "commands" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        if (!keepAudit)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM audit WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM agents WHERE agent_id=$id";
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return (summary, bundles);
    }

    public (int Snapshots, int Records, List<string> BundlePaths) OrphanScan()
    {
        var bundles = new List<string>();
        using var conn = Open();
        CacheDeps.NoteRead(CacheKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT s.id, s.bundle_path FROM snapshots s LEFT JOIN agents a ON a.agent_id=s.agent_id WHERE a.agent_id IS NULL";
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
            if (!reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1))) bundles.Add(reader.GetString(1));
        }
        int records = 0;
        foreach (string id in ids)
        {
            using var rec = conn.CreateCommand();
            rec.CommandText = "DELETE FROM records WHERE snapshot_id=$id";
            rec.Parameters.AddWithValue("$id", id);
            records += rec.ExecuteNonQuery();
            using var snap = conn.CreateCommand();
            snap.CommandText = "DELETE FROM snapshots WHERE id=$id";
            snap.Parameters.AddWithValue("$id", id);
            snap.ExecuteNonQuery();
        }
        return (ids.Count, records, bundles);
    }

    private static void AddAgentParameters(SqliteCommand cmd, KliveLinkV2AgentRecord record)
    {
        cmd.Parameters.AddWithValue("$id", record.AgentId);
        cmd.Parameters.AddWithValue("$machine", record.MachineName ?? "");
        cmd.Parameters.AddWithValue("$os", record.OsVersion ?? "");
        cmd.Parameters.AddWithValue("$version", record.AgentVersion ?? "");
        cmd.Parameters.AddWithValue("$first", record.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("$last", record.LastSeen.ToString("O"));
        cmd.Parameters.AddWithValue("$connected", record.ConnectedAt.HasValue ? record.ConnectedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$caps", record.CapabilitiesJson ?? "[]");
        cmd.Parameters.AddWithValue("$tags", record.TagsJson ?? "[]");
        cmd.Parameters.AddWithValue("$group", record.GroupName ?? "");
        cmd.Parameters.AddWithValue("$collection", record.CollectionStatus ?? "none");
        cmd.Parameters.AddWithValue("$health", record.HealthJson ?? "{}");
        cmd.Parameters.AddWithValue("$token", record.TokenHash ?? "");
        cmd.Parameters.AddWithValue("$connectedFlag", record.IsConnected ? 1 : 0);
    }

    private static KliveLinkV2AgentRecord ReadAgent(SqliteDataReader r) => new()
    {
        AgentId = r.GetString(0),
        MachineName = r.GetString(1),
        OsVersion = r.GetString(2),
        AgentVersion = r.GetString(3),
        FirstSeen = DateTimeOffset.Parse(r.GetString(4)),
        LastSeen = DateTimeOffset.Parse(r.GetString(5)),
        ConnectedAt = r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6)),
        CapabilitiesJson = r.GetString(7),
        TagsJson = r.GetString(8),
        GroupName = r.GetString(9),
        CollectionStatus = r.GetString(10),
        HealthJson = r.GetString(11),
        TokenHash = r.GetString(12),
        IsConnected = r.GetInt32(13) != 0,
    };

    private static object SafeJson(string json)
    {
        try { return JsonSerializer.Deserialize<object>(json) ?? new { }; }
        catch { return json; }
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}


