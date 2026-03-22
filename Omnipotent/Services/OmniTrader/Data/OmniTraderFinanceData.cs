using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using ScottPlot.TickGenerators;
using SteamKit2.GC.Deadlock.Internal;
using SteamKit2.Internal;
using System.IO.Compression;
using System.Net;

namespace Omnipotent.Services.OmniTrader.Data
{
    //this uses the Kraken exchange to source OHLC data
    public class OmniTraderFinanceData
    {
        public OmniTrader parent;
        private string krakenAPI = @"https://api.kraken.com/";
        private HttpClient httpClient = new HttpClient();
        public OmniTraderFinanceData(OmniTrader parent)
        {
            this.parent = parent;
        }

        public enum TimeInterval
        {
            OneMinute = 1,
            FiveMinute = 5,
            FifteenMinute = 15,
            ThirtyMinute = 30,
            OneHour = 60,
            FourHour = 240,
            OneDay = 1440,
            OneWeek = 10080,
            FifteenDay = 21600
        }

        public class OHLCCandlesData
        {
            public OmniTraderFinanceData.TimeInterval timeInterval;
            public DateTime startDate;
            public DateTime endDate;
            public List<OHLCCandle> candles;

            public OHLCCandlesData(List<OHLCCandle> candles, OmniTraderFinanceData.TimeInterval interval)
            {
                this.candles = candles;
                timeInterval= interval;
                startDate= candles.Select(k=>k.Timestamp).OrderBy(k=>k.Ticks).ToList()[0];
                endDate= candles.Select(k => k.Timestamp).OrderByDescending(k => k.Ticks).ToList()[0];
            }
        }
        public struct OHLCCandle
        {
            public DateTime Timestamp;
            public decimal Open;
            public decimal High;
            public decimal Low;
            public decimal Close;
            public decimal VWAP;
            public decimal Volume;
            public decimal TradeCount;
        }
        public struct Tick
        {
            public decimal Price { get; set; }
            public decimal Quantity { get; set; }
            public long TimestampMs { get; set; }
            public bool IsBuyerMaker { get; set; }
        }

        public async Task<List<Tick>> GetTickDataAsync(string symbol, DateTime start, DateTime end)
        {
            List<Tick> ticks = new();
            symbol = symbol.ToUpper();

            // Ensure we are working strictly in UTC to avoid server mismatch bugs
            start = start.Kind == DateTimeKind.Utc ? start : start.ToUniversalTime();
            end = end.Kind == DateTimeKind.Utc ? end : end.ToUniversalTime();

            long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
            long endMs = new DateTimeOffset(end).ToUnixTimeMilliseconds();

            // Loop over every day in the requested range
            for (DateTime currentDate = start.Date; currentDate <= end.Date; currentDate = currentDate.AddDays(1))
            {
                // Binance archives are generated the day AFTER. 
                // If the loop hits 'today', the zip does not exist yet.
                if (currentDate >= DateTime.UtcNow.Date)
                {
                    break;
                }

                string dateStr = currentDate.ToString("yyyy-MM-dd");
                string url = $"https://data.binance.vision/data/spot/daily/aggTrades/{symbol}/{symbol}-aggTrades-{dateStr}.zip";

                Stream stream = null;
                int maxRetries = 4;
                int delayMs = 1000;

                // --- RATE LIMITING & RETRY LOGIC ---
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        stream = await httpClient.GetStreamAsync(url);
                        break; // Success, exit retry loop
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        // 404: The symbol didn't exist yet, or data is missing. Gracefully skip this day.
                        stream = null;
                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        // 429 (Rate Limit) or 5xx (Server Error). 
                        if (i == maxRetries - 1) throw new Exception($"Failed to download {url} after {maxRetries} attempts.", ex);

                        await Task.Delay(delayMs);
                        delayMs *= 2; // Exponential backoff (1s, 2s, 4s...)
                    }
                }

                if (stream == null) continue; // Move to the next day if 404

                //DATA PARSING & FILTERING LOGIC
                using (stream)
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count == 0) continue;

                    using var entryStream = archive.Entries[0].Open();
                    using var reader = new StreamReader(entryStream);

                    string line;
                    bool isFirstLine = true;

                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var cols = line.Split(',');

                        // Handle potential headers gracefully
                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            if (!long.TryParse(cols[0], out _)) continue; // Skip header row
                        }

                        if (cols.Length < 7) continue;

                        long timestamp = long.Parse(cols[5]);

                        // Skip ticks that occurred earlier on the start day than the requested start time
                        if (timestamp < startMs) continue;

                        // If we pass the exact end time, we are completely done.
                        // Because CSVs are chronological, we can immediately terminate the entire function.
                        if (timestamp > endMs) break;

                        ticks.Add(new Tick
                        {
                            Price = decimal.Parse(cols[1], System.Globalization.CultureInfo.InvariantCulture),
                            Quantity = decimal.Parse(cols[2], System.Globalization.CultureInfo.InvariantCulture),
                            TimestampMs = timestamp,
                            IsBuyerMaker = bool.Parse(cols[6])
                        });
                    }
                }
            }
            return ticks;
        }

        // This method retrieves OHLC candle data for a specified cryptocurrency pair and time interval.
        // Automatically paginates if more than 720 candles are requested and trims to exactly candleCount.
        public async Task<OHLCCandlesData> GetCryptoCandlesDataAsync(string coin, string currency, TimeInterval interval, int candleCount, DateTime? since = null)
        {
            string pair = $"{coin}/{currency}";
            List<OHLCCandle> allCandles = [];

            // Calculate the starting timestamp so we fetch far enough back to cover candleCount candles
            long sinceUnix;
            if (since.HasValue)
            {
                sinceUnix = new DateTimeOffset(since.Value).ToUnixTimeSeconds();
            }
            else
            {
                long intervalSeconds = (int)interval * 60;
                sinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (candleCount * intervalSeconds);
            }

            const int maxRetries = 10;
            const int baseDelayMs = 1500;

            while (allCandles.Count < candleCount)
            {
                string url = $"{krakenAPI}0/public/OHLC?pair={pair}&interval={(int)interval}&since={sinceUnix}";

                dynamic jsonResponse = null;

                for (int attempt = 0; ; attempt++)
                {
                    var response = await httpClient.GetAsync(url);

                    // Handle HTTP-level rate limiting (429)
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt >= maxRetries)
                            throw new Exception($"Failed to retrieve OHLC data after {maxRetries + 1} attempts: rate limited by Kraken API.");
                        int delay = baseDelayMs * (int)Math.Pow(2, attempt);
                        await Task.Delay(delay);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"Failed to retrieve OHLC data: {response.StatusCode} Reason: {await response.Content.ReadAsStringAsync()}");

                    string responseContent = await response.Content.ReadAsStringAsync();
                    jsonResponse = JsonConvert.DeserializeObject(responseContent);

                    // Check for API-level errors before accessing result
                    var errors = jsonResponse.error;
                    if (errors != null && errors.Count > 0)
                    {
                        bool isRateLimited = false;
                        foreach (var error in errors)
                        {
                            string msg = (string)error;
                            if (msg.Contains("EAPI:Rate limit") || msg.Contains("EGeneral:Too many requests"))
                            {
                                isRateLimited = true;
                                break;
                            }
                        }

                        if (isRateLimited)
                        {
                            if (attempt >= maxRetries)
                                throw new Exception($"Failed to retrieve OHLC data after {maxRetries + 1} attempts: rate limited by Kraken API.");
                            int delay = baseDelayMs * (int)Math.Pow(2, attempt);
                            await Task.Delay(delay);
                            continue;
                        }

                        throw new Exception($"Kraken API error: {string.Join(", ", errors)}");
                    }

                    break;
                }

                var result = jsonResponse.result;
                var candles = result[pair];

                int batchCount = 0;
                long lastTimestamp = sinceUnix;

                foreach (var candle in candles)
                {
                    OHLCCandle oHLCCandle;
                    oHLCCandle.Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)candle[0]).DateTime;
                    oHLCCandle.Open = (decimal)candle[1];
                    oHLCCandle.High = (decimal)candle[2];
                    oHLCCandle.Low = (decimal)candle[3];
                    oHLCCandle.Close = (decimal)candle[4];
                    oHLCCandle.VWAP = (decimal)candle[5];
                    oHLCCandle.Volume = (decimal)candle[6];
                    oHLCCandle.TradeCount = (decimal)candle[7];
                    allCandles.Add(oHLCCandle);

                    lastTimestamp = (long)candle[0];
                    batchCount++;
                }

                // No new candles returned — nothing left to fetch
                if (batchCount == 0)
                    break;

                // Advance past the last candle we received for the next page
                sinceUnix = lastTimestamp + 1;

                // Rate-limit: respect 1 call/second cadence between paginated requests
                await Task.Delay(baseDelayMs);
            }

            // Deduplicate by timestamp (in case of overlap between pages) and take the most recent candleCount
            allCandles = allCandles
                .GroupBy(c => c.Timestamp)
                .Select(g => g.First())
                .OrderBy(c => c.Timestamp)
                .TakeLast(candleCount)
                .ToList();

            return new OHLCCandlesData(allCandles, interval);
        }

        // Represents a single price level in the order book
        public class OmniOrderBookLevel
        {
            public decimal Price { get; set; }
            public decimal Quantity { get; set; }
        }

        // Represents the full Order Book
        public class OmniOrderBook
        {
            public long LastUpdateId { get; set; }
            public List<OmniOrderBookLevel> Bids { get; set; } = new List<OmniOrderBookLevel>();
            public List<OmniOrderBookLevel> Asks { get; set; } = new List<OmniOrderBookLevel>();
        }

        // Internal class to map Binance's raw JSON response
        internal class BinanceDepthResponse
        {
            public long lastUpdateId { get; set; }
            public string[][] bids { get; set; }
            public string[][] asks { get; set; }
        }

            /// <summary>
            /// Fetches the live Level 2 Order Book (Market Depth) for a given symbol.
            /// </summary>
            /// <param name="symbol">The trading pair (e.g. "BTCUSDT")</param>
            /// <param name="depth">Valid limits: 5, 10, 20, 50, 100, 500, 1000, 5000.</param>
            public async Task<OmniOrderBook> GetLiveOrderBookAsync(string symbol, int depth = 1000)
            {
                symbol = symbol.ToUpper();
                string url = $"https://api.binance.com/api/v3/depth?symbol={symbol}&limit={depth}";

                int maxRetries = 4;
                int delayMs = 500;

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        HttpResponseMessage response = await httpClient.GetAsync(url);

                        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode == 418) // Binance uses 418 for IP bans
                        {
                            // If we hit the rate limit, we must back off or risk an IP ban.
                            string retryAfter = response.Headers.Contains("Retry-After")
                                ? response.Headers.GetValues("Retry-After").FirstOrDefault()
                                : null;

                            int waitTime = retryAfter != null ? int.Parse(retryAfter) * 1000 : delayMs;

                            parent.ServiceLog($"GetLiveOrderBookAsync Rate limited! Waiting {waitTime}ms before retry...");
                            await Task.Delay(waitTime);
                            delayMs *= 2;
                            continue;
                        }

                        response.EnsureSuccessStatusCode();

                        string json = await response.Content.ReadAsStringAsync();

                        // Deserialize the raw string arrays
                        var rawData = JsonConvert.DeserializeObject<BinanceDepthResponse>(json);

                        if (rawData == null) throw new Exception("Failed to deserialize Binance response.");

                        // Map the string arrays into precise Decimal objects for our bot
                        var orderBook = new OmniOrderBook
                        {
                            LastUpdateId = rawData.lastUpdateId,
                            Bids = ParseLevels(rawData.bids),
                            Asks = ParseLevels(rawData.asks)
                        };

                        return orderBook;
                    }
                    catch (HttpRequestException ex)
                    {
                        if (i == maxRetries - 1) throw new Exception($"Failed to fetch L2 Data for {symbol} after {maxRetries} attempts.", ex);
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                    }
                }

                return null;
            }

            private static List<OmniOrderBookLevel> ParseLevels(string[][] rawLevels)
            {
                var levels = new List<OmniOrderBookLevel>(rawLevels.Length);
                foreach (var level in rawLevels)
                {
                    levels.Add(new OmniOrderBookLevel
                    {
                        Price = decimal.Parse(level[0], System.Globalization.CultureInfo.InvariantCulture),
                        Quantity = decimal.Parse(level[1], System.Globalization.CultureInfo.InvariantCulture)
                    });
                }
                return levels;
            }
        }
}
