using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>
    /// The intel.db tables of the knowledge workstream, one migration line per module (frozen contract R7: modules own
    /// their tables). Every derived row records <c>source_seq</c> (the corpus change-log watermark it reflects) and the
    /// D2 dimensions it is filtered by. The bitemporal assertion and evidence tables themselves are the kernel's
    /// (<see cref="Schemas.IntelSchema"/>); <c>kn_assertion_dims</c> carries their dimensions.
    /// </summary>
    public static class KnowledgeSchema
    {
        public const string DeductionModule = "knowledge.deduction";
        public const string IdentityModule = "knowledge.identity";
        public const string TimelineModule = "knowledge.timeline";
        public const string PlansModule = "knowledge.plans";
        public const string QualityModule = "knowledge.quality";
        public const string InquiryModule = "knowledge.inquiry";

        public static IReadOnlyList<Migration> Deduction { get; } = new[]
        {
            new Migration(DeductionModule, 1, "knowledge store dimensions, coverage, claims, age evidence, review, hypotheses, calibration, aliases, entities", """
CREATE INDEX ix_kn_assertions_subject_recorded ON assertions(subject_kind, subject_id, recorded_at_ms);

CREATE TABLE kn_assertion_dims(
  assertion_id TEXT PRIMARY KEY,
  natural_key TEXT NOT NULL,
  producer TEXT NOT NULL,
  category TEXT NOT NULL,
  attribute TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  object_id TEXT,
  author_person_id TEXT,
  occurred_at_ms INTEGER NOT NULL,
  first_evidence_at_ms INTEGER,
  recorded_at_ms INTEGER NOT NULL,
  valid_from_ms INTEGER,
  valid_to_ms INTEGER,
  platform TEXT,
  source_id TEXT,
  guild_id TEXT,
  conversation_id TEXT,
  conversation_kind TEXT,
  status TEXT NOT NULL,
  modality TEXT,
  source_kind TEXT,
  probability REAL,
  evidence_count INTEGER NOT NULL,
  independent_count INTEGER NOT NULL,
  module_id TEXT NOT NULL,
  model TEXT,
  prompt_version TEXT,
  run_id TEXT,
  job_id INTEGER,
  content_hash TEXT NOT NULL,
  live INTEGER NOT NULL DEFAULT 1,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_dims_subject ON kn_assertion_dims(subject_id, producer, live);
CREATE INDEX ix_kn_dims_key ON kn_assertion_dims(natural_key, live);
CREATE INDEX ix_kn_dims_occurred ON kn_assertion_dims(occurred_at_ms);
CREATE INDEX ix_kn_dims_recorded ON kn_assertion_dims(recorded_at_ms, assertion_id);
CREATE INDEX ix_kn_dims_category ON kn_assertion_dims(category, status, live);

CREATE TABLE kn_person_state(
  person_id TEXT PRIMARY KEY,
  tracked INTEGER NOT NULL,
  pending INTEGER NOT NULL DEFAULT 0,
  covered_messages INTEGER NOT NULL DEFAULT 0,
  last_extract_ms INTEGER,
  last_consolidate_ms INTEGER,
  last_detective_ms INTEGER,
  prompt_version TEXT,
  last_error TEXT,
  updated_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_kn_person_state_pending ON kn_person_state(pending, tracked);

CREATE TABLE kn_coverage(
  person_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  message_version INTEGER NOT NULL,
  prompt_version TEXT NOT NULL,
  run_id TEXT NOT NULL,
  outcome TEXT NOT NULL DEFAULT 'ok',
  covered_at_ms INTEGER NOT NULL,
  PRIMARY KEY(person_id, message_id)
);

CREATE TABLE kn_extraction_runs(
  run_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  job_id INTEGER,
  prompt_version TEXT NOT NULL,
  model TEXT,
  slices INTEGER NOT NULL DEFAULT 0,
  messages INTEGER NOT NULL DEFAULT 0,
  claims_raw INTEGER NOT NULL DEFAULT 0,
  claims_agreed INTEGER NOT NULL DEFAULT 0,
  claims_verified INTEGER NOT NULL DEFAULT 0,
  claims_rejected INTEGER NOT NULL DEFAULT 0,
  rejections_json TEXT,
  llm_calls INTEGER NOT NULL DEFAULT 0,
  outcome TEXT NOT NULL,
  error TEXT,
  started_at_ms INTEGER NOT NULL,
  finished_at_ms INTEGER
);
CREATE INDEX ix_kn_runs_person ON kn_extraction_runs(person_id, started_at_ms);
CREATE INDEX ix_kn_runs_started ON kn_extraction_runs(started_at_ms, run_id);

CREATE TABLE kn_claims(
  claim_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  person_id TEXT NOT NULL,
  slice_index INTEGER NOT NULL,
  attribute TEXT NOT NULL,
  category TEXT NOT NULL,
  value_text TEXT,
  value_norm TEXT,
  object_id TEXT,
  polarity TEXT,
  modality TEXT,
  source_kind TEXT,
  speech_act TEXT,
  sincerity TEXT,
  name_kind TEXT,
  probability REAL,
  time_json TEXT,
  detail_json TEXT,
  evidence_json TEXT NOT NULL,
  agreement TEXT NOT NULL,
  verdict TEXT NOT NULL,
  reason TEXT,
  critic TEXT,
  asserted_at_ms INTEGER NOT NULL,
  recorded_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL,
  prompt_version TEXT NOT NULL,
  model TEXT,
  conversation_id TEXT,
  guild_id TEXT,
  platform TEXT,
  author_person_id TEXT,
  superseded INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_kn_claims_person ON kn_claims(person_id, verdict, superseded);
CREATE INDEX ix_kn_claims_run ON kn_claims(run_id);
CREATE INDEX ix_kn_claims_recorded ON kn_claims(recorded_at_ms, claim_id);
CREATE TABLE kn_claim_messages(
  claim_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  PRIMARY KEY(claim_id, message_id)
);
CREATE INDEX ix_kn_claim_messages_message ON kn_claim_messages(message_id);

CREATE TABLE kn_age_evidence(
  evidence_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  birth_earliest_ms INTEGER,
  birth_latest_ms INTEGER,
  month INTEGER,
  day INTEGER,
  source_kind TEXT NOT NULL,
  asserted_at_ms INTEGER NOT NULL,
  message_id TEXT NOT NULL,
  message_version INTEGER NOT NULL,
  claim_id TEXT,
  probability REAL NOT NULL,
  status TEXT NOT NULL,
  natural_key TEXT NOT NULL,
  recorded_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_age_person ON kn_age_evidence(person_id, status);

CREATE TABLE kn_review_items(
  item_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  subject_id TEXT,
  natural_key TEXT,
  title TEXT NOT NULL,
  detail_json TEXT,
  priority REAL NOT NULL,
  state TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  resolved_at_ms INTEGER,
  resolution TEXT,
  actor TEXT,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_review_open ON kn_review_items(state, priority DESC, item_id);
CREATE INDEX ix_kn_review_subject ON kn_review_items(subject_id, state);
CREATE INDEX ix_kn_review_kind ON kn_review_items(kind, state, created_at_ms);

CREATE TABLE kn_hypotheses(
  hypothesis_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  statement TEXT NOT NULL,
  attribute TEXT,
  value_text TEXT,
  derived_from_json TEXT NOT NULL,
  confirm_queries_json TEXT NOT NULL,
  refute_queries_json TEXT NOT NULL,
  probability REAL NOT NULL,
  status TEXT NOT NULL,
  evidence_json TEXT,
  created_at_ms INTEGER NOT NULL,
  expires_at_ms INTEGER NOT NULL,
  resolved_at_ms INTEGER,
  resolution_note TEXT,
  last_checked_seq INTEGER NOT NULL DEFAULT 0,
  prompt_version TEXT,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_hyp_person ON kn_hypotheses(person_id, status);
CREATE INDEX ix_kn_hyp_status ON kn_hypotheses(status, expires_at_ms);

CREATE TABLE kn_calibration(
  scope TEXT PRIMARY KEY,
  method TEXT NOT NULL,
  points_json TEXT NOT NULL,
  n_gold INTEGER NOT NULL,
  base_rate REAL,
  fitted_at_ms INTEGER NOT NULL
);

CREATE TABLE kn_aliases(
  alias_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  alias TEXT NOT NULL,
  alias_norm TEXT NOT NULL,
  name_kind TEXT NOT NULL,
  scope_kind TEXT NOT NULL,
  scope_id TEXT,
  valid_from_ms INTEGER NOT NULL,
  valid_to_ms INTEGER,
  first_seen_ms INTEGER NOT NULL,
  last_seen_ms INTEGER NOT NULL,
  usages INTEGER NOT NULL,
  speakers INTEGER NOT NULL,
  self_claims INTEGER NOT NULL,
  probability REAL NOT NULL,
  status TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  common_word INTEGER NOT NULL DEFAULT 0,
  updated_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_aliases_person ON kn_aliases(person_id, status);
CREATE INDEX ix_kn_aliases_norm ON kn_aliases(alias_norm, status);

CREATE TABLE kn_entities(
  entity_id TEXT PRIMARY KEY,
  owner_person_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  name TEXT NOT NULL,
  name_norm TEXT NOT NULL,
  attributes_json TEXT NOT NULL,
  first_evidence_ms INTEGER NOT NULL,
  last_evidence_ms INTEGER NOT NULL,
  mentions INTEGER NOT NULL,
  evidence_json TEXT NOT NULL,
  status TEXT NOT NULL,
  merged_into TEXT,
  updated_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_entities_owner ON kn_entities(owner_person_id, status);

CREATE TABLE kn_value_groups(
  attribute TEXT NOT NULL,
  value_norm TEXT NOT NULL,
  canonical_norm TEXT NOT NULL,
  canonical_text TEXT NOT NULL,
  decided_at_ms INTEGER NOT NULL,
  model TEXT,
  prompt_version TEXT,
  PRIMARY KEY(attribute, value_norm)
);
"""),
            // What is waiting to be read about each tracked person (Knowledge.Evidence.EvidenceQueue). An extraction job used
            // to work this out afresh every time, from every message the person ever wrote: for the three busiest people
            // that never finished, and the one worker of the model lane was held by it for a day (21 Nov 2026).
            // kn_evidence_queue: the unread anchors, oldest first. kn_evidence_state: how far each person's history has
            // been looked through. kn_evidence_convs: where each person writes (names are looked for there only).
            // kn_evidence_cursor: how far the corpus change log has been routed into the queues.
            new Migration(DeductionModule, 2, "evidence queue: unread anchors per person, history cursors, change-log routing", """
CREATE TABLE kn_evidence_queue(
  person_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  version INTEGER NOT NULL,
  sent_at_ms INTEGER NOT NULL,
  order_key INTEGER NOT NULL,
  ingest_seq INTEGER NOT NULL,
  conversation_id TEXT NOT NULL,
  relation INTEGER NOT NULL,
  queued_at_ms INTEGER NOT NULL,
  PRIMARY KEY(person_id, message_id)
) WITHOUT ROWID;
CREATE INDEX ix_kn_evidence_queue_order ON kn_evidence_queue(person_id, sent_at_ms, order_key, ingest_seq, message_id);

CREATE TABLE kn_evidence_state(
  person_id TEXT PRIMARY KEY,
  prompt_version TEXT NOT NULL,
  complete INTEGER NOT NULL DEFAULT 0,
  state_json TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE kn_evidence_convs(
  person_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  PRIMARY KEY(person_id, conversation_id)
) WITHOUT ROWID;
CREATE INDEX ix_kn_evidence_convs_conversation ON kn_evidence_convs(conversation_id);

CREATE TABLE kn_evidence_cursor(
  name TEXT PRIMARY KEY,
  seq INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) WITHOUT ROWID;

-- An edited or erased message has its coverage removed, and its age evidence retracted, by message id alone.
CREATE INDEX ix_kn_coverage_message ON kn_coverage(message_id);
CREATE INDEX ix_kn_age_message ON kn_age_evidence(message_id);
"""),
            // Until 22 Nov 2026 a fact whose messages named no time was stored as holding from zero to zero, which is
            // 1 January 1970: every such event, plan and promise, and every such fixed fact (Consolidator.FirstStated has
            // the cause). No real fact holds from that exact millisecond, so the rows are put right where they stand:
            // no date, and an event's place in time is its first evidence, as it would have been. The people whose
            // facts were touched are marked for their views (profile, timeline, plans) to be built again; the mark is
            // made first, while the rows can still be told apart.
            new Migration(DeductionModule, 3, "facts dated 1 January 1970 for want of a date: no date, and their people's views rebuilt", $"""
INSERT OR IGNORE INTO module_dirty(module_id, subject_kind, subject_id, dirty_since_seq, dirty_at_ms)
SELECT '{DeductionModule}', '{ViewsDirtyKind}', subject_id, 0, CAST(strftime('%s','now') AS INTEGER) * 1000
FROM assertions WHERE subject_kind='person' AND valid_from_earliest_ms=0 AND valid_from_latest_ms=0 GROUP BY subject_id;

UPDATE assertions SET valid_from_earliest_ms=NULL, valid_from_latest_ms=NULL WHERE valid_from_earliest_ms=0 AND valid_from_latest_ms=0;
UPDATE kn_assertion_dims SET valid_from_ms=NULL WHERE valid_from_ms=0;
UPDATE kn_assertion_dims SET occurred_at_ms=COALESCE(first_evidence_at_ms, recorded_at_ms) WHERE occurred_at_ms=0;
"""),
            // Until 22 Nov 2026 a fact's text was stored as the model wrote it, roster handles included ("meet with p9a05
            // to understand the project system"; "p9a05" alone as somebody's friend). Whose a handle is has to be worked
            // out from the people and the conversation of each claim, which is not a statement's work: this only asks
            // for the job that does it (Deduction.HandleRepair), once.
            new Migration(DeductionModule, 4, "the repair of roster handles in stored claims and facts is asked for", $"""
INSERT OR IGNORE INTO module_dirty(module_id, subject_kind, subject_id, dirty_since_seq, dirty_at_ms)
VALUES('{DeductionModule}', '{HandlesDirtyKind}', 'all', 0, CAST(strftime('%s','now') AS INTEGER) * 1000);
"""),
        };

        /// <summary>The dirty kind that asks for a person's views to be built again from the facts as they are stored
        /// (<see cref="Deduction.DeductionModule.ViewsKind"/>).</summary>
        public const string ViewsDirtyKind = "views";

        /// <summary>The dirty kind that starts the repair of roster handles in stored claims and facts
        /// (<see cref="Deduction.DeductionModule.HandlesKind"/>).</summary>
        public const string HandlesDirtyKind = "handles";

        public static IReadOnlyList<Migration> Identity { get; } = new[]
        {
            new Migration(IdentityModule, 1, "identity-link suggestions and scan cursors", """
CREATE TABLE kn_identity_links(
  link_id TEXT PRIMARY KEY,
  identity_a TEXT NOT NULL,
  identity_b TEXT NOT NULL,
  person_a TEXT NOT NULL,
  person_b TEXT NOT NULL,
  signals_json TEXT NOT NULL,
  independent_signals INTEGER NOT NULL,
  probability REAL NOT NULL,
  verdict TEXT NOT NULL,
  judge_json TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  decided_at_ms INTEGER,
  merge_id INTEGER,
  actor TEXT,
  source_seq INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_kn_links_pair ON kn_identity_links(identity_a, identity_b);
CREATE INDEX ix_kn_links_verdict ON kn_identity_links(verdict, probability DESC, link_id);
CREATE INDEX ix_kn_links_person_a ON kn_identity_links(person_a);
CREATE INDEX ix_kn_links_person_b ON kn_identity_links(person_b);
CREATE TABLE kn_identity_cursors(
  name TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
"""),
        };

        public static IReadOnlyList<Migration> Timeline { get; } = new[]
        {
            new Migration(TimelineModule, 1, "life events", """
CREATE TABLE kn_life_events(
  event_id TEXT PRIMARY KEY,
  person_id TEXT NOT NULL,
  category TEXT NOT NULL,
  title TEXT NOT NULL,
  attribute TEXT NOT NULL,
  assertion_id TEXT,
  occurred_earliest_ms INTEGER,
  occurred_latest_ms INTEGER,
  occurred_mid_ms INTEGER,
  precision TEXT NOT NULL,
  probability REAL NOT NULL,
  status TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  guild_id TEXT,
  conversation_id TEXT,
  platform TEXT,
  recorded_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_life_person ON kn_life_events(person_id, status, occurred_mid_ms);
CREATE INDEX ix_kn_life_time ON kn_life_events(occurred_mid_ms, event_id);
"""),
        };

        public static IReadOnlyList<Migration> Plans { get; } = new[]
        {
            new Migration(PlansModule, 1, "plans, promises, dates and reminders", """
CREATE TABLE kn_plans(
  plan_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  subtype TEXT,
  owner_person_id TEXT NOT NULL,
  counterparty_person_id TEXT,
  involves_klives INTEGER NOT NULL,
  klives_promised INTEGER NOT NULL,
  title TEXT NOT NULL,
  scheduled_earliest_ms INTEGER,
  scheduled_latest_ms INTEGER,
  local_zone TEXT,
  all_day INTEGER NOT NULL,
  firmness TEXT NOT NULL,
  status TEXT NOT NULL,
  recurrence TEXT,
  month INTEGER,
  day INTEGER,
  year INTEGER,
  superseded_by TEXT,
  assertion_id TEXT,
  natural_key TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  conversation_id TEXT,
  guild_id TEXT,
  platform TEXT,
  minor_restricted INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_plans_owner ON kn_plans(owner_person_id, status);
CREATE INDEX ix_kn_plans_time ON kn_plans(scheduled_earliest_ms, plan_id);
CREATE INDEX ix_kn_plans_key ON kn_plans(natural_key);
CREATE TABLE kn_reminders(
  reminder_id TEXT PRIMARY KEY,
  plan_id TEXT NOT NULL,
  occurrence_ms INTEGER NOT NULL,
  fire_at_ms INTEGER NOT NULL,
  kind TEXT NOT NULL,
  state TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  fired_at_ms INTEGER
);
CREATE INDEX ix_kn_reminders_due ON kn_reminders(state, fire_at_ms);
CREATE INDEX ix_kn_reminders_plan ON kn_reminders(plan_id);
"""),
            // said_at_ms: when the plan was first said (its earliest evidence, read from the fact's own row: this module
            // depends on the deduction module, whose tables are there before these statements run). Most plans in chat
            // name no date ("let's play", "I'll send it"); when they were said is then the one time there is to show
            // and to sort them by.
            // The second part undoes what the store's 1970 dates (deduction migration 3) left here: a plan "scheduled"
            // for 1 January 1970 has no date, was never past or overdue on that account, and has no reminder to fire.
            new Migration(PlansModule, 2, "when a plan was said; plans dated 1 January 1970 have no date", """
ALTER TABLE kn_plans ADD COLUMN said_at_ms INTEGER;
UPDATE kn_plans SET said_at_ms=(SELECT d.first_evidence_at_ms FROM kn_assertion_dims d WHERE d.assertion_id=kn_plans.assertion_id);
UPDATE kn_plans SET scheduled_earliest_ms=NULL, scheduled_latest_ms=NULL,
    status=CASE WHEN status IN ('past','overdue','due','upcoming') THEN 'undated' ELSE status END
WHERE scheduled_earliest_ms=0 AND scheduled_latest_ms=0;
UPDATE kn_reminders SET state='cancelled' WHERE occurrence_ms=0 AND state='pending';
DROP INDEX ix_kn_plans_time;
CREATE INDEX ix_kn_plans_time ON kn_plans(COALESCE(scheduled_earliest_ms, said_at_ms, 0), plan_id);
"""),
        };

        public static IReadOnlyList<Migration> Quality { get; } = new[]
        {
            new Migration(QualityModule, 1, "quality lab runs, metrics and regression-gate decisions", """
CREATE TABLE kn_quality_runs(
  run_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  subject TEXT NOT NULL,
  prompt_version TEXT,
  model TEXT,
  baseline_run_id TEXT,
  n_items INTEGER NOT NULL,
  metrics_json TEXT NOT NULL,
  as_of_ms INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_kn_qruns_subject ON kn_quality_runs(subject, created_at_ms);
CREATE TABLE kn_quality_metrics(
  run_id TEXT NOT NULL,
  scope TEXT NOT NULL,
  metric TEXT NOT NULL,
  value REAL,
  ci_low REAL,
  ci_high REAL,
  n INTEGER NOT NULL,
  prompt_version TEXT,
  model TEXT,
  created_at_ms INTEGER NOT NULL,
  PRIMARY KEY(run_id, scope, metric)
);
CREATE INDEX ix_kn_qmetrics_scope ON kn_quality_metrics(scope, metric, created_at_ms);
CREATE TABLE kn_gate_decisions(
  gate_id TEXT PRIMARY KEY,
  subject TEXT NOT NULL,
  candidate TEXT NOT NULL,
  baseline TEXT,
  decision TEXT NOT NULL,
  reasons_json TEXT NOT NULL,
  run_id TEXT,
  decided_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_kn_gate_subject ON kn_gate_decisions(subject, candidate, decided_at_ms);
"""),
        };

        public static IReadOnlyList<Migration> Inquiry { get; } = new[]
        {
            new Migration(InquiryModule, 1, "inquiries and standing-question state", """
CREATE TABLE kn_inquiries(
  inquiry_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  tactic_key TEXT NOT NULL,
  question TEXT NOT NULL,
  restatement TEXT,
  answer_type TEXT NOT NULL,
  subjects_json TEXT NOT NULL,
  primary_subject_id TEXT,
  as_of_ms INTEGER NOT NULL,
  verdict TEXT,
  probability_raw REAL,
  probability REAL,
  band_low REAL,
  band_high REAL,
  evidence_strength INTEGER,
  evidence_count INTEGER,
  report_json TEXT,
  assertion_id TEXT,
  standing_id TEXT,
  feedback TEXT,
  feedback_at_ms INTEGER,
  model TEXT,
  prompt_version TEXT,
  state TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  finished_at_ms INTEGER,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_kn_inq_created ON kn_inquiries(created_at_ms, inquiry_id);
CREATE INDEX ix_kn_inq_subject ON kn_inquiries(primary_subject_id, created_at_ms);
CREATE INDEX ix_kn_inq_standing ON kn_inquiries(standing_id, created_at_ms);
CREATE INDEX ix_kn_inq_run ON kn_inquiries(run_id);
CREATE TABLE kn_standing_state(
  question_id TEXT PRIMARY KEY,
  last_seq INTEGER NOT NULL,
  last_inquiry_id TEXT,
  last_run_id TEXT,
  last_verdict TEXT,
  last_probability REAL,
  pending INTEGER NOT NULL DEFAULT 0,
  pending_since_ms INTEGER,
  budget_day TEXT,
  budget_used INTEGER NOT NULL DEFAULT 0,
  updated_at_ms INTEGER NOT NULL
);
"""),
        };

        /// <summary>The intel.db table (or view) behind each knowledge dataset, for the Explore mirror (WS-C).</summary>
        public static readonly IReadOnlyDictionary<string, string> DatasetTables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["knowledge.assertions"] = "kn_assertion_dims",
            ["knowledge.claims"] = "kn_claims",
            ["knowledge.review"] = "kn_review_items",
            ["knowledge.hypotheses"] = "kn_hypotheses",
            ["knowledge.aliases"] = "kn_aliases",
            ["knowledge.identity_links"] = "kn_identity_links",
            ["knowledge.life_events"] = "kn_life_events",
            ["knowledge.plans"] = "kn_plans",
            ["knowledge.quality_metrics"] = "kn_quality_metrics",
            ["knowledge.inquiries"] = "kn_inquiries",
        };

        /// <summary>
        /// Where the Explore mirror (WS-C) copies each knowledge dataset from: table, key, the row's time, and the column that
        /// grows when a row changes (null: small tables copied whole). The Quality Lab's metrics have a composite key and are
        /// not mirrored.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, Kernel.Explore.ExploreSource> Sources = new Dictionary<string, Kernel.Explore.ExploreSource>(StringComparer.Ordinal)
        {
            ["knowledge.assertions"] = new("knowledge.assertions", "kn_assertion_dims", "assertion_id", "occurred_at_ms", "recorded_at_ms"),
            ["knowledge.claims"] = new("knowledge.claims", "kn_claims", "claim_id", "asserted_at_ms", "recorded_at_ms"),
            ["knowledge.review"] = new("knowledge.review", "kn_review_items", "item_id", "created_at_ms", "updated_at_ms"),
            ["knowledge.hypotheses"] = new("knowledge.hypotheses", "kn_hypotheses", "hypothesis_id", "created_at_ms"),
            ["knowledge.aliases"] = new("knowledge.aliases", "kn_aliases", "alias_id", "last_seen_ms", "updated_at_ms"),
            ["knowledge.identity_links"] = new("knowledge.identity_links", "kn_identity_links", "link_id", "created_at_ms", "updated_at_ms"),
            ["knowledge.life_events"] = new("knowledge.life_events", "kn_life_events", "event_id", "recorded_at_ms", "updated_at_ms"),
            ["knowledge.plans"] = new("knowledge.plans", "kn_plans", "plan_id", "created_at_ms", "updated_at_ms"),
            ["knowledge.inquiries"] = new("knowledge.inquiries", "kn_inquiries", "inquiry_id", "created_at_ms"),
        };

        /// <summary>The Explore sources of the datasets a module declares (integration, Phase 3).</summary>
        public static IReadOnlyList<Kernel.Explore.ExploreSource> ExploreSourcesOf(IEnumerable<DatasetDescriptor> datasets) =>
            datasets.Select(d => Sources.TryGetValue(d.Id, out var s) ? s : null).Where(s => s != null).Select(s => s!).ToList();

        private static DimensionBinding B(string dim, string col) => new(dim, col);
        private static MeasureDescriptor Count(string title) => new("count", title, "count", null);

        public static DatasetDescriptor AssertionsDataset => new("knowledge.assertions", "Facts and inferences",
            new[]
            {
                B("time.occurred", "occurred_at_ms"), B("time.recorded", "recorded_at_ms"), B("time.valid_from", "valid_from_ms"), B("time.valid_to", "valid_to_ms"),
                B("person.subject", "subject_id"), B("person.author", "author_person_id"), B("person.partner", "object_id"),
                B("where.platform", "platform"), B("where.source", "source_id"), B("where.guild", "guild_id"), B("where.conversation", "conversation_id"), B("where.kind", "conversation_kind"),
                B("content.modality", "modality"), B("knowledge.attribute", "attribute"), B("knowledge.category", "category"), B("prov.source_kind", "source_kind"),
                B("prov.module", "module_id"), B("prov.model", "model"), B("prov.prompt_version", "prompt_version"), B("prov.run", "run_id"),
                B("prov.confidence", "probability"), B("prov.status", "status"),
            },
            new[] { Count("Assertions"), new MeasureDescriptor("subjects", "People", "distinct", "subject_id"), new MeasureDescriptor("probability", "Mean probability", "avg", "probability"),
                    new MeasureDescriptor("evidence", "Evidence messages", "sum", "evidence_count") },
            "assertion", "omniscience.persons.read", true);

        public static DatasetDescriptor ClaimsDataset => new("knowledge.claims", "Extracted claims (incl. rejected)",
            new[]
            {
                B("time.occurred", "asserted_at_ms"), B("time.recorded", "recorded_at_ms"), B("person.subject", "person_id"), B("person.author", "author_person_id"),
                B("where.platform", "platform"), B("where.guild", "guild_id"), B("where.conversation", "conversation_id"),
                B("content.modality", "modality"), B("knowledge.attribute", "attribute"), B("knowledge.category", "category"), B("prov.source_kind", "source_kind"),
                B("prov.model", "model"), B("prov.prompt_version", "prompt_version"), B("prov.run", "run_id"), B("prov.confidence", "probability"), B("prov.status", "verdict"),
            },
            new[] { Count("Claims"), new MeasureDescriptor("probability", "Mean probability", "avg", "probability") },
            "claim", "omniscience.persons.read", true);

        public static DatasetDescriptor ReviewDataset => new("knowledge.review", "Review queue",
            new[] { B("time.occurred", "created_at_ms"), B("time.recorded", "updated_at_ms"), B("person.subject", "subject_id"), B("prov.status", "state"), B("knowledge.category", "kind"), B("prov.confidence", "priority") },
            new[] { Count("Items") }, "review_item", "omniscience.persons.read", true);

        public static DatasetDescriptor HypothesesDataset => new("knowledge.hypotheses", "Hypotheses",
            new[] { B("time.occurred", "created_at_ms"), B("time.recorded", "resolved_at_ms"), B("person.subject", "person_id"), B("prov.status", "status"), B("prov.confidence", "probability"),
                    B("knowledge.attribute", "attribute"), B("prov.prompt_version", "prompt_version") },
            new[] { Count("Hypotheses") }, "hypothesis", "omniscience.persons.read", true);

        public static DatasetDescriptor AliasesDataset => new("knowledge.aliases", "Aliases",
            new[] { B("time.valid_from", "valid_from_ms"), B("time.valid_to", "valid_to_ms"), B("time.occurred", "last_seen_ms"), B("person.subject", "person_id"),
                    B("where.guild", "scope_id"), B("prov.status", "status"), B("prov.confidence", "probability"), B("knowledge.category", "name_kind") },
            new[] { Count("Aliases"), new MeasureDescriptor("usages", "Usages", "sum", "usages") }, "alias", "omniscience.persons.read", true);

        public static DatasetDescriptor IdentityLinksDataset => new("knowledge.identity_links", "Same-person suggestions",
            new[] { B("time.occurred", "created_at_ms"), B("time.recorded", "updated_at_ms"), B("person.subject", "person_a"), B("person.partner", "person_b"),
                    B("prov.status", "verdict"), B("prov.confidence", "probability") },
            new[] { Count("Suggestions") }, "identity_link", "omniscience.persons.read", true);

        public static DatasetDescriptor LifeEventsDataset => new("knowledge.life_events", "Life events",
            new[] { B("time.occurred", "occurred_mid_ms"), B("time.recorded", "recorded_at_ms"), B("person.subject", "person_id"), B("knowledge.category", "category"),
                    B("knowledge.attribute", "attribute"), B("where.guild", "guild_id"), B("where.conversation", "conversation_id"), B("where.platform", "platform"),
                    B("prov.status", "status"), B("prov.confidence", "probability") },
            new[] { Count("Events") }, "life_event", "omniscience.persons.read", true);

        public static DatasetDescriptor PlansDataset => new("knowledge.plans", "Plans, promises and dates",
            new[] { B("time.occurred", "scheduled_earliest_ms"), B("time.recorded", "created_at_ms"), B("person.subject", "owner_person_id"), B("person.partner", "counterparty_person_id"),
                    B("knowledge.category", "kind"), B("prov.status", "status"), B("where.conversation", "conversation_id"), B("where.guild", "guild_id"), B("where.platform", "platform") },
            new[] { Count("Plans") }, "plan", "omniscience.persons.read", true);

        public static DatasetDescriptor QualityDataset => new("knowledge.quality_metrics", "Quality Lab metrics",
            new[] { B("time.recorded", "created_at_ms"), B("knowledge.category", "scope"), B("prov.prompt_version", "prompt_version"), B("prov.model", "model"), B("prov.run", "run_id") },
            new[] { Count("Metrics"), new MeasureDescriptor("value", "Value", "avg", "value") }, "quality_metric", "omniscience.overview.view", false);

        public static DatasetDescriptor InquiriesDataset => new("knowledge.inquiries", "Inquiries",
            new[] { B("time.occurred", "created_at_ms"), B("time.recorded", "finished_at_ms"), B("person.subject", "primary_subject_id"), B("prov.status", "state"),
                    B("knowledge.category", "answer_type"), B("prov.confidence", "probability"), B("prov.model", "model"), B("prov.prompt_version", "prompt_version"), B("prov.run", "run_id") },
            new[] { Count("Inquiries"), new MeasureDescriptor("probability", "Mean probability", "avg", "probability") }, "inquiry", "omniscience.persons.read", true);
    }
}
