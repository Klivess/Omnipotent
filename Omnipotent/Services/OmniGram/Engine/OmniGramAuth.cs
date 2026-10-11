using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;
using System.Collections.Specialized;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>Thrown when an account cannot be acted as right now (not connected, needs reconnecting).</summary>
    internal sealed class AccountUnavailableException : Exception
    {
        public AccountUnavailableException(string accountId, string message) : base(message) => AccountId = accountId;
        public string AccountId { get; }
    }

    public sealed class AuthCallbackResult
    {
        public bool Ok { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string? FlowId { get; set; }
        public string? AccountId { get; set; }
        public string? Username { get; set; }
    }

    public sealed class ConnectResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string? AccountId { get; set; }
        public string? Username { get; set; }
        public bool IsNew { get; set; }
        public bool AttachedToImported { get; set; }
    }

    /// <summary>
    /// Who OmniGram acts as: the Instagram app (for the Business Login redirect flow), each account's
    /// long-lived token, and the browser authorizations that produce them. Tokens last 60 days and are
    /// refreshed well before they lapse (a token can only be refreshed once it is a day old, and never
    /// after it has expired). Secrets are decrypted only for the duration of a call.
    /// </summary>
    internal sealed class OmniGramAuth
    {
        public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(60);
        /// <summary>Refresh once a token has this little life left (and is at least a day old).</summary>
        public static readonly TimeSpan RefreshWhenRemaining = TimeSpan.FromDays(50);
        private const string SecretPurpose = "app:secret";

        private readonly OmniGramStore store;
        private readonly IInstagramApi api;
        private readonly Func<DateTime> clock;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);

        public OmniGramAuth(OmniGramStore store, IInstagramApi api, Func<DateTime> clock)
        {
            this.store = store;
            this.api = api;
            this.clock = clock;
        }

        public static string TokenPurpose(string accountId) => $"account:{accountId}:token";

        // ─────────────────────────────── App ───────────────────────────────

        public (string AppId, string Secret)? AppCredentials() => store.Read<(string, string)?>(s =>
        {
            if (!s.App.IsConfigured) return null;
            string? secret = s.Vault.Unprotect(s.App.AppSecretCipher, SecretPurpose);
            return secret == null ? null : (s.App.AppId!, secret);
        });

        public string RedirectUrl => store.Read(s => string.IsNullOrWhiteSpace(s.App.RedirectUrl) ? OmniGramAppConfig.DefaultRedirectUrl : s.App.RedirectUrl);

        /// <summary>
        /// Saves the Instagram app id/secret. Instagram offers no call that proves them on their own, so they
        /// are confirmed by the first successful Business Login (and the error then says which part is wrong).
        /// </summary>
        public async Task<(bool Saved, string Message)> SaveAppAsync(string? appId, string? appSecret, string? redirectUrl)
        {
            var existing = AppCredentials();
            string id = string.IsNullOrWhiteSpace(appId) ? existing?.AppId ?? "" : appId.Trim();
            string secret = string.IsNullOrWhiteSpace(appSecret) ? existing?.Secret ?? "" : appSecret.Trim();
            string redirect = string.IsNullOrWhiteSpace(redirectUrl) ? RedirectUrl : redirectUrl.Trim();
            if (id.Length == 0 || secret.Length == 0)
                return (false, "Both the Instagram app ID and the Instagram app secret are required.");
            if (!id.All(char.IsDigit))
                return (false, "The Instagram app ID is a number (Instagram → API setup with Instagram business login → Business login settings).");
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return (false, "The redirect URL must be an absolute https:// URL.");

            DateTime now = clock();
            store.Mutate(s =>
            {
                bool changed = s.App.AppId != id || existing?.Secret != secret;
                s.App.AppId = id;
                s.App.AppSecretCipher = s.Vault.Protect(secret, SecretPurpose);
                s.App.RedirectUrl = redirect;
                if (changed)
                {
                    s.App.VerifiedUtc = null;
                    s.App.LastVerifyError = null;
                }
                s.App.UpdatedUtc = now;
                s.MarkApp();
                s.AddEvent(EventLevel.Info, "app.saved", "Instagram app settings saved. They are confirmed by the next account you connect with Instagram.", utc: now);
            });
            await store.FlushAsync();
            return (true, "Saved. Connect an account with Instagram to confirm the app ID and secret.");
        }

        // ─────────────────────────────── Credentials ───────────────────────────────

        /// <summary>The account's token and Instagram user id, or <see cref="AccountUnavailableException"/>.</summary>
        public (string Token, string IgUserId) GetCredentials(string accountId) => store.Read(s =>
        {
            var a = s.Account(accountId) ?? throw new AccountUnavailableException(accountId, "That account no longer exists.");
            if (a.Connection == ConnectionState.NeedsReauth)
                throw new AccountUnavailableException(accountId, $"Reconnect @{a.Username}: {a.ConnectionDetail ?? "Instagram no longer accepts its token."}");
            string? token = s.Vault.Unprotect(a.TokenCipher, TokenPurpose(accountId));
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(a.IgUserId))
                throw new AccountUnavailableException(accountId, $"@{a.Username} is not connected to Instagram yet.");
            if (a.TokenExpiresUtc is DateTime expires && expires <= clock())
                throw new AccountUnavailableException(accountId, $"@{a.Username}'s token expired on {expires:d MMM}. Reconnect it.");
            return (token, a.IgUserId!);
        });

        public void MarkNeedsReauth(string accountId, string detail)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a == null) return;
                bool transition = a.Connection != ConnectionState.NeedsReauth;
                a.Connection = ConnectionState.NeedsReauth;
                a.ConnectionDetail = detail;
                if (transition)
                {
                    a.ConnectionChangedUtc = now;
                    s.AddEvent(EventLevel.Error, "account.reauth", $"@{a.Username} needs to be reconnected: {detail}", accountId, utc: now);
                }
                s.MarkAccount(accountId);
            });
        }

        public void MarkRestricted(string accountId, string detail)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a == null) return;
                bool transition = a.Connection != ConnectionState.Restricted;
                a.Connection = ConnectionState.Restricted;
                a.ConnectionDetail = detail;
                if (transition)
                {
                    a.ConnectionChangedUtc = now;
                    s.AddEvent(EventLevel.Error, "account.restricted", $"Instagram restricted @{a.Username}: {detail} Open the Instagram app on that account to resolve it.", accountId, utc: now);
                }
                s.MarkAccount(accountId);
            });
        }

        public void MarkHealthy(string accountId)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a == null || !a.IsConnected || (a.Connection == ConnectionState.Healthy && a.ConnectionDetail == null)) return;
                if (a.Connection != ConnectionState.Healthy)
                {
                    a.ConnectionChangedUtc = now;
                    if (a.Connection == ConnectionState.Restricted)
                        s.AddEvent(EventLevel.Success, "account.healthy", $"@{a.Username} is working again.", accountId, utc: now);
                }
                a.Connection = ConnectionState.Healthy;
                a.ConnectionDetail = null;
                s.MarkAccount(accountId);
            });
        }

        // ─────────────────────────────── Business Login ───────────────────────────────

        public async Task<(string FlowId, string AuthorizationUrl)> BeginAsync(string? reconnectAccountId, string? preset, bool autopilot)
        {
            var app = AppCredentials() ?? throw new InvalidOperationException("Set the Instagram app ID and secret first (Settings → Instagram app), or connect with a token instead.");
            DateTime now = clock();
            var flow = new PendingAuthFlow { CreatedUtc = now, ReconnectAccountId = reconnectAccountId, Preset = preset, Autopilot = autopilot };
            // force_reauth: the browser is often signed in to a different account than the one being added.
            string url = api.BuildAuthorizeUrl(app.AppId, RedirectUrl, flow.FlowId, forceReauth: true);
            store.Mutate(s =>
            {
                ExpireFlowsLocked(s, now);
                s.Flows[flow.FlowId] = flow;
                s.MarkFlows();
            });
            await store.FlushAsync();
            return (flow.FlowId, url);
        }

        public PendingAuthFlow? GetFlow(string flowId) =>
            store.Read(s => s.Flows.TryGetValue(flowId, out var f) ? OmniGramStore.Clone(f) : null);

        public void ExpireFlows() => store.Mutate(s => ExpireFlowsLocked(s, clock()));

        private static void ExpireFlowsLocked(OmniGramState s, DateTime now)
        {
            foreach (var flow in s.Flows.Values.ToList())
            {
                if (flow.State == "pending" && now - flow.CreatedUtc > FlowLifetime)
                {
                    flow.State = "failed";
                    flow.Error = "The authorization was not completed within 30 minutes.";
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
        /// Completes an authorization from Instagram's redirect (code + state): exchanges the code for a
        /// short-lived token, that for a 60-day one, reads the profile and creates or updates the account.
        /// </summary>
        public async Task<AuthCallbackResult> HandleCallbackAsync(NameValueCollection query, CancellationToken ct)
        {
            string? state = query["state"];
            string? code = query["code"];
            string? error = query["error"];

            PendingAuthFlow? flow = string.IsNullOrEmpty(state) ? null : store.Read(s => s.Flows.TryGetValue(state, out var f) ? OmniGramStore.Clone(f) : null);
            if (flow == null)
                return Fail(null, "Authorization not recognised", "This authorization link has expired or was already used. Start again from the OmniGram page.");
            if (flow.State != "pending")
                return flow.State == "completed"
                    ? new AuthCallbackResult { Ok = true, FlowId = flow.FlowId, AccountId = flow.AccountId, Title = "Already connected", Message = "This account was already connected. You can close this tab." }
                    : Fail(flow.FlowId, "Authorization failed", flow.Error ?? "This authorization can no longer be completed.");
            if (error != null)
                return CompleteFlow(flow.FlowId, error == "access_denied" ? "You declined the authorization on Instagram." : "Instagram reported an error: " + (query["error_description"] ?? error));
            if (string.IsNullOrEmpty(code)) return CompleteFlow(flow.FlowId, "Instagram's redirect had no authorization code.");
            if (AppCredentials() is not { } app)
                return CompleteFlow(flow.FlowId, "The Instagram app settings were removed before the authorization finished.");

            IgShortLivedToken shortLived;
            IgLongLivedToken longLived;
            try
            {
                shortLived = await api.ExchangeCodeAsync(app.AppId, app.Secret, RedirectUrl, code, ct);
                longLived = await api.ExchangeForLongLivedAsync(app.Secret, shortLived.AccessToken, ct);
            }
            catch (InstagramApiException ex)
            {
                string hint = ex.Message.Contains("secret", StringComparison.OrdinalIgnoreCase) ? " Check the Instagram app secret."
                    : ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase) ? $" Check that {RedirectUrl} is listed exactly under Business login settings → OAuth redirect URIs."
                    : ex.Message.Contains("client_id", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("app", StringComparison.OrdinalIgnoreCase) ? " Check the Instagram app ID."
                    : "";
                store.Mutate(s => { s.App.LastVerifyError = ex.Message; s.MarkApp(); });
                return CompleteFlow(flow.FlowId, "Exchanging the authorization for a token failed: " + ex.Message + hint);
            }
            catch (Exception ex)
            {
                return CompleteFlow(flow.FlowId, "Exchanging the authorization for a token failed: " + ex.Message);
            }

            ConnectResult result;
            try
            {
                result = await ConnectAsync(longLived.AccessToken, longLived.ExpiresInSeconds > 0 ? longLived.ExpiresInSeconds : null, shortLived.Permissions,
                    "oauth", flow.ReconnectAccountId, flow.Preset, flow.Autopilot, ct);
            }
            catch (Exception ex)
            {
                return CompleteFlow(flow.FlowId, "Authorized, but reading the Instagram account failed: " + ex.Message);
            }
            if (!result.Ok) return CompleteFlow(flow.FlowId, result.Message);

            DateTime now = clock();
            store.Mutate(s =>
            {
                s.App.VerifiedUtc = now;
                s.App.LastVerifyError = null;
                s.MarkApp();
                if (s.Flows.TryGetValue(flow.FlowId, out var live))
                {
                    live.State = "completed";
                    live.AccountId = result.AccountId;
                    live.CompletedUtc = now;
                    s.MarkFlows();
                }
            });
            await store.FlushAsync();
            return new AuthCallbackResult
            {
                Ok = true,
                FlowId = flow.FlowId,
                AccountId = result.AccountId,
                Username = result.Username,
                Title = "Instagram account connected",
                Message = $"@{result.Username} is connected. Return to OmniGram to review its strategy — you can close this tab.",
            };
        }

        private AuthCallbackResult CompleteFlow(string flowId, string error)
        {
            store.Mutate(s =>
            {
                if (s.Flows.TryGetValue(flowId, out var flow))
                {
                    flow.State = "failed";
                    flow.Error = error;
                    flow.CompletedUtc = clock();
                    s.MarkFlows();
                }
                s.AddEvent(EventLevel.Error, "account.connect-failed", "Instagram authorization failed: " + error);
            });
            return Fail(flowId, "Authorization failed", error);
        }

        private static AuthCallbackResult Fail(string? flowId, string title, string message) =>
            new() { Ok = false, FlowId = flowId, Title = title, Message = message };

        // ─────────────────────────────── Connecting ───────────────────────────────

        /// <summary>
        /// Connects (or reconnects) an account from a long-lived token: validates it against /me and creates
        /// or updates the account. A token for an account that was imported from v1 (matched by username)
        /// attaches to it, keeping its strategy and history.
        /// </summary>
        public async Task<ConnectResult> ConnectAsync(string token, long? expiresInSeconds, IReadOnlyCollection<string>? scopes, string via,
            string? reconnectAccountId, string? preset, bool autopilot, CancellationToken ct)
        {
            token = token.Trim();
            if (token.Length < 20) return new ConnectResult { Message = "That does not look like an Instagram access token." };
            IgProfile profile;
            try
            {
                profile = await api.GetProfileAsync(token, ct);
            }
            catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.TokenInvalid or IgErrorKind.PermissionDenied or IgErrorKind.BadRequest)
            {
                return new ConnectResult { Message = $"Instagram rejected the token ({ex.Code}): {ex.Message}" };
            }
            string? igUserId = profile.PublishingId;
            if (string.IsNullOrEmpty(igUserId) || string.IsNullOrEmpty(profile.Username))
                return new ConnectResult { Message = "Instagram accepted the token but did not say which account it belongs to." };
            if (profile.AccountType is string type && type.Length > 0 && !type.Equals("BUSINESS", StringComparison.OrdinalIgnoreCase) && !type.Equals("MEDIA_CREATOR", StringComparison.OrdinalIgnoreCase))
                return new ConnectResult { Message = $"@{profile.Username} is a {type.ToLowerInvariant()} account. Switch it to a Professional (Creator or Business) account in the Instagram app first." };

            DateTime now = clock();
            var result = store.Mutate(s =>
            {
                OmniGramAccount? account = reconnectAccountId != null ? s.Account(reconnectAccountId) : null;
                if (account != null && account.IgUserId != null && account.IgUserId != igUserId)
                    account = null; // reconnected as a different Instagram account: keep the old one, make a new one
                var existing = s.Accounts.Values.FirstOrDefault(a => a.IgUserId == igUserId);
                if (account != null && account.IgUserId == null && existing != null && existing != account)
                    return new ConnectResult { Message = $"@{profile.Username} is already connected in OmniGram as a separate account (@{existing.Username}). Remove one of them first." };
                // Connecting a never-connected (imported) account by its id: it keeps its v1 settings even if renamed since.
                bool attached = account != null && account.IgUserId == null && account.Legacy != null;
                account ??= existing;
                if (account == null)
                {
                    var byName = s.AccountByUsername(profile.Username);
                    if (byName != null && byName.IgUserId == null)
                    {
                        account = byName;
                        attached = byName.Legacy != null;
                    }
                }
                bool isNew = account == null;
                if (account == null)
                {
                    account = new OmniGramAccount
                    {
                        AddedUtc = now,
                        Strategy = OmniGramStrategyRules.Preset(preset),
                        Autopilot = autopilot && preset != "manual",
                    };
                    s.Accounts[account.AccountId] = account;
                    s.PostsOf(account.AccountId);
                    s.InsightsOf(account.AccountId);
                }

                account.IgUserId = igUserId;
                account.TokenCipher = s.Vault.Protect(token, TokenPurpose(account.AccountId));
                account.TokenIssuedUtc = now;
                account.TokenRefreshedUtc = null;
                // A pasted dashboard token says nothing about its expiry; assume a fresh 60 days until the first refresh tells.
                account.TokenExpiresUtc = now + (expiresInSeconds is > 0 ? TimeSpan.FromSeconds(expiresInSeconds.Value) : TokenLifetime);
                if (scopes != null && scopes.Count > 0) account.Scopes = scopes.ToList();
                account.ConnectedVia = via;
                account.ConnectedUtc = now;
                if (account.Connection != ConnectionState.Healthy) account.ConnectionChangedUtc = now;
                account.Connection = ConnectionState.Healthy;
                account.ConnectionDetail = null;
                ApplyProfile(s, account, profile, now);
                s.MarkAccount(account.AccountId);
                s.AddEvent(EventLevel.Success, isNew ? "account.connected" : "account.reconnected",
                    attached ? $"@{account.Username} connected and attached to its OmniGram v1 settings and history. Review the strategy, then switch autopilot on."
                    : $"@{account.Username} {(isNew ? "connected" : "reconnected")} ({(via == "oauth" ? "Instagram login" : "access token")}).",
                    account.AccountId, utc: now);
                return new ConnectResult { Ok = true, AccountId = account.AccountId, Username = account.Username, IsNew = isNew, AttachedToImported = attached, Message = "Connected." };
            });
            await store.FlushAsync();
            return result;
        }

        /// <summary>Copies a fresh /me reading onto the account (renames included) and records a snapshot.</summary>
        internal static void ApplyProfile(OmniGramState s, OmniGramAccount account, IgProfile p, DateTime now)
        {
            if (!string.IsNullOrEmpty(p.Username) && !string.Equals(account.Username, p.Username, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(account.Username) && !string.Equals(account.Username, p.Username, StringComparison.OrdinalIgnoreCase))
                    s.AddEvent(EventLevel.Info, "account.renamed", $"@{account.Username} is now @{p.Username} on Instagram.", account.AccountId, utc: now);
                account.Username = p.Username;
            }
            account.Name = p.Name ?? account.Name;
            account.AccountType = p.AccountType ?? account.AccountType;
            account.ProfilePictureUrl = p.ProfilePictureUrl ?? account.ProfilePictureUrl;
            account.Biography = p.Biography ?? account.Biography;
            account.Website = p.Website ?? account.Website;
            account.Stats.Followers = p.FollowersCount ?? account.Stats.Followers;
            account.Stats.Follows = p.FollowsCount ?? account.Stats.Follows;
            account.Stats.MediaCount = p.MediaCount ?? account.Stats.MediaCount;
            account.Stats.ProfileSyncedUtc = now;
            OmniGramInsights.RecordSnapshot(s.InsightsOf(account.AccountId), now, p.FollowersCount, p.FollowsCount, p.MediaCount);
            s.MarkInsights(account.AccountId);
            s.MarkAccount(account.AccountId);
        }

        /// <summary>Re-reads the profile (followers, picture, renames). Marks the account for reconnecting when the token is refused.</summary>
        public async Task<IgProfile> RefreshProfileAsync(string accountId, CancellationToken ct)
        {
            var (token, _) = GetCredentials(accountId);
            IgProfile profile;
            try
            {
                profile = await api.GetProfileAsync(token, ct);
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.TokenInvalid)
            {
                MarkNeedsReauth(accountId, ex.Message);
                throw;
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.AccountRestricted)
            {
                MarkRestricted(accountId, ex.Message);
                throw;
            }
            DateTime now = clock();
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a != null) ApplyProfile(s, a, profile, now);
            });
            MarkHealthy(accountId);
            return profile;
        }

        // ─────────────────────────────── Token refresh ───────────────────────────────

        /// <summary>Whether the account's token should be refreshed now.</summary>
        public static bool RefreshDue(OmniGramAccount a, DateTime now)
        {
            if (!a.IsConnected || a.Connection == ConnectionState.NeedsReauth) return false;
            DateTime issued = a.TokenRefreshedUtc ?? a.TokenIssuedUtc ?? now;
            if (now - issued < TimeSpan.FromHours(25)) return false; // Instagram refuses to refresh a token younger than a day
            if (a.TokenExpiresUtc is not DateTime expires) return true;
            return expires > now && expires - now < RefreshWhenRemaining;
        }

        /// <summary>Refreshes the long-lived token when due (or <paramref name="force"/>). Returns true when it was refreshed.</summary>
        public async Task<bool> RefreshTokenIfDueAsync(string accountId, bool force, CancellationToken ct)
        {
            var gate = refreshLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                DateTime now = clock();
                var due = store.Read(s => s.Account(accountId) is { } a && (force ? a.IsConnected : RefreshDue(a, now)));
                if (!due) return false;
                var (token, _) = GetCredentials(accountId);
                IgLongLivedToken fresh;
                try
                {
                    fresh = await api.RefreshLongLivedAsync(token, ct);
                }
                catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.TokenInvalid or IgErrorKind.PermissionDenied)
                {
                    MarkNeedsReauth(accountId, "Instagram refused to refresh the token: " + ex.Message);
                    throw;
                }
                now = clock();
                store.Mutate(s =>
                {
                    var a = s.Account(accountId);
                    if (a == null) return;
                    a.TokenCipher = s.Vault.Protect(fresh.AccessToken, TokenPurpose(accountId));
                    a.TokenRefreshedUtc = now;
                    a.TokenExpiresUtc = now + (fresh.ExpiresInSeconds > 0 ? TimeSpan.FromSeconds(fresh.ExpiresInSeconds) : TokenLifetime);
                    s.MarkAccount(accountId);
                    s.AddEvent(EventLevel.Info, "token.refreshed", $"@{a.Username}'s Instagram token was renewed; valid until {a.TokenExpiresUtc:d MMM yyyy}.", accountId, utc: now);
                });
                // The old token may stop working once the new one exists: persist before anything uses it.
                await store.FlushAsync();
                return true;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
