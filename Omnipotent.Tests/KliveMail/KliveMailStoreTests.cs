using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveMail;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Tests.KliveMail
{
    /// <summary>
    /// The store behind the rebuilt mail page: lists that page past 200 (the old "Load more" could
    /// not), many messages at once (268 unread were marked one at a time), the way back out of the
    /// trash (there was none), and a summary that also counts the addresses nobody pinned (42 live
    /// messages to 3 such addresses showed only under All mail).
    /// </summary>
    public sealed class KliveMailStoreTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "klivemail-store-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly KliveMailDb db;
        private readonly KliveMailRepository repo;
        private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        public KliveMailStoreTests()
        {
            db = new KliveMailDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            repo = new KliveMailRepository(db);
        }

        public void Dispose()
        {
            try { db.Dispose(); } catch { }
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + ext); } catch { } }
        }

        private static StoredMessage Mail(string id, string to, DateTime received, string subject = "Hello", string? text = "Body", string? html = null) => new()
        {
            Id = id, ToAddress = to, FromAddress = "sender@example.com", FromName = "Sender", Subject = subject,
            DateUtc = received, ReceivedUtc = received, ThreadId = id, BodyText = text, BodyHtml = html,
        };

        private async Task<List<string>> IdsAsync(MessageQuery q) => (await repo.ListMessagesAsync(q)).Select(m => m.Id).ToList();

        [Fact]
        public async Task Lists_PagePastTwoHundred_NewestFirst()
        {
            for (int i = 0; i < 260; i++) await repo.InsertMessageAsync(Mail("m" + i.ToString("D3"), "a@klive.dev", Now.AddMinutes(-i)));

            var seen = new List<string>();
            for (int page = 1; page <= 6; page++) seen.AddRange(await IdsAsync(new MessageQuery { Page = page, PageSize = 50 }));

            Assert.Equal(260, seen.Count);
            Assert.Equal(Enumerable.Range(0, 260).Select(i => "m" + i.ToString("D3")), seen);
        }

        [Fact]
        public async Task EveryView_ReturnsTheRowsItNames_WithPreviewAndCode()
        {
            await repo.InsertMessageAsync(Mail("code", "a@klive.dev", Now.AddMinutes(-1), "483920 is your verification code", text: null, html: "<p>Use the code above.</p>"));
            await repo.InsertMessageAsync(Mail("plain", "b@klive.dev", Now.AddMinutes(-2), "News"));
            await repo.InsertMessageAsync(Mail("read", "a@klive.dev", Now.AddMinutes(-3), "Old news"));
            await repo.SetReadAsync("read", true);
            var withFile = Mail("file", "a@klive.dev", Now.AddMinutes(-4), "Invoice");
            withFile.HasAttachments = true;
            await repo.InsertMessageAsync(withFile);

            Assert.Equal(new[] { "code", "plain", "read", "file" }, await IdsAsync(new MessageQuery()));
            Assert.Equal(new[] { "code", "plain", "file" }, await IdsAsync(new MessageQuery { UnreadOnly = true }));
            Assert.Equal(new[] { "code", "read", "file" }, await IdsAsync(new MessageQuery { Mailbox = "A@klive.dev" }));
            Assert.Equal(new[] { "code", "read", "file" }, await IdsAsync(new MessageQuery { Mailbox = "a" }));
            Assert.Equal(new[] { "file" }, await IdsAsync(new MessageQuery { HasAttachmentOnly = true }));
            Assert.Equal(new[] { "code" }, await IdsAsync(new MessageQuery { CodesOnly = true }));
            Assert.Equal(new[] { "code" }, await IdsAsync(new MessageQuery { Mailbox = "a@klive.dev", CodesOnly = true, UnreadOnly = true }));

            var top = (await repo.ListMessagesAsync(new MessageQuery()))[0];
            Assert.Equal("483920", top.Code);
            Assert.Equal("Use the code above.", top.Snippet);   // HTML-only: the old preview was blank
            Assert.Equal("483920", await repo.GetCodeAsync("code"));
        }

        [Fact]
        public async Task BulkRead_ChangesOnlyWhatNeedsIt_AndMarkAllReadStaysInItsMailbox()
        {
            for (int i = 0; i < 6; i++) await repo.InsertMessageAsync(Mail("a" + i, "a@klive.dev", Now.AddMinutes(-i)));
            for (int i = 0; i < 3; i++) await repo.InsertMessageAsync(Mail("b" + i, "b@klive.dev", Now.AddMinutes(-10 - i)));

            Assert.Equal(2, await repo.SetReadManyAsync(new[] { "a0", "a1", "a1", "missing" }, true));
            Assert.Equal(0, await repo.SetReadManyAsync(new[] { "a0" }, true));       // already read
            Assert.Equal(4, await repo.MarkAllReadAsync("a@klive.dev"));
            Assert.Empty(await IdsAsync(new MessageQuery { Mailbox = "a@klive.dev", UnreadOnly = true }));
            Assert.Equal(3, (await IdsAsync(new MessageQuery { UnreadOnly = true })).Count);
            Assert.Equal(1, await repo.SetReadManyAsync(new[] { "a2" }, false));
            Assert.Equal(4, await repo.MarkAllReadAsync(null));
            Assert.Empty(await IdsAsync(new MessageQuery { UnreadOnly = true }));
        }

        [Fact]
        public async Task Trash_IsReversible()
        {
            for (int i = 0; i < 3; i++) await repo.InsertMessageAsync(Mail("m" + i, "a@klive.dev", Now.AddMinutes(-i)));

            Assert.Equal(2, await repo.SetDeletedManyAsync(new[] { "m0", "m2" }, true));
            Assert.Equal(new[] { "m1" }, await IdsAsync(new MessageQuery()));
            Assert.Equal(new[] { "m0", "m2" }, await IdsAsync(new MessageQuery { Trash = true }));
            Assert.Equal(new[] { "m1" }, (await repo.SearchAsync("Body")).Select(m => m.Id));   // trash is not searched

            Assert.Equal(1, await repo.SetDeletedManyAsync(new[] { "m2" }, false));
            Assert.Equal(new[] { "m1", "m2" }, await IdsAsync(new MessageQuery()));
            Assert.Equal(new[] { "m0" }, await IdsAsync(new MessageQuery { Trash = true }));
            Assert.True(await repo.SoftDeleteAsync("m1"));                               // single-message route still answers true
            Assert.False(await repo.SoftDeleteAsync("nope"));
        }

        [Fact]
        public async Task Summary_CountsPinnedAndUnpinned_AndArrivalsByDay()
        {
            await repo.CreateMailboxAsync("pinned", "Pinned one");
            await repo.CreateMailboxAsync("empty", null);
            await repo.InsertMessageAsync(Mail("p1", "pinned@klive.dev", Now.AddHours(-2)));
            await repo.InsertMessageAsync(Mail("p2", "pinned@klive.dev", Now.AddDays(-3)));
            await repo.InsertMessageAsync(Mail("o1", "stray@klive.dev", Now.AddHours(-30)));
            await repo.InsertMessageAsync(Mail("gone", "stray@klive.dev", Now.AddHours(-1)));
            await repo.InsertMessageAsync(Mail("ancient", "pinned@klive.dev", Now.AddDays(-90)));
            await repo.SetReadAsync("p2", true);
            await repo.SetDeletedManyAsync(new[] { "gone" }, true);

            var s = await repo.GetSummaryAsync(Now);

            Assert.Equal(4, s.Total);
            Assert.Equal(3, s.Unread);
            Assert.Equal(1, s.Trash);
            Assert.Equal(Now.AddHours(-2), s.LastReceivedUtc);
            Assert.Equal(new[] { "empty@klive.dev", "pinned@klive.dev" }, s.Mailboxes.Select(m => m.Address));
            var pinned = s.Mailboxes.Single(m => m.Address == "pinned@klive.dev");
            Assert.Equal((3, 2, "Pinned one", true), (pinned.Total, pinned.Unread, pinned.DisplayName, pinned.Pinned));
            Assert.Equal(Now.AddHours(-2), pinned.LastReceivedUtc);
            Assert.Null(s.Mailboxes.Single(m => m.Address == "empty@klive.dev").LastReceivedUtc);
            var stray = Assert.Single(s.Others);
            Assert.Equal(("stray@klive.dev", 1, 1, false), (stray.Address, stray.Total, stray.Unread, stray.Pinned));

            // Thirty UTC days ending today, every day present; deleted mail still arrived.
            Assert.Equal(KliveMailRepository.DailyDays, s.Daily.Count);
            Assert.Equal(Now.ToString("yyyy-MM-dd"), s.Daily[^1].D);
            Assert.Equal(2, s.Daily[^1].N);                    // p1 and gone, both today
            Assert.Equal(4, s.Daily.Sum(d => d.N));             // "ancient" is outside the window
            Assert.Equal(2, s.Received24h);
            Assert.Equal(4, s.Received7d);

            // ListMailboxesAsync (the account registry reads it) gives the pinned list alone.
            Assert.Equal(2, (await repo.ListMailboxesAsync()).Count);
        }

        [Fact]
        public async Task Backfill_BuildsMissingAndOutdatedDigests_Once()
        {
            await repo.InsertMessageAsync(Mail("new", "a@klive.dev", Now, "Fresh"));
            // Rows as an older build left them: no digest, or one from older rules.
            using (var conn = await db.OpenAsync())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO messages(id,to_address,from_address,subject,received_utc,thread_id,body_text,body_html)
                        VALUES ('legacy1','a@klive.dev','x@example.com','246810 is your code','2026-11-01T00:00:00.0000000Z','legacy1',NULL,'<p>Legacy html</p>'),
                               ('legacy2','a@klive.dev','x@example.com','Hi','2026-11-02T00:00:00.0000000Z','legacy2','Legacy text',NULL);
                    UPDATE message_digest SET version = 0, snippet = 'stale' WHERE message_id = 'new';";
                cmd.ExecuteNonQuery();
            }

            Assert.Equal(3, await repo.BackfillDigestsAsync(batchSize: 2));
            Assert.Equal(0, await repo.BackfillDigestsAsync());

            var rows = (await repo.ListMessagesAsync(new MessageQuery())).ToDictionary(m => m.Id);
            Assert.Equal("Body", rows["new"].Snippet);
            Assert.Equal(("Legacy html", "246810"), (rows["legacy1"].Snippet, rows["legacy1"].Code));
            Assert.Equal("Legacy text", rows["legacy2"].Snippet);
        }

        [Fact]
        public async Task UpgradeFromV2_AddsTheIndexes_DropsTheOneBitOnes_KeepsEveryMessage()
        {
            string path = Path.Combine(Path.GetTempPath(), "klivemail-v2-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var legacy = new SqliteConnection("Data Source=" + path))
                {
                    legacy.Open();
                    foreach (var (version, sql) in KliveMailSchema.Migrations.Where(m => m.Version <= 2))
                    {
                        using var cmd = legacy.CreateCommand();
                        cmd.CommandText = sql;
                        cmd.ExecuteNonQuery();
                    }
                    using var seed = legacy.CreateCommand();
                    seed.CommandText = @"CREATE TABLE schema_versions (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                        INSERT INTO schema_versions VALUES (1, '2026-06-01T00:00:00Z'), (2, '2026-09-11T00:00:00Z');
                        INSERT INTO messages(id,to_address,from_address,subject,received_utc,thread_id,body_text,is_read)
                            VALUES ('old1','a@klive.dev','x@example.com','One','2026-10-01T00:00:00.0000000Z','old1','first',1),
                                   ('old2','a@klive.dev','x@example.com','Two','2026-10-02T00:00:00.0000000Z','old2','second',0);
                        INSERT INTO attachments(id,message_id,file_name,storage_path) VALUES ('att1','old1','a.txt','');";
                    seed.ExecuteNonQuery();
                }
                SqliteConnection.ClearAllPools();

                var upgraded = new KliveMailDb(path);
                await upgraded.InitialiseAsync();
                var upRepo = new KliveMailRepository(upgraded);
                await upRepo.BackfillDigestsAsync();

                using (var conn = await upgraded.OpenAsync())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx_messages%' ORDER BY name)";
                    string indexes = (string)(await cmd.ExecuteScalarAsync())!;
                    Assert.Equal("idx_messages_box,idx_messages_conv,idx_messages_feed,idx_messages_received,idx_messages_thread,idx_messages_to", indexes);
                    cmd.CommandText = "SELECT COUNT(*) FROM attachments";
                    Assert.Equal(1L, await cmd.ExecuteScalarAsync());
                }
                var list = await upRepo.ListMessagesAsync(new MessageQuery());
                Assert.Equal(new[] { "old2", "old1" }, list.Select(m => m.Id));
                Assert.Equal(new[] { "second", "first" }, list.Select(m => m.Snippet));
                Assert.Equal(new[] { false, true }, list.Select(m => m.IsRead));
                upgraded.Dispose();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var ext in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + ext); } catch { } }
            }
        }

        [Fact]
        public void Ingestion_KeepsTheImagesTheHtmlDraws_AndEveryAttachment()
        {
            var builder = new MimeKit.BodyBuilder { HtmlBody = "<p>Hi</p><img src=\"cid:logo@fixture\"><img src=\"https://example.com/remote.png\">" };
            var logo = builder.LinkedResources.Add("logo.png", new byte[] { 137, 80, 78, 71 }, new MimeKit.ContentType("image", "png"));
            logo.ContentId = "logo@fixture";
            var unused = builder.LinkedResources.Add("spacer.gif", new byte[] { 71, 73, 70 }, new MimeKit.ContentType("image", "gif"));
            unused.ContentId = "spacer@fixture";
            builder.Attachments.Add("ticket.pdf", new byte[] { 37, 80, 68, 70 }, new MimeKit.ContentType("application", "pdf"));
            var message = new MimeKit.MimeMessage { Subject = "Your ticket", Body = builder.ToMessageBody() };

            var parts = global::Omnipotent.Services.KliveMail.Smtp.KliveMailMessageStore.PartsToKeep(message);

            Assert.Equal(2, parts.Count);
            Assert.Contains(parts, p => !p.Inline && p.Entity is MimeKit.MimePart { FileName: "ticket.pdf" });
            Assert.Contains(parts, p => p.Inline && p.Entity.ContentId == "logo@fixture");
            Assert.DoesNotContain(parts, p => p.Entity.ContentId == "spacer@fixture");   // not drawn by the HTML

            var plain = new MimeKit.MimeMessage { Body = new MimeKit.BodyBuilder { HtmlBody = "<p>No pictures</p>" }.ToMessageBody() };
            Assert.Empty(global::Omnipotent.Services.KliveMail.Smtp.KliveMailMessageStore.PartsToKeep(plain));
        }

        [Fact]
        public async Task SentCopies_AreFiledRead()
        {
            var copy = Mail("sent", "me@klive.dev", Now, "[sent] Hi");
            copy.IsRead = true;
            await repo.InsertMessageAsync(copy);
            Assert.True((await repo.ListMessagesAsync(new MessageQuery()))[0].IsRead);
        }

        [Fact]
        public void Snapshot_KeepsTheOldFields_AndAddsTheNewOnes()
        {
            var summary = new MailSummary
            {
                Total = 5, Unread = 2, Trash = 1, LastReceivedUtc = Now, Received24h = 1, Received7d = 3,
                Mailboxes = { new MailboxInfo { Address = "a@klive.dev", Total = 5, Unread = 2, Pinned = true } },
                Others = { new MailboxInfo { Address = "stray@klive.dev", Total = 1 } },
                Daily = { new DayCount { D = "2026-11-11", N = 1 } },
            };
            var health = new KliveMailHealth(() => Now);
            health.SmtpFailed("SocketException: address already in use");

            var snapshot = global::Omnipotent.Services.KliveMail.KliveMail.BuildSnapshot(summary, health.View(), Now);
            var stats = JObject.Parse(snapshot.StatsJson!);
            var boxes = JObject.Parse(snapshot.MailboxesJson!);

            Assert.Equal((5, 2, 1), (stats.Value<int>("total"), stats.Value<int>("unread"), stats.Value<int>("trash")));
            Assert.Equal(5, boxes["all"]!.Value<int>("total"));
            Assert.Equal(2, boxes["all"]!.Value<int>("unread"));
            Assert.Equal(1, boxes.Value<int>("trash"));
            Assert.Equal("a@klive.dev", boxes["mailboxes"]![0]!.Value<string>("Address"));
            Assert.Equal("stray@klive.dev", boxes["others"]![0]!.Value<string>("Address"));
            Assert.Equal(1, boxes["daily"]![0]!.Value<int>("N"));
            Assert.Equal("failed", boxes["health"]!["smtp"]!.Value<string>("state"));
            Assert.Contains("already in use", boxes["health"]!["smtp"]!.Value<string>("lastError"));
        }
    }
}
