using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>
    /// Text from media: what is stored, where, and what happens when an engine has nothing to say or cannot run.
    /// The engines themselves are stand-ins: Tesseract needs downloaded language data and transcription a speech service.
    /// </summary>
    public class MediaTextTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_mediatext_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private MediaStore store = null!;
        private readonly FakeOcr ocr = new();
        private readonly FakeSpeech speech = new();
        private readonly FakeSegmenter segmenter = new();
        private MediaTextSettings settings = new();
        private bool ocrInstalled = true, speechInstalled = true;

        private static readonly long T0 = Instant.FromUtc(new DateTime(2024, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            await stores.Intel.ApplyMigrationsAsync(new MediaModule().IntelMigrations);
            await stores.Intel.ApplyMigrationsAsync(new MediaTextModule().IntelMigrations);
            store = new MediaStore(stores.Paths);
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        // ── stand-in engines ─────────────────────────────────────────────────────

        private sealed class FakeOcr : IOcrEngine
        {
            public int Calls;
            public MediaTextResult Result = new("SALE ENDS FRIDAY", 0.91, "eng");
            public string? Unavailable;
            public string Model => "fake-ocr";
            public string Version { get; set; } = "fake-ocr/1";

            public Task<MediaTextResult> ReadAsync(IReadOnlyList<SKBitmap> frames, CancellationToken ct)
            {
                Calls++;
                Assert.NotEmpty(frames);
                if (Unavailable != null) throw new MediaTextUnavailableException(Unavailable);
                return Task.FromResult(Result);
            }
        }

        /// <summary>"Hears" whatever text the segment's bytes spell; the text "(silence)" is heard as no speech.</summary>
        private sealed class FakeSpeech : ITranscriber
        {
            public List<string?> LanguagesAsked { get; } = new();
            public string Model => "fake-whisper";

            public Task<SpeechResult> TranscribeAsync(AudioSegment segment, string? language, CancellationToken ct)
            {
                LanguagesAsked.Add(language);
                string heard = Encoding.UTF8.GetString(segment.Bytes);
                return Task.FromResult(heard == "(silence)" ? new SpeechResult("", null, 0.95, -2.0) : new SpeechResult(heard, "en", 0.02, -0.2));
            }
        }

        private sealed class FakeSegmenter : IAudioSegmenter
        {
            public string[] Pieces = { "hello there" };
            public string? Unavailable;

            public Task<IReadOnlyList<AudioSegment>> SegmentAsync(string path, TimeSpan maxLength, CancellationToken ct)
            {
                if (Unavailable != null) throw new MediaTextUnavailableException(Unavailable);
                return Task.FromResult<IReadOnlyList<AudioSegment>>(Pieces.Select((p, i) => new AudioSegment(Encoding.UTF8.GetBytes(p), $"{i}.mp3", "audio/mpeg", i * 48.0)).ToList());
            }
        }

        private MediaTextProcessor Processor() => new(stores, clock, store, () => settings,
            () => Task.FromResult<IOcrEngine?>(ocrInstalled ? ocr : null), () => Task.FromResult<ITranscriber?>(speechInstalled ? speech : null), segmenter);

        // ── helpers ──────────────────────────────────────────────────────────────

        private static byte[] Picture(int seed)
        {
            using var bitmap = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(seed * 40), 120, 200));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private async Task<string> AttachAsync(string messageId, byte[] bytes, string filename, string? kind = null, string author = "u-ada")
        {
            var stored = await store.PutAsync(new MemoryStream(bytes), null, 1 << 24);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", "room", ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", author),
                    SentAtMs = T0, OrderKey = T0, Attachments = new[] { new IngestAttachment(filename, MediaKind: kind) { Stored = stored } },
                });
                return w.Complete();
            });
            return $"testnet:{messageId}#0";
        }

        private List<(string Item, string Task, string? Label, double Probability, bool Abstained, JsonElement Signals, string Version)> Labels()
        {
            using var c = stores.Intel.OpenRead();
            return c.Query(null, "SELECT item_id, task, label, probability, abstained, signals_json, model_version FROM labels WHERE item_kind='attachment' ORDER BY item_id, task",
                r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetDouble(3), r.GetInt32(4) == 1, JsonDocument.Parse(r.GetString(5)).RootElement.Clone(), r.GetString(6)));
        }

        // ── OCR ──────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Ocr_ReadsEachPictureOnce_AndLabelsEveryAttachmentThatCarriesIt()
        {
            byte[] meme = Picture(1);
            string first = await AttachAsync("m1", meme, "meme.png");
            string repost = await AttachAsync("m2", meme, "same-meme.png");
            var processor = Processor();

            Assert.Equal("16 characters", await processor.ProcessAsync(first, 5, default));
            Assert.Equal("16 characters", await processor.ProcessAsync(repost, 6, default));

            Assert.Equal(1, ocr.Calls); // the repost is the same content: read from the cache
            var labels = Labels();
            Assert.Equal(new[] { first, repost }, labels.Select(l => l.Item));
            Assert.All(labels, l =>
            {
                Assert.Equal((MediaTextTasks.Ocr, "text", false, "fake-ocr/1"), (l.Task, l.Label, l.Abstained, l.Version));
                Assert.Equal("SALE ENDS FRIDAY", l.Signals.GetProperty("text").GetString());
                // Text in a picture is quoted material, not something the poster wrote.
                Assert.Equal("image_text", l.Signals.GetProperty("origin").GetString());
                Assert.Equal(0.91, l.Probability, 3);
            });

            // The shape the knowledge workstream's evidence reader queries.
            using var c = stores.Intel.OpenRead();
            Assert.Equal(2, c.Query(null, "SELECT item_id, task, signals_json FROM labels WHERE item_kind='attachment' AND task IN ('ocr','transcript','caption')", r => r.GetString(0)).Count);

            // A new engine version reads again and keeps the old label beside the new one.
            ocr.Version = "fake-ocr/2";
            ocr.Result = new MediaTextResult("SALE ENDS FRIDAY!", 0.95, "eng");
            await processor.ProcessAsync(first, 7, default);
            Assert.Equal(2, ocr.Calls);
            Assert.Equal(2, Labels().Count(l => l.Item == first));
        }

        [Fact]
        public async Task Ocr_DropsWhatIsNotWriting_AndSaysSoInsteadOfLeavingItUnread()
        {
            var processor = Processor();

            // The engine doubts itself: stylised lettering, a texture.
            ocr.Result = new MediaTextResult("WVVVW lllI", 0.31, "eng");
            string texture = await AttachAsync("m1", Picture(1), "texture.png");
            Assert.Equal("nothing to read", await processor.ProcessAsync(texture, 1, default));

            // Confident, but only stray marks.
            ocr.Result = new MediaTextResult(" ~ | . ", 0.9, "eng");
            string marks = await AttachAsync("m2", Picture(2), "marks.png");
            Assert.Equal("nothing to read", await processor.ProcessAsync(marks, 1, default));

            var labels = Labels();
            Assert.Equal(2, labels.Count);
            Assert.All(labels, l =>
            {
                Assert.True(l.Abstained);
                Assert.Equal("none", l.Label);
                Assert.Equal("", l.Signals.GetProperty("text").GetString());
            });
        }

        [Fact]
        public async Task UnreadableFiles_AreRememberedAsSuch_AndFilesWithNoTextToGiveAreLeftAlone()
        {
            var processor = Processor();
            string broken = await AttachAsync("m1", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 }, "broken.png");

            Assert.StartsWith("unreadable:", await processor.ProcessAsync(broken, 1, default));
            Assert.StartsWith("unreadable:", await processor.ProcessAsync(broken, 2, default));
            Assert.Equal(0, ocr.Calls); // never reached the engine, and the verdict was cached
            Assert.Empty(Labels());

            string pdf = await AttachAsync("m2", Encoding.ASCII.GetBytes("%PDF-1.7 synthetic"), "notes.pdf");
            Assert.Equal("this kind of file has no text to read", await processor.ProcessAsync(pdf, 1, default));
            Assert.Equal("no such attachment", await processor.ProcessAsync("testnet:none#0", 1, default));

            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE attachments SET download_state='quarantined' WHERE attachment_id=$a", ("$a", pdf)));
            Assert.Equal("no stored file", await processor.ProcessAsync(pdf, 1, default));
        }

        [Fact]
        public async Task AnEngineThatCannotRun_SkipsWithTheReason_AndNothingIsRecordedSoALaterTryWorks()
        {
            string picture = await AttachAsync("m1", Picture(1), "sign.png");

            ocrInstalled = false;
            Assert.Equal("skipped: OCR is not available in this build", await Processor().ProcessAsync(picture, 1, default));

            ocrInstalled = true;
            ocr.Unavailable = "the OCR language data could not be loaded (no network)";
            Assert.Equal("skipped: the OCR language data could not be loaded (no network)", await Processor().ProcessAsync(picture, 1, default));
            Assert.Empty(Labels());

            ocr.Unavailable = null;
            Assert.Equal("16 characters", await Processor().ProcessAsync(picture, 1, default));
            Assert.Single(Labels());

            settings = settings with { OcrEnabled = false };
            Assert.Equal("OCR is off", await Processor().ProcessAsync(await AttachAsync("m2", Picture(2), "other.png"), 1, default));
        }

        // ── transcripts ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Transcript_JoinsOverlappingPieces_AndIsFiledAsSpeech()
        {
            // Three pieces of one voice note; each starts with the last words of the one before.
            segmenter.Pieces = new[] { "so I was thinking we could", "we could go on Saturday.", "on saturday if the weather holds" };
            string note = await AttachAsync("m1", Encoding.ASCII.GetBytes("synthetic voice note"), "voice-message.ogg", MediaKinds.Voice);

            await Processor().ProcessAsync(note, 3, default);

            var label = Assert.Single(Labels());
            Assert.Equal((MediaTextTasks.Transcript, "text", false, "fake-whisper"), (label.Task, label.Label, label.Abstained, label.Version));
            Assert.Equal("so I was thinking we could go on Saturday. if the weather holds", label.Signals.GetProperty("text").GetString());
            Assert.Equal("speech", label.Signals.GetProperty("origin").GetString());
            Assert.Equal("en", label.Signals.GetProperty("language").GetString());
            // The first piece is heard cold; the rest are told the language it was in.
            Assert.Equal(new string?[] { null, "en", "en" }, speech.LanguagesAsked);
        }

        [Fact]
        public async Task Transcript_SilenceIsAnEmptyTranscript_AndMissingToolsAreASkip()
        {
            string clip = await AttachAsync("m1", Encoding.ASCII.GetBytes("synthetic clip"), "clip.mp4");
            segmenter.Pieces = new[] { "(silence)", "(silence)" };
            Assert.Equal("nothing to read", await Processor().ProcessAsync(clip, 1, default));
            var label = Assert.Single(Labels());
            Assert.True(label.Abstained);
            Assert.Equal("", label.Signals.GetProperty("text").GetString());

            string song = await AttachAsync("m2", Encoding.ASCII.GetBytes("another clip"), "song.mp3");
            segmenter.Unavailable = "ffmpeg is not installed, so audio and video cannot be prepared for transcription";
            Assert.StartsWith("skipped: ffmpeg is not installed", await Processor().ProcessAsync(song, 1, default));
            speechInstalled = false;
            Assert.Equal("skipped: no speech service is available", await Processor().ProcessAsync(song, 1, default));
            settings = settings with { TranscribeEnabled = false };
            Assert.Equal("transcription is off", await Processor().ProcessAsync(song, 1, default));
            Assert.Single(Labels());
        }

        [Fact]
        public void TranscriptMerge_RemovesOnlyARealOverlap()
        {
            Assert.Equal("one two three four", TranscriptMerge.Join(new[] { "one two", "three four" }));
            Assert.Equal("we went to the shop and bought milk", TranscriptMerge.Join(new[] { "we went to the shop", "To the shop, and bought milk" }));
            // One shared word is how sentences work, not an overlap.
            Assert.Equal("I said no no I did not", TranscriptMerge.Join(new[] { "I said no", "no I did not" }));
            Assert.Equal("alone", TranscriptMerge.Join(new[] { "", "alone", "  " }));
            Assert.Equal("", TranscriptMerge.Join(Array.Empty<string>()));
        }

        [Fact]
        public void TheTaskFollowsTheKindOfFile()
        {
            Assert.Equal(MediaTextTasks.Ocr, MediaTextProcessor.TaskFor(MediaKinds.Image));
            Assert.Equal(MediaTextTasks.Transcript, MediaTextProcessor.TaskFor(MediaKinds.Voice));
            Assert.Equal(MediaTextTasks.Transcript, MediaTextProcessor.TaskFor(MediaKinds.Audio));
            Assert.Equal(MediaTextTasks.Transcript, MediaTextProcessor.TaskFor(MediaKinds.Video));
            Assert.Null(MediaTextProcessor.TaskFor(MediaKinds.File));
        }

        // ── the module in the kernel ─────────────────────────────────────────────

        private sealed class NullLlm : ILlmClient
        {
            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) => throw new InvalidOperationException("No LLM in this test.");
            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("AIRouter", "test", 260_000, 16_384, "operator-stated", null, 0, null));
            public void EndSession(string sessionKey) { }
        }

        [Fact]
        public async Task Module_ReadsWhatIsStored_AndItsSweepFindsWhatWasMissed()
        {
            await stores.DisposeAsync(); // the runtime owns the stores in this test
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var module = new MediaTextModule(() => Task.FromResult<ITranscriber?>(speech), ocr, segmenter);
            var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new NullLlm(), Log = _ => { },
                LogError = (m, e) => errors.Enqueue(m + " " + e?.Message),
                Kernel = new KernelOptions { StartWorkers = true, StartLoop = false },
                Modules = new IIntelModule[] { new MediaModule(), module }, LockWait = TimeSpan.FromSeconds(5),
            });
            try
            {
                stores = rt.Stores;
                store = new MediaStore(stores.Paths);

                // Announced by the change log: stored, so read.
                string sign = await AttachAsync("live1", Picture(1), "sign.png");
                await rt.Host.PumpChangesOnceAsync();
                await rt.Host.PlanOnceAsync();
                await WaitForAsync(() => Labels().Any(l => l.Item == sign));

                // Missed (it was stored while the module was not running): only the sweep can find it.
                string missed = await AttachAsync("old1", Encoding.ASCII.GetBytes("synthetic voice note"), "voice.ogg", MediaKinds.Voice);
                await rt.Host.PumpChangesOnceAsync(); // consumed, but nothing is planned from it here
                await rt.Host.Jobs.EnqueueAsync(new Omnipotent.Services.OmniscienceV3.Kernel.Jobs.JobRequest
                {
                    ModuleId = MediaTextModule.ModuleId, Kind = MediaTextModule.SweepKind, Lane = Omnipotent.Services.OmniscienceV3.Kernel.Jobs.Lane.Io, DedupeKey = "test-sweep",
                });
                await WaitForAsync(() => Labels().Any(l => l.Item == missed && l.Task == MediaTextTasks.Transcript));
                Assert.Equal("hello there", Labels().Single(l => l.Item == missed).Signals.GetProperty("text").GetString());
                Assert.Empty(errors);
            }
            finally
            {
                await rt.DisposeAsync();
                stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            }
        }

        [Fact]
        public async Task Sweep_QueuesTheBacklogOfTrackedPeopleFirst_AtAHigherPriority()
        {
            await stores.DisposeAsync(); // the runtime owns the stores in this test
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var module = new MediaTextModule(() => Task.FromResult<ITranscriber?>(speech), ocr, segmenter);
            var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new NullLlm(), Log = _ => { },
                LogError = (m, e) => errors.Enqueue(m + " " + e?.Message),
                Kernel = new KernelOptions { StartWorkers = true, StartLoop = false },
                Modules = new IIntelModule[] { new MediaModule(), module }, LockWait = TimeSpan.FromSeconds(5),
            });
            try
            {
                stores = rt.Stores;
                store = new MediaStore(stores.Paths);

                // Twelve old pictures from someone Klives does not track, then one from someone he does, last in attachment order.
                for (int i = 1; i <= 12; i++) await AttachAsync($"a{i:00}", Picture(i), "p.png");
                string theirs = await AttachAsync("z99", Picture(13), "t.png", author: "u-bo");
                string bo;
                using (var c = stores.Corpus.OpenRead()) bo = c.Scalar<string>(null, "SELECT person_id FROM identities WHERE platform_user_id='u-bo'")!;
                await stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, minor_marked, pinned, updated_at_ms) VALUES($p, 'tracked', 0, 0, $n)",
                    ("$p", bo), ("$n", clock.NowMs)));
                await rt.Host.PumpChangesOnceAsync(); // consumed, nothing planned from it: only the sweep can find these
                await rt.Host.Settings.SetAsync(MediaTextModule.SweepBatchSetting, 10, "test");

                await rt.Host.Jobs.EnqueueAsync(new Omnipotent.Services.OmniscienceV3.Kernel.Jobs.JobRequest
                {
                    ModuleId = MediaTextModule.ModuleId, Kind = MediaTextModule.SweepKind, Lane = Omnipotent.Services.OmniscienceV3.Kernel.Jobs.Lane.Io, DedupeKey = "test-sweep",
                });
                List<(string Subject, int Priority)> Extracts()
                {
                    using var c = stores.Intel.OpenRead();
                    return c.Query(null, "SELECT subject_id, priority FROM jobs WHERE module_id=$m AND kind=$k ORDER BY job_id",
                        r => (r.GetString(0), r.GetInt32(1)), ("$m", MediaTextModule.ModuleId), ("$k", MediaTextModule.ExtractKind));
                }
                bool SweepDone()
                {
                    using var c = stores.Intel.OpenRead();
                    return c.Scalar<long>(null, "SELECT COUNT(*) FROM jobs WHERE module_id=$m AND kind=$k AND state='succeeded'", ("$m", MediaTextModule.ModuleId), ("$k", MediaTextModule.SweepKind)) == 1;
                }
                await WaitForAsync(SweepDone);

                // The budget of ten went to the tracked person's picture first, at the higher priority, then to the oldest nine others.
                var queued = Extracts();
                Assert.Equal(10, queued.Count);
                Assert.Equal((theirs, MediaTextModule.TrackedPriority), queued[0]);
                Assert.Equal(Enumerable.Range(1, 9).Select(i => $"testnet:a{i:00}#0"), queued.Skip(1).Select(q => q.Subject));
                Assert.All(queued.Skip(1), q => Assert.Equal(MediaTextModule.BacklogPriority, q.Priority));
                await WaitForAsync(() => Labels().Count(l => l.Task == MediaTextTasks.Ocr) == 10);

                // The next sweep does not walk the tracked person's media again (nothing of theirs is left) and finishes the rest.
                await rt.Host.Jobs.EnqueueAsync(new Omnipotent.Services.OmniscienceV3.Kernel.Jobs.JobRequest
                {
                    ModuleId = MediaTextModule.ModuleId, Kind = MediaTextModule.SweepKind, Lane = Omnipotent.Services.OmniscienceV3.Kernel.Jobs.Lane.Io, DedupeKey = "test-sweep-2",
                });
                // (A job that finishes between the sweep's label check and its enqueue may be queued twice; dedupe covers only
                // queued and leased jobs, and the second read finds the cached text. So distinct subjects are counted.)
                await WaitForAsync(() => Extracts().Select(q => q.Subject).Distinct().Count() == 13);
                Assert.Equal(new[] { "testnet:a10#0", "testnet:a11#0", "testnet:a12#0" }, Extracts().Skip(10).Select(q => q.Subject).Distinct());
                await WaitForAsync(() => Labels().Count(l => l.Task == MediaTextTasks.Ocr) == 13);
                Assert.Empty(errors);
            }
            finally
            {
                await rt.DisposeAsync();
                stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            }
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The kernel did not do the work within 30 seconds.");
                await Task.Delay(50);
            }
        }
    }
}
