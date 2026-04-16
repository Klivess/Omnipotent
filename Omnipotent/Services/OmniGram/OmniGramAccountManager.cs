using InstagramApiSharp;
using InstagramApiSharp.API;
using InstagramApiSharp.API.Builder;
using InstagramApiSharp.Classes;
using InstagramApiSharp.Classes.Android.DeviceInfo;
using InstagramApiSharp.Logger;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;
using System.Net;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramAccountManager
    {
        private readonly OmniGram service;
        public readonly OmniGramLoginHandler loginHandler;
        private readonly ConcurrentDictionary<string, OmniGramAccount> accounts = new();
        private readonly ConcurrentDictionary<string, IInstaApi> apiInstances = new();
        private readonly ConcurrentDictionary<string, DateTime> lastActionTimestamps = new();
        private string[] proxyPool = Array.Empty<string>();
        private int proxyIndex = 0;

        public OmniGramAccountManager(OmniGram service)
        {
            this.service = service;
            this.loginHandler = new OmniGramLoginHandler(service);
        }

        public IReadOnlyCollection<OmniGramAccount> GetAllAccounts() => accounts.Values.ToList().AsReadOnly();

        public OmniGramAccount GetAccountById(string accountId)
        {
            accounts.TryGetValue(accountId, out var account);
            return account;
        }

        public OmniGramAccount GetAccountByUsername(string username)
        {
            return accounts.Values.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }

        public IInstaApi GetApiInstance(string accountId)
        {
            apiInstances.TryGetValue(accountId, out var api);
            return api;
        }

        public async Task InitializeAsync()
        {
            EnsureDirectories();
            await LoadProxyPool();
            await LoadAccountsFromDisk();
            await RestoreAllSessions();
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramAccountsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramPostsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramMediaDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramAnalyticsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramSessionsDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramUploadsDirectory);
        }

        private async Task LoadProxyPool()
        {
            var proxyList = await service.GetStringOmniSetting("OmniGram_ProxyList", defaultValue: "", sensitive: true);
            if (!string.IsNullOrWhiteSpace(proxyList))
            {
                proxyPool = proxyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                await service.ServiceLog($"[OmniGram] Loaded {proxyPool.Length} proxies.");
            }
        }

        private string GetNextProxy()
        {
            if (proxyPool.Length == 0) return null;
            var proxy = proxyPool[proxyIndex % proxyPool.Length];
            Interlocked.Increment(ref proxyIndex);
            return proxy;
        }

        // ── Account CRUD ──

        public async Task<OmniGramAccount> AddAccountAsync(string username, string password)
        {
            if (GetAccountByUsername(username) != null)
                throw new InvalidOperationException($"Account '{username}' already exists.");

            var account = new OmniGramAccount
            {
                Username = username,
                Password = password,
                ProxyAddress = GetNextProxy()
            };

            var instaApi = BuildInstaApi(account);
            accounts[account.AccountId] = account;
            apiInstances[account.AccountId] = instaApi;

            await SaveAccountToDisk(account);
            await service.ServiceLog($"[OmniGram] Added account {username} (ID: {account.AccountId}).");

            // Attempt login
            var status = await loginHandler.HandleLoginAsync(instaApi, account);
            await SaveAccountToDisk(account);

            return account;
        }

        public async Task<bool> RemoveAccountAsync(string accountId)
        {
            if (!accounts.TryRemove(accountId, out var account))
                return false;

            apiInstances.TryRemove(accountId, out _);
            lastActionTimestamps.TryRemove(accountId, out _);

            // Remove files
            var accountFile = Path.Combine(OmniPaths.GlobalPaths.OmniGramAccountsDirectory, $"{account.Username}.json");
            if (File.Exists(accountFile)) File.Delete(accountFile);

            var sessionFile = Path.Combine(OmniPaths.GlobalPaths.OmniGramSessionsDirectory, $"{account.Username}.session");
            if (File.Exists(sessionFile)) File.Delete(sessionFile);

            await service.ServiceLog($"[OmniGram] Removed account {account.Username} (ID: {accountId}).");
            return true;
        }

        public async Task PauseAccountAsync(string accountId)
        {
            if (accounts.TryGetValue(accountId, out var account))
            {
                account.IsPaused = true;
                await SaveAccountToDisk(account);
                await service.ServiceLog($"[OmniGram] Paused account {account.Username}.");
            }
        }

        public async Task ResumeAccountAsync(string accountId)
        {
            if (accounts.TryGetValue(accountId, out var account))
            {
                account.IsPaused = false;
                await SaveAccountToDisk(account);
                await service.ServiceLog($"[OmniGram] Resumed account {account.Username}.");
            }
        }

        // ── Login & Session Management ──

        private IInstaApi BuildInstaApi(OmniGramAccount account)
        {
            var userSession = new UserSessionData
            {
                UserName = account.Username,
                Password = account.Password
            };

            var builder = InstaApiBuilder.CreateBuilder()
                .SetUser(userSession)
                .UseLogger(new DebugLogger(InstagramApiSharp.Logger.LogLevel.None))
                .SetRequestDelay(RequestDelay.FromSeconds(1, 3));

            // Reuse persisted device fingerprint to avoid checkpoint triggers from device changes
            if (!string.IsNullOrEmpty(account.DeviceData))
            {
                try
                {
                    var device = JsonConvert.DeserializeObject<AndroidDevice>(account.DeviceData);
                    if (device != null)
                        builder.SetDevice(device);
                }
                catch { /* fall through to default device */ }
            }

            if (!string.IsNullOrWhiteSpace(account.ProxyAddress))
            {
                builder.UseHttpClientHandler(new System.Net.Http.HttpClientHandler
                {
                    Proxy = new WebProxy(account.ProxyAddress),
                    UseProxy = true
                });
            }

            return builder.Build();
        }

        private async Task LoadAccountsFromDisk()
        {
            var accountFiles = Directory.GetFiles(OmniPaths.GlobalPaths.OmniGramAccountsDirectory, "*.json");
            foreach (var file in accountFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var account = JsonConvert.DeserializeObject<OmniGramAccount>(json);
                    if (account != null && !string.IsNullOrEmpty(account.AccountId))
                    {
                        // Password is loaded from OmniSettings (sensitive), not from the JSON file
                        account.Password = await service.GetStringOmniSetting(
                            $"OmniGram_AccountPassword_{account.Username}", sensitive: true);

                        accounts[account.AccountId] = account;
                    }
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Failed to load account from {file}");
                }
            }
            await service.ServiceLog($"[OmniGram] Loaded {accounts.Count} accounts from disk.");
        }

        private async Task RestoreAllSessions()
        {
            foreach (var account in accounts.Values)
            {
                try
                {
                    var instaApi = BuildInstaApi(account);
                    var sessionPath = Path.Combine(OmniPaths.GlobalPaths.OmniGramSessionsDirectory, $"{account.Username}.session");

                    if (File.Exists(sessionPath))
                    {
                        using var stream = File.OpenRead(sessionPath);
                        await instaApi.LoadStateDataFromStreamAsync(stream);

                        // Capture device fingerprint from restored session if not yet persisted
                        if (string.IsNullOrEmpty(account.DeviceData))
                        {
                            try
                            {
                                var stateJson = instaApi.GetStateDataAsString();
                                var stateObj = JObject.Parse(stateJson);
                                var deviceToken = stateObj["DeviceInfo"];
                                if (deviceToken != null)
                                {
                                    account.DeviceData = deviceToken.ToString(Formatting.None);
                                    await SaveAccountToDisk(account);
                                }
                            }
                            catch { /* non-critical */ }
                        }

                        await service.ServiceLog($"[OmniGram] Restored session for {account.Username}.");
                    }

                    apiInstances[account.AccountId] = instaApi;

                    // Validate session
                    if (File.Exists(sessionPath))
                    {
                        var userResult = await instaApi.GetCurrentUserAsync();
                        if (userResult.Succeeded)
                        {
                            account.LoginStatus = OmniGramLoginStatus.LoggedIn;
                            account.LastLoginTime = DateTime.UtcNow;
                            var userInfo = await instaApi.UserProcessor.GetUserInfoByIdAsync(userResult.Value.Pk);
                            if (userInfo.Succeeded)
                            {
                                account.FollowerCount = (int)userInfo.Value.FollowerCount;
                                account.FollowingCount = (int)userInfo.Value.FollowingCount;
                                account.MediaCount = (int)userInfo.Value.MediaCount;
                            }
                            await service.ServiceLog($"[OmniGram] Session valid for {account.Username}.");
                        }
                        else
                        {
                            await service.ServiceLog($"[OmniGram] Session expired for {account.Username}, re-authenticating...");
                            await loginHandler.HandleLoginAsync(instaApi, account);
                        }
                    }
                    else
                    {
                        // No session file - need fresh login
                        if (!string.IsNullOrEmpty(account.Password))
                        {
                            await loginHandler.HandleLoginAsync(instaApi, account);
                        }
                        else
                        {
                            account.LoginStatus = OmniGramLoginStatus.LoggedOut;
                            await service.ServiceLog($"[OmniGram] No session or password for {account.Username}. Skipping login.");
                        }
                    }

                    await SaveAccountToDisk(account);
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Failed to restore session for {account.Username}");
                    account.LoginStatus = OmniGramLoginStatus.Error;
                    account.LoginErrorMessage = ex.Message;
                }
            }
        }

        // ── Session Health Check ──

        public async Task RunSessionHealthCheck()
        {
            await service.ServiceLog("[OmniGram] Running session health check...");
            foreach (var account in accounts.Values.Where(a => a.LoginStatus == OmniGramLoginStatus.LoggedIn && a.IsActive))
            {
                if (!apiInstances.TryGetValue(account.AccountId, out var instaApi))
                    continue;

                try
                {
                    var userResult = await instaApi.GetCurrentUserAsync();
                    if (!userResult.Succeeded)
                    {
                        var responseType = userResult.Info?.ResponseType ?? ResponseType.Unknown;
                        var errorMsg = (userResult.Info?.Message ?? "").ToLowerInvariant();
                        if (responseType == ResponseType.LoginRequired || responseType == ResponseType.ChallengeRequired
                            || errorMsg.Contains("checkpoint_required") || errorMsg.Contains("challenge_required"))
                        {
                            await service.ServiceLog($"[OmniGram] Session invalid/checkpoint for {account.Username}, re-authenticating...");
                            account.LoginStatus = OmniGramLoginStatus.CheckpointRequired;
                            await loginHandler.HandleLoginAsync(instaApi, account);
                            await SaveAccountToDisk(account);
                        }
                        else
                        {
                            await service.ServiceLog($"[OmniGram] Health check failed for {account.Username}: {userResult.Info?.Message}. Will retry next cycle.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Health check error for {account.Username}");
                }
            }

            // Retry accounts in retryable states
            foreach (var account in accounts.Values.Where(a =>
                a.LoginStatus == OmniGramLoginStatus.RateLimited ||
                a.LoginStatus == OmniGramLoginStatus.ChallengeTimedOut))
            {
                if (apiInstances.TryGetValue(account.AccountId, out var instaApi))
                {
                    await service.ServiceLog($"[OmniGram] Retrying login for {account.Username} (status: {account.LoginStatus})...");
                    await loginHandler.HandleLoginAsync(instaApi, account);
                    await SaveAccountToDisk(account);
                }
            }

            await service.ServiceLog("[OmniGram] Session health check complete.");
        }

        // ── Rate Limiting ──

        public bool CanPerformAction(string accountId)
        {
            if (!lastActionTimestamps.TryGetValue(accountId, out var lastAction))
                return true;

            var actionDelay = service.GetActionDelaySeconds();
            return (DateTime.UtcNow - lastAction).TotalSeconds >= actionDelay;
        }

        public void RecordAction(string accountId)
        {
            lastActionTimestamps[accountId] = DateTime.UtcNow;
        }

        // ── Persistence ──

        public async Task SaveAccountToDisk(OmniGramAccount account)
        {
            try
            {
                var accountFile = Path.Combine(OmniPaths.GlobalPaths.OmniGramAccountsDirectory, $"{account.Username}.json");
                var json = JsonConvert.SerializeObject(account, Formatting.Indented);
                await File.WriteAllTextAsync(accountFile, json);
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, $"[OmniGram] Failed to save account {account.Username}");
            }
        }

        public async Task SaveAllAccountsToDisk()
        {
            foreach (var account in accounts.Values)
            {
                await SaveAccountToDisk(account);
            }
        }
    }
}
