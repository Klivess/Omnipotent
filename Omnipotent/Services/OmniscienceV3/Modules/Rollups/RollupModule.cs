using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Rollups
{
    /// <summary>
    /// The rollup layer (plan "Analytics & read models": "Rollups: per (person, local-day), maintained incrementally from
    /// ingest_seq. Modules compute from rollups (O(days)), not raw messages" — the fix for 1–2 h per person, F001/F478).
    /// <list type="bullet">
    /// <item><b>Cells are recomputed, never adjusted.</b> A change marks cells dirty; a dirty cell is rebuilt from the corpus for
    /// its exact window. Edits, deletions, reactions removed, merges and late backfill therefore cannot make counts drift
    /// (F488/F489/F493/F500).</item>
    /// <item><b>Grains:</b> person-day in the person's own zone for that month (unknown → UTC, R4), the person's outgoing
    /// pair-days (replies, mentions, DMs, reactions, reply latencies), conversation/guild/global days in Klives's zone,
    /// and UTC quarter-hour slots per person-month (the zone-independent base of zone inference, T4).</item>
    /// <item><b>Labels</b> come from the active label set; every cell records which, so charts never mix label sets (A3).
    /// When a new label set becomes active the label columns are rebuilt in the background.</item>
    /// <item><b>Exclusions</b> (plan analytics edge cases): bots and system messages are excluded; deleted messages count as
    /// activity but not as style; non-authored text (forwards, code, links, quotes, copypasta) is excluded from style
    /// counts; day-precision timestamps are excluded from hour statistics (R4).</item>
    /// <item><b>Outbox:</b> every rebuilt cell appends to <c>rollup_changes</c>, which projections consume.</item>
    /// </list>
    /// </summary>
    public sealed partial class RollupModule : IntelModuleBase
    {
        public const string ModuleId = "rollups";

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Rollups",
            Category = "analytics",
            Description = "Per person-day (in the person's own zone), pair-day, conversation-day, guild-day and global-day counts, label distributions, hours " +
                          "and reply latencies, rebuilt exactly when their messages change; plus UTC activity slots and time-zone inference.",
            DependsOn = new[] { ClassificationModule.ModuleId },
            SubscribedChangeKinds = new[]
            {
                ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.ReactionChange, ChangeKinds.AttachmentAdd,
                ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge, ChangeKinds.PersonErase, ChangeKinds.PersonReanalyse, ChangeKinds.IdentityUpsert,
            },
            Schedules = new[]
            {
                new ScheduleTrigger("drain", Every: TimeSpan.FromSeconds(30)),
                new ScheduleTrigger("zones", DailyAtLocal: new TimeOnly(4, 40)),
                new ScheduleTrigger("drift", DailyAtLocal: new TimeOnly(5, 20)),
            },
            DefaultLane = Lane.Cpu,
            Settings = new[]
            {
                new SettingDescriptor("rollups.zone_min_messages", Kernel.SettingTypes.Int, "150", "Messages a person needs in a month before their zone is inferred for it.", 20, 100000),
                new SettingDescriptor("rollups.zone_min_days", Kernel.SettingTypes.Int, "8", "Active days a person needs in a month before their zone is inferred for it.", 2, 31),
                new SettingDescriptor("rollups.drift_psi_alert", Kernel.SettingTypes.Number, "0.2", "Weekly label-distribution shift (PSI) that raises a drift alert.", 0.01, 5),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "rollup cells, slots, zones, outbox", """
CREATE TABLE rollup_cursors(
  name TEXT PRIMARY KEY,
  seq INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
CREATE TABLE rollup_dirty(
  kind TEXT NOT NULL,
  key TEXT NOT NULL,
  day TEXT NOT NULL,
  since_seq INTEGER NOT NULL,
  PRIMARY KEY(kind, key, day)
) WITHOUT ROWID;
CREATE TABLE rollup_person_day(
  person_id TEXT NOT NULL,
  day TEXT NOT NULL,
  zone TEXT NOT NULL,
  zone_known INTEGER NOT NULL,
  msgs INTEGER NOT NULL,
  authored INTEGER NOT NULL,
  deleted INTEGER NOT NULL,
  edited INTEGER NOT NULL,
  replies INTEGER NOT NULL,
  forwarded INTEGER NOT NULL,
  non_authored INTEGER NOT NULL,
  with_attachments INTEGER NOT NULL,
  with_links INTEGER NOT NULL,
  chars INTEGER NOT NULL,
  words INTEGER NOT NULL,
  dm_msgs INTEGER NOT NULL,
  group_msgs INTEGER NOT NULL,
  server_msgs INTEGER NOT NULL,
  day_only INTEGER NOT NULL,
  reactions_given INTEGER NOT NULL,
  mentions_made INTEGER NOT NULL,
  first_ms INTEGER,
  last_ms INTEGER,
  hours TEXT NOT NULL,
  labels TEXT NOT NULL,
  conversations TEXT NOT NULL,
  guilds TEXT NOT NULL,
  labelset INTEGER,
  source_seq INTEGER NOT NULL,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(person_id, day)
) WITHOUT ROWID;
CREATE TABLE rollup_pair_day(
  person_id TEXT NOT NULL,
  partner_id TEXT NOT NULL,
  day TEXT NOT NULL,
  replies INTEGER NOT NULL,
  mentions INTEGER NOT NULL,
  dm_msgs INTEGER NOT NULL,
  reactions INTEGER NOT NULL,
  latency_explicit TEXT NOT NULL,
  latency_implicit TEXT NOT NULL,
  labels TEXT NOT NULL,
  labelset INTEGER,
  source_seq INTEGER NOT NULL,
  PRIMARY KEY(person_id, partner_id, day)
) WITHOUT ROWID;
CREATE INDEX ix_rollup_pair_partner ON rollup_pair_day(partner_id, day);
CREATE INDEX ix_rollup_pair_actor_day ON rollup_pair_day(person_id, day);
CREATE TABLE rollup_scope_day(
  scope TEXT NOT NULL,
  key TEXT NOT NULL,
  day TEXT NOT NULL,
  msgs INTEGER NOT NULL,
  authored INTEGER NOT NULL,
  deleted INTEGER NOT NULL,
  people INTEGER NOT NULL,
  persons TEXT NOT NULL,
  labels TEXT NOT NULL,
  platforms TEXT NOT NULL,
  first_ms INTEGER,
  last_ms INTEGER,
  labelset INTEGER,
  source_seq INTEGER NOT NULL,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(scope, key, day)
) WITHOUT ROWID;
CREATE INDEX ix_rollup_scope_day ON rollup_scope_day(scope, day);
CREATE TABLE rollup_slots(
  person_id TEXT NOT NULL,
  month TEXT NOT NULL,
  msgs INTEGER NOT NULL,
  days INTEGER NOT NULL,
  slots TEXT NOT NULL,
  week_hours TEXT NOT NULL,
  source_seq INTEGER NOT NULL,
  PRIMARY KEY(person_id, month)
) WITHOUT ROWID;
CREATE TABLE rollup_zones(
  person_id TEXT NOT NULL,
  month TEXT NOT NULL,
  zone TEXT,
  offset_minutes INTEGER,
  confidence REAL NOT NULL,
  method TEXT NOT NULL,
  alternatives TEXT,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(person_id, month)
) WITHOUT ROWID;
CREATE TABLE rollup_changes(
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  subject_kind TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  day TEXT NOT NULL,
  at_ms INTEGER NOT NULL
);
CREATE TABLE rollup_drift(
  task TEXT NOT NULL,
  week TEXT NOT NULL,
  distribution TEXT NOT NULL,
  answered INTEGER NOT NULL,
  psi REAL,
  alerted INTEGER NOT NULL DEFAULT 0,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(task, week)
) WITHOUT ROWID;
CREATE TABLE rollup_state(
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL
);
"""),
        };

        /// <summary>Cell kinds in <c>rollup_dirty</c>.</summary>
        internal static class Cells
        {
            public const string PersonDay = "person";
            public const string Conversation = "conversation";
            public const string Guild = "guild";
            public const string Global = "global";
            public const string Slots = "slots";
            /// <summary>Every day of a person (merge, identity move, zone change): key = person, day = "*" or a month "yyyy-MM".</summary>
            public const string PersonAll = "person-all";
        }

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Count == 0 ? Array.Empty<DirtySubject>() : new[] { new DirtySubject("stream", "changes") };

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            if (context.Dirty.Count > 0 || context.VersionChanged || context.DueSchedules.Any(s => s.Name == "drain"))
                jobs.Add(Job("drain", "stream", "changes", priority: 115));
            if (context.DueSchedules.Any(s => s.Name == "zones")) jobs.Add(Job("zones", "stream", "zones", priority: 80));
            if (context.DueSchedules.Any(s => s.Name == "drift")) jobs.Add(Job("drift", "stream", "drift", priority: 60));
            if (LabelsetChanged()) jobs.Add(Job("relabel", "stream", "labels", priority: 70));
            return jobs;
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            "drain" => await DrainAsync(job).ConfigureAwait(false),
            "zones" => await ZonesAsync(job).ConfigureAwait(false),
            "drift" => await DriftAsync(job).ConfigureAwait(false),
            "relabel" => await RelabelAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown rollups job kind '{job.Job.Kind}'."),
        };

        // ── state helpers ────────────────────────────────────────────────────────

        internal long Cursor(string name)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT seq FROM rollup_cursors WHERE name=$n", ("$n", name)) ?? 0;
        }

        internal Task SetCursorAsync(string name, long seq, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO rollup_cursors(name, seq, updated_at_ms) VALUES($n,$s,$t)
                ON CONFLICT(name) DO UPDATE SET seq=MAX(rollup_cursors.seq, excluded.seq), updated_at_ms=excluded.updated_at_ms
                """, ("$n", name), ("$s", seq), ("$t", Context.Clock.NowMs)), ct);

        internal T? State<T>(string key)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT value_json FROM rollup_state WHERE key=$k", ("$k", key));
            return json == null ? default : JsonSerializer.Deserialize<T>(json, KernelJson.Options);
        }

        internal Task SetStateAsync<T>(string key, T value, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO rollup_state(key, value_json, updated_at_ms) VALUES($k,$v,$t)
                ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at_ms=excluded.updated_at_ms
                """, ("$k", key), ("$v", JsonSerializer.Serialize(value, KernelJson.Options)), ("$t", Context.Clock.NowMs)), ct);

        internal int? ActiveLabelset()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return ClassificationReader.ActiveLabelset(c);
        }

        /// <summary>True when the classification label set the cells were computed with is no longer the active one.</summary>
        private bool LabelsetChanged()
        {
            int? active = ActiveLabelset();
            if (active == null) return false;
            return State<int?>("labelset") != active;
        }

        internal static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        internal static DateOnly ParseDay(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        internal static string Month(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }
}
