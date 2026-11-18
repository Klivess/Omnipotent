using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The dashboard's reads of the Asset Library, at the live library's size and mix (13 Nov 2026: 43,677 models —
    /// 23,596 labelled, 14,324 rejected, 4,740 previewed, 1,017 failed).
    ///
    /// Live: the Assets page's default view (/omniblox/assets?limit=60) timed out at 60 s on the server, and its
    /// worst load took 79 minutes; the studio overview's first poll after a start waited 4 to 19 minutes on the
    /// library lock; and the harvester, switched on, had added nothing for 9 days because 4,740 previewed models it
    /// will never download counted as its backlog.
    /// </summary>
    public sealed class AssetLibraryCostTests : IClassFixture<AssetLibraryCostTests.LiveSizedLibrary>
    {
        private readonly LiveSizedLibrary fixture;
        private readonly ITestOutputHelper output;

        public AssetLibraryCostTests(LiveSizedLibrary fixture, ITestOutputHelper output)
        {
            this.fixture = fixture;
            this.output = output;
        }

        /// <summary>A library shaped like the live one, built once for the class (rows written straight to SQLite).</summary>
        public sealed class LiveSizedLibrary : IDisposable
        {
            public const int Labeled = 23_596, Rejected = 14_324, Previewed = 4_740, Failed = 1_017;
            public const int Total = Labeled + Rejected + Previewed + Failed;
            /// <summary>Of the previewed rows, how many the download step can take (the live library had none left).</summary>
            public const int DownloadablePreviewed = 0;

            private readonly TempDir dir = new();
            public string Directory => dir.Path;
            public AssetLibrary Library { get; }

            public LiveSizedLibrary()
            {
                var library = new AssetLibrary(dir.Path);
                library.Open();
                library.Dispose();
                Fill(Path.Combine(dir.Path, "library.db"));
                Library = new AssetLibrary(dir.Path);
                Library.Open();
            }

            public void Dispose()
            {
                Library.Dispose();
                SqliteConnection.ClearAllPools();
                dir.Dispose();
            }

            private static void Fill(string file)
            {
                using var conn = new SqliteConnection($"Data Source={file}");
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO assets (asset_id, name, description, status, attempts, placeable, triangles, script_count, has_scripts,
  measurements, label, category, quality, discovered_utc, analyzed_utc, labeled_utc)
VALUES ($id, $name, $desc, $status, $attempts, $placeable, $tri, $scripts, $hasScripts, $m, $label, $cat, $q, $disc, $an, $lab)";
                var p = new Dictionary<string, SqliteParameter>();
                foreach (string name in new[] { "$id", "$name", "$desc", "$status", "$attempts", "$placeable", "$tri", "$scripts", "$hasScripts", "$m", "$label", "$cat", "$q", "$disc", "$an", "$lab" })
                    p[name] = cmd.Parameters.Add(name, SqliteType.Text);
                // A measured model's JSON columns are a few KB (class counts, colours, materials, the label): the
                // part of each row a full scan has to read.
                string measurements = "{\"SizeX\":12.5,\"SizeY\":18.2,\"SizeZ\":11.9,\"ClassCounts\":{" + string.Join(",", Enumerable.Range(0, 60).Select(i => $"\"Class{i}\":{i}")) + "},\"Colors\":[" +
                    string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{\"Hex\":\"#a{i}b{i}c{i}\",\"Name\":\"Colour {i}\",\"Share\":0.1}}")) + "]}";
                string label = "{\"What\":\"a low-poly pine tree\",\"Category\":\"nature\",\"Description\":\"" + new string('x', 600) + "\",\"Themes\":[\"forest\",\"snow\"],\"Quality\":7}";
                var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
                int id = 0;
                void Add(int count, string status, Func<int, (int Attempts, long Triangles, int Scripts)> shape)
                {
                    for (int i = 0; i < count; i++, id++)
                    {
                        var (attempts, triangles, scripts) = shape(i);
                        var found = start.AddMinutes(id);
                        p["$id"].Value = 100_000 + id;
                        p["$name"].Value = $"Model {id}";
                        p["$desc"].Value = "A free model from the Creator Store, " + new string('y', 160);
                        p["$status"].Value = status;
                        p["$attempts"].Value = attempts;
                        p["$placeable"].Value = status is "labeled" or "failed" ? 1 : 0;
                        p["$tri"].Value = triangles;
                        p["$scripts"].Value = scripts;
                        p["$hasScripts"].Value = scripts > 0 ? 1 : 0;
                        p["$m"].Value = status == "previewed" ? DBNull.Value : measurements;
                        p["$label"].Value = status == "labeled" ? label : DBNull.Value;
                        p["$cat"].Value = status == "labeled" ? (i % 3 == 0 ? "nature" : i % 3 == 1 ? "prop" : "structure") : "";
                        p["$q"].Value = status == "labeled" ? 5 + i % 5 : 0;
                        p["$disc"].Value = found.ToString("o");
                        p["$an"].Value = status == "previewed" ? DBNull.Value : found.AddMinutes(5).ToString("o");
                        p["$lab"].Value = status == "labeled" ? found.AddMinutes(9).ToString("o") : DBNull.Value;
                        cmd.ExecuteNonQuery();
                    }
                }
                Add(Labeled, "labeled", _ => (1, 4_000, 0));
                Add(Rejected, "rejected", _ => (1, 200_000, 20));
                Add(Failed, "failed", _ => (1, 2_000, 1));
                // What the live sample showed: about half over 120k triangles, the rest scripted past the limit or tried three times.
                Add(Previewed, "previewed", i => i % 2 == 0 ? (0, 150_000, 2) : i % 3 == 0 ? (0, 5_000, 13) : (0, 6_000, 14));
                tx.Commit();
            }
        }

        [Fact]
        public void NewestFirst_WalksAnIndex_InsteadOfSortingEveryRow()
        {
            foreach (string? status in new[] { null, AssetStatus.Previewed, AssetStatus.Labeled })
            {
                string plan = fixture.Library.ExplainRecent(status);
                output.WriteLine($"{status ?? "all"}: {plan}");
                Assert.Contains(status == null ? "ix_assets_recency" : "ix_assets_status_recency", plan);
                Assert.DoesNotContain("TEMP B-TREE", plan);
            }
        }

        [Fact]
        public void WithoutTheIndexes_TheSameReadSortsTheWholeTable()
        {
            // The live failure, on the same data: drop the indexes Open adds and the plan is a scan plus a sort.
            using var dir = new TempDir();
            File.Copy(Path.Combine(fixture.Directory, "library.db"), Path.Combine(dir.Path, "library.db"));
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir.Path, "library.db")};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DROP INDEX ix_assets_recency; DROP INDEX ix_assets_status_recency; DROP INDEX ix_assets_counts;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM assets ORDER BY COALESCE(labeled_utc, analyzed_utc, discovered_utc) DESC LIMIT 60";
                var lines = new List<string>();
                using (var r = cmd.ExecuteReader()) while (r.Read()) lines.Add(r.GetString(r.FieldCount - 1));
                string plan = string.Join(" | ", lines);
                output.WriteLine("before: " + plan);
                Assert.Contains("SCAN assets", plan);
                Assert.Contains("TEMP B-TREE FOR ORDER BY", plan);

                var timer = Stopwatch.StartNew();
                cmd.CommandText = "SELECT * FROM assets ORDER BY COALESCE(labeled_utc, analyzed_utc, discovered_utc) DESC LIMIT 60";
                int rows = 0;
                using (var r = cmd.ExecuteReader()) while (r.Read()) rows++;
                output.WriteLine($"before: {rows} rows in {timer.ElapsedMilliseconds} ms (warm cache, idle machine)");
            }

            // Opening it again builds them, once, and says so.
            var library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                Assert.NotNull(library.OpenNote);
                Assert.Contains("ix_assets_recency", library.OpenNote);
                output.WriteLine(library.OpenNote);
                var timer = Stopwatch.StartNew();
                var newest = library.Recent(null, 60);
                output.WriteLine($"after: {newest.Count} rows in {timer.ElapsedMilliseconds} ms");
                Assert.Equal(60, newest.Count);
                Assert.Equal(AssetStatus.Previewed, newest[0].Status); // the last rows written were found last
            }
            finally { library.Dispose(); }
            var again = new AssetLibrary(dir.Path);
            try { again.Open(); Assert.Null(again.OpenNote); }
            finally { again.Dispose(); SqliteConnection.ClearAllPools(); }
        }

        [Fact]
        public void NewestFirst_StillReturnsNewestFirst_AndPages()
        {
            var first = fixture.Library.Recent(AssetStatus.Labeled, 60);
            var second = fixture.Library.Recent(AssetStatus.Labeled, 60, 60);
            Assert.Equal(60, first.Count);
            Assert.Equal(60, second.Count);
            var all = first.Concat(second).Select(r => r.LabeledUtc!.Value).ToList();
            Assert.Equal(all.OrderByDescending(t => t), all);
            Assert.Empty(first.Select(r => r.AssetId).Intersect(second.Select(r => r.AssetId)));
        }

        [Fact]
        public void TheOverviewCounts_AreReadFromIndexesOnly()
        {
            string plans = fixture.Library.ExplainStats();
            output.WriteLine(plans);
            foreach (string line in plans.Split('\n').Where(l => l.Contains("assets")))
                Assert.Contains("INDEX", line);
            var stats = fixture.Library.Stats();
            Assert.False(stats.Counting);
            Assert.Equal(LiveSizedLibrary.Total, stats.Total);
            Assert.Equal(LiveSizedLibrary.Previewed, stats.ByStatus[AssetStatus.Previewed]);
            Assert.Equal(LiveSizedLibrary.DownloadablePreviewed, stats.Downloadable);
            Assert.NotNull(stats.NewestUtc);
        }

        [Fact]
        public void ACountsReader_NeverWaitsLongOnABusyLibrary()
        {
            // A fresh library has no cached counts: its first reader used to wait as long as the lock was held.
            using var dir = new TempDir();
            var library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                using var holding = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var holder = Task.Run(() => library.HoldLockFor(() => { holding.Set(); release.Wait(TimeSpan.FromSeconds(30)); }));
                Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));

                var timer = Stopwatch.StartNew();
                var stats = library.Stats();
                timer.Stop();
                release.Set();
                holder.Wait(TimeSpan.FromSeconds(10));

                Assert.True(stats.Counting);
                Assert.True(timer.Elapsed < AssetLibrary.StatsWaitCold + TimeSpan.FromSeconds(3), $"waited {timer.Elapsed}");
                // Once the lock is free the real counts come back.
                Assert.False(library.Stats().Counting);
            }
            finally { library.Dispose(); SqliteConnection.ClearAllPools(); }
        }

        [Fact]
        public void PreviewedModelsItWillNeverDownload_DoNotStopDiscovery()
        {
            // Live 13 Nov: 4,740 previewed, none downloadable, so discovery had waited since 4 Nov with nothing else to do.
            var stats = fixture.Library.Stats();
            long oldBacklog = stats.ByStatus.GetValueOrDefault(AssetStatus.Discovered) + stats.ByStatus.GetValueOrDefault(AssetStatus.Previewed);
            var h = new HarvesterState();
            var now = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
            Assert.Null(AssetHarvester.PickQuery(h, oldBacklog, now).Query);
            Assert.NotNull(AssetHarvester.PickQuery(h, AssetHarvester.Backlog(stats), now).Query);
            // A backlog the download step can work through still holds discovery back.
            Assert.Null(AssetHarvester.PickQuery(h, AssetHarvester.Backlog(new LibraryStats { Downloadable = AssetHarvester.MaxBacklog }), now).Query);
            Assert.Empty(fixture.Library.NextForDownload(4));
        }

        [Fact]
        public void TheDownloadableCount_MatchesWhatTheDownloadStepTakes()
        {
            using var dir = new TempDir();
            var library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                var found = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                library.UpsertDiscovered(new[]
                {
                    new CreatorStoreAsset { AssetId = 1, Name = "light crate", AssetTypeId = 10, Triangles = 900 },
                    new CreatorStoreAsset { AssetId = 2, Name = "huge castle", AssetTypeId = 10, Triangles = 400_000 },
                    new CreatorStoreAsset { AssetId = 3, Name = "lamp", AssetTypeId = 10, Triangles = 300 },
                }, "test", 0, found);
                library.MarkFailed(3, "x");
                library.MarkFailed(3, "x");
                library.MarkFailed(3, "x");
                var stats = library.Stats();
                var taken = library.NextForDownload(50).Select(r => r.AssetId).ToList();
                Assert.Equal(taken.Count, stats.Downloadable);
                Assert.Equal(new long[] { 1 }, taken);
            }
            finally { library.Dispose(); SqliteConnection.ClearAllPools(); }
        }

        [Fact]
        public void TheWorldCatalog_IsInvalidatedByLabels_NotByTheHarvestersOtherWrites()
        {
            using var dir = new TempDir();
            var library = new AssetLibrary(dir.Path);
            try
            {
                library.Open();
                library.UpsertDiscovered(new[] { new CreatorStoreAsset { AssetId = 7, Name = "oak", AssetTypeId = 10, Triangles = 900 } }, "tree", 0, DateTime.UtcNow);
                long before = library.CatalogVersion;
                long countsBefore = library.Version;
                library.SaveThumbnail(7, new byte[] { 137, 80, 78, 71 });
                library.MarkFailed(7, "download timed out");
                Assert.True(library.Version > countsBefore);       // the counts moved
                Assert.Equal(before, library.CatalogVersion);       // the catalog did not
                library.SaveLabel(7, new AssetLabel { What = "an oak", Category = "nature", Style = "low-poly", Quality = 7 }, DateTime.UtcNow, AssetLabeler.ScaleVersion);
                Assert.True(library.CatalogVersion > before);
            }
            finally { library.Dispose(); SqliteConnection.ClearAllPools(); }
        }

        [Fact]
        public void Measure_TheWorldCatalogRebuild()
        {
            var timer = Stopwatch.StartNew();
            var rows = fixture.Library.Labeled();
            long read = timer.ElapsedMilliseconds;
            var catalog = Omnipotent.Services.OmniBlox.Build.WorldGen.AssetCatalog.FromRecords(rows);
            output.WriteLine($"Labeled(): {rows.Count} rows in {read} ms; catalog built in {timer.ElapsedMilliseconds - read} ms more");
            Assert.Equal(LiveSizedLibrary.Labeled, rows.Count);
            Assert.NotNull(catalog);
        }
    }
}
