using System.Text.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>
    /// Text from media as a kernel module: OCR of images, transcripts of voice notes, audio and video (plan I-Media,
    /// N3). Every stored attachment is read once; results are labels on the attachment (tasks <c>ocr</c> and
    /// <c>transcript</c>, the text in <c>signals_json.text</c>). Durable jobs replace v2's in-memory OCR queue,
    /// which lost its backlog on every restart and recorded failures as "processed" (F472–F475).
    /// </summary>
    public sealed class MediaTextModule : IntelModuleBase
    {
        public const string ModuleId = "media.text";
        public const string ExtractKind = "extract";
        public const string SweepKind = "sweep";
        public const string OcrEnabledSetting = "media.text.ocr_enabled";
        public const string OcrLanguagesSetting = "media.text.ocr_languages";
        public const string OcrMinConfidenceSetting = "media.text.ocr_min_confidence";
        public const string TranscribeEnabledSetting = "media.text.transcribe_enabled";
        public const string MaxAudioMinutesSetting = "media.text.max_audio_minutes";
        public const string SweepBatchSetting = "media.text.sweep_batch";

        private readonly Func<Task<ITranscriber?>> transcriber;
        private readonly IOcrEngine? fixedOcr;
        private readonly IAudioSegmenter? fixedSegmenter;
        private readonly object ocrGate = new();
        private TesseractOcr? tesseract;
        private string tesseractLanguages = "";
        private string sweepCursor = "";

        /// <param name="transcriber">Resolves the speech service when a transcript is first needed; null result = none available.</param>
        /// <param name="ocr">Replaces Tesseract (tests).</param>
        /// <param name="segmenter">Replaces ffmpeg (tests).</param>
        public MediaTextModule(Func<Task<ITranscriber?>>? transcriber = null, IOcrEngine? ocr = null, IAudioSegmenter? segmenter = null)
        {
            this.transcriber = transcriber ?? (() => Task.FromResult<ITranscriber?>(null));
            fixedOcr = ocr;
            fixedSegmenter = segmenter;
        }

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Text from media",
            Category = "ingest",
            Description = "Reads the writing in images (OCR) and the speech in voice notes, audio and video, once per file. The text is filed as quoted material found in the attachment, never as something its poster wrote.",
            DependsOn = new[] { MediaModule.ModuleId },
            SubscribedChangeKinds = new[] { ChangeKinds.AttachmentStored },
            Schedules = new[] { new ScheduleTrigger("sweep", Every: TimeSpan.FromMinutes(15)) },
            DefaultLane = Lane.Io,
            Settings = new[]
            {
                new SettingDescriptor(OcrEnabledSetting, SettingTypes.Bool, "true", "Read the text in images."),
                new SettingDescriptor(OcrLanguagesSetting, SettingTypes.String, "\"eng\"",
                    "Tesseract language codes to read, separated by commas, most likely first (eng, nld, deu, fra…). Language data is downloaded once per language."),
                new SettingDescriptor(OcrMinConfidenceSetting, SettingTypes.Number, "0.6",
                    "Below this confidence what OCR found is treated as texture or stylised lettering and dropped.", 0, 1),
                new SettingDescriptor(TranscribeEnabledSetting, SettingTypes.Bool, "true", "Transcribe voice notes, audio and video sound tracks through the speech service."),
                new SettingDescriptor(MaxAudioMinutesSetting, SettingTypes.Int, "30", "Only this many minutes from the start of a recording are transcribed.", 1, 600),
                new SettingDescriptor(SweepBatchSetting, SettingTypes.Int, "500", "How many unread attachments each fifteen-minute sweep queues.", 10, 100000),
            },
        };

        public override IReadOnlyList<Migration> IntelMigrations { get; } = new[]
        {
            new Migration(ModuleId, 1, "media text cache", """
CREATE TABLE media_text(
  sha256 TEXT NOT NULL,
  task TEXT NOT NULL,
  model TEXT NOT NULL,
  model_version TEXT NOT NULL,
  text TEXT NOT NULL,
  confidence REAL,
  language TEXT,
  state TEXT NOT NULL,
  error TEXT,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(sha256, task, model_version)
);
"""),
        };

        public MediaTextProcessor Processor { get; private set; } = null!;

        public override Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            base.InitializeAsync(context, ct);
            var segmenter = fixedSegmenter ?? new FfmpegSegmenter(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), Path.Combine(context.Stores.Paths.Root, "tmp", "audio"));
            Processor = new MediaTextProcessor(context.Stores, context.Clock, new MediaStore(context.Stores.Paths), ReadSettings, () => Task.FromResult(Ocr()), transcriber, segmenter);
            return Task.CompletedTask;
        }

        private MediaTextSettings ReadSettings() => new()
        {
            OcrEnabled = Context.Settings.Get(OcrEnabledSetting, true),
            OcrMinConfidence = Math.Clamp(Context.Settings.Get(OcrMinConfidenceSetting, 0.6), 0, 1),
            TranscribeEnabled = Context.Settings.Get(TranscribeEnabledSetting, true),
            MaxAudio = TimeSpan.FromMinutes(Math.Clamp(Context.Settings.Get(MaxAudioMinutesSetting, 30), 1, 600)),
        };

        /// <summary>The OCR engine for the configured languages; rebuilt when they change (which changes the engine version).</summary>
        private IOcrEngine? Ocr()
        {
            if (fixedOcr != null) return fixedOcr;
            if (Context.Models == null) return null;
            string languages = Context.Settings.Get(OcrLanguagesSetting, "eng");
            lock (ocrGate)
            {
                if (tesseract != null && languages == tesseractLanguages) return tesseract;
                var codes = languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(code => code.Length is >= 3 and <= 12 && code.All(ch => char.IsAsciiLetterLower(ch) || ch == '_')).ToList();
                tesseract = new TesseractOcr(Context.Models, codes);
                tesseractLanguages = languages;
                return tesseract;
            }
        }

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Where(c => c.Kind == ChangeKinds.AttachmentStored).Select(c => new DirtySubject(ExtractKind, c.EntityId));

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            foreach (var subject in context.Dirty)
                if (subject.Kind == ExtractKind) yield return Job(ExtractKind, "attachment", subject.Id, priority: 130);
            if (context.DueSchedules.Count > 0 || context.VersionChanged) yield return Job(SweepKind, priority: 210) with { MaxAttempts = 2 };
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            switch (job.Job.Kind)
            {
                case ExtractKind:
                {
                    string id = job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "The job names no attachment.");
                    long seq;
                    using (var c = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(c);
                    string outcome = await Processor.ProcessAsync(id, seq, job.Cancellation).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { outcome }, KernelJson.Options);
                }
                case SweepKind:
                    return JsonSerializer.Serialize(await SweepAsync(job.Cancellation).ConfigureAwait(false), KernelJson.Options);
                default:
                    throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            }
        }

        /// <summary>
        /// Queues stored attachments that have no label for their task yet: media that predates the module, or that
        /// was skipped while its engine was unavailable. A kind whose engine cannot run right now is left out, so an
        /// unavailable engine does not turn every sweep into thousands of jobs that each say "skipped".
        /// </summary>
        private async Task<object> SweepAsync(CancellationToken ct)
        {
            var cfg = ReadSettings();
            bool images = cfg.OcrEnabled && Ocr() != null;
            bool sound = cfg.TranscribeEnabled && await transcriber().ConfigureAwait(false) != null;
            if (!images && !sound) return new { queued = 0, reason = "no engine is available" };

            int budget = Math.Clamp(Context.Settings.Get(SweepBatchSetting, 500), 10, 100000), queued = 0;
            bool wrapped = sweepCursor.Length == 0;
            while (queued < budget)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Id, string Kind)> page;
                using (var c = Context.Stores.Corpus.OpenRead())
                    page = c.Query(null, """
                        SELECT attachment_id, media_kind FROM attachments
                        WHERE attachment_id > $after AND download_state='done' AND cas_sha256 IS NOT NULL
                          AND (($images = 1 AND media_kind = 'image') OR ($sound = 1 AND media_kind IN ('audio', 'voice', 'video')))
                        ORDER BY attachment_id LIMIT 500
                        """, r => (r.GetString(0), r.GetString(1)), ("$after", sweepCursor), ("$images", images ? 1 : 0), ("$sound", sound ? 1 : 0));
                if (page.Count > 0)
                {
                    HashSet<string> labelled;
                    using (var c = Context.Stores.Intel.OpenRead())
                        labelled = c.QueryIn(null, "SELECT DISTINCT item_id FROM labels WHERE item_kind='attachment' AND task IN ('ocr','transcript') AND item_id IN ({keys})",
                            page.Select(p => p.Id).ToList(), r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                    foreach (var (id, _) in page)
                    {
                        sweepCursor = id;
                        if (labelled.Contains(id)) continue;
                        await Context.Jobs.EnqueueAsync(Job(ExtractKind, "attachment", id, priority: 170), ct).ConfigureAwait(false);
                        if (++queued >= budget) break;
                    }
                }
                if (page.Count < 500)
                {
                    sweepCursor = "";
                    if (wrapped) break;
                    wrapped = true;
                }
            }
            return new { queued };
        }
    }
}
