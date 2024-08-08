using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.Omniscience.DiscordInterface;
using System.Diagnostics;
using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;

namespace Omnipotent.Services.Omniscience
{
    public class Omniscience : OmniService
    {
        public Omniscience()
        {
            name = "Omniscience";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            DiscordCrawl crawl = new(serviceManager);

        }
    }
}
