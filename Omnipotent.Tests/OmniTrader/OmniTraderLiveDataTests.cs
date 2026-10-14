using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Data.News;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>Runs only with OMNITRADER_LIVE=1: these call the real free data sources.</summary>
    public sealed class OmniTraderLiveFactAttribute : FactAttribute
    {
        public OmniTraderLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OMNITRADER_LIVE") != "1")
                Skip = "Live data test; set OMNITRADER_LIVE=1 (needs internet).";
        }
    }

    /// <summary>
    /// Every free source the terminal depends on, parsed against the real endpoint. Run after any
    /// provider change: <c>OMNITRADER_LIVE=1 dotnet test -c Release --filter "FullyQualifiedName~OmniTraderLiveDataTests"</c>
    /// </summary>
    public class OmniTraderLiveDataTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly MarketDataRouter router;
        private readonly DataHub hub;

        public OmniTraderLiveDataTests(ITestOutputHelper output)
        {
            this.output = output;
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-live-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            router = new MarketDataRouter(new CandleCacheRepository(db));
            hub = new DataHub(router);
        }

        public void Dispose()
        {
            db.Dispose();
            try { File.Delete(dbPath); } catch { }
        }

        private void Log(string s) => output.WriteLine(s);

        [OmniTraderLiveFact]
        public async Task Binance_Bars_Are_Open_Stamped_With_A_Forming_Bar()
        {
            var fetch = await router.Binance.FetchLatestAsync("BTCUSDT", TimeInterval.OneHour, 50);
            Assert.Equal(50, fetch.Closed.Count);
            Assert.All(fetch.Closed, b => Assert.Equal(0, b.Timestamp.Minute));
            Assert.Equal(CandleClock.LastClosedOpen(DateTime.UtcNow, TimeInterval.OneHour), fetch.Closed[^1].Timestamp);
            Assert.NotNull(fetch.Forming);
            Log($"last closed {fetch.Closed[^1].Timestamp:o} forming {fetch.Forming!.Value.Timestamp:o} {fetch.Forming.Value.Close}");
        }

        [OmniTraderLiveFact]
        public async Task Coinbase_And_Kraken_Fallbacks_Answer()
        {
            var cb = await new CoinbaseMarketDataProvider().FetchLatestAsync("BTCUSDT", TimeInterval.FourHour, 30);
            Assert.True(cb.Closed.Count >= 25, $"coinbase {cb.Closed.Count}");
            Assert.All(cb.Closed, b => Assert.Equal(0, b.Timestamp.Hour % 4));
            var kr = await new KrakenMarketDataProvider().FetchLatestAsync("ETHUSDT", TimeInterval.OneDay, 30);
            Assert.True(kr.Closed.Count >= 29, $"kraken {kr.Closed.Count}");
            Log($"coinbase last {cb.Closed[^1].Timestamp:o}, kraken last {kr.Closed[^1].Timestamp:o}");
        }

        [OmniTraderLiveFact]
        public async Task Router_Caches_And_Serves_Equity_Ranges()
        {
            var daily = await router.GetHistoricalCandlesAsync("AAPL", TimeInterval.OneDay, 300);
            Assert.True(daily.Count >= 290, $"AAPL daily {daily.Count}");
            var four = await router.GetHistoricalCandlesAsync("MSFT", TimeInterval.FourHour, 60);
            Assert.True(four.Count >= 50, $"MSFT 4h {four.Count}");
            var range = await router.GetHistoricalCandlesRangeAsync("SPY", TimeInterval.OneDay, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            Assert.InRange(range.Count, 240, 260);
            var crypto = await router.GetHistoricalCandlesRangeAsync("ETHUSDT", TimeInterval.OneDay, new DateTime(2024, 1, 1), new DateTime(2024, 3, 31));
            Assert.InRange(crypto.Count, 89, 91);
            var again = await router.GetHistoricalCandlesRangeAsync("ETHUSDT", TimeInterval.OneDay, new DateTime(2024, 1, 1), new DateTime(2024, 3, 31));
            Assert.Equal(crypto.Count, again.Count);
            var chart = await router.GetChartCandlesAsync("SOLUSDT", TimeInterval.FifteenMinute, 100);
            Assert.True(chart.Closed.Count >= 99);
            Log($"SPY 2024 {range.Count} bars, ETH Q1 {crypto.Count}, SOL forming {chart.Forming?.Timestamp:o}");
        }

        [OmniTraderLiveFact]
        public async Task Yahoo_Quotes_Company_Financials_Options()
        {
            var quotes = await hub.GetQuotesAsync(new[] { "AAPL", "^GSPC", "EURUSD=X", "BTCUSDT", "GC=F" });
            Assert.Equal(5, quotes.Count);
            Assert.All(quotes, q => Assert.True(q.Price > 0, q.Symbol));
            var company = await hub.GetCompanyAsync("AAPL");
            Assert.NotNull(company);
            Assert.Equal("Technology", company!.Sector);
            Assert.True(company.Stats["Market cap"] > 1e11);
            var fin = await hub.GetFinancialsAsync("AAPL");
            Assert.True(fin!.Annual.TryGetValue("TotalRevenue", out var rev) && rev.Count >= 3);
            var chain = await hub.GetOptionsAsync("AAPL");
            Assert.NotNull(chain);
            Assert.NotEmpty(chain!.Calls);
            Assert.Contains(chain.Calls, c => c.Delta.HasValue);
            Log($"AAPL {company.Name} cap {company.Stats["Market cap"]:E2} revenue years {rev!.Count} expiries {chain.Expirations.Count} maxpain {chain.MaxPain} atmIV {chain.AtmIvPct:F1}");
            var movers = await hub.GetMoversAsync("day_gainers", 10);
            Assert.NotEmpty(movers!.Rows);
            var trending = await hub.GetTrendingAsync();
            Assert.NotEmpty(trending!);
        }

        [OmniTraderLiveFact]
        public async Task Sec_Facts_And_Filings()
        {
            var facts = await hub.GetSecFactsAsync("MSFT");
            Assert.NotNull(facts);
            Assert.True(facts!.Annual.ContainsKey("Revenue") && facts.Annual["Revenue"].Count >= 5);
            var filings = await hub.GetFilingsAsync("MSFT", "10-K");
            Assert.NotEmpty(filings!);
            Log($"{facts.EntityName}: revenue years {facts.Annual["Revenue"].Count}, latest 10-K {filings![0].FiledUtc:d} {filings[0].Url}");
        }

        [OmniTraderLiveFact]
        public async Task Crypto_Derivatives_And_Market()
        {
            var board = await hub.GetFundingBoardAsync();
            Assert.True(board!.Count > 100);
            var deriv = await hub.GetDerivativesAsync("BTCUSDT");
            Assert.NotNull(deriv!.Current);
            Assert.NotEmpty(deriv.OpenInterest);
            Assert.NotEmpty(deriv.LongShortAccounts);
            Assert.NotEmpty(deriv.Venues);
            var dvol = await hub.GetDvolAsync("BTC", 30);
            Assert.NotEmpty(dvol!);
            var surface = await hub.GetCryptoOptionSurfaceAsync("ETH");
            Assert.NotEmpty(surface!.Term);
            var global = await hub.GetCryptoGlobalAsync();
            Assert.True(global!.TotalMarketCapUsd > 1e11);
            var coins = await hub.GetTopCoinsAsync(50);
            Assert.True(coins!.Count >= 50);
            var fng = await hub.GetFearGreedAsync(30);
            Assert.True(fng!.Count >= 25);
            var tvl = await hub.GetTvlAsync();
            Assert.NotEmpty(tvl!);
            var book = await hub.GetOrderBookAsync("BTCUSDT", 20);
            Assert.NotNull(book);
            Log($"funding board {board.Count}, BTC funding {deriv.Current!.FundingAnnualPct:F1}%/yr, venues {string.Join(",", deriv.Venues.Select(v => v.Venue))}, DVOL {dvol![^1].V}, ETH expiries {surface.Term.Count}, BTC dom {global.BtcDominancePct:F1}%");
        }

        [OmniTraderLiveFact]
        public async Task Macro_Curve_Bls_Dbnomics_Cboe()
        {
            var curve = await hub.GetLatestCurveAsync();
            Assert.NotNull(curve);
            Assert.True(curve!.Points.Count >= 10);
            var macro = await hub.GetMacroAsync();
            foreach (var s in macro!) Log($"{s.Info.Id}: {s.Latest} @ {s.LatestDate:d} {s.Error}");
            Assert.Contains(macro, s => s.Info.Id == "us-cpi-yoy" && s.Latest.HasValue);
            Assert.Contains(macro, s => s.Info.Id == "us-fed-funds" && s.Latest.HasValue);
            var vol = await hub.GetVolTermAsync();
            Assert.True(vol!.Latest.Vix > 0);
            Log($"curve {curve.Date:d} 2s10s {curve.Spread2s10s:F2}, VIX {vol.Latest.Vix} ratio {vol.Latest.Ratio:F2}");
        }

        [OmniTraderLiveFact]
        public async Task News_Feeds_Parse()
        {
            var markets = await hub.GetNewsAsync("markets");
            Assert.True(markets!.Count >= 10);
            var symbol = await hub.GetNewsAsync(symbol: "NVDA");
            Assert.NotEmpty(symbol!);
            var crypto = await hub.GetNewsAsync(symbol: "ETHUSDT");
            Assert.NotEmpty(crypto!);
            foreach (var feed in NewsData.Feeds)
            {
                try { Log($"{feed.Id}: {(await hub.News.GetFeedAsync(feed)).Count}"); }
                catch (Exception ex) { Log($"{feed.Id}: FAILED {ex.Message}"); }
            }
        }

        [OmniTraderLiveFact]
        public async Task Universes_And_Breadth()
        {
            var sp = await hub.Universes.GetAsync("sp500");
            Assert.InRange(sp.Count, 480, 520);
            var ftse = await hub.Universes.GetAsync("ftse100");
            Assert.InRange(ftse.Count, 90, 110);
            Assert.All(ftse, m => Assert.EndsWith(".L", m.Symbol));
            var top = await hub.Universes.GetAsync("crypto-top50");
            Assert.Equal(50, top.Count);
            var breadth = await hub.GetBreadthAsync("sp500");
            Assert.NotNull(breadth);
            Assert.True(breadth!.Members > 400);
            Log($"S&P breadth: {breadth.Advancers} up / {breadth.Decliners} down, >50d {breadth.PctAbove50d}%, >200d {breadth.PctAbove200d}%");
            var monitor = await hub.GetMonitorAsync();
            Assert.NotNull(monitor);
            foreach (var s in monitor!.Sections) Log($"{s.Title}: {s.Rows.Count} {s.Error}");
            foreach (var e in monitor.Errors) Log("monitor error: " + e);
        }
    }
}

namespace Omnipotent.Tests.OmniTrader
{
    public class OmniTraderLiveTimingProbe
    {
        private readonly Xunit.Abstractions.ITestOutputHelper output;
        public OmniTraderLiveTimingProbe(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

        [OmniTraderLiveFact]
        public async Task Time_Each_Source()
        {
            var session = new Omnipotent.Services.OmniTrader.MarketData.YahooSession();
            var yahoo = new Omnipotent.Services.OmniTrader.Data.Equities.YahooData(session);
            var macro = new Omnipotent.Services.OmniTrader.Data.Macro.MacroData();
            async Task T(string label, Func<Task> f)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { await f(); output.WriteLine($"{label}: {sw.ElapsedMilliseconds} ms"); }
                catch (Exception ex) { output.WriteLine($"{label}: FAIL {sw.ElapsedMilliseconds} ms {ex.Message}"); }
            }
            await T("yahoo quotes (first, incl crumb)", () => yahoo.GetQuotesAsync(new[] { "AAPL" }));
            await T("yahoo quotes (warm)", () => yahoo.GetQuotesAsync(new[] { "MSFT" }));
            await T("yahoo company", () => yahoo.GetCompanyAsync("AAPL"));
            await T("yahoo financials", () => yahoo.GetFinancialsAsync("AAPL"));
            await T("yahoo options", () => yahoo.GetOptionsAsync("AAPL"));
            await T("treasury 2026", () => macro.GetYieldCurvesAsync(2026));
            await T("bls", () => macro.GetBlsAsync(new[] { "CUUR0000SA0" }, 10));
            await T("dbnomics fedfunds", () => macro.GetDbnomicsAsync("FED/H15/RIFSPFF_N.B"));
            await T("cboe vix", () => macro.GetCboeAsync("VIX"));
        }
    }
}
