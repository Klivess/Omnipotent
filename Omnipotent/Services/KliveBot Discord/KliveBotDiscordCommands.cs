using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Humanizer;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Omnipotent.Services.KliveTechHub;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Subsystem.Prediction;
using static IsStatementPositiveOrNegative.IsStatementPositiveOrNegative;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscordCommands : ApplicationCommandModule
    {
        public KliveBotDiscord parent { private get; set; }

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
            foreach (var item in parent.serviceManager.activeServices)
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
                var kt = (KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0];
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

        /*
        [SlashCommand("activateKlivetechAction", "Activates a klivetech action.")]
        public async Task ActivateKliveTechAction(InteractionContext ctx, [Option("Gadget Name", "The name of the gadget to execute.")] string gadgetName,
    [Option("Gadget Action", "The action to execute")] string gadgetAction,
    [Option("Gadget Parameter", "The data to send.")] string gadgetParameter)
        {
            try
            {
                if (((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).GetKliveTechGadgetByName(gadgetName) != null)
                {
                    if (((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).GetKliveTechGadgetByName(gadgetName).actions.Select(k => k.name).Contains(gadgetAction))
                    {
                        var gadget = ((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).GetKliveTechGadgetByName(gadgetName);
                        var action = gadget.actions.Where(k => k.name == gadgetAction).FirstOrDefault();
                        if (action != null)
                        {
                            if (action.parameters == KliveTechHub.KliveTechActions.ActionParameterType.String)
                            {
                                await ((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).ExecuteActionByName(gadget, action.name, gadgetParameter);
                            }
                            else if (action.parameters == KliveTechHub.KliveTechActions.ActionParameterType.Bool)
                            {
                                await ((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).ExecuteActionByName(gadget, action.name, gadgetParameter);
                            }
                            else if (action.parameters == KliveTechHub.KliveTechActions.ActionParameterType.Integer)
                            {
                                await ((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).ExecuteActionByName(gadget, action.name, int.Parse(gadgetParameter).ToString());
                            }
                            else if (action.parameters == KliveTechHub.KliveTechActions.ActionParameterType.None)
                            {
                                await ((KliveTechHub.KliveTechHub)(await parent.serviceManager.GetServiceByClassType<KliveTechHub.KliveTechHub>())[0]).ExecuteActionByName(gadget, action.name, "");
                            }
                            else
                            {
                                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Couldn't find gadget by the name of {gadgetName}"));
                                return;
                            }
                        }
                    }
                    else
                    {
                        await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Couldn't find action by the name of {gadgetAction}"));
                        return;
                    }
                }
                else
                {
                    await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Couldn't find gadget by the name of {gadgetName}"));
                    return;
                }
            }
            catch (Exception ex)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Kinda awkward but an error just occurred so I can't do this, sorry."));
            }
        }
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
            if (parent.serviceManager.GetServiceByName(serviceName) != null)
            {
                parent.serviceManager.GetServiceByName(serviceName).RestartService();
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"Restarted service {parent.serviceManager.GetServiceByName(serviceName).GetName()}"));
            }
            else
            {
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent($"Couldn't find service by the name of {serviceName}"));
            }
        }

        [SlashCommand("updatebot", "Updates the bot to the latest build in the Omnipotent github repository. Only Klives can do this")]
        public async Task UpdateBotAsync(InteractionContext ctx)
        {
            if (ctx.User.Id == OmniPaths.KlivesDiscordAccountID)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
                await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Restarting bot."));
                ExistentialBotUtilities.UpdateBot();
            }
            else
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Only Klives can use this command."));
            }
        }

        [SlashCommand("producelogs", "Serialises and sends logs to Klives. Only Klives can do this")]
        public async Task ProduceLogsAsync(InteractionContext ctx)
        {
            if (ctx.User.Id == OmniPaths.KlivesDiscordAccountID)
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
                try
                {
                    var copy = new List<OmniLogging.LoggedMessage>(parent.serviceManager.GetLogger().overallMessages.ToList());
                    string serial = JsonConvert.SerializeObject(copy, Formatting.Indented);
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Logs produced, sending to Klives."));
                    DiscordMessageBuilder dmb = new DiscordMessageBuilder();
                    string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveBotDiscordBotDirectory), "logs.json");
                    await parent.GetDataHandler().GetDataHandler().WriteToFile(path, serial);
                    var str = File.OpenRead(path);
                    dmb.AddFile("logs.json", str);
                    await (await parent.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives(dmb);
                    dmb.Clear();
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Logs sent to Klives."));
                    str.Close();
                    parent.GetDataHandler().DeleteFile(path);
                }
                catch (Exception e)
                {
                    ErrorInformation errorInformation = new ErrorInformation(e);
                    await ctx.EditResponseAsync(new DiscordWebhookBuilder().WithContent("Error occurred:\n\n" + errorInformation.FullFormattedMessage));
                }
            }
            else
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Only Klives can use this command."));
            }
        }

        [SlashCommand("produceliquidityplan", "Produces a liquidity strategy for the CS2 Arbitrage Bot.")]
        public async Task ProduceAndDisplayLiquidityPlan(InteractionContext ctx)
        {
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
            var cs2Service = (CS2ArbitrageBot.CS2ArbitrageBot)(await parent.serviceManager.GetServiceByClassType<CS2ArbitrageBot.CS2ArbitrageBot>())[0];
            var scanalytics = cs2Service.scanalytics;

            var liquidityPlan = scanalytics.ProduceLiquidityPlanAsync(await scanalytics.GetLatestLiquiditySearchResult(), cs2Service.steamBalance.Value.TotalBalanceInPounds);

            
        }


        [SlashCommand("GetCS2ArbitrageAnalytics", "Generates and returns the latest analytics for Klives's CS2 Arbitrage Strategy")]
        public async Task GetCS2ArbitrageAnalytics(InteractionContext ctx)
        {
            try
            {
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.DeferredChannelMessageWithSource);
                var cs2Service = (CS2ArbitrageBot.CS2ArbitrageBot)(await parent.serviceManager.GetServiceByClassType<CS2ArbitrageBot.CS2ArbitrageBot>())[0];
                var scanalytics = cs2Service.scanalytics;
                // Fix: Pass the required third argument (currentExpectedReturnCoefficientOfSteamToCSFloat)
                double currentExpectedReturnCoefficient = await scanalytics.ExpectedSteamToCSFloatConversionPercentage();
                CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.ScannedComparisonAnalytics analytics =
                    new CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics.ScannedComparisonAnalytics(
                        scanalytics.AllScannedComparisonsInHistory,
                        scanalytics.AllPurchasedListingsInHistory,
                        currentExpectedReturnCoefficient);

                string report = $@"
        [Arbitrage Analytics Report - Generated at {analytics.AnalyticsGeneratedAt}]
        Earliest Listing Recorded: {analytics.FirstListingDateRecorded}

        **Total Listings Scanned: {analytics.TotalListingsScanned}**

        --- Gain Buckets ---
        Listings with < 0% Gain: {analytics.NumberOfListingsBelow0PercentGain} (Avg Price: £{analytics.MeanPriceOfListingsBelow0PercentGain:F2})
        Listings with 0-5% Gain: {analytics.NumberOfListingsBetween0And5PercentGain} (Avg Price: £{analytics.MeanPriceOfListingsBetween0And5PercentGain:F2})
        Listings with 5-10% Gain: {analytics.NumberOfListingsBetween5And10PercentGain} (Avg Price: £{analytics.MeanPriceOfListingsBetween5And10PercentGain:F2})
        Listings with 10-20% Gain: {analytics.NumberOfListingsBetween10And20PercentGain} (Avg Price: £{analytics.MeanPriceOfListingsBetween10And20PercentGain:F2})
        Listings with > 20% Gain: {analytics.NumberOfListingsAbove20PercentGain} (Avg Price: £{analytics.MeanPriceOfListingsAbove20PercentGain:F2})

        --- Profitability Stats ---
        Listings with Positive Gain: {analytics.CountListingsWithPositiveGain}
        Listings with Negative Gain: {analytics.CountListingsWithNegativeGain}
        **Chance of Positive Gain: {analytics.PercentageChanceOfFindingPositiveGainListing:F2}%**
        Avg Gain of Profitable Listings: {analytics.MeanGainOfProfitableListings:F2}%
        Avg Float (Profitable): {analytics.MeanFloatValueOfProfitableListings:F5}
        Avg Price (Profitable): £{analytics.MeanPriceOfProfitableListings:F2}
        Avg Float (Unprofitable): {analytics.MeanFloatValueOfUnprofitableListings:F5}
        Avg Price (Unprofitable): £{analytics.MeanPriceOfUnprofitableListings:F2}

        --- Expected Returns ---
        Expected Return of All Snipes: {Math.Round((analytics.TotalExpectedProfitPercent - 1) * 100, 2)}%

        --- Top Opportunity ---
        Highest Predicted Gain Found: {Math.Round((analytics.HighestPredictedGainFoundSoFar - 1) * 100, 2)}%
        Item: {analytics.NameOfItemWithHighestPredictedGain}
        ";

                var embed = KliveBotDiscord.MakeSimpleEmbed("CS2 Arbitrage Analytics Report", report, DSharpPlus.Entities.DiscordColor.Green);
                // Edit response with the Embed
                await ctx.EditResponseAsync(new DSharpPlus.Entities.DiscordWebhookBuilder().AddEmbed(embed.Embed));
            }
            catch (Exception ex)
            {
                //Return apology message to 
                await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent("Kinda awkward but an error just occurred so I can't do this, sorry."));
                (await parent.serviceManager.GetKliveBotDiscordService()).ServiceLogError(ex);
            }
        }
    }
}
