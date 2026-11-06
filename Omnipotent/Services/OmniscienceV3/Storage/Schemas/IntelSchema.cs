using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Schemas
{
    /// <summary>
    /// intel.db — everything derived; fully re-derivable from corpus.db + operator.db (plan R3).
    /// Kernel-owned base tables: jobs, module state, dirty sets, read models, LLM call audit, live-stream
    /// events; plus the knowledge-store contract (R7 #6: bitemporal assertions with evidence) and the
    /// classifier label contract (R7 #5). Modules add their own tables through module-scoped migrations.
    ///
    /// Every derived row records <c>source_seq</c>: the corpus change-log watermark it was computed from.
    /// </summary>
    public static class IntelSchema
    {
        public const string ModuleId = "core.intel";

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "kernel tables, knowledge store, labels", """
CREATE TABLE jobs(
  job_id INTEGER PRIMARY KEY AUTOINCREMENT,
  module_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  subject_kind TEXT,
  subject_id TEXT,
  dedupe_key TEXT,
  priority INTEGER NOT NULL,
  lane TEXT NOT NULL,
  state TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  max_attempts INTEGER NOT NULL,
  not_before_ms INTEGER NOT NULL,
  lease_owner TEXT,
  lease_expires_ms INTEGER,
  heartbeat_ms INTEGER,
  payload_json TEXT,
  progress_json TEXT,
  result_json TEXT,
  error_class TEXT,
  error_message TEXT,
  created_at_ms INTEGER NOT NULL,
  started_at_ms INTEGER,
  finished_at_ms INTEGER,
  parent_job_id INTEGER,
  run_id TEXT
);
CREATE UNIQUE INDEX ux_jobs_dedupe_active ON jobs(dedupe_key) WHERE dedupe_key IS NOT NULL AND state IN ('queued','leased');
CREATE INDEX ix_jobs_ready ON jobs(lane, state, priority DESC, not_before_ms, job_id);
CREATE INDEX ix_jobs_module_state ON jobs(module_id, state);
CREATE INDEX ix_jobs_lease ON jobs(state, lease_expires_ms) WHERE state = 'leased';
CREATE INDEX ix_jobs_run ON jobs(run_id) WHERE run_id IS NOT NULL;

CREATE TABLE module_state(
  module_id TEXT PRIMARY KEY,
  version INTEGER NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  watermark_seq INTEGER NOT NULL DEFAULT 0,
  state_json TEXT,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE module_dirty(
  module_id TEXT NOT NULL,
  subject_kind TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  dirty_since_seq INTEGER NOT NULL,
  dirty_at_ms INTEGER NOT NULL,
  PRIMARY KEY(module_id, subject_kind, subject_id)
);

CREATE TABLE read_models(
  model_id TEXT NOT NULL,
  key TEXT NOT NULL,
  version INTEGER NOT NULL,
  schema_version INTEGER NOT NULL,
  as_of_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL,
  computed_at_ms INTEGER NOT NULL,
  body TEXT NOT NULL,
  PRIMARY KEY(model_id, key)
);

CREATE TABLE llm_calls(
  call_id INTEGER PRIMARY KEY AUTOINCREMENT,
  at_ms INTEGER NOT NULL,
  module_id TEXT NOT NULL,
  purpose TEXT NOT NULL,
  job_id INTEGER,
  model TEXT,
  provider TEXT,
  work_class TEXT,
  prompt_tokens INTEGER,
  cached_tokens INTEGER,
  completion_tokens INTEGER,
  request_bytes INTEGER,
  latency_ms INTEGER,
  outcome TEXT NOT NULL,
  finish_reason TEXT,
  prompt_version TEXT,
  error TEXT
);
CREATE INDEX ix_llm_calls_time ON llm_calls(at_ms);
CREATE INDEX ix_llm_calls_module ON llm_calls(module_id, at_ms);

CREATE TABLE stream_events(
  event_id INTEGER PRIMARY KEY AUTOINCREMENT,
  at_ms INTEGER NOT NULL,
  topic TEXT NOT NULL,
  payload_json TEXT NOT NULL
);
CREATE INDEX ix_stream_events_time ON stream_events(at_ms);

CREATE TABLE assertions(
  assertion_id TEXT PRIMARY KEY,
  natural_key TEXT NOT NULL,
  subject_kind TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  attribute TEXT NOT NULL,
  attr_class TEXT NOT NULL,
  value_json TEXT NOT NULL,
  value_text TEXT,
  object_kind TEXT,
  object_id TEXT,
  valid_from_earliest_ms INTEGER,
  valid_from_latest_ms INTEGER,
  valid_to_earliest_ms INTEGER,
  valid_to_latest_ms INTEGER,
  asserted_at_ms INTEGER,
  recorded_at_ms INTEGER NOT NULL,
  superseded_at_ms INTEGER,
  retracted_at_ms INTEGER,
  status TEXT NOT NULL,
  modality TEXT,
  source_kind TEXT,
  probability REAL,
  calibrated INTEGER NOT NULL DEFAULT 0,
  sensitivity TEXT NOT NULL DEFAULT 'normal',
  module_id TEXT NOT NULL,
  module_version INTEGER NOT NULL,
  prompt_version TEXT,
  model TEXT,
  job_id INTEGER,
  source_seq INTEGER NOT NULL,
  derivation_json TEXT
);
CREATE INDEX ix_assertions_current ON assertions(subject_kind, subject_id, attribute) WHERE superseded_at_ms IS NULL AND retracted_at_ms IS NULL;
CREATE INDEX ix_assertions_key ON assertions(natural_key);
CREATE INDEX ix_assertions_object ON assertions(object_kind, object_id) WHERE object_id IS NOT NULL;
CREATE INDEX ix_assertions_recorded ON assertions(recorded_at_ms);

CREATE TABLE assertion_evidence(
  assertion_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  message_version INTEGER NOT NULL,
  role TEXT NOT NULL,
  quote TEXT,
  weight REAL,
  PRIMARY KEY(assertion_id, message_id, message_version, role)
);
CREATE INDEX ix_assertion_evidence_message ON assertion_evidence(message_id);

CREATE TABLE labels(
  item_kind TEXT NOT NULL,
  item_id TEXT NOT NULL,
  item_version INTEGER NOT NULL,
  task TEXT NOT NULL,
  label TEXT,
  probability REAL,
  abstained INTEGER NOT NULL DEFAULT 0,
  signals_json TEXT,
  model TEXT NOT NULL,
  model_version TEXT NOT NULL,
  prompt_version TEXT,
  as_of_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL,
  PRIMARY KEY(item_kind, item_id, task, model_version)
);
CREATE INDEX ix_labels_task_label ON labels(task, label);
"""),
        };
    }
}
