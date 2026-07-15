using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using System.Security.Cryptography;
using OmniDefenceService = Omnipotent.Services.OmniDefence.OmniDefence;
using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.KliveAPI.Caching;


namespace Omnipotent.Services.KliveAPI
{
    public class KliveAPI : OmniService
    {
        // Served verbatim from GET / — a friendly landing page for humans and the
        // countless scraper/attack bots that ping the API root.
        private const string RootLandingText = """




                                             ............ ....
                             :::.:    .......:-*#%%%%%%%#=+%%*::..
                             .:-*+====++**######**%@@@@@@@@@@@@#-..
                               ..:-*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#:.
                        .:=--::::::-*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@%:.
                        ..:*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-.
                           .:-*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-..:.
                          .:::--=%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@*-:.
                          .::-=*%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@*:.
                   ..----=+%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@+:.
                    ...:-=+#%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@=..
                   ..-*##%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#%@=.
                     ..:::-==%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-+%-.
                       .::=%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-:*:.
                     ...:+#@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#+=:-..
                    .:#@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@:::..
                     ....::=*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#%-.
                      ..-*%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#%+-.
                      .:=----:::%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-==:.
                           ..:+@@@%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@+#@:.:..
                          .:-+=-:=%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@%:*#..
                           ...:.--:#@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@=:*-.
                                 .:-@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-:=..
                                   .-@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@:...
                                ..:---#@@@@@@@@@@@@@@@@@@@@@@@@@@@@#..
                               .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@=.
                              ..#@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@+:.
                              .-@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#:.
                             .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@+:..
                             .-@@@@@@@@@@@@@@@@@@@@@@@@@@@@#-:.
                             .-@@@@@@@@@@@@@@@@@@@@@@@@@@@@%-..
                             .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@=:.
                             ::=%@@@@@@@@@@@@@@@@@@@@@@@@@@@%=:.
                          ..:*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#..
                         .:*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@%..
                        .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@::
                       .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-.
                      .:%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@%-.
                      :*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#:.
                     .=@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@+..
                    .:@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@:.
                   ..*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@*..
                   ..*@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@-.
                    ..+@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@=.
                      .:-*%@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@@#-..
                         ..::-==++++++++++++**#####%%%##*==-::.:.
                                             ...........




                                                                                                    Hey there, either youre a human or one of the thousands of scraper/cyberattack bots pinging my API. Anyway, you should check out my github! https://github.com/Klivess
                """;

        public static int apiPORT = 443;
        public static int apiHTTPPORT = 5000;
        public static string domainName = "klive.dev"; //This is the domain name that the SSL certificate will be signed for. It should be the same as the domain name that the API will be accessed from.
        public HttpListener listener = new HttpListener();
        private bool ContinueListenLoop = true;
        private Task<HttpListenerContext> getContextTask;

        public enum RequestBodyMode
        {
            Buffered = 0,
            Streaming = 1
        }

        public struct RouteInfo
        {
            public Func<UserRequest, Task> action;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
            public HttpMethod method;
            public string normalizedMethod;
            public RequestBodyMode requestBodyMode;
            public long? maxBodyBytes;
        }
        public struct WebSocketRouteInfo
        {
            public Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task> handler;
            public KMProfileManager.KMPermissions authenticationLevelRequired;
        }

        /// <summary>
        /// Receives a route handler's response instead of the real HttpListener
        /// response when a UserRequest runs as a /batch sub-request.
        /// </summary>
        public sealed class CapturedResponse
        {
            public int StatusCode = 200;
            public string ContentType = "application/json";
            public NameValueCollection Headers = new();
            public byte[] Body = Array.Empty<byte>();
            public bool Completed;
            // Which response helper produced this, so a batch fill stored in the shared
            // cache carries the same binary/text semantics a direct GET would.
            public bool IsBinary;
        }

        /// <summary>
        /// Mutable request-body telemetry shared by every copy of a UserRequest.
        /// Streaming handlers can report a length/hash explicitly; otherwise the
        /// streaming wrapper reports them automatically when the body is consumed.
        /// </summary>
        public sealed class RequestBodyAuditState
        {
            private readonly object sync = new();
            private long bodyLength;
            private string? bodySha256;
            private bool explicitlyReported;
            private bool oversized;

            internal RequestBodyAuditState(long defaultBodyLength)
            {
                bodyLength = Math.Max(0, defaultBodyLength);
            }

            public long BodyLength
            {
                get { lock (sync) return bodyLength; }
            }

            public string? BodySha256
            {
                get { lock (sync) return bodySha256; }
            }

            /// <summary>
            /// Overrides the automatically observed body telemetry. This is useful
            /// when a handler delegates consumption to another component.
            /// </summary>
            public void Report(long length, string? sha256)
            {
                if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
                lock (sync)
                {
                    if (oversized) return;
                    bodyLength = length;
                    bodySha256 = string.IsNullOrWhiteSpace(sha256)
                        ? null
                        : sha256.Trim().ToUpperInvariant();
                    explicitlyReported = true;
                }
            }

            internal void ReportAutomatically(long length, string? sha256)
            {
                lock (sync)
                {
                    if (explicitlyReported || oversized) return;
                    bodyLength = Math.Max(0, length);
                    bodySha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256;
                }
            }

            internal void ReportOversized(long minimumObservedLength)
            {
                lock (sync)
                {
                    oversized = true;
                    bodyLength = Math.Max(bodyLength, minimumObservedLength);
                    bodySha256 = null;
                    // A later limit violation is authoritative and cannot be hidden by
                    // telemetry a handler reported before it attempted the next read.
                    explicitlyReported = false;
                }
            }
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

            // NOTE: UserRequest is a struct that gets copied around — any state that
            // must survive copies has to be a reference type (copies share the ref).
            [JsonIgnore]
            internal Stopwatch? requestTimer;
            [JsonIgnore]
            internal CapturedResponse? capture;
            // Tee target for the response cache: set during a cache-miss fill so
            // ReturnResponse/ReturnBinaryResponse additionally record what they emit,
            // without diverting the real socket write. Reference type, shared across
            // struct copies just like capture/requestTimer.
            [JsonIgnore]
            internal Caching.ResponseRecording? recording;
            [JsonIgnore]
            internal Stream? streamingRequestBody;
            [JsonIgnore]
            internal RequestBodyAuditState? requestBodyAudit;

            /// <summary>
            /// The unread, bounded request body for a streaming route. Buffered
            /// routes continue to use userMessageBytes/userMessageContent.
            /// </summary>
            [JsonIgnore]
            public Stream RequestBodyStream => streamingRequestBody ?? Stream.Null;

            /// <summary>
            /// Shared request-body telemetry used by the OmniDefence request audit.
            /// </summary>
            [JsonIgnore]
            public RequestBodyAuditState? RequestBodyAudit => requestBodyAudit;

            public void ReportRequestBodyAudit(long bodyLength, string? sha256)
            {
                if (requestBodyAudit == null)
                {
                    throw new InvalidOperationException("Request body audit state is only available on streaming routes.");
                }
                requestBodyAudit.Report(bodyLength, sha256);
            }

            [JsonIgnore]
            public KliveAPI ParentService;
            public async Task ReturnResponse(string response, string contentType = "application/json", NameValueCollection headers = null, HttpStatusCode code = HttpStatusCode.OK)
            {
                try
                {
                    if (contentType == "application/json")
                    {
                        if (OmniPaths.IsValidJson(response) != true)
                        {
                            response = JsonConvert.SerializeObject(response);
                        }
                    }

                    // Batch sub-request: divert everything into the capture buffer,
                    // leave the real HttpListener response untouched.
                    if (capture != null)
                    {
                        capture.StatusCode = (int)code;
                        capture.ContentType = contentType;
                        if (headers != null) capture.Headers.Add(headers);
                        capture.Body = Encoding.UTF8.GetBytes(response);
                        capture.Completed = true;
                        capture.IsBinary = false;
                        return;
                    }

                    HttpListenerResponse resp = context.Response;
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
                        resp.Headers.Set("Access-Control-Allow-Headers", "*");
                        resp.Headers.Set("Access-Control-Allow-Methods", "*");
                        resp.Headers.Set("Access-Control-Max-Age", "1728000");
                    }
                    resp.Headers.Set("Access-Control-Allow-Origin", "*");
                    resp.Headers.Set("Access-Control-Expose-Headers", "*");
                    SetTimingHeaders(resp);

                    byte[] buffer = Encoding.UTF8.GetBytes(response);

                    // Cache tee: record the uncompressed response before the ETag/304
                    // and compression branches run, so a fill that answers 304 to its
                    // own client still stores the full body. buffer is a fresh array
                    // that is never mutated below (compression reassigns the local), so
                    // the recording can hold the reference without copying.
                    recording?.Record((int)code, contentType, headers, buffer, isBinary: false);

                    // ETag / If-None-Match => 304 for successful GETs. Weak tag over the
                    // uncompressed bytes (one tag covers every Content-Encoding), checked
                    // before compression so matches cost no compression CPU.
                    const int MaxETagPayloadBytes = 8 * 1024 * 1024;
                    if (req.HttpMethod == "GET" && code == HttpStatusCode.OK
                        && buffer.Length > 0 && buffer.Length <= MaxETagPayloadBytes)
                    {
                        string etag = HttpResponseHelpers.ComputeWeakETag(buffer);
                        resp.Headers.Set("ETag", etag);
                        resp.Headers.Set("Cache-Control", "private, no-cache");
                        if (HttpResponseHelpers.ETagMatches(req.Headers["If-None-Match"], etag))
                        {
                            resp.StatusCode = (int)HttpStatusCode.NotModified;
                            resp.ContentLength64 = 0;
                            resp.OutputStream.Close();
                            return;
                        }
                    }

                    // Negotiated compression for compressible payloads worth the CPU.
                    if (HttpResponseHelpers.IsCompressibleContentType(contentType) && req.HttpMethod != "HEAD")
                    {
                        resp.Headers.Set("Vary", "Accept-Encoding");
                        if (buffer.Length >= 1024 && string.IsNullOrEmpty(resp.Headers["Content-Encoding"]))
                        {
                            var encoding = HttpResponseHelpers.PickEncoding(req.Headers["Accept-Encoding"]);
                            if (encoding != HttpResponseHelpers.ContentEncoding.None)
                            {
                                byte[] compressed = HttpResponseHelpers.Compress(buffer, encoding);
                                if (compressed.Length < buffer.Length)
                                {
                                    buffer = compressed;
                                    resp.Headers.Set("Content-Encoding", HttpResponseHelpers.EncodingHeaderValue(encoding));
                                    recording?.RecordCompressedVariant(encoding, compressed);
                                }
                            }
                        }
                    }

                    resp.ContentLength64 = buffer.Length;
                    resp.StatusCode = (int)code;
                    using Stream ros = resp.OutputStream;
                    await ros.WriteAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    // A client that hung up mid-response surfaces as HttpListenerException
                    // ("nonexistent network connection"), IOException, or a disposed/socket
                    // error. That's the caller's doing, not a server fault: the connection
                    // is already gone, so log it quietly (no stack-trace spam) and don't
                    // attempt the doomed 500 write. Keeping it distinct stops benign
                    // disconnects from masking real errors in the log.
                    bool clientGone = ex is HttpListenerException
                        || ex is System.IO.IOException
                        || ex is ObjectDisposedException
                        || ex is System.Net.Sockets.SocketException;

                    if (clientGone)
                    {
                        _ = ParentService.ServiceLog($"Client disconnected before response completed for route: " +
                            $"{context.Request?.RawUrl} ({ex.GetType().Name}: {ex.Message})");
                        if (capture != null)
                        {
                            capture.StatusCode = (int)HttpStatusCode.InternalServerError;
                            capture.Body = Encoding.UTF8.GetBytes("Client disconnected.");
                            capture.Completed = true;
                        }
                        return;
                    }

                    ParentService.ServiceLogError(ex, "Error while returning response for route: " + context.Request.RawUrl);
                    if (capture != null)
                    {
                        capture.StatusCode = (int)HttpStatusCode.InternalServerError;
                        capture.ContentType = "text/plain";
                        capture.Body = Encoding.UTF8.GetBytes("Error occurred on server.");
                        capture.Completed = true;
                        return;
                    }
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
                    if (capture != null)
                    {
                        capture.StatusCode = (int)code;
                        capture.ContentType = contentType;
                        if (headers != null) capture.Headers.Add(headers);
                        capture.Body = data;
                        capture.Completed = true;
                        capture.IsBinary = true;
                        return;
                    }

                    HttpListenerResponse resp = context.Response;
                    resp.ContentType = contentType;
                    resp.StatusCode = (int)code;
                    if (headers != null)
                    {
                        for (int i = 0; i < headers.Count; i++)
                        {
                            resp.Headers.Add(headers.GetKey(i), headers.Get(i));
                        }
                    }
                    resp.Headers.Set("Access-Control-Allow-Origin", "*");
                    resp.Headers.Set("Access-Control-Expose-Headers", "*");
                    SetTimingHeaders(resp);

                    byte[] buffer = data;

                    // Cache tee: binary bodies are caller-owned, so defensively copy.
                    recording?.Record((int)code, contentType, headers, (byte[])(data ?? Array.Empty<byte>()).Clone(), isBinary: true);

                    // Same negotiated compression as ReturnResponse; the content-type
                    // allowlist naturally skips already-compressed media/archives.
                    if (HttpResponseHelpers.IsCompressibleContentType(contentType) && req.HttpMethod != "HEAD")
                    {
                        resp.Headers.Set("Vary", "Accept-Encoding");
                        if (buffer.Length >= 1024 && string.IsNullOrEmpty(resp.Headers["Content-Encoding"]))
                        {
                            var encoding = HttpResponseHelpers.PickEncoding(req.Headers["Accept-Encoding"]);
                            if (encoding != HttpResponseHelpers.ContentEncoding.None)
                            {
                                byte[] compressed = HttpResponseHelpers.Compress(buffer, encoding);
                                if (compressed.Length < buffer.Length)
                                {
                                    buffer = compressed;
                                    resp.Headers.Set("Content-Encoding", HttpResponseHelpers.EncodingHeaderValue(encoding));
                                    recording?.RecordCompressedVariant(encoding, compressed);
                                }
                            }
                        }
                    }

                    resp.ContentLength64 = buffer.Length;
                    using Stream ros = resp.OutputStream;
                    await ros.WriteAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    ParentService.ServiceLogError(ex, "Error while returning binary response for route: " + context.Request.RawUrl);
                }
            }

            public Stream PrepareStreamResponse(string contentType, long contentLength, HttpStatusCode code = HttpStatusCode.OK, NameValueCollection headers = null)
            {
                if (capture != null)
                {
                    throw new NotSupportedException("Streaming routes cannot run inside /batch.");
                }
                // A streaming response can't be captured for the cache; mark the fill
                // so it is never stored (the socket write below proceeds normally).
                recording?.MarkStreaming();
                HttpListenerResponse resp = context.Response;
                resp.ContentType = contentType;
                resp.StatusCode = (int)code;
                resp.ContentLength64 = contentLength;
                if (headers != null)
                {
                    for (int i = 0; i < headers.Count; i++)
                    {
                        resp.Headers.Add(headers.GetKey(i), headers.Get(i));
                    }
                }
                resp.Headers.Set("Access-Control-Allow-Origin", "*");
                resp.Headers.Set("Access-Control-Expose-Headers", "*");
                SetTimingHeaders(resp);
                return resp.OutputStream;
            }

            /// <summary>
            /// Server-Timing lets browser devtools show real server processing time
            /// on every response; Timing-Allow-Origin is required for the website's
            /// cross-origin requests to read it.
            /// </summary>
            private void SetTimingHeaders(HttpListenerResponse resp)
            {
                try
                {
                    double ms = requestTimer?.Elapsed.TotalMilliseconds ?? 0;
                    resp.Headers.Set("Server-Timing", $"app;dur={ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                    resp.Headers.Set("Timing-Allow-Origin", "*");
                }
                catch
                {
                    // Never let observability headers break a response.
                }
            }
        }

        //Controller Lookup
        //Key: Route (example: /omniscience/getmessagecount)
        //Value: EventHandler<route, routeInfo>
        public ConcurrentDictionary<string, RouteInfo> ControllerLookup;
        public ConcurrentDictionary<string, WebSocketRouteInfo> WebSocketRouteLookup;

        private KMProfileManager profileManager;
        private KliveApiStatisticsStore? apiStatistics;

        // ── Transparent response cache (dependency-versioned, never stale) ──
        private readonly ResponseCache responseCache = new();
        private volatile bool cacheEnabled;
        private volatile string[] cacheDenylistPrefixes = Array.Empty<string>();

        // ── Watchdog diagnostics ──
        // Temporary instrumentation to find why /ping can cross the watchdog's 10s
        // timeout while the rest of the process keeps logging. These are cheap, lock-free
        // signals sampled when a ping fails so the failure category is unambiguous:
        //   • _pingsServed advancing during a failed window  => server answered, the
        //     watchdog's own continuation was starved (thread-pool), not the API.
        //   • _pingsServed flat + stale last-accept          => listen loop wedged / listener down.
        //   • high _inFlightRequests + slow-request logs      => a real handler is hogging the pipeline.
        private long _pingsServed;
        private int _inFlightRequests;
        private long _lastContextAcceptedUtcTicks;
        private long _lastRequestCompletedUtcTicks;
        // Requests slower than this get a one-line log with a runtime-health snapshot.
        private const int SlowRequestLogThresholdMs = 3000;
        private Thread? _healthHeartbeatThread;

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

                listener = CreateConfiguredListener();

                ServiceQuitRequest += KliveAPI_ServiceQuitRequest;

                ServiceLog($"Checking SSL Certificates ");
                await CheckForSSLCertificate();
                await LinkSSLCertificate(certInstaller.rootAuthorityPfxPath);

                await StartListenerWithRetry();

                ServiceLog($"Listening on: {string.Join(", ", listener.Prefixes)}");

                // http.sys negotiates TLS 1.3 (saves 1 RTT on cold connections) only on
                // Windows 11 / Server 2022+ (build >= 20348). Log so a stuck-on-TLS-1.2
                // server is diagnosable without guessing.
                int osBuild = Environment.OSVersion.Version.Build;
                ServiceLog(osBuild >= 20348
                    ? $"OS build {osBuild}: http.sys TLS 1.3 supported (verify with openssl s_client -tls1_3)."
                    : $"OS build {osBuild}: http.sys TLS 1.3 NOT supported — cold connections stay on TLS 1.2 (2-RTT handshake) until OS upgrade.");

                ServerListenLoop();
                //Create profile manager
                CreateAndStartService(new KMProfileManager());
                profileManager = (KMProfileManager)(await GetServicesByType<KMProfileManager>())[0];

                //Create KliveLink remote administration service
                CreateAndStartService(new KliveLink.KliveLinkService());

                CreateMetaKLIVEAPIRoutes();

                StartWatchdog();
                StartResponseCacheConfigWatcher();
                StartHealthHeartbeat();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "KliveAPI Failed!");
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", "KliveAPI Failed to start! Error Info: " + new ErrorInformation(ex).FullFormattedMessage);
            }
        }

        /// <summary>
        /// Builds a fresh <see cref="HttpListener"/> with KliveAPI's HTTP + HTTPS prefixes.
        /// Kept in one place so <see cref="StartListenerWithRetry"/> can rebuild an identical
        /// listener after disposing a stale one.
        /// </summary>
        private HttpListener CreateConfiguredListener()
        {
            var l = new HttpListener();
            //l.Prefixes.Add($"https://+:{apiPORT}/");
            l.Prefixes.Add($"http://+:{apiHTTPPORT}/");
            l.Prefixes.Add($"https://+:{apiPORT}/");
            return l;
        }

        /// <summary>
        /// Starts <see cref="listener"/>, tolerating the http.sys "conflicts with an existing
        /// registration on the machine" failure (Win32 ERROR_ALREADY_EXISTS / ERROR_SHARING_VIOLATION).
        /// That conflict is almost always a previous KliveAPI listener whose "https://+:443/" URL
        /// registration has not been released yet — after a crash-restart of this Critical service or
        /// an ungraceful process exit. Each retry fully disposes the stale listener (Close() releases
        /// the http.sys URL group; a bare Stop() does not) and rebuilds a fresh one, with a short
        /// backoff to give http.sys time to drop the old registration. Non-conflict failures
        /// (e.g. ERROR_ACCESS_DENIED) are rethrown immediately since retrying cannot fix them.
        /// </summary>
        private async Task StartListenerWithRetry()
        {
            const int maxAttempts = 6;
            const int backoffMs = 2500;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    listener.Start();
                    return;
                }
                catch (HttpListenerException ex) when (
                    ex.NativeErrorCode == 183 /* ERROR_ALREADY_EXISTS */ ||
                    ex.NativeErrorCode == 32  /* ERROR_SHARING_VIOLATION */)
                {
                    await ServiceLogError(ex, $"HttpListener.Start() failed — a prefix conflicts with an existing " +
                        $"registration (attempt {attempt}/{maxAttempts}). A previous listener has likely not released " +
                        $"the URL yet; disposing it and retrying in {backoffMs}ms.");

                    try { listener.Close(); } catch { }

                    if (attempt >= maxAttempts) throw;

                    await Task.Delay(backoffMs);
                    listener = CreateConfiguredListener();
                }
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
                // Counts pings the handler actually served — the watchdog compares this
                // across a failed ping to tell "server answered but client starved" apart
                // from "server never processed it".
                Interlocked.Increment(ref _pingsServed);
                await req.ReturnResponse("Pong", "text/html");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);
            await CreateRoute("/", async (req) =>
            {
                await req.ReturnResponse(RootLandingText, "text/plain");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Anybody);
            await CreateRoute("/allRoutes", async (req) =>
            {
                var copy = ControllerLookup.ToDictionary();
                string resp = JsonConvert.SerializeObject(copy);
                await req.ReturnResponse(resp, "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Associate);
            // One round-trip for what used to be N parallel dashboard GETs.
            // Guest (not Anybody) so a stale-cookie call fails once at pipeline
            // level (smart-tarpit fast-fail) instead of N per-item 401s.
            await CreateBufferedRoute("/batch", HandleBatchRequest, HttpMethod.Post, KMProfileManager.KMPermissions.Guest, 64 * 1024);
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

            // Response-cache observability + manual controls (Klives-only). The stats
            // route reads no tracked store, so it is never itself cached.
            await CreateRoute("/KliveAPI/cache/stats", async (req) =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    enabled = cacheEnabled,
                    denylistPrefixes = cacheDenylistPrefixes,
                    cache = responseCache.GetStatsSnapshot()
                }), "application/json");
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

            await CreateRoute("/KliveAPI/cache/clear", async (req) =>
            {
                responseCache.Clear();
                await req.ReturnResponse(JsonConvert.SerializeObject(new { cleared = true }), "application/json");
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        /// <summary>
        /// Dispatches a JSON array of GET sub-requests through the existing route
        /// handlers and returns their combined responses, collapsing what used to
        /// be N parallel browser fetches (and N connection/preflight round-trips)
        /// into a single request. Each sub-request is permission-checked and
        /// isolated individually; a failing item never fails the whole batch.
        /// Body shape: [{"path":"/route?query"}, ...] (bare-string items accepted too).
        /// </summary>
        private async Task HandleBatchRequest(UserRequest req)
        {
            const int MaxItems = 20;
            const int MaxBodyBytes = 64 * 1024;

            if ((req.userMessageBytes?.Length ?? 0) > MaxBodyBytes)
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Batch body too large." }), "application/json", null, (HttpStatusCode)413);
                return;
            }

            List<string> paths = new();
            try
            {
                var token = JToken.Parse(req.userMessageContent ?? string.Empty);
                if (token is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item.Type == JTokenType.String) paths.Add(item.ToString());
                        else if (item is JObject obj && obj["path"] != null) paths.Add(obj["path"].ToString());
                    }
                }
            }
            catch
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Invalid batch body; expected a JSON array." }), "application/json", null, HttpStatusCode.BadRequest);
                return;
            }

            if (paths.Count == 0)
            {
                await req.ReturnResponse("[]", "application/json");
                return;
            }
            if (paths.Count > MaxItems)
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = $"Batch limited to {MaxItems} items." }), "application/json", null, HttpStatusCode.BadRequest);
                return;
            }

            var results = new JObject[paths.Count];
            var tasks = new Task[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                int index = i;
                string rawPath = paths[i];
                // Task.Run so sub-requests run in parallel like today's browser burst
                // (handlers with synchronous prologues don't serialise each other).
                tasks[i] = Task.Run(async () => { results[index] = await ExecuteBatchItem(req, rawPath); });
            }
            await Task.WhenAll(tasks);

            var response = new JArray();
            foreach (var r in results) response.Add(r);
            await req.ReturnResponse(response.ToString(Formatting.None), "application/json");
        }

        private async Task<JObject> ExecuteBatchItem(UserRequest batchReq, string rawPath)
        {
            var result = new JObject { ["path"] = rawPath ?? "" };
            try
            {
                if (string.IsNullOrWhiteSpace(rawPath)) return BatchError(result, 400, "Empty path.");

                string pathPart = rawPath;
                string queryPart = "";
                int q = rawPath.IndexOf('?');
                if (q >= 0)
                {
                    pathPart = rawPath[..q];
                    queryPart = rawPath[(q + 1)..];
                }

                string normalized = NormalizeRoute(pathPart);
                if (string.Equals(normalized, "/batch", StringComparison.OrdinalIgnoreCase))
                    return BatchError(result, 400, "Nested /batch is not allowed.");

                if (!ControllerLookup.TryGetValue(normalized, out RouteInfo routeInfo))
                    return BatchError(result, 404, "Route not found.");
                if (routeInfo.requestBodyMode == RequestBodyMode.Streaming)
                    return BatchError(result, 400, "Streaming routes cannot run inside /batch.");
                if (routeInfo.normalizedMethod != "GET")
                    return BatchError(result, 405, "Only GET routes may be batched.");

                // Permission — mirror the pipeline's checks exactly (auth-bypass guard).
                var required = routeInfo.authenticationLevelRequired;
                bool allowed = required == KMProfileManager.KMPermissions.Anybody
                    || (batchReq.user != null && batchReq.user.CanLogin && batchReq.user.KlivesManagementRank >= required);
                if (!allowed) return BatchError(result, 401, "Insufficient permission.");

                NameValueCollection subParams = string.IsNullOrEmpty(queryPart)
                    ? new NameValueCollection()
                    : HttpUtility.ParseQueryString(queryPart);

                // Batch sub-requests share the direct-GET cache: a hit skips execution
                // entirely (collapsing to a dictionary lookup), and a fill warmed by
                // one path accelerates the other.
                bool cacheable = cacheEnabled && !IsRouteDenylisted(normalized);
                string? cacheKey = cacheable
                    ? ResponseCache.BuildKey(normalized, subParams, batchReq.user?.UserID)
                    : null;
                if (cacheKey != null)
                {
                    CacheEntry? hit = responseCache.TryGetValid(cacheKey);
                    if (hit != null)
                    {
                        responseCache.RecordHit(normalized);
                        apiStatistics?.RecordRequest(normalized, "GET", hit.StatusCode, TimeSpan.Zero, true);
                        return BuildBatchResultFromParts(result, hit.StatusCode, hit.ContentType, hit.RawBody);
                    }
                    responseCache.RecordMiss(normalized);
                }

                // Sub-request is a copy of the batch request writing into a capture buffer.
                UserRequest sub = batchReq;
                sub.route = normalized;
                sub.userParameters = subParams;
                sub.userMessageBytes = Array.Empty<byte>();
                sub.userMessageContent = string.Empty;
                sub.capture = new CapturedResponse();

                var itemStopwatch = Stopwatch.StartNew();
                DependencyScope? scope = cacheable ? CacheDeps.OpenScope() : null;
                try
                {
                    await routeInfo.action(sub);
                }
                finally
                {
                    if (scope != null) CacheDeps.Seal(scope);
                }
                itemStopwatch.Stop();

                if (!sub.capture.Completed)
                {
                    apiStatistics?.RecordRequest(normalized, "GET", 500, itemStopwatch.Elapsed, true);
                    return BatchError(result, 500, "Handler did not produce a response.");
                }

                apiStatistics?.RecordRequest(normalized, "GET", sub.capture.StatusCode, itemStopwatch.Elapsed, true);

                if (cacheKey != null && scope != null)
                {
                    try
                    {
                        responseCache.TryStoreFromParts(cacheKey, sub.capture.StatusCode, sub.capture.ContentType,
                            sub.capture.Headers, sub.capture.Body, sub.capture.IsBinary, scope);
                    }
                    catch { /* storing must never affect the batch response */ }
                }

                return BuildBatchResultFromParts(result, sub.capture.StatusCode, sub.capture.ContentType, sub.capture.Body);
            }
            catch (Exception ex)
            {
                return BatchError(result, 500, ex.Message);
            }
        }

        private static JObject BuildBatchResultFromParts(JObject result, int status, string contentType, byte[] body)
        {
            result["status"] = status;
            result["ok"] = status is >= 200 and < 300;
            result["contentType"] = contentType;
            string text = Encoding.UTF8.GetString(body ?? Array.Empty<byte>());
            if (contentType != null
                && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                && OmniPaths.IsValidJson(text))
            {
                result["body"] = new JRaw(text);
            }
            else
            {
                result["body"] = text;
            }
            return result;
        }

        private static JObject BatchError(JObject result, int status, string message)
        {
            result["status"] = status;
            result["ok"] = false;
            result["contentType"] = "application/json";
            result["body"] = new JObject { ["error"] = message };
            return result;
        }

        private async void StartWatchdog()
        {
            // Ensure the omnisetting exists with its default value so it is discoverable
            // from the omnisettings UI as soon as KliveAPI starts.
            try { await GetBoolOmniSetting("KliveAPIWatchdogEnabled", defaultValue: true); }
            catch { /* settings manager may not yet be ready; ignore */ }

            using System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            await Task.Delay(15000); // Give the API time to initialize

            int consecutiveFailures = 0;
            const int FailureThresholdBeforeRestart = 3;

            while (ContinueListenLoop)
            {
                bool watchdogEnabled = true;
                try { watchdogEnabled = await GetBoolOmniSetting("KliveAPIWatchdogEnabled", defaultValue: true); }
                catch { /* if the settings manager fails, fall back to enabled */ }

                if (!watchdogEnabled)
                {
                    consecutiveFailures = 0;
                    try { await Task.Delay(30000, cancellationToken.Token); }
                    catch (TaskCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }

                long pingsBefore = Interlocked.Read(ref _pingsServed);
                try
                {
                    var response = await client.GetAsync($"http://127.0.0.1:{apiHTTPPORT}/ping");
                    response.EnsureSuccessStatusCode();
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    if (!ContinueListenLoop) return;

                    consecutiveFailures++;
                    long pingsDuring = Interlocked.Read(ref _pingsServed) - pingsBefore;
                    await ServiceLogError(ex, $"Watchdog ping failed ({consecutiveFailures}/{FailureThresholdBeforeRestart}). " +
                        $"pingsServedDuringFailedWindow={pingsDuring}. {CaptureRuntimeHealth()}");

                    if (consecutiveFailures >= FailureThresholdBeforeRestart)
                    {
                        await ServiceLogError(ex, "Watchdog detected API is unresponsive across multiple consecutive checks. Restarting API service...");
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

        /// <summary>
        /// Emits a health snapshot once a minute from a DEDICATED OS thread (never the
        /// thread pool), so it keeps logging even when the pool is fully starved — the
        /// prime suspect for "API stops responding after a while". The trajectory of
        /// inFlight / worker-threads-available / heapMB across the minutes leading up to
        /// the failure is what pins the cause. Guarded so a graceful restart reuses the
        /// single long-lived thread instead of spawning duplicates.
        /// </summary>
        private void StartHealthHeartbeat()
        {
            if (_healthHeartbeatThread is { IsAlive: true }) return;
            _healthHeartbeatThread = new Thread(() =>
            {
                while (ContinueListenLoop)
                {
                    try { _ = ServiceLog($"[watchdog-diag heartbeat] {CaptureRuntimeHealth()}"); }
                    catch { }
                    try { Thread.Sleep(60000); }
                    catch (ThreadInterruptedException) { return; }
                }
            })
            { IsBackground = true, Name = "KliveAPI_HealthHeartbeat" };
            _healthHeartbeatThread.Start();
        }

        /// <summary>
        /// Cheap, lock-free snapshot of process/runtime health formatted for a single log
        /// line. The three watchdog hypotheses read straight off it:
        ///   • workerThreadsAvail near 0 + high pendingWorkItems => thread-pool starvation
        ///     (the watchdog's own GetAsync continuation can't run; the API may be fine).
        ///   • sinceLastAccept large + listening=true            => listen loop wedged.
        ///   • high inFlight + coincident slow-request logs       => a handler hogging the pipeline.
        /// </summary>
        private string CaptureRuntimeHealth()
        {
            try
            {
                ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
                ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
                long pending = ThreadPool.PendingWorkItemCount;
                int poolThreads = ThreadPool.ThreadCount;
                int inFlight = Volatile.Read(ref _inFlightRequests);

                long acceptTicks = Interlocked.Read(ref _lastContextAcceptedUtcTicks);
                long completeTicks = Interlocked.Read(ref _lastRequestCompletedUtcTicks);
                string sinceAccept = acceptTicks == 0 ? "n/a"
                    : $"{(DateTime.UtcNow - new DateTime(acceptTicks, DateTimeKind.Utc)).TotalMilliseconds:F0}ms";
                string sinceComplete = completeTicks == 0 ? "n/a"
                    : $"{(DateTime.UtcNow - new DateTime(completeTicks, DateTimeKind.Utc)).TotalMilliseconds:F0}ms";

                bool listening = false;
                try { listening = listener?.IsListening ?? false; } catch { }

                return $"[health] inFlight={inFlight}, poolThreads={poolThreads}, "
                    + $"workerThreadsAvail={availWorker}/{maxWorker}, ioThreadsAvail={availIo}/{maxIo}, "
                    + $"pendingWorkItems={pending}, sinceLastAccept={sinceAccept}, "
                    + $"sinceLastRequestDone={sinceComplete}, listening={listening}, "
                    + $"gc(g0={GC.CollectionCount(0)},g1={GC.CollectionCount(1)},g2={GC.CollectionCount(2)}), "
                    + $"heapMB={GC.GetTotalMemory(false) / (1024 * 1024)}";
            }
            catch (Exception ex)
            {
                return $"[health-unavailable: {ex.Message}]";
            }
        }

        private void KliveAPI_ServiceQuitRequest()
        {
            ServiceLog("Stopping KliveAPI listener, as service is quitting.");
            ContinueListenLoop = false;
            // Close() (not just Stop()) so http.sys fully releases the URL group. A bare Stop()
            // can leave the "https://+:443/" registration lingering, which then makes the next
            // start (e.g. a crash-restart of this Critical service) fail with
            // "conflicts with an existing registration on the machine".
            try { listener.Close(); } catch { }
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

        // Returns the production TLS certificate (the same PFX used for HTTPS on klive.dev) so
        // other services can reuse it — e.g. KliveMail's inbound SMTP STARTTLS. Returns null if
        // the certificate has not been created yet (caller should fall back to plaintext / retry).
        public X509Certificate2? GetServerCertificate()
        {
            try
            {
                if (certInstaller == null) return null;
                string pfxPath = certInstaller.rootAuthorityPfxPath;
                if (string.IsNullOrEmpty(pfxPath) || !File.Exists(pfxPath)) return null;
                return new X509Certificate2(
                    pfxPath,
                    "klives",
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Failed to load server certificate for reuse.");
                return null;
            }
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
        public Task CreateRoute(string route, Func<UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired)
        {
            return CreateRouteCore(route, handler, method, authenticationLevelRequired, RequestBodyMode.Buffered, null);
        }

        /// <summary>
        /// Creates a conventional buffered route with an enforced request-body limit.
        /// Existing CreateRoute callers remain unlimited for backwards compatibility.
        /// </summary>
        public Task CreateBufferedRoute(string route, Func<UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired, long maxBodyBytes)
        {
            return CreateRouteCore(route, handler, method, authenticationLevelRequired, RequestBodyMode.Buffered, ValidateBodyLimit(maxBodyBytes));
        }

        /// <summary>
        /// Creates a route whose handler consumes UserRequest.RequestBodyStream
        /// directly, without populating userMessageBytes or userMessageContent.
        /// </summary>
        public Task CreateStreamingRoute(string route, Func<UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired, long maxBodyBytes)
        {
            return CreateRouteCore(route, handler, method, authenticationLevelRequired, RequestBodyMode.Streaming, ValidateBodyLimit(maxBodyBytes));
        }

        private Task CreateRouteCore(string route, Func<UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions authenticationLevelRequired, RequestBodyMode requestBodyMode, long? maxBodyBytes)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(method);
            route = NormalizeRoute(route);
            RouteInfo routeInfo = new()
            {
                action = handler,
                authenticationLevelRequired = authenticationLevelRequired,
                method = method,
                normalizedMethod = NormalizeMethod(method.Method),
                requestBodyMode = requestBodyMode,
                maxBodyBytes = maxBodyBytes
            };
            if (ControllerLookup.TryAdd(route, routeInfo))
            {
                string bodyMode = requestBodyMode == RequestBodyMode.Streaming ? "streaming" : "buffered";
                string limit = maxBodyBytes.HasValue ? $", max body {maxBodyBytes.Value} bytes" : string.Empty;
                ServiceLog($"New {method.ToString().ToUpper()} route created: {route} ({bodyMode}{limit})");
            }
            return Task.CompletedTask;
        }

        private static long ValidateBodyLimit(long maxBodyBytes)
        {
            if (maxBodyBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBodyBytes), "The maximum request body size cannot be negative.");
            }
            return maxBodyBytes;
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

        private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "Cookie", "Set-Cookie", "Proxy-Authorization"
        };

        private static string? CaptureHeadersJson(HttpListenerRequest req)
        {
            try
            {
                if (req?.Headers == null || req.Headers.Count == 0) return null;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in req.Headers.AllKeys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    string raw = req.Headers[key] ?? string.Empty;
                    if (SensitiveHeaderNames.Contains(key))
                    {
                        dict[key] = string.IsNullOrEmpty(raw) ? "[empty]" : "[REDACTED:" + raw.Length + "]";
                    }
                    else
                    {
                        if (raw.Length > 4096) raw = raw.Substring(0, 4096) + "...[truncated]";
                        dict[key] = raw;
                    }
                }
                string json = JsonConvert.SerializeObject(dict);
                if (json.Length > 32768) json = json.Substring(0, 32768);
                return json;
            }
            catch { return null; }
        }

        private static (string? text, bool truncated) TruncateBodyForStorage(byte[]? bodyBytes, string? bodyText, int maxBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0) return (null, false);
            if (bodyBytes.Length <= maxBytes) return (bodyText ?? string.Empty, false);
            string truncated = Encoding.UTF8.GetString(bodyBytes, 0, maxBytes);
            return (truncated, true);
        }

        internal sealed class RequestBodyTooLargeException : IOException
        {
            public long MaxBodyBytes { get; }

            public RequestBodyTooLargeException(long maxBodyBytes)
                : base($"Request body exceeds the route limit of {maxBodyBytes} bytes.")
            {
                MaxBodyBytes = maxBodyBytes;
            }
        }

        /// <summary>
        /// Read-only wrapper that keeps the HttpListener request stream unread until
        /// the route handler consumes it, while enforcing the route limit and hashing
        /// the bytes that are successfully delivered to the handler.
        /// </summary>
        internal sealed class AuditedRequestBodyStream : Stream
        {
            private readonly Stream inner;
            private readonly long? maxBodyBytes;
            private readonly long declaredLength;
            private readonly RequestBodyAuditState audit;
            private readonly IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private long bytesRead;
            private bool sawEndOfStream;
            private bool finished;

            public AuditedRequestBodyStream(Stream inner, long? maxBodyBytes, long declaredLength, RequestBodyAuditState audit)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
                this.maxBodyBytes = maxBodyBytes;
                this.declaredLength = declaredLength;
                this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
            }

            public override bool CanRead => !finished && inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => bytesRead;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                EnsureReadable();
                ArgumentNullException.ThrowIfNull(buffer);
                if ((uint)offset > buffer.Length || (uint)count > buffer.Length - offset)
                    throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                if (IsAtLimit) return ProbeForOverflow();
                int allowedCount = GetAllowedReadCount(count);
                int read = inner.Read(buffer, offset, allowedCount);
                return ProcessRead(buffer.AsSpan(offset, read));
            }

            public override int Read(Span<byte> buffer)
            {
                EnsureReadable();
                if (buffer.IsEmpty) return 0;
                if (IsAtLimit) return ProbeForOverflow();
                int allowedCount = GetAllowedReadCount(buffer.Length);
                int read = inner.Read(buffer[..allowedCount]);
                return ProcessRead(buffer[..read]);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                EnsureReadable();
                ArgumentNullException.ThrowIfNull(buffer);
                if ((uint)offset > buffer.Length || (uint)count > buffer.Length - offset)
                    throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                if (IsAtLimit) return await ProbeForOverflowAsync(cancellationToken);
                int allowedCount = GetAllowedReadCount(count);
                int read = await inner.ReadAsync(buffer, offset, allowedCount, cancellationToken);
                return ProcessRead(buffer.AsSpan(offset, read));
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                EnsureReadable();
                if (buffer.IsEmpty) return 0;
                if (IsAtLimit) return await ProbeForOverflowAsync(cancellationToken);
                int allowedCount = GetAllowedReadCount(buffer.Length);
                int read = await inner.ReadAsync(buffer[..allowedCount], cancellationToken);
                return ProcessRead(buffer.Span[..read]);
            }

            public override int ReadByte()
            {
                Span<byte> oneByte = stackalloc byte[1];
                return Read(oneByte) == 0 ? -1 : oneByte[0];
            }

            private bool IsAtLimit => maxBodyBytes.HasValue && bytesRead == maxBodyBytes.Value;

            public bool CompleteBody => sawEndOfStream || (declaredLength >= 0 && bytesRead == declaredLength);

            private void EnsureReadable()
            {
                if (finished) throw new ObjectDisposedException(nameof(AuditedRequestBodyStream));
            }

            private int GetAllowedReadCount(int requestedCount)
            {
                if (requestedCount <= 0) return 0;
                if (!maxBodyBytes.HasValue) return requestedCount;

                long remaining = maxBodyBytes.Value - bytesRead;
                if (remaining < 0) throw new RequestBodyTooLargeException(maxBodyBytes.Value);
                return (int)Math.Min(requestedCount, Math.Min((long)int.MaxValue, remaining));
            }

            private int ProbeForOverflow()
            {
                if (declaredLength >= 0 && declaredLength == bytesRead)
                {
                    sawEndOfStream = true;
                    Finish();
                    return 0;
                }

                int extraByte = inner.ReadByte();
                if (extraByte < 0)
                {
                    sawEndOfStream = true;
                    Finish();
                    return 0;
                }

                audit.ReportOversized(bytesRead == long.MaxValue ? long.MaxValue : bytesRead + 1);
                throw new RequestBodyTooLargeException(maxBodyBytes!.Value);
            }

            private async ValueTask<int> ProbeForOverflowAsync(CancellationToken cancellationToken)
            {
                if (declaredLength >= 0 && declaredLength == bytesRead)
                {
                    sawEndOfStream = true;
                    Finish();
                    return 0;
                }

                byte[] probe = new byte[1];
                int read = await inner.ReadAsync(probe.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    sawEndOfStream = true;
                    Finish();
                    return 0;
                }

                audit.ReportOversized(bytesRead == long.MaxValue ? long.MaxValue : bytesRead + 1);
                throw new RequestBodyTooLargeException(maxBodyBytes!.Value);
            }

            private int ProcessRead(ReadOnlySpan<byte> data)
            {
                if (data.Length == 0)
                {
                    sawEndOfStream = true;
                    Finish();
                    return 0;
                }

                long newLength = checked(bytesRead + data.Length);
                if (maxBodyBytes.HasValue && newLength > maxBodyBytes.Value)
                {
                    audit.ReportOversized(newLength);
                    throw new RequestBodyTooLargeException(maxBodyBytes.Value);
                }

                hasher.AppendData(data);
                bytesRead = newLength;
                return data.Length;
            }

            public void Finish()
            {
                if (finished) return;
                finished = true;

                // With a known Content-Length, reading exactly that many bytes is a
                // complete body even if the handler did not perform one final EOF read.
                bool completeBody = CompleteBody;
                if (completeBody)
                {
                    string hash = bytesRead == 0 ? string.Empty : Convert.ToHexString(hasher.GetHashAndReset());
                    audit.ReportAutomatically(bytesRead, hash);
                }
                else
                {
                    // A handler may intentionally inspect only a prefix. Record what was
                    // actually observed, but never claim a hash for an incomplete entity.
                    audit.ReportAutomatically(bytesRead, null);
                }
                hasher.Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Finish();
                // HttpListener owns the underlying request stream.
                base.Dispose(disposing);
            }
        }

        private async Task<(byte[] BodyBytes, string BodyText)> ReadRequestBodyAsync(HttpListenerRequest req, long? maxBodyBytes = null)
        {
            if (!req.HasEntityBody || !CanRequestCarryBody(req.HttpMethod))
            {
                return (Array.Empty<byte>(), string.Empty);
            }

            if (maxBodyBytes.HasValue && req.ContentLength64 > maxBodyBytes.Value)
            {
                throw new RequestBodyTooLargeException(maxBodyBytes.Value);
            }

            int bodyCapacity = req.ContentLength64 > 0 && req.ContentLength64 <= int.MaxValue
                ? (int)req.ContentLength64
                : 0;

            using MemoryStream bodyStream = bodyCapacity > 0 ? new MemoryStream(bodyCapacity) : new MemoryStream();
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            while (true)
            {
                int requested = buffer.Length;
                if (maxBodyBytes.HasValue)
                {
                    long remaining = maxBodyBytes.Value - totalRead;
                    long detectionWindow = remaining == long.MaxValue ? long.MaxValue : remaining + 1;
                    requested = (int)Math.Min(buffer.Length, detectionWindow);
                }

                int read = await req.InputStream.ReadAsync(buffer.AsMemory(0, requested));
                if (read == 0) break;

                totalRead = checked(totalRead + read);
                if (maxBodyBytes.HasValue && totalRead > maxBodyBytes.Value)
                {
                    throw new RequestBodyTooLargeException(maxBodyBytes.Value);
                }
                await bodyStream.WriteAsync(buffer.AsMemory(0, read));
            }
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
                    // Timestamp every accept: a stale value while pings fail means the
                    // listen loop is wedged (not dequeuing from http.sys) rather than a
                    // slow handler downstream.
                    Interlocked.Exchange(ref _lastContextAcceptedUtcTicks, DateTime.UtcNow.Ticks);
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

        /// <summary>
        /// Periodically refreshes the response-cache configuration from OmniSettings
        /// (registering them so they appear in the settings UI). A 15s cadence gives
        /// a near-instant kill switch without hooking the settings-changed event,
        /// mirroring how the watchdog reads its own setting each loop.
        /// </summary>
        private async void StartResponseCacheConfigWatcher()
        {
            while (ContinueListenLoop)
            {
                try
                {
                    // The cache is ON by default and the setting is a kill switch. It is
                    // deliberately named ...Disabled (not the old ...Enabled): OmniSettings
                    // persist their default on first read, so live servers already have
                    // KliveAPIResponseCacheEnabled=false materialized from the initial
                    // default-off rollout — flipping that default in code would never
                    // take effect on them. A fresh inverted setting defaults every
                    // deployment to cached-on while keeping a one-toggle escape hatch.
                    bool disabled = await GetBoolOmniSetting("KliveAPIResponseCacheDisabled", defaultValue: false);
                    int maxMB = await GetIntOmniSetting("KliveAPICacheMaxMB", defaultValue: 256);
                    string denylist = await GetStringOmniSetting("KliveAPICacheDenylistPrefixes", defaultValue: "") ?? "";

                    cacheEnabled = !disabled;
                    cacheDenylistPrefixes = ParseDenylist(denylist);
                    responseCache.Configure((long)Math.Max(1, maxMB) * 1024 * 1024, 10_000);
                }
                catch { /* settings manager may not be ready yet; retry next loop */ }

                try { await Task.Delay(15000, cancellationToken.Token); }
                catch (TaskCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private static string[] ParseDenylist(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        private bool IsRouteDenylisted(string route)
        {
            string[] list = cacheDenylistPrefixes;
            if (list.Length == 0) return false;
            string r = route.ToLowerInvariant();
            foreach (string prefix in list)
            {
                if (r.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool ClientRequestedNoCache(HttpListenerRequest req)
        {
            string? cc = req.Headers["Cache-Control"];
            return !string.IsNullOrEmpty(cc) && cc.Contains("no-cache", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The single dispatch chokepoint for the response cache. On a cache-eligible
        /// GET it serves a valid stored entry (near-free, incl. 304s), otherwise it
        /// runs the handler exactly as before while teeing the response and tracking
        /// the datasets it read, then stores the result if the fill is cacheable.
        /// Non-GET, disabled, denylisted, or forced-bypass requests run untouched.
        /// </summary>
        private async Task DispatchRouteAsync(RouteInfo routeData, UserRequest request, string route)
        {
            if (!cacheEnabled
                || NormalizeMethod(request.req.HttpMethod) != "GET"
                || IsRouteDenylisted(route))
            {
                await routeData.action(request);
                return;
            }

            string? userId = request.user?.UserID;
            string key = ResponseCache.BuildKey(route, request.userParameters, userId);

            bool forceBypass = request.user != null
                && request.user.KlivesManagementRank >= KMProfileManager.KMPermissions.Klives
                && ClientRequestedNoCache(request.req);

            if (!forceBypass)
            {
                CacheEntry? hit = responseCache.TryGetValid(key);
                if (hit != null)
                {
                    responseCache.RecordHit(route);
                    await CachedResponseWriter.WriteCachedResponseAsync(request.context, request.req, hit, request.requestTimer);
                    return;
                }
            }

            // Miss (or forced bypass): fill while recording + tracking dependencies.
            if (forceBypass) responseCache.RecordBypass(route);
            else responseCache.RecordMiss(route);
            try { request.context.Response.Headers.Set("X-KliveAPI-Cache", forceBypass ? "BYPASS" : "MISS"); } catch { }

            var recording = new ResponseRecording();
            request.recording = recording;
            DependencyScope scope = CacheDeps.OpenScope();
            try
            {
                await routeData.action(request);
            }
            finally
            {
                CacheDeps.Seal(scope);
            }

            try { responseCache.TryStoreFromRecording(key, recording, scope); }
            catch { /* storing must never affect the already-sent response */ }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            Stopwatch requestStopwatch = Stopwatch.StartNew();
            Interlocked.Increment(ref _inFlightRequests);
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
            string? defenceBodyText = null;
            bool defenceBodyTruncated = false;
            string? defenceHeadersJson = null;
            RequestBodyAuditState? defenceBodyAudit = null;
            AuditedRequestBodyStream? streamingBodyStream = null;
            const int MaxStoredBodyBytes = 65536; // 64KB cap for stored body text

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
                defenceHeadersJson = CaptureHeadersJson(req);
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
                request.requestTimer = requestStopwatch;
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

                    if (routeData.requestBodyMode == RequestBodyMode.Streaming)
                    {
                        long declaredBodyLength = req.ContentLength64 >= 0 ? req.ContentLength64 : 0;
                        // Streaming audit records bytes actually observed, not an untrusted
                        // Content-Length declaration. A known oversize is reported explicitly.
                        defenceBodyAudit = new RequestBodyAuditState(0);
                        streamingBodyStream = new AuditedRequestBodyStream(
                            req.InputStream,
                            routeData.maxBodyBytes,
                            req.ContentLength64,
                            defenceBodyAudit);
                        request.requestBodyAudit = defenceBodyAudit;
                        request.streamingRequestBody = streamingBodyStream;
                        defenceBodyLength = 0;
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

                    async Task PrepareBodyForDispatchAsync()
                    {
                        // Do not reveal a private route's body limit until its permission check
                        // has passed. Unknown/chunked lengths are enforced while reading.
                        if (routeData.maxBodyBytes.HasValue && req.HasEntityBody &&
                            req.ContentLength64 > routeData.maxBodyBytes.Value)
                        {
                            defenceBodyLength = req.ContentLength64;
                            defenceBodyAudit?.ReportOversized(req.ContentLength64);
                            throw new RequestBodyTooLargeException(routeData.maxBodyBytes.Value);
                        }
                        if (routeData.requestBodyMode == RequestBodyMode.Buffered && CanRequestCarryBody(req.HttpMethod))
                        {
                            (request.userMessageBytes, request.userMessageContent) = await ReadRequestBodyAsync(req, routeData.maxBodyBytes);
                            defenceBodyLength = request.userMessageBytes?.LongLength ?? 0;
                            defenceBodyHash = OmniDefenceService.HashBody(request.userMessageBytes ?? Array.Empty<byte>());
                            (defenceBodyText, defenceBodyTruncated) = TruncateBodyForStorage(
                                request.userMessageBytes, request.userMessageContent, MaxStoredBodyBytes);
                        }
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
                            await PrepareBodyForDispatchAsync();
                            await DispatchRouteAsync(routeData, request, route);
                            return;
                        }

                        if (request.user.KlivesManagementRank >= routeData.authenticationLevelRequired)
                        {
                            await PrepareBodyForDispatchAsync();
                            await DispatchRouteAsync(routeData, request, route);
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
                        await PrepareBodyForDispatchAsync();
                        await DispatchRouteAsync(routeData, request, route);
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
                            // Smart tarpit: the first few failures per IP fail fast so a
                            // legitimate user with a stale cookie gets an instant logout
                            // redirect instead of 6-9s hangs; repeat offenders (scanners)
                            // still get the escalating delay. If OmniDefence is down we
                            // fail fast rather than slow.
                            const int FastFailAuthFailures = 3;
                            int recentFailures = defence?.RegisterAuthFailure(defenceIp) ?? 0;
                            if (recentFailures > FastFailAuthFailures)
                            {
                                try { await Task.Delay(TimeSpan.FromSeconds(6) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)), cancellationToken.Token); } catch { }
                            }
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
                    if (CanRequestCarryBody(req.HttpMethod))
                    {
                        try
                        {
                            var (bodyBytes, bodyText) = await ReadRequestBodyAsync(req);
                            defenceBodyLength = bodyBytes?.LongLength ?? 0;
                            defenceBodyHash = OmniDefenceService.HashBody(bodyBytes ?? Array.Empty<byte>());
                            (defenceBodyText, defenceBodyTruncated) = TruncateBodyForStorage(bodyBytes, bodyText, MaxStoredBodyBytes);
                        }
                        catch { }
                    }
                    await request.ReturnResponse("Route not found", "text/plain", null, HttpStatusCode.NotFound);
                }
            }
            catch (RequestBodyTooLargeException ex)
            {
                defenceDenyReason = "RequestBodyTooLarge";
                defenceOutcome = RequestOutcome.ClientError;
                try { context.Response.KeepAlive = false; } catch { }
                await ReturnPayloadTooLargeAsync(context, ex.MaxBodyBytes);
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
                Interlocked.Decrement(ref _inFlightRequests);
                Interlocked.Exchange(ref _lastRequestCompletedUtcTicks, DateTime.UtcNow.Ticks);

                // Surface any request that itself ran slowly (as opposed to the process
                // being globally starved). If /ping timeouts coincide with these, a real
                // handler is hogging the pipeline; if they don't, the stall is elsewhere.
                try
                {
                    double elapsedMs = requestStopwatch.Elapsed.TotalMilliseconds;
                    if (elapsedMs >= SlowRequestLogThresholdMs)
                    {
                        int statusForLog = context?.Response?.StatusCode ?? 0;
                        _ = ServiceLog($"[watchdog-diag] Slow request: {statsMethod} {statsRoute} " +
                            $"took {elapsedMs:F0}ms (status {statusForLog}). {CaptureRuntimeHealth()}");
                    }
                }
                catch { /* diagnostics must never affect the response */ }

                if (streamingBodyStream != null && !streamingBodyStream.CompleteBody)
                {
                    // Never reuse a connection whose streaming entity was only partially
                    // consumed; unread bytes must not become the next request.
                    try { context.Response.KeepAlive = false; } catch { }
                }
                streamingBodyStream?.Finish();
                if (defenceBodyAudit != null)
                {
                    defenceBodyLength = defenceBodyAudit.BodyLength;
                    defenceBodyHash = defenceBodyAudit.BodySha256;
                    // Streaming bodies are intentionally never retained as audit text.
                    defenceBodyText = null;
                    defenceBodyTruncated = false;
                }

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
                                                : RequestOutcome.ClientError;
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
                                ClientPage = defenceClientPage,
                                BodyText = defenceBodyText,
                                BodyTruncated = defenceBodyTruncated,
                                HeadersJson = defenceHeadersJson
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

        private static async Task ReturnPayloadTooLargeAsync(HttpListenerContext context, long maxBodyBytes)
        {
            try
            {
                HttpListenerResponse response = context.Response;
                if (!response.OutputStream.CanWrite) return;

                byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    error = "Request body too large.",
                    maxBodyBytes
                }));
                response.StatusCode = 413;
                response.ContentType = "application/json";
                response.Headers.Set("Access-Control-Allow-Origin", "*");
                response.Headers.Set("Access-Control-Expose-Headers", "*");
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body);
                response.Close();
            }
            catch
            {
                // A handler may already have closed its response before a late read
                // discovers the overflow. The connection is already safely closed.
                try { context.Response.Abort(); } catch { }
            }
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
