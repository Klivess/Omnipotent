using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Schemas
{
    /// <summary>
    /// operator.db — everything Klives authored. Never derived, never rebuilt (plan storage section).
    /// Frozen contract R7 #1.
    ///
    /// Gold labels (review decisions) reference <b>natural keys</b> (canonical JSON of person + attribute +
    /// normalised value + evidence ids, face-crop hashes, …), never intel.db row ids, so the whole intel
    /// store can be rebuilt without losing a single decision (plan R3). Person-scoped settings are keyed by
    /// the canonical person id at decision time and re-resolved through corpus.db's canonical map.
    /// </summary>
    public static class OperatorSchema
    {
        public const string ModuleId = "core.operator";

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "operator base schema", """
CREATE TABLE gold_labels(
  label_id INTEGER PRIMARY KEY AUTOINCREMENT,
  kind TEXT NOT NULL,
  natural_key TEXT NOT NULL,
  verdict TEXT NOT NULL,
  correction_json TEXT,
  note TEXT,
  decided_at_ms INTEGER NOT NULL,
  actor TEXT NOT NULL,
  superseded_at_ms INTEGER
);
CREATE INDEX ix_gold_labels_key ON gold_labels(kind, natural_key);
CREATE INDEX ix_gold_labels_active ON gold_labels(kind) WHERE superseded_at_ms IS NULL;

CREATE TABLE person_settings(
  person_id TEXT PRIMARY KEY,
  tier TEXT NOT NULL DEFAULT 'archive',
  minor_marked INTEGER NOT NULL DEFAULT 0,
  pinned INTEGER NOT NULL DEFAULT 0,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE notes(
  note_id TEXT PRIMARY KEY,
  subject_kind TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  body TEXT NOT NULL,
  tags TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  deleted_at_ms INTEGER
);
CREATE INDEX ix_notes_subject ON notes(subject_kind, subject_id);

CREATE TABLE cohorts(
  cohort_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  definition_json TEXT,
  members_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE saved_views(
  view_id TEXT PRIMARY KEY,
  page TEXT NOT NULL,
  name TEXT NOT NULL,
  state_json TEXT NOT NULL,
  use_count INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE playbooks(
  playbook_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  definition_json TEXT NOT NULL,
  trigger_json TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  last_run_ms INTEGER,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE watchlists(
  watch_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  name TEXT NOT NULL,
  definition_json TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE standing_questions(
  question_id TEXT PRIMARY KEY,
  inquiry_json TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  min_interval_ms INTEGER NOT NULL,
  last_evaluated_ms INTEGER,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE settings(
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE last_seen(
  scope TEXT PRIMARY KEY,
  ingest_seq INTEGER NOT NULL,
  sent_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE dismissals(
  kind TEXT NOT NULL,
  key TEXT NOT NULL,
  until_ms INTEGER,
  at_ms INTEGER NOT NULL,
  PRIMARY KEY(kind, key)
);

CREATE TABLE replica_chats(
  chat_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  title TEXT,
  as_of_ms INTEGER,
  context_json TEXT,
  archived INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE replica_chat_messages(
  chat_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  role TEXT NOT NULL,
  content TEXT NOT NULL,
  meta_json TEXT,
  at_ms INTEGER NOT NULL,
  PRIMARY KEY(chat_id, seq)
);

CREATE TABLE audit_log(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at_ms INTEGER NOT NULL,
  actor TEXT NOT NULL,
  action TEXT NOT NULL,
  target TEXT,
  detail_json TEXT
);
CREATE INDEX ix_audit_log_time ON audit_log(at_ms);
"""),
        };
    }
}
