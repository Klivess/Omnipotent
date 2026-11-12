using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>
    /// Analytics over the rollups that take a longer view than a day: relationship dynamics (N12), communities (N13),
    /// wellbeing check-ins (N14) and the map of similar people (N16). Everything here is recomputed from the rollups by
    /// scheduled jobs and can be rebuilt at any time; what deserves Klives's attention is handed to the notice sink.
    /// <para>
    /// Measures are normalised by each person's own activity, so someone who simply writes less is not read as a
    /// relationship cooling. Minors are in none of it (MinorGuard: no routine or relationship profiling of minors).
    /// </para>
    /// </summary>
    public sealed partial class InsightsModule : IntelModuleBase, IExploreSourceProvider
    {
        public const string ModuleId = "insights";
        public const string RelationshipsKind = "relationships";
        public const string CommunitiesModel = "insights.communities";
        public const string PairsDataset = "insights.pairs";
        public const string PairEventsDataset = "insights.pair_events";

        private readonly INoticeSink? notices;
        private readonly Func<IExploreEngine?>? explore;

        /// <param name="notices">Where findings that deserve attention go (the signals module); null keeps them in the tables only.</param>
        /// <param name="explore">The Explore engine of this build, for the few figures that need message-level labels.</param>
        public InsightsModule(INoticeSink? notices = null, Func<IExploreEngine?>? explore = null)
        {
            this.notices = notices;
            this.explore = explore;
        }

        private static DimensionBinding D(string dim, string column) => new(dim, column);

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Insights",
            Category = "analytics",
            Description = "Relationship dynamics, communities, wellbeing check-ins and the map of similar people, recomputed nightly from the rollups.",
            DependsOn = new[] { RollupModule.ModuleId },
            SubscribedChangeKinds = new[] { Storage.ChangeKinds.PersonErase },
            Schedules = new[] { new ScheduleTrigger("nightly", DailyAtLocal: new TimeOnly(4, 15)) },
            DefaultLane = Lane.Cpu,
            MinorRestricted = true,
            Settings = new[]
            {
                new SettingDescriptor("insights.pair.weeks", SettingTypes.Int, "104", "How many weeks of history the relationship series cover.", 26, 520),
                new SettingDescriptor("insights.pair.min_interactions", SettingTypes.Int, "10", "Pairs with fewer interactions in the whole period are not tracked.", 2, 1000),
                new SettingDescriptor("insights.community.min_edge", SettingTypes.Int, "3", "Two people need at least this many interactions in half a year to count as connected.", 1, 1000),
                new SettingDescriptor("insights.community.resolution", SettingTypes.Number, "1", "Higher finds more, smaller communities.", 0.1, 5),
                new SettingDescriptor("insights.notify.relationships", SettingTypes.Bool, "true", "Tell Klives when a relationship of his, or between people he tracks, changes (X45)."),
                new SettingDescriptor("insights.wellbeing.enabled", SettingTypes.Bool, "true", "Suggest checking in with a close contact whose mood or activity has shifted (X44)."),
                new SettingDescriptor("insights.wellbeing.close_contacts", SettingTypes.Int, "30", "How many of the people Klives interacts with most are watched, besides those he tracks or pinned.", 0, 500),
                new SettingDescriptor("insights.wellbeing.threshold", SettingTypes.Number, "2", "How far from their own usual (in robust standard deviations) two weeks running must be.", 1, 6),
                new SettingDescriptor("insights.wellbeing.crisis_probability", SettingTypes.Number, "0.6", "How sure the ensemble must be that a message says the author is in danger.", 0.3, 0.99),
                new SettingDescriptor("insights.space.window_days", SettingTypes.Int, "90", "How many days a community page (a server or a group chat) looks back.", 30, 365),
                new SettingDescriptor("insights.space.min_messages", SettingTypes.Int, "200", "A server or group chat with fewer messages in the window gets no page of its own.", 1, 1000000),
                new SettingDescriptor("insights.space.max_spaces", SettingTypes.Int, "150", "How many of the busiest servers and group chats get a page each night.", 1, 5000),
                new SettingDescriptor("insights.people.min_messages", SettingTypes.Int, "50", "Someone needs at least this many messages in a year to be compared with others and put on the map.", 10, 100000),
                new SettingDescriptor("insights.map.max_people", SettingTypes.Int, "2000", "How many of the most active people the map lays out itself; the rest are placed near who they resemble.", 50, 4000),
                new SettingDescriptor("insights.space.faction_min_edge", SettingTypes.Int, "2", "Two members need at least this many replies or mentions between them in the window to count as connected.", 1, 1000),
            },
            Datasets = new[]
            {
                new DatasetDescriptor(PairsDataset, "Relationships", new[]
                {
                    D("time.recorded", "updated_at_ms"), D("person.subject", "a"), D("person.partner", "b"), D("prov.status", "trend"), D("prov.confidence", "closeness"),
                }, new[]
                {
                    new MeasureDescriptor("count", "Pairs", "count", null), new MeasureDescriptor("interactions", "Interactions", "sum", "interactions"),
                    new MeasureDescriptor("closeness", "Average closeness", "avg", "closeness"),
                }, "relationship", "omniscience.persons.read", MinorFiltered: true),
                new DatasetDescriptor(PairEventsDataset, "Relationship events", new[]
                {
                    D("time.occurred", "at_ms"), D("time.recorded", "created_at_ms"), D("person.subject", "a"), D("person.partner", "b"), D("content.event", "kind"),
                }, new[] { new MeasureDescriptor("count", "Events", "count", null) }, "relationship_event", "omniscience.persons.read", MinorFiltered: true),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(CommunitiesModel, "Communities", "omniscience.persons.read", 1, 2 * 86_400_000, "Communities found in who interacts with whom, their main members, and the people who bridge them."),
            },
        };

        public IReadOnlyList<ExploreSource> ExploreSources { get; } = new[]
        {
            new ExploreSource(PairsDataset, "ins_pairs", "pair_id", "updated_at_ms", "change_seq"),
            new ExploreSource(PairEventsDataset, "ins_pair_events", "event_id", "at_ms", "change_seq"),
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "relationship series, events and communities", """
CREATE TABLE ins_pair_week(
  a TEXT NOT NULL,
  b TEXT NOT NULL,
  week TEXT NOT NULL,
  out_n INTEGER NOT NULL,
  in_n INTEGER NOT NULL,
  conflict INTEGER NOT NULL,
  negative INTEGER NOT NULL,
  positive INTEGER NOT NULL,
  answered INTEGER NOT NULL,
  closeness REAL NOT NULL,
  PRIMARY KEY(a, b, week)
) WITHOUT ROWID;
CREATE TABLE ins_pairs(
  pair_id TEXT PRIMARY KEY,
  a TEXT NOT NULL,
  b TEXT NOT NULL,
  interactions INTEGER NOT NULL,
  recent INTEGER NOT NULL,
  closeness REAL NOT NULL,
  previous_closeness REAL NOT NULL,
  reciprocity REAL NOT NULL,
  tension REAL NOT NULL,
  trend TEXT NOT NULL,
  first_week TEXT NOT NULL,
  last_week TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_ins_pairs_a ON ins_pairs(a, interactions DESC);
CREATE INDEX ix_ins_pairs_b ON ins_pairs(b, interactions DESC);
CREATE TABLE ins_pair_events(
  event_id TEXT PRIMARY KEY,
  a TEXT NOT NULL,
  b TEXT NOT NULL,
  kind TEXT NOT NULL,
  week TEXT NOT NULL,
  at_ms INTEGER NOT NULL,
  data_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_ins_pair_events_a ON ins_pair_events(a, at_ms DESC);
CREATE INDEX ix_ins_pair_events_b ON ins_pair_events(b, at_ms DESC);
CREATE TABLE ins_communities(
  community_id TEXT PRIMARY KEY,
  size INTEGER NOT NULL,
  weight REAL NOT NULL,
  top_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE ins_members(
  person_id TEXT PRIMARY KEY,
  community_id TEXT NOT NULL,
  strength REAL NOT NULL,
  outside_share REAL NOT NULL,
  bridge_to TEXT,
  updated_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_ins_members_community ON ins_members(community_id);
CREATE TABLE ins_state(
  name TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
INSERT INTO ins_state(name, value) VALUES('change_seq', 0);
INSERT INTO ins_state(name, value) VALUES('community_seq', 0);
"""),
            WellbeingMigration,
            SpacesMigration,
            PeopleMigration,
        };

        internal ModuleContext Ctx => Context;

        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[] { new InfluenceTactic(this), new RelationshipWatchTactic(this), new WellbeingWatchTactic(this) };

        private IReadOnlyList<ITactic>? tactics;

        public const string ForgetKind = "forget";

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<Storage.ChangeRecord> changes) =>
            changes.Where(c => c.Kind == Storage.ChangeKinds.PersonErase).SelectMany(c => new[] { c.EntityId, c.PersonId }).OfType<string>().Distinct(StringComparer.Ordinal)
                .Select(p => new DirtySubject("person", p));

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            // The nightly pass rebuilds the relationships; the wellbeing pass follows it (it reads who is close to Klives).
            if (context.VersionChanged || context.DueSchedules.Any(s => s.Name == "nightly")) yield return Job(RelationshipsKind, "all", "all", priority: 60);
            foreach (var d in context.Dirty.Where(d => d.Kind == "person")) yield return Job(ForgetKind, "person", d.Id, priority: 200);
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            RelationshipsKind => await BuildRelationshipsAsync(job).ConfigureAwait(false),
            WellbeingKind => await BuildWellbeingAsync(job).ConfigureAwait(false),
            SpacesKind or SpaceKind => await BuildSpacesAsync(job).ConfigureAwait(false),
            PeopleKind => await BuildPeopleAsync(job).ConfigureAwait(false),
            ForgetKind => await ForgetAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown insights job kind '{job.Job.Kind}'."),
        };

        /// <summary>Queues a recomputation now (the nightly schedule does the same).</summary>
        public Task<long> RefreshAsync(CancellationToken ct) => Context.Jobs.EnqueueAsync(Job(RelationshipsKind, "all", "all", priority: 160), ct);

        /// <summary>A person was forgotten: everything kept about them here goes at once (the nightly rebuild would only reach
        /// the tables it recomputes).</summary>
        private async Task<string?> ForgetAsync(JobContext job)
        {
            string person = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Forget job without a person.");
            int removed = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int n = c.Exec(tx, "DELETE FROM ins_pair_week WHERE a=$p OR b=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM ins_pairs WHERE a=$p OR b=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM ins_pair_events WHERE a=$p OR b=$p OR json_extract(data_json,'$.by')=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM ins_members WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM ins_wellbeing WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM ins_people WHERE person_id=$p", ("$p", person));
                // A community page that names them keeps standing, without them (its counts are corrected by the next rebuild).
                foreach (var (kind, id, body, factions) in c.Query(tx, "SELECT scope_kind, scope_id, body_json, factions_json FROM ins_spaces WHERE body_json LIKE $l OR factions_json LIKE $l",
                             r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$l", "%" + JsonSerializer.Serialize(person) + "%")))
                {
                    var page = JsonSerializer.Deserialize<SpacePageDto>(body, KernelJson.Options)!;
                    page = page with
                    {
                        Core = page.Core.Where(m => m.PersonId != person).ToList(),
                        Rising = page.Rising.Where(m => m.PersonId != person).ToList(),
                        AboutKlives = page.AboutKlives with { People = page.AboutKlives.People.Where(m => m.PersonId != person).ToList() },
                        Factions = page.Factions.Select(f => f with { Members = f.Members.Where(m => m.PersonId != person).ToList() }).ToList(),
                    };
                    var members = JsonSerializer.Deserialize<Dictionary<string, string>>(factions, KernelJson.Options) ?? new Dictionary<string, string>();
                    members.Remove(person);
                    n += c.Exec(tx, "UPDATE ins_spaces SET body_json=$b, factions_json=$f WHERE scope_kind=$k AND scope_id=$i",
                        ("$b", JsonSerializer.Serialize(page, KernelJson.Options)), ("$f", JsonSerializer.Serialize(members, KernelJson.Options)), ("$k", kind), ("$i", id));
                }
                foreach (var (id, top) in c.Query(tx, "SELECT community_id, top_json FROM ins_communities WHERE top_json LIKE $l", r => (r.GetString(0), r.GetString(1)), ("$l", "%" + person + "%")))
                {
                    var kept = (JsonSerializer.Deserialize<List<JsonElement>>(top, KernelJson.Options) ?? new List<JsonElement>()).Where(t => t.GetProperty("personId").GetString() != person).ToList();
                    c.Exec(tx, "UPDATE ins_communities SET top_json=$t WHERE community_id=$c", ("$t", JsonSerializer.Serialize(kept, KernelJson.Options)), ("$c", id));
                }
                return n;
            }, job.Cancellation).ConfigureAwait(false);
            // The communities and the served summary still count them: both are rebuilt from what is left.
            await RefreshAsync(job.Cancellation).ConfigureAwait(false);
            return Json(new { person, removed });
        }

        // ── shared pieces ────────────────────────────────────────────────────────

        private static long NextChange(SqliteConnection c, SqliteTransaction tx) => c.Scalar<long>(tx, "UPDATE ins_state SET value = value + 1 WHERE name='change_seq' RETURNING value");

        internal bool HasTable(string name)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", name)) > 0;
        }

        internal bool Restricted(string person, long atMs) => MinorGuard.IsRestricted(Context.Policy.BandOf(person, atMs));

        internal static string PairId(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        internal static (string A, string B) Ordered(string x, string y) => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

        /// <summary>The Monday (yyyy-MM-dd) of the week a day belongs to.</summary>
        internal static string WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal string WeekOf(long ms) => WeekOf(Zones.LocalDate(ms, Context.Settings.KlivesZone));

        internal long WeekStartMs(string week) =>
            Zones.DayStartUtcMs(DateOnly.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture), Context.Settings.KlivesZone);

        internal string? SelfPerson()
        {
            using var corpus = Context.Stores.Corpus.OpenRead();
            return corpus.Scalar<string>(null, "SELECT COALESCE(pc.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE p.is_self=1 ORDER BY p.person_id LIMIT 1");
        }

        internal Dictionary<string, string?> Names(IEnumerable<string?> persons)
        {
            using var corpus = Context.Stores.Corpus.OpenRead();
            return corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", persons.OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        }

        private static string Json(object value) => JsonSerializer.Serialize(value, KernelJson.Options);
    }
}
