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

        public OmniTumblrAccountManager(OmniTumblr service)
        {
            this.service = service;
        }

        public IReadOnlyCollection<OmniTumblrAccount> GetAllAccounts() => accounts.Values.ToList().AsReadOnly();

        public OmniTumblrAccount GetAccountById(string accountId)
        {
            accounts.TryGetValue(accountId, out var account);
            return account;
        }

        public OmniTumblrAccount GetAccountByBlogName(string blogName)
        {
            return accounts.Values.FirstOrDefault(a => a.BlogName.Equals(blogName, StringComparison.OrdinalIgnoreCase));
        }

        public TumblrClient GetApiInstance(string accountId)
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

        public async Task<OmniTumblrAccount> AddAccountAsync(string blogName, string consumerKey, string consumerSecret, string oauthToken, string oauthTokenSecret)
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
            var token = new Token(account.OAuthToken, account.OAuthTokenSecret);
            return new TumblrClientFactory().Create<TumblrClient>(account.ConsumerKey, account.ConsumerSecret, token);
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
                    account.ConnectionErrorMessage = null;
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
                    if (string.IsNullOrEmpty(account.ConsumerKey) || string.IsNullOrEmpty(account.OAuthToken))
                    {
                        account.ConnectionStatus = OmniTumblrConnectionStatus.Disconnected;
                        account.ConnectionErrorMessage = "Missing OAuth credentials in settings.";
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
            await settingsManager.SetStringOmniSetting($"OmniTumblr_OAuthToken_{account.BlogName}", account.OAuthToken);
            await settingsManager.SetStringOmniSetting($"OmniTumblr_OAuthTokenSecret_{account.BlogName}", account.OAuthTokenSecret);
        }
    }
}
