using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    /// <summary>Something a monitor found. <c>data</c> holds ids and numbers; the page words it.</summary>
    [Api.ApiContract]
    public sealed record MonitorEventDto(string EventId, string Kind, string? RuleId, string? PersonId, string? PersonName, string? GuildId, string? ConversationId, long AtMs, JsonElement Data,
        bool Notified);

    [Api.ApiContract]
    public sealed record MonitorEventPageDto(IReadOnlyList<MonitorEventDto> Items);

    /// <summary>A person-activity watch (X42, <c>kind</c> person_activity) or a topic-surge watch (X43, topic_surge).</summary>
    [Api.ApiContract]
    public sealed record MonitorRuleDto(string WatchId, string Kind, string Name, bool Enabled, JsonElement Definition, long CreatedAtMs, long UpdatedAtMs, int Fired30d, long? LastFiredMs);

    [Api.ApiContract]
    public sealed record MonitorRuleListDto(IReadOnlyList<MonitorRuleDto> Rules);

    /// <summary>
    /// The monitors that need no model (X42, X43, X46, X47, X51): watches on people Klives chose, topics surging in a
    /// community, new people around him, bursts of what is said about him, and the health of Omniscience's own data.
    /// <para>
    /// Every finding is recorded once (by an id that says what it is about) and handed to the notice sink, which decides how
    /// it is delivered (quiet hours, digest, push). Only messages that arrived live raise anything (plan T5): a backfill or an
    /// import is history, not news. Nothing is about a person who is, or may be, under 18, or whose analysis is switched off.
    /// </para>
    /// </summary>
    public sealed partial class MonitorsModule : IntelModuleBase
    {
        public const string ModuleId = "monitors";
        public const string ScanKind = "scan";
        public const string SurgeKind = "surge";
        public const string PeopleKind = "people";
        public const string HealthKind = "health";
        public const string ForgetKind = "forget";
        /// <summary>operator.db <c>watchlists.kind</c> of a person-activity watch (X42).</summary>
        public const string PersonRuleKind = "person_activity";
        /// <summary>operator.db <c>watchlists.kind</c> of a topic-surge watch (X43).</summary>
        public const string TopicRuleKind = "topic_surge";

        private const long Day = 86_400_000L, Hour = 3_600_000L;
        private readonly INoticeSink? notices;

        /// <param name="notices">Where findings go (the signals module); null keeps them in this module's own log only.</param>
        public MonitorsModule(INoticeSink? notices = null) => this.notices = notices;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Monitors",
            Category = "signals",
            Description = "Watches on people Klives chose, topics surging in a community, new people around him, bursts of what is said about him, and the health of Omniscience's own data.",
            SubscribedChangeKinds = new[] { ChangeKinds.MessageInsert, ChangeKinds.PersonErase },
            Schedules = new[]
            {
                new ScheduleTrigger("scan", Every: TimeSpan.FromMinutes(1)),
                new ScheduleTrigger("surge", Every: TimeSpan.FromMinutes(15)),
                new ScheduleTrigger("people", Every: TimeSpan.FromHours(1)),
                new ScheduleTrigger("health", Every: TimeSpan.FromMinutes(10)),
            },
            DefaultLane = Lane.Cpu,
            MinorRestricted = true,
            Settings = new[]
            {
                new SettingDescriptor("monitors.freshness_hours", SettingTypes.Number, "12", "Messages sent or received longer ago than this raise nothing (they are history by the time Omniscience sees them).", 1, 168),
                new SettingDescriptor("monitors.new_people.enabled", SettingTypes.Bool, "true", "Tell Klives when someone new writes to him directly, joins his closest circle, or is new and rising fast in a community (X46)."),
                new SettingDescriptor("monitors.klives_tone.enabled", SettingTypes.Bool, "true", "Tell Klives when a community suddenly says much more about him than usual, in a negative or a positive way (X47)."),
                new SettingDescriptor("monitors.klives_tone.window_hours", SettingTypes.Int, "6", "How many hours a burst of what is said about Klives is counted over.", 1, 48),
                new SettingDescriptor("monitors.klives_tone.min_messages", SettingTypes.Int, "5", "How many messages about Klives, of one tone, make a burst at the least.", 2, 1000),
                new SettingDescriptor("monitors.klives_tone.factor", SettingTypes.Number, "3", "How many times the community's usual amount a burst must be.", 1.5, 50),
                new SettingDescriptor("monitors.surge.min_count", SettingTypes.Int, "10", "The fewest messages in a day that make a topic surge, unless a watch says otherwise.", 3, 100000),
                new SettingDescriptor("monitors.surge.factor", SettingTypes.Number, "3", "How many times its usual day a topic must be to surge, unless a watch says otherwise.", 1.5, 50),
                new SettingDescriptor("monitors.health.enabled", SettingTypes.Bool, "true", "Tell Klives when a source is down or silent, a backfill is stuck, the model fails, its cache efficiency falls, or jobs keep failing (X51)."),
                new SettingDescriptor("monitors.health.source_stall_hours", SettingTypes.Number, "12", "A busy source with no new message for this long has stalled.", 1, 168),
                new SettingDescriptor("monitors.health.source_min_daily", SettingTypes.Int, "20", "A source counts as busy when it brought at least this many messages a day over the last week.", 1, 100000),
                new SettingDescriptor("monitors.health.backfill_stall_hours", SettingTypes.Number, "24", "A backfill of a conversation that has not moved for this long is stuck.", 1, 720),
                new SettingDescriptor("monitors.health.module_stall_hours", SettingTypes.Number, "6", "A module with work queued that has finished none of it for this long is stalled.", 0.5, 720),
                new SettingDescriptor("monitors.health.tail_stall_minutes", SettingTypes.Number, "30", "The copy of v2's new messages runs every two minutes; with none completed for this long it has stalled.", 5, 10080),
                new SettingDescriptor("monitors.health.llm_error_share", SettingTypes.Number, "0.3", "The share of failed model calls in an hour that is worth telling.", 0.05, 1),
                new SettingDescriptor("monitors.health.cache_floor", SettingTypes.Number, "0.5", "The prefix-cache efficiency of background model work (over six hours) below which Klives is told.", 0, 1),
                new SettingDescriptor("monitors.health.cache_min_calls", SettingTypes.Int, "20", "How many background model calls in six hours the cache efficiency needs before it is judged.", 1, 100000),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "monitor findings, state and data health", """
CREATE TABLE mon_events(
  event_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  rule_id TEXT,
  person_id TEXT,
  guild_id TEXT,
  conversation_id TEXT,
  at_ms INTEGER NOT NULL,
  data_json TEXT NOT NULL,
  notified INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL
);
CREATE INDEX ix_mon_events_kind ON mon_events(kind, at_ms DESC);
CREATE INDEX ix_mon_events_rule ON mon_events(rule_id, at_ms DESC) WHERE rule_id IS NOT NULL;
CREATE TABLE mon_state(
  name TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
CREATE TABLE mon_seen(
  seen_key TEXT PRIMARY KEY,
  person_id TEXT,
  at_ms INTEGER NOT NULL
);
CREATE TABLE mon_circle(
  person_id TEXT PRIMARY KEY,
  community_id TEXT NOT NULL,
  since_ms INTEGER NOT NULL
);
CREATE TABLE mon_sources(
  source_id TEXT PRIMARY KEY,
  last_ingest_ms INTEGER NOT NULL,
  days_json TEXT NOT NULL
);
CREATE TABLE mon_health(
  issue_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  subject TEXT,
  detail_json TEXT NOT NULL,
  severity TEXT NOT NULL,
  first_ms INTEGER NOT NULL,
  last_ms INTEGER NOT NULL,
  resolved_ms INTEGER
);
"""),
        };

        internal ModuleContext Ctx => Context;

        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[]
        {
            new PersonActivityWatchTactic(this), new TopicSurgeTactic(this), new NewPersonRadarTactic(this), new KlivesToneWatchTactic(this), new DataHealthTactic(this),
        };

        private IReadOnlyList<ITactic>? tactics;

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            if (changes.Any(c => c.Kind == ChangeKinds.MessageInsert && c.SourceKind == SourceKinds.Live)) yield return new DirtySubject("stream", "live");
            foreach (var c in changes.Where(c => c.Kind == ChangeKinds.PersonErase)) yield return new DirtySubject("person", c.EntityId);
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            if (context.Dirty.Any(d => d.Kind == "stream") || context.DueSchedules.Any(s => s.Name == "scan")) yield return Job(ScanKind, "stream", "live", priority: 260);
            if (context.DueSchedules.Any(s => s.Name == "surge")) yield return Job(SurgeKind, "stream", "surge", priority: 120);
            if (context.DueSchedules.Any(s => s.Name == "people")) yield return Job(PeopleKind, "stream", "people", priority: 90);
            if (context.DueSchedules.Any(s => s.Name == "health")) yield return Job(HealthKind, "stream", "health", priority: 150);
            foreach (var d in context.Dirty.Where(d => d.Kind == "person")) yield return Job(ForgetKind, "person", d.Id, priority: 200);
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            ScanKind => await ScanAsync(job).ConfigureAwait(false),
            SurgeKind => await SurgesAsync(job).ConfigureAwait(false),
            PeopleKind => await PeopleAsync(job).ConfigureAwait(false),
            HealthKind => await HealthAsync(job).ConfigureAwait(false),
            ForgetKind => await ForgetAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown monitors job kind '{job.Job.Kind}'."),
        };

        private static string Json(object value) => JsonSerializer.Serialize(value, KernelJson.Options);

        internal bool Restricted(string person, long atMs) => MinorGuard.IsRestricted(Context.Policy.BandOf(person, atMs));

        private string KlivesDay(long ms) => Zones.LocalDate(ms, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private long FreshMs => (long)(Context.Settings.Get("monitors.freshness_hours", 12.0) * Hour);

        // ── state ────────────────────────────────────────────────────────────────

        private long State(string name, SqliteConnection? c = null)
        {
            if (c != null) return c.Scalar<long?>(null, "SELECT value FROM mon_state WHERE name=$n", ("$n", name)) ?? 0;
            using var conn = Context.Stores.Intel.OpenRead();
            return conn.Scalar<long?>(null, "SELECT value FROM mon_state WHERE name=$n", ("$n", name)) ?? 0;
        }

        private static void SetState(SqliteConnection c, SqliteTransaction tx, string name, long value) =>
            c.Exec(tx, "INSERT INTO mon_state(name, value) VALUES($n,$v) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("$n", name), ("$v", value));

        /// <summary>The first change at or after an instant (the log's times rise with its sequence; it has no index by time).</summary>
        internal static long FirstChangeAtOrAfter(SqliteConnection corpus, long atMs, long maxSeq)
        {
            long lo = 1, hi = maxSeq + 1;
            while (lo < hi)
            {
                long mid = lo + (hi - lo) / 2;
                var row = corpus.Query(null, "SELECT seq, at_ms FROM change_log WHERE seq >= $s ORDER BY seq LIMIT 1", r => (Seq: r.GetInt64(0), At: r.GetInt64(1)), ("$s", mid)).FirstOrDefault();
                if (row.Seq == 0 || row.At >= atMs) hi = mid;
                else lo = row.Seq + 1;
            }
            return lo;
        }

        // ── findings ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Records a finding once, by its id, and hands it to the notice sink. A finding about a person who is, or may be, a
        /// minor is neither recorded nor told (MinorGuard: no person-targeted alerts). Returns false when it was already there.
        /// </summary>
        private async Task<bool> FireAsync(string eventId, string kind, string? ruleId, string? person, string? guild, string? conversation, long atMs, object data, string severity,
            bool personTargeted, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            if (person != null && (Restricted(person, now) || AnalysisOff.IsOff(Context.Stores, person))) return false;
            string json = Json(data);
            bool fresh = await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT OR IGNORE INTO mon_events(event_id, kind, rule_id, person_id, guild_id, conversation_id, at_ms, data_json, notified, created_at_ms)
                VALUES($e,$k,$r,$p,$g,$c,$a,$d,0,$n)
                """, ("$e", eventId), ("$k", kind), ("$r", ruleId), ("$p", person), ("$g", guild), ("$c", conversation), ("$a", atMs), ("$d", json), ("$n", now)) == 1, ct).ConfigureAwait(false);
            if (!fresh) return false;
            if (notices != null)
            {
                bool told = await notices.NoticeAsync(new Notice("mon:" + eventId, kind, ModuleId, atMs, data, severity, person, null, guild, conversation, personTargeted), ct).ConfigureAwait(false);
                if (told) await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE mon_events SET notified=1 WHERE event_id=$e", ("$e", eventId)), ct).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>Recent findings, newest first; people who are now protected or switched off are left out.</summary>
        public MonitorEventPageDto Events(string? kind, string? ruleId, int limit = 50)
        {
            var kinds = (kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            List<(string Id, string Kind, string? Rule, string? Person, string? Guild, string? Conversation, long At, string Data, bool Notified)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                var where = new List<string> { "1=1" };
                var args = new List<(string, object?)> { ("$l", Math.Clamp(limit, 1, 500)) };
                if (kinds.Count > 0)
                {
                    var (list, inArgs) = SqlHelpers.InList(kinds, "$k");
                    where.Add($"kind IN ({list})");
                    args.AddRange(inArgs);
                }
                if (!string.IsNullOrWhiteSpace(ruleId)) { where.Add("rule_id=$r"); args.Add(("$r", ruleId.Trim())); }
                rows = c.Query(null, $"SELECT event_id, kind, rule_id, person_id, guild_id, conversation_id, at_ms, data_json, notified FROM mon_events WHERE {string.Join(" AND ", where)} ORDER BY at_ms DESC, event_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6), r.GetString(7), r.GetInt64(8) == 1),
                    args.ToArray());
            }
            long now = Context.Clock.NowMs;
            var off = AnalysisOff.Load(Context.Stores);
            rows = rows.Where(r => r.Person == null || (!off.Person(r.Person) && !Restricted(r.Person, now))).ToList();
            Dictionary<string, string?> names;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                names = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", rows.Select(r => r.Person).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            return new MonitorEventPageDto(rows.Select(r =>
            {
                using var doc = JsonDocument.Parse(r.Data);
                return new MonitorEventDto(r.Id, r.Kind, r.Rule, r.Person, r.Person == null ? null : names.GetValueOrDefault(r.Person), r.Guild, r.Conversation, r.At, doc.RootElement.Clone(), r.Notified);
            }).ToList());
        }

        // ── forgetting ───────────────────────────────────────────────────────────

        /// <summary>A person was forgotten or switched off: their findings and what was kept about them here go; a finding that
        /// listed their messages keeps only the messages that still exist.</summary>
        private async Task<string?> ForgetAsync(JobContext job)
        {
            string person = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Forget job without a person.");
            var listed = new List<(string Id, string Data)>();
            using (var c = Context.Stores.Intel.OpenRead())
                listed = c.Query(null, "SELECT event_id, data_json FROM mon_events WHERE data_json LIKE '%\"messageIds\"%'", r => (r.GetString(0), r.GetString(1)));
            var cited = listed.SelectMany(l => MessageIds(l.Data)).Distinct(StringComparer.Ordinal).ToList();
            HashSet<string> exists;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                exists = corpus.QueryIn(null, "SELECT message_id FROM messages WHERE message_id IN ({keys})", cited, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            int removed = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int n = c.Exec(tx, "DELETE FROM mon_events WHERE person_id=$p OR data_json LIKE $l", ("$p", person), ("$l", "%" + JsonSerializer.Serialize(person) + "%"));
                n += c.Exec(tx, "DELETE FROM mon_seen WHERE person_id=$p", ("$p", person));
                n += c.Exec(tx, "DELETE FROM mon_circle WHERE person_id=$p", ("$p", person));
                foreach (var (id, data) in listed)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(data)?.AsObject();
                    if (node?["messageIds"] is not System.Text.Json.Nodes.JsonArray ids) continue;
                    int before = ids.Count;
                    for (int i = ids.Count - 1; i >= 0; i--)
                        if (ids[i]?.GetValue<string>() is { } m && !exists.Contains(m)) ids.RemoveAt(i);
                    if (ids.Count != before) n += c.Exec(tx, "UPDATE mon_events SET data_json=$d WHERE event_id=$e", ("$d", node.ToJsonString()), ("$e", id));
                }
                return n;
            }, job.Cancellation).ConfigureAwait(false);
            return Json(new { person, removed });
        }

        private static IEnumerable<string> MessageIds(string data)
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("messageIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var id in ids.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String) yield return id.GetString()!;
        }

        // ── rules (X42, X43) ─────────────────────────────────────────────────────

        internal sealed record PersonRuleDefinition(List<string> Persons, int? AbsenceDays, List<string> Guilds);

        internal sealed record TopicRuleDefinition(string? ConceptId, string? Phrase, List<string> Guilds, List<string> Conversations, int? MinCount, double? Factor);

        internal sealed record Rule<T>(string Id, string Name, bool Enabled, T Definition, long CreatedAtMs, long UpdatedAtMs);

        internal List<Rule<T>> LoadRules<T>(string kind, string? watchId = null)
        {
            using var op = Context.Stores.Operator.OpenRead();
            var result = new List<Rule<T>>();
            foreach (var (id, name, json, enabled, created, updated) in op.Query(null,
                         "SELECT watch_id, name, definition_json, enabled, created_at_ms, updated_at_ms FROM watchlists WHERE kind=$k AND ($i IS NULL OR watch_id=$i) ORDER BY created_at_ms, watch_id",
                         r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, r.GetInt64(4), r.GetInt64(5)), ("$k", kind), ("$i", watchId)))
            {
                T? definition;
                try { definition = JsonSerializer.Deserialize<T>(json, KernelJson.Options); }
                catch (JsonException ex)
                {
                    Context.LogError($"[Monitors] watch {id} cannot be read; it is skipped until it is saved again.", ex);
                    continue;
                }
                if (definition != null) result.Add(new Rule<T>(id, name, enabled, definition, created, updated));
            }
            return result;
        }

        public MonitorRuleListDto Rules(string? kind = null)
        {
            var kinds = kind is PersonRuleKind or TopicRuleKind ? new[] { kind } : new[] { PersonRuleKind, TopicRuleKind };
            var rows = new List<(string Id, string Kind, string Name, bool Enabled, string Json, long Created, long Updated)>();
            using (var op = Context.Stores.Operator.OpenRead())
                foreach (string k in kinds)
                    rows.AddRange(op.Query(null, "SELECT watch_id, kind, name, enabled, definition_json, created_at_ms, updated_at_ms FROM watchlists WHERE kind=$k ORDER BY created_at_ms, watch_id",
                        r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, r.GetString(4), r.GetInt64(5), r.GetInt64(6)), ("$k", k)));
            long since = Context.Clock.NowMs - 30 * Day;
            Dictionary<string, (int N, long Last)> fired;
            using (var c = Context.Stores.Intel.OpenRead())
                fired = c.QueryInWith("SELECT rule_id, COUNT(*), MAX(at_ms) FROM mon_events WHERE at_ms >= $s AND rule_id IN ({keys}) GROUP BY rule_id", rows.Select(r => r.Id).ToList(),
                    r => (r.GetString(0), r.GetInt32(1), r.GetInt64(2)), ("$s", since)).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.Ordinal);
            return new MonitorRuleListDto(rows.Select(r =>
            {
                using var doc = JsonDocument.Parse(r.Json);
                var f = fired.GetValueOrDefault(r.Id);
                return new MonitorRuleDto(r.Id, r.Kind, r.Name, r.Enabled, doc.RootElement.Clone(), r.Created, r.Updated, f.N, f.N == 0 ? null : f.Last);
            }).ToList());
        }

        private async Task<MonitorRuleDto> SaveRuleAsync(string kind, string? watchId, string name, object definition, string actor, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            string id = string.IsNullOrWhiteSpace(watchId) ? (kind == PersonRuleKind ? "pw" : "tw") + Guid.NewGuid().ToString("N")[..12] : watchId.Trim();
            string json = Json(definition);
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                string? existing = c.Scalar<string>(tx, "SELECT kind FROM watchlists WHERE watch_id=$w", ("$w", id));
                if (existing != null && existing != kind) throw new ArgumentException($"'{id}' is a watch of another kind.");
                if (existing == null && !string.IsNullOrWhiteSpace(watchId)) throw new KeyNotFoundException($"No watch '{watchId}'.");
                c.Exec(tx, """
                    INSERT INTO watchlists(watch_id, kind, name, definition_json, enabled, created_at_ms, updated_at_ms) VALUES($w,$k,$n,$d,1,$t,$t)
                    ON CONFLICT(watch_id) DO UPDATE SET name=excluded.name, definition_json=excluded.definition_json, enabled=1, updated_at_ms=excluded.updated_at_ms
                    """, ("$w", id), ("$k", kind), ("$n", name), ("$d", json), ("$t", now));
            }, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, "monitor.rule.save", id, new { kind }, ct).ConfigureAwait(false);
            return Rules(kind).Rules.Single(r => r.WatchId == id);
        }

        public async Task<bool> DeleteRuleAsync(string watchId, string actor, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(watchId)) throw new ArgumentException("'watchId' is required.");
            int n = await Context.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM watchlists WHERE watch_id=$w AND kind IN ($a,$b)", ("$w", watchId.Trim()), ("$a", PersonRuleKind), ("$b", TopicRuleKind)), ct)
                .ConfigureAwait(false);
            if (n > 0) await OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, "monitor.rule.delete", watchId.Trim(), null, ct).ConfigureAwait(false);
            return n > 0;
        }

        public async Task<bool> SetRuleEnabledAsync(string watchId, bool enabled, string actor, CancellationToken ct)
        {
            int n = await Context.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "UPDATE watchlists SET enabled=$e, updated_at_ms=$t WHERE watch_id=$w AND kind IN ($a,$b)",
                ("$e", enabled ? 1 : 0), ("$t", Context.Clock.NowMs), ("$w", (watchId ?? "").Trim()), ("$a", PersonRuleKind), ("$b", TopicRuleKind)), ct).ConfigureAwait(false);
            if (n > 0) await OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, enabled ? "monitor.rule.enable" : "monitor.rule.disable", watchId, null, ct).ConfigureAwait(false);
            return n > 0;
        }

        /// <summary>Saves a person-activity watch (X42). Refused for anyone who is, or may be, under 18, or whose analysis is off.</summary>
        public async Task<MonitorRuleDto> SavePersonRuleAsync(string? watchId, string? name, IReadOnlyList<string> persons, int? absenceDays, IReadOnlyList<string> guilds, string actor, CancellationToken ct)
        {
            var people = persons.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Context.Policy.Canonical(p.Trim())).Distinct(StringComparer.Ordinal).ToList();
            if (people.Count is 0 or > 20) throw new ArgumentException("Give between 1 and 20 people to watch.");
            if (absenceDays is { } d && (d < 1 || d > 3650)) throw new ArgumentException("'absenceDays' must be between 1 and 3650.");
            var places = guilds.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (absenceDays == null && places.Count == 0) throw new ArgumentException("Say what to watch for: a return after 'absenceDays' of silence, and/or posting in the selected servers.");
            long now = Context.Clock.NowMs;
            var off = AnalysisOff.Load(Context.Stores);
            Dictionary<string, string?> names;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                names = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", people, r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                foreach (string p in people.Where(p => !names.ContainsKey(p))) throw new KeyNotFoundException($"No person '{p}'.");
                var known = corpus.QueryIn(null, "SELECT guild_id FROM guilds WHERE guild_id IN ({keys})", places, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                foreach (string g in places.Where(g => !known.Contains(g))) throw new KeyNotFoundException($"No server '{g}'.");
            }
            foreach (string p in people)
            {
                if (Restricted(p, now)) throw new TacticRefusedException($"{names[p] ?? p} is, or may be, under 18: minors are never the subject of an alert.");
                if (off.Person(p)) throw new TacticRefusedException($"Analysis is switched off for {names[p] ?? p}, so they are not watched.");
            }
            string title = string.IsNullOrWhiteSpace(name) ? "Activity of " + string.Join(", ", people.Select(p => names[p] ?? p)) : name.Trim();
            if (title.Length > 120) title = title[..120];
            return await SaveRuleAsync(PersonRuleKind, watchId, title, new PersonRuleDefinition(people, absenceDays, places), actor, ct).ConfigureAwait(false);
        }

        /// <summary>Saves a topic-surge watch (X43): a taught concept (X19) or a phrase, everywhere or in given servers and conversations.</summary>
        public async Task<MonitorRuleDto> SaveTopicRuleAsync(string? watchId, string? name, string? conceptId, string? phrase, IReadOnlyList<string> guilds, IReadOnlyList<string> conversations,
            int? minCount, double? factor, string actor, CancellationToken ct)
        {
            conceptId = string.IsNullOrWhiteSpace(conceptId) ? null : conceptId.Trim();
            phrase = string.IsNullOrWhiteSpace(phrase) ? null : phrase.Trim();
            if ((conceptId == null) == (phrase == null)) throw new ArgumentException("Give either a taught concept ('conceptId') or a 'phrase' to watch.");
            if (phrase != null && (phrase.Length is < 2 or > 100 || !phrase.Any(char.IsLetterOrDigit))) throw new ArgumentException("The phrase must be 2 to 100 characters, with at least one letter or digit.");
            if (minCount is { } m && (m < 3 || m > 100_000)) throw new ArgumentException("'minCount' must be between 3 and 100000.");
            if (factor is { } f && (f < 1.5 || f > 50)) throw new ArgumentException("'factor' must be between 1.5 and 50.");
            string? conceptName = null;
            if (conceptId != null)
            {
                using var intel = Context.Stores.Intel.OpenRead();
                if (intel.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='classify_concepts'") == 0)
                    throw new ArgumentException("This build has no taught concepts; watch a phrase instead.");
                var concept = intel.Query(null, "SELECT name, state FROM classify_concepts WHERE concept_id=$c", r => (Name: r.GetString(0), State: r.GetString(1)), ("$c", conceptId)).FirstOrDefault();
                if (concept.Name == null) throw new KeyNotFoundException($"No taught concept '{conceptId}'.");
                if (concept.State != "active") throw new ArgumentException($"The concept '{concept.Name}' is not active yet ({concept.State}); a surge needs it to be recognised in messages.");
                conceptName = concept.Name;
            }
            var places = guilds.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var chats = conversations.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.Ordinal).ToList();
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var known = corpus.QueryIn(null, "SELECT guild_id FROM guilds WHERE guild_id IN ({keys})", places, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                foreach (string g in places.Where(g => !known.Contains(g))) throw new KeyNotFoundException($"No server '{g}'.");
                var knownChats = corpus.QueryIn(null, "SELECT conversation_id FROM conversations WHERE conversation_id IN ({keys})", chats, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                foreach (string c in chats.Where(c => !knownChats.Contains(c))) throw new KeyNotFoundException($"No conversation '{c}'.");
            }
            string title = string.IsNullOrWhiteSpace(name) ? "Surge of " + (phrase != null ? "\"" + phrase + "\"" : conceptName) : name.Trim();
            if (title.Length > 120) title = title[..120];
            return await SaveRuleAsync(TopicRuleKind, watchId, title, new TopicRuleDefinition(conceptId, phrase, places, chats, minCount, factor), actor, ct).ConfigureAwait(false);
        }
    }
}
