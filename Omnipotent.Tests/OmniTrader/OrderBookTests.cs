using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Data.Crypto;

namespace Omnipotent.Tests.OmniTrader
{
    public class OrderBookTests
    {
        /// <summary>A BTC-like book: 0.01 tick, best bid 83,435.99 / best ask 83,436.00, dust on most ticks.</summary>
        internal static BookSnapshot Btc(int levels = 100, int limit = 100, int seed = 3)
        {
            var rng = new Random(seed);
            var bids = new List<BookLevel>();
            var asks = new List<BookLevel>();
            decimal bid = 83_435.99m, ask = 83_436.00m;
            for (int i = 0; i < levels; i++)
            {
                decimal size = i % 17 == 0 ? 1.25m + i / 100m : Math.Round(0.0001m + (decimal)rng.NextDouble() * 0.05m, 4);
                bids.Add(new BookLevel(bid, size));
                asks.Add(new BookLevel(ask, size * 1.1m));
                bid -= 0.01m * (1 + i % 3);
                ask += 0.01m * (1 + i % 2);
            }
            return new BookSnapshot { Symbol = "BTCUSDT", Bids = bids, Asks = asks, Limit = limit };
        }

        /// <summary>The payload as the route serialises it, read back with exact decimals.</summary>
        private static JObject Parse(object view)
        {
            string text = JsonConvert.SerializeObject(view, TerminalRoutes.JsonSettings);
            using var reader = new JsonTextReader(new StringReader(text)) { FloatParseHandling = FloatParseHandling.Decimal };
            return JObject.Load(reader);
        }

        [Fact]
        public void Grouping_Is_Tick_Exact_And_Never_Crosses_The_Spread()
        {
            var book = Btc();
            var bids = OrderBook.Group(book.Bids, 1m, bids: true);
            var asks = OrderBook.Group(book.Asks, 1m, bids: false);
            // 83,435.99 floors to 83,435 and 83,436.00 ceils to 83,436: no bucket on both sides.
            Assert.Equal(83_435m, bids[0].Price);
            Assert.Equal(83_436m, asks[0].Price);
            Assert.True(bids[0].Price < asks[0].Price);
            Assert.Equal(book.Bids.Sum(l => l.Size), bids.Sum(l => l.Size));
            Assert.Equal(book.Asks.Sum(l => l.Size), asks.Sum(l => l.Size));
            Assert.All(bids.Zip(bids.Skip(1)), p => Assert.True(p.First.Price > p.Second.Price));
            Assert.All(asks.Zip(asks.Skip(1)), p => Assert.True(p.First.Price < p.Second.Price));

            // A 0.05 grouping lands on exact multiples: decimal, not double, arithmetic.
            var fine = OrderBook.Group(book.Bids, 0.05m, bids: true);
            Assert.All(fine, l => Assert.Equal(0m, l.Price % 0.05m));
            Assert.Equal(83_435.95m, fine[0].Price);
        }

        [Fact]
        public void A_Truncated_Side_Drops_Its_Partial_Last_Bucket()
        {
            var cut = Btc(levels: 100, limit: 100);
            var whole = Btc(levels: 100, limit: 1000);
            int cutRows = OrderBook.Ladder(cut, 1m, 0.01m, bids: true).Count;
            int wholeRows = OrderBook.Ladder(whole, 1m, 0.01m, bids: true).Count;
            Assert.Equal(wholeRows - 1, cutRows);
            // At the raw tick nothing is partial.
            Assert.Equal(100, OrderBook.Ladder(cut, 0.01m, 0.01m, bids: true).Count);
            Assert.True(OrderBook.Fills(cut, 0.01m, 0.01m, 20));
            Assert.False(OrderBook.Fills(cut, 10m, 0.01m, 20));
            Assert.True(OrderBook.Fills(whole, 10m, 0.01m, 20)); // the whole book: a bigger snapshot adds nothing
        }

        [Fact]
        public void Group_Options_And_Default_Follow_Tick_And_Price()
        {
            var btc = OrderBook.GroupOptions(0.01m, 83_436m);
            Assert.Equal(new[] { 0.01m, 0.05m, 0.1m, 0.5m, 1m, 5m, 10m, 50m, 100m }, btc);
            Assert.Equal(0.5m, OrderBook.DefaultGroup(btc, 0.01m, 83_436m));
            var eth = OrderBook.GroupOptions(0.01m, 2_512m);
            Assert.Equal(0.01m, OrderBook.DefaultGroup(eth, 0.01m, 2_512m));
            Assert.Equal(0.00001m, OrderBook.GroupOptions(0.00001m, 0.15m)[0]);
            Assert.Equal(0.05m, OrderBook.Snap(0.049m, 0.01m));
            Assert.Equal(0.01m, OrderBook.Snap(0.001m, 0.01m));
            Assert.Equal(0.01m, OrderBook.InferTick(Btc()));
        }

        [Fact]
        public void View_Reports_Real_Spread_Cumulative_Rows_And_Honest_Bands()
        {
            var book = Btc(levels: 100, limit: 100);
            var json = Parse(OrderBook.View(book, null, new BookFilters { Tick = 0.01m, Step = 0.00001m }, 0.01m, 15, "Binance spot"));

            Assert.Equal(83_435.99m, json.Value<decimal>("BestBid"));
            Assert.Equal(83_436.00m, json.Value<decimal>("BestAsk"));
            Assert.Equal(0.01m, json.Value<decimal>("Spread"));
            Assert.InRange(json.Value<double>("SpreadBps"), 0.0011, 0.0013);
            var bids = (JArray)json["Bids"]!;
            Assert.Equal(15, bids.Count);
            // [price, size, cumulative]: cumulative is the running sum, distinct prices at the tick.
            decimal running = 0;
            foreach (var row in bids)
            {
                running += row[1]!.Value<decimal>();
                Assert.Equal(running, row[2]!.Value<decimal>());
            }
            Assert.Equal(15, bids.Select(r => r[0]!.Value<decimal>()).Distinct().Count());

            // 100 levels of a dense book reach a few dollars, nowhere near ±1%: the band says so.
            var bands = (JArray)json["Bands"]!;
            var one = bands.First(b => b.Value<double>("Pct") == 1);
            Assert.False(one.Value<bool>("BidCovered"));
            Assert.False(one.Value<bool>("AskCovered"));
            Assert.True(json["Coverage"]!.Value<double>("BidPct") < 0.1);

            // A deep snapshot that does reach the band covers it.
            var deep = new BookSnapshot
            {
                Symbol = "BTCUSDT", Limit = 5000,
                Bids = book.Bids.Append(new BookLevel(81_000m, 3m)).ToList(),
                Asks = book.Asks.Append(new BookLevel(86_000m, 2m)).ToList()
            };
            var withDeep = Parse(OrderBook.View(book, deep, null, null, 20, "Binance spot"));
            var deepOne = ((JArray)withDeep["Bands"]!).First(b => b.Value<double>("Pct") == 1);
            Assert.True(deepOne.Value<bool>("BidCovered"));
            Assert.True(deepOne.Value<bool>("AskCovered"));
            Assert.Equal(0.5m, withDeep.Value<decimal>("Group")); // default for an ~83k price
        }

        [Fact]
        public void Binance_Levels_Parse_As_Exact_Decimals()
        {
            var side = JArray.Parse("""[["83435.99000000","0.00010000"],["83435.98000000","1.10910000"],["0","5"],["bad","1"]]""");
            var levels = CryptoData.Levels(side);
            Assert.Equal(2, levels.Count);
            Assert.Equal(83_435.99m, levels[0].Price);
            Assert.Equal(1.1091m, levels[1].Size);
        }
    }
}
