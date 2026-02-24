using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Data;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public RequestKlineData requestKlineData;
        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            requestKlineData = new RequestKlineData(this);

            var candlesData = await requestKlineData.GetCryptoCandlesDataAsync("ETH", "USD", RequestKlineData.TimeInterval.OneHour, 100);
            var chart = new CandlestickChartGenerator { Title = "ETH/USD 1H" };
            chart.SaveChartPng(candlesData, RequestKlineData.TimeInterval.OneHour, @"C:\Charts\eth_usd.png");
        }
    }
}
