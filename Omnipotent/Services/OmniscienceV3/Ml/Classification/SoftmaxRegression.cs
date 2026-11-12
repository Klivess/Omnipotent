using System.Numerics.Tensors;

namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>One training example: features, label index, and a weight (class balance × duplicate folding).</summary>
    public readonly record struct Example(float[] Features, int Label, double Weight = 1.0);

    public sealed record TrainingOptions
    {
        /// <summary>L2 penalty on the weights (not the biases).</summary>
        public double L2 { get; init; } = 1e-3;
        public int MaxIterations { get; init; } = 300;
        public double GradientTolerance { get; init; } = 1e-6;
        public int Memory { get; init; } = 10;
        /// <summary>Examples per parallel gradient chunk; chunks are summed in a fixed order, so training is deterministic.</summary>
        public int ChunkSize { get; init; } = 512;
    }

    public sealed record TrainingResult(float[] Weights, float[] Bias, int Iterations, double FinalLoss, bool Converged);

    /// <summary>
    /// Multinomial logistic regression (softmax regression) with L2, fitted by L-BFGS with an Armijo backtracking line
    /// search: the "light task head" family of plan R2 (the plan names ML.NET logistic regression; this is the same model
    /// family implemented in-house so that training is bit-for-bit deterministic for a given input order, gradients
    /// are computed in parallel fixed-order chunks, and weights can be applied with SIMD straight to stored embeddings).
    /// </summary>
    public static class SoftmaxRegression
    {
        public static TrainingResult Train(IReadOnlyList<Example> examples, int labels, int dims, TrainingOptions? options = null, CancellationToken ct = default)
        {
            var o = options ?? new TrainingOptions();
            if (examples.Count == 0) throw new ArgumentException("No training examples.");
            if (labels < 2) throw new ArgumentException("A classifier needs at least two labels.");
            foreach (var e in examples)
            {
                if (e.Features.Length != dims) throw new ArgumentException($"Example has {e.Features.Length} features, expected {dims}.");
                if (e.Label < 0 || e.Label >= labels) throw new ArgumentException($"Label {e.Label} is outside 0..{labels - 1}.");
            }
            int n = labels * dims + labels;
            var theta = new double[n];
            double totalWeight = examples.Sum(e => Math.Max(0, e.Weight));
            if (totalWeight <= 0) throw new ArgumentException("Example weights sum to zero.");

            double Evaluate(double[] t, double[] grad) => LossAndGradient(examples, labels, dims, t, grad, totalWeight, o, ct);

            var g = new double[n];
            double f = Evaluate(theta, g);
            var sList = new List<double[]>();
            var yList = new List<double[]>();
            var rhoList = new List<double>();
            int iter = 0;
            bool converged = false;
            var direction = new double[n];
            var xNew = new double[n];
            var gNew = new double[n];
            for (; iter < o.MaxIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                double gnorm = Math.Sqrt(DotD(g, g));
                if (gnorm < o.GradientTolerance) { converged = true; break; }

                // Two-loop recursion: direction = -H·g.
                Array.Copy(g, direction, n);
                var alpha = new double[sList.Count];
                for (int i = sList.Count - 1; i >= 0; i--)
                {
                    alpha[i] = rhoList[i] * DotD(sList[i], direction);
                    Axpy(-alpha[i], yList[i], direction);
                }
                if (sList.Count > 0)
                {
                    double gamma = DotD(sList[^1], yList[^1]) / DotD(yList[^1], yList[^1]);
                    for (int i = 0; i < n; i++) direction[i] *= gamma;
                }
                for (int i = 0; i < sList.Count; i++)
                {
                    double beta = rhoList[i] * DotD(yList[i], direction);
                    Axpy(alpha[i] - beta, sList[i], direction);
                }
                for (int i = 0; i < n; i++) direction[i] = -direction[i];
                double slope = DotD(g, direction);
                if (slope >= 0)
                {
                    // Not a descent direction (curvature information went stale): restart from steepest descent.
                    for (int i = 0; i < n; i++) direction[i] = -g[i];
                    slope = -DotD(g, g);
                    sList.Clear(); yList.Clear(); rhoList.Clear();
                }

                double step = iter == 0 && sList.Count == 0 ? 1.0 / Math.Max(1.0, gnorm) : 1.0;
                double fNew = double.NaN;
                bool accepted = false;
                for (int ls = 0; ls < 40; ls++)
                {
                    for (int i = 0; i < n; i++) xNew[i] = theta[i] + step * direction[i];
                    fNew = Evaluate(xNew, gNew);
                    if (double.IsFinite(fNew) && fNew <= f + 1e-4 * step * slope) { accepted = true; break; }
                    step *= 0.5;
                }
                if (!accepted) { converged = true; break; } // no further progress possible at machine precision

                var s = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { s[i] = xNew[i] - theta[i]; y[i] = gNew[i] - g[i]; }
                double sy = DotD(s, y);
                if (sy > 1e-12)
                {
                    sList.Add(s); yList.Add(y); rhoList.Add(1.0 / sy);
                    if (sList.Count > o.Memory) { sList.RemoveAt(0); yList.RemoveAt(0); rhoList.RemoveAt(0); }
                }
                bool small = Math.Abs(f - fNew) < 1e-10 * Math.Max(1, Math.Abs(f));
                Array.Copy(xNew, theta, n);
                Array.Copy(gNew, g, n);
                f = fNew;
                if (small) { converged = true; iter++; break; }
            }

            var weights = new float[labels * dims];
            var bias = new float[labels];
            for (int i = 0; i < labels * dims; i++) weights[i] = (float)theta[i];
            for (int k = 0; k < labels; k++) bias[k] = (float)theta[labels * dims + k];
            return new TrainingResult(weights, bias, iter, f, converged);
        }

        private static double LossAndGradient(IReadOnlyList<Example> examples, int labels, int dims, double[] theta, double[] grad, double totalWeight,
            TrainingOptions o, CancellationToken ct)
        {
            int n = theta.Length;
            var w = new float[labels * dims];
            for (int i = 0; i < w.Length; i++) w[i] = (float)theta[i];
            var b = new double[labels];
            for (int k = 0; k < labels; k++) b[k] = theta[labels * dims + k];

            int chunks = (examples.Count + o.ChunkSize - 1) / o.ChunkSize;
            var partialLoss = new double[chunks];
            var partialGrad = new double[chunks][];
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, c =>
            {
                var gradLocal = new double[n];
                var logits = new double[labels];
                var p = new double[labels];
                double loss = 0;
                int end = Math.Min(examples.Count, (c + 1) * o.ChunkSize);
                for (int i = c * o.ChunkSize; i < end; i++)
                {
                    var e = examples[i];
                    double wt = Math.Max(0, e.Weight);
                    if (wt == 0) continue;
                    var x = e.Features;
                    for (int k = 0; k < labels; k++) logits[k] = TensorPrimitives.Dot(new ReadOnlySpan<float>(w, k * dims, dims), x) + b[k];
                    double max = logits.Max(), sum = 0;
                    for (int k = 0; k < labels; k++) { p[k] = Math.Exp(logits[k] - max); sum += p[k]; }
                    for (int k = 0; k < labels; k++) p[k] /= sum;
                    loss -= wt * Math.Log(Math.Max(p[e.Label], 1e-300));
                    for (int k = 0; k < labels; k++)
                    {
                        double d = wt * (p[k] - (k == e.Label ? 1 : 0));
                        if (d == 0) continue;
                        int off = k * dims;
                        for (int j = 0; j < dims; j++) gradLocal[off + j] += d * x[j];
                        gradLocal[labels * dims + k] += d;
                    }
                }
                partialLoss[c] = loss;
                partialGrad[c] = gradLocal;
            });

            Array.Clear(grad);
            double total = 0;
            for (int c = 0; c < chunks; c++)
            {
                total += partialLoss[c];
                var pg = partialGrad[c];
                for (int i = 0; i < n; i++) grad[i] += pg[i];
            }
            double inv = 1.0 / totalWeight;
            total *= inv;
            for (int i = 0; i < n; i++) grad[i] *= inv;
            double reg = 0;
            for (int i = 0; i < labels * dims; i++)
            {
                reg += theta[i] * theta[i];
                grad[i] += o.L2 * theta[i];
            }
            return total + 0.5 * o.L2 * reg;
        }

        private static double DotD(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        private static void Axpy(double a, double[] x, double[] y)
        {
            for (int i = 0; i < y.Length; i++) y[i] += a * x[i];
        }

        /// <summary>Raw logits of one feature vector (weights row-major labels × dims).</summary>
        public static void Logits(ReadOnlySpan<float> weights, ReadOnlySpan<float> bias, ReadOnlySpan<float> x, Span<float> logits)
        {
            int labels = bias.Length, dims = x.Length;
            for (int k = 0; k < labels; k++) logits[k] = TensorPrimitives.Dot(weights.Slice(k * dims, dims), x) + bias[k];
        }

        /// <summary>Softmax with temperature, in place.</summary>
        public static void Softmax(Span<float> logits, float temperature = 1f)
        {
            float t = temperature <= 0 ? 1f : temperature;
            float max = float.NegativeInfinity;
            foreach (var l in logits) max = Math.Max(max, l);
            float sum = 0;
            for (int i = 0; i < logits.Length; i++) { logits[i] = MathF.Exp((logits[i] - max) / t); sum += logits[i]; }
            for (int i = 0; i < logits.Length; i++) logits[i] /= sum;
        }
    }
}
