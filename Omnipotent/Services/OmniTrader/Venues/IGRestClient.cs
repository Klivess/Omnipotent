using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Thin, signed transport for IG's REST trading API. Owns the session lifecycle (CST +
    /// X-SECURITY-TOKEN), the demo/live base-URL split, and the request-quota accounting IG returns
    /// on price endpoints.
    ///
    /// Demo and live are separate instances with separate credentials — nothing is shared, and the
    /// environment is baked into the base URL so a live call can never be issued with demo intent.
    /// </summary>
    public sealed class IGRestClient
    {
        public const string LiveBase = "https://api.ig.com/gateway/deal";
        public const string DemoBase = "https://demo-api.ig.com/gateway/deal";

        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string apiKey;
        private readonly string identifier;
        private readonly string password;
        private readonly SemaphoreSlim sessionLock = new(1, 1);

        private string? cst;
        private string? securityToken;

        public string BaseUrl { get; }
        public TradingEnvironment Environment { get; }
        public string? CurrentAccountId { get; private set; }
        public string? LightstreamerEndpoint { get; private set; }
        public DateTime? SessionEstablishedUtc { get; private set; }
        public bool HasSession => cst != null && securityToken != null;

        /// <summary>Remaining historical-price allowance as reported by IG on the last price call.</summary>
        public int? HistoricalAllowanceRemaining { get; private set; }
        public int? HistoricalAllowanceTotal { get; private set; }

        public IGRestClient(string apiKey, string identifier, string password, TradingEnvironment environment)
        {
            this.apiKey = apiKey;
            this.identifier = identifier;
            this.password = password;
            Environment = environment;
            BaseUrl = environment == TradingEnvironment.Live ? LiveBase : DemoBase;
        }

        /// <summary>
        /// Open while IG keeps rejecting these credentials. A wrong API key is not a transient fault,
        /// so retrying it on every account poll only rate-limits us and floods the alert list.
        /// </summary>
        public AuthCircuitBreaker Auth { get; } = new();

        /// <summary>Create an authenticated session. Idempotent — returns immediately if one is live.</summary>
        public async Task<bool> LoginAsync(bool force = false, CancellationToken ct = default)
        {
            if (Auth.IsOpen) throw new IGApiException(Auth.Reason, HttpStatusCode.Forbidden);

            await sessionLock.WaitAsync(ct);
            try
            {
                if (HasSession && !force) return true;

                var body = new JObject { ["identifier"] = identifier, ["password"] = password };
                using var req = BuildRequest(HttpMethod.Post, "/session", version: "2", authed: false);
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using var resp = await http.SendAsync(req, ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    cst = securityToken = null;
                    string detail = $"IG {Environment} login rejected ({(int)resp.StatusCode}): {ExtractErrorCode(text)}.";
                    if (AuthCircuitBreaker.IsRejection(resp.StatusCode))
                    {
                        // `error.security.api-key-invalid` against the demo gateway almost always means
                        // a live key: IG issues demo keys separately, on the demo platform.
                        Auth.RecordRejection(Environment == TradingEnvironment.Demo
                            ? detail + " IG demo needs its own API key, created while logged in to the demo platform."
                            : detail);
                    }
                    throw new IGApiException(detail, resp.StatusCode);
                }

                Auth.RecordSuccess();

                cst = FirstHeader(resp, "CST");
                securityToken = FirstHeader(resp, "X-SECURITY-TOKEN");
                var parsed = JObject.Parse(text);
                CurrentAccountId = (string?)parsed["currentAccountId"];
                LightstreamerEndpoint = (string?)parsed["lightstreamerEndpoint"];
                SessionEstablishedUtc = DateTime.UtcNow;
                return HasSession;
            }
            finally { sessionLock.Release(); }
        }

        public void Logout()
        {
            cst = securityToken = null;
            SessionEstablishedUtc = null;
        }

        public Task<JObject> GetAsync(string path, string version = "1", CancellationToken ct = default)
            => SendAsync(HttpMethod.Get, path, version, null, ct);

        public Task<JObject> PostAsync(string path, JObject body, string version = "1", CancellationToken ct = default)
            => SendAsync(HttpMethod.Post, path, version, body, ct);

        /// <summary>IG tunnels DELETE through POST with a <c>_method: DELETE</c> header.</summary>
        public Task<JObject> DeleteAsync(string path, JObject body, string version = "1", CancellationToken ct = default)
            => SendAsync(HttpMethod.Post, path, version, body, ct, tunnelDelete: true);

        private async Task<JObject> SendAsync(HttpMethod method, string path, string version, JObject? body,
                                              CancellationToken ct, bool tunnelDelete = false, bool retried = false)
        {
            if (!HasSession) await LoginAsync(ct: ct);

            using var req = BuildRequest(method, path, version, authed: true);
            if (tunnelDelete) req.Headers.TryAddWithoutValidation("_method", "DELETE");
            if (body != null) req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);

            // An expired session is recoverable exactly once; anything else surfaces to the caller so
            // an ambiguous order outcome is never quietly retried.
            if (resp.StatusCode == HttpStatusCode.Unauthorized && !retried)
            {
                Logout();
                await LoginAsync(force: true, ct: ct);
                return await SendAsync(method, path, version, body, ct, tunnelDelete, retried: true);
            }

            if (!resp.IsSuccessStatusCode)
                throw new IGApiException($"IG {method} {path} ({(int)resp.StatusCode}): {ExtractErrorCode(text)}", resp.StatusCode);

            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            var parsed = JObject.Parse(text);
            CaptureAllowance(parsed);
            return parsed;
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string path, string version, bool authed)
        {
            var req = new HttpRequestMessage(method, BaseUrl + path);
            req.Headers.TryAddWithoutValidation("X-IG-API-KEY", apiKey);
            req.Headers.TryAddWithoutValidation("Version", version);
            req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=UTF-8");
            if (authed)
            {
                if (cst != null) req.Headers.TryAddWithoutValidation("CST", cst);
                if (securityToken != null) req.Headers.TryAddWithoutValidation("X-SECURITY-TOKEN", securityToken);
            }
            return req;
        }

        private void CaptureAllowance(JObject parsed)
        {
            var allowance = parsed["allowance"] ?? parsed["metadata"]?["allowance"];
            if (allowance == null) return;
            HistoricalAllowanceRemaining = (int?)allowance["remainingAllowance"];
            HistoricalAllowanceTotal = (int?)allowance["totalAllowance"];
        }

        private static string? FirstHeader(HttpResponseMessage resp, string name)
            => resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static string ExtractErrorCode(string body)
        {
            try { return (string?)JObject.Parse(body)["errorCode"] ?? body; }
            catch { return string.IsNullOrWhiteSpace(body) ? "(no body)" : body; }
        }
    }

    public sealed class IGApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public IGApiException(string message, HttpStatusCode statusCode) : base(message) => StatusCode = statusCode;
    }
}
