using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ml.Vectors
{
    /// <summary>Well-known vector spaces (vectors.db <c>space</c>).</summary>
    public static class VectorSpaces
    {
        public const string Message = "message";
        public const string Stimulus = "stimulus";
        public const string Image = "image";
        public const string Face = "face";
        public const string Person = "person";
        /// <summary>Query texts of semantic watchlists and taught concepts.</summary>
        public const string Rule = "rule";
    }

    public sealed record VectorItem(string ItemId, float[] Vector, int SourceVersion, string? PartitionKey = null);

    public sealed record StoredVector(string ItemId, float[] Vector, int SourceVersion, string? PartitionKey, long CreatedAtMs);

    public sealed record VectorHit(string ItemId, float Score);

    /// <summary>
    /// Restricts a search. <see cref="Candidates"/>: score exactly these ids (a person's messages, a conversation);
    /// <see cref="Partitions"/>: scan these partition keys; neither: the whole space (through the IVF index when it has
    /// one). <see cref="Accept"/> drops hits after scoring (MinorGuard, time windows); the scan keeps going until it has
    /// <c>k</c> accepted hits or runs out of probes.
    /// </summary>
    public sealed record VectorFilter(IReadOnlyCollection<string>? Candidates = null, IReadOnlyCollection<string>? Partitions = null, int Probes = 16,
        Func<string, bool>? Accept = null);

    public sealed record IvfStatus(int ActiveVersion, int Lists, long TrainedOn, long TrainedAtMs, int? PendingVersion, long UnassignedOrStale);

    /// <summary>
    /// vectors.db access for every module (plan "vectors.db: int8-quantised embeddings … with SIMD scan plus per-subject
    /// partitions"; F534: no brute-force synchronous scan per query).
    /// <list type="bullet">
    /// <item>Vectors are stored int8 with a per-vector scale and scored with SIMD dot products against a float query.</item>
    /// <item>Every row records the model that produced it and the source version it embeds (F333/F388), so re-embedding
    /// an edited message replaces exactly its row and vectors of different models are never compared.</item>
    /// <item>Spaces too large to scan get an inverted-file (IVF) coarse index: spherical k-means centroids, each vector
    /// filed under its nearest centroid in <c>partition_key</c> ("ivf{version}:{list}"), searched by probing the lists
    /// nearest the query. Retraining writes a new version while the old one keeps answering; until every vector has
    /// moved, searches probe both, so recall never dips during a rebuild.</item>
    /// </list>
    /// Writes go through vectors.db's single writer; searches open short-lived pooled readers.
    /// </summary>
    public sealed class VectorStore
    {
        public const string MigrationModule = "ml.vectors";
        public const string Quant = "i8";
        public const int MaxLists = 4096;

        private readonly SqliteStore store;
        private readonly IClock clock;
        private readonly ConcurrentDictionary<(string, string), IvfState?> ivf = new();

        public VectorStore(SqliteStore store, IClock clock)
        {
            this.store = store;
            this.clock = clock;
        }

        public static IReadOnlyList<Migration> Migrations { get; } = new[]
        {
            new Migration(MigrationModule, 1, "IVF coarse index", """
CREATE TABLE vector_ivf(
  space TEXT NOT NULL,
  model TEXT NOT NULL,
  version INTEGER NOT NULL,
  state TEXT NOT NULL,
  lists INTEGER NOT NULL,
  dims INTEGER NOT NULL,
  centroids BLOB NOT NULL,
  trained_on INTEGER NOT NULL,
  trained_at_ms INTEGER NOT NULL,
  PRIMARY KEY(space, model, version)
);
"""),
        };

        public Task InitializeAsync(CancellationToken ct = default) => store.ApplyMigrationsAsync(Migrations, ct);

        // ── writes ───────────────────────────────────────────────────────────────

        public async Task<int> UpsertAsync(string space, string model, IReadOnlyList<VectorItem> items, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(space);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            if (items.Count == 0) return 0;
            var index = Index(space, model);
            long now = clock.NowMs;
            var rows = items.Select(i =>
            {
                if (i.Vector.Length == 0 || i.Vector.Any(x => !float.IsFinite(x))) throw new ArgumentException($"Vector for {i.ItemId} is empty or not finite.");
                var (q, scale) = VectorMath.QuantizeInt8(i.Vector);
                string? partition = index?.WriteVersion is { } wv ? PartitionKey(wv.Version, wv.Nearest(i.Vector)) : i.PartitionKey;
                return (i.ItemId, Dims: i.Vector.Length, Scale: scale, Blob: VectorMath.ToBytes(q), Partition: partition, i.SourceVersion);
            }).ToList();
            return await store.WriteAsync((c, tx) =>
            {
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO vectors(space, item_id, model, dims, quant, scale, vec, partition_key, source_version, created_at_ms)
                    VALUES($s,$i,$m,$d,'i8',$sc,$v,$p,$sv,$t)
                    ON CONFLICT(space, model, item_id) DO UPDATE SET dims=excluded.dims, quant=excluded.quant, scale=excluded.scale, vec=excluded.vec,
                        partition_key=excluded.partition_key, source_version=excluded.source_version, created_at_ms=excluded.created_at_ms
                    """, "$s", "$i", "$m", "$d", "$sc", "$v", "$p", "$sv", "$t");
                foreach (var r in rows) put.Run(space, r.ItemId, model, r.Dims, r.Scale, r.Blob, r.Partition, r.SourceVersion, now);
                return rows.Count;
            }, ct).ConfigureAwait(false);
        }

        public Task<int> DeleteAsync(string space, string model, IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            store.WriteAsync((c, tx) =>
            {
                int n = 0;
                using var del = new PreparedStatement(c, tx, "DELETE FROM vectors WHERE space=$s AND model=$m AND item_id=$i", "$s", "$m", "$i");
                foreach (var id in ids) n += del.Run(space, model, id);
                return n;
            }, ct);

        /// <summary>Removes the items from every model of a space (forget-person purge, N20).</summary>
        public Task<int> DeleteEverywhereAsync(string space, IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            store.WriteAsync((c, tx) =>
            {
                int n = 0;
                using var del = new PreparedStatement(c, tx, "DELETE FROM vectors WHERE space=$s AND item_id=$i", "$s", "$i");
                foreach (var id in ids) n += del.Run(space, id);
                return n;
            }, ct);

        // ── reads ────────────────────────────────────────────────────────────────

        public long Count(string space, string model)
        {
            using var c = store.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM vectors WHERE space=$s AND model=$m", ("$s", space), ("$m", model));
        }

        /// <summary>The stored versions of the given items (absent ids are simply missing from the result).</summary>
        public Dictionary<string, StoredVector> Get(string space, string model, IReadOnlyCollection<string> ids)
        {
            var result = new Dictionary<string, StoredVector>(StringComparer.Ordinal);
            if (ids.Count == 0) return result;
            using var c = store.OpenRead();
            foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(400))
            {
                var names = chunk.Select((_, i) => "$k" + i).ToArray();
                var args = chunk.Select((k, i) => (names[i], (object?)k)).Append(("$s", space)).Append(("$m", model)).ToArray();
                foreach (var row in c.Query(null,
                             $"SELECT item_id, scale, vec, source_version, partition_key, created_at_ms FROM vectors WHERE space=$s AND model=$m AND item_id IN ({string.Join(",", names)})",
                             r => new StoredVector(r.GetString(0), VectorMath.DequantizeInt8(VectorMath.SBytes((byte[])r[2]), (float)r.GetDouble(1)), r.GetInt32(3),
                                 r.GetStringOrNull(4), r.GetInt64(5)), args))
                    result[row.ItemId] = row;
            }
            return result;
        }

        /// <summary>Items stored after <paramref name="afterItemId"/> in id order (bounded): for training sets and rebuilds.</summary>
        public IReadOnlyList<StoredVector> Page(string space, string model, string? afterItemId, int limit)
        {
            using var c = store.OpenRead();
            return c.Query(null, """
                SELECT item_id, scale, vec, source_version, partition_key, created_at_ms FROM vectors
                WHERE space=$s AND model=$m AND ($a IS NULL OR item_id > $a) ORDER BY item_id LIMIT $l
                """, r => new StoredVector(r.GetString(0), VectorMath.DequantizeInt8(VectorMath.SBytes((byte[])r[2]), (float)r.GetDouble(1)), r.GetInt32(3),
                    r.GetStringOrNull(4), r.GetInt64(5)),
                ("$s", space), ("$m", model), ("$a", afterItemId), ("$l", Math.Clamp(limit, 1, 50_000)));
        }

        /// <summary>
        /// The <paramref name="k"/> nearest items by cosine (vectors are unit length, so the dot product). Exact over
        /// candidates or partitions; through the IVF index (both versions during a rebuild, plus never-filed rows) for the
        /// whole space; a full scan when the space has no index yet.
        /// </summary>
        public IReadOnlyList<VectorHit> Search(string space, string model, float[] query, int k, VectorFilter? filter = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
            var q = VectorMath.Normalized(query);
            var top = new TopK(k);
            var accept = filter?.Accept;
            var scratch = new float[q.Length];
            using var c = store.OpenRead();

            void Score(SqliteDataReader r)
            {
                string id = r.GetString(0);
                var vec = (byte[])r[2];
                if (vec.Length != q.Length) return; // a different dimensionality cannot be compared
                float s = VectorMath.DotInt8(q, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(vec), (float)r.GetDouble(1), scratch);
                if (top.WouldAccept(s) && (accept == null || accept(id))) top.Add(id, s);
            }

            void Run(string sql, params (string, object?)[] args)
            {
                using var cmd = c.Cmd(null, sql, args);
                using var r = cmd.ExecuteReader();
                while (r.Read()) Score(r);
            }

            if (filter?.Candidates is { } candidates)
            {
                foreach (var chunk in candidates.Distinct(StringComparer.Ordinal).Chunk(400))
                {
                    var names = chunk.Select((_, i) => "$k" + i).ToArray();
                    var args = chunk.Select((id, i) => (names[i], (object?)id)).Append(("$s", space)).Append(("$m", model)).ToArray();
                    Run($"SELECT item_id, scale, vec FROM vectors WHERE space=$s AND model=$m AND item_id IN ({string.Join(",", names)})", args);
                }
                return top.Result();
            }

            IEnumerable<string?> partitions;
            if (filter?.Partitions is { } explicitPartitions) partitions = explicitPartitions;
            else if (Index(space, model) is { } index)
            {
                int probes = Math.Clamp(filter?.Probes ?? 16, 1, MaxLists);
                partitions = index.Versions.SelectMany(v => v.NearestLists(q, probes).Select(l => (string?)PartitionKey(v.Version, l)))
                    .Append(null); // rows written before any index existed, or by a caller that chose no partition
            }
            else
            {
                Run("SELECT item_id, scale, vec FROM vectors WHERE space=$s AND model=$m", ("$s", space), ("$m", model));
                return top.Result();
            }
            foreach (var p in partitions)
            {
                if (p == null) Run("SELECT item_id, scale, vec FROM vectors WHERE space=$s AND model=$m AND partition_key IS NULL", ("$s", space), ("$m", model));
                else Run("SELECT item_id, scale, vec FROM vectors WHERE space=$s AND model=$m AND partition_key=$p", ("$s", space), ("$m", model), ("$p", p));
            }
            return top.Result();
        }

        // ── IVF index ────────────────────────────────────────────────────────────

        public static string PartitionKey(int version, int list) =>
            "ivf" + version.ToString(CultureInfo.InvariantCulture) + ":" + list.ToString(CultureInfo.InvariantCulture);

        private sealed class IvfVersion
        {
            public IvfVersion(int version, int dims, float[][] centroids)
            {
                Version = version;
                Dims = dims;
                Centroids = centroids;
            }

            public int Version { get; }
            public int Dims { get; }
            public float[][] Centroids { get; }

            public int Nearest(ReadOnlySpan<float> v)
            {
                int best = 0;
                float bestScore = float.NegativeInfinity;
                for (int i = 0; i < Centroids.Length; i++)
                {
                    float s = VectorMath.Dot(v, Centroids[i]);
                    if (s > bestScore) { bestScore = s; best = i; }
                }
                return best;
            }

            public IEnumerable<int> NearestLists(float[] q, int probes) =>
                Enumerable.Range(0, Centroids.Length).Select(i => (i, s: VectorMath.Dot(q, Centroids[i])))
                    .OrderByDescending(x => x.s).ThenBy(x => x.i).Take(probes).Select(x => x.i);
        }

        /// <summary>Active (answering) and pending (being filled) versions of one space's index.</summary>
        private sealed class IvfState
        {
            public required IvfVersion Active { get; init; }
            public IvfVersion? Pending { get; init; }
            public required long TrainedOn { get; init; }
            public required long TrainedAtMs { get; init; }
            /// <summary>New rows are filed under the newest version so a rebuild never falls behind.</summary>
            public IvfVersion? WriteVersion => Pending ?? Active;
            public IEnumerable<IvfVersion> Versions => Pending == null ? new[] { Active } : new[] { Active, Pending };
        }

        private IvfState? Index(string space, string model) => ivf.GetOrAdd((space, model), _ => LoadIndex(space, model));

        private IvfState? LoadIndex(string space, string model)
        {
            using var c = store.OpenRead();
            var rows = c.Query(null, "SELECT version, state, dims, centroids, trained_on, trained_at_ms FROM vector_ivf WHERE space=$s AND model=$m ORDER BY version",
                r => (Version: r.GetInt32(0), State: r.GetString(1), Dims: r.GetInt32(2), Blob: (byte[])r[3], On: r.GetInt64(4), At: r.GetInt64(5)),
                ("$s", space), ("$m", model));
            var active = rows.LastOrDefault(r => r.State == "active");
            if (active == default) return null;
            var pending = rows.LastOrDefault(r => r.State == "pending" && r.Version > active.Version);
            return new IvfState
            {
                Active = new IvfVersion(active.Version, active.Dims, Split(VectorMath.Floats(active.Blob), active.Dims)),
                Pending = pending == default ? null : new IvfVersion(pending.Version, pending.Dims, Split(VectorMath.Floats(pending.Blob), pending.Dims)),
                TrainedOn = pending == default ? active.On : pending.On,
                TrainedAtMs = pending == default ? active.At : pending.At,
            };
        }

        private static float[][] Split(float[] flat, int dims) =>
            Enumerable.Range(0, flat.Length / dims).Select(i => flat.AsSpan(i * dims, dims).ToArray()).ToArray();

        public IvfStatus? Status(string space, string model)
        {
            var index = Index(space, model);
            if (index == null) return null;
            using var c = store.OpenRead();
            string keep = "ivf" + (index.WriteVersion!.Version).ToString(CultureInfo.InvariantCulture) + ":%";
            long stale = c.Scalar<long>(null, "SELECT COUNT(*) FROM vectors WHERE space=$s AND model=$m AND (partition_key IS NULL OR partition_key NOT LIKE $k)",
                ("$s", space), ("$m", model), ("$k", keep));
            return new IvfStatus(index.Active.Version, index.WriteVersion.Centroids.Length, index.TrainedOn, index.TrainedAtMs, index.Pending?.Version, stale);
        }

        /// <summary>
        /// Trains a new index version (spherical k-means++ on a deterministic sample, seeded) and registers it as pending,
        /// or as active when the space had none. Rows are then moved by <see cref="ReassignStepAsync"/>.
        /// </summary>
        public async Task<int> TrainAsync(string space, string model, int? lists = null, int sampleSize = 65_536, int seed = 1, int iterations = 12,
            CancellationToken ct = default)
        {
            long total = Count(space, model);
            if (total < 2) throw new InvalidOperationException($"Space {space}/{model} has too few vectors to index.");
            int k = Math.Clamp(lists ?? (int)Math.Round(Math.Sqrt(total)), 2, Math.Min(MaxLists, (int)Math.Min(total, int.MaxValue)));
            var sample = Sample(space, model, sampleSize, seed);
            int dims = sample[0].Length;
            var centroids = await Task.Run(() => KMeans.Spherical(sample, Math.Min(k, sample.Count), iterations, seed, ct), ct).ConfigureAwait(false);
            var existing = Index(space, model);
            int version = existing == null ? 1 : Math.Max(existing.Active.Version, existing.Pending?.Version ?? 0) + 1;
            byte[] blob = VectorMath.ToBytes(centroids.SelectMany(x => x).ToArray());
            long now = clock.NowMs;
            await store.WriteAsync((c, tx) =>
            {
                // A pending version that never finished is superseded by this one.
                c.Exec(tx, "DELETE FROM vector_ivf WHERE space=$s AND model=$m AND state='pending'", ("$s", space), ("$m", model));
                c.Exec(tx, "INSERT INTO vector_ivf(space, model, version, state, lists, dims, centroids, trained_on, trained_at_ms) VALUES($s,$m,$v,$st,$l,$d,$c,$n,$t)",
                    ("$s", space), ("$m", model), ("$v", version), ("$st", existing == null ? "active" : "pending"), ("$l", centroids.Length), ("$d", dims),
                    ("$c", blob), ("$n", total), ("$t", now));
            }, ct).ConfigureAwait(false);
            ivf.TryRemove((space, model), out _);
            return version;
        }

        /// <summary>
        /// Files up to <paramref name="batch"/> rows that are not yet under the newest version. When none are left, a pending
        /// version is promoted to active and the old one is dropped. Returns the rows moved (0 = done).
        /// </summary>
        public async Task<int> ReassignStepAsync(string space, string model, int batch = 5_000, CancellationToken ct = default)
        {
            var index = Index(space, model);
            if (index?.WriteVersion is not { } target) return 0;
            string keep = "ivf" + target.Version.ToString(CultureInfo.InvariantCulture) + ":%";
            List<(string Id, float[] Vec)> rows;
            using (var c = store.OpenRead())
            {
                rows = c.Query(null, """
                    SELECT item_id, scale, vec FROM vectors WHERE space=$s AND model=$m AND (partition_key IS NULL OR partition_key NOT LIKE $k) LIMIT $l
                    """, r => (r.GetString(0), VectorMath.DequantizeInt8(VectorMath.SBytes((byte[])r[2]), (float)r.GetDouble(1))),
                    ("$s", space), ("$m", model), ("$k", keep), ("$l", Math.Clamp(batch, 1, 50_000)));
            }
            if (rows.Count > 0)
            {
                var moves = rows.Where(r => r.Vec.Length == target.Dims).Select(r => (r.Id, Key: PartitionKey(target.Version, target.Nearest(r.Vec)))).ToList();
                var skipped = rows.Where(r => r.Vec.Length != target.Dims).Select(r => r.Id).ToList();
                await store.WriteAsync((c, tx) =>
                {
                    using var upd = new PreparedStatement(c, tx, "UPDATE vectors SET partition_key=$p WHERE space=$s AND model=$m AND item_id=$i", "$p", "$s", "$m", "$i");
                    foreach (var (id, key) in moves) upd.Run(key, space, model, id);
                    // Vectors of another dimensionality cannot be filed; they keep a marker so the step terminates.
                    foreach (var id in skipped) upd.Run(PartitionKey(target.Version, -1), space, model, id);
                }, ct).ConfigureAwait(false);
                return rows.Count;
            }
            if (index.Pending is { } pending)
            {
                await store.WriteAsync((c, tx) =>
                {
                    c.Exec(tx, "DELETE FROM vector_ivf WHERE space=$s AND model=$m AND version < $v", ("$s", space), ("$m", model), ("$v", pending.Version));
                    c.Exec(tx, "UPDATE vector_ivf SET state='active' WHERE space=$s AND model=$m AND version=$v", ("$s", space), ("$m", model), ("$v", pending.Version));
                }, ct).ConfigureAwait(false);
                ivf.TryRemove((space, model), out _);
            }
            return 0;
        }

        /// <summary>
        /// A seeded random sample of stored items (ids drawn through the covering primary-key index, then their vectors):
        /// background sets for taught concepts, uncertainty pools, evaluation samples.
        /// </summary>
        public IReadOnlyList<StoredVector> RandomSample(string space, string model, int size, int seed)
        {
            if (size <= 0) return Array.Empty<StoredVector>();
            var ids = new List<string>();
            long total = Count(space, model);
            using (var c = store.OpenRead())
            {
                if (total > 100_000 && size < total / 10)
                {
                    // Large space: seeded random rowid probes (each an index seek), duplicates folded.
                    long maxRowId = c.Scalar<long?>(null, "SELECT MAX(rowid) FROM vectors") ?? 0;
                    var rng = new Random(seed);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (int attempt = 0; attempt < size * 4 && seen.Count < size; attempt++)
                    {
                        long r = 1 + (long)(rng.NextDouble() * maxRowId);
                        string? id = c.Scalar<string>(null, "SELECT item_id FROM vectors WHERE rowid >= $r AND space=$s AND model=$m ORDER BY rowid LIMIT 1",
                            ("$r", r), ("$s", space), ("$m", model));
                        if (id != null && seen.Add(id)) ids.Add(id);
                    }
                }
                else
                {
                    ids = c.Query(null, """
                        SELECT item_id FROM vectors WHERE space=$s AND model=$m ORDER BY ((rowid * 2654435761 + $seed) % 4294967291) LIMIT $n
                        """, r => r.GetString(0), ("$s", space), ("$m", model), ("$seed", (long)(seed & 0x7fffffff)), ("$n", size));
                }
            }
            var got = Get(space, model, ids);
            return ids.Where(got.ContainsKey).Select(id => got[id]).ToList();
        }

        /// <summary>A deterministic sample (every n-th row in id order, seeded offset) for index training.</summary>
        private List<float[]> Sample(string space, string model, int size, int seed)
        {
            long total = Count(space, model);
            long step = Math.Max(1, total / Math.Max(1, size));
            long offset = Math.Abs(seed) % step;
            List<string> ids;
            using (var c = store.OpenRead())
            {
                // Ids only: the primary-key index covers them, so picking the sample never reads the vector pages.
                ids = c.Query(null, """
                    SELECT item_id FROM (SELECT item_id, ROW_NUMBER() OVER (ORDER BY item_id) - 1 AS n FROM vectors WHERE space=$s AND model=$m)
                    WHERE n % $step = $off LIMIT $lim
                    """, r => r.GetString(0), ("$s", space), ("$m", model), ("$step", step), ("$off", offset), ("$lim", size));
            }
            var vectors = Get(space, model, ids);
            return ids.Where(vectors.ContainsKey).Select(id => VectorMath.Normalized(vectors[id].Vector)).ToList();
        }

        /// <summary>A bounded min-heap keeping the best k scores (ties broken by id for stable results).</summary>
        private sealed class TopK
        {
            private readonly int k;
            private readonly PriorityQueue<string, (float Score, string Id)> heap;

            public TopK(int k)
            {
                this.k = k;
                heap = new PriorityQueue<string, (float, string)>(Comparer<(float Score, string Id)>.Create((a, b) =>
                {
                    int c = a.Score.CompareTo(b.Score);
                    return c != 0 ? c : string.CompareOrdinal(b.Id, a.Id);
                }));
            }

            public bool WouldAccept(float score) => heap.Count < k || (heap.TryPeek(out _, out var worst) && score > worst.Score);

            public void Add(string id, float score)
            {
                if (heap.Count < k) { heap.Enqueue(id, (score, id)); return; }
                heap.TryPeek(out _, out var worst);
                if (score > worst.Score || (score == worst.Score && string.CompareOrdinal(id, worst.Id) < 0)) heap.EnqueueDequeue(id, (score, id));
            }

            public IReadOnlyList<VectorHit> Result()
            {
                var items = new List<VectorHit>(heap.Count);
                while (heap.TryDequeue(out var id, out var p)) items.Add(new VectorHit(id, p.Score));
                items.Reverse();
                return items.OrderByDescending(h => h.Score).ThenBy(h => h.ItemId, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Spherical k-means (cosine) with k-means++ seeding; deterministic for a seed.</summary>
    public static class KMeans
    {
        public static float[][] Spherical(IReadOnlyList<float[]> points, int k, int iterations, int seed, CancellationToken ct)
        {
            if (points.Count == 0) throw new ArgumentException("No points.");
            k = Math.Clamp(k, 1, points.Count);
            int dims = points[0].Length;
            var rng = new Random(seed);
            var centroids = new List<float[]> { points[rng.Next(points.Count)] };
            var best = new float[points.Count];
            for (int i = 0; i < points.Count; i++) best[i] = 1 - VectorMath.Dot(points[i], centroids[0]);
            while (centroids.Count < k)
            {
                ct.ThrowIfCancellationRequested();
                double sum = 0;
                for (int i = 0; i < best.Length; i++) sum += Math.Max(0, best[i]) * Math.Max(0, best[i]);
                int pick;
                if (sum <= 0) pick = rng.Next(points.Count);
                else
                {
                    double x = rng.NextDouble() * sum;
                    pick = points.Count - 1;
                    for (int i = 0; i < best.Length; i++)
                    {
                        x -= Math.Max(0, best[i]) * Math.Max(0, best[i]);
                        if (x <= 0) { pick = i; break; }
                    }
                }
                var c = points[pick];
                centroids.Add(c);
                for (int i = 0; i < points.Count; i++) best[i] = Math.Min(best[i], 1 - VectorMath.Dot(points[i], c));
            }

            var assign = new int[points.Count];
            var result = centroids.Select(c => (float[])c.Clone()).ToArray();
            for (int it = 0; it < iterations; it++)
            {
                ct.ThrowIfCancellationRequested();
                int changed = 0;
                Parallel.For(0, points.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
                {
                    int bi = 0;
                    float bs = float.NegativeInfinity;
                    for (int j = 0; j < result.Length; j++)
                    {
                        float s = VectorMath.Dot(points[i], result[j]);
                        if (s > bs) { bs = s; bi = j; }
                    }
                    if (assign[i] != bi || it == 0) Interlocked.Increment(ref changed);
                    assign[i] = bi;
                });
                var sums = new float[result.Length][];
                for (int j = 0; j < result.Length; j++) sums[j] = new float[dims];
                var counts = new int[result.Length];
                for (int i = 0; i < points.Count; i++)
                {
                    System.Numerics.Tensors.TensorPrimitives.Add(sums[assign[i]], points[i], sums[assign[i]]);
                    counts[assign[i]]++;
                }
                for (int j = 0; j < result.Length; j++)
                {
                    if (counts[j] == 0)
                    {
                        // An empty cluster takes the point farthest from its centroid, so no list is ever empty by construction.
                        int far = 0;
                        float worst = float.PositiveInfinity;
                        for (int i = 0; i < points.Count; i++)
                        {
                            float s = VectorMath.Dot(points[i], result[assign[i]]);
                            if (s < worst) { worst = s; far = i; }
                        }
                        result[j] = (float[])points[far].Clone();
                        continue;
                    }
                    VectorMath.NormalizeInPlace(sums[j]);
                    result[j] = sums[j];
                }
                if (it > 0 && changed == 0) break;
            }
            return result;
        }

        /// <summary>Nearest centroid of each point (cosine).</summary>
        public static int[] Assign(IReadOnlyList<float[]> points, IReadOnlyList<float[]> centroids)
        {
            var result = new int[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                int bi = 0;
                float bs = float.NegativeInfinity;
                for (int j = 0; j < centroids.Count; j++)
                {
                    float s = VectorMath.Dot(points[i], centroids[j]);
                    if (s > bs) { bs = s; bi = j; }
                }
                result[i] = bi;
            }
            return result;
        }
    }
}
