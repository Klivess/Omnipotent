using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Tests.KliveMail
{
    // Feature #1 (approved 2026-09-11, directive 28cf2e03): full-text search over decoded HTML
    // body + attachment text, plus mailbox/from filters on SearchAsync, plus a v1->v2 FTS
    // migration that keeps pre-feature messages searchable.
    //
    // Hermetic: every test points KliveMailDb at a temp file, never the production klivemail.db.
    public class KliveMailSearchTests : IDisposable
    {
        private readonly string dbPath;
        private readonly KliveMailDb db;
        private readonly KliveMailRepository repo;

        public KliveMailSearchTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "klivemail_test_" + Guid.NewGuid().ToString("N") + ".db");
            db = new KliveMailDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            repo = new KliveMailRepository(db);
        }

        public void Dispose()
        {
            try { db.Dispose(); } catch { }
            SqliteConnection.ClearAllPools();
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + ext); } catch { }
            }
        }

        private static StoredMessage MakeMessage(string id, string to, string from, string subject,
            string? bodyText = null, string? bodyHtml = null, List<StoredAttachment>? attachments = null) => new()
        {
            Id = id,
            ToAddress = to,
            FromAddress = from,
            FromName = "Sender " + from.Split('@')[0],
            Subject = subject,
            DateUtc = DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            MessageId = "<" + id + "@klive.dev>",
            ThreadId = id,
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            HasAttachments = attachments != null && attachments.Count > 0,
            Attachments = attachments ?? new List<StoredAttachment>()
        };

        private async Task InsertWithAttachmentAsync(string id, string attFileName, string attContent, string contentType = "text/plain")
        {
            string dir = Path.Combine(Path.GetTempPath(), "klivemail_att_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, attFileName);
            await File.WriteAllTextAsync(path, attContent);
            var msg = MakeMessage(id, "inbox@klive.dev", "ext@example.com", "with attachment",
                attachments: new List<StoredAttachment>
                {
                    new() { Id = id + "a1", MessageId = id, FileName = attFileName, ContentType = contentType,
                            SizeBytes = (long)new FileInfo(path).Length, StoragePath = path }
                });
            await repo.InsertMessageAsync(msg);
            try { File.Delete(path); Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public async Task Search_PlainBodyText_FindsMessage()
        {
            await repo.InsertMessageAsync(MakeMessage("m1", "inbox@klive.dev", "a@example.com", "hi", bodyText: "your password reset token is 482913"));
            var hits = await repo.SearchAsync("482913");
            Assert.Single(hits);
            Assert.Equal("m1", hits[0].Id);
        }

        [Fact]
        public async Task Search_HtmlOnlyBody_FindsMessage()
        {
            // No body_text; the searchable HTML body must be what the match lands on.
            await repo.InsertMessageAsync(MakeMessage("m1", "inbox@klive.dev", "a@example.com", "hi", bodyHtml: "<html><body><p>Your verification code is <b>778214</b></p></body></html>"));
            var hits = await repo.SearchAsync("778214");
            Assert.Single(hits);
            Assert.Equal("m1", hits[0].Id);
        }

        [Fact]
        public async Task Search_AttachmentPlainText_FindsMessage()
        {
            await InsertWithAttachmentAsync("m1", "notes.txt", "meeting agenda: zebraquartz rollback plan");
            var hits = await repo.SearchAsync("zebraquartz");
            Assert.Single(hits);
            Assert.Equal("m1", hits[0].Id);
        }

        [Fact]
        public async Task Search_MailboxFilter_ScopesToMailbox()
        {
            await repo.InsertMessageAsync(MakeMessage("m1", "box1@klive.dev", "a@example.com", "shared word", bodyText: "zetafunk marker here"));
            await repo.InsertMessageAsync(MakeMessage("m2", "box2@klive.dev", "a@example.com", "shared word", bodyText: "zetafunk marker here"));
            var hits = await repo.SearchAsync("zetafunk", mailbox: "box1@klive.dev");
            Assert.Single(hits);
            Assert.Equal("m1", hits[0].Id);
            Assert.Equal("box1@klive.dev", hits[0].ToAddress);
        }

        [Fact]
        public async Task Search_FromFilter_MatchesFromAddress()
        {
            await repo.InsertMessageAsync(MakeMessage("m1", "inbox@klive.dev", "alice@example.com", "hi", bodyText: "quixote signal one"));
            await repo.InsertMessageAsync(MakeMessage("m2", "inbox@klive.dev", "bob@example.com", "hi", bodyText: "quixote signal one"));
            var hits = await repo.SearchAsync("quixote", from: "alice@example.com");
            Assert.Single(hits);
            Assert.Equal("m1", hits[0].Id);
        }

        [Fact]
        public async Task Search_TokenInAttachmentColumn_RankedAndFound()
        {
            await repo.InsertMessageAsync(MakeMessage("m1", "inbox@klive.dev", "a@example.com", "word in body", bodyText: "velvetanchor appears in the body"));
            await InsertWithAttachmentAsync("m2", "report.txt", "velvetanchor appears in the report attachment");
            var hits = await repo.SearchAsync("velvetanchor");
            Assert.Equal(2, hits.Count);
            var ids = hits.Select(h => h.Id).ToHashSet();
            Assert.Contains("m1", ids);
            Assert.Contains("m2", ids);
        }

        [Fact]
        public void Migrate_V1Schema_UpgradesFtsAndKeepsMessages()
        {
            // Build a v1-only DB the way an old production store would exist: apply migration 1
            // (creates the 5-column messages_fts), stamp schema_versions(1), and insert one legacy
            // message row via raw SQL (the old insert path only wrote the 5 FTS columns).
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            using (var legacy = new SqliteConnection("Data Source=" + dbPath))
            {
                legacy.Open();
                using (var cmd = legacy.CreateCommand())
                {
                    cmd.CommandText = KliveMailSchema.Migrations[0].Sql;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = legacy.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE schema_versions (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                                        INSERT INTO schema_versions(version, applied_utc) VALUES(1, '2026-01-01T00:00:00Z');";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = legacy.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO messages(id,to_address,from_address,from_name,subject,date_utc,received_utc,message_id,in_reply_to,references_raw,thread_id,body_text,body_html,has_attachments,raw_size,is_read,is_deleted)
                                        VALUES('legacy1','inbox@klive.dev','a@example.com','Sender a','old msg','2026-01-01T00:00:00Z','2026-01-01T00:00:01Z','<legacy1@klive.dev>',NULL,NULL,'legacy1','oldbody token zorkblat',NULL,0,0,0,0)";
                    cmd.ExecuteNonQuery();
                }
            }
            SqliteConnection.ClearAllPools();

            // Re-initialise against the same file: migration 2 must run, and the C# backfill must
            // rebuild the extended FTS index from the messages table so the legacy row is searchable.
            var upDb = new KliveMailDb(dbPath);
            upDb.InitialiseAsync().GetAwaiter().GetResult();
            var upRepo = new KliveMailRepository(upDb);
            try
            {
                var hits = upRepo.SearchAsync("zorkblat").GetAwaiter().GetResult();
                Assert.Single(hits);
                Assert.Equal("legacy1", hits[0].Id);
                using var conn = upDb.OpenAsync().GetAwaiter().GetResult();
                using var ver = conn.CreateCommand();
                ver.CommandText = "SELECT version FROM schema_versions ORDER BY version DESC LIMIT 1";
                Assert.Equal(2, Convert.ToInt32(ver.ExecuteScalar()));
            }
            finally
            {
                upDb.Dispose();
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
