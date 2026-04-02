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
using System.Diagnostics;
using Humanizer;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscord : OmniService
    {
        public DiscordClient Client { get; set; }
        DiscordGuild GuildContainingKlives;
        DiscordMember KlivesMember;
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
                var parent = this;
                var slash = Client.UseSlashCommands(new SlashCommandsConfiguration
                {
                    Services = new ServiceCollection().AddSingleton(parent).BuildServiceProvider()
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

        private static string GetLlmSessionIdForChat(DSharpPlus.EventArgs.MessageCreateEventArgs args)
        {
            // One Discord chat/channel = one LLM conversation session
            return $"discord-chat-{args.Channel.Id}";
        }

        private async Task Client_MessageCreated(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs args)
        {
            try
            {
                if (args.Channel.IsPrivate && args.Author.Id != Client.CurrentUser.Id)
                {
                    //
                    // I KNOW THIS PARTICULAR FUNCTION IS REALLY REALLY MESSY! I REALLY DO NOT CARE IN THE SLIGHTEST!!
                    //
                    DiscordMessageBuilder embed = new DiscordMessageBuilder();
                    DiscordMessage message = null;
                    try
                    {
                        embed = MakeSimpleEmbed(
                            $"New message sent to KliveBot: {args.Author.Username}",
                            $"Content: {args.Message.Content}" + (args.Message.Attachments.Any()
                                ? $"\n\nAttachments: {string.Join("\n", args.Message.Attachments.Select(k => k.Url))}"
                                : ""),
                            DiscordColor.Orange);

                        if (args.Author.Id != OmniPaths.KlivesDiscordAccountID)
                        {
                            message = await SendMessageToKlives(embed);
                        }

                        var llmService = (KliveLocalLLM.KliveLLM)(await GetServicesByType<KliveLocalLLM.KliveLLM>())[0];
                        if (llmService.IsServiceActive())
                        {
                            string sessionId = GetLlmSessionIdForChat(args);
                            Stopwatch stopwatch = Stopwatch.StartNew();
                            var llmResponse = await llmService.QueryLocalLLMAsync(args.Message.Content, sessionId);
                            stopwatch.Stop();
                            await args.Message.RespondAsync(llmResponse.Response+"\n\nProcessing Time: "+stopwatch.Elapsed.Humanize());
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, "Error responding to MessageCreated");
                        string response = "I tried to respond to this, but an exception occurred. 😢";
                        await args.Message.RespondAsync(response);

                        if (message != null && message.Embeds.Any())
                        {
                            await message.ModifyAsync(
                                MakeSimpleEmbed(
                                    message.Embeds[0].Title,
                                    message.Embeds[0].Description + $"\n\nKliveBot Response: {response}",
                                    message.Embeds[0].Color.Value));
                        }
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
            try
            {
                // Use a TaskCompletionSource to avoid busy-waiting
                await WaitForClientInitializationAsync();

                if (KlivesMember == null)
                {
                    GuildContainingKlives = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                    KlivesMember = await GuildContainingKlives.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
                }

                string prefix = OmniPaths.CheckIfOnServer() == false ? "(Not Production) " : "";
                return await KlivesMember.SendMessageAsync(prefix + message);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Sending message to Klives failed!");
                return null;
            }
        }

        private async Task WaitForClientInitializationAsync()
        {
            while (Client == null)
            {
                await Task.Delay(50); // Avoid busy-waiting by yielding control
            }
        }

        public async Task<DiscordMessage> SendMessageToKlives(DiscordMessageBuilder builder)
        {
            try
            {
                while (Client == null) { await Task.Delay(100); }
                var guildID = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                var member = await guildID.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
                return await member.SendMessageAsync(builder);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Sending message to Klives failed!");
                return null;
            }
        }

        public async Task<DiscordMessage> SendMessageToChannel(ulong guildId, ulong channelId, DiscordMessageBuilder builder)
        {
            try
            {
                while (Client == null) { await Task.Delay(100); }
                var channel = await Client.GetChannelAsync(channelId);
                return await channel.SendMessageAsync(builder);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Sending message to channel failed!");
                return null;
            }
        }

        public static DiscordMessageBuilder MakeSimpleEmbed(string title, string description, DiscordColor color, string imagefilepath = "")
        {
            DiscordMessageBuilder builder = new DiscordMessageBuilder();
            DiscordEmbedBuilder discordEmbed = new DiscordEmbedBuilder();
            discordEmbed.Title = OmniPaths.CheckIfOnServer() == false ? "(Not Production) " : "" + title;
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

        public static DiscordMessageBuilder MakeSimpleEmbed(string title, string description, DiscordColor color, Uri imageLink)
        {
            DiscordMessageBuilder builder = new DiscordMessageBuilder();
            DiscordEmbedBuilder discordEmbed = new DiscordEmbedBuilder();
            discordEmbed.Title = title;
            discordEmbed.Description = description;
            discordEmbed.Color = color;
            discordEmbed.WithThumbnail(imageLink);
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
