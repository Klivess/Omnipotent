using System.Collections.Specialized;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Ml.Vision;
using Omnipotent.Services.OmniscienceV3.Modules.Links;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Tests.OmniscienceV3.Ingest;
using SkiaSharp;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The filters every search shares (who, platform, server, conversation, kind of place, when), the lookups that let a
    /// person choose them by name, and what each search does with them. Synthetic data only.
    /// </summary>
    public class SearchFilterTests
    {
        private const string Ana = SignalsHarness.Ana, Ben = SignalsHarness.Ben, Cyd = SignalsHarness.Cyd, Self = SignalsHarness.Self;
        private const string General = SignalsHarness.General, Offtopic = SignalsHarness.Offtopic, Other = SignalsHarness.Other, DmAna = SignalsHarness.DmAna, Group = SignalsHarness.Group;
        private const string Video = "https://www.youtube.com/watch?v=dQw4w9WgXcQ", Song = "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT";
        private static readonly string[] None = Array.Empty<string>();

        private static ApiQuery Q(params (string Name, string Value)[] pairs)
        {
            var c = new NameValueCollection();
            foreach (var (name, value) in pairs) c[name] = value;
            return new ApiQuery(c);
        }

        [Fact]
        public void Filters_AreReadFromTheQuery_AndTheOlderOneValueNamesStillWork()
        {
            var f = ScopeFilter.Parse(Q(("persons", "p-ana, p-ben,p-ana"), ("notGuilds", "discord:g2"), ("conversations", "discord:c1"), ("platforms", "Discord"), ("places", "dm,group"),
                ("from", "1000"), ("to", "2000")));
            Assert.Equal(new[] { "p-ana", "p-ben" }, f.Persons);
            Assert.Equal(new[] { "discord:g2" }, f.NotGuilds);
            Assert.Equal(new[] { "discord:c1" }, f.Conversations);
            Assert.Equal(new[] { "discord" }, f.Platforms);
            Assert.Equal(new[] { "dm", "group" }, f.Places);
            Assert.Equal((1000L, 2000L), (f.FromMs, f.ToMs));
            Assert.False(f.IsEmpty);
            Assert.False(f.OnlyTime);

            // A route's older single-value parameters add to the lists.
            var older = ScopeFilter.Parse(Q(("person", "p-ana"), ("conversation", "discord:c2"), ("persons", "p-cyd")), singlePerson: new[] { "person" }, singleConversation: new[] { "conversation" });
            Assert.Equal(new[] { "p-cyd", "p-ana" }, older.Persons);
            Assert.Equal(new[] { "discord:c2" }, older.Conversations);

            Assert.True(ScopeFilter.Parse(Q()).IsEmpty);
            Assert.True(ScopeFilter.Parse(Q(("from", "5"))).OnlyTime);
            // Every kind of place is the same as none said.
            Assert.True(ScopeFilter.Parse(Q(("places", "server,group,dm"))).IsEmpty);

            // What cannot be a filter is named, never quietly dropped.
            Assert.Contains("places", Assert.Throws<ApiException>(() => ScopeFilter.Parse(Q(("places", "moon")))).Message);
            Assert.Contains("platform", Assert.Throws<ApiException>(() => ScopeFilter.Parse(Q(("platforms", "dis%cord")))).Message);
            Assert.Throws<ApiException>(() => ScopeFilter.Parse(Q(("from", "2000"), ("to", "1000"))));
        }

        [Fact]
        public async Task Filters_BecomeClauses_AndOneATableCannotAnswerIsRefused()
        {
            await using var h = await SignalsHarness.StartAsync();
            var resolver = new ScopeResolver(h.Runtime.Stores, h.Runtime.Host.Policy);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            // A table of shares: no platform and no kind column, so both are told from the conversation.
            ScopeSql.Apply(ScopeFilter.None with { Persons = new[] { "p-ana" }, NotGuilds = new[] { "discord:g2" }, Platforms = new[] { "discord" }, Places = new[] { "dm" }, FromMs = 5 },
                new ScopeColumns(Person: "s.person_id", Guild: "s.guild_id", Conversation: "s.conversation_id", Time: "s.at_ms"), resolver, where, args);
            Assert.Contains("s.person_id IN ($sfp0)", where);
            Assert.Contains("(s.guild_id IS NULL OR s.guild_id NOT IN ($sfng0))", where);
            Assert.Contains("(s.conversation_id LIKE $sfpl0)", where);
            Assert.Contains("s.at_ms >= $sffrom", where);
            Assert.Contains(("$sfpl0", (object?)"discord:%"), args);
            // "Direct chats" names the three seeded ones.
            string direct = Assert.Single(where, w => w.Contains("$sfkc0"));
            Assert.Equal("(s.conversation_id IN ($sfkc0,$sfkc1,$sfkc2))", direct);
            Assert.Equal(new[] { SignalsHarness.DmAna, SignalsHarness.DmBen, SignalsHarness.DmBotBen }.OrderBy(x => x, StringComparer.Ordinal),
                args.Where(a => a.Item1.StartsWith("$sfkc", StringComparison.Ordinal)).Select(a => (string)a.Item2!).OrderBy(x => x, StringComparer.Ordinal));

            // With a kind column the place is read from it; "not in a server" needs no lookup when there is a server column.
            where.Clear();
            args.Clear();
            ScopeSql.Apply(ScopeFilter.None with { Places = new[] { "server" } }, new ScopeColumns(Kind: "cv.kind"), resolver, where, args);
            Assert.Equal("cv.kind IN ($sfk0,$sfk1,$sfk2,$sfk3)", Assert.Single(where));
            where.Clear();
            args.Clear();
            ScopeSql.Apply(ScopeFilter.None with { Places = new[] { "dm", "group" } }, new ScopeColumns(Guild: "s.guild_id", Conversation: "s.conversation_id"), resolver, where, args);
            Assert.Equal("(s.guild_id IS NULL)", Assert.Single(where));
            Assert.Empty(args);

            // Two people's columns (a notice about a pair): either one counts.
            where.Clear();
            args.Clear();
            ScopeSql.Apply(ScopeFilter.None with { Persons = new[] { "p-ana" } }, new ScopeColumns(Person: "person_id", SecondPerson: "other_person_id"), resolver, where, args);
            Assert.Equal("(person_id IN ($sfp0) OR other_person_id IN ($sfp0))", Assert.Single(where));

            Assert.Contains("server", Assert.Throws<ApiException>(() =>
                ScopeSql.Apply(ScopeFilter.None with { Guilds = new[] { "discord:g1" } }, new ScopeColumns(Person: "p"), resolver, new List<string>(), new List<(string, object?)>())).Message);
            // Nothing asked, nothing added.
            ScopeSql.Apply(ScopeFilter.None, new ScopeColumns(), resolver, where = new List<string>(), args);
            Assert.Empty(where);
        }

        [Fact]
        public async Task Lookups_FindServersAndConversationsByName_AndCallAChatByWhoIsInIt()
        {
            await using var h = await SignalsHarness.StartAsync();
            // A direct chat nobody named, with Cyd.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO conversations(conversation_id, platform, kind, guild_id, title, first_seen_ms, last_message_ms) VALUES('discord:9000000001','discord','dm',NULL,NULL,1,$n)", ("$n", h.Clock.NowMs));
                foreach (string member in new[] { Self, Cyd })
                    c.Exec(tx, "INSERT INTO conversation_members(conversation_id, identity_id, role, from_ms) VALUES('discord:9000000001',$i,'member',1)", ("$i", member));
                return 0;
            });
            var lookups = new Lookups(h.Runtime.Stores);

            // Servers: all of them with nothing typed, then by part of the name, by the whole id.
            var servers = lookups.Guilds(null, None, None, 30);
            Assert.Equal(new[] { "discord:g1", "discord:g2", "discord:g3" }, servers.Items.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal));
            var one = Assert.Single(lookups.Guilds("ver 1", None, None, 30).Items);
            Assert.Equal(("discord:g1", "Server 1", "discord", 2L), (one.Id, one.Label, one.Platform, one.Count));
            Assert.Equal("discord:g2", Assert.Single(lookups.Guilds("discord:g2", None, None, 30).Items).Id);
            Assert.Empty(lookups.Guilds("100%", None, None, 30).Items);
            Assert.Empty(lookups.Guilds(null, new[] { "whatsapp" }, None, 30).Items);
            Assert.True(lookups.Guilds(null, None, None, 2).Truncated);

            // Conversations of one server; by part of a name; each says where it is.
            var inServer = lookups.Conversations(null, new[] { "discord:g1" }, None, None, None, 30, namePeople: true);
            Assert.Equal(new[] { General, Offtopic }, inServer.Items.Select(o => o.Id).OrderBy(x => x, StringComparer.Ordinal));
            Assert.All(inServer.Items, o => Assert.Equal(("server", "Server 1", "discord:g1"), (o.Kind, o.Detail, o.GroupId)));
            var lobby = Assert.Single(lookups.Conversations("lob", None, None, None, None, 30, namePeople: true).Items);
            Assert.Equal((Other, "lobby", "Server 2"), (lobby.Id, lobby.Label, lobby.GroupLabel));
            Assert.Equal(new[] { Group }, lookups.Conversations(null, None, None, new[] { "group" }, None, 30, namePeople: true).Items.Select(o => o.Id));

            // The unnamed direct chat is found by who is in it, and called by their name. Its number finds it too.
            var withCyd = Assert.Single(lookups.Conversations("cy", None, None, new[] { "dm" }, None, 30, namePeople: true).Items);
            Assert.Equal(("discord:9000000001", "Cyd", "Direct chat", "dm"), (withCyd.Id, withCyd.Label, withCyd.Detail, withCyd.Kind));
            Assert.Equal("discord:9000000001", Assert.Single(lookups.Conversations("9000000001", None, None, None, None, 30, namePeople: true).Items).Id);
            // Whoever may not read people gets the chat, without the name, and cannot find it by one.
            Assert.Empty(lookups.Conversations("cy", None, None, new[] { "dm" }, None, 30, namePeople: false).Items);
            Assert.Equal("Direct chat", lookups.Conversations(null, None, None, None, new[] { "discord:9000000001" }, 30, namePeople: false).Items.Single().Label);
            // The most recently written-in come first.
            Assert.Equal("discord:9000000001", lookups.Conversations(null, None, None, None, None, 30, namePeople: true).Items[0].Id);

            var labels = lookups.Labels(new[] { "p-ana", "p-nobody" }, new[] { "discord:g1" }, new[] { General, "discord:9000000001" }, namePeople: true);
            Assert.Equal("Ana", Assert.Single(labels.Persons).Value);
            Assert.Equal("Server 1", labels.Guilds["discord:g1"]);
            Assert.Equal(("general · Server 1", "Cyd"), (labels.Conversations[General], labels.Conversations["discord:9000000001"]));
            Assert.Empty(lookups.Labels(new[] { "p-ana" }, None, None, namePeople: false).Persons);

            var platform = Assert.Single(lookups.Platforms().Items);
            Assert.Equal(("discord", "Discord", 9L), (platform.Id, platform.Label, platform.Count));
        }

        [Fact]
        public async Task Links_AreSearchedByWhoWhereAndWhat_FromTheNarrowestIndex()
        {
            await using var h = await SignalsHarness.StartAsync(links: true);
            var links = h.Links!;
            string a1 = await h.PostAsync(General, Ana, "watch https://youtu.be/dQw4w9WgXcQ");
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            string b1 = await h.PostAsync(Offtopic, Ben, "song " + Song);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            string c1 = await h.PostAsync(Other, Cyd, "again " + Video);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            string a2 = await h.PostAsync(DmAna, Ana, "privately https://news.example.test/story/1");
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            string g1 = await h.PostAsync(Group, Ben, "for the group https://news.example.test/story/2");
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();

            string[] Found(LinkSearchQuery query) => links.Search(query).Items.Select(i => i.MessageId).OrderBy(m => m, StringComparer.Ordinal).ToArray();
            string[] Scoped(ScopeFilter filter) => Found(new LinkSearchQuery { Filter = filter });
            static string[] Sorted(params string[] ids) => ids.OrderBy(m => m, StringComparer.Ordinal).ToArray();

            // Where: a server, everything but a server (direct and group chats included), a conversation, a kind of place.
            Assert.Equal(Sorted(a1, b1), Scoped(ScopeFilter.None with { Guilds = new[] { "discord:g1" } }));
            Assert.Equal(Sorted(a1, b1, c1), Scoped(ScopeFilter.None with { Guilds = new[] { "discord:g1", "discord:g2" } }));
            Assert.Equal(Sorted(c1, a2, g1), Scoped(ScopeFilter.None with { NotGuilds = new[] { "discord:g1" } }));
            Assert.Equal(Sorted(c1), Scoped(ScopeFilter.None with { Conversations = new[] { Other } }));
            Assert.Equal(Sorted(b1, c1, a2, g1), Scoped(ScopeFilter.None with { NotConversations = new[] { General } }));
            Assert.Equal(Sorted(a2), Scoped(ScopeFilter.None with { Places = new[] { "dm" } }));
            Assert.Equal(Sorted(a2, g1), Scoped(ScopeFilter.None with { Places = new[] { "dm", "group" } }));
            Assert.Equal(Sorted(a1, b1, c1), Scoped(ScopeFilter.None with { Places = new[] { "server" } }));
            Assert.Equal(5, Scoped(ScopeFilter.None with { Platforms = new[] { "discord" } }).Length);
            Assert.Empty(Scoped(ScopeFilter.None with { Platforms = new[] { "whatsapp" } }));
            Assert.Equal(5, Scoped(ScopeFilter.None with { NotPlatforms = new[] { "whatsapp" } }).Length);

            // Who: several people, or everyone but someone.
            Assert.Equal(Sorted(a1, c1, a2), Scoped(ScopeFilter.None with { Persons = new[] { "p-ana", "p-cyd" } }));
            Assert.Equal(Sorted(b1, c1, g1), Scoped(ScopeFilter.None with { NotPersons = new[] { "p-ana" } }));
            // Who and where and when together.
            Assert.Equal(Sorted(a1), Scoped(ScopeFilter.None with { Persons = new[] { "p-ana" }, Guilds = new[] { "discord:g1" } }));
            Assert.Empty(Scoped(ScopeFilter.None with { Persons = new[] { "p-ana" }, Guilds = new[] { "discord:g2" } }));
            Assert.Equal(Sorted(g1), Scoped(ScopeFilter.None with { NotPersons = new[] { "p-ana" }, FromMs = h.Clock.NowMs - 30_000 }));

            // What: any of several kinds or sites, alone and inside a place; words inside a place.
            Assert.Equal(Sorted(a1, b1, c1), Found(new LinkSearchQuery { Kinds = new[] { "video", "music" } }));
            Assert.Equal(Sorted(a1), Found(new LinkSearchQuery { Kinds = new[] { "video", "music" }, Filter = ScopeFilter.None with { Conversations = new[] { General } } }));
            Assert.Equal(Sorted(a1, b1, c1), Found(new LinkSearchQuery { Sites = new[] { "youtube.com", "https://open.spotify.com/" } }));
            Assert.Equal(Sorted(b1), Found(new LinkSearchQuery { Sites = new[] { "youtube.com", "spotify.com" }, Kinds = new[] { "music" } }));
            Assert.Equal(Sorted(c1), Found(new LinkSearchQuery("youtube") { Filter = ScopeFilter.None with { Guilds = new[] { "discord:g2" } } }));
            Assert.Empty(Found(new LinkSearchQuery("spotify") { Filter = ScopeFilter.None with { Guilds = new[] { "discord:g2" } } }));
            Assert.Equal(Sorted(a2), Found(new LinkSearchQuery("story") { Filter = ScopeFilter.None with { Places = new[] { "dm" } } }));
            // Newest first, and it says when there is more than it shows.
            var two = links.Search(new LinkSearchQuery(Limit: 2) { Filter = ScopeFilter.None with { NotPersons = new[] { "p-cyd" } } });
            Assert.Equal(new[] { g1, a2 }, two.Items.Select(i => i.MessageId));
            Assert.True(two.Truncated);
            var newestOfServer = links.Search(new LinkSearchQuery(Limit: 1) { Filter = ScopeFilter.None with { Guilds = new[] { "discord:g1" } } });
            Assert.Equal((b1, true), (newestOfServer.Items.Single().MessageId, newestOfServer.Truncated));

            // The sites a filter offers by name: the most linked first, or those that hold what was typed.
            var sites = links.Sites(null, 10);
            Assert.Equal(("example.test", 2L), (sites.Items[0].Id, sites.Items[0].Count));
            Assert.Equal(new[] { "spotify.com", "youtube.com" }, sites.Items.Skip(1).Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal("youtube.com", Assert.Single(links.Sites("TUBE", 10).Items).Id);
            Assert.Empty(links.Sites("100%", 10).Items);

            // The tracker under the same filters.
            Assert.Equal(2, links.Track(Video, null).Shares);
            Assert.Equal(1, links.Track(Video, null, scope: ScopeFilter.None with { Guilds = new[] { "discord:g2" } }).Shares);
            Assert.Equal(1, links.Track(Video, null, scope: ScopeFilter.None with { NotPersons = new[] { "p-cyd" } }).Shares);

            // Someone who is or may be under 18 can be asked for by name like anyone else.
            await h.MarkMinorAsync("p-ben");
            Assert.Equal(Sorted(a1, b1, a2, g1), Scoped(ScopeFilter.None with { Persons = new[] { "p-ana", "p-ben" } }));
            Assert.Equal(Sorted(a1, c1, a2), Scoped(ScopeFilter.None with { NotPersons = new[] { "p-ben" } }));
            Assert.Throws<KeyNotFoundException>(() => Scoped(ScopeFilter.None with { Persons = new[] { "p-nobody" } }));
        }

        [Fact]
        public async Task People_AreFoundByWhereTheyHaveWritten_FromTheRollups()
        {
            await using var h = await SignalsHarness.StartAsync(rollups: true);
            await h.PostAsync(General, Ana, "hello there");
            await h.PostAsync(General, Ana, "and again");
            await h.PostAsync(Offtopic, Ben, "over here");
            await h.PostAsync(Other, Cyd, "in the other server");
            await h.PostAsync(Group, Ben, "in the group");
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();

            // The most active there first.
            Assert.Equal(new[] { "p-ana", "p-ben" }, WsCRoutes.WroteIn(h.Runtime.Stores, new[] { "discord:g1" }, None));
            Assert.Equal(new[] { "p-cyd" }, WsCRoutes.WroteIn(h.Runtime.Stores, new[] { "discord:g2" }, None));
            Assert.Equal(new[] { "p-ben" }, WsCRoutes.WroteIn(h.Runtime.Stores, None, new[] { Group, Offtopic }));
            Assert.Equal(new[] { "p-ana", "p-ben", "p-cyd" }, WsCRoutes.WroteIn(h.Runtime.Stores, new[] { "discord:g1" }, new[] { Other }).OrderBy(p => p, StringComparer.Ordinal));
            Assert.Empty(WsCRoutes.WroteIn(h.Runtime.Stores, new[] { "discord:g3" }, None));
        }
    }

    /// <summary>The image search under the shared filters and its own (kind, format, text in it, deleted). Synthetic images only.</summary>
    public class MediaSearchFilterTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_visualfilter_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)));
        private static readonly IngestSource Source = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
        private V3Runtime rt = null!;
        private MediaVisualModule module = null!;

        /// <summary>A stand-in for the image model: every image and every description is the same, so only the filters decide what is found.</summary>
        private sealed class FlatEmbedder : IImageTextEmbedder
        {
            public string ModelVersion => "test-flat.1";

            public Task<float[][]> EmbedImagesAsync(IReadOnlyList<SKBitmap> images, CancellationToken ct) =>
                Task.FromResult(images.Select(_ => VectorMath.Normalized(new float[] { 1, 1, 1, 1 })).ToArray());

            public Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
                Task.FromResult(texts.Select(_ => VectorMath.Normalized(new float[] { 1, 1, 1, 1 })).ToArray());
        }

        public async Task InitializeAsync()
        {
            module = new MediaVisualModule(new FlatEmbedder());
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

        private static byte[] Picture(SKColor colour, SKEncodedImageFormat format)
        {
            using var bitmap = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(colour);
            using var data = bitmap.Encode(format, 90);
            return data.ToArray();
        }

        /// <summary>One image posted by <paramref name="author"/> in a conversation, stored and embedded.</summary>
        private async Task<string> PostAsync(string messageId, string author, IngestConversation where, SKColor colour, string mime = "image/png", long? at = null)
        {
            var stored = await new MediaStore(rt.Stores.Paths).PutAsync(new MemoryStream(Picture(colour, mime == "image/jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png)), null, 1 << 24);
            long sent = at ?? clock.NowMs - 60_000;
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, Source, SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = messageId, Conversation = where, Author = new IngestIdentity("testnet", author), SentAtMs = sent, OrderKey = sent,
                    Attachments = new[] { new IngestAttachment(mime == "image/jpeg" ? "picture.jpg" : "picture.png", mime) { Stored = stored } },
                });
                return w.Complete();
            });
            string id = $"testnet:{messageId}#0";
            Assert.Equal("embedded", await module.EmbedAsync(id, CancellationToken.None));
            return id;
        }

        private Task LabelAsync(string attachmentId, string task, string? label, object signals) =>
            rt.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO labels(item_kind, item_id, item_version, task, label, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                VALUES('attachment',$i,1,$t,$l,0,$s,'test',$t || '-1',$t || '-1',$n,1)
                """, ("$i", attachmentId), ("$t", task), ("$l", label), ("$s", System.Text.Json.JsonSerializer.Serialize(signals)), ("$n", clock.NowMs)));

        private string PersonOf(string author)
        {
            using var c = rt.Stores.Corpus.OpenRead();
            return c.Scalar<string>(null, "SELECT person_id FROM identities WHERE identity_id=$i", ("$i", "testnet:" + author))!;
        }

        [Fact]
        public async Task Images_AreNarrowedByWhoWhereWhenAndByWhatTheyAre()
        {
            var general = new IngestConversation("testnet", "general", ConversationKinds.Text, "general", GuildId: "g1", GuildName: "First server");
            var lobby = new IngestConversation("testnet", "lobby", ConversationKinds.Text, "lobby", GuildId: "g2", GuildName: "Second server");
            var direct = new IngestConversation("testnet", "dm-ada", ConversationKinds.Dm);
            var group = new IngestConversation("testnet", "the-group", ConversationKinds.GroupDm, "The group");
            string meme = await PostAsync("m1", "u-ada", general, SKColors.Red);
            string shot = await PostAsync("m2", "u-bo", general, SKColors.Blue, mime: "image/jpeg");
            string photo = await PostAsync("m3", "u-ada", lobby, SKColors.Green);
            string priv = await PostAsync("m4", "u-ada", direct, SKColors.Yellow);
            string old = await PostAsync("m5", "u-cy", group, SKColors.Gray, at: clock.NowMs - 40 * 86_400_000L);
            await LabelAsync(meme, "caption", "meme", new { caption = "A picture with a joke on it.", kind = "meme", tags = new[] { "joke" }, nsfw = false });
            await LabelAsync(shot, "caption", "screenshot", new { caption = "A picture of a settings page.", kind = "screenshot", tags = new[] { "settings" }, nsfw = true });
            await LabelAsync(shot, "ocr", null, new { text = "Settings: notifications on" });
            await LabelAsync(photo, "ocr", null, new { text = "" });
            await rt.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE messages SET deleted_at_ms=$n WHERE message_id='testnet:m3'", ("$n", clock.NowMs)));

            async Task<string[]> Found(MediaSearchQuery query) =>
                (await module.SearchAsync(query, CancellationToken.None)).Hits.Select(h => h.AttachmentId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Task<string[]> Scoped(ScopeFilter filter) => Found(new MediaSearchQuery("a picture") { Filter = filter });
            static string[] Sorted(params string[] ids) => ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string g1 = CorpusIds.Guild("testnet", "g1"), g2 = CorpusIds.Guild("testnet", "g2");

            Assert.Equal(Sorted(meme, shot, photo, priv, old), await Found(new MediaSearchQuery("a picture")));

            // Where: servers, everything outside one, conversations, kinds of place, the platform.
            Assert.Equal(Sorted(meme, shot), await Scoped(ScopeFilter.None with { Guilds = new[] { g1 } }));
            Assert.Equal(Sorted(meme, shot, photo), await Scoped(ScopeFilter.None with { Guilds = new[] { g1, g2 } }));
            Assert.Equal(Sorted(photo, priv, old), await Scoped(ScopeFilter.None with { NotGuilds = new[] { g1 } }));
            Assert.Equal(Sorted(photo, old), await Scoped(ScopeFilter.None with { Conversations = new[] { CorpusIds.Conversation("testnet", "lobby"), CorpusIds.Conversation("testnet", "the-group") } }));
            Assert.Equal(Sorted(meme, shot, photo), await Scoped(ScopeFilter.None with { Places = new[] { "server" } }));
            Assert.Equal(Sorted(priv), await Scoped(ScopeFilter.None with { Places = new[] { "dm" } }));
            Assert.Equal(Sorted(priv, old), await Scoped(ScopeFilter.None with { Places = new[] { "dm", "group" } }));
            Assert.Equal(5, (await Scoped(ScopeFilter.None with { Platforms = new[] { "testnet" } })).Length);
            Assert.Empty(await Scoped(ScopeFilter.None with { Platforms = new[] { "elsewhere" } }));

            // Who: several people, everyone but one, and with a place.
            Assert.Equal(Sorted(shot, old), await Scoped(ScopeFilter.None with { Persons = new[] { PersonOf("u-bo"), PersonOf("u-cy") } }));
            Assert.Equal(Sorted(shot, old), await Scoped(ScopeFilter.None with { NotPersons = new[] { PersonOf("u-ada") } }));
            Assert.Equal(Sorted(meme), await Scoped(ScopeFilter.None with { Persons = new[] { PersonOf("u-ada") }, Guilds = new[] { g1 } }));
            // The one-person and one-conversation parameters of before still narrow, and add to the lists.
            Assert.Equal(Sorted(photo), await Found(new MediaSearchQuery("a picture", PersonId: PersonOf("u-ada")) { Filter = ScopeFilter.None with { Guilds = new[] { g2 } } }));

            // When.
            Assert.Equal(Sorted(old), await Scoped(ScopeFilter.None with { ToMs = clock.NowMs - 86_400_000L }));
            Assert.Equal(4, (await Scoped(ScopeFilter.None with { FromMs = clock.NowMs - 86_400_000L })).Length);

            // What the image is: its kind (only captioned images have one), its format, text in it, deleted, marked sensitive.
            Assert.Equal(Sorted(meme), await Found(new MediaSearchQuery("a picture") { ImageKinds = new[] { "meme" } }));
            Assert.Equal(Sorted(meme, shot), await Found(new MediaSearchQuery("a picture") { ImageKinds = new[] { "Meme", "screenshot" } }));
            Assert.Empty(await Found(new MediaSearchQuery("a picture") { ImageKinds = new[] { "chart" } }));
            Assert.Equal(Sorted(shot), await Found(new MediaSearchQuery("a picture") { ImageFormats = new[] { "jpeg" } }));
            Assert.Equal(Sorted(meme, photo, priv, old), await Found(new MediaSearchQuery("a picture") { ImageFormats = new[] { "png", "webp" } }));
            Assert.Equal(Sorted(shot), await Found(new MediaSearchQuery("a picture") { HasText = true }));
            Assert.Equal(Sorted(meme, photo, priv, old), await Found(new MediaSearchQuery("a picture") { HasText = false }));
            Assert.Equal(Sorted(photo), await Found(new MediaSearchQuery("a picture") { Deleted = true }));
            Assert.Equal(Sorted(meme, shot, priv, old), await Found(new MediaSearchQuery("a picture") { Deleted = false }));
            Assert.Equal(Sorted(shot), await Found(new MediaSearchQuery("a picture") { Nsfw = true }));
            Assert.Equal(Sorted(meme, photo, priv, old), await Found(new MediaSearchQuery("a picture") { Nsfw = false }));
            // Together: what it is, where, and not from someone.
            Assert.Equal(Sorted(meme), await Found(new MediaSearchQuery("a picture")
            {
                ImageKinds = new[] { "meme", "screenshot" }, HasText = false, Filter = ScopeFilter.None with { Guilds = new[] { g1 }, NotPersons = new[] { PersonOf("u-bo") } },
            }));
            Assert.Empty(await Found(new MediaSearchQuery("a picture") { ImageKinds = new[] { "meme" }, Filter = ScopeFilter.None with { Guilds = new[] { g2 } } }));

            await Assert.ThrowsAsync<ApiException>(() => Found(new MediaSearchQuery("a picture") { ImageKinds = new[] { "selfie" } }));
            await Assert.ThrowsAsync<ApiException>(() => Found(new MediaSearchQuery("a picture") { ImageFormats = new[] { "bmp" } }));
        }
    }
}
