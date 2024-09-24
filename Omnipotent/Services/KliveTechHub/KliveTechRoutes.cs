﻿using Newtonsoft.Json;

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
                string actionName = req.userParameters["actionName"];
                string actionParams = req.userParameters["actionParam"];
                var g = p.GetKliveTechGadgetByID(id);
                if (await p.ExecuteActionByName(g, actionName, actionParams))
                {
                    await req.ReturnResponse("Action executed successfully!");
                }
                else
                {
                    await req.ReturnResponse("Action failed to execute!", code: System.Net.HttpStatusCode.InternalServerError);
                }

            }, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);
        }
    }
}
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed