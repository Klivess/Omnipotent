using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscord : OmniService
    {
        public DiscordClient Client { get; set; }
        public KliveBotDiscord()
        {
            name = "KliveBot Discord Bot";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            try
            {
                string tokenPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveBotDiscordTokenText);
                string token = await dataHandler.ReadDataFromFile(tokenPath);
                DiscordConfiguration connectionConfig = new DiscordConfiguration()
                {
                    Token = token,
                    ReconnectIndefinitely = true,
                    AutoReconnect = true,
                    MinimumLogLevel = LogLevel.None,
                    //Intents = DiscordIntents.AllUnprivileged
                };
                Client = new DiscordClient(connectionConfig);
                await Client.ConnectAsync(new DiscordActivity("Ran by Omnipotent!", ActivityType.ListeningTo));
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                serviceManager.logger.LogError(name, ex, "Discord Bot Crashed!");
                TerminateService();
            }
        }

        public async Task<DiscordMessage> SendMessageToKlives(string message)
        {
            while (Client == null) { }
            var guildID = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
            var member = await guildID.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
            return await member.SendMessageAsync(message);
        }

        public async Task<DiscordMessage> SendMessageToKlives(DiscordMessageBuilder builder)
        {
            while (Client == null) { }
            var guildID = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
            var member = await guildID.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
            return await member.SendMessageAsync(builder);
        }

        public static DiscordMessageBuilder MakeSimpleEmbed(string title, string description, DiscordColor color, string imagefilepath = "", int imagewidth = 0, int imageheight = 0)
        {
            DiscordMessageBuilder builder = new DiscordMessageBuilder();
            DiscordEmbedBuilder discordEmbed = new DiscordEmbedBuilder();
            discordEmbed.Title = title;
            discordEmbed.Description = description;
            discordEmbed.Color = color;
            if (imagefilepath != "")
            {
                builder.AddFile(File.OpenRead(imagefilepath));
                discordEmbed.WithThumbnail("attachment://" + Path.GetFileName(imagefilepath));
            }
            builder.AddEmbed(discordEmbed);
            return builder;
        }

        public static Color DiscordColorToColor(DiscordColor discordColor)
        {
            Color color = Color.FromArgb(discordColor.R, discordColor.G, discordColor.B);
            return color;
        }
    }
}
