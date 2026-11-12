using System.Numerics.Tensors;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Dense-vector arithmetic on L2-normalised embeddings (SIMD through TensorPrimitives, F295).</summary>
    public static class VecMath
    {
        public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => a.Length == b.Length ? TensorPrimitives.Dot(a, b) : 0f;

        public static float[] Normalized(float[] v)
        {
            float norm = TensorPrimitives.Norm(v);
            if (norm <= 1e-12f) return (float[])v.Clone();
            var r = new float[v.Length];
            TensorPrimitives.Divide(v, norm, r);
            return r;
        }

        /// <summary>Mean of vectors, re-normalised (a centroid on the unit sphere); null when empty.</summary>
        public static float[]? Centroid(IReadOnlyList<float[]> vectors)
        {
            if (vectors.Count == 0) return null;
            var sum = new float[vectors[0].Length];
            foreach (var v in vectors)
                if (v.Length == sum.Length) TensorPrimitives.Add(sum, v, sum);
            return Normalized(sum);
        }

        /// <summary>int8 quantisation with a per-vector scale (vectors.db "i8").</summary>
        public static (byte[] Bytes, float Scale) QuantizeI8(float[] v)
        {
            float max = 0;
            foreach (var x in v) max = Math.Max(max, Math.Abs(x));
            float scale = max <= 0 ? 1f : max / 127f;
            var bytes = new byte[v.Length];
            for (int i = 0; i < v.Length; i++) bytes[i] = unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(v[i] / scale), -127, 127));
            return (bytes, scale);
        }

        public static float[] DequantizeI8(byte[] bytes, float scale)
        {
            var v = new float[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) v[i] = unchecked((sbyte)bytes[i]) * scale;
            return v;
        }

        public static byte[] ToBytes(float[] v)
        {
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] FromBytes(byte[] bytes)
        {
            var v = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
            return v;
        }
    }

    /// <summary>
    /// In-memory BM25 over token lists (the lexical half of hybrid retrieval; always available, multilingual through
    /// <see cref="TextFeatures.Tokens"/>). Built once per replica version and cached.
    /// </summary>
    public sealed class Bm25Index
    {
        private readonly Dictionary<string, List<(int Doc, int Tf)>> postings = new(StringComparer.Ordinal);
        private readonly int[] lengths;
        private readonly double avgLength;
        private const double K1 = 1.2, B = 0.75;

        public Bm25Index(IReadOnlyList<IReadOnlyList<string>> documents)
        {
            lengths = new int[documents.Count];
            long total = 0;
            for (int d = 0; d < documents.Count; d++)
            {
                var tokens = documents[d];
                lengths[d] = tokens.Count;
                total += tokens.Count;
                foreach (var g in tokens.GroupBy(t => t, StringComparer.Ordinal))
                {
                    if (!postings.TryGetValue(g.Key, out var list)) postings[g.Key] = list = new List<(int, int)>();
                    list.Add((d, g.Count()));
                }
            }
            avgLength = documents.Count == 0 ? 1 : Math.Max(1, (double)total / documents.Count);
        }

        public int Count => lengths.Length;

        /// <summary>BM25 score of every document for the query (0 where no term matches).</summary>
        public double[] Score(IEnumerable<string> query)
        {
            var scores = new double[lengths.Length];
            int n = lengths.Length;
            foreach (var term in query.Distinct(StringComparer.Ordinal))
            {
                if (!postings.TryGetValue(term, out var list)) continue;
                double idf = Math.Log(1 + (n - list.Count + 0.5) / (list.Count + 0.5));
                foreach (var (doc, tf) in list)
                    scores[doc] += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * lengths[doc] / avgLength));
            }
            return scores;
        }

        /// <summary>Inverse document frequency of a token (rarer = more distinctive).</summary>
        public double Idf(string token)
        {
            int df = postings.TryGetValue(token, out var list) ? list.Count : 0;
            return Math.Log(1 + (lengths.Length - df + 0.5) / (df + 0.5));
        }
    }

    /// <summary>Reciprocal-rank fusion of several ranked lists (the KliveRAG HybridRetriever rule): no absolute
    /// similarity floors, only ranks (F296).</summary>
    public static class RankFusion
    {
        public static Dictionary<int, double> Rrf(IEnumerable<IReadOnlyList<int>> rankings, int k = 60)
        {
            var fused = new Dictionary<int, double>();
            foreach (var ranking in rankings)
                for (int r = 0; r < ranking.Count; r++)
                    fused[ranking[r]] = fused.GetValueOrDefault(ranking[r]) + 1.0 / (k + r + 1);
            return fused;
        }

        /// <summary>Indices of the top <paramref name="n"/> positive scores, best first (ties by index for determinism).</summary>
        public static List<int> TopIndices(IReadOnlyList<double> scores, int n, Func<int, bool>? eligible = null)
        {
            var list = new List<int>();
            for (int i = 0; i < scores.Count; i++)
                if (scores[i] > 0 && (eligible == null || eligible(i))) list.Add(i);
            list.Sort((a, b) => scores[b] != scores[a] ? scores[b].CompareTo(scores[a]) : a.CompareTo(b));
            return list.Count > n ? list.GetRange(0, n) : list;
        }
    }

    /// <summary>
    /// A small L2-regularised logistic regression over sparse hashed features plus dense features, trained by
    /// deterministic full-batch gradient descent. Used for the assistant-voice house model (A2: trained on
    /// LLM-teacher assistant replies vs real messages, F017/F303). Serialisable as JSON.
    /// </summary>
    public sealed class LogisticModel
    {
        public int HashDims { get; init; } = 4096;
        public int DenseDims { get; init; }
        public double[] Weights { get; init; } = Array.Empty<double>();
        public double Bias { get; set; }

        public static int HashOf(string feature, int dims) => (int)((uint)TextFeatures.Seed(feature) % (uint)dims);

        public double Predict((int[] Hashed, double[] Dense) x)
        {
            double z = Bias;
            foreach (int h in x.Hashed) if (h >= 0 && h < HashDims && h < Weights.Length) z += Weights[h];
            for (int i = 0; i < x.Dense.Length && i < DenseDims; i++)
                if (HashDims + i < Weights.Length) z += Weights[HashDims + i] * x.Dense[i];
            return 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30, 30)));
        }

        public static LogisticModel Train(IReadOnlyList<((int[] Hashed, double[] Dense) X, bool Y)> samples, int hashDims, int denseDims,
            int epochs = 200, double learningRate = 0.5, double l2 = 1e-3)
        {
            var w = new double[hashDims + denseDims];
            double bias = 0;
            int n = Math.Max(1, samples.Count);
            var grad = new double[w.Length];
            for (int epoch = 0; epoch < epochs; epoch++)
            {
                Array.Clear(grad);
                double gb = 0;
                foreach (var (x, y) in samples)
                {
                    double z = bias;
                    foreach (int h in x.Hashed) z += w[h];
                    for (int i = 0; i < denseDims && i < x.Dense.Length; i++) z += w[hashDims + i] * x.Dense[i];
                    double p = 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30, 30)));
                    double err = p - (y ? 1 : 0);
                    foreach (int h in x.Hashed) grad[h] += err;
                    for (int i = 0; i < denseDims && i < x.Dense.Length; i++) grad[hashDims + i] += err * x.Dense[i];
                    gb += err;
                }
                for (int j = 0; j < w.Length; j++) w[j] -= learningRate * (grad[j] / n + l2 * w[j]);
                bias -= learningRate * gb / n;
            }
            return new LogisticModel { HashDims = hashDims, DenseDims = denseDims, Weights = w, Bias = bias };
        }
    }
}
