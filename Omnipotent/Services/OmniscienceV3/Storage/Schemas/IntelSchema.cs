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
            // Calls that carried images (captions) are counted apart: no prefix cache can serve a different picture.
            new Migration(ModuleId, 2, "llm_calls.images", """
ALTER TABLE llm_calls ADD COLUMN images INTEGER NOT NULL DEFAULT 0;
"""),
            // Two days after launch the queue held 650,000 media jobs. Leasing by priority alone let that backlog outrank
            // every other module in its lane, and the status page recounted a million rows on each request. Workers now
            // take turns between modules (ix_jobs_fair), and the counts every page shows are kept by triggers, so reading
            // them is a lookup in a table of a few dozen rows. The seeding statements read the jobs table once.
            new Migration(ModuleId, 3, "fair leasing index, job counters kept by triggers", """
CREATE INDEX ix_jobs_fair ON jobs(lane, state, module_id, priority DESC, not_before_ms, job_id);
DROP INDEX ix_jobs_ready;
CREATE INDEX ix_jobs_state ON jobs(state, job_id);

CREATE TABLE job_counts(
  lane TEXT NOT NULL,
  module_id TEXT NOT NULL,
  state TEXT NOT NULL,
  n INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(lane, module_id, state)
) WITHOUT ROWID;
INSERT INTO job_counts(lane, module_id, state, n)
  SELECT lane, module_id, state, COUNT(*) FROM jobs GROUP BY lane, module_id, state;

CREATE TABLE job_module_last(
  module_id TEXT PRIMARY KEY,
  last_ok_ms INTEGER,
  last_fail_ms INTEGER,
  last_error TEXT
) WITHOUT ROWID;
INSERT INTO job_module_last(module_id, last_ok_ms, last_fail_ms)
  SELECT module_id, MAX(CASE WHEN state='succeeded' THEN finished_at_ms END), MAX(CASE WHEN state='dead' THEN finished_at_ms END)
  FROM jobs GROUP BY module_id;
UPDATE job_module_last SET last_error = (
  SELECT j.error_message FROM jobs j WHERE j.module_id = job_module_last.module_id AND j.state = 'dead'
  ORDER BY j.finished_at_ms DESC LIMIT 1)
WHERE last_fail_ms IS NOT NULL;

CREATE TRIGGER trg_jobs_count_insert AFTER INSERT ON jobs BEGIN
  INSERT OR IGNORE INTO job_counts(lane, module_id, state, n) VALUES(NEW.lane, NEW.module_id, NEW.state, 0);
  UPDATE job_counts SET n = n + 1 WHERE lane = NEW.lane AND module_id = NEW.module_id AND state = NEW.state;
END;
CREATE TRIGGER trg_jobs_count_delete AFTER DELETE ON jobs BEGIN
  UPDATE job_counts SET n = n - 1 WHERE lane = OLD.lane AND module_id = OLD.module_id AND state = OLD.state;
END;
CREATE TRIGGER trg_jobs_count_state AFTER UPDATE OF state ON jobs WHEN OLD.state <> NEW.state BEGIN
  UPDATE job_counts SET n = n - 1 WHERE lane = OLD.lane AND module_id = OLD.module_id AND state = OLD.state;
  INSERT OR IGNORE INTO job_counts(lane, module_id, state, n) VALUES(NEW.lane, NEW.module_id, NEW.state, 0);
  UPDATE job_counts SET n = n + 1 WHERE lane = NEW.lane AND module_id = NEW.module_id AND state = NEW.state;
END;
CREATE TRIGGER trg_jobs_last_outcome AFTER UPDATE OF state ON jobs WHEN OLD.state <> NEW.state AND NEW.state IN ('succeeded', 'dead') BEGIN
  INSERT OR IGNORE INTO job_module_last(module_id) VALUES(NEW.module_id);
  UPDATE job_module_last SET
    last_ok_ms = CASE WHEN NEW.state = 'succeeded' THEN NEW.finished_at_ms ELSE last_ok_ms END,
    last_fail_ms = CASE WHEN NEW.state = 'dead' THEN NEW.finished_at_ms ELSE last_fail_ms END,
    last_error = CASE WHEN NEW.state = 'dead' THEN NEW.error_message ELSE last_error END
  WHERE module_id = NEW.module_id;
END;
"""),
            // How much of a call's prompt the provider had been sent before (a later question over the same evidence):
            // what its cached tokens are measured against. Zero for every call made before this column existed.
            new Migration(ModuleId, 4, "llm_calls.resent_tokens", """
ALTER TABLE llm_calls ADD COLUMN resent_tokens INTEGER NOT NULL DEFAULT 0;
"""),
            // The Jobs page lists one module's jobs, newest first. The only index on the module was (module_id, state), which
            // cannot hand rows back in job order across states, so every such listing read and sorted all of the module's jobs:
            // for projections, the media store and captions the request was still open after 60 seconds (20 Nov 2026).
            // module_problems: what a part of the engine reports as standing in its way (Kernel.ModuleProblems).
            new Migration(ModuleId, 5, "job listings by module, and by module and kind, in job order; standing problems", """
CREATE INDEX ix_jobs_module ON jobs(module_id, job_id);
CREATE INDEX ix_jobs_module_kind ON jobs(module_id, kind, job_id);

CREATE TABLE module_problems(
  module_id TEXT NOT NULL,
  problem TEXT NOT NULL,
  severity TEXT NOT NULL,
  detail_json TEXT NOT NULL,
  since_ms INTEGER NOT NULL,
  last_ms INTEGER NOT NULL,
  PRIMARY KEY(module_id, problem)
) WITHOUT ROWID;
"""),
            // How long the runs of each kind of job take (Kernel.Jobs.LaneWorkers): what keeps the long kinds off a lane's
            // express worker. Learned as jobs run; kept here so that it is known from the first job after a restart, when
            // an express worker would otherwise take each long kind once more (a nightly backup: five hours).
            // It starts with what the server showed on 21 and 22 Nov 2026 (updated_at_ms 0 marks those rows), so the very
            // first start knows it too; every figure is replaced by what the lanes measure from then on.
            new Migration(ModuleId, 6, "how long each kind of job takes", """
CREATE TABLE job_kind_pace(
  module_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  typical_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  PRIMARY KEY(module_id, kind)
) WITHOUT ROWID;
INSERT INTO job_kind_pace(module_id, kind, typical_ms, updated_at_ms) VALUES
  ('datacontrols', 'backup', 18000000, 0),
  ('explore', 'mirror', 240000, 0),
  ('rollups', 'drain', 300000, 0),
  ('projections', 'daily', 1800000, 0),
  ('ingest.discord_bot', 'backfill', 800000, 0),
  ('knowledge.deduction', 'extract', 600000, 0);
"""),
        };
    }
}
