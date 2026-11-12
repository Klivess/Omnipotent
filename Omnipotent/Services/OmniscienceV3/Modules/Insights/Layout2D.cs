namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>
    /// Laying people out on a plane (N16): a deterministic t-SNE started from the principal components, and the Procrustes
    /// alignment that keeps tonight's map facing the way last night's did. No randomness anywhere, so the same input gives
    /// the same map, and a small change in the input gives a map that is recognisably the same.
    /// </summary>
    internal static class Layout2D
    {
        /// <summary>The first two principal components of the rows (power iteration from a fixed start).</summary>
        public static double[][] Pca(IReadOnlyList<float[]> x)
        {
            int n = x.Count;
            var result = new double[n][];
            if (n == 0) return result;
            int d = x[0].Length;
            var mean = new double[d];
            foreach (var row in x)
                for (int j = 0; j < d; j++) mean[j] += row[j];
            for (int j = 0; j < d; j++) mean[j] /= n;

            double[] Component(double[]? orthogonalTo)
            {
                var v = new double[d];
                for (int j = 0; j < d; j++) v[j] = Math.Sin(j * 12.9898 + (orthogonalTo == null ? 1.0 : 2.0)) + 1e-3;
                var projected = new double[n];
                for (int iter = 0; iter < 60; iter++)
                {
                    if (orthogonalTo != null)
                    {
                        double along = 0;
                        for (int j = 0; j < d; j++) along += v[j] * orthogonalTo[j];
                        for (int j = 0; j < d; j++) v[j] -= along * orthogonalTo[j];
                    }
                    double norm = Math.Sqrt(v.Sum(a => a * a));
                    if (norm < 1e-12) break;
                    for (int j = 0; j < d; j++) v[j] /= norm;
                    for (int i = 0; i < n; i++)
                    {
                        double s = 0;
                        var row = x[i];
                        for (int j = 0; j < d; j++) s += (row[j] - mean[j]) * v[j];
                        projected[i] = s;
                    }
                    var next = new double[d];
                    for (int i = 0; i < n; i++)
                    {
                        var row = x[i];
                        double s = projected[i];
                        for (int j = 0; j < d; j++) next[j] += (row[j] - mean[j]) * s;
                    }
                    if (next.Sum(a => a * a) < 1e-24) break;
                    v = next;
                }
                if (orthogonalTo != null)
                {
                    double along = 0;
                    for (int j = 0; j < d; j++) along += v[j] * orthogonalTo[j];
                    for (int j = 0; j < d; j++) v[j] -= along * orthogonalTo[j];
                }
                double length = Math.Sqrt(v.Sum(a => a * a));
                if (length > 1e-12)
                    for (int j = 0; j < d; j++) v[j] /= length;
                return v;
            }

            var first = Component(null);
            var second = Component(first);
            for (int i = 0; i < n; i++)
            {
                double a = 0, b = 0;
                var row = x[i];
                for (int j = 0; j < d; j++)
                {
                    double c = row[j] - mean[j];
                    a += c * first[j];
                    b += c * second[j];
                }
                result[i] = new[] { a, b };
            }
            return result;
        }

        /// <summary>
        /// Exact t-SNE over unit vectors (cosine distance), started from the principal components. Good for a few thousand
        /// points: time and memory grow with the square of their number.
        /// <para>
        /// With <paramref name="start"/> (a position for every point, for instance last night's map) it refines that layout
        /// instead of making a new one: a small change in the input then gives a small change in the map.
        /// </para>
        /// </summary>
        public static double[][] Tsne(IReadOnlyList<float[]> x, int iterations = 500, int maxThreads = 0, IReadOnlyList<(double X, double Y)>? start = null)
        {
            int n = x.Count;
            bool warm = start != null && start.Count == n && n >= 5;
            var y = warm ? start!.Select(p => new[] { p.X, p.Y }).ToArray() : Pca(x);
            if (n < 5) return y;
            if (warm) iterations = Math.Min(iterations, 200);
            // A small, well-spread start: the principal components, shrunk.
            double spread = Math.Sqrt(y.Sum(p => p[0] * p[0] + p[1] * p[1]) / (2.0 * n));
            for (int i = 0; i < n && !warm; i++)
            {
                if (spread < 1e-9)
                {
                    // All points alike: a spiral, so that they can move apart at all.
                    double angle = i * 2.399963, radius = 1e-4 * Math.Sqrt(i + 1);
                    y[i] = new[] { radius * Math.Cos(angle), radius * Math.Sin(angle) };
                }
                else
                {
                    y[i][0] *= 1e-2 / spread;
                    y[i][1] *= 1e-2 / spread;
                }
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Math.Max(1, Environment.ProcessorCount / 2) };
            double perplexity = Math.Min(30, Math.Max(2, (n - 1) / 3.0)), target = Math.Log(perplexity);
            var p = new float[(long)n * n];
            Parallel.For(0, n, options, i =>
            {
                var d2 = new double[n];
                var xi = x[i];
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    double dot = 0;
                    var xj = x[j];
                    for (int k = 0; k < xi.Length; k++) dot += xi[k] * xj[k];
                    d2[j] = Math.Max(0, 2 - 2 * dot);
                }
                double beta = 1, lo = 0, hi = double.PositiveInfinity;
                var row = new double[n];
                for (int step = 0; step < 60; step++)
                {
                    double sum = 0, weighted = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) { row[j] = 0; continue; }
                        row[j] = Math.Exp(-d2[j] * beta);
                        sum += row[j];
                        weighted += d2[j] * row[j];
                    }
                    if (sum < 1e-300) { hi = beta; beta = (lo + hi) / 2; continue; }
                    double entropy = Math.Log(sum) + beta * weighted / sum;
                    for (int j = 0; j < n; j++) row[j] /= sum;
                    if (Math.Abs(entropy - target) < 1e-5) break;
                    if (entropy > target) { lo = beta; beta = double.IsPositiveInfinity(hi) ? beta * 2 : (lo + hi) / 2; }
                    else { hi = beta; beta = (lo + hi) / 2; }
                }
                for (int j = 0; j < n; j++) p[(long)i * n + j] = (float)row[j];
            });
            // Symmetric joint probabilities.
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float v = Math.Max(1e-12f, (p[(long)i * n + j] + p[(long)j * n + i]) / (2f * n));
                    p[(long)i * n + j] = v;
                    p[(long)j * n + i] = v;
                }

            var num = new float[(long)n * n];
            var rowSum = new double[n];
            var grad = new double[n][];
            var velocity = new double[n][];
            var gain = new double[n][];
            for (int i = 0; i < n; i++)
            {
                grad[i] = new double[2];
                velocity[i] = new double[2];
                gain[i] = new[] { 1.0, 1.0 };
            }
            for (int iter = 0; iter < iterations; iter++)
            {
                Parallel.For(0, n, options, i =>
                {
                    double s = 0, yi0 = y[i][0], yi1 = y[i][1];
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) { num[(long)i * n + j] = 0; continue; }
                        double dx = yi0 - y[j][0], dy = yi1 - y[j][1];
                        double q = 1 / (1 + dx * dx + dy * dy);
                        num[(long)i * n + j] = (float)q;
                        s += q;
                    }
                    rowSum[i] = s;
                });
                double total = 0;
                for (int i = 0; i < n; i++) total += rowSum[i];
                total = Math.Max(total, 1e-12);
                double exaggeration = !warm && iter < 100 ? 12 : 1;
                Parallel.For(0, n, options, i =>
                {
                    double g0 = 0, g1 = 0, yi0 = y[i][0], yi1 = y[i][1];
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        double q = num[(long)i * n + j];
                        double w = (exaggeration * p[(long)i * n + j] - q / total) * q;
                        g0 += w * (yi0 - y[j][0]);
                        g1 += w * (yi1 - y[j][1]);
                    }
                    grad[i][0] = 4 * g0;
                    grad[i][1] = 4 * g1;
                });
                double momentum = !warm && iter < 250 ? 0.5 : 0.8;
                double mean0 = 0, mean1 = 0;
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        gain[i][k] = Math.Sign(grad[i][k]) != Math.Sign(velocity[i][k]) ? gain[i][k] + 0.2 : Math.Max(0.01, gain[i][k] * 0.8);
                        velocity[i][k] = momentum * velocity[i][k] - 200 * gain[i][k] * grad[i][k];
                        y[i][k] += velocity[i][k];
                    }
                    mean0 += y[i][0];
                    mean1 += y[i][1];
                }
                mean0 /= n;
                mean1 /= n;
                for (int i = 0; i < n; i++)
                {
                    y[i][0] -= mean0;
                    y[i][1] -= mean1;
                }
            }
            return y;
        }

        /// <summary>
        /// The similarity transform (turn, mirror, scale, shift) that puts <paramref name="from"/> as close as possible onto
        /// <paramref name="to"/> (the same points in last night's map), to be applied to every point of tonight's map.
        /// With fewer than three pairs there is nothing to align to and the transform changes nothing.
        /// </summary>
        public static Func<double, double, (double X, double Y)> Procrustes(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
        {
            int n = Math.Min(from.Count, to.Count);
            if (n < 3) return (x, y) => (x, y);
            double fx = from.Take(n).Average(p => p.X), fy = from.Take(n).Average(p => p.Y), tx = to.Take(n).Average(p => p.X), ty = to.Take(n).Average(p => p.Y);
            double size = 0;
            for (int i = 0; i < n; i++) size += (from[i].X - fx) * (from[i].X - fx) + (from[i].Y - fy) * (from[i].Y - fy);
            if (size < 1e-18) return (x, y) => (x, y);

            (double Cos, double Sin, double Scale, double Residual) Fit(double flip)
            {
                double cross = 0, dot = 0;
                for (int i = 0; i < n; i++)
                {
                    double ax = from[i].X - fx, ay = (from[i].Y - fy) * flip, bx = to[i].X - tx, by = to[i].Y - ty;
                    cross += ax * by - ay * bx;
                    dot += ax * bx + ay * by;
                }
                double angle = Math.Atan2(cross, dot), cos = Math.Cos(angle), sin = Math.Sin(angle);
                double scale = (cos * dot + sin * cross) / size, residual = 0;
                for (int i = 0; i < n; i++)
                {
                    double ax = from[i].X - fx, ay = (from[i].Y - fy) * flip, bx = to[i].X - tx, by = to[i].Y - ty;
                    double rx = scale * (cos * ax - sin * ay) - bx, ry = scale * (sin * ax + cos * ay) - by;
                    residual += rx * rx + ry * ry;
                }
                return (cos, sin, scale, residual);
            }

            var plain = Fit(1);
            var mirrored = Fit(-1);
            double f = mirrored.Residual < plain.Residual - 1e-12 ? -1 : 1;
            var best = f < 0 ? mirrored : plain;
            return (x, y) =>
            {
                double ax = x - fx, ay = (y - fy) * f;
                return (best.Scale * (best.Cos * ax - best.Sin * ay) + tx, best.Scale * (best.Sin * ax + best.Cos * ay) + ty);
            };
        }
    }
}
