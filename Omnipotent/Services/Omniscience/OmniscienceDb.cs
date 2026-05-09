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
            // NOTE: do NOT set Cache=Shared here.
            // Microsoft.Data.Sqlite's shared-cache mode funnels every connection in the
            // process through a single underlying SQLite connection / mutex. Combined
            // with the connection pool and several concurrent readers (the Omniscience
            // dashboard fires 6+ parallel queries on page load) plus the IngestPipeline
            // writer, that mutex serialises everything and routinely deadlocks the
            // thread pool, which then wedges the rest of KliveAPI. Default (private)
            // cache + WAL gives us proper concurrent readers + one writer without that
            // global bottleneck.
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 30, // seconds; gives SQLite room to wait for a busy lock instead of throwing immediately
            }.ToString();
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                // journal_mode=WAL is persistent on disk and is enforced once during
                // Migrate(); no need to re-apply per connection. The remaining pragmas
                // are per-connection. busy_timeout makes any transient lock contention
                // wait politely instead of bubbling up as SQLITE_BUSY.
                pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
            return conn;
        }

        public void Migrate()
        {
            using var conn = Open();

            // journal_mode=WAL is a persistent, file-level setting. It must be applied
            // outside any transaction (SQLite refuses to change journal mode mid-tx).
            // Doing it once here means readers no longer pay the cost on every Open().
            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
            }

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

            if (currentVersion < 2)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SchemaV2;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "PRAGMA user_version = 2;";
                    cmd.ExecuteNonQuery();
                }
            }

            if (currentVersion < 3)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SchemaV3;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "PRAGMA user_version = 3;";
                    cmd.ExecuteNonQuery();
                }
            }

            if (currentVersion < 4)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SchemaV4;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "PRAGMA user_version = 4;";
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        // ── Migration: v4 (Replica system: per-person LLM-driven chat agent) ──
        // The Replica feature lets the user chat with an LLM that mimics a specific
        // person, trained from their message history. We persist:
        //   - replicas:                   the trained persona dossier + status
        //   - replica_message_embeddings: vector index over the person's messages
        //   - replica_chats:              ChatGPT-style multi-conversation per replica
        //   - replica_chat_messages:      individual user/assistant turns
        //   - replica_training_jobs:      live training progress for the UI
        //   - replica_notifications:      server-side toast queue for the website
        private const string SchemaV4 = @"
CREATE TABLE IF NOT EXISTS replicas (
    person_id TEXT PRIMARY KEY,
    status TEXT NOT NULL,                  -- 'none' | 'training' | 'ready' | 'failed' | 'stale'
    version INTEGER NOT NULL DEFAULT 0,    -- bumped each successful retrain
    built_at INTEGER,
    model_used TEXT,
    messages_embedded INTEGER NOT NULL DEFAULT 0,
    prompt_token_estimate INTEGER NOT NULL DEFAULT 0,
    dossier_json TEXT,                     -- structured ReplicaDossier (voice rules, opinions, reflexes, etc.)
    stylistic_exemplars_json TEXT,         -- top-30 always-on (stimulus, reply) pairs
    last_error TEXT,
    last_status_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_replicas_status ON replicas(status, last_status_at DESC);

-- Vector index over the person's messages. embedding is a packed float[384] (MiniLM-L6 dim).
-- Brute-force cosine in C# at query time; fine at expected per-person scale (10k-100k msgs).
CREATE TABLE IF NOT EXISTS replica_message_embeddings (
    person_id TEXT NOT NULL,
    message_id TEXT NOT NULL,
    embedding BLOB NOT NULL,
    embedded_at INTEGER NOT NULL,
    PRIMARY KEY(person_id, message_id)
);
CREATE INDEX IF NOT EXISTS idx_replica_emb_person ON replica_message_embeddings(person_id);

CREATE TABLE IF NOT EXISTS replica_chats (
    chat_id INTEGER PRIMARY KEY AUTOINCREMENT,
    person_id TEXT NOT NULL,
    title TEXT,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    archived INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_replica_chats_person ON replica_chats(person_id, archived, updated_at DESC);

CREATE TABLE IF NOT EXISTS replica_chat_messages (
    message_id INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id INTEGER NOT NULL,
    role TEXT NOT NULL,                    -- 'user' | 'assistant' | 'system'
    content TEXT NOT NULL,
    sent_at INTEGER NOT NULL,
    prompt_token_count INTEGER,
    completion_token_count INTEGER,
    latency_ms INTEGER,
    used_self_critique INTEGER NOT NULL DEFAULT 0,
    model_used TEXT,
    FOREIGN KEY(chat_id) REFERENCES replica_chats(chat_id)
);
CREATE INDEX IF NOT EXISTS idx_replica_chat_msgs ON replica_chat_messages(chat_id, sent_at);

CREATE TABLE IF NOT EXISTS replica_training_jobs (
    job_id INTEGER PRIMARY KEY AUTOINCREMENT,
    person_id TEXT NOT NULL,
    started_at INTEGER NOT NULL,
    finished_at INTEGER,
    status TEXT NOT NULL,                  -- 'queued' | 'running' | 'ok' | 'failed' | 'cancelled'
    stage TEXT,                            -- e.g. 'voice', 'opinions', 'reflexes', 'stylometric', 'relational', 'forbidden', 'embedding'
    progress_pct INTEGER NOT NULL DEFAULT 0,
    log_json TEXT,                         -- accumulated [{at, stage, msg}] entries
    error TEXT
);
CREATE INDEX IF NOT EXISTS idx_replica_jobs_person ON replica_training_jobs(person_id, started_at DESC);
CREATE INDEX IF NOT EXISTS idx_replica_jobs_status ON replica_training_jobs(status, started_at DESC);

CREATE TABLE IF NOT EXISTS replica_notifications (
    notif_id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,                    -- 'replica_ready' | 'replica_failed'
    person_id TEXT,
    payload_json TEXT,
    created_at INTEGER NOT NULL,
    dismissed INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_replica_notif_undismissed ON replica_notifications(dismissed, created_at DESC);
";

        // ── Migration: v3 (explicit profile target allow-list) ──
        private const string SchemaV3 = @"
CREATE TABLE IF NOT EXISTS person_profile_targets (
    person_id TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL DEFAULT 1,
    added_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_profiled_at INTEGER,
    last_profile_status TEXT,
    notes TEXT
);
CREATE INDEX IF NOT EXISTS idx_profile_targets_enabled ON person_profile_targets(enabled, updated_at DESC);
";

        // ── Migration: v2 (biographical dossier + identity alt-names) ──
        // Existing databases will not have these columns/tables; ALTER and CREATE IF NOT EXISTS
        // both succeed regardless of fresh-vs-upgrade because v1 ran first.
        private const string SchemaV2 = @"
ALTER TABLE personality_profiles ADD COLUMN biographical_markdown TEXT;

CREATE TABLE IF NOT EXISTS identity_alt_names (
    alt_name_id INTEGER PRIMARY KEY AUTOINCREMENT,
    identity_id TEXT NOT NULL,
    alt_name TEXT NOT NULL,
    source TEXT,
    first_seen INTEGER,
    last_seen INTEGER,
    UNIQUE(identity_id, alt_name)
);
CREATE INDEX IF NOT EXISTS idx_alt_name ON identity_alt_names(alt_name);
CREATE INDEX IF NOT EXISTS idx_alt_identity ON identity_alt_names(identity_id);
";

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
