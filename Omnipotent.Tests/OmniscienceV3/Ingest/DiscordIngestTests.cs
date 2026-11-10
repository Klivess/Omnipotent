using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Discord;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using static Omnipotent.Tests.OmniscienceV3.Ingest.DiscordFixtures;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>
    /// Discord as v3 stores it: reading Discord's own payload, the events that are not messages, and how two copies
    /// of the same message from sources of different quality are reconciled.
    /// </summary>
    public class DiscordIngestTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_discord_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Now);
        private OmniscienceStores stores = null!;

        private static readonly long Now = Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        private static readonly long T2021 = Instant.FromUtc(new DateTime(2021, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        private static readonly IngestSource Platform = new("discord:klivebot", "discord", "bot", "KliveBot", IngestSource.PlatformRank);
        private static readonly IngestSource Relay = new("discord:klivebot:relay", "discord", "bot", "KliveBot (as relayed)", IngestSource.RelayedRank);
        private static readonly IngestSource Export = new("import:test", "discord", "import", "An export", IngestSource.ImportRank);

        public async Task InitializeAsync() => stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private Task<T> Write<T>(IngestSource source, string kind, Func<CorpusWriter, T> work) =>
            stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, source, kind, clock.NowMs);
                var result = work(w);
                w.Complete();
                return result;
            });

        private T One<T>(string sql, params (string, object?)[] args)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private List<T> Rows<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, sql, map, args);
        }

        private static IngestMessage Plain(string id, string? content, IngestIdentity? author = null, long? editedAtMs = null) => new()
        {
            PlatformMessageId = id,
            Conversation = GeneralChannel.ToConversation(),
            Author = author ?? new IngestIdentity("discord", Sam, "sam"),
            SentAtMs = Instant.FromDiscordSnowflake(ulong.Parse(id)),
            OrderKey = long.Parse(id),
            Content = content,
            EditedAtMs = editedAtMs,
        };

        // ── reading Discord's payload ────────────────────────────────────────────

        [Fact]
        public void Payload_KeepsEverythingDiscordSays()
        {
            string id = Snow(T2021);
            string repliedTo = Snow(T2021 - 60_000);
            var json = Message(id, User(Sam, "sam", "Sam W.", avatar: "abc123"), "look at this", more: m =>
            {
                m["type"] = 19;
                m["guild_id"] = Guild;
                m["member"] = new JsonObject { ["nick"] = "Sammy" };
                m["message_reference"] = new JsonObject { ["type"] = 0, ["message_id"] = repliedTo, ["channel_id"] = General };
                m["edited_timestamp"] = "2021-03-10T09:05:00.000000+00:00";
                m["flags"] = 1 << 13;
                m["attachments"] = new JsonArray(new JsonObject
                {
                    ["id"] = "600000000000000001", ["filename"] = "voice-message.ogg", ["content_type"] = "audio/ogg", ["size"] = 4096,
                    ["url"] = "https://cdn.discordapp.com/attachments/1/2/voice-message.ogg", ["duration_secs"] = 3.5,
                });
                m["mentions"] = new JsonArray(User(Alex, "alex"));
                m["reactions"] = new JsonArray(new JsonObject { ["count"] = 2, ["emoji"] = new JsonObject { ["id"] = null, ["name"] = "👍" } });
            });

            var message = DiscordPayload.Message(El(json), GeneralChannel, Reader, observedAtMs: Now)!;

            Assert.Equal(T2021, message.SentAtMs);                       // from the id, not from any timestamp field
            Assert.Equal(TimeQuality.Exact, message.Quality);
            Assert.Equal("look at this", message.Content);
            Assert.Equal(Instant.FromUtc(new DateTime(2021, 3, 10, 9, 5, 0, DateTimeKind.Utc)), message.EditedAtMs);
            Assert.Equal(repliedTo, message.ReplyToPlatformMessageId);
            Assert.False(message.IsSystem);
            Assert.Equal("sam", message.Author.Username);
            Assert.Equal("Sam W.", message.Author.DisplayName);          // the global name, not the nickname
            Assert.Equal("Sammy", message.Author.Nickname);
            Assert.Equal(Guild, message.Author.NicknameScope);
            Assert.Equal("abc123", message.Author.AvatarHash);
            Assert.Equal(Now, message.Author.ObservedAtMs);
            var voice = Assert.Single(message.Attachments);
            Assert.Equal("voice", voice.MediaKind);
            Assert.Equal(3500, voice.DurationMs);
            Assert.Contains(message.Mentions, x => x.Kind == "user" && x.Target == "discord:" + Alex);
            Assert.Equal(2, Assert.Single(message.Reactions).Count);
            Assert.Contains("\"duration_secs\"", message.RawJson);       // the payload itself is kept
        }

        [Fact]
        public void Payload_ForwardsPollsAndSystemNoticesAreToldApart()
        {
            var forward = DiscordPayload.Message(El(Message(Snow(T2021, 1), User(Sam, "sam"), "", more: m =>
            {
                m["message_reference"] = new JsonObject { ["type"] = 1, ["message_id"] = Snow(T2021 - 5000), ["channel_id"] = Private };
                m["message_snapshots"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = "the forwarded text" } });
            })), GeneralChannel, Reader)!;
            Assert.Null(forward.ReplyToPlatformMessageId);               // a forward is not a reply
            Assert.Contains("the forwarded text", forward.ForwardedFromJson);
            Assert.Equal("", forward.Content);                           // with the content intent, empty means empty

            var poll = DiscordPayload.Message(El(Message(Snow(T2021, 2), User(Sam, "sam"), "", more: m =>
                m["poll"] = new JsonObject { ["question"] = new JsonObject { ["text"] = "Pizza or pasta?" }, ["answers"] = new JsonArray() })), GeneralChannel, Reader)!;
            Assert.Equal("Pizza or pasta?", poll.Poll!.Question);

            var joined = DiscordPayload.Message(El(Message(Snow(T2021, 3), User(Sam, "sam"), "", more: m => m["type"] = 7)), GeneralChannel, Reader)!;
            Assert.True(joined.IsSystem);                                // "sam joined the server" is Discord speaking, not sam

            var hook = DiscordPayload.Message(El(Message(Snow(T2021, 4), User("900000000000000099", "Build Status"), "deploy finished",
                more: m => m["webhook_id"] = "900000000000000099")), GeneralChannel, Reader)!;
            Assert.True(hook.IsWebhook);
            Assert.True(hook.Author.IsBot);

            var mine = DiscordPayload.Message(El(Message(Snow(T2021, 5), User(Owner, "klives"), "hi")), GeneralChannel, Reader)!;
            Assert.True(mine.Author.IsSelf);

            Assert.Null(DiscordPayload.Message(El(new JsonObject { ["id"] = "not-an-id", ["author"] = User(Sam, "sam") }), GeneralChannel, Reader));
            Assert.Null(DiscordPayload.Message("{ not json", GeneralChannel, Reader));
        }

        [Fact]
        public void Payload_WithoutTheContentIntent_EmptyTextIsUnknownNotEmpty()
        {
            var blind = Reader with { ContentIntent = false };
            string id = Snow(T2021);

            Assert.Null(DiscordPayload.Message(El(Message(id, User(Sam, "sam"), "")), GeneralChannel, blind)!.Content);
            // Discord still gives the text in DMs, for the bot's own messages and when the bot is mentioned.
            Assert.Equal("", DiscordPayload.Message(El(Message(id, User(Sam, "sam"), "", Dm)), DmChannel, blind)!.Content);
            Assert.Equal("", DiscordPayload.Message(El(Message(id, User(Bot, "KliveBot", bot: true), "")), GeneralChannel, blind)!.Content);
            Assert.Equal("", DiscordPayload.Message(El(Message(id, User(Sam, "sam"), "", more: m => m["mentions"] = new JsonArray(User(Bot, "KliveBot", bot: true)))),
                GeneralChannel, blind)!.Content);
            Assert.Equal("hello", DiscordPayload.Message(El(Message(id, User(Sam, "sam"), "hello")), GeneralChannel, blind)!.Content);
        }

        [Fact]
        public void Payload_ChannelsBecomeTheRightKindOfConversation()
        {
            Assert.Equal(ConversationKinds.Text, DiscordPayload.ConversationKind(0));
            Assert.Equal(ConversationKinds.Dm, DiscordPayload.ConversationKind(1));
            Assert.Equal(ConversationKinds.GroupDm, DiscordPayload.ConversationKind(3));
            Assert.Equal(ConversationKinds.Channel, DiscordPayload.ConversationKind(5));
            Assert.Equal(ConversationKinds.Thread, DiscordPayload.ConversationKind(11));
            Assert.Null(DiscordPayload.ConversationKind(4));    // a category holds no messages
            Assert.Null(DiscordPayload.ConversationKind(15));   // nor does a forum itself; its posts are threads

            var thread = DiscordPayload.Channel(El(new JsonObject { ["id"] = PlansThread, ["type"] = 11, ["name"] = "release plans", ["guild_id"] = Guild, ["parent_id"] = General }), Reader, "Test Server")!;
            Assert.Equal(General, thread.ParentChannelId);
            Assert.Equal("Test Server", thread.GuildName);

            var text = DiscordPayload.Channel(El(new JsonObject { ["id"] = General, ["type"] = 0, ["name"] = "general", ["guild_id"] = Guild, ["parent_id"] = "700000000000000099" }), Reader)!;
            Assert.Null(text.ParentChannelId);                  // a channel's category is not its parent conversation

            var dm = DiscordPayload.Channel(El(new JsonObject { ["id"] = Dm, ["type"] = 1, ["recipients"] = new JsonArray(User(Sam, "sam", "Sam W.")) }), Reader)!;
            Assert.Equal("Sam W.", Assert.Single(dm.Recipients!).DisplayName);
            Assert.Null(DiscordPayload.Channel(El(new JsonObject { ["id"] = "700000000000000098", ["type"] = 4, ["name"] = "Category" }), Reader));
        }

        // ── two copies of one message ────────────────────────────────────────────

        [Fact]
        public async Task TextBeatsNoText_WhateverTheSource()
        {
            string id = Snow(T2021);
            // The bot saw the message without the right to read it.
            await Write(Platform, SourceKinds.Live, w => w.Message(Plain(id, null)));
            // An export, a lower-ranked source, has the text.
            await Write(Export, SourceKinds.Import, w => w.Message(Plain(id, "the real text")));
            Assert.Equal("the real text", One<string>("SELECT content FROM messages"));
            Assert.Equal(2L, One<long>("SELECT current_version FROM messages"));

            // A later blank copy from the better source never blanks it.
            var stats = await Write(Platform, SourceKinds.Backfill, w => { w.Message(Plain(id, "")); return w.Complete(); });
            Assert.Equal(1, stats.Unchanged);
            Assert.Equal("the real text", One<string>("SELECT content FROM messages"));
        }

        [Fact]
        public async Task ThePlatformsPayloadReplacesAPoorerCopy_WithoutANewVersion()
        {
            string id = Snow(T2021);
            await Write(Relay, SourceKinds.Live, w => w.Message(Plain(id, "look at this")));
            Assert.Null(One<string?>("SELECT raw_json FROM messages"));

            var full = DiscordPayload.Message(El(Message(id, User(Sam, "sam", "Sam W."), "look at this", more: m =>
            {
                m["type"] = 19;
                m["message_reference"] = new JsonObject { ["type"] = 0, ["message_id"] = Snow(T2021 - 1000) };
                m["embeds"] = new JsonArray(new JsonObject { ["type"] = "link", ["url"] = "https://example.com/a", ["title"] = "A page" });
            })), GeneralChannel, Reader, Now)!;
            var stats = await Write(Platform, SourceKinds.Backfill, w => { w.Message(full); return w.Complete(); });

            Assert.Equal(1, stats.Updated);
            Assert.Equal(19L, One<long>("SELECT type FROM messages"));
            Assert.Equal(Platform.SourceId, One<string>("SELECT best_source_id FROM messages"));
            Assert.NotNull(One<string?>("SELECT raw_json FROM messages"));
            Assert.Equal("A page", One<string>("SELECT title FROM message_embeds"));
            Assert.NotNull(One<string?>("SELECT reply_to_message_id FROM messages"));
            // Same text: no new version, and nothing downstream is told to redo its work.
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM message_versions"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM change_log WHERE kind='message.version'"));
            Assert.Equal("live", One<string>("SELECT first_source_kind FROM messages"));
        }

        [Fact]
        public async Task AProfileOnAnOldMessageIsTodaysProfile_NotTheOneFromThen()
        {
            long t2020 = Instant.FromUtc(new DateTime(2020, 1, 5, 0, 0, 0, DateTimeKind.Utc));
            long t2019 = Instant.FromUtc(new DateTime(2019, 1, 5, 0, 0, 0, DateTimeKind.Utc));
            IngestIdentity SamNamed(string username, long observed) => new("discord", Sam, username) { ObservedAtMs = observed };

            // History is read backwards: 2021 first, then 2020, each carrying the name the account has today.
            await Write(Platform, SourceKinds.Backfill, w => w.Message(Plain(Snow(T2021), "a", SamNamed("sam", Now))));
            await Write(Platform, SourceKinds.Backfill, w => w.Message(Plain(Snow(t2020), "b", SamNamed("sam", Now))));
            // The only name known reaches back to the oldest message: there is nothing better to show for it.
            var only = Assert.Single(Rows("SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE name_kind='username'", r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2))));
            Assert.Equal(("sam", t2020, (long?)null), only);

            // A day later the account is renamed and seen live.
            long later = Now + 86_400_000;
            clock.Set(later);
            await Write(Platform, SourceKinds.Live, w => w.Message(Plain(Snow(later), "c", SamNamed("sammy", later))));
            // Older history read after that carries the new name too. It must not rewrite the past.
            await Write(Platform, SourceKinds.Backfill, w => w.Message(Plain(Snow(t2019), "d", SamNamed("sammy", later + 1000))));

            var names = Rows("SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE name_kind='username' ORDER BY valid_from_ms", r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2)));
            Assert.Equal(new[] { ("sam", t2020, (long?)later), ("sammy", later, (long?)null) }, names);
            // The account itself is known from its oldest message.
            Assert.Equal(t2019, One<long>("SELECT first_seen_ms FROM identities"));
        }

        [Fact]
        public async Task NicknamesBelongToAServer_AndPicturesHaveAHistory()
        {
            IngestIdentity In(string? nick, string? avatar) => new("discord", Sam, "sam", "Sam W.") { Nickname = nick, NicknameScope = Guild, AvatarHash = avatar };

            await Write(Platform, SourceKinds.Live, w => w.MemberEvent(In("Sammy", "pic1"), Guild, "join", Now, null));
            await Write(Platform, SourceKinds.Live, w => w.MemberEvent(In("Samwise", "pic1"), Guild, "update", Now + 1000, "{\"nick_after\":\"Samwise\"}"));
            // The nickname is taken away; the picture changes.
            await Write(Platform, SourceKinds.Live, w => w.MemberEvent(In(null, "pic2"), Guild, "update", Now + 2000, "{\"nick_after\":null}"));

            var nicks = Rows("SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE name_kind=$k ORDER BY valid_from_ms",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2)), ("$k", "nick:" + Guild));
            Assert.Equal(new[] { ("Sammy", Now, (long?)(Now + 1000)), ("Samwise", Now + 1000, (long?)(Now + 2000)) }, nicks);
            Assert.Equal("Sam W.", One<string>("SELECT value FROM identity_names WHERE name_kind='display'"));

            var pictures = Rows("SELECT avatar_hash, valid_to_ms FROM identity_avatars ORDER BY valid_from_ms", r => (r.GetString(0), r.GetInt64OrNull(1)));
            Assert.Equal(new[] { ("pic1", (long?)(Now + 2000)), ("pic2", (long?)null) }, pictures);
            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM member_events"));
            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM change_log WHERE kind='event.member'"));

            // A message from history (no member object) says nothing about the nickname and leaves it alone.
            await Write(Platform, SourceKinds.Live, w => w.MemberEvent(In("Back Again", "pic2"), Guild, "update", Now + 3000, null));
            await Write(Platform, SourceKinds.Backfill, w => w.Message(Plain(Snow(Now + 4000), "x", new IngestIdentity("discord", Sam, "sam"))));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM identity_names WHERE name_kind=$k AND valid_to_ms IS NULL", ("$k", "nick:" + Guild)));
        }

        [Fact]
        public async Task ConversationKindsGetMoreSpecific_NeverLess()
        {
            // As Omniscience v2 recorded it: some server channel.
            await Write(Export, SourceKinds.Legacy, w => w.ConversationSeen(new IngestConversation("discord", PlansThread, ConversationKinds.LegacyServerChannel, "release plans", Guild), T2021));
            await Write(Platform, SourceKinds.Live, w => w.ConversationSeen(new IngestConversation("discord", PlansThread, ConversationKinds.Thread, "release plans", Guild, "Test Server", General), Now));
            Assert.Equal(("thread", "discord:" + General), Rows("SELECT kind, parent_conversation_id FROM conversations", r => (r.GetString(0), r.GetString(1))).Single());

            // A vague kind never replaces a specific one.
            await Write(Export, SourceKinds.Legacy, w => w.ConversationSeen(new IngestConversation("discord", PlansThread, ConversationKinds.LegacyServerChannel), Now + 1));
            Assert.Equal("thread", One<string>("SELECT kind FROM conversations"));

            // A DM that gained a third person is a group.
            await Write(Platform, SourceKinds.Live, w => w.ConversationSeen(new IngestConversation("discord", Dm, ConversationKinds.Dm), Now));
            await Write(Platform, SourceKinds.Live, w => w.ConversationSeen(new IngestConversation("discord", Dm, ConversationKinds.GroupDm), Now + 1));
            Assert.Equal("group_dm", One<string>("SELECT kind FROM conversations WHERE conversation_id=$c", ("$c", "discord:" + Dm)));

            // A rename is dated from when it was seen.
            await Write(Platform, SourceKinds.Backfill, w => w.ConversationSeen(new IngestConversation("discord", PlansThread, ConversationKinds.Thread, "shipping plans", Guild) { ObservedAtMs = Now + 5000 }, T2021));
            var titles = Rows("SELECT name, valid_from_ms, valid_to_ms FROM conversation_names WHERE conversation_id=$c ORDER BY valid_from_ms",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2)), ("$c", "discord:" + PlansThread));
            Assert.Equal(new[] { ("release plans", T2021, (long?)(Now + 5000)), ("shipping plans", Now + 5000, (long?)null) }, titles);
        }

        // ── events that are not messages ─────────────────────────────────────────

        [Fact]
        public async Task ADeletedMessageKeepsItsText()
        {
            string id = Snow(T2021);
            await Write(Platform, SourceKinds.Live, w => w.Message(Plain(id, "something regretted")));

            Assert.True(await Write(Platform, SourceKinds.Live, w => w.MessageDeleted("discord", id, Now)));
            Assert.False(await Write(Platform, SourceKinds.Live, w => w.MessageDeleted("discord", id, Now + 5)));          // already marked
            Assert.False(await Write(Platform, SourceKinds.Live, w => w.MessageDeleted("discord", Snow(T2021, 9), Now)));  // never seen

            Assert.Equal(("something regretted", Now), Rows("SELECT content, deleted_at_ms FROM messages", r => (r.GetString(0), r.GetInt64(1))).Single());
            var logged = Assert.Single(Rows("SELECT person_id, conversation_id, source_kind FROM change_log WHERE kind='message.delete'",
                r => (r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetString(2))));
            Assert.Equal(CorpusWriter.PersonIdFor("discord:" + Sam), logged.Item1);
            Assert.Equal("discord:" + General, logged.Item2);
        }

        [Fact]
        public async Task ReactionsAreGivenTakenBackAndCleared()
        {
            string id = Snow(T2021);
            var sam = new IngestIdentity("discord", Sam, "sam");
            var alex = new IngestIdentity("discord", Alex, "alex");
            await Write(Platform, SourceKinds.Live, w => w.Message(Plain(id, "good news")));

            Assert.True(await Write(Platform, SourceKinds.Live, w => w.ReactionAdded("discord", id, General, alex, "🎉", "🎉", Now, TimeQuality.Second)));
            Assert.False(await Write(Platform, SourceKinds.Live, w => w.ReactionAdded("discord", id, General, alex, "🎉", "🎉", Now + 1, TimeQuality.Second)));   // heard twice
            Assert.True(await Write(Platform, SourceKinds.Live, w => w.ReactionRemoved("discord", id, General, alex, "🎉", "🎉", Now + 1000)));
            Assert.Equal((Now, (long?)(Now + 1000)), Rows("SELECT added_at_ms, removed_at_ms FROM reactions", r => (r.GetInt64(0), r.GetInt64OrNull(1))).Single());

            // Given again: the same row, its times moved on.
            Assert.True(await Write(Platform, SourceKinds.Live, w => w.ReactionAdded("discord", id, General, alex, "🎉", "🎉", Now + 2000, TimeQuality.Second)));
            Assert.Equal((Now + 2000, (long?)null), Rows("SELECT added_at_ms, removed_at_ms FROM reactions", r => (r.GetInt64(0), r.GetInt64OrNull(1))).Single());

            // A removal of a reaction never seen being given is still a fact.
            Assert.True(await Write(Platform, SourceKinds.Live, w => w.ReactionRemoved("discord", id, General, sam, "custom:600000000000000009", "custom", Now + 3000)));
            Assert.Null(One<long?>("SELECT added_at_ms FROM reactions WHERE identity_id=$i", ("$i", "discord:" + Sam)));

            Assert.Equal(1, await Write(Platform, SourceKinds.Live, w => w.ReactionsCleared("discord", id, General, null, Now + 4000)));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM reactions WHERE removed_at_ms IS NULL"));

            // One change per message per batch, however many reactions it carried.
            await Write(Platform, SourceKinds.Live, w =>
            {
                w.ReactionAdded("discord", id, General, alex, "👍", "👍", Now + 5000, TimeQuality.Second);
                w.ReactionAdded("discord", id, General, sam, "👍", "👍", Now + 5001, TimeQuality.Second);
                return 0;
            });
            Assert.Equal(6L, One<long>("SELECT COUNT(*) FROM change_log WHERE kind='reaction.change'"));
        }

        [Fact]
        public async Task PresenceIsRecordedWhenItChanges_NotEveryTimeItIsRelayed()
        {
            var sam = new IngestIdentity("discord", Sam, "sam");
            PresenceSighting Playing(string? guild, string game, long start) => new(sam, guild, "online", "{\"desktop\":\"online\"}",
                $"[{{\"type\":0,\"name\":\"{game}\",\"timestamps\":{{\"start\":{start}}}}}]", null,
                CorpusWriter.PresenceFingerprint("online", new[] { "desktop=online" }, new[] { $"0|{game}||" }, null));

            // The same change relayed through two shared servers, in one batch.
            await Write(Platform, SourceKinds.Live, w => { w.Presence(Playing(Guild, "Chess", 1), Now); w.Presence(Playing("800000000000000002", "Chess", 1), Now + 5); return 0; });
            // Repeated after a restart, and with a start time that moved: still the same thing being done.
            Assert.False(await Write(Platform, SourceKinds.Live, w => w.Presence(Playing(Guild, "Chess", 99), Now + 60_000)));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM presence_events"));

            Assert.True(await Write(Platform, SourceKinds.Live, w => w.Presence(Playing(Guild, "Checkers", 5), Now + 120_000)));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM presence_events"));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM change_log WHERE kind='event.presence'"));
            Assert.Equal("discord:" + Guild, One<string>("SELECT guild_id FROM presence_events ORDER BY at_ms LIMIT 1"));
        }

        [Fact]
        public async Task VoiceSessionsOpenMoveCloseAndAreReconciled()
        {
            var sam = new IngestIdentity("discord", Sam, "sam");
            var alex = new IngestIdentity("discord", Alex, "alex");
            const string lounge = "700000000000000010", stage = "700000000000000011";

            await Write(Platform, SourceKinds.Live, w => w.Voice(new VoiceSighting(sam, Guild, lounge, "{\"self_mute\":false}"), Now));
            // Muting changes the open session, it does not start another.
            await Write(Platform, SourceKinds.Live, w => w.Voice(new VoiceSighting(sam, Guild, lounge, "{\"self_mute\":true}"), Now + 10_000));
            Assert.Equal("{\"self_mute\":true}", One<string>("SELECT flags_json FROM voice_sessions"));

            await Write(Platform, SourceKinds.Live, w => w.Voice(new VoiceSighting(sam, Guild, stage, null), Now + 60_000));
            await Write(Platform, SourceKinds.Live, w => w.Voice(new VoiceSighting(sam, Guild, null, null), Now + 120_000));
            var sessions = Rows("SELECT channel_id, joined_at_ms, left_at_ms, left_reason FROM voice_sessions ORDER BY joined_at_ms",
                r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2), r.GetStringOrNull(3)));
            Assert.Equal(new[]
            {
                ("discord:" + lounge, Now, (long?)(Now + 60_000), (string?)"moved"),
                ("discord:" + stage, Now + 60_000, (long?)(Now + 120_000), (string?)"left"),
            }, sessions);

            // Two people in voice, then the connection drops; when it is back only one is still there.
            await Write(Platform, SourceKinds.Live, w => { w.Voice(new VoiceSighting(sam, Guild, lounge, null), Now + 200_000); w.Voice(new VoiceSighting(alex, Guild, lounge, null), Now + 200_000); return 0; });
            Assert.Equal(1, await Write(Platform, SourceKinds.Live, w => w.VoiceReconcile("discord", Guild, new[] { Sam }, Now + 900_000)));
            Assert.Equal(("unseen", Now + 900_000), Rows("SELECT left_reason, left_at_ms FROM voice_sessions WHERE identity_id=$i AND left_reason='unseen'",
                r => (r.GetString(0), r.GetInt64(1)), ("$i", "discord:" + Alex)).Single());
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM voice_sessions WHERE left_at_ms IS NULL"));
        }

        [Fact]
        public async Task BeingSeenIsNotTakingPart_OnlyTakingPartMakesAPerson()
        {
            var lurker = new IngestIdentity("discord", Alex, "alex", "Alex P.") { Nickname = "Lex", NicknameScope = Guild, AvatarHash = "pic" };
            await Write(Platform, SourceKinds.Live, w =>
            {
                w.IdentitySeen(lurker, Now);                                                     // in a member list
                w.MemberEvent(lurker, Guild, "join", Now, null);
                w.Presence(new PresenceSighting(lurker, Guild, "online", null, null, null, CorpusWriter.PresenceFingerprint("online", null, null, null)), Now);
                w.Voice(new VoiceSighting(lurker, Guild, "700000000000000010", null), Now);
                w.Typing(lurker, "discord", General, Now);
                return 0;
            });
            // Everything about the account is on record, against its id...
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM presence_events WHERE identity_id=$i", ("$i", "discord:" + Alex)));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM voice_sessions WHERE identity_id=$i", ("$i", "discord:" + Alex)));
            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM identity_names WHERE identity_id=$i", ("$i", "discord:" + Alex)));
            // ...but a silent member is not one of the people.
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM persons"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM identities"));

            // The moment the account writes, it is a person, and what was recorded is already theirs.
            await Write(Platform, SourceKinds.Live, w => w.Message(Plain(Snow(Now + 1000), "hello", lurker with { NicknameScope = null, Nickname = null })));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM persons"));
            Assert.Equal("Lex", One<string>("SELECT value FROM identity_names WHERE identity_id=$i AND name_kind=$k AND valid_to_ms IS NULL", ("$i", "discord:" + Alex), ("$k", "nick:" + Guild)));
            // From then on its events carry the person.
            await Write(Platform, SourceKinds.Live, w => w.Presence(new PresenceSighting(lurker, Guild, "idle", null, null, null, CorpusWriter.PresenceFingerprint("idle", null, null, null)), Now + 2000));
            Assert.Equal(CorpusWriter.PersonIdFor("discord:" + Alex), One<string>("SELECT person_id FROM change_log WHERE kind='event.presence' ORDER BY seq DESC LIMIT 1"));
        }

        // ── the bot API client ───────────────────────────────────────────────────

        private sealed class Stub : HttpMessageHandler
        {
            public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Answers { get; } = new();
            public List<HttpRequestMessage> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(Answers.Dequeue()(request));
            }
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }

        private (DiscordBotApi Api, Stub Http, List<TimeSpan> Waits) NewApi()
        {
            var http = new Stub();
            var waits = new List<TimeSpan>();
            var api = new DiscordBotApi(() => Task.FromResult<string?>("test-token"), clock, http, (span, _) => { waits.Add(span); return Task.CompletedTask; });
            return (api, http, waits);
        }

        [Fact]
        public async Task Api_KeepsToTheAllowanceDiscordStates()
        {
            var (api, http, waits) = NewApi();
            using var owned = api;
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.OK, "[]", ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-After", "1.5")));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.OK, "[]", ("X-RateLimit-Remaining", "4"), ("X-RateLimit-Reset-After", "5")));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.OK, "[]"));

            await api.MessagesAsync(General, null, null, 100, default);
            Assert.Empty(waits);
            await api.MessagesAsync(General, null, Snow(T2021), 100, default);      // the allowance for this channel is spent: wait for its reset
            Assert.InRange(Assert.Single(waits).TotalMilliseconds, 1500, 1600);
            await api.MessagesAsync(Private, null, null, 100, default);             // another channel has its own allowance
            Assert.Single(waits);

            var first = http.Requests[0];
            Assert.Equal("Bot", first.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", first.Headers.Authorization.Parameter);
            Assert.StartsWith("DiscordBot (", string.Join(" ", first.Headers.GetValues("User-Agent")));
            Assert.Equal($"https://discord.com/api/v10/channels/{General}/messages?limit=100", first.RequestUri!.ToString());
            Assert.EndsWith("&after=" + Snow(T2021), http.Requests[1].RequestUri!.ToString());
        }

        [Fact]
        public async Task Api_WaitsOutA429_AndRetriesDiscordsOwnErrors()
        {
            var (api, http, waits) = NewApi();
            using var owned = api;
            http.Answers.Enqueue(_ => Answer((HttpStatusCode)429, "{\"message\":\"You are being rate limited.\",\"retry_after\":2.0,\"global\":false}", ("Retry-After", "2")));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.OK, "[{\"id\":\"1\"}]"));
            Assert.Single(await api.MessagesAsync(General, null, null, 100, default));
            Assert.InRange(Assert.Single(waits).TotalMilliseconds, 2000, 2100);

            waits.Clear();
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.InternalServerError, "oops"));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.OK, "{\"id\":\"" + General + "\",\"type\":0}"));
            Assert.Equal(General, (await api.ChannelAsync(General, default)).GetProperty("id").GetString());
            Assert.Equal(2, waits.Count);

            // A wait too long to sit through is handed back to be retried later.
            http.Answers.Enqueue(_ => Answer((HttpStatusCode)429, "{\"retry_after\":600,\"global\":true}", ("Retry-After", "600"), ("X-RateLimit-Global", "true")));
            var limited = await Assert.ThrowsAsync<DiscordApiException>(() => api.MessagesAsync(Private, null, null, 100, default));
            Assert.Equal(DiscordApiFailure.RateLimited, limited.Failure);
            Assert.True(limited.IsTransient);
        }

        [Fact]
        public async Task Api_SaysWhatKindOfRefusalItGot()
        {
            var (api, http, _) = NewApi();
            using var owned = api;
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.Unauthorized, "{\"message\":\"401: Unauthorized\",\"code\":0}"));
            var refused = await Assert.ThrowsAsync<DiscordApiException>(() => api.MessagesAsync(General, null, null, 100, default));
            Assert.Equal(DiscordApiFailure.Unauthorised, refused.Failure);
            Assert.False(refused.IsTransient);
            Assert.DoesNotContain("test-token", refused.Message);

            http.Answers.Enqueue(_ => Answer(HttpStatusCode.Forbidden, "{\"message\":\"Missing Access\",\"code\":50001}"));
            var forbidden = await Assert.ThrowsAsync<DiscordApiException>(() => api.MessagesAsync(General, null, null, 100, default));
            Assert.Equal((DiscordApiFailure.Forbidden, (int?)50001), (forbidden.Failure, forbidden.DiscordCode));

            // A deleted message is an answer; a vanished channel is a failure.
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.NotFound, "{\"message\":\"Unknown Message\",\"code\":10008}"));
            Assert.Null(await api.MessageAsync(General, Snow(T2021), default));
            http.Answers.Enqueue(_ => Answer(HttpStatusCode.NotFound, "{\"message\":\"Unknown Channel\",\"code\":10003}"));
            Assert.Equal(DiscordApiFailure.NotFound, (await Assert.ThrowsAsync<DiscordApiException>(() => api.MessageAsync(General, Snow(T2021), default))).Failure);

            // Ids go into the URL: only real ids are accepted.
            await Assert.ThrowsAsync<ArgumentException>(() => api.MessagesAsync("../users/@me", null, null, 100, default));
            await Assert.ThrowsAsync<ArgumentException>(() => api.MessagesAsync(General, "1 OR 1", null, 100, default));
        }
    }
}
