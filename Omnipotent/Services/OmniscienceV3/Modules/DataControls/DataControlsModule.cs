using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.DataControls
{
    /// <summary>
    /// Data controls (N20): forgetting a person everywhere, retention of high-volume event streams, encrypted snapshot
    /// backups of the two stores that cannot be rebuilt (corpus.db and operator.db), and a per-person export.
    /// <para>
    /// Everything here is deliberate and audited. Forgetting needs a confirmation token from a preview that says exactly
    /// what will go; it then runs as a job that can be resumed, and it verifies itself at the end. Nothing in this module
    /// ever touches the old (v2) database or its media folder.
    /// </para>
    /// </summary>
    public sealed partial class DataControlsModule : IntelModuleBase
    {
        public const string ModuleId = "datacontrols";
        public const string ForgetKind = "forget";
        public const string RetentionKind = "retention";
        public const string BackupKind = "backup";

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Data controls",
            Category = "data",
            Description = "Forget a person everywhere, keep high-volume event streams for a set time, take encrypted backups of the corpus and of Klives's own decisions, and export everything about one person.",
            Schedules = new[] { new ScheduleTrigger("nightly", DailyAtLocal: new TimeOnly(3, 30)) },
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor("datacontrols.retention.presence_days", SettingTypes.Int, "0", "Presence events older than this are deleted (0 keeps them for ever).", 0, 36500),
                new SettingDescriptor("datacontrols.retention.typing_days", SettingTypes.Int, "0", "Typing events older than this are deleted (0 keeps them for ever).", 0, 36500),
                new SettingDescriptor("datacontrols.retention.voice_days", SettingTypes.Int, "0", "Voice sessions that ended longer ago than this are deleted (0 keeps them for ever).", 0, 36500),
                new SettingDescriptor("datacontrols.backup.enabled", SettingTypes.Bool, "true", "Take an encrypted backup of corpus.db and operator.db every night."),
                new SettingDescriptor("datacontrols.backup.keep", SettingTypes.Int, "7", "How many backups are kept; older ones are deleted.", 1, 365),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "forget runs and retention log", """
CREATE TABLE dc_forget(
  run_id TEXT PRIMARY KEY,
  state TEXT NOT NULL,
  target_json TEXT,
  counts_json TEXT,
  error TEXT,
  started_at_ms INTEGER NOT NULL,
  finished_at_ms INTEGER
);
CREATE TABLE dc_retention(
  stream TEXT PRIMARY KEY,
  kept_days INTEGER NOT NULL,
  cutoff_ms INTEGER NOT NULL,
  deleted_total INTEGER NOT NULL,
  last_deleted INTEGER NOT NULL,
  last_run_ms INTEGER NOT NULL
);
"""),
        };

        internal ModuleContext Ctx => Context;

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            if (!context.DueSchedules.Any(s => s.Name == "nightly")) yield break;
            yield return Job(RetentionKind, "all", "all", priority: 40, lane: Lane.Cpu);
            if (Context.Settings.Get("datacontrols.backup.enabled", true)) yield return Job(BackupKind, "all", "scheduled", priority: 30);
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            ForgetKind => await RunForgetAsync(job).ConfigureAwait(false),
            RetentionKind => await RunRetentionAsync(job).ConfigureAwait(false),
            BackupKind => Json(await BackupAsync(job.Job.SubjectId ?? "scheduled", job.Cancellation).ConfigureAwait(false)),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown data-controls job kind '{job.Job.Kind}'."),
        };

        private static string Json(object value) => JsonSerializer.Serialize(value, KernelJson.Options);
    }
}
