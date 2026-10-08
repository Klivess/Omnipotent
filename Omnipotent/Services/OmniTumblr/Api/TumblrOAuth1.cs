using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniTumblr.Api
{
    /// <summary>
    /// OAuth 1.0a request signing (RFC 5849, HMAC-SHA1 — the only method Tumblr accepts). Query
    /// parameters and form-urlencoded body parameters are signed; JSON and multipart bodies are not.
    /// </summary>
    public static class TumblrOAuth1
    {
        /// <summary>RFC 3986 percent-encoding: only A–Z a–z 0–9 - . _ ~ pass through; UTF-8, uppercase hex.</summary>
        public static string PercentEncode(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length * 2);
            foreach (byte b in Encoding.UTF8.GetBytes(value))
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '.' || c == '_' || c == '~')
                    sb.Append(c);
                else
                    sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>Scheme and host lowercased, default ports dropped, query and fragment removed.</summary>
        public static string NormalizeUrl(Uri uri)
        {
            string scheme = uri.Scheme.ToLowerInvariant();
            string host = uri.Host.ToLowerInvariant();
            bool defaultPort = (scheme == "http" && uri.Port == 80) || (scheme == "https" && uri.Port == 443) || uri.IsDefaultPort;
            return $"{scheme}://{host}{(defaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}";
        }

        /// <summary>Decodes the query string the way OAuth does (application/x-www-form-urlencoded).</summary>
        public static IEnumerable<KeyValuePair<string, string>> ParseQuery(string? query)
        {
            if (string.IsNullOrEmpty(query)) yield break;
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part[..eq];
                string value = eq < 0 ? string.Empty : part[(eq + 1)..];
                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(key.Replace('+', ' ')),
                    Uri.UnescapeDataString(value.Replace('+', ' ')));
            }
        }

        public static string BuildSignatureBaseString(string method, Uri uri, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var encoded = ParseQuery(uri.Query)
                .Concat(parameters)
                .Select(p => (Key: PercentEncode(p.Key), Value: PercentEncode(p.Value)))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal);
            string normalizedParameters = string.Join("&", encoded.Select(p => p.Key + "=" + p.Value));
            return method.ToUpperInvariant() + "&" + PercentEncode(NormalizeUrl(uri)) + "&" + PercentEncode(normalizedParameters);
        }

        public static string ComputeSignature(string baseString, string consumerSecret, string? tokenSecret)
        {
            byte[] key = Encoding.ASCII.GetBytes(PercentEncode(consumerSecret) + "&" + PercentEncode(tokenSecret ?? string.Empty));
            using var hmac = new HMACSHA1(key);
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(baseString)));
        }

        /// <summary>
        /// Builds the complete "OAuth …" Authorization header value for a request.
        /// </summary>
        /// <param name="formParameters">Form-urlencoded body parameters (signed); omit for JSON/multipart bodies.</param>
        /// <param name="extraOAuthParameters">oauth_callback / oauth_verifier during the token handshake.</param>
        public static string BuildAuthorizationHeader(string method, Uri uri, string consumerKey, string consumerSecret,
            string? token, string? tokenSecret,
            IEnumerable<KeyValuePair<string, string>>? formParameters = null,
            IReadOnlyDictionary<string, string>? extraOAuthParameters = null,
            string? nonce = null, string? timestamp = null)
        {
            var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["oauth_consumer_key"] = consumerKey,
                ["oauth_nonce"] = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                ["oauth_signature_method"] = "HMAC-SHA1",
                ["oauth_timestamp"] = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["oauth_version"] = "1.0",
            };
            if (!string.IsNullOrEmpty(token)) oauth["oauth_token"] = token;
            if (extraOAuthParameters != null)
                foreach (var kv in extraOAuthParameters) oauth[kv.Key] = kv.Value;

            string baseString = BuildSignatureBaseString(method, uri,
                oauth.Concat(formParameters ?? Enumerable.Empty<KeyValuePair<string, string>>()));
            oauth["oauth_signature"] = ComputeSignature(baseString, consumerSecret, tokenSecret);

            return "OAuth " + string.Join(", ", oauth.Select(kv => $"{PercentEncode(kv.Key)}=\"{PercentEncode(kv.Value)}\""));
        }

        /// <summary>Parses an application/x-www-form-urlencoded token response (oauth_token=…&amp;oauth_token_secret=…).</summary>
        public static Dictionary<string, string> ParseFormResponse(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in ParseQuery(body?.Trim()))
                result[kv.Key] = kv.Value;
            return result;
        }
    }
}
