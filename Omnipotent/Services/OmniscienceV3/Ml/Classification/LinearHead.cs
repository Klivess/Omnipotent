using System.Text.Json;

namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>Which features a head reads.</summary>
    public enum HeadKind
    {
        /// <summary>The message's own embedding.</summary>
        Single,
        /// <summary>Message + previous message (concatenated, element-wise product) + structural context flags.</summary>
        Context,
        /// <summary>A binary taught concept (X19) on the message embedding.</summary>
        Concept,
    }

    /// <summary>
    /// A light task head: softmax regression on the shared embedding (or context features), with a fitted temperature.
    /// Implements <see cref="ITaskHead"/> (frozen R7 #5). Deterministic: the same features always give the same output.
    /// </summary>
    public sealed class LinearHead : ITaskHead
    {
        public LinearHead(string task, HeadKind kind, int version, string encoderVersion, IReadOnlyList<string> labels, int dims, float[] weights, float[] bias, float temperature)
        {
            if (weights.Length != labels.Count * dims) throw new ArgumentException("Weights do not match labels × dims.");
            if (bias.Length != labels.Count) throw new ArgumentException("Bias does not match labels.");
            Task = task;
            Kind = kind;
            Version = version;
            EncoderVersion = encoderVersion;
            Labels = labels;
            Dims = dims;
            Weights = weights;
            Bias = bias;
            Temperature = temperature <= 0 ? 1f : temperature;
        }

        public string Task { get; }
        public HeadKind Kind { get; }
        public int Version { get; }
        /// <summary>The encoder whose embeddings this head was trained on; a head never scores another encoder's vectors.</summary>
        public string EncoderVersion { get; }
        public IReadOnlyList<string> Labels { get; }
        public int Dims { get; }
        public float[] Weights { get; }
        public float[] Bias { get; }
        public float Temperature { get; }

        public string ModelVersion => $"{Task}.{Kind.ToString().ToLowerInvariant()}.v{Version}";

        public float[] Predict(ReadOnlySpan<float> features)
        {
            if (features.Length != Dims) throw new ArgumentException($"Head {ModelVersion} expects {Dims} features, got {features.Length}.");
            var p = new float[Labels.Count];
            SoftmaxRegression.Logits(Weights, Bias, features, p);
            SoftmaxRegression.Softmax(p, Temperature);
            return p;
        }

        public float[] Logits(ReadOnlySpan<float> features)
        {
            var z = new float[Labels.Count];
            SoftmaxRegression.Logits(Weights, Bias, features, z);
            return z;
        }

        public float[][] Predict(IReadOnlyList<float[]> embeddings) => embeddings.Select(e => Predict(e)).ToArray();

        public string LabelsJson() => JsonSerializer.Serialize(Labels);
    }

    /// <summary>
    /// Context features of the context heads (plan A2 #2 "the same classifiers fed previous message(s) + current"; R2
    /// "context models run on the same encoder with pair inputs"): [u, v, u⊙v] of the current and previous message's
    /// shared embeddings (no second encoder pass) followed by exact structural flags of the exchange.
    /// </summary>
    public static class ContextFeatures
    {
        public const int Flags = 6;

        public static int DimsFor(int embeddingDims) => 3 * embeddingDims + Flags;

        /// <param name="previous">The previous message in the same conversation (within the gap threshold), if any.</param>
        public static float[] Build(ReadOnlySpan<float> current, ReadOnlySpan<float> previous, ContextFlags flags)
        {
            int d = current.Length;
            var x = new float[DimsFor(d)];
            current.CopyTo(x);
            if (previous.Length == d)
            {
                previous.CopyTo(x.AsSpan(d));
                for (int i = 0; i < d; i++) x[2 * d + i] = current[i] * previous[i];
            }
            int f = 3 * d;
            x[f] = previous.Length == d ? 1 : 0;
            x[f + 1] = flags.PreviousByKlives ? 1 : 0;
            x[f + 2] = flags.DirectWithKlives ? 1 : 0;
            x[f + 3] = flags.RepliesToKlives ? 1 : 0;
            x[f + 4] = flags.MentionsKlives ? 1 : 0;
            x[f + 5] = flags.SameAuthorAsPrevious ? 1 : 0;
            return x;
        }
    }

    /// <summary>Exact facts about a message's place in its conversation.</summary>
    public readonly record struct ContextFlags(bool PreviousByKlives, bool DirectWithKlives, bool RepliesToKlives, bool MentionsKlives, bool SameAuthorAsPrevious);
}
