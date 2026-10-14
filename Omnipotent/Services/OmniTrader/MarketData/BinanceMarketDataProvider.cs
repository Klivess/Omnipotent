using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Binance spot klines (REST + websocket). Bars are stamped with their OPEN time; the REST
    /// response's trailing bar is split off as <see cref="CandleFetch.Forming"/> while it is open.
    /// </summary>
    public sealed class BinanceMarketDataProvider : IMarketDataProvider, ICandleSource
    {
        public string Name => "Binance";
        private const string RestBase = "https://api.binance.com/api/v3/";
        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

        public bool Supports(TimeInterval interval) => true;

        public async Task<IReadOnlyList<OHLCCandle>> GetHistoricalCandlesAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
            => (await FetchLatestAsync(symbol, interval, count, ct)).Closed;

        public async Task<CandleFetch> FetchLatestAsync(string symbol, TimeInterval interval, int count, CancellationToken ct = default)
        {
            string sym = Normalize(symbol);
            string intv = ToBinanceInterval(interval);
            var rows = new List<(OHLCCandle Bar, long CloseMs)>();
            int remaining = count + 1; // +1 for the forming bar the response usually ends with
            long? endTime = null;
            int safety = 60;

            while (remaining > 0 && safety-- > 0)
            {
                int batch = Math.Min(1000, remaining);
                string url = $"{RestBase}klines?symbol={sym}&interval={intv}&limit={batch}";
                if (endTime.HasValue) url += $"&endTime={endTime.Value}";
                var page = await GetPageAsync(url, ct);
                if (page.Count == 0) break;
                rows.InsertRange(0, page);
                remaining -= page.Count;
                endTime = new DateTimeOffset(page[0].Bar.Timestamp).ToUnixTimeMilliseconds() - 1;
                if (page.Count < batch) break;
            }

            return Split(rows, count);
        }

        public async Task<CandleFetch> FetchRangeAsync(string symbol, TimeInterval interval, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            string sym = Normalize(symbol);
            string intv = ToBinanceInterval(interval);
            long startMs = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

            var rows = new List<(OHLCCandle Bar, long CloseMs)>();
            long cursor = startMs;
            int safety = 600;
            while (cursor <= endMs && safety-- > 0)
            {
                string url = $"{RestBase}klines?symbol={sym}&interval={intv}&startTime={cursor}&endTime={endMs}&limit=1000";
                var page = await GetPageAsync(url, ct);
                if (page.Count == 0) break;
                rows.AddRange(page);
                cursor = new DateTimeOffset(page[^1].Bar.Timestamp).ToUnixTimeMilliseconds() + 1;
                if (page.Count < 1000) break;
            }
            return Split(rows, int.MaxValue);
        }

        private static async Task<List<(OHLCCandle Bar, long CloseMs)>> GetPageAsync(string url, CancellationToken ct)
        {
            using var resp = await httpClient.GetAsync(url, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Binance klines {(int)resp.StatusCode}: {Trim(body)}");
            var arr = JArray.Parse(body);
            var page = new List<(OHLCCandle, long)>(arr.Count);
            foreach (var k in arr)
            {
                page.Add((new OHLCCandle(
                    DateTimeOffset.FromUnixTimeMilliseconds((long)k[0]!).UtcDateTime,
                    D(k[1]), D(k[2]), D(k[3]), D(k[4]), D(k[5])), (long)k[6]!));
            }
            return page;
        }

        private CandleFetch Split(List<(OHLCCandle Bar, long CloseMs)> rows, int count)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var closed = new List<OHLCCandle>(rows.Count);
            OHLCCandle? forming = null;
            var seen = new HashSet<DateTime>();
            foreach (var (bar, closeMs) in rows.OrderBy(r => r.Bar.Timestamp))
            {
                if (!seen.Add(bar.Timestamp)) continue;
                if (closeMs < nowMs) closed.Add(bar);
                else forming = bar;
            }
            if (closed.Count > count) closed = closed.GetRange(closed.Count - count, count);
            return new CandleFetch(closed, forming, Name);
        }

        /// <summary>Latest traded price (REST ticker).</summary>
        public async Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken ct = default)
        {
            string url = $"{RestBase}ticker/price?symbol={Normalize(symbol)}";
            using var resp = await httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return 0m;
            var obj = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            return decimal.TryParse((string?)obj["price"], NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0m;
        }

        /// <summary>Health key for the kline websocket (separate from REST, which can work when the socket is blocked).</summary>
        public const string WebSocketSource = "Binance-ws";

        /// <summary>
        /// Closed bars pushed by the exchange websocket, stamped with their open time. Reconnects
        /// with a short pause on a dropped socket; gives up after three failed connects in a row so
        /// the caller can fall back to polling.
        /// </summary>
        public async IAsyncEnumerable<OHLCCandle> StreamCandlesAsync(string symbol, TimeInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
        {
            string sym = Normalize(symbol).ToLowerInvariant();
            string intv = ToBinanceInterval(interval);
            string endpoint = $"wss://stream.binance.com:9443/ws/{sym}@kline_{intv}";
            int failedConnects = 0;

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? socket = null;
                bool connected = false;
                try
                {
                    socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(endpoint), ct);
                    connected = true;
                    failedConnects = 0;
                    Data.SourceHealth.Success(WebSocketSource, 0, "market-data");
                }
                catch (OperationCanceledException) { socket?.Dispose(); yield break; }
                catch (Exception ex)
                {
                    socket?.Dispose();
                    Data.SourceHealth.Failure(WebSocketSource, ex.Message, "market-data");
                }

                if (!connected)
                {
                    if (++failedConnects >= 3) yield break;
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { yield break; }
                    continue;
                }

                byte[] buffer = new byte[32 * 1024];
                while (socket!.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    OHLCCandle? candle;
                    try
                    {
                        string message = await ReceiveFullMessageAsync(socket, buffer, ct);
                        candle = TryParseClosedCandle(message);
                    }
                    catch { break; }
                    if (candle.HasValue) yield return candle.Value;
                }
                socket.Dispose();
                if (ct.IsCancellationRequested) yield break;
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { yield break; }
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

        internal static OHLCCandle? TryParseClosedCandle(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("k", out JsonElement kline)) return null;
            if (!kline.TryGetProperty("x", out JsonElement isClosed) || !isClosed.GetBoolean()) return null;
            long openMs = kline.GetProperty("t").GetInt64();
            decimal P(string p) => decimal.Parse(kline.GetProperty(p).GetString()!, CultureInfo.InvariantCulture);
            return new OHLCCandle(DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime,
                P("o"), P("h"), P("l"), P("c"), P("v"));
        }

        public static string Normalize(string symbol) => symbol.ToUpperInvariant().Replace("/", "").Replace("-", "");

        public static string ToBinanceInterval(TimeInterval interval) => interval switch
        {
            TimeInterval.OneMinute => "1m",
            TimeInterval.FiveMinute => "5m",
            TimeInterval.FifteenMinute => "15m",
            TimeInterval.ThirtyMinute => "30m",
            TimeInterval.OneHour => "1h",
            TimeInterval.TwoHour => "2h",
            TimeInterval.FourHour => "4h",
            TimeInterval.TwelveHour => "12h",
            TimeInterval.OneDay => "1d",
            TimeInterval.OneWeek => "1w",
            TimeInterval.OneMonth => "1M",
            _ => "1h"
        };

        private static decimal D(JToken? t) => decimal.Parse((string)t!, NumberStyles.Any, CultureInfo.InvariantCulture);
        private static string Trim(string s) => s.Length <= 200 ? s : s[..200];
    }
}
