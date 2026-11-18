using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Tests.KliveMail
{
    /// <summary>File reads are counted for the whole process, so these tests run with nothing beside them.</summary>
    [CollectionDefinition("KliveMailReadCost", DisableParallelization = true)]
    public sealed class KliveMailReadCostCollection { }

    /// <summary>
    /// What the mail lists read. On the live store (364 messages, most of them ~40 KB of HTML) the
    /// first list after a start took 35.7 s, the unread list 35.7 s, a search 28.9 s and opening a
    /// message up to 4.4 s; warm repeats took 150–210 ms. The difference is reads that have to come
    /// off the server's disk, so cost is counted in reads, not milliseconds (a development SSD hides it).
    ///
    /// SQLite's own cache-miss counter (what OverviewQueryCostTests uses) is not enough here: it reads
    /// overflow pages straight from the file, past its cache, so a query that walks every message's
    /// body shows up as a few hundred "pages". The process's read count sees every one of them.
    /// </summary>
    [Collection("KliveMailReadCost")]
    public sealed class KliveMailListCostTests : IDisposable
    {
        private const int Messages = 364;
        private const int PinnedMailboxes = 81;

        private readonly string dir = Path.Combine(Path.GetTempPath(), "klivemail-cost-" + Guid.NewGuid().ToString("N"));
        private string NewPath => Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");

        public KliveMailListCostTests() { Directory.CreateDirectory(dir); }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }

        // ── the queries as deployed through 43afdee ──

        private const string OldList = @"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE is_deleted = 0 ORDER BY received_utc DESC LIMIT 50 OFFSET 0";
        private const string OldUnread = @"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE is_deleted = 0 AND is_read = 0 ORDER BY received_utc DESC LIMIT 50 OFFSET 0";
        private const string OldMailbox = @"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE is_deleted = 0 AND to_address = $mailbox ORDER BY received_utc DESC LIMIT 50 OFFSET 0";
        private const string OldStats = @"SELECT
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 0) AS total,
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 0 AND is_read = 0) AS unread,
                    (SELECT COUNT(*) FROM messages WHERE is_deleted = 1) AS trash";
        private const string OldMailboxes = @"SELECT mb.address, mb.display_name,
                    (SELECT COUNT(*) FROM messages m WHERE m.to_address = mb.address AND m.is_deleted = 0) AS total,
                    (SELECT COUNT(*) FROM messages m WHERE m.to_address = mb.address AND m.is_deleted = 0 AND m.is_read = 0) AS unread
                    FROM mailboxes mb ORDER BY mb.address ASC";
        private const string OldThread = @"SELECT id,to_address,from_address,from_name,subject,received_utc,date_utc,thread_id,has_attachments,is_read,
                    substr(replace(replace(coalesce(body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages WHERE thread_id = $thread AND is_deleted = 0 ORDER BY received_utc ASC";
        private const string OldSearch = @"SELECT m.id,m.to_address,m.from_address,m.from_name,m.subject,m.received_utc,m.date_utc,m.thread_id,m.has_attachments,m.is_read,
                    substr(replace(replace(coalesce(m.body_text,''),char(10),' '),char(13),' '),1,200) AS snippet
                    FROM messages_fts f JOIN messages m ON m.id = f.message_id
                    WHERE messages_fts MATCH $q AND m.is_deleted = 0
                    ORDER BY m.received_utc DESC LIMIT 50 OFFSET 0";

        // ── a store shaped like the live one ──

        private static readonly DateTime Newest = new(2026, 11, 9, 16, 15, 17, DateTimeKind.Utc);

        private static string Box(int i) => $"box{i:D2}@klive.dev";

        /// <summary>Mail to 34 addresses (the first 31 pinned, 3 not), newest first every 9 hours;
        /// three in four unread, one in trash; one in six HTML-only; bodies the live sizes.</summary>
        private static StoredMessage Message(int i)
        {
            string id = i.ToString("x32");
            var rng = new Random(i);
            int htmlBytes = i % 11 == 0 ? 3_000 : 20_000 + rng.Next(48_000);
            string html = "<html><head><style>" + new string('s', 2_000) + "</style></head><body><p>Hello</p>"
                + string.Concat(Enumerable.Range(0, htmlBytes / 64).Select(k => $"<td>row {k} of mail {i} ...........................</td>")) + "</body></html>";
            return new StoredMessage
            {
                Id = id,
                ToAddress = Box(i % 34),
                FromAddress = $"no-reply@sender{i % 15}.example",
                FromName = "Sender " + (i % 15),
                Subject = i % 4 == 0 ? $"{100000 + i} is your verification code" : $"Weekly update {i}",
                DateUtc = Newest.AddHours(-9 * i),
                ReceivedUtc = Newest.AddHours(-9 * i),
                MessageId = $"<{id}@sender.example>",
                ThreadId = i % 50 == 0 ? "thread-shared" : id,
                BodyText = i % 6 == 0 ? null : "Hello,\n" + new string('t', 400 + rng.Next(1_200)) + $"\nquokka{i % 7}",
                BodyHtml = html,
                RawSize = html.Length + 2_000,
                IsRead = i % 4 == 3,
            };
        }

        /// <summary>The store as it was before migration 3: schema v1 + v2, no digest, the old indexes.</summary>
        private static async Task<string> BuildOldStoreAsync(string path)
        {
            using var conn = new SqliteConnection("Data Source=" + path);
            await conn.OpenAsync();
            Exec(conn, KliveMailSchema.Migrations[0].Sql);
            Exec(conn, KliveMailSchema.Migrations[1].Sql);
            using var tx = conn.BeginTransaction();
            for (int i = 0; i < Messages; i++)
            {
                var m = Message(i);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO messages(id,to_address,from_address,from_name,subject,date_utc,received_utc,message_id,thread_id,body_text,body_html,has_attachments,raw_size,is_read,is_deleted)
                    VALUES ($id,$to,$from,$fn,$s,$d,$r,$mid,$t,$bt,$bh,0,$raw,$read,$del)";
                cmd.Parameters.AddWithValue("$id", m.Id);
                cmd.Parameters.AddWithValue("$to", m.ToAddress);
                cmd.Parameters.AddWithValue("$from", m.FromAddress);
                cmd.Parameters.AddWithValue("$fn", m.FromName);
                cmd.Parameters.AddWithValue("$s", m.Subject);
                cmd.Parameters.AddWithValue("$d", m.DateUtc!.Value.ToString("o"));
                cmd.Parameters.AddWithValue("$r", m.ReceivedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$mid", m.MessageId);
                cmd.Parameters.AddWithValue("$t", m.ThreadId);
                cmd.Parameters.AddWithValue("$bt", (object?)m.BodyText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bh", m.BodyHtml);
                cmd.Parameters.AddWithValue("$raw", m.RawSize);
                cmd.Parameters.AddWithValue("$read", m.IsRead ? 1 : 0);
                cmd.Parameters.AddWithValue("$del", i == 7 ? 1 : 0);
                cmd.ExecuteNonQuery();
                using var fts = conn.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "INSERT INTO messages_fts(message_id,from_address,from_name,subject,body_text,body_html,attachments) VALUES ($id,$from,$fn,$s,$bt,'','')";
                fts.Parameters.AddWithValue("$id", m.Id);
                fts.Parameters.AddWithValue("$from", m.FromAddress);
                fts.Parameters.AddWithValue("$fn", m.FromName);
                fts.Parameters.AddWithValue("$s", m.Subject);
                fts.Parameters.AddWithValue("$bt", (object?)m.BodyText ?? DBNull.Value);
                fts.ExecuteNonQuery();
            }
            for (int b = 0; b < PinnedMailboxes; b++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO mailboxes(address, created_utc) VALUES ($a, '2026-07-01T00:00:00Z')";
                cmd.Parameters.AddWithValue("$a", b < 31 ? Box(b) : $"quiet{b:D2}@klive.dev");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            using (var stamp = conn.CreateCommand())
            {
                stamp.CommandText = @"CREATE TABLE schema_versions (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                    INSERT INTO schema_versions VALUES (1, '2026-06-01T00:00:00Z'), (2, '2026-09-11T00:00:00Z');";
                stamp.ExecuteNonQuery();
            }
            return path;
        }

        /// <summary>The same store, upgraded the way the server will upgrade it: migration 3 + digest backfill.</summary>
        private async Task<(string Path, KliveMailDb Db, KliveMailRepository Repo)> BuildUpgradedStoreAsync()
        {
            string path = await BuildOldStoreAsync(NewPath);
            SqliteConnection.ClearAllPools();
            var db = new KliveMailDb(path);
            await db.InitialiseAsync();
            var repo = new KliveMailRepository(db);
            Assert.Equal(Messages, await repo.BackfillDigestsAsync());
            return (path, db, repo);
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        private static ulong ProcessReads()
        {
            GetProcessIoCounters(System.Diagnostics.Process.GetCurrentProcess().Handle, out var counters);
            return counters.ReadOperationCount;
        }

        /// <summary>
        /// Runs one query on a fresh connection and counts the file reads it made (Windows), with
        /// the plan and the row count. A fresh connection has an empty page cache, as the server's
        /// pooled connections effectively do for pages another database pushed out of memory.
        /// </summary>
        private static async Task<(int Pages, List<string> Plan, int Rows)> CostAsync(string path, string sql, params (string Name, object Value)[] parameters)
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await conn.OpenAsync();
            var plan = new List<string>();
            using (var explain = conn.CreateCommand())
            {
                explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
                foreach (var (name, value) in parameters) explain.Parameters.AddWithValue(name, value);
                using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            }
            int rows = 0;
            ulong before = ProcessReads();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows++;
            }
            return ((int)(ProcessReads() - before), plan, rows);
        }

        private static async Task<long> PageCountAsync(string path)
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA page_count";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private static (string, object)[] ListParams(bool trash = false) =>
            new (string, object)[] { ("$deleted", trash ? 1 : 0), ("$limit", 50), ("$offset", 0) };

        [Fact]
        public async Task OldLists_ReadEveryMessage_ToShowFifty()
        {
            // The cause, reproduced. No statistics: every list picks the one-bit is_deleted index and
            // sorts all rows, computing each row's preview from its body before the sort, so a page of
            // 50 reads every message in the store. (The 0/1 flags live in the record header, so they
            // cost nothing; the reads are the rows themselves.)
            string path = await BuildOldStoreAsync(NewPath);
            long dbPages = await PageCountAsync(path);

            var (list, plan, _) = await CostAsync(path, OldList);
            var (unread, _, _) = await CostAsync(path, OldUnread);
            string detail = $"store {dbPages} pages, {Messages} messages; list {list} reads; unread {unread}. Plan: {string.Join(" / ", plan)}";

            Assert.Contains(plan, step => step.Contains("idx_messages_deleted", StringComparison.Ordinal));
            Assert.Contains(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
            // Every message's first page or close to it: several times the 50 rows shown.
            Assert.True(list > Messages / 2, detail);
            Assert.True(unread > Messages / 2, detail);
        }

        [Fact]
        public async Task NewLists_ReadTheirPage_NotTheStore()
        {
            var (path, db, repo) = await BuildUpgradedStoreAsync();
            string oldPath = await BuildOldStoreAsync(NewPath);
            long dbPages = await PageCountAsync(path);

            var views = new (string Name, MessageQuery Query, string Old, (string, object)[] OldParams)[]
            {
                ("all mail", new MessageQuery(), OldList, Array.Empty<(string, object)>()),
                ("unread", new MessageQuery { UnreadOnly = true }, OldUnread, Array.Empty<(string, object)>()),
                ("one mailbox", new MessageQuery { Mailbox = Box(3) }, OldMailbox, new (string, object)[] { ("$mailbox", Box(3)) }),
            };
            var measured = new List<(string Name, int Pages, int OldPages, int Rows, List<string> Plan, int OldRows)>();
            foreach (var (name, query, oldSql, oldParams) in views)
            {
                var parameters = ListParams().ToList();
                if (query.Mailbox != null) parameters.Add(("$mailbox", query.Mailbox));
                var (pages, plan, rows) = await CostAsync(path, KliveMailRepository.ListSql(query), parameters.ToArray());
                var (oldPages, _, oldRows) = await CostAsync(oldPath, oldSql, oldParams);
                measured.Add((name, pages, oldPages, rows, plan, oldRows));
            }
            string detail = $"store {dbPages} pages. " + string.Join(" | ", measured.Select(m =>
                $"'{m.Name}': {m.OldPages} reads before, {m.Pages} now, {m.Rows} rows ({string.Join(" / ", m.Plan)})"));
            foreach (var m in measured)
            {
                Assert.Equal(m.OldRows, m.Rows);
                Assert.DoesNotContain(m.Plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
                // About a read a row (its first page) plus the index and digest pages, never the store.
                Assert.True(m.Pages <= m.Rows + 24, detail);
            }
            // The all-mail and unread views were the ones that read every message to show 50.
            Assert.True(measured[0].Pages * 4 < measured[0].OldPages, detail);
            Assert.True(measured[1].Pages * 4 < measured[1].OldPages, detail);
            Assert.True(measured[2].Pages <= measured[2].OldPages, detail);
            db.Dispose();
        }

        [Fact]
        public async Task Summary_CostsTheIndex_NotEveryMailboxTimesEveryRow()
        {
            var (path, db, _) = await BuildUpgradedStoreAsync();
            string oldPath = await BuildOldStoreAsync(NewPath);

            var (oldStats, _, _) = await CostAsync(oldPath, OldStats);
            var (oldBoxes, _, _) = await CostAsync(oldPath, OldMailboxes);
            var (counts, countsPlan, _) = await CostAsync(path, KliveMailRepository.AddressCountsSql);
            var (trash, _, _) = await CostAsync(path, KliveMailRepository.TrashCountSql);
            var (arrivals, arrivalsPlan, _) = await CostAsync(path, KliveMailRepository.ArrivalsSql, ("$since", "2026-10-11"), ("$day", "2026-11-10T00:00:00Z"));
            int now = counts + trash + arrivals;
            string detail = $"before {oldStats} + {oldBoxes}; now {counts} + {trash} + {arrivals}. Plans: {string.Join(" / ", countsPlan)} | {string.Join(" / ", arrivalsPlan)}";

            Assert.Contains(countsPlan, step => step.Contains("COVERING INDEX idx_messages_box", StringComparison.Ordinal));
            Assert.Contains(arrivalsPlan, step => step.Contains("COVERING INDEX idx_messages_feed", StringComparison.Ordinal));
            Assert.True(now < 40, detail);
            Assert.True(now * 20 < oldStats + oldBoxes, detail);
            db.Dispose();
        }

        [Fact]
        public async Task ThreadAndSearch_ReadOnlyTheRowsTheyReturn()
        {
            var (path, db, repo) = await BuildUpgradedStoreAsync();
            string oldPath = await BuildOldStoreAsync(NewPath);

            var (thread, threadPlan, threadRows) = await CostAsync(path, KliveMailRepository.ThreadSql, ("$thread", "thread-shared"));
            var (oldThread, _, oldThreadRows) = await CostAsync(oldPath, OldThread, ("$thread", "thread-shared"));
            Assert.Equal(oldThreadRows, threadRows);
            Assert.Contains(threadPlan, step => step.Contains("idx_messages_conv", StringComparison.Ordinal));
            Assert.True(thread <= threadRows * 3 + 12, $"thread {thread} pages ({oldThread} before) for {threadRows} rows");

            // "verification" is in a quarter of the subjects: a search with many hits.
            var (search, searchPlan, hits) = await CostAsync(path, KliveMailRepository.SearchSql(false, false), ("$q", "verification*"), ("$limit", 50), ("$offset", 0));
            var (oldSearch, _, oldHits) = await CostAsync(oldPath, OldSearch, ("$q", "verification*"));
            string detail = $"search: {oldSearch} reads before, {search} now, {hits} hits. Plan: {string.Join(" / ", searchPlan)}";
            Assert.Equal(oldHits, hits);
            // Both read the full-text index's content row for every match to learn its message id
            // (91 here); after that the old query fetched and sorted every matching message, the new
            // one reads the date index and the first page of the 50 it returns.
            Assert.True(search <= hits * 3 + 40, detail);
            Assert.True(search < oldSearch, detail);

            // And they agree with each other on what was found.
            var found = await repo.SearchAsync("verification");
            Assert.Equal(hits, found.Count);
            Assert.All(found, m => Assert.Matches("^[0-9]+ is your verification code$", m.Subject!));
            db.Dispose();
        }
    }
}
