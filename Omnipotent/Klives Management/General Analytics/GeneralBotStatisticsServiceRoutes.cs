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
            (await this.g.serviceManager.GetKliveAPIService()).CreateRoute("/GeneralBotStatistics/GetFrontpageStats", async (req) =>
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
        }
    }
}
