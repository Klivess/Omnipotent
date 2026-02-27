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
        // Maximum candle count is 720
        public async Task<OHLCCandlesData> GetCryptoCandlesDataAsync(string coin, string currency, TimeInterval interval, int candleCount, DateTime? since = null)
        {
            OHLCCandlesData oHLCCandlesData = new OHLCCandlesData();

            HttpClient httpClient = new HttpClient();
            string url = $"{krakenAPI}0/public/OHLC?pair={coin}/{currency}&interval={(int)interval}&since={(since.HasValue ? new DateTimeOffset(since.Value).ToUnixTimeSeconds() : 0)}";
            var response = await httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                dynamic jsonResponse = JsonConvert.DeserializeObject(responseContent);
                var result = jsonResponse.result;
                var candles = result[$"{coin}/{currency}"];
                oHLCCandlesData.candles = new List<OHLCCandle>();
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
                    oHLCCandlesData.candles.Add(oHLCCandle);
                }

                return oHLCCandlesData;
            }
            else
            {
                throw new Exception($"Failed to retrieve OHLC data: {response.StatusCode} Reason: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
}
