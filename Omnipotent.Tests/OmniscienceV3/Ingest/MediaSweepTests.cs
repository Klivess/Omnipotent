using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>
    /// The media sweeps that were stopped for "no progress" on the server (20 Nov 2026): every ten minutes the store sweep
    /// walked every stored file and read and sorted every pending attachment, with 217,000 probes already queued; every
    /// half hour the caption sweep loaded every image of everyone Klives tracks before queueing a hundred. A sweep is a
    /// top-up: it works for a short time from where it left off, says where it is, and adds nothing while enough waits.
    /// </summary>
    [Collection(nameof(MediaSweepCollection))]
    public class MediaSweepTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_sweep_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc)));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
        private readonly TimeSpan limit = MediaSweep.TimeLimit;
        private V3Runtime rt = null!;

        private sealed class Vision : ILlmClient
        {
            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) =>
                Task.FromResult(new LlmResult(true, "{}", "{}", LlmErrorKind.None, null, "stop", "fake-vision", "fake", 900, 0, 40, 50_000, 5));

            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("fake", "fake-vision", 64_000, 4_096, "test", null, 0, null));

            public void EndSession(string sessionKey) { }
        }

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new Vision(), Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new MediaModule(), new MediaCaptionModule() }, LockWait = TimeSpan.FromSeconds(5),
            });
        }

        public async Task DisposeAsync()
        {
            MediaSweep.TimeLimit = limit;
            await rt.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private static byte[] Picture(int seed)
        {
            using var bitmap = new SKBitmap(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(seed * 9), (byte)(255 - seed * 7), 200));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>One message with one attachment: stored when <paramref name="bytes"/> are given, otherwise still to download.</summary>
        private async Task<string> AttachAsync(string messageId, string author, byte[]? bytes)
        {
            var stored = bytes == null ? null : await new MediaStore(rt.Stores.Paths).PutAsync(new MemoryStream(bytes), null, 1 << 24);
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", "room", ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", author),
                    SentAtMs = clock.NowMs - 60_000, OrderKey = clock.NowMs - 60_000,
                    Attachments = new[] { new IngestAttachment("picture.png", "image/png", "image", SourceUrl: stored == null ? "https://cdn.test/" + messageId + ".png" : null) { Stored = stored } },
                });
                return w.Complete();
            });
            return $"testnet:{messageId}#0";
        }

        private async Task<(JsonElement Result, List<JsonElement> Reports)> SweepAsync(string moduleId, string kind, Lane lane)
        {
            var job = new JobRecord(1, moduleId, kind, null, null, null, 200, lane, JobStates.Leased, 1, 2, 0, "test", null, null, null, null, null, null, clock.NowMs, clock.NowMs, null, null, null);
            var reports = new List<JsonElement>();
            var ctx = new JobContext(job, clock, CancellationToken.None, p => { reports.Add(JsonDocument.Parse(p).RootElement); return Task.FromResult(true); });
            string? result = await rt.Host.Resolve(moduleId, kind)!(ctx);
            return (JsonDocument.Parse(result!).RootElement, reports);
        }

        private List<(string Kind, string Subject, int Priority)> Queued(string moduleId)
        {
            using var c = rt.Stores.Intel.OpenRead();
            return c.Query(null, "SELECT kind, subject_id, priority FROM jobs WHERE module_id=$m AND state='queued' ORDER BY job_id", r => (r.GetString(0), r.GetString(1), r.GetInt32(2)), ("$m", moduleId));
        }

        [Fact]
        public async Task TheStoreSweep_FindsPendingDownloadsThroughTheirIndex_AndAddsNothingWhileEnoughWaits()
        {
            await rt.Host.Settings.SetAsync(MediaModule.SweepBatchSetting, 10, "test");
            await rt.Host.Settings.SetAsync(MediaModule.DownloadSetting, true, "test");
            for (int i = 0; i < 3; i++) await AttachAsync("stored" + i, "u-ada", Picture(i));
            var pending = new List<string>();
            for (int i = 0; i < 15; i++) pending.Add(await AttachAsync("pending" + i.ToString("00"), "u-bo", null));

            var (first, reports) = await SweepAsync(MediaModule.ModuleId, MediaModule.SweepKind, Lane.Io);

            // The three stored files are probed; of the fifteen still to download, the ten newest are queued (the budget).
            Assert.Equal((3, 10), (first.GetProperty("probesQueued").GetInt32(), first.GetProperty("downloadsQueued").GetInt32()));
            var queued = Queued(MediaModule.ModuleId);
            Assert.Equal(pending.Skip(5).OrderBy(x => x), queued.Where(q => q.Kind == MediaModule.DownloadKind).Select(q => q.Subject).OrderBy(x => x));
            Assert.Contains(reports, r => r.GetProperty("stage").GetString() == "downloads");

            // Thirteen jobs wait and a sweep would add ten: there is nothing to top up, and nothing is read to find out.
            var (second, _) = await SweepAsync(MediaModule.ModuleId, MediaModule.SweepKind, Lane.Io);
            Assert.Equal("enough is already queued", second.GetProperty("skipped").GetString());
            Assert.Equal(13, second.GetProperty("queued").GetInt64());
            Assert.Equal(queued, Queued(MediaModule.ModuleId));

            // What is pending is read from its own index, newest first, with nothing sorted (it was a scan of every attachment).
            using var c = rt.Stores.Corpus.OpenRead();
            var plan = c.Query(null, """
                EXPLAIN QUERY PLAN SELECT attachment_id FROM attachments INDEXED BY ix_attachments_pending
                WHERE download_state <> 'done' AND download_state='pending' ORDER BY rowid DESC LIMIT 10
                """, r => r.GetString(3));
            Assert.Contains(plan, p => p.Contains("ix_attachments_pending"));
            Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE") || p.Contains("SCAN attachments") && !p.Contains("INDEX"));
        }

        [Fact]
        public async Task ASweepWorksForItsTime_ThenCarriesOnFromWhereItStopped()
        {
            await rt.Host.Settings.SetAsync(MediaModule.SweepBatchSetting, 10, "test");
            for (int i = 0; i < 4; i++) await AttachAsync("stored" + i, "u-ada", Picture(i));

            // No time at all: the sweep ends at once, having read nothing, and says so by what it queued.
            MediaSweep.TimeLimit = TimeSpan.Zero;
            var (none, _) = await SweepAsync(MediaModule.ModuleId, MediaModule.SweepKind, Lane.Io);
            Assert.Equal(0, none.GetProperty("probesQueued").GetInt32());
            Assert.Empty(Queued(MediaModule.ModuleId));

            MediaSweep.TimeLimit = TimeSpan.FromSeconds(45);
            var (all, _) = await SweepAsync(MediaModule.ModuleId, MediaModule.SweepKind, Lane.Io);
            Assert.Equal(4, all.GetProperty("probesQueued").GetInt32());
        }

        [Fact]
        public async Task TheCaptionSweep_TakesThePeopleWhoComeFirst_APageAtATime_AndStopsWhenEnoughWaits()
        {
            await rt.Host.Settings.SetAsync(MediaCaptionModule.SweepBatchSetting, 3, "test");
            var theirs = new List<string>();
            for (int i = 0; i < 5; i++) await AttachAsync("b" + i, "u-bo", Picture(i));
            for (int i = 0; i < 3; i++) theirs.Add(await AttachAsync("z" + i, "u-ada", Picture(10 + i)));
            for (int i = 5; i < 8; i++) await AttachAsync("b" + i, "u-bo", Picture(i));
            string ada;
            using (var c = rt.Stores.Corpus.OpenRead()) ada = c.Scalar<string>(null, "SELECT person_id FROM identities WHERE identity_id='testnet:u-ada'")!;
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES($p,'tracked',$t)", ("$p", ada), ("$t", clock.NowMs)));

            var (first, _) = await SweepAsync(MediaCaptionModule.ModuleId, MediaCaptionModule.SweepKind, Lane.Cpu);

            // Ada is tracked: her three pictures are the three this sweep may queue, though eight of Bo's come before and
            // after them in the walk.
            Assert.Equal((3, 3), (first.GetProperty("queued").GetInt32(), first.GetProperty("first").GetInt32()));
            var queued = Queued(MediaCaptionModule.ModuleId);
            Assert.Equal(theirs.OrderBy(x => x), queued.Select(q => q.Subject).OrderBy(x => x));
            Assert.All(queued, q => Assert.Equal(MediaCaptionModule.TrackedPriority, q.Priority));

            // Three wait, and three is what a sweep adds: the next one adds nothing.
            var (second, _) = await SweepAsync(MediaCaptionModule.ModuleId, MediaCaptionModule.SweepKind, Lane.Cpu);
            Assert.Equal("enough is already queued", second.GetProperty("reason").GetString());
            Assert.Equal(3, Queued(MediaCaptionModule.ModuleId).Count);
        }
    }

    /// <summary>These tests set the time a sweep gets (process-wide): they do not run beside each other.</summary>
    [CollectionDefinition(nameof(MediaSweepCollection), DisableParallelization = true)]
    public class MediaSweepCollection { }
}
