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
using Omnipotent.Profiles;
using System.Management.Automation.Runspaces;
using static Omnipotent.Profiles.KMProfileManager;
using System.Security.Cryptography.X509Certificates;
using System.Management.Automation;
using System.Diagnostics;


namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        public static int apiPORT = 7777;
        public static int apiHTTPPORT = 5000;
        public static string domainName = "klive.dev"; //This is the domain name that the SSL certificate will be signed for. It should be the same as the domain name that the API will be accessed from.
        public HttpListener listener = new HttpListener();
        private bool ContinueListenLoop = true;
        private Task<HttpListenerContext> getContextTask;

        public struct RouteInfo
        {
            public Action<UserRequest> action;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
            public HttpMethod method;
        }
        public struct UserRequest
        {
            public HttpListenerContext context;
            public HttpListenerRequest req;
            public NameValueCollection userParameters;
            public KMProfileManager.KMProfile? user;
            public string userMessageContent;
            public async Task ReturnResponse(string response, string contentType = "application/json", NameValueCollection headers = null, HttpStatusCode code = HttpStatusCode.OK)
            {
                HttpListenerResponse resp = context.Response;
                if (contentType == "application/json")
                {
                    if (OmniPaths.IsValidJson(response) != true)
                    {
                        response = JsonConvert.SerializeObject(response);
                    }
                }
                resp.Headers.Set("Content-Type", contentType);
                if (headers != null)
                {
                    for (global::System.Int32 i = 0; i < headers.Count; i++)
                    {
                        resp.Headers.Add(headers.GetKey(i), headers.Get(i));
                    }
                }
                resp.Headers.Add("Access-Control-Allow-Origin", "*");

                byte[] buffer = Encoding.UTF8.GetBytes(response);
                resp.ContentLength64 = buffer.Length;
                resp.StatusCode = (int)code;
                using Stream ros = resp.OutputStream;
                ros.Write(buffer, 0, buffer.Length);
            }
        }

        //Controller Lookup
        //Key: Route (example: /omniscience/getmessagecount)
        //Value: EventHandler<route, routeInfo>
        public Dictionary<string, RouteInfo> ControllerLookup;

        private KMProfileManager profileManager;

        private CertificateInstaller certInstaller;
        public KliveAPI()
        {
            name = "KliveAPI";
            threadAnteriority = ThreadAnteriority.Critical;
        }
        protected override async void ServiceMain()
        {
            try
            {
                //await CheckForSSLCertificate();

                //Create API listener
                ControllerLookup = new();

                listener = new();
                listener.Prefixes.Add($"https://+:{apiPORT}/");
                listener.Prefixes.Add($"http://+:{apiHTTPPORT}/");
                listener.Prefixes.Add($"https://{domainName}:{apiPORT}/");

                ServiceQuitRequest += KliveAPI_ServiceQuitRequest;

                await CheckForSSLCertificate();
                await LinkSSLCertificate(certInstaller.rootAuthorityCrtPath);

                listener.Start();

                ServiceLog($"Listening on: {string.Join(", ", listener.Prefixes)}");

                ServerListenLoop();
                //Create profile manager
                serviceManager.CreateAndStartNewMonitoredOmniService(new KMProfileManager());
                profileManager = (KMProfileManager)serviceManager.GetServiceByClassType<KMProfileManager>()[0];

                CreateMetaKLIVEAPIRoutes();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "KliveAPI Failed!");
            }
        }

        private async void CreateMetaKLIVEAPIRoutes()
        {
            await CreateRoute("/redirect", async (req) =>
            {
                string url = req.userParameters.Get("redirectURL");
                string code = $"<script>window.location.replace('{url}');</script>";
                await req.ReturnResponse(code, "text/html");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);
            await CreateRoute("/ping", async (req) =>
            {
                await req.ReturnResponse("Pong", "text/html");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);
        }

        private void KliveAPI_ServiceQuitRequest()
        {
            ServiceLog("Stopping KliveAPI listener, as service is quitting.");
            ContinueListenLoop = false;
            listener.Stop();
        }

        private async Task CheckForSSLCertificate()
        {
            certInstaller = new(this);
            if (!(await certInstaller.IsCertificateCreated()))
            {
                await certInstaller.CreateInstallCert(10, "klives", "KliveAPI");
            }
        }

        private async Task LinkSSLCertificate(string pathToCrt)
        {
            // Load the certificate from the .crt file
            string crtFilePath = pathToCrt;

            X509Certificate2 certificate = new X509Certificate2(crtFilePath);

            // Get the certificate hash (thumbprint)
            string certHash = certificate.Thumbprint;

            string script = $"http add sslcert ipport=0.0.0.0:{apiPORT} certhash={certHash} appid={{86476d42-f4f3-48f5-9367-ff60f2ed2cdc}}";
            // Set up the process start info
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = script,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Start the process
            Process process = new Process();
            process.StartInfo = processInfo;
            process.Start();

            // Read the output and errors
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
        }

        //Example of how to define a route
        //Action<KliveAPI.KliveAPI.UserRequest> lengthyBuffer = async (request) =>
        //  {
        //      //Do work and stuff
        //      await Task.Delay(10000);
        //      //Return a response
        //      await request.ReturnResponse("BLAHAHHH" + RandomGeneration.GenerateRandomLengthOfNumbers(10));
        //  };
        //await serviceManager.GetKliveAPIService().CreateRoute("/omniscience/getmessagecount", getMessageCount);
        public async Task CreateRoute(string route, Action<UserRequest> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired)
        {
            while (!listener.IsListening) { Task.Delay(10).Wait(); }
            if (!route.StartsWith('/'))
            {
                //Add a / to the beginning of the route if it doesn't have one
                route = "/" + route;
            }
            RouteInfo routeInfo = new();
            routeInfo.action = handler;
            routeInfo.authenticationLevelRequired = authenticationLevelRequired;
            routeInfo.method = method;
            ControllerLookup.Add(route, routeInfo);
            ServiceLog($"New {method.ToString().ToUpper()} route created: " + route);
        }

        private async void ServerListenLoop()
        {
            while (ContinueListenLoop)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    HttpListenerRequest req = context.Request;
                    string route = req.RawUrl;
                    string query = req.Url.Query;
                    NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(query);
                    UserRequest request = new();
                    request.req = req;
                    request.context = context;
                    StreamReader reader = new StreamReader(request.req.InputStream, Encoding.UTF8);
                    request.userMessageContent = await reader.ReadToEndAsync();
                    request.userParameters = nameValueCollection;
                    request.user = null;
                    if (!string.IsNullOrEmpty(query))
                    {
                        route = route.Replace(query, "");
                    }
                    if (req.Headers.AllKeys.Contains("Authorization"))
                    {
                        string password = req.Headers.Get("Authorization");
                        var profileManager = ((KMProfileManager)(serviceManager.GetServiceByClassType<KMProfileManager>()[0]));
                        if (profileManager.CheckIfProfileExists(password))
                        {
                            request.user = await profileManager.GetProfileByPassword(password);
                        }
                        else
                        {
                            request.user = null;
                        }
                    }
                    if (ControllerLookup.ContainsKey(route))
                    {
                        RouteInfo routeData = ControllerLookup[route];
                        if (req.HttpMethod != routeData.method.ToString())
                        {
                            ServiceLog($"Route {route} has been requested with an incorrect HTTP method.");
                            DenyRequest(request, DeniedRequestReason.IncorrectHTTPMethod);
                        }
                        else
                        {
                            if (routeData.authenticationLevelRequired == KMProfileManager.KMPermissions.Anybody)
                            {
                                //ServiceLog($"Unauthenticated route {route} has been requested.");
                                ControllerLookup[route].action.Invoke(request);
                            }
                            else
                            {
                                if (request.user != null)
                                {
                                    if (request.user.KlivesManagementRank >= routeData.authenticationLevelRequired)
                                    {
                                        ServiceLog($"{request.user.Name} requested an authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()}.");
                                        ControllerLookup[route].action.Invoke(request);
                                    }
                                    else
                                    {
                                        ServiceLog($"{request.user.Name} requested an authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()}, but requester doesn't have permission.");
                                        DenyRequest(request, DeniedRequestReason.TooLowClearance);
                                    }
                                }
                                else
                                {
                                    ServiceLog($"Authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()} has been requested, but requester doesn't have an account. Scary!!");
                                    DenyRequest(request, DeniedRequestReason.NoProfile);
                                }
                            }
                        }
                    }
                    else
                    {
                        await request.ReturnResponse("Route not found", "text/plain", null, HttpStatusCode.NotFound);
                    }
                }
                catch (Exception ioe)
                {
                    ServiceLogError(ioe, "Error in ServerListenLoop");
                    ServerListenLoop();
                }
            }
        }
        private enum DeniedRequestReason
        {
            NoProfile = 0,
            InvalidPassword = 1,
            TooLowClearance = 2,
            IncorrectHTTPMethod = 3
        }
        private async void DenyRequest(UserRequest request, DeniedRequestReason reason)
        {
            NameValueCollection headers = new();
            headers.Add("RequestDeniedReason", reason.ToString());
            headers.Add("RequestDeniedCode", ((int)reason).ToString());
            await request.ReturnResponse("Access Denied: " + reason, "text/plain", headers, HttpStatusCode.Unauthorized);
        }
    }
}