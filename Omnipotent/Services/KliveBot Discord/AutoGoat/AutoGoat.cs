using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using SteamKit2.Internal;
using System.Web.WebPages.Scope;

namespace Omnipotent.Services.KliveBot_Discord.AutoGoat
{
    public class AutoGoat : OmniService
    {
        public static string josueID = "1438288341886832875";
        public static string oliverID = "489871029100085248";
        public static string nourdinID = "976648966944989204";
        public static string bartuID = "720531645152755804";
        public static string alexID = "316456930316910593";
        public static string victorID = "437602988291391490";

        public AutoGoat()
        {
            name = "AutoGoat";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            await Task.Delay(5000);
            ServiceLog(GetName() + " is now watching.");
            (await serviceManager.GetKliveBotDiscordService()).Client.MessageCreated += AutoGoat_MessageCreated;
        }

        private async Task AutoGoat_MessageCreated(DiscordClient sender, MessageCreateEventArgs args)
        {
            Random rnd = new Random();

            //if in hypixel
            if (args.Guild.Id.ToString() == "802103827100467240")
            {
                var task = Task.Run(async () =>
                {
                    //if its josue
                    if (args.Author.Id.ToString() == josueID&&rnd.Next(0, 100) < 5)
                    {
                        await args.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, ":wacky:"));
                        ServiceLog(GetName() + " reacted to Josue's message in Hypixel with a goat emoji.");
                    }
                    if (args.Author.Id.ToString() == nourdinID && rnd.Next(0, 100) < 10)
                    {
                        await args.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, ":goat:"));
                        ServiceLog(GetName() + " reacted to Nourdin's message in Hypixel with a goat emoji.");
                    }
                    if (args.Author.Id.ToString() == alexID&&rnd.Next(0,100)<5)
                    {
                        await args.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, ":EyeofQuok:"));
                        ServiceLog(GetName() + " reacted to Alex's message in Hypixel with an eye of quok emoji.");
                    }
                    if(args.Author.Id.ToString() == victorID && rnd.Next(0, 100) < 5)
                    {
                        await args.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, ":nerd:"));
                        ServiceLog(GetName() + " reacted to Victor's message in Hypixel with a nerd emoji.");
                    }
                });
                task.Start();
            }
        }

        private async Task TypeWithRegionalIndicators(DiscordClient sender, MessageCreateEventArgs args, string text)
        {
            foreach(char c in text.ToLower())
            {
                try
                {
                    string emoji = $":regional_indicator_{c.ToString()}:";
                    await args.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, emoji));
                }
                catch (Exception e)   {
                    ServiceLogError(e, "Failed to react with letter " + c.ToString()+" with ");
                
                }
            }
        }
    }
}
