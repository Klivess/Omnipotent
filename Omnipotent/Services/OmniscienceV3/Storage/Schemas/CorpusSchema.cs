using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Schemas
{
    /// <summary>
    /// corpus.db — raw platform data only, the single source of truth (plan R3). Frozen contract R7 #1.
    ///
    /// Conventions:
    /// <list type="bullet">
    /// <item>All instants are UTC epoch milliseconds (*_ms); <c>time_quality</c> uses <see cref="Temporal.TimeQuality"/>.</item>
    /// <item><c>change_log.seq</c> is the ingest sequence: every mutation appends exactly one row in the
    /// same transaction, so derived stores resume from a watermark after any crash (durable outbox).</item>
    /// <item>Names, titles, avatars and memberships carry validity intervals (plan T5).</item>
    /// <item>Messages keep every content version; <c>messages.content</c> mirrors the latest one.</item>
    /// <item>Field-level merge across sources is ranked by <c>sources.rank</c> (fixes first-writer-wins, F394).</item>
    /// </list>
    /// </summary>
    public static class CorpusSchema
    {
        public const string ModuleId = "core.corpus";

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "corpus base schema", """
CREATE TABLE change_log(
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  at_ms INTEGER NOT NULL,
  kind TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  person_id TEXT,
  conversation_id TEXT,
  source_kind TEXT NOT NULL
);
CREATE INDEX ix_change_log_entity ON change_log(entity_id);
CREATE INDEX ix_change_log_kind_seq ON change_log(kind, seq);

CREATE TABLE sources(
  source_id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  kind TEXT NOT NULL,
  label TEXT,
  self_platform_user_id TEXT,
  secret_blob BLOB,
  status TEXT NOT NULL,
  status_detail TEXT,
  rank INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE guilds(
  guild_id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  name TEXT,
  first_seen_ms INTEGER NOT NULL,
  last_seen_ms INTEGER NOT NULL
);
CREATE TABLE guild_names(
  guild_id TEXT NOT NULL,
  name TEXT NOT NULL,
  valid_from_ms INTEGER NOT NULL,
  valid_to_ms INTEGER,
  PRIMARY KEY(guild_id, valid_from_ms)
);

CREATE TABLE conversations(
  conversation_id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  kind TEXT NOT NULL,
  guild_id TEXT,
  parent_conversation_id TEXT,
  category_id TEXT,
  title TEXT,
  first_seen_ms INTEGER NOT NULL,
  last_message_ms INTEGER,
  coverage_json TEXT
);
CREATE INDEX ix_conversations_guild ON conversations(guild_id);
CREATE TABLE conversation_names(
  conversation_id TEXT NOT NULL,
  name TEXT NOT NULL,
  valid_from_ms INTEGER NOT NULL,
  valid_to_ms INTEGER,
  PRIMARY KEY(conversation_id, valid_from_ms)
);
CREATE TABLE conversation_members(
  conversation_id TEXT NOT NULL,
  identity_id TEXT NOT NULL,
  role TEXT NOT NULL,
  from_ms INTEGER NOT NULL,
  to_ms INTEGER,
  PRIMARY KEY(conversation_id, identity_id, from_ms)
);
CREATE INDEX ix_conversation_members_identity ON conversation_members(identity_id);

CREATE TABLE persons(
  person_id TEXT PRIMARY KEY,
  created_at_ms INTEGER NOT NULL,
  display_name TEXT,
  is_self INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE person_merges(
  merge_id INTEGER PRIMARY KEY AUTOINCREMENT,
  from_person_id TEXT NOT NULL,
  into_person_id TEXT NOT NULL,
  merged_at_ms INTEGER NOT NULL,
  actor TEXT NOT NULL,
  reason TEXT,
  undone_at_ms INTEGER
);
CREATE INDEX ix_person_merges_from ON person_merges(from_person_id);
CREATE TABLE person_canonical(
  person_id TEXT PRIMARY KEY,
  canonical_id TEXT NOT NULL
);
CREATE INDEX ix_person_canonical_canonical ON person_canonical(canonical_id);

CREATE TABLE identities(
  identity_id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  platform_user_id TEXT NOT NULL,
  person_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  first_seen_ms INTEGER NOT NULL,
  last_seen_ms INTEGER NOT NULL,
  UNIQUE(platform, platform_user_id)
);
CREATE INDEX ix_identities_person ON identities(person_id);
CREATE TABLE identity_names(
  identity_id TEXT NOT NULL,
  name_kind TEXT NOT NULL,
  value TEXT NOT NULL,
  valid_from_ms INTEGER NOT NULL,
  valid_to_ms INTEGER,
  source_kind TEXT NOT NULL,
  PRIMARY KEY(identity_id, name_kind, valid_from_ms)
);
CREATE INDEX ix_identity_names_value ON identity_names(value COLLATE NOCASE);
CREATE TABLE identity_avatars(
  identity_id TEXT NOT NULL,
  avatar_hash TEXT NOT NULL,
  cas_sha256 TEXT,
  valid_from_ms INTEGER NOT NULL,
  valid_to_ms INTEGER,
  PRIMARY KEY(identity_id, valid_from_ms)
);

CREATE TABLE messages(
  message_id TEXT PRIMARY KEY,
  platform TEXT NOT NULL,
  platform_message_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  guild_id TEXT,
  author_identity_id TEXT NOT NULL,
  sent_at_ms INTEGER NOT NULL,
  time_quality INTEGER NOT NULL,
  order_key INTEGER NOT NULL,
  type INTEGER NOT NULL DEFAULT 0,
  flags INTEGER NOT NULL DEFAULT 0,
  is_bot INTEGER NOT NULL DEFAULT 0,
  is_system INTEGER NOT NULL DEFAULT 0,
  is_webhook INTEGER NOT NULL DEFAULT 0,
  reply_to_message_id TEXT,
  forwarded_from_json TEXT,
  current_version INTEGER NOT NULL,
  content TEXT,
  edited_at_ms INTEGER,
  deleted_at_ms INTEGER,
  first_source_kind TEXT NOT NULL,
  best_source_id TEXT NOT NULL,
  best_source_rank INTEGER NOT NULL,
  ingested_at_ms INTEGER NOT NULL,
  ingest_seq INTEGER NOT NULL,
  raw_json TEXT,
  UNIQUE(platform, platform_message_id)
);
CREATE INDEX ix_messages_conv_time ON messages(conversation_id, sent_at_ms, order_key);
CREATE INDEX ix_messages_author_time ON messages(author_identity_id, sent_at_ms);
CREATE INDEX ix_messages_time ON messages(sent_at_ms);
CREATE INDEX ix_messages_guild_time ON messages(guild_id, sent_at_ms) WHERE guild_id IS NOT NULL;
CREATE INDEX ix_messages_reply ON messages(reply_to_message_id) WHERE reply_to_message_id IS NOT NULL;
CREATE INDEX ix_messages_seq ON messages(ingest_seq);

CREATE TABLE message_versions(
  message_id TEXT NOT NULL,
  version INTEGER NOT NULL,
  content TEXT,
  edited_at_ms INTEGER,
  recorded_at_ms INTEGER NOT NULL,
  source_id TEXT NOT NULL,
  PRIMARY KEY(message_id, version)
);
CREATE TABLE message_mentions(
  message_id TEXT NOT NULL,
  target_kind TEXT NOT NULL,
  target_id TEXT NOT NULL,
  PRIMARY KEY(message_id, target_kind, target_id)
);
CREATE INDEX ix_message_mentions_target ON message_mentions(target_kind, target_id);
CREATE TABLE message_embeds(
  message_id TEXT NOT NULL,
  idx INTEGER NOT NULL,
  type TEXT,
  provider TEXT,
  url TEXT,
  title TEXT,
  description TEXT,
  json TEXT,
  PRIMARY KEY(message_id, idx)
);
CREATE TABLE message_stickers(
  message_id TEXT NOT NULL,
  sticker_id TEXT NOT NULL,
  name TEXT,
  PRIMARY KEY(message_id, sticker_id)
);
CREATE TABLE message_polls(
  message_id TEXT PRIMARY KEY,
  question TEXT,
  answers_json TEXT,
  expiry_ms INTEGER
);
CREATE TABLE poll_votes(
  message_id TEXT NOT NULL,
  answer_id INTEGER NOT NULL,
  identity_id TEXT NOT NULL,
  at_ms INTEGER NOT NULL,
  removed_at_ms INTEGER,
  PRIMARY KEY(message_id, answer_id, identity_id, at_ms)
);

CREATE TABLE attachments(
  attachment_id TEXT PRIMARY KEY,
  message_id TEXT NOT NULL,
  idx INTEGER NOT NULL,
  platform_attachment_id TEXT,
  filename TEXT,
  mime TEXT,
  media_kind TEXT NOT NULL,
  size_bytes INTEGER,
  width INTEGER,
  height INTEGER,
  duration_ms INTEGER,
  alt_text TEXT,
  is_spoiler INTEGER NOT NULL DEFAULT 0,
  source_url TEXT,
  cas_sha256 TEXT,
  download_state TEXT NOT NULL,
  download_error TEXT,
  download_attempts INTEGER NOT NULL DEFAULT 0,
  taken_at_ms INTEGER,
  taken_quality INTEGER,
  created_at_ms INTEGER NOT NULL,
  UNIQUE(message_id, idx)
);
CREATE INDEX ix_attachments_pending ON attachments(download_state) WHERE download_state <> 'done';
CREATE INDEX ix_attachments_cas ON attachments(cas_sha256) WHERE cas_sha256 IS NOT NULL;
CREATE TABLE cas_objects(
  sha256 TEXT PRIMARY KEY,
  size_bytes INTEGER NOT NULL,
  mime TEXT,
  relative_path TEXT NOT NULL,
  stored_at_ms INTEGER NOT NULL
);

CREATE TABLE reactions(
  message_id TEXT NOT NULL,
  emoji_key TEXT NOT NULL,
  emoji_name TEXT,
  identity_id TEXT NOT NULL,
  is_burst INTEGER NOT NULL DEFAULT 0,
  added_at_ms INTEGER,
  removed_at_ms INTEGER,
  time_quality INTEGER NOT NULL,
  source_kind TEXT NOT NULL,
  PRIMARY KEY(message_id, emoji_key, identity_id)
);
CREATE INDEX ix_reactions_identity ON reactions(identity_id, added_at_ms);
CREATE TABLE reaction_counts(
  message_id TEXT NOT NULL,
  emoji_key TEXT NOT NULL,
  count INTEGER NOT NULL,
  observed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(message_id, emoji_key)
);

CREATE TABLE presence_events(
  event_id INTEGER PRIMARY KEY AUTOINCREMENT,
  identity_id TEXT NOT NULL,
  guild_id TEXT,
  at_ms INTEGER NOT NULL,
  status TEXT,
  client_status_json TEXT,
  activities_json TEXT,
  custom_status TEXT,
  source_id TEXT NOT NULL,
  dedupe_key TEXT NOT NULL UNIQUE
);
CREATE INDEX ix_presence_identity_time ON presence_events(identity_id, at_ms);
CREATE TABLE voice_sessions(
  session_id TEXT PRIMARY KEY,
  identity_id TEXT NOT NULL,
  guild_id TEXT,
  channel_id TEXT,
  joined_at_ms INTEGER NOT NULL,
  left_at_ms INTEGER,
  left_reason TEXT,
  flags_json TEXT,
  source_id TEXT NOT NULL
);
CREATE INDEX ix_voice_identity_time ON voice_sessions(identity_id, joined_at_ms);
CREATE TABLE typing_events(
  identity_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  at_ms INTEGER NOT NULL,
  source_id TEXT NOT NULL,
  PRIMARY KEY(identity_id, conversation_id, at_ms)
);
CREATE TABLE member_events(
  event_id INTEGER PRIMARY KEY AUTOINCREMENT,
  guild_id TEXT,
  identity_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  at_ms INTEGER NOT NULL,
  detail_json TEXT,
  source_id TEXT NOT NULL,
  dedupe_key TEXT NOT NULL UNIQUE
);
CREATE INDEX ix_member_events_identity ON member_events(identity_id, at_ms);

CREATE TABLE ingest_cursors(
  source_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  oldest_platform_id TEXT,
  newest_platform_id TEXT,
  backward_complete INTEGER NOT NULL DEFAULT 0,
  state TEXT,
  updated_at_ms INTEGER NOT NULL,
  PRIMARY KEY(source_id, conversation_id)
);
CREATE TABLE import_ledger(
  import_id TEXT PRIMARY KEY,
  format TEXT NOT NULL,
  file_sha256 TEXT NOT NULL UNIQUE,
  zone TEXT,
  owner_identity_id TEXT,
  state TEXT NOT NULL,
  counts_json TEXT,
  created_at_ms INTEGER NOT NULL,
  committed_at_ms INTEGER,
  error TEXT
);
CREATE TABLE legacy_import_state(
  table_name TEXT PRIMARY KEY,
  last_rowid INTEGER NOT NULL,
  rows_imported INTEGER NOT NULL,
  rows_quarantined INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE quarantine(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  origin TEXT NOT NULL,
  reason TEXT NOT NULL,
  payload TEXT,
  at_ms INTEGER NOT NULL
);

CREATE VIRTUAL TABLE messages_fts USING fts5(
  content,
  content='messages',
  content_rowid='rowid',
  tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER messages_fts_ai AFTER INSERT ON messages BEGIN
  INSERT INTO messages_fts(rowid, content) VALUES (new.rowid, new.content);
END;
CREATE TRIGGER messages_fts_ad AFTER DELETE ON messages BEGIN
  INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.rowid, old.content);
END;
CREATE TRIGGER messages_fts_au AFTER UPDATE OF content ON messages BEGIN
  INSERT INTO messages_fts(messages_fts, rowid, content) VALUES ('delete', old.rowid, old.content);
  INSERT INTO messages_fts(rowid, content) VALUES (new.rowid, new.content);
END;
"""),
            // A row that fails the same way twice (a re-scanned v2 table, a retried import) is quarantined once.
            new Migration(ModuleId, 2, "quarantine dedupe", """
CREATE UNIQUE INDEX ux_quarantine_origin_reason ON quarantine(origin, reason);
CREATE INDEX ix_quarantine_time ON quarantine(at_ms);
"""),
        };
    }
}
