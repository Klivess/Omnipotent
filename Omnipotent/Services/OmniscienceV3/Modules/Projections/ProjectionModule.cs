using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Projections
{
    /// <summary>
    /// Read-model projections (plan "Read models are the only thing routes read"; F003/F005/F538–F545/F556): every page
    /// route is one indexed read of a row these jobs maintain from the rollups, never a computation on request.
    /// <list type="bullet">
    /// <item><b>person.dossier</b> (key: canonical person id): identity and name history, activity in the person's own days,
    /// partners both ways, conversations and servers, label mix against their own baseline, language, zone, facts from the
    /// knowledge store (policy-filtered), coverage and a staleness stamp. Bounded size whatever the history.</item>
    /// <item><b>directory</b>: <c>projection_people</c>, one indexed row per person for keyset-paged lists (F005/F545).</item>
    /// <item><b>overview</b>, <b>engine.health</b>: the dashboard and the data-health view (F003/F556).</item>
    /// <item><b>conversation.card</b>, <b>guild.card</b>: per conversation and server.</item>
    /// </list>
    /// MinorGuard runs twice (plan "filters run at serialization, defence in depth"): restricted sections are left out when a
    /// dossier is built, and <see cref="DossierServingFilter"/> strips them again when it is served if the band has changed since.
    /// </summary>
    public sealed partial class ProjectionModule : IntelModuleBase, IReadModelServingFilter
    {
        public const string ModuleId = "projections";
        public const string Dossier = "person.dossier";
        public const string Overview = "overview";
        public const string Health = "engine.health";
        public const string ConversationCard = "conversation.card";
        public const string GuildCard = "guild.card";
        public const int SeriesDays = 180;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Read-model projections",
            Category = "core",
            Description = "Builds the dossier, directory, overview, health, conversation and server views the pages read, from the rollups and the knowledge store.",
            DependsOn = new[] { RollupModule.ModuleId },
            SubscribedChangeKinds = new[] { ChangeKinds.IdentityName, ChangeKinds.IdentityUpsert, ChangeKinds.PersonCreate, ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge,
                ChangeKinds.PersonErase, ChangeKinds.ConversationUpsert, ChangeKinds.GuildUpsert },
            Schedules = new[]
            {
                new ScheduleTrigger("outbox", Every: TimeSpan.FromSeconds(30)),
                new ScheduleTrigger("overview", Every: TimeSpan.FromMinutes(5)),
                new ScheduleTrigger("health", Every: TimeSpan.FromMinutes(1)),
                new ScheduleTrigger("policy", Every: TimeSpan.FromMinutes(2)),
                new ScheduleTrigger("daily", DailyAtLocal: new TimeOnly(4, 55)),
            },
            DefaultLane = Lane.Cpu,
            ReadModels = new[]
            {
                new ReadModelDescriptor(Dossier, "Person dossier", "omniscience.persons.read", 1, 10 * 60_000, "Everything known about one person, bounded, with coverage and staleness."),
                new ReadModelDescriptor(Overview, "Overview", "omniscience.overview.view", 1, 5 * 60_000, "Totals, activity, top people and servers, label mix, ingest and coverage."),
                new ReadModelDescriptor(Health, "Engine health", "omniscience.overview.view", 1, 2 * 60_000, "Modules, backlogs, jobs, stores, LLM usage, encoder and data health."),
                new ReadModelDescriptor(ConversationCard, "Conversation", "omniscience.persons.read", 1, 10 * 60_000, "One conversation: names, participants, activity, label mix."),
                new ReadModelDescriptor(GuildCard, "Server", "omniscience.persons.read", 1, 10 * 60_000, "One server: names, channels, members, activity, label mix."),
            },
        };

        private readonly DossierServingFilter dossierFilter = new();

        public bool Handles(string modelId) => dossierFilter.Handles(modelId);

        public string Filter(ReadModelRow row, IPolicyOracle policy, long nowMs) => dossierFilter.Filter(row, policy, nowMs);

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "directory and cursors", """
CREATE TABLE projection_people(
  person_id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  sort_name TEXT NOT NULL,
  tier TEXT NOT NULL,
  pinned INTEGER NOT NULL,
  band TEXT NOT NULL,
  is_self INTEGER NOT NULL,
  platforms TEXT NOT NULL,
  identities INTEGER NOT NULL,
  first_seen_ms INTEGER,
  last_seen_ms INTEGER,
  msgs_total INTEGER NOT NULL,
  msgs_30d INTEGER NOT NULL,
  msgs_90d INTEGER NOT NULL,
  card_json TEXT NOT NULL,
  as_of_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_pp_last ON projection_people(last_seen_ms, person_id);
CREATE INDEX ix_pp_total ON projection_people(msgs_total, person_id);
CREATE INDEX ix_pp_recent ON projection_people(msgs_30d, person_id);
CREATE INDEX ix_pp_name ON projection_people(sort_name, person_id);
CREATE INDEX ix_pp_tier_last ON projection_people(tier, last_seen_ms, person_id);
CREATE TABLE projection_cursors(
  name TEXT PRIMARY KEY,
  seq INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
"""),
        };

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            foreach (var c in changes)
            {
                switch (c.Kind)
                {
                    case ChangeKinds.ConversationUpsert: yield return new DirtySubject("conversation", c.EntityId); break;
                    case ChangeKinds.GuildUpsert: yield return new DirtySubject("guild", c.EntityId); break;
                    default:
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        if (c.Kind is ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge or ChangeKinds.PersonErase or ChangeKinds.PersonCreate)
                            yield return new DirtySubject("person", c.EntityId);
                        break;
                }
            }
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            foreach (var d in context.Dirty)
                jobs.Add(Job("project", d.Kind, d.Id, priority: 130));
            if (context.DueSchedules.Any(s => s.Name == "outbox") || context.VersionChanged) jobs.Add(Job("outbox", "stream", "rollups", priority: 125));
            if (context.DueSchedules.Any(s => s.Name == "overview") || context.VersionChanged) jobs.Add(Job("overview", "view", "global", priority: 120));
            if (context.DueSchedules.Any(s => s.Name == "health") || context.VersionChanged) jobs.Add(Job("health", "view", "global", priority: 120));
            if (context.DueSchedules.Any(s => s.Name == "policy")) jobs.Add(Job("policy", "stream", "operator", priority: 125));
            if (context.DueSchedules.Any(s => s.Name == "daily") || context.VersionChanged) jobs.Add(Job("daily", "stream", "all", priority: 60));
            return jobs;
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            var ct = job.Cancellation;
            switch (job.Job.Kind)
            {
                case "project":
                    await ProjectAsync(job.Job.SubjectKind!, job.Job.SubjectId!, ct).ConfigureAwait(false);
                    return null;
                case "outbox": return await OutboxAsync(job).ConfigureAwait(false);
                case "overview": await ProjectOverviewAsync(ct).ConfigureAwait(false); return null;
                case "health": await ProjectHealthAsync(ct).ConfigureAwait(false); return null;
                case "policy": return await PolicyAsync(job).ConfigureAwait(false);
                case "daily": return await DailyAsync(job).ConfigureAwait(false);
                default: throw new JobFailureException(ErrorClass.Permanent, $"Unknown projections job kind '{job.Job.Kind}'.");
            }
        }

        private long Cursor(string name)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT seq FROM projection_cursors WHERE name=$n", ("$n", name)) ?? 0;
        }

        private Task SetCursorAsync(string name, long seq, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO projection_cursors(name, seq, updated_at_ms) VALUES($n,$s,$t)
                ON CONFLICT(name) DO UPDATE SET seq=MAX(projection_cursors.seq, excluded.seq), updated_at_ms=excluded.updated_at_ms
                """, ("$n", name), ("$s", seq), ("$t", Context.Clock.NowMs)), ct);

        /// <summary>Consumes the rollup outbox: each changed person, conversation or server is projected once per batch.</summary>
        private async Task<string?> OutboxAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long cursor = Cursor("rollups");
            int projected = 0;
            for (int round = 0; round < 50; round++)
            {
                List<(long Seq, string Kind, string Id)> rows;
                using (var c = Context.Stores.Intel.OpenRead())
                    rows = c.Query(null, "SELECT seq, subject_kind, subject_id FROM rollup_changes WHERE seq > $s ORDER BY seq LIMIT 2000",
                        r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$s", cursor));
                if (rows.Count == 0) break;
                var subjects = rows.Select(r => (r.Kind, r.Id)).Distinct().ToList();
                foreach (var (kind, id) in subjects)
                {
                    ct.ThrowIfCancellationRequested();
                    if (kind == "global") continue; // the overview refreshes on its own schedule
                    await ProjectAsync(kind, id, ct).ConfigureAwait(false);
                    projected++;
                }
                cursor = rows[^1].Seq;
                await SetCursorAsync("rollups", cursor, ct).ConfigureAwait(false);
                await job.ReportProgressAsync(new { cursor, projected }).ConfigureAwait(false);
            }
            // Consumed outbox rows are trimmed (the outbox is only a queue; the cells themselves are the record).
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM rollup_changes WHERE seq <= $s AND at_ms < $cut",
                ("$s", cursor), ("$cut", Context.Clock.NowMs - 86_400_000L)), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { cursor, projected });
        }

        internal async Task ProjectAsync(string kind, string id, CancellationToken ct)
        {
            switch (kind)
            {
                case "person": await ProjectPersonAsync(id, ct).ConfigureAwait(false); break;
                case "conversation": await ProjectConversationAsync(id, ct).ConfigureAwait(false); break;
                case "guild": await ProjectGuildAsync(id, ct).ConfigureAwait(false); break;
            }
        }

        /// <summary>Operator changes (tiers, pins, minor marks) carry no change log: people whose settings moved are re-projected.</summary>
        private async Task<string?> PolicyAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long since = Cursor("operator");
            List<(string Person, long At)> rows;
            using (var op = Context.Stores.Operator.OpenRead())
                rows = op.Query(null, "SELECT person_id, updated_at_ms FROM person_settings WHERE updated_at_ms > $s ORDER BY updated_at_ms LIMIT 5000",
                    r => (r.GetString(0), r.GetInt64(1)), ("$s", since));
            foreach (var p in rows.Select(r => Context.Policy.Canonical(r.Person)).Distinct()) await ProjectPersonAsync(p, ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                await SetCursorAsync("operator", rows[^1].At, ct).ConfigureAwait(false);
                ReadModelStore.PolicyChanged();
            }
            return JsonSerializer.Serialize(new { people = rows.Count });
        }

        /// <summary>Sliding windows (30/90/365 days) move every day: every person and the overview are re-projected.</summary>
        private async Task<string?> DailyAsync(JobContext job)
        {
            var ct = job.Cancellation;
            List<string> persons;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                persons = corpus.Query(null, "SELECT p.person_id FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE COALESCE(pc.canonical_id, p.person_id)=p.person_id",
                    r => r.GetString(0));
            int n = 0;
            foreach (var p in persons)
            {
                ct.ThrowIfCancellationRequested();
                await ProjectPersonAsync(p, ct).ConfigureAwait(false);
                if (++n % 200 == 0) await job.ReportProgressAsync(new { projected = n, of = persons.Count }).ConfigureAwait(false);
            }
            await ProjectOverviewAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { persons = n });
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private string Today() => RollupModule.Day(Zones.LocalDate(Context.Clock.NowMs, Context.Settings.KlivesZone));

        private static string DaysBefore(string day, int days) => RollupModule.Day(RollupModule.ParseDay(day).AddDays(-days));

        internal static Dictionary<string, Dictionary<string, double>> Shares(Dictionary<string, Dictionary<string, int>> counts) =>
            counts.ToDictionary(t => t.Key, t =>
            {
                double total = t.Value.Values.Sum();
                return t.Value.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => total == 0 ? 0 : Math.Round(kv.Value / total, 4));
            });

        private static string Name(SqliteConnection corpus, string table, string idColumn, string id) =>
            corpus.Scalar<string>(null, $"SELECT name FROM {table} WHERE {idColumn}=$i AND valid_to_ms IS NULL ORDER BY valid_from_ms DESC LIMIT 1", ("$i", id)) ?? "";

        internal static string DisplayName(SqliteConnection corpus, string person)
        {
            string? name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return corpus.Scalar<string>(null, """
                SELECT n.value FROM identity_names n JOIN identities i ON i.identity_id=n.identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                WHERE COALESCE(pc.canonical_id, i.person_id)=$p AND n.valid_to_ms IS NULL ORDER BY n.valid_from_ms DESC LIMIT 1
                """, ("$p", person)) ?? person;
        }

        private static JsonNode? Node(object? value) => value == null ? null : JsonSerializer.SerializeToNode(value, Kernel.Jobs.KernelJson.Options);
    }
}
