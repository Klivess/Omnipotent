using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Discord;
using Omnipotent.Services.OmniscienceV3.Ingest.Imports;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>The media store: reading images, downloading attachments, and the module that drives both. Generated images only.</summary>
    public class MediaTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_media_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private MediaStore store = null!;
        private string scratch = null!;

        private static readonly long T0 = Instant.FromUtc(new DateTime(2024, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);

        public async Task InitializeAsync()
        {
            scratch = Path.Combine(root, "scratch");
            Directory.CreateDirectory(scratch);
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            await stores.Intel.ApplyMigrationsAsync(new MediaModule().IntelMigrations);
            store = new MediaStore(stores.Paths);
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        // ── generated images ─────────────────────────────────────────────────────

        /// <summary>Left half red, right half blue: which way is up is visible in the pixels.</summary>
        private static SKBitmap TwoTone(int w, int h)
        {
            var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(220, 20, 20));
            using var paint = new SKPaint { Color = new SKColor(20, 20, 220) };
            canvas.DrawRect(new SKRect(w / 2f, 0, w, h), paint);
            return bitmap;
        }

        /// <summary>
        /// A picture with structure everywhere: a coarse mosaic of grey tiles. (Large flat areas would make its
        /// perceptual hash a coin toss under lossy compression, which real photographs are not.)
        /// </summary>
        private static SKBitmap Pattern(int w, int h, int seed)
        {
            var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            var random = new Random(seed);
            const int tilesX = 6, tilesY = 5;
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    byte shade = (byte)random.Next(0, 256);
                    using var paint = new SKPaint { Color = new SKColor(shade, shade, shade) };
                    canvas.DrawRect(new SKRect(tx * w / (float)tilesX, ty * h / (float)tilesY, (tx + 1) * w / (float)tilesX, (ty + 1) * h / (float)tilesY), paint);
                }
            }
            return bitmap;
        }

        private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 92)
        {
            using var data = bitmap.Encode(format, quality);
            return data.ToArray();
        }

        /// <summary>
        /// Inserts an EXIF block after the JPEG's start marker: orientation, when it was taken, optionally the
        /// camera's offset — and a GPS directory pointer aimed outside the data, which a reader that followed it
        /// would trip over.
        /// </summary>
        private static byte[] WithExif(byte[] jpeg, ushort orientation, string taken, string? offset)
        {
            using var tiff = new MemoryStream();
            using var w = new BinaryWriter(tiff, Encoding.ASCII, leaveOpen: true);
            void Entry(ushort tag, ushort type, uint count, uint value) { w.Write(tag); w.Write(type); w.Write(count); w.Write(value); }
            w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write((uint)8);
            // IFD0 at 8: orientation, Exif directory, GPS directory.
            w.Write((ushort)3);
            Entry(0x0112, 3, 1, orientation);
            Entry(0x8769, 4, 1, 50);
            Entry(0x8825, 4, 1, 0x00FFFFFF);
            w.Write((uint)0);
            // Exif directory at 50.
            ushort exifCount = (ushort)(offset == null ? 1 : 2);
            uint dataAt = (uint)(50 + 2 + exifCount * 12 + 4);
            w.Write(exifCount);
            Entry(0x9003, 2, 20, dataAt);
            if (offset != null) Entry(0x9011, 2, 7, dataAt + 20);
            w.Write((uint)0);
            w.Write(Encoding.ASCII.GetBytes(taken.PadRight(19)[..19] + "\0"));
            if (offset != null) w.Write(Encoding.ASCII.GetBytes(offset + "\0"));
            w.Flush();

            byte[] body = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff.ToArray()).ToArray();
            int length = body.Length + 2;
            return jpeg.Take(2).Concat(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }).Concat(body).Concat(jpeg.Skip(2)).ToArray();
        }

        /// <summary>A two-frame, one-pixel animated GIF.</summary>
        private static readonly byte[] AnimatedGif =
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, 0x03, 0x01, 0x00, 0x00, 0x00,
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00,
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x4C, 0x01, 0x00,
            0x3B,
        };

        private string File_(string name, byte[] bytes)
        {
            string path = Path.Combine(scratch, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private T One<T>(SqliteStore db, string sql, params (string, object?)[] args)
        {
            using var c = db.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        // ── reading images ───────────────────────────────────────────────────────

        [Fact]
        public void Probe_ReadsSizeAndMakesABoundedThumbnail()
        {
            using var picture = Pattern(800, 400, seed: 1);
            var facts = ImageProbe.Probe(File_("wide.png", Encode(picture, SKEncodedImageFormat.Png)));

            Assert.Equal((800, 400, 1, 1), (facts.Width, facts.Height, facts.Frames, facts.Orientation));
            Assert.Null(facts.TakenLocal);
            Assert.Equal("image/webp", facts.ThumbnailMime);
            using var thumb = SKBitmap.Decode(facts.Thumbnail);
            Assert.Equal((320, 160), (thumb.Width, thumb.Height));

            // A picture already smaller than the thumbnail size is not enlarged.
            using var small = Pattern(40, 30, seed: 2);
            using var smallThumb = SKBitmap.Decode(ImageProbe.Probe(File_("small.png", Encode(small, SKEncodedImageFormat.Png))).Thumbnail);
            Assert.Equal((40, 30), (smallThumb.Width, smallThumb.Height));
        }

        [Fact]
        public void Probe_TurnsASidewaysPhotoUpright_AndReadsWhenItWasTaken_ButNeverWhere()
        {
            using var stored = TwoTone(40, 20); // as the phone stored it: red on the left
            string path = File_("sideways.jpg", WithExif(Encode(stored, SKEncodedImageFormat.Jpeg), orientation: 6, "2024:02:14 10:31:00", "+01:00"));

            var facts = ImageProbe.Probe(path);

            // Orientation 6 = "rotate a quarter turn clockwise to view": 40×20 becomes 20×40, red on top.
            Assert.Equal((20, 40, 6), (facts.Width, facts.Height, facts.Orientation));
            Assert.Equal("2024-02-14T10:31:00", facts.TakenLocal);
            Assert.Equal(60, facts.TakenOffsetMinutes);

            var frames = ImageProbe.LoadFrames(path);
            try
            {
                var frame = Assert.Single(frames);
                Assert.Equal((20, 40), (frame.Width, frame.Height));
                var top = frame.GetPixel(10, 5);
                var bottom = frame.GetPixel(10, 35);
                Assert.True(top.Red > 150 && top.Blue < 100, $"top should be red, was {top}");
                Assert.True(bottom.Blue > 150 && bottom.Red < 100, $"bottom should be blue, was {bottom}");
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }
            // The thumbnail is upright too.
            using var thumb = SKBitmap.Decode(facts.Thumbnail);
            Assert.True(thumb.Height > thumb.Width);

            // No offset recorded by the camera: the wall clock comes back alone. A camera with no clock set says nothing.
            Assert.Equal(("2019-12-31T23:59:59", (int?)null), ExifReader.TakenAt(WithExif(Encode(stored, SKEncodedImageFormat.Jpeg), 1, "2019:12:31 23:59:59", null)));
            Assert.Equal(((string?)null, (int?)null), ExifReader.TakenAt(WithExif(Encode(stored, SKEncodedImageFormat.Jpeg), 1, "0000:00:00 00:00:00", null)));
            Assert.Equal(((string?)null, (int?)null), ExifReader.TakenAt(Encode(stored, SKEncodedImageFormat.Jpeg)));
            Assert.Equal(((string?)null, (int?)null), ExifReader.TakenAt(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 1, 2 })); // a lying segment length
        }

        [Fact]
        public void PerceptualHash_IsCloseForCopiesOfAPicture_AndFarForDifferentPictures()
        {
            using var original = Pattern(640, 480, seed: 7);
            using var other = Pattern(640, 480, seed: 8);
            using var smaller = original.Resize(new SKImageInfo(200, 150), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            long a = ImageProbe.Probe(File_("a.png", Encode(original, SKEncodedImageFormat.Png))).PerceptualHash;
            long recompressed = ImageProbe.Probe(File_("a.jpg", Encode(original, SKEncodedImageFormat.Jpeg, 60))).PerceptualHash;
            long resized = ImageProbe.Probe(File_("a-small.webp", Encode(smaller, SKEncodedImageFormat.Webp, 70))).PerceptualHash;
            long different = ImageProbe.Probe(File_("b.png", Encode(other, SKEncodedImageFormat.Png))).PerceptualHash;

            Assert.InRange(ImageProbe.HashDistance(a, recompressed), 0, 6);
            Assert.InRange(ImageProbe.HashDistance(a, resized), 0, 6);
            Assert.True(ImageProbe.HashDistance(a, different) > 12, $"distance was {ImageProbe.HashDistance(a, different)}");
        }

        [Fact]
        public void Animations_ReportTheirFrames_AndDamagedOrUnknownFilesAreToldApart()
        {
            string gif = File_("two.gif", AnimatedGif);
            Assert.Equal(2, ImageProbe.Probe(gif).Frames);
            var frames = ImageProbe.LoadFrames(gif);
            Assert.Equal(2, frames.Count);
            foreach (var f in frames) f.Dispose();

            // Not an image at all, and a PNG cut off after its signature: damaged.
            var garbage = Assert.Throws<ImageUnreadableException>(() => ImageProbe.Probe(File_("garbage.png", Encoding.ASCII.GetBytes("this is not a picture"))));
            Assert.False(garbage.Unsupported);
            var truncated = Assert.Throws<ImageUnreadableException>(() => ImageProbe.Probe(File_("cut.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })));
            Assert.False(truncated.Unsupported);

            // A real format this build cannot decode is not "damaged": it is kept and said to be unsupported.
            byte[] heic = new byte[] { 0, 0, 0, 0x18 }.Concat(Encoding.ASCII.GetBytes("ftypheic")).Concat(new byte[32]).ToArray();
            var unsupported = Assert.Throws<ImageUnreadableException>(() => ImageProbe.Probe(File_("photo.heic", heic)));
            Assert.True(unsupported.Unsupported);
        }

        // ── the processor ────────────────────────────────────────────────────────

        private async Task<string> AttachAsync(string messageId, byte[] bytes, string filename, string sourceKind = SourceKinds.Live)
        {
            var stored = await store.PutAsync(new MemoryStream(bytes), null, 1 << 24);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, sourceKind, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", "room", ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", "u-ada"),
                    SentAtMs = T0, OrderKey = T0, Attachments = new[] { new IngestAttachment(filename) { Stored = stored } },
                });
                return w.Complete();
            });
            return stored.Sha256;
        }

        private MediaProcessor Processor(HttpMessageHandler? handler = null, MediaSettings? settings = null) =>
            new(stores, clock, store, () => settings ?? new MediaSettings(), () => Zones.GetOrThrow("Europe/London"), handler);

        [Fact]
        public async Task Probe_RecordsTheObjectOnce_FillsTheAttachment_AndStoresAThumbnail()
        {
            using var stored = TwoTone(40, 20);
            string sha = await AttachAsync("m1", WithExif(Encode(stored, SKEncodedImageFormat.Jpeg), 6, "2024:07:01 14:00:00", "+02:00"), "holiday.jpg");
            var processor = Processor();

            Assert.Equal(MediaObjectStates.Ok, await processor.ProbeAsync(sha, 7, default));

            Assert.Equal(20L, One<long>(stores.Corpus, "SELECT width FROM attachments WHERE attachment_id='testnet:m1#0'"));
            Assert.Equal(40L, One<long>(stores.Corpus, "SELECT height FROM attachments WHERE attachment_id='testnet:m1#0'"));
            // 14:00 at +02:00 is 12:00 UTC, known to the second because the camera recorded its offset.
            Assert.Equal(Instant.FromUtc(new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc)), One<long>(stores.Corpus, "SELECT taken_at_ms FROM attachments WHERE attachment_id='testnet:m1#0'"));
            Assert.Equal((long)TimeQuality.Second, One<long>(stores.Corpus, "SELECT taken_quality FROM attachments WHERE attachment_id='testnet:m1#0'"));
            Assert.Equal("2024-07-01T14:00:00", One<string>(stores.Intel, "SELECT taken_local FROM media_objects WHERE sha256=$s", ("$s", sha)));
            Assert.Equal(7L, One<long>(stores.Intel, "SELECT source_seq FROM media_objects WHERE sha256=$s", ("$s", sha)));
            string thumb = One<string>(stores.Intel, "SELECT thumb_sha256 FROM media_objects WHERE sha256=$s", ("$s", sha));
            Assert.True(store.Exists(thumb));
            Assert.Equal("image/webp", One<string>(stores.Corpus, "SELECT mime FROM cas_objects WHERE sha256=$s", ("$s", thumb)));

            // Probing again is a no-op (one row, same time).
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(MediaObjectStates.Ok, await processor.ProbeAsync(sha, 9, default));
            Assert.Equal(7L, One<long>(stores.Intel, "SELECT source_seq FROM media_objects WHERE sha256=$s", ("$s", sha)));

            // No camera offset: read on Klives's clock (BST in July) and marked as inferred.
            string noOffset = await AttachAsync("m2", WithExif(Encode(stored, SKEncodedImageFormat.Jpeg, 80), 1, "2024:07:01 14:00:00", null), "other.jpg");
            await processor.ProbeAsync(noOffset, 9, default);
            Assert.Equal(Instant.FromUtc(new DateTime(2024, 7, 1, 13, 0, 0, DateTimeKind.Utc)), One<long>(stores.Corpus, "SELECT taken_at_ms FROM attachments WHERE attachment_id='testnet:m2#0'"));
            Assert.Equal((long)TimeQuality.InferredZone, One<long>(stores.Corpus, "SELECT taken_quality FROM attachments WHERE attachment_id='testnet:m2#0'"));
        }

        [Fact]
        public async Task Probe_QuarantinesDamagedImages_AndLeavesOtherFilesAlone()
        {
            // PNG signature, then nothing: sniffed as an image, impossible to decode.
            string damaged = await AttachAsync("bad", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 }, "broken.png");
            string pdf = await AttachAsync("doc", Encoding.ASCII.GetBytes("%PDF-1.7 synthetic document"), "notes.pdf");
            var processor = Processor();

            Assert.Equal(MediaObjectStates.Corrupt, await processor.ProbeAsync(damaged, 1, default));
            Assert.Equal(DownloadStates.Quarantined, One<string>(stores.Corpus, "SELECT download_state FROM attachments WHERE attachment_id='testnet:bad#0'"));
            Assert.NotNull(One<string>(stores.Corpus, "SELECT download_error FROM attachments WHERE attachment_id='testnet:bad#0'"));
            Assert.True(store.Exists(damaged)); // kept for inspection, never deleted

            Assert.Equal(MediaObjectStates.Ok, await processor.ProbeAsync(pdf, 1, default));
            Assert.Equal("file", One<string>(stores.Intel, "SELECT media_kind FROM media_objects WHERE sha256=$s", ("$s", pdf)));
            Assert.Equal(DownloadStates.Done, One<string>(stores.Corpus, "SELECT download_state FROM attachments WHERE attachment_id='testnet:doc#0'"));
            Assert.Equal("missing", await processor.ProbeAsync(new string('0', 64), 1, default));
        }

        private sealed class FakeAv : IAvProbe
        {
            public AvFacts? Facts { get; set; } = new(61_500, 1280, 720, HasAudio: true, HasVideo: true);
            public bool Unreadable { get; set; }
            public int Calls { get; private set; }

            public Task<AvFacts?> ProbeAsync(string path, CancellationToken ct)
            {
                Calls++;
                if (Unreadable) throw new ImageUnreadableException("ffprobe could not read the file: synthetic", unsupported: true);
                return Task.FromResult(Facts);
            }
        }

        [Fact]
        public async Task Probe_ReadsTheLengthAndSizeOfVideo_OncePerContent_AndLeavesWhatItCannotRead()
        {
            var av = new FakeAv();
            var processor = new MediaProcessor(stores, clock, store, () => new MediaSettings(), () => Zones.GetOrThrow("Europe/London"), av: av);

            string clip = await AttachAsync("v1", Encoding.ASCII.GetBytes("synthetic video bytes"), "clip.mp4");
            Assert.Equal(MediaObjectStates.Ok, await processor.ProbeAsync(clip, 3, default));
            Assert.Equal(61_500L, One<long>(stores.Corpus, "SELECT duration_ms FROM attachments WHERE attachment_id='testnet:v1#0'"));
            Assert.Equal(1280L, One<long>(stores.Corpus, "SELECT width FROM attachments WHERE attachment_id='testnet:v1#0'"));
            Assert.Equal(720L, One<long>(stores.Intel, "SELECT height FROM media_objects WHERE sha256=$s", ("$s", clip)));
            Assert.Equal(MediaObjectStates.Ok, await processor.ProbeAsync(clip, 4, default));
            Assert.Equal(1, av.Calls); // once per distinct content

            // ffprobe cannot make sense of it: unsupported, not quarantined (transcription may still read its sound).
            av.Unreadable = true;
            string odd = await AttachAsync("v2", Encoding.ASCII.GetBytes("other synthetic video"), "odd.mov");
            Assert.Equal(MediaObjectStates.Unsupported, await processor.ProbeAsync(odd, 5, default));
            Assert.Equal(DownloadStates.Done, One<string>(stores.Corpus, "SELECT download_state FROM attachments WHERE attachment_id='testnet:v2#0'"));

            // No ffprobe installed: nothing is recorded, so a later probe can still read it.
            av.Unreadable = false;
            av.Facts = null;
            string later = await AttachAsync("v3", Encoding.ASCII.GetBytes("third synthetic video"), "later.mp4");
            Assert.Equal("not read", await processor.ProbeAsync(later, 6, default));
            Assert.Equal(0L, One<long>(stores.Intel, "SELECT COUNT(*) FROM media_objects WHERE sha256=$s", ("$s", later)));
        }

        // ── downloading ──────────────────────────────────────────────────────────

        private sealed class FakeNet : HttpMessageHandler
        {
            public List<string> Requests { get; } = new();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request.RequestUri!.ToString());
                return Task.FromResult(Respond(request));
            }
        }

        private async Task PendingAsync(string messageId, string url, string sourceKind = SourceKinds.Backfill, string filename = "cat.png")
        {
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, sourceKind, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", "room", ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", "u-ada"),
                    SentAtMs = T0, OrderKey = T0, Attachments = new[] { new IngestAttachment(filename, SourceUrl: url) },
                });
                return w.Complete();
            });
        }

        private (string State, string? Error, long Attempts) Attachment(string messageId)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, "SELECT download_state, download_error, download_attempts FROM attachments WHERE attachment_id=$a",
                r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64(2)), ("$a", $"testnet:{messageId}#0")).Single();
        }

        private static HttpResponseMessage Bytes(byte[] body, string type = "image/png") =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type) } } };

        [Fact]
        public async Task Download_StoresTheFile_AndTagsItWithHowTheMessageArrived()
        {
            using var picture = Pattern(64, 64, seed: 3);
            byte[] png = Encode(picture, SKEncodedImageFormat.Png);
            var net = new FakeNet { Respond = _ => Bytes(png, "application/octet-stream") };
            await PendingAsync("m1", "https://cdn.discordapp.com/attachments/1/2/cat.png?ex=abc");

            var outcome = await Processor(net).DownloadAsync("testnet:m1#0", default);

            Assert.Equal(DownloadStates.Done, outcome.State);
            Assert.Equal((DownloadStates.Done, (string?)null, 1L), Attachment("m1"));
            string sha = One<string>(stores.Corpus, "SELECT cas_sha256 FROM attachments WHERE attachment_id='testnet:m1#0'");
            Assert.True(store.Exists(sha));
            Assert.Equal("image/png", One<string>(stores.Corpus, "SELECT mime FROM attachments WHERE attachment_id='testnet:m1#0'")); // from the bytes
            // The message came in by backfill, so its media is backfill too: it can never look live.
            Assert.Equal("backfill", One<string>(stores.Corpus, "SELECT source_kind FROM change_log WHERE kind='attachment.stored'"));
            // Asking again does nothing.
            Assert.Equal("nothing to do", (await Processor(net).DownloadAsync("testnet:m1#0", default)).Detail);
            Assert.Single(net.Requests);
        }

        [Fact]
        public async Task Download_NeverRequestsWhatItShouldNot()
        {
            var net = new FakeNet { Respond = _ => Bytes(new byte[] { 1 }) };
            await PendingAsync("other-host", "https://files.example.invalid/cat.png");
            await PendingAsync("plain-http", "http://cdn.discordapp.com/attachments/1/2/cat.png");
            await PendingAsync("lookalike", "https://cdn.discordapp.com.example.invalid/cat.png");
            var processor = Processor(net);

            foreach (var id in new[] { "other-host", "plain-http", "lookalike" })
            {
                var outcome = await processor.DownloadAsync($"testnet:{id}#0", default);
                Assert.Equal(DownloadStates.Unavailable, outcome.State);
                Assert.Equal(DownloadStates.Unavailable, Attachment(id).State);
            }
            Assert.Empty(net.Requests);
            Assert.Contains("not a host", Attachment("other-host").Error);
            Assert.Contains("https", Attachment("plain-http").Error);

            var hosts = new[] { "cdn.discordapp.com" };
            Assert.Null(MediaProcessor.RefusalFor("https://cdn.discordapp.com/a.png", hosts));
            Assert.NotNull(MediaProcessor.RefusalFor("https://user:pw@cdn.discordapp.com/a.png", hosts));
            Assert.NotNull(MediaProcessor.RefusalFor("https://cdn.discordapp.com:8443/a.png", hosts));
            Assert.NotNull(MediaProcessor.RefusalFor("file:///etc/passwd", hosts));
            Assert.NotNull(MediaProcessor.RefusalFor("https://127.0.0.1/a.png", hosts));
            Assert.NotNull(MediaProcessor.RefusalFor(null, hosts));
        }

        private sealed class FakeLinks : IMediaLinkRefresher
        {
            public List<MediaLinkRequest> Asked { get; } = new();
            public Func<MediaLinkRequest, string?> Answer { get; set; } = _ => null;
            public Exception? Fails { get; set; }

            public Task<string?> RefreshAsync(MediaLinkRequest request, CancellationToken ct)
            {
                Asked.Add(request);
                if (Fails != null) throw Fails;
                return Task.FromResult(Answer(request));
            }
        }

        [Fact]
        public async Task Download_AnExpiredLink_IsRefreshedOnceThroughItsSource_AndTheFreshLinkIsKept()
        {
            using var picture = Pattern(32, 32, seed: 5);
            byte[] png = Encode(picture, SKEncodedImageFormat.Png);
            const string expired = "https://cdn.discordapp.com/attachments/111/222/cat.png?ex=old";
            const string fresh = "https://cdn.discordapp.com/attachments/111/222/cat.png?ex=new";
            var net = new FakeNet { Respond = r => r.RequestUri!.ToString() == fresh ? Bytes(png) : new HttpResponseMessage(HttpStatusCode.NotFound) };
            var links = new FakeLinks { Answer = _ => fresh };
            var processor = new MediaProcessor(stores, clock, store, () => new MediaSettings(), () => Zones.GetOrThrow("Europe/London"), net, () => links);

            // Expired, refreshed, fetched; the fresh link is kept on the attachment.
            await PendingAsync("m1", expired);
            Assert.Equal(DownloadStates.Done, (await processor.DownloadAsync("testnet:m1#0", default)).State);
            Assert.Equal(new[] { expired, fresh }, net.Requests);
            var asked = Assert.Single(links.Asked);
            Assert.Equal(("testnet", "m1", "room", 0, expired), (asked.Platform, asked.PlatformMessageId, asked.PlatformConversationId, asked.Index, asked.ExpiredUrl));
            Assert.Equal(fresh, One<string>(stores.Corpus, "SELECT source_url FROM attachments WHERE attachment_id='testnet:m1#0'"));

            // The platform has no fresh link (the message is gone): unavailable.
            await PendingAsync("gone", "https://cdn.discordapp.com/attachments/111/333/gone.png?ex=old");
            links.Answer = _ => null;
            Assert.Equal(DownloadStates.Unavailable, (await processor.DownloadAsync("testnet:gone#0", default)).State);
            Assert.Contains("expired", Attachment("gone").Error);

            // A "fresh" link to another host is never requested: the allow-list holds for refreshed links too.
            await PendingAsync("elsewhere", "https://cdn.discordapp.com/attachments/111/444/x.png?ex=old");
            links.Answer = _ => "https://files.example.invalid/x.png";
            Assert.Equal(DownloadStates.Unavailable, (await processor.DownloadAsync("testnet:elsewhere#0", default)).State);
            Assert.DoesNotContain(net.Requests, r => r.Contains("example.invalid"));

            // The fresh link refused as well: unavailable, and the platform is asked only once per download.
            await PendingAsync("twice", "https://cdn.discordapp.com/attachments/111/555/y.png?ex=old");
            links.Answer = _ => "https://cdn.discordapp.com/attachments/111/555/y.png?ex=also-old";
            int before = links.Asked.Count;
            Assert.Equal(DownloadStates.Unavailable, (await processor.DownloadAsync("testnet:twice#0", default)).State);
            Assert.Equal(before + 1, links.Asked.Count);
            Assert.Contains("fresh one was refused too", Attachment("twice").Error);

            // Asking failed (the platform was busy): transient, so the job system tries again later.
            await PendingAsync("busy", "https://cdn.discordapp.com/attachments/111/666/z.png?ex=old");
            links.Fails = new InvalidOperationException("rate limited");
            var transient = await Assert.ThrowsAsync<JobFailureException>(() => processor.DownloadAsync("testnet:busy#0", default));
            Assert.Equal(ErrorClass.Transient, transient.Class);
            Assert.Equal(DownloadStates.Pending, Attachment("busy").State);
        }

        [Fact]
        public async Task DiscordRefresher_FindsTheSameFileInTheMessageReadAgain()
        {
            var discord = new FakeDiscord();
            var refresher = new DiscordLinkRefresher(discord);
            string id = DiscordFixtures.Snow(T0);
            discord.Put(DiscordFixtures.General, DiscordFixtures.Message(id, DiscordFixtures.User(DiscordFixtures.Sam, "sam"), "two files", more: m => m["attachments"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["id"] = "600000000000000001", ["filename"] = "a.png", ["url"] = "https://cdn.discordapp.com/attachments/7/1/a.png?ex=new" },
                new System.Text.Json.Nodes.JsonObject { ["id"] = "600000000000000002", ["filename"] = "b.png", ["url"] = "https://cdn.discordapp.com/attachments/7/2/b.png?ex=new" },
            }));
            MediaLinkRequest Ask(string? fileId, int index, string message = "", string channel = DiscordFixtures.General, string platform = "discord") =>
                new("discord:x#0", platform, message.Length > 0 ? message : id, channel, fileId, index, "https://cdn.discordapp.com/old");

            // By the file's id when it was recorded, by its position otherwise.
            Assert.Equal("https://cdn.discordapp.com/attachments/7/2/b.png?ex=new", await refresher.RefreshAsync(Ask("600000000000000002", 0), default));
            Assert.Equal("https://cdn.discordapp.com/attachments/7/1/a.png?ex=new", await refresher.RefreshAsync(Ask(null, 0), default));
            Assert.Null(await refresher.RefreshAsync(Ask("600000000000000009", 0), default));        // that file was removed from the message
            Assert.Null(await refresher.RefreshAsync(Ask(null, 0, message: DiscordFixtures.Snow(T0 + 1)), default)); // the message is gone
            Assert.Null(await refresher.RefreshAsync(Ask(null, 0, platform: "whatsapp"), default));    // not Discord's to refresh
            Assert.Null(await refresher.RefreshAsync(Ask(null, 0, message: "not-a-snowflake"), default)); // nothing Discord could look up
            Assert.DoesNotContain(discord.Calls, c => c.Contains("not-a-snowflake"));

            // A channel the bot can no longer read has no fresh link; a refused token is not swallowed.
            discord.Forbidden[DiscordFixtures.General] = 1;
            Assert.Null(await refresher.RefreshAsync(Ask(null, 0), default));
            discord.Forbidden.Clear();
            discord.Unauthorised = true;
            await Assert.ThrowsAsync<DiscordApiException>(() => refresher.RefreshAsync(Ask(null, 0), default));
        }

        [Fact]
        public async Task Download_ExpiredLinksStop_ServerErrorsRetryThenGiveUp_AndSizeIsCapped()
        {
            var net = new FakeNet();
            var settings = new MediaSettings { MaxAttempts = 2, Limits = new MediaLimits(ImageBytes: 100) };
            var processor = Processor(net, settings);

            // Expired: recorded once, never retried.
            await PendingAsync("expired", "https://cdn.discordapp.com/a/expired.png");
            net.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
            Assert.Equal(DownloadStates.Unavailable, (await processor.DownloadAsync("testnet:expired#0", default)).State);
            Assert.Contains("expired", Attachment("expired").Error);

            // A redirect is not followed: its target was never checked.
            await PendingAsync("moved", "https://cdn.discordapp.com/a/moved.png");
            net.Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example.invalid/x.png") } };
            Assert.Equal(DownloadStates.Unavailable, (await processor.DownloadAsync("testnet:moved#0", default)).State);
            Assert.DoesNotContain(net.Requests, r => r.Contains("elsewhere"));

            // Server trouble: transient (the job system retries), then failed once the attempts are used up.
            await PendingAsync("flaky", "https://cdn.discordapp.com/a/flaky.png");
            net.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
            var transient = await Assert.ThrowsAsync<JobFailureException>(() => processor.DownloadAsync("testnet:flaky#0", default));
            Assert.Equal(ErrorClass.Transient, transient.Class);
            Assert.Equal((DownloadStates.Pending, 1L), (Attachment("flaky").State, Attachment("flaky").Attempts));
            Assert.Equal(DownloadStates.Failed, (await processor.DownloadAsync("testnet:flaky#0", default)).State);
            Assert.Equal(2L, Attachment("flaky").Attempts);

            // Too large by its own declaration, and too large although it declared nothing.
            await PendingAsync("declared", "https://cdn.discordapp.com/a/declared.png");
            net.Respond = _ => Bytes(new byte[500]);
            Assert.Equal(DownloadStates.TooLarge, (await processor.DownloadAsync("testnet:declared#0", default)).State);
            await PendingAsync("undeclared", "https://cdn.discordapp.com/a/undeclared.png");
            net.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NoLength(new byte[500])) };
            Assert.Equal(DownloadStates.TooLarge, (await processor.DownloadAsync("testnet:undeclared#0", default)).State);
            Assert.Equal(0L, One<long>(stores.Corpus, "SELECT COUNT(*) FROM cas_objects"));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(stores.Paths.Media, "tmp")));
        }

        /// <summary>A body whose length is not known up front.</summary>
        private sealed class NoLength : MemoryStream
        {
            public NoLength(byte[] bytes) : base(bytes) { }
            public override bool CanSeek => false;
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
        public async Task Module_ProbesWhatArrives_AndTheSweepFindsWhatTheChangeLogNeverAnnounced()
        {
            await stores.DisposeAsync(); // the runtime owns the stores in this test
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var module = new MediaModule(new FakeNet());
            await using var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new NullLlm(), Log = _ => { },
                LogError = (m, e) => errors.Enqueue(m + " " + e?.Message),
                Kernel = new KernelOptions { StartWorkers = true, StartLoop = false },
                Modules = new IIntelModule[] { module }, LockWait = TimeSpan.FromSeconds(5),
            });
            stores = rt.Stores;
            store = module.Media;

            // Mapping and planning are pure: a new attachment wants a download, a stored one a probe.
            var dirty = module.MapChanges(new[]
            {
                new ChangeRecord(1, 0, ChangeKinds.AttachmentAdd, "testnet:x#0", null, null, SourceKinds.Live),
                new ChangeRecord(2, 0, ChangeKinds.AttachmentStored, "testnet:x#0", null, null, SourceKinds.Live),
                new ChangeRecord(3, 0, ChangeKinds.MessageInsert, "testnet:x", null, null, SourceKinds.Live),
            }).ToList();
            Assert.Equal(new[] { new DirtySubject("download", "testnet:x#0"), new DirtySubject("probe", "testnet:x#0") }, dirty);
            var planned = module.Plan(new PlanContext { Dirty = dirty, DueSchedules = Array.Empty<(string, TimeSpan)>(), NowMs = clock.NowMs, ChangeWatermark = 3 }).ToList();
            // Downloading is off by default (v2 still fetches attachments), so only the probe is planned.
            Assert.Equal(new[] { "probe" }, planned.Select(j => j.Kind));

            // End to end: an attachment stored through the corpus writer is probed by the kernel.
            using var picture = Pattern(128, 96, seed: 4);
            string sha = await AttachAsync("live1", Encode(picture, SKEncodedImageFormat.Png), "live.png");
            await rt.Host.PumpChangesOnceAsync();
            await rt.Host.PlanOnceAsync();
            await WaitForAsync(() => One<long>(stores.Intel, "SELECT COUNT(*) FROM media_objects WHERE sha256=$s", ("$s", sha)) == 1);
            Assert.Equal(128L, One<long>(stores.Corpus, "SELECT width FROM attachments WHERE attachment_id='testnet:live1#0'"));

            // A file that was stored before the module existed (v2's media): no change row will ever announce it.
            using var old = Pattern(50, 50, seed: 5);
            var legacy = await store.PutAsync(new MemoryStream(Encode(old, SKEncodedImageFormat.Png)), null, 1 << 20);
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                MediaStore.Register(c, tx, legacy, clock.NowMs);
                c.Exec(tx, "UPDATE attachments SET cas_sha256=$s WHERE attachment_id='testnet:live1#0'", ("$s", legacy.Sha256));
            });
            await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = MediaModule.ModuleId, Kind = MediaModule.SweepKind, Lane = Lane.Io, DedupeKey = "test-sweep" });
            await WaitForAsync(() => One<long>(stores.Intel, "SELECT COUNT(*) FROM media_objects WHERE sha256=$s", ("$s", legacy.Sha256)) == 1);
            // Thumbnails live in the same store but are never themselves treated as attachments to probe.
            Assert.Equal(2L, One<long>(stores.Intel, "SELECT COUNT(*) FROM media_objects"));
            Assert.Empty(errors);

            stores = await ReopenAfter(rt);
        }

        /// <summary>Hands the stores back to the fixture so its own disposal has something to close.</summary>
        private async Task<OmniscienceStores> ReopenAfter(V3Runtime rt)
        {
            await rt.DisposeAsync();
            return await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
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
