using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniTrader.Backtesting.Studies;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research.Alpha;
using Omnipotent.Services.OmniTrader.Scripting;
using Omnipotent.Services.OmniTrader.Sessions;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using Omnipotent.Services.OmniTrader.Studio;

namespace Omnipotent.Tests.OmniTrader
{
    // ── test strategies ──────────────────────────────────────────────────────────

    /// <summary>A fixed two-coin basket, the shape of the live ETF deployment that never traded.</summary>
    [TradingStrategy("Test basket", "Equal weight across two coins.", RequiresUniverse = true)]
    public sealed class TestBasketStrategy : ScriptUniverseStrategy
    {
        [Param("Lookback", Min = 5, Max = 400)] public int Lookback { get; set; } = 252;
        protected override string[] Symbols { get; } = { "AAAUSDT", "BBBUSDT" };
        public int Calls;

        public override void OnBar(UniverseView u)
        {
            Calls++;
            SetWeights(new Dictionary<string, double> { ["AAAUSDT"] = 0.4, ["BBBUSDT"] = 0.4 });
        }
    }

    /// <summary>An ETF basket: its regime symbol (BTCUSDT by default) is appended, as in the live strategy.</summary>
    public sealed class TestEtfBasketStrategy : ScriptUniverseStrategy
    {
        [Param("Crash lookback", Min = 100, Max = 800)] public int CrashLookback { get; set; } = 504;
        protected override string[] Symbols { get; } = { "SPY", "QQQ", "IWM" };
        public override void OnBar(UniverseView u) { }
    }

    /// <summary>A single-symbol script that buys half its equity once it has five bars.</summary>
    public sealed class TestBuyOnceStrategy : ScriptStrategy
    {
        [Param("Slow length", Min = 5, Max = 300)] public int SlowLength { get; set; } = 50;
        public override void OnBar()
        {
            if (IsFlat && BarCount > 5) BuyPercent(50);
        }
    }

    public class DeploymentSystemTests : IDisposable
    {
        private readonly string dbPath;
        private readonly OmniTraderDb db;
        private readonly CandleCacheRepository cache;
        private readonly MarketDataRouter router;
        private readonly DeploymentRepository deployments;
        private readonly OrderRepository orders;
        private readonly FillRepository fills;
        private readonly EquityRepository equity;
        private readonly PaperBookStore books;
        private readonly List<string> errors = new();

        public DeploymentSystemTests()
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"ot-deploy-{Guid.NewGuid():N}.db");
            db = new OmniTraderDb(dbPath);
            db.InitialiseAsync().GetAwaiter().GetResult();
            cache = new CandleCacheRepository(db);
            router = new MarketDataRouter(cache);
            deployments = new DeploymentRepository(db);
            orders = new OrderRepository(db);
            fills = new FillRepository(db);
            equity = new EquityRepository(db);
            books = new PaperBookStore(db);
        }

        public void Dispose()
        {
            db.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }

        private void Log(string _) { }
        private void Err(string m, Exception? e) { lock (errors) errors.Add(e == null ? m : $"{m}: {e.Message}"); }

        private static List<OHLCCandle> Bars(int n, decimal start, DateTime lastOpen, TimeInterval interval, double drift = 0.002)
        {
            var list = new List<OHLCCandle>(n);
            decimal price = start;
            for (int i = 0; i < n; i++)
            {
                var t = lastOpen.AddMinutes((int)interval * (i - (n - 1)));
                decimal next = price * (decimal)(1 + drift + Math.Sin(i) * 0.004);
                list.Add(new OHLCCandle(t, price, Math.Max(price, next) * 1.01m, Math.Min(price, next) * 0.99m, next, 1000));
                price = next;
            }
            return list;
        }

        private async Task<DeploymentRow> InsertRowAsync(DeploymentConfig config, DeploymentStatus status = DeploymentStatus.Running)
        {
            var row = new DeploymentRow
            {
                Id = Guid.NewGuid().ToString("N"),
                StrategyClass = config.StrategyClass,
                Config = config,
                Mode = config.Mode,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                EquityInitial = config.InitialQuoteBalance,
                EquityCurrent = config.InitialQuoteBalance,
            };
            await deployments.InsertAsync(row);
            return row;
        }

        private static StrategyDescriptor Describe<T>(string className, string source = "script") where T : TradingStrategy, new() => new()
        {
            Name = className,
            Description = "",
            ClassName = className,
            Type = typeof(T),
            RequiresUniverse = typeof(ScriptUniverseStrategy).IsAssignableFrom(typeof(T)),
            Parameters = StrategyParams.For(typeof(T)),
            Source = source,
            Factory = () => new T(),
        };

        // ── F1: baskets get a multi-asset session ────────────────────────────────

        [Fact]
        public void A_fixed_basket_is_not_a_single_symbol_strategy()
        {
            var basket = new TestBasketStrategy().DeclareSymbols();
            Assert.False(basket.IsUniverse);                 // why SessionManager used to miss it
            Assert.Equal(SessionKind.Basket, SessionManager.KindOf(basket));
            Assert.Equal(SessionKind.Single, SessionManager.KindOf(new TestBuyOnceStrategy().DeclareSymbols()));
            Assert.Equal(SessionKind.Universe, SessionManager.KindOf(StrategySymbols.FromUniverse(new UniverseSpec { TopN = 20 })));
            // The live ETF strategy's basket carries its regime symbol too.
            Assert.Contains("BTCUSDT", new TestEtfBasketStrategy().DeclareSymbols().Fixed!);
        }

        [Fact]
        public async Task A_basket_deployment_runs_on_the_multi_asset_session()
        {
            var interval = TimeInterval.OneDay;
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            await cache.UpsertManyAsync("AAAUSDT", interval, Bars(900, 100m, last, interval));
            await cache.UpsertManyAsync("BBBUSDT", interval, Bars(900, 50m, last, interval));
            var registry = new StrategyRegistry();
            registry.RegisterDynamic(Describe<TestBasketStrategy>("test:basket"));
            var manager = new SessionManager(router, registry, deployments, orders, fills, equity, () => null, Log, Err, books);

            string id = await manager.CreateDeploymentAsync(new DeploymentConfig
            {
                StrategyClass = "test:basket", Symbol = "BTCUSDT", Interval = interval, Mode = SessionMode.Paper,
                Parameters = new() { ["RegimeSymbol"] = "AAAUSDT" },
            });
            try
            {
                Assert.True(manager.TryGetMulti(id, out var session));
                Assert.False(manager.TryGetPaper(id, out _));
                Assert.True(session!.IsBasket);
                Assert.Equal(new[] { "AAAUSDT", "BBBUSDT" }, session.Universe);
                Assert.Equal("AAAUSDT", (await deployments.GetAsync(id))!.Config.Symbol);
                Assert.Equal("basket", manager.PulseOf(id)!.Kind);
            }
            finally { await manager.KillAsync(id); }
        }

        [Fact]
        public async Task A_basket_session_trades_its_basket_and_records_the_fills()
        {
            var interval = TimeInterval.OneDay;
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            var config = new DeploymentConfig { StrategyClass = "test:basket", Symbol = "AAAUSDT", Interval = interval, Mode = SessionMode.Paper };
            var row = await InsertRowAsync(config);
            var strategy = new TestBasketStrategy { RegimeSymbol = "AAAUSDT" };
            var basket = strategy.DeclareSymbols().Fixed!;
            var session = new MultiAssetSession(row.Id, config, strategy, SessionMode.Paper, router, new BinanceUniverseProvider(), null,
                orders, fills, equity, deployments, Log, Err, basket: basket, books: books);

            await session.ResolveUniverseAsync(CancellationToken.None);          // no Binance call for a basket
            Assert.Equal(new[] { "AAAUSDT", "BBBUSDT" }, session.Universe);

            var a = Bars(21, 100m, last, interval);
            var b = Bars(21, 50m, last, interval);
            session.Seed(new Dictionary<string, IReadOnlyList<OHLCCandle>> { ["AAAUSDT"] = a.Take(20).ToList(), ["BBBUSDT"] = b.Take(20).ToList() });
            bool dispatched = await session.IngestAsync(new Dictionary<string, IReadOnlyList<OHLCCandle>> { ["AAAUSDT"] = a.Skip(18).ToList(), ["BBBUSDT"] = b.Skip(18).ToList() }, CancellationToken.None);

            Assert.True(dispatched);
            Assert.Equal(1, strategy.Calls);
            var placed = await orders.ListByDeploymentAsync(row.Id);
            Assert.Equal(2, placed.Count);
            Assert.All(placed, o => Assert.Equal("Filled", o.Status));
            Assert.Equal(2, (await fills.ListByDeploymentAsync(row.Id)).Count);   // F2: the fills exist
            Assert.Equal(2, session.PortfolioPositions().Count);
            var book = await books.GetAsync(row.Id);
            Assert.NotNull(book);
            Assert.Equal(2, book!.Balances.Count);
            Assert.Equal(1, book.Bars);
            Assert.DoesNotContain(errors, e => e.Contains("could not be recorded"));
        }

        // ── F2 + F4: single-symbol paper fills, marked equity, book across a restart ──

        [Fact]
        public async Task A_paper_fill_is_stored_and_the_book_survives_a_restart()
        {
            var interval = TimeInterval.OneHour;
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            var config = new DeploymentConfig { StrategyClass = "test:buy", Symbol = "AAAUSDT", Interval = interval, Mode = SessionMode.Paper, Parameters = new() { ["TradeSymbol"] = "AAAUSDT" } };
            var row = await InsertRowAsync(config);
            var bars = Bars(22, 100m, last, interval);
            var session = new PaperSession(row.Id, config, new TestBuyOnceStrategy { TradeSymbol = "AAAUSDT" }, router, orders, fills, equity, deployments, Log, Err, books: books);
            session.Seed(bars.Take(20).ToList());

            await session.ProcessClosedCandleAsync(bars[20], CancellationToken.None);

            var placed = await orders.ListByDeploymentAsync(row.Id);
            var filled = await fills.ListByDeploymentAsync(row.Id);
            Assert.Single(placed);
            Assert.Equal("Filled", placed[0].Status);
            // Before the fix: the fill raced its own order row, hit the foreign key and was swallowed.
            Assert.Single(filled);
            Assert.Equal(placed[0].Id, filled[0].OrderId);

            // Equity is marked at the last close, not at the entry price.
            await session.ProcessClosedCandleAsync(bars[21], CancellationToken.None);
            decimal expected = session.GetQuoteBalance() + session.GetBaseBalance() * bars[21].Close;
            Assert.Equal(expected, session.GetEquity());
            Assert.NotEqual(session.GetQuoteBalance() + session.GetBaseBalance() * session.GetPosition()!.AveragePrice, session.GetEquity());

            // A restart restores the holding instead of turning it into cash.
            var book = await books.GetAsync(row.Id);
            Assert.NotNull(book);
            Assert.True(book!.Base > 0m);
            var restarted = new PaperSession(row.Id, config, new TestBuyOnceStrategy { TradeSymbol = "AAAUSDT" }, router, orders, fills, equity, deployments, Log, Err,
                startingQuote: row.EquityCurrent, books: books, restore: book);
            Assert.Equal(book.Base, restarted.GetBaseBalance());
            Assert.Equal(book.Quote, restarted.GetQuoteBalance());
            Assert.Equal(book.Position!.Qty, restarted.GetPosition()!.Qty);
            Assert.Equal(2, restarted.Pulse().BarsTotal);
        }

        [Fact]
        public async Task Next_open_paper_fills_at_the_next_bars_open_as_the_lab_tested()
        {
            var interval = TimeInterval.OneHour;
            var last = CandleClock.LastClosedOpen(DateTime.UtcNow, interval);
            var config = new DeploymentConfig
            {
                StrategyClass = "test:buy", Symbol = "AAAUSDT", Interval = interval, Mode = SessionMode.Paper,
                SlippageFraction = 0.001m, Execution = ExecutionTiming.NextOpen, Parameters = new() { ["TradeSymbol"] = "AAAUSDT" },
            };
            var row = await InsertRowAsync(config);
            var bars = Bars(22, 100m, last, interval);
            // Make the next bar gap up, so a fill at the signal close and one at the next open differ.
            bars[21] = bars[21] with { Open = bars[20].Close * 1.002m, High = Math.Max(bars[21].High, bars[20].Close * 1.003m) };
            var session = new PaperSession(row.Id, config, new TestBuyOnceStrategy { TradeSymbol = "AAAUSDT" }, router, orders, fills, equity, deployments, Log, Err, books: books);
            session.Seed(bars.Take(20).ToList());

            await session.ProcessClosedCandleAsync(bars[20], CancellationToken.None);   // the strategy buys at this close
            Assert.Empty(await orders.ListByDeploymentAsync(row.Id));                     // queued, not filled
            Assert.Equal(0m, session.GetBaseBalance());

            // The server restarts before the next bar: the queued order survives in the paper book.
            var book = await books.GetAsync(row.Id);
            Assert.Single(book!.AtNextOpen);
            session = new PaperSession(row.Id, config, new TestBuyOnceStrategy { TradeSymbol = "AAAUSDT" }, router, orders, fills, equity, deployments, Log, Err,
                startingQuote: row.EquityCurrent, books: books, restore: book);
            session.Seed(bars.Take(21).ToList());

            await session.ProcessClosedCandleAsync(bars[21], CancellationToken.None);   // filled at this bar's open
            var placed = await orders.ListByDeploymentAsync(row.Id);
            var filled = await fills.ListByDeploymentAsync(row.Id);
            Assert.Single(placed);
            Assert.Single(filled);
            Assert.Equal(bars[21].Open * 1.001m, filled[0].Price, 6);
            Assert.True(session.GetBaseBalance() > 0m);
        }

        [Fact]
        public async Task A_document_collection_version_moves_on_every_write_and_only_then()
        {
            // The Alpha Lab overview and candidate list keep what they read until this moves.
            var store = new DocumentStore<PaperBook>(db, "version-probe-" + Guid.NewGuid().ToString("N")[..6], b => b.DeploymentId);
            long v0 = store.Version;
            await store.GetAsync("x");
            await store.QueryAsync();
            Assert.Equal(v0, store.Version);                       // reads don't move it
            await store.UpsertAsync(new PaperBook { DeploymentId = "x" });
            long v1 = store.Version;
            Assert.True(v1 > v0);
            await store.DeleteAsync("x");
            Assert.True(store.Version > v1);
        }

        [Fact]
        public async Task The_database_refuses_a_fill_before_its_order_row()
        {
            // The live mechanism: 100 Filled orders and 0 fills on a paper deployment, because the router
            // fills inside PlaceOrderAsync and the fill was written first.
            var ex = await Assert.ThrowsAnyAsync<SqliteException>(() => fills.InsertAsync(new FillEvent
            {
                OrderId = "not-written-yet", IntentId = "i", Qty = 1, Price = 1, Fee = 0, FeeCurrency = "USDT", FilledUtc = DateTime.UtcNow,
            }));
            Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── F5: stopping a deployment with no session ────────────────────────────

        [Fact]
        public async Task A_paused_paper_deployment_can_be_stopped()
        {
            var manager = new SessionManager(router, new StrategyRegistry(), deployments, orders, fills, equity, () => null, Log, Err, books);
            var row = await InsertRowAsync(new DeploymentConfig { StrategyClass = "x", Symbol = "AAAUSDT", Interval = TimeInterval.OneHour, Mode = SessionMode.Paper }, DeploymentStatus.Paused);

            Assert.True(await manager.KillAsync(row.Id));
            Assert.Equal(DeploymentStatus.Stopped, (await deployments.GetAsync(row.Id))!.Status);
            Assert.True(await manager.KillAsync(row.Id));         // idempotent
            Assert.False(await manager.KillAsync("no-such-deployment"));
        }

        // ── F6: equity series ────────────────────────────────────────────────────

        [Fact]
        public void Thinning_keeps_the_ends_and_the_extremes()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var points = Enumerable.Range(0, 12_000).Select(i => new EquityPoint
            {
                Ts = start.AddMinutes(15 * i), MarkPrice = 1, QuoteBalance = 0, BaseBalance = 0,
                Equity = 10_000m + (decimal)(Math.Sin(i / 300.0) * 500) + (i == 7_777 ? -3_000m : 0m),
            }).ToList();

            var thin = EquityRepository.Thin(points, 1500);

            Assert.InRange(thin.Count, 100, 1500);
            Assert.Equal(points[0].Ts, thin[0].Ts);
            Assert.Equal(points[^1].Ts, thin[^1].Ts);
            Assert.Contains(thin, p => p.Equity == points.Min(x => x.Equity));      // the drawdown survives
            Assert.Equal(thin.OrderBy(p => p.Ts).Select(p => p.Ts), thin.Select(p => p.Ts));
        }

        [Fact]
        public async Task The_equity_series_reads_from_its_covering_index()
        {
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT ts, mark_price, quote_balance, base_balance, equity FROM equity_ticks WHERE deployment_id='x' ORDER BY ts ASC";
            var plan = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) plan.Add(r.GetString(3));
            Assert.Contains(plan, p => p.Contains("COVERING INDEX idx_equity_ticks_series"));
        }

        // ── F9: health ───────────────────────────────────────────────────────────

        private static DeploymentRow Row(DeploymentStatus status, TimeInterval interval = TimeInterval.OneHour, string symbol = "BTCUSDT", DeploymentOrigin? origin = null, SessionMode mode = SessionMode.Paper) => new()
        {
            Id = "d1", StrategyClass = "s", Mode = mode, Status = status, CreatedUtc = DateTime.UtcNow.AddDays(-25),
            EquityInitial = 10_000, EquityCurrent = 10_000,
            Config = new DeploymentConfig { StrategyClass = "s", Symbol = symbol, Interval = interval, Mode = mode, Origin = origin },
        };

        private static SessionPulse Pulse(DateTime? lastBar, int bars, string kind = "single") => new()
        {
            Kind = kind, StartedUtc = DateTime.UtcNow.AddDays(-25), LastBarUtc = lastBar, BarsThisRun = bars, BarsTotal = bars,
        };

        [Fact]
        public void Health_says_what_the_live_basket_deployment_never_did()
        {
            var now = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc);
            // 143 bars, no orders, backtest traded every ~25 bars → quiet.
            var origin = new DeploymentOrigin { BarsPerTrade = 25 };
            var h = DeploymentHealth.Assess(Row(DeploymentStatus.Running, origin: origin), Pulse(now.AddHours(-1), 143), null, false, now);
            Assert.Equal("quiet", h.State);
            Assert.Contains("143", h.Title);

            // Without an expectation (every deployment made before plans) it waits 120 bars: the live one reads quiet.
            Assert.Equal("ok", DeploymentHealth.Assess(Row(DeploymentStatus.Running), Pulse(now.AddHours(-1), 119), null, false, now).State);
            Assert.Equal("quiet", DeploymentHealth.Assess(Row(DeploymentStatus.Running), Pulse(now.AddHours(-1), 143), null, false, now).State);

            // Orders → trading.
            var traded = DeploymentHealth.Assess(Row(DeploymentStatus.Running, origin: origin), Pulse(now.AddHours(-1), 143), new OrderSummary { Orders = 4, Filled = 4 }, false, now);
            Assert.Equal("ok", traded.State);
            Assert.Equal(4, traded.Orders);
        }

        [Fact]
        public void Health_flags_stalls_detached_sessions_and_warm_ups()
        {
            var now = new DateTime(2026, 11, 13, 14, 0, 0, DateTimeKind.Utc); // a Friday
            Assert.Equal("stalled", DeploymentHealth.Assess(Row(DeploymentStatus.Running), Pulse(now.AddHours(-5), 10), null, false, now).State);
            // An equity feed gets the weekend.
            Assert.Equal("ok", DeploymentHealth.Assess(Row(DeploymentStatus.Running, symbol: "SPY"), Pulse(now.AddHours(-60), 10), null, false, now).State);
            Assert.Equal("detached", DeploymentHealth.Assess(Row(DeploymentStatus.Running), null, null, false, now).State);
            Assert.Equal("starting", DeploymentHealth.Assess(Row(DeploymentStatus.Running), Pulse(null, 0), null, false, now).State);
            var warm = DeploymentHealth.Assess(Row(DeploymentStatus.Running, TimeInterval.OneDay, origin: new DeploymentOrigin { WarmupBars = 506 }), Pulse(now.AddHours(-20), 3, "basket"), null, false, now);
            Assert.Equal("warming", warm.State);
            Assert.Contains("103", warm.Title);
            Assert.Equal("paused", DeploymentHealth.Assess(Row(DeploymentStatus.Paused), null, null, false, now).State);
            Assert.Equal("disarmed", DeploymentHealth.Assess(Row(DeploymentStatus.Paused, mode: SessionMode.Live), Pulse(now.AddHours(-1), 3), null, false, now).State);
            Assert.Equal("stopped", DeploymentHealth.Assess(Row(DeploymentStatus.Stopped), null, null, false, now).State);
        }

        // ── deploy planner ───────────────────────────────────────────────────────

        private static AlphaCandidate Candidate(CandidateStatus status, TimeInterval interval, params string[] symbols) => new()
        {
            Id = "ac_test",
            StrategyId = "st_test",
            StrategyName = "Test strategy",
            StrategyVersion = 2,
            Hypothesis = "it works",
            Status = status,
            Score = status == CandidateStatus.Promising ? 71 : 31,
            Interval = interval,
            TestSymbols = symbols.ToList(),
            Evaluation = new CandidateEvaluation
            {
                Primary = new RunMetrics { ReturnPct = 51.2, CagrPct = 9.1, Sharpe = 0.85, MaxDrawdownPct = 14.9, Trades = 25, ExposurePct = 40, WinRatePct = 52 },
                PrimaryStressed = new RunMetrics { ReturnPct = 40, Sharpe = 0.8 },
                OutOfSample = new RunMetrics { Sharpe = 1.6 },
                PerSymbol = symbols.Select((s, i) => new StudyRun { Symbol = s, Metrics = new RunMetrics { Sharpe = 0.9 - i * 0.1, ReturnPct = 50 - i, MaxDrawdownPct = 15, Trades = 25 } }).ToList(),
                FinishedUtc = DateTime.UtcNow.AddDays(-30),
                Findings = { "primary: 51.2% return" },
            },
        };

        private static DeployPlanInputs Inputs<T>(StrategyDescriptor d, AlphaCandidate? c, IReadOnlyList<DeploymentRow>? existing = null, StudioStrategy? studio = null) where T : TradingStrategy, new() => new()
        {
            Descriptor = d,
            Create = p => { var s = new T(); StrategyParams.Apply(s, new Dictionary<string, object?>(p, StringComparer.OrdinalIgnoreCase)); return s; },
            Candidate = c,
            Studio = studio,
            EvaluationBars = 1500,
            Existing = existing ?? Array.Empty<DeploymentRow>(),
            KrakenConfigured = true,
            NowUtc = DateTime.UtcNow,
        };

        [Fact]
        public void A_plan_from_a_candidate_uses_what_it_was_tested_on()
        {
            var d = Describe<TestBuyOnceStrategy>("studio:st_test@v2");
            var plan = DeployPlanner.Build(new DeployPlanRequest { CandidateId = "ac_test" }, Inputs<TestBuyOnceStrategy>(d, Candidate(CandidateStatus.Promising, TimeInterval.FourHour, "ETHUSDT", "BTCUSDT")));

            Assert.Equal("studio:st_test@v2", plan.Request.StrategyClass);    // pinned
            Assert.Equal("FourHour", plan.Request.Interval);                   // not the old 1h default
            Assert.Equal("ETHUSDT", plan.Request.Symbol);                     // the primary test symbol
            Assert.Equal("ETHUSDT", plan.Request.Parameters["TradeSymbol"]);
            Assert.Equal(0.0026m, plan.Request.FeeFraction);                  // Kraken, where it would trade
            Assert.Equal("binance", plan.Costs.EvaluatedPreset);
            Assert.Equal("Paper", plan.Request.Mode);
            Assert.Equal("NextOpen", plan.Request.Execution);                  // paper fills as the lab tested
            Assert.Equal("single", plan.Shape);
            Assert.Equal("alpha", plan.Request.Origin.Source);
            Assert.Equal("ac_test", plan.Request.Origin.CandidateId);
            Assert.Equal(60, plan.Request.Origin.BarsPerTrade);               // 1500 bars / 25 trades
            Assert.Equal(52, plan.Request.Origin.WarmupBars);                 // SlowLength 50 + 2
            Assert.Equal(2, plan.SymbolChoices.Count);
            Assert.True(plan.SymbolChoices[0].Primary);
            Assert.Contains(plan.Checks, c => c.Id == "lab" && c.Level == "pass");
            Assert.Contains(plan.Checks, c => c.Id == "fees" && c.Level == "info");
            Assert.NotNull(plan.Expect.TradesPerMonth);
            Assert.True(plan.Modes.Single(m => m.Mode == "Live").Allowed);

            // Live caps follow the capital: one order up to the capital, 5 % daily loss.
            var live = DeployPlanner.Build(new DeployPlanRequest { CandidateId = "ac_test", Mode = "Live", Capital = 2_000m },
                Inputs<TestBuyOnceStrategy>(d, Candidate(CandidateStatus.Promising, TimeInterval.FourHour, "ETHUSDT")));
            Assert.Equal(2_000m, live.Request.MaxPositionQuoteUsd);
            Assert.Equal(100m, live.Request.MaxDailyLossUsd);
            Assert.Equal(30, live.Request.MaxOrdersPerHour);
            Assert.Equal(new List<string> { "ETHUSDT" }, live.Request.AllowedSymbols);
            Assert.Equal("SignalClose", live.Request.Execution);
        }

        [Fact]
        public void A_plan_warns_when_the_settings_leave_what_was_tested()
        {
            var d = Describe<TestBuyOnceStrategy>("studio:st_test@v2");
            var cand = Candidate(CandidateStatus.Rejected, TimeInterval.OneDay, "GC=F");
            var running = new DeploymentRow
            {
                Id = "dup12345678", StrategyClass = "studio:st_test", Mode = SessionMode.Paper, Status = DeploymentStatus.Running, CreatedUtc = DateTime.UtcNow,
                EquityInitial = 1, EquityCurrent = 1,
                Config = new DeploymentConfig { StrategyClass = "studio:st_test", Symbol = "SPY", Interval = TimeInterval.OneHour, Mode = SessionMode.Paper },
            };
            var plan = DeployPlanner.Build(new DeployPlanRequest { CandidateId = "ac_test", Interval = "OneHour", Symbol = "SPY", Mode = "Live" },
                Inputs<TestBuyOnceStrategy>(d, cand, new[] { running }));

            Assert.Equal("blocked", plan.Verdict);                              // Live on an equity
            Assert.Contains(plan.Checks, c => c.Id == "live" && c.Level == "block");
            Assert.Contains(plan.Checks, c => c.Id == "lab" && c.Level == "warn");
            Assert.Contains(plan.Checks, c => c.Id == "interval" && c.Level == "warn");
            Assert.Contains(plan.Checks, c => c.Id == "symbol" && c.Level == "warn");
            Assert.Contains(plan.Checks, c => c.Id == "duplicate" && c.Level == "warn");
            Assert.False(plan.Modes.Single(m => m.Mode == "Live").Allowed);
            Assert.Equal("us-broker", plan.Costs.Preset);
        }

        [Fact]
        public void A_basket_plan_loads_enough_history_and_fills_at_the_next_open()
        {
            var d = Describe<TestEtfBasketStrategy>("studio:st_etf@v1");
            var plan = DeployPlanner.Build(new DeployPlanRequest { StrategyClass = "studio:st_etf@v1" }, Inputs<TestEtfBasketStrategy>(d, null,
                studio: new StudioStrategy { Id = "st_etf", Name = "ETF basket", DefaultSymbol = "SPY", DefaultInterval = TimeInterval.OneDay, Version = 1 }));

            Assert.Equal("basket", plan.Shape);
            Assert.Equal(new[] { "SPY", "QQQ", "IWM", "BTCUSDT" }, plan.Basket);
            Assert.Equal("SPY", plan.Request.Symbol);
            Assert.Equal("OneDay", plan.Request.Interval);                    // the Studio default, not 1h
            Assert.Equal("NextOpen", plan.Request.Execution);
            Assert.Equal(506, plan.Expect.WarmupBars);
            Assert.Equal(556, plan.Request.HistoryBars);                       // not the 400 it would have waited on
            Assert.Equal("mixed", plan.AssetClass);
            Assert.False(plan.Modes.Single(m => m.Mode == "Live").Allowed);
            Assert.Contains(plan.Checks, c => c.Id == "basket");
            Assert.Contains(plan.Checks, c => c.Id == "lab" && c.Level == "warn");   // never evaluated
            Assert.Equal("caution", plan.Verdict);
        }

        [Fact]
        public void The_catalog_ranks_evidence_first()
        {
            var registry = new StrategyRegistry();
            registry.RegisterDynamic(Describe<TestBuyOnceStrategy>("studio:st_test"));
            registry.RegisterDynamic(Describe<TestBasketStrategy>("studio:st_basket"));
            var promising = Candidate(CandidateStatus.Promising, TimeInterval.FourHour, "ETHUSDT");
            var rejected = Candidate(CandidateStatus.Rejected, TimeInterval.OneDay, "BTCUSDT");
            rejected.Id = "ac_rejected";
            rejected.StrategyVersion = 1;
            var studios = new List<StudioStrategy>
            {
                new() { Id = "st_test", Name = "Test strategy", Version = 3, Status = StudioStatus.Valid },
                new() { Id = "st_basket", Name = "Basket", Version = 1, Status = StudioStatus.Valid, DefaultInterval = TimeInterval.OneDay },
            };

            var rows = DeployCatalog.Build(new[] { rejected, promising }, studios, registry);

            Assert.Equal("ac_test", rows[0].Key);
            Assert.True(rows[0].Recommended);
            Assert.Equal("studio:st_test@v2", rows[0].StrategyClass);
            Assert.Equal(3, rows[0].CurrentVersion);                           // edited since tested
            Assert.Equal("basket", rows.Single(r => r.Key == "studio:st_basket").Shape);
            Assert.Equal("ac_rejected", rows[^1].Key);
        }
    }
}
