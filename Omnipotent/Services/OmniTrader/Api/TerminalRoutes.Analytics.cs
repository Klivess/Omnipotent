using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class TerminalRoutes
    {
        private async Task RegisterAnalyticsAsync()
        {
            // Everything about one symbol's own history: the screener's fields, trailing returns,
            // realised-volatility cone and the deepest drawdowns.
            await Get("/terminal/stats", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } symbol) { await Bad(req, "symbol required"); return; }
                Live();
                bool equity = MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown);
                var candles = await parent.MarketData.GetHistoricalCandlesAsync(symbol, TimeInterval.OneDay, equity ? 2600 : 3000);
                if (candles.Count < 5) { await NotFound(req, "No daily history for " + symbol); return; }
                var quote = (await Hub.GetQuotesAsync(new[] { symbol })).FirstOrDefault();
                await Json(req, StatsPayload(symbol, candles, quote));
            });

            await Get("/terminal/correlation", OmniTraderPerms.TerminalRead, async req =>
            {
                var symbols = Csv(req, "symbols");
                if (symbols.Count < 2) { await Bad(req, "two or more symbols required"); return; }
                Live();
                var interval = CandleClock.Parse(req.userParameters.Get("interval")) ?? TimeInterval.OneDay;
                int bars = ParseInt(req.userParameters.Get("bars"), 180, 20, 5000);
                await Json(req, await CorrelationAsync(symbols.Take(30).ToList(), interval, bars));
            });

            await Get("/terminal/seasonality", OmniTraderPerms.TerminalRead, async req =>
            {
                if (Symbol(req) is not { } symbol) { await Bad(req, "symbol required"); return; }
                Live();
                int years = ParseInt(req.userParameters.Get("years"), 10, 1, 40);
                bool equity = MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown);
                var daily = await parent.MarketData.GetHistoricalCandlesAsync(symbol, TimeInterval.OneDay, Math.Min(20_000, years * (equity ? 253 : 366) + 40));
                if (daily.Count < 30) { await NotFound(req, "Not enough daily history"); return; }
                IReadOnlyList<OHLCCandle>? hourly = null;
                if (!equity && ParseBool(req.userParameters.Get("intraday"), true))
                {
                    try { hourly = await parent.MarketData.GetHistoricalCandlesAsync(symbol, TimeInterval.OneHour, 24 * 365); }
                    catch { hourly = null; }
                }
                await Json(req, Seasonality(symbol, daily, hourly));
            });
        }

        // ── single-symbol statistics ─────────────────────────────────────────────

        internal static object StatsPayload(string symbol, IReadOnlyList<OHLCCandle> candles, Data.Equities.QuoteRow? quote)
        {
            bool equity = MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown);
            var bars = BarSeries.From(candles, CandleClock.BarsPerYear(TimeInterval.OneDay, !equity));
            return new
            {
                Symbol = symbol,
                AssetClass = equity ? "Equity" : "Crypto",
                Bars = candles.Count,
                From = candles[0].Timestamp,
                To = candles[^1].Timestamp,
                Quote = quote,
                Fields = ScreenerEngine.ComputeFields(bars, TimeInterval.OneDay, !equity, quote),
                Returns = TrailingReturns(bars, quote?.Price),
                VolCone = VolCone(bars),
                Drawdowns = Drawdowns(bars, 8),
                Calendar = YearlyReturns(bars)
            };
        }

        internal static object TrailingReturns(BarSeries bars, double? livePrice)
        {
            var t = bars.Time;
            var c = bars.Close;
            double last = livePrice is > 0 ? livePrice.Value : c[^1];
            DateTime end = t[^1];
            double? Since(DateTime from)
            {
                int idx = Array.FindLastIndex(t, x => x <= from);
                return idx >= 0 && c[idx] > 0 ? (last / c[idx] - 1) * 100 : null;
            }
            double? Annualised(DateTime from, double years)
            {
                int idx = Array.FindLastIndex(t, x => x <= from);
                return idx >= 0 && c[idx] > 0 ? (Math.Pow(last / c[idx], 1 / years) - 1) * 100 : null;
            }
            var prevYearEnd = new DateTime(end.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1);
            return new Dictionary<string, double?>
            {
                ["1D"] = c.Length > 1 && c[^2] > 0 ? (last / c[^2] - 1) * 100 : null,
                ["1W"] = Since(end.AddDays(-7)),
                ["1M"] = Since(end.AddMonths(-1)),
                ["3M"] = Since(end.AddMonths(-3)),
                ["6M"] = Since(end.AddMonths(-6)),
                ["YTD"] = Since(prevYearEnd),
                ["1Y"] = Since(end.AddYears(-1)),
                ["3Y"] = Since(end.AddYears(-3)),
                ["5Y"] = Since(end.AddYears(-5)),
                ["3Y ann."] = Annualised(end.AddYears(-3), 3),
                ["5Y ann."] = Annualised(end.AddYears(-5), 5),
                ["Max"] = c[0] > 0 ? (last / c[0] - 1) * 100 : null,
            };
        }

        /// <summary>Realised volatility at several windows: where today sits in each window's history.</summary>
        internal static List<object> VolCone(BarSeries bars)
        {
            var c = bars.Close;
            var r = new double[c.Length];
            r[0] = double.NaN;
            for (int i = 1; i < c.Length; i++) r[i] = c[i - 1] > 0 && c[i] > 0 ? Math.Log(c[i] / c[i - 1]) : double.NaN;
            double ann = Math.Sqrt(bars.BarsPerYear) * 100;
            var output = new List<object>();
            foreach (int w in new[] { 10, 20, 30, 60, 90, 120, 250 })
            {
                if (c.Length < w + 10) continue;
                var vols = new List<double>();
                double current = double.NaN;
                for (int i = w; i < c.Length; i++)
                {
                    double sum = 0, sq = 0;
                    int n = 0;
                    for (int k = i - w + 1; k <= i; k++)
                    {
                        if (!double.IsFinite(r[k])) continue;
                        sum += r[k]; sq += r[k] * r[k]; n++;
                    }
                    if (n < w * 0.8) continue;
                    double mean = sum / n;
                    double v = Math.Sqrt(Math.Max(0, sq / n - mean * mean) * n / Math.Max(1, n - 1)) * ann;
                    vols.Add(v);
                    if (i == c.Length - 1) current = v;
                }
                if (vols.Count < 5) continue;
                vols.Sort();
                double P(double q) => vols[(int)Math.Round(q * (vols.Count - 1))];
                output.Add(new
                {
                    Window = w,
                    Current = double.IsFinite(current) ? Math.Round(current, 2) : (double?)null,
                    Min = Math.Round(vols[0], 2), P10 = Math.Round(P(0.1), 2), P25 = Math.Round(P(0.25), 2), Median = Math.Round(P(0.5), 2),
                    P75 = Math.Round(P(0.75), 2), P90 = Math.Round(P(0.9), 2), Max = Math.Round(vols[^1], 2),
                    Percentile = double.IsFinite(current) ? Math.Round(vols.Count(v => v <= current) * 100.0 / vols.Count, 1) : (double?)null
                });
            }
            return output;
        }

        internal static List<object> Drawdowns(BarSeries bars, int top)
        {
            var c = bars.Close;
            var t = bars.Time;
            var episodes = new List<(int Peak, int Trough, int? Recovery, double Depth)>();
            int peak = 0, trough = 0;
            double depth = 0;
            bool inDd = false;
            for (int i = 1; i < c.Length; i++)
            {
                if (c[i] >= c[peak])
                {
                    if (inDd) episodes.Add((peak, trough, i, depth));
                    inDd = false;
                    peak = i; trough = i; depth = 0;
                    continue;
                }
                double dd = c[peak] > 0 ? (c[i] / c[peak] - 1) * 100 : 0;
                if (dd < depth) { depth = dd; trough = i; }
                inDd = true;
            }
            if (inDd) episodes.Add((peak, trough, null, depth));
            return episodes.OrderBy(e => e.Depth).Take(top).Select(e => (object)new
            {
                Peak = t[e.Peak], Trough = t[e.Trough], Recovery = e.Recovery.HasValue ? t[e.Recovery.Value] : (DateTime?)null,
                DepthPct = Math.Round(e.Depth, 2),
                DaysToTrough = Math.Round((t[e.Trough] - t[e.Peak]).TotalDays),
                DaysToRecover = e.Recovery.HasValue ? Math.Round((t[e.Recovery.Value] - t[e.Trough]).TotalDays) : (double?)null,
                Ongoing = !e.Recovery.HasValue
            }).ToList();
        }

        internal static List<object> YearlyReturns(BarSeries bars)
        {
            var output = new List<object>();
            var t = bars.Time;
            var c = bars.Close;
            foreach (var g in Enumerable.Range(0, t.Length).GroupBy(i => t[i].Year))
            {
                int first = g.First(), last = g.Last();
                double basis = first > 0 ? c[first - 1] : bars.Open[first];
                if (basis <= 0) continue;
                double hi = g.Max(i => bars.High[i]), lo = g.Min(i => bars.Low[i]);
                output.Add(new { Year = g.Key, ReturnPct = Math.Round((c[last] / basis - 1) * 100, 2), High = hi, Low = lo, Partial = first == 0 || last == t.Length - 1 });
            }
            return output;
        }

        // ── cross-asset ──────────────────────────────────────────────────────────

        private async Task<object> CorrelationAsync(List<string> symbols, TimeInterval interval, int bars)
        {
            using var gate = new SemaphoreSlim(6);
            var fetched = await Task.WhenAll(symbols.Select(async s =>
            {
                await gate.WaitAsync();
                try { return (Symbol: s, Bars: await parent.MarketData.GetHistoricalCandlesAsync(s, interval, bars + 1)); }
                catch { return (Symbol: s, Bars: (IReadOnlyList<OHLCCandle>)Array.Empty<OHLCCandle>()); }
                finally { gate.Release(); }
            }));
            return Correlation(fetched, interval);
        }

        internal static object Correlation(IReadOnlyList<(string Symbol, IReadOnlyList<OHLCCandle> Bars)> fetched, TimeInterval interval)
        {
            var usable = fetched.Where(f => f.Bars.Count > 10).ToList();
            var missing = fetched.Where(f => f.Bars.Count <= 10).Select(f => f.Symbol).ToList();
            if (usable.Count < 2) return new { Symbols = usable.Select(u => u.Symbol), Missing = missing, Error = "Not enough overlapping history" };

            // Common bar times: mixing a 24/7 crypto series with an exchange-hours one keeps the
            // exchange's sessions only, so every return covers the same interval for every symbol.
            var common = usable.Select(u => u.Bars.Select(b => b.Timestamp).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; })
                .OrderBy(x => x).ToList();
            var returns = new List<double[]>();
            foreach (var u in usable)
            {
                var map = u.Bars.ToDictionary(b => b.Timestamp, b => (double)b.Close);
                var r = new double[Math.Max(0, common.Count - 1)];
                for (int i = 1; i < common.Count; i++)
                {
                    double a = map[common[i - 1]], b = map[common[i]];
                    r[i - 1] = a > 0 && b > 0 ? Math.Log(b / a) : 0;
                }
                returns.Add(r);
            }
            int n = returns[0].Length;
            double ann = Math.Sqrt(CandleClock.BarsPerYear(interval, usable.All(u => !MarketDataRouter.UsesEquityFeed(u.Symbol, AssetClass.Unknown))));
            var matrix = new double?[usable.Count][];
            for (int i = 0; i < usable.Count; i++)
            {
                matrix[i] = new double?[usable.Count];
                for (int j = 0; j < usable.Count; j++)
                    matrix[i][j] = n < 5 ? null : Math.Round(Pearson(returns[i], returns[j]), 4);
            }
            var stats = usable.Select((u, i) =>
            {
                var r = returns[i];
                double mean = r.Length == 0 ? 0 : r.Average();
                double sd = r.Length < 2 ? 0 : Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Length - 1));
                double beta = Beta(r, returns[0]);
                return new
                {
                    u.Symbol,
                    ReturnPct = Math.Round((Math.Exp(r.Sum()) - 1) * 100, 2),
                    VolPct = Math.Round(sd * ann * 100, 2),
                    BetaToFirst = i == 0 ? 1 : Math.Round(beta, 3),
                };
            }).ToList();
            return new
            {
                Symbols = usable.Select(u => u.Symbol),
                Missing = missing,
                Observations = n,
                From = common.FirstOrDefault(),
                To = common.LastOrDefault(),
                Interval = interval.ToString(),
                Matrix = matrix,
                Stats = stats
            };
        }

        internal static double Pearson(double[] a, double[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n < 2) return double.NaN;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            double cov = 0, va = 0, vb = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                cov += da * db; va += da * da; vb += db * db;
            }
            return va <= 0 || vb <= 0 ? 0 : cov / Math.Sqrt(va * vb);
        }

        internal static double Beta(double[] r, double[] market)
        {
            int n = Math.Min(r.Length, market.Length);
            if (n < 2) return double.NaN;
            double mr = r.Take(n).Average(), mm = market.Take(n).Average();
            double cov = 0, vm = 0;
            for (int i = 0; i < n; i++) { cov += (r[i] - mr) * (market[i] - mm); vm += (market[i] - mm) * (market[i] - mm); }
            return vm <= 0 ? 0 : cov / vm;
        }

        // ── seasonality ──────────────────────────────────────────────────────────

        internal static object Seasonality(string symbol, IReadOnlyList<OHLCCandle> daily, IReadOnlyList<OHLCCandle>? hourly)
        {
            // Month-end closes → monthly returns by year.
            var monthEnds = daily.GroupBy(b => (b.Timestamp.Year, b.Timestamp.Month)).OrderBy(g => g.Key)
                .Select(g => (g.Key.Year, g.Key.Month, Close: (double)g.Last().Close)).ToList();
            var monthly = new List<(int Year, int Month, double Ret)>();
            for (int i = 1; i < monthEnds.Count; i++)
                if (monthEnds[i - 1].Close > 0)
                    monthly.Add((monthEnds[i].Year, monthEnds[i].Month, (monthEnds[i].Close / monthEnds[i - 1].Close - 1) * 100));

            var years = monthly.Select(m => m.Year).Distinct().OrderByDescending(y => y).ToList();
            var grid = years.Select(y => new
            {
                Year = y,
                Months = Enumerable.Range(1, 12).Select(m => monthly.Where(x => x.Year == y && x.Month == m).Select(x => (double?)Math.Round(x.Ret, 2)).FirstOrDefault()).ToList(),
                Total = Math.Round((monthly.Where(x => x.Year == y).Aggregate(1.0, (acc, x) => acc * (1 + x.Ret / 100)) - 1) * 100, 2)
            }).ToList();

            object Summarise(IEnumerable<double> values)
            {
                var v = values.ToList();
                if (v.Count == 0) return new { Count = 0 };
                var sorted = v.OrderBy(x => x).ToList();
                double mean = v.Average();
                double sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : 0;
                return new
                {
                    Count = v.Count,
                    Mean = Math.Round(mean, 3),
                    Median = Math.Round(sorted[sorted.Count / 2], 3),
                    PositivePct = Math.Round(v.Count(x => x > 0) * 100.0 / v.Count, 1),
                    StdDev = Math.Round(sd, 3),
                    TStat = sd > 0 ? Math.Round(mean / (sd / Math.Sqrt(v.Count)), 2) : 0,
                    Best = Math.Round(sorted[^1], 2),
                    Worst = Math.Round(sorted[0], 2)
                };
            }

            var byMonth = Enumerable.Range(1, 12).Select(m => new { Month = m, Stats = Summarise(monthly.Where(x => x.Month == m).Select(x => x.Ret)) }).ToList();

            var dailyRets = new List<(DateTime T, double Ret)>();
            for (int i = 1; i < daily.Count; i++)
                if (daily[i - 1].Close > 0) dailyRets.Add((daily[i].Timestamp, ((double)daily[i].Close / (double)daily[i - 1].Close - 1) * 100));
            var byWeekday = Enum.GetValues<DayOfWeek>().Where(d => dailyRets.Any(r => r.T.DayOfWeek == d))
                .Select(d => new { Day = d.ToString(), Stats = Summarise(dailyRets.Where(r => r.T.DayOfWeek == d).Select(r => r.Ret)) }).ToList();

            // Turn of the month: the last day and first three trading days versus the rest.
            var tom = new List<double>();
            var rest = new List<double>();
            foreach (var g in dailyRets.GroupBy(r => (r.T.Year, r.T.Month)))
            {
                var days = g.OrderBy(x => x.T).ToList();
                for (int i = 0; i < days.Count; i++)
                    (i < 3 || i == days.Count - 1 ? tom : rest).Add(days[i].Ret);
            }

            object? byHour = null;
            if (hourly is { Count: > 48 })
            {
                var hr = new List<(int Hour, double Ret)>();
                for (int i = 1; i < hourly.Count; i++)
                    if (hourly[i - 1].Close > 0) hr.Add((hourly[i].Timestamp.Hour, ((double)hourly[i].Close / (double)hourly[i - 1].Close - 1) * 100));
                byHour = Enumerable.Range(0, 24).Select(h => new { HourUtc = h, Stats = Summarise(hr.Where(x => x.Hour == h).Select(x => x.Ret)) }).ToList();
            }

            return new
            {
                Symbol = symbol,
                From = daily[0].Timestamp,
                To = daily[^1].Timestamp,
                Grid = grid,
                ByMonth = byMonth,
                ByWeekday = byWeekday,
                TurnOfMonth = new { Window = Summarise(tom), Rest = Summarise(rest) },
                ByHour = byHour
            };
        }
    }
}
