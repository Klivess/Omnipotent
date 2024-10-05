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
using DSharpPlus.SlashCommands;
using System.Security.Policy;
using Omnipotent.Services.KliveLocalLLM;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscord : OmniService
    {
        public DiscordClient Client { get; set; }
        DiscordGuild GuildContainingKlives;
        DiscordMember KlivesMember;
        Dictionary<ulong, KliveLocalLLM.KliveLocalLLM.KliveLLMSession> sessions = new();
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
                string token = await GetDataHandler().ReadDataFromFile(tokenPath, true);
                DiscordConfiguration connectionConfig = new DiscordConfiguration()
                {
                    Token = token,
                    ReconnectIndefinitely = true,
                    AutoReconnect = true,
                    MinimumLogLevel = LogLevel.None,
                    //Intents = DiscordIntents.AllUnprivileged
                };
                Client = new DiscordClient(connectionConfig);

                var slash = Client.UseSlashCommands(new SlashCommandsConfiguration
                {
                    Services = new ServiceCollection().AddSingleton(serviceManager).BuildServiceProvider()
                });
                slash.RegisterCommands<KliveBotDiscordCommands>();
                ServiceLog("Slash commands registered!");

                Client.MessageCreated += Client_MessageCreated;

                await Client.ConnectAsync(new DiscordActivity("Ran by Omnipotent!", ActivityType.ListeningTo));
                ServiceLog("KliveBot connected to Discord!");

                await LoadVariables();

                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Discord Bot Crashed!");
                TerminateService();
            }
        }

        private async Task LoadVariables()
        {
            if (KlivesMember == null)
            {
                GuildContainingKlives = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                KlivesMember = await GuildContainingKlives.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
            }
        }

        private async Task Client_MessageCreated(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs args)
        {
            try
            {
                if (args.Channel.IsPrivate && args.Author.Id != Client.CurrentUser.Id)
                {
                    DiscordMessageBuilder embed = new DiscordMessageBuilder();
                    DiscordMessage message = null;
                    try
                    {
                        embed = MakeSimpleEmbed($"New message sent to KliveBot: {args.Author.Username}", $"Content: {args.Message.Content}" + (args.Message.Attachments.Any() ? $"" +
$"\n\nAttachments: {string.Join("\n", args.Message.Attachments.Select(k => k.Url))}" : ""), DiscordColor.Orange);
                        message = await SendMessageToKlives(embed);
                        if (serviceManager.GetKliveLocalLLMService().IsServiceActive())
                        {
                            string response = "";
                            if (!sessions.ContainsKey(args.Author.Id))
                            {
                                sessions.Add(args.Author.Id, serviceManager.GetKliveLocalLLMService().CreateSession(new List<string> { KliveBotPersonalityString.personality }, false));
                            }
                            response = await sessions[args.Author.Id].SendMessage($"New Message from {args.Author.Username}: {args.Message.Content}");
                            await args.Message.RespondAsync(response);
                            if (args.Author.Id != OmniPaths.KlivesDiscordAccountID)
                            {
                                await message.ModifyAsync(MakeSimpleEmbed(message.Embeds[0].Title, message.Embeds[0].Description + $"\n\nKliveBot Response: {response}", message.Embeds[0].Color.Value));
                            }
                        }
                        else
                        {
                            if (args.Author.Id != OmniPaths.KlivesDiscordAccountID)
                            {
                                string resp = await serviceManager.GetNotificationsService().SendTextPromptToKlivesDiscord($"New message sent to KliveBot: {args.Author.Username}", $"Content: {args.Message.Content}" + (args.Message.Attachments.Any() ? $"" +
$"\n\nAttachments: {string.Join("\n", args.Message.Attachments.Select(k => k.Url))}" : ""), TimeSpan.FromDays(3), "KliveBot's Response", "Response");

                                await args.Message.RespondAsync(resp);
                            }

                        }

                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, "Error responding to MessageCreated");
                        string response = "I tried to respond to this, but an exception occurred. 😢";
                        await args.Message.RespondAsync(response);
                        await message.ModifyAsync(MakeSimpleEmbed(message.Embeds[0].Title, message.Embeds[0].Description + $"\n\nKliveBot Response: {response}", message.Embeds[0].Color.Value));
                    }
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in MessageCreated event!");
            }
        }

        public async Task<DiscordMessage> SendMessageToKlives(string message)
        {
            while (Client == null) { }
            if (KlivesMember == null)
            {
                GuildContainingKlives = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                KlivesMember = await GuildContainingKlives.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
            }
            return await KlivesMember.SendMessageAsync(message);
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
