using Humanizer;
using Microsoft.WSMan.Management;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;

namespace Omnipotent.Klives_Management.General_Analytics
{
    public class GeneralBotStatisticsService : OmniService
    {
        public FrontPageStatistics fpstats;
        private GeneralBotStatisticsServiceRoutes routes;
        public GeneralBotStatisticsService()
        {
            name = "General Bot Statistics Service (KM)";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override void ServiceMain()
        {
            GetGeneralBotStatistics();
            routes = new(this);
        }

        private async Task GetGeneralBotStatistics()
        {
            fpstats.lastOmnipotentUpdate = OmniPaths.LastOmnipotentUpdate;
            fpstats.lastOmnipotentUpdateHumanized = fpstats.lastOmnipotentUpdate.Humanize();
            fpstats.BotUptime = serviceManager.GetOverallUptime();
            fpstats.BotUptimeHumanized = fpstats.BotUptime.Humanize();
            fpstats.TotalStatusLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Status).Count();
            fpstats.TotalErrorLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Error).Count();
            fpstats.TotalLogs = serviceManager.GetLogger().overallMessages.Count();
            var service = ((OmniServiceMonitor)serviceManager.GetServiceByClassType<OmniServiceMonitor>()[0]);
            fpstats.RamUsagePercentageGB = (service.MemoryUsage.Bytes / service.TotalSystemRAM.Bytes) * 100;
            fpstats.CpuUsagePercentage = Convert.ToInt32(service.CPUUsagePercentage);
            fpstats.RamTotalGB = service.TotalSystemRAM.GigaBytes;


            await Task.Delay(5000);
            GetGeneralBotStatistics();
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
            public double RamTotalGB;
            public double RamUsagePercentageGB;
            public double CpuUsagePercentage;

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
