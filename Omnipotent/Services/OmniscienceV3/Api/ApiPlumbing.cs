using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Temporal;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>Marks a type that is part of the v3 HTTP contract. <see cref="TypeScriptContracts"/> emits it,
    /// and every type reachable from it, for the website.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
    public sealed class ApiContractAttribute : Attribute { }

    /// <summary>Stable, documented error codes. Responses never carry exception text or stack traces (F548).</summary>
    public static class ApiErrors
    {
        public const string BadRequest = "bad_request";
        public const string NotFound = "not_found";
        public const string Forbidden = "forbidden";
        public const string Conflict = "conflict";
        public const string Unavailable = "unavailable";
        /// <summary>v3 is installed but <c>OmniscienceV3_ShadowEnabled</c> is off.</summary>
        public const string Disabled = "disabled";
        /// <summary>v3 is still opening its stores and modules; the message names the stage.</summary>
        public const string Starting = "starting";
        /// <summary>v3 failed to start; the message says why and the service log has the details.</summary>
        public const string Failed = "failed";
        public const string Internal = "internal";
    }

    [ApiContract]
    public sealed record ApiError(string Code, string Message);

    /// <summary>Thrown by a handler to answer with a classified error (the registrar turns it into JSON).</summary>
    public sealed class ApiException : Exception
    {
        public ApiException(HttpStatusCode status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public HttpStatusCode Status { get; }
        public string Code { get; }

        public static ApiException BadRequest(string message) => new(HttpStatusCode.BadRequest, ApiErrors.BadRequest, message);
        public static ApiException NotFound(string message) => new(HttpStatusCode.NotFound, ApiErrors.NotFound, message);
        public static ApiException Conflict(string message) => new(HttpStatusCode.Conflict, ApiErrors.Conflict, message);
        public static ApiException Forbidden(string message) => new(HttpStatusCode.Forbidden, ApiErrors.Forbidden, message);
        public static ApiException Unavailable(string code, string message) => new(HttpStatusCode.ServiceUnavailable, code, message);
    }

    /// <summary>The one JSON shape of the v3 API: camelCase, enums as their names, nulls omitted, instants as
    /// UTC epoch milliseconds (T1, F559: <see cref="TypeScriptContracts"/> rejects DateTime/TimeSpan members).</summary>
    public static class ApiJson
    {
        public static readonly JsonSerializerOptions Options = Create();

        // KernelJson already writes enums by name.
        private static JsonSerializerOptions Create() =>
            new(KernelJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    }

    /// <summary>JSON responses for v3 routes.</summary>
    public static class ApiResponses
    {
        /// <summary>The charset parameter also stops KliveAPI re-parsing every body to check it is JSON.</summary>
        public const string JsonContentType = "application/json; charset=utf-8";

        /// <summary>
        /// v3 stores are not instrumented for the KliveAPI response cache, so every v3 response opts out of it.
        /// Otherwise a response that also touched a tracked dataset (the caller's own access dependency is one)
        /// would be cached against a version v3's writes never bump and served stale — the trap that shipped the
        /// "settings don't save" bug. Read models are precomputed, so the server cache would add nothing; browsers
        /// still revalidate cheaply because KliveAPI attaches an ETag and answers 304.
        /// </summary>
        public static Task OkAsync(UserRequest req, object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            CacheDeps.MarkUncacheable("omniscience-v3: stores are not version-tracked");
            return req.ReturnResponse(JsonSerializer.Serialize(body, ApiJson.Options), JsonContentType, NoHeaders, status);
        }

        /// <summary>KliveAPI's parameter is declared non-nullable but treats null as "no extra headers".</summary>
        private static readonly NameValueCollection NoHeaders = null!;

        public static Task ErrorAsync(UserRequest req, HttpStatusCode status, string code, string message)
        {
            CacheDeps.MarkUncacheable("omniscience-v3: error response");
            return req.ReturnResponse(JsonSerializer.Serialize(new ApiError(code, message), ApiJson.Options), JsonContentType, NoHeaders, status);
        }
    }

    /// <summary>
    /// Strict query-string reading. A malformed value is a 400 naming the parameter, never a silent fallback to
    /// a default (which made v2 filters quietly return unfiltered data).
    /// </summary>
    public readonly struct ApiQuery
    {
        private readonly NameValueCollection values;

        public ApiQuery(NameValueCollection? values) => this.values = values ?? new NameValueCollection();

        public static ApiQuery Of(UserRequest req) => new(req.userParameters);

        public string? Str(string name, int maxLength = 512)
        {
            string? v = values[name];
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.Length > maxLength) throw ApiException.BadRequest($"'{name}' is longer than {maxLength} characters.");
            return v;
        }

        public string Required(string name, int maxLength = 512) =>
            Str(name, maxLength) ?? throw ApiException.BadRequest($"'{name}' is required.");

        public long? Long(string name)
        {
            string? v = Str(name, 32);
            if (v == null) return null;
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
                ? n : throw ApiException.BadRequest($"'{name}' must be an integer.");
        }

        public int Int(string name, int fallback, int min, int max)
        {
            long? v = Long(name);
            if (v == null) return fallback;
            if (v < min || v > max) throw ApiException.BadRequest($"'{name}' must be between {min} and {max}.");
            return (int)v.Value;
        }

        public bool? Bool(string name)
        {
            string? v = Str(name, 8);
            if (v == null) return null;
            return v.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => throw ApiException.BadRequest($"'{name}' must be true or false."),
            };
        }

        public T? Enum<T>(string name) where T : struct, System.Enum
        {
            string? v = Str(name, 64);
            if (v == null) return null;
            if (System.Enum.TryParse<T>(v, true, out var e) && System.Enum.IsDefined(e)) return e;
            throw ApiException.BadRequest($"'{name}' must be one of: {string.Join(", ", System.Enum.GetNames<T>())}.");
        }

        /// <summary>A comma-separated list (empty when absent).</summary>
        public IReadOnlyList<string> List(string name, int maxItems = 100)
        {
            string? v = Str(name, 4096);
            if (v == null) return Array.Empty<string>();
            var items = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length > maxItems) throw ApiException.BadRequest($"'{name}' lists more than {maxItems} values.");
            return items;
        }

        /// <summary>
        /// An instant: UTC epoch milliseconds, or ISO-8601 with an explicit offset or Z. A local time without an
        /// offset is rejected — it would be guessed in the server's zone, which is exactly the v2 bug (T1).
        /// </summary>
        public long? Instant(string name)
        {
            string? v = Str(name, 64);
            if (v == null) return null;
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)) return ms;
            if (Temporal.Instant.TryParseIsoWithOffset(v, out long parsed)) return parsed;
            throw ApiException.BadRequest($"'{name}' must be epoch milliseconds or ISO-8601 with an offset (e.g. 2024-01-14T23:41:00Z).");
        }

        /// <summary>A half-open window [from, to). Either end may be open; from must precede to.</summary>
        public (long? From, long? To) Window(string fromName = "from", string toName = "to")
        {
            long? from = Instant(fromName), to = Instant(toName);
            if (from != null && to != null && from >= to) throw ApiException.BadRequest($"'{fromName}' must be earlier than '{toName}'.");
            return (from, to);
        }
    }

    /// <summary>Request bodies.</summary>
    public static class ApiBody
    {
        public static T Read<T>(UserRequest req) where T : class
        {
            if (string.IsNullOrWhiteSpace(req.userMessageContent)) throw ApiException.BadRequest("A JSON body is required.");
            try
            {
                return JsonSerializer.Deserialize<T>(req.userMessageContent, ApiJson.Options)
                       ?? throw ApiException.BadRequest("A JSON body is required.");
            }
            catch (JsonException ex)
            {
                throw ApiException.BadRequest($"The body is not valid JSON for this route ({ex.Path ?? "root"}).");
            }
        }

        /// <summary>Who did it, for audit rows: the signed-in profile's name and id.</summary>
        public static string Actor(UserRequest req) =>
            req.user == null ? "unknown" : $"{req.user.Name} ({req.user.UserID})";
    }

    /// <summary>
    /// Typed route registration against the live KliveAPI instance (the OmniTrader/Projects pattern), replacing
    /// v2's reflection-based registration that failed silently and partially (F364). Every handler is wrapped:
    /// an <see cref="ApiException"/> becomes its classified response, anything else a logged 500 with a stable
    /// code (F548), and a failure while answering never escapes into KliveAPI's dispatcher.
    /// </summary>
    public sealed class RouteRegistrar
    {
        /// <summary>Default request-body ceiling for v3 routes (bodies are small JSON commands).</summary>
        public const long DefaultMaxBodyBytes = 1024 * 1024;

        private readonly KliveAPI.KliveAPI? direct;
        private readonly Func<IEnumerable<object>> activeServices;
        private readonly Action<string, Exception?> logError;
        private readonly ConcurrentDictionary<string, byte> registered = new(StringComparer.Ordinal);
        private KliveAPI.KliveAPI? api;

        public RouteRegistrar(KliveAPI.KliveAPI? direct, Func<IEnumerable<object>> activeServices, Action<string, Exception?> logError)
        {
            this.direct = direct;
            this.activeServices = activeServices;
            this.logError = logError;
        }

        /// <summary>"GET /omniscience/v3/status"-style keys of everything registered so far.</summary>
        public IReadOnlyCollection<string> Registered => registered.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        private async Task<KliveAPI.KliveAPI> ApiAsync(CancellationToken ct)
        {
            if (api != null) return api;
            if (direct != null) return api = direct;
            var waited = Stopwatch.StartNew();
            while (true)
            {
                var found = activeServices().OfType<KliveAPI.KliveAPI>().FirstOrDefault();
                if (found != null) return api = found;
                if (waited.Elapsed > TimeSpan.FromSeconds(90))
                    throw new InvalidOperationException("KliveAPI did not become active within 90s; OmniscienceV3 routes cannot register.");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        public async Task RouteAsync(string path, HttpMethod method, PermissionDef permission, Func<UserRequest, Task> handler,
            long maxBodyBytes = DefaultMaxBodyBytes, CancellationToken ct = default)
        {
            var target = await ApiAsync(ct).ConfigureAwait(false);
            try
            {
                await target.CreateBufferedRoute(path, req => GuardAsync(path, method, handler, req), method, permission, maxBodyBytes).ConfigureAwait(false);
                registered[method.Method + " " + path] = 0;
            }
            catch (Exception ex)
            {
                logError($"[OmniscienceV3] failed to register route {method} {path}.", ex);
                throw;
            }
        }

        /// <summary>
        /// A route whose handler reads the request body as a stream (<c>UserRequest.RequestBodyStream</c>) instead of
        /// having it buffered in memory: uploads. <paramref name="maxBodyBytes"/> is enforced while reading.
        /// </summary>
        public async Task StreamingRouteAsync(string path, HttpMethod method, PermissionDef permission, Func<UserRequest, Task> handler,
            long maxBodyBytes, CancellationToken ct = default)
        {
            var target = await ApiAsync(ct).ConfigureAwait(false);
            try
            {
                await target.CreateStreamingRoute(path, req => GuardAsync(path, method, handler, req), method, permission, maxBodyBytes).ConfigureAwait(false);
                registered[method.Method + " " + path] = 0;
            }
            catch (Exception ex)
            {
                logError($"[OmniscienceV3] failed to register route {method} {path}.", ex);
                throw;
            }
        }

        public async Task WebSocketAsync(string path, PermissionDef permission,
            Func<HttpListenerContext, System.Net.WebSockets.WebSocket, NameValueCollection, Omnipotent.Profiles.KMProfileManager.KMProfile?, Task> handler,
            CancellationToken ct = default)
        {
            var target = await ApiAsync(ct).ConfigureAwait(false);
            try
            {
                await target.CreateWebSocketRoute(path, handler, permission).ConfigureAwait(false);
                registered["WS " + path] = 0;
            }
            catch (Exception ex)
            {
                logError($"[OmniscienceV3] failed to register WebSocket route {path}.", ex);
                throw;
            }
        }

        private async Task GuardAsync(string path, HttpMethod method, Func<UserRequest, Task> handler, UserRequest req)
        {
            try
            {
                await handler(req).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                await TryAnswerAsync(req, ex.Status, ex.Code, ex.Message).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                await TryAnswerAsync(req, HttpStatusCode.NotFound, ApiErrors.NotFound, "Not found.").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryAnswerAsync(req, HttpStatusCode.ServiceUnavailable, ApiErrors.Unavailable, "The request was cancelled because the service is stopping.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logError($"[OmniscienceV3] {method.Method} {path} failed.", ex);
                await TryAnswerAsync(req, HttpStatusCode.InternalServerError, ApiErrors.Internal, "Internal error; the service log has the details.").ConfigureAwait(false);
            }
        }

        private static async Task TryAnswerAsync(UserRequest req, HttpStatusCode status, string code, string message)
        {
            try
            {
                await ApiResponses.ErrorAsync(req, status, code, message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The response had already started (a stream that failed midway) or the client left: nothing more can be sent.
                return;
            }
        }
    }

    /// <summary>
    /// NDJSON streaming for answers that arrive incrementally (inquiries, tactics, replica replies, exports): a
    /// chunked HTTP response read with fetch + ReadableStream in the browser, so the Bearer header still works
    /// (EventSource cannot send it). One JSON object per line; a mid-stream failure ends with an in-band
    /// <c>{"type":"error"}</c> line because the 200 status has already been sent.
    /// </summary>
    public static class NdjsonStream
    {
        public const string ContentType = "application/x-ndjson; charset=utf-8";

        public static async Task WriteAsync(UserRequest req, IAsyncEnumerable<object> events, CancellationToken ct)
        {
            CacheDeps.MarkUncacheable("omniscience-v3: streaming");
            var headers = new NameValueCollection { ["Cache-Control"] = "no-store", ["X-Accel-Buffering"] = "no" };
            var stream = req.PrepareStreamResponse(ContentType, -1, HttpStatusCode.OK, headers);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024) { NewLine = "\n" };
            try
            {
                await foreach (var ev in events.WithCancellation(ct).ConfigureAwait(false))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(ev, ApiJson.Options)).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                // The client went away: stop producing. Nothing can be written back.
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TryWriteFinalAsync(writer, new { type = "error", code = ApiErrors.Unavailable, message = "The service is stopping." }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await TryWriteFinalAsync(writer, new { type = "error", code = ApiErrors.Internal, message = "The stream failed; the service log has the details." }).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task TryWriteFinalAsync(StreamWriter writer, object line)
        {
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(line, ApiJson.Options)).ConfigureAwait(false);
                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                // The client is gone too.
                return;
            }
        }
    }
}
