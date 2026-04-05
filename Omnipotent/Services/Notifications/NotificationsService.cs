using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;

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

        public NotificationsService()
        {
            name = "Notifications";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            var search = await GetServicesByType<KliveBotDiscord>();
            if (search == null)
            {
                await Task.Delay(2000);
                ServiceMain();
            }
            else
            {
                KliveBotDiscord = (KliveBotDiscord)search[0];
            }
        }

        public async Task<string> SendButtonsPromptToKlivesDiscord(string title, string description, Dictionary<string, DiscordButtonStyle> buttonsInfo, TimeSpan timeToAnswer)
        {
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed($"Notification: {title}", description, DiscordColor.DarkBlue);

            var buttons = new List<DiscordButtonComponent>();
            var buttonIDs = new List<string>();

            foreach (var button in buttonsInfo)
            {
                string id = button.Key + RandomGeneration.GenerateRandomLengthOfNumbers(5);
                buttonIDs.Add(id);
                buttons.Add(new DiscordButtonComponent(button.Value, id, button.Key));
            }

            // AddComponents was renamed to AddActionRowComponent in the nightly.
            // See: https://dsharpplus.github.io/DSharpPlus/api/DSharpPlus.Entities.DiscordMessageBuilder.html
            embedBuilder.AddActionRowComponent(buttons);

            var message = await KliveBotDiscord.SendMessageToKlives(embedBuilder);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            KliveBotDiscord.RegisterComponentHandler(async (s, e) =>
            {
                int idx = buttonIDs.IndexOf(e.Id);
                if (idx < 0) return;

                var response = new DiscordInteractionResponseBuilder()
                    .WithContent($"Submitted! ||option: {e.Id}||");
                await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource, response);
                tcs.TrySetResult(buttonsInfo.ElementAt(idx).Key);
            });

            try
            {
                return await tcs.Task.WaitAsync(timeToAnswer);
            }
            catch (TimeoutException)
            {
                await message.RespondAsync("Prompt timed out. KliveBot will make do on its own.");
                throw;
            }
        }

        public async Task<string> SendTextPromptToKlivesDiscord(string title, string description, TimeSpan timeToAnswer, string modalTitle = "", string modalPlaceholder = "")
            => await SendTextPromptCore(title, description, timeToAnswer, modalTitle, modalPlaceholder, null);

        public async Task<string> SendTextPromptToKlivesDiscordTracked(string trackingId, string title, string description, TimeSpan timeToAnswer, string modalTitle = "", string modalPlaceholder = "")
            => await SendTextPromptCore(title, description, timeToAnswer, modalTitle, modalPlaceholder, trackingId);

        public async Task<bool> CancelTrackedTextPrompt(string trackingId, string cancellationMessage)
        {
            if (string.IsNullOrWhiteSpace(trackingId)) return false;
            if (!_pendingTextPrompts.TryRemove(trackingId.Trim(), out var pending)) return false;

            try
            {
                if (pending.PromptMessage != null)
                    await pending.PromptMessage.RespondAsync(string.IsNullOrWhiteSpace(cancellationMessage) ? "Notification cancelled." : cancellationMessage);
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
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed($"Notification: {title}", description, DiscordColor.DarkBlue);

            string buttonID = RandomGeneration.GenerateRandomLengthOfNumbers(5);
            string modalID = RandomGeneration.GenerateRandomLengthOfNumbers(5);
            string inputID = RandomGeneration.GenerateRandomLengthOfNumbers(5);

            // AddActionRowComponent replaces AddComponents in the nightly.
            // See: https://dsharpplus.github.io/DSharpPlus/api/DSharpPlus.Entities.DiscordMessageBuilder.html
            embedBuilder.AddActionRowComponent(new DiscordButtonComponent(DiscordButtonStyle.Primary, buttonID, "Input Text"));

            var message = await KliveBotDiscord.SendMessageToKlives(embedBuilder);
            var pending = new PendingTextPrompt { PromptMessage = message };
            string normalizedTrackingId = string.IsNullOrWhiteSpace(trackingId) ? null : trackingId.Trim();

            if (!string.IsNullOrEmpty(normalizedTrackingId))
                _pendingTextPrompts[normalizedTrackingId] = pending;

            // Button click -> respond with a modal.
            // In the nightly, modals use the dedicated DiscordModalBuilder class with its own
            // CreateResponseAsync overload — NOT DiscordInteractionResponseBuilder.
            // See: https://github.com/DSharpPlus/DSharpPlus/blob/master/DSharpPlus/Entities/Interaction/DiscordInteraction.cs
            KliveBotDiscord.RegisterComponentHandler(async (s, e) =>
            {
                if (e.Id != buttonID) return;

                DiscordTextInputComponent textInput = new DiscordTextInputComponent(
                    customId: inputID,
                    placeholder: modalPlaceholder,
                    style: DiscordTextInputStyle.Short,
                    required: true);

                var modal = new DiscordModalBuilder()
                    .WithCustomId(modalID)
                    .WithTitle(string.IsNullOrWhiteSpace(modalTitle) ? "Input" : modalTitle)
                    .AddTextInput(textInput, "Your input");

                await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.Modal, modal);
            });

            // Modal submission handler.
            // In the nightly, submitted values are in e.Interaction.Data.Components,
            // each being a DiscordTextInputComponent whose Value holds the user's input.
            KliveBotDiscord.RegisterModalHandler(async (s, e) =>
            {
                if (e.Interaction.Data.CustomId != modalID) return;

                // Find the text input by its customId from the submitted components.
                var submittedInput = e.Interaction.Data.TextInputComponents?
                    .FirstOrDefault(c => c.CustomId == inputID);

                if (submittedInput == null) return;

                string submitted = submittedInput.Value;
                var builder = new DiscordInteractionResponseBuilder().WithContent("Submitted!");
                await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource, builder);
                pending.Completion.TrySetResult(submitted);
            });

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
                    _pendingTextPrompts.TryRemove(normalizedTrackingId, out _);
            }
        }
    }
}