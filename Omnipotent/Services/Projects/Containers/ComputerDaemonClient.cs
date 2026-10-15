using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The per-container credential for klive-computerd's privileged routes (exec, input, screenshot,
    /// clipboard, windows, browser state, diagnostics).
    ///
    /// Derived, not stored: HMAC-SHA256 of the container ID under one host key, so the harness can
    /// always recompute a desktop's token — after a restart, after a registry rebuild, for a desktop
    /// it adopted — and the only secret to keep is that key. The container learns its token from a
    /// file the harness writes into it through the Docker archive API.
    ///
    /// What it protects against is narrow on purpose: other processes on the Docker host reaching the
    /// loopback-published port. Anything inside the container can read the file, which is fine — the
    /// agent owns that computer outright (passwordless sudo) and the token grants nothing beyond it.
    /// </summary>
    public static class ComputerDaemonToken
    {
        /// <summary>Where the token lives inside a desktop. Root-owned, world-readable: the daemon runs as
        /// the desktop user and must read it, and nothing in the container is less trusted than that user.</summary>
        internal const string ContainerTokenDirectory = "/etc";
        internal const string ContainerTokenFileName = "klive-computerd.token";
        internal const string ContainerTokenPath = ContainerTokenDirectory + "/" + ContainerTokenFileName;

        private static readonly object Gate = new();
        private static byte[]? hostKey;

        /// <summary>Test seam: a fixed key instead of the persisted host key.</summary>
        internal static Func<byte[]>? KeyOverride { get; set; }

        public static string For(string containerID)
        {
            byte[] key = KeyOverride?.Invoke() ?? HostKey();
            byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("klive-computerd|" + (containerID ?? "")));
            return Convert.ToHexString(mac).ToLowerInvariant()[..48];
        }

        /// <summary>SHA-256 of a token as <c>sha256sum</c> prints it, to compare against the file a
        /// container holds without ever logging the token itself.</summary>
        internal static string FileHash(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + "\n"))).ToLowerInvariant();

        private static byte[] HostKey()
        {
            lock (Gate)
            {
                if (hostKey != null) return hostKey;
                string directory = Path.GetDirectoryName(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsContainersFile))!;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "computerd.key");
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] existing = File.ReadAllBytes(path);
                        if (existing.Length == 32) return hostKey = existing;
                    }
                }
                catch { /* unreadable key: mint a new one below; tokens are re-delivered on next sync */ }
                byte[] fresh = RandomNumberGenerator.GetBytes(32);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, fresh);
                File.Move(tmp, path, overwrite: true);
                return hostKey = fresh;
            }
        }
    }

    /// <summary>A daemon exec either never reached the container (safe to run the same command another
    /// way) or did — in which case its result, even a timeout, is final.</summary>
    public readonly record struct ComputerDaemonExec(bool Reached, ContainerShellResult? Result, string? Problem)
    {
        public static ComputerDaemonExec NotReached(string problem) => new(false, null, problem);
    }

    /// <summary>What klive-computerd reports about its desktop on /health.</summary>
    public sealed class ComputerDaemonHealth
    {
        public bool Ok { get; set; }
        public bool BrowserUp { get; set; }
        public string? Service { get; set; }
        public int Protocol { get; set; }
        public long UptimeSeconds { get; set; }
        public ScreenInfo? Screen { get; set; }
        public Dictionary<string, bool> Components { get; set; } = new(StringComparer.Ordinal);
        public BrowserInfo? Browser { get; set; }
        public ResourceInfo? Resources { get; set; }
        public Dictionary<string, bool> Tools { get; set; } = new(StringComparer.Ordinal);
        public ImageInfo? Image { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public bool AuthConfigured { get; set; }
        public List<DaemonEvent> Events { get; set; } = new();

        /// <summary>True for a protocol-2+ daemon (the full control plane), false for the original
        /// browser-only service that a helper refresh has not replaced yet.</summary>
        [JsonIgnore] public bool IsControlPlane => Protocol >= 2;
        public bool Has(string capability) => Capabilities.Contains(capability, StringComparer.Ordinal);
        public bool Component(string name) => Components.TryGetValue(name, out bool up) && up;

        public sealed class ScreenInfo { public int Width { get; set; } public int Height { get; set; } }
        public sealed class BrowserInfo
        {
            public bool Process { get; set; }
            public bool Cdp { get; set; }
            public int? Tabs { get; set; }
            public int ProcessCount { get; set; }
            public JsonElement Watch { get; set; }
        }
        public sealed class ResourceInfo
        {
            public MemoryInfo? Memory { get; set; }
            public double? Load1 { get; set; }
            public double? Load5 { get; set; }
            public int? Cpus { get; set; }
            public double? RootFreeMB { get; set; }
            public double? ProjectFreeMB { get; set; }
        }
        public sealed class MemoryInfo
        {
            public double? LimitMB { get; set; }
            public double? UsedMB { get; set; }
            public double? AvailableMB { get; set; }
            public long? OomKills { get; set; }
            public string? Cgroup { get; set; }
        }
        public sealed class ImageInfo { public string Version { get; set; } = ""; public List<string> Capabilities { get; set; } = new(); }
        public sealed class DaemonEvent
        {
            public long Seq { get; set; }
            public string At { get; set; } = "";
            public string Kind { get; set; } = "";
            public string Message { get; set; } = "";
            public JsonElement Details { get; set; }
        }
    }

    /// <summary>
    /// Talks to klive-computerd, the control plane inside a desktop container (Containers/browser-service.py),
    /// over the container's published loopback port.
    ///
    /// Every operation degrades rather than throws: an absent port, a daemon that predates a route, a
    /// token not delivered yet, a stopped container — all answer null/NotReached so the caller takes the
    /// path that existed before (docker exec, VNC). The one thing that never degrades silently is a
    /// request that was SENT and then went unanswered: the command may have run, so re-running it some
    /// other way could submit a form twice. That comes back as a timed-out result, never a fallback.
    /// </summary>
    public sealed class ComputerDaemonClient
    {
        // One pooled client for the whole fleet. Per-call deadlines come from linked tokens.
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        /// <summary>Endpoints that could not be used, and until when not to try them again. Keeps a dead or
        /// outdated daemon from costing a connect timeout on every single action.</summary>
        private static readonly ConcurrentDictionary<string, (DateTime Until, string Why)> Unusable = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, (DateTime At, ComputerDaemonHealth Health)> HealthCache = new(StringComparer.Ordinal);
        private static readonly TimeSpan HealthFreshness = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

        /// <summary>How long past a command's own timeout the client waits for the daemon's answer. The
        /// daemon kills the command at its timeout plus a grace period, so this only fires when the
        /// daemon itself has stopped answering.</summary>
        internal static TimeSpan ExecResponseMargin { get; set; } = TimeSpan.FromSeconds(25);

        /// <summary>Where protocol disagreements and unreachable daemons are reported.</summary>
        internal static Action<string>? Diagnostics { get; set; }

        private readonly string host;
        private readonly int port;
        private readonly string containerID;

        public ComputerDaemonClient(string host, int port, string containerID)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            this.port = port;
            this.containerID = containerID ?? "";
        }

        public bool Configured => port > 0;
        private string Endpoint => $"{host}:{port}";
        private string Token => ComputerDaemonToken.For(containerID);

        internal static void ResetForTests()
        {
            Unusable.Clear();
            HealthCache.Clear();
        }

        /// <summary>Forget everything learned about this endpoint — after a container restart, a helper
        /// refresh or a token delivery, the next call should try the daemon again immediately.</summary>
        public void Forget()
        {
            foreach (var key in Unusable.Keys.Where(k => k.StartsWith(Endpoint + "|", StringComparison.Ordinal)).ToList())
                Unusable.TryRemove(key, out _);
            HealthCache.TryRemove(Endpoint, out _);
        }

        private bool Skipping(string route, out string why)
        {
            why = "";
            if (!Unusable.TryGetValue(Endpoint + "|" + route, out var entry)) return false;
            if (DateTime.UtcNow < entry.Until) { why = entry.Why; return true; }
            Unusable.TryRemove(Endpoint + "|" + route, out _);
            return false;
        }

        private void MarkUnusable(string route, TimeSpan forHowLong, string why)
        {
            Unusable[Endpoint + "|" + route] = (DateTime.UtcNow + forHowLong, why);
            try { Diagnostics?.Invoke($"computerd {Endpoint} {route}: {why} — using the fallback path for {forHowLong.TotalMinutes:0.#} min."); }
            catch { }
        }

        private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null, bool authenticated = true)
        {
            var request = new HttpRequestMessage(method, $"http://{host}:{port}{path}") { Content = content };
            if (authenticated) request.Headers.TryAddWithoutValidation("X-Klive-Token", Token);
            return request;
        }

        private static ByteArrayContent JsonContent(object body)
        {
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(body);
            var content = new ByteArrayContent(raw);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = raw.Length;
            return content;
        }

        /// <summary>
        /// The daemon's view of its desktop, or null when it cannot be reached. Cached for a few seconds
        /// across every adapter on this endpoint: a burst of actions asks once.
        /// </summary>
        public async Task<ComputerDaemonHealth?> HealthAsync(CancellationToken ct, bool fresh = false)
        {
            if (!Configured) return null;
            if (!fresh && HealthCache.TryGetValue(Endpoint, out var cached) && DateTime.UtcNow - cached.At < HealthFreshness)
                return cached.Health;
            if (!fresh && Skipping("health", out _)) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                using var response = await Http.SendAsync(Request(HttpMethod.Get, "/health", authenticated: false), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    // Something answers on the port, but not a daemon that speaks /health.
                    MarkUnusable("health", TimeSpan.FromSeconds(30), $"HTTP {(int)response.StatusCode} on /health");
                    return null;
                }
                var health = await response.Content.ReadFromJsonAsync<ComputerDaemonHealth>(Json, timeout.Token);
                if (health == null) return null;
                HealthCache[Endpoint] = (DateTime.UtcNow, health);
                return health;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A daemon that does not answer its cheapest route will not answer the others either;
                // skip it briefly instead of paying this timeout on every action.
                MarkUnusable("health", TimeSpan.FromSeconds(20), ex.GetType().Name);
                return null;
            }
        }

        /// <summary>The control plane, when this desktop runs a daemon new enough to have it and the
        /// harness's token reaches it. Null means "use the old path".</summary>
        public async Task<ComputerDaemonHealth?> ControlPlaneAsync(string capability, CancellationToken ct)
        {
            var health = await HealthAsync(ct);
            if (health == null || !health.IsControlPlane || !health.Has(capability) || !health.AuthConfigured) return null;
            return health;
        }

        /// <summary>
        /// Runs a command inside the desktop. <paramref name="idempotent"/> commands (probes, the browser
        /// supervisor) may be re-run elsewhere after any failure; everything else only when the request
        /// provably never reached the daemon.
        /// </summary>
        public async Task<ComputerDaemonExec> ExecAsync(string? command, IReadOnlyList<string>? argv, string? workingDirectory,
            int timeoutSeconds, CancellationToken ct, bool idempotent = false, string? stdin = null)
        {
            if (!Configured) return ComputerDaemonExec.NotReached("no published computerd port");
            if (Skipping("exec", out string why)) return ComputerDaemonExec.NotReached(why);
            if (await ControlPlaneAsync("exec", ct) == null)
                return ComputerDaemonExec.NotReached("computerd exec unavailable (older daemon, no token, or unreachable)");

            int seconds = Math.Clamp(timeoutSeconds, 1, 900);
            var body = new Dictionary<string, object?>
            {
                ["timeoutSeconds"] = seconds,
                ["maxOutputBytes"] = 64 * 1024,
                ["cwd"] = workingDirectory,
            };
            if (argv != null) body["argv"] = argv; else body["command"] = command;
            if (stdin != null) body["stdin"] = stdin;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds) + ExecResponseMargin);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(Request(HttpMethod.Post, "/exec", JsonContent(body)), timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex) when (IsConnectFailure(ex))
            {
                MarkUnusable("exec", TimeSpan.FromSeconds(30), "connection refused");
                return ComputerDaemonExec.NotReached("computerd refused the connection");
            }
            catch (Exception ex)
            {
                if (idempotent) return ComputerDaemonExec.NotReached($"computerd exec failed ({ex.GetType().Name})");
                return new ComputerDaemonExec(true, new ContainerShellResult(124, "",
                    $"The desktop's control daemon did not answer within {(TimeSpan.FromSeconds(seconds) + ExecResponseMargin).TotalSeconds:0}s ({ex.GetType().Name}). " +
                    "The command may or may not have run — check its effect before running it again.",
                    TimedOut: true, OutputTruncated: false), null);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // The token has not reached this container yet (or it was rebuilt). The next helper
                    // sync delivers it; until then the old path keeps working.
                    MarkUnusable("exec", TimeSpan.FromMinutes(2), $"token not accepted ({(int)response.StatusCode})");
                    Forget();
                    return ComputerDaemonExec.NotReached("computerd did not accept the token");
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    MarkUnusable("exec", TimeSpan.FromMinutes(10), "daemon predates /exec");
                    return ComputerDaemonExec.NotReached("computerd predates /exec");
                }
                if (!response.IsSuccessStatusCode)
                {
                    string detail = await SafeReadAsync(response, timeout.Token);
                    // 4xx/5xx come back before anything runs.
                    return ComputerDaemonExec.NotReached($"computerd rejected the exec ({(int)response.StatusCode} {detail})");
                }
                ExecBody? parsed;
                try { parsed = await response.Content.ReadFromJsonAsync<ExecBody>(Json, timeout.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (idempotent) return ComputerDaemonExec.NotReached("computerd returned an unreadable exec result");
                    return new ComputerDaemonExec(true, new ContainerShellResult(124, "",
                        "The desktop's control daemon returned an unreadable result; the command may have run.",
                        TimedOut: true, OutputTruncated: false), null);
                }
                if (parsed == null) return ComputerDaemonExec.NotReached("empty exec result");
                return new ComputerDaemonExec(true, new ContainerShellResult(parsed.ExitCode, parsed.Stdout ?? "",
                    parsed.Stderr ?? "", parsed.TimedOut, parsed.Truncated), null);
            }
        }

        private static bool IsConnectFailure(HttpRequestException ex) =>
            ex.InnerException is System.Net.Sockets.SocketException socket
                && socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionRefused
                    or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable
                    or System.Net.Sockets.SocketError.AddressNotAvailable
            || ex.HttpRequestError == HttpRequestError.ConnectionError;

        private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                string text = (await response.Content.ReadAsStringAsync(ct)).Trim();
                return text.Length > 300 ? text[..300] : text;
            }
            catch { return ""; }
        }

        /// <summary>The whole display as BGRA, captured inside the container. Null when unavailable.</summary>
        public async Task<(byte[] Bgra, int Width, int Height)?> CaptureBgraAsync(CancellationToken ct)
        {
            if (Skipping("screenshot", out _) || await ControlPlaneAsync("screenshot", ct) == null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using var response = await Http.SendAsync(Request(HttpMethod.Get, "/screenshot?format=raw"), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        MarkUnusable("screenshot", TimeSpan.FromMinutes(2), $"HTTP {(int)response.StatusCode}");
                    return null;
                }
                int width = HeaderInt(response, "X-Image-Width");
                int height = HeaderInt(response, "X-Image-Height");
                byte[] data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (width <= 0 || height <= 0 || data.LongLength < (long)width * height * 4) return null;
                return (data, width, height);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>A compressed screenshot for viewers that do not need the raw frame.</summary>
        public async Task<(byte[] Jpeg, int Width, int Height)?> CaptureJpegAsync(int quality, int maxWidth, CancellationToken ct)
        {
            if (Skipping("screenshot", out _) || await ControlPlaneAsync("screenshot", ct) == null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await Http.SendAsync(Request(HttpMethod.Get,
                    $"/screenshot?format=jpeg&quality={Math.Clamp(quality, 10, 95)}&maxWidth={Math.Max(0, maxWidth)}"), timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                byte[] data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                return (data, HeaderInt(response, "X-Image-Width"), HeaderInt(response, "X-Image-Height"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private static int HeaderInt(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out int value) ? value : 0;

        /// <summary>
        /// Plays pointer/keyboard events inside the desktop through XTEST. Returns the pointer position
        /// afterwards, or null when the daemon could not take the input (the caller then uses VNC).
        /// A request that reached the daemon and failed is reported, never silently replayed.
        /// </summary>
        public async Task<InputOutcome?> InputAsync(IReadOnlyList<DesktopInputEvent> events, CancellationToken ct)
        {
            if (events.Count == 0) return new InputOutcome(true, null, null);
            if (Skipping("input", out _) || await ControlPlaneAsync("input", ct) == null) return null;
            double sleepSeconds = events.Sum(e => e.T == "sleep" ? (e.Ms ?? 0) / 1000.0 : 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30 + sleepSeconds));
            try
            {
                using var response = await Http.SendAsync(Request(HttpMethod.Post, "/input", JsonContent(new { events })), timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    or HttpStatusCode.ServiceUnavailable)
                {
                    MarkUnusable("input", TimeSpan.FromMinutes(2), $"HTTP {(int)response.StatusCode}");
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                    return new InputOutcome(false, null, $"computerd rejected the input ({(int)response.StatusCode}: {await SafeReadAsync(response, timeout.Token)})");
                var body = await response.Content.ReadFromJsonAsync<InputBody>(Json, timeout.Token);
                return new InputOutcome(body?.Ok == true, body?.Pointer is { } p ? (p.X, p.Y) : null, body?.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex) when (IsConnectFailure(ex))
            {
                MarkUnusable("input", TimeSpan.FromSeconds(30), "connection refused");
                return null;
            }
            catch (Exception ex)
            {
                return new InputOutcome(false, null, $"The desktop's control daemon did not confirm the input ({ex.GetType().Name}); it may have been applied.");
            }
        }

        public async Task<string?> ClipboardGetAsync(CancellationToken ct)
        {
            if (Skipping("clipboard", out _) || await ControlPlaneAsync("clipboard", ct) == null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await Http.SendAsync(Request(HttpMethod.Get, "/clipboard"), timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                return document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? "" : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        public async Task<bool?> ClipboardSetAsync(string text, CancellationToken ct)
        {
            if (Skipping("clipboard", out _) || await ControlPlaneAsync("clipboard", ct) == null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await Http.SendAsync(Request(HttpMethod.Post, "/clipboard", JsonContent(new { text })), timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>A privileged read-only JSON route (/windows, /browser/state, /diag) as raw JSON, or null.</summary>
        public async Task<string?> GetJsonAsync(string path, string capability, CancellationToken ct, int timeoutSeconds = 15)
        {
            if (Skipping(capability, out _) || await ControlPlaneAsync(capability, ct) == null) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 60)));
                using var response = await Http.SendAsync(Request(HttpMethod.Get, path), timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        public readonly record struct InputOutcome(bool Ok, (int X, int Y)? Pointer, string? Error);

        private sealed class ExecBody
        {
            public long ExitCode { get; set; }
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
            public bool TimedOut { get; set; }
            public bool Truncated { get; set; }
            public long DurationMs { get; set; }
        }

        private sealed class InputBody
        {
            public bool Ok { get; set; }
            public string? Error { get; set; }
            public PointerBody? Pointer { get; set; }
        }

        private sealed class PointerBody { public int X { get; set; } public int Y { get; set; } }
    }

    /// <summary>One low-level input event in klive-computerd's /input vocabulary.</summary>
    public sealed class DesktopInputEvent
    {
        [JsonPropertyName("t")] public string T { get; init; } = "";
        [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? X { get; init; }
        [JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Y { get; init; }
        [JsonPropertyName("b"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? B { get; init; }
        [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? N { get; init; }
        [JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? K { get; init; }
        [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; init; }
        [JsonPropertyName("delay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Delay { get; init; }
        [JsonPropertyName("repeat"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Repeat { get; init; }
        [JsonPropertyName("ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Ms { get; init; }
        [JsonPropertyName("dx"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Dx { get; init; }
        [JsonPropertyName("dy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Dy { get; init; }

        public static DesktopInputEvent Move(int x, int y) => new() { T = "move", X = x, Y = y };
        public static DesktopInputEvent Down(int button) => new() { T = "down", B = button };
        public static DesktopInputEvent Up(int button) => new() { T = "up", B = button };
        public static DesktopInputEvent Sleep(int ms) => new() { T = "sleep", Ms = Math.Clamp(ms, 0, 10_000) };
        public static DesktopInputEvent Key(string chord, int holdMs = 55, int repeat = 1) => new() { T = "key", K = chord, Delay = holdMs, Repeat = repeat };
        public static DesktopInputEvent KeyDown(string key) => new() { T = "keydown", K = key };
        public static DesktopInputEvent KeyUp(string key) => new() { T = "keyup", K = key };
        public static DesktopInputEvent Type(string text, int delayMs) => new() { T = "type", Text = text, Delay = delayMs };
        public static DesktopInputEvent Scroll(int dy, int dx) => new() { T = "scroll", Dy = dy, Dx = dx };
    }
}
