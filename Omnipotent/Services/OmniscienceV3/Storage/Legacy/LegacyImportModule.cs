using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>
    /// Runs the v2 migration inside the kernel (plan R8 shadow mode): the first sync snapshots v2's database with the
    /// backup API and imports it; every later sync (every two minutes) tails the live file read-only. One sync at a
    /// time (single-flight), resumable after a crash, progress on the Jobs page, and the report kept beside the stores.
    /// </summary>
    public sealed class LegacyImportModule : IntelModuleBase
    {
        public const string ModuleId = "ingest.legacy";
        public const string SyncKind = "sync";
        public const string EnabledSetting = "ingest.legacy.enabled";
        public const string KeepSnapshotSetting = "ingest.legacy.keep_snapshot";
        public const string ReportFileName = "legacy-import-report.json";
        public const string Topic = "ingest.legacy";

        private readonly Func<string> legacyDatabasePath;

        public LegacyImportModule(Func<string> legacyDatabasePath) => this.legacyDatabasePath = legacyDatabasePath;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "v2 migration and shadow tail",
            Category = "ingest",
            Description = "Imports Omniscience v2's database into v3: a snapshot first, then the live file every two minutes, so v3 runs on real data beside v2 until cutover.",
            Schedules = new[] { new ScheduleTrigger("tail", Every: TimeSpan.FromMinutes(2)) },
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor(EnabledSetting, SettingTypes.Bool, "true", "Import from Omniscience v2 (a snapshot, then the live file every two minutes)."),
                new SettingDescriptor(KeepSnapshotSetting, SettingTypes.Bool, "false", "Keep the snapshot of v2's database after the initial import instead of deleting it."),
            },
        };

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            // The first pass after start (or a version change) syncs at once; then every due tail. Single-flight by key.
            if (context.DueSchedules.Count == 0 && !context.VersionChanged) return Array.Empty<JobRequest>();
            var job = Job(SyncKind, priority: 50);
            return new[] { job with { MaxAttempts = 3 } };
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            if (job.Job.Kind != SyncKind) throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            if (!Context.Settings.Get(EnabledSetting, true)) return JsonSerializer.Serialize(new { skipped = "disabled" }, KernelJson.Options);

            string livePath = legacyDatabasePath();
            if (!File.Exists(livePath))
                throw new JobFailureException(ErrorClass.Permanent, $"The Omniscience v2 database was not found at {livePath}; nothing to import.");

            var importer = new LegacyImporter(Context.Stores, Context.Clock, Context.Settings.KlivesZone, Context.Log);
            var throttle = Stopwatch.StartNew();
            async Task Progress(LegacyProgress p)
            {
                if (throttle.ElapsedMilliseconds < 2000) return;
                throttle.Restart();
                await job.ReportProgressAsync(p).ConfigureAwait(false);
            }

            LegacyImportReport? snapshotReport = null;
            if (!importer.SnapshotCompleted())
            {
                string dir = Context.Stores.Paths.LegacySnapshots;
                RemoveOldSnapshots(dir);
                Context.Log($"[OmniscienceV3] taking a snapshot of the v2 database for the initial import ({livePath}).");
                string snapshot = await LegacyDatabase.SnapshotAsync(livePath, dir, Context.Clock, job.Cancellation).ConfigureAwait(false);
                try
                {
                    using (var db = LegacyDatabase.OpenReadOnly(snapshot))
                        snapshotReport = await importer.RunAsync(db, LegacyImportMode.Snapshot, Progress, job.Cancellation).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(Context.Stores.Paths.Root, ReportFileName),
                        JsonSerializer.Serialize(snapshotReport, new JsonSerializerOptions(KernelJson.Options) { WriteIndented = true }), job.Cancellation).ConfigureAwait(false);
                    await Context.Events.PublishAsync(Topic, Summary(snapshotReport), job.Cancellation).ConfigureAwait(false);
                    Context.Log($"[OmniscienceV3] v2 snapshot imported: {Describe(snapshotReport)}.");
                }
                finally
                {
                    // The snapshot was read through an unpooled connection, so its file is closed by now.
                    if (!Context.Settings.Get(KeepSnapshotSetting, false)) TryDelete(snapshot);
                }
            }

            // Tail the live file right away, so the gap between the snapshot and now closes in the same job.
            LegacyImportReport tail;
            using (var live = LegacyDatabase.OpenReadOnly(livePath))
                tail = await importer.RunAsync(live, LegacyImportMode.Tail, Progress, job.Cancellation).ConfigureAwait(false);
            if (tail.Tables.Values.Any(t => t.Imported + t.Updated + t.Quarantined > 0))
                await Context.Events.PublishAsync(Topic, Summary(tail), job.Cancellation).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { snapshot = snapshotReport == null ? null : Summary(snapshotReport), tail = Summary(tail) }, KernelJson.Options);
        }

        private static object Summary(LegacyImportReport r) => new
        {
            r.Mode,
            r.StartedAtMs,
            r.FinishedAtMs,
            imported = r.Tables.Sum(t => t.Value.Imported),
            updated = r.Tables.Sum(t => t.Value.Updated),
            quarantined = r.Tables.Sum(t => t.Value.Quarantined),
            tables = r.Tables.Where(t => t.Value.Imported + t.Value.Updated + t.Value.Quarantined > 0)
                .ToDictionary(t => t.Key, t => new { t.Value.Imported, t.Value.Updated, t.Value.Quarantined }),
            notes = r.Notes.Count,
        };

        private static string Describe(LegacyImportReport r) =>
            $"{r.Tables.Sum(t => t.Value.Imported)} rows imported, {r.Tables.Sum(t => t.Value.Quarantined)} quarantined, {r.Notes.Count} note(s)";

        private void RemoveOldSnapshots(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "omniscience-v2-*")) TryDelete(file);
        }

        private void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException ex) { Context.LogError($"[OmniscienceV3] could not delete the v2 snapshot {path}; delete it by hand.", ex); }
            catch (UnauthorizedAccessException ex) { Context.LogError($"[OmniscienceV3] could not delete the v2 snapshot {path}; delete it by hand.", ex); }
        }
    }
}
