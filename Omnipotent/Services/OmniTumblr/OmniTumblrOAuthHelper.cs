using DontPanic.TumblrSharp.OAuth;
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
        private const string AuthorizeUrl    = "https://www.tumblr.com/oauth/authorize";
        internal const string CallbackUrl    = "https://klive.dev/omnitumblr/oauth/callback";

        // ── Step 1 ──

        /// <summary>Requests a temporary OAuth request token from Tumblr using the consumer credentials.</summary>
        public static async Task<(string RequestToken, string RequestTokenSecret)> GetRequestTokenAsync(
            string consumerKey, string consumerSecret, string callbackUrl)
        {
            var oauthClient = new OAuthClientFactory().Create(consumerKey, consumerSecret);
            var requestToken = await oauthClient.GetRequestTokenAsync(callbackUrl);
            return (requestToken.Key, requestToken.Secret);
        }

        /// <summary>Returns the URL the user must visit to authorize the app and receive a PIN verifier.</summary>
        public static string BuildAuthorizationUrl(string requestToken)
            => $"{AuthorizeUrl}?oauth_token={Uri.EscapeDataString(requestToken)}";

        // ── Step 3 ──

        /// <summary>Exchanges the temporary request token + verifier PIN for a permanent access token pair.</summary>
        public static async Task<(string AccessToken, string AccessTokenSecret)> GetAccessTokenAsync(
            string consumerKey, string consumerSecret,
            Token requestToken,
            string verifierUrl)
        {
            var oauthClient = new OAuthClientFactory().Create(consumerKey, consumerSecret);
            var accessToken = await oauthClient.GetAccessTokenAsync(requestToken, verifierUrl);
            return (accessToken.Key, accessToken.Secret);
        }

        /// <summary>Exchanges the temporary request token + verifier PIN for a permanent access token pair.</summary>
        public static async Task<(string AccessToken, string AccessTokenSecret)> GetAccessTokenAsync(
            string consumerKey, string consumerSecret,
            string requestToken, string requestTokenSecret,
            string verifier)
        {
            var verifierUrl = $"oauth_token={Uri.EscapeDataString(requestToken)}&oauth_verifier={Uri.EscapeDataString(verifier.Trim())}";
            return await GetAccessTokenAsync(
                consumerKey,
                consumerSecret,
                new Token(requestToken, requestTokenSecret),
                verifierUrl);
        }
    }
}
