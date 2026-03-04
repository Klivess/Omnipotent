using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using SteamKit2.GC.Deadlock.Internal;
using SteamKit2.Internal;

namespace Omnipotent.Services.OmniTrader.Data
{
    //this uses the Kraken exchange to source OHLC data
    public class RequestKlineData
    {
        public OmniTrader parent;
        private string krakenAPI = @"https://api.kraken.com/";
        public RequestKlineData(OmniTrader parent)
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
            public List<OHLCCandle> candles;
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

        // This method retrieves OHLC candle data for a specified cryptocurrency pair and time interval.
        // Automatically paginates if more than 720 candles are requested and trims to exactly candleCount.
        public async Task<OHLCCandlesData> GetCryptoCandlesDataAsync(string coin, string currency, TimeInterval interval, int candleCount, DateTime? since = null)
        {
            string pair = $"{coin}/{currency}";
            HttpClient httpClient = new HttpClient();
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

            while (allCandles.Count < candleCount)
            {
                string url = $"{krakenAPI}0/public/OHLC?pair={pair}&interval={(int)interval}&since={sinceUnix}";
                var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    throw new Exception($"Failed to retrieve OHLC data: {response.StatusCode} Reason: {await response.Content.ReadAsStringAsync()}");

                string responseContent = await response.Content.ReadAsStringAsync();
                dynamic jsonResponse = JsonConvert.DeserializeObject(responseContent);

                // Check for API-level errors before accessing result
                var errors = jsonResponse.error;
                if (errors != null && errors.Count > 0)
                    throw new Exception($"Kraken API error: {string.Join(", ", errors)}");

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

                // Rate-limit: wait before the next paginated request
                await Task.Delay(1500);
            }

            // Deduplicate by timestamp (in case of overlap between pages) and take the most recent candleCount
            allCandles = allCandles
                .GroupBy(c => c.Timestamp)
                .Select(g => g.First())
                .OrderBy(c => c.Timestamp)
                .TakeLast(candleCount)
                .ToList();

            return new OHLCCandlesData { candles = allCandles };
        }
    }
}
