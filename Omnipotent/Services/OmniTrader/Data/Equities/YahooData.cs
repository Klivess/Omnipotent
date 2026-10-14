using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.MarketData;

namespace Omnipotent.Services.OmniTrader.Data.Equities
{
    public sealed class QuoteRow
    {
        public required string Symbol { get; init; }
        public string? Name { get; init; }
        public string? QuoteType { get; init; }
        public string? Exchange { get; init; }
        public string? Currency { get; init; }
        public string? MarketState { get; init; }
        public double? Price { get; init; }
        public double? Change { get; init; }
        public double? ChangePct { get; init; }
        public double? PreviousClose { get; init; }
        public double? Open { get; init; }
        public double? DayHigh { get; init; }
        public double? DayLow { get; init; }
        public double? Volume { get; init; }
        public double? AvgVolume3M { get; init; }
        public double? MarketCap { get; init; }
        public double? TrailingPE { get; init; }
        public double? ForwardPE { get; init; }
        public double? Eps { get; init; }
        public double? DividendYieldPct { get; init; }
        public double? High52w { get; init; }
        public double? Low52w { get; init; }
        public double? Change52wPct { get; init; }
        public double? Avg50d { get; init; }
        public double? Avg200d { get; init; }
        public double? PriceToBook { get; init; }
        public double? PreMarketPrice { get; init; }
        public double? PreMarketChangePct { get; init; }
        public double? PostMarketPrice { get; init; }
        public double? PostMarketChangePct { get; init; }
        public DateTime? AsOfUtc { get; init; }
        public string Source { get; init; } = "yahoo";
    }

    public sealed class CompanySnapshot
    {
        public required string Symbol { get; init; }
        public string? Name { get; set; }
        public string? QuoteType { get; set; }
        public string? Exchange { get; set; }
        public string? Currency { get; set; }
        public string? Sector { get; set; }
        public string? Industry { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string? Website { get; set; }
        public int? Employees { get; set; }
        public string? Summary { get; set; }
        public List<Officer> Officers { get; set; } = new();
        /// <summary>Flat key-statistics grid: label → value (already in display units; null when unknown).</summary>
        public Dictionary<string, double?> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public AnalystView? Analysts { get; set; }
        public List<EarningsPoint> Earnings { get; set; } = new();
        public DateTime? NextEarningsUtc { get; set; }
        public double? NextEpsEstimate { get; set; }
        public DateTime? ExDividendUtc { get; set; }
        public List<RatingChange> RatingChanges { get; set; } = new();
        public List<Holder> Institutions { get; set; } = new();
        public List<InsiderTrade> Insiders { get; set; } = new();
        public Dictionary<string, double?> Ownership { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Officer(string Name, string? Title, int? Age, double? Pay);
    public sealed record RatingChange(DateTime Date, string Firm, string? From, string? To, string? Action);
    public sealed record Holder(string Name, double? PctHeld, double? Shares, double? Value, DateTime? ReportDate);
    public sealed record InsiderTrade(string Name, string? Relation, string? Text, DateTime? Date, double? Shares, double? Value);
    public sealed record EarningsPoint(string Period, DateTime? Quarter, double? Actual, double? Estimate, double? SurprisePct);

    public sealed class AnalystView
    {
        public double? TargetMean { get; init; }
        public double? TargetMedian { get; init; }
        public double? TargetHigh { get; init; }
        public double? TargetLow { get; init; }
        public double? RecommendationMean { get; init; }
        public string? RecommendationKey { get; init; }
        public int? Analysts { get; init; }
        public List<RecommendationBucket> Trend { get; init; } = new();
    }

    public sealed record RecommendationBucket(string Period, int StrongBuy, int Buy, int Hold, int Sell, int StrongSell);

    public sealed class FinancialStatements
    {
        public required string Symbol { get; init; }
        public string? Currency { get; set; }
        /// <summary>metric → points, oldest first, for annual and quarterly reports.</summary>
        public Dictionary<string, List<FinancialPoint>> Annual { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<FinancialPoint>> Quarterly { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record FinancialPoint(DateTime Date, double Value);

    public sealed class OptionChain
    {
        public required string Symbol { get; init; }
        public double? UnderlyingPrice { get; set; }
        public List<DateTime> Expirations { get; set; } = new();
        public DateTime? Expiration { get; set; }
        public List<OptionContract> Calls { get; set; } = new();
        public List<OptionContract> Puts { get; set; } = new();
        public double? PutCallVolumeRatio { get; set; }
        public double? PutCallOiRatio { get; set; }
        public double? MaxPain { get; set; }
        public double? AtmIvPct { get; set; }
        public double RiskFreeRatePct { get; set; }
        public double DaysToExpiry { get; set; }
        public List<TermPoint> TermStructure { get; set; } = new();
    }

    public sealed record TermPoint(DateTime Expiration, double Days, double? AtmIvPct);

    public sealed class OptionContract
    {
        public required string Contract { get; init; }
        public double Strike { get; init; }
        public double? Last { get; init; }
        public double? Bid { get; init; }
        public double? Ask { get; init; }
        public double? Change { get; init; }
        public double? Volume { get; init; }
        public double? OpenInterest { get; init; }
        public double? IvPct { get; init; }
        public bool InTheMoney { get; init; }
        public double? Delta { get; set; }
        public double? Gamma { get; set; }
        public double? Theta { get; set; }
        public double? Vega { get; set; }
    }

    public sealed class MoverList
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public List<QuoteRow> Rows { get; init; } = new();
    }

    /// <summary>Keyless Yahoo Finance equity data beyond bars: quotes, company profile, statements, options, movers.</summary>
    public sealed class YahooData
    {
        private const string Q1 = "https://query1.finance.yahoo.com";
        private const string Q2 = "https://query2.finance.yahoo.com";
        private readonly YahooSession session;

        public YahooData(YahooSession session) { this.session = session; }

        public static readonly Dictionary<string, string> PredefinedScreens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["day_gainers"] = "Top gainers",
            ["day_losers"] = "Top losers",
            ["most_actives"] = "Most active",
            ["undervalued_growth_stocks"] = "Undervalued growth",
            ["growth_technology_stocks"] = "Growth technology",
            ["undervalued_large_caps"] = "Undervalued large caps",
            ["aggressive_small_caps"] = "Aggressive small caps",
            ["small_cap_gainers"] = "Small-cap gainers",
            ["most_shorted_stocks"] = "Most shorted",
            ["portfolio_anchors"] = "Portfolio anchors",
            ["solid_large_growth_funds"] = "Large growth funds",
            ["top_mutual_funds"] = "Top mutual funds",
        };

        private async Task<JObject> GetAsync(string url, bool crumb, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (status, body) = crumb ? await session.GetWithCrumbAsync(url, ct) : await session.GetAsync(url, ct);
                if ((int)status >= 400) throw new HttpRequestException($"Yahoo {(int)status}");
                SourceHealth.Success("yahoo-data", sw.Elapsed.TotalMilliseconds, "equities");
                return JObject.Parse(body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SourceHealth.Failure("yahoo-data", ex.Message, "equities");
                throw;
            }
        }

        // ── quotes ────────────────────────────────────────────────────────────────

        public async Task<List<QuoteRow>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
        {
            var list = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rows = new List<QuoteRow>();
            foreach (var chunk in list.Chunk(50))
            {
                string url = $"{Q1}/v7/finance/quote?symbols={Uri.EscapeDataString(string.Join(",", chunk))}";
                var json = await GetAsync(url, crumb: true, ct);
                foreach (var q in json["quoteResponse"]?["result"] as JArray ?? new JArray())
                    rows.Add(ParseQuote(q));
            }
            return rows;
        }

        internal static QuoteRow ParseQuote(JToken q) => new()
        {
            Symbol = q["symbol"]?.Value<string>() ?? "",
            Name = q["shortName"]?.Value<string>() ?? q["longName"]?.Value<string>(),
            QuoteType = q["quoteType"]?.Value<string>(),
            Exchange = q["fullExchangeName"]?.Value<string>() ?? q["exchange"]?.Value<string>(),
            Currency = q["currency"]?.Value<string>(),
            MarketState = q["marketState"]?.Value<string>(),
            Price = DataHttp.Num(q["regularMarketPrice"]),
            Change = DataHttp.Num(q["regularMarketChange"]),
            ChangePct = DataHttp.Num(q["regularMarketChangePercent"]),
            PreviousClose = DataHttp.Num(q["regularMarketPreviousClose"]),
            Open = DataHttp.Num(q["regularMarketOpen"]),
            DayHigh = DataHttp.Num(q["regularMarketDayHigh"]),
            DayLow = DataHttp.Num(q["regularMarketDayLow"]),
            Volume = DataHttp.Num(q["regularMarketVolume"]),
            AvgVolume3M = DataHttp.Num(q["averageDailyVolume3Month"]),
            MarketCap = DataHttp.Num(q["marketCap"]),
            TrailingPE = DataHttp.Num(q["trailingPE"]),
            ForwardPE = DataHttp.Num(q["forwardPE"]),
            Eps = DataHttp.Num(q["epsTrailingTwelveMonths"]),
            // Yahoo reports dividendYield in percent and trailingAnnualDividendYield as a fraction.
            DividendYieldPct = DataHttp.Num(q["dividendYield"]) ?? (DataHttp.Num(q["trailingAnnualDividendYield"]) is { } f ? f * 100 : null),
            High52w = DataHttp.Num(q["fiftyTwoWeekHigh"]),
            Low52w = DataHttp.Num(q["fiftyTwoWeekLow"]),
            Change52wPct = DataHttp.Num(q["fiftyTwoWeekChangePercent"]),
            Avg50d = DataHttp.Num(q["fiftyDayAverage"]),
            Avg200d = DataHttp.Num(q["twoHundredDayAverage"]),
            PriceToBook = DataHttp.Num(q["priceToBook"]),
            PreMarketPrice = DataHttp.Num(q["preMarketPrice"]),
            PreMarketChangePct = DataHttp.Num(q["preMarketChangePercent"]),
            PostMarketPrice = DataHttp.Num(q["postMarketPrice"]),
            PostMarketChangePct = DataHttp.Num(q["postMarketChangePercent"]),
            AsOfUtc = DataHttp.Unix(q["regularMarketTime"]),
        };

        // ── company profile (DES) ─────────────────────────────────────────────────

        public async Task<CompanySnapshot?> GetCompanyAsync(string symbol, CancellationToken ct = default)
        {
            const string modules = "assetProfile,summaryDetail,defaultKeyStatistics,financialData,price,calendarEvents,"
                + "recommendationTrend,upgradeDowngradeHistory,earningsHistory,institutionOwnership,insiderTransactions,majorHoldersBreakdown";
            string url = $"{Q1}/v10/finance/quoteSummary/{Uri.EscapeDataString(symbol)}?modules={modules}";
            var json = await GetAsync(url, crumb: true, ct);
            var r = json["quoteSummary"]?["result"]?.FirstOrDefault();
            if (r == null) return null;

            var profile = r["assetProfile"];
            var detail = r["summaryDetail"];
            var stats = r["defaultKeyStatistics"];
            var fin = r["financialData"];
            var price = r["price"];
            var snap = new CompanySnapshot
            {
                Symbol = symbol.ToUpperInvariant(),
                Name = price?["longName"]?.Value<string>() ?? price?["shortName"]?.Value<string>(),
                QuoteType = price?["quoteType"]?.Value<string>(),
                Exchange = price?["exchangeName"]?.Value<string>(),
                Currency = price?["currency"]?.Value<string>() ?? detail?["currency"]?.Value<string>(),
                Sector = profile?["sector"]?.Value<string>(),
                Industry = profile?["industry"]?.Value<string>(),
                Country = profile?["country"]?.Value<string>(),
                City = profile?["city"]?.Value<string>(),
                Website = profile?["website"]?.Value<string>(),
                Employees = profile?["fullTimeEmployees"]?.Type is JTokenType.Integer ? profile["fullTimeEmployees"]!.Value<int>() : null,
                Summary = profile?["longBusinessSummary"]?.Value<string>(),
            };
            foreach (var o in profile?["companyOfficers"] as JArray ?? new JArray())
                snap.Officers.Add(new Officer(o["name"]?.Value<string>() ?? "", o["title"]?.Value<string>(),
                    o["age"]?.Type == JTokenType.Integer ? o["age"]!.Value<int>() : null, DataHttp.Num(o["totalPay"])));

            void S(string label, JToken? token, double scale = 1)
            {
                var v = DataHttp.Num(token);
                snap.Stats[label] = v.HasValue ? v * scale : null;
            }
            S("Market cap", price?["marketCap"] ?? detail?["marketCap"]);
            S("Enterprise value", stats?["enterpriseValue"]);
            S("P/E (trailing)", detail?["trailingPE"]);
            S("P/E (forward)", detail?["forwardPE"] ?? stats?["forwardPE"]);
            S("PEG", stats?["pegRatio"]);
            S("P/S", detail?["priceToSalesTrailing12Months"]);
            S("P/B", stats?["priceToBook"]);
            S("EV/Revenue", stats?["enterpriseToRevenue"]);
            S("EV/EBITDA", stats?["enterpriseToEbitda"]);
            S("EPS (ttm)", stats?["trailingEps"]);
            S("EPS (forward)", stats?["forwardEps"]);
            S("Beta", detail?["beta"] ?? stats?["beta"]);
            S("Dividend yield %", detail?["dividendYield"], 100);
            S("Payout ratio %", detail?["payoutRatio"], 100);
            S("Revenue", fin?["totalRevenue"]);
            S("Revenue growth %", fin?["revenueGrowth"], 100);
            S("Earnings growth %", fin?["earningsGrowth"], 100);
            S("Gross margin %", fin?["grossMargins"], 100);
            S("Operating margin %", fin?["operatingMargins"], 100);
            S("Profit margin %", fin?["profitMargins"], 100);
            S("EBITDA", fin?["ebitda"]);
            S("Free cash flow", fin?["freeCashflow"]);
            S("Operating cash flow", fin?["operatingCashflow"]);
            S("Total cash", fin?["totalCash"]);
            S("Total debt", fin?["totalDebt"]);
            S("Debt/Equity", fin?["debtToEquity"]);
            S("Current ratio", fin?["currentRatio"]);
            S("Quick ratio", fin?["quickRatio"]);
            S("ROA %", fin?["returnOnAssets"], 100);
            S("ROE %", fin?["returnOnEquity"], 100);
            S("Shares outstanding", stats?["sharesOutstanding"]);
            S("Float", stats?["floatShares"]);
            S("Short % of float", stats?["shortPercentOfFloat"], 100);
            S("Short ratio (days)", stats?["shortRatio"]);
            S("Insiders %", stats?["heldPercentInsiders"], 100);
            S("Institutions %", stats?["heldPercentInstitutions"], 100);
            S("52w change %", stats?["52WeekChange"], 100);
            S("52w high", detail?["fiftyTwoWeekHigh"]);
            S("52w low", detail?["fiftyTwoWeekLow"]);
            S("50d average", detail?["fiftyDayAverage"]);
            S("200d average", detail?["twoHundredDayAverage"]);
            S("Avg volume", detail?["averageVolume"]);

            snap.Analysts = new AnalystView
            {
                TargetMean = DataHttp.Num(fin?["targetMeanPrice"]),
                TargetMedian = DataHttp.Num(fin?["targetMedianPrice"]),
                TargetHigh = DataHttp.Num(fin?["targetHighPrice"]),
                TargetLow = DataHttp.Num(fin?["targetLowPrice"]),
                RecommendationMean = DataHttp.Num(fin?["recommendationMean"]),
                RecommendationKey = fin?["recommendationKey"]?.Value<string>(),
                Analysts = (int?)DataHttp.Num(fin?["numberOfAnalystOpinions"]),
                Trend = (r["recommendationTrend"]?["trend"] as JArray ?? new JArray()).Select(t => new RecommendationBucket(
                    t["period"]?.Value<string>() ?? "", t["strongBuy"]?.Value<int>() ?? 0, t["buy"]?.Value<int>() ?? 0,
                    t["hold"]?.Value<int>() ?? 0, t["sell"]?.Value<int>() ?? 0, t["strongSell"]?.Value<int>() ?? 0)).ToList()
            };

            var cal = r["calendarEvents"];
            snap.NextEarningsUtc = (cal?["earnings"]?["earningsDate"] as JArray)?.Select(DataHttp.Unix).FirstOrDefault(d => d.HasValue);
            snap.NextEpsEstimate = DataHttp.Num(cal?["earnings"]?["earningsAverage"]);
            snap.ExDividendUtc = DataHttp.Unix(cal?["exDividendDate"] ?? detail?["exDividendDate"]);

            foreach (var e in r["earningsHistory"]?["history"] as JArray ?? new JArray())
                snap.Earnings.Add(new EarningsPoint(e["period"]?.Value<string>() ?? "", DataHttp.Unix(e["quarter"]),
                    DataHttp.Num(e["epsActual"]), DataHttp.Num(e["epsEstimate"]), DataHttp.Num(e["surprisePercent"]) is { } sp ? sp * 100 : null));

            foreach (var h in (r["upgradeDowngradeHistory"]?["history"] as JArray ?? new JArray()).Take(25))
                if (DataHttp.Unix(h["epochGradeDate"]) is { } when)
                    snap.RatingChanges.Add(new RatingChange(when, h["firm"]?.Value<string>() ?? "", h["fromGrade"]?.Value<string>(),
                        h["toGrade"]?.Value<string>(), h["action"]?.Value<string>()));

            foreach (var h in (r["institutionOwnership"]?["ownershipList"] as JArray ?? new JArray()).Take(15))
                snap.Institutions.Add(new Holder(h["organization"]?.Value<string>() ?? "", DataHttp.Num(h["pctHeld"]) is { } p ? p * 100 : null,
                    DataHttp.Num(h["position"]), DataHttp.Num(h["value"]), DataHttp.Unix(h["reportDate"])));

            foreach (var t in (r["insiderTransactions"]?["transactions"] as JArray ?? new JArray()).Take(25))
                snap.Insiders.Add(new InsiderTrade(t["filerName"]?.Value<string>() ?? "", t["filerRelation"]?.Value<string>(),
                    t["transactionText"]?.Value<string>(), DataHttp.Unix(t["startDate"]), DataHttp.Num(t["shares"]), DataHttp.Num(t["value"])));

            var holders = r["majorHoldersBreakdown"];
            snap.Ownership["Insiders %"] = DataHttp.Num(holders?["insidersPercentHeld"]) is { } i ? i * 100 : null;
            snap.Ownership["Institutions %"] = DataHttp.Num(holders?["institutionsPercentHeld"]) is { } inst ? inst * 100 : null;
            snap.Ownership["Institutions (float) %"] = DataHttp.Num(holders?["institutionsFloatPercentHeld"]) is { } fl ? fl * 100 : null;
            snap.Ownership["Institutions"] = DataHttp.Num(holders?["institutionsCount"]);
            return snap;
        }

        // ── statements (FA) ───────────────────────────────────────────────────────

        public static readonly string[] StatementMetrics =
        {
            "TotalRevenue", "CostOfRevenue", "GrossProfit", "OperatingExpense", "OperatingIncome", "EBITDA", "NetIncome",
            "DilutedEPS", "BasicEPS", "DilutedAverageShares", "ResearchAndDevelopment", "InterestExpense", "TaxProvision",
            "TotalAssets", "TotalLiabilitiesNetMinorityInterest", "StockholdersEquity", "CashAndCashEquivalents",
            "CurrentAssets", "CurrentLiabilities", "TotalDebt", "NetDebt", "WorkingCapital", "RetainedEarnings",
            "OperatingCashFlow", "CapitalExpenditure", "FreeCashFlow", "RepurchaseOfCapitalStock", "CashDividendsPaid"
        };

        public async Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default)
        {
            var types = StatementMetrics.SelectMany(m => new[] { "annual" + m, "quarterly" + m });
            long p2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long p1 = DateTimeOffset.UtcNow.AddYears(-12).ToUnixTimeSeconds();
            string url = $"{Q1}/ws/fundamentals-timeseries/v1/finance/timeseries/{Uri.EscapeDataString(symbol)}"
                + $"?type={string.Join(",", types)}&period1={p1}&period2={p2}";
            var json = await GetAsync(url, crumb: true, ct);
            var result = new FinancialStatements { Symbol = symbol.ToUpperInvariant() };
            foreach (var series in json["timeseries"]?["result"] as JArray ?? new JArray())
            {
                string? type = (series["meta"]?["type"] as JArray)?.FirstOrDefault()?.Value<string>();
                if (type == null || series[type] is not JArray points) continue;
                bool annual = type.StartsWith("annual", StringComparison.Ordinal);
                string metric = annual ? type["annual".Length..] : type["quarterly".Length..];
                var list = new List<FinancialPoint>();
                foreach (var p in points)
                {
                    if (p == null || p.Type == JTokenType.Null) continue;
                    var date = DataHttp.Date(p["asOfDate"]);
                    var value = DataHttp.Num(p["reportedValue"]);
                    result.Currency ??= p["currencyCode"]?.Value<string>();
                    if (date.HasValue && value.HasValue) list.Add(new FinancialPoint(date.Value, value.Value));
                }
                if (list.Count == 0) continue;
                (annual ? result.Annual : result.Quarterly)[metric] = list.OrderBy(x => x.Date).ToList();
            }
            return result;
        }

        // ── options (OMON) ────────────────────────────────────────────────────────

        public async Task<OptionChain?> GetOptionsAsync(string symbol, DateTime? expiration = null, double riskFreePct = 4.0,
            CancellationToken ct = default)
        {
            string url = $"{Q2}/v7/finance/options/{Uri.EscapeDataString(symbol)}";
            if (expiration.HasValue) url += $"?date={new DateTimeOffset(DateTime.SpecifyKind(expiration.Value.Date, DateTimeKind.Utc)).ToUnixTimeSeconds()}";
            var json = await GetAsync(url, crumb: true, ct);
            var r = json["optionChain"]?["result"]?.FirstOrDefault();
            if (r == null) return null;

            var chain = new OptionChain
            {
                Symbol = r["underlyingSymbol"]?.Value<string>() ?? symbol.ToUpperInvariant(),
                UnderlyingPrice = DataHttp.Num(r["quote"]?["regularMarketPrice"]),
                Expirations = (r["expirationDates"] as JArray ?? new JArray()).Select(DataHttp.Unix).Where(d => d.HasValue).Select(d => d!.Value).ToList(),
                RiskFreeRatePct = riskFreePct
            };
            var block = r["options"]?.FirstOrDefault();
            chain.Expiration = DataHttp.Unix(block?["expirationDate"]);
            double spot = chain.UnderlyingPrice ?? 0;
            chain.DaysToExpiry = chain.Expiration.HasValue ? Math.Max(0.5, (chain.Expiration.Value.AddHours(20) - DateTime.UtcNow).TotalDays) : 0;
            double t = chain.DaysToExpiry / 365.0;

            List<OptionContract> Parse(JToken? arr, bool call) => (arr as JArray ?? new JArray()).Select(o =>
            {
                double strike = DataHttp.Num(o["strike"]) ?? 0;
                double? iv = DataHttp.Num(o["impliedVolatility"]);
                var c = new OptionContract
                {
                    Contract = o["contractSymbol"]?.Value<string>() ?? "",
                    Strike = strike,
                    Last = DataHttp.Num(o["lastPrice"]),
                    Bid = DataHttp.Num(o["bid"]),
                    Ask = DataHttp.Num(o["ask"]),
                    Change = DataHttp.Num(o["change"]),
                    Volume = DataHttp.Num(o["volume"]),
                    OpenInterest = DataHttp.Num(o["openInterest"]),
                    IvPct = iv.HasValue ? iv * 100 : null,
                    InTheMoney = o["inTheMoney"]?.Value<bool>() ?? false,
                };
                if (spot > 0 && strike > 0 && iv is > 0.001 and < 10 && t > 0)
                {
                    var g = BlackScholes.Greeks(call, spot, strike, t, riskFreePct / 100, iv.Value);
                    c.Delta = g.Delta; c.Gamma = g.Gamma; c.Theta = g.Theta; c.Vega = g.Vega;
                }
                return c;
            }).OrderBy(c => c.Strike).ToList();

            chain.Calls = Parse(block?["calls"], call: true);
            chain.Puts = Parse(block?["puts"], call: false);

            double callVol = chain.Calls.Sum(c => c.Volume ?? 0), putVol = chain.Puts.Sum(c => c.Volume ?? 0);
            double callOi = chain.Calls.Sum(c => c.OpenInterest ?? 0), putOi = chain.Puts.Sum(c => c.OpenInterest ?? 0);
            chain.PutCallVolumeRatio = callVol > 0 ? putVol / callVol : null;
            chain.PutCallOiRatio = callOi > 0 ? putOi / callOi : null;
            chain.MaxPain = MaxPain(chain.Calls, chain.Puts);
            chain.AtmIvPct = AtmIv(chain.Calls, chain.Puts, spot);
            return chain;
        }

        /// <summary>Strike that minimises the total intrinsic value paid to option holders at expiry.</summary>
        internal static double? MaxPain(List<OptionContract> calls, List<OptionContract> puts)
        {
            var strikes = calls.Select(c => c.Strike).Concat(puts.Select(p => p.Strike)).Distinct().OrderBy(s => s).ToList();
            if (strikes.Count == 0) return null;
            double best = strikes[0], bestPain = double.MaxValue;
            foreach (var s in strikes)
            {
                double pain = calls.Sum(c => Math.Max(0, s - c.Strike) * (c.OpenInterest ?? 0))
                            + puts.Sum(p => Math.Max(0, p.Strike - s) * (p.OpenInterest ?? 0));
                if (pain < bestPain) { bestPain = pain; best = s; }
            }
            return best;
        }

        internal static double? AtmIv(List<OptionContract> calls, List<OptionContract> puts, double spot)
        {
            if (spot <= 0) return null;
            var ivs = calls.Concat(puts).Where(c => c.IvPct is > 0.1)
                .OrderBy(c => Math.Abs(c.Strike - spot)).Take(4).Select(c => c.IvPct!.Value).ToList();
            return ivs.Count == 0 ? null : ivs.Average();
        }

        /// <summary>ATM implied vol for each of the first expiries — the term structure.</summary>
        public async Task<List<TermPoint>> GetTermStructureAsync(string symbol, int maxExpiries = 8, CancellationToken ct = default)
        {
            var first = await GetOptionsAsync(symbol, null, ct: ct);
            if (first == null) return new List<TermPoint>();
            var points = new List<TermPoint>();
            foreach (var exp in first.Expirations.Take(maxExpiries))
            {
                try
                {
                    var chain = exp == first.Expiration ? first : await GetOptionsAsync(symbol, exp, ct: ct);
                    if (chain?.Expiration == null) continue;
                    points.Add(new TermPoint(chain.Expiration.Value, Math.Round(chain.DaysToExpiry, 1), chain.AtmIvPct));
                }
                catch { }
            }
            return points;
        }

        // ── movers & discovery ────────────────────────────────────────────────────

        public async Task<MoverList> GetPredefinedScreenAsync(string id, int count = 25, CancellationToken ct = default)
        {
            string url = $"{Q1}/v1/finance/screener/predefined/saved?scrIds={Uri.EscapeDataString(id)}&count={Math.Clamp(count, 1, 250)}";
            var json = await GetAsync(url, crumb: true, ct);
            var result = json["finance"]?["result"]?.FirstOrDefault();
            return new MoverList
            {
                Id = id,
                Title = result?["title"]?.Value<string>() ?? (PredefinedScreens.TryGetValue(id, out var t) ? t : id),
                Rows = (result?["quotes"] as JArray ?? new JArray()).Select(ParseQuote).ToList()
            };
        }

        public async Task<List<string>> GetTrendingAsync(string region = "US", int count = 20, CancellationToken ct = default)
        {
            string url = $"{Q1}/v1/finance/trending/{Uri.EscapeDataString(region)}?count={count}";
            var json = await GetAsync(url, crumb: true, ct);
            return (json["finance"]?["result"]?.FirstOrDefault()?["quotes"] as JArray ?? new JArray())
                .Select(q => q["symbol"]?.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        }
    }

    /// <summary>Black-Scholes-Merton greeks (no dividend). Theta per calendar day; vega per 1 vol point.</summary>
    public static class BlackScholes
    {
        public static (double Delta, double Gamma, double Theta, double Vega, double Price) Greeks(bool call, double s, double k, double t, double r, double sigma)
        {
            double sqrtT = Math.Sqrt(t);
            double d1 = (Math.Log(s / k) + (r + sigma * sigma / 2) * t) / (sigma * sqrtT);
            double d2 = d1 - sigma * sqrtT;
            double nd1 = Pdf(d1);
            double price = call
                ? s * Cdf(d1) - k * Math.Exp(-r * t) * Cdf(d2)
                : k * Math.Exp(-r * t) * Cdf(-d2) - s * Cdf(-d1);
            double delta = call ? Cdf(d1) : Cdf(d1) - 1;
            double gamma = nd1 / (s * sigma * sqrtT);
            double theta = call
                ? -s * nd1 * sigma / (2 * sqrtT) - r * k * Math.Exp(-r * t) * Cdf(d2)
                : -s * nd1 * sigma / (2 * sqrtT) + r * k * Math.Exp(-r * t) * Cdf(-d2);
            double vega = s * nd1 * sqrtT / 100;
            return (Math.Round(delta, 4), Math.Round(gamma, 6), Math.Round(theta / 365, 4), Math.Round(vega, 4), price);
        }

        public static double Pdf(double x) => Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI);

        /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26, |ε| &lt; 7.5e-8).</summary>
        public static double Cdf(double x)
        {
            double t = 1 / (1 + 0.2316419 * Math.Abs(x));
            double poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
            double p = 1 - Pdf(x) * poly;
            return x >= 0 ? p : 1 - p;
        }
    }
}
