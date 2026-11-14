using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveMail.Models;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Tests.KliveMail
{
    /// <summary>
    /// Lists come from memory between writes. Live 13 Nov 2026: 101 of 122 list calls in a day were the mail page re-polling
    /// the same first page, each opening SQLite on the server's slow D: volume (p50 0.7 ms, p95 4.0 s, max 16 s). A repeat must
    /// not touch the store, and no write may ever leave a stale list behind.
    /// </summary>
    public sealed class KliveMailListMemoTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "klivemail-memo-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly KliveMailDb db;
        private readonly KliveMailRepository repo;
        private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        public KliveMailListMemoTests()
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

        private static StoredMessage Mail(string id, string to, DateTime received) => new()
        {
            Id = id, ToAddress = to, FromAddress = "sender@example.com", FromName = "Sender", Subject = "Hello " + id,
            DateUtc = received, ReceivedUtc = received, ThreadId = id, BodyText = "Body",
        };

        private async Task<List<string>> Ids(MessageQuery q) => (await repo.ListMessagesAsync(q)).Select(m => m.Id).ToList();

        [Fact]
        public async Task ARepeatedListIsAnsweredFromMemory()
        {
            for (int i = 0; i < 5; i++) await repo.InsertMessageAsync(Mail("m" + i, "a@klive.dev", Now.AddMinutes(-i)));
            long before = repo.ListQueries;

            var first = await Ids(new MessageQuery());
            for (int poll = 0; poll < 10; poll++) Assert.Equal(first, await Ids(new MessageQuery()));

            Assert.Equal(before + 1, repo.ListQueries);
            // Another view of the store is its own query.
            await Ids(new MessageQuery { UnreadOnly = true });
            await Ids(new MessageQuery { Page = 2, PageSize = 2 });
            Assert.Equal(before + 3, repo.ListQueries);
        }

        [Fact]
        public async Task EveryKindOfWriteIsSeenByTheNextList()
        {
            await repo.InsertMessageAsync(Mail("old", "a@klive.dev", Now.AddMinutes(-10)));
            Assert.Equal(new[] { "old" }, await Ids(new MessageQuery()));

            // New mail arrives.
            await repo.InsertMessageAsync(Mail("new", "a@klive.dev", Now));
            Assert.Equal(new[] { "new", "old" }, await Ids(new MessageQuery()));

            // Read.
            Assert.Equal(new[] { "new", "old" }, await Ids(new MessageQuery { UnreadOnly = true }));
            await repo.SetReadAsync("new", true);
            Assert.Equal(new[] { "old" }, await Ids(new MessageQuery { UnreadOnly = true }));
            await repo.MarkAllReadAsync(null);
            Assert.Empty(await Ids(new MessageQuery { UnreadOnly = true }));

            // Trash and back.
            await repo.SetDeletedManyAsync(new[] { "old" }, true);
            Assert.Equal(new[] { "new" }, await Ids(new MessageQuery()));
            Assert.Equal(new[] { "old" }, await Ids(new MessageQuery { Trash = true }));
            await repo.SetDeletedManyAsync(new[] { "old" }, false);
            Assert.Equal(new[] { "new", "old" }, await Ids(new MessageQuery()));
            Assert.Empty(await Ids(new MessageQuery { Trash = true }));
        }

        [Fact]
        public async Task ACallerChangingItsListDoesNotChangeTheNextOne()
        {
            await repo.InsertMessageAsync(Mail("one", "a@klive.dev", Now));
            var mine = await repo.ListMessagesAsync(new MessageQuery());
            mine.Clear();
            Assert.Single(await repo.ListMessagesAsync(new MessageQuery()));
        }
    }
}
