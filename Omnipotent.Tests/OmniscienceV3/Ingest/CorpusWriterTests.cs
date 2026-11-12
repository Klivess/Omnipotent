using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>The one write path into corpus.db: idempotence, versions, name intervals, source ranking. Synthetic data only.</summary>
    public class CorpusWriterTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_ingest_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;

        private static readonly long T0 = Instant.FromUtc(new DateTime(2024, 3, 10, 9, 0, 0, DateTimeKind.Utc));
        private const long Minute = 60_000, Day = 86_400_000;
        private static readonly IngestSource Import = new("import:test:1", "testnet", "import", "Test export", IngestSource.ImportRank);
        private static readonly IngestSource Platform = new("bot:test", "testnet", "bot", "Test bot", IngestSource.PlatformRank);
        private static readonly IngestConversation Room = new("testnet", "room-1", ConversationKinds.GroupDm, "Synthetic room");
        private static readonly IngestIdentity Ada = new("testnet", "u-ada", "ada", "Ada Example");
        private static readonly IngestIdentity Bo = new("testnet", "u-bo", "bo", "Bo Example");

        public async Task InitializeAsync() => stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private static IngestMessage Msg(string id, IngestIdentity author, long at, string? content, IngestConversation? room = null) => new()
        {
            PlatformMessageId = id, Conversation = room ?? Room, Author = author, SentAtMs = at, OrderKey = at, Content = content,
        };

        private Task<IngestStats> Write(IngestSource source, string sourceKind, params IngestMessage[] messages) =>
            stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, source, sourceKind, clock.NowMs);
                foreach (var m in messages) w.Message(m);
                return w.Complete();
            });

        private T One<T>(string sql, params (string, object?)[] args)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private long Changes(string kind) => One<long>("SELECT COUNT(*) FROM change_log WHERE kind=$k", ("$k", kind));

        [Fact]
        public async Task FirstSight_WritesMessagePeoplePlaceAndOneChangeRowEach()
        {
            var stats = await Write(Import, SourceKinds.Import, Msg("m1", Ada, T0, "hello"), Msg("m2", Bo, T0 + Minute, "hi Ada"));

            Assert.Equal(2, stats.Inserted);
            Assert.Equal(2, stats.NewIdentities);
            Assert.Equal(1, stats.NewConversations);
            Assert.Equal("hello", One<string>("SELECT content FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal(1L, One<long>("SELECT current_version FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal("import", One<string>("SELECT first_source_kind FROM messages WHERE message_id='testnet:m1'"));
            // The message's ingest sequence is its row in the change log, so derived modules can resume from it.
            Assert.Equal(One<long>("SELECT seq FROM change_log WHERE kind='message.insert' AND entity_id='testnet:m1'"),
                One<long>("SELECT ingest_seq FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal(2L, Changes(ChangeKinds.MessageInsert));
            Assert.Equal(2L, Changes(ChangeKinds.PersonCreate));
            Assert.Equal(2L, Changes(ChangeKinds.IdentityUpsert));
            Assert.Equal(1L, Changes(ChangeKinds.ConversationUpsert));
            // Nothing imported is ever tagged live (plan T5: imports never raise alerts).
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM change_log WHERE source_kind <> 'import'"));
            Assert.Equal(T0 + Minute, One<long>("SELECT last_message_ms FROM conversations WHERE conversation_id='testnet:room-1'"));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM conversation_members WHERE conversation_id='testnet:room-1'"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'hello'"));

            // The person id is a function of the identity, and the person is its own canonical.
            string person = CorpusWriter.PersonIdFor("testnet:u-ada");
            Assert.Equal(person, One<string>("SELECT person_id FROM identities WHERE identity_id='testnet:u-ada'"));
            Assert.Equal(person, One<string>("SELECT canonical_id FROM person_canonical WHERE person_id=$p", ("$p", person)));
            Assert.Equal(person, One<string>("SELECT person_id FROM change_log WHERE kind='message.insert' AND entity_id='testnet:m1'"));
        }

        [Fact]
        public async Task SameInputAgain_ChangesNothing()
        {
            var batch = new[] { Msg("m1", Ada, T0, "hello"), Msg("m2", Bo, T0 + Minute, "hi") };
            await Write(Import, SourceKinds.Import, batch);
            long log = One<long>("SELECT COUNT(*) FROM change_log");

            var again = await Write(Import, SourceKinds.Import, batch);

            Assert.Equal(0, again.Inserted);
            Assert.Equal(0, again.Updated);
            Assert.Equal(2, again.Unchanged);
            Assert.Equal(log, One<long>("SELECT COUNT(*) FROM change_log"));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM message_versions"));
        }

        [Fact]
        public async Task Edit_BecomesANewVersion_AndTheOldTextIsKept()
        {
            await Write(Platform, SourceKinds.Live, Msg("m1", Ada, T0, "I'm free saturday"));
            var stats = await Write(Platform, SourceKinds.Live, Msg("m1", Ada, T0, "I'm free sunday") with { EditedAtMs = T0 + 5 * Minute });

            Assert.Equal(1, stats.Updated);
            Assert.Equal("I'm free sunday", One<string>("SELECT content FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal(2L, One<long>("SELECT current_version FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal("I'm free saturday", One<string>("SELECT content FROM message_versions WHERE message_id='testnet:m1' AND version=1"));
            Assert.Equal(T0 + 5 * Minute, One<long>("SELECT edited_at_ms FROM message_versions WHERE message_id='testnet:m1' AND version=2"));
            Assert.Equal(1L, Changes(ChangeKinds.MessageVersion));

            // An older edit arriving late (an export taken before the last edit) does not roll the message back.
            var stale = await Write(Import, SourceKinds.Import, Msg("m1", Ada, T0, "I'm free friday") with { EditedAtMs = T0 + 2 * Minute });
            Assert.Equal(1, stale.Unchanged);
            Assert.Equal("I'm free sunday", One<string>("SELECT content FROM messages WHERE message_id='testnet:m1'"));
        }

        [Fact]
        public async Task ConflictingCopiesWithoutEditTimes_AreSettledBySourceRank()
        {
            await Write(Import, SourceKinds.Import, Msg("m1", Ada, T0, "from the export"));

            // An equal-ranked source does not replace what is there (no first-writer-wins flip-flop between imports).
            var same = await Write(Import with { SourceId = "import:test:2" }, SourceKinds.Import, Msg("m1", Ada, T0, "from another export"));
            Assert.Equal(1, same.Unchanged);

            // The platform itself outranks an export.
            var higher = await Write(Platform, SourceKinds.Backfill, Msg("m1", Ada, T0, "from the platform"));
            Assert.Equal(1, higher.Updated);
            Assert.Equal("from the platform", One<string>("SELECT content FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal("bot:test", One<string>("SELECT best_source_id FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal("import", One<string>("SELECT first_source_kind FROM messages WHERE message_id='testnet:m1'"));

            // And an export arriving after that cannot take it back.
            var lower = await Write(Import, SourceKinds.Import, Msg("m1", Ada, T0, "from the export"));
            Assert.Equal(1, lower.Unchanged);
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM message_versions WHERE message_id='testnet:m1'"));
        }

        [Fact]
        public async Task Deletion_MarksTheMessage_AndNeverBlanksItsText()
        {
            await Write(Platform, SourceKinds.Live, Msg("m1", Ada, T0, "said then unsaid"));
            var stats = await Write(Platform, SourceKinds.Live, Msg("m1", Ada, T0, null) with { DeletedAtMs = T0 + Day });

            Assert.Equal(1, stats.Updated);
            Assert.Equal(T0 + Day, One<long>("SELECT deleted_at_ms FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal("said then unsaid", One<string>("SELECT content FROM messages WHERE message_id='testnet:m1'"));
            Assert.Equal(1L, Changes(ChangeKinds.MessageDelete));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM message_versions WHERE message_id='testnet:m1'"));
        }

        [Fact]
        public async Task Names_AreIntervalsFromWhenTheyWereSeen_InAnyArrivalOrder()
        {
            // Live: "Ada" in March, renamed "Ada L" in May.
            await Write(Platform, SourceKinds.Live, Msg("m1", Ada with { DisplayName = "Ada" }, T0, "one"));
            await Write(Platform, SourceKinds.Live, Msg("m2", Ada with { DisplayName = "Ada L" }, T0 + 60 * Day, "two"));
            // A backfill then finds January history under an older name, and February under "Ada" again.
            await Write(Platform, SourceKinds.Backfill, Msg("m0", Ada with { DisplayName = "A." }, T0 - 60 * Day, "zero"));
            await Write(Platform, SourceKinds.Backfill, Msg("m00", Ada with { DisplayName = "A." }, T0 - 70 * Day, "earlier still"));

            using var c = stores.Corpus.OpenRead();
            var names = c.Query(null, "SELECT value, valid_from_ms, valid_to_ms FROM identity_names WHERE identity_id='testnet:u-ada' AND name_kind='display' ORDER BY valid_from_ms",
                r => (Value: r.GetString(0), From: r.GetInt64(1), To: r.GetInt64OrNull(2)));
            Assert.Equal(3, names.Count);
            Assert.Equal(("A.", T0 - 70 * Day, (long?)T0), names[0]);
            Assert.Equal(("Ada", T0, (long?)(T0 + 60 * Day)), names[1]);
            Assert.Equal(("Ada L", T0 + 60 * Day, (long?)null), names[2]);
            // "Who was 'Ada' in April?" has exactly one answer.
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM identity_names WHERE value='Ada' AND valid_from_ms <= $t AND (valid_to_ms IS NULL OR valid_to_ms > $t)", ("$t", T0 + 30 * Day)));
            // First and last seen cover the whole span.
            Assert.Equal(T0 - 70 * Day, c.Scalar<long>(null, "SELECT first_seen_ms FROM identities WHERE identity_id='testnet:u-ada'"));
            Assert.Equal(T0 + 60 * Day, c.Scalar<long>(null, "SELECT last_seen_ms FROM identities WHERE identity_id='testnet:u-ada'"));
        }

        [Fact]
        public async Task GuessedTimes_AreCorrected_PlatformTimesAreNot()
        {
            var guessed = Msg("w1", Ada, T0, "hello") with { Quality = TimeQuality.InferredZone };
            await Write(Import, SourceKinds.Import, guessed);

            // The same export re-imported with the zone fixed: an hour earlier.
            var corrected = await Write(Import, SourceKinds.Import, guessed with { SentAtMs = T0 - 60 * Minute });
            Assert.Equal(1, corrected.Updated);
            Assert.Equal(T0 - 60 * Minute, One<long>("SELECT sent_at_ms FROM messages WHERE message_id='testnet:w1'"));

            // A platform instant replaces a guess, and is then never replaced by one.
            await Write(Platform, SourceKinds.Backfill, guessed with { SentAtMs = T0 - 30 * Minute, Quality = TimeQuality.Exact });
            Assert.Equal(T0 - 30 * Minute, One<long>("SELECT sent_at_ms FROM messages WHERE message_id='testnet:w1'"));
            Assert.Equal((long)TimeQuality.Exact, One<long>("SELECT time_quality FROM messages WHERE message_id='testnet:w1'"));
            var ignored = await Write(Import, SourceKinds.Import, guessed with { SentAtMs = T0 + 600 * Minute });
            Assert.Equal(1, ignored.Unchanged);
            Assert.Equal(T0 - 30 * Minute, One<long>("SELECT sent_at_ms FROM messages WHERE message_id='testnet:w1'"));
        }

        [Fact]
        public async Task Attachments_AreAddedOnce_AndALaterCopyCanSupplyTheBytes()
        {
            var store = new MediaStore(stores.Paths);
            var link = new IngestAttachment("cat.png", SourceUrl: "https://cdn.example.invalid/cat.png", Unavailable: true);
            await Write(Import, SourceKinds.Import, Msg("m1", Ada, T0, "look") with { Attachments = new[] { link } });
            Assert.Equal(DownloadStates.Unavailable, One<string>("SELECT download_state FROM attachments WHERE attachment_id='testnet:m1#0'"));
            Assert.Equal("image", One<string>("SELECT media_kind FROM attachments WHERE attachment_id='testnet:m1#0'"));

            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            var stored = await store.PutAsync(new MemoryStream(png), "application/octet-stream", 1024);
            var stats = await Write(Import with { SourceId = "import:test:2" }, SourceKinds.Import,
                Msg("m1", Ada, T0, "look") with { Attachments = new[] { new IngestAttachment("cat.png") { Stored = stored } } });

            Assert.Equal(1, stats.AttachmentsStored);
            Assert.Equal(0, stats.AttachmentsAdded);
            Assert.Equal(DownloadStates.Done, One<string>("SELECT download_state FROM attachments WHERE attachment_id='testnet:m1#0'"));
            Assert.Equal(stored.Sha256, One<string>("SELECT cas_sha256 FROM attachments WHERE attachment_id='testnet:m1#0'"));
            // The type comes from the bytes, not from what the source claimed.
            Assert.Equal("image/png", One<string>("SELECT mime FROM cas_objects WHERE sha256=$s", ("$s", stored.Sha256)));
            Assert.Equal(1L, Changes(ChangeKinds.AttachmentAdd));
            Assert.Equal(1L, Changes(ChangeKinds.AttachmentStored));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM attachments"));
        }

        [Fact]
        public async Task Reactions_OnePerPersonAndEmoji_AndCountsWhenOnlyCountsAreKnown()
        {
            var withReactions = Msg("m1", Ada, T0, "news") with
            {
                Reactions = new[] { new IngestReaction("👍", "👍", Bo, T0 + Minute), new IngestReaction("🎉", "🎉", Count: 4) },
            };
            var first = await Write(Platform, SourceKinds.Live, withReactions);
            var second = await Write(Platform, SourceKinds.Live, withReactions);

            Assert.Equal(1, first.ReactionsAdded);
            Assert.Equal(0, second.ReactionsAdded);
            Assert.Equal(1, second.Unchanged);
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM reactions WHERE message_id='testnet:m1' AND identity_id='testnet:u-bo'"));
            Assert.Equal(4L, One<long>("SELECT count FROM reaction_counts WHERE message_id='testnet:m1' AND emoji_key='🎉'"));
            Assert.Equal(1L, Changes(ChangeKinds.ReactionChange));
            // Someone who only reacted is still a known identity.
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM identities WHERE identity_id='testnet:u-bo'"));
        }

        [Fact]
        public async Task UnusableRecords_AreQuarantinedWithAReason_AndTheRestGoesIn()
        {
            var stats = await Write(Import, SourceKinds.Import,
                Msg("ok", Ada, T0, "fine"),
                Msg("ancient", Ada, 0, "epoch zero"),
                Msg("", Ada, T0, "no id"),
                Msg("anon", Ada with { PlatformUserId = " " }, T0, "no author"));

            Assert.Equal(1, stats.Inserted);
            Assert.Equal(3, stats.Rejected);
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM quarantine WHERE reason='send time outside 2000–2100'"));
            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM quarantine"));
        }

        [Fact]
        public async Task SelfMembersBotsAndClockSkew()
        {
            var dm = new IngestConversation("testnet", "dm-1", ConversationKinds.Dm, "Bo Example") { Members = new[] { Bo } };
            long future = clock.NowMs + 90_000; // the platform's clock runs ahead of ours
            await Write(Platform, SourceKinds.Live,
                Msg("s1", Ada with { IsSelf = true }, T0, "to bo", dm),
                Msg("b1", new IngestIdentity("testnet", "u-bot", "helper", IsBot: true), T0 + Minute, "beep", dm),
                Msg("f1", Ada with { IsSelf = true }, future, "from the future", dm));

            Assert.Equal(1L, One<long>("SELECT is_self FROM persons WHERE person_id=$p", ("$p", CorpusWriter.PersonIdFor("testnet:u-ada"))));
            Assert.Equal(0L, One<long>("SELECT is_self FROM persons WHERE person_id=$p", ("$p", CorpusWriter.PersonIdFor("testnet:u-bo"))));
            // The other side of the DM is known and a member although they wrote nothing.
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM conversation_members WHERE conversation_id='testnet:dm-1' AND identity_id='testnet:u-bo'"));
            Assert.Equal("bot", One<string>("SELECT kind FROM identities WHERE identity_id='testnet:u-bot'"));
            Assert.Equal(1L, One<long>("SELECT is_bot FROM messages WHERE message_id='testnet:b1'"));
            // A message is never recorded as ingested before it was sent.
            Assert.Equal(future, One<long>("SELECT ingested_at_ms FROM messages WHERE message_id='testnet:f1'"));
            Assert.Equal(clock.NowMs, One<long>("SELECT ingested_at_ms FROM messages WHERE message_id='testnet:s1'"));
        }

        [Fact]
        public async Task MediaStore_StoresEachContentOnce_SniffsTheType_AndRefusesOversize()
        {
            var store = new MediaStore(stores.Paths);
            byte[] jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(Encoding.ASCII.GetBytes(new string('x', 5000))).ToArray();

            var a = await store.PutAsync(new MemoryStream(jpeg), "text/plain", 1 << 20);
            var b = await store.PutAsync(new MemoryStream(jpeg), null, 1 << 20);

            Assert.Equal(a.Sha256, b.Sha256);
            Assert.Equal(jpeg.Length, a.SizeBytes);
            Assert.Equal("image/jpeg", a.Mime);
            Assert.Equal($"{a.Sha256[..2]}/{a.Sha256[2..4]}/{a.Sha256}", a.RelativePath);
            Assert.True(File.Exists(store.FullPath(a.RelativePath)));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(stores.Paths.Media, a.Sha256[..2]), "*", SearchOption.AllDirectories));

            await Assert.ThrowsAsync<MediaTooLargeException>(() => store.PutAsync(new MemoryStream(jpeg), null, 1000));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(stores.Paths.Media, "tmp")));

            // A part file left by a crash is swept at the next start.
            File.WriteAllBytes(Path.Combine(stores.Paths.Media, "tmp", "orphan.part"), new byte[] { 1 });
            Assert.Equal(1, store.SweepTemp());
            Assert.Null(MediaSniffer.Detect(Encoding.ASCII.GetBytes("just some text")));
        }
    }
}
