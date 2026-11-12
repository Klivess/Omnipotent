using System.Text;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ingest.Imports;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Ingest
{
    /// <summary>The import inbox: what an upload may be called, and exports dropped into the folder being picked up.</summary>
    public class ImportInboxTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_inbox_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)));
        private OmniscienceStores stores = null!;
        private ModuleHost host = null!;
        private readonly ImportModule module = new();

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(Path.Combine(root, "v3")), clock);
            var lease = TimeSpan.FromSeconds(5);
            var poll = TimeSpan.FromMilliseconds(20);
            host = new ModuleHost(stores, clock, new KernelOptions
            {
                StartLoop = false,
                Lanes = new[]
                {
                    new LaneOptions(Lane.Cpu, 1, lease, poll), new LaneOptions(Lane.Io, 1, lease, poll), new LaneOptions(Lane.LlmBackground, 1, lease, poll),
                    new LaneOptions(Lane.LlmInteractive, 1, lease, poll), new LaneOptions(Lane.Coordinator, 1, lease, poll),
                },
            }, _ => { }, (_, _) => { });
            host.Register(module);
            await host.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await host.DisposeAsync();
            await stores.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
        }

        private string Drop(string name, string text, TimeSpan age)
        {
            string path = Path.Combine(module.InboxPath, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, Instant.ToUtcDateTime(clock.NowMs) - age);
            return path;
        }

        [Fact]
        public void AnUploadTakesAPlainFreeName_AndIsNotOfferedUntilComplete()
        {
            string target = module.NewInboxFile("holiday chat.zip");
            Assert.Equal(Path.Combine(module.InboxPath, "holiday chat.zip"), target);

            foreach (string bad in new[] { "", "..", "a/b.zip", "a\\b.zip", "C:evil.zip", "half.zip" + ImportModule.UploadingSuffix })
                Assert.Throws<InvalidDataException>(() => module.NewInboxFile(bad));

            Drop("taken.txt", "x", TimeSpan.Zero);
            Assert.Contains("already has", Assert.Throws<InvalidDataException>(() => module.NewInboxFile("taken.txt")).Message);

            // A file still arriving exists on disk under its temporary name, but cannot be previewed or committed.
            Drop("big.zip" + ImportModule.UploadingSuffix, "partial", TimeSpan.Zero);
            Assert.Contains("still being uploaded", Assert.Throws<InvalidDataException>(() => module.ResolveInboxEntry("big.zip" + ImportModule.UploadingSuffix)).Message);
        }

        [Fact]
        public async Task ExportsDroppedInTheFolder_ArePreviewedOnce_WhenTheyHaveSettled()
        {
            Drop("settled.txt", "12/03/2024, 09:15 - Robin: morning\n12/03/2024, 09:16 - Jo: hi\n", TimeSpan.FromMinutes(10));
            Drop("still copying.txt", "12/03/2024, 09:15 - Robin: morning\n", TimeSpan.FromSeconds(20));
            Drop("arriving.zip" + ImportModule.UploadingSuffix, "partial", TimeSpan.FromMinutes(10));

            async Task<int> ScanAsync()
            {
                long id = await host.Jobs.EnqueueAsync(new JobRequest { ModuleId = ImportModule.ModuleId, Kind = ImportModule.ScanKind, Lane = Lane.Io });
                var deadline = Environment.TickCount64 + 15_000;
                while (host.Jobs.Get(id)!.State != JobStates.Succeeded)
                {
                    Assert.True(Environment.TickCount64 < deadline, "the scan did not finish: " + host.Jobs.Get(id)!.ErrorMessage);
                    await Task.Delay(20);
                }
                return host.Jobs.Query(new JobQuery { ModuleId = ImportModule.ModuleId, Kind = ImportModule.PreviewKind, Limit = 50 }).Items.Count;
            }

            Assert.Equal(1, await ScanAsync());
            var preview = host.Jobs.Query(new JobQuery { ModuleId = ImportModule.ModuleId, Kind = ImportModule.PreviewKind, Limit = 50 }).Items.Single();
            Assert.Equal("settled.txt", preview.SubjectId);

            // Looked at already: a second pass queues nothing more.
            Assert.Equal(1, await ScanAsync());
        }
    }
}
