using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Technicals;

namespace Omnipotent.Tests.OmniTrader
{
    public class TerminalAnalyticsTests
    {
        private static List<OHLCCandle> Daily(IEnumerable<double> closes, DateTime? start = null)
        {
            var t = start ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return closes.Select((c, i) => new OHLCCandle(t.AddDays(i), (decimal)c, (decimal)c * 1.01m, (decimal)c * 0.99m, (decimal)c, 100)).ToList();
        }

        [Fact]
        public void Pearson_And_Beta_Recover_Known_Relationships()
        {
            var rng = new Random(3);
            var a = Enumerable.Range(0, 500).Select(_ => rng.NextDouble() - 0.5).ToArray();
            var twice = a.Select(x => 2 * x).ToArray();
            var inverse = a.Select(x => -x).ToArray();
            Assert.Equal(1, TerminalRoutes.Pearson(a, twice), 6);
            Assert.Equal(-1, TerminalRoutes.Pearson(a, inverse), 6);
            Assert.Equal(2, TerminalRoutes.Beta(twice, a), 6);
        }

        [Fact]
        public void Compare_Series_Align_Without_Looking_Ahead()
        {
            var main = Daily(new double[] { 1, 2, 3, 4, 5 });
            // The other symbol only trades on days 1 and 3.
            var other = new List<OHLCCandle>
            {
                new(main[1].Timestamp, 10, 10, 10, 10, 1),
                new(main[3].Timestamp, 30, 30, 30, 30, 1),
            };
            var aligned = TerminalRoutes.AlignCloses(main, other);
            Assert.Equal(new double?[] { null, 10, 10, 30, 30 }, aligned);
        }

        [Fact]
        public void Drawdowns_Find_Depth_And_Recovery()
        {
            // 100 → 50 (−50%) → back to 100 → 120 → 90 (−25%, ongoing)
            var closes = new double[] { 100, 80, 50, 70, 100, 120, 90 };
            var bars = BarSeries.From(Daily(closes));
            var dds = JArray.FromObject(TerminalRoutes.Drawdowns(bars, 5));
            Assert.Equal(2, dds.Count);
            Assert.Equal(-50, dds[0]["DepthPct"]!.Value<double>(), 3);
            Assert.False(dds[0]["Ongoing"]!.Value<bool>());
            Assert.Equal(-25, dds[1]["DepthPct"]!.Value<double>(), 3);
            Assert.True(dds[1]["Ongoing"]!.Value<bool>());
        }

        [Fact]
        public void Seasonality_Buckets_Months_And_Weekdays()
        {
            // Two years of daily bars rising 1% every January day and flat otherwise.
            var start = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var closes = new List<double>();
            double price = 100;
            for (int i = 0; i < 730; i++)
            {
                if (start.AddDays(i).Month == 1) price *= 1.01;
                closes.Add(price);
            }
            var json = JObject.FromObject(TerminalRoutes.Seasonality("TEST", Daily(closes, start), null));
            var byMonth = (JArray)json["ByMonth"]!;
            double jan = byMonth[0]["Stats"]!["Mean"]!.Value<double>();
            double feb = byMonth[1]["Stats"]!["Mean"]!.Value<double>();
            Assert.True(jan > 20, $"January mean {jan}");
            Assert.Equal(0, feb, 6);
            Assert.Equal(7, ((JArray)json["ByWeekday"]!).Count);
            Assert.Equal(2, ((JArray)json["Grid"]!).Count);
        }

        [Fact]
        public void Vol_Cone_Places_Current_Vol_In_Its_History()
        {
            var rng = new Random(5);
            double price = 100;
            var closes = new List<double>();
            for (int i = 0; i < 800; i++)
            {
                // Calm for most of the history, violent at the end.
                double sigma = i > 760 ? 0.08 : 0.01;
                price *= Math.Exp((rng.NextDouble() - 0.5) * 2 * sigma);
                closes.Add(price);
            }
            var cone = JArray.FromObject(TerminalRoutes.VolCone(BarSeries.From(Daily(closes), 365)));
            var w20 = cone.First(c => c["Window"]!.Value<int>() == 20);
            Assert.True(w20["Percentile"]!.Value<double>() >= 95, w20.ToString());
            Assert.True(w20["Current"]!.Value<double>() > w20["Median"]!.Value<double>());
        }

        [Fact]
        public void Non_Finite_Numbers_Serialise_As_Null()
        {
            string json = JsonConvert.SerializeObject(new { A = double.NaN, B = (double?)double.PositiveInfinity, C = 1.5, D = new[] { 1.0, double.NaN } },
                TerminalRoutes.JsonSettings);
            Assert.Equal("{\"A\":null,\"B\":null,\"C\":1.5,\"D\":[1.0,null]}", json);
        }
    }
}
