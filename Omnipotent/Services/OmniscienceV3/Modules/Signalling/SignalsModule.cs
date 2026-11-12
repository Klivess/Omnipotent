using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Signalling
{
    /// <summary>What the classification ensemble read in one message, as far as the signals need it.</summary>
    public sealed record MessageReading(string? AboutKlives, double AboutKlivesProbability, string? DialogueAct, double DialogueActProbability, bool NonAuthored, int? Labelset);

    /// <summary>Where the signals get a message's meaning from (the A2 ensemble's labels; tests supply their own).</summary>
    public interface ISignalReadings
    {
        /// <summary>Whether a new message can be expected to get a reading soon. When it can, a signal that is only a
        /// name match waits a short while for the reading instead of alerting on the name alone.</summary>
        bool Expected { get; }

        IReadOnlyDictionary<string, MessageReading> For(IReadOnlyCollection<string> messageIds);
    }

    /// <summary>The production readings: the active label set of the classification module.</summary>
    public sealed class ClassifiedReadings : ISignalReadings
    {
        private readonly OmniscienceStores stores;

        public ClassifiedReadings(OmniscienceStores stores) => this.stores = stores;

        public bool Expected
        {
            get
            {
                using var c = stores.Intel.OpenRead();
                return ClassificationReader.ActiveLabelset(c) != null;
            }
        }

        private static PackedLabel? Answered(MessageLabels labels, string task)
        {
            foreach (var l in labels.Labels)
                if (l.Task == task && !l.Abstained) return l;
            return null;
        }

        public IReadOnlyDictionary<string, MessageReading> For(IReadOnlyCollection<string> messageIds)
        {
            var labels = ClassificationReader.ForMessages(stores, messageIds);
            var result = new Dictionary<string, MessageReading>(StringComparer.Ordinal);
            foreach (var (id, l) in labels)
            {
                var about = Answered(l, "about_klives");
                var act = Answered(l, "dialogue_act");
                result[id] = new MessageReading(about?.Label, about?.Probability ?? 0, act?.Label, act?.Probability ?? 0, l.NonAuthored, l.Labelset);
            }
            return result;
        }
    }

    /// <summary>
    /// Signals for Klives (plan "Signals, briefs and search"): the radar (people talking about him), semantic watchlists
    /// (N11: rules in plain language) and the reply inbox (N9: what is waiting for his answer).
    /// <para>
    /// What judges meaning is the classification ensemble and, for watch rules, the model; what is exact stays exact
    /// (an @-mention, a reply to his message, a name he confirmed, whose turn it is in a direct chat). Alerts only ever
    /// fire for live messages inside a freshness window (T5): a backfill or an import never pages him. Everything is
    /// durable: an alert or an inbox item is a row, not something held in memory (F527, F528).
    /// </para>
    /// </summary>
    public sealed partial class SignalsModule : IntelModuleBase, IExploreSourceProvider
    {
        public const string ModuleId = "signals";
        public const string SummaryModel = "signals.summary";
        public const string AlertsDataset = "signals.alerts";
        public const string InboxDataset = "signals.inbox";
        public const string ScanKind = "scan";
        public const string ConfirmKind = "confirm";
        public const string ActivateKind = "activate";
        /// <summary>operator.db <c>watchlists.kind</c> of the rules this module runs.</summary>
        public const string SemanticWatch = "semantic";
        /// <summary>operator.db <c>gold_labels.kind</c> of Klives's verdicts on alerts.</summary>
        public const string GoldKind = "signal";
        /// <summary>operator.db <c>dismissals.kind</c> of inbox items Klives put away.</summary>
        public const string DismissalKind = "inbox";

        private readonly ISignalReadings? readingsOverride;
        private readonly Func<IReadOnlyList<string>>? confirmedAliases;
        private ISignalReadings readings = null!;

        /// <param name="readings">Where message meanings come from; null uses the classification module's labels.</param>
        /// <param name="confirmedAliases">Names of Klives that he confirmed elsewhere (the knowledge store's accepted
        /// nicknames); they join his account names and the names in the settings. Learned names that nobody confirmed
        /// never alert.</param>
        public SignalsModule(ISignalReadings? readings = null, Func<IReadOnlyList<string>>? confirmedAliases = null)
        {
            readingsOverride = readings;
            this.confirmedAliases = confirmedAliases;
        }

        private static DimensionBinding D(string dim, string column) => new(dim, column);

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Signals",
            Category = "signals",
            Description = "The radar (people talking about Klives), watch rules written in plain language, and the reply inbox. Live messages only; every alert is kept.",
            SubscribedChangeKinds = new[] { ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.PersonErase },
            Schedules = new[] { new ScheduleTrigger("scan", Every: TimeSpan.FromSeconds(30)) },
            DefaultLane = Lane.Cpu,
            Settings = new[]
            {
                new SettingDescriptor("signals.freshness_hours", SettingTypes.Number, "12", "Only a live message that arrived within this many hours can raise an alert; an edit only alerts for a message this young (T5).", 0.25, 168),
                new SettingDescriptor("signals.burst_minutes", SettingTypes.Int, "10", "Hits in one server (or one chat) this close together are one alert.", 1, 240),
                new SettingDescriptor("signals.max_pushed_per_hour", SettingTypes.Int, "6", "Above this many pushed alerts in an hour the rest wait for the digest.", 1, 1000),
                new SettingDescriptor("signals.quiet_from", SettingTypes.String, "\"23:00\"", "Start of quiet hours in Klives's zone (HH:mm); alerts are kept but not pushed. Equal to the end: no quiet hours."),
                new SettingDescriptor("signals.quiet_to", SettingTypes.String, "\"07:30\"", "End of quiet hours in Klives's zone (HH:mm)."),
                new SettingDescriptor("signals.radar.named_probability", SettingTypes.Number, "0.6", "How sure the ensemble must be that a message names Klives.", 0.3, 0.99),
                new SettingDescriptor("signals.radar.indirect_probability", SettingTypes.Number, "0.75", "How sure it must be that a message is about Klives without naming him.", 0.3, 0.99),
                new SettingDescriptor("signals.radar.aliases", SettingTypes.Json, "[]", "Extra names Klives is called by (a JSON list of strings). Matched as whole words; code and links are ignored."),
                new SettingDescriptor("signals.radar.account_names", SettingTypes.Bool, "true", "Also match the current names of Klives's own accounts."),
                new SettingDescriptor("signals.radar.reading_grace_seconds", SettingTypes.Int, "90", "How long a name match waits for the ensemble's reading before it alerts on the name alone.", 0, 3600),
                new SettingDescriptor("signals.inbox.stale_days", SettingTypes.Int, "30", "An unanswered message older than this leaves the inbox as stale.", 1, 3650),
                new SettingDescriptor("signals.inbox.act_probability", SettingTypes.Number, "0.5", "How sure the ensemble must be that a message is a question or a request.", 0.3, 0.99),
                new SettingDescriptor("signals.inbox.channel_answer_minutes", SettingTypes.Int, "120", "In a server channel or a group, something Klives writes this soon after a message counts as his answer (a direct reply always does).", 1, 10080),
                new SettingDescriptor("signals.inbox.nudge_hours", SettingTypes.Number, "24", "An item waiting this long is nudged once (X49). 0 turns nudges off.", 0, 8760),
                new SettingDescriptor("signals.watch.max_checks_per_hour", SettingTypes.Int, "120", "Messages one watch rule may put to the model per hour.", 1, 5000),
                new SettingDescriptor("signals.watch.match_confidence", SettingTypes.Number, "0.6", "How sure the model must be that a message matches a watch rule.", 0.3, 0.99),
            },
            Datasets = new[]
            {
                new DatasetDescriptor(AlertsDataset, "Alerts", new[]
                {
                    D("time.occurred", "occurred_at_ms"), D("time.recorded", "created_at_ms"), D("person.author", "person_id"), D("where.conversation", "conversation_id"),
                    D("where.guild", "guild_id"), D("where.platform", "platform"), D("content.event", "kind"), D("prov.status", "state"), D("prov.confidence", "probability"),
                }, new[]
                {
                    new MeasureDescriptor("count", "Alerts", "count", null), new MeasureDescriptor("hits", "Messages", "sum", "hit_count"),
                    new MeasureDescriptor("people", "People", "distinct", "person_id"),
                }, "alert", "omniscience.persons.read", MinorFiltered: true),
                new DatasetDescriptor(InboxDataset, "Reply inbox", new[]
                {
                    D("time.occurred", "sent_at_ms"), D("time.recorded", "created_at_ms"), D("person.author", "person_id"), D("where.conversation", "conversation_id"),
                    D("where.guild", "guild_id"), D("where.platform", "platform"), D("where.kind", "conversation_kind"), D("content.event", "kind"), D("prov.status", "state"),
                }, new[] { new MeasureDescriptor("count", "Items", "count", null), new MeasureDescriptor("people", "People", "distinct", "person_id") },
                    "inbox_item", "omniscience.persons.read", MinorFiltered: true),
                new DatasetDescriptor(NoticesDataset, "Notices", new[]
                {
                    D("time.occurred", "occurred_at_ms"), D("time.recorded", "created_at_ms"), D("person.subject", "person_id"), D("person.partner", "other_person_id"),
                    D("where.guild", "guild_id"), D("where.conversation", "conversation_id"), D("content.event", "kind"), D("prov.module", "module_id"), D("prov.status", "state"),
                }, new[] { new MeasureDescriptor("count", "Notices", "count", null), new MeasureDescriptor("people", "People", "distinct", "person_id") },
                    "notice", "omniscience.persons.read", MinorFiltered: true),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(SummaryModel, "Signals summary", "omniscience.persons.read", 1, 60_000, "Unseen alerts by kind and what is waiting for a reply."),
            },
        };

        public IReadOnlyList<ExploreSource> ExploreSources { get; } = new[]
        {
            new ExploreSource(AlertsDataset, "sig_alerts", "alert_id", "occurred_at_ms", "change_seq"),
            new ExploreSource(InboxDataset, "sig_inbox", "item_id", "sent_at_ms", "change_seq"),
            new ExploreSource(NoticesDataset, "sig_notices", "notice_id", "occurred_at_ms", "change_seq"),
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "alerts, reply inbox, watch rule state", """
CREATE TABLE sig_alerts(
  alert_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  rule_id TEXT,
  rule_name TEXT,
  reason TEXT NOT NULL,
  message_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  group_key TEXT NOT NULL,
  guild_id TEXT,
  platform TEXT NOT NULL,
  person_id TEXT,
  occurred_at_ms INTEGER NOT NULL,
  last_hit_ms INTEGER NOT NULL,
  hit_count INTEGER NOT NULL DEFAULT 1,
  hits_json TEXT NOT NULL,
  probability REAL,
  state TEXT NOT NULL,
  delivery TEXT NOT NULL,
  source_deleted_at_ms INTEGER,
  labelset INTEGER,
  source_seq INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_sig_alerts_time ON sig_alerts(created_at_ms DESC, alert_id DESC);
CREATE INDEX ix_sig_alerts_state ON sig_alerts(state, created_at_ms DESC);
CREATE INDEX ix_sig_alerts_open ON sig_alerts(kind, group_key, last_hit_ms);
CREATE INDEX ix_sig_alerts_person ON sig_alerts(person_id);
CREATE TABLE sig_alert_hits(
  kind TEXT NOT NULL,
  rule_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  alert_id TEXT NOT NULL,
  PRIMARY KEY(kind, rule_id, message_id)
) WITHOUT ROWID;
CREATE INDEX ix_sig_alert_hits_message ON sig_alert_hits(message_id);
CREATE INDEX ix_sig_alert_hits_alert ON sig_alert_hits(alert_id);
CREATE TABLE sig_pending(
  message_id TEXT PRIMARY KEY,
  due_ms INTEGER NOT NULL
) WITHOUT ROWID;
CREATE TABLE sig_inbox(
  item_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  message_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  conversation_kind TEXT,
  guild_id TEXT,
  platform TEXT NOT NULL,
  person_id TEXT NOT NULL,
  sent_at_ms INTEGER NOT NULL,
  state TEXT NOT NULL,
  reason TEXT NOT NULL,
  probability REAL,
  resolved_at_ms INTEGER,
  resolved_by TEXT,
  snoozed_until_ms INTEGER,
  nudged_at_ms INTEGER,
  live INTEGER NOT NULL DEFAULT 0,
  source_seq INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_sig_inbox_state ON sig_inbox(state, sent_at_ms DESC, item_id DESC);
CREATE INDEX ix_sig_inbox_conversation ON sig_inbox(conversation_id, state);
CREATE INDEX ix_sig_inbox_message ON sig_inbox(message_id);
CREATE INDEX ix_sig_inbox_person ON sig_inbox(person_id);
CREATE TABLE sig_watch_state(
  watch_id TEXT PRIMARY KEY,
  state TEXT NOT NULL,
  reason TEXT,
  text_hash TEXT,
  encoder_model TEXT,
  threshold REAL,
  calibration_json TEXT,
  calibrated_at_ms INTEGER,
  checked INTEGER NOT NULL DEFAULT 0,
  matched INTEGER NOT NULL DEFAULT 0,
  last_match_ms INTEGER,
  hour_bucket INTEGER,
  hour_checked INTEGER NOT NULL DEFAULT 0,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE sig_watch_candidates(
  watch_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  similarity REAL,
  queued_at_ms INTEGER NOT NULL,
  PRIMARY KEY(watch_id, message_id)
) WITHOUT ROWID;
CREATE TABLE sig_watch_judgements(
  watch_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  source TEXT NOT NULL,
  matched INTEGER NOT NULL,
  confidence REAL,
  similarity REAL,
  at_ms INTEGER NOT NULL,
  PRIMARY KEY(watch_id, message_id, source)
) WITHOUT ROWID;
CREATE TABLE sig_state(
  name TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
INSERT INTO sig_state(name, value) VALUES('change_seq', 0);
"""),
            NoticesMigration,
        };

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            readings = readingsOverride ?? new ClassifiedReadings(context.Stores);
        }

        internal ModuleContext Ctx => Context;

        public override IReadOnlyList<ITactic> Tactics => tactics ??= new ITactic[] { new SemanticWatchTactic(this), new ReplyNudgesTactic(this) };

        private IReadOnlyList<ITactic>? tactics;

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Count == 0 ? Array.Empty<DirtySubject>() : new[] { new DirtySubject("stream", "scan") };

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            if (context.Dirty.Count > 0 || context.VersionChanged || context.DueSchedules.Any(s => s.Name == "scan"))
                yield return Job(ScanKind, "stream", "scan", priority: 300);
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            ScanKind => await ScanAsync(job).ConfigureAwait(false),
            ConfirmKind => await ConfirmAsync(job).ConfigureAwait(false),
            ActivateKind => await ActivateAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown signals job kind '{job.Job.Kind}'."),
        };

        // ── small shared pieces ──────────────────────────────────────────────────

        /// <summary>The next value of the module's change counter (the Explore mirror follows the tables by it). Call inside the write.</summary>
        private static long NextChange(SqliteConnection c, SqliteTransaction tx) =>
            c.Scalar<long>(tx, "UPDATE sig_state SET value = value + 1 WHERE name='change_seq' RETURNING value");

        private static long Cursor(SqliteConnection c, string name, SqliteTransaction? tx = null) => c.Scalar<long?>(tx, "SELECT value FROM sig_state WHERE name=$n", ("$n", name)) ?? 0;

        private static void SetCursor(SqliteConnection c, SqliteTransaction tx, string name, long value) =>
            c.Exec(tx, "INSERT INTO sig_state(name, value) VALUES($n,$v) ON CONFLICT(name) DO UPDATE SET value=excluded.value", ("$n", name), ("$v", value));

        private long FreshnessMs => (long)(Context.Settings.Get("signals.freshness_hours", 12.0) * 3_600_000);

        private long BurstMs => Context.Settings.Get("signals.burst_minutes", 10) * 60_000L;

        private long StaleMs => Context.Settings.Get("signals.inbox.stale_days", 30) * 86_400_000L;

        /// <summary>Whether <paramref name="nowMs"/> falls in Klives's quiet hours (a window that may cross midnight).</summary>
        internal bool Quiet(long nowMs)
        {
            if (!TimeOnly.TryParseExact(Context.Settings.Get("signals.quiet_from", "23:00"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                || !TimeOnly.TryParseExact(Context.Settings.Get("signals.quiet_to", "07:30"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
                || from == to) return false;
            var local = TimeOnly.FromDateTime(Zones.ToLocal(nowMs, Context.Settings.KlivesZone));
            return from < to ? local >= from && local < to : local >= from || local < to;
        }

        /// <summary>How a new alert is delivered right now: pushed, held by quiet hours, or held because the hour is full.</summary>
        private string DeliveryFor(SqliteConnection c, SqliteTransaction tx, long now)
        {
            if (Quiet(now)) return "quiet";
            long pushed = c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_alerts WHERE delivery='pushed' AND created_at_ms > $t", ("$t", now - 3_600_000L));
            return pushed >= Context.Settings.Get("signals.max_pushed_per_hour", 6) ? "digest" : "pushed";
        }

        private static string Json(object value) => JsonSerializer.Serialize(value, KernelJson.Options);
    }
}
