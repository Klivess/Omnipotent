using Microsoft.Data.Sqlite;
using Omnipotent.Data_Handling;
using System.Threading;

namespace Omnipotent.Services.Omniscience
{
    /// <summary>
    /// Owns the SQLite database file for Omniscience. Provides connection factory,
    /// runs migrations on first use, exposes a global write lock for serialised writes.
    /// </summary>
    public class OmniscienceDb
    {
        public string DbPath { get; }
        public string ConnectionString { get; }

        // SQLite is single-writer; we serialise writes through the service to avoid
        // SQLITE_BUSY chains under concurrent ingest + analytics.
        public readonly SemaphoreSlim WriteLock = new(1, 1);

        public OmniscienceDb()
        {
            Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory));
            DbPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDbFile);
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString();
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;";
                pragma.ExecuteNonQuery();
            }
            return conn;
        }

        public void Migrate()
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            int currentVersion;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "PRAGMA user_version;";
                currentVersion = Convert.ToInt32(cmd.ExecuteScalar());
            }

            if (currentVersion < 1)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SchemaV1;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "PRAGMA user_version = 1;";
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        // ── Migration: v1 (initial schema) ──
        private const string SchemaV1 = @"
CREATE TABLE IF NOT EXISTS persons (
    person_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    notes TEXT,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    merged_into_person_id TEXT,
    avatar_path TEXT
);

CREATE TABLE IF NOT EXISTS platform_identities (
    identity_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    platform TEXT NOT NULL,
    platform_user_id TEXT NOT NULL,
    platform_username TEXT,
    display_name TEXT,
    avatar_path TEXT,
    bio TEXT,
    extra_json TEXT,
    first_seen INTEGER,
    last_seen INTEGER,
    FOREIGN KEY(person_id) REFERENCES persons(person_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_identity_platform_user ON platform_identities(platform, platform_user_id);
CREATE INDEX IF NOT EXISTS idx_identity_person ON platform_identities(person_id);

CREATE TABLE IF NOT EXISTS conversations (
    conversation_id TEXT PRIMARY KEY,
    platform TEXT NOT NULL,
    kind TEXT NOT NULL,
    guild_id TEXT,
    guild_name TEXT,
    channel_id TEXT,
    title TEXT,
    extra_json TEXT,
    first_seen INTEGER,
    last_seen INTEGER
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_conv_platform_channel ON conversations(platform, channel_id);

CREATE TABLE IF NOT EXISTS conversation_participants (
    conversation_id TEXT NOT NULL,
    identity_id TEXT NOT NULL,
    PRIMARY KEY(conversation_id, identity_id)
);
CREATE INDEX IF NOT EXISTS idx_part_identity ON conversation_participants(identity_id);

CREATE TABLE IF NOT EXISTS messages (
    message_id TEXT PRIMARY KEY,
    platform TEXT NOT NULL,
    platform_message_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    author_identity_id TEXT NOT NULL,
    sent_at INTEGER NOT NULL,
    edited_at INTEGER,
    content TEXT,
    reply_to_message_id TEXT,
    language TEXT,
    raw_json TEXT,
    captured_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_msg_author_time ON messages(author_identity_id, sent_at);
CREATE INDEX IF NOT EXISTS idx_msg_conv_time ON messages(conversation_id, sent_at);
CREATE INDEX IF NOT EXISTS idx_msg_platform_pmid ON messages(platform, platform_message_id);

CREATE TABLE IF NOT EXISTS attachments (
    attachment_id TEXT PRIMARY KEY,
    message_id TEXT NOT NULL,
    kind TEXT,
    original_url TEXT,
    local_path TEXT,
    mime TEXT,
    size_bytes INTEGER,
    sha256 TEXT,
    FOREIGN KEY(message_id) REFERENCES messages(message_id)
);
CREATE INDEX IF NOT EXISTS idx_attach_msg ON attachments(message_id);

CREATE TABLE IF NOT EXISTS harvest_sources (
    source_id TEXT PRIMARY KEY,
    platform TEXT NOT NULL,
    label TEXT,
    token_encrypted BLOB,
    self_platform_user_id TEXT,
    self_username TEXT,
    status TEXT NOT NULL,
    last_status_message TEXT,
    added_at INTEGER NOT NULL,
    last_full_sync_at INTEGER,
    last_event_at INTEGER
);

CREATE TABLE IF NOT EXISTS ingest_cursors (
    source_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    earliest_message_id TEXT,
    latest_message_id TEXT,
    fully_backfilled INTEGER NOT NULL DEFAULT 0,
    last_synced_at INTEGER,
    PRIMARY KEY(source_id, conversation_id)
);

CREATE TABLE IF NOT EXISTS person_statistics (
    person_id TEXT NOT NULL,
    module_name TEXT NOT NULL,
    module_version INTEGER NOT NULL,
    computed_at INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    PRIMARY KEY(person_id, module_name)
);

CREATE TABLE IF NOT EXISTS personality_profiles (
    profile_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    generated_at INTEGER NOT NULL,
    model_used TEXT,
    prompt_hash TEXT,
    profile_markdown TEXT,
    traits_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_profile_person ON personality_profiles(person_id, generated_at DESC);
";
    }
}
