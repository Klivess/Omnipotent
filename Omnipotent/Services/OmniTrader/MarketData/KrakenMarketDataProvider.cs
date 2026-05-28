using Newtonsoft.Json;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Runtime.CompilerServices;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    public sealed class KrakenMarketDataProvider : IMarketDataProvider
    {
        public string Name => "Kraken";

        private const string ApiBase = "https://api.kraken.com/";
        private static readonly HttpClient httpClient = new();

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            (string coin, string currency) = SplitSymbol(symbol);
            string pair = $"{coin}/{currency}";
            var allCandles = new List<OHLCCandle>();

            long intervalSeconds = (long)interval * 60;
            long sinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - count * intervalSeconds;

            const int maxRetries = 10;
            const int baseDelayMs = 1500;

            while (allCandles.Count < count)
            {
                string url = $"{ApiBase}0/public/OHLC?pair={Uri.EscapeDataString(pair)}&interval={(int)interval}&since={sinceUnix}";
                dynamic? jsonResponse = null;

                for (int attempt = 0; ; attempt++)
                {
                    var response = await httpClient.GetAsync(url, ct);
                    if ((int)response.StatusCode == 429)
                    {
                        if (attempt >= maxRetries)
                            throw new Exception("Kraken rate limit exceeded");
                        await Task.Delay(baseDelayMs * (int)Math.Pow(2, attempt), ct);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"Kraken OHLC failed: {response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");

                    string body = await response.Content.ReadAsStringAsync(ct);
                    jsonResponse = JsonConvert.DeserializeObject(body);
                    var errors = jsonResponse?.error;
                    if (errors != null && errors.Count > 0)
                    {
                        bool rateLimited = false;
                        foreach (var error in errors)
                        {
                            string msg = (string)error;
                            if (msg.Contains("EAPI:Rate limit") || msg.Contains("EGeneral:Too many requests"))
                            {
                                rateLimited = true;
                                break;
                            }
                        }
                        if (rateLimited)
                        {
                            if (attempt >= maxRetries) throw new Exception("Kraken rate limit exceeded");
                            await Task.Delay(baseDelayMs * (int)Math.Pow(2, attempt), ct);
                            continue;
                        }
                        throw new Exception($"Kraken error: {string.Join(", ", errors)}");
                    }
                    break;
                }

                var result = jsonResponse!.result;
                dynamic? candles = null;
                foreach (var p in result)
                {
                    string key = (string)p.Name;
                    if (key == "last") continue;
                    candles = p.Value;
                    break;
                }
                if (candles == null) break;

                int batchCount = 0;
                long lastTimestamp = sinceUnix;
                foreach (var c in candles)
                {
                    allCandles.Add(new OHLCCandle(
                        DateTimeOffset.FromUnixTimeSeconds((long)c[0]).UtcDateTime,
                        (decimal)c[1], (decimal)c[2], (decimal)c[3], (decimal)c[4], (decimal)c[6]));
                    lastTimestamp = (long)c[0];
                    batchCount++;
                }
                if (batchCount == 0) break;
                sinceUnix = lastTimestamp + 1;
                await Task.Delay(baseDelayMs, ct);
            }

            return allCandles
                .GroupBy(c => c.Timestamp)
                .Select(g => g.First())
                .OrderBy(c => c.Timestamp)
                .TakeLast(count)
                .ToList();
        }

        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Kraken streaming not implemented for v1 — fall back to polling REST.
            DateTime? lastEmitted = null;
            int intervalSec = (int)interval * 60;
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<OHLCCandle> latest;
                try
                {
                    latest = await GetHistoricalCandlesAsync(symbol, interval, 2, ct);
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
                    continue;
                }
                if (latest.Count > 0)
                {
                    var closed = latest[0];
                    if (lastEmitted == null || closed.Timestamp > lastEmitted.Value)
                    {
                        lastEmitted = closed.Timestamp;
                        yield return closed;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
            }
        }

        private static (string coin, string currency) SplitSymbol(string symbol)
        {
            // Accept BTCUSDT, BTC/USD, XBTUSD — normalize to two parts.
            symbol = symbol.ToUpperInvariant();
            if (symbol.Contains('/'))
            {
                var parts = symbol.Split('/');
                return (parts[0], parts[1]);
            }
            if (symbol.EndsWith("USDT")) return (symbol[..^4], "USD");
            if (symbol.EndsWith("USDC")) return (symbol[..^4], "USD");
            if (symbol.EndsWith("USD")) return (symbol[..^3], "USD");
            if (symbol.EndsWith("EUR")) return (symbol[..^3], "EUR");
            if (symbol.EndsWith("GBP")) return (symbol[..^3], "GBP");
            return (symbol, "USD");
        }
    }
}
