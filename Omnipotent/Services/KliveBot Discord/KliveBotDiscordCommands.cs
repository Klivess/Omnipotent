using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveTechHub;
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
                uptimes += $"Service - {item.GetName()}: {(item.IsServiceActive() ? item.GetServiceUptime().Humanize() : "Inactive")}\n";
            }
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent(uptimes));
        }
        [SlashCommand("getConnectedKliveTechGadgets", "Gets all connected KliveTech gadgets and their actions.")]
        public async Task GetKliveTechGadgets(InteractionContext ctx)
        {
            try
            {
                string gadgets = "";
                var kt = (KliveTechHub.KliveTechHub)serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>()[0];
                foreach (var item in kt.connectedGadgets)
                {
                    gadgets += $"**Gadget: {item.name}**";
                    foreach (var action in item.actions)
                    {
                        gadgets += $"\nAction: {action.name} Description: {action.paramDescription}, Accepted Parameter Type: {action.parameters.ToString()}";
                    }
                    gadgets += "\n\n";
                }
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent(gadgets));
            }
            catch (Exception ex)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Kinda awkward but an error just occurred so I can't do this, sorry."));
            }
        }

        [SlashCommand("quitKliveBot", "Quits KliveBot, only Klives can do this.")]
        public async Task QuitKliveBot(InteractionContext ctx)
        {
            if (ctx.Member.Id == OmniPaths.KlivesDiscordAccountID)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Quitting KliveBot..."));
                ExistentialBotUtilities.QuitBot();
            }
            else
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Only Klives can use this command."));
            }
        }

        [SlashCommand("activateKlivetechAction", "Activates a klivetech action.")]
        public async Task ActivateKliveTechAction(InteractionContext ctx, [Option("Gadget Name", "The name of the gadget to execute.")] string gadgetName,
    [Option("Gadget Action", "The action to execute")] string gadgetAction,
    [Option("Gadget Parameter", "The data to send.")] string gadgetParameter)
        {
            try
            {
                var kt = (KliveTechHub.KliveTechHub)serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>()[0];
                if (kt.connectedGadgets.Select(k => k.name).Contains(gadgetName))
                {
                    var gadget = kt.connectedGadgets.Where(k => k.name == gadgetName).First();
                    if (gadget.actions.Select(k => k.name).Contains(gadgetAction))
                    {
                        var action = gadget.actions.Where(k => k.name == gadgetAction).First();
                        if (action.parameters == KliveTechActions.ActionParameterType.Bool)
                        {
                            if (gadgetParameter == "true" || gadgetParameter == "false")
                            {
                                kt.ExecuteActionByName(gadget, gadgetAction, gadgetParameter);
                                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Executed action {gadgetAction} on gadget {gadgetName} with parameter {gadgetParameter}"));
                            }
                            else
                            {
                                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Invalid parameter value, please use 'true' or 'false'"));
                            }
                        }
                        else if (action.parameters == KliveTechActions.ActionParameterType.Integer)
                        {
                            if (int.TryParse(gadgetParameter, out int result))
                            {
                                //Execute action.
                                kt.ExecuteActionByName(gadget, gadgetAction, gadgetParameter);
                                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Executed action {gadgetAction} on gadget {gadgetName} with parameter {gadgetParameter}"));
                            }
                            else
                            {
                                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Invalid parameter value, please use an integer."));
                            }
                        }
                        else if (action.parameters == KliveTechActions.ActionParameterType.String)
                        {
                            kt.ExecuteActionByName(gadget, gadgetAction, gadgetParameter);
                            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Executed action {gadgetAction} on gadget {gadgetName} with parameter {gadgetParameter}"));
                        }
                    }
                    else
                    {
                        await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Gadget {gadget.name} found, but not an action with the name '{gadgetAction}'."));
                    }
                }
                else
                {
                    await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Gadget with the name '{gadgetName}' not found or not connected."));
                }
            }
            catch (Exception ex)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Kinda awkward but an error just occurred so I can't do this, sorry."));
            }
        }
        */

        /*
        [SlashCommand("messageUser", "Sends a message to a user")]
        public async Task MessageUserDirectly(InteractionContext ctx, [Option("userID", "The user to message")] string id, [Option("messageContent", "The content of the message")] string message)
        {
            if (ctx.Member.Id == OmniPaths.KlivesDiscordAccountID)
            {
                //Get member by ID and send message to them
                var member = await ctx.Guild.GetMemberAsync(ulong.Parse(id));
                await member.SendMessageAsync(message);
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Done!"));
            }
            else
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Only Klives can use this command."));
            }
        }
        */

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
