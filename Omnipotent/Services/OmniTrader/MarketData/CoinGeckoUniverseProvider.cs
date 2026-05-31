using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Point-in-time universe data from CoinGecko. Seeds a candidate set from the current top coins by
    /// market cap, unions in an explicit include-list of notable coins that later delisted/died (whose
    /// historical series CoinGecko still serves — this is what makes the universe survivorship-free),
    /// then pulls each coin's daily price / market cap / USD volume over the backtest window and caches
    /// it via <see cref="UniverseRepository"/>.
    /// <para>CoinGecko's <c>market_chart/range</c> returns daily close + market cap + USD volume (no
    /// intraday OHLC); the engine synthesises per-coin candles as O=H=L=C=close with Volume = USD quote
    /// volume, which is exactly what a daily-close cross-sectional strategy needs.</para>
    /// </summary>
    public sealed class CoinGeckoUniverseProvider
    {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private readonly string? apiKey;
        private readonly bool pro;
        private readonly int throttleMs;

        public CoinGeckoUniverseProvider(string? apiKey = null, bool pro = false, int throttleMs = 2500)
        {
            this.apiKey = apiKey;
            this.pro = pro;
            // Demo/free tier is ~30 calls/min → ~2s spacing; pro can go much faster.
            this.throttleMs = pro ? 350 : throttleMs;
        }

        private string Base => pro ? "https://pro-api.coingecko.com/api/v3" : "https://api.coingecko.com/api/v3";

        /// <summary>
        /// Notable coins that have died/delisted/depegged. Including them by id makes past-date
        /// universes survivorship-free (CoinGecko still returns their historical series).
        /// </summary>
        public static readonly Dictionary<string, string> DelistedIncludeList = new(StringComparer.OrdinalIgnoreCase)
        {
            ["terra-luna"] = "LUNC",      // Terra Classic (collapsed May 2022)
            ["terrausd"] = "USTC",        // TerraUSD (depegged)
            ["ftx-token"] = "FTT",        // FTX (collapsed Nov 2022)
            ["celsius-degree-token"] = "CEL",
            ["bitconnect"] = "BCC",
            ["safemoon"] = "SAFEMOON",
            ["waves"] = "WAVES",
            ["multichain"] = "MULTI",
            ["gemini-dollar"] = "GUSD",
        };

        private HttpRequestMessage Request(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Add(pro ? "x-cg-pro-api-key" : "x-cg-demo-api-key", apiKey);
            return req;
        }

        /// <summary>
        /// Ensure daily universe data covering [from, to] is cached. Skips the network entirely if the
        /// repository already covers the window for a reasonable number of coins. Best-effort and
        /// resilient: a failed coin is logged and skipped, not fatal.
        /// </summary>
        public async Task EnsureUniverseDataAsync(
            UniverseRepository repo, DateTime from, DateTime to, int topN,
            Action<string> log, CancellationToken ct = default)
        {
            var stats = await repo.StatsAsync(ct);
            if (stats.coins >= Math.Min(topN, 20) && stats.minDate.HasValue && stats.maxDate.HasValue
                && stats.minDate.Value <= from && stats.maxDate.Value >= to.AddDays(-2))
            {
                log($"Universe cache already covers {from:yyyy-MM-dd}..{to:yyyy-MM-dd} ({stats.coins} coins, {stats.rows} rows) — skipping fetch.");
                return;
            }

            var candidates = await FetchCandidatesAsync(topN, log, ct);
            log($"CoinGecko: {candidates.Count} candidate coins; fetching daily series {from:yyyy-MM-dd}..{to:yyyy-MM-dd}.");

            long unixFrom = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeSeconds();
            long unixTo = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeSeconds();
            int done = 0, kept = 0;

            foreach (var (id, symbol, name) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var points = await FetchDailySeriesAsync(id, unixFrom, unixTo, ct);
                    if (points.Count > 0)
                    {
                        await repo.UpsertDailyAsync(id, points, ct);
                        bool denylisted = UniverseBuilder.DefaultDenylist.Contains(symbol.ToUpperInvariant());
                        await repo.UpsertCoinMetaAsync(new CoinMeta
                        {
                            CoinId = id,
                            Symbol = symbol,
                            Name = name,
                            Denylisted = denylisted,
                            FirstDate = points[0].Date,
                            LastDate = points[^1].Date,
                        }, ct);
                        kept++;
                    }
                }
                catch (Exception ex)
                {
                    log($"CoinGecko: skipped {id}: {ex.Message}");
                }
                done++;
                if (done % 20 == 0) log($"CoinGecko: {done}/{candidates.Count} fetched ({kept} with data).");
                await Task.Delay(throttleMs, ct);
            }
            log($"CoinGecko: done. {kept}/{candidates.Count} coins stored.");
        }

        private async Task<List<(string id, string symbol, string name)>> FetchCandidatesAsync(int topN, Action<string> log, CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<(string, string, string)>();

            int perPage = 250;
            int pages = (int)Math.Ceiling(topN / (double)perPage);
            for (int page = 1; page <= pages; page++)
            {
                string url = $"{Base}/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={perPage}&page={page}&sparkline=false";
                try
                {
                    using var resp = await http.SendAsync(Request(url), ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        log($"CoinGecko markets page {page} failed: {resp.StatusCode}");
                        break;
                    }
                    var arr = JArray.Parse(await resp.Content.ReadAsStringAsync(ct));
                    foreach (var c in arr)
                    {
                        string id = (string?)c["id"] ?? "";
                        string sym = ((string?)c["symbol"] ?? "").ToUpperInvariant();
                        string name = (string?)c["name"] ?? "";
                        if (id.Length == 0 || !seen.Add(id)) continue;
                        result.Add((id, sym, name));
                        if (result.Count >= topN) break;
                    }
                }
                catch (Exception ex) { log($"CoinGecko markets page {page} error: {ex.Message}"); break; }
                await Task.Delay(throttleMs, ct);
            }

            // Union the explicit delisted include-list (combats survivorship bias).
            foreach (var (id, sym) in DelistedIncludeList)
                if (seen.Add(id)) result.Add((id, sym, id));

            return result;
        }

        private async Task<List<UniverseDailyPoint>> FetchDailySeriesAsync(string id, long unixFrom, long unixTo, CancellationToken ct)
        {
            string url = $"{Base}/coins/{id}/market_chart/range?vs_currency=usd&from={unixFrom}&to={unixTo}";
            using var resp = await http.SendAsync(Request(url), ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(15000, ct);
                using var retry = await http.SendAsync(Request(url), ct);
                if (!retry.IsSuccessStatusCode) throw new Exception($"429 then {retry.StatusCode}");
                return ParseSeries(await retry.Content.ReadAsStringAsync(ct));
            }
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"market_chart {resp.StatusCode}");
            return ParseSeries(await resp.Content.ReadAsStringAsync(ct));
        }

        /// <summary>
        /// Collapse CoinGecko's prices/market_caps/total_volumes arrays into one row per UTC day
        /// (last observation of the day), keyed and sorted by date.
        /// </summary>
        private static List<UniverseDailyPoint> ParseSeries(string json)
        {
            var root = JObject.Parse(json);
            var prices = root["prices"] as JArray ?? new JArray();
            var caps = root["market_caps"] as JArray ?? new JArray();
            var vols = root["total_volumes"] as JArray ?? new JArray();

            var capByMs = ToMap(caps);
            var volByMs = ToMap(vols);

            var byDay = new SortedDictionary<DateTime, UniverseDailyPoint>();
            foreach (var p in prices)
            {
                long ms = (long)p[0]!;
                decimal price = (decimal)p[1]!;
                var day = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.Date;
                decimal cap = capByMs.TryGetValue(ms, out var c) ? c : 0m;
                decimal vol = volByMs.TryGetValue(ms, out var v) ? v : 0m;
                byDay[day] = new UniverseDailyPoint(DateTime.SpecifyKind(day, DateTimeKind.Utc), price, cap, vol);
            }
            return byDay.Values.ToList();
        }

        private static Dictionary<long, decimal> ToMap(JArray arr)
        {
            var map = new Dictionary<long, decimal>(arr.Count);
            foreach (var e in arr)
            {
                long ms = (long)e[0]!;
                map[ms] = (decimal)e[1]!;
            }
            return map;
        }
    }
}
