using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Discord;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;
using static Omnipotent.Tests.OmniscienceV3.Ingest.DiscordFixtures;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>
    /// Reading Discord through KliveBot: history paging with its cursors, and the module end to end against a
    /// stand-in connection and a stand-in bot API (no network, no Discord library).
    /// </summary>
    public class DiscordBotTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_discordbot_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Now);
        private readonly FakeDiscord discord = new();
        private readonly FakeFeed feed = new();
        private readonly ConcurrentQueue<string> errors = new();
        private OmniscienceStores stores = null!;

        private static readonly long Now = Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        private static readonly long Old = Now - 2 * 86_400_000L;
        private static readonly IngestSource Source = new(DiscordBotModule.SourceId, "discord", "bot", "KliveBot", IngestSource.PlatformRank);
        private static readonly IngestSource Relay = new(DiscordBotModule.RelaySourceId, "discord", "bot", "KliveBot (as relayed)", IngestSource.RelayedRank);

        public async Task InitializeAsync() => stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private DiscordHistory History() => new(stores, clock, discord, () => Source, () => Reader, () => TimeSpan.FromMinutes(5));

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

        private string Post(long atMs, string text, int sequence = 0, string channel = General, string author = Sam, Action<JsonObject>? more = null)
        {
            string id = Snow(atMs, sequence);
            discord.Put(channel, Message(id, User(author, author == Sam ? "sam" : "alex", author == Sam ? "Sam W." : "Alex P."), text, channel, more));
            return id;
        }

        private static async Task Until(Func<bool> condition, string what, int timeoutMs = 15_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!condition())
            {
                if (Environment.TickCount64 > deadline) Assert.Fail("Timed out waiting for: " + what);
                await Task.Delay(20);
            }
        }

        // ── history ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task FirstRead_TakesTheLatestPage_AndTellsLiveFromHistory()
        {
            Post(Old, "two days ago");
            Post(Old + 1000, "also two days ago");
            string fresh = Post(Now - 10_000, "ten seconds ago");

            var outcome = await History().ForwardAsync(GeneralChannel, 5, default);

            Assert.Equal((1, 3, 3), (outcome.Pages, outcome.Messages, outcome.Stats.Inserted));
            var kinds = Rows("SELECT content, first_source_kind FROM messages ORDER BY sent_at_ms", r => (r.GetString(0), r.GetString(1)));
            Assert.Equal(new[] { ("two days ago", "backfill"), ("also two days ago", "backfill"), ("ten seconds ago", "live") }, kinds);
            // Only the fresh one is logged as live: nothing can raise an alert about the old ones.
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM change_log WHERE kind='message.insert' AND source_kind='live'"));

            var cursor = History().Cursor(General)!;
            Assert.Equal((Snow(Old), fresh, true, CursorStates.Ok), (cursor.Oldest, cursor.Newest, cursor.BackwardComplete, cursor.State));
            // Discord's display name came with the payload; the title and server are on the conversation.
            Assert.Equal("Sam W.", One<string>("SELECT value FROM identity_names WHERE name_kind='display'"));
            Assert.Equal(("general", "discord:" + Guild, "text"), Rows("SELECT title, guild_id, kind FROM conversations", r => (r.GetString(0), r.GetString(1), r.GetString(2))).Single());
            Assert.Equal("Test Server", One<string>("SELECT name FROM guilds"));
        }

        [Fact]
        public async Task ForwardRead_ClosesAGapOfAnySize_AndReadsNothingTwice()
        {
            for (int i = 0; i < 5; i++) Post(Old + i * 1000, "before " + i);
            await History().ForwardAsync(GeneralChannel, 5, default);

            // The service was down while 250 messages were posted.
            for (int i = 0; i < 250; i++) Post(Old + 3_600_000 + i * 1000, "missed " + i);
            var gap = await History().ForwardAsync(GeneralChannel, 25, default);

            Assert.Equal((3, 250, 250, true), (gap.Pages, gap.Messages, gap.Stats.Inserted, gap.Complete));
            Assert.Equal(255L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(250L, One<long>("SELECT COUNT(*) FROM messages WHERE first_source_kind='backfill' AND content LIKE 'missed %'"));

            int callsBefore = discord.Calls.Count;
            var again = await History().ForwardAsync(GeneralChannel, 25, default);
            Assert.Equal((1, 0, 0), (again.Pages, again.Messages, again.Stats.Inserted));
            Assert.Equal(callsBefore + 1, discord.Calls.Count);
            Assert.Contains("after=" + Snow(Old + 3_600_000 + 249 * 1000), discord.Calls.Last());

            // A read stopped part-way resumes from where it got to.
            for (int i = 0; i < 250; i++) Post(Old + 7_200_000 + i * 1000, "later " + i);
            var partial = await History().ForwardAsync(GeneralChannel, 1, default);
            Assert.Equal((1, 100, false), (partial.Pages, partial.Messages, partial.Complete));
            var rest = await History().ForwardAsync(GeneralChannel, 25, default);
            Assert.Equal((2, 150, true), (rest.Pages, rest.Messages, rest.Complete));
            Assert.Equal(505L, One<long>("SELECT COUNT(*) FROM messages"));
        }

        [Fact]
        public async Task BackwardRead_GoesToTheChannelsStart_AcrossSeveralRuns()
        {
            for (int i = 0; i < 350; i++) Post(Old + i * 1000, "message " + i);
            var history = History();

            int allowed = 1;
            var first = await history.BackwardAsync(GeneralChannel, () => allowed-- > 0, null, default);
            Assert.False(first.Complete);
            Assert.Equal(200L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.False(history.Cursor(General)!.BackwardComplete);

            var second = await history.BackwardAsync(GeneralChannel, () => true, null, default);
            Assert.True(second.Complete);
            Assert.Equal(350L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(350L, One<long>("SELECT COUNT(DISTINCT message_id) FROM messages"));
            var cursor = history.Cursor(General)!;
            Assert.Equal((Snow(Old), Snow(Old + 349 * 1000), true), (cursor.Oldest, cursor.Newest, cursor.BackwardComplete));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM change_log WHERE source_kind='live'"));

            // Finished: it does not ask Discord again.
            int calls = discord.Calls.Count;
            Assert.True((await history.BackwardAsync(GeneralChannel, () => true, null, default)).Complete);
            Assert.Equal(calls, discord.Calls.Count);

            // An empty channel is complete at once.
            var empty = await history.BackwardAsync(new DiscordChannelInfo(Private, ConversationKinds.Text, "empty", Guild), () => true, null, default);
            Assert.True(empty.Complete);
            Assert.True(history.Cursor(Private)!.BackwardComplete);
        }

        [Fact]
        public async Task AChannelThatRefuses_IsRecordedAndLeft_NotMarkedFinished()
        {
            Post(Old, "visible but not readable");
            discord.Forbidden[General] = 0;

            var outcome = await History().ForwardAsync(GeneralChannel, 5, default);
            Assert.Equal(CursorStates.Forbidden, outcome.Stopped);
            var cursor = History().Cursor(General)!;
            Assert.Equal(CursorStates.Forbidden, cursor.State);
            Assert.Contains("Missing Access", cursor.Detail);
            Assert.False(cursor.BackwardComplete);
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM messages"));

            // Access granted later: the same call now reads it, and the refusal is cleared.
            discord.Forbidden.Clear();
            Assert.Equal(1, (await History().ForwardAsync(GeneralChannel, 5, default)).Stats.Inserted);
            Assert.Equal(CursorStates.Ok, History().Cursor(General)!.State);

            discord.Gone[Private] = 0;
            var gone = await History().BackwardAsync(new DiscordChannelInfo(Private, ConversationKinds.Text, "deleted", Guild), () => true, null, default);
            Assert.Equal(CursorStates.Gone, gone.Stopped);

            // A refused token is not about one channel: it is raised for the caller to handle.
            discord.Unauthorised = true;
            var refused = await Assert.ThrowsAsync<DiscordApiException>(() => History().ForwardAsync(GeneralChannel, 5, default));
            Assert.Equal(DiscordApiFailure.Unauthorised, refused.Failure);
        }

        [Fact]
        public async Task AChangedMessageIsFetchedAgainAndMerged()
        {
            string id = Post(Old, "see you at 7");
            var history = History();
            await history.ForwardAsync(GeneralChannel, 5, default);

            // Edited on Discord.
            discord.Put(General, Message(id, User(Sam, "sam", "Sam W."), "see you at 8", more: m => m["edited_timestamp"] = "2025-06-01T11:59:00+00:00"));
            Assert.True(await history.RefetchAsync(GeneralChannel, id, default));
            var versions = Rows("SELECT version, content FROM message_versions ORDER BY version", r => (r.GetInt32(0), r.GetString(1)));
            Assert.Equal(new[] { (1, "see you at 7"), (2, "see you at 8") }, versions);
            // The edit happened now, so it is a live change even though the message is two days old.
            Assert.Equal("live", One<string>("SELECT source_kind FROM change_log WHERE kind='message.version'"));
            Assert.Contains("see you at 8", One<string>("SELECT raw_json FROM messages"));

            // A link preview that finished loading: Discord says "updated", nothing was edited.
            discord.Put(General, Message(id, User(Sam, "sam", "Sam W."), "see you at 8", more: m =>
            {
                m["edited_timestamp"] = "2025-06-01T11:59:00+00:00";
                m["embeds"] = new JsonArray(new JsonObject { ["type"] = "link", ["url"] = "https://example.com/menu", ["title"] = "Menu" });
            }));
            Assert.True(await history.RefetchAsync(GeneralChannel, id, default));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM message_versions"));
            Assert.Equal("Menu", One<string>("SELECT title FROM message_embeds"));

            // An update for a message never seen (it predates what has been read): stored, as history.
            string unseen = Post(Old - 86_400_000, "an old one, edited today", more: m => m["edited_timestamp"] = "2025-06-01T11:59:30+00:00");
            Assert.True(await history.RefetchAsync(GeneralChannel, unseen, default));
            Assert.Equal("backfill", One<string>("SELECT first_source_kind FROM messages WHERE platform_message_id=$m", ("$m", unseen)));

            // Deleted before it could be fetched.
            Assert.False(await history.RefetchAsync(GeneralChannel, Snow(Old, 77), default));
        }

        [Fact]
        public async Task WhatWasRelayed_IsBroughtUpToTheFullPayload_OnceHistoryCanBeRead()
        {
            string id = Snow(Now - 5000);
            var relayed = new IngestMessage
            {
                PlatformMessageId = id,
                Conversation = GeneralChannel.ToConversation(),
                Author = new IngestIdentity("discord", Sam, "sam"),
                SentAtMs = Now - 5000,
                OrderKey = long.Parse(id),
                Content = "relayed by the library",
            };
            await History().WriteRelayedAsync(new[] { relayed }, Relay, default);
            Assert.Equal((DiscordBotModule.RelaySourceId, "live"), Rows("SELECT best_source_id, first_source_kind FROM messages", r => (r.GetString(0), r.GetString(1))).Single());
            Assert.Null(History().Cursor(General));     // the cursor did not move: the channel still counts as unread

            discord.Put(General, Message(id, User(Sam, "sam", "Sam W."), "relayed by the library", more: m => m["type"] = 0));
            var outcome = await History().ForwardAsync(GeneralChannel, 5, default);
            Assert.Equal(1, outcome.Stats.Updated);
            Assert.Equal(DiscordBotModule.SourceId, One<string>("SELECT best_source_id FROM messages"));
            Assert.NotNull(One<string?>("SELECT raw_json FROM messages"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM message_versions"));
            Assert.Equal("Sam W.", One<string>("SELECT value FROM identity_names WHERE name_kind='display'"));
        }

        // ── the module, end to end ───────────────────────────────────────────────

        private async Task<(ModuleHost Host, DiscordBotModule Module)> StartAsync(bool enabled = true)
        {
            var lease = TimeSpan.FromSeconds(5);
            var poll = TimeSpan.FromMilliseconds(20);
            var host = new ModuleHost(stores, clock, new KernelOptions
            {
                StartLoop = false,
                Lanes = new[]
                {
                    new LaneOptions(Lane.Cpu, 1, lease, poll), new LaneOptions(Lane.Io, 2, lease, poll), new LaneOptions(Lane.LlmBackground, 1, lease, poll),
                    new LaneOptions(Lane.LlmInteractive, 1, lease, poll), new LaneOptions(Lane.Coordinator, 1, lease, poll),
                },
            }, _ => { }, (message, ex) => errors.Enqueue(message + " " + ex?.Message));
            var module = new DiscordBotModule(feed, () => Task.FromResult<string?>("unused"), Owner, discord);
            host.Register(module);
            await host.Settings.SetAsync(DiscordBotModule.EnabledSetting, enabled, "test");
            await host.StartAsync();
            if (enabled) await Until(() => feed.Sink != null, "the module to start listening");
            return (host, module);
        }

        [Fact]
        public async Task ADoorbellBringsInTheFullMessage_ThenItsEditItsReactionAndItsDeletion()
        {
            feed.ChannelList.Add(GeneralChannel);
            var (host, module) = await StartAsync();
            await using var owned = host;
            var sink = feed.Sink!;

            string id = Post(Now - 3000, "dinner at mine on friday?", more: m =>
                m["attachments"] = new JsonArray(new JsonObject { ["id"] = "600000000000000001", ["filename"] = "menu.png", ["content_type"] = "image/png", ["size"] = 2048,
                    ["url"] = "https://cdn.discordapp.com/attachments/1/2/menu.png", ["width"] = 640, ["height"] = 480 }));
            sink(new BotChannelSeen(GeneralChannel));
            sink(new BotMessageSignal(General, id, Edited: false));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 1, "the message to be stored");

            Assert.Equal(("dinner at mine on friday?", "live", DiscordBotModule.SourceId),
                Rows("SELECT content, first_source_kind, best_source_id FROM messages", r => (r.GetString(0), r.GetString(1), r.GetString(2))).Single());
            Assert.Contains("menu.png", One<string>("SELECT raw_json FROM messages"));
            Assert.Equal((640L, "pending"), Rows("SELECT width, download_state FROM attachments", r => (r.GetInt64(0), r.GetString(1))).Single());
            Assert.Equal("Sam W.", One<string>("SELECT value FROM identity_names WHERE name_kind='display'"));

            // Edited on Discord: the doorbell says which message, the API says what it is now.
            discord.Put(General, Message(id, User(Sam, "sam", "Sam W."), "dinner at mine on saturday?", more: m => m["edited_timestamp"] = "2025-06-01T12:00:00+00:00"));
            sink(new BotMessageSignal(General, id, Edited: true));
            await Until(() => One<long>("SELECT COUNT(*) FROM message_versions") == 2, "the edit to become a version");
            Assert.Equal("dinner at mine on saturday?", One<string>("SELECT content FROM messages"));

            var alex = new IngestIdentity("discord", Alex, "alex");
            sink(new BotReaction(BotReactionKind.Added, General, id, alex, "👍", "👍"));
            sink(new BotTyping(General, alex, Now));
            sink(new BotMessagesDeleted(General, new[] { id }));
            await Until(() => One<long?>("SELECT deleted_at_ms FROM messages") != null, "the deletion to be marked");
            Assert.Equal("discord:" + Alex, One<string>("SELECT identity_id FROM reactions"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM typing_events"));
            Assert.Equal("dinner at mine on saturday?", One<string>("SELECT content FROM messages"));     // deleted, not erased

            var status = module.Status();
            Assert.True(status is { Enabled: true, Attached: true, ContentIntent: true, SourceStatus: "active", Channels: 1 });
            Assert.Equal(Bot, status.BotUserId);
            Assert.Empty(errors);
        }

        [Fact]
        public async Task WhenTheConnectionComesBack_TheGapIsRead_AndHistoryIsQueued()
        {
            feed.ChannelList.Add(GeneralChannel);
            for (int i = 0; i < 3; i++) Post(Old + i * 1000, "before " + i);
            var (host, _) = await StartAsync();
            await using var owned = host;
            var sink = feed.Sink!;

            sink(new BotConnected("ready"));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 3, "the first read");
            await Until(() => One<long>("SELECT COALESCE(SUM(backward_complete),0) FROM ingest_cursors") == 1, "history to be read to the start");

            // Messages posted while nobody was listening: no doorbell ever rang for them.
            for (int i = 0; i < 4; i++) Post(Now - 3_600_000 + i * 1000, "while away " + i);
            sink(new BotConnected("resumed"));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 7, "the gap to be read");
            Assert.Equal(4L, One<long>("SELECT COUNT(*) FROM messages WHERE content LIKE 'while away %' AND first_source_kind='backfill'"));
            Assert.Empty(errors);
        }

        [Fact]
        public async Task OpenThreadsAreFoundAndRead_AndACleanStopWritesWhatWasHeard()
        {
            const string thread = "700000000000000003";
            feed.ChannelList.Add(GeneralChannel);
            discord.ActiveThreads.Add(El(new JsonObject { ["id"] = thread, ["type"] = 11, ["name"] = "release plans", ["guild_id"] = Guild, ["parent_id"] = General }));
            Post(Old, "in the channel");
            Post(Old + 1000, "in the thread", channel: thread);
            var (host, _) = await StartAsync();
            var sink = feed.Sink!;

            sink(new BotConnected("ready"));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 2, "the channel and its thread to be read");
            Assert.Equal(("thread", "discord:" + General, "release plans"),
                Rows("SELECT kind, parent_conversation_id, title FROM conversations WHERE conversation_id=$c", r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$c", "discord:" + thread)).Single());

            // Heard a moment before the service stops: still written.
            string id = One<string>("SELECT platform_message_id FROM messages WHERE content='in the thread'");
            sink(new BotMessagesDeleted(thread, new[] { id }));
            await host.DisposeAsync();
            Assert.NotNull(One<long?>("SELECT deleted_at_ms FROM messages WHERE content='in the thread'"));
            Assert.Empty(errors);
        }

        [Fact]
        public async Task AChannelWhoseHistoryIsRefused_KeepsWhatTheConnectionRelayed()
        {
            feed.ChannelList.Add(GeneralChannel);
            discord.Forbidden[General] = 0;
            var (host, module) = await StartAsync();
            await using var owned = host;
            var sink = feed.Sink!;

            IngestMessage Relayed(string id, string text) => new()
            {
                PlatformMessageId = id,
                Conversation = GeneralChannel.ToConversation(),
                Author = new IngestIdentity("discord", Sam, "sam"),
                SentAtMs = Instant.FromDiscordSnowflake(ulong.Parse(id)),
                OrderKey = long.Parse(id),
                Content = text,
            };
            string first = Snow(Now - 2000), second = Snow(Now - 1000);
            sink(new BotMessageSignal(General, first, false, Relayed(first, "first")));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 1, "the relayed message to be stored");
            Assert.Equal(DiscordBotModule.RelaySourceId, One<string>("SELECT best_source_id FROM messages"));

            // Now known to be refused: the next one is written without asking Discord again.
            int asked = discord.Calls.Count(c => c.StartsWith("messages "));
            sink(new BotMessageSignal(General, second, false, Relayed(second, "second")));
            await Until(() => One<long>("SELECT COUNT(*) FROM messages") == 2, "the second relayed message");
            Assert.Equal(asked, discord.Calls.Count(c => c.StartsWith("messages ")));
            Assert.Equal(1, module.Status().ChannelsForbidden);
        }

        [Fact]
        public async Task ARefusedToken_StopsReadingAndSaysSo()
        {
            feed.ChannelList.Add(GeneralChannel);
            discord.Unauthorised = true;
            var (host, module) = await StartAsync();
            await using var owned = host;

            feed.Sink!(new BotMessageSignal(General, Snow(Now - 1000), false));
            await Until(() => module.Status().SourceStatus == "unauthorised", "the source to be marked unauthorised");
            Assert.Equal("unauthorised", One<string>("SELECT status FROM sources WHERE source_id=$s", ("$s", DiscordBotModule.SourceId)));
            await Until(() => host.Jobs.List(JobStates.Dead, DiscordBotModule.ModuleId, 10).Count == 1, "the job to fail for good");
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM ingest_cursors"));     // no channel was marked finished or refused
        }

        [Fact]
        public async Task SwitchedOff_ItDoesNotListen()
        {
            feed.ChannelList.Add(GeneralChannel);
            Post(Now - 1000, "nobody is listening");
            var (host, module) = await StartAsync(enabled: false);
            await using var owned = host;

            await Task.Delay(300);
            Assert.Null(feed.Sink);
            Assert.False(module.Status().Attached);
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM messages"));
        }
    }
}
