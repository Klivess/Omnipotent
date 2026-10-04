using Newtonsoft.Json.Linq;
using System.Net;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    /// <summary>
    /// Bulk Steam median prices for every CS2 item (USD), from the public feed the open-source "CSGO Trader"
    /// extension publishes (~34k items, refreshed every ~8h). It is a prior, never a decision input: it lets
    /// the bot skip the ~99% of listings priced at market without spending a Steam request on each, and
    /// ranks the whole market for the structural scan. Real decisions always use a live order book.
    /// If the feed disappears the bot keeps working; it just looks up more books.
    /// </summary>
    public sealed class SteamReferencePrices
    {
        public const string FeedUrl = "https://prices.csgotrader.app/latest/steam.json";

        public readonly record struct Entry(double Last24hUsd, double Last7dUsd, double Last30dUsd, double Last90dUsd)
        {
            /// <summary>The highest recent median — deliberately optimistic, so skipping on it is safe.</summary>
            public double HighestRecentUsd => new[] { Last24hUsd, Last7dUsd, Last30dUsd }.Max();
            /// <summary>A typical recent price for ranking (7-day median, falling back to longer windows).</summary>
            public double TypicalUsd => Last7dUsd > 0 ? Last7dUsd : Last30dUsd > 0 ? Last30dUsd : Last24hUsd > 0 ? Last24hUsd : Last90dUsd;
        }

        private volatile IReadOnlyDictionary<string, Entry> entries = new Dictionary<string, Entry>();
        public DateTime? LoadedAtUtc { get; private set; }
        public string? LastError { get; private set; }
        public int Count => entries.Count;

        public bool TryGet(string marketHashName, out Entry entry) => entries.TryGetValue(marketHashName, out entry);

        public IEnumerable<KeyValuePair<string, Entry>> All => entries;

        /// <summary>Optimistic Steam price in GBP pence, or null when the item is unknown.</summary>
        public int? OptimisticPence(string marketHashName, double gbpPerUsd, double headroom = 1.15)
        {
            if (gbpPerUsd <= 0 || !entries.TryGetValue(marketHashName, out var entry)) return null;
            double usd = entry.HighestRecentUsd;
            if (usd <= 0) return null;
            return (int)Math.Ceiling(usd * 100 * gbpPerUsd * headroom);
        }

        /// <param name="minimumItems">A feed with fewer items is treated as truncated and ignored (tests pass a small number).</param>
        public async Task<bool> RefreshAsync(HttpClient http, CancellationToken ct = default, int minimumItems = 1000)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"feed returned {(int)response.StatusCode}";
                    return false;
                }
                byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
                var parsed = Parse(Decompress(raw));
                if (parsed.Count < minimumItems)
                {
                    LastError = $"feed only had {parsed.Count} items; keeping the previous copy";
                    return false;
                }
                entries = parsed;
                LoadedAtUtc = DateTime.UtcNow;
                LastError = null;
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        /// <summary>The CDN serves the file gzip-compressed even when the client did not ask for it.</summary>
        internal static string Decompress(byte[] raw)
        {
            if (raw.Length > 2 && raw[0] == 0x1f && raw[1] == 0x8b)
            {
                using var input = new MemoryStream(raw);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                return reader.ReadToEnd();
            }
            return System.Text.Encoding.UTF8.GetString(raw);
        }

        internal static Dictionary<string, Entry> Parse(string json)
        {
            var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (JToken.Parse(json) is not JObject root) return result;
            foreach (var property in root.Properties())
            {
                if (property.Value is not JObject o) continue;
                double Read(string key) => o[key]?.Type is JTokenType.Float or JTokenType.Integer ? o.Value<double>(key) : 0;
                var entry = new Entry(Read("last_24h"), Read("last_7d"), Read("last_30d"), Read("last_90d"));
                if (entry.TypicalUsd > 0) result[property.Name] = entry;
            }
            return result;
        }

        public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // decompressed manually (see Decompress)
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromSeconds(60) };
    }
}
