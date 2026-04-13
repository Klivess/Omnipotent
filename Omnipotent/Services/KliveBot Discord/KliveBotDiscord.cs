using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Commands;
using Microsoft.Extensions.Logging;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Drawing;
using DSharpPlus.EventArgs;
using Humanizer;
using System.Diagnostics;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscord : OmniService
    {
        public DiscordClient Client { get; set; }
        DiscordGuild GuildContainingKlives;
        DiscordMember KlivesMember;
        public event EventHandler<MessageCreatedEventArgs> MessageCreated;

        // Use copy-on-write lists for thread safety under the nightly's parallel event dispatch.
        // See: https://dsharpplus.github.io/DSharpPlus/articles/beyond_basics/events.html
        private readonly object _handlersLock = new();
        private List<Func<DiscordClient, ComponentInteractionCreatedEventArgs, Task>> _componentHandlers = new();
        private List<Func<DiscordClient, ModalSubmittedEventArgs, Task>> _modalHandlers = new();

        public KliveBotDiscord()
        {
            name = "KliveBot Discord Bot";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        public void RegisterComponentHandler(Func<DiscordClient, ComponentInteractionCreatedEventArgs, Task> handler)
        {
            lock (_handlersLock)
                _componentHandlers = new List<Func<DiscordClient, ComponentInteractionCreatedEventArgs, Task>>(_componentHandlers) { handler };
        }

        public void RegisterModalHandler(Func<DiscordClient, ModalSubmittedEventArgs, Task> handler)
        {
            lock (_handlersLock)
                _modalHandlers = new List<Func<DiscordClient, ModalSubmittedEventArgs, Task>>(_modalHandlers) { handler };
        }

        protected override async void ServiceMain()
        {
            try
            {
                string tokenPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveBotDiscordTokenText);
                string token = await GetDataHandler().ReadDataFromFile(tokenPath, true);

                DiscordClientBuilder builder = DiscordClientBuilder.CreateDefault(token, DiscordIntents.All);

                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(this);
                });

                builder.ConfigureEventHandlers(b => b
                    .HandleMessageCreated(async (s, e) =>
                    {
                        MessageCreated?.Invoke(s, e);
                        await Client_MessageCreated(s, e);
                    })
                    .HandleComponentInteractionCreated(async (s, e) =>
                    {
                        // Snapshot list before iterating — safe under nightly's parallel dispatch.
                        // See: https://dsharpplus.github.io/DSharpPlus/articles/beyond_basics/events.html
                        List<Func<DiscordClient, ComponentInteractionCreatedEventArgs, Task>> snapshot;
                        lock (_handlersLock) snapshot = _componentHandlers;
                        foreach (var handler in snapshot)
                            await handler(s, e);
                    })
                    .HandleModalSubmitted(async (s, e) =>
                    {
                        List<Func<DiscordClient, ModalSubmittedEventArgs, Task>> snapshot;
                        lock (_handlersLock) snapshot = _modalHandlers;
                        foreach (var handler in snapshot)
                            await handler(s, e);
                    })
                );

                // UseCommands is the correct nightly DSharpPlus.Commands API.
                // See: https://dsharpplus.github.io/DSharpPlus/articles/commands/introduction.html
                builder.UseCommands((IServiceProvider serviceProvider, CommandsExtension extension) =>
                {
                    extension.AddCommands(typeof(KliveBotDiscordCommands).Assembly);
                });
                ServiceLog("Commands registered!");

                Client = builder.Build();
                await Client.ConnectAsync(new DiscordActivity("Ran by Omnipotent!", DiscordActivityType.ListeningTo));
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

        private static string GetLlmSessionIdForChat(MessageCreatedEventArgs args)
            => $"discord-chat-{args.Channel.Id}";

        private async Task Client_MessageCreated(DiscordClient sender, MessageCreatedEventArgs args)
        {
            try
            {
                if (args.Channel.IsPrivate && args.Author.Id != Client.CurrentUser.Id)
                {
                    DiscordMessageBuilder embed = new();
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
                            message = await SendMessageToKlives(embed);

                        var llmService = (KliveLLM.KliveLLM)(await GetServicesByType<KliveLLM.KliveLLM>())[0];
                        if (llmService.IsServiceActive())
                        {
                            string sessionId = GetLlmSessionIdForChat(args);
                            Stopwatch stopwatch = Stopwatch.StartNew();
                            var llmResponse = await llmService.QueryLLM(
                                args.Message.Content,
                                sessionId,
                                maxTokensOverride: 30000);
                            stopwatch.Stop();
                            await args.Message.RespondAsync(llmResponse.Response + "\n\nProcessing Time: " + stopwatch.Elapsed.Humanize());
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, "Error responding to MessageCreated");
                        string response = "I tried to respond to this, but an exception occurred. 😢";
                        await args.Message.RespondAsync(response);

                        if (message != null && message.Embeds.Any())
                        {
                            await message.ModifyAsync(MakeSimpleEmbed(
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
                await Task.Delay(50);
        }

        public async Task<DiscordMessage> SendMessageToKlives(DiscordMessageBuilder builder)
        {
            try
            {
                while (Client == null) { await Task.Delay(100); }
                var guild = await Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                var member = await guild.GetMemberAsync(OmniPaths.KlivesDiscordAccountID);
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
            DiscordMessageBuilder builder = new();
            DiscordEmbedBuilder discordEmbed = new()
            {
                Title = OmniPaths.CheckIfOnServer() == false ? "(Not Production) " : "" + title,
                Description = description,
                Color = color
            };
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
            DiscordMessageBuilder builder = new();
            DiscordEmbedBuilder discordEmbed = new()
            {
                Title = title,
                Description = description,
                Color = color
            };
            discordEmbed.WithThumbnail(imageLink);
            builder.AddEmbed(discordEmbed);
            return builder;
        }

        public static Color DiscordColorToColor(DiscordColor discordColor)
            => Color.FromArgb(discordColor.R, discordColor.G, discordColor.B);
    }
}