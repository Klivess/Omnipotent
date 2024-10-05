using Newtonsoft.Json;

namespace Omnipotent.Services.KliveTechHub
{
    public class KliveTechRoutes
    {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        private KliveTechHub p;
        public KliveTechRoutes(KliveTechHub parentService)
        {
            p = parentService;
        }

        public async Task RegisterRoutes()
        {
            p.serviceManager.GetKliveAPIService().CreateRoute("/klivetech/GetAllGadgets", async (req) =>
            {
                try
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(p.connectedGadgets));
                }
                catch (Exception ex)
                {
                    ErrorInformation er = new ErrorInformation(ex);
                    await req.ReturnResponse(JsonConvert.SerializeObject(er), code: System.Net.HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);
            p.serviceManager.GetKliveAPIService().CreateRoute("/klivetech/executegadgetaction", async (req) =>
            {
                //params: gadgetid, actionid, actionparams
                string id = req.userParameters["gadgetID"];
                string gadgetName = req.userParameters["gadgetName"];
                string actionName = req.userParameters["actionName"];
                string actionParams = req.userParameters["actionParam"];
                p.ServiceLog($"Request from {req.user.Name} to execute gadget '{gadgetName}' action '{actionName}' with param '{actionParams}'");
                KliveTechHub.KliveTechGadget g;
                if (string.IsNullOrEmpty(gadgetName))
                {
                    g = p.GetKliveTechGadgetByID(id);
                }
                else
                {
                    g = p.GetKliveTechGadgetByName(gadgetName);
                }
                p.ExecuteActionByName(g, actionName, actionParams);
                await req.ReturnResponse("Action executed successfully!");

            }, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Guest);
        }
    }
}
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
