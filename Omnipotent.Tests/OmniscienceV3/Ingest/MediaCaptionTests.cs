using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>Captions (N2): what is shown to the model, what is filed, and whose pictures are never described. Synthetic images only.</summary>
    public class MediaCaptionTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_caption_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
        private readonly VisionModel model = new();
        private V3Runtime rt = null!;
        private MediaCaptionModule module = null!;

        /// <summary>A stand-in vision model: records what it was shown and answers with a fixed caption.</summary>
        private sealed class VisionModel : ILlmClient
        {
            public readonly ConcurrentQueue<LlmRequest> Requests = new();
            public string Answer = """{"caption":"A blue square on a plain background.","kind":"drawing","tags":["Blue","square","blue"],"has_text":false,"nsfw":false}""";

            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
            {
                Requests.Enqueue(request);
                return Task.FromResult(new LlmResult(true, Answer, Answer, LlmErrorKind.None, null, "stop", "fake-vision", "fake", 900, 120, 40, 50_000, 5));
            }

            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("fake", "fake-vision", 64_000, 4_096, "test", null, 0, null));

            public void EndSession(string sessionKey) { }
        }

        public async Task InitializeAsync()
        {
            module = new MediaCaptionModule();
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = model, Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new MediaModule(), module }, LockWait = TimeSpan.FromSeconds(5),
            });
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private JobContext Job(string? subject) =>
            new(new JobRecord(1, MediaCaptionModule.ModuleId, MediaCaptionModule.CaptionKind, "attachment", subject, null, 100, Lane.LlmBackground, JobStates.Leased, 1, 5, 0,
                "test", null, null, null, null, null, null, clock.NowMs, clock.NowMs, null, null, null), clock, CancellationToken.None, _ => Task.FromResult(true));

        private static byte[] Picture(int seed, int width = 1600, int height = 1200)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(seed * 40), 90, 200));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private async Task<string> AttachAsync(string messageId, byte[] bytes, string author)
        {
            var stored = await new MediaStore(rt.Stores.Paths).PutAsync(new MemoryStream(bytes), null, 1 << 26);
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", "room", ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", author),
                    SentAtMs = clock.NowMs - 60_000, OrderKey = clock.NowMs - 60_000, Attachments = new[] { new IngestAttachment("picture.png") { Stored = stored } },
                });
                return w.Complete();
            });
            return $"testnet:{messageId}#0";
        }

        private string PersonOf(string author)
        {
            using var c = rt.Stores.Corpus.OpenRead();
            return c.Scalar<string>(null, "SELECT person_id FROM identities WHERE identity_id=$i", ("$i", "testnet:" + author))!;
        }

        private List<(string Item, string Label, JsonElement Signals)> Captions()
        {
            using var c = rt.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT item_id, label, signals_json FROM labels WHERE item_kind='attachment' AND task='caption' ORDER BY item_id",
                r => (r.GetString(0), r.GetString(1), JsonDocument.Parse(r.GetString(2)).RootElement.Clone()));
        }

        [Fact]
        public async Task AnImageIsCaptionedOnce_FromASmallJpeg_AndFiledAsAnAiCaption()
        {
            byte[] picture = Picture(1);
            string first = await AttachAsync("m1", picture, "u-ada");
            string repost = await AttachAsync("m2", picture, "u-bo");

            Assert.Equal("captioned", await module.CaptionAsync(first, Job(first)));
            Assert.Equal("captioned", await module.CaptionAsync(repost, Job(repost)));

            // The same picture is described once.
            var request = Assert.Single(model.Requests);
            Assert.Contains("Never say who anyone is", request.SystemPrompt);
            var image = Assert.Single(Assert.Single(request.Turns).Images!);
            Assert.Equal("image/jpeg", image.MimeType);
            using (var shown = SKBitmap.Decode(image.Data))
                Assert.Equal((MediaCaptionModule.MaxEdge, 576), (shown.Width, shown.Height)); // 1600×1200 brought down to a 768 long edge

            var labels = Captions();
            Assert.Equal(new[] { first, repost }, labels.Select(l => l.Item));
            Assert.All(labels, l =>
            {
                Assert.Equal("drawing", l.Label);
                Assert.Equal("ai_caption", l.Signals.GetProperty("origin").GetString());
                Assert.Equal("A blue square on a plain background.", l.Signals.GetProperty("caption").GetString());
                Assert.Equal(new[] { "blue", "square" }, l.Signals.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
            });

            // The call is recorded as one that carried an image, and it does not count against the cache-efficiency guard.
            using (var c = rt.Stores.Intel.OpenRead())
                Assert.Equal(1L, c.Scalar<long>(null, "SELECT images FROM llm_calls WHERE purpose=$p", ("$p", CaptionPrompt.Purpose)));
            Assert.Equal(0, rt.Host.Context.Llm!.CacheEfficiency(TimeSpan.FromHours(1)).Samples);
        }

        [Fact]
        public async Task PicturesOfPeopleWithAnalysisOff_OrWhoMayBeMinors_AreNeverDescribed()
        {
            string off = await AttachAsync("m1", Picture(2), "u-cy");
            string minor = await AttachAsync("m2", Picture(3), "u-dee");
            await rt.Stores.Operator.WriteAsync((c, tx) =>
            {
                Omnipotent.Services.OmniscienceV3.Storage.AnalysisOff.Set(c, tx, new[] { PersonOf("u-cy") }, clock.NowMs);
                c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1",
                    ("$p", PersonOf("u-dee")), ("$t", clock.NowMs));
            });
            ReadModelStore.PolicyChanged();

            Assert.StartsWith("skipped: analysis is switched off", await module.CaptionAsync(off, Job(off)));
            Assert.StartsWith("skipped: posted by someone who is or may be a minor", await module.CaptionAsync(minor, Job(minor)));
            Assert.Empty(model.Requests);
            Assert.Empty(Captions());
        }

        [Fact]
        public async Task TheDailyBudgetDefersTheRest_AndABadAnswerIsNotFiledAsACaption()
        {
            await rt.Host.Settings.SetAsync(MediaCaptionModule.DailyBudgetSetting, 1, "test");
            string a = await AttachAsync("m1", Picture(4), "u-ada");
            string b = await AttachAsync("m2", Picture(5), "u-ada");
            Assert.Equal("captioned", await module.CaptionAsync(a, Job(a)));
            Assert.Equal("deferred: today's caption budget is used", await module.CaptionAsync(b, Job(b)));
            Assert.Equal(0, module.BudgetLeft());

            clock.Advance(TimeSpan.FromHours(25));
            model.Answer = """{"caption":"","kind":"photo","tags":[],"has_text":false,"nsfw":false}""";
            Assert.Equal("failed", await module.CaptionAsync(b, Job(b)));
            Assert.Equal(new[] { a }, Captions().Select(x => x.Item));

            Assert.Null(MediaCaptionModule.Parse("not json", "m"));
            var parsed = MediaCaptionModule.Parse("""{"caption":"A cat.","kind":"unheard-of","tags":["CAT"," cat ","pet"],"nsfw":true}""", "m")!;
            Assert.Equal(("other", true, false), (parsed.Kind, parsed.Nsfw, parsed.HasText));
            Assert.Equal(new[] { "cat", "pet" }, parsed.Tags);
        }
    }
}
