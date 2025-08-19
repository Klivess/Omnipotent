using Omnipotent.Service_Manager;
using System.Net;
using System.Collections.Concurrent;


namespace Omnipotent.Services.MemeScraper
{
    public class MemeScraper : OmniService
    {
        MemeScraperSources SourceManager;
        public MemeScraper()
        {
            name = "MemeScraper";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        protected override async void ServiceMain()
        {
            SourceManager = new MemeScraperSources(this);




        }
    }
}
