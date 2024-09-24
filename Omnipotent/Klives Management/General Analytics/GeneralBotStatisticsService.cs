using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;

namespace Omnipotent.Klives_Management.General_Analytics
{
    public class GeneralBotStatisticsService : OmniService
    {
        FrontPageStatistics fpstats;
        public GeneralBotStatisticsService()
        {
            name = "General Bot Statistics Service (KM)";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override void ServiceMain()
        {
            GetGeneralBotStatistics();
        }

        private async Task GetGeneralBotStatistics()
        {
            FrontPageStatistics stats;
            //Calculate general statistics
            stats = new FrontPageStatistics();
            stats.lastOmnipotentUpdate = OmniPaths.LastOmnipotentUpdate;
            stats.lastOmnipotentUpdateHumanized = stats.lastOmnipotentUpdate.Humanize();
            stats.BotUptime = serviceManager.GetOverallUptime();
            stats.BotUptimeHumanized = stats.BotUptime.Humanize();
            stats.TotalStatusLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Status).Count();
            stats.TotalErrorLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Error).Count();
            stats.TotalLogs = serviceManager.GetLogger().overallMessages.Count();


        }

        public class FrontPageStatistics
        {
            public DateTime lastOmnipotentUpdate;
            public string lastOmnipotentUpdateHumanized;
            public TimeSpan BotUptime;
            public string BotUptimeHumanized;
            public int TotalStatusLogs;
            public int TotalErrorLogs;
            public int TotalLogs;

            //KliveGadgets connected
            public string[] ConnectedKliveGadgets;

            //Omniscience
            public int TotalMessagesLogged;
            public int TotalImagesLogged;
            public int TotalVideosLogged;
            public int TotalFilesLogged;
            public int MessagesLoggedToday;

            //Next Task Scheduled
            public string NextTaskScheduledSummary;


        }
    }
}
