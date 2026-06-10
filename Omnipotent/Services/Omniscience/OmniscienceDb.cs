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

        public OmniscienceDb() : this(null) { }

        /// <summary><paramref name="dbPathOverride"/> lets tests run migrations against a temp file.</summary>
        public OmniscienceDb(string? dbPathOverride)
        {
            if (dbPathOverride == null)
            {
                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory));
                DbPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDbFile);
            }
            else
            {
                DbPath = dbPathOverride;
            }
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

            var migrations = new (int Version, string Sql)[]
            {
                (1, SchemaV1),
                (2, SchemaV2),
                (3, SchemaV3),
                (4, SchemaV4),
                (5, SchemaV5),
                (6, SchemaV6),
                (7, SchemaV7),
                (8, SchemaV8),
                (9, SchemaV9),
                (10, SchemaV10),
                (11, SchemaV11),
            };

            foreach (var (version, sql) in migrations)
            {
                if (currentVersion >= version) continue;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"PRAGMA user_version = {version};";
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        // ── Migration: v11 (self-disclosure fast-track + cross-platform identity links) ──
        // priority on extraction_cursors lets ingest-time self-disclosure detection jump
        // a conversation to the front of the next extraction pass (answers get mined the
        // night they're given, not weeks later). person_link_suggestions is the
        // cross-platform identity-resolution queue (WhatsApp 'Sarah' ↔ Discord 'sarah_x').
        private const string SchemaV11 = @"
ALTER TABLE extraction_cursors ADD COLUMN priority INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS person_link_suggestions (
    suggestion_id TEXT PRIMARY KEY,
    person_id_a TEXT NOT NULL,
    person_id_b TEXT NOT NULL,
    score REAL NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'pending',  -- pending|accepted|rejected
    created_at INTEGER NOT NULL,
    UNIQUE(person_id_a, person_id_b)
);
CREATE INDEX IF NOT EXISTS idx_personlink_pending ON person_link_suggestions(status, score DESC);
";

        // ── Migration: v10 (generic watchlists) ──
        // The configurable generalisation of the built-in Klives radar: alert on any
        // keyword/topic, optionally scoped to one person's messages. Fired alerts share
        // radar_alerts (watch_label distinguishes which watchlist hit).
        private const string SchemaV10 = @"
CREATE TABLE IF NOT EXISTS watchlists (
    watch_id TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    terms TEXT NOT NULL,                    -- comma-separated keywords/phrases
    person_id TEXT,                         -- optional: only fire when this person speaks
    enabled INTEGER NOT NULL DEFAULT 1,
    notify INTEGER NOT NULL DEFAULT 1,      -- 1 = immediate DM; 0 = log + briefing only
    created_at INTEGER NOT NULL
);

ALTER TABLE radar_alerts ADD COLUMN watch_label TEXT;   -- NULL = built-in Klives radar
";

        // ── Migration: v9 (replica fidelity benchmarking) ──
        // Replica accuracy becomes measurable: each run holds out recent real
        // (stimulus → reply) pairs, generates replica predictions, and scores them on
        // embedding similarity + stylometric match. One fidelity number per replica
        // version means every trainer/prompt change is measured, regressions visible.
        private const string SchemaV9 = @"
CREATE TABLE IF NOT EXISTS replica_fidelity_runs (
    run_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    replica_version INTEGER NOT NULL DEFAULT 0,
    ran_at INTEGER NOT NULL,
    pairs_tested INTEGER NOT NULL,
    avg_embedding_similarity REAL,
    avg_style_score REAL,
    overall_fidelity REAL,
    details_json TEXT                       -- per-pair breakdown for the worst-miss review
);
CREATE INDEX IF NOT EXISTS idx_fidelity_person ON replica_fidelity_runs(person_id, ran_at DESC);
";

        // ── Migration: v8 (Deduction Engine stages 2-3: knowledge graph + detective) ──
        // person_facts is the heart of "know everything": every concrete claim with
        // provenance, confidence, source-context trust (DM > public), and — for
        // detective-derived facts — an explicit derivation chain. Entities and typed,
        // time-bounded relationship edges make "Sarah — gf 2024-2025" representable.
        // hypotheses + open_questions give the engine goals: it knows what it doesn't
        // know yet and watches incoming messages for answers.
        private const string SchemaV8 = @"
CREATE TABLE IF NOT EXISTS person_facts (
    fact_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    category TEXT NOT NULL,                -- location|education|employment|relationships|family|pets|health|possessions|schedule|preferences|beliefs|skills|finances|plans|age|name|misc
    fact_text TEXT NOT NULL,
    confidence REAL NOT NULL,
    status TEXT NOT NULL DEFAULT 'active', -- active|decayed|contradicted|superseded
    source_context TEXT,                   -- dm|group_dm|server : trust weighting (DM wins)
    first_evidence_at INTEGER,
    last_evidence_at INTEGER,
    evidence_message_ids_json TEXT,
    derived_from_json TEXT,                -- derivation chain for detective-pass facts
    extracted_by TEXT NOT NULL,            -- extraction|temporal|detective|alias|manual
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_facts_person ON person_facts(person_id, status, category);

CREATE TABLE IF NOT EXISTS entities (
    entity_id TEXT PRIMARY KEY,
    owner_person_id TEXT NOT NULL,         -- whose knowledge graph this entity lives in
    kind TEXT NOT NULL,                    -- person|place|org|school|pet|event|object
    canonical_name TEXT NOT NULL,
    descriptor TEXT,
    linked_person_id TEXT,                 -- set when resolved to a known person record
    mention_count INTEGER NOT NULL DEFAULT 1,
    first_seen INTEGER,
    last_seen INTEGER
);
CREATE INDEX IF NOT EXISTS idx_entities_owner ON entities(owner_person_id, kind);

CREATE TABLE IF NOT EXISTS entity_relationships (
    rel_id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_person_id TEXT NOT NULL,
    related_person_id TEXT,                -- resolved person, when known
    entity_id TEXT,                        -- otherwise the entity
    rel_type TEXT NOT NULL,                -- sibling|parent|partner|ex|friend|best_friend|classmate|coworker|teacher|affection|banter|family|romance|hostility|support|inside_joke|...
    confidence REAL NOT NULL DEFAULT 0.3,
    evidence_count INTEGER NOT NULL DEFAULT 1,
    valid_from INTEGER,
    valid_to INTEGER,                      -- NULL = current ('dated 2024-2025' is representable)
    evidence_json TEXT,
    status TEXT NOT NULL DEFAULT 'active',
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rel_owner ON entity_relationships(owner_person_id, status);

CREATE TABLE IF NOT EXISTS entity_merge_suggestions (
    suggestion_id TEXT PRIMARY KEY,
    owner_person_id TEXT NOT NULL,
    entity_id_a TEXT NOT NULL,
    entity_id_b TEXT NOT NULL,
    score REAL NOT NULL,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'pending', -- pending|accepted|rejected
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_merge_pending ON entity_merge_suggestions(status, created_at DESC);

CREATE TABLE IF NOT EXISTS hypotheses (
    hypothesis_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    statement TEXT NOT NULL,
    rationale TEXT,
    confirm_query TEXT,                    -- retrieval query for ingest-time watchers
    status TEXT NOT NULL DEFAULT 'open',   -- open|confirmed|refuted|stale
    evidence_json TEXT,
    created_at INTEGER NOT NULL,
    resolved_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_hypotheses_person ON hypotheses(person_id, status);

CREATE TABLE IF NOT EXISTS open_questions (
    question_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    slot TEXT NOT NULL,                    -- canonical completeness slot
    question TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',   -- open|answered
    created_at INTEGER NOT NULL,
    answered_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_oq_person ON open_questions(person_id, status);

CREATE TABLE IF NOT EXISTS profile_changelogs (
    changelog_id TEXT PRIMARY KEY,
    person_id TEXT NOT NULL,
    generated_at INTEGER NOT NULL,
    changes_markdown TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_changelog_person ON profile_changelogs(person_id, generated_at DESC);

CREATE TABLE IF NOT EXISTS target_suggestions (
    person_id TEXT PRIMARY KEY,
    score REAL NOT NULL,
    reasons_json TEXT NOT NULL,
    computed_at INTEGER NOT NULL,
    dismissed INTEGER NOT NULL DEFAULT 0
);

ALTER TABLE personality_profiles ADD COLUMN evidence_json TEXT;
ALTER TABLE personality_profiles ADD COLUMN era TEXT;
";

        // ── Migration: v7 (Deduction Engine stage 1: exhaustive extraction) ──
        // Every conversation involving a Tracked person is read in overlapping windows by
        // a free-tier LLM. Raw window outputs persist in extraction_results so the
        // knowledge graph (v8) can be rebuilt without re-calling the LLM. qa_pairs and
        // name_usages are first-class because they're the highest-grade evidence
        // (answers to direct personal questions; what people actually call each other).
        // stimulus_reply_pairs are harvested deterministically and feed replica training.
        private const string SchemaV7 = @"
CREATE TABLE IF NOT EXISTS extraction_cursors (
    conversation_id TEXT PRIMARY KEY,
    last_extracted_sent_at INTEGER NOT NULL DEFAULT 0,   -- watermark: messages up to here processed
    last_run_at INTEGER
);

CREATE TABLE IF NOT EXISTS extraction_results (
    window_id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    window_start_sent_at INTEGER NOT NULL,
    window_end_sent_at INTEGER NOT NULL,
    message_count INTEGER NOT NULL,
    extracted_at INTEGER NOT NULL,
    model_used TEXT,
    status TEXT NOT NULL DEFAULT 'ok',     -- 'ok' | 'skipped_low_density' | 'failed'
    payload_json TEXT,                     -- raw LLM JSON: facts, name_usages, qa_pairs, entity_mentions, relationship_signals, temporal_refs
    graph_applied INTEGER NOT NULL DEFAULT 0  -- set by stage-2 graph assembly (v8)
);
CREATE INDEX IF NOT EXISTS idx_extraction_conv ON extraction_results(conversation_id, window_end_sent_at);
CREATE INDEX IF NOT EXISTS idx_extraction_unapplied ON extraction_results(graph_applied, extracted_at);

CREATE TABLE IF NOT EXISTS qa_pairs (
    qa_id INTEGER PRIMARY KEY AUTOINCREMENT,
    asker_identity_id TEXT,
    answerer_identity_id TEXT,
    question TEXT NOT NULL,
    answer TEXT NOT NULL,
    category TEXT,
    question_message_id TEXT,
    answer_message_id TEXT,
    conversation_id TEXT,
    occurred_at INTEGER,
    extracted_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_qa_answerer ON qa_pairs(answerer_identity_id);

CREATE TABLE IF NOT EXISTS name_usages (
    usage_id INTEGER PRIMARY KEY AUTOINCREMENT,
    speaker_identity_id TEXT,
    name_used TEXT NOT NULL,
    usage_type TEXT NOT NULL,              -- 'vocative' | 'self_identification' | 'third_person' | 'greeting'
    target_identity_id TEXT,               -- resolved when determinable
    target_hint TEXT,                      -- raw hint when unresolved
    evidence_message_id TEXT,
    conversation_id TEXT,
    occurred_at INTEGER,
    extracted_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_nameusage_target ON name_usages(target_identity_id, name_used);
CREATE INDEX IF NOT EXISTS idx_nameusage_speaker ON name_usages(speaker_identity_id);

CREATE TABLE IF NOT EXISTS stimulus_reply_pairs (
    pair_id INTEGER PRIMARY KEY AUTOINCREMENT,
    replier_identity_id TEXT NOT NULL,
    stimulus_message_id TEXT,
    reply_message_id TEXT UNIQUE,
    stimulus_text TEXT NOT NULL,
    reply_text TEXT NOT NULL,
    conversation_id TEXT,
    occurred_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_srp_replier ON stimulus_reply_pairs(replier_identity_id, occurred_at);
";

        // ── Migration: v6 (global message embedding index) ──
        // Generalises the replica-only embedding store into a corpus-wide semantic layer:
        // semantic search, person Q&A retrieval, deduction hypothesis watchers, and
        // replica stimulus-matching all read from this one index. Replica training now
        // writes here instead of replica_message_embeddings.
        private const string SchemaV6 = @"
CREATE TABLE IF NOT EXISTS message_embeddings (
    message_id TEXT PRIMARY KEY,
    embedding BLOB NOT NULL,                -- packed float[384], L2-normalised (MiniLM-L6)
    embedded_at INTEGER NOT NULL
);
";

        // ── Migration: v5 (expanded event capture + tracking tiers + radar) ──
        // People reveal as much through *behaviour* as through message text: presence
        // (game/Spotify activity, custom statuses), typing telemetry, reactions, voice
        // hangouts, edits and deletions. v5 also introduces tracking tiers on persons
        // (tracked / watch / archive) so heavy compute is only spent on people Klives
        // actually cares about, plus the radar alert log.
        private const string SchemaV5 = @"
ALTER TABLE persons ADD COLUMN tier TEXT NOT NULL DEFAULT 'archive';
UPDATE persons SET tier='tracked' WHERE person_id IN (SELECT person_id FROM person_profile_targets WHERE enabled=1);

ALTER TABLE attachments ADD COLUMN ocr_text TEXT;

CREATE TABLE IF NOT EXISTS omniscience_meta (
    key TEXT PRIMARY KEY,
    value TEXT
);

CREATE TABLE IF NOT EXISTS presence_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    platform TEXT NOT NULL DEFAULT 'discord',
    platform_user_id TEXT NOT NULL,
    status TEXT,                            -- 'online' | 'idle' | 'dnd' | 'offline'
    custom_status TEXT,                     -- custom status text (micro-blog goldmine)
    activities_json TEXT,                   -- raw activities array (games, Spotify, streaming)
    captured_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_presence_user_time ON presence_events(platform_user_id, captured_at);

CREATE TABLE IF NOT EXISTS typing_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    platform TEXT NOT NULL DEFAULT 'discord',
    platform_user_id TEXT NOT NULL,
    channel_id TEXT,
    started_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_typing_user_time ON typing_events(platform_user_id, started_at);

CREATE TABLE IF NOT EXISTS reaction_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    platform TEXT NOT NULL DEFAULT 'discord',
    platform_message_id TEXT NOT NULL,
    channel_id TEXT,
    reactor_platform_user_id TEXT NOT NULL, -- '' for snapshot rows (raw_json has counts, not reactors)
    emoji TEXT NOT NULL,
    action TEXT NOT NULL,                   -- 'add' | 'remove' | 'snapshot' (raw_json backfill)
    count INTEGER NOT NULL DEFAULT 1,
    occurred_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_reaction_msg ON reaction_events(platform_message_id);
CREATE INDEX IF NOT EXISTS idx_reaction_user ON reaction_events(reactor_platform_user_id, occurred_at);

CREATE TABLE IF NOT EXISTS voice_sessions (
    session_id INTEGER PRIMARY KEY AUTOINCREMENT,
    platform TEXT NOT NULL DEFAULT 'discord',
    platform_user_id TEXT NOT NULL,
    guild_id TEXT,
    channel_id TEXT,
    joined_at INTEGER NOT NULL,
    left_at INTEGER                         -- NULL while session is open
);
CREATE INDEX IF NOT EXISTS idx_voice_user_time ON voice_sessions(platform_user_id, joined_at);
CREATE INDEX IF NOT EXISTS idx_voice_open ON voice_sessions(platform_user_id, left_at);

CREATE TABLE IF NOT EXISTS message_edits (
    edit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id TEXT NOT NULL,               -- composite 'discord:<id>'
    old_content TEXT,
    new_content TEXT,
    edited_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_edits_msg ON message_edits(message_id);

CREATE TABLE IF NOT EXISTS message_deletes (
    delete_id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id TEXT NOT NULL,
    channel_id TEXT,
    author_identity_id TEXT,
    content TEXT,                           -- recovered from our DB: the deleted-message archive
    deleted_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_deletes_author ON message_deletes(author_identity_id, deleted_at);

CREATE TABLE IF NOT EXISTS identity_history (
    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
    identity_id TEXT NOT NULL,
    field TEXT NOT NULL,                    -- 'username' | 'display_name' | 'bio' | 'avatar' | 'custom_status'
    old_value TEXT,
    new_value TEXT,
    changed_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_idhist ON identity_history(identity_id, changed_at);

CREATE TABLE IF NOT EXISTS radar_alerts (
    alert_id INTEGER PRIMARY KEY AUTOINCREMENT,
    matched_alias TEXT NOT NULL,
    message_id TEXT,
    author_display TEXT,
    channel_label TEXT,
    snippet TEXT,
    occurred_at INTEGER NOT NULL,
    notified INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_radar_time ON radar_alerts(occurred_at DESC);
";

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
