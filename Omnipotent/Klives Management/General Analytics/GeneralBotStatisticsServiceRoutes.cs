using Newtonsoft.Json;

namespace Omnipotent.Klives_Management.General_Analytics
{
    public class GeneralBotStatisticsServiceRoutes
    {
        private GeneralBotStatisticsService g;
        public GeneralBotStatisticsServiceRoutes(GeneralBotStatisticsService generalBotStatisticsService)
        {
            this.g = generalBotStatisticsService;
            CreateRoutes();
        }

        public async void CreateRoutes()
        {
            var api = await g.serviceManager.GetKliveAPIService();

            // Full statistics snapshot (all data in one call – ideal for the 5-second dashboard refresh)
            api.CreateRoute("/GeneralBotStatistics/GetFrontpageStats", async (req) =>
            {
                try
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(g.fpstats));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);

            // Lightweight hardware-only summary (CPU, RAM, disks, network)
            api.CreateRoute("/GeneralBotStatistics/GetHardwareStats", async (req) =>
            {
                try
                {
                    var stats = g.fpstats;
                    var hw = new
                    {
                        stats?.OSVersion,
                        stats?.MachineName,
                        stats?.ProcessorName,
                        stats?.ProcessorCount,
                        stats?.CpuUsagePercentage,
                        stats?.RamTotalGB,
                        stats?.RamUsedGB,
                        stats?.RamUsagePercentage,
                        stats?.DiskStatistics,
                        stats?.NetworkInterfaces
                    };
                    await req.ReturnResponse(JsonConvert.SerializeObject(hw));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);

            // Services overview
            api.CreateRoute("/GeneralBotStatistics/GetServicesStats", async (req) =>
            {
                try
                {
                    var stats = g.fpstats;
                    var svc = new
                    {
                        stats?.TotalServicesRegistered,
                        stats?.TotalServicesActive,
                        stats?.Services
                    };
                    await req.ReturnResponse(JsonConvert.SerializeObject(svc));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);

            // Process / GC diagnostics
            api.CreateRoute("/GeneralBotStatistics/GetProcessStats", async (req) =>
            {
                try
                {
                    var stats = g.fpstats;
                    var proc = new
                    {
                        stats?.ProcessMemoryMB,
                        stats?.ProcessThreadCount,
                        stats?.GCTotalMemoryMB,
                        stats?.GCGen0Collections,
                        stats?.GCGen1Collections,
                        stats?.GCGen2Collections
                    };
                    await req.ReturnResponse(JsonConvert.SerializeObject(proc));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);
        }
    }
}
