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
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.VisualBasic.FileIO;
using DSharpPlus.Entities;
using Org.BouncyCastle.Asn1.IsisMtt.Ocsp;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Crypto;
using Microsoft.PowerShell.Commands;
using OmniDefenceService = Omnipotent.Services.OmniDefence.OmniDefence;
using Omnipotent.Services.OmniDefence;


namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        public static int apiPORT = 443;
        public static int apiHTTPPORT = 5000;
        public static string domainName = "klive.dev"; //This is the domain name that the SSL certificate will be signed for. It should be the same as the domain name that the API will be accessed from.
        public HttpListener listener = new HttpListener();
        private bool ContinueListenLoop = true;
        private Task<HttpListenerContext> getContextTask;

        public struct RouteInfo
        {
            public Func<UserRequest, Task> action;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
            public HttpMethod method;
            public string normalizedMethod;

            public List<TimeSpan> TimeTakenToProcess;
            public async Task InvokeAction(UserRequest request)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                await action(request);
                stopwatch.Stop();
                if (TimeTakenToProcess == null)
                {
                    TimeTakenToProcess = new List<TimeSpan>();
                }
                TimeTakenToProcess.Add(stopwatch.Elapsed);
            }
        }
        public struct WebSocketRouteInfo
        {
            public Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task> handler;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
        }

        public struct UserRequest
        {
            public string route;
            public HttpListenerContext context;
            public HttpListenerRequest req;
            public NameValueCollection userParameters;
            public KMProfileManager.KMProfile? user;
            public string userMessageContent;
            public byte[] userMessageBytes;

            [JsonIgnore]
            public KliveAPI ParentService;
            public async Task ReturnResponse(string response, string contentType = "application/json", NameValueCollection headers = null, HttpStatusCode code = HttpStatusCode.OK)
            {
                try
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
                    if (req.HttpMethod == "OPTIONS")
                    {
                        resp.Headers.Add("Access-Control-Allow-Headers", "*");
                        resp.Headers.Add("Access-Control-Allow-Methods", "*");
                        resp.Headers.Add("Access-Control-Max-Age", "1728000");
                        resp.Headers.Add("Access-Control-Expose-Headers", "*");
                    }
                    resp.Headers.Add("Access-Control-Allow-Origin", "*");
                    resp.Headers.Add("Access-Control-Expose-Headers", "*");

                    byte[] buffer = Encoding.UTF8.GetBytes(response);
                    resp.ContentLength64 = buffer.Length;
                    resp.StatusCode = (int)code;
                    using Stream ros = resp.OutputStream;
                    await ros.WriteAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    ParentService.ServiceLogError(ex, "Error while returning response for route: " + context.Request.RawUrl);
                    try
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        context.Response.ContentType = "text/plain";
                        byte[] errorBytes = Encoding.UTF8.GetBytes("Error occurred on server.");
                        context.Response.ContentLength64 = errorBytes.Length;
                        await context.Response.OutputStream.WriteAsync(errorBytes);
                        context.Response.Close();
                    }
                    catch
                    {
                        // Response stream already closed, nothing more we can do
                    }
                }
            }

            public async Task ReturnBinaryResponse(byte[] data, string contentType, HttpStatusCode code = HttpStatusCode.OK, NameValueCollection headers = null)
            {
                try
                {
                    HttpListenerResponse resp = context.Response;
                    resp.ContentType = contentType;
                    resp.StatusCode = (int)code;
                    resp.ContentLength64 = data.Length;
                    resp.Headers.Add("Access-Control-Allow-Origin", "*");
                    resp.Headers.Add("Access-Control-Expose-Headers", "*");
                    if (headers != null)
                    {
                        for (int i = 0; i < headers.Count; i++)
                        {
                            resp.Headers.Add(headers.GetKey(i), headers.Get(i));
                        }
                    }
                    using Stream ros = resp.OutputStream;
                    await ros.WriteAsync(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    ParentService.ServiceLogError(ex, "Error while returning binary response for route: " + context.Request.RawUrl);
                }
            }

            public Stream PrepareStreamResponse(string contentType, long contentLength, HttpStatusCode code = HttpStatusCode.OK, NameValueCollection headers = null)
            {
                HttpListenerResponse resp = context.Response;
                resp.ContentType = contentType;
                resp.StatusCode = (int)code;
                resp.ContentLength64 = contentLength;
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Expose-Headers", "*");
                if (headers != null)
                {
                    for (int i = 0; i < headers.Count; i++)
                    {
                        resp.Headers.Add(headers.GetKey(i), headers.Get(i));
                    }
                }
                return resp.OutputStream;
            }
        }

        //Controller Lookup
        //Key: Route (example: /omniscience/getmessagecount)
        //Value: EventHandler<route, routeInfo>
        public ConcurrentDictionary<string, RouteInfo> ControllerLookup;
        public ConcurrentDictionary<string, WebSocketRouteInfo> WebSocketRouteLookup;

        private KMProfileManager profileManager;
        private KliveApiStatisticsStore? apiStatistics;

        private CertificateInstaller certInstaller;
        public KliveAPI()
        {
            name = "KliveAPI";
            threadAnteriority = ThreadAnteriority.Critical;
            ControllerLookup = new ConcurrentDictionary<string, RouteInfo>(StringComparer.OrdinalIgnoreCase);
            WebSocketRouteLookup = new ConcurrentDictionary<string, WebSocketRouteInfo>(StringComparer.OrdinalIgnoreCase);
        }
        protected override async void ServiceMain()
        {
            try
            {
                //await CheckForSSLCertificate();

                ContinueListenLoop = true;
                apiStatistics = new KliveApiStatisticsStore(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAPIStatisticsFile));
                await apiStatistics.InitializeAsync();

                listener = new();
                //listener.Prefixes.Add($"https://+:{apiPORT}/");
                listener.Prefixes.Add($"http://+:{apiHTTPPORT}/");
                listener.Prefixes.Add($"https://+:{apiPORT}/");

                ServiceQuitRequest += KliveAPI_ServiceQuitRequest;

                ServiceLog($"Checking SSL Certificates ");
                await CheckForSSLCertificate();
                await LinkSSLCertificate(certInstaller.rootAuthorityPfxPath);

                listener.Start();

                ServiceLog($"Listening on: {string.Join(", ", listener.Prefixes)}");

                ServerListenLoop();
                //Create profile manager
                CreateAndStartService(new KMProfileManager());
                profileManager = (KMProfileManager)(await GetServicesByType<KMProfileManager>())[0];

                //Create KliveLink remote administration service
                CreateAndStartService(new KliveLink.KliveLinkService());

                CreateMetaKLIVEAPIRoutes();

                StartWatchdog();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "KliveAPI Failed!");
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", "KliveAPI Failed to start! Error Info: " + new ErrorInformation(ex).FullFormattedMessage);
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
            await CreateRoute("/allRoutes", async (req) =>
            {
                var copy = ControllerLookup.ToDictionary();
                string resp = JsonConvert.SerializeObject(copy);
                await req.ReturnResponse(resp, "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Associate);
            await CreateRoute("/KliveAPI/Statistics", async (req) =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(apiStatistics?.GetSummary() ?? new
                {
                    lifetime = new
                    {
                        totalRequests = 0,
                        successfulRequests = 0,
                        clientErrorRequests = 0,
                        serverErrorRequests = 0,
                        notFoundRequests = 0,
                        unauthorizedRequests = 0,
                        avgResponseMs = 0,
                        maxResponseMs = 0,
                        availabilityPct = 100,
                        lastRequestAt = (DateTime?)null
                    },
                    historyWindow = new
                    {
                        firstDay = (string?)null,
                        lastDay = (string?)null,
                        totalDays = 0
                    },
                    dailyHistory = Array.Empty<object>(),
                    topRoutes = Array.Empty<object>(),
                    slowestRoutes = Array.Empty<object>()
                }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);
        }

        private async void StartWatchdog()
        {
            using System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            await Task.Delay(15000); // Give the API time to initialize

            while (ContinueListenLoop)
            {
                try
                {
                    var response = await client.GetAsync($"http://127.0.0.1:{apiHTTPPORT}/ping");
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    if (ContinueListenLoop)
                    {
                        await ServiceLogError(ex, "Watchdog detected API is unresponsive. Restarting API service...");
                        _ = RestartService();
                        return;
                    }
                }

                try
                {
                    await Task.Delay(30000, cancellationToken.Token);
                }
                catch (TaskCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
            }
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
        private async Task LinkSSLCertificate(string pathToPfx)
        {
            string logDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesCertificateLinkingLogsDirectory);
            string logFilePath = Path.Combine(logDirectory, $"CertificateLinkingLog_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            //Delete existing SSL certificate linkage
            string deleteOutput = ExistentialBotUtilities.SendTerminalCommand("netsh", "http delete sslcert hostnameport=klive.dev:443");
            // Log output to a file
            File.AppendAllText(logFilePath, $"[{DateTime.Now}] Delete Output:\n{deleteOutput}\n\n");

            var certificate = new X509Certificate2(
                pathToPfx,
                "klives",
                X509KeyStorageFlags.MachineKeySet |  // Critical for system-wide access  
                X509KeyStorageFlags.PersistKeySet
            );
            //(serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Linking Certificate with Thumbprint: " + certificate.Thumbprint);
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(certificate); // Install to Local Machine store  
                store.Close();
            }
            string script;
            if (OmniPaths.CheckIfOnServer())
            {
                script = $"http add sslcert hostnameport={domainName}:{apiPORT} certhash={certificate.Thumbprint} appid={{86476d42-f4f3-48f5-9367-ff60f2ed2cdc}} certstorename=MY";
            }
            else
            {
                script = $"http add sslcert ipport=0.0.0.0:{apiPORT} certhash={certificate.Thumbprint} appid={{86476d42-f4f3-48f5-9367-ff60f2ed2cdc}}";
            }
            ServiceLog("Running terminal command: " + script);
            string output = ExistentialBotUtilities.SendTerminalCommand("netsh", script);
            // Log output to a file          
            File.AppendAllText(logFilePath, $"[{DateTime.Now}] Output:\n{output}\n\n");
            DiscordMessageBuilder builder = new DiscordMessageBuilder();
            builder.WithContent("SSL Certificate Linking Output. \n\n Expiration date of certificate: " + certificate.GetExpirationDateString());
            Stream fileStream = File.Open(logFilePath, FileMode.Open);
            builder.AddFile("SSLCertificateLinkingOutput.txt", fileStream);
            //serviceManager.GetKliveBotDiscordService().SendMessageToKlives(builder);
            fileStream.Close();
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
        public async Task CreateRoute(string route, Func<UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired)
        {
            route = NormalizeRoute(route);
            RouteInfo routeInfo = new();
            routeInfo.action = handler;
            routeInfo.authenticationLevelRequired = authenticationLevelRequired;
            routeInfo.method = method;
            routeInfo.normalizedMethod = NormalizeMethod(method.Method);
            if (ControllerLookup.TryAdd(route, routeInfo))
            {
                ServiceLog($"New {method.ToString().ToUpper()} route created: " + route);
            }
        }

        public async Task CreateWebSocketRoute(string route, Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task> handler, KMProfileManager.KMPermissions authenticationLevelRequired)
        {
            route = NormalizeRoute(route);
            var info = new WebSocketRouteInfo
            {
                handler = handler,
                authenticationLevelRequired = authenticationLevelRequired
            };
            if (WebSocketRouteLookup.TryAdd(route, info))
            {
                ServiceLog($"New WebSocket route created: {route}");
            }
        }

        private static string NormalizeRoute(string route)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                return "/";
            }

            int queryStart = route.IndexOf('?');
            if (queryStart >= 0)
            {
                route = route[..queryStart];
            }

            route = route.Trim();
            if (!route.StartsWith('/'))
            {
                route = "/" + route;
            }

            if (route.Length > 1)
            {
                route = route.TrimEnd('/');
            }

            return route;
        }

        private static string NormalizeMethod(string method)
        {
            return (method ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static bool CanRequestCarryBody(string method)
        {
            var normalizedMethod = NormalizeMethod(method);
            return normalizedMethod == "POST" || normalizedMethod == "PUT" || normalizedMethod == "PATCH";
        }

        private static bool IsRequestMethodAllowed(string requestMethod, string routeMethod)
        {
            string normalizedRequestMethod = NormalizeMethod(requestMethod);
            string normalizedRouteMethod = NormalizeMethod(routeMethod);
            return normalizedRequestMethod == normalizedRouteMethod
                || (normalizedRequestMethod == "HEAD" && normalizedRouteMethod == "GET");
        }

        private static bool ShouldResolveUser(HttpListenerRequest req, KMPermissions requiredPermission)
        {
            return requiredPermission != KMPermissions.Anybody || !string.IsNullOrWhiteSpace(req.Headers["Authorization"]);
        }

        private static bool IsWebsiteClientRequest(HttpListenerRequest req)
        {
            string client = req.Headers["X-Klive-Client"] ?? string.Empty;
            if (string.Equals(client, "website", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(req.Headers["X-Klive-Page"])) return true;

            string origin = req.Headers["Origin"] ?? string.Empty;
            string referer = req.Headers["Referer"] ?? string.Empty;
            return !string.IsNullOrWhiteSpace(origin)
                || !string.IsNullOrWhiteSpace(referer)
                || origin.Contains(domainName, StringComparison.OrdinalIgnoreCase)
                || referer.Contains(domainName, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<KMProfileManager.KMProfile?> ResolveRequestUserAsync(HttpListenerRequest req)
        {
            string password = req.Headers["Authorization"];
            if (string.IsNullOrWhiteSpace(password) || profileManager == null)
            {
                return null;
            }

            return await profileManager.GetProfileByPassword(password);
        }

        private async Task<(byte[] BodyBytes, string BodyText)> ReadRequestBodyAsync(HttpListenerRequest req)
        {
            if (!req.HasEntityBody || !CanRequestCarryBody(req.HttpMethod))
            {
                return (Array.Empty<byte>(), string.Empty);
            }

            int bodyCapacity = req.ContentLength64 > 0 && req.ContentLength64 <= int.MaxValue
                ? (int)req.ContentLength64
                : 0;

            using MemoryStream bodyStream = bodyCapacity > 0 ? new MemoryStream(bodyCapacity) : new MemoryStream();
            await req.InputStream.CopyToAsync(bodyStream);
            byte[] bodyBytes = bodyStream.ToArray();
            string bodyText = bodyBytes.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(bodyBytes);

            return (bodyBytes, bodyText);
        }

        private async void ServerListenLoop()
        {
            while (ContinueListenLoop)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (Exception ioe)
                {
                    if (ContinueListenLoop)
                    {
                        _ = ServiceLogError(ioe, "Error in ServerListenLoop");
                        await Task.Delay(1000); // Prevent tight spin if listener completely breaks
                    }
                }
            }
        }

        private OmniDefenceService? _defenceCache;
        private OmniDefenceService? TryGetDefence()
        {
            if (_defenceCache != null && _defenceCache.IsServiceActive()) return _defenceCache;
            try
            {
                _defenceCache = GetActiveServices().OfType<OmniDefenceService>().FirstOrDefault();
                return _defenceCache;
            }
            catch { return null; }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            Stopwatch requestStopwatch = Stopwatch.StartNew();
            bool matchedRoute = false;
            bool shouldRecordStatistics = true;
            string statsRoute = context?.Request?.Url?.AbsolutePath ?? context?.Request?.RawUrl ?? "/";
            string statsMethod = NormalizeMethod(context?.Request?.HttpMethod ?? string.Empty);

            // OmniDefence telemetry locals (captured into finally)
            string defenceIp = "";
            string? defenceUserAgent = null;
            string? defenceQueryString = null;
            string? defenceBodyHash = null;
            long defenceBodyLength = 0;
            int defencePermRequired = 0;
            string? defenceProfileId = null;
            string? defenceProfileName = null;
            int? defenceProfileRank = null;
            string? defenceDenyReason = null;
            string defenceRequestOrigin = "DirectApi";
            string? defenceClientPage = null;
            bool defenceFromWebsite = false;
            RequestOutcome defenceOutcome = RequestOutcome.Success;
            bool defenceSkipRecord = false;

            try
            {
                HttpListenerRequest req = context.Request;
                string query = req.Url?.Query ?? string.Empty;
                string route = NormalizeRoute(req.Url?.AbsolutePath ?? req.RawUrl);
                statsRoute = route;
                statsMethod = NormalizeMethod(req.HttpMethod);
                defenceIp = OmniDefenceService.ExtractClientIp(req);
                defenceUserAgent = req.UserAgent;
                defenceQueryString = query;
                defenceFromWebsite = IsWebsiteClientRequest(req);
                defenceClientPage = req.Headers["X-Klive-Page"] ?? req.Headers["Referer"];
                string defenceAuthHeader = req.Headers["Authorization"] ?? string.Empty;
                defenceRequestOrigin = defenceFromWebsite ? (string.IsNullOrWhiteSpace(defenceAuthHeader) ? "WebsiteNoProfile" : "WebsiteInvalidProfile") : "DirectApi";
                NameValueCollection nameValueCollection = string.IsNullOrEmpty(query)
                    ? new NameValueCollection()
                    : HttpUtility.ParseQueryString(query);
                UserRequest request = new();
                request.req = req;
                request.route = route;
                request.context = context;
                request.ParentService = this;
                request.userParameters = nameValueCollection;
                request.user = null;
                request.userMessageBytes = Array.Empty<byte>();
                request.userMessageContent = string.Empty;

                //HANDLE PREFLIGHT REQUESTS
                if (NormalizeMethod(request.req.HttpMethod) == "OPTIONS")
                {
                    shouldRecordStatistics = false;
                    defenceSkipRecord = true;
                    await request.ReturnResponse("", "text/plain", null, HttpStatusCode.OK);
                    return;
                }

                KMProfileManager.KMProfile? preResolvedUser = null;
                if (!string.IsNullOrWhiteSpace(defenceAuthHeader))
                {
                    preResolvedUser = await ResolveRequestUserAsync(req);
                    if (preResolvedUser != null)
                    {
                        defenceProfileId = preResolvedUser.UserID;
                        defenceProfileName = preResolvedUser.Name;
                        defenceProfileRank = (int)preResolvedUser.KlivesManagementRank;
                        defenceRequestOrigin = defenceFromWebsite ? "WebsiteProfile" : "DirectApiProfile";
                    }
                }
                bool skipDefenceGateForKlives = preResolvedUser != null && preResolvedUser.KlivesManagementRank >= KMProfileManager.KMPermissions.Klives;

                // ---- OmniDefence pre-dispatch gate ----
                var defence = TryGetDefence();
                if (defence != null && !string.IsNullOrEmpty(defenceIp) && !skipDefenceGateForKlives)
                {
                    var decision = defence.EvaluateRequestGate(defenceIp, route);
                    if (decision == IpThreatTracker.GateDecision.Block)
                    {
                        defenceOutcome = RequestOutcome.PreBlocked;
                        defenceDenyReason = "IPBlocked";
                        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        context.Response.Close();
                        return;
                    }
                    else if (decision == IpThreatTracker.GateDecision.Tarpit)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5000)), cancellationToken.Token); } catch { }
                        defenceDenyReason = "Tarpit";
                        defenceOutcome = RequestOutcome.PreBlocked;
                        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        context.Response.Close();
                        return;
                    }
                    else if (decision == IpThreatTracker.GateDecision.Honeypot)
                    {
                        defenceDenyReason = "Honeypot";
                        string junk = defence.GenerateHoneypotResponse(defenceIp);
                        await request.ReturnResponse(junk, "application/json");
                        return;
                    }
                }

                // --- WebSocket upgrade handling ---
                if (req.IsWebSocketRequest && WebSocketRouteLookup.TryGetValue(route, out WebSocketRouteInfo wsRouteData))
                {
                    shouldRecordStatistics = false;
                    defenceSkipRecord = true;
                    if (ShouldResolveUser(req, wsRouteData.authenticationLevelRequired))
                    {
                        request.user = preResolvedUser ?? await ResolveRequestUserAsync(req);
                    }

                    if (wsRouteData.authenticationLevelRequired == KMPermissions.Anybody
                        || (request.user != null && request.user.KlivesManagementRank >= wsRouteData.authenticationLevelRequired))
                    {
                        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                        await wsRouteData.handler(context, wsContext.WebSocket, nameValueCollection, request.user);
                    }
                    else
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                    }
                    return;
                }

                if (ControllerLookup.TryGetValue(route, out RouteInfo routeData))
                {
                    matchedRoute = true;
                    defencePermRequired = (int)routeData.authenticationLevelRequired;
                    if (!IsRequestMethodAllowed(req.HttpMethod, routeData.normalizedMethod))
                    {
                        defenceOutcome = RequestOutcome.IncorrectMethod;
                        defenceDenyReason = "IncorrectHTTPMethod";
                        await DenyRequest(request, DeniedRequestReason.IncorrectHTTPMethod);
                        return;
                    }

                    if (ShouldResolveUser(req, routeData.authenticationLevelRequired))
                    {
                        request.user = preResolvedUser ?? await ResolveRequestUserAsync(req);
                    }

                    if (defenceFromWebsite)
                    {
                        defenceRequestOrigin = request.user != null
                            ? "WebsiteProfile"
                            : routeData.authenticationLevelRequired == KMProfileManager.KMPermissions.Anybody
                                ? "WebsitePublicNoProfile"
                                : string.IsNullOrWhiteSpace(req.Headers["Authorization"])
                                    ? "WebsiteNoProfile"
                                    : "WebsiteInvalidProfile";
                    }
                    else
                    {
                        defenceRequestOrigin = request.user != null ? "DirectApiProfile" : "DirectApi";
                    }

                    if (CanRequestCarryBody(req.HttpMethod))
                    {
                        (request.userMessageBytes, request.userMessageContent) = await ReadRequestBodyAsync(req);
                        defenceBodyLength = request.userMessageBytes?.LongLength ?? 0;
                        defenceBodyHash = OmniDefenceService.HashBody(request.userMessageBytes ?? Array.Empty<byte>());
                    }

                    bool isUserNull = request.user == null;
                    if (isUserNull != true)
                    {
                        defenceProfileId = request.user.UserID;
                        defenceProfileName = request.user.Name;
                        defenceProfileRank = (int)request.user.KlivesManagementRank;

                        if (request.user.CanLogin == false && routeData.authenticationLevelRequired != KMProfileManager.KMPermissions.Anybody)
                        {
                            defenceOutcome = RequestOutcome.InsufficientClearance;
                            defenceDenyReason = "ProfileDisabled";
                            await DenyRequest(request, DeniedRequestReason.ProfileDisabled);
                            return;
                        }

                        if (routeData.authenticationLevelRequired == KMProfileManager.KMPermissions.Anybody)
                        {
                            await routeData.InvokeAction(request);
                            return;
                        }

                        if (request.user.KlivesManagementRank >= routeData.authenticationLevelRequired)
                        {
                            await routeData.InvokeAction(request);
                            return;
                        }

                        _ = ServiceLog($"{request.user.Name} requested route {route} without sufficient permission.");
                        defenceOutcome = RequestOutcome.InsufficientClearance;
                        defenceDenyReason = "TooLowClearance";
                        if (defence != null)
                        {
                            _ = defence.RecordAuthEventAsync(new AuthEventRow
                            {
                                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                Ip = defenceIp,
                                Type = "InsufficientClearance",
                                ProfileId = request.user.UserID,
                                ProfileName = request.user.Name,
                                Route = route,
                                UserAgent = defenceUserAgent,
                                Detail = "Required " + routeData.authenticationLevelRequired
                            });
                        }
                        await DenyRequest(request, DeniedRequestReason.TooLowClearance);
                    }
                    else if (routeData.authenticationLevelRequired == KMPermissions.Anybody)
                    {
                        await routeData.InvokeAction(request);
                    }
                    else
                    {
                        _ = ServiceLog($"Authenticated route {route} was requested without valid credentials.");
                        bool isWebsiteNoProfile = defenceFromWebsite;
                        string authHeader = req.Headers["Authorization"] ?? string.Empty;
                        defenceOutcome = isWebsiteNoProfile ? RequestOutcome.WebsiteNoProfile : RequestOutcome.UnauthRoute;
                        defenceDenyReason = isWebsiteNoProfile
                            ? (string.IsNullOrWhiteSpace(authHeader) ? "WebsiteNoProfile" : "WebsiteInvalidProfile")
                            : "NoProfile";
                        if (defence != null)
                        {
                            _ = defence.RecordAuthEventAsync(new AuthEventRow
                            {
                                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                Ip = defenceIp,
                                Type = isWebsiteNoProfile
                                    ? defenceDenyReason
                                    : string.IsNullOrWhiteSpace(authHeader) ? "UnauthRoute" : "InvalidPassword",
                                Route = route,
                                UserAgent = defenceUserAgent,
                                Detail = "Required " + routeData.authenticationLevelRequired + (string.IsNullOrWhiteSpace(defenceClientPage) ? "" : "; Page " + defenceClientPage)
                            });
                            if (!isWebsiteNoProfile && !string.IsNullOrWhiteSpace(authHeader))
                            {
                                defenceOutcome = RequestOutcome.InvalidPassword;
                                defenceDenyReason = "InvalidPassword";
                            }
                        }
                        if (isWebsiteNoProfile)
                        {
                            try { await Task.Delay(TimeSpan.FromSeconds(6) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)), cancellationToken.Token); } catch { }
                            await DenyRequest(request, DeniedRequestReason.NoProfile, HttpStatusCode.Forbidden);
                        }
                        else
                        {
                            await DenyRequest(request, DeniedRequestReason.NoProfile);
                        }
                    }
                }
                else
                {
                    defenceOutcome = RequestOutcome.NotFound;
                    await request.ReturnResponse("Route not found", "text/plain", null, HttpStatusCode.NotFound);
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error processing request: " + context.Request?.RawUrl);
                defenceOutcome = RequestOutcome.ServerError;
                try
                {
                    if (context?.Response != null && context.Response.OutputStream.CanWrite)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        context.Response.ContentType = "application/json";
                        byte[] errorBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                        {
                            Error = "Unhandled exception while processing request.",
                            Route = context.Request?.RawUrl,
                            Message = ex.Message
                        }));
                        context.Response.ContentLength64 = errorBytes.Length;
                        await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length);
                        context.Response.Close();
                    }
                }
                catch
                {
                }
            }
            finally
            {
                if (shouldRecordStatistics && apiStatistics != null)
                {
                    requestStopwatch.Stop();
                    int statusCode = context?.Response?.StatusCode > 0 ? context.Response.StatusCode : (int)HttpStatusCode.InternalServerError;
                    apiStatistics.RecordRequest(statsRoute, statsMethod, statusCode, requestStopwatch.Elapsed, matchedRoute);
                }

                // Record everything to OmniDefence
                if (!defenceSkipRecord)
                {
                    var defence = TryGetDefence();
                    if (defence != null)
                    {
                        try
                        {
                            int statusCode = context?.Response?.StatusCode > 0 ? context.Response.StatusCode : (int)HttpStatusCode.InternalServerError;
                            // Map status code to outcome when not already set
                            if (defenceOutcome == RequestOutcome.Success && statusCode >= 400)
                            {
                                defenceOutcome = statusCode == 401 ? RequestOutcome.UnauthRoute
                                                : statusCode == 403 ? RequestOutcome.InsufficientClearance
                                                : statusCode == 404 ? RequestOutcome.NotFound
                                                : statusCode >= 500 ? RequestOutcome.ServerError
                                                : RequestOutcome.UnauthRoute;
                            }
                            var row = new RequestRow
                            {
                                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                Ip = defenceIp,
                                Method = statsMethod,
                                Route = statsRoute,
                                Query = string.IsNullOrEmpty(defenceQueryString) ? null : defenceQueryString,
                                StatusCode = statusCode,
                                DurationMs = requestStopwatch.Elapsed.TotalMilliseconds,
                                ProfileId = defenceProfileId,
                                ProfileName = defenceProfileName,
                                ProfileRank = defenceProfileRank,
                                PermRequired = defencePermRequired,
                                MatchedRoute = matchedRoute,
                                BodyHash = defenceBodyHash,
                                BodyLength = defenceBodyLength,
                                UserAgent = defenceUserAgent,
                                DenyReason = defenceDenyReason,
                                RequestOrigin = defenceRequestOrigin,
                                ClientPage = defenceClientPage
                            };
                            _ = defence.RecordRequestAsync(row, defenceOutcome);
                        }
                        catch { }
                    }
                }
            }
        }
        private enum DeniedRequestReason
        {
            NoProfile = 0,
            InvalidPassword = 1,
            TooLowClearance = 2,
            IncorrectHTTPMethod = 3,
            ProfileDisabled = 4
        }
        private async Task DenyRequest(UserRequest request, DeniedRequestReason reason, HttpStatusCode code = HttpStatusCode.Unauthorized)
        {
            NameValueCollection headers = new();
            headers.Add("RequestDeniedReason", reason.ToString());
            headers.Add("RequestDeniedCode", ((int)reason).ToString());
            await request.ReturnResponse("Access Denied: " + reason, "text/plain", headers, code);
        }
    }
}