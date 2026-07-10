using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>
    /// Covers the startup-performance fix for OmniDefence's IP tracker: flushing must
    /// (a) write dirty records in a single batched transaction and (b) skip records that
    /// have not changed since load. Before the fix, PersistAllDirtyAsync re-persisted the
    /// entire cache one transaction at a time on every startup, saturating the shared DB
    /// lock for many minutes and hanging the (awaited) login audit write.
    /// </summary>
    public class IpThreatTrackerPersistenceTests : IDisposable
    {
        private readonly string dbPath;

        public IpThreatTrackerPersistenceTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "omnidefence-" + Guid.NewGuid().ToString("N") + ".db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
            }
        }

        private async Task<OmniDefenceStore> NewStoreAsync()
        {
            var store = new OmniDefenceStore(dbPath);
            await store.InitializeAsync();
            return store;
        }

        [Fact]
        public async Task PersistAllDirty_BatchWrites_RoundTripThroughReload()
        {
            var store = await NewStoreAsync();
            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();

            // Touch several IPs so each is created + counted, hence dirty.
            for (int i = 0; i < 5; i++) tracker.RecordOutcome("10.0.0." + i, RequestOutcome.Success);
            tracker.RecordOutcome("10.0.0.1", RequestOutcome.InvalidPassword); // second hit on one IP
            tracker.LinkProfile(tracker.GetOrCreate("10.0.0.2"), "pid-2", "Alice", 5, 1000);

            await tracker.PersistAllDirtyAsync();

            // Reload into a fresh tracker: everything must have survived.
            var reloaded = new IpThreatTracker(store);
            await reloaded.LoadAsync();

            Assert.NotNull(reloaded.Get("10.0.0.0"));
            Assert.Equal(2, reloaded.Get("10.0.0.1")!.TotalRequests);
            Assert.Equal("pid-2", reloaded.Get("10.0.0.2")!.AssociatedProfileId);
            Assert.Equal(5, reloaded.Get("10.0.0.2")!.AssociatedProfileRank);
        }

        [Fact]
        public async Task PersistAllDirty_OnlyWritesDirtyRecords()
        {
            var store = await NewStoreAsync();

            // Seed one record and persist it.
            var t1 = new IpThreatTracker(store);
            await t1.LoadAsync();
            t1.RecordOutcome("1.1.1.1", RequestOutcome.Success);
            await t1.PersistAllDirtyAsync();

            // Fresh tracker loads it into cache as CLEAN (loaded, not mutated).
            var t2 = new IpThreatTracker(store);
            await t2.LoadAsync();
            Assert.NotNull(t2.Get("1.1.1.1"));

            // Delete the row underneath the tracker.
            await store.WithLockAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM ip_records WHERE ip=$ip";
                cmd.Parameters.AddWithValue("$ip", "1.1.1.1");
                await cmd.ExecuteNonQueryAsync();
            });

            // Nothing is dirty, so flushing must NOT re-insert the deleted row.
            await t2.PersistAllDirtyAsync();

            var t3 = new IpThreatTracker(store);
            await t3.LoadAsync();
            Assert.Null(t3.Get("1.1.1.1"));
        }

        [Fact]
        public async Task PersistAllDirty_ClearsDirtySet_SecondFlushIsNoOp()
        {
            var store = await NewStoreAsync();
            var tracker = new IpThreatTracker(store);
            await tracker.LoadAsync();

            tracker.RecordOutcome("2.2.2.2", RequestOutcome.Success);
            await tracker.PersistAllDirtyAsync();

            // Delete the row, then flush again with no new mutations: dirty set was cleared
            // by the first flush, so the second must be a no-op and leave the row deleted.
            await store.WithLockAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM ip_records WHERE ip=$ip";
                cmd.Parameters.AddWithValue("$ip", "2.2.2.2");
                await cmd.ExecuteNonQueryAsync();
            });

            await tracker.PersistAllDirtyAsync();

            var reloaded = new IpThreatTracker(store);
            await reloaded.LoadAsync();
            Assert.Null(reloaded.Get("2.2.2.2"));

            // But a subsequent mutation re-dirties and persists again.
            tracker.RecordOutcome("2.2.2.2", RequestOutcome.Success);
            await tracker.PersistAllDirtyAsync();
            var reloaded2 = new IpThreatTracker(store);
            await reloaded2.LoadAsync();
            Assert.NotNull(reloaded2.Get("2.2.2.2"));
        }
    }
}
