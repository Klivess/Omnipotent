using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Ml.Vision;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>Visual search (N2): what is embedded, how a search ranks and filters, and whose images never appear. Synthetic images only.</summary>
    public class MediaVisualTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_visual_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
        private V3Runtime rt = null!;
        private MediaVisualModule module = null!;
        private readonly ColourEmbedder embedder = new();

        /// <summary>A stand-in for CLIP: an image is its average colour; a text is the colour words it names.</summary>
        private sealed class ColourEmbedder : IImageTextEmbedder
        {
            public int Images;
            public string ModelVersion => "test-colours.1";

            public Task<float[][]> EmbedImagesAsync(IReadOnlyList<SKBitmap> images, CancellationToken ct)
            {
                Images += images.Count;
                return Task.FromResult(images.Select(b =>
                {
                    var c = b.GetPixel(b.Width / 2, b.Height / 2);
                    return VectorMath.Normalized(new float[] { c.Red, c.Green, c.Blue, 1 });
                }).ToArray());
            }

            public Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
                Task.FromResult(texts.Select(t => VectorMath.Normalized(new float[]
                {
                    t.Contains("red") ? 255 : 0, t.Contains("green") ? 255 : 0, t.Contains("blue") ? 255 : 0, 1,
                })).ToArray());
        }

        public async Task InitializeAsync()
        {
            module = new MediaVisualModule(embedder);
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new MediaCaptionTestsNullLlm(), Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new MediaModule(), new MediaCaptionModule(), module }, LockWait = TimeSpan.FromSeconds(5),
            });
        }

        public async Task DisposeAsync()
        {
            await rt.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private static byte[] Picture(SKColor colour)
        {
            using var bitmap = new SKBitmap(96, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(colour);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private async Task<string> AttachAsync(string messageId, byte[] bytes, string author, string room = "room", long? at = null)
        {
            var stored = await new MediaStore(rt.Stores.Paths).PutAsync(new MemoryStream(bytes), null, 1 << 24);
            long sent = at ?? clock.NowMs - 60_000;
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = new IngestConversation("testnet", room, ConversationKinds.GroupDm), Author = new IngestIdentity("testnet", author),
                    SentAtMs = sent, OrderKey = sent, Attachments = new[] { new IngestAttachment("picture.png") { Stored = stored } },
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

        private Task CaptionAsync(string attachmentId, string caption, params string[] tags) =>
            rt.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO labels(item_kind, item_id, item_version, task, label, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                VALUES('attachment',$i,1,'caption','photo',0,$s,'test','caption-1','caption-1',$n,1)
                """, ("$i", attachmentId), ("$s", System.Text.Json.JsonSerializer.Serialize(new { caption, tags, origin = "ai_caption" })), ("$n", clock.NowMs)));

        [Fact]
        public async Task ImagesAreFoundByHowTheyLook_AndByWhatTheirCaptionsSay_WithinTheirFilters()
        {
            string red = await AttachAsync("m1", Picture(SKColors.Red), "u-ada");
            string blue = await AttachAsync("m2", Picture(SKColors.Blue), "u-bo", room: "other");
            string green = await AttachAsync("m3", Picture(SKColors.Green), "u-ada", at: clock.NowMs - 10 * 86_400_000L);
            foreach (var id in new[] { red, blue, green }) Assert.Equal("embedded", await module.EmbedAsync(id, CancellationToken.None));
            Assert.Equal(3, module.Embedded);
            await CaptionAsync(green, "A screenshot of a trade in a market app.", "screenshot", "trade");

            // By look: the red picture comes first for "red".
            var byLook = await module.SearchAsync(new MediaSearchQuery("something red"), CancellationToken.None);
            Assert.True(byLook.VisualAvailable);
            Assert.Equal(red, byLook.Hits[0].AttachmentId);
            Assert.Contains("looks like it", byLook.Hits[0].Why);

            // By words: the caption finds the green screenshot, and says why.
            var byWords = await module.SearchAsync(new MediaSearchQuery("the screenshot of the trade"), CancellationToken.None);
            var hit = byWords.Hits.First(h => h.AttachmentId == green);
            Assert.Contains("the caption says so", hit.Why);
            Assert.Equal("A screenshot of a trade in a market app.", hit.Caption);
            Assert.Equal(new[] { "screenshot", "trade" }, hit.Tags);
            Assert.Equal(byWords.Hits[0].AttachmentId, green); // both rankings put it first: it fuses highest

            // Filters: by who posted it, the conversation, and when.
            var ada = await module.SearchAsync(new MediaSearchQuery("blue", PersonId: PersonOf("u-ada")), CancellationToken.None);
            Assert.DoesNotContain(ada.Hits, h => h.AttachmentId == blue);
            var other = await module.SearchAsync(new MediaSearchQuery("red", ConversationId: CorpusIds.Conversation("testnet", "other")), CancellationToken.None);
            Assert.Equal(new[] { blue }, other.Hits.Select(h => h.AttachmentId));
            var recent = await module.SearchAsync(new MediaSearchQuery("green", FromMs: clock.NowMs - 86_400_000L), CancellationToken.None);
            Assert.DoesNotContain(recent.Hits, h => h.AttachmentId == green);

            await Assert.ThrowsAsync<Omnipotent.Services.OmniscienceV3.Api.ApiException>(() => module.SearchAsync(new MediaSearchQuery("  "), CancellationToken.None));
        }

        [Fact]
        public async Task AHitSaysWhereItWasPosted_WhatWasSaidWithIt_AboutTheFile_AndLinksToTheMessage()
        {
            var store = new MediaStore(rt.Stores.Paths);
            var redFile = await store.PutAsync(new MemoryStream(Picture(SKColors.Red)), null, 1 << 24);
            var blueFile = await store.PutAsync(new MemoryStream(Picture(SKColors.Blue)), null, 1 << 24);
            long sent = clock.NowMs - 60_000;
            var ada = new IngestIdentity("discord", "700000000000000001", DisplayName: "Synthetic Ada");
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                // A server channel: two pictures in one message, with words and reactions.
                w.Message(new IngestMessage
                {
                    PlatformMessageId = "1024709476269436948",
                    Conversation = new IngestConversation("discord", "900000000000000001", ConversationKinds.Text, "general", GuildId: "800000000000000001", GuildName: "Synthetic server"),
                    Author = ada, SentAtMs = sent, OrderKey = sent, EditedAtMs = sent + 5_000, Content = "look at this " + new string('x', 700),
                    Attachments = new[]
                    {
                        new IngestAttachment("blue.png", "image/png", SizeBytes: 2048) { Stored = blueFile },
                        new IngestAttachment("red.png", "image/png", SizeBytes: 1024, Width: 96, Height: 64, AltText: "a red square", Spoiler: true) { Stored = redFile },
                    },
                    Reactions = new[] { new IngestReaction("thumbsup", Count: 3) },
                });
                // The same red file again, in a direct chat and with nothing said.
                w.Message(new IngestMessage
                {
                    PlatformMessageId = "1024709476269436949", Conversation = new IngestConversation("discord", "900000000000000002", ConversationKinds.Dm),
                    Author = ada, SentAtMs = sent + 1, OrderKey = sent + 1, Attachments = new[] { new IngestAttachment("red-again.png") { Stored = redFile } },
                });
                return w.Complete();
            });
            const string inServer = "discord:1024709476269436948#1", inDm = "discord:1024709476269436949#0";
            foreach (var id in new[] { "discord:1024709476269436948#0", inServer, inDm }) Assert.Equal("embedded", await module.EmbedAsync(id, CancellationToken.None));
            await rt.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO labels(item_kind, item_id, item_version, task, label, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                VALUES('attachment',$i,1,'caption','drawing',0,$s,'vision-test','caption-1','caption-1',$n,1)
                """, ("$i", inServer), ("$s", """{"caption":"A plain red square.","kind":"drawing","tags":["red"],"hasText":false,"nsfw":true,"origin":"ai_caption"}"""), ("$n", clock.NowMs - 1_000)));

            var found = await module.SearchAsync(new MediaSearchQuery("red"), CancellationToken.None);
            var hit = found.Hits.Single(h => h.AttachmentId == inServer);
            Assert.Equal("discord", hit.Platform);
            Assert.Equal((ConversationKinds.Text, "general", "discord:800000000000000001", "Synthetic server"), (hit.ConversationKind, hit.ConversationTitle, hit.GuildId, hit.GuildName));
            Assert.Equal("https://discord.com/channels/800000000000000001/900000000000000001/1024709476269436948", hit.MessageUrl);
            Assert.StartsWith("look at this xxx", hit.MessageText);
            Assert.Equal(600, hit.MessageText!.Length);
            Assert.True(hit.MessageTextCut);
            Assert.True(hit.Edited);
            Assert.Equal(3, hit.Reactions);
            Assert.Equal((2, 2), (hit.Position, hit.InMessage));
            // The size is the stored file's own, not the one the platform claimed.
            Assert.Equal(("red.png", "image/png", (long)Picture(SKColors.Red).Length, 96, 64), (hit.Filename, hit.Mime, hit.SizeBytes, hit.Width, hit.Height));
            Assert.True(hit.Spoiler);
            Assert.Equal("a red square", hit.AltText);
            Assert.Equal(("drawing", true, "vision-test", clock.NowMs - 1_000), (hit.Kind, hit.Nsfw, hit.CaptionModel, hit.CaptionedAtMs));
            Assert.Equal(2, hit.TimesPosted);

            // A direct chat links through "@me"; nothing was said with it, and it is the only picture there.
            var dm = found.Hits.Single(h => h.AttachmentId == inDm);
            Assert.Equal("https://discord.com/channels/@me/900000000000000002/1024709476269436949", dm.MessageUrl);
            Assert.Equal((ConversationKinds.Dm, null, null), (dm.ConversationKind, dm.ConversationTitle, dm.GuildName));
            Assert.Null(dm.MessageText);
            Assert.False(dm.MessageTextCut);
            Assert.Equal((1, 1, 0, 2), (dm.Position, dm.InMessage, dm.Reactions, dm.TimesPosted));
            Assert.Null(dm.Kind);

            // Another platform has no message links.
            string elsewhere = await AttachAsync("m9", Picture(SKColors.Red), "u-ada");
            Assert.Equal("embedded", await module.EmbedAsync(elsewhere, CancellationToken.None));
            var plain = (await module.SearchAsync(new MediaSearchQuery("red"), CancellationToken.None)).Hits.Single(h => h.AttachmentId == elsewhere);
            Assert.Equal("testnet", plain.Platform);
            Assert.Null(plain.MessageUrl);
            Assert.Equal(3, plain.TimesPosted);
        }

        [Theory]
        [InlineData("discord:900", "text", "discord:800", "discord:1000", "https://discord.com/channels/800/900/1000")]
        [InlineData("discord:900", "thread", "discord:800", null, "https://discord.com/channels/800/900")]
        [InlineData("discord:900", "dm", null, "discord:1000", "https://discord.com/channels/@me/900/1000")]
        [InlineData("discord:900", "group_dm", null, "discord:1000", "https://discord.com/channels/@me/900/1000")]
        [InlineData("discord:900", null, null, "discord:1000", "https://discord.com/channels/@me/900/1000")]
        // A server channel whose server is not on record: "@me" would open nothing, so there is no link.
        [InlineData("discord:900", "guild_channel", null, "discord:1000", null)]
        [InlineData("discord:900", "text", null, "discord:1000", null)]
        // Ids Discord did not issue (an import that made them up), and other platforms.
        [InlineData("discord:dm-with-ada", "dm", null, "discord:1000", null)]
        [InlineData("discord:900", "text", "discord:server", "discord:1000", null)]
        [InlineData("discord:900", "dm", null, "discord:row-7", "https://discord.com/channels/@me/900")]
        [InlineData("whatsapp:900", "dm", null, "whatsapp:1000", null)]
        public void DiscordLinks_AreOnlyBuiltWhereDiscordWouldOpenThem(string conversation, string? kind, string? guild, string? message, string? expected) =>
            Assert.Equal(expected, PlatformLinks.Discord(conversation, kind, guild, message));

        [Fact]
        public async Task ImagesOfPeopleWithAnalysisOff_AreNotReturned_ButMinorsAreEmbeddedAndReturned()
        {
            string ada = await AttachAsync("m1", Picture(SKColors.Red), "u-ada");
            string cy = await AttachAsync("m2", Picture(SKColors.Red), "u-cy");
            string dee = await AttachAsync("m3", Picture(SKColors.Red), "u-dee");
            Assert.Equal("embedded", await module.EmbedAsync(ada, CancellationToken.None));
            Assert.Equal("embedded", await module.EmbedAsync(cy, CancellationToken.None)); // before the switch

            await rt.Stores.Operator.WriteAsync((c, tx) =>
            {
                AnalysisOff.Set(c, tx, new[] { PersonOf("u-cy") }, clock.NowMs);
                c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1",
                    ("$p", PersonOf("u-dee")), ("$t", clock.NowMs));
            });
            ReadModelStore.PolicyChanged();
            int before = embedder.Images;
            Assert.Equal("embedded", await module.EmbedAsync(dee, CancellationToken.None));
            Assert.Equal(before + 1, embedder.Images);

            // Cy's image was embedded before analysis was switched off: it is still never returned. Dee is a minor and
            // is treated like anyone else.
            var found = await module.SearchAsync(new MediaSearchQuery("red"), CancellationToken.None);
            Assert.Equal(new[] { ada, dee }.OrderBy(x => x), found.Hits.Select(h => h.AttachmentId).OrderBy(x => x));
        }
    }

    /// <summary>An LLM that is never asked (these tests make no model calls).</summary>
    internal sealed class MediaCaptionTestsNullLlm : Omnipotent.Services.OmniscienceV3.Llm.ILlmClient
    {
        public Task<Omnipotent.Services.KliveLLM.LlmResult> QueryAsync(Omnipotent.Services.KliveLLM.LlmRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("No model call was expected.");

        public Task<Omnipotent.Services.KliveLLM.ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            Task.FromResult(new Omnipotent.Services.KliveLLM.ModelContextInfo("fake", "fake", 64_000, 4_096, "test", null, 0, null));

        public void EndSession(string sessionKey) { }
    }
}
