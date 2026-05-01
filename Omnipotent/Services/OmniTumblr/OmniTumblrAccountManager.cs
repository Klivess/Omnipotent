using DontPanic.TumblrSharp;
using DontPanic.TumblrSharp.Client;
using DontPanic.TumblrSharp.OAuth;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblrAccountManager
    {
        private readonly OmniTumblr service;
        private readonly ConcurrentDictionary<string, OmniTumblrAccount> accounts = new();
        private readonly ConcurrentDictionary<string, TumblrClient> apiInstances = new();
        private readonly ConcurrentDictionary<string, DateTime> lastActionTimestamps = new();
        private readonly ConcurrentDictionary<string, PendingOAuthFlow> pendingFlows = new();
        // Maps request-token key → flowId so we can look up the flow from Tumblr's callback redirect
        private readonly ConcurrentDictionary<string, string> requestTokenIndex = new();
        // Completed callback flows awaiting the frontend to acknowledge
        private readonly ConcurrentDictionary<string, OmniTumblrAccount> completedCallbackFlows = new();

        private class PendingOAuthFlow
        {
            public string? FlowId { get; set; }
            public string? BlogName { get; set; }
            public string? ConsumerKey { get; set; }
            public string? ConsumerSecret { get; set; }
            public Token? RequestToken { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        public OmniTumblrAccountManager(OmniTumblr service)
        {
            this.service = service;
        }

        public IReadOnlyCollection<OmniTumblrAccount> GetAllAccounts() => accounts.Values.ToList().AsReadOnly();

        public OmniTumblrAccount? GetAccountById(string accountId)
        {
            accounts.TryGetValue(accountId, out var account);
            return account;
        }

        public OmniTumblrAccount? GetAccountByBlogName(string blogName)
        {
            return accounts.Values.FirstOrDefault(a => a.BlogName.Equals(blogName, StringComparison.OrdinalIgnoreCase));
        }

        public TumblrClient? GetApiInstance(string accountId)
        {
            apiInstances.TryGetValue(accountId, out var client);
            return client;
        }

        public async Task InitializeAsync()
        {
            EnsureDirectories();
            await LoadAccountsFromDisk();
            await ValidateAllConnections();
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrPostsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrMediaDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrAnalyticsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrUploadsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrEventsDirectory);
        }

        // ── Account CRUD ──

        // ── OAuth 1.0a Onboarding Flow ──

        /// <summary>
        /// Step 1: Exchange consumer key/secret for a temporary request token and return the
        /// Tumblr authorization URL the user must visit to authorize the app.
        /// Returns a flowId that must be supplied to check GetFlowStatus after Tumblr redirects back.
        /// </summary>
        public async Task<(string FlowId, string AuthorizationUrl)> BeginOAuthFlowAsync(string consumerKey, string consumerSecret, string blogName)
        {
            var (requestToken, requestTokenSecret) = await OmniTumblrOAuthHelper.GetRequestTokenAsync(consumerKey, consumerSecret);

            var flowId = Guid.NewGuid().ToString("N");
            var flow = new PendingOAuthFlow
            {
                FlowId         = flowId,
                BlogName       = blogName,
                ConsumerKey    = consumerKey,
                ConsumerSecret = consumerSecret,
                RequestToken   = new DontPanic.TumblrSharp.OAuth.Token(requestToken, requestTokenSecret)
            };
            pendingFlows[flowId] = flow;
            requestTokenIndex[requestToken] = flowId;

            // Expire flows older than 30 minutes
            var staleKeys = pendingFlows
                .Where(kv => (DateTime.UtcNow - kv.Value.CreatedAt).TotalMinutes > 30)
                .Select(kv => kv.Key).ToList();
            foreach (var k in staleKeys)
            {
                if (pendingFlows.TryRemove(k, out var stale) && stale.RequestToken != null)
                    requestTokenIndex.TryRemove(stale.RequestToken.Key, out _);
            }

            var authUrl = OmniTumblrOAuthHelper.BuildAuthorizationUrl(requestToken);
            await service.ServiceLog($"[OmniTumblr] OAuth flow {flowId} started for blog '{blogName}'.");
            return (flowId, authUrl);
        }

        /// <summary>
        /// Called by the GET /omnitumblr/oauth/callback route when Tumblr redirects back.
        /// Looks up the pending flow by request token, exchanges for access tokens, and creates the account.
        /// </summary>
        public async Task<OmniTumblrAccount> CompleteOAuthCallbackAsync(string requestToken, string verifier)
        {
            if (!requestTokenIndex.TryRemove(requestToken, out var flowId))
                throw new InvalidOperationException("No pending OAuth flow found for the returned request token. It may have expired.");

            if (!pendingFlows.TryRemove(flowId, out var flow))
                throw new InvalidOperationException("OAuth flow expired before the callback was received.");

            var (accessToken, accessTokenSecret) = await OmniTumblrOAuthHelper.GetAccessTokenAsync(
                flow.ConsumerKey!, flow.ConsumerSecret!,
                flow.RequestToken!.Key, flow.RequestToken.Secret,
                verifier);

            await service.ServiceLog($"[OmniTumblr] OAuth callback received for flow {flowId}, blog '{flow.BlogName}'.");

            var account = await AddAccountAsync(flow.BlogName!, flow.ConsumerKey!, flow.ConsumerSecret!, accessToken, accessTokenSecret);
            completedCallbackFlows[flowId] = account;
            return account;
        }

        /// <summary>
        /// Returns null if the flow is still pending, or the created account if the callback has been received.
        /// Removes the entry after the first successful read.
        /// </summary>
        public OmniTumblrAccount? GetFlowStatus(string flowId)
        {
            if (completedCallbackFlows.TryRemove(flowId, out var account))
                return account;
            return null;
        }

        /// <summary>
        /// Manual fallback: complete using a flowId + verifier (e.g., if the callback URL redirect is unavailable).
        /// </summary>
        public async Task<OmniTumblrAccount> CompleteOAuthFlowAsync(string flowId, string verifier, string blogName)
        {
            if (!pendingFlows.TryRemove(flowId, out var flow))
                throw new InvalidOperationException("OAuth flow not found or expired. Please start a new authorization.");

            if (flow.RequestToken != null)
                requestTokenIndex.TryRemove(flow.RequestToken.Key, out _);

            var (accessToken, accessTokenSecret) = await OmniTumblrOAuthHelper.GetAccessTokenAsync(
                flow.ConsumerKey!, flow.ConsumerSecret!,
                flow.RequestToken!.Key, flow.RequestToken.Secret,
                verifier);

            await service.ServiceLog($"[OmniTumblr] OAuth flow {flowId} manually completed for blog '{blogName}'.");
            return await AddAccountAsync(blogName, flow.ConsumerKey!, flow.ConsumerSecret!, accessToken, accessTokenSecret);
        }

        // ────────────────────────────────────────────────────────────────

        public async Task<OmniTumblrAccount> AddAccountAsync(string blogName, string consumerKey, string consumerSecret, string? oauthToken = null, string? oauthTokenSecret = null)
        {
            if (GetAccountByBlogName(blogName) != null)
                throw new InvalidOperationException($"Account '{blogName}' already exists.");

            var account = new OmniTumblrAccount
            {
                BlogName = blogName,
                ConsumerKey = consumerKey,
                ConsumerSecret = consumerSecret,
                OAuthToken = oauthToken,
                OAuthTokenSecret = oauthTokenSecret
            };

            var client = BuildTumblrClient(account);
            accounts[account.AccountId] = account;
            apiInstances[account.AccountId] = client;

            await SaveAccountToDisk(account);
            await SaveCredentialsToSettings(account);

            await service.ServiceLog($"[OmniTumblr] Added account '{blogName}' (ID: {account.AccountId}).");

            await ValidateConnection(client, account);
            await SaveAccountToDisk(account);

            return account;
        }

        public async Task<bool> RemoveAccountAsync(string accountId)
        {
            if (!accounts.TryRemove(accountId, out var account))
                return false;

            if (apiInstances.TryRemove(accountId, out var client))
                client?.Dispose();

            lastActionTimestamps.TryRemove(accountId, out _);

            var accountFile = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory, $"{account.BlogName}.json");
            if (File.Exists(accountFile)) File.Delete(accountFile);

            await service.ServiceLog($"[OmniTumblr] Removed account '{account.BlogName}' (ID: {accountId}).");
            return true;
        }

        public async Task PauseAccountAsync(string accountId)
        {
            if (accounts.TryGetValue(accountId, out var account))
            {
                account.IsPaused = true;
                await SaveAccountToDisk(account);
                await service.ServiceLog($"[OmniTumblr] Paused account '{account.BlogName}'.");
            }
        }

        public async Task ResumeAccountAsync(string accountId)
        {
            if (accounts.TryGetValue(accountId, out var account))
            {
                account.IsPaused = false;
                await SaveAccountToDisk(account);
                await service.ServiceLog($"[OmniTumblr] Resumed account '{account.BlogName}'.");
            }
        }

        // ── Client Building ──

        private TumblrClient BuildTumblrClient(OmniTumblrAccount account)
        {
            Token? token = null;
            if (!string.IsNullOrEmpty(account.OAuthToken) && !string.IsNullOrEmpty(account.OAuthTokenSecret))
                token = new Token(account.OAuthToken, account.OAuthTokenSecret);
            return new TumblrClientFactory().Create<TumblrClient>(account.ConsumerKey, account.ConsumerSecret, token!);
        }

        // ── Connection Validation ──

        public async Task ValidateConnection(TumblrClient client, OmniTumblrAccount account)
        {
            try
            {
                var blogInfo = await client.GetBlogInfoAsync(account.BlogName);
                if (blogInfo != null)
                {
                    account.ConnectionStatus = OmniTumblrConnectionStatus.Connected;
                    account.ConnectionErrorMessage = null!;
                    account.PostCount = blogInfo.PostsCount;
                    account.Title = blogInfo.Title;
                    account.Description = blogInfo.Description;
                    account.Url = blogInfo.Url;
                    try
                    {
                        var followersInfo = await client.GetFollowersAsync(account.BlogName, 0, 1);
                        if (followersInfo != null) account.FollowerCount = followersInfo.Count;
                    }
                    catch { /* followers unavailable for some blogs */ }
                    await service.ServiceLog($"[OmniTumblr] Connection validated for '{account.BlogName}'. Followers: {account.FollowerCount}, Posts: {account.PostCount}.");
                }
                else
                {
                    account.ConnectionStatus = OmniTumblrConnectionStatus.Error;
                    account.ConnectionErrorMessage = "Blog info returned null.";
                }
            }
            catch (Exception ex)
            {
                account.ConnectionStatus = OmniTumblrConnectionStatus.Error;
                account.ConnectionErrorMessage = ex.Message;
                await service.ServiceLogError(ex, $"[OmniTumblr] Failed to validate connection for '{account.BlogName}'");
            }
        }

        public async Task RunConnectionHealthCheck()
        {
            await service.ServiceLog("[OmniTumblr] Running connection health check...");
            foreach (var account in accounts.Values.Where(a => a.IsActive))
            {
                try
                {
                    var client = GetApiInstance(account.AccountId);
                    if (client == null)
                    {
                        var freshClient = BuildTumblrClient(account);
                        apiInstances[account.AccountId] = freshClient;
                        client = freshClient;
                    }
                    await ValidateConnection(client, account);
                    await SaveAccountToDisk(account);
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] Health check failed for '{account.BlogName}'");
                }
            }
        }

        // ── Action Rate Limiting ──

        public bool CanPerformAction(string accountId)
        {
            var delay = service.GetActionDelaySeconds();
            if (!lastActionTimestamps.TryGetValue(accountId, out var last)) return true;
            return (DateTime.UtcNow - last).TotalSeconds >= delay;
        }

        public void RecordAction(string accountId)
        {
            lastActionTimestamps[accountId] = DateTime.UtcNow;
        }

        // ── Persistence ──

        private async Task LoadAccountsFromDisk()
        {
            var accountFiles = Directory.GetFiles(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory, "*.json");
            foreach (var file in accountFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var account = JsonConvert.DeserializeObject<OmniTumblrAccount>(json);
                    if (account != null && !string.IsNullOrEmpty(account.AccountId))
                    {
                        // Credentials are stored in OmniSettings (sensitive), not in JSON
                        account.ConsumerKey = await service.GetStringOmniSetting($"OmniTumblr_ConsumerKey_{account.BlogName}", sensitive: true);
                        account.ConsumerSecret = await service.GetStringOmniSetting($"OmniTumblr_ConsumerSecret_{account.BlogName}", sensitive: true);
                        account.OAuthToken = await service.GetStringOmniSetting($"OmniTumblr_OAuthToken_{account.BlogName}", sensitive: true);
                        account.OAuthTokenSecret = await service.GetStringOmniSetting($"OmniTumblr_OAuthTokenSecret_{account.BlogName}", sensitive: true);

                        accounts[account.AccountId] = account;
                    }
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] Failed to load account from {file}");
                }
            }
            await service.ServiceLog($"[OmniTumblr] Loaded {accounts.Count} accounts from disk.");
        }

        private async Task ValidateAllConnections()
        {
            foreach (var account in accounts.Values)
            {
                try
                {
                    if (string.IsNullOrEmpty(account.ConsumerKey))
                    {
                        account.ConnectionStatus = OmniTumblrConnectionStatus.Disconnected;
                        account.ConnectionErrorMessage = "Missing Consumer Key in settings.";
                        await SaveAccountToDisk(account);
                        continue;
                    }

                    var client = BuildTumblrClient(account);
                    apiInstances[account.AccountId] = client;
                    await ValidateConnection(client, account);
                    await SaveAccountToDisk(account);
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniTumblr] Failed to restore connection for '{account.BlogName}'");
                    account.ConnectionStatus = OmniTumblrConnectionStatus.Error;
                    account.ConnectionErrorMessage = ex.Message;
                }
            }
        }

        public async Task SaveAccountToDisk(OmniTumblrAccount account)
        {
            try
            {
                var filePath = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrAccountsDirectory, $"{account.BlogName}.json");
                var json = JsonConvert.SerializeObject(account, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniTumblr] Failed to save account '{account.BlogName}'");
            }
        }

        private async Task SaveCredentialsToSettings(OmniTumblrAccount account)
        {
            var settingsManager = await service.GetOmniGlobalSettingsManager();
            await settingsManager.SetStringOmniSetting($"OmniTumblr_ConsumerKey_{account.BlogName}", account.ConsumerKey);
            await settingsManager.SetStringOmniSetting($"OmniTumblr_ConsumerSecret_{account.BlogName}", account.ConsumerSecret);
            if (account.OAuthToken != null)
                await settingsManager.SetStringOmniSetting($"OmniTumblr_OAuthToken_{account.BlogName}", account.OAuthToken);
            if (account.OAuthTokenSecret != null)
                await settingsManager.SetStringOmniSetting($"OmniTumblr_OAuthTokenSecret_{account.BlogName}", account.OAuthTokenSecret);
        }
    }
}
