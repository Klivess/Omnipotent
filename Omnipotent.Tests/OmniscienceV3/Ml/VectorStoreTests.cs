using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Ml
{
    /// <summary>vectors.db access: int8 storage, exact and partitioned search, and the IVF index with its rebuild transition.</summary>
    public class VectorStoreTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_vectors_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(1_700_000_000_000);
        private OmniscienceStores stores = null!;
        private VectorStore vectors = null!;

        public async Task InitializeAsync()
        {
            stores = await OmniscienceStores.OpenAsync(new StorePaths(root), clock);
            vectors = new VectorStore(stores.Vectors, clock);
            await vectors.InitializeAsync();
        }

        public async Task DisposeAsync()
        {
            await stores.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        /// <summary>Unit vectors scattered around <paramref name="clusters"/> random centres (deterministic).</summary>
        private static List<(string Id, float[] V)> Clustered(int n, int clusters, int dims, int seed, float spread = 0.35f)
        {
            var rng = new Random(seed);
            var centres = Enumerable.Range(0, clusters).Select(_ => VectorMath.Normalized(Enumerable.Range(0, dims).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray())).ToArray();
            return Enumerable.Range(0, n).Select(i =>
            {
                var c = centres[i % clusters];
                var v = c.Select(x => x + spread * (float)(rng.NextDouble() * 2 - 1) / MathF.Sqrt(dims) * 4).ToArray();
                return ($"item{i:D6}", VectorMath.Normalized(v));
            }).ToList();
        }

        [Fact]
        public void Int8Quantisation_KeepsDotProductsClose()
        {
            var rng = new Random(3);
            for (int t = 0; t < 50; t++)
            {
                var a = VectorMath.Normalized(Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray());
                var b = VectorMath.Normalized(Enumerable.Range(0, 384).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray());
                var (q, scale) = VectorMath.QuantizeInt8(b);
                float exact = VectorMath.Dot(a, b);
                float approx = VectorMath.DotInt8(a, q, scale, new float[384]);
                Assert.InRange(approx - exact, -0.01f, 0.01f);
            }
            var (zero, s) = VectorMath.QuantizeInt8(new float[16]);
            Assert.All(zero, x => Assert.Equal(0, x));
            Assert.Equal(1f, s);
        }

        [Fact]
        public async Task Upsert_ReplacesByModelAndItem_AndModelsNeverMix()
        {
            var a = VectorMath.Normalized(new float[] { 1, 0, 0, 0 });
            var b = VectorMath.Normalized(new float[] { 0, 1, 0, 0 });
            await vectors.UpsertAsync("message", "m1", new[] { new VectorItem("x", a, 1) });
            await vectors.UpsertAsync("message", "m1", new[] { new VectorItem("x", b, 2) }); // the message was edited: version 2 replaces version 1
            await vectors.UpsertAsync("message", "m2", new[] { new VectorItem("x", a, 1) });
            var got = vectors.Get("message", "m1", new[] { "x", "missing" });
            Assert.Single(got);
            Assert.Equal(2, got["x"].SourceVersion);
            Assert.True(VectorMath.Dot(got["x"].Vector, b) > 0.99f);
            Assert.Equal(1, vectors.Count("message", "m1"));
            Assert.Equal(1, vectors.Count("message", "m2"));
            Assert.Equal(2, await vectors.DeleteEverywhereAsync("message", new[] { "x" }));
            Assert.Equal(0, vectors.Count("message", "m1") + vectors.Count("message", "m2"));
        }

        [Fact]
        public async Task Upsert_RejectsNonFiniteVectors()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => vectors.UpsertAsync("message", "m1", new[] { new VectorItem("x", new[] { float.NaN, 1f }, 1) }));
            await Assert.ThrowsAsync<ArgumentException>(() => vectors.UpsertAsync("message", "m1", new[] { new VectorItem("x", Array.Empty<float>(), 1) }));
        }

        [Fact]
        public async Task Search_ExactOverCandidatesPartitionsAndWholeSpace()
        {
            var data = Clustered(600, 6, 32, seed: 5);
            await vectors.UpsertAsync("message", "m", data.Select((d, i) => new VectorItem(d.Id, d.V, 1, i % 2 == 0 ? "even" : "odd")).ToList());
            var query = data[10].V;

            var all = vectors.Search("message", "m", query, 5);
            Assert.Equal(data[10].Id, all[0].ItemId);
            Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Score >= p.Second.Score), "hits are sorted by score");

            var candidates = data.Skip(100).Take(50).Select(d => d.Id).ToList();
            var scoped = vectors.Search("message", "m", query, 5, new VectorFilter(Candidates: candidates));
            Assert.All(scoped, h => Assert.Contains(h.ItemId, candidates));

            var odd = vectors.Search("message", "m", query, 5, new VectorFilter(Partitions: new[] { "odd" }));
            Assert.All(odd, h => Assert.Equal(1, int.Parse(h.ItemId[4..]) % 2));

            var accepted = vectors.Search("message", "m", query, 5, new VectorFilter(Accept: id => id != data[10].Id));
            Assert.DoesNotContain(accepted, h => h.ItemId == data[10].Id);
            Assert.Equal(5, accepted.Count);
        }

        [Fact]
        public async Task Ivf_RecallStaysHighAndSurvivesARebuild()
        {
            var data = Clustered(4_000, 40, 48, seed: 9);
            await vectors.UpsertAsync("message", "m", data.Select(d => new VectorItem(d.Id, d.V, 1)).ToList());
            var queries = Enumerable.Range(0, 40).Select(i => data[i * 97 % data.Count].V).ToList();
            var exact = queries.Select(q => vectors.Search("message", "m", q, 10).Select(h => h.ItemId).ToHashSet()).ToList();

            int v1 = await vectors.TrainAsync("message", "m", lists: 32, seed: 2);
            Assert.Equal(1, v1);
            while (await vectors.ReassignStepAsync("message", "m", batch: 700) > 0) { }
            var status = vectors.Status("message", "m")!;
            Assert.Equal(0, status.UnassignedOrStale);
            Assert.Null(status.PendingVersion);

            double Recall(int probes) => queries.Select((q, i) =>
                    (double)vectors.Search("message", "m", q, 10, new VectorFilter(Probes: probes)).Count(h => exact[i].Contains(h.ItemId)) / 10).Average();
            Assert.True(Recall(8) >= 0.9, $"recall@10 with 8 probes was {Recall(8):0.00}");

            // A rebuild: version 2 is pending while rows move in small steps. Mid-move, searches probe both versions.
            int v2 = await vectors.TrainAsync("message", "m", lists: 24, seed: 7);
            Assert.Equal(2, v2);
            await vectors.ReassignStepAsync("message", "m", batch: 1_500);
            var mid = vectors.Status("message", "m")!;
            Assert.Equal(2, mid.PendingVersion);
            Assert.True(mid.UnassignedOrStale > 0);
            Assert.True(Recall(8) >= 0.9, "recall must not dip while an index rebuild is half done");

            // New rows during a rebuild are filed under the newest version.
            var fresh = VectorMath.Normalized(data[0].V.Select(x => x + 0.001f).ToArray());
            await vectors.UpsertAsync("message", "m", new[] { new VectorItem("fresh", fresh, 1) });
            Assert.StartsWith("ivf2:", vectors.Get("message", "m", new[] { "fresh" })["fresh"].PartitionKey);

            while (await vectors.ReassignStepAsync("message", "m", batch: 1_500) > 0) { }
            var done = vectors.Status("message", "m")!;
            Assert.Equal(2, done.ActiveVersion);
            Assert.Null(done.PendingVersion);
            Assert.Equal(0, done.UnassignedOrStale);
            Assert.True(Recall(8) >= 0.9);
            using var c = stores.Vectors.OpenRead();
            Assert.Equal(1, c.Scalar<long>(null, "SELECT COUNT(*) FROM vector_ivf WHERE space='message' AND model='m'"));
        }

        [Fact]
        public async Task Ivf_TrainingIsDeterministicForASeed()
        {
            var data = Clustered(1_000, 10, 16, seed: 4);
            await vectors.UpsertAsync("message", "m", data.Select(d => new VectorItem(d.Id, d.V, 1)).ToList());
            var a = KMeans.Spherical(data.Select(d => d.V).ToList(), 10, 10, seed: 3, CancellationToken.None);
            var b = KMeans.Spherical(data.Select(d => d.V).ToList(), 10, 10, seed: 3, CancellationToken.None);
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
        }

        [Fact]
        public async Task Search_SkipsVectorsOfAnotherDimensionality()
        {
            await vectors.UpsertAsync("message", "m", new[]
            {
                new VectorItem("short", VectorMath.Normalized(new float[] { 1, 0 }), 1),
                new VectorItem("long", VectorMath.Normalized(new float[] { 1, 0, 0, 0 }), 1),
            });
            var hits = vectors.Search("message", "m", new float[] { 1, 0, 0, 0 }, 5);
            Assert.Equal(new[] { "long" }, hits.Select(h => h.ItemId));
        }
    }
}
