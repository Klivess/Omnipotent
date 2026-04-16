using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Diagnostics;
using System.Drawing;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Notifications
{
    public class NotificationsService : OmniService
    {
        KliveBotDiscord KliveBotDiscord;
        private readonly ConcurrentDictionary<string, PendingTextPrompt> _pendingTextPrompts = new(StringComparer.OrdinalIgnoreCase);

        private sealed class PendingTextPrompt
        {
            public DiscordMessage PromptMessage { get; set; }
            public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

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
            var search = ((await GetServicesByType<KliveBotDiscord>()));
            if (search == null)
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
                        builder.WithContent($"Submitted! ||option: {e.Id}||");
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
            return await SendTextPromptCore(title, description, timeToAnswer, modalTitle, modalPlaceholder, null);
        }

        public async Task<string> SendTextPromptToKlivesDiscordTracked(string trackingId, string title, string description, TimeSpan timeToAnswer, string modalTitle = "", string modalPlaceholder = "")
        {
            return await SendTextPromptCore(title, description, timeToAnswer, modalTitle, modalPlaceholder, trackingId);
        }

        public async Task<bool> CancelTrackedTextPrompt(string trackingId, string cancellationMessage)
        {
            if (string.IsNullOrWhiteSpace(trackingId)) return false;

            if (!_pendingTextPrompts.TryRemove(trackingId.Trim(), out var pending)) return false;

            try
            {
                if (pending.PromptMessage != null)
                {
                    await pending.PromptMessage.RespondAsync(string.IsNullOrWhiteSpace(cancellationMessage) ? "Notification cancelled." : cancellationMessage);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to send tracked prompt cancellation reply");
            }

            pending.Completion.TrySetCanceled();
            return true;
        }

        private async Task<string> SendTextPromptCore(string title, string description, TimeSpan timeToAnswer, string modalTitle, string modalPlaceholder, string trackingId)
        {
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed($"Notification: {title}", description, DSharpPlus.Entities.DiscordColor.DarkBlue);


            string buttonID = RandomGeneration.GenerateRandomLengthOfNumbers(5);
            string modalID = RandomGeneration.GenerateRandomLengthOfNumbers(5);

            embedBuilder.AddComponents(new DiscordButtonComponent(ButtonStyle.Primary, buttonID, "Input Text"));

            var message = await KliveBotDiscord.SendMessageToKlives(embedBuilder);

            PendingTextPrompt pending = new PendingTextPrompt { PromptMessage = message };
            string normalizedTrackingId = string.IsNullOrWhiteSpace(trackingId) ? null : trackingId.Trim();

            if (!string.IsNullOrEmpty(normalizedTrackingId))
            {
                _pendingTextPrompts[normalizedTrackingId] = pending;
            }

            KliveBotDiscord.Client.ComponentInteractionCreated += async (s, e) =>
            {
                if (e.Id == buttonID)
                {
                    DiscordInteractionResponseBuilder modal = new DiscordInteractionResponseBuilder();
                    modal.CustomId = modalID;
                    modal.AddComponents(new TextInputComponent("Your input", modalID, modalPlaceholder));
                    modal.Title = modalTitle;
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.Modal, modal);
                }
            };

            KliveBotDiscord.Client.ModalSubmitted += async (s, e) =>
            {
                if (e.Values.ContainsKey(modalID))
                {
                    DiscordInteractionResponseBuilder builder = new();
                    string submitted = e.Values[modalID];
                    builder.WithContent($"Submitted!");
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, builder);
                    pending.Completion.TrySetResult(submitted);
                }
            };

            try
            {
                return await pending.Completion.Task.WaitAsync(timeToAnswer);
            }
            catch (TimeoutException)
            {
                await message.RespondAsync("Prompt timed out. KliveBot will make do on its own.");
                throw;
            }
            finally
            {
                if (!string.IsNullOrEmpty(normalizedTrackingId))
                {
                    _pendingTextPrompts.TryRemove(normalizedTrackingId, out _);
                }
            }
        }

    }
}
