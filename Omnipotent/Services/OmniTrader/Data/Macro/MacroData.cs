using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Data.Crypto;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Data.Macro
{
    public sealed class MacroSeriesInfo
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Group { get; init; }
        public required string Source { get; init; }
        public required string Code { get; init; }
        public string Unit { get; init; } = "";
        /// <summary>level | yoy — yoy series are transformed from an index level to a 12-month % change.</summary>
        public string Transform { get; init; } = "level";
        public string Frequency { get; init; } = "M";
    }

    public sealed class MacroSeries
    {
        public required MacroSeriesInfo Info { get; init; }
        public List<TimePoint> Points { get; init; } = new();
        public double? Latest => Points.Count > 0 ? Points[^1].V : null;
        public double? Previous => Points.Count > 1 ? Points[^2].V : null;
        public DateTime? LatestDate => Points.Count > 0 ? Points[^1].T : null;
        public string? Error { get; set; }
    }

    public sealed record CurvePoint(string Tenor, double Years, double Yield);

    public sealed class YieldCurve
    {
        public DateTime Date { get; init; }
        /// <summary>tenor label → yield %, in maturity order.</summary>
        public List<CurvePoint> Points { get; init; } = new();
        public double? Spread2s10s => Get("10 Yr") - Get("2 Yr");
        public double? Spread3m10y => Get("10 Yr") - Get("3 Mo");
        public double? Get(string tenor) => Points.FirstOrDefault(p => p.Tenor == tenor)?.Yield;
    }

    public sealed class VolTerm
    {
        public DateTime Date { get; init; }
        public double? Vix9d { get; init; }
        public double? Vix { get; init; }
        public double? Vix3m { get; init; }
        public double? Vix6m { get; init; }
        public double? Vvix { get; init; }
        public double? Skew { get; init; }
        /// <summary>VIX / VIX3M — above 1 is backwardation (stress).</summary>
        public double? Ratio => Vix.HasValue && Vix3m is > 0 ? Vix / Vix3m : null;
    }

    /// <summary>
    /// Free macro data: the US Treasury par yield curve, BLS (keyless v1 API, ~25 calls/day so results
    /// are cached for hours), DBnomics (a keyless mirror of the Fed, ECB, ONS, BEA and Eurostat), the
    /// CBOE volatility family, and FRED when a free key is configured.
    /// </summary>
    public sealed class MacroData
    {
        public Func<Task<string?>>? FredKey { get; set; }

        public static readonly MacroSeriesInfo[] Catalog =
        {
            new() { Id = "us-cpi-yoy", Name = "US CPI YoY", Group = "Inflation", Source = "bls", Code = "CUUR0000SA0", Unit = "%", Transform = "yoy" },
            new() { Id = "us-core-cpi-yoy", Name = "US Core CPI YoY", Group = "Inflation", Source = "bls", Code = "CUUR0000SA0L1E", Unit = "%", Transform = "yoy" },
            new() { Id = "us-ppi-yoy", Name = "US PPI Final Demand YoY", Group = "Inflation", Source = "bls", Code = "WPUFD4", Unit = "%", Transform = "yoy" },
            new() { Id = "us-unemployment", Name = "US Unemployment Rate", Group = "Labour", Source = "bls", Code = "LNS14000000", Unit = "%" },
            new() { Id = "us-payrolls", Name = "US Nonfarm Payrolls (k)", Group = "Labour", Source = "bls", Code = "CES0000000001", Unit = "k" },
            new() { Id = "us-earnings-yoy", Name = "US Avg Hourly Earnings YoY", Group = "Labour", Source = "bls", Code = "CES0500000003", Unit = "%", Transform = "yoy" },
            new() { Id = "us-fed-funds", Name = "Fed Funds Effective", Group = "Rates", Source = "dbnomics", Code = "FED/H15/RIFSPFF_N.B", Unit = "%", Frequency = "D" },
            new() { Id = "us-2y", Name = "US 2Y Treasury", Group = "Rates", Source = "dbnomics", Code = "FED/H15/RIFLGFCY02_N.B", Unit = "%", Frequency = "D" },
            new() { Id = "us-10y", Name = "US 10Y Treasury", Group = "Rates", Source = "dbnomics", Code = "FED/H15/RIFLGFCY10_N.B", Unit = "%", Frequency = "D" },
            new() { Id = "ecb-deposit", Name = "ECB Deposit Facility", Group = "Rates", Source = "dbnomics", Code = "ECB/FM/D.U2.EUR.4F.KR.DFR.LEV", Unit = "%", Frequency = "D" },
            new() { Id = "ea-hicp-yoy", Name = "Euro Area HICP YoY", Group = "Inflation", Source = "dbnomics", Code = "Eurostat/prc_hicp_manr/M.RCH_A.CP00.EA", Unit = "%" },
            new() { Id = "uk-cpi-yoy", Name = "UK CPI YoY", Group = "Inflation", Source = "dbnomics", Code = "ONS/MM23/D7G7.M", Unit = "%" },
            new() { Id = "us-gdp-qoq", Name = "US Real GDP (q/q saar)", Group = "Growth", Source = "dbnomics", Code = "BEA/NIPA-T10101/A191RL-Q", Unit = "%", Frequency = "Q" },
        };

        private static readonly (string Tenor, double Years)[] Tenors =
        {
            ("1 Mo", 1 / 12.0), ("2 Mo", 2 / 12.0), ("3 Mo", 0.25), ("4 Mo", 4 / 12.0), ("6 Mo", 0.5), ("1 Yr", 1), ("2 Yr", 2),
            ("3 Yr", 3), ("5 Yr", 5), ("7 Yr", 7), ("10 Yr", 10), ("20 Yr", 20), ("30 Yr", 30)
        };

        // ── Treasury yield curve ──────────────────────────────────────────────────

        /// <summary>Daily par yield curves for a calendar year, newest first.</summary>
        public async Task<List<YieldCurve>> GetYieldCurvesAsync(int year, CancellationToken ct = default)
        {
            string url = $"https://home.treasury.gov/resource-center/data-chart-center/interest-rates/daily-treasury-rates.csv/{year}/all"
                + $"?type=daily_treasury_yield_curve&field_tdr_date_value={year}&page&_format=csv";
            // treasury.gov's edge holds non-browser user agents for ~17s per request; the browser identity is answered at once.
            string csv = await DataHttp.GetStringAsync("treasury", "macro", url, DataHttp.Browser, ct);
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) return new List<YieldCurve>();
            var header = SplitCsv(lines[0]);
            var curves = new List<YieldCurve>();
            foreach (var line in lines.Skip(1))
            {
                var cells = SplitCsv(line);
                if (cells.Count < 2 || !DateTime.TryParseExact(cells[0], "MM/dd/yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) continue;
                var points = new List<CurvePoint>();
                foreach (var (tenor, years) in Tenors)
                {
                    int idx = header.FindIndex(h => string.Equals(h, tenor, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0 || idx >= cells.Count) continue;
                    if (double.TryParse(cells[idx], NumberStyles.Any, CultureInfo.InvariantCulture, out var y)) points.Add(new CurvePoint(tenor, years, y));
                }
                curves.Add(new YieldCurve { Date = date.Date, Points = points });
            }
            return curves.OrderByDescending(c => c.Date).ToList();
        }

        private static List<string> SplitCsv(string line) => line.Split(',').Select(c => c.Trim().Trim('"')).ToList();

        // ── BLS ───────────────────────────────────────────────────────────────────

        /// <summary>Several BLS series in one call (the keyless API allows ~25 calls a day).</summary>
        public async Task<Dictionary<string, List<TimePoint>>> GetBlsAsync(IEnumerable<string> seriesIds, int years = 10, CancellationToken ct = default)
        {
            var ids = seriesIds.Distinct().ToList();
            int end = DateTime.UtcNow.Year;
            var payload = new JObject
            {
                ["seriesid"] = new JArray(ids),
                ["startyear"] = (end - Math.Clamp(years, 1, 10) + 1).ToString(),
                ["endyear"] = end.ToString()
            };
            string body = await DataHttp.PostJsonAsync("bls", "macro", "https://api.bls.gov/publicAPI/v1/timeseries/data/", payload.ToString(), DataHttp.Research, ct);
            var json = JObject.Parse(body);
            if (json["status"]?.Value<string>() != "REQUEST_SUCCEEDED")
                throw new HttpRequestException("BLS: " + string.Join("; ", (json["message"] as JArray ?? new JArray()).Select(m => m.ToString())));
            var result = new Dictionary<string, List<TimePoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in json["Results"]?["series"] as JArray ?? new JArray())
            {
                string id = series["seriesID"]?.Value<string>() ?? "";
                var points = new List<TimePoint>();
                foreach (var d in series["data"] as JArray ?? new JArray())
                {
                    string period = d["period"]?.Value<string>() ?? "";
                    if (!period.StartsWith('M') || period == "M13") continue;
                    if (!int.TryParse(d["year"]?.Value<string>(), out var y) || !int.TryParse(period[1..], out var m)) continue;
                    if (!double.TryParse(d["value"]?.Value<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;
                    points.Add(new TimePoint(new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc), v));
                }
                result[id] = points.OrderBy(p => p.T).ToList();
            }
            return result;
        }

        // ── DBnomics ──────────────────────────────────────────────────────────────

        public async Task<List<TimePoint>> GetDbnomicsAsync(string code, CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("dbnomics", "macro", $"https://api.db.nomics.world/v22/series/{code}?observations=1&format=json", DataHttp.Research, ct);
            var doc = json["series"]?["docs"]?.FirstOrDefault();
            if (doc == null) return new List<TimePoint>();
            var periods = doc["period_start_day"] as JArray ?? doc["period"] as JArray ?? new JArray();
            var values = doc["value"] as JArray ?? new JArray();
            var points = new List<TimePoint>();
            for (int i = 0; i < Math.Min(periods.Count, values.Count); i++)
            {
                if (values[i].Type is not (JTokenType.Float or JTokenType.Integer)) continue;
                if (!DateTime.TryParse(periods[i].ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
                points.Add(new TimePoint(t, values[i].Value<double>()));
            }
            return points.OrderBy(p => p.T).ToList();
        }

        // ── FRED (optional free key) ──────────────────────────────────────────────

        public async Task<List<TimePoint>?> GetFredAsync(string seriesId, CancellationToken ct = default)
        {
            string? key = FredKey == null ? null : await FredKey();
            if (string.IsNullOrWhiteSpace(key)) return null;
            var json = await DataHttp.GetJsonAsync("fred", "macro",
                $"https://api.stlouisfed.org/fred/series/observations?series_id={Uri.EscapeDataString(seriesId)}&api_key={Uri.EscapeDataString(key)}&file_type=json", DataHttp.Research, ct);
            return (json["observations"] as JArray ?? new JArray())
                .Select(o => (Date: DataHttp.Date(o["date"]), Value: DataHttp.Num(o["value"])))
                .Where(o => o.Date.HasValue && o.Value.HasValue)
                .Select(o => new TimePoint(o.Date!.Value, o.Value!.Value)).ToList();
        }

        // ── catalog series ────────────────────────────────────────────────────────

        public async Task<List<MacroSeries>> GetCatalogSeriesAsync(IEnumerable<MacroSeriesInfo> infos, CancellationToken ct = default)
        {
            var list = infos.ToList();
            var output = list.ToDictionary(i => i.Id, i => new MacroSeries { Info = i });

            var bls = list.Where(i => i.Source == "bls").ToList();
            if (bls.Count > 0)
            {
                try
                {
                    var data = await GetBlsAsync(bls.Select(b => b.Code), 10, ct);
                    foreach (var info in bls)
                    {
                        if (!data.TryGetValue(info.Code, out var points)) { output[info.Id].Error = "not returned"; continue; }
                        output[info.Id] = new MacroSeries { Info = info, Points = info.Transform == "yoy" ? YoY(points) : points };
                    }
                }
                catch (Exception ex) { foreach (var info in bls) output[info.Id].Error = ex.Message; }
            }

            await Task.WhenAll(list.Where(i => i.Source == "dbnomics").Select(async info =>
            {
                try
                {
                    var points = await GetDbnomicsAsync(info.Code, ct);
                    if (info.Frequency == "D") points = points.Where(p => p.T >= DateTime.UtcNow.AddYears(-12)).ToList();
                    lock (output) output[info.Id] = new MacroSeries { Info = info, Points = info.Transform == "yoy" ? YoY(points) : points };
                }
                catch (Exception ex) { lock (output) output[info.Id].Error = ex.Message; }
            }));
            return list.Select(i => output[i.Id]).ToList();
        }

        /// <summary>Monthly index level → 12-month % change.</summary>
        public static List<TimePoint> YoY(List<TimePoint> monthly)
        {
            var byMonth = monthly.ToDictionary(p => (p.T.Year, p.T.Month), p => p.V);
            var result = new List<TimePoint>();
            foreach (var p in monthly)
                if (byMonth.TryGetValue((p.T.Year - 1, p.T.Month), out var prior) && prior != 0)
                    result.Add(new TimePoint(p.T, Math.Round((p.V / prior - 1) * 100, 3)));
            return result;
        }

        // ── CBOE volatility ───────────────────────────────────────────────────────

        public static readonly string[] VolIndices = { "VIX", "VIX9D", "VIX3M", "VIX6M", "VVIX", "SKEW" };

        public async Task<List<TimePoint>> GetCboeAsync(string index, int days = 730, CancellationToken ct = default)
        {
            string csv = await DataHttp.GetStringAsync("cboe", "macro",
                $"https://cdn.cboe.com/api/global/us_indices/daily_prices/{index.ToUpperInvariant()}_History.csv", ct: ct);
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) return new List<TimePoint>();
            var header = SplitCsv(lines[0]);
            int closeIdx = header.FindIndex(h => h.Equals("CLOSE", StringComparison.OrdinalIgnoreCase));
            if (closeIdx < 0) closeIdx = header.Count - 1;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var points = new List<TimePoint>();
            foreach (var line in lines.Skip(1))
            {
                var cells = SplitCsv(line);
                if (cells.Count <= closeIdx) continue;
                if (!DateTime.TryParseExact(cells[0], "MM/dd/yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) || d < cutoff) continue;
                if (double.TryParse(cells[closeIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) points.Add(new TimePoint(d.Date, v));
            }
            return points.OrderBy(p => p.T).ToList();
        }

        public async Task<(VolTerm Latest, Dictionary<string, List<TimePoint>> History)> GetVolTermAsync(int days = 365, CancellationToken ct = default)
        {
            var history = new Dictionary<string, List<TimePoint>>(StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(VolIndices.Select(async idx =>
            {
                try { var s = await GetCboeAsync(idx, days, ct); lock (history) history[idx] = s; }
                catch { lock (history) history[idx] = new List<TimePoint>(); }
            }));
            double? Last(string k) => history.TryGetValue(k, out var s) && s.Count > 0 ? s[^1].V : null;
            var date = history.Values.Where(s => s.Count > 0).Select(s => s[^1].T).DefaultIfEmpty(DateTime.UtcNow.Date).Max();
            return (new VolTerm
            {
                Date = date, Vix9d = Last("VIX9D"), Vix = Last("VIX"), Vix3m = Last("VIX3M"), Vix6m = Last("VIX6M"),
                Vvix = Last("VVIX"), Skew = Last("SKEW")
            }, history);
        }
    }
}
