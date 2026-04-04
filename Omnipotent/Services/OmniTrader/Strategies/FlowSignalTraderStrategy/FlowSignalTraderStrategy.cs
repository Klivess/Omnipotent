using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.Json.Serialization;
using TL;
using XGBoostSharp;
using static LLama.Native.NativeLibraryConfig;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace Omnipotent.Services.OmniTrader.Strategies.FlowSignalTraderStrategy
{
    public class FlowSignalTraderStrategy : OmniTraderStrategy
    {
        public FlowSignalScalpingEngine engine;
        private bool signalHandlerAttached;

        public FlowSignalTraderStrategy()
        {
            Name = "FlowSignal Trader Strategy";
            Description = "Listens to signals from FlowSignal application, and places orders.";
        }

        // DTO for FlowSignal JSON payload
        public class FlowSignalDto
    {
        [JsonPropertyName("symbol")] public string Symbol { get; set; }
        [JsonPropertyName("direction")] public string Direction { get; set; }
        [JsonPropertyName("setup_type")] public string SetupType { get; set; }
        [JsonPropertyName("strength")] public string Strength { get; set; }
        [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
        [JsonPropertyName("price")] public double Price { get; set; }
        [JsonPropertyName("entry_low")] public double EntryLow { get; set; }
        [JsonPropertyName("entry_high")] public double EntryHigh { get; set; }
        [JsonPropertyName("stop")] public double Stop { get; set; }
        [JsonPropertyName("tp1")] public double Tp1 { get; set; }
        [JsonPropertyName("tp2")] public double Tp2 { get; set; }
        [JsonPropertyName("vpin")] public double Vpin { get; set; }
        [JsonPropertyName("ofi")] public double Ofi { get; set; }
        [JsonPropertyName("rsi_1m")] public double Rsi1m { get; set; }
        [JsonPropertyName("rsi_5m")] public double Rsi5m { get; set; }
        [JsonPropertyName("cvd")] public string Cvd { get; set; }
        [JsonPropertyName("cvd_momentum")] public string CvdMomentum { get; set; }
        [JsonPropertyName("cvd_div")] public string? CvdDiv { get; set; }
        [JsonPropertyName("large_b")] public int LargeB { get; set; }
        [JsonPropertyName("large_s")] public int LargeS { get; set; }
        [JsonPropertyName("large_ratio")] public double LargeRatio { get; set; }
        [JsonPropertyName("funding")] public double Funding { get; set; }
        [JsonPropertyName("trend")] public string Trend { get; set; }
        [JsonPropertyName("book_imb")] public double BookImb { get; set; }
        [JsonPropertyName("spread")] public double Spread { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; }
        [JsonPropertyName("score")] public int Score { get; set; }
        [JsonPropertyName("telegram_text")] public string TelegramText { get; set; }
        [JsonPropertyName("bot_token")] public string? BotToken { get; set; }
        [JsonPropertyName("chat_id")] public string? ChatId { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; }
    }

        protected override Task OnLoad()
        {
            engine = new FlowSignalScalpingEngine(this, "BTCUSDT");
            if (!signalHandlerAttached)
            {
                // Subscribe to Signal Events
                engine.OnSignal += (sender, e) =>
                {
                    var s = e.Signal;
                    string msg = "\n*****************************************\n" +
                                 $"NEW SIGNAL: {e.Symbol} {s.Direction}\n" +
                                 $"Type: {s.SetupType} | Strength: {s.Strength}\n" +
                                 $"Entry: {s.Price} | SL: {s.StopLoss} | TP: {s.TakeProfit1}\n" +
                                 $"Reason: {s.Reason} | Score: {s.Score}/15\n" +
                                 "*****************************************\n";

                    if (tradeSessionState.GetOpenPositions().Count > 0)
                    {
                        //StrategyLog("Already have an open position. Skipping signal.");
                        return;
                    }
                    //if datetime is between 15:30 and 22:00 UTC time
                    else if (DateTime.UtcNow.TimeOfDay > TimeSpan.FromHours(15.5) && DateTime.UtcNow.TimeOfDay < TimeSpan.FromHours(22))
                    {
                        //StrategyLog(msg);
                        RaiseLong(AmountType.Percentage, 10, Convert.ToDecimal(s.StopLoss), Convert.ToDecimal(s.TakeProfit1));
                    }
                };

                // 3. Subscribe to Heartbeat/Status Updates
                engine.OnStatusUpdate += (sender, statusText) =>
                {
                    //StrategyLog($"   > TELEMETRY: {statusText}");
                };

                signalHandlerAttached = true;
            }

            return Task.CompletedTask;
        }

        protected override Task OnSessionStart()
        {
            if (tradeSessionState.sessionType == TradeSessionType.Live
                || tradeSessionState.sessionType == TradeSessionType.Simulator
                || tradeSessionState.sessionType == TradeSessionType.Testnet)
            {
                engine.Stop();
                _ = engine.RunAsync();
            }

            return Task.CompletedTask;
        }
        protected override async Task OnCandleClose(OmniTraderFinanceData.OHLCCandle latest)
        {
        }
    }
}
