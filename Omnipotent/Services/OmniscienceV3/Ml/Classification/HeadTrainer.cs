namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>A labelled item for head training. <see cref="TextHash"/> folds duplicates; gold outranks teacher labels.</summary>
    public sealed record LabelledItem(string ItemId, float[] Features, string Label, string TextHash, bool Gold);

    public sealed record HeadTrainingReport(
        LinearHead Head,
        ClassifierMetrics Validation,
        ClassifierMetrics? GoldValidation,
        int TrainItems,
        int ValidationItems,
        int DuplicatesFolded,
        double L2,
        int Iterations,
        IReadOnlyList<string> UnseenLabels);

    /// <summary>
    /// Trains one task head end to end (plan R2 "trained on LLM-teacher + gold labels"):
    /// <list type="number">
    /// <item>duplicates (same normalised text) are folded: a group counts once, its label by majority with gold winning
    /// (plan A5 "repeated identical messages count once for calibration");</item>
    /// <item>train/validation split by text hash, so no copy of a validation text is ever trained on;</item>
    /// <item>class weights by inverse square-root frequency, so rare labels are learned without drowning the common ones;</item>
    /// <item>L2 chosen on validation log-loss from a small grid; temperature fitted on validation logits;</item>
    /// <item>metrics on validation (all labels, and gold-only when there is enough gold).</item>
    /// </list>
    /// Deterministic for a given input set and seed.
    /// </summary>
    public static class HeadTrainer
    {
        public const int MinExamples = 60;
        public const int MinGoldForGoldMetrics = 30;
        private static readonly double[] L2Grid = { 1e-4, 1e-3, 1e-2 };

        public static HeadTrainingReport? Train(TaskDefinition task, HeadKind kind, int version, string encoderVersion, IReadOnlyList<LabelledItem> items,
            int seed = 13, CancellationToken ct = default)
        {
            if (items.Count == 0) return null;
            int dims = items[0].Features.Length;
            var labels = task.LabelIds;

            // 1. Fold duplicates.
            var groups = items.Where(i => task.IndexOf(i.Label) >= 0 && i.Features.Length == dims)
                .GroupBy(i => i.TextHash, StringComparer.Ordinal)
                .Select(g =>
                {
                    var gold = g.Where(i => i.Gold).ToList();
                    var pool = gold.Count > 0 ? gold : g.ToList();
                    string label = pool.GroupBy(i => i.Label, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
                    var first = pool.OrderBy(i => i.ItemId, StringComparer.Ordinal).First();
                    return (Hash: g.Key, Features: first.Features, Label: task.IndexOf(label), Gold: gold.Count > 0, Size: g.Count());
                })
                .OrderBy(g => g.Hash, StringComparer.Ordinal)
                .ToList();
            int folded = items.Count - groups.Count;
            if (groups.Count < MinExamples) return null;

            // 2. Split by hash (20% validation), deterministic for the seed.
            bool IsValidation(string hash) => (uint)(StableHash(hash) ^ (uint)seed) % 5 == 0;
            var train = groups.Where(g => !IsValidation(g.Hash)).ToList();
            var validation = groups.Where(g => IsValidation(g.Hash)).ToList();
            if (validation.Count < 10 || train.Count < MinExamples / 2) return null;

            // 3. Class weights (inverse sqrt frequency, mean 1).
            var counts = new double[labels.Count];
            foreach (var g in train) counts[g.Label]++;
            var classWeight = counts.Select(c => c == 0 ? 0 : 1 / Math.Sqrt(c)).ToArray();
            double mean = train.Average(g => classWeight[g.Label]);
            var examples = train.Select(g => new Example(g.Features, g.Label, classWeight[g.Label] / mean)).ToList();

            // 4. L2 by validation log-loss; temperature on validation logits.
            TrainingResult? best = null;
            double bestLoss = double.PositiveInfinity, bestL2 = L2Grid[0];
            foreach (double l2 in L2Grid)
            {
                ct.ThrowIfCancellationRequested();
                var r = SoftmaxRegression.Train(examples, labels.Count, dims, new TrainingOptions { L2 = l2 }, ct);
                var head = new LinearHead(task.Id, kind, version, encoderVersion, labels, dims, r.Weights, r.Bias, 1f);
                double loss = validation.Average(g => -Math.Log(Math.Max(1e-9, head.Predict(g.Features)[g.Label])));
                if (loss < bestLoss) { bestLoss = loss; best = r; bestL2 = l2; }
            }
            var raw = new LinearHead(task.Id, kind, version, encoderVersion, labels, dims, best!.Weights, best.Bias, 1f);
            float temperature = Calibration.FitTemperature(validation.Select(g => raw.Logits(g.Features)).ToList(), validation.Select(g => g.Label).ToList());
            var final = new LinearHead(task.Id, kind, version, encoderVersion, labels, dims, best.Weights, best.Bias, temperature);

            // 5. Metrics.
            ClassifierMetrics Measure(IReadOnlyList<(string Hash, float[] Features, int Label, bool Gold, int Size)> set)
            {
                var probs = set.Select(g => (float[]?)final.Predict(g.Features)).ToList();
                var abstained = probs.Select(p =>
                {
                    var sorted = p!.OrderByDescending(x => x).ToArray();
                    return sorted[0] < FusionConfig.BaseAbstainBelow || sorted[0] - (sorted.Length > 1 ? sorted[1] : 0) < FusionConfig.BaseMinMargin;
                }).ToList();
                return Calibration.Evaluate(labels, set.Select(g => g.Label).ToList(), probs, abstained, seed: seed);
            }
            var goldValidation = validation.Where(g => g.Gold).ToList();
            return new HeadTrainingReport(final, Measure(validation),
                goldValidation.Count >= MinGoldForGoldMetrics ? Measure(goldValidation) : null,
                train.Count, validation.Count, folded, bestL2, best.Iterations,
                labels.Where((_, i) => counts[i] == 0).ToList());
        }

        /// <summary>FNV-1a over the string: stable across processes (string.GetHashCode is randomised).</summary>
        public static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }

        /// <summary>
        /// The regression gate (plan N18 "blocks a version whose precision drops beyond the CI"): a candidate replaces the
        /// active head unless its macro-F1 falls below the active head's bootstrap lower bound. Gold metrics decide when both have them.
        /// </summary>
        public static bool Promotes(ClassifierMetrics candidate, ClassifierMetrics? candidateGold, ClassifierMetrics? active, ClassifierMetrics? activeGold)
        {
            if (active == null) return true;
            if (candidateGold != null && activeGold != null) return candidateGold.MacroF1 >= activeGold.MacroF1Low;
            return candidate.MacroF1 >= active.MacroF1Low;
        }
    }
}
