using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    /// <summary>
    /// Everything computed once at the end of a run: annualised risk/return, distribution shape,
    /// benchmark-relative statistics, trade quality, the monthly return grid and downsampled series
    /// for the underwater and rolling-Sharpe charts. Persisted with the result so the UI never
    /// recomputes from a truncated page.
    /// </summary>
    public sealed class BacktestAnalysis
    {
        public double BarsPerYear { get; set; }
        public bool ContinuousMarket { get; set; }

        // returns
        public double TotalReturnPct { get; set; }
        public double CagrPct { get; set; }
        public double BestBarPct { get; set; }
        public double WorstBarPct { get; set; }
        public double PositiveBarsPct { get; set; }

        // risk-adjusted
        public double Sharpe { get; set; }
        public double Sortino { get; set; }
        public double Calmar { get; set; }
        public double Omega { get; set; }
        public double VolatilityPct { get; set; }
        public double DownsideVolatilityPct { get; set; }
        public double TailRatio { get; set; }
        public double Skew { get; set; }
        public double Kurtosis { get; set; }
        public double VaR95Pct { get; set; }
        public double CVaR95Pct { get; set; }

        // drawdown
        public double MaxDrawdownPct { get; set; }
        public double AvgDrawdownPct { get; set; }
        public double UlcerIndex { get; set; }
        public int MaxDrawdownBars { get; set; }
        public double MaxDrawdownDays { get; set; }
        public int DrawdownEpisodes { get; set; }
        public DateTime? MaxDrawdownPeakUtc { get; set; }
        public DateTime? MaxDrawdownTroughUtc { get; set; }
        public DateTime? MaxDrawdownRecoveryUtc { get; set; }

        // trades
        public int Trades { get; set; }
        public double WinRatePct { get; set; }
        public double? ProfitFactor { get; set; }
        public double ExpectancyPct { get; set; }
        public double PayoffRatio { get; set; }
        public double Sqn { get; set; }
        public double KellyPct { get; set; }
        public double AvgBarsHeld { get; set; }
        public double TradesPerYear { get; set; }
        public double ExposurePct { get; set; }
        public double AvgMaePct { get; set; }
        public double AvgMfePct { get; set; }
        public double LongPnL { get; set; }
        public double ShortPnL { get; set; }
        public double FeesPctOfGross { get; set; }

        // benchmark
        public double BenchmarkReturnPct { get; set; }
        public double BenchmarkCagrPct { get; set; }
        public double BenchmarkMaxDrawdownPct { get; set; }
        public double BenchmarkSharpe { get; set; }
        public double Alpha { get; set; }
        public double Beta { get; set; }
        public double Correlation { get; set; }
        public double InformationRatio { get; set; }
        public double TrackingErrorPct { get; set; }
        public double UpCapture { get; set; }
        public double DownCapture { get; set; }

        /// <summary>year → month (1-12) → return %. Plus a "Year" total per year under key 13.</summary>
        public SortedDictionary<int, SortedDictionary<int, double>> Monthly { get; set; } = new();
        public List<SeriesPoint> Drawdown { get; set; } = new();
        public List<SeriesPoint> RollingSharpe { get; set; } = new();
        public List<SeriesPoint> Benchmark { get; set; } = new();
        public List<HistogramBin> ReturnHistogram { get; set; } = new();
        public List<HistogramBin> TradeHistogram { get; set; } = new();
    }

    public sealed record SeriesPoint(DateTime T, double V);
    public sealed record HistogramBin(double From, double To, int Count);

    public static class BacktestAnalyzer
    {
        public static BacktestAnalysis Analyze(IReadOnlyList<EquityPoint> curve, IReadOnlyList<TradeRecord> trades,
            IReadOnlyList<OHLCCandle> benchmark, double barsPerYear, bool continuousMarket, decimal fees)
        {
            var a = new BacktestAnalysis { BarsPerYear = barsPerYear, ContinuousMarket = continuousMarket };
            if (curve.Count < 2) return a;

            double[] eq = curve.Select(p => (double)p.Equity).ToArray();
            DateTime[] ts = curve.Select(p => p.Ts).ToArray();
            double[] r = Returns(eq);
            double years = Math.Max(1e-9, (ts[^1] - ts[0]).TotalDays / 365.25);

            a.TotalReturnPct = eq[0] > 0 ? (eq[^1] / eq[0] - 1) * 100 : 0;
            a.CagrPct = eq[0] > 0 && eq[^1] > 0 ? (Math.Pow(eq[^1] / eq[0], 1 / years) - 1) * 100 : -100;
            if (r.Length > 0)
            {
                a.BestBarPct = r.Max() * 100;
                a.WorstBarPct = r.Min() * 100;
                a.PositiveBarsPct = r.Count(x => x > 0) * 100.0 / r.Length;
            }

            double mean = Mean(r), sd = Std(r);
            double downside = Math.Sqrt(r.Length == 0 ? 0 : r.Sum(x => x < 0 ? x * x : 0) / r.Length);
            double ann = Math.Sqrt(barsPerYear);
            a.Sharpe = sd > 0 ? mean / sd * ann : 0;
            a.Sortino = downside > 0 ? mean / downside * ann : 0;
            a.VolatilityPct = sd * ann * 100;
            a.DownsideVolatilityPct = downside * ann * 100;
            double gains = r.Where(x => x > 0).Sum(), losses = -r.Where(x => x < 0).Sum();
            a.Omega = losses > 0 ? gains / losses : 0;
            if (r.Length >= 20)
            {
                var sorted = r.OrderBy(x => x).ToArray();
                double p5 = Percentile(sorted, 0.05), p95 = Percentile(sorted, 0.95);
                a.TailRatio = p5 < 0 ? p95 / -p5 : 0;
                a.VaR95Pct = -p5 * 100;
                var tail = sorted.Where(x => x <= p5).ToArray();
                a.CVaR95Pct = tail.Length > 0 ? -tail.Average() * 100 : a.VaR95Pct;
            }
            a.Skew = Skewness(r);
            a.Kurtosis = ExcessKurtosis(r);

            // drawdowns
            double peak = eq[0];
            int peakIdx = 0, maxBars = 0, episodes = 0;
            double maxDd = 0, ddSumSq = 0, ddSum = 0;
            int ddBarsCount = 0;
            bool inDd = false;
            int mddPeak = 0, mddTrough = 0;
            var ddSeries = new double[eq.Length];
            for (int i = 0; i < eq.Length; i++)
            {
                if (eq[i] >= peak)
                {
                    if (inDd) { inDd = false; maxBars = Math.Max(maxBars, i - peakIdx); }
                    peak = eq[i];
                    peakIdx = i;
                }
                double dd = peak > 0 ? (peak - eq[i]) / peak : 0;
                ddSeries[i] = -dd * 100;
                if (dd > 0)
                {
                    if (!inDd) { inDd = true; episodes++; }
                    ddSum += dd; ddBarsCount++;
                }
                ddSumSq += dd * 100 * dd * 100;
                if (dd > maxDd) { maxDd = dd; mddPeak = peakIdx; mddTrough = i; }
            }
            if (inDd) maxBars = Math.Max(maxBars, eq.Length - 1 - peakIdx);
            a.MaxDrawdownPct = maxDd * 100;
            a.AvgDrawdownPct = ddBarsCount > 0 ? ddSum / ddBarsCount * 100 : 0;
            a.UlcerIndex = Math.Sqrt(ddSumSq / eq.Length);
            a.MaxDrawdownBars = maxBars;
            a.MaxDrawdownDays = maxBars / Math.Max(1e-9, barsPerYear) * 365;
            a.DrawdownEpisodes = episodes;
            if (maxDd > 0)
            {
                a.MaxDrawdownPeakUtc = ts[mddPeak];
                a.MaxDrawdownTroughUtc = ts[mddTrough];
                for (int i = mddTrough; i < eq.Length; i++)
                    if (eq[i] >= eq[mddPeak]) { a.MaxDrawdownRecoveryUtc = ts[i]; break; }
            }
            a.Calmar = a.MaxDrawdownPct > 0 ? a.CagrPct / a.MaxDrawdownPct : 0;
            a.Drawdown = Downsample(ts, ddSeries, 800);

            // rolling Sharpe over ~a quarter of a year of bars (min 20)
            int window = (int)Math.Clamp(barsPerYear / 4, 20, 2000);
            if (r.Length > window)
            {
                var roll = new double[r.Length];
                double s = 0, s2 = 0;
                for (int i = 0; i < r.Length; i++)
                {
                    s += r[i]; s2 += r[i] * r[i];
                    if (i >= window) { s -= r[i - window]; s2 -= r[i - window] * r[i - window]; }
                    if (i >= window - 1)
                    {
                        double m = s / window, v = Math.Max(0, s2 / window - m * m);
                        roll[i] = v > 0 ? m / Math.Sqrt(v) * ann : 0;
                    }
                    else roll[i] = double.NaN;
                }
                a.RollingSharpe = Downsample(ts.Skip(1).ToArray(), roll, 600).Where(p => !double.IsNaN(p.V)).ToList();
            }

            // monthly grid (month-end equity over previous month-end)
            var monthEnds = new SortedDictionary<(int Y, int M), double>();
            for (int i = 0; i < eq.Length; i++) monthEnds[(ts[i].Year, ts[i].Month)] = eq[i];
            double prev = eq[0];
            var yearStart = new Dictionary<int, double>();
            foreach (var ((y, m), value) in monthEnds)
            {
                if (!a.Monthly.TryGetValue(y, out var row)) a.Monthly[y] = row = new SortedDictionary<int, double>();
                if (!yearStart.ContainsKey(y)) yearStart[y] = prev;
                row[m] = prev > 0 ? (value / prev - 1) * 100 : 0;
                prev = value;
                row[13] = yearStart[y] > 0 ? (value / yearStart[y] - 1) * 100 : 0;
            }

            a.ReturnHistogram = Histogram(r.Select(x => x * 100).ToArray(), 30);

            // trades
            a.Trades = trades.Count;
            if (trades.Count > 0)
            {
                var pnl = trades.Select(t => (double)t.RealizedPnL).ToArray();
                var tradeRet = trades.Select(t => (double)(t.EntryPrice * t.Qty) is var cost && cost != 0 ? (double)t.RealizedPnL / cost * 100 : 0).ToArray();
                int wins = pnl.Count(x => x > 0);
                a.WinRatePct = wins * 100.0 / trades.Count;
                double grossWin = pnl.Where(x => x > 0).Sum(), grossLoss = -pnl.Where(x => x <= 0).Sum();
                a.ProfitFactor = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? null : 0);
                a.ExpectancyPct = tradeRet.Average();
                double avgWin = wins > 0 ? pnl.Where(x => x > 0).Average() : 0;
                double avgLoss = trades.Count - wins > 0 ? -pnl.Where(x => x <= 0).Average() : 0;
                a.PayoffRatio = avgLoss > 0 ? avgWin / avgLoss : 0;
                double tsd = Std(tradeRet);
                a.Sqn = tsd > 0 ? Math.Sqrt(Math.Min(trades.Count, 100)) * tradeRet.Average() / tsd : 0;
                double w = a.WinRatePct / 100;
                a.KellyPct = a.PayoffRatio > 0 ? (w - (1 - w) / a.PayoffRatio) * 100 : 0;
                a.AvgBarsHeld = trades.Average(t => t.BarsHeld);
                a.TradesPerYear = trades.Count / years;
                double inMarketDays = trades.Sum(t => (t.ExitTime - t.EntryTime).TotalDays);
                a.ExposurePct = Math.Min(100, inMarketDays / Math.Max(1e-9, (ts[^1] - ts[0]).TotalDays) * 100);
                a.AvgMaePct = trades.Average(t => t.MaePct);
                a.AvgMfePct = trades.Average(t => t.MfePct);
                a.LongPnL = trades.Where(t => !t.IsShort).Sum(t => (double)t.RealizedPnL);
                a.ShortPnL = trades.Where(t => t.IsShort).Sum(t => (double)t.RealizedPnL);
                double gross = Math.Abs(pnl.Sum() + (double)fees);
                a.FeesPctOfGross = gross > 0 ? (double)fees / gross * 100 : 0;
                a.TradeHistogram = Histogram(tradeRet, 24);
            }

            // benchmark (buy and hold of the benchmark series, aligned to the equity timestamps)
            if (benchmark.Count > 1)
            {
                var byTs = new Dictionary<DateTime, double>();
                foreach (var c in benchmark) byTs[c.Timestamp] = (double)c.Close;
                var bench = new double[eq.Length];
                double last = double.NaN;
                for (int i = 0; i < eq.Length; i++)
                {
                    if (byTs.TryGetValue(ts[i], out var px)) last = px;
                    bench[i] = last;
                }
                int first = Array.FindIndex(bench, x => !double.IsNaN(x) && x > 0);
                if (first >= 0 && first < eq.Length - 1)
                {
                    double b0 = bench[first], bN = bench[^1];
                    a.BenchmarkReturnPct = (bN / b0 - 1) * 100;
                    double byears = Math.Max(1e-9, (ts[^1] - ts[first]).TotalDays / 365.25);
                    a.BenchmarkCagrPct = bN > 0 ? (Math.Pow(bN / b0, 1 / byears) - 1) * 100 : -100;
                    double bpeak = b0, bmdd = 0;
                    var bseries = new double[eq.Length];
                    for (int i = 0; i < eq.Length; i++)
                    {
                        double v = double.IsNaN(bench[i]) ? b0 : bench[i];
                        bpeak = Math.Max(bpeak, v);
                        bmdd = Math.Max(bmdd, (bpeak - v) / bpeak);
                        bseries[i] = v / b0 * eq[0];
                    }
                    a.BenchmarkMaxDrawdownPct = bmdd * 100;
                    a.Benchmark = Downsample(ts, bseries, 800);

                    var br = new List<double>();
                    var sr = new List<double>();
                    for (int i = Math.Max(1, first + 1); i < eq.Length; i++)
                    {
                        if (double.IsNaN(bench[i - 1]) || bench[i - 1] <= 0 || eq[i - 1] <= 0) continue;
                        br.Add(bench[i] / bench[i - 1] - 1);
                        sr.Add(eq[i] / eq[i - 1] - 1);
                    }
                    if (br.Count > 2)
                    {
                        double bm = br.Average(), sm = sr.Average();
                        double cov = 0, vb = 0, vs = 0;
                        for (int i = 0; i < br.Count; i++)
                        {
                            cov += (br[i] - bm) * (sr[i] - sm);
                            vb += (br[i] - bm) * (br[i] - bm);
                            vs += (sr[i] - sm) * (sr[i] - sm);
                        }
                        a.Beta = vb > 0 ? cov / vb : 0;
                        a.Correlation = vb > 0 && vs > 0 ? cov / Math.Sqrt(vb * vs) : 0;
                        a.Alpha = (sm - a.Beta * bm) * barsPerYear * 100;
                        var active = sr.Zip(br, (s, b) => s - b).ToArray();
                        double te = Std(active);
                        a.TrackingErrorPct = te * ann * 100;
                        a.InformationRatio = te > 0 ? Mean(active) / te * ann : 0;
                        double bsd = Std(br.ToArray());
                        a.BenchmarkSharpe = bsd > 0 ? bm / bsd * ann : 0;
                        double upS = 0, upB = 0, dnS = 0, dnB = 0;
                        for (int i = 0; i < br.Count; i++)
                        {
                            if (br[i] > 0) { upS += sr[i]; upB += br[i]; }
                            else if (br[i] < 0) { dnS += sr[i]; dnB += br[i]; }
                        }
                        a.UpCapture = upB != 0 ? upS / upB * 100 : 0;
                        a.DownCapture = dnB != 0 ? dnS / dnB * 100 : 0;
                    }
                }
            }

            Sanitize(a);
            return a;
        }

        public static double[] Returns(double[] equity)
        {
            if (equity.Length < 2) return Array.Empty<double>();
            var r = new double[equity.Length - 1];
            for (int i = 1; i < equity.Length; i++)
                r[i - 1] = equity[i - 1] > 0 ? equity[i] / equity[i - 1] - 1 : 0;
            return r;
        }

        public static double Mean(double[] x) => x.Length == 0 ? 0 : x.Average();

        public static double Std(double[] x)
        {
            if (x.Length < 2) return 0;
            double m = x.Average();
            return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / (x.Length - 1));
        }

        public static double Skewness(double[] x)
        {
            if (x.Length < 3) return 0;
            double m = x.Average(), s = Std(x);
            if (s == 0) return 0;
            double n = x.Length;
            return n / ((n - 1) * (n - 2)) * x.Sum(v => Math.Pow((v - m) / s, 3));
        }

        public static double ExcessKurtosis(double[] x)
        {
            if (x.Length < 4) return 0;
            double m = x.Average(), s = Std(x);
            if (s == 0) return 0;
            double n = x.Length;
            double k = n * (n + 1) / ((n - 1) * (n - 2) * (n - 3)) * x.Sum(v => Math.Pow((v - m) / s, 4));
            return k - 3 * (n - 1) * (n - 1) / ((n - 2) * (n - 3));
        }

        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double idx = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }

        public static List<HistogramBin> Histogram(double[] values, int bins)
        {
            var finite = values.Where(double.IsFinite).ToArray();
            if (finite.Length < 2) return new List<HistogramBin>();
            double min = finite.Min(), max = finite.Max();
            if (max - min < 1e-12) return new List<HistogramBin>();
            double width = (max - min) / bins;
            var counts = new int[bins];
            foreach (var v in finite) counts[Math.Min(bins - 1, (int)((v - min) / width))]++;
            return Enumerable.Range(0, bins).Select(i => new HistogramBin(min + i * width, min + (i + 1) * width, counts[i])).ToList();
        }

        /// <summary>Downsample keeping each bucket's extreme so spikes and troughs survive.</summary>
        public static List<SeriesPoint> Downsample(DateTime[] ts, double[] values, int maxPoints)
        {
            int n = Math.Min(ts.Length, values.Length);
            var output = new List<SeriesPoint>();
            if (n == 0) return output;
            if (n <= maxPoints)
            {
                for (int i = 0; i < n; i++) output.Add(new SeriesPoint(ts[i], values[i]));
                return output;
            }
            double bucket = (double)n / maxPoints;
            for (int b = 0; b < maxPoints; b++)
            {
                int start = (int)(b * bucket), end = Math.Min(n, (int)((b + 1) * bucket));
                if (end <= start) continue;
                int pick = start;
                double baseline = values[start];
                for (int i = start; i < end; i++)
                    if (Math.Abs(values[i] - baseline) > Math.Abs(values[pick] - baseline)) pick = i;
                output.Add(new SeriesPoint(ts[pick], values[pick]));
            }
            if (output[^1].T != ts[n - 1]) output.Add(new SeriesPoint(ts[n - 1], values[n - 1]));
            return output;
        }

        /// <summary>No NaN/Infinity reaches JSON.</summary>
        private static void Sanitize(BacktestAnalysis a)
        {
            foreach (var p in typeof(BacktestAnalysis).GetProperties())
            {
                if (p.PropertyType == typeof(double) && p.CanWrite)
                {
                    double v = (double)p.GetValue(a)!;
                    if (!double.IsFinite(v)) p.SetValue(a, 0.0);
                }
                else if (p.PropertyType == typeof(double?) && p.CanWrite)
                {
                    var v = (double?)p.GetValue(a);
                    if (v.HasValue && !double.IsFinite(v.Value)) p.SetValue(a, null);
                }
            }
        }
    }
}
