namespace Omnipotent.Services.OmniscienceV3.Ml
{
    /// <summary>
    /// The shared encoder of the local-first classification layer (plan R2): one pass per message
    /// produces an L2-normalised embedding used for search, clustering AND every light task head.
    /// </summary>
    public interface IEmbeddingEncoder
    {
        string ModelId { get; }
        string ModelVersion { get; }
        int Dimensions { get; }
        Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
    }

    /// <summary>A light task head on top of the shared embedding (sentiment, toxicity, dialogue act, custom concept…).</summary>
    public interface ITaskHead
    {
        string Task { get; }
        string ModelVersion { get; }
        IReadOnlyList<string> Labels { get; }
        /// <summary>Per-label probabilities for each embedding (rows sum to 1 for single-label tasks).</summary>
        float[][] Predict(IReadOnlyList<float[]> embeddings);
    }

    /// <summary>One signal's opinion in the ensemble (plan A2), kept for explainability and calibration.</summary>
    public sealed record SignalVote(string Signal, string? Label, double Probability, string ModelVersion);

    /// <summary>
    /// The label record contract (plan A3), stored in intel.db <c>labels</c>: label, probability, abstained
    /// flag, contributing signals, model + version + prompt version + as-of. A model upgrade writes new rows
    /// under a new model version; old rows stay queryable and charts never mix versions silently.
    /// </summary>
    public sealed record LabelRecord(
        string ItemKind,
        string ItemId,
        int ItemVersion,
        string Task,
        string? Label,
        double Probability,
        bool Abstained,
        IReadOnlyList<SignalVote> Signals,
        string Model,
        string ModelVersion,
        string? PromptVersion,
        long AsOfMs,
        long SourceSeq);

    /// <summary>Fusion of several signals into one calibrated label with abstention (plan A2).</summary>
    public static class SignalFusion
    {
        /// <summary>
        /// Weighted average of per-label probabilities across signals, then abstain when the winner is below
        /// <paramref name="abstainBelow"/> or the runner-up is within <paramref name="minMargin"/>. Returns
        /// (label or null when abstaining, probability, disagreement), where disagreement is the share of
        /// signals whose own top label differs from the fused winner (high disagreement → LLM adjudication).
        /// </summary>
        public static (string? Label, double Probability, double Disagreement) Fuse(
            IReadOnlyList<(IReadOnlyDictionary<string, double> Distribution, double Weight)> signals,
            double abstainBelow = 0.55,
            double minMargin = 0.10)
        {
            if (signals.Count == 0) return (null, 0, 0);
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            double weightSum = 0;
            foreach (var (dist, weight) in signals)
            {
                if (weight <= 0) continue;
                weightSum += weight;
                foreach (var (label, p) in dist)
                    totals[label] = totals.GetValueOrDefault(label) + weight * Math.Clamp(p, 0, 1);
            }
            if (weightSum <= 0 || totals.Count == 0) return (null, 0, 0);
            var ranked = totals.Select(kv => (Label: kv.Key, P: kv.Value / weightSum)).OrderByDescending(x => x.P).ThenBy(x => x.Label, StringComparer.Ordinal).ToList();
            var top = ranked[0];
            double second = ranked.Count > 1 ? ranked[1].P : 0;
            int disagreeing = signals.Count(s => s.Weight > 0 && s.Distribution.Count > 0
                && s.Distribution.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key != top.Label);
            double disagreement = (double)disagreeing / Math.Max(1, signals.Count(s => s.Weight > 0));
            bool abstain = top.P < abstainBelow || top.P - second < minMargin;
            return (abstain ? null : top.Label, top.P, disagreement);
        }
    }
}
