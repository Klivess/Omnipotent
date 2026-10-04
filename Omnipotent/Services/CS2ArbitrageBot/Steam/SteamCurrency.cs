namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    /// <summary>
    /// Steam ECurrencyCode ↔ ISO code, and conversion of Steam minor-unit prices into GBP pence.
    /// Steam picks the currency from the caller's wallet or IP, so a book is not guaranteed to be in GBP
    /// (e.g. if the server's IP ever geolocates elsewhere); prices are normalised instead of trusted.
    /// </summary>
    public static class SteamCurrency
    {
        public const int GBP = 2;

        private static readonly Dictionary<int, string> IsoByCode = new()
        {
            [1] = "usd", [2] = "gbp", [3] = "eur", [4] = "chf", [5] = "rub", [6] = "pln", [7] = "brl",
            [8] = "jpy", [9] = "nok", [10] = "idr", [11] = "myr", [12] = "php", [13] = "sgd", [14] = "thb",
            [15] = "vnd", [16] = "krw", [17] = "try", [18] = "uah", [19] = "mxn", [20] = "cad", [21] = "aud",
            [22] = "nzd", [23] = "cny", [24] = "inr", [25] = "clp", [26] = "pen", [27] = "cop", [28] = "zar",
            [29] = "hkd", [30] = "twd", [31] = "sar", [32] = "aed", [34] = "ars", [35] = "ils", [37] = "kzt",
            [38] = "kwd", [39] = "qar", [40] = "crc", [41] = "uyu",
        };

        public static string? IsoCode(int steamCurrency) => IsoByCode.TryGetValue(steamCurrency, out var iso) ? iso : null;

        /// <summary>
        /// Converts <paramref name="minorUnits"/> of Steam currency <paramref name="steamCurrency"/> to GBP pence
        /// using USD-based rates (units per 1 USD, as CSFloat's /meta/exchange-rates returns). Null when unknown.
        /// </summary>
        public static int? ToGbpPence(int minorUnits, int steamCurrency, IReadOnlyDictionary<string, double>? usdRates)
        {
            if (steamCurrency == GBP) return minorUnits;
            string? iso = IsoCode(steamCurrency);
            if (iso == null || usdRates == null) return null;
            if (!usdRates.TryGetValue(iso, out double sourcePerUsd) || sourcePerUsd <= 0) return null;
            if (!usdRates.TryGetValue("gbp", out double gbpPerUsd) || gbpPerUsd <= 0) return null;
            return (int)Math.Round(minorUnits / sourcePerUsd * gbpPerUsd, MidpointRounding.AwayFromZero);
        }
    }
}
