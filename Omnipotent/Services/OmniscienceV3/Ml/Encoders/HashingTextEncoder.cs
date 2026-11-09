using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniscienceV3.Ml.Encoders
{
    /// <summary>
    /// A deterministic, dependency-free stand-in for the shared encoder, for tests and for development machines without
    /// the model files: signed feature hashing of Unicode words and character trigrams into a unit vector. Texts that share
    /// words or spellings land close together, which is enough to exercise heads, search, clustering and their edge cases
    /// with synthetic data. It is never wired into a production runtime (<see cref="V3RuntimeOptions.Encoder"/> defaults to
    /// the pinned e5 model in the service), and its <see cref="ModelVersion"/> marks every vector it produced.
    /// </summary>
    public sealed class HashingTextEncoder : IEmbeddingEncoder
    {
        public HashingTextEncoder(int dimensions = 384)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 8);
            Dimensions = dimensions;
            ModelVersion = $"hashing.v1.d{dimensions}";
        }

        public string ModelId => "omniscience/hashing-test-encoder";
        public string ModelVersion { get; }
        public int Dimensions { get; }

        /// <summary>Calls made so far (tests assert batching and caching through it).</summary>
        public int Calls;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var result = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                result[i] = Embed(texts[i] ?? "");
            }
            return Task.FromResult(result);
        }

        public float[] Embed(string text)
        {
            var v = new float[Dimensions];
            foreach (string word in Words(text))
            {
                Add(v, "w:" + word, 1.0f);
                string padded = "^" + word + "$";
                var elements = TextElements(padded);
                for (int i = 0; i + 3 <= elements.Count; i++) Add(v, "c:" + string.Concat(elements.Skip(i).Take(3)), 0.5f);
            }
            VectorMath.NormalizeInPlace(v);
            return v;
        }

        private void Add(float[] v, string feature, float weight)
        {
            ulong h = Fnv1a(feature);
            int index = (int)(h % (ulong)Dimensions);
            float sign = ((h >> 63) & 1) == 0 ? 1f : -1f;
            v[index] += sign * weight;
        }

        /// <summary>Words by Unicode category (letters, digits, marks), lower-cased invariantly; emoji count as words.</summary>
        internal static IEnumerable<string> Words(string text)
        {
            var sb = new StringBuilder();
            foreach (string el in TextElements(text.Normalize(NormalizationForm.FormKC)))
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(el, 0);
                bool wordy = char.IsLetterOrDigit(el, 0) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
                bool symbol = cat is UnicodeCategory.OtherSymbol || char.IsSurrogate(el, 0);
                if (wordy) { sb.Append(el.ToLowerInvariant()); continue; }
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                if (symbol) yield return el;
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private static List<string> TextElements(string s)
        {
            var list = new List<string>();
            var e = StringInfo.GetTextElementEnumerator(s);
            while (e.MoveNext()) list.Add(e.GetTextElement());
            return list;
        }

        private static ulong Fnv1a(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }
    }
}
