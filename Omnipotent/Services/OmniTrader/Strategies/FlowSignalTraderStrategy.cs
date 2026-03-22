using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using TL;
using XGBoostSharp;
using static LLama.Native.NativeLibraryConfig;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    public class FlowSignalTraderStrategy : OmniTraderStrategy
    {
        public string telegramApiID;
        public string telegramApiHash;
        public string phoneNumber;

        private HttpClient _http = new HttpClient();
        private int _updateOffset = 0;
        private readonly long _targetChatId = -1003783537817L;

        public FlowSignalTraderStrategy()
        {
            Name = "FlowSignal Trader Strategy";
            Description = "Listens to signals from FlowSignal application, and places orders.";
        }

        protected override async Task OnLoad()
        {
            telegramApiID = await parent.GetStringOmniSetting("TelegramUserAPIID", askKlivesForFulfillment: true);
            telegramApiHash = await parent.GetStringOmniSetting("TelegramUserAPIHash", askKlivesForFulfillment: true);
            phoneNumber = await parent.GetStringOmniSetting("TelegramUserPhoneNumber", askKlivesForFulfillment: true);

            var client = new WTelegram.Client(Config);
            // 3. Subscribe to the Update event BEFORE logging in
            client.OnUpdates += Client_OnUpdates;

            // 4. Login (This will trigger the Config method to ask for your Telegram code)
            var user = await client.LoginUserIfNeeded();
            StrategyLog($"Logged in as {user.username ?? user.first_name}");
            StrategyLog("Listening for bot messages in chat -1003783537817...\n");
        }
        internal string Config(string what)
        {
            switch (what)
            {
                case "api_id": return telegramApiID;
                case "api_hash": return telegramApiHash;
                case "phone_number": return phoneNumber; // Include country code, e.g., +1...
                default:
                    // This handles verification codes and 2FA passwords via console input
                    StrategyLog("Asking Klives for 2FA code.");
                    var twofacode = (string)parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                        $"Telegram 2FA Code needed for {Name} OmniTrader Strategy",
                        $"Please provide me with the 2FA code just sent.\n\n More specifically, enter: {what}", TimeSpan.FromDays(3), "2FA!", "2FA Code").GetAwaiter().GetResult();

                    return twofacode;
            }
        }

        private Task Client_OnUpdates(TL.UpdatesBase arg)
        {
            // We only care about base updates
            if (arg is not UpdatesBase updates) return Task.CompletedTask;

            foreach (var update in updates.UpdateList)
            {
                // Group/Supergroup messages come in as UpdateNewChannelMessage
                if (update is UpdateNewChannelMessage uncm && uncm.message is Message msg)
                {
                    // MTProto supergroup IDs do not have the "-100" prefix
                    long targetChatId = 3783537817;

                    // Check if the message is from our target chat
                    if (msg.peer_id.ID == targetChatId)
                    {
                        // Get the ID of the user who sent it
                        long senderId = msg.from_id?.ID ?? 0;

                        // Look up the user in the Updates dictionary
                        if (updates.Users.TryGetValue(senderId, out User senderUser))
                        {
                            // Check if the sender is actually a bot
                            if (senderUser.IsBot)
                            {
                                string botName = senderUser.username ?? senderUser.first_name;
                                StrategyLog($"[Bot @{botName}]: {msg.message}");
                            }
                        }
                    }
                }
            }
            return Task.CompletedTask;
        }

        protected override async Task OnCandleClose(OmniTraderFinanceData.OHLCCandle latest)
        {
        }

    }
}
