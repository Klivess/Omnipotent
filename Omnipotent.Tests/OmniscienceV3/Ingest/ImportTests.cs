using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Ingest.Imports;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>
    /// Chat-export imports against synthetic exports written in each format's real layout (plan I-Importers).
    /// Every name, number and message here is made up: both repositories are public.
    /// </summary>
    public class ImportTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_import_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private ImportService service = null!;
        private string exports = null!;

        private static readonly string Lrm = ((char)0x200E).ToString();
        private static readonly string Nnbsp = ((char)0x202F).ToString();
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 1, 2, 3, 4, 5, 6 };
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6, 5 };

        public async Task InitializeAsync()
        {
            exports = Path.Combine(root, "exports");
            Directory.CreateDirectory(exports);
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            service = new ImportService(stores, clock, new MediaStore(stores.Paths), () => "Europe/London");
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static long Utc(int y, int mo, int d, int h, int mi, int s = 0) => Instant.FromUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc));

        private string Put(string relative, string text, Encoding? encoding = null)
        {
            string path = Path.Combine(exports, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
            return path;
        }

        private string PutBytes(string relative, byte[] bytes)
        {
            string path = Path.Combine(exports, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Json(string relative, object value) => Put(relative, JsonSerializer.Serialize(value));

        private T One<T>(string sql, params (string, object?)[] args)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<T>(null, sql, args)!;
        }

        private List<T> Many<T>(string sql, Func<SqliteDataReader, T> map)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Query(null, sql, map);
        }

        /// <summary>UTF-8 text as the Meta exports write it: each byte as one Latin-1 character.</summary>
        private static string Moji(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

        private static string Snowflake(long ms, int seq) => ((((ulong)(ms - Instant.DiscordEpochMs)) << 22) | (uint)seq).ToString();

        private const string AndroidChat =
            "25/12/2023, 09:15 - Messages and calls are end-to-end encrypted. No one outside of this chat, not even WhatsApp, can read or listen to them.\n" +
            "25/12/2023, 09:16 - Ada Example: Merry Christmas!\n" +
            "second line of the same message\n" +
            "25/12/2023, 09:17 - Klives Test: You too\n" +
            "25/12/2023, 09:18 - Ada Example: <Media omitted>\n" +
            "25/12/2023, 09:19 - Ada Example: This message was deleted\n" +
            "25/12/2023, 09:20 - Klives Test: fixed typo <This message was edited>\n" +
            "01/07/2024, 14:00 - Ada Example: summer now\n";

        // ── WhatsApp ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task WhatsApp_Android_PreviewThenCommit_AndASecondCommitChangesNothing()
        {
            string file = Put("android/WhatsApp Chat with Ada Example.txt", AndroidChat);
            var options = new ImportOptions { OwnerName = "Klives Test" };

            var preview = service.Preview(file, null, options);
            Assert.Equal("whatsapp", preview.Format);
            Assert.Null(preview.DecisionNeeded);
            Assert.Equal(7, preview.Messages);
            Assert.Equal(1, preview.SystemMessages);
            Assert.Equal("day-first", preview.DateOrder);
            Assert.Equal("Europe/London", preview.ZoneUsed);
            Assert.Equal(1, preview.MediaMissing);
            Assert.Equal(0, preview.AlreadyInCorpus);
            Assert.Null(preview.CommittedBeforeMs);
            // Winter is GMT, summer is BST: the same wall clock is a different offset.
            Assert.Equal(Utc(2023, 12, 25, 9, 15), preview.FirstMs);
            Assert.Equal(Utc(2024, 7, 1, 13, 0), preview.LastMs);
            Assert.Equal(2, preview.SenderCount);
            Assert.Equal(("Ada Example", 4, false), (preview.Senders[0].Name, preview.Senders[0].Messages, preview.Senders[0].IsOwner));
            Assert.Equal(("Klives Test", 2, true), (preview.Senders[1].Name, preview.Senders[1].Messages, preview.Senders[1].IsOwner));
            var conversation = Assert.Single(preview.Conversations);
            Assert.Equal(("Ada Example", ConversationKinds.Dm, 7), (conversation.Title, conversation.Kind, conversation.Messages));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM messages")); // a preview writes nothing

            var result = await service.CommitAsync(file, null, options);
            Assert.Equal(7, result.Inserted);
            Assert.Equal(0, result.Rejected);

            var rows = Many("SELECT content, sent_at_ms, time_quality, is_system, deleted_at_ms, flags, author_identity_id, first_source_kind FROM messages ORDER BY order_key",
                r => (Content: r.GetStringOrNull(0), Sent: r.GetInt64(1), Quality: r.GetInt32(2), System: r.GetInt32(3), Deleted: r.GetInt64OrNull(4), Flags: r.GetInt64(5),
                    Author: r.GetString(6), Kind: r.GetString(7)));
            Assert.Equal(7, rows.Count);
            Assert.All(rows, r => Assert.Equal((int)TimeQuality.InferredZone, r.Quality));
            Assert.All(rows, r => Assert.Equal("import", r.Kind));
            Assert.Equal((1, "whatsapp:system"), (rows[0].System, rows[0].Author));
            Assert.Equal("Merry Christmas!\nsecond line of the same message", rows[1].Content);
            Assert.Null(rows[3].Content); // "<Media omitted>" is an attachment, not something Ada said
            Assert.Null(rows[4].Content);
            Assert.Equal(Utc(2023, 12, 25, 9, 19), rows[4].Deleted);
            Assert.Equal("fixed typo", rows[5].Content);
            Assert.NotEqual(0, rows[5].Flags & IngestFlags.EditedAtUnknownTime);
            Assert.Equal(DownloadStates.Unavailable, One<string>("SELECT download_state FROM attachments"));
            Assert.Equal(1L, One<long>("SELECT p.is_self FROM persons p JOIN identities i ON i.person_id=p.person_id JOIN identity_names n ON n.identity_id=i.identity_id WHERE n.value='Klives Test'"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM change_log WHERE source_kind <> 'import'"));
            Assert.Equal("committed", One<string>("SELECT state FROM import_ledger"));

            long log = One<long>("SELECT COUNT(*) FROM change_log");
            var again = await service.CommitAsync(file, null, options);
            Assert.Equal((0, 0, 7), (again.Inserted, again.Updated, again.Unchanged));
            Assert.Equal(log, One<long>("SELECT COUNT(*) FROM change_log"));

            var after = service.Preview(file, null, options);
            Assert.Equal(7, after.AlreadyInCorpus);
            Assert.Equal(clock.NowMs, after.CommittedBeforeMs);
        }

        [Fact]
        public async Task WhatsApp_ALongerExportOfTheSameChat_AddsOnlyWhatIsNew_AndUtf16IsRead()
        {
            var options = new ImportOptions { OwnerName = "Klives Test" };
            await service.CommitAsync(Put("first/WhatsApp Chat with Ada Example.txt", AndroidChat), null, options);

            // Months later the chat is exported again, from a phone that writes UTF-16.
            string later = Put("second/WhatsApp Chat with Ada Example.txt", AndroidChat + "02/07/2024, 08:30 - Klives Test: new since last time\n", Encoding.Unicode);
            var result = await service.CommitAsync(later, null, options);

            Assert.Equal((1, 7), (result.Inserted, result.Unchanged));
            Assert.Equal(8L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM conversations"));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM import_ledger"));
        }

        [Fact]
        public async Task WhatsApp_Ios_BracketsSecondsAmPm_MediaFromTheZip_AndAppLinesAreNotPeople()
        {
            string dir = Path.Combine(exports, "ios");
            Put("ios/_chat.txt",
                $"[2/14/24, 10:30:05{Nnbsp}AM] Synthetic Group: {Lrm}Messages and calls are end-to-end encrypted. Only people in this chat can read them.\n" +
                $"[2/14/24, 10:30:59{Nnbsp}AM] Ada Example: morning all\n" +
                $"[2/14/24, 10:31:00{Nnbsp}AM] Bo Example: {Lrm}<attached: 00000003-PHOTO-2024-02-14-10-31-00.jpg>\n" +
                $"[2/14/24, 1:05:09{Nnbsp}PM] +44 7700 900123: lunch?\n" +
                $"[2/14/24, 1:06:00{Nnbsp}PM] Cy Example: {Lrm}<attached: 00000009-VIDEO-missing.mp4>\n" +
                $"[2/14/24, 1:07:00{Nnbsp}PM] Cy Example: {Lrm}image omitted\n" +
                $"[2/14/24, 1:08:00{Nnbsp}PM] Ada Example: {Lrm}Ada Example changed the group description\n" +
                $"[2/14/24, 12:00:00{Nnbsp}AM] Bo Example: midnight is twelve AM\n");
            PutBytes("ios/00000003-PHOTO-2024-02-14-10-31-00.jpg", Jpeg);
            string zip = Path.Combine(exports, "WhatsApp Chat - Synthetic Group.zip");
            ZipFile.CreateFromDirectory(dir, zip);

            var preview = service.Preview(zip, null, new ImportOptions());
            Assert.Equal("whatsapp", preview.Format);
            Assert.Equal("month-first", preview.DateOrder);
            Assert.Equal(8, preview.Messages);
            Assert.Equal(2, preview.SystemMessages);
            Assert.Equal((1, 2), (preview.MediaFound, preview.MediaMissing));
            Assert.Equal(4, preview.SenderCount); // Ada, Bo, Cy and the phone number — not "Synthetic Group"
            Assert.Equal(("Synthetic Group", ConversationKinds.GroupDm), (preview.Conversations[0].Title, preview.Conversations[0].Kind));

            var result = await service.CommitAsync(zip, null, new ImportOptions());
            Assert.Equal(8, result.Inserted);
            Assert.Equal(1, result.AttachmentsStored);

            Assert.Equal(Utc(2024, 2, 14, 10, 30, 59), One<long>("SELECT sent_at_ms FROM messages WHERE content='morning all'"));
            Assert.Equal(Utc(2024, 2, 14, 13, 5, 9), One<long>("SELECT sent_at_ms FROM messages WHERE content='lunch?'"));
            Assert.Equal(Utc(2024, 2, 14, 0, 0, 0), One<long>("SELECT sent_at_ms FROM messages WHERE content='midnight is twelve AM'"));
            Assert.Equal("whatsapp:+447700900123", One<string>("SELECT author_identity_id FROM messages WHERE content='lunch?'"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM identity_names WHERE value='Synthetic Group'"));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM messages WHERE is_system=1 AND author_identity_id='whatsapp:system'"));

            var attachments = Many("SELECT filename, download_state, media_kind, mime, cas_sha256 FROM attachments ORDER BY created_at_ms",
                r => (Name: r.GetStringOrNull(0), State: r.GetString(1), Kind: r.GetString(2), Mime: r.GetStringOrNull(3), Cas: r.GetStringOrNull(4)));
            Assert.Equal(3, attachments.Count);
            Assert.Equal((DownloadStates.Done, "image", "image/jpeg"), (attachments[0].State, attachments[0].Kind, attachments[0].Mime));
            Assert.True(File.Exists(new MediaStore(stores.Paths).FullPath(MediaStore.RelativePathFor(attachments[0].Cas!))));
            Assert.Equal((DownloadStates.Unavailable, "video"), (attachments[1].State, attachments[1].Kind));
            Assert.Equal((DownloadStates.Unavailable, "image", (string?)null), (attachments[2].State, attachments[2].Kind, attachments[2].Name));
        }

        [Fact]
        public async Task WhatsApp_AmbiguousDates_NeedADecision_AndRecommittingWithTheOtherOrderCorrectsTheSameRows()
        {
            string file = Put("ambiguous/WhatsApp Chat with Ada Example.txt",
                "03/04/2021, 10:00 - Ada Example: first\n05/06/2021, 11:00 - Klives Test: second\n");

            var preview = service.Preview(file, null, new ImportOptions());
            Assert.Equal("ambiguous", preview.DateOrder);
            Assert.Contains("day or the month", preview.DecisionNeeded);
            Assert.Equal(0, preview.Messages);

            await Assert.ThrowsAsync<ImportDecisionNeededException>(() => service.CommitAsync(file, null, new ImportOptions()));
            Assert.Equal("needs_decision", One<string>("SELECT state FROM import_ledger"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM messages"));

            // Read as month-first: 4 March (GMT) and 6 May (BST).
            var american = await service.CommitAsync(file, null, new ImportOptions { DayFirst = false });
            Assert.Equal(2, american.Inserted);
            Assert.Equal(Utc(2021, 3, 4, 10, 0), One<long>("SELECT sent_at_ms FROM messages WHERE content='first'"));
            Assert.Equal(Utc(2021, 5, 6, 10, 0), One<long>("SELECT sent_at_ms FROM messages WHERE content='second'"));

            // That was wrong. Day-first: 3 April (BST) and 5 June (BST). Same messages, corrected in place.
            var british = await service.CommitAsync(file, null, new ImportOptions { DayFirst = true });
            Assert.Equal((0, 2), (british.Inserted, british.Updated));
            Assert.Equal(2L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal(Utc(2021, 4, 3, 9, 0), One<long>("SELECT sent_at_ms FROM messages WHERE content='first'"));
            Assert.Equal(Utc(2021, 6, 5, 10, 0), One<long>("SELECT sent_at_ms FROM messages WHERE content='second'"));
            Assert.Equal("committed", One<string>("SELECT state FROM import_ledger"));
        }

        [Fact]
        public async Task WhatsApp_ClockChanges_AreResolvedByMessageOrder_AndCounted()
        {
            string file = Put("dst/WhatsApp Chat with Ada Example.txt",
                "29/10/2023, 01:30 - Ada Example: before the clocks change\n" +
                "29/10/2023, 01:45 - Ada Example: still summer time\n" +
                "29/10/2023, 01:10 - Ada Example: clocks went back\n" +
                "31/03/2024, 01:30 - Ada Example: this hour does not exist\n");

            var preview = service.Preview(file, null, new ImportOptions());
            Assert.Equal((3, 1), (preview.AmbiguousTimes, preview.InvalidTimes));

            await service.CommitAsync(file, null, new ImportOptions());
            var sent = Many("SELECT sent_at_ms FROM messages ORDER BY order_key", r => r.GetInt64(0));
            // 01:30 and 01:45 BST, then 01:10 can only be GMT (after the change) because the export is in order.
            Assert.Equal(new[] { Utc(2023, 10, 29, 0, 30), Utc(2023, 10, 29, 0, 45), Utc(2023, 10, 29, 1, 10), Utc(2024, 3, 31, 1, 30) }, sent);
            Assert.True(sent.SequenceEqual(sent.OrderBy(x => x)));
        }

        [Fact]
        public async Task WhatsApp_AChatLogPastedIntoAMessage_StaysTextOfThatMessage()
        {
            string file = Put("pasted/WhatsApp Chat with Ada Example.txt",
                "25/12/2023, 09:16 - Ada Example: look what I found in my old chats:\n" +
                "01/02/2019, 10:00 - Someone Else: an old line\n" +
                "01/02/2019, 10:01 - Someone Else: another old line\n" +
                "25/12/2023, 09:20 - Klives Test: ha\n");

            var result = await service.CommitAsync(file, null, new ImportOptions());

            Assert.Equal(2, result.Inserted);
            Assert.Contains("01/02/2019, 10:00 - Someone Else: an old line", One<string>("SELECT content FROM messages ORDER BY order_key LIMIT 1"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM identity_names WHERE value='Someone Else'"));
        }

        // ── Telegram ─────────────────────────────────────────────────────────────

        private static object TelegramMessage(int id, long unix, string from, string fromId, object text, Dictionary<string, object>? extra = null)
        {
            var m = new Dictionary<string, object>
            {
                ["id"] = id, ["type"] = "message", ["date"] = Instant.ToUtcDateTime(unix * 1000).ToString("yyyy-MM-dd'T'HH:mm:ss"),
                ["date_unixtime"] = unix.ToString(), ["from"] = from, ["from_id"] = fromId, ["text"] = text,
            };
            foreach (var (k, v) in extra ?? new Dictionary<string, object>()) m[k] = v;
            return m;
        }

        [Fact]
        public async Task Telegram_FullExport_IsStreamed_WithOwnerRepliesMediaReactionsAndServiceMessages()
        {
            const long t = 1710061200; // 2024-03-10T09:00:00Z
            Json("telegram/result.json", new Dictionary<string, object>
            {
                ["about"] = "synthetic export",
                ["personal_information"] = new { user_id = 1001, first_name = "Klives", last_name = "Test" },
                ["contacts"] = new { about = "x", list = new[] { new { first_name = "Ada", last_name = "Example", phone_number = "000" } } },
                ["chats"] = new
                {
                    about = "x",
                    list = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "Ada Example", ["type"] = "personal_chat", ["id"] = 2002,
                            ["messages"] = new[]
                            {
                                TelegramMessage(1, t, "Ada Example", "user2002", "plain"),
                                TelegramMessage(2, t + 60, "Klives Test", "user1001", new object[] { "see ", new { type = "link", text = "https://example.invalid" }, " ok" },
                                    new() { ["edited"] = "2024-03-10T09:05:00", ["edited_unixtime"] = (t + 300).ToString(), ["reply_to_message_id"] = 1 }),
                                TelegramMessage(3, t + 120, "Ada Example", "user2002", "", new() { ["photo"] = "chats/chat_01/photos/photo_1.jpg", ["width"] = 4, ["height"] = 3 }),
                                TelegramMessage(4, t + 180, "Ada Example", "user2002", "", new()
                                {
                                    ["file"] = "(File not included. Change data exporting settings to download.)", ["media_type"] = "voice_message",
                                    ["mime_type"] = "audio/ogg", ["duration_seconds"] = 7,
                                    ["reactions"] = new[] { new { type = "emoji", count = 1, emoji = "👍", recent = new[] { new { from = "Klives Test", from_id = "user1001", date = "2024-03-10T09:04:00" } } } },
                                }),
                                new Dictionary<string, object>
                                {
                                    ["id"] = 5, ["type"] = "service", ["date"] = "2024-03-10T09:04:00", ["date_unixtime"] = (t + 240).ToString(),
                                    ["actor"] = "Ada Example", ["actor_id"] = "user2002", ["action"] = "pin_message", ["message_id"] = 1, ["text"] = "",
                                },
                            },
                        },
                        new Dictionary<string, object>
                        {
                            ["name"] = "Synthetic Channel", ["type"] = "public_channel", ["id"] = 3003,
                            ["messages"] = new[] { TelegramMessage(1, t + 86400, "Synthetic Channel", "channel3003", "broadcast", new() { ["forwarded_from"] = "Bo Example" }) },
                        },
                    },
                },
            });
            PutBytes("telegram/chats/chat_01/photos/photo_1.jpg", Png);
            string dir = Path.Combine(exports, "telegram");

            var preview = service.Preview(dir, null, new ImportOptions());
            Assert.Equal("telegram", preview.Format);
            Assert.Equal((6, 1), (preview.Messages, preview.SystemMessages));
            Assert.Equal("user1001", preview.OwnerDetected);
            Assert.Equal(2, preview.Conversations.Count);
            Assert.Equal((1, 1), (preview.MediaFound, preview.MediaMissing));

            var result = await service.CommitAsync(dir, null, new ImportOptions());
            Assert.Equal(6, result.Inserted);
            Assert.Equal(1, result.AttachmentsStored);

            Assert.Equal("see https://example.invalid ok", One<string>("SELECT content FROM messages WHERE message_id='telegram:2002/2'"));
            Assert.Equal((t + 300) * 1000, One<long>("SELECT edited_at_ms FROM messages WHERE message_id='telegram:2002/2'"));
            Assert.Equal("telegram:2002/1", One<string>("SELECT reply_to_message_id FROM messages WHERE message_id='telegram:2002/2'"));
            Assert.Equal((long)TimeQuality.Second, One<long>("SELECT time_quality FROM messages WHERE message_id='telegram:2002/1'"));
            Assert.Equal(t * 1000, One<long>("SELECT sent_at_ms FROM messages WHERE message_id='telegram:2002/1'"));
            Assert.Equal(ConversationKinds.Dm, One<string>("SELECT kind FROM conversations WHERE conversation_id='telegram:2002'"));
            Assert.Equal(ConversationKinds.Channel, One<string>("SELECT kind FROM conversations WHERE conversation_id='telegram:3003'"));
            Assert.Equal(1L, One<long>("SELECT p.is_self FROM persons p JOIN identities i ON i.person_id=p.person_id WHERE i.identity_id='telegram:user1001'"));
            Assert.Equal(0L, One<long>("SELECT p.is_self FROM persons p JOIN identities i ON i.person_id=p.person_id WHERE i.identity_id='telegram:user2002'"));
            Assert.Equal((DownloadStates.Done, 4L), Many("SELECT download_state, width FROM attachments WHERE message_id='telegram:2002/3'", r => (r.GetString(0), r.GetInt64(1))).Single());
            Assert.Equal((DownloadStates.Unavailable, "voice", 7000L),
                Many("SELECT download_state, media_kind, duration_ms FROM attachments WHERE message_id='telegram:2002/4'", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))).Single());
            // The reaction's own time is a wall clock: read in the chosen zone (GMT in March).
            Assert.Equal(Utc(2024, 3, 10, 9, 4), One<long>("SELECT added_at_ms FROM reactions WHERE message_id='telegram:2002/4' AND identity_id='telegram:user1001'"));
            Assert.Equal(1L, One<long>("SELECT count FROM reaction_counts WHERE message_id='telegram:2002/4'"));
            Assert.Equal((1L, "[pin_message]"), Many("SELECT is_system, content FROM messages WHERE message_id='telegram:2002/5'", r => (r.GetInt64(0), r.GetString(1))).Single());
            Assert.Contains("Bo Example", One<string>("SELECT forwarded_from_json FROM messages WHERE message_id='telegram:3003/1'"));
            Assert.NotNull(One<string>("SELECT raw_json FROM messages WHERE message_id='telegram:2002/1'"));

            Assert.Equal(6, (await service.CommitAsync(dir, null, new ImportOptions())).Unchanged);
        }

        [Fact]
        public void Telegram_WithoutUnixTimes_NeedsAZone()
        {
            string file = Json("oldtelegram/result.json", new
            {
                name = "Ada Example", type = "personal_chat", id = 7,
                messages = new[] { new { id = 1, type = "message", date = "2019-07-01T12:00:00", from = "Ada Example", from_id = "user7", text = "old export" } },
            });
            using var input = ImportInput.Open(file);
            var format = new TelegramImport();

            Assert.Throws<ImportDecisionNeededException>(() => format.Read(input, new ImportOptions(), new ImportDiagnostics()).ToList());

            var diagnostics = new ImportDiagnostics();
            var message = Assert.Single(format.Read(input, new ImportOptions { Zone = "Australia/Sydney" }, diagnostics).ToList());
            // Noon in Sydney in July (AEST, UTC+10) is 02:00 UTC.
            Assert.Equal(Utc(2019, 7, 1, 2, 0), message.SentAtMs);
            Assert.Equal(TimeQuality.InferredZone, message.Quality);
            Assert.Equal("Australia/Sydney", diagnostics.ZoneUsed);
        }

        // ── Instagram / Messenger ────────────────────────────────────────────────

        [Fact]
        public async Task Instagram_RepairsDoubleEncodedText_OrdersNewestFirstFiles_AndScopesNamesToTheirThread()
        {
            const long t = 1_710_061_200_000;
            string ada = Moji("Ada Éxample");
            string thread = "ig/your_instagram_activity/messages/inbox/adaexample_111";
            Json($"{thread}/message_2.json", new
            {
                participants = new[] { new { name = ada }, new { name = "Klives Test" } },
                messages = new object[]
                {
                    new { sender_name = ada, timestamp_ms = t + 1000, content = Moji("olá 😂") },
                    new { sender_name = "Klives Test", timestamp_ms = t, content = "first" },
                },
                title = ada, thread_path = "inbox/adaexample_111",
            });
            Json($"{thread}/message_1.json", new
            {
                participants = new[] { new { name = ada }, new { name = "Klives Test" } },
                messages = new object[]
                {
                    new { sender_name = "Klives Test", timestamp_ms = t + 5000, content = "Liked a message" },
                    new { sender_name = ada, timestamp_ms = t + 4000, is_unsent = true },
                    new { sender_name = ada, timestamp_ms = t + 3000, content = "Ada sent an attachment.", share = new { link = "https://example.invalid/p/1", share_text = "a post" } },
                    new
                    {
                        sender_name = "Klives Test", timestamp_ms = t + 2000, content = "pic",
                        photos = new[] { new { uri = "your_instagram_activity/messages/inbox/adaexample_111/photos/1.jpg", creation_timestamp = 1710061202 } },
                        reactions = new[] { new { reaction = Moji("❤"), actor = ada } },
                    },
                },
                title = ada, thread_path = "inbox/adaexample_111",
            });
            PutBytes($"{thread}/photos/1.jpg", Jpeg);
            Json("ig/your_instagram_activity/messages/inbox/group_222/message_1.json", new
            {
                participants = new[] { new { name = "Bo Example" }, new { name = ada }, new { name = "Klives Test" } },
                messages = new object[] { new { sender_name = ada, timestamp_ms = t + 9000, content = "hey both" } },
                title = "Synthetic trio", thread_path = "inbox/group_222",
            });
            Json("ig/your_instagram_activity/messages/inbox/bo_444/message_1.json", new
            {
                participants = new[] { new { name = "Bo Example" }, new { name = "Klives Test" } },
                messages = new object[] { new { sender_name = "Bo Example", timestamp_ms = t + 9500, content = "just us" } },
                title = "Bo Example", thread_path = "inbox/bo_444",
            });
            Put("ig/your_instagram_activity/messages/inbox/broken_333/message_1.json", "{ \"participants\": [ {\"name\": \"Klives Test\"");
            string dir = Path.Combine(exports, "ig");

            var preview = service.Preview(dir, null, new ImportOptions());
            Assert.Equal("instagram", preview.Format);
            Assert.Equal(8, preview.Messages);
            Assert.Equal("Klives Test", preview.OwnerDetected); // the one name in every readable thread
            Assert.True(preview.RepairedText > 0);
            Assert.Equal("broken_333/message_1.json", Assert.Single(preview.UnreadableFiles).File.Split("inbox/")[1]);

            var result = await service.CommitAsync(dir, null, new ImportOptions());
            Assert.Equal(8, result.Inserted); // the damaged thread costs only itself
            Assert.Single(result.UnreadableFiles);

            var texts = Many("SELECT content FROM messages WHERE conversation_id LIKE 'instagram:thread:%' AND content IS NOT NULL ORDER BY sent_at_ms", r => r.GetString(0));
            Assert.Equal(new[] { "first", "olá 😂", "pic", "Ada sent an attachment.", "Liked a message", "hey both", "just us" }, texts);
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM messages WHERE deleted_at_ms IS NOT NULL AND content IS NULL"));
            Assert.Equal(1L, One<long>("SELECT is_system FROM messages WHERE content='Liked a message'"));
            Assert.Equal("https://example.invalid/p/1", One<string>("SELECT url FROM message_embeds"));
            Assert.Equal(DownloadStates.Done, One<string>("SELECT download_state FROM attachments"));
            Assert.Equal("❤", One<string>("SELECT emoji_key FROM reactions"));
            Assert.Equal((long)TimeQuality.Exact, One<long>("SELECT DISTINCT time_quality FROM messages"));
            Assert.Equal(ConversationKinds.GroupDm, One<string>("SELECT kind FROM conversations WHERE title='Synthetic trio'"));

            // The same display name in two threads is two identities until Klives links them; the owner is one.
            Assert.Equal(2L, One<long>("SELECT COUNT(DISTINCT identity_id) FROM identity_names WHERE value='Ada Éxample'"));
            Assert.Equal(1L, One<long>("SELECT COUNT(DISTINCT identity_id) FROM identity_names WHERE value='Klives Test'"));
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM persons WHERE is_self=1"));
            // Bo never wrote, but is a member of the group.
            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM conversation_members m JOIN conversations c ON c.conversation_id=m.conversation_id WHERE c.title='Synthetic trio'"));

            Assert.Equal(8, (await service.CommitAsync(dir, null, new ImportOptions())).Unchanged);
        }

        [Fact]
        public void Messenger_IsToldApartFromInstagram_ByItsThreadType_AndGenuineLatin1IsLeftAlone()
        {
            Json("fb/messages/inbox/thread_1/message_1.json", new
            {
                participants = new[] { new { name = "Ada Example" }, new { name = "Klives Test" } },
                messages = new[] { new { sender_name = "Ada Example", timestamp_ms = 1_710_061_200_000, content = "hi" } },
                title = "Ada Example", thread_path = "inbox/thread_1", thread_type = "Regular",
            });
            using var input = ImportInput.Open(Path.Combine(exports, "fb"));
            Assert.Equal("messenger", ImportService.Detect(input, null).Id);

            Assert.Equal("café", TextDecoding.RepairMojibake("café", out bool repaired));
            Assert.False(repaired);
            Assert.Equal("日本語", TextDecoding.RepairMojibake("日本語", out repaired));
            Assert.False(repaired);
            Assert.Equal("naïve 😀", TextDecoding.RepairMojibake(Moji("naïve 😀"), out repaired));
            Assert.True(repaired);
        }

        // ── Discord data package ─────────────────────────────────────────────────

        [Fact]
        public async Task DiscordPackage_OwnerOnly_SnowflakeTimes_BothLayouts_AndNoDuplicateOfALiveCapture()
        {
            long t = Utc(2024, 3, 10, 9, 0);
            string m1 = Snowflake(t, 1), m2 = Snowflake(t + 60_000, 2), m3 = Snowflake(t + 120_000, 3);
            const string owner = "100000000000000001", dmChannel = "200000000000000002", guildChannel = "300000000000000003", friend = "400000000000000004", guild = "500000000000000005";
            Json("package/account/user.json", new { id = owner, username = "klivestest", global_name = "Klives Test" });
            Json("package/messages/index.json", new Dictionary<string, string> { [dmChannel] = "Direct Message with adaexample", [guildChannel] = "general in Synthetic Server" });
            Json($"package/messages/c{dmChannel}/channel.json", new { id = dmChannel, type = "DM", recipients = new[] { owner, friend } });
            // Newer packages: JSON, ids as numbers, the timestamp without an offset (ignored: the id carries the instant).
            Put($"package/messages/c{dmChannel}/messages.json",
                $"[{{\"ID\": {m1}, \"Timestamp\": \"2024-03-10 09:00:00\", \"Contents\": \"hello ada\", \"Attachments\": \"\"}},\n" +
                $" {{\"ID\": {m2}, \"Timestamp\": \"2024-03-10 09:01:00\", \"Contents\": \"\", \"Attachments\": \"https://cdn.example.invalid/attachments/1/2/cat.png?ex=abc\"}}]");
            Json($"package/messages/c{guildChannel}/channel.json", new { id = guildChannel, type = 0, name = "general", guild = new { id = guild, name = "Synthetic Server" } });
            // Older packages: CSV with quoted, multi-line text.
            Put($"package/messages/c{guildChannel}/messages.csv",
                "ID,Timestamp,Contents,Attachments\r\n" + m3 + ",2024-03-10 09:02:00.000000+00:00,\"multi\nline, with \"\"quotes\"\"\",\r\n");
            string dir = Path.Combine(exports, "package");

            // The first message was also captured live, by another source.
            await stores.Corpus.WriteAsync((c, tx) =>
            {
                using var w = new CorpusWriter(c, tx, new IngestSource("bot:synthetic", "discord", "bot", null, IngestSource.PlatformRank), SourceKinds.Live, clock.NowMs);
                w.Message(new IngestMessage
                {
                    PlatformMessageId = m1, Conversation = new IngestConversation("discord", dmChannel, ConversationKinds.Dm), Author = new IngestIdentity("discord", owner, "klivestest"),
                    SentAtMs = t, OrderKey = long.Parse(m1), Content = "hello ada",
                });
                return w.Complete();
            });

            var preview = service.Preview(dir, null, new ImportOptions());
            Assert.Equal("discord-package", preview.Format);
            Assert.Equal(3, preview.Messages);
            Assert.Equal(1, preview.AlreadyInCorpus);
            Assert.Equal("klivestest", preview.OwnerDetected);

            var result = await service.CommitAsync(dir, null, new ImportOptions());
            Assert.Equal((2, 1), (result.Inserted, result.Unchanged));

            Assert.Equal(3L, One<long>("SELECT COUNT(*) FROM messages"));
            Assert.Equal("live", One<string>("SELECT first_source_kind FROM messages WHERE message_id=$m", ("$m", "discord:" + m1)));
            Assert.Equal(t + 120_000, One<long>("SELECT sent_at_ms FROM messages WHERE message_id=$m", ("$m", "discord:" + m3)));
            Assert.Equal("multi\nline, with \"quotes\"", One<string>("SELECT content FROM messages WHERE message_id=$m", ("$m", "discord:" + m3)));
            Assert.Equal(long.Parse(m3), One<long>("SELECT order_key FROM messages WHERE message_id=$m", ("$m", "discord:" + m3)));
            Assert.Equal(1L, One<long>("SELECT COUNT(DISTINCT author_identity_id) FROM messages"));
            Assert.Equal(1L, One<long>("SELECT p.is_self FROM persons p JOIN identities i ON i.person_id=p.person_id WHERE i.identity_id=$i", ("$i", "discord:" + owner)));
            // The other side of the DM is known by id, and the DM is named after the index.
            Assert.Equal(1L, One<long>("SELECT COUNT(*) FROM conversation_members WHERE conversation_id=$c AND identity_id=$i", ("$c", "discord:" + dmChannel), ("$i", "discord:" + friend)));
            Assert.Equal("adaexample", One<string>("SELECT title FROM conversations WHERE conversation_id=$c", ("$c", "discord:" + dmChannel)));
            Assert.Equal(("Synthetic Server", ConversationKinds.Text),
                Many("SELECT g.name, c.kind FROM conversations c JOIN guilds g ON g.guild_id=c.guild_id WHERE c.conversation_id='discord:" + guildChannel + "'", r => (r.GetString(0), r.GetString(1))).Single());
            // Attachment links in a package have expired: recorded, never fetched.
            Assert.Equal((DownloadStates.Unavailable, "cat.png", "image"),
                Many("SELECT download_state, filename, media_kind FROM attachments", r => (r.GetString(0), r.GetString(1), r.GetString(2))).Single());
        }

        // ── tables ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task Table_FindsColumnsByUsualNames_ReadsThreeKindsOfTime_AndAsksWhenColumnsAreUnknown()
        {
            string csv = Put("log.csv",
                "time,from,message,chat\n" +
                "2024-03-10T09:00:00Z,Ada Example,hello,Room A\n" +
                "1710061260,Klives Test,\"hi, there\",Room A\n" +
                "2024-03-10 09:02,Ada Example,wall clock,Room B\n" +
                ",Ada Example,no time,Room A\n");

            var preview = service.Preview(csv, null, new ImportOptions { OwnerName = "Klives Test" });
            Assert.Equal("table", preview.Format);
            Assert.Equal((3, 1, 2), (preview.Messages, preview.SkippedRecords, preview.Conversations.Count));
            Assert.Equal("Europe/London", preview.ZoneUsed);

            await service.CommitAsync(csv, null, new ImportOptions { OwnerName = "Klives Test", Platform = "Forum" });
            var rows = Many("SELECT content, sent_at_ms, time_quality, platform FROM messages ORDER BY sent_at_ms", r => (r.GetString(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3)));
            Assert.Equal(("hello", Utc(2024, 3, 10, 9, 0), (int)TimeQuality.Second, "forum"), rows[0]);
            Assert.Equal(("hi, there", Utc(2024, 3, 10, 9, 1), (int)TimeQuality.Second, "forum"), rows[1]);
            Assert.Equal(("wall clock", Utc(2024, 3, 10, 9, 2), (int)TimeQuality.InferredZone, "forum"), rows[2]);

            string odd = Put("odd.csv", "a,b\nx,y\n");
            Assert.Throws<InvalidDataException>(() => service.Preview(odd, null, new ImportOptions()));
            Assert.Contains("Say which column", service.Preview(odd, "table", new ImportOptions()).DecisionNeeded);

            string json = Json("log.json", new[] { new { timestamp = 1710061200123, sender = "Ada Example", text = "from json", id = "77" } });
            await service.CommitAsync(json, null, new ImportOptions());
            Assert.Equal(1710061200123, One<long>("SELECT sent_at_ms FROM messages WHERE content='from json'"));
        }

        // ── the service around the formats ───────────────────────────────────────

        [Fact]
        public async Task UnknownFiles_AreRefused_AndOversizeMediaIsRecordedNotStored()
        {
            string notes = Put("notes.txt", "Just some notes.\nNothing that looks like a chat export.\n");
            var ex = Assert.Throws<InvalidDataException>(() => service.Preview(notes, null, new ImportOptions()));
            Assert.Contains("does not look like", ex.Message);
            Assert.Throws<InvalidDataException>(() => service.Preview(notes, "nope", new ImportOptions()));

            Put("big/_chat.txt", $"[14/2/24, 10:31:00] Bo Example: {Lrm}<attached: photo.jpg>\n");
            PutBytes("big/photo.jpg", Jpeg);
            var strict = new ImportService(stores, clock, new MediaStore(stores.Paths), () => "Europe/London", new MediaLimits(ImageBytes: 4));
            var result = await strict.CommitAsync(Path.Combine(exports, "big"), null, new ImportOptions());

            Assert.Equal((1, 1, 0), (result.Inserted, result.MediaTooLarge, result.AttachmentsStored));
            Assert.Equal(DownloadStates.TooLarge, One<string>("SELECT download_state FROM attachments"));
            Assert.NotNull(One<string>("SELECT download_error FROM attachments"));
            Assert.Equal(0L, One<long>("SELECT COUNT(*) FROM cas_objects"));
        }

        [Fact]
        public void JsonPullReader_StreamsElementsAcrossBufferBoundaries_AndSkipsWhatItIsToldTo()
        {
            var sb = new StringBuilder("{\"skip\": {\"deep\": [1, 2, {\"x\": [3, \"]}\"]}]}, \"items\": [");
            for (int i = 0; i < 3000; i++) sb.Append(i == 0 ? "" : ",").Append("{\"n\":").Append(i).Append(",\"pad\":\"").Append('p', 200).Append("\"}");
            sb.Append("], \"tail\": true}");

            using var reader = new JsonPullReader(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
            var seen = new List<int>();
            bool tail = false;
            while (reader.Read())
            {
                if (reader.Token != JsonTokenType.PropertyName) continue;
                if (reader.Text == "skip") reader.SkipValue();
                else if (reader.Text == "items")
                {
                    Assert.True(reader.Read());
                    Assert.Equal(JsonTokenType.StartArray, reader.Token);
                    while (reader.TryReadElement(out var e)) seen.Add(e.GetProperty("n").GetInt32());
                }
                else if (reader.Text == "tail") tail = reader.ReadValue().GetBoolean();
            }
            Assert.Equal(Enumerable.Range(0, 3000), seen);
            Assert.True(tail);

            using var truncated = new JsonPullReader(new MemoryStream(Encoding.UTF8.GetBytes("[{\"n\":1},{\"n\":")));
            Assert.True(truncated.Read());
            Assert.True(truncated.TryReadElement(out _));
            Assert.ThrowsAny<JsonException>(() => truncated.TryReadElement(out _));
        }

        [Fact]
        public void Csv_ReadsQuotedFieldsWithCommasQuotesAndLineBreaks()
        {
            var rows = Csv.Read(new StringReader("a,\"b \"\"q\"\" c\",d\r\n\"line1\nline2\",,\n\nlast"), ',').Select(r => r.ToArray()).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "a", "b \"q\" c", "d" }, rows[0]);
            Assert.Equal(new[] { "line1\nline2", "", "" }, rows[1]);
            Assert.Equal(new[] { "last" }, rows[2]);
        }
    }
}
