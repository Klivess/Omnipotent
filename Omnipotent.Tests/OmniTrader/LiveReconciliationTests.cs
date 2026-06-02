using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Live fill reconciliation: the ledger books fills into position/cash/realized PnL, and the
    /// Kraken parser turns a QueryOrders response into cumulative ExchangeFills. Both are pure, so
    /// the live execution accounting is covered without touching the network.
    /// </summary>
    public class LiveReconciliationTests
    {
        private static readonly DateTime T = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Ledger_Long_RoundTrip_Realizes_Profit()
        {
            var l = new LiveLedger(10_000m, 0m);
            l.ApplyFill(OrderSide.Buy, 1m, 100m, 0m, "X", T);
            Assert.Equal(9_900m, l.QuoteBalance);
            Assert.Equal(1m, l.BaseBalance);
            Assert.False(l.Position!.IsShort);

            decimal realized = l.ApplyFill(OrderSide.Sell, 1m, 110m, 0m, "X", T);
            Assert.Equal(10m, realized);
            Assert.Equal(10m, l.RealizedPnL);
            Assert.Equal(10_010m, l.QuoteBalance);
            Assert.Null(l.Position);
        }

        [Fact]
        public void Ledger_Short_RoundTrip_Realizes_Profit_When_Price_Falls()
        {
            var l = new LiveLedger(10_000m, 0m);
            l.ApplyFill(OrderSide.Sell, 1m, 100m, 0m, "X", T);
            Assert.Equal(-1m, l.BaseBalance);
            Assert.True(l.Position!.IsShort);

            decimal realized = l.ApplyFill(OrderSide.Buy, 1m, 90m, 0m, "X", T);
            Assert.Equal(10m, realized);
            Assert.Null(l.Position);
        }

        [Fact]
        public void Ledger_Averages_Entry_On_Adds_And_Realizes_On_Partial_Close()
        {
            var l = new LiveLedger(100_000m, 0m);
            l.ApplyFill(OrderSide.Buy, 1m, 100m, 0m, "X", T);
            l.ApplyFill(OrderSide.Buy, 1m, 200m, 0m, "X", T);
            Assert.Equal(150m, l.Position!.AveragePrice);
            Assert.Equal(2m, l.Position.Qty);

            decimal realized = l.ApplyFill(OrderSide.Sell, 1m, 180m, 0m, "X", T);
            Assert.Equal(30m, realized);             // (180-150)*1
            Assert.Equal(1m, l.Position!.Qty);       // half remains
            Assert.Equal(150m, l.Position.AveragePrice);
        }

        [Fact]
        public void Ledger_Realized_Is_Net_Of_Fee()
        {
            var l = new LiveLedger(10_000m, 0m);
            l.ApplyFill(OrderSide.Buy, 1m, 100m, 1m, "X", T);
            decimal realized = l.ApplyFill(OrderSide.Sell, 1m, 110m, 1m, "X", T);
            Assert.Equal(9m, realized);              // 10 gross - 1 fee
            Assert.Equal(2m, l.Fees);
        }

        [Fact]
        public void ParseFills_Maps_Kraken_QueryOrders_Response()
        {
            var response = JObject.Parse(@"{
              ""error"": [],
              ""result"": {
                ""OABC"": { ""status"":""closed"", ""vol"":""2.0"", ""vol_exec"":""2.0"", ""cost"":""200.0"", ""fee"":""0.32"", ""price"":""100.0"", ""descr"": { ""pair"":""XBTUSD"", ""type"":""buy"" } },
                ""ODEF"": { ""status"":""open"", ""vol"":""1.0"", ""vol_exec"":""0.5"", ""cost"":""45.0"", ""fee"":""0.10"", ""price"":""90"", ""descr"": { ""pair"":""ETHUSD"", ""type"":""sell"" } }
              }
            }");

            var fills = KrakenOrderRouter.ParseFills(response).OrderBy(f => f.ExchangeOrderId).ToList();
            Assert.Equal(2, fills.Count);

            var buy = fills[0];
            Assert.Equal("OABC", buy.ExchangeOrderId);
            Assert.Equal(OrderSide.Buy, buy.Side);
            Assert.Equal(2.0m, buy.CumulativeQty);
            Assert.Equal(100m, buy.AvgPrice);        // cost/vol_exec
            Assert.Equal(0.32m, buy.CumulativeFee);
            Assert.True(buy.Closed);
            Assert.Equal("XBTUSD", buy.Symbol);

            var sell = fills[1];
            Assert.Equal(OrderSide.Sell, sell.Side);
            Assert.Equal(0.5m, sell.CumulativeQty);
            Assert.Equal(90m, sell.AvgPrice);
            Assert.False(sell.Closed);               // still open / partial
        }

        [Fact]
        public void ParseFills_Empty_On_Missing_Result()
        {
            Assert.Empty(KrakenOrderRouter.ParseFills(JObject.Parse(@"{ ""error"": [] }")));
        }
    }
}
