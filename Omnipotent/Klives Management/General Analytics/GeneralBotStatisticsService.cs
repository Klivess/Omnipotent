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
            routes = new GeneralBotStatisticsServiceRoutes(this);
        }

        private async Task GetGeneralBotStatistics()
        {
            try
            {
                fpstats = new();
                fpstats.lastOmnipotentUpdate = File.GetLastWriteTime(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe"));
                fpstats.lastOmnipotentUpdateHumanized = fpstats.lastOmnipotentUpdate.Humanize();
                fpstats.BotUptime = serviceManager.GetOverallUptime();
                fpstats.BotUptimeHumanized = fpstats.BotUptime.Humanize();
                fpstats.TotalStatusLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Status).Count();
                fpstats.TotalErrorLogs = serviceManager.GetLogger().overallMessages.Where(k => k.type == OmniLogging.LogType.Error).Count();
                fpstats.TotalLogs = serviceManager.GetLogger().overallMessages.Count();
                try
                {
                    fpstats.RamUsagePercentageGB = (serviceManager.GetMonitor().MemoryUsage.Bytes / serviceManager.GetMonitor().TotalSystemRAM.Bytes) * 100;
                    fpstats.CpuUsagePercentage = Convert.ToInt32(serviceManager.GetMonitor().CPUUsagePercentage);
                    fpstats.RamTotalGB = serviceManager.GetMonitor().TotalSystemRAM.GigaBytes;
                }
                catch (Exception ex) { }

                fpstats.TimeStatisticsGenerated = DateTime.Now;
                await Task.Delay(5000);
                GetGeneralBotStatistics();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                await Task.Delay(5000);
                GetGeneralBotStatistics();
            }
        }

        public class FrontPageStatistics
        {
            public DateTime TimeStatisticsGenerated;


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
            public int TotalOmniDiscordMessagesLogged;
            public int TotalOmniDiscordImagesLogged;
            public int TotalOmniDiscordVideosLogged;
            public int TotalOmniDiscordFilesLogged;
            public int OmniDiscordMessagesLoggedToday;

            //Next Task Scheduled
            public string NextTaskScheduledSummary;


        }
    }
}
