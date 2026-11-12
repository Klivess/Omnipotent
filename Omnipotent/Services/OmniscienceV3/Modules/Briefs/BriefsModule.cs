using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Briefs
{
    /// <summary>
    /// Catch-up digests (N10), person briefs (N4) and the summarising tactics (X18, X30, X31, X52). Everything written here
    /// is a cited summary: each point names the messages it rests on, and only citations that were really shown to the model
    /// are kept. A period with nothing in it says so without asking the model. Text never contains "yesterday" or "last
    /// week": points carry message ids and instants, and the page words them relative to when it is read.
    /// </summary>
    public sealed partial class BriefsModule : IntelModuleBase
    {
        public const string ModuleId = "briefs";
        public const string DigestModel = "briefs.digest";
        public const string PersonModel = "briefs.person";
        public const string DigestKind = "digest";
        public const string PersonKind = "person";
        public const string TickKind = "tick";
        /// <summary>operator.db <c>watchlists.kind</c> of digest subscriptions (X52).</summary>
        public const string SubscriptionKind = "digest";
        public const string MinorRefusal = "A digest or a brief about one person is not available for someone who is, or may be, under 18. Their conversations stay readable.";

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Digests and briefs",
            Category = "briefs",
            Description = "Catch-up digests since Klives last looked (per server, chat or person, on demand and scheduled) and one-page person briefs. Every point cites its messages.",
            Schedules = new[] { new ScheduleTrigger("tick", Every: TimeSpan.FromMinutes(5)) },
            DefaultLane = Lane.LlmBackground,
            Settings = new[]
            {
                new SettingDescriptor("briefs.digest.daily_at", SettingTypes.String, "\"07:30\"", "When the daily catch-up digest is written, in Klives's zone (HH:mm). Empty: no daily digest."),
                new SettingDescriptor("briefs.digest.default_hours", SettingTypes.Int, "24", "How far back a digest reaches when Klives has never looked at its scope.", 1, 720),
                new SettingDescriptor("briefs.digest.max_days", SettingTypes.Int, "30", "The longest period one digest covers.", 1, 366),
                new SettingDescriptor("briefs.digest.max_messages", SettingTypes.Int, "20000", "The most messages one digest reads; above it the newest of each conversation are kept and the digest says so.", 100, 200000),
                new SettingDescriptor("briefs.person.days", SettingTypes.Int, "30", "How many days of a person's messages a brief reads.", 7, 366),
                new SettingDescriptor("briefs.person.stale_days", SettingTypes.Int, "30", "A brief whose newest evidence is older than this carries a staleness banner.", 1, 3650),
                new SettingDescriptor("briefs.person.max_messages", SettingTypes.Int, "1500", "The most messages one brief reads.", 50, 20000),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(DigestModel, "Latest digest of a scope", "omniscience.messages.read", 1, 86_400_000, "The newest finished digest of a scope (key: all, guild:<id>, conversation:<id>, person:<id>)."),
                new ReadModelDescriptor(PersonModel, "Person brief", "omniscience.persons.read", 1, 86_400_000, "The newest brief of a person."),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "digests, person briefs, subscription runs", """
CREATE TABLE brief_digests(
  digest_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL,
  scope_id TEXT,
  from_ms INTEGER NOT NULL,
  to_ms INTEGER NOT NULL,
  state TEXT NOT NULL,
  trigger TEXT NOT NULL,
  subscription_id TEXT,
  messages INTEGER NOT NULL DEFAULT 0,
  body_json TEXT,
  error TEXT,
  llm_calls INTEGER NOT NULL DEFAULT 0,
  actor TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  finished_at_ms INTEGER
);
CREATE INDEX ix_brief_digests_time ON brief_digests(created_at_ms DESC, digest_id DESC);
CREATE INDEX ix_brief_digests_scope ON brief_digests(scope_kind, scope_id, created_at_ms DESC);
CREATE TABLE brief_person(
  person_id TEXT PRIMARY KEY,
  state TEXT NOT NULL,
  body_json TEXT,
  as_of_ms INTEGER,
  newest_evidence_ms INTEGER,
  error TEXT,
  llm_calls INTEGER NOT NULL DEFAULT 0,
  actor TEXT NOT NULL,
  requested_at_ms INTEGER NOT NULL,
  finished_at_ms INTEGER
);
CREATE TABLE brief_runs(
  subscription_id TEXT PRIMARY KEY,
  last_run_ms INTEGER NOT NULL
);
"""),
        };

        internal ModuleContext Ctx => Context;

        private CitedSummarizer Summarizer => new(Context.Llm ?? throw new JobFailureException(ErrorClass.Permanent, "Digests and briefs need the model, which this runtime does not have."), ModuleId);

        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[]
        {
            new SummariseSelectionTactic(this), new TopicSnapshotTactic(this), new ActionItemsTactic(this), new DigestSubscriptionTactic(this),
        };

        private IReadOnlyList<ITactic>? tactics;

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            if (context.DueSchedules.Any(s => s.Name == "tick")) yield return Job(TickKind, "schedule", "tick", priority: 120, lane: Lane.Cpu);
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            DigestKind => await BuildDigestAsync(job).ConfigureAwait(false),
            PersonKind => await BuildPersonBriefAsync(job).ConfigureAwait(false),
            TickKind => await TickAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown briefs job kind '{job.Job.Kind}'."),
        };

        // ── shared reads ─────────────────────────────────────────────────────────

        /// <summary>A message as the summaries read it.</summary>
        public sealed record SourceMessage(string Id, string Conversation, string? Guild, string AuthorIdentity, string? Person, long AtMs, string Text, string? ReplyTo);

        internal const string MessageColumns = """
            m.message_id, m.conversation_id, m.guild_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.sent_at_ms, m.content, m.reply_to_message_id
            FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
            """;

        /// <summary>Human, undeleted messages with text.</summary>
        internal const string Readable = "m.deleted_at_ms IS NULL AND m.is_bot=0 AND m.is_system=0 AND m.is_webhook=0 AND COALESCE(i.kind,'')<>'bot' AND m.content IS NOT NULL AND length(trim(m.content)) > 0";

        internal static SourceMessage ReadMessage(SqliteDataReader r) =>
            new(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetInt64(5), r.GetString(6), r.GetStringOrNull(7));

        /// <summary>Names for a set of messages: the author as the model should call them, and where the message was said.</summary>
        internal sealed class Naming
        {
            private readonly Dictionary<string, string> people;
            private readonly Dictionary<string, (string Title, string? Kind, string? Guild)> conversations;
            private readonly Dictionary<string, string> guilds;
            private readonly HashSet<string> self;

            public Naming(SqliteConnection corpus, IReadOnlyCollection<SourceMessage> messages)
            {
                self = ClassificationModule.SelfIdentities(corpus);
                people = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", messages.Select(m => m.Person).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).Where(x => !string.IsNullOrWhiteSpace(x.Item2)).ToDictionary(x => x.Item1, x => x.Item2!, StringComparer.Ordinal);
                conversations = corpus.QueryIn(null, "SELECT conversation_id, title, kind, guild_id FROM conversations WHERE conversation_id IN ({keys})",
                    messages.Select(m => m.Conversation).Distinct(StringComparer.Ordinal).ToList(), r => (r.GetString(0), r.GetStringOrNull(1), r.GetStringOrNull(2), r.GetStringOrNull(3)))
                    .ToDictionary(x => x.Item1, x => (x.Item2 ?? x.Item1, x.Item3, x.Item4), StringComparer.Ordinal);
                guilds = corpus.QueryIn(null, "SELECT guild_id, name FROM guilds WHERE guild_id IN ({keys})", conversations.Values.Select(c => c.Guild).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2 ?? x.Item1, StringComparer.Ordinal);
            }

            public bool IsKlives(SourceMessage m) => self.Contains(m.AuthorIdentity);

            public string Author(SourceMessage m) => IsKlives(m) ? "Klives" : m.Person != null && people.TryGetValue(m.Person, out var n) ? n : "someone";

            public string? PersonName(string? person) => person != null && people.TryGetValue(person, out var n) ? n : null;

            public string ConversationTitle(string id) => conversations.TryGetValue(id, out var c) ? c.Title : id;

            public string? ConversationKind(string id) => conversations.TryGetValue(id, out var c) ? c.Kind : null;

            public string? GuildOf(string conversation) => conversations.TryGetValue(conversation, out var c) ? c.Guild : null;

            public string GuildName(string id) => guilds.TryGetValue(id, out var n) ? n : id;
        }

        private static string Json(object value) => JsonSerializer.Serialize(value, KernelJson.Options);

        private static T? Parse<T>(string? json) where T : class => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, KernelJson.Options);

        /// <summary>The most recent moment a daily time (HH:mm in Klives's zone) occurred at or before now; DST-safe.</summary>
        internal long? LatestOccurrence(string hhmm, int? weekday, long nowMs)
        {
            if (!TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
            var zone = Context.Settings.KlivesZone;
            var today = Zones.LocalDate(nowMs, zone);
            for (int back = 0; back <= 8; back++)
            {
                var day = today.AddDays(-back);
                if (weekday != null && (int)day.DayOfWeek != weekday) continue;
                long ms = Zones.ResolveLocal(day.ToDateTime(at), zone).UtcMs;
                if (ms <= nowMs) return ms;
            }
            return null;
        }

        private AIRouterWorkClass WorkClassOf(JobContext job) => job.Job.Lane == Lane.LlmInteractive ? AIRouterWorkClass.Interactive : AIRouterWorkClass.Background;
    }
}
