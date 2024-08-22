using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Diagnostics;
using System.Management.Automation.Subsystem.Prediction;
using static IsStatementPositiveOrNegative.IsStatementPositiveOrNegative;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscordCommands : ApplicationCommandModule
    {
        public OmniServiceManager serviceManager { private get; set; }

        [SlashCommand("ping", "Replies with pong!")]
        public async Task PingAsync(InteractionContext ctx)
        {
            Stopwatch sw = new Stopwatch();
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Pong - latency is {ctx.Client.Ping} milliseconds."));
            sw.Stop();
        }


        [SlashCommand("uptimes", "Sends uptime of all processes")]
        public async Task UptimesAsync(InteractionContext ctx)
        {
            string uptimes = $"";
            foreach (var item in serviceManager.activeServices)
            {
                uptimes += $"Service - {item.GetName()}: {item.GetServiceUptime().Humanize()}\n";
            }
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent(uptimes));
        }

        [SlashCommand("analyze", "Analyzes a sentiment using Omnipotent's Sentiment Analysis model")]
        public async Task AnalyzeSentimentAsync(InteractionContext ctx, [Option("text", "The text to analyze")] string text)
        {
            if (!OmniPaths.CheckIfOnServer())
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
                ModelInput sampleData = new ModelInput()
                {
                    Col0 = text,
                };

                // Make a single prediction on the sample data and print results
                var predictionResult = IsStatementPositiveOrNegative.IsStatementPositiveOrNegative.Predict(sampleData);

                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"Positive Statement Confidence Score: {predictionResult.Score[0] * 100}\n" +
                    $"Negative Statement Confidence Score: {predictionResult.Score[1] * 100}"));
            }
            else
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Feature not available on production build yet, sorry :("));
            }
        }

        [SlashCommand("serviceRestart", "Restarts a service")]
        public async Task ServiceRestartAsync(InteractionContext ctx, [Option("serviceName", "The service to restart")] string serviceName)
        {
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
            if (ctx.User.Id != OmniPaths.KlivesDiscordAccountID)
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("You do not have permission to use this command."));
                return;
            }
            if (serviceManager.GetServiceByName(serviceName) != null)
            {
                serviceManager.GetServiceByName(serviceName).RestartService();
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"Restarted service {serviceManager.GetServiceByName(serviceName).GetName()}"));
            }
            else
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"Couldn't find service by the name of {serviceName}"));
            }
        }
    }
}
