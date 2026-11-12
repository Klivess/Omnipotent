using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Modules.Explore
{
    /// <summary>
    /// The datasets the Explore mirror serves, their DuckDB tables and the D2 bindings (plan D4 "self-describing": the
    /// filter bar is generated from these declarations). Person columns hold the person id as ingested; the engine resolves
    /// them through <c>person_map</c> at query time, so a merge never rewrites the mirror.
    /// </summary>
    public static class ExploreDatasets
    {
        public const string Messages = "messages";
        public const string Events = "events";
        public const string Relations = "relations";
        public const string Concepts = "concepts";
        public const string Assertions = "assertions";
        public const string TacticRuns = "tactic_runs";
        public const string Jobs = "jobs";
        public const string LlmCalls = "llm_calls";

        private static DimensionBinding D(string dim, string column) => new(dim, column);
        private static MeasureDescriptor M(string id, string title, string agg, string? column, string unit = "") => new(id, title, agg, column, unit);

        public static readonly IReadOnlyList<DatasetDescriptor> All = new[]
        {
            new DatasetDescriptor(Messages, "Messages", new[]
            {
                D("time.occurred", "occurred"), D("time.ingested", "ingested"), D("time.edited", "edited"), D("time.deleted", "deleted"),
                D("person.author", "author"), D("person.partner", "partner"),
                D("where.platform", "platform"), D("where.source", "source"), D("where.guild", "guild"), D("where.channel", "conversation"),
                D("where.conversation", "conversation"), D("where.kind", "kind"),
                D("content.language", "language"), D("content.length", "length"), D("content.link_domain", "link_domain"), D("content.media", "media"),
                D("content.sentiment", "sentiment"), D("content.emotion", "emotion"), D("content.toxicity", "toxicity"), D("content.humour", "humour"),
                D("content.dialogue_act", "dialogue_act"), D("content.modality", "modality"), D("content.politeness", "politeness"), D("content.formality", "formality"),
                D("content.self_disclosure", "self_disclosure"), D("content.info_density", "info_density"), D("content.conflict", "conflict"),
                D("content.support", "support"), D("content.about_klives", "about_klives"), D("content.is_reply", "is_reply"), D("content.is_edited", "is_edited"),
                D("content.is_deleted", "is_deleted"), D("content.is_forwarded", "is_forwarded"), D("content.mentions_klives", "mentions_klives"),
            }, new[]
            {
                M("count", "Messages", "count", null), M("people", "People", "distinct", "author"), M("conversations", "Conversations", "distinct", "conversation"),
                M("avg_length", "Average length", "avg", "length", "chars"), M("median_length", "Median length", "p50", "length", "chars"),
            }, "message", "omniscience.overview.view", MinorFiltered: true),

            new DatasetDescriptor(Events, "Events", new[]
            {
                D("time.occurred", "occurred"), D("person.subject", "person"), D("where.guild", "guild"), D("where.channel", "channel"), D("where.platform", "platform"),
                D("where.source", "source"), D("content.event", "kind"), D("content.activity", "activity"), D("content.reaction", "emoji"),
            }, new[]
            {
                M("count", "Events", "count", null), M("people", "People", "distinct", "person"), M("duration", "Total duration", "sum", "duration_ms", "ms"),
                M("avg_duration", "Average duration", "avg", "duration_ms", "ms"),
            }, "event", "omniscience.persons.read", MinorFiltered: true),

            new DatasetDescriptor(Relations, "Interactions between people", new[]
            {
                D("time.occurred", "day_ms"), D("person.author", "person"), D("person.partner", "partner"),
            }, new[]
            {
                M("interactions", "Interactions", "sum", "interactions"), M("replies", "Replies", "sum", "replies"), M("mentions", "Mentions", "sum", "mentions"),
                M("dm_messages", "DM messages", "sum", "dm_msgs"), M("reactions", "Reactions", "sum", "reactions"), M("partners", "People", "distinct", "partner"),
            }, "relation", "omniscience.persons.read", MinorFiltered: true),

            new DatasetDescriptor(Concepts, "Taught concepts", new[]
            {
                D("time.occurred", "occurred"), D("person.author", "author"), D("where.conversation", "conversation"), D("where.guild", "guild"),
                D("content.concept", "concept"), D("prov.confidence", "probability"),
            }, new[]
            {
                M("count", "Matching messages", "count", null), M("people", "People", "distinct", "author"), M("avg_probability", "Average probability", "avg", "probability"),
            }, "message", "omniscience.overview.view", MinorFiltered: true),

            new DatasetDescriptor(Assertions, "Facts and inferences", new[]
            {
                D("time.recorded", "recorded"), D("time.valid_from", "valid_from"), D("time.valid_to", "valid_to"), D("time.occurred", "asserted"),
                D("person.subject", "subject"), D("content.attribute", "attribute"), D("prov.status", "status"), D("prov.module", "module"), D("prov.model", "model"),
                D("prov.prompt_version", "prompt_version"), D("prov.confidence", "probability"), D("content.modality", "modality"),
            }, new[]
            {
                M("count", "Assertions", "count", null), M("subjects", "People", "distinct", "subject"), M("avg_probability", "Average probability", "avg", "probability"),
            }, "assertion", "omniscience.persons.read", MinorFiltered: true),

            new DatasetDescriptor(TacticRuns, "Tactic runs", new[]
            {
                D("time.occurred", "created"), D("prov.run", "run_id"), D("prov.module", "tactic"), D("prov.status", "state"),
            }, new[] { M("count", "Runs", "count", null), M("avg_duration", "Average duration", "avg", "duration_ms", "ms") }, "tactic_run", "omniscience.overview.view", MinorFiltered: false),

            new DatasetDescriptor(Jobs, "Background jobs", new[]
            {
                D("time.occurred", "created"), D("prov.module", "module"), D("prov.status", "state"), D("where.kind", "kind"),
            }, new[] { M("count", "Jobs", "count", null), M("avg_duration", "Average duration", "avg", "duration_ms", "ms"), M("attempts", "Attempts", "sum", "attempts") },
                "job", "omniscience.overview.view", MinorFiltered: false),

            new DatasetDescriptor(LlmCalls, "LLM calls", new[]
            {
                D("time.occurred", "at_ms"), D("prov.module", "module"), D("prov.model", "model"), D("prov.status", "outcome"), D("prov.prompt_version", "prompt_version"),
                D("where.kind", "work_class"),
            }, new[]
            {
                M("count", "Calls", "count", null), M("prompt_tokens", "Prompt tokens", "sum", "prompt_tokens"), M("cached_tokens", "Cached tokens", "sum", "cached_tokens"),
                M("completion_tokens", "Completion tokens", "sum", "completion_tokens"), M("p95_latency", "p95 latency", "p95", "latency_ms", "ms"),
            }, "llm_call", "omniscience.overview.view", MinorFiltered: false),
        };

        /// <summary>Key column and record-ordering time column of each dataset's table.</summary>
        public static (string Key, string Time) KeyOf(string dataset) => dataset switch
        {
            Messages => ("message_id", "occurred"),
            Events => ("event_id", "occurred"),
            Relations => ("row_id", "day_ms"),
            Concepts => ("row_id", "occurred"),
            Assertions => ("assertion_id", "recorded"),
            TacticRuns => ("run_id", "created"),
            Jobs => ("job_id", "created"),
            LlmCalls => ("call_id", "at_ms"),
            _ => throw new KeyNotFoundException(dataset),
        };

        /// <summary>Datasets with a per-row zone for the subject lens and a time-quality column (day precision leaves hour stats).</summary>
        public static bool HasSubjectZone(string dataset) => dataset is Messages;
        public static bool HasTimeQuality(string dataset) => dataset is Messages;

        /// <summary>The layout of the mirror's own tables. The mirror is derived data: when this number differs from the one
        /// stored in the file, every table is dropped and the mirror is rebuilt from the stores (bump it with any DDL change).</summary>
        public const long SchemaVersion = 2;

        public static readonly IReadOnlyList<string> Ddl = new[]
        {
            "CREATE TABLE IF NOT EXISTS mirror_state(key VARCHAR PRIMARY KEY, value BIGINT)",
            "CREATE TABLE IF NOT EXISTS person_map(person_id VARCHAR PRIMARY KEY, canonical_id VARCHAR)",
            "CREATE TABLE IF NOT EXISTS restricted_persons(person_id VARCHAR PRIMARY KEY)",
            "CREATE TABLE IF NOT EXISTS off_persons(person_id VARCHAR PRIMARY KEY)",
            """
            CREATE TABLE IF NOT EXISTS messages(
              message_id VARCHAR, occurred BIGINT, ingested BIGINT, edited BIGINT, deleted BIGINT, time_quality TINYINT,
              author VARCHAR, partner VARCHAR, conversation VARCHAR, guild VARCHAR, kind VARCHAR, platform VARCHAR, source VARCHAR,
              length INTEGER, link_domain VARCHAR, media VARCHAR, is_reply BOOLEAN, is_edited BOOLEAN, is_deleted BOOLEAN, is_forwarded BOOLEAN,
              mentions_klives BOOLEAN, language VARCHAR, sentiment VARCHAR, emotion VARCHAR, toxicity VARCHAR, humour VARCHAR, dialogue_act VARCHAR,
              modality VARCHAR, politeness VARCHAR, formality VARCHAR, self_disclosure VARCHAR, info_density VARCHAR, conflict VARCHAR, support VARCHAR,
              about_klives VARCHAR, non_authored BOOLEAN, labelset INTEGER, s_zone VARCHAR, s_offset_ms INTEGER)
            """,
            """
            CREATE TABLE IF NOT EXISTS events(
              event_id VARCHAR, kind VARCHAR, occurred BIGINT, person VARCHAR, guild VARCHAR, channel VARCHAR, platform VARCHAR, source VARCHAR,
              emoji VARCHAR, activity VARCHAR, status VARCHAR, duration_ms BIGINT)
            """,
            "CREATE TABLE IF NOT EXISTS relations(row_id VARCHAR, day_ms BIGINT, day_text VARCHAR, person VARCHAR, partner VARCHAR, replies INTEGER, mentions INTEGER, dm_msgs INTEGER, reactions INTEGER, interactions INTEGER)",
            "CREATE TABLE IF NOT EXISTS concepts(row_id VARCHAR, message_id VARCHAR, occurred BIGINT, author VARCHAR, conversation VARCHAR, guild VARCHAR, concept VARCHAR, concept_id VARCHAR, probability DOUBLE)",
            """
            CREATE TABLE IF NOT EXISTS assertions(
              assertion_id VARCHAR, recorded BIGINT, valid_from BIGINT, valid_to BIGINT, asserted BIGINT, subject VARCHAR, attribute VARCHAR, value VARCHAR,
              status VARCHAR, module VARCHAR, model VARCHAR, prompt_version VARCHAR, probability DOUBLE, modality VARCHAR, superseded BIGINT, retracted BIGINT)
            """,
            "CREATE TABLE IF NOT EXISTS tactic_runs(run_id VARCHAR, created BIGINT, tactic VARCHAR, state VARCHAR, actor VARCHAR, duration_ms BIGINT, error VARCHAR)",
            "CREATE TABLE IF NOT EXISTS jobs(job_id VARCHAR, created BIGINT, module VARCHAR, kind VARCHAR, state VARCHAR, attempts INTEGER, duration_ms BIGINT, error_class VARCHAR)",
            """
            CREATE TABLE IF NOT EXISTS llm_calls(
              call_id VARCHAR, at_ms BIGINT, module VARCHAR, purpose VARCHAR, model VARCHAR, work_class VARCHAR, outcome VARCHAR, prompt_version VARCHAR,
              prompt_tokens BIGINT, cached_tokens BIGINT, completion_tokens BIGINT, latency_ms BIGINT)
            """,
        };
    }
}
