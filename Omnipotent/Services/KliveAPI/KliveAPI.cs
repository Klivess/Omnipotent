using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Net;
using Newtonsoft.Json;
using System.Web;
using System.Collections.Specialized;


namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        public static int apiPORT = 7777;
        HttpListener listener = new HttpListener();

        public struct UserRequest
        {
            public HttpListenerContext context;
            public HttpListenerRequest req;
            public NameValueCollection userParameters;
            public async Task ReturnResponse(string response, string contentType = "text/plain")
            {
                HttpListenerResponse resp = context.Response;
                resp.Headers.Set("Content-Type", response);

                byte[] buffer = Encoding.UTF8.GetBytes(response);
                resp.ContentLength64 = buffer.Length;

                using Stream ros = resp.OutputStream;
                ros.Write(buffer, 0, buffer.Length);
            }
        }

        //Controller Lookup
        //Key: Route (example: /omniscience/getmessagecount)
        //Value: EventHandler<request, params(name, value)>
        public Dictionary<string, Action<UserRequest>> ControllerLookup;
        public KliveAPI()
        {
            name = "KliveAPI";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            try
            {
                ControllerLookup = new();

                listener = new();
                listener.Prefixes.Add($"http://127.0.0.1:{apiPORT}/");
                listener.Start();

                ServiceLog($"Listening on port {apiPORT}...");
                ServerListenLoop();
            }
            catch (Exception ex)
            {
                serviceManager.logger.LogError(name, ex, "KliveAPI Failed!");
            }
        }

        //Example of how to define a route
        //Action<KliveAPI.KliveAPI.UserRequest> lengthyBuffer = async (request) =>
        //  {
        //      await Task.Delay(10000);
        //      await request.ReturnResponse("BLAHAHHH" + RandomGeneration.GenerateRandomLengthOfNumbers(10));
        //  };
        //await serviceManager.GetKliveAPIService().CreateRoute("/omniscience/getmessagecount", getMessageCount);
        public async Task CreateRoute(string route, Action<UserRequest> handler)
        {
            while (!listener.IsListening) { }
            ControllerLookup.Add(route, handler);
            ServiceLog("New route created: " + route);
        }

        private async void ServerListenLoop()
        {
            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                HttpListenerRequest req = context.Request;
                string route = req.RawUrl;
                string query = req.Url.Query;
                NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(query);
                UserRequest request = new();
                request.req = req;
                request.context = context;
                request.userParameters = nameValueCollection;
                if (ControllerLookup.ContainsKey(route))
                {
                    ControllerLookup[route].Invoke(request);
                }
            }
        }

        private async void RespondToMessage(HttpListenerContext context, HttpListenerRequest request, string response, string contentType = "text/plain")
        {
            HttpListenerResponse resp = context.Response;
            resp.Headers.Set("Content-Type", response);

            byte[] buffer = Encoding.UTF8.GetBytes(response);
            resp.ContentLength64 = buffer.Length;

            using Stream ros = resp.OutputStream;
            ros.Write(buffer, 0, buffer.Length);
        }
    }
}