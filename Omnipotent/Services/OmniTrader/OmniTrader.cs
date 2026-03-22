using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Strategies;
using static Omnipotent.Services.OmniTrader.Data.OmniTraderFinanceData;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public OmniTraderFinanceData data;
        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            data = new OmniTraderFinanceData(this);

            if (!OmniPaths.CheckIfOnServer())
            {
                FlowSignalTraderStrategy strategy = new();
                await strategy.Initialise(this);
                ServiceLog("");
            }
        }

        public async Task WriteBacktestResultToDesktop(OmniBacktestResult result)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, $"BacktestResult_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filePath, result.ToString());
        }
    }
}
