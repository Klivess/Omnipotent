using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Roblox
{
    public enum QuickLoginState { Created, UserLinked, Validated, Cancelled, Expired }

    /// <summary>A Quick Log In code: the owner types <see cref="Code"/> on a signed-in device; the private key never leaves the server.</summary>
    public sealed record QuickLoginCode(string Code, string PrivateKey, DateTime ExpiresUtc);

    /// <param name="AccountName">Who entered the code, as Roblox shows it (all but the first characters masked).</param>
    public sealed record QuickLoginStatus(QuickLoginState State, string? AccountName);

    /// <summary>The session Roblox handed over for a confirmed code.</summary>
    public sealed record QuickLoginSession(string Cookie, long UserId, string Name);

    /// <summary>Roblox wanted a check before handing over the session (a captcha or a two-step code). The studio never answers those for the owner.</summary>
    public sealed class QuickLoginRefusedException : Exception
    {
        public QuickLoginRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// Roblox's Quick Log In, the way a new device signs in: the studio asks for a short code, the owner enters it on a
    /// device already signed in to Roblox (Account Settings → Quick Log In) and confirms, and the studio signs in with
    /// the code and receives a session of its own. No password passes through the studio, and the session is made from
    /// the server's own network, so Roblox does not sign it out for being used from another region, as it can a cookie
    /// copied out of a browser elsewhere. Roblox checks that the confirming device is near the one asking, so the owner
    /// confirms from the server's network. Codes last about five minutes.
    /// </summary>
    public sealed class RobloxQuickLogin
    {
        private const string Tokens = "https://apis.roblox.com/auth-token-service/v1/login";
        private const string Auth = "https://auth.roblox.com/v2";
        /// <summary>Where a signed-in owner enters the code (the same page as Account Settings → Quick Log In).</summary>
        public const string ConfirmUrl = "https://www.roblox.com/crossdevicelogin/ConfirmCode";

        private readonly RobloxHttp http;
        private readonly Func<DateTime> clock;
        private string? csrf;

        public RobloxQuickLogin(RobloxHttp http, Func<DateTime>? clock = null)
        {
            this.http = http;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public async Task<QuickLoginCode> CreateAsync(CancellationToken ct)
        {
            var r = await PostAsync(Tokens + "/create", new JObject(), null, ct);
            if (!r.Ok) throw new RobloxApiException("auth-token-service/login/create", r.Status, r.Body);
            var j = JObject.Parse(r.Body);
            string code = (string?)j["code"] ?? "", key = (string?)j["privateKey"] ?? "";
            if (code.Length == 0 || key.Length == 0) throw new RobloxApiException("auth-token-service/login/create", r.Status, r.Body, "no code in the answer");
            return new QuickLoginCode(code, key, RobloxPublicClient.Date(j["expirationTime"]) ?? clock().AddMinutes(5));
        }

        public async Task<QuickLoginStatus> StatusAsync(QuickLoginCode code, CancellationToken ct)
        {
            var r = await PostAsync(Tokens + "/status", new JObject { ["code"] = code.Code, ["privateKey"] = code.PrivateKey }, null, ct);
            // An expired or unknown code: 400 "CodeInvalid".
            if (r.Status == HttpStatusCode.BadRequest && r.Body.Contains("CodeInvalid", StringComparison.OrdinalIgnoreCase)) return new QuickLoginStatus(QuickLoginState.Expired, null);
            if (!r.Ok) throw new RobloxApiException("auth-token-service/login/status", r.Status, r.Body);
            var j = JObject.Parse(r.Body);
            var state = Enum.TryParse<QuickLoginState>((string?)j["status"], ignoreCase: true, out var s) ? s : QuickLoginState.Created;
            return new QuickLoginStatus(state, (string?)j["accountName"]);
        }

        /// <summary>Withdraws a code that is no longer wanted (best effort: an expired code is gone anyway).</summary>
        public async Task CancelAsync(QuickLoginCode code, CancellationToken ct)
        {
            try { await PostAsync(Tokens + "/cancel", new JObject { ["code"] = code.Code }, null, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or RobloxApiException) { }
        }

        /// <summary>Signs in with a confirmed code. Never answers a challenge: a captcha or two-step check ends in <see cref="QuickLoginRefusedException"/>.</summary>
        public async Task<QuickLoginSession> LoginAsync(QuickLoginCode code, CancellationToken ct)
        {
            var body = new JObject { ["ctype"] = "AuthToken", ["cvalue"] = code.Code, ["password"] = code.PrivateKey };
            var r = await PostAsync(Auth + "/login", body, null, ct);
            if (r.Headers.TryGetValues("rblx-challenge-type", out var challenge))
                throw new QuickLoginRefusedException($"Roblox asked for a {challenge.FirstOrDefault() ?? "verification"} check before signing the studio in.");
            if (!r.Ok) throw new RobloxApiException("auth.roblox.com/v2/login", r.Status, r.Body);
            var j = JObject.Parse(r.Body);
            if (j["twoStepVerificationData"] is JObject twoStep && twoStep.HasValues)
                throw new QuickLoginRefusedException("Roblox asked for the account's two-step verification code before signing the studio in.");
            string? cookie = r.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? cookies.Where(c => c.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase)).Select(RobloxSessionClient.Normalize).FirstOrDefault(c => c != null)
                : null;
            if (cookie == null) throw new RobloxApiException("auth.roblox.com/v2/login", r.Status, "", "signed in, but Roblox sent no session cookie");
            return new QuickLoginSession(cookie, (long?)j["user"]?["id"] ?? 0, (string?)j["user"]?["name"] ?? "");
        }

        /// <summary>Ends a session the studio made itself (best effort). Never used on a cookie the owner pasted: that would sign their browser out.</summary>
        public async Task LogoutAsync(string cookie, CancellationToken ct)
        {
            try { await PostAsync(Auth + "/logout", new JObject(), cookie, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException) { }
        }

        /// <summary>A JSON POST with Roblox's CSRF handshake: the first write is refused with the token to use, and sent again with it.</summary>
        private async Task<RobloxResponse> PostAsync(string url, JObject body, string? cookie, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                string? token = csrf;
                var response = await http.SendAsync("quick-login", () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json") };
                    if (token != null) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
                    if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                    return req;
                }, ct, maxAttempts: 2);
                if (response.Status == HttpStatusCode.Forbidden && attempt == 0 && response.Headers.TryGetValues("x-csrf-token", out var tokens)
                    && tokens.FirstOrDefault() is { Length: > 0 } fresh && fresh != token)
                {
                    csrf = fresh;
                    continue;
                }
                return response;
            }
        }
    }
}
