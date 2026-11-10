using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Imports = Omnipotent.Services.OmniscienceV3.Ingest.Imports;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>
    /// The media store as a kernel module (plan I-Media): every attachment that arrives is downloaded (when
    /// downloading is on) and every stored object is probed once. Work is driven by the corpus change log, so
    /// media that arrives by backfill or import is handled exactly like live media; a periodic sweep picks up
    /// anything that predates the module (the files v2 already stored) or was skipped while downloading was off.
    /// </summary>
    public sealed class MediaModule : IntelModuleBase
    {
        public const string ModuleId = "media.store";
        public const string DownloadKind = "download";
        public const string ProbeKind = "probe";
        public const string SweepKind = "sweep";
        public const string DownloadSetting = "media.store.download_enabled";
        public const string HostsSetting = "media.store.allowed_hosts";
        public const string SweepBatchSetting = "media.store.sweep_batch";
        private const string MaxImageSetting = "media.store.max_image_mb";
        private const string MaxVideoSetting = "media.store.max_video_mb";
        private const string MaxAudioSetting = "media.store.max_audio_mb";
        private const string MaxFileSetting = "media.store.max_file_mb";

        private readonly HttpMessageHandler? handler;
        private string sweepCursor = "";

        /// <param name="handler">Replaces the network in tests.</param>
        public MediaModule(HttpMessageHandler? handler = null) => this.handler = handler;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Media store",
            Category = "ingest",
            Description = "Downloads attachments into the content-addressed store and reads each stored file once: size, animation, orientation, when it was taken, a perceptual hash and a thumbnail. Location data in photos is never read.",
            SubscribedChangeKinds = new[] { ChangeKinds.AttachmentAdd, ChangeKinds.AttachmentStored },
            Schedules = new[] { new ScheduleTrigger("sweep", Every: TimeSpan.FromMinutes(10)) },
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor(DownloadSetting, SettingTypes.Bool, "false",
                    "Download attachments that have a link but no stored file. Off while Omniscience v2 is still the service fetching them, so nothing is fetched twice."),
                new SettingDescriptor(HostsSetting, SettingTypes.String, "\"cdn.discordapp.com,media.discordapp.net\"",
                    "Hosts attachments may be downloaded from, separated by commas. Links to any other host are never requested."),
                new SettingDescriptor(SweepBatchSetting, SettingTypes.Int, "2000", "How many unprocessed attachments each ten-minute sweep queues.", 10, 100000),
                new SettingDescriptor(MaxImageSetting, SettingTypes.Int, "64", "Largest image downloaded, in MB.", 1, 4096),
                new SettingDescriptor(MaxVideoSetting, SettingTypes.Int, "1024", "Largest video downloaded, in MB.", 1, 16384),
                new SettingDescriptor(MaxAudioSetting, SettingTypes.Int, "256", "Largest audio file or voice note downloaded, in MB.", 1, 4096),
                new SettingDescriptor(MaxFileSetting, SettingTypes.Int, "256", "Largest other file downloaded, in MB.", 1, 4096),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "media objects", """
CREATE TABLE media_objects(
  sha256 TEXT PRIMARY KEY,
  media_kind TEXT NOT NULL,
  mime TEXT,
  size_bytes INTEGER NOT NULL,
  width INTEGER,
  height INTEGER,
  frames INTEGER,
  orientation INTEGER,
  phash INTEGER,
  taken_local TEXT,
  taken_offset_min INTEGER,
  thumb_sha256 TEXT,
  state TEXT NOT NULL,
  error TEXT,
  probed_at_ms INTEGER NOT NULL,
  source_seq INTEGER NOT NULL
);
CREATE INDEX ix_media_objects_phash ON media_objects(phash) WHERE phash IS NOT NULL;
CREATE INDEX ix_media_objects_state ON media_objects(state) WHERE state <> 'ok';
"""),
        };

        public MediaStore Media { get; private set; } = null!;
        public MediaProcessor Processor { get; private set; } = null!;

        public override Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            base.InitializeAsync(context, ct);
            Media = new MediaStore(context.Stores.Paths);
            Processor = new MediaProcessor(context.Stores, context.Clock, Media, ReadSettings, () => context.Settings.KlivesZone, handler);
            return Task.CompletedTask;
        }

        private MediaSettings ReadSettings() => new()
        {
            AllowedHosts = Context.Settings.Get(HostsSetting, "cdn.discordapp.com,media.discordapp.net")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Limits = new Imports.MediaLimits(Mb(MaxImageSetting, 64), Mb(MaxVideoSetting, 1024), Mb(MaxAudioSetting, 256), Mb(MaxFileSetting, 256)),
        };

        private long Mb(string key, int fallback) => (long)Math.Clamp(Context.Settings.Get(key, fallback), 1, 16384) << 20;

        private bool DownloadsOn => Context.Settings.Get(DownloadSetting, false);

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            foreach (var change in changes)
            {
                if (change.Kind == ChangeKinds.AttachmentAdd) yield return new DirtySubject(DownloadKind, change.EntityId);
                else if (change.Kind == ChangeKinds.AttachmentStored) yield return new DirtySubject(ProbeKind, change.EntityId);
            }
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            bool downloads = DownloadsOn;
            foreach (var subject in context.Dirty)
            {
                // With downloading off the attachment simply stays pending; the sweep queues it if it is turned on.
                if (subject.Kind == DownloadKind && downloads) yield return Job(DownloadKind, "attachment", subject.Id, priority: 120);
                else if (subject.Kind == ProbeKind) yield return Job(ProbeKind, "attachment", subject.Id, priority: 110, lane: Lane.Cpu);
            }
            if (context.DueSchedules.Count > 0 || context.VersionChanged) yield return Job(SweepKind, priority: 200) with { MaxAttempts = 2 };
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            switch (job.Job.Kind)
            {
                case DownloadKind:
                {
                    if (!DownloadsOn) return JsonSerializer.Serialize(new { skipped = "downloading is off" }, KernelJson.Options);
                    var outcome = await Processor.DownloadAsync(Subject(job), job.Cancellation).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { outcome.State, outcome.Detail }, KernelJson.Options);
                }
                case ProbeKind:
                {
                    string? sha;
                    long seq;
                    using (var c = Context.Stores.Corpus.OpenRead())
                    {
                        sha = c.Scalar<string>(null, "SELECT cas_sha256 FROM attachments WHERE attachment_id=$a", ("$a", Subject(job)));
                        seq = ChangeLog.MaxSeq(c);
                    }
                    if (sha == null) return JsonSerializer.Serialize(new { skipped = "the attachment has no stored file" }, KernelJson.Options);
                    string state = await Processor.ProbeAsync(sha, seq, job.Cancellation).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { sha256 = sha, state }, KernelJson.Options);
                }
                case SweepKind:
                    return JsonSerializer.Serialize(await SweepAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options);
                default:
                    throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            }
        }

        private static string Subject(JobContext job) =>
            job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "The job names no attachment.");

        /// <summary>
        /// Queues work the change log did not: stored files never probed (one job per distinct content), and — when
        /// downloading is on — attachments still pending. Walks the corpus in id order from where the last sweep
        /// stopped and wraps around, so a large backlog is spread over sweeps without rescanning from the start.
        /// </summary>
        private async Task<object> SweepAsync(CancellationToken ct)
        {
            int budget = Math.Clamp(Context.Settings.Get(SweepBatchSetting, 2000), 10, 100000);
            int probes = 0, downloads = 0;
            // Starting from the beginning, one pass is the whole corpus.
            bool wrapped = sweepCursor.Length == 0;
            while (probes < budget)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Sha, string AttachmentId)> page;
                using (var c = Context.Stores.Corpus.OpenRead())
                    page = c.Query(null, """
                        SELECT cas_sha256, MIN(attachment_id) FROM attachments
                        WHERE cas_sha256 IS NOT NULL AND cas_sha256 > $after AND download_state='done'
                        GROUP BY cas_sha256 ORDER BY cas_sha256 LIMIT 500
                        """, r => (r.GetString(0), r.GetString(1)), ("$after", sweepCursor));
                if (page.Count == 0)
                {
                    sweepCursor = "";
                    if (wrapped) break;
                    wrapped = true;
                    continue;
                }
                HashSet<string> known;
                using (var c = Context.Stores.Intel.OpenRead())
                    known = c.QueryIn(null, "SELECT sha256 FROM media_objects WHERE sha256 IN ({keys})", page.Select(p => p.Sha).ToList(), r => r.GetString(0))
                        .ToHashSet(StringComparer.Ordinal);
                foreach (var (sha, attachmentId) in page)
                {
                    sweepCursor = sha;
                    if (known.Contains(sha)) continue;
                    await Context.Jobs.EnqueueAsync(Job(ProbeKind, "attachment", attachmentId, priority: 150, lane: Lane.Cpu), ct).ConfigureAwait(false);
                    if (++probes >= budget) break;
                }
                if (page.Count < 500)
                {
                    sweepCursor = "";
                    if (wrapped) break;
                    wrapped = true;
                }
            }

            if (DownloadsOn)
            {
                List<string> pending;
                using (var c = Context.Stores.Corpus.OpenRead())
                    pending = c.Query(null, "SELECT attachment_id FROM attachments WHERE download_state='pending' ORDER BY created_at_ms DESC LIMIT $n",
                        r => r.GetString(0), ("$n", budget));
                foreach (var id in pending)
                {
                    await Context.Jobs.EnqueueAsync(Job(DownloadKind, "attachment", id, priority: 160), ct).ConfigureAwait(false);
                    downloads++;
                }
            }
            return new { probesQueued = probes, downloadsQueued = downloads };
        }
    }
}
