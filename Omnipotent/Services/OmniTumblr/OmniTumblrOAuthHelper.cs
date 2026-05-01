using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniTumblr
{
    /// <summary>
    /// Performs the Tumblr OAuth 1.0a PIN-based ("out-of-band") handshake.
    /// Flow:
    ///   1. GetRequestTokenAsync  → (requestToken, requestTokenSecret) + authorization URL
    ///   2. User visits authorization URL, authorizes, receives a numeric verifier PIN
    ///   3. GetAccessTokenAsync   → (oauthToken, oauthTokenSecret) ready to store
    /// </summary>
    internal static class OmniTumblrOAuthHelper
    {
        private const string RequestTokenUrl = "https://www.tumblr.com/oauth/request_token";
        private const string AccessTokenUrl  = "https://www.tumblr.com/oauth/access_token";
        private const string AuthorizeUrl    = "https://www.tumblr.com/oauth/authorize";
        internal const string CallbackUrl    = "https://klive.dev/omnitumblr/oauth/callback";

        // ── Step 1 ──

        /// <summary>Requests a temporary OAuth request token from Tumblr using the consumer credentials.</summary>
        public static async Task<(string RequestToken, string RequestTokenSecret)> GetRequestTokenAsync(
            string consumerKey, string consumerSecret, string callbackUrl)
        {
            var oauthParams = BuildBaseParams(consumerKey);
            oauthParams["oauth_callback"] = callbackUrl;

            var signature = ComputeSignature(HttpMethod.Post.Method, RequestTokenUrl, oauthParams, consumerSecret, "");
            oauthParams["oauth_signature"] = signature;

            var header = BuildAuthorizationHeader(oauthParams);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "OmniTumblr/1.0");
            var request = new HttpRequestMessage(HttpMethod.Post, RequestTokenUrl);
            request.Headers.Add("Authorization", header);

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Tumblr request-token failed ({(int)response.StatusCode}): {body}");

            var parsed = ParseOAuthBody(body);
            if (!parsed.TryGetValue("oauth_token", out var rt) || !parsed.TryGetValue("oauth_token_secret", out var rts))
                throw new InvalidOperationException($"Tumblr request-token response missing expected fields: {body}");

            return (rt, rts);
        }

        /// <summary>Returns the URL the user must visit to authorize the app and receive a PIN verifier.</summary>
        public static string BuildAuthorizationUrl(string requestToken)
            => $"{AuthorizeUrl}?oauth_token={Uri.EscapeDataString(requestToken)}";

        // ── Step 3 ──

        /// <summary>Exchanges the temporary request token + verifier PIN for a permanent access token pair.</summary>
        public static async Task<(string AccessToken, string AccessTokenSecret)> GetAccessTokenAsync(
            string consumerKey, string consumerSecret,
            string requestToken, string requestTokenSecret,
            string verifier)
        {
            var oauthParams = BuildBaseParams(consumerKey);
            oauthParams["oauth_token"]    = requestToken;
            oauthParams["oauth_verifier"] = verifier.Trim();

            var signature = ComputeSignature(HttpMethod.Post.Method, AccessTokenUrl, oauthParams, consumerSecret, requestTokenSecret);
            oauthParams["oauth_signature"] = signature;

            var header = BuildAuthorizationHeader(oauthParams);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "OmniTumblr/1.0");
            var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl);
            request.Headers.Add("Authorization", header);

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Tumblr access-token failed ({(int)response.StatusCode}): {body}");

            var parsed = ParseOAuthBody(body);
            if (!parsed.TryGetValue("oauth_token", out var at) || !parsed.TryGetValue("oauth_token_secret", out var ats))
                throw new InvalidOperationException($"Tumblr access-token response missing expected fields: {body}");

            return (at, ats);
        }

        // ── Internals ──

        private static SortedDictionary<string, string> BuildBaseParams(string consumerKey) =>
            new()
            {
                ["oauth_consumer_key"]     = consumerKey,
                ["oauth_nonce"]            = Guid.NewGuid().ToString("N"),
                ["oauth_signature_method"] = "HMAC-SHA1",
                ["oauth_timestamp"]        = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["oauth_version"]          = "1.0"
            };

        private static string ComputeSignature(
            string method, string url,
            SortedDictionary<string, string> oauthParams,
            string consumerSecret, string tokenSecret)
        {
            // Build normalized parameter string (sorted by key, percent-encoded)
            var paramStr = string.Join("&",
                oauthParams
                    .OrderBy(kv => PercentEncode(kv.Key))
                    .ThenBy(kv => PercentEncode(kv.Value))
                    .Select(kv => $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}"));

            var signatureBase = string.Join("&",
                method.ToUpperInvariant(),
                PercentEncode(url),
                PercentEncode(paramStr));

            var signingKey = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret)}";

            using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
            var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBase));
            return Convert.ToBase64String(hash);
        }

        private static string BuildAuthorizationHeader(SortedDictionary<string, string> oauthParams)
        {
            var parts = oauthParams
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{PercentEncode(kv.Key)}=\"{PercentEncode(kv.Value)}\"");
            return "OAuth " + string.Join(", ", parts);
        }

        private static string PercentEncode(string value)
            => Uri.EscapeDataString(value ?? "");

        private static Dictionary<string, string> ParseOAuthBody(string body)
            => body
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(
                    p => Uri.UnescapeDataString(p[0]),
                    p => Uri.UnescapeDataString(p[1]));
    }
}
