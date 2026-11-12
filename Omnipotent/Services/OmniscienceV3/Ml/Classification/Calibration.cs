namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>Per-label quality on an evaluation set.</summary>
    public sealed record LabelMetrics(string Label, int Support, int Predicted, double Precision, double Recall, double F1);

    /// <summary>
    /// What every ensemble task reports (plan F-ML acceptance: "per-label precision/recall/calibration on its eval set").
    /// <see cref="MacroF1Low"/>/<see cref="MacroF1High"/> are a seeded bootstrap 90% interval, used by the regression gate.
    /// <see cref="Coverage"/> is the share not abstained; <see cref="Accuracy"/> and the per-label figures are over the
    /// answered items only, so abstaining is never rewarded as a wrong answer nor hidden as a right one.
    /// </summary>
    public sealed record ClassifierMetrics(
        int Items,
        int Answered,
        double Coverage,
        double Accuracy,
        double MacroF1,
        double MacroF1Low,
        double MacroF1High,
        double Ece,
        double Brier,
        double LogLoss,
        IReadOnlyList<LabelMetrics> PerLabel,
        int[][] Confusion);

    /// <summary>Calibration and evaluation helpers for task heads and fused ensembles.</summary>
    public static class Calibration
    {
        /// <summary>
        /// Temperature scaling: the single T in [0.2, 10] minimising the negative log-likelihood of the validation labels
        /// (golden-section search on log T). Preserves the argmax; only the confidence moves.
        /// </summary>
        public static float FitTemperature(IReadOnlyList<float[]> logits, IReadOnlyList<int> labels)
        {
            if (logits.Count == 0) return 1f;
            double Nll(double logT)
            {
                double t = Math.Exp(logT), total = 0;
                for (int i = 0; i < logits.Count; i++)
                {
                    var z = logits[i];
                    double max = double.NegativeInfinity;
                    foreach (var v in z) max = Math.Max(max, v);
                    double sum = 0;
                    foreach (var v in z) sum += Math.Exp((v - max) / t);
                    total -= (z[labels[i]] - max) / t - Math.Log(sum);
                }
                return total / logits.Count;
            }
            double a = Math.Log(0.2), b = Math.Log(10);
            double gr = (Math.Sqrt(5) - 1) / 2;
            double c = b - gr * (b - a), d = a + gr * (b - a);
            double fc = Nll(c), fd = Nll(d);
            for (int i = 0; i < 60; i++)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = Nll(c); }
                else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = Nll(d); }
            }
            return (float)Math.Exp((a + b) / 2);
        }

        /// <summary>
        /// Metrics of predicted distributions against gold labels. A row with a null prediction is an abstention.
        /// </summary>
        public static ClassifierMetrics Evaluate(IReadOnlyList<string> labelNames, IReadOnlyList<int> gold, IReadOnlyList<float[]?> probabilities,
            IReadOnlyList<bool>? abstained = null, int bootstrap = 200, int seed = 7)
        {
            int k = labelNames.Count;
            var confusion = Enumerable.Range(0, k).Select(_ => new int[k]).ToArray();
            int answered = 0, correct = 0;
            double brier = 0, logLoss = 0;
            var eceBins = new (double Conf, double Acc, int N)[15];
            var answeredIdx = new List<int>();
            for (int i = 0; i < gold.Count; i++)
            {
                var p = probabilities[i];
                if (p == null) continue;
                int pred = ArgMax(p);
                double conf = p[pred];
                for (int j = 0; j < k; j++) { double y = j == gold[i] ? 1 : 0; brier += (p[j] - y) * (p[j] - y); }
                logLoss -= Math.Log(Math.Max(1e-12, p[gold[i]]));
                int bin = Math.Min(14, (int)(conf * 15));
                eceBins[bin] = (eceBins[bin].Conf + conf, eceBins[bin].Acc + (pred == gold[i] ? 1 : 0), eceBins[bin].N + 1);
                if (abstained != null && abstained[i]) continue;
                answered++;
                answeredIdx.Add(i);
                confusion[gold[i]][pred]++;
                if (pred == gold[i]) correct++;
            }
            int scored = probabilities.Count(p => p != null);
            double ece = 0;
            foreach (var b in eceBins)
                if (b.N > 0) ece += (double)b.N / Math.Max(1, scored) * Math.Abs(b.Acc / b.N - b.Conf / b.N);
            var perLabel = PerLabel(labelNames, confusion);
            double macro = MacroF1(perLabel);

            // Bootstrap interval of macro-F1 over the answered items (seeded, so the gate is reproducible).
            double lo = macro, hi = macro;
            if (answeredIdx.Count >= 10 && bootstrap > 0)
            {
                var rng = new Random(seed);
                var samples = new List<double>(bootstrap);
                for (int b = 0; b < bootstrap; b++)
                {
                    var conf = Enumerable.Range(0, k).Select(_ => new int[k]).ToArray();
                    for (int s = 0; s < answeredIdx.Count; s++)
                    {
                        int i = answeredIdx[rng.Next(answeredIdx.Count)];
                        conf[gold[i]][ArgMax(probabilities[i]!)]++;
                    }
                    samples.Add(MacroF1(PerLabel(labelNames, conf)));
                }
                samples.Sort();
                lo = samples[(int)(0.05 * (samples.Count - 1))];
                hi = samples[(int)(0.95 * (samples.Count - 1))];
            }
            return new ClassifierMetrics(gold.Count, answered, gold.Count == 0 ? 0 : (double)answered / gold.Count,
                answered == 0 ? 0 : (double)correct / answered, macro, lo, hi, ece,
                scored == 0 ? 0 : brier / scored, scored == 0 ? 0 : logLoss / scored, perLabel, confusion);
        }

        private static List<LabelMetrics> PerLabel(IReadOnlyList<string> labelNames, int[][] confusion)
        {
            int k = labelNames.Count;
            var result = new List<LabelMetrics>(k);
            for (int j = 0; j < k; j++)
            {
                int tp = confusion[j][j];
                int support = confusion[j].Sum();
                int predicted = Enumerable.Range(0, k).Sum(r => confusion[r][j]);
                double precision = predicted == 0 ? 0 : (double)tp / predicted;
                double recall = support == 0 ? 0 : (double)tp / support;
                double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
                result.Add(new LabelMetrics(labelNames[j], support, predicted, precision, recall, f1));
            }
            return result;
        }

        /// <summary>Macro-F1 over the labels that occur in the gold set (absent labels would only add zeros).</summary>
        private static double MacroF1(IReadOnlyList<LabelMetrics> perLabel)
        {
            var present = perLabel.Where(l => l.Support > 0).ToList();
            return present.Count == 0 ? 0 : present.Average(l => l.F1);
        }

        public static int ArgMax(IReadOnlyList<float> p)
        {
            int best = 0;
            for (int i = 1; i < p.Count; i++) if (p[i] > p[best]) best = i;
            return best;
        }

        /// <summary>
        /// Population stability index between two label distributions (drift alert, plan F-ML): Σ (a − b)·ln(a / b) with a
        /// small floor so a label that appears or disappears counts instead of dividing by zero.
        /// </summary>
        public static double Psi(IReadOnlyDictionary<string, double> baseline, IReadOnlyDictionary<string, double> current)
        {
            const double floor = 1e-4;
            double bTotal = Math.Max(floor, baseline.Values.Sum()), cTotal = Math.Max(floor, current.Values.Sum());
            double psi = 0;
            foreach (var label in baseline.Keys.Union(current.Keys))
            {
                double b = Math.Max(floor, baseline.GetValueOrDefault(label) / bTotal);
                double c = Math.Max(floor, current.GetValueOrDefault(label) / cTotal);
                psi += (c - b) * Math.Log(c / b);
            }
            return psi;
        }
    }
}
