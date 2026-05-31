using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Point-in-time universe data from Binance's public REST API — free, no API key, generous limits.
    /// Seeds a candidate set from the current top symbols by 24h quote volume (plus an explicit
    /// include-list of notable delisted pairs to fight survivorship bias), then pulls each symbol's
    /// daily klines (close + USD quote volume) over the backtest window and caches them via
    /// <see cref="UniverseRepository"/>.
    /// <para>Unlike CoinGecko, Binance gives real daily OHLC and real quote volume with no key or rate
    /// gate. It does not provide market cap, so the universe is ranked by trailing quote volume — which
    /// the spec explicitly permits ("rank survivors by market cap, or just trailing volume"). Coverage
    /// of delisted coins is limited to what Binance still serves (the include-list captures what's
    /// available); the survivorship audit still flags coins whose data stops mid-window.</para>
    /// </summary>
    public sealed class BinanceUniverseProvider
    {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private const string Base = "https://api.binance.com";
        private readonly string quoteAsset;
        private readonly int throttleMs;

        public BinanceUniverseProvider(string quoteAsset = "USDT", int throttleMs = 60)
        {
            this.quoteAsset = quoteAsset.ToUpperInvariant();
            this.throttleMs = throttleMs;
        }

        /// <summary>Notable delisted/dead pairs to union in (Binance may still serve partial history).</summary>
        public static readonly string[] DelistedInclude =
        {
            "LUNAUSDT", "USTUSDT", "FTTUSDT", "SRMUSDT", "WAVESUSDT", "ANTUSDT", "CVCUSDT",
            "BTSUSDT", "MIRUSDT", "TLMUSDT", "RAYUSDT",
        };

        /// <summary>
        /// Ensure daily universe data covering [from, to] is cached. Skips the network entirely if the
        /// repository already covers the window for a reasonable number of coins. Best-effort: a failed
        /// symbol is logged and skipped, not fatal.
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
            log($"Binance: {candidates.Count} candidate {quoteAsset} pairs; fetching daily klines {from:yyyy-MM-dd}..{to:yyyy-MM-dd}.");

            long fromMs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeMilliseconds();
            long toMs = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeMilliseconds();
            int done = 0, kept = 0;

            foreach (var symbol in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var points = await FetchDailySeriesAsync(symbol, fromMs, toMs, ct);
                    if (points.Count > 0)
                    {
                        await repo.UpsertDailyAsync(symbol, points, ct);
                        string baseTicker = BaseTicker(symbol);
                        await repo.UpsertCoinMetaAsync(new CoinMeta
                        {
                            CoinId = symbol,
                            Symbol = baseTicker,
                            Name = baseTicker,
                            Denylisted = UniverseBuilder.DefaultDenylist.Contains(baseTicker),
                            FirstDate = points[0].Date,
                            LastDate = points[^1].Date,
                        }, ct);
                        kept++;
                    }
                }
                catch (Exception ex) { log($"Binance: skipped {symbol}: {ex.Message}"); }
                done++;
                if (done % 25 == 0) log($"Binance: {done}/{candidates.Count} fetched ({kept} with data).");
                if (throttleMs > 0) await Task.Delay(throttleMs, ct);
            }
            log($"Binance: done. {kept}/{candidates.Count} symbols stored.");
        }

        private async Task<List<string>> FetchCandidatesAsync(int topN, Action<string> log, CancellationToken ct)
        {
            var ranked = new List<(string symbol, double quoteVol)>();
            try
            {
                using var resp = await http.GetAsync($"{Base}/api/v3/ticker/24hr", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var arr = JArray.Parse(await resp.Content.ReadAsStringAsync(ct));
                    foreach (var t in arr)
                    {
                        string sym = (string?)t["symbol"] ?? "";
                        if (!sym.EndsWith(quoteAsset, StringComparison.Ordinal)) continue;
                        if (IsExcluded(sym)) continue;
                        double qv = double.TryParse((string?)t["quoteVolume"], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
                        ranked.Add((sym, qv));
                    }
                }
                else log($"Binance ticker/24hr failed: {resp.StatusCode}");
            }
            catch (Exception ex) { log($"Binance ticker/24hr error: {ex.Message}"); }

            var top = ranked.OrderByDescending(x => x.quoteVol).Take(topN).Select(x => x.symbol).ToList();
            var set = new HashSet<string>(top, StringComparer.OrdinalIgnoreCase);
            foreach (var d in DelistedInclude)
                if (d.EndsWith(quoteAsset, StringComparison.Ordinal) && set.Add(d)) top.Add(d);
            return top;
        }

        /// <summary>Exclude leveraged tokens (…UP/DOWN/BULL/BEAR) which are not spot coins.</summary>
        private bool IsExcluded(string symbol)
        {
            string b = BaseTicker(symbol);
            return b.EndsWith("UP") || b.EndsWith("DOWN") || b.Contains("BULL") || b.Contains("BEAR");
        }

        private string BaseTicker(string symbol)
            => symbol.EndsWith(quoteAsset, StringComparison.Ordinal) ? symbol[..^quoteAsset.Length] : symbol;

        private async Task<List<UniverseDailyPoint>> FetchDailySeriesAsync(string symbol, long fromMs, long toMs, CancellationToken ct)
        {
            var output = new List<UniverseDailyPoint>();
            long cursor = fromMs;
            int safety = 20;
            while (cursor < toMs && safety-- > 0)
            {
                string url = $"{Base}/api/v3/klines?symbol={symbol}&interval=1d&startTime={cursor}&endTime={toMs}&limit=1000";
                using var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) throw new Exception($"klines {resp.StatusCode}");
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (arr.Count == 0) break;
                foreach (var k in arr)
                {
                    long openMs = (long)k[0]!;
                    var day = DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime.Date;
                    decimal close = decimal.Parse((string)k[4]!, CultureInfo.InvariantCulture);
                    decimal quoteVol = decimal.Parse((string)k[7]!, CultureInfo.InvariantCulture);
                    // market_cap unknown on Binance → 0; ranking falls back to trailing volume.
                    output.Add(new UniverseDailyPoint(DateTime.SpecifyKind(day, DateTimeKind.Utc), close, 0m, quoteVol));
                }
                long lastOpen = (long)arr[^1]![0]!;
                cursor = lastOpen + 86_400_000; // next day
                if (arr.Count < 1000) break;
                if (throttleMs > 0) await Task.Delay(throttleMs, ct);
            }
            return output;
        }
    }
}
