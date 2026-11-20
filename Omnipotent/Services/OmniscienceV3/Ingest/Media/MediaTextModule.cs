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
        private string priorityCursor = "", priorityKey = "", priorityDoneKey = "";

        /// <summary>Job priorities of backlog reads (higher runs first): someone Klives tracks or Klives himself, someone he watches, anyone.</summary>
        public const int TrackedPriority = 185, WatchPriority = 178, BacklogPriority = 170;
        private const int PageSize = 500, PriorityPagesPerSweep = 200;

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
                    string outcome = await Processor.ProcessAsync(id, seq, job.Cancellation, job.ReportProgressAsync,
                        job.Job.Attempts >= job.Job.MaxAttempts ? job.Job.StartedAtMs ?? job.Job.CreatedAtMs : null).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { outcome }, KernelJson.Options);
                }
                case SweepKind:
                    return JsonSerializer.Serialize(await SweepAsync(job).ConfigureAwait(false), KernelJson.Options);
                default:
                    throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'.");
            }
        }

        /// <summary>
        /// Queues stored attachments that have no label for their task yet: media that predates the module, or that
        /// was skipped while its engine was unavailable. A kind whose engine cannot run right now is left out, so an
        /// unavailable engine does not turn every sweep into thousands of jobs that each say "skipped".
        /// Plan R2: the backlog of the people Klives tracks (and his own) is queued first and runs first, then that of
        /// the people he watches, then everyone else's in attachment order.
        /// </summary>
        private async Task<object> SweepAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var cfg = ReadSettings();
            bool images = cfg.OcrEnabled && Ocr() != null;
            bool sound = cfg.TranscribeEnabled && await transcriber().ConfigureAwait(false) != null;
            if (!images && !sound) return new { queued = 0, reason = "no engine is available" };

            int budget = Math.Clamp(Context.Settings.Get(SweepBatchSetting, 500), 10, 100000), queued = 0, first = 0;
            var clock = MediaSweep.Start();

            // 1. The people who come first. One full pass per set of people (and engines): media of theirs that arrives
            //    later is announced by the change log, so once their backlog has been walked there is nothing left to find.
            var (authors, key) = PriorityAuthors(images, sound);
            if (authors.Count > 0 && key != priorityDoneKey)
            {
                if (key != priorityKey) { priorityKey = key; priorityCursor = ""; }
                var part = clock.Part(0.6);
                for (int pages = 0; pages < PriorityPagesPerSweep && queued < budget && !part.Spent; pages++)
                {
                    ct.ThrowIfCancellationRequested();
                    await job.ReportProgressAsync(new { stage = "priority", pages, queued }).ConfigureAwait(false);
                    var page = Page(priorityCursor, images, sound);
                    var theirs = page.Where(p => p.Author != null && authors.ContainsKey(p.Author)).Select(p => p.Id).ToList();
                    var labelled = Labelled(theirs);
                    foreach (var (id, author) in page)
                    {
                        priorityCursor = id;
                        if (author == null || !authors.TryGetValue(author, out int priority) || labelled.Contains(id)) continue;
                        await Context.Jobs.EnqueueAsync(Job(ExtractKind, "attachment", id, priority: priority), ct).ConfigureAwait(false);
                        first++;
                        if (++queued >= budget) break;
                    }
                    bool walked = page.Count < PageSize && (page.Count == 0 || priorityCursor == page[^1].Id);
                    if (walked) { priorityDoneKey = key; priorityCursor = ""; break; }
                    // Otherwise the budget ran out, or the page was full: the next sweep (or page) resumes from the cursor.
                }
            }

            // 2. Everyone, in attachment order, with what is left of the budget. A top-up: while more is already waiting
            //    than a sweep would add there is nothing to top up (on the server 180,000 extractions were queued, and each
            //    sweep still walked the attachments to find more).
            long waiting = Context.Jobs.ModuleStats().GetValueOrDefault(ModuleId)?.Queued ?? 0;
            if (waiting >= budget) return new { queued, first, skipped = "enough is already queued", waiting };
            bool wrapped = sweepCursor.Length == 0;
            int pagesRead = 0;
            while (queued < budget && !clock.Spent)
            {
                ct.ThrowIfCancellationRequested();
                if (++pagesRead % 4 == 0) await job.ReportProgressAsync(new { stage = "everyone", pages = pagesRead, queued }).ConfigureAwait(false);
                var page = Page(sweepCursor, images, sound);
                if (page.Count > 0)
                {
                    var labelled = Labelled(page.Select(p => p.Id).ToList());
                    foreach (var (id, _) in page)
                    {
                        sweepCursor = id;
                        if (labelled.Contains(id)) continue;
                        await Context.Jobs.EnqueueAsync(Job(ExtractKind, "attachment", id, priority: BacklogPriority), ct).ConfigureAwait(false);
                        if (++queued >= budget) break;
                    }
                }
                if (page.Count < PageSize)
                {
                    sweepCursor = "";
                    if (wrapped) break;
                    wrapped = true;
                }
            }
            return new { queued, first };
        }

        /// <summary>One page of stored media of the kinds an engine can read now, with the identity that posted it.</summary>
        private List<(string Id, string? Author)> Page(string after, bool images, bool sound)
        {
            using var c = Context.Stores.Corpus.OpenRead();
            return c.Query(null, $"""
                SELECT a.attachment_id, m.author_identity_id FROM attachments a LEFT JOIN messages m ON m.message_id = a.message_id
                WHERE a.attachment_id > $after AND a.download_state='done' AND a.cas_sha256 IS NOT NULL
                  AND (($images = 1 AND a.media_kind = 'image') OR ($sound = 1 AND a.media_kind IN ('audio', 'voice', 'video')))
                ORDER BY a.attachment_id LIMIT {PageSize}
                """, r => (r.GetString(0), r.GetStringOrNull(1)), ("$after", after), ("$images", images ? 1 : 0), ("$sound", sound ? 1 : 0));
        }

        private HashSet<string> Labelled(List<string> ids)
        {
            if (ids.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
            using var c = Context.Stores.Intel.OpenRead();
            return c.QueryIn(null, "SELECT DISTINCT item_id FROM labels WHERE item_kind='attachment' AND task IN ('ocr','transcript') AND item_id IN ({keys})",
                ids, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// The identities whose media is read first, each with its job priority (merged people count as the person they
        /// were merged into), and a key that changes whenever that set or the engines that can run change.
        /// </summary>
        private (Dictionary<string, int> Identities, string Key) PriorityAuthors(bool images, bool sound)
        {
            var people = new Dictionary<string, int>(StringComparer.Ordinal);
            void Rank(string person, int priority)
            {
                string canonical = Context.Policy.Canonical(person);
                people[canonical] = Math.Max(people.GetValueOrDefault(canonical), priority);
            }
            using (var o = Context.Stores.Operator.OpenRead())
                foreach (var (person, tier) in o.Query(null, "SELECT person_id, tier FROM person_settings WHERE tier IN ('tracked', 'watch')", r => (r.GetString(0), r.GetString(1))))
                    Rank(person, tier == "tracked" ? TrackedPriority : WatchPriority);
            var identities = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var c = Context.Stores.Corpus.OpenRead())
            {
                foreach (string self in c.Query(null, "SELECT person_id FROM persons WHERE is_self=1", r => r.GetString(0))) Rank(self, TrackedPriority);
                if (people.Count == 0) return (identities, "");
                foreach (var (identity, person) in c.Query(null, "SELECT identity_id, person_id FROM identities", r => (r.GetString(0), r.GetString(1))))
                    if (people.TryGetValue(Context.Policy.Canonical(person), out int priority)) identities[identity] = priority;
            }
            string key = (images ? "i" : "-") + (sound ? "s" : "-") + "|" + string.Join(",", identities.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
            return (identities, key);
        }
    }
}
