using System.Diagnostics;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>Which export a preview or commit job works on: a file or folder in the import inbox, by name.</summary>
    [ApiContract]
    public sealed record ImportJobPayload(string Name, string? Format = null, ImportOptions? Options = null);

    /// <summary>
    /// Chat-export imports as kernel jobs (plan I-Importers, N17). Klives puts an export in the inbox folder (or
    /// uploads it there), previews it — a job, because reading a multi-gigabyte export is not something a request
    /// should wait on — and commits it. Both jobs are single-flight per export and survive a restart: a commit that
    /// was interrupted simply runs again and skips what it already wrote.
    /// </summary>
    public sealed class ImportModule : IntelModuleBase
    {
        public const string ModuleId = "ingest.imports";
        public const string PreviewKind = "preview";
        public const string CommitKind = "commit";
        public const string Topic = "ingest.import";
        private const string MaxImageSetting = "ingest.imports.max_image_mb";
        private const string MaxVideoSetting = "ingest.imports.max_video_mb";
        private const string MaxAudioSetting = "ingest.imports.max_audio_mb";
        private const string MaxFileSetting = "ingest.imports.max_file_mb";

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Chat-export imports",
            Category = "ingest",
            Description = "Imports chat exports Klives provides: Discord data packages, WhatsApp, Telegram, Instagram, Messenger and CSV/JSON tables. Preview first, then commit; importing the same export twice changes nothing.",
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor(MaxImageSetting, SettingTypes.Int, "64", "Largest image taken from an export, in MB. Larger ones are recorded but not stored.", 1, 4096),
                new SettingDescriptor(MaxVideoSetting, SettingTypes.Int, "1024", "Largest video taken from an export, in MB.", 1, 16384),
                new SettingDescriptor(MaxAudioSetting, SettingTypes.Int, "256", "Largest audio file or voice note taken from an export, in MB.", 1, 4096),
                new SettingDescriptor(MaxFileSetting, SettingTypes.Int, "256", "Largest other file taken from an export, in MB.", 1, 4096),
            },
        };

        /// <summary>Where exports wait to be imported. Jobs only ever read exports from here, named by file name.</summary>
        public string InboxPath => Path.Combine(Context.Stores.Paths.Root, "imports");

        public MediaStore Media { get; private set; } = null!;

        public override Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            base.InitializeAsync(context, ct);
            Directory.CreateDirectory(InboxPath);
            Media = new MediaStore(context.Stores.Paths);
            int swept = Media.SweepTemp();
            if (swept > 0) context.Log($"[OmniscienceV3] removed {swept} unfinished media file(s) left by an earlier run.");
            return Task.CompletedTask;
        }

        private ImportService Service() => new(Context.Stores, Context.Clock, Media,
            () => Context.Settings.Get(KernelSettings.KlivesTimeZoneKey, Temporal.Zones.DefaultKlivesZone),
            new MediaLimits(Mb(MaxImageSetting, 64), Mb(MaxVideoSetting, 1024), Mb(MaxAudioSetting, 256), Mb(MaxFileSetting, 256)));

        private long Mb(string key, int fallback) => (long)Math.Clamp(Context.Settings.Get(key, fallback), 1, 16384) << 20;

        /// <summary>
        /// The full path of an inbox entry. The name must be a plain file or folder name directly inside the inbox:
        /// the API can never be used to read a path of the caller's choosing.
        /// </summary>
        public string ResolveInboxEntry(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Name one file or folder in the import inbox (no paths).");
            string full = Path.Combine(InboxPath, name);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException($"There is no '{name}' in the import inbox.", name);
            return full;
        }

        public Task<long> EnqueueAsync(string kind, ImportJobPayload payload, CancellationToken ct)
        {
            if (kind is not (PreviewKind or CommitKind)) throw new ArgumentException($"Unknown import job '{kind}'.", nameof(kind));
            ResolveInboxEntry(payload.Name);
            var job = Job(kind, "import", payload.Name, priority: kind == PreviewKind ? 20 : 40, payloadJson: JsonSerializer.Serialize(payload, KernelJson.Options));
            // A failed import is retried by Klives once he has fixed what it complained about, not by a timer.
            return Context.Jobs.EnqueueAsync(job with { MaxAttempts = 1 }, ct);
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            var payload = job.Payload<ImportJobPayload>() ?? throw new JobFailureException(ErrorClass.Permanent, "The import job has no payload.");
            var options = payload.Options ?? new ImportOptions();
            try
            {
                string path = ResolveInboxEntry(payload.Name);
                var service = Service();
                switch (job.Job.Kind)
                {
                    case PreviewKind:
                        return JsonSerializer.Serialize(service.Preview(path, payload.Format, options, job.Cancellation), KernelJson.Options);
                    case CommitKind:
                        var throttle = Stopwatch.StartNew();
                        var result = await service.CommitAsync(path, payload.Format, options, async snapshot =>
                        {
                            if (throttle.ElapsedMilliseconds < 2000) return;
                            throttle.Restart();
                            await job.ReportProgressAsync(new { snapshot.Read, snapshot.Inserted, snapshot.Updated, snapshot.Unchanged, snapshot.Rejected, snapshot.AttachmentsStored }).ConfigureAwait(false);
                        }, job.Cancellation).ConfigureAwait(false);
                        await Context.Events.PublishAsync(Topic, new { name = payload.Name, result.ImportId, result.Format, result.Read, result.Inserted, result.Updated, result.Unchanged, result.Rejected },
                            job.Cancellation).ConfigureAwait(false);
                        Context.Log($"[OmniscienceV3] imported {payload.Name} ({result.Format}): {result.Inserted} new, {result.Updated} updated, {result.Unchanged} unchanged, {result.Rejected} rejected.");
                        return JsonSerializer.Serialize(result, KernelJson.Options);
                    default:
                        throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
                }
            }
            catch (Exception ex) when (ex is ImportDecisionNeededException or InvalidDataException or FileNotFoundException or JsonException or TimeZoneNotFoundException or ArgumentException)
            {
                // Wrong input, or a decision only Klives can make: retrying the same job cannot help.
                throw new JobFailureException(ErrorClass.Permanent, ex.Message, ex);
            }
        }
    }
}
