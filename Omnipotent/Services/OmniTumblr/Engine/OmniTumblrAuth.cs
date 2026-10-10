using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;
using System.Collections.Specialized;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>Thrown when a connection cannot be used right now (not authorized, needs reauth, no app).</summary>
    internal sealed class ConnectionUnavailableException : Exception
    {
        public ConnectionUnavailableException(string connectionId, string message) : base(message) => ConnectionId = connectionId;
        public string ConnectionId { get; }
    }

    public sealed class AuthCallbackResult
    {
        public bool Ok { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string? FlowId { get; set; }
        public string? UserName { get; set; }
        public int BlogCount { get; set; }
    }

    public sealed class AppSaveResult
    {
        public bool Saved { get; set; }
        public bool Verified { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Everything about who OmniTumblr acts as: the Tumblr app (consumer key/secret), the authorized user
    /// connections and their tokens, and the browser authorization flows that create them. Secrets are
    /// decrypted only for the duration of a call. OAuth2 access tokens (42-minute lifetime) are refreshed
    /// ahead of expiry, one refresh per connection at a time, and the rotated refresh token is flushed to
    /// disk before it is used.
    /// </summary>
    internal sealed class OmniTumblrAuth
    {
        public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(30);
        private const string KeyPurpose = "app:consumer-key";
        private const string SecretPurpose = "app:consumer-secret";

        private readonly OmniTumblrStore store;
        private readonly ITumblrApi api;
        private readonly Func<DateTime> clock;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

        public OmniTumblrAuth(OmniTumblrStore store, ITumblrApi api, Func<DateTime> clock)
        {
            this.store = store;
            this.api = api;
            this.clock = clock;
        }

        private static string TokenPurpose(string connectionId) => $"conn:{connectionId}:token";
        private static string TokenSecretPurpose(string connectionId) => $"conn:{connectionId}:token-secret";
        private static string RefreshPurpose(string connectionId) => $"conn:{connectionId}:refresh";
        private static string FlowSecretPurpose(string flowId) => $"flow:{flowId}:request-secret";

        // ─────────────────────────────── App ───────────────────────────────

        public (string Key, string Secret)? AppCredentials() => store.Read<(string, string)?>(s =>
        {
            if (!s.App.IsConfigured) return null;
            string? key = s.Vault.Unprotect(s.App.ConsumerKeyCipher, KeyPurpose);
            string? secret = s.Vault.Unprotect(s.App.ConsumerSecretCipher, SecretPurpose);
            return key == null || secret == null ? null : (key, secret);
        });

        public string CallbackUrl => store.Read(s => string.IsNullOrWhiteSpace(s.App.CallbackUrl) ? OmniTumblrAppConfig.DefaultCallbackUrl : s.App.CallbackUrl);

        /// <summary>
        /// Saves the app credentials after proving them: asking Tumblr for an OAuth 1.0a request token fails
        /// with 401 for a wrong key or secret, whichever flow the app will use. A wrong pair is not saved.
        /// </summary>
        public async Task<AppSaveResult> SaveAppAsync(string? consumerKey, string? consumerSecret, string? callbackUrl, TumblrAuthMode? mode, CancellationToken ct)
        {
            var existing = AppCredentials();
            string key = string.IsNullOrWhiteSpace(consumerKey) ? existing?.Key ?? "" : consumerKey.Trim();
            string secret = string.IsNullOrWhiteSpace(consumerSecret) ? existing?.Secret ?? "" : consumerSecret.Trim();
            string callback = string.IsNullOrWhiteSpace(callbackUrl) ? CallbackUrl : callbackUrl.Trim();
            if (key.Length == 0 || secret.Length == 0)
                return new AppSaveResult { Message = "Both the OAuth consumer key and the secret key are required." };
            if (!Uri.TryCreate(callback, UriKind.Absolute, out var cb) || cb.Scheme != Uri.UriSchemeHttps)
                return new AppSaveResult { Message = "The callback URL must be an absolute https:// URL." };

            bool verified = false;
            string message;
            try
            {
                await api.GetOAuth1RequestTokenAsync(key, secret, callback, ct);
                verified = true;
                message = "Tumblr accepted the app credentials.";
            }
            catch (TumblrApiException ex) when (ex.Status is 400 or 401 or 403 && ex.Kind != TumblrErrorKind.EdgeBlocked)
            {
                return new AppSaveResult { Message = $"Tumblr rejected these credentials ({ex.Code}): {ex.Message}. Check the OAuth consumer key and secret key at tumblr.com/oauth/apps, and that the default callback URL there is exactly {callback}." };
            }
            catch (Exception ex)
            {
                message = "Saved, but Tumblr could not be reached to verify them: " + ex.Message;
            }

            DateTime now = clock();
            store.Mutate(s =>
            {
                s.App.ConsumerKeyCipher = s.Vault.Protect(key, KeyPurpose);
                s.App.ConsumerSecretCipher = s.Vault.Protect(secret, SecretPurpose);
                s.App.ConsumerKeyHint = key.Length > 4 ? key[^4..] : key;
                s.App.CallbackUrl = callback;
                if (mode.HasValue) s.App.PreferredAuthMode = mode.Value;
                s.App.VerifiedUtc = verified ? now : s.App.VerifiedUtc;
                s.App.LastVerifyError = verified ? null : message;
                s.App.UpdatedUtc = now;
                s.MarkApp();
                s.AddEvent(verified ? EventLevel.Success : EventLevel.Warning, "app.saved", "Tumblr app credentials saved. " + message, utc: now);
            });
            await store.FlushAsync();
            return new AppSaveResult { Saved = true, Verified = verified, Message = message };
        }

        // ─────────────────────────────── Credentials ───────────────────────────────

        public async Task<TumblrCredentials> GetCredentialsAsync(string connectionId, CancellationToken ct)
        {
            var app = AppCredentials() ?? throw new ConnectionUnavailableException(connectionId, "The Tumblr app credentials are not set (Settings → Tumblr app).");
            var snap = Snapshot(connectionId);
            if (snap.Mode == TumblrAuthMode.OAuth2 && (snap.ExpiresUtc == null || snap.ExpiresUtc <= clock().AddMinutes(5)))
            {
                await RefreshOAuth2Async(connectionId, app, ct);
                snap = Snapshot(connectionId);
            }
            return new TumblrCredentials
            {
                ConsumerKey = app.Key,
                ConsumerSecret = app.Secret,
                Mode = snap.Mode,
                Token = snap.Token,
                TokenSecret = snap.TokenSecret,
            };
        }

        /// <summary>App-only credentials for public endpoints (api_key), when no user token is needed.</summary>
        public TumblrCredentials? AppOnlyCredentials()
        {
            var app = AppCredentials();
            return app == null ? null : new TumblrCredentials { ConsumerKey = app.Value.Key, ConsumerSecret = app.Value.Secret };
        }

        private (TumblrAuthMode Mode, string Token, string? TokenSecret, DateTime? ExpiresUtc) Snapshot(string connectionId) => store.Read(s =>
        {
            var conn = s.Connection(connectionId) ?? throw new ConnectionUnavailableException(connectionId, "That Tumblr connection no longer exists.");
            if (conn.Health == ConnectionHealth.NeedsReauth)
                throw new ConnectionUnavailableException(connectionId, $"Reconnect @{conn.UserName ?? "the Tumblr account"}: {conn.HealthDetail ?? "authorization expired or was revoked."}");
            string? token = s.Vault.Unprotect(conn.TokenCipher, TokenPurpose(connectionId));
            string? tokenSecret = s.Vault.Unprotect(conn.TokenSecretCipher, TokenSecretPurpose(connectionId));
            if (string.IsNullOrEmpty(token) || (conn.AuthMode == TumblrAuthMode.OAuth1 && string.IsNullOrEmpty(tokenSecret)))
                throw new ConnectionUnavailableException(connectionId, $"@{conn.UserName ?? "this account"} is not authorized. Reconnect it.");
            return (conn.AuthMode, token, tokenSecret, conn.AccessTokenExpiresUtc);
        });

        private async Task RefreshOAuth2Async(string connectionId, (string Key, string Secret) app, CancellationToken ct)
        {
            var gate = refreshLocks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var (refresh, expires) = store.Read(s =>
                {
                    var conn = s.Connection(connectionId);
                    return (s.Vault.Unprotect(conn?.RefreshTokenCipher, RefreshPurpose(connectionId)), conn?.AccessTokenExpiresUtc);
                });
                if (expires > clock().AddMinutes(5)) return; // another caller refreshed while we waited
                if (string.IsNullOrEmpty(refresh))
                {
                    MarkNeedsReauth(connectionId, "No refresh token is stored (the offline_access scope was not granted).");
                    throw new ConnectionUnavailableException(connectionId, "The access token expired and there is no refresh token. Reconnect the account.");
                }
                TumblrOAuth2Token token;
                try
                {
                    token = await api.RefreshOAuth2TokenAsync(app.Key, app.Secret, refresh, ct);
                }
                catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.Unauthorized || ex.Status == 400)
                {
                    MarkNeedsReauth(connectionId, "Tumblr refused to refresh the token: " + ex.Message);
                    throw new ConnectionUnavailableException(connectionId, "Tumblr refused to refresh the token. Reconnect the account.");
                }
                DateTime now = clock();
                store.Mutate(s =>
                {
                    var conn = s.Connection(connectionId);
                    if (conn == null) return;
                    conn.TokenCipher = s.Vault.Protect(token.AccessToken, TokenPurpose(connectionId));
                    if (!string.IsNullOrEmpty(token.RefreshToken))
                        conn.RefreshTokenCipher = s.Vault.Protect(token.RefreshToken, RefreshPurpose(connectionId));
                    conn.AccessTokenExpiresUtc = now.AddSeconds(Math.Max(60, token.ExpiresIn));
                    s.MarkConnections();
                });
                // Refresh tokens rotate: persist the new one before anything can use (and invalidate) it.
                await store.FlushAsync();
            }
            finally
            {
                gate.Release();
            }
        }

        public void MarkNeedsReauth(string connectionId, string detail)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var conn = s.Connection(connectionId);
                if (conn == null) return;
                bool transition = conn.Health != ConnectionHealth.NeedsReauth;
                conn.Health = ConnectionHealth.NeedsReauth;
                conn.HealthDetail = detail;
                if (transition)
                {
                    conn.HealthChangedUtc = now;
                    s.AddEvent(EventLevel.Error, "connection.reauth", $"Tumblr account @{conn.UserName} needs to be reconnected: {detail}", utc: now);
                }
                s.MarkConnections();
            });
        }

        public void MarkHealthy(string connectionId)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var conn = s.Connection(connectionId);
                if (conn == null || (conn.Health == ConnectionHealth.Healthy && conn.HealthDetail == null)) return;
                if (conn.Health != ConnectionHealth.Healthy) conn.HealthChangedUtc = now;
                conn.Health = ConnectionHealth.Healthy;
                conn.HealthDetail = null;
                s.MarkConnections();
            });
        }

        // ─────────────────────────────── Authorization flows ───────────────────────────────

        public async Task<(string FlowId, string AuthorizationUrl, TumblrAuthMode Mode)> BeginAsync(TumblrAuthMode? requestedMode, string? reconnectConnectionId, CancellationToken ct)
        {
            var app = AppCredentials() ?? throw new InvalidOperationException("Set the Tumblr app credentials first (Settings → Tumblr app).");
            var mode = requestedMode ?? store.Read(s => s.App.PreferredAuthMode);
            string callback = CallbackUrl;
            DateTime now = clock();
            var flow = new PendingAuthFlow { Mode = mode, CreatedUtc = now, ReconnectConnectionId = reconnectConnectionId };
            string url;
            if (mode == TumblrAuthMode.OAuth2)
            {
                url = api.BuildOAuth2AuthorizeUrl(app.Key, flow.FlowId, callback);
            }
            else
            {
                var (token, secret) = await api.GetOAuth1RequestTokenAsync(app.Key, app.Secret, callback, ct);
                flow.RequestToken = token;
                flow.RequestTokenSecretCipher = store.Read(s => s.Vault.Protect(secret, FlowSecretPurpose(flow.FlowId)));
                url = api.BuildOAuth1AuthorizeUrl(token);
            }
            store.Mutate(s =>
            {
                ExpireFlowsLocked(s, now);
                s.Flows[flow.FlowId] = flow;
                s.MarkFlows();
            });
            await store.FlushAsync();
            return (flow.FlowId, url, mode);
        }

        public PendingAuthFlow? GetFlow(string flowId) =>
            store.Read(s => s.Flows.TryGetValue(flowId, out var f) ? OmniTumblrStore.Clone(f) : null);

        public void ExpireFlows() => store.Mutate(s => ExpireFlowsLocked(s, clock()));

        private static void ExpireFlowsLocked(OmniTumblrState s, DateTime now)
        {
            foreach (var flow in s.Flows.Values.ToList())
            {
                if (flow.State == "pending" && now - flow.CreatedUtc > FlowLifetime)
                {
                    flow.State = "failed";
                    flow.Error = "The authorization was not completed within 30 minutes.";
                    flow.RequestTokenSecretCipher = null;
                    s.MarkFlows();
                }
                else if (flow.State != "pending" && now - flow.CreatedUtc > TimeSpan.FromDays(1))
                {
                    s.Flows.Remove(flow.FlowId);
                    s.MarkFlows();
                }
            }
        }

        /// <summary>
        /// Completes an authorization from Tumblr's redirect: OAuth2 (code + state) or OAuth 1.0a
        /// (oauth_token + oauth_verifier). Creates or updates the connection for that Tumblr user and loads
        /// the list of blogs it can post to.
        /// </summary>
        public async Task<AuthCallbackResult> HandleCallbackAsync(NameValueCollection query, CancellationToken ct)
        {
            string? state = query["state"];
            string? code = query["code"];
            string? oauthToken = query["oauth_token"];
            string? verifier = query["oauth_verifier"];
            string? error = query["error"] ?? (query["denied"] != null ? "access_denied" : null);

            PendingAuthFlow? flow = store.Read(s =>
            {
                if (!string.IsNullOrEmpty(state) && s.Flows.TryGetValue(state, out var byState)) return byState;
                string? token = oauthToken ?? query["denied"];
                return string.IsNullOrEmpty(token) ? null : s.Flows.Values.FirstOrDefault(f => f.RequestToken == token);
            });
            if (flow == null)
                return Fail(null, "Authorization not recognised", "This authorization link has expired or was already used. Start again from the OmniTumblr page.");
            if (flow.State != "pending")
                return flow.State == "completed"
                    ? new AuthCallbackResult { Ok = true, FlowId = flow.FlowId, Title = "Already connected", Message = "This account was already connected. You can close this tab." }
                    : Fail(flow.FlowId, "Authorization failed", flow.Error ?? "This authorization can no longer be completed.");
            if (error != null)
                return CompleteFlow(flow.FlowId, null, error == "access_denied" ? "You declined the authorization on Tumblr." : "Tumblr reported an error: " + (query["error_description"] ?? error));

            if (AppCredentials() is not { } app)
                return CompleteFlow(flow.FlowId, null, "The Tumblr app credentials were removed before the authorization finished.");

            TumblrAuthMode mode = flow.Mode;
            string accessToken;
            string? tokenSecret = null, refreshToken = null, scope = null;
            DateTime? expires = null;
            try
            {
                if (mode == TumblrAuthMode.OAuth2)
                {
                    if (string.IsNullOrEmpty(code)) return CompleteFlow(flow.FlowId, null, "Tumblr's redirect had no authorization code.");
                    var token = await api.ExchangeOAuth2CodeAsync(app.Key, app.Secret, code, CallbackUrl, ct);
                    accessToken = token.AccessToken;
                    refreshToken = token.RefreshToken;
                    scope = token.Scope;
                    expires = clock().AddSeconds(Math.Max(60, token.ExpiresIn));
                }
                else
                {
                    if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(oauthToken))
                        return CompleteFlow(flow.FlowId, null, "Tumblr's redirect had no oauth_verifier.");
                    string? requestSecret = store.Read(s => s.Vault.Unprotect(flow.RequestTokenSecretCipher, FlowSecretPurpose(flow.FlowId)));
                    if (requestSecret == null) return CompleteFlow(flow.FlowId, null, "The pending authorization's secret could not be read. Start again.");
                    var (token, secret) = await api.GetOAuth1AccessTokenAsync(app.Key, app.Secret, oauthToken, requestSecret, verifier, ct);
                    accessToken = token;
                    tokenSecret = secret;
                }
            }
            catch (Exception ex)
            {
                return CompleteFlow(flow.FlowId, null, "Exchanging the authorization for a token failed: " + ex.Message);
            }

            TumblrUserInfo user;
            try
            {
                user = await api.GetUserInfoAsync(new TumblrCredentials
                {
                    ConsumerKey = app.Key, ConsumerSecret = app.Secret, Mode = mode, Token = accessToken, TokenSecret = tokenSecret,
                }, ct);
            }
            catch (Exception ex)
            {
                return CompleteFlow(flow.FlowId, null, "Authorized, but reading the account's blogs failed: " + ex.Message);
            }

            DateTime now = clock();
            string connectionId = store.Mutate(s =>
            {
                OmniTumblrConnection? conn = flow.ReconnectConnectionId != null ? s.Connection(flow.ReconnectConnectionId) : null;
                conn ??= s.Connections.Values.FirstOrDefault(c => string.Equals(c.UserName, user.Name, StringComparison.OrdinalIgnoreCase));
                bool isNew = conn == null;
                conn ??= new OmniTumblrConnection { CreatedUtc = now };
                if (!isNew && flow.ReconnectConnectionId != null && !string.IsNullOrEmpty(conn.UserName)
                    && !string.Equals(conn.UserName, user.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Reconnected as a different Tumblr user: keep the old connection, make a new one.
                    conn = new OmniTumblrConnection { CreatedUtc = now };
                    isNew = true;
                }
                conn.UserName = user.Name;
                conn.AuthMode = mode;
                conn.TokenCipher = s.Vault.Protect(accessToken, TokenPurpose(conn.ConnectionId));
                conn.TokenSecretCipher = tokenSecret == null ? null : s.Vault.Protect(tokenSecret, TokenSecretPurpose(conn.ConnectionId));
                conn.RefreshTokenCipher = refreshToken == null ? null : s.Vault.Protect(refreshToken, RefreshPurpose(conn.ConnectionId));
                conn.AccessTokenExpiresUtc = expires;
                conn.Scope = scope;
                conn.Blogs = user.Blogs;
                conn.UserInfoFetchedUtc = now;
                conn.LastVerifiedUtc = now;
                if (conn.Health != ConnectionHealth.Healthy) conn.HealthChangedUtc = now;
                conn.Health = ConnectionHealth.Healthy;
                conn.HealthDetail = null;
                s.Connections[conn.ConnectionId] = conn;
                s.MarkConnections();

                ApplyUserInfoToBlogs(s, conn, now);

                if (s.Flows.TryGetValue(flow.FlowId, out var live))
                {
                    live.State = "completed";
                    live.ConnectionId = conn.ConnectionId;
                    live.CompletedUtc = now;
                    live.RequestTokenSecretCipher = null;
                    s.MarkFlows();
                }
                s.AddEvent(EventLevel.Success, isNew ? "connection.added" : "connection.reconnected",
                    $"Tumblr account @{user.Name} {(isNew ? "connected" : "reconnected")} ({mode}); {user.Blogs.Count} blog(s) available.", utc: now);
                return conn.ConnectionId;
            });
            await store.FlushAsync();

            return new AuthCallbackResult
            {
                Ok = true,
                FlowId = flow.FlowId,
                UserName = user.Name,
                BlogCount = user.Blogs.Count,
                Title = "Tumblr account connected",
                Message = $"@{user.Name} is connected with {user.Blogs.Count} blog(s). Return to OmniTumblr to choose which blogs to manage — you can close this tab.",
            };
        }

        private AuthCallbackResult CompleteFlow(string flowId, string? connectionId, string error)
        {
            store.Mutate(s =>
            {
                if (s.Flows.TryGetValue(flowId, out var flow))
                {
                    flow.State = "failed";
                    flow.Error = error;
                    flow.ConnectionId = connectionId;
                    flow.CompletedUtc = clock();
                    flow.RequestTokenSecretCipher = null;
                    s.MarkFlows();
                }
                s.AddEvent(EventLevel.Error, "connection.failed", "Tumblr authorization failed: " + error);
            });
            return Fail(flowId, "Authorization failed", error);
        }

        private static AuthCallbackResult Fail(string? flowId, string title, string message) =>
            new() { Ok = false, FlowId = flowId, Title = title, Message = message };

        // ─────────────────────────────── User info ───────────────────────────────

        /// <summary>Re-reads /v2/user/info: the blogs the account can post to and their follower counts.</summary>
        public async Task<TumblrUserInfo> RefreshUserInfoAsync(string connectionId, CancellationToken ct)
        {
            var creds = await GetCredentialsAsync(connectionId, ct);
            TumblrUserInfo user;
            try
            {
                user = await api.GetUserInfoAsync(creds, ct);
            }
            catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.Unauthorized)
            {
                MarkNeedsReauth(connectionId, ex.Message);
                throw;
            }
            DateTime now = clock();
            store.Mutate(s =>
            {
                var conn = s.Connection(connectionId);
                if (conn == null) return;
                conn.UserName = string.IsNullOrEmpty(user.Name) ? conn.UserName : user.Name;
                conn.Blogs = user.Blogs;
                conn.UserInfoFetchedUtc = now;
                conn.LastVerifiedUtc = now;
                if (conn.Health != ConnectionHealth.Healthy) conn.HealthChangedUtc = now;
                conn.Health = ConnectionHealth.Healthy;
                conn.HealthDetail = null;
                s.MarkConnections();
                ApplyUserInfoToBlogs(s, conn, now);
            });
            return user;
        }

        /// <summary>Copies follower counts (and renames, matched by uuid) from the account's blog list onto managed blogs.</summary>
        internal static void ApplyUserInfoToBlogs(OmniTumblrState s, OmniTumblrConnection conn, DateTime now)
        {
            foreach (var blog in s.Blogs.Values.Where(b => b.ConnectionId == conn.ConnectionId))
            {
                var match = conn.Blogs.FirstOrDefault(b => blog.Uuid != null && b.Uuid == blog.Uuid)
                    ?? conn.Blogs.FirstOrDefault(b => string.Equals(b.Name, blog.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                if (blog.Uuid == null && match.Uuid != null) blog.Uuid = match.Uuid;
                if (!string.Equals(blog.Name, match.Name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(match.Name))
                {
                    s.AddEvent(EventLevel.Info, "blog.renamed", $"@{blog.Name} is now @{match.Name} on Tumblr.", blog.BlogId, utc: now);
                    blog.Name = match.Name;
                }
                if (!string.IsNullOrEmpty(match.Title)) blog.Title = match.Title;
                if (!string.IsNullOrEmpty(match.Url)) blog.Url = match.Url;
                if (match.Followers.HasValue)
                {
                    blog.Stats.Followers = match.Followers;
                    blog.Stats.FollowersSyncedUtc = now;
                    OmniTumblrInsights.RecordSnapshot(s.InsightsOf(blog.BlogId), now, match.Followers, blog.Stats.Posts);
                    s.MarkInsights(blog.BlogId);
                }
                s.MarkBlog(blog.BlogId);
            }
        }
    }
}
