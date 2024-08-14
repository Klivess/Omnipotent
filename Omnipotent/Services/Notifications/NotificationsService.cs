using DSharpPlus;
using DSharpPlus.Entities;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Diagnostics;
using System.Drawing;

namespace Omnipotent.Services.Notifications
{
    public class NotificationsService : OmniService
    {
        KliveBotDiscord KliveBotDiscord;

        public enum PromptType
        {
            TextInput,
            Button,
            Selection
        }

        public NotificationsService()
        {
            name = "Notifications";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            //Acquire KliveBot Discord Service
            var search = (serviceManager.GetServiceByClassType<KliveBotDiscord>());
            if (!search.Any())
            {
                await Task.Delay(2000);
                ServiceMain();
            }
            else
            {
                KliveBotDiscord = (KliveBot_Discord.KliveBotDiscord)(search[0]);
            }
        }
        public async Task<string> SendButtonsPromptToKlivesDiscord(string title, string description, Dictionary<string, ButtonStyle> buttonsInfo, TimeSpan timeToAnswer)
        {
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed($"Notification: {title}",
                description,
                DSharpPlus.Entities.DiscordColor.DarkBlue);

            List<DiscordComponent> components = new();

            List<string> buttonIDs = new();
            foreach (var button in buttonsInfo)
            {
                string id = button.Key + RandomGeneration.GenerateRandomLengthOfNumbers(5);
                buttonIDs.Add(id);
                components.Add(new DSharpPlus.Entities.DiscordButtonComponent(button.Value, id, button.Key));
            }
            embedBuilder.AddComponents(components.ToArray());
            bool cancelled = false;
            string submitted = "";

            var message = await KliveBotDiscord.SendMessageToKlives(embedBuilder);

            CancellationTokenSource token = new();

            KliveBotDiscord.Client.ComponentInteractionCreated += async (s, e) =>
            {
                for (global::System.Int32 i = 0; i < buttonIDs.Count; i++)
                {
                    if (e.Id == buttonIDs[i])
                    {
                        token.Cancel();
                        DiscordInteractionResponseBuilder builder = new();
                        builder.WithContent($"Submitted!");
                        await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, builder);
                        submitted = buttonsInfo.ElementAt(i).Key;
                        break;
                    }
                }
            };
            Stopwatch countdown = Stopwatch.StartNew();
            while (submitted == "")
            {
                if (countdown.Elapsed >= timeToAnswer)
                {
                    await message.RespondAsync("Prompt timed out. KliveBot will make do on its own.");
                    throw new TimeoutException("No response to prompt.");
                }
            }
            return submitted;
        }
        public async Task<string> SendTextPromptToKlivesDiscord(string title, string description, TimeSpan timeToAnswer, string modalTitle = "", string modalPlaceholder = "")
        {
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed($"Notification: {title}", description, DSharpPlus.Entities.DiscordColor.DarkBlue);


            string buttonID = RandomGeneration.GenerateRandomLengthOfNumbers(5);
            string modalID = RandomGeneration.GenerateRandomLengthOfNumbers(5);

            embedBuilder.AddComponents(new DiscordButtonComponent(ButtonStyle.Primary, buttonID, "Input Text"));

            var message = await KliveBotDiscord.SendMessageToKlives(embedBuilder);
            string submitted = "";
            KliveBotDiscord.Client.ComponentInteractionCreated += async (s, e) =>
            {
                if (e.Id == buttonID)
                {
                    DiscordInteractionResponseBuilder modal = new DiscordInteractionResponseBuilder();
                    modal.CustomId = modalID;
                    modal.AddComponents(new TextInputComponent("", modalID, modalPlaceholder));
                    modal.Title = modalTitle;
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.Modal, modal);
                }
            };
            KliveBotDiscord.Client.ModalSubmitted += async (s, e) =>
            {
                if (e.Values.Keys.ToArray()[0].ToString() == modalID)
                {
                    DiscordInteractionResponseBuilder builder = new();
                    builder.WithContent($"Submitted!");
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, builder);
                    submitted = e.Values.Values.First();
                }
            };

            Stopwatch countdown = Stopwatch.StartNew();
            while (submitted == "")
            {
                if (countdown.Elapsed >= timeToAnswer)
                {
                    await message.RespondAsync("Prompt timed out. KliveBot will make do on its own.");
                    throw new TimeoutException("No response to prompt.");
                }
            }
            return submitted;
        }

    }
}
