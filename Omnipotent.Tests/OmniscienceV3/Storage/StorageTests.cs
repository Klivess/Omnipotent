using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Schemas;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Storage
{
    /// <summary>Storage foundation: migrations, the single writer with per-operation savepoints, read-only
    /// readers, the change log and FTS synchronisation. Temp files only.</summary>
    public class StorageTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_storage_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(1_700_000_000_000);
        private OmniscienceStores stores = null!;

        public async Task InitializeAsync() => stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; temp dir is unique per test */ }
        }

        [Fact]
        public async Task Migrations_AreRecordedAndIdempotentAcrossReopen()
        {
            using (var c = stores.Corpus.OpenRead())
                Assert.Equal(CorpusSchema.Migrations.Max(m => m.Version), SchemaMigrator.AppliedVersion(c, CorpusSchema.ModuleId));

            await stores.DisposeAsync();
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            int again = await stores.Intel.ApplyMigrationsAsync(IntelSchema.Migrations);
            Assert.Equal(0, again);
        }

        [Fact]
        public async Task EditedShippedMigration_IsRejected()
        {
            var tampered = new[] { new Migration(IntelSchema.ModuleId, 1, "tampered", "CREATE TABLE x(a INTEGER);") };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stores.Intel.ApplyMigrationsAsync(tampered));
            Assert.Contains("changed after it was applied", ex.Message);
        }

        [Fact]
        public async Task NonContiguousModuleVersions_AreRejected()
        {
            var gap = new[]
            {
                new Migration("test.module", 1, "one", "CREATE TABLE t1(a INTEGER);"),
                new Migration("test.module", 3, "three", "CREATE TABLE t3(a INTEGER);"),
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => stores.Intel.ApplyMigrationsAsync(gap));
        }

        [Fact]
        public async Task ModuleMigrations_ApplyWhileLive()
        {
            var mod = new[] { new Migration("test.live", 1, "table", "CREATE TABLE live_t(a INTEGER);") };
            Assert.Equal(1, await stores.Intel.ApplyMigrationsAsync(mod));
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO live_t(a) VALUES(1)"));
            using var r = stores.Intel.OpenRead();
            Assert.Equal(1L, r.Scalar<long>(null, "SELECT COUNT(*) FROM live_t"));
        }

        [Fact]
        public async Task OneFailingWrite_DoesNotRollBackItsBatchNeighbours()
        {
            await stores.Intel.ApplyMigrationsAsync(new[] { new Migration("test.batch", 1, "t", "CREATE TABLE bt(k INTEGER PRIMARY KEY);") });
            var tasks = new List<Task>();
            for (int i = 0; i < 300; i++)
            {
                int k = i;
                tasks.Add(stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO bt(k) VALUES($k)", ("$k", k))));
            }
            // Duplicate key: fails alone.
            var failing = stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO bt(k) VALUES(5)"));
            for (int i = 300; i < 400; i++)
            {
                int k = i;
                tasks.Add(stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO bt(k) VALUES($k)", ("$k", k))));
            }
            await Task.WhenAll(tasks);
            await Assert.ThrowsAsync<SqliteException>(() => failing);
            using var r = stores.Intel.OpenRead();
            Assert.Equal(400L, r.Scalar<long>(null, "SELECT COUNT(*) FROM bt"));
        }

        [Fact]
        public async Task CancelledWrite_IsNeverApplied()
        {
            await stores.Intel.ApplyMigrationsAsync(new[] { new Migration("test.cancel", 1, "t", "CREATE TABLE ct(k INTEGER);") });
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var write = stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO ct(k) VALUES(1)"), cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "SELECT 1"));
            using var r = stores.Intel.OpenRead();
            Assert.Equal(0L, r.Scalar<long>(null, "SELECT COUNT(*) FROM ct"));
        }

        [Fact]
        public void ReadConnections_CannotWrite()
        {
            using var r = stores.Operator.OpenRead();
            Assert.ThrowsAny<SqliteException>(() =>
                r.Exec(null, "INSERT INTO settings(key, value_json, updated_at_ms) VALUES('a','1',1)"));
        }

        [Fact]
        public async Task ChangeLog_AppendsInSequenceWithKnownSourceKinds()
        {
            long a = await stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, "discord:1", "p1", "discord:c1", SourceKinds.Live));
            long b = await stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, "discord:2", "p1", "discord:c1", SourceKinds.Backfill));
            Assert.True(b > a);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, "x", null, null, "bogus")));
            using var r = stores.Corpus.OpenRead();
            var after = ChangeLog.ReadAfter(r, a, 10);
            Assert.Single(after);
            Assert.Equal("discord:2", after[0].EntityId);
            Assert.Equal(SourceKinds.Backfill, after[0].SourceKind);
            Assert.Equal(b, ChangeLog.MaxSeq(r));
        }

        [Fact]
        public async Task FullTextIndex_FollowsInsertUpdateAndDelete()
        {
            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO messages(message_id, platform, platform_message_id, conversation_id, author_identity_id, sent_at_ms,
                  time_quality, order_key, current_version, content, first_source_kind, best_source_id, best_source_rank,
                  ingested_at_ms, ingest_seq)
                VALUES('discord:9','discord','9','discord:c','discord:u',1,0,0,1,'the quick brown fox','live','s',0,1,1)
                """));
            using (var r = stores.Corpus.OpenRead())
                Assert.Equal(1L, r.Scalar<long>(null, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'brown'"));

            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "UPDATE messages SET content='a lazy dog' WHERE message_id='discord:9'"));
            using (var r = stores.Corpus.OpenRead())
            {
                Assert.Equal(0L, r.Scalar<long>(null, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'brown'"));
                Assert.Equal(1L, r.Scalar<long>(null, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'lazy'"));
            }

            await stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM messages WHERE message_id='discord:9'"));
            using (var r = stores.Corpus.OpenRead())
                Assert.Equal(0L, r.Scalar<long>(null, "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'lazy'"));
        }

        [Fact]
        public async Task DisposedStore_RejectsWrites()
        {
            var path = Path.Combine(root, "solo.db");
            var store = new SqliteStore("solo", path, clock);
            await store.InitializeAsync(Array.Empty<Migration>());
            await store.DisposeAsync();
            // The guard throws synchronously, before any task is created.
            Assert.Throws<ObjectDisposedException>(() => { _ = store.WriteAsync((c, tx) => 1); });
        }

        [Fact]
        public void Health_ReportsFileSizes()
        {
            var h = stores.Corpus.GetHealth();
            Assert.Equal("corpus", h.Name);
            Assert.True(h.FileBytes > 0);
        }
    }
}
