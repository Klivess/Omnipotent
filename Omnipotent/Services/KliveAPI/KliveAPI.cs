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
using Microsoft.VisualBasic.FileIO;
using DSharpPlus.Entities;
using Org.BouncyCastle.Asn1.IsisMtt.Ocsp;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Crypto;


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
            public Action<UserRequest> action;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
            public HttpMethod method;

            public List<TimeSpan> TimeTakenToProcess;
            public async Task InvokeAction(UserRequest request)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                action.Invoke(request);
                stopwatch.Stop();
                if (TimeTakenToProcess == null)
                {
                    TimeTakenToProcess = new List<TimeSpan>();
                }
                TimeTakenToProcess.Add(stopwatch.Elapsed);
            }
        }
        public struct UserRequest
        {
            public HttpListenerContext context;
            public HttpListenerRequest req;
            public NameValueCollection userParameters;
            public KMProfileManager.KMProfile? user;
            public string userMessageContent;

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
                    }
                    resp.Headers.Add("Access-Control-Allow-Origin", "*");

                    byte[] buffer = Encoding.UTF8.GetBytes(response);
                    resp.ContentLength64 = buffer.Length;
                    resp.StatusCode = (int)code;
                    using Stream ros = resp.OutputStream;
                    ros.Write(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    ParentService.ServiceLogError(ex, "Error while returning response for route: " + context.Request.RawUrl);
                    //Return Error Response, this is a last resort to prevent the server from crashing
                    await context.Response.OutputStream.FlushAsync();
                    await context.Response.OutputStream.DisposeAsync();
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.ContentType = "text/plain";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Error occurred on server."));
                    await context.Response.OutputStream.FlushAsync();
                    context.Response.Close();
                }
            }
        }

        //Controller Lookup
        //Key: Route (example: /omniscience/getmessagecount)
        //Value: EventHandler<route, routeInfo>
        public ConcurrentDictionary<string, RouteInfo> ControllerLookup;

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
                serviceManager.CreateAndStartNewMonitoredOmniService(new KMProfileManager());
                profileManager = (KMProfileManager)(await serviceManager.GetServiceByClassType<KMProfileManager>())[0];

                CreateMetaKLIVEAPIRoutes();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "KliveAPI Failed!");
                (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("KliveAPI Failed to start! Error Info: " + new ErrorInformation(ex).FullFormattedMessage);
            }
        }

        /// <summary>
        /// Handles file upload requests with multipart form data.
        /// Security features:
        /// - File type validation (whitelist of allowed extensions)
        /// - File size limit (10MB per file)
        /// - Automatic filename sanitization with timestamps
        /// - Directory creation if not exists
        /// </summary>
        private async Task HandleFileUpload(UserRequest req)
        {
            try
            {
                // Ensure upload directory exists
                var uploadDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesAPIUploadedFilesDirectory);
                var dataHandler = GetDataHandler();
                await dataHandler.CreateDirectory(uploadDir);

                // Get content type
                string contentType = req.req.ContentType ?? "";
                
                if (!contentType.StartsWith("multipart/form-data"))
                {
                    await req.ReturnResponse("Content-Type must be multipart/form-data", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }

                // Parse multipart form data
                var boundary = ExtractBoundary(contentType);
                if (string.IsNullOrEmpty(boundary))
                {
                    await req.ReturnResponse("Missing boundary in Content-Type", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }

                // Read the request body as bytes to handle binary data correctly
                using var memoryStream = new MemoryStream();
                await req.req.InputStream.CopyToAsync(memoryStream);
                var bodyBytes = memoryStream.ToArray();
                
                // Parse multipart data
                var files = ParseMultipartFormData(bodyBytes, boundary);
                
                if (files.Count == 0)
                {
                    await req.ReturnResponse("No files found in request", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }

                var uploadedFiles = new List<object>();
                
                foreach (var file in files)
                {
                    // Validate file
                    if (string.IsNullOrEmpty(file.FileName))
                    {
                        continue;
                    }
                    
                    // Security: validate file extension
                    var allowedExtensions = new[] { ".txt", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".doc", ".docx", ".zip" };
                    var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
                    
                    if (!allowedExtensions.Contains(fileExtension))
                    {
                        ServiceLog($"File upload rejected: {file.FileName} - unsupported extension {fileExtension}");
                        continue;
                    }
                    
                    // Security: validate file size (10MB limit)
                    if (file.Content.Length > 10 * 1024 * 1024)
                    {
                        ServiceLog($"File upload rejected: {file.FileName} - file too large ({file.Content.Length} bytes)");
                        continue;
                    }
                    
                    // Generate safe filename
                    var safeFileName = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_{Path.GetFileName(file.FileName)}";
                    var filePath = Path.Combine(uploadDir, safeFileName);
                    
                    // Save file
                    await dataHandler.WriteBytesToFile(filePath, file.Content);
                    
                    uploadedFiles.Add(new
                    {
                        originalName = file.FileName,
                        savedName = safeFileName,
                        size = file.Content.Length,
                        contentType = file.ContentType,
                        uploadTime = DateTime.UtcNow
                    });
                    
                    ServiceLog($"File uploaded: {file.FileName} -> {safeFileName} ({file.Content.Length} bytes)");
                }
                
                var response = new
                {
                    success = true,
                    uploadedFiles = uploadedFiles,
                    message = $"Successfully uploaded {uploadedFiles.Count} file(s)"
                };
                
                await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error during file upload");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "Upload failed" }), 
                    "application/json", null, HttpStatusCode.InternalServerError);
            }
        }

        /// <summary>
        /// Handles file download requests by filename.
        /// Security features:
        /// - Path sanitization to prevent directory traversal
        /// - File existence validation
        /// - Proper content type detection
        /// - Support for both JSON (base64) and raw binary responses
        /// </summary>
        private async Task HandleFileDownload(UserRequest req)
        {
            try
            {
                var fileName = req.userParameters.Get("filename");
                
                if (string.IsNullOrEmpty(fileName))
                {
                    await req.ReturnResponse("Missing filename parameter", "application/json", null, HttpStatusCode.BadRequest);
                    return;
                }
                
                // Security: prevent directory traversal
                fileName = Path.GetFileName(fileName);
                
                var uploadDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesAPIUploadedFilesDirectory);
                var filePath = Path.Combine(uploadDir, fileName);
                
                if (!File.Exists(filePath))
                {
                    await req.ReturnResponse("File not found", "application/json", null, HttpStatusCode.NotFound);
                    return;
                }
                
                var dataHandler = GetDataHandler();
                var fileBytes = await dataHandler.ReadBytesFromFile(filePath);
                
                // Determine content type based on file extension
                var contentType = GetContentType(fileName);
                
                // Set headers for file download
                var headers = new NameValueCollection();
                headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                
                // Convert bytes to base64 for JSON response or return raw bytes
                if (req.userParameters.Get("raw") == "true")
                {
                    // Return raw bytes
                    req.context.Response.ContentType = contentType;
                    req.context.Response.ContentLength64 = fileBytes.Length;
                    req.context.Response.StatusCode = 200;
                    
                    foreach (string key in headers.AllKeys)
                    {
                        req.context.Response.Headers.Add(key, headers[key]);
                    }
                    
                    await req.context.Response.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                    req.context.Response.Close();
                }
                else
                {
                    // Return JSON with base64 encoded content
                    var response = new
                    {
                        success = true,
                        fileName = fileName,
                        contentType = contentType,
                        size = fileBytes.Length,
                        content = Convert.ToBase64String(fileBytes)
                    };
                    
                    await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
                }
                
                ServiceLog($"File downloaded: {fileName} ({fileBytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error during file download");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "Download failed" }), 
                    "application/json", null, HttpStatusCode.InternalServerError);
            }
        }

        private string ExtractBoundary(string contentType)
        {
            var boundaryPrefix = "boundary=";
            var boundaryIndex = contentType.IndexOf(boundaryPrefix);
            if (boundaryIndex == -1) return null;
            
            var boundary = contentType.Substring(boundaryIndex + boundaryPrefix.Length);
            boundary = boundary.Split(';')[0].Trim();
            
            // Remove quotes if present
            if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
            {
                boundary = boundary.Substring(1, boundary.Length - 2);
            }
            
            return boundary;
        }

        private List<MultipartFile> ParseMultipartFormData(byte[] bodyBytes, string boundary)
        {
            var files = new List<MultipartFile>();
            var boundaryMarker = "--" + boundary;
            var boundaryBytes = Encoding.UTF8.GetBytes(boundaryMarker);
            var body = Encoding.UTF8.GetString(bodyBytes);
            
            var parts = body.Split(new[] { boundaryMarker }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part) || part.Trim() == "--") continue;
                
                var headerEndIndex = part.IndexOf("\r\n\r\n");
                if (headerEndIndex == -1) continue;
                
                var headers = part.Substring(0, headerEndIndex);
                var contentStart = headerEndIndex + 4;
                
                // Get content as bytes for binary files
                var partBytes = Encoding.UTF8.GetBytes(part);
                var headerBytes = Encoding.UTF8.GetBytes(headers);
                var contentBytes = new byte[partBytes.Length - (headerBytes.Length + 4)];
                
                if (contentBytes.Length > 0)
                {
                    Array.Copy(partBytes, headerBytes.Length + 4, contentBytes, 0, contentBytes.Length);
                    
                    // Remove trailing CRLF if present
                    if (contentBytes.Length >= 2 && 
                        contentBytes[contentBytes.Length - 2] == 13 && 
                        contentBytes[contentBytes.Length - 1] == 10)
                    {
                        var trimmedBytes = new byte[contentBytes.Length - 2];
                        Array.Copy(contentBytes, 0, trimmedBytes, 0, trimmedBytes.Length);
                        contentBytes = trimmedBytes;
                    }
                }
                
                // Parse Content-Disposition header
                var contentDisposition = ExtractHeader(headers, "Content-Disposition");
                var fileName = ExtractAttributeFromHeader(contentDisposition, "filename");
                var name = ExtractAttributeFromHeader(contentDisposition, "name");
                
                if (!string.IsNullOrEmpty(fileName))
                {
                    var contentType = ExtractHeader(headers, "Content-Type") ?? "application/octet-stream";
                    
                    files.Add(new MultipartFile
                    {
                        Name = name,
                        FileName = fileName,
                        ContentType = contentType,
                        Content = contentBytes
                    });
                }
            }
            
            return files;
        }

        private string ExtractHeader(string headers, string headerName)
        {
            var lines = headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(headerName.Length + 1).Trim();
                }
            }
            return null;
        }

        private string ExtractAttributeFromHeader(string header, string attributeName)
        {
            if (string.IsNullOrEmpty(header)) return null;
            
            var pattern = attributeName + "=";
            var index = header.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index == -1) return null;
            
            var value = header.Substring(index + pattern.Length);
            var endIndex = value.IndexOf(';');
            if (endIndex != -1)
            {
                value = value.Substring(0, endIndex);
            }
            
            // Remove quotes
            value = value.Trim().Trim('"');
            return value;
        }

        private string GetContentType(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".txt" => "text/plain",
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        private DataUtil GetDataHandler()
        {
            return (DataUtil)serviceManager.GetServiceByClassType<DataUtil>().Result[0];
        }

        private class MultipartFile
        {
            public string Name { get; set; }
            public string FileName { get; set; }
            public string ContentType { get; set; }
            public byte[] Content { get; set; }
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

            // File upload route - handles multipart form data uploads
            // Supports multiple files, file type validation, size limits
            // Authentication required: Associate level or higher
            await CreateRoute("/files/upload", async (req) =>
            {
                await HandleFileUpload(req);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Associate);

            // File download route - serves uploaded files by filename
            // Supports both JSON (base64) and raw binary responses
            // Authentication required: Associate level or higher
            await CreateRoute("/files/download", async (req) =>
            {
                await HandleFileDownload(req);
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Associate);
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
        public async Task CreateRoute(string route, Action<UserRequest> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired)
        {
            //while (!listener.IsListening) { await Task.Delay(10); }
            if (!route.StartsWith('/'))
            {
                //Add a / to the beginning of the route if it doesn't have one
                route = "/" + route;
            }
            RouteInfo routeInfo = new();
            routeInfo.action = handler;
            routeInfo.authenticationLevelRequired = authenticationLevelRequired;
            routeInfo.method = method;
            if (ControllerLookup.TryAdd(route, routeInfo))
            {
                ServiceLog($"New {method.ToString().ToUpper()} route created: " + route);
            }
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
                    request.ParentService = this;
                    StreamReader reader = new StreamReader(request.req.InputStream, Encoding.UTF8);
                    request.userMessageContent = await reader.ReadToEndAsync();
                    request.userParameters = nameValueCollection;
                    request.user = null;

                    //HANDLE PREFLIGHT REQUESTS
                    if (request.req.HttpMethod == "OPTIONS")
                    {
                        request.ReturnResponse("", "text/plain", null, HttpStatusCode.OK);
                        continue;
                    }


                    if (!string.IsNullOrEmpty(query))
                    {
                        route = route.Replace(query, "");
                    }
                    if (req.Headers.AllKeys.Contains("Authorization"))
                    {
                        string password = req.Headers.Get("Authorization");
                        var profileManager = ((KMProfileManager)((await serviceManager.GetServiceByClassType<KMProfileManager>())[0]));
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
                        bool isUserNull = true;
                        try
                        {
                            isUserNull = request.user == null;
                        }
                        catch (NullReferenceException ex)
                        {
                            isUserNull = true;
                        }
                        if (isUserNull != true)
                        {
                            if (request.user.CanLogin == false && routeData.authenticationLevelRequired != KMProfileManager.KMPermissions.Anybody)
                            {
                                ServiceLog($"Route {route} has been requested by a user that can't login.");
                                DenyRequest(request, DeniedRequestReason.ProfileDisabled);
                                continue;
                            }
                            else
                            {
                                if (req.HttpMethod.Trim().ToLower() != routeData.method.Method.Trim().ToLower())
                                {
                                    ServiceLog($"Route {route} has been requested with an incorrect HTTP method.");
                                    DenyRequest(request, DeniedRequestReason.IncorrectHTTPMethod);
                                }
                                else
                                {
                                    if (routeData.authenticationLevelRequired == KMProfileManager.KMPermissions.Anybody)
                                    {
                                        //ServiceLog($"Unauthenticated route {route} has been requested.");
                                        ControllerLookup[route].InvokeAction(request);
                                    }
                                    else
                                    {
                                        if (request.user.KlivesManagementRank >= routeData.authenticationLevelRequired)
                                        {
                                            ServiceLog($"{request.user.Name} requested an authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()}.");
                                            ControllerLookup[route].InvokeAction(request);
                                        }
                                        else
                                        {
                                            ServiceLog($"{request.user.Name} requested an authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()}, but requester doesn't have permission.");
                                            DenyRequest(request, DeniedRequestReason.TooLowClearance);
                                        }
                                    }
                                }
                            }
                        }
                        else if (routeData.authenticationLevelRequired == KMPermissions.Anybody)
                        {
                            if (req.HttpMethod.Trim().ToLower() != routeData.method.Method.Trim().ToLower())
                            {
                                ServiceLog($"Route {route} has been requested with an incorrect HTTP method.");
                                DenyRequest(request, DeniedRequestReason.IncorrectHTTPMethod);
                            }
                            else
                            {
                                ControllerLookup[route].InvokeAction(request);
                            }
                        }
                        else
                        {
                            ServiceLog($"Authenticated route {route} with permission level {routeData.authenticationLevelRequired.ToString()} has been requested, but requester doesn't have an account. Scary!!");
                            DenyRequest(request, DeniedRequestReason.NoProfile);
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
            IncorrectHTTPMethod = 3,
            ProfileDisabled = 4
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