using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.CS2ArbitrageBot.CSFloat
{
    /// <summary>
    /// A CSFloat listing as returned by <c>GET /api/v1/listings</c> (and nested in <c>/me/trades</c>).
    /// Parsed from a JObject rather than <c>dynamic</c>: the old dynamic code silently produced wrong
    /// types (e.g. float math into int properties) and threw from deep inside the scan loop.
    /// </summary>
    public sealed class CSFloatListing
    {
        public string Id { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string Type { get; set; } = "";
        public string State { get; set; } = "";
        /// <summary>Asking price in USD cents.</summary>
        public int PriceCents { get; set; }

        public string MarketHashName { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string WearName { get; set; } = "";
        public string ItemType { get; set; } = "";
        public double? FloatValue { get; set; }
        public int? PaintSeed { get; set; }
        public bool IsStatTrak { get; set; }
        public bool IsSouvenir { get; set; }
        public bool IsCommodity { get; set; }
        public string AssetId { get; set; } = "";
        public string DParam { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public int StickerCount { get; set; }

        /// <summary>CSFloat's market value for this market_hash_name, USD cents.</summary>
        public int? BasePriceCents { get; set; }
        /// <summary>CSFloat's value for this exact item (float-adjusted), USD cents.</summary>
        public int? PredictedPriceCents { get; set; }
        /// <summary>CSFloat's activity count behind the reference price (a liquidity proxy).</summary>
        public int? ReferenceQuantity { get; set; }

        public string SellerSteamId { get; set; } = "";
        public bool SellerOnline { get; set; }
        public bool SellerAway { get; set; }
        public int SellerTotalTrades { get; set; }
        public int SellerVerifiedTrades { get; set; }
        public int SellerFailedTrades { get; set; }
        public int SellerAvoidedTrades { get; set; }
        /// <summary>Seller's median time to send a trade, seconds (0 when unknown).</summary>
        public int SellerMedianTradeTimeSeconds { get; set; }

        public string ListingUrl => $"https://csfloat.com/item/{Id}";
        public string ImageUrl => string.IsNullOrEmpty(IconUrl) ? "" :
            IconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? IconUrl : "https://community.cloudflare.steamstatic.com/economy/image/" + IconUrl;

        /// <summary>Fraction of the seller's trades that failed or were avoided (0 when they have no history).</summary>
        public double SellerFailureRate => SellerTotalTrades <= 0 ? 0 : (SellerFailedTrades + SellerAvoidedTrades) / (double)SellerTotalTrades;

        public static CSFloatListing FromJson(JToken token)
        {
            if (token is not JObject o) throw new FormatException("CSFloat listing is not a JSON object.");
            var item = o["item"] as JObject ?? new JObject();
            var reference = o["reference"] as JObject;
            var seller = o["seller"] as JObject;
            var stats = seller?["statistics"] as JObject;

            var listing = new CSFloatListing
            {
                Id = o.Value<string>("id") ?? throw new FormatException("CSFloat listing has no id."),
                CreatedAtUtc = ReadUtc(o["created_at"]),
                Type = o.Value<string>("type") ?? "",
                State = o.Value<string>("state") ?? "",
                PriceCents = ReadInt(o["price"]) ?? 0,
                MarketHashName = item.Value<string>("market_hash_name") ?? "",
                ItemName = item.Value<string>("item_name") ?? "",
                WearName = item.Value<string>("wear_name") ?? "",
                ItemType = item.Value<string>("type") ?? "",
                FloatValue = ReadDouble(item["float_value"]),
                PaintSeed = ReadInt(item["paint_seed"]),
                IsStatTrak = item.Value<bool?>("is_stattrak") ?? false,
                IsSouvenir = item.Value<bool?>("is_souvenir") ?? false,
                IsCommodity = item.Value<bool?>("is_commodity") ?? false,
                AssetId = item.Value<string>("asset_id") ?? "",
                DParam = item.Value<string>("d_param") ?? "",
                IconUrl = item.Value<string>("icon_url") ?? "",
                StickerCount = (item["stickers"] as JArray)?.Count ?? 0,
                BasePriceCents = ReadInt(reference?["base_price"]),
                PredictedPriceCents = ReadInt(reference?["predicted_price"]),
                ReferenceQuantity = ReadInt(reference?["quantity"]),
                SellerSteamId = seller?.Value<string>("steam_id") ?? "",
                SellerOnline = seller?.Value<bool?>("online") ?? false,
                SellerAway = seller?.Value<bool?>("away") ?? false,
                SellerTotalTrades = ReadInt(stats?["total_trades"]) ?? 0,
                SellerVerifiedTrades = ReadInt(stats?["total_verified_trades"]) ?? 0,
                SellerFailedTrades = ReadInt(stats?["total_failed_trades"]) ?? 0,
                SellerAvoidedTrades = ReadInt(stats?["total_avoided_trades"]) ?? 0,
                SellerMedianTradeTimeSeconds = ReadInt(stats?["median_trade_time"]) ?? 0,
            };
            if (string.IsNullOrEmpty(listing.MarketHashName))
                throw new FormatException($"CSFloat listing {listing.Id} has no market_hash_name.");
            return listing;
        }

        private static DateTime ReadUtc(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return default;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>().ToUniversalTime();
            return DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : default;
        }

        internal static int? ReadInt(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Integer) return (int)Math.Clamp(token.Value<long>(), int.MinValue, int.MaxValue);
            if (token.Type == JTokenType.Float) return (int)Math.Round(token.Value<double>());
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
        }

        private static double? ReadDouble(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type is JTokenType.Float or JTokenType.Integer) return token.Value<double>();
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
        }
    }
}
