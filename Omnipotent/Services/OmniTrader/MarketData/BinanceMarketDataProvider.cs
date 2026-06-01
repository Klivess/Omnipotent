using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    public sealed class BinanceMarketDataProvider : IMarketDataProvider
    {
        public string Name => "Binance";
        private static readonly HttpClient httpClient = new();

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            string sym = symbol.ToUpperInvariant().Replace("/", "");
            string intv = ToBinanceInterval(interval);
            var output = new List<OHLCCandle>();
            int remaining = count;
            long? endTime = null;
            int safetyLimit = 50;

            while (remaining > 0 && safetyLimit-- > 0)
            {
                int batch = Math.Min(1000, remaining);
                string url = $"https://api.binance.com/api/v3/klines?symbol={sym}&interval={intv}&limit={batch}";
                if (endTime.HasValue) url += $"&endTime={endTime.Value}";
                var resp = await httpClient.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Binance klines failed: {resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
                var body = await resp.Content.ReadAsStringAsync(ct);
                var arr = JArray.Parse(body);
                if (arr.Count == 0) break;
                var batchCandles = new List<OHLCCandle>(arr.Count);
                foreach (var k in arr)
                {
                    batchCandles.Add(new OHLCCandle(
                        DateTimeOffset.FromUnixTimeMilliseconds((long)k[6]!).UtcDateTime,
                        decimal.Parse((string)k[1]!, CultureInfo.InvariantCulture),
                        decimal.Parse((string)k[2]!, CultureInfo.InvariantCulture),
                        decimal.Parse((string)k[3]!, CultureInfo.InvariantCulture),
                        decimal.Parse((string)k[4]!, CultureInfo.InvariantCulture),
                        decimal.Parse((string)k[5]!, CultureInfo.InvariantCulture)));
                }
                output.InsertRange(0, batchCandles);
                remaining -= batchCandles.Count;
                long oldestOpen = (long)arr[0]![0]!;
                endTime = oldestOpen - 1;
                if (batchCandles.Count < batch) break;
            }

            return output
                .GroupBy(c => c.Timestamp)
                .Select(g => g.First())
                .OrderBy(c => c.Timestamp)
                .TakeLast(count)
                .ToList();
        }

        /// <summary>Latest traded price for a symbol (REST ticker) — drives the live forming candle.</summary>
        public async Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
        {
            string sym = symbol.ToUpperInvariant().Replace("/", "");
            string url = $"https://api.binance.com/api/v3/ticker/price?symbol={sym}";
            var resp = await httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return 0m;
            var obj = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            return decimal.TryParse((string?)obj["price"], NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m;
        }

        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
        {
            string sym = symbol.ToLowerInvariant().Replace("/", "");
            string intv = ToBinanceInterval(interval);
            string endpoint = $"wss://stream.binance.com:9443/ws/{sym}@kline_{intv}";

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? socket = null;
                Exception? caught = null;
                try
                {
                    socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(endpoint), ct);
                }
                catch (OperationCanceledException) { socket?.Dispose(); yield break; }
                catch (Exception ex) { caught = ex; socket?.Dispose(); }

                if (caught != null)
                {
                    await Task.Delay(3000, ct);
                    continue;
                }

                byte[] buffer = new byte[32 * 1024];
                while (socket!.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    string message;
                    OHLCCandle? candle;
                    try
                    {
                        message = await ReceiveFullMessageAsync(socket, buffer, ct);
                        candle = TryParseClosedCandle(message);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { break; }
                    if (candle.HasValue)
                        yield return candle.Value;
                }
                socket.Dispose();
                if (!ct.IsCancellationRequested)
                    await Task.Delay(3000, ct);
            }
        }

        private static async Task<string> ReceiveFullMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static OHLCCandle? TryParseClosedCandle(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("k", out JsonElement kline)) return null;
            if (!kline.TryGetProperty("x", out JsonElement isClosed) || !isClosed.GetBoolean()) return null;
            long closeMs = kline.GetProperty("T").GetInt64();
            decimal P(string p) => decimal.Parse(kline.GetProperty(p).GetString()!, CultureInfo.InvariantCulture);
            return new OHLCCandle(
                DateTimeOffset.FromUnixTimeMilliseconds(closeMs).UtcDateTime,
                P("o"), P("h"), P("l"), P("c"), P("v"));
        }

        public static string ToBinanceInterval(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => "1m",
            TimeInterval.FiveMinute => "5m",
            TimeInterval.FifteenMinute => "15m",
            TimeInterval.ThirtyMinute => "30m",
            TimeInterval.OneHour => "1h",
            TimeInterval.FourHour => "4h",
            TimeInterval.OneDay => "1d",
            TimeInterval.OneWeek => "1w",
            _ => "1m"
        };
    }
}
