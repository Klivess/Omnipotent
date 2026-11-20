using System.Text;
using System.Text.Json;
using FFMpegCore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>Text read out of a media file. Empty <see cref="Text"/> is a result ("nothing legible", "silence"), not a failure.</summary>
    public sealed record MediaTextResult(string Text, double Confidence, string? Language);

    /// <summary>The engine cannot run here at all (no model files, no ffmpeg, no speech service). The attachment is skipped with the reason.</summary>
    public sealed class MediaTextUnavailableException : Exception
    {
        public MediaTextUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public interface IOcrEngine
    {
        string Model { get; }
        /// <summary>Changes whenever results could (engine, language data): a new version re-reads everything.</summary>
        string Version { get; }
        /// <summary>Reads text from upright frames of one image (several for an animation).</summary>
        Task<MediaTextResult> ReadAsync(IReadOnlyList<SKBitmap> frames, CancellationToken ct);
    }

    public sealed record AudioSegment(byte[] Bytes, string FileName, string Mime, double StartSeconds);

    /// <summary>Cuts an audio or video file into pieces a speech service accepts.</summary>
    public interface IAudioSegmenter
    {
        Task<IReadOnlyList<AudioSegment>> SegmentAsync(string path, TimeSpan maxLength, CancellationToken ct);
    }

    public sealed record SpeechResult(string Text, string? Language, double NoSpeechProbability, double AverageLogProbability);

    public interface ITranscriber
    {
        string Model { get; }
        Task<SpeechResult> TranscribeAsync(AudioSegment segment, string? language, CancellationToken ct);
    }

    /// <summary>The tasks under which media text is stored in <c>labels</c> (the knowledge workstream reads these names).</summary>
    public static class MediaTextTasks
    {
        public const string Ocr = "ocr";
        public const string Transcript = "transcript";
        public const string Caption = "caption";
    }

    public sealed record MediaTextSettings
    {
        public bool OcrEnabled { get; init; } = true;
        /// <summary>Below this mean confidence OCR output is noise from stylised text or textures and is dropped.</summary>
        public double OcrMinConfidence { get; init; } = 0.6;
        public bool TranscribeEnabled { get; init; } = true;
        public TimeSpan MaxAudio { get; init; } = TimeSpan.FromMinutes(30);
        public int MaxChars { get; init; } = 20_000;
    }

    /// <summary>
    /// Turns stored media into text (plan I-Media: OCR; N3: voice and video transcripts) and files it as labels on
    /// the attachment, where evidence assembly reads it.
    /// <list type="bullet">
    /// <item><b>Once per content.</b> The reading is cached by content hash, so a meme posted fifty times is read once
    /// and labelled fifty times.</item>
    /// <item><b>Not the poster's words.</b> OCR text is tagged <c>origin: image_text</c> and a transcript <c>origin:
    /// speech</c>: posting a screenshot is not authorship of what it says, and downstream extraction treats it as quoted.</item>
    /// <item><b>Empty is an answer.</b> A photo with no writing, or a silent clip, gets an abstaining label with empty
    /// text, so it is not read again and nothing mistakes "unprocessed" for "nothing there".</item>
    /// <item><b>Unavailable is not failure.</b> Without language data, ffmpeg or a speech service the attachment is
    /// skipped with the reason and picked up by a later sweep once the engine exists.</item>
    /// </list>
    /// </summary>
    public sealed class MediaTextProcessor
    {
        private const double NoSpeechThreshold = 0.6;
        private static readonly TimeSpan SegmentLength = TimeSpan.FromSeconds(50);

        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly MediaStore media;
        private readonly Func<MediaTextSettings> settings;
        private readonly Func<Task<IOcrEngine?>> ocr;
        private readonly Func<Task<ITranscriber?>> transcriber;
        private readonly IAudioSegmenter segmenter;

        public MediaTextProcessor(OmniscienceStores stores, IClock clock, MediaStore media, Func<MediaTextSettings> settings,
            Func<Task<IOcrEngine?>> ocr, Func<Task<ITranscriber?>> transcriber, IAudioSegmenter segmenter)
        {
            this.stores = stores;
            this.clock = clock;
            this.media = media;
            this.settings = settings;
            this.ocr = ocr;
            this.transcriber = transcriber;
            this.segmenter = segmenter;
        }

        /// <summary>The task an attachment of this kind gets, or null when it has no text to give (a document, an archive).</summary>
        public static string? TaskFor(string mediaKind) => mediaKind switch
        {
            MediaKinds.Image => MediaTextTasks.Ocr,
            MediaKinds.Audio or MediaKinds.Voice or MediaKinds.Video => MediaTextTasks.Transcript,
            _ => null,
        };

        /// <summary>How long the speech service gets for one piece of audio (under a minute of sound).</summary>
        internal static TimeSpan PieceLimit { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>When the speech service last transcribed a piece (0: not yet in this process).</summary>
        private long speechAnsweredMs;

        /// <summary>Reads one attachment. Returns what happened in a few words (for the job result).</summary>
        /// <param name="progress">Told after every piece of audio, so a long recording is seen to be moving.</param>
        /// <param name="lastAttemptSinceMs">Set on a job's last attempt, to when the job first started. A recording the
        /// speech service fails on every time, while it goes on transcribing others, is the recording's fault: it is filed
        /// as unreadable with the reason instead of leaving a dead job (two such files did, 19 and 20 Nov 2026). If the
        /// service has answered nothing since then it is the service that is down, and the job fails as before.</param>
        public async Task<string> ProcessAsync(string attachmentId, long sourceSeq, CancellationToken ct, Func<object, Task>? progress = null, long? lastAttemptSinceMs = null)
        {
            var cfg = settings();
            (bool Found, string? Sha, string Kind, string State) row;
            string? relative = null;
            using (var c = stores.Corpus.OpenRead())
            {
                row = c.Query(null, "SELECT cas_sha256, media_kind, download_state FROM attachments WHERE attachment_id=$a",
                    r => (true, r.GetStringOrNull(0), r.GetString(1), r.GetString(2)), ("$a", attachmentId)).FirstOrDefault();
                if (row.Found && row.Sha != null) relative = c.Scalar<string>(null, "SELECT relative_path FROM cas_objects WHERE sha256=$s", ("$s", row.Sha));
            }
            if (!row.Found) return "no such attachment";
            if (row.Sha == null || relative == null || row.State != DownloadStates.Done) return "no stored file";
            string? task = TaskFor(row.Kind);
            if (task == null) return "this kind of file has no text to read";
            if (task == MediaTextTasks.Ocr && !cfg.OcrEnabled) return "OCR is off";
            if (task == MediaTextTasks.Transcript && !cfg.TranscribeEnabled) return "transcription is off";

            string model, version;
            IOcrEngine? ocrEngine = null;
            ITranscriber? speech = null;
            if (task == MediaTextTasks.Ocr)
            {
                ocrEngine = await ocr().ConfigureAwait(false);
                if (ocrEngine == null) return "skipped: OCR is not available in this build";
                (model, version) = (ocrEngine.Model, ocrEngine.Version);
            }
            else
            {
                speech = await transcriber().ConfigureAwait(false);
                if (speech == null) return "skipped: no speech service is available";
                (model, version) = (speech.Model, speech.Model);
            }

            var cached = ReadCache(row.Sha, task, version);
            if (cached == null)
            {
                string path = media.FullPath(relative);
                try
                {
                    var result = task == MediaTextTasks.Ocr
                        ? await ReadImageAsync(ocrEngine!, path, cfg, ct).ConfigureAwait(false)
                        : await ReadSpeechAsync(speech!, path, cfg, ct, progress).ConfigureAwait(false);
                    cached = (result.Text.Length > cfg.MaxChars ? result.Text[..cfg.MaxChars] : result.Text, result.Confidence, result.Language, "ok", null);
                }
                catch (MediaTextUnavailableException ex)
                {
                    // Nothing is recorded: a later sweep tries again once the engine can run.
                    return "skipped: " + ex.Message;
                }
                catch (JobFailureException ex) when (task == MediaTextTasks.Transcript && ex.Class == ErrorClass.Transient
                                                     && lastAttemptSinceMs is long since && Volatile.Read(ref speechAnsweredMs) > since)
                {
                    cached = ("", 0, null, "unreadable", "The speech service failed on this recording every time it was tried, while it transcribed others: " + ex.Message);
                }
                catch (ImageUnreadableException ex)
                {
                    cached = ("", 0, null, "unreadable", ex.Message);
                }
                await WriteCacheAsync(row.Sha, task, model, version, cached.Value, ct).ConfigureAwait(false);
            }

            if (cached.Value.State != "ok") return "unreadable: " + cached.Value.Error;
            await WriteLabelAsync(attachmentId, row.Sha, task, model, version, cached.Value, sourceSeq, ct).ConfigureAwait(false);
            return cached.Value.Text.Length == 0 ? "nothing to read" : $"{cached.Value.Text.Length} characters";
        }

        private static async Task<MediaTextResult> ReadImageAsync(IOcrEngine engine, string path, MediaTextSettings cfg, CancellationToken ct)
        {
            var frames = ImageProbe.LoadFrames(path);
            try
            {
                var result = await engine.ReadAsync(frames, ct).ConfigureAwait(false);
                string text = Clean(result.Text);
                // A handful of stray glyphs, or text the engine itself doubts, is texture — not writing.
                bool legible = result.Confidence >= cfg.OcrMinConfidence && text.Count(char.IsLetterOrDigit) >= 3;
                return legible ? result with { Text = text } : new MediaTextResult("", result.Confidence, result.Language);
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }
        }

        private async Task<MediaTextResult> ReadSpeechAsync(ITranscriber speech, string path, MediaTextSettings cfg, CancellationToken ct, Func<object, Task>? progress)
        {
            var segments = await segmenter.SegmentAsync(path, cfg.MaxAudio, ct).ConfigureAwait(false);
            var parts = new List<string>(segments.Count);
            string? language = null;
            double confidence = 0;
            int spoken = 0, done = 0;
            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                // Half an hour of sound is some forty pieces, each a request of its own: the job says where it is after
                // every one (it used to say nothing until the end, and a long recording looked like a job that had hung).
                if (progress != null) await progress(new { stage = "transcribe", piece = done + 1, of = segments.Count }).ConfigureAwait(false);
                SpeechResult piece;
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(PieceLimit);
                    try
                    {
                        // Later pieces are told the language the first piece was in, so a quiet stretch is not re-guessed.
                        piece = await speech.TranscribeAsync(segment, language, limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new JobFailureException(ErrorClass.Transient, $"the speech service did not answer within {PieceLimit.TotalMinutes:0.#} minutes");
                    }
                }
                Volatile.Write(ref speechAnsweredMs, clock.NowMs);
                done++;
                if (piece.NoSpeechProbability >= NoSpeechThreshold || string.IsNullOrWhiteSpace(piece.Text)) continue;
                language ??= piece.Language;
                parts.Add(Clean(piece.Text));
                confidence += Math.Clamp(Math.Exp(piece.AverageLogProbability), 0, 1);
                spoken++;
            }
            // Music, silence, a pocket recording: an empty transcript, which is the truth about the clip.
            return spoken == 0 ? new MediaTextResult("", 0, null) : new MediaTextResult(TranscriptMerge.Join(parts), confidence / spoken, language);
        }

        private static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text.Replace("\r\n", "\n"))
                if (ch == '\n' || ch == '\t' || !char.IsControl(ch)) sb.Append(ch);
            return sb.ToString().Trim();
        }

        private (string Text, double Confidence, string? Language, string State, string? Error)? ReadCache(string sha, string task, string version)
        {
            using var c = stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT text, confidence, language, state, error FROM media_text WHERE sha256=$s AND task=$t AND model_version=$v",
                r => (Found: true, Text: r.GetString(0), Confidence: r.GetDoubleOrNull(1) ?? 0, Language: r.GetStringOrNull(2), State: r.GetString(3), Error: r.GetStringOrNull(4)),
                ("$s", sha), ("$t", task), ("$v", version)).FirstOrDefault();
            return row.Found ? (row.Text, row.Confidence, row.Language, row.State, row.Error) : null;
        }

        private Task WriteCacheAsync(string sha, string task, string model, string version, (string Text, double Confidence, string? Language, string State, string? Error) r, CancellationToken ct)
        {
            long now = clock.NowMs;
            return stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO media_text(sha256, task, model, model_version, text, confidence, language, state, error, computed_at_ms)
                VALUES($s,$t,$m,$v,$x,$c,$l,$st,$e,$n)
                ON CONFLICT(sha256, task, model_version) DO UPDATE SET text=excluded.text, confidence=excluded.confidence, language=excluded.language,
                    state=excluded.state, error=excluded.error, computed_at_ms=excluded.computed_at_ms
                """, ("$s", sha), ("$t", task), ("$m", model), ("$v", version), ("$x", r.Text), ("$c", r.Confidence), ("$l", r.Language), ("$st", r.State),
                ("$e", r.Error), ("$n", now)), ct);
        }

        private Task WriteLabelAsync(string attachmentId, string sha, string task, string model, string version,
            (string Text, double Confidence, string? Language, string State, string? Error) r, long sourceSeq, CancellationToken ct)
        {
            long now = clock.NowMs;
            string signals = JsonSerializer.Serialize(new
            {
                text = r.Text,
                language = r.Language,
                origin = task == MediaTextTasks.Ocr ? "image_text" : "speech",
                sha256 = sha,
            }, KernelJson.Options);
            bool empty = r.Text.Length == 0;
            return stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                VALUES('attachment',$i,1,$t,$l,$p,$a,$s,$m,$v,NULL,$n,$seq)
                ON CONFLICT(item_kind, item_id, task, model_version) DO UPDATE SET label=excluded.label, probability=excluded.probability,
                    abstained=excluded.abstained, signals_json=excluded.signals_json, as_of_ms=excluded.as_of_ms, source_seq=excluded.source_seq
                """, ("$i", attachmentId), ("$t", task), ("$l", empty ? "none" : "text"), ("$p", r.Confidence), ("$a", empty ? 1 : 0), ("$s", signals),
                ("$m", model), ("$v", version), ("$n", now), ("$seq", sourceSeq)), ct);
        }
    }

    /// <summary>
    /// Joins transcripts of consecutive, slightly overlapping audio pieces. The overlap means the end of one piece
    /// and the start of the next say the same few words; the repeated words are kept once.
    /// </summary>
    public static class TranscriptMerge
    {
        private const int MaxOverlapWords = 14;

        public static string Join(IReadOnlyList<string> pieces)
        {
            var words = new List<string>();
            foreach (var piece in pieces)
            {
                var next = piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (next.Length == 0) continue;
                int overlap = 0;
                for (int n = Math.Min(MaxOverlapWords, Math.Min(words.Count, next.Length)); n >= 2; n--)
                {
                    bool same = true;
                    for (int i = 0; i < n && same; i++) same = Normal(words[words.Count - n + i]) == Normal(next[i]);
                    if (same) { overlap = n; break; }
                }
                words.AddRange(next.Skip(overlap));
            }
            return string.Join(' ', words);
        }

        private static string Normal(string word) => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// Tesseract OCR with language data fetched and pinned through <see cref="ModelHub"/> (the v2 service downloaded
    /// it unhashed and gave up for good on one failure, F335/F475). One engine, one caller at a time: Tesseract
    /// engines are not thread-safe.
    /// </summary>
    public sealed class TesseractOcr : IOcrEngine, IDisposable
    {
        private const string DataRelease = "4.1.0";
        private readonly ModelHub hub;
        private readonly IReadOnlyList<string> languages;
        private readonly SemaphoreSlim gate = new(1, 1);
        private Tesseract.TesseractEngine? engine;
        private int abandoned;

        /// <summary>How long one image may take. Tesseract cannot be interrupted, and on some images it does not come back:
        /// on 19 Nov 2026 one of them held the engine, and so every worker waiting for it, from 16:27 until the service
        /// was restarted.</summary>
        public TimeSpan PerImageLimit { get; init; } = TimeSpan.FromSeconds(90);

        /// <summary>After this many images that never came back, OCR stops until the next start: each one left a thread running.</summary>
        public const int MaxAbandoned = 3;

        /// <summary>Images the engine did not come back from since this engine was created.</summary>
        public int Abandoned => Volatile.Read(ref abandoned);

        /// <param name="languages">Tesseract language codes ("eng", "nld", "deu"…); the first is the primary one.</param>
        public TesseractOcr(ModelHub hub, IReadOnlyList<string> languages)
        {
            this.hub = hub;
            this.languages = languages.Count == 0 ? new[] { "eng" } : languages;
        }

        public string Model => "tesseract";
        public string Version => $"tesseract-5.2/tessdata_fast-{DataRelease}/{string.Join('+', languages)}";

        private static ModelArtifact Data(string language) => new(
            "tessdata-fast-" + DataRelease, language + ".traineddata",
            new Uri($"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{DataRelease}/{language}.traineddata"),
            ExpectedSha256: null, // pinned on first download in the model lock file, verified on every load after
            Licence: "Apache-2.0");

        public async Task<MediaTextResult> ReadAsync(IReadOnlyList<SKBitmap> frames, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Abandoned >= MaxAbandoned)
                    throw new MediaTextUnavailableException($"OCR has stopped for now: {Abandoned} images never came back from the engine. It starts again with the service.");
                if (engine == null)
                {
                    string? folder = null;
                    try
                    {
                        foreach (var language in languages)
                            folder = Path.GetDirectoryName(await hub.EnsureAsync(Data(language), ct).ConfigureAwait(false));
                        engine = new Tesseract.TesseractEngine(folder!, string.Join('+', languages), Tesseract.EngineMode.Default);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or ModelIntegrityException or Tesseract.TesseractException or DllNotFoundException)
                    {
                        throw new MediaTextUnavailableException("the OCR language data could not be loaded (" + ex.Message + ")", ex);
                    }
                }

                // The pixels are encoded here, on this side of the limit: the frames belong to the caller, who disposes
                // them when this returns, and a recognition that is left behind must not be reading them then.
                var images = new List<byte[]>(frames.Count);
                foreach (var frame in frames)
                {
                    using var png = frame.Encode(SKEncodedImageFormat.Png, 100);
                    if (png != null) images.Add(png.ToArray());
                }
                var current = engine;
                var work = Task.Run(() => Recognise(current, images, languages[0], ct), CancellationToken.None);
                bool finished;
                try
                {
                    finished = await Task.WhenAny(work, Task.Delay(PerImageLimit, ct)).ConfigureAwait(false) == work;
                }
                catch (OperationCanceledException)
                {
                    finished = work.IsCompleted;
                    if (!finished) LeaveBehind(work);
                    throw;
                }
                if (!finished)
                {
                    LeaveBehind(work);
                    throw new ImageUnreadableException($"OCR did not finish within {PerImageLimit.TotalSeconds:0} seconds; the image is skipped.");
                }
                return await work.ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The engine is still inside the image and cannot be stopped: it is never used again, and the next image
        /// gets a new one.</summary>
        private void LeaveBehind(Task work)
        {
            engine = null;
            Interlocked.Increment(ref abandoned);
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private static MediaTextResult Recognise(Tesseract.TesseractEngine engine, IReadOnlyList<byte[]> images, string language, CancellationToken ct)
        {
            var texts = new List<string>();
            double confidence = 0;
            int read = 0;
            foreach (var image in images)
            {
                if (ct.IsCancellationRequested) break;
                using var pix = Tesseract.Pix.LoadFromMemory(image);
                using var page = engine.Process(pix);
                string text = (page.GetText() ?? "").Trim();
                confidence += page.GetMeanConfidence();
                read++;
                // Frames of an animation mostly repeat each other's text.
                if (text.Length > 0 && !texts.Contains(text)) texts.Add(text);
            }
            return new MediaTextResult(string.Join("\n", texts), read == 0 ? 0 : confidence / read, language);
        }

        public void Dispose()
        {
            engine?.Dispose();
            gate.Dispose();
        }
    }

    /// <summary>
    /// Cuts audio (or a video's sound track) into mono 16 kHz MP3 pieces with ffmpeg: small enough for the speech
    /// service's request limit, short enough for its time limit, and overlapping by two seconds so no word is lost
    /// at a cut (the overlap is removed again by <see cref="TranscriptMerge"/>).
    /// </summary>
    public sealed class FfmpegSegmenter : IAudioSegmenter
    {
        private static readonly TimeSpan Piece = TimeSpan.FromSeconds(50), Overlap = TimeSpan.FromSeconds(2);
        private readonly string ffmpegFolder;
        private readonly string workFolder;

        public FfmpegSegmenter(string ffmpegFolder, string workFolder)
        {
            this.ffmpegFolder = ffmpegFolder;
            this.workFolder = workFolder;
        }

        public async Task<IReadOnlyList<AudioSegment>> SegmentAsync(string path, TimeSpan maxLength, CancellationToken ct)
        {
            var options = new FFOptions { BinaryFolder = ffmpegFolder, WorkingDirectory = workFolder };
            if (!Directory.Exists(ffmpegFolder) || !Directory.EnumerateFiles(ffmpegFolder, "ffmpeg*").Any())
                throw new MediaTextUnavailableException("ffmpeg is not installed, so audio and video cannot be prepared for transcription");
            Directory.CreateDirectory(workFolder);

            TimeSpan duration;
            try
            {
                var analysis = await FFProbe.AnalyseAsync(path, options, ct).ConfigureAwait(false);
                if (analysis.PrimaryAudioStream == null) return Array.Empty<AudioSegment>(); // a silent video
                duration = analysis.Duration;
            }
            catch (Exception ex) when (ex is FFMpegCore.Exceptions.FFMpegException or IOException or System.ComponentModel.Win32Exception or JsonException)
            {
                throw new ImageUnreadableException("ffmpeg could not read the file: " + ex.Message);
            }
            if (duration > maxLength) duration = maxLength;

            var segments = new List<AudioSegment>();
            for (var start = TimeSpan.Zero; start < duration; start += Piece - Overlap)
            {
                ct.ThrowIfCancellationRequested();
                var length = duration - start < Piece ? duration - start : Piece;
                if (length < TimeSpan.FromMilliseconds(300)) break;
                string output = Path.Combine(workFolder, Guid.NewGuid().ToString("N") + ".mp3");
                try
                {
                    await FFMpegArguments
                        .FromFileInput(path, verifyExists: true, o => o.Seek(start).WithDuration(length))
                        .OutputToFile(output, overwrite: true, o => o.DisableChannel(FFMpegCore.Enums.Channel.Video).WithAudioSamplingRate(16000)
                            .WithCustomArgument("-ac 1 -b:a 32k"))
                        .CancellableThrough(ct)
                        .ProcessAsynchronously(throwOnError: true, options).ConfigureAwait(false);
                    segments.Add(new AudioSegment(await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false), Path.GetFileName(output), "audio/mpeg", start.TotalSeconds));
                }
                catch (FFMpegCore.Exceptions.FFMpegException ex)
                {
                    throw new ImageUnreadableException("ffmpeg could not convert the audio: " + ex.Message);
                }
                finally
                {
                    if (File.Exists(output)) File.Delete(output);
                }
            }
            return segments;
        }
    }

    /// <summary>Speech-to-text through KliveLLM's AIRouter audio client (Whisper), resolved when first needed.</summary>
    public sealed class AiRouterTranscriber : ITranscriber
    {
        private readonly KliveLLM.AIRouterAudioClient audio;

        public AiRouterTranscriber(KliveLLM.AIRouterAudioClient audio) => this.audio = audio;

        public string Model => KliveLLM.AIRouterAudioClient.TranscriptionModel;

        public async Task<SpeechResult> TranscribeAsync(AudioSegment segment, string? language, CancellationToken ct)
        {
            try
            {
                var t = await audio.TranscribeAsync(segment.Bytes, segment.FileName, segment.Mime, language, null, ct).ConfigureAwait(false);
                return new SpeechResult(t.Text ?? "", t.Language, t.NoSpeechProbability, t.AverageLogProbability);
            }
            catch (KliveLLM.AIRouterAudioException ex) when ((int?)ex.Status is 429 or >= 500 or null)
            {
                throw new JobFailureException(ErrorClass.Transient, "the speech service did not answer: " + ex.Message, ex);
            }
            catch (KliveLLM.AIRouterAudioException ex)
            {
                throw new MediaTextUnavailableException("the speech service refused the request (" + ex.Message + ")", ex);
            }
        }
    }
}
