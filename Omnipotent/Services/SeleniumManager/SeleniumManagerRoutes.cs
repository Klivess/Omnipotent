using Newtonsoft.Json;
using Omnipotent.Profiles;

namespace Omnipotent.Services.SeleniumManager
{
    public class SeleniumManagerRoutes
    {
        public SeleniumManager parent;

        public SeleniumManagerRoutes(SeleniumManager parent)
        {
            this.parent = parent;
        }

        public async void CreateRoutes()
        {
            await (await parent.serviceManager.GetKliveAPIService()).CreateRoute("seleniumManager/getAllSeleniumInstances", async (req) =>
            {
                string json = JsonConvert.SerializeObject(parent.GetCurrentActiveSeleniumInstances());

                await req.ReturnResponse(json, "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);
        }
    }
}
