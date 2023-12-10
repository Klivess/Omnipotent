using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscord : OmniService
    {
        private DiscordClient Client { get; set; }
        public KliveBotDiscord()
        {
            name = "KliveBot Discord Bot";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            DiscordConfiguration connectionConfig = new DiscordConfiguration()
            {
                Token = "OTg3NTA0MTA0NzIzMDgzMzM0.Gbj764.XsDsz9ClRQFFqPIYp87ipqVtbrIF-q7gpEdq2U",
                ReconnectIndefinitely = true,
                AutoReconnect = true,
                MinimumLogLevel = LogLevel.None,
                //Intents = DiscordIntents.AllUnprivileged
            };
            Client = new DiscordClient(connectionConfig);
            await Client.ConnectAsync(new DiscordActivity("Ran by Omnipotent!", ActivityType.ListeningTo));
            await Task.Delay(-1);
        }
    }
}
