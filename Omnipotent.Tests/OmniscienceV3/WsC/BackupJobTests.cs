using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Modules.DataControls;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// The nightly backup on the server (20 Nov 2026): a 15 GB corpus copied in one call that could neither report progress
    /// nor be stopped, needing 30 GB on a drive with 21 GB free. It was stopped for "no progress" after half an hour, left
    /// running, and started again, ten times over two nights. These tests hold it to: no room, no attempt, and a standing
    /// problem that says what is needed; a copy that is one snapshot however long it takes, says how far it is, and stops
    /// when told, leaving nothing behind.
    /// </summary>
    [Collection(nameof(BackupJobCollection))]
    public class BackupJobTests
    {
        private const string General = SignalsHarness.General, Ben = SignalsHarness.Ben;

        private static long Count(SqliteStore store, string sql)
        {
            using var c = store.OpenRead();
            return c.Scalar<long>(null, sql);
        }

        [Fact]
        public async Task WithNoRoom_TheBackupIsNotStarted_TheJobEnds_AndTheHealthCheckSaysWhatItNeeds()
        {
            if (!OperatingSystem.IsWindows()) return; // the key is protected with Windows DPAPI
            await using var h = await SignalsHarness.StartAsync(datacontrols: true, monitors: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            await h.PostAsync(General, Ben, "a pangolin walked past the window");
            data.FreeSpace = _ => 4096;

            var refused = await Assert.ThrowsAsync<DataControlsModule.BackupSpaceException>(() => data.BackupAsync("manual", default));
            Assert.True(refused.NeededBytes > 256L * 1024 * 1024 && refused.FreeBytes == 4096);
            Assert.False(Directory.Exists(stores.Paths.Backups) && Directory.GetDirectories(stores.Paths.Backups, "bk*").Length > 0);

            // As a job: it ends (nothing to retry, no dead job), and the problem stands until a backup is taken.
            long jobId = await data.RequestBackupAsync(default);
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
            var job = h.Runtime.Host.Jobs.Get(jobId)!;
            Assert.Equal(JobStates.Succeeded, job.State);
            Assert.Contains("\"skipped\"", job.ResultJson);
            var problem = Assert.Single(ModuleProblems.Open(stores.Intel));
            Assert.Equal((DataControlsModule.ModuleId, DataControlsModule.BackupProblem), (problem.ModuleId, problem.Problem));
            Assert.Contains("no_space", problem.DetailJson);
            Assert.Contains(DataControlsModule.BackupFolderSetting, problem.DetailJson);

            h.Clock.Advance(TimeSpan.FromMinutes(11)); // the health check runs every ten minutes
            await h.Driver.RunUntilIdleAsync(3_000);
            var issue = h.Monitors!.Health().Open.Single(i => i.Kind == "module_problem");
            Assert.Equal(DataControlsModule.ModuleId, issue.Subject);
            Assert.Equal("no_space", issue.Detail.GetProperty("detail").GetProperty("reason").GetString());

            // Room again: the backup is taken and the problem is gone, from the list and from the health check.
            data.FreeSpace = _ => long.MaxValue;
            await data.RequestBackupAsync(default);
            await h.Driver.RunUntilIdleAsync(3_000);
            h.Driver.AssertNoFailures();
            Assert.Single(data.Backups().Backups);
            Assert.Empty(ModuleProblems.Open(stores.Intel));
            h.Clock.Advance(TimeSpan.FromMinutes(11)); // the health check runs every ten minutes
            await h.Driver.RunUntilIdleAsync(3_000);
            Assert.DoesNotContain(h.Monitors.Health().Open, i => i.Kind == "module_problem");
        }

        [Fact]
        public void TheRoomABackupNeeds_IsItsLargestMoment()
        {
            const long Gb = 1024L * 1024 * 1024, margin = 256L * 1024 * 1024;
            // The server: a 15 GB corpus and a small operator store. Two copies of the corpus at once, at the worst.
            Assert.Equal(30 * Gb + margin, DataControlsModule.BytesNeeded(new[] { 15 * Gb, 1L }));
            // A small store first, then a large one: the encrypted small one is still there while the large one has two copies.
            Assert.Equal(1 * Gb + 2 * 15 * Gb + margin, DataControlsModule.BytesNeeded(new[] { 1 * Gb, 15 * Gb }));
        }

        [Fact]
        public async Task TheCopyIsOneMoment_HoweverManyStepsItTakes_AndSaysHowFarItIs()
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            for (int i = 0; i < 40; i++) await h.PostAsync(General, Ben, "before the backup " + i + " " + new string('x', 900));
            long before = Count(stores.Corpus, "SELECT COUNT(*) FROM messages");
            int previous = DataControlsModule.CopyPagesPerStep;
            DataControlsModule.CopyPagesPerStep = 2;
            try
            {
                var seen = new List<string>();
                int writes = 0;
                var backup = await data.BackupAsync("manual", default, async p =>
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(p);
                    seen.Add(json);
                    // The corpus is written while it is being copied, as it always is on the server.
                    if (json.Contains("\"corpus\"") && json.Contains("\"copy\"") && writes < 5)
                    {
                        writes++;
                        await h.PostAsync(General, Ben, "written during the backup " + writes);
                    }
                });

                Assert.Equal(5, writes);
                Assert.True(seen.Count(s => s.Contains("\"copy\"")) > 10, "the copy went in many steps and reported each");
                Assert.Equal(before + 5, Count(stores.Corpus, "SELECT COUNT(*) FROM messages"));
                var restored = await data.RestoreAsync(backup.BackupId, default); // verifies the hash and SQLite's own integrity check
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={restored.Files[0]};Mode=ReadOnly;Pooling=False");
                c.Open();
                // Exactly what was there when the copy began: none of the five, and nothing torn.
                Assert.Equal(before, c.Scalar<long>(null, "SELECT COUNT(*) FROM messages"));
                Assert.Equal(0, c.Scalar<long>(null, "SELECT COUNT(*) FROM messages WHERE content LIKE 'written during the backup%'"));
            }
            finally
            {
                DataControlsModule.CopyPagesPerStep = previous;
            }
        }

        [Fact]
        public async Task ABackupToldToStop_Stops_AndLeavesNoCopyBehind()
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            for (int i = 0; i < 40; i++) await h.PostAsync(General, Ben, "a message " + i + " " + new string('x', 900));
            int previous = DataControlsModule.CopyPagesPerStep;
            DataControlsModule.CopyPagesPerStep = 2;
            try
            {
                using var cts = new CancellationTokenSource();
                int steps = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => data.BackupAsync("manual", cts.Token, _ =>
                {
                    if (++steps == 3) cts.Cancel();
                    return Task.CompletedTask;
                }));

                Assert.Equal(3, steps);
                Assert.Empty(data.Backups().Backups);
                // No clear copy and no half-written encrypted one: each would be the size of the database.
                Assert.Empty(Directory.GetFiles(stores.Paths.Backups, "*.db*", SearchOption.AllDirectories));
            }
            finally
            {
                DataControlsModule.CopyPagesPerStep = previous;
            }
        }

        [Fact]
        public async Task BackupsGoToTheFolderKlivesNames_AndTheOnesTakenBeforeAreStillListed()
        {
            if (!OperatingSystem.IsWindows()) return;
            await using var h = await SignalsHarness.StartAsync(datacontrols: true);
            var data = h.Data!;
            var stores = h.Runtime.Stores;
            await h.PostAsync(General, Ben, "a pangolin walked past the window");
            var first = await data.BackupAsync("manual", default);
            Assert.True(Directory.Exists(Path.Combine(stores.Paths.Backups, first.BackupId)));

            string elsewhere = Path.Combine(Path.GetTempPath(), "omniv3_bk_" + Guid.NewGuid().ToString("N"));
            try
            {
                await h.Settings.SetAsync(DataControlsModule.BackupFolderSetting, elsewhere, "test");
                var asked = new List<string>();
                data.FreeSpace = folder => { asked.Add(folder); return long.MaxValue; };
                h.Clock.Advance(TimeSpan.FromMinutes(1));
                var second = await data.BackupAsync("manual", default);

                Assert.Equal(new[] { elsewhere }, asked); // the room is measured where the backup will be written
                Assert.True(File.Exists(Path.Combine(elsewhere, second.BackupId, "manifest.json")));
                Assert.Equal(new[] { second.BackupId, first.BackupId }, data.Backups().Backups.Select(b => b.BackupId));
                await data.RestoreAsync(second.BackupId, default);
                await data.RestoreAsync(first.BackupId, default);
                // The key stays beside the databases, not with the backups it opens.
                Assert.True(File.Exists(Path.Combine(stores.Paths.Backups, "backup-key.dpapi")));
                Assert.False(File.Exists(Path.Combine(elsewhere, "backup-key.dpapi")));
            }
            finally
            {
                try { Directory.Delete(elsewhere, true); }
                catch (IOException) { /* unique temp dir, or never created */ }
            }
        }
    }

    /// <summary>These tests set how many pages a backup copies per step (process-wide): they do not run beside each other.</summary>
    [CollectionDefinition(nameof(BackupJobCollection), DisableParallelization = true)]
    public class BackupJobCollection { }
}
