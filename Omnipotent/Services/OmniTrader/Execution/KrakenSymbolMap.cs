namespace Omnipotent.Services.OmniTrader.Execution
{
    public static class KrakenSymbolMap
    {
        private static readonly Dictionary<string, string> BaseAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "BTC", "XBT" },
            { "DOGE", "XDG" }
        };

        public static string ToKrakenPair(string symbol)
        {
            symbol = symbol.ToUpperInvariant().Replace("/", "");
            (string baseAsset, string quote) = Split(symbol);
            if (BaseAliases.TryGetValue(baseAsset, out var alias)) baseAsset = alias;
            if (string.Equals(quote, "USDT", StringComparison.OrdinalIgnoreCase)) quote = "USDT";
            return baseAsset + quote;
        }

        private static (string baseAsset, string quote) Split(string symbol)
        {
            foreach (var suffix in new[] { "USDT", "USDC", "USD", "EUR", "GBP" })
            {
                if (symbol.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return (symbol[..^suffix.Length], suffix);
            }
            return (symbol, "USD");
        }
    }
}
