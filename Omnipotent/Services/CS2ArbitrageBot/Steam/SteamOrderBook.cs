using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    /// <summary>One price level of a Steam order book, already converted to GBP pence.</summary>
    public readonly record struct SteamPriceLevel(int PricePence, int Quantity);

    /// <summary>
    /// A Steam Community Market order book for one market_hash_name, normalised to GBP pence.
    ///
    /// Steam's May 2026 market redesign removed the old <c>/market/listings/730/{name}/render</c> JSON
    /// endpoint (it now 302s to an HTML page), which is what the bot used for every price lookup. The
    /// replacement is the SPA's own query action <c>/market/orderbook?q=Load&amp;qp=[730,"name"]</c>; the
    /// legacy <c>itemordershistogram</c> endpoint still answers and is kept as a fallback.
    /// </summary>
    public sealed class SteamOrderBook
    {
        public string MarketHashName { get; set; } = "";
        /// <summary>Highest buy order (what an instant sale is filled at), GBP pence. 0 = no buy orders.</summary>
        public int HighestBuyOrderPence { get; set; }
        /// <summary>Lowest sell listing (what an instant purchase costs), GBP pence. 0 = no listings.</summary>
        public int LowestSellOrderPence { get; set; }
        public int BuyOrderCount { get; set; }
        public int SellOrderCount { get; set; }
        /// <summary>Buy side, best (highest) price first.</summary>
        public List<SteamPriceLevel> BuyLevels { get; set; } = new();
        /// <summary>Sell side, best (lowest) price first.</summary>
        public List<SteamPriceLevel> SellLevels { get; set; } = new();
        /// <summary>Steam ECurrencyCode the raw prices were quoted in (2 = GBP).</summary>
        public int SourceCurrency { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        /// <summary>"orderbook" (new SPA endpoint) or "histogram" (legacy endpoint).</summary>
        public string Source { get; set; } = "";

        public bool HasBuyOrders => HighestBuyOrderPence > 0;
        public bool HasSellOrders => LowestSellOrderPence > 0;
        public TimeSpan Age(DateTime nowUtc) => nowUtc - FetchedAtUtc;

        /// <summary>
        /// The buy-order price at which at least <paramref name="units"/> items could be sold into the book.
        /// A lone high order is often filled (or cancelled) long before an item clears its 7-day trade
        /// protection, so the decision price is taken a few units deep. Returns 0 when the book is too thin.
        /// </summary>
        public int PriceAtBuyDepth(int units)
        {
            if (units <= 1) return HighestBuyOrderPence;
            int cumulative = 0;
            foreach (var level in BuyLevels)
            {
                cumulative += Math.Max(0, level.Quantity);
                if (cumulative >= units) return level.PricePence;
            }
            // The compact book Steam returns is truncated; if the summary says there are enough
            // orders, the deepest level we were shown is a conservative stand-in.
            if (BuyOrderCount >= units && BuyLevels.Count > 0) return BuyLevels[^1].PricePence;
            return 0;
        }

        /// <summary>Total cost, in pence, of buying <paramref name="units"/> items off the sell side.</summary>
        public long CostToBuyUnits(int units)
        {
            long cost = 0;
            int remaining = units;
            foreach (var level in SellLevels)
            {
                int take = Math.Min(remaining, Math.Max(0, level.Quantity));
                cost += (long)take * level.PricePence;
                remaining -= take;
                if (remaining <= 0) return cost;
            }
            return remaining <= 0 ? cost : -1;
        }

        // ───────────────────────────── Parsing ─────────────────────────────

        /// <summary>
        /// Parses the new SPA response: <c>{"data":{"success":true,"data":{"amtMaxBuyOrder":579,
        /// "amtMinSellOrder":594,"eCurrency":2,"cBuyOrders":158839,"cSellOrders":1539,
        /// "rgCompactBuyOrders":[price,qty,price,qty,...],"rgCompactSellOrders":[...]}}}</c>.
        /// Also accepts the inner object on its own. Returns null when Steam reported failure.
        /// </summary>
        public static SteamOrderBook? ParseOrderbookResponse(string json, string marketHashName, Func<int, int, int?> toGbpPence, DateTime fetchedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JToken root = JToken.Parse(json);
            JToken? node = root;
            // Unwrap {"data":{"success":..,"data":{...}}} (query-action envelope + endpoint payload).
            for (int i = 0; i < 3 && node is JObject obj && obj["amtMaxBuyOrder"] == null && obj["cBuyOrders"] == null; i++)
            {
                if (obj["success"] != null && !IsTruthy(obj["success"])) return null;
                node = obj["data"];
            }
            if (node is not JObject book) return null;

            int currency = book.Value<int?>("eCurrency") ?? 2;
            int Convert(int minorUnits) => minorUnits <= 0 ? 0 : (toGbpPence(minorUnits, currency) ?? -1);

            var result = new SteamOrderBook
            {
                MarketHashName = marketHashName,
                SourceCurrency = currency,
                FetchedAtUtc = fetchedAtUtc,
                Source = "orderbook",
                BuyOrderCount = ReadInt(book["cBuyOrders"]),
                SellOrderCount = ReadInt(book["cSellOrders"]),
                HighestBuyOrderPence = Convert(ReadInt(book["amtMaxBuyOrder"])),
                LowestSellOrderPence = Convert(ReadInt(book["amtMinSellOrder"])),
                BuyLevels = ReadCompactLevels(book["rgCompactBuyOrders"], Convert),
                SellLevels = ReadCompactLevels(book["rgCompactSellOrders"], Convert),
            };
            if (result.HighestBuyOrderPence < 0 || result.LowestSellOrderPence < 0) return null; // unconvertible currency
            result.BuyLevels.Sort((a, b) => b.PricePence.CompareTo(a.PricePence));
            result.SellLevels.Sort((a, b) => a.PricePence.CompareTo(b.PricePence));
            if (result.HighestBuyOrderPence == 0 && result.BuyLevels.Count > 0) result.HighestBuyOrderPence = result.BuyLevels[0].PricePence;
            if (result.LowestSellOrderPence == 0 && result.SellLevels.Count > 0) result.LowestSellOrderPence = result.SellLevels[0].PricePence;
            return result;
        }

        /// <summary>
        /// Parses the legacy <c>itemordershistogram</c> response. Its graphs are
        /// <c>[[price (major units, float), CUMULATIVE quantity, label], ...]</c>; levels are de-accumulated.
        /// </summary>
        public static SteamOrderBook? ParseHistogramResponse(string json, string marketHashName, int requestedCurrency, Func<int, int, int?> toGbpPence, DateTime fetchedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (JToken.Parse(json) is not JObject obj) return null;
            if (!IsTruthy(obj["success"])) return null;

            int Convert(int minorUnits) => minorUnits <= 0 ? 0 : (toGbpPence(minorUnits, requestedCurrency) ?? -1);
            var result = new SteamOrderBook
            {
                MarketHashName = marketHashName,
                SourceCurrency = requestedCurrency,
                FetchedAtUtc = fetchedAtUtc,
                Source = "histogram",
                HighestBuyOrderPence = Convert(ReadInt(obj["highest_buy_order"])),
                LowestSellOrderPence = Convert(ReadInt(obj["lowest_sell_order"])),
                BuyLevels = ReadCumulativeGraph(obj["buy_order_graph"], Convert),
                SellLevels = ReadCumulativeGraph(obj["sell_order_graph"], Convert),
            };
            if (result.HighestBuyOrderPence < 0 || result.LowestSellOrderPence < 0) return null;
            result.BuyOrderCount = result.BuyLevels.Sum(l => l.Quantity);
            result.SellOrderCount = result.SellLevels.Sum(l => l.Quantity);
            // The summary strings carry the real totals ("12345 requests to buy at £1.00 or lower").
            int? buyTotal = ReadLeadingCount(obj.Value<string>("buy_order_summary"));
            int? sellTotal = ReadLeadingCount(obj.Value<string>("sell_order_summary"));
            if (buyTotal > result.BuyOrderCount) result.BuyOrderCount = buyTotal.Value;
            if (sellTotal > result.SellOrderCount) result.SellOrderCount = sellTotal.Value;
            return result;
        }

        private static List<SteamPriceLevel> ReadCompactLevels(JToken? token, Func<int, int> convert)
        {
            var levels = new List<SteamPriceLevel>();
            if (token is not JArray array) return levels;
            // The SPA drops a trailing unpaired value ("Incomplete order book data"); so do we.
            for (int i = 1; i < array.Count; i += 2)
            {
                int price = convert(ReadInt(array[i - 1]));
                int quantity = ReadInt(array[i]);
                if (price > 0 && quantity > 0) levels.Add(new SteamPriceLevel(price, quantity));
            }
            return levels;
        }

        private static List<SteamPriceLevel> ReadCumulativeGraph(JToken? token, Func<int, int> convert)
        {
            var levels = new List<SteamPriceLevel>();
            if (token is not JArray array) return levels;
            int previousCumulative = 0;
            foreach (var point in array.OfType<JArray>())
            {
                if (point.Count < 2) continue;
                if (!double.TryParse(point[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double major)) continue;
                int cumulative = ReadInt(point[1]);
                int quantity = Math.Max(0, cumulative - previousCumulative);
                previousCumulative = Math.Max(previousCumulative, cumulative);
                int price = convert((int)Math.Round(major * 100, MidpointRounding.AwayFromZero));
                if (price > 0 && quantity > 0) levels.Add(new SteamPriceLevel(price, quantity));
            }
            return levels;
        }

        private static int? ReadLeadingCount(string? html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(html, @"market_commodity_orders_header_promote"">\s*([\d,\.]+)\s*<");
            if (!match.Success) return null;
            string digits = new string(match.Groups[1].Value.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int value) ? value : null;
        }

        private static bool IsTruthy(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.Integer) return token.Value<long>() != 0;
            string s = token.ToString();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        internal static int ReadInt(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            if (token.Type == JTokenType.Integer) return (int)Math.Clamp(token.Value<long>(), int.MinValue, int.MaxValue);
            if (token.Type == JTokenType.Float) return (int)Math.Round(token.Value<double>());
            string s = token.ToString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return (int)Math.Round(d);
            return 0;
        }
    }
}
