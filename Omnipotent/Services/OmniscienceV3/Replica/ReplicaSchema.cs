using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica module's own intel.db migration line (module id <c>replica</c>). Everything here is derived and
    /// re-derivable from corpus.db + operator.db: deleting it costs compute, never data. Replica chat transcripts are
    /// Klives-authored and live in operator.db (<c>replica_chats</c>/<c>replica_chat_messages</c>, frozen schema).
    /// <para>
    /// Training is versioned (F268/F269): every build writes its own rows keyed by <c>version_id</c>; the person row
    /// points at the serving version and is swapped atomically when a new build has finished every stage, so a
    /// retrain never disables chat and a failed build never damages the version that is serving.
    /// </para>
    /// </summary>
    public static class ReplicaSchema
    {
        public const string ModuleId = "replica";

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "replica versions, index, persona, evaluation, generations", """
CREATE TABLE replica_persons(
  person_id TEXT PRIMARY KEY,
  display_name TEXT,
  serving_version_id TEXT,
  building_version_id TEXT,
  status TEXT NOT NULL,
  status_detail TEXT,
  stale INTEGER NOT NULL DEFAULT 0,
  stale_since_ms INTEGER,
  stale_reason TEXT,
  served_source_seq INTEGER NOT NULL DEFAULT 0,
  new_authored INTEGER NOT NULL DEFAULT 0,
  readiness_json TEXT,
  readiness_at_ms INTEGER,
  auto_retrain INTEGER NOT NULL DEFAULT 1,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_replica_persons_status ON replica_persons(status);

CREATE TABLE replica_versions(
  version_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  number INTEGER NOT NULL,
  state TEXT NOT NULL,
  reason TEXT NOT NULL,
  requested_by TEXT NOT NULL,
  module_version INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL,
  started_at_ms INTEGER,
  finished_at_ms INTEGER,
  source_seq INTEGER NOT NULL DEFAULT 0,
  data_from_ms INTEGER,
  data_to_ms INTEGER,
  adult_from_ms INTEGER,
  encoder_model TEXT,
  window_tokens INTEGER,
  stages_json TEXT NOT NULL,
  counts_json TEXT,
  config_json TEXT,
  metrics_json TEXT,
  degraded_json TEXT,
  style_json TEXT,
  error TEXT,
  UNIQUE(person_id, number)
);
CREATE INDEX ix_replica_versions_state ON replica_versions(state);

CREATE TABLE replica_pairs(
  version_id TEXT NOT NULL,
  pair_id TEXT NOT NULL,
  person_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  guild_id TEXT,
  platform TEXT NOT NULL,
  context_kind TEXT NOT NULL,
  partner_person_id TEXT,
  partner_is_self INTEGER NOT NULL DEFAULT 0,
  addressee_source TEXT NOT NULL,
  is_initiation INTEGER NOT NULL,
  explicit_reply INTEGER NOT NULL,
  reply_at_ms INTEGER NOT NULL,
  reply_end_ms INTEGER NOT NULL,
  stimulus_at_ms INTEGER,
  latency_ms INTEGER,
  reply_count INTEGER NOT NULL,
  reply_chars INTEGER NOT NULL,
  reply_ids TEXT NOT NULL,
  stimulus_ids TEXT NOT NULL,
  context_ids TEXT NOT NULL,
  reply_text TEXT NOT NULL,
  stimulus_plain TEXT NOT NULL,
  rendered TEXT NOT NULL,
  text_hash TEXT NOT NULL,
  stimulus_hash TEXT NOT NULL,
  time_quality INTEGER NOT NULL,
  flags INTEGER NOT NULL DEFAULT 0,
  split TEXT NOT NULL,
  era_index INTEGER NOT NULL DEFAULT 0,
  slice_index INTEGER NOT NULL DEFAULT 0,
  reflex_class TEXT,
  source_seq INTEGER NOT NULL,
  PRIMARY KEY(version_id, pair_id)
);
CREATE INDEX ix_replica_pairs_time ON replica_pairs(version_id, reply_at_ms);
CREATE INDEX ix_replica_pairs_person ON replica_pairs(person_id, reply_at_ms);

CREATE TABLE replica_eras(
  version_id TEXT NOT NULL,
  era_index INTEGER NOT NULL,
  from_ms INTEGER NOT NULL,
  to_ms INTEGER NOT NULL,
  pairs INTEGER NOT NULL,
  summary_json TEXT NOT NULL,
  PRIMARY KEY(version_id, era_index)
);

CREATE TABLE replica_slices(
  version_id TEXT NOT NULL,
  slice_index INTEGER NOT NULL,
  from_ms INTEGER NOT NULL,
  to_ms INTEGER NOT NULL,
  pairs INTEGER NOT NULL,
  content_hash TEXT NOT NULL,
  state TEXT NOT NULL,
  detail TEXT,
  PRIMARY KEY(version_id, slice_index)
);

CREATE TABLE replica_persona_items(
  version_id TEXT NOT NULL,
  item_id TEXT NOT NULL,
  slice_index INTEGER NOT NULL,
  kind TEXT NOT NULL,
  subject TEXT,
  text TEXT NOT NULL,
  strength TEXT,
  first_ms INTEGER,
  last_ms INTEGER,
  visible_from_ms INTEGER NOT NULL,
  evidence_json TEXT NOT NULL,
  sensitive INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(version_id, item_id)
);
CREATE INDEX ix_replica_persona_visible ON replica_persona_items(version_id, visible_from_ms);

CREATE TABLE replica_slice_cache(
  person_id TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  prompt_version TEXT NOT NULL,
  items_json TEXT NOT NULL,
  dropped INTEGER NOT NULL DEFAULT 0,
  model TEXT,
  created_at_ms INTEGER NOT NULL,
  PRIMARY KEY(person_id, content_hash, prompt_version)
);

CREATE TABLE replica_reflex_labels(
  stimulus_hash TEXT NOT NULL,
  prompt_version TEXT NOT NULL,
  label TEXT NOT NULL,
  model TEXT,
  created_at_ms INTEGER NOT NULL,
  PRIMARY KEY(stimulus_hash, prompt_version)
);

CREATE TABLE replica_eval_items(
  version_id TEXT NOT NULL,
  pair_id TEXT NOT NULL,
  config_key TEXT NOT NULL,
  split TEXT NOT NULL,
  person_id TEXT NOT NULL,
  context_kind TEXT NOT NULL,
  occurred_ms INTEGER NOT NULL,
  as_of_ms INTEGER NOT NULL,
  generated_text TEXT,
  outcome TEXT NOT NULL,
  judge_correct INTEGER,
  judge_confidence REAL,
  embedding_sim REAL,
  style_distance REAL,
  length_error REAL,
  burst_error REAL,
  model TEXT,
  prompt_version TEXT,
  created_at_ms INTEGER NOT NULL,
  detail_json TEXT,
  PRIMARY KEY(version_id, pair_id, config_key)
);
CREATE INDEX ix_replica_eval_person ON replica_eval_items(person_id, created_at_ms);

CREATE TABLE replica_generations(
  generation_id TEXT PRIMARY KEY,
  source TEXT NOT NULL,
  chat_id TEXT,
  turn_id TEXT,
  run_id TEXT,
  person_id TEXT NOT NULL,
  version_id TEXT NOT NULL,
  as_of_ms INTEGER NOT NULL,
  context_kind TEXT NOT NULL,
  model TEXT,
  prompt_version TEXT,
  candidates INTEGER NOT NULL,
  chosen_index INTEGER,
  chosen_score REAL,
  outcome TEXT NOT NULL,
  latency_ms INTEGER,
  prompt_tokens INTEGER,
  cached_tokens INTEGER,
  completion_tokens INTEGER,
  coverage_json TEXT,
  candidates_json TEXT,
  created_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_replica_generations_person ON replica_generations(person_id, created_at_ms);
CREATE INDEX ix_replica_generations_chat ON replica_generations(chat_id, created_at_ms) WHERE chat_id IS NOT NULL;
CREATE INDEX ix_replica_generations_time ON replica_generations(created_at_ms);

CREATE TABLE replica_chat_memory(
  chat_id TEXT PRIMARY KEY,
  through_seq INTEGER NOT NULL,
  summary TEXT NOT NULL,
  model TEXT,
  prompt_version TEXT,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE replica_models(
  model_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  version INTEGER NOT NULL,
  state TEXT NOT NULL,
  params_json TEXT,
  metrics_json TEXT,
  detail TEXT,
  trained_at_ms INTEGER,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE replica_blind_sessions(
  session_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  version_id TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  items_json TEXT NOT NULL,
  answered INTEGER NOT NULL DEFAULT 0,
  correct INTEGER NOT NULL DEFAULT 0,
  finished_at_ms INTEGER
);
CREATE INDEX ix_replica_blind_person ON replica_blind_sessions(person_id, created_at_ms);
"""),
        };
    }
}
