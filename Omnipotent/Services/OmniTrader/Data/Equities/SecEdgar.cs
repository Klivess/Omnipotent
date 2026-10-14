using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniTrader.Data.Equities
{
    public sealed class SecFiling
    {
        public required string Form { get; init; }
        public DateTime FiledUtc { get; init; }
        public DateTime? ReportDate { get; init; }
        public required string Accession { get; init; }
        public string? Description { get; init; }
        public required string Url { get; init; }
    }

    public sealed class SecFacts
    {
        public required string Ticker { get; init; }
        public required string Cik { get; init; }
        public string? EntityName { get; init; }
        /// <summary>concept label → annual (10-K, FY) values, oldest first.</summary>
        public Dictionary<string, List<FinancialPoint>> Annual { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>concept label → quarterly (10-Q) values, oldest first.</summary>
        public Dictionary<string, List<FinancialPoint>> Quarterly { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SEC EDGAR — US filings and XBRL financial facts, free with a declared User-Agent. The facts
    /// are as-filed, so they answer "what did the company report" without a vendor's restatement
    /// conventions in between.
    /// </summary>
    public sealed class SecEdgar
    {
        private readonly SemaphoreSlim tickerLock = new(1, 1);
        private Dictionary<string, (string Cik, string Name)>? tickers;
        private DateTime tickersUtc;

        /// <summary>Display label → candidate us-gaap concepts in preference order.</summary>
        public static readonly (string Label, string[] Concepts, string Unit)[] Concepts =
        {
            ("Revenue", new[] { "RevenueFromContractWithCustomerExcludingAssessedTax", "Revenues", "SalesRevenueNet" }, "USD"),
            ("Gross profit", new[] { "GrossProfit" }, "USD"),
            ("Operating income", new[] { "OperatingIncomeLoss" }, "USD"),
            ("Net income", new[] { "NetIncomeLoss" }, "USD"),
            ("EPS (diluted)", new[] { "EarningsPerShareDiluted" }, "USD/shares"),
            ("Total assets", new[] { "Assets" }, "USD"),
            ("Total liabilities", new[] { "Liabilities" }, "USD"),
            ("Equity", new[] { "StockholdersEquity" }, "USD"),
            ("Cash", new[] { "CashAndCashEquivalentsAtCarryingValue" }, "USD"),
            ("Long-term debt", new[] { "LongTermDebtNoncurrent", "LongTermDebt" }, "USD"),
            ("Operating cash flow", new[] { "NetCashProvidedByUsedInOperatingActivities" }, "USD"),
            ("Capex", new[] { "PaymentsToAcquirePropertyPlantAndEquipment" }, "USD"),
            ("R&D", new[] { "ResearchAndDevelopmentExpense" }, "USD"),
            ("Shares outstanding", new[] { "CommonStockSharesOutstanding" }, "shares"),
            ("Dividends per share", new[] { "CommonStockDividendsPerShareDeclared" }, "USD/shares"),
        };

        public async Task<(string Cik, string Name)?> ResolveAsync(string ticker, CancellationToken ct = default)
        {
            await EnsureTickersAsync(ct);
            string key = ticker.ToUpperInvariant().Replace(".", "-");
            return tickers!.TryGetValue(key, out var v) ? v : null;
        }

        public async Task<int> TickerCountAsync(CancellationToken ct = default)
        {
            await EnsureTickersAsync(ct);
            return tickers!.Count;
        }

        private async Task EnsureTickersAsync(CancellationToken ct)
        {
            if (tickers != null && DateTime.UtcNow - tickersUtc < TimeSpan.FromHours(24)) return;
            await tickerLock.WaitAsync(ct);
            try
            {
                if (tickers != null && DateTime.UtcNow - tickersUtc < TimeSpan.FromHours(24)) return;
                var json = await DataHttp.GetJsonAsync("sec", "equities", "https://www.sec.gov/files/company_tickers.json", DataHttp.Research, ct);
                var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ((JObject)json).Properties())
                {
                    string? t = p.Value["ticker"]?.Value<string>();
                    long cik = p.Value["cik_str"]?.Value<long>() ?? 0;
                    if (string.IsNullOrEmpty(t) || cik == 0) continue;
                    map[t] = (cik.ToString("D10"), p.Value["title"]?.Value<string>() ?? t);
                }
                tickers = map;
                tickersUtc = DateTime.UtcNow;
            }
            finally { tickerLock.Release(); }
        }

        public async Task<SecFacts?> GetFactsAsync(string ticker, CancellationToken ct = default)
        {
            var resolved = await ResolveAsync(ticker, ct);
            if (resolved == null) return null;
            var json = await DataHttp.GetJsonAsync("sec", "equities",
                $"https://data.sec.gov/api/xbrl/companyfacts/CIK{resolved.Value.Cik}.json", DataHttp.Research, ct);
            var facts = new SecFacts { Ticker = ticker.ToUpperInvariant(), Cik = resolved.Value.Cik, EntityName = json["entityName"]?.Value<string>() };
            var gaap = json["facts"]?["us-gaap"] as JObject;
            var dei = json["facts"]?["dei"] as JObject;
            foreach (var (label, concepts, unit) in Concepts)
            {
                JToken? units = null;
                foreach (var concept in concepts)
                {
                    units = gaap?[concept]?["units"]?[unit] ?? dei?[concept]?["units"]?[unit];
                    if (units != null) break;
                }
                if (units is not JArray rows) continue;

                var annual = new Dictionary<DateTime, double>();
                var quarterly = new Dictionary<DateTime, double>();
                foreach (var row in rows)
                {
                    string? form = row["form"]?.Value<string>();
                    string? fp = row["fp"]?.Value<string>();
                    var end = DataHttp.Date(row["end"]);
                    var val = DataHttp.Num(row["val"]);
                    if (end == null || val == null) continue;
                    var start = DataHttp.Date(row["start"]);
                    double days = start.HasValue ? (end.Value - start.Value).TotalDays : 0;
                    // Duration facts: a fiscal year is ~1 year, a quarter ~3 months. Instant facts (no start) are balances.
                    if (form is "10-K" or "20-F" && fp == "FY" && (!start.HasValue || days > 300))
                        annual[end.Value.Date] = val.Value;
                    else if (form is "10-Q" && (!start.HasValue || days is > 70 and < 110))
                        quarterly[end.Value.Date] = val.Value;
                }
                if (annual.Count > 0) facts.Annual[label] = annual.OrderBy(k => k.Key).Select(k => new FinancialPoint(k.Key, k.Value)).ToList();
                if (quarterly.Count > 0) facts.Quarterly[label] = quarterly.OrderBy(k => k.Key).Select(k => new FinancialPoint(k.Key, k.Value)).ToList();
            }
            return facts;
        }

        public async Task<List<SecFiling>> GetFilingsAsync(string ticker, int limit = 40, string? form = null, CancellationToken ct = default)
        {
            var resolved = await ResolveAsync(ticker, ct);
            if (resolved == null) return new List<SecFiling>();
            var json = await DataHttp.GetJsonAsync("sec", "equities",
                $"https://data.sec.gov/submissions/CIK{resolved.Value.Cik}.json", DataHttp.Research, ct);
            var recent = json["filings"]?["recent"];
            if (recent == null) return new List<SecFiling>();
            var forms = recent["form"] as JArray ?? new JArray();
            var dates = recent["filingDate"] as JArray ?? new JArray();
            var reports = recent["reportDate"] as JArray ?? new JArray();
            var accessions = recent["accessionNumber"] as JArray ?? new JArray();
            var docs = recent["primaryDocument"] as JArray ?? new JArray();
            var descs = recent["primaryDocDescription"] as JArray ?? new JArray();
            string cikTrim = resolved.Value.Cik.TrimStart('0');
            var list = new List<SecFiling>();
            for (int i = 0; i < forms.Count && list.Count < limit; i++)
            {
                string f = forms[i]?.Value<string>() ?? "";
                if (form != null && !string.Equals(f, form, StringComparison.OrdinalIgnoreCase)) continue;
                string acc = accessions.ElementAtOrDefault(i)?.Value<string>() ?? "";
                string doc = docs.ElementAtOrDefault(i)?.Value<string>() ?? "";
                list.Add(new SecFiling
                {
                    Form = f,
                    FiledUtc = DataHttp.Date(dates.ElementAtOrDefault(i)) ?? DateTime.MinValue,
                    ReportDate = DataHttp.Date(reports.ElementAtOrDefault(i)),
                    Accession = acc,
                    Description = descs.ElementAtOrDefault(i)?.Value<string>(),
                    Url = $"https://www.sec.gov/Archives/edgar/data/{cikTrim}/{acc.Replace("-", "")}/{doc}"
                });
            }
            return list;
        }
    }
}
