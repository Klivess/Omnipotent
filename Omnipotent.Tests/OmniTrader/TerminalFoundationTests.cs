using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Strategy;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Bar clock, aggregation, gap-aware conditional fills, next-open execution, the analysis block
    /// and provider parsing — the foundations everything else in the terminal reads from.
    /// </summary>
    public class TerminalFoundationTests
    {
        private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

        // ── bar clock ─────────────────────────────────────────────────────────────

        [Fact]
        public void BarOpen_Aligns_To_Venue_Clock()
        {
            var t = U(2026, 10, 14, 13, 47); // a Wednesday
            Assert.Equal(U(2026, 10, 14, 13), CandleClock.BarOpen(t, TimeInterval.OneHour));
            Assert.Equal(U(2026, 10, 14, 12), CandleClock.BarOpen(t, TimeInterval.FourHour));
            Assert.Equal(U(2026, 10, 14, 12), CandleClock.BarOpen(t, TimeInterval.TwoHour));
            Assert.Equal(U(2026, 10, 14), CandleClock.BarOpen(t, TimeInterval.OneDay));
            Assert.Equal(U(2026, 10, 12), CandleClock.BarOpen(t, TimeInterval.OneWeek)); // Monday
            Assert.Equal(U(2026, 10, 1), CandleClock.BarOpen(t, TimeInterval.OneMonth));
        }

        [Fact]
        public void LastClosedOpen_Is_The_Bar_Before_The_Forming_One()
        {
            var now = U(2026, 10, 14, 13, 5);
            Assert.Equal(U(2026, 10, 14, 12), CandleClock.LastClosedOpen(now, TimeInterval.OneHour));
            Assert.Equal(U(2026, 10, 13), CandleClock.LastClosedOpen(now, TimeInterval.OneDay));
            Assert.Equal(U(2026, 9, 1), CandleClock.LastClosedOpen(now, TimeInterval.OneMonth));
            Assert.True(CandleClock.IsClosed(U(2026, 10, 14, 12), TimeInterval.OneHour, now));
            Assert.False(CandleClock.IsClosed(U(2026, 10, 14, 13), TimeInterval.OneHour, now));
        }

        [Theory]
        [InlineData("1h", TimeInterval.OneHour)]
        [InlineData("4H", TimeInterval.FourHour)]
        [InlineData("1D", TimeInterval.OneDay)]
        [InlineData("W", TimeInterval.OneWeek)]
        [InlineData("1M", TimeInterval.OneMonth)]
        [InlineData("OneHour", TimeInterval.OneHour)]
        [InlineData("15", TimeInterval.FifteenMinute)]
        public void Interval_Parse_Accepts_Every_Spelling(string raw, TimeInterval expected)
            => Assert.Equal(expected, CandleClock.Parse(raw));

        [Fact]
        public void BarsPerYear_Depends_On_Calendar()
        {
            Assert.Equal(365, CandleClock.BarsPerYear(TimeInterval.OneDay, continuousMarket: true));
            Assert.Equal(252, CandleClock.BarsPerYear(TimeInterval.OneDay, continuousMarket: false));
            Assert.Equal(365 * 24, CandleClock.BarsPerYear(TimeInterval.OneHour, true), 6);
            Assert.Equal(252 * 6.5, CandleClock.BarsPerYear(TimeInterval.OneHour, false), 6);
        }

        // ── aggregation ───────────────────────────────────────────────────────────

        [Fact]
        public void Aggregate_Continuous_Hourly_Into_FourHour()
        {
            var bars = Enumerable.Range(0, 8).Select(i => new OHLCCandle(U(2026, 1, 1, i), 100 + i, 110 + i, 90 + i, 101 + i, 10)).ToList();
            var four = CandleAggregator.Aggregate(bars, TimeInterval.FourHour, continuousMarket: true, TimeInterval.OneHour);
            Assert.Equal(2, four.Count);
            Assert.Equal(U(2026, 1, 1, 0), four[0].Timestamp);
            Assert.Equal(100m, four[0].Open);
            Assert.Equal(113m, four[0].High);
            Assert.Equal(90m, four[0].Low);
            Assert.Equal(104m, four[0].Close);
            Assert.Equal(40m, four[0].Volume);
        }

        [Fact]
        public void Aggregate_Session_Groups_By_Trading_Day()
        {
            // 7 hourly bars from 13:30 UTC (a US session) → 4h chunks of 4 + 3 bars.
            var bars = Enumerable.Range(0, 7).Select(i => new OHLCCandle(U(2026, 3, 2, 13, 30).AddHours(i), 10, 11, 9, 10, 1)).ToList();
            var four = CandleAggregator.Aggregate(bars, TimeInterval.FourHour, continuousMarket: false, TimeInterval.OneHour);
            Assert.Equal(2, four.Count);
            Assert.Equal(U(2026, 3, 2, 13, 30), four[0].Timestamp);
            Assert.Equal(4m, four[0].Volume);
            Assert.Equal(3m, four[1].Volume);
        }

        // ── fills ─────────────────────────────────────────────────────────────────

        private static OHLCCandle Bar(decimal o, decimal h, decimal l, decimal c) => new(U(2026, 1, 1), o, h, l, c, 1);

        private static OrderRequest Req(OrderSide side, OrderType type, decimal? limit = null, decimal? stop = null) => new()
        {
            IntentId = Guid.NewGuid().ToString("N"), Side = side, Type = type, Symbol = "X", Qty = 1,
            LimitPrice = limit, StopPrice = stop,
        };

        [Fact]
        public void Sell_Stop_Gapped_Through_Fills_At_The_Open()
        {
            var fill = SimulatedOrderRouter.TriggerPrice(Req(OrderSide.Sell, OrderType.StopLoss, stop: 95), Bar(90, 92, 85, 88));
            Assert.Equal(90m, fill!.Value.Price);
            Assert.Equal("stop", fill.Value.Reason);
        }

        [Fact]
        public void Sell_Stop_Touched_Intrabar_Fills_At_The_Stop()
        {
            var fill = SimulatedOrderRouter.TriggerPrice(Req(OrderSide.Sell, OrderType.StopLoss, stop: 95), Bar(100, 101, 94, 99));
            Assert.Equal(95m, fill!.Value.Price);
        }

        [Fact]
        public void Buy_Limit_Opened_Below_Fills_At_The_Better_Open()
        {
            var fill = SimulatedOrderRouter.TriggerPrice(Req(OrderSide.Buy, OrderType.Limit, limit: 100), Bar(97, 99, 96, 98));
            Assert.Equal(97m, fill!.Value.Price);
        }

        [Fact]
        public void Untouched_Order_Does_Not_Trigger()
        {
            Assert.Null(SimulatedOrderRouter.TriggerPrice(Req(OrderSide.Sell, OrderType.TakeProfit, limit: 120), Bar(100, 110, 95, 105)));
        }

        // ── execution timing ──────────────────────────────────────────────────────

        /// <summary>Buys on the first bar, sells on the third.</summary>
        private sealed class BuyThenSell : TradingStrategy
        {
            private int bar;
            public override StrategySymbols DeclareSymbols() => StrategySymbols.Of("TESTUSDT");
            public override async Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
            {
                bar++;
                if (bar == 1)
                    await SubmitOrder(new OrderRequest { IntentId = "b", Side = OrderSide.Buy, Type = OrderType.Market, Symbol = "TESTUSDT", Qty = 10 }, ct);
                if (bar == 3)
                    await SubmitOrder(new OrderRequest { IntentId = "s", Side = OrderSide.Sell, Type = OrderType.Market, Symbol = "TESTUSDT", Qty = 10 }, ct);
            }
        }

        private static List<OHLCCandle> Series(params (decimal O, decimal C)[] bars)
            => bars.Select((b, i) => new OHLCCandle(U(2026, 1, 1).AddDays(i), b.O, Math.Max(b.O, b.C) + 1, Math.Min(b.O, b.C) - 1, b.C, 100)).ToList();

        private static BacktestConfig Cfg(ExecutionTiming timing) => new()
        {
            StrategyClass = "BuyThenSell", Coin = "TESTUSDT", Currency = "", Interval = TimeInterval.OneDay,
            CandleCount = 5, FeeFraction = 0, SlippageFraction = 0, InitialQuoteBalance = 10_000, Execution = timing,
        };

        [Fact]
        public async Task SignalClose_Fills_At_The_Signal_Bar_Close()
        {
            var candles = Series((100, 101), (105, 106), (110, 111), (120, 121), (130, 131));
            var res = await new BacktestSession(new BuyThenSell(), candles, Cfg(ExecutionTiming.SignalClose)).RunAsync();
            var trade = Assert.Single(res.Trades);
            Assert.Equal(101m, trade.EntryPrice);
            Assert.Equal(111m, trade.ExitPrice);
            Assert.Equal("signal", trade.ExitReason);
        }

        [Fact]
        public async Task NextOpen_Fills_At_The_Following_Bar_Open()
        {
            var candles = Series((100, 101), (105, 106), (110, 111), (120, 121), (130, 131));
            var res = await new BacktestSession(new BuyThenSell(), candles, Cfg(ExecutionTiming.NextOpen)).RunAsync();
            var trade = Assert.Single(res.Trades);
            Assert.Equal(105m, trade.EntryPrice);
            Assert.Equal(120m, trade.ExitPrice);
            Assert.Equal(U(2026, 1, 2), trade.EntryTime);
            Assert.Equal(ExecutionTiming.NextOpen, res.Execution);
            Assert.True(trade.MfePct > 0);
        }

        /// <summary>Throws on every bar — must fail the run, not report a quiet zero.</summary>
        private sealed class AlwaysThrows : TradingStrategy
        {
            public override StrategySymbols DeclareSymbols() => StrategySymbols.Of("TESTUSDT");
            public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct) => throw new InvalidOperationException("boom");
        }

        [Fact]
        public async Task A_Strategy_That_Keeps_Throwing_Fails_The_Run()
        {
            var candles = Enumerable.Range(0, 60).Select(i => new OHLCCandle(U(2026, 1, 1).AddDays(i), 100, 101, 99, 100, 1)).ToList();
            var ex = await Assert.ThrowsAsync<StrategyRuntimeException>(() =>
                new BacktestSession(new AlwaysThrows(), candles, Cfg(ExecutionTiming.SignalClose)).RunAsync());
            Assert.Contains("boom", ex.Message);
        }

        // ── analysis ──────────────────────────────────────────────────────────────

        private static List<EquityPoint> Curve(params double[] equity) => equity.Select((e, i) => new EquityPoint
        {
            Ts = U(2025, 1, 1).AddDays(i * 7), Equity = (decimal)e, MarkPrice = 0, QuoteBalance = (decimal)e, BaseBalance = 0
        }).ToList();

        [Fact]
        public void Analysis_Computes_Drawdown_Episode_And_Recovery()
        {
            var a = BacktestAnalyzer.Analyze(Curve(100, 110, 99, 88, 105, 121, 130), new List<TradeRecord>(),
                new List<OHLCCandle>(), 52, continuousMarket: true, fees: 0);
            Assert.Equal(20.0, a.MaxDrawdownPct, 6);            // 110 → 88
            Assert.Equal(1, a.DrawdownEpisodes);
            Assert.Equal(U(2025, 1, 8), a.MaxDrawdownPeakUtc);
            Assert.Equal(U(2025, 1, 22), a.MaxDrawdownTroughUtc);
            Assert.Equal(U(2025, 2, 5), a.MaxDrawdownRecoveryUtc);
            Assert.Equal(30.0, a.TotalReturnPct, 6);
            Assert.True(a.Sharpe > 0);
            Assert.Equal(7, a.Drawdown.Count);
        }

        [Fact]
        public void Analysis_Monthly_Grid_Compounds_To_The_Year()
        {
            var points = new List<EquityPoint>();
            double e = 100;
            for (int d = 0; d < 90; d++)
            {
                e *= 1.001;
                points.Add(new EquityPoint { Ts = U(2025, 1, 1).AddDays(d), Equity = (decimal)e, MarkPrice = 0, QuoteBalance = 0, BaseBalance = 0 });
            }
            var a = BacktestAnalyzer.Analyze(points, new List<TradeRecord>(), new List<OHLCCandle>(), 365, true, 0);
            Assert.True(a.Monthly.ContainsKey(2025));
            var row = a.Monthly[2025];
            double compounded = (row[1] / 100 + 1) * (row[2] / 100 + 1) * (row[3] / 100 + 1) - 1;
            Assert.Equal(row[13] / 100, compounded, 9);
        }

        [Fact]
        public void Analysis_Against_Itself_Has_Beta_One()
        {
            var curve = Curve(100, 103, 101, 106, 104, 110, 108, 115);
            var bench = curve.Select(p => new OHLCCandle(p.Ts, p.Equity, p.Equity, p.Equity, p.Equity, 1)).ToList();
            var a = BacktestAnalyzer.Analyze(curve, new List<TradeRecord>(), bench, 52, true, 0);
            Assert.Equal(1.0, a.Beta, 6);
            Assert.Equal(1.0, a.Correlation, 6);
            Assert.Equal(0.0, a.TrackingErrorPct, 6);
        }

        [Fact]
        public void Profit_Factor_Is_Finite_With_No_Losers()
        {
            var trades = new List<TradeRecord>
            {
                new() { EntryTime = U(2025, 1, 1), ExitTime = U(2025, 1, 2), EntryPrice = 10, ExitPrice = 11, Qty = 1, IsShort = false, Fees = 0 }
            };
            Assert.Equal(999m, BacktestMetrics.ProfitFactor(trades));
        }

        // ── provider parsing ──────────────────────────────────────────────────────

        [Fact]
        public void Binance_Socket_Bars_Are_Stamped_With_Open_Time()
        {
            string json = """{"e":"kline","k":{"t":1791900000000,"T":1791903599999,"o":"1","h":"2","l":"0.5","c":"1.5","v":"10","x":true}}""";
            var bar = BinanceMarketDataProvider.TryParseClosedCandle(json);
            Assert.NotNull(bar);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791900000000).UtcDateTime, bar!.Value.Timestamp);
            Assert.Null(BinanceMarketDataProvider.TryParseClosedCandle(json.Replace("\"x\":true", "\"x\":false")));
        }

        [Fact]
        public void Yahoo_Chart_Skips_Null_Bars_And_Reads_Session()
        {
            string body = """
            {"chart":{"result":[{"meta":{"symbol":"AAPL","currency":"USD","instrumentType":"EQUITY","regularMarketPrice":344.6,"chartPreviousClose":340.9,
              "currentTradingPeriod":{"regular":{"start":1791898200,"end":1791921600}}},
              "timestamp":[1791811800,1791898200,1791900000],
              "indicators":{"quote":[{"open":[330,null,341],"high":[335,null,346],"low":[329,null,340],"close":[333,null,344],"volume":[10,null,12]}]}}]}}
            """;
            var chart = YahooMarketDataProvider.ParseChart(body)!;
            Assert.Equal(2, chart.Bars.Count);
            Assert.Equal(344.6m, chart.RegularMarketPrice);
            Assert.False(chart.IsContinuous);

            var duringSession = DateTimeOffset.FromUnixTimeSeconds(1791910000).UtcDateTime;
            var afterClose = DateTimeOffset.FromUnixTimeSeconds(1791925000).UtcDateTime;
            var daily = chart.Bars[^1];
            Assert.False(YahooMarketDataProvider.IsBarComplete(daily, TimeInterval.OneDay, chart, duringSession));
            Assert.True(YahooMarketDataProvider.IsBarComplete(daily, TimeInterval.OneDay, chart, afterClose));
            Assert.Equal("CLOSED", chart.MarketStateNow(afterClose));
            Assert.Equal("REGULAR", chart.MarketStateNow(duringSession));
        }

        [Theory]
        [InlineData("BTCUSDT", false)]
        [InlineData("ETHGBP", false)]
        [InlineData("AAPL", true)]
        [InlineData("VOD.L", true)]
        [InlineData("^FTSE", true)]
        [InlineData("GBPUSD=X", true)]
        [InlineData("GC=F", true)]
        [InlineData("BTC-USD", true)]
        public void Feed_Routing_By_Symbol_Shape(string symbol, bool equity)
            => Assert.Equal(equity, MarketDataRouter.UsesEquityFeed(symbol, Omnipotent.Services.OmniTrader.Venues.AssetClass.Unknown));

        [Fact]
        public void Coinbase_Product_Mapping()
        {
            Assert.Equal("BTC-USD", CoinbaseMarketDataProvider.ToProduct("BTCUSDT"));
            Assert.Equal("ETH-GBP", CoinbaseMarketDataProvider.ToProduct("ETHGBP"));
            Assert.Equal("SOL-USD", CoinbaseMarketDataProvider.ToProduct("SOL/USDC"));
        }
    }
}
