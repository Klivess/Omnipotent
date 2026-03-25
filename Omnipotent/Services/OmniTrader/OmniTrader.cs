using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Strategies.FlowSignalTraderStrategy;
using static Omnipotent.Services.OmniTrader.Data.OmniTraderFinanceData;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public OmniTraderFinanceData data;
        public OmniTraderSimulator simulator;
        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            data = new OmniTraderFinanceData(this);
            simulator= new OmniTraderSimulator(this);

            FlowSignalTraderStrategy strategy = new();
            await strategy.Initialise(this);
            strategy.engine.OnSignal += async (sender, e) =>
            {
                var s = e.Signal;
                string msg = "\n*****************************************\n" +
             $"NEW SIGNAL: {e.Symbol} {s.Direction}\n" +
             $"Type: {s.SetupType} | Strength: {s.Strength}\n" +
             $"Entry: {s.Price} | SL: {s.StopLoss} | TP: {s.TakeProfit1}\n" +
             $"Reason: {s.Reason} | Score: {s.Score}/15\n" +
             "*****************************************\n";


                if (DateTime.UtcNow.TimeOfDay > TimeSpan.FromHours(15.5) && DateTime.UtcNow.TimeOfDay < TimeSpan.FromHours(22)&&s.Score>=8)
                {
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", msg);
                }
            };
        }

        public async Task WriteBacktestResultToDesktop(OmniBacktestResult result)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, $"BacktestResult_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filePath, result.ToString());
        }
    }
}
