using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Data.Crypto;
using Omnipotent.Services.OmniTrader.Data.Equities;
using Omnipotent.Services.OmniTrader.Data.Macro;
using Omnipotent.Services.OmniTrader.Data.News;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Screening;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;

namespace Omnipotent.Tests.OmniTrader
{
    public sealed class OmniTraderFixtureFactAttribute : FactAttribute
    {
        public OmniTraderFixtureFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OMNITRADER_FIXTURES")))
                Skip = "Fixture export; set OMNITRADER_FIXTURES=<website>/tests/e2e/fixtures/omnitrader.";
        }
    }

    /// <summary>
    /// Writes the JSON the website's OmniTrader e2e specs replay. Every payload comes from the engine's
    /// own code paths and mappers on seeded synthetic bars (offline), serialised with the routes' settings.
    /// </summary>
    public class OmniTraderFixtureExport : IDisposable
    {
        private static readonly string[] Crypto = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT" };
        private static readonly Dictionary<string, double> StartPrice = new()
        {
            ["BTCUSDT"] = 28_000, ["ETHUSDT"] = 1_800, ["SOLUSDT"] = 22, ["BNBUSDT"] = 240, ["XRPUSDT"] = 0.5, ["ADAUSDT"] = 0.3,
        };

        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly StrategyRegistry registry = new();
        private readonly MarketDataRouter router;
        private readonly BacktestJobRepository jobs;
        private readonly BacktestJobQueue queue;
        private readonly StudioService studio;
        private readonly StudyRunner runner;
        private readonly CancellationTokenSource cts = new();
        private readonly Dictionary<string, List<OHLCCandle>> daily = new();
        private readonly string outDir;

        public OmniTraderFixtureExport()
        {
            outDir = Environment.GetEnvironmentVariable("OMNITRADER_FIXTURES") ?? Path.Combine(Path.GetTempPath(), "ot-fixtures");
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-fix-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            var cache = new CandleCacheRepository(db);
            router = new MarketDataRouter(cache);
            jobs = new BacktestJobRepository(db);
            queue = new BacktestJobQueue(jobs, router, registry, new UniverseRepository(db), _ => { }, (_, _) => { });
            studio = new StudioService(db, registry, queue, router, _ => { });
            runner = new StudyRunner(db, queue, jobs, (_, _) => Task.FromResult(new List<string>()), (_, _) => { });
            queue.Start();
            runner.Start(cts.Token);
            int seed = 1;
            foreach (var s in Crypto)
            {
                var bars = Synthetic(s, seed++, 1100, TimeInterval.OneDay);
                daily[s] = bars;
                cache.UpsertManyAsync(s, TimeInterval.OneDay, bars).GetAwaiter().GetResult();
            }
            cache.UpsertManyAsync("BTCUSDT", TimeInterval.OneHour, Synthetic("BTCUSDT", 99, 900, TimeInterval.OneHour)).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            cts.Cancel();
            db.Dispose();
            try { File.Delete(dbPath); } catch { }
        }

        private static List<OHLCCandle> Synthetic(string symbol, int seed, int n, TimeInterval interval)
        {
            var rng = new Random(seed * 7919);
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            var step = TimeSpan.FromSeconds(CandleClock.Seconds(interval));
            double price = StartPrice.GetValueOrDefault(symbol, 100);
            double vol = interval == TimeInterval.OneDay ? 0.032 : 0.007;
            var bars = new List<OHLCCandle>(n);
            for (int i = 0; i < n; i++)
            {
                double regime = Math.Sin(i / 90.0 + seed) * 0.0035 + 0.0006;
                double shock = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * vol * 1.4;
                double open = price;
                double close = Math.Max(open * 0.5, open * (1 + regime + shock));
                double high = Math.Max(open, close) * (1 + rng.NextDouble() * vol * 0.6);
                double low = Math.Min(open, close) * (1 - rng.NextDouble() * vol * 0.6);
                double volume = 1000 * (1 + rng.NextDouble() * 2) * (1 + Math.Abs(shock) * 20);
                var t = last - step * (n - 1 - i);
                bars.Add(new OHLCCandle(t, (decimal)open, (decimal)high, (decimal)low, (decimal)close, (decimal)volume));
                price = close;
            }
            return bars;
        }

        private void Write(string name, object? payload)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonConvert.SerializeObject(payload, Formatting.None, TerminalRoutes.JsonSettings));
        }

        private static QuoteRow Quote(string symbol, string? name, double price, double chgPct, int seed) => new()
        {
            Symbol = symbol, Name = name, Price = price, Change = price - price / (1 + chgPct / 100), ChangePct = chgPct,
            PreviousClose = price / (1 + chgPct / 100), Open = price / (1 + chgPct / 200), DayHigh = price * (1 + Math.Abs(chgPct) / 150 + 0.002),
            DayLow = price * (1 - Math.Abs(chgPct) / 150 - 0.002), Volume = 1_000_000 + seed * 13_331, AvgVolume3M = 1_200_000,
            Change52wPct = (seed % 7 - 3) * 6.5, MarketState = "REGULAR", Source = "fixture", AsOfUtc = DateTime.UtcNow,
        };

        [OmniTraderFixtureFact]
        public async Task Export()
        {
            var rng = new Random(42);
            Write("meta", new { GeneratedUtc = DateTime.UtcNow });

            // ── catalogs ──
            Write("indicators", IndicatorCatalog.All.Select(IndicatorEngine.Describe));
            Write("formula-reference", FormulaCompiler.Reference());
            Write("cost-presets", CostPresets.All);
            Write("universes", new { Catalog = UniverseCatalog.Known, Watchlists = new[] { new { Id = "watchlist:wl1", Name = "Core", Group = "Watchlists", Size = 6 } } });
            Write("screener-fields", new { Fields = ScreenerEngine.Fields.Select(f => new { f.Key, f.Label, f.Format }), DefaultColumns = ScreenerEngine.DefaultColumns() });
            Write("screener-presets", ScreenerEngine.Presets());
            Write("studio-templates", studio.Templates.Select(t => new { t.Id, t.Name, t.Description, Kind = t.Kind.ToString(), t.Category, t.Symbol, Interval = t.Interval.ToString(), t.Blueprint, t.Source }));
            Write("studio-reference", TerminalRoutes.StudioReferencePayload());

            // ── chart ──
            var btc = daily["BTCUSDT"].TakeLast(600).ToList();
            var requests = new List<IndicatorRequest>
            {
                new() { Id = "ema", Key = "s_ema20", Inputs = { ["length"] = 20 } },
                new() { Id = "ema", Key = "s_ema50", Inputs = { ["length"] = 50 } },
                new() { Id = "rsi", Key = "s_rsi", Inputs = { ["length"] = 14 } },
                new() { Id = "macd", Key = "x_macd" }, new() { Id = "bb", Key = "x_bb" }, new() { Id = "supertrend", Key = "x_supertrend" },
                new() { Id = "atr", Key = "x_atr" }, new() { Id = "stoch", Key = "x_stoch" }, new() { Id = "adx", Key = "x_adx" }, new() { Id = "obv", Key = "x_obv" },
            };
            var formulas = new List<ChartFormula>
            {
                new() { Key = "f_cross", Formula = "crossover(ema(close, 20), ema(close, 50))" },
                new() { Key = "f_zscore", Formula = "zscore(close, 50)" },
            };
            var compare = new Dictionary<string, (IReadOnlyList<OHLCCandle>? Bars, string? Error)> { ["ETHUSDT"] = (daily["ETHUSDT"], null) };
            Write("chart-BTCUSDT", TerminalRoutes.ChartPayload("BTCUSDT", TimeInterval.OneDay, btc, false, "Binance", requests, formulas, null, compare));

            // ── per-symbol analytics ──
            Write("stats-BTCUSDT", TerminalRoutes.StatsPayload("BTCUSDT", daily["BTCUSDT"], null));
            var hourly = await router.GetHistoricalCandlesAsync("BTCUSDT", TimeInterval.OneHour, 800);
            Write("seasonality-BTCUSDT", TerminalRoutes.Seasonality("BTCUSDT", daily["BTCUSDT"], hourly));
            Write("correlation", TerminalRoutes.Correlation(Crypto.Select(s => (s, (IReadOnlyList<OHLCCandle>)daily[s].TakeLast(181).ToList())).ToList(), TimeInterval.OneDay));

            // ── screener ──
            var hub = new DataHub(router);
            var screener = new ScreenerEngine(router, hub);
            var def = new ScreenDefinition
            {
                Name = "Fixture screen", Universe = "", Symbols = Crypto.ToList(), Interval = TimeInterval.OneDay, IncludeForming = false,
                Filters = new() { "close > 0" }, Columns = ScreenerEngine.DefaultColumns(), SortBy = "chg1m"
            };
            Write("screener-result", await screener.RunAsync(def));

            // ── quotes / monitor ──
            QuoteRow[] Rows(IEnumerable<UniverseMember> members, double basePrice) => members.Select((m, i) =>
                Quote(m.Symbol, m.Name, basePrice * (1 + i * 0.37), Math.Round((rng.NextDouble() - 0.45) * 3, 2), i)).ToArray();
            var sections = new List<MonitorSection>
            {
                new() { Id = "indices", Title = "Indices", Rows = Rows(UniverseCatalog.WorldIndices, 5200).ToList() },
                new() { Id = "fx", Title = "FX", Rows = Rows(UniverseCatalog.Fx, 1.08).ToList() },
                new() { Id = "commodities", Title = "Commodities", Rows = Rows(UniverseCatalog.Commodities, 80).ToList() },
                new() { Id = "rates", Title = "Rates", Rows = Rows(UniverseCatalog.Rates, 4.2).ToList() },
                new() { Id = "vol", Title = "Volatility", Rows = Rows(UniverseCatalog.Volatility, 15).ToList() },
                new() { Id = "crypto", Title = "Crypto", Rows = UniverseCatalog.CryptoMajors.Select((m, i) => Quote(m.Symbol, m.Name, (double)(daily.GetValueOrDefault(m.Symbol)?[^1].Close ?? 10m), Math.Round((rng.NextDouble() - 0.4) * 6, 2), i)).ToList() },
                new() { Id = "sectors", Title = "Sectors", Rows = Rows(UniverseCatalog.SectorEtfs, 90).ToList() },
            };
            var today = DateTime.UtcNow.Date;
            YieldCurve Curve(DateTime date, double shift) => new()
            {
                Date = date,
                Points = new()
                {
                    new("1 Mo", 1 / 12.0, 5.30 + shift), new("3 Mo", 0.25, 5.22 + shift), new("6 Mo", 0.5, 5.05 + shift), new("1 Yr", 1, 4.80 + shift),
                    new("2 Yr", 2, 4.45 + shift), new("5 Yr", 5, 4.10 + shift), new("10 Yr", 10, 4.20 + shift), new("30 Yr", 30, 4.42 + shift),
                }
            };
            var curves = Enumerable.Range(0, 400).Select(d => Curve(today.AddDays(-d), Math.Sin(d / 40.0) * 0.25 - d * 0.0015)).ToList();
            var fng = Enumerable.Range(0, 365).Select(d => new FearGreedPoint(today.AddDays(d - 364), 30 + (int)(Math.Sin(d / 20.0) * 25 + 20), d % 2 == 0 ? "Greed" : "Neutral")).ToList();
            var funding = Crypto.Select((s, i) => new FundingSnapshot
            {
                Symbol = s, MarkPrice = (double)daily[s][^1].Close, IndexPrice = (double)daily[s][^1].Close * 0.9996, FundingRate = 0.0001 * (i - 1.5),
                NextFundingUtc = DateTime.UtcNow.AddHours(3), QuoteVolume24h = 1e9 / (i + 1), PriceChangePct24h = (i - 2) * 1.3,
            }).ToList();
            var volTerm = new VolTerm { Date = today, Vix9d = 13.1, Vix = 14.2, Vix3m = 16.4, Vix6m = 17.9, Vvix = 88, Skew = 142 };
            var global = new CryptoGlobal { TotalMarketCapUsd = 2.41e12, TotalVolumeUsd = 8.9e10, MarketCapChange24hPct = 1.84, BtcDominancePct = 54.2, EthDominancePct = 16.9, ActiveCoins = 13_400 };
            Write("monitor", new MarketMonitor
            {
                AsOfUtc = DateTime.UtcNow, Sections = sections, CryptoGlobal = global, FearGreed = fng[^1], FearGreedHistory = fng.TakeLast(30).ToList(),
                Funding = funding, Dvol = new() { ["BTC"] = 52.4, ["ETH"] = 61.8 }, Curve = curves[0], CurveMonthAgo = curves[30], CurveYearAgo = curves[365], VolTerm = volTerm,
            });
            Write("quotes", sections.SelectMany(s => s.Rows));
            Write("breadth", new Breadth
            {
                Universe = "sp500", Members = 503, Advancers = 311, Decliners = 186, PctAbove50d = 61.2, PctAbove200d = 68.9, PctNear52wHigh = 9.4, PctNear52wLow = 1.8,
                MedianChangePct = 0.42, Sectors = new() { new("Information Technology", 1.21, 67), new("Financials", 0.62, 71), new("Health Care", -0.31, 63), new("Energy", -0.88, 22), new("Utilities", 0.12, 31) },
                AsOfUtc = DateTime.UtcNow
            });
            Write("movers", new MoverList { Id = "day_gainers", Title = "Day gainers", Rows = UniverseCatalog.MegaCaps.Select((m, i) => Quote(m.Symbol, m.Name, 100 + i * 23, 9.5 - i * 0.6, i)).ToList() });
            Write("news", Enumerable.Range(0, 40).Select(i => new NewsItem
            {
                Title = $"Fixture headline {i + 1}: markets digest data", Url = $"https://example.org/news/{i}", Source = i % 3 == 0 ? "CNBC" : i % 3 == 1 ? "CoinDesk" : "Federal Reserve",
                PublishedUtc = DateTime.UtcNow.AddMinutes(-i * 17), Feed = i % 2 == 0 ? "markets" : "crypto", Symbols = i % 5 == 0 ? new() { "BTCUSDT" } : new(),
            }));

            // ── crypto ──
            Write("crypto-global", global);
            Write("crypto-coins", Crypto.Select((s, i) => new CoinRow
            {
                Id = s.Replace("USDT", "").ToLowerInvariant(), Symbol = s.Replace("USDT", "").ToLowerInvariant(), Name = s.Replace("USDT", ""), Rank = i + 1,
                Price = (double)daily[s][^1].Close, MarketCap = 1e12 / (i + 1), Volume24h = 3e10 / (i + 1), Change1hPct = (i - 2) * 0.2, Change24hPct = (i - 2) * 1.1,
                Change7dPct = (i - 3) * 3.4, AthChangePct = -10 - i * 7, CirculatingSupply = 1e7 * (i + 1), BinanceSymbol = s,
            }));
            Write("crypto-funding", funding);
            Write("crypto-fng", fng);
            Write("crypto-dvol", Enumerable.Range(0, 365).Select(d => new TimePoint(today.AddDays(d - 364), 50 + Math.Sin(d / 15.0) * 9)));
            Write("crypto-defi", new
            {
                Tvl = Enumerable.Range(0, 365).Select(d => new TimePoint(today.AddDays(d - 364), 8e10 + d * 1.2e8)),
                Chains = new[] { new { Name = "Ethereum", Token = "ETH", Tvl = 6.1e10 }, new { Name = "Solana", Token = "SOL", Tvl = 9.8e9 }, new { Name = "BSC", Token = "BNB", Tvl = 5.4e9 } },
                Stablecoins = Enumerable.Range(0, 365).Select(d => new TimePoint(today.AddDays(d - 364), 1.5e11 + d * 2e8)),
            });
            Write("crypto-trending", Crypto.Take(4).Select((s, i) => new { Id = s.ToLowerInvariant(), Symbol = s.Replace("USDT", ""), Name = s.Replace("USDT", ""), Rank = i + 3, Price = (double)daily[s][^1].Close, Change24hPct = 3.1 - i }));
            var hours = Enumerable.Range(0, 200).Select(h => DateTime.UtcNow.AddHours(h - 200)).ToList();
            Write("crypto-derivatives", new DerivativesView
            {
                Symbol = "BTCUSDT", Current = funding[0],
                Funding = hours.Where((_, i) => i % 8 == 0).Select((t, i) => new TimePoint(t, 0.0001 * Math.Sin(i / 3.0))).ToList(),
                OpenInterestValue = hours.Select((t, i) => new TimePoint(t, 9e9 + i * 1e7)).ToList(),
                LongShortAccounts = hours.Select((t, i) => new TimePoint(t, 1.8 + Math.Sin(i / 20.0) * 0.3)).ToList(),
                TopTraderLongShort = hours.Select((t, i) => new TimePoint(t, 1.4 + Math.Cos(i / 25.0) * 0.2)).ToList(),
                TakerBuySell = hours.Select((t, i) => new TimePoint(t, 1 + Math.Sin(i / 9.0) * 0.12)).ToList(),
                Venues = new() { new VenueFunding("Binance", "BTCUSDT", 0.0001, 10.95, DateTime.UtcNow.AddHours(3)), new VenueFunding("OKX", "BTC-USDT-SWAP", 0.00008, 8.76, DateTime.UtcNow.AddHours(3)) },
            });

            // ── macro ──
            Write("macro", MacroData.Catalog.Select((info, i) => new MacroSeries
            {
                Info = info, Points = Enumerable.Range(0, 120).Select(m => new TimePoint(today.AddMonths(m - 119), 2 + i * 0.7 + Math.Sin(m / 8.0 + i))).ToList()
            }));
            Write("macro-curve", new { Latest = curves[0], History = curves });
            Write("macro-volterm", new
            {
                Latest = volTerm,
                History = MacroData.VolIndices.ToDictionary(k => k, k => Enumerable.Range(0, 365).Select(d => new TimePoint(today.AddDays(d - 364), k switch
                {
                    "VIX9D" => 13 + Math.Sin(d / 9.0) * 3, "VIX" => 14 + Math.Sin(d / 10.0) * 3, "VIX3M" => 16 + Math.Sin(d / 14.0) * 2, "VIX6M" => 17.5 + Math.Sin(d / 18.0) * 1.5,
                    "VVIX" => 85 + Math.Sin(d / 7.0) * 9, _ => 140 + Math.Sin(d / 11.0) * 6
                })).ToList())
            });

            // ── studio ──
            var ema = await studio.CreateFromTemplateAsync("ema-cross", "klives", "EMA cross 20/50");
            await studio.UpdateAsync(ema.Id, new StudioDraft { DefaultSymbol = "BTCUSDT" }, "klives");
            var rsi2 = await studio.CreateFromTemplateAsync("rsi2-reversion", "kliveagent", "RSI(2) reversion");
            await studio.UpdateAsync(rsi2.Id, new StudioDraft { DefaultSymbol = "ETHUSDT" }, "kliveagent");
            var script = await studio.CreateFromTemplateAsync("script-keltner", "klives", "Keltner breakout (C#)");
            await studio.UpdateAsync(script.Id, new StudioDraft { DefaultSymbol = "SOLUSDT" }, "klives");
            foreach (var s in new[] { ema, rsi2, script })
            {
                var fresh = (await studio.GetAsync(s.Id))!;
                Write($"studio-get-{s.Id}", TerminalRoutes.StudioDetail(fresh, registry.Resolve(fresh.ClassName), await studio.VersionsAsync(s.Id)));
            }
            var emaFresh = (await studio.GetAsync(ema.Id))!;
            Write("studio-check", studio.Check(StudioKind.Blueprint, emaFresh.Blueprint, null, "fixture_check"));
            var (check, result, error) = await studio.QuickTestAsync(
                new StudioDraft { Kind = StudioKind.Blueprint, Blueprint = emaFresh.Blueprint, DefaultSymbol = "BTCUSDT", DefaultInterval = TimeInterval.OneDay, Name = emaFresh.Name },
                new StudioRunRequest { Symbol = "BTCUSDT", Interval = TimeInterval.OneDay, Bars = 1000 });
            Assert.Null(error);
            Write("studio-quick-test", new { Check = check, Result = result, Error = error });

            // ── lab: real queued jobs + a sweep ──
            var jobIds = new List<string>();
            foreach (var (s, sym) in new[] { (ema, "BTCUSDT"), (ema, "ETHUSDT"), (rsi2, "ETHUSDT"), (script, "SOLUSDT") })
                jobIds.Add(await studio.QueueBacktestAsync(s.Id, new StudioRunRequest { Symbol = sym, Interval = TimeInterval.OneDay, Bars = 1000 }, "lab"));
            foreach (var id in jobIds)
                for (int i = 0; i < 600; i++)
                {
                    var j = await jobs.GetAsync(id);
                    if (j?.Status is BacktestJobStatus.Succeeded or BacktestJobStatus.Failed) break;
                    await Task.Delay(100);
                }
            foreach (var id in jobIds) Write($"backtest-{id}", TerminalRoutes.JobDetail((await jobs.GetAsync(id))!));

            var baseConfig = studio.BuildConfig(emaFresh, new StudioRunRequest { Symbol = "BTCUSDT", Interval = TimeInterval.OneDay, Bars = 900 }, "lab-study");
            var study = await runner.EnqueueAsync(new StudyRequest
            {
                Kind = StudyKind.Sweep, Base = baseConfig, Objective = "sharpe",
                Params = new() { new SweepParam { Name = "fast", Min = 5, Max = 40, Step = 5, Integer = true }, new SweepParam { Name = "slow", Min = 40, Max = 160, Step = 20, Integer = true } }
            }, "klives");
            StudyRecord? done = null;
            for (int i = 0; i < 1200 && done == null; i++)
            {
                var r = await runner.GetAsync(study.Id);
                if (r?.Status is StudyStatus.Done or StudyStatus.Failed) done = r;
                else await Task.Delay(100);
            }
            Assert.NotNull(done);
            Write($"study-{study.Id}", done);
            Write("lab-studies", (await runner.ListAsync()).Select(TerminalRoutes.StudyRow));

            // ── alpha lab ──
            AlphaLab? lab = null;
            var evaluator = new CandidateEvaluator(studio, queue, jobs, runner, registry);
            lab = new AlphaLab(db, studio, evaluator, async (_, _, _) =>
            {
                var paperId = (await lab!.ListPapersAsync()).OrderByDescending(p => p.Relevance).First().Id;
                await lab.SubmitAsync(paperId, ema.Id, "Trend persists across major crypto assets", Crypto.Take(3), TimeInterval.OneDay, "kliveagent");
                await lab.SetVerdictAsync(paperId, "implemented", "EMA 20/50 crossover; parameters from the paper.", "kliveagent");
                return "Implemented one paper.";
            }, _ => Task.CompletedTask, _ => { }, (_, _) => { });
            await lab.LoadAsync();
            var settings = lab.Settings;
            settings.EvaluationBars = 900;
            settings.UseArxiv = settings.UseOpenAlex = false;
            settings.CryptoTestSymbols = Crypto.ToList();
            await lab.SaveSettingsAsync(settings);
            lab.Start(cts.Token);
            var papers = new[]
            {
                ("Time-series momentum in cryptocurrency markets", "We backtest a trend-following moving average rule on bitcoin and the largest cryptocurrencies with transaction costs and report out-of-sample Sharpe ratios above one, robust across subperiods and to realistic trading frictions."),
                ("Funding rates and the cross-section of perpetual futures returns", "Perpetual futures funding rates predict returns. A carry strategy long low-funding and short high-funding contracts earns excess returns after costs; we document the effect out-of-sample."),
                ("Existence and uniqueness of equilibria in a mean-field game", "We prove a theorem establishing existence and uniqueness of solutions to a class of stochastic differential equations arising in a mean-field game of optimal execution."),
            };
            foreach (var (title, abs) in papers) await lab.AddPaperAsync(title, abs, "https://example.org/paper", "klives");
            var topPaper = (await lab.ListPapersAsync()).OrderByDescending(p => p.Relevance).First();
            await lab.DispatchAsync("manual", default, new[] { topPaper.Id });
            AlphaCandidate? cand = null;
            for (int i = 0; i < 2400; i++)
            {
                cand = (await lab.ListCandidatesAsync()).FirstOrDefault();
                if (cand != null && cand.Status is not (CandidateStatus.Proposed or CandidateStatus.Evaluating)) break;
                await Task.Delay(100);
            }
            Assert.NotNull(cand);
            for (int i = 0; i < 50 && (await lab.ListRunsAsync("analyse")).FirstOrDefault()?.Status == "running"; i++) await Task.Delay(100);

            var allPapers = await lab.ListPapersAsync(limit: 500);
            Write("alpha-papers", new { Total = allPapers.Count, Rows = allPapers.Select(TerminalRoutes.PaperRow) });
            foreach (var p in allPapers)
                Write($"alpha-paper-{p.Id.Replace(':', '_')}", new { Paper = p, Candidates = (await lab.ListCandidatesAsync(null, p.Id, 50)).Select(TerminalRoutes.CandidateRow) });
            var cands = await lab.ListCandidatesAsync();
            Write("alpha-candidates", cands.Select(TerminalRoutes.CandidateRow));
            foreach (var c in cands) Write($"alpha-candidate-{c.Id}", c);
            Write("alpha-runs", (await lab.ListRunsAsync(null, 50)).Select(TerminalRoutes.RunRow));
            Write("alpha-overview", new
            {
                lab.Settings, lab.AgentBusy, LastIngestUtc = (DateTime?)DateTime.UtcNow.AddHours(-2), LastDispatchUtc = (DateTime?)DateTime.UtcNow.AddMinutes(-5),
                Papers = await lab.PaperStatusCountsAsync(), Candidates = await lab.CandidateStatusCountsAsync(),
                Runs = (await lab.ListRunsAsync(null, 15)).Select(TerminalRoutes.RunRow),
                Leaders = cands.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score).Take(10).Select(TerminalRoutes.CandidateRow)
            });

            // lab blotter after everything ran (alpha's primary job included)
            var (rows, total) = await jobs.QuerySummariesAsync(new BacktestJobFilter { Limit = 200 });
            Write("lab-backtests", new { Total = total, Running = 0, Workers = queue.Workers, Rows = rows.Select(TerminalRoutes.LabRow) });
            foreach (var r in rows.Where(r => !jobIds.Contains(r.Id))) Write($"backtest-{r.Id}", TerminalRoutes.JobDetail((await jobs.GetAsync(r.Id))!));
            Write("lab-strategies", registry.All.OrderBy(d => d.Source == "builtin" ? 1 : 0).ThenBy(d => d.Name)
                .Select(d => new { d.Name, d.ClassName, d.Description, d.RequiresUniverse, d.Parameters, d.Source, d.StudioId, d.Version, d.Author }));
            Write("studio-list", (await studio.ListAsync(null, null, null, true, 300)).Select(TerminalRoutes.StudioRow));

            // ── alerts ──
            var alerts = new MarketAlertService(db, router, _ => Task.CompletedTask, (_, _) => { });
            await alerts.SaveAsync(new MarketAlert { Name = "BTC above 200d", Symbol = "BTCUSDT", Interval = TimeInterval.OneDay, Condition = "close > sma(close, 200)", Mode = AlertMode.Once });
            await alerts.SaveAsync(new MarketAlert { Name = "ETH RSI oversold", Symbol = "ETHUSDT", Interval = TimeInterval.OneDay, Condition = "rsi(14) < 30", Mode = AlertMode.OnChange });
            await alerts.TickAsync();
            Write("alerts", await alerts.ListAsync());
            Write("alert-events", await alerts.EventsAsync());
        }
    }
}
