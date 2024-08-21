using DSharpPlus.SlashCommands;
using Omnipotent.Service_Manager;
using System.Diagnostics;

namespace Omnipotent.Services.KliveBot_Discord
{
    public class KliveBotDiscordCommands : ApplicationCommandModule
    {
        public OmniServiceManager parent { private get; set; }

        [SlashCommand("ping", "Replies with pong!")]
        public async Task PingAsync(InteractionContext ctx)
        {
            Stopwatch sw = new Stopwatch();
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent($"Pong - response time in {sw.Elapsed.Milliseconds} milliseconds."));
            sw.Stop();
        }


        [SlashCommand("uptimes", "Sends uptime of all processes")]
        public async Task UptimesAsync(InteractionContext ctx)
        {
            string uptimes = $"";
            foreach (var item in parent.activeServices)
            {
                uptimes += $"Service - {item.GetName()}: {item.GetServiceUptime().TotalHours} hours\n";
            }
            await ctx.CreateResponseAsync(DSharpPlus.InteractionResponseType.ChannelMessageWithSource, new DSharpPlus.Entities.DiscordInteractionResponseBuilder().WithContent(uptimes));
        }
    }
}
