using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>The kinds of signal the A2 ensemble fuses (plan A2 #1–#5).</summary>
    public static class Signals
    {
        public const string Head = "head";
        public const string Context = "context";
        public const string Llm = "llm";
        public const string Specialist = "specialist";
        /// <summary>The person's own history (language prior for short messages, N19).</summary>
        public const string Prior = "prior";
        public const string Gold = "gold";

        public static readonly IReadOnlyList<string> Fusable = new[] { Head, Context, Llm, Specialist, Prior };
    }

    /// <summary>One signal's distribution over a task's labels.</summary>
    public sealed record SignalOpinion(string Signal, float[] Distribution, string ModelVersion);

    /// <summary>A fused, calibrated label with everything plan A3 asks every model-derived value to carry.</summary>
    public sealed record FusedLabel(string Task, string? Label, string TopLabel, double Probability, bool Abstained, double Disagreement, IReadOnlyList<SignalVote> Votes)
    {
        /// <summary>Signals disagree: the item goes to LLM adjudication (plan A2 "Disagreement between signals → LLM adjudication").</summary>
        public bool NeedsAdjudication => Disagreement >= 0.5 && Votes.Count(v => v.Signal != Signals.Prior) >= 2;
    }

    /// <summary>Per-task fusion settings: learned signal weights and the abstention bar.</summary>
    public sealed record FusionConfig(IReadOnlyDictionary<string, double> Weights, double AbstainBelow, double MinMargin, int GoldExamples, string Source)
    {
        public const double BaseAbstainBelow = 0.55;
        public const double BaseMinMargin = 0.10;
        /// <summary>Below this much gold the stacker is not trusted (plan F-ML: equal weights, wider abstention).</summary>
        public const int MinGoldForStacker = 50;
        public const double ThinGoldWiden = 0.10;

        /// <summary>Equal weights with a wider abstention band: the fallback when gold is too thin to learn weights from.</summary>
        public static FusionConfig EqualWeights(int goldExamples = 0) => new(
            Signals.Fusable.ToDictionary(s => s, s => s == Signals.Prior ? 0.5 : 1.0), BaseAbstainBelow + ThinGoldWiden, BaseMinMargin, goldExamples, "equal");
    }

    /// <summary>Fusion of signals for one task (plan A2 "fused by a calibrated stacker").</summary>
    public static class Ensemble
    {
        /// <summary>
        /// Gold (Klives's correction of this exact item) wins outright. Otherwise the weighted average of the available
        /// signals, abstaining below the bar (raised for ultra-short or emoji-only text) or when the runner-up is too close.
        /// </summary>
        public static FusedLabel Fuse(TaskDefinition task, IReadOnlyList<SignalOpinion> signals, FusionConfig config, double abstainBoost = 0, string? gold = null)
        {
            var labels = task.LabelIds;
            if (gold != null && task.IndexOf(gold) >= 0)
            {
                var votes = signals.Select(s => Vote(task, s)).Append(new SignalVote(Signals.Gold, gold, 1.0, "operator")).ToList();
                return new FusedLabel(task.Id, gold, gold, 1.0, false, 0, votes);
            }
            var usable = signals.Where(s => s.Distribution.Length == labels.Count && config.Weights.GetValueOrDefault(s.Signal) > 0).ToList();
            if (usable.Count == 0)
                return new FusedLabel(task.Id, null, task.DefaultLabel ?? labels[0], 0, true, 0, signals.Select(s => Vote(task, s)).ToList());
            var dists = usable.Select(s => ((IReadOnlyDictionary<string, double>)labels.Select((l, i) => (l, (double)s.Distribution[i])).ToDictionary(x => x.l, x => x.Item2), config.Weights[s.Signal])).ToList();
            double abstainBelow = Math.Min(0.95, config.AbstainBelow + abstainBoost);
            var (label, p, disagreement) = SignalFusion.Fuse(dists, abstainBelow, config.MinMargin);
            string top = label ?? TopOf(labels, usable, config);
            return new FusedLabel(task.Id, label, top, p, label == null, disagreement, signals.Select(s => Vote(task, s)).ToList());
        }

        private static string TopOf(IReadOnlyList<string> labels, IReadOnlyList<SignalOpinion> usable, FusionConfig config)
        {
            var totals = new double[labels.Count];
            foreach (var s in usable)
                for (int i = 0; i < labels.Count; i++) totals[i] += config.Weights[s.Signal] * s.Distribution[i];
            int best = 0;
            for (int i = 1; i < totals.Length; i++) if (totals[i] > totals[best]) best = i;
            return labels[best];
        }

        private static SignalVote Vote(TaskDefinition task, SignalOpinion s)
        {
            if (s.Distribution.Length != task.Labels.Count) return new SignalVote(s.Signal, null, 0, s.ModelVersion);
            int i = Calibration.ArgMax(s.Distribution);
            return new SignalVote(s.Signal, task.Labels[i].Id, s.Distribution[i], s.ModelVersion);
        }

        /// <summary>
        /// An LLM's label as a distribution: the label gets the LLM's measured reliability for this task (its accuracy
        /// against gold, default 0.85 until measured), the rest share the remainder. LLMs are not calibrated, so their
        /// stated confidence is not used as a probability.
        /// </summary>
        public static float[] LlmDistribution(TaskDefinition task, string label, double reliability)
        {
            int k = task.Labels.Count, idx = task.IndexOf(label);
            var d = new float[k];
            if (idx < 0) { Array.Fill(d, 1f / k); return d; }
            double r = Math.Clamp(reliability, 1.0 / k, 0.99);
            for (int i = 0; i < k; i++) d[i] = (float)(i == idx ? r : (1 - r) / (k - 1));
            return d;
        }

        /// <summary>
        /// Learns signal weights for one task on labelled validation rows (gold first, teacher labels when gold is thin):
        /// a grid search over the weight simplex (step 0.1) minimising the log-loss of the fused distribution. With fewer
        /// than <see cref="FusionConfig.MinGoldForStacker"/> gold rows it returns equal weights with wider abstention.
        /// </summary>
        public static FusionConfig FitStacker(TaskDefinition task, IReadOnlyList<(int Gold, IReadOnlyList<SignalOpinion> Signals)> rows, int goldRows)
        {
            if (goldRows < FusionConfig.MinGoldForStacker || rows.Count < FusionConfig.MinGoldForStacker) return FusionConfig.EqualWeights(goldRows);
            var present = Signals.Fusable.Where(s => rows.Any(r => r.Signals.Any(x => x.Signal == s))).ToList();
            if (present.Count == 0) return FusionConfig.EqualWeights(goldRows);
            double bestLoss = double.PositiveInfinity;
            Dictionary<string, double>? best = null;
            foreach (var w in Simplex(present.Count, 10))
            {
                var weights = present.Select((s, i) => (s, w[i])).ToDictionary(x => x.s, x => x.Item2);
                double loss = 0;
                foreach (var (gold, signals) in rows)
                {
                    double total = 0, mass = 0;
                    foreach (var s in signals)
                    {
                        double wt = weights.GetValueOrDefault(s.Signal);
                        if (wt <= 0 || s.Distribution.Length != task.Labels.Count) continue;
                        total += wt;
                        mass += wt * s.Distribution[gold];
                    }
                    double p = total <= 0 ? 1.0 / task.Labels.Count : mass / total;
                    loss -= Math.Log(Math.Max(1e-9, p));
                }
                if (loss < bestLoss - 1e-12) { bestLoss = loss; best = weights; }
            }
            foreach (var s in Signals.Fusable) best!.TryAdd(s, 0);
            return new FusionConfig(best!, FusionConfig.BaseAbstainBelow, FusionConfig.BaseMinMargin, goldRows, "stacker");
        }

        /// <summary>All weight vectors of <paramref name="n"/> components on a grid of 1/<paramref name="steps"/>, summing to 1, none all-zero.</summary>
        private static IEnumerable<double[]> Simplex(int n, int steps)
        {
            var current = new int[n];
            IEnumerable<double[]> Rec(int i, int left)
            {
                if (i == n - 1)
                {
                    current[i] = left;
                    yield return current.Select(c => (double)c / steps).ToArray();
                    yield break;
                }
                for (int v = left; v >= 0; v--)
                {
                    current[i] = v;
                    foreach (var r in Rec(i + 1, left - v)) yield return r;
                }
            }
            return Rec(0, steps);
        }
    }

    /// <summary>One decoded label of a packed row.</summary>
    public readonly record struct PackedLabel(string Task, string Label, double Probability, bool Abstained);

    /// <summary>
    /// The compact per-message label row (intel.db <c>classify_message.codes</c>): one row per message and label set
    /// instead of one row per message × task, because 5M messages × 15 tasks as separate rows would cost ~12 GB and hours of
    /// writes. Format <c>task=label:pp|task=?label:pp</c> (pp = percent; "?" = abstained, keeping the leaning). Detailed
    /// records with every signal (LLM labels, adjudications, concept hits) live in the frozen <c>labels</c> table.
    /// </summary>
    public static class LabelCodec
    {
        public static string Encode(IEnumerable<FusedLabel> labels)
        {
            var sb = new StringBuilder();
            foreach (var l in labels.OrderBy(l => l.Task, StringComparer.Ordinal))
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(l.Task).Append('=');
                if (l.Abstained) sb.Append('?');
                sb.Append(l.Label ?? l.TopLabel).Append(':').Append(((int)Math.Round(l.Probability * 100)).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static IReadOnlyList<PackedLabel> Decode(string? codes)
        {
            var result = new List<PackedLabel>();
            if (string.IsNullOrEmpty(codes)) return result;
            foreach (var part in codes.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('='), colon = part.LastIndexOf(':');
                if (eq <= 0 || colon <= eq) continue;
                string task = part[..eq];
                string label = part[(eq + 1)..colon];
                bool abstained = label.StartsWith('?');
                if (abstained) label = label[1..];
                double p = int.TryParse(part[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) ? pct / 100.0 : 0;
                result.Add(new PackedLabel(task, label, p, abstained));
            }
            return result;
        }

        /// <summary>The label a chart counts: null when the ensemble abstained (charts never silently count a guess).</summary>
        public static string? Answer(string? codes, string task)
        {
            foreach (var l in Decode(codes))
                if (l.Task == task) return l.Abstained ? null : l.Label;
            return null;
        }

        public static string SignalsJson(IReadOnlyList<SignalVote> votes) => JsonSerializer.Serialize(votes.Select(v => new { s = v.Signal, l = v.Label, p = Math.Round(v.Probability, 3), m = v.ModelVersion }));
    }
}
