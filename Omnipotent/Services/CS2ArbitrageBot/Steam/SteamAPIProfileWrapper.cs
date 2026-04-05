using Omnipotent.Data_Handling;
using Newtonsoft.Json;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using SteamKit2;
using SteamKit2.Authentication;
using System.Text;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    public class SteamAPIProfileWrapper
    {
        public List<string> LoginCookies;
        private SteamAPIWrapper parent;
        private readonly SemaphoreSlim authLock;
        private string? steamRefreshToken;
        private string? steamAccessToken;
        private string? steamSessionId;
        private ulong? authenticatedSteamId64;

        private class SteamAuthState
        {
            public string? RefreshToken { get; set; }
            public string? AccessToken { get; set; }
            public string? GuardData { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
        }

        private sealed class SteamMobileOnlyAuthenticator : IAuthenticator
        {
            private readonly SteamAPIProfileWrapper wrapper;

            public SteamMobileOnlyAuthenticator(SteamAPIProfileWrapper wrapper)
            {
                this.wrapper = wrapper;
            }

            public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            {
                wrapper.parent.parent.ServiceLogError("Steam requested a device code. This bot is configured for mobile confirmation flow only.");
                return Task.FromResult(string.Empty);
            }

            public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            {
                wrapper.parent.parent.ServiceLogError($"Steam requested an email code for {email}. This bot is configured for mobile confirmation flow only.");
                return Task.FromResult(string.Empty);
            }

            public async Task<bool> AcceptDeviceConfirmationAsync()
            {
                try
                {
                    if (!await wrapper.AskKlivesReadyForSteamMobileAction("approve the Steam login confirmation"))
                    {
                        wrapper.parent.parent.ServiceLogError("Klives is not ready to approve Steam login confirmation.");
                        return false;
                    }

                    string response = (string)await wrapper.parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                        "SendButtonsPromptToKlivesDiscord",
                        "CS2 Arbitrage Bot — Steam Login Confirmation Required",
                        "Please approve the Steam mobile login confirmation in your Steam app, then press **Confirmed**.",
                        new Dictionary<string, DSharpPlus.Entities.DiscordButtonStyle>
                        {
                            { "Confirmed", DSharpPlus.Entities.DiscordButtonStyle.Success },
                            { "Failed", DSharpPlus.Entities.DiscordButtonStyle.Danger }
                        },
                        TimeSpan.FromHours(24)
                    );

                    return response == "Confirmed";
                }
                catch (Exception ex)
                {
                    wrapper.parent.parent.ServiceLogError(ex, "Error while waiting for Steam mobile login confirmation.");
                    return false;
                }
            }
        }

        public SteamAPIProfileWrapper(SteamAPIWrapper parent)
        {
            LoginCookies = new List<string>();
            this.parent = parent;
            authLock = new SemaphoreSlim(1, 1);
        }

        public struct SteamBalance
        {
            public double UsableBalanceInPounds;
            public double PendingBalanceInPounds;
            public double TotalBalanceInPounds;
        }

        public async Task<string?> GetSteamInventoryAssetID(string marketHashName)
        {
            if (!await CheckIfCommunityCookieStringWorks())
            {
                parent.parent.ServiceLogError("Not logged in, can't fetch Steam inventory.");
                return null;
            }

            string cookieString = await ProduceCommunityCookieString();
            string steamID = GetEffectiveSteamId();
            string? lastAssetId = null;
            bool moreItems = true;
            string? foundButNotMarketableAssetId = null;

            while (moreItems)
            {
                string url = $"https://steamcommunity.com/inventory/{steamID}/{SteamAPIWrapper.CS2APPID}/2?l=english&count=75";
                if (lastAssetId != null)
                {
                    url += $"&start_assetid={lastAssetId}";
                }

                HttpClient client = new();
                client.DefaultRequestHeaders.Add("Cookie", cookieString);
                HttpResponseMessage response = await client.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    parent.parent.ServiceLog("Steam inventory rate limited, waiting 30 seconds...");
                    await Task.Delay(30000);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    parent.parent.ServiceLogError($"Failed to fetch Steam inventory. Status: {response.StatusCode}");
                    return null;
                }

                string content = await response.Content.ReadAsStringAsync();
                dynamic json = JsonConvert.DeserializeObject(content);

                int assetCount = 0;
                int descCount = 0;

                // Build a lookup from classid+instanceid → description for efficiency
                var descLookup = new Dictionary<string, dynamic>();
                if (json.descriptions != null)
                {
                    foreach (var desc in json.descriptions)
                    {
                        descCount++;
                        string key = Convert.ToString(desc.classid) + "_" + Convert.ToString(desc.instanceid);
                        descLookup.TryAdd(key, desc);
                    }
                }

                if (json.assets != null)
                {
                    foreach (var asset in json.assets)
                    {
                        assetCount++;
                        string key = Convert.ToString(asset.classid) + "_" + Convert.ToString(asset.instanceid);

                        if (descLookup.TryGetValue(key, out var desc))
                        {
                            string descName = Convert.ToString(desc.market_hash_name);
                            if (descName == marketHashName)
                            {
                                int marketable = Convert.ToInt32(desc.marketable);
                                if (marketable == 1)
                                {
                                    return Convert.ToString(asset.assetid);
                                }
                                else
                                {
                                    foundButNotMarketableAssetId = Convert.ToString(asset.assetid);
                                    parent.parent.ServiceLog($"Found {marketHashName} (assetid: {foundButNotMarketableAssetId}) but marketable={marketable}, skipping.");
                                }
                            }
                        }

                        lastAssetId = Convert.ToString(asset.assetid);
                    }
                }

                parent.parent.ServiceLog($"Inventory page scanned: {assetCount} assets, {descCount} descriptions. Searching for: {marketHashName}");
                moreItems = json.more_items != null && (bool)json.more_items;
            }

            if (foundButNotMarketableAssetId != null)
            {
                parent.parent.ServiceLogError($"Item {marketHashName} exists in inventory (assetid: {foundButNotMarketableAssetId}) but is not marketable (likely trade-held).");
            }
            else
            {
                parent.parent.ServiceLogError($"Could not find asset ID for item {marketHashName} in Steam inventory.");
            }
            return null;
        }

        public async Task<bool> SellItem(Scanalytics.PurchasedListing purchasedListing, int salePriceInPence)
        {
            if (!await CheckIfCommunityCookieStringWorks())
            {
                parent.parent.ServiceLogError("Not logged in to Steam, cannot sell item.");
                return false;
            }

            string? assetId = await GetSteamInventoryAssetID(purchasedListing.ItemMarketHashName);
            if (string.IsNullOrEmpty(assetId))
            {
                parent.parent.ServiceLogError($"Could not find {purchasedListing.ItemMarketHashName} in Steam inventory to sell.");
                return false;
            }

            string cookieString = await ProduceCommunityCookieString();
            string? sessionId = ExtractSessionIdFromCookies(cookieString);
            if (string.IsNullOrEmpty(sessionId))
            {
                parent.parent.ServiceLogError("Could not extract sessionid from Steam cookies.");
                return false;
            }

            int sellerReceivesInPence = (int)Math.Floor(salePriceInPence / 1.15);
            string url = "https://steamcommunity.com/market/sellitem/";

            HttpClient client = new();
            client.DefaultRequestHeaders.Add("Cookie", cookieString);
            client.DefaultRequestHeaders.Add("Referer", $"https://steamcommunity.com/profiles/{GetEffectiveSteamId()}/inventory");

            int retryCount = 0;
            const int maxRetries = 5;
            const int baseDelay = 2000; // 2 seconds

            while (retryCount < maxRetries)
            {
                var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "sessionid", sessionId },
                    { "appid", SteamAPIWrapper.CS2APPID },
                    { "contextid", "2" },
                    { "assetid", assetId },
                    { "amount", "1" },
                    { "price", sellerReceivesInPence.ToString() }
                });

                HttpResponseMessage response = await client.PostAsync(url, formContent);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    dynamic json = JsonConvert.DeserializeObject(responseBody);
                    if (json.success == true)
                    {
                        parent.parent.ServiceLog($"Successfully submitted sell request for {purchasedListing.ItemMarketHashName} on Steam Market for £{salePriceInPence / 100.0:F2}. Awaiting mobile confirmation.");

                        bool confirmed = await WaitForSteamMobileConfirmation(purchasedListing.ItemMarketHashName, salePriceInPence);
                        return confirmed;
                    }
                    else
                    {
                        string message = json.message ?? "Unknown error";
                        parent.parent.ServiceLogError($"Steam sellitem returned success=false: {message}");

                        if (retryCount < maxRetries - 1)
                        {
                            await Task.Delay(baseDelay * (int)Math.Pow(2, retryCount));
                            retryCount++;
                            continue;
                        }
                        return false;
                    }
                }
                else
                {
                    parent.parent.ServiceLogError($"Steam sellitem failed. Status: {response.StatusCode}, Body: {responseBody}");

                    if (retryCount < maxRetries - 1)
                    {
                        await Task.Delay(baseDelay * (int)Math.Pow(2, retryCount));
                        retryCount++;
                        continue;
                    }
                    return false;
                }
            }

            parent.parent.ServiceLogError($"Exceeded maximum retries for selling {purchasedListing.ItemMarketHashName}.");
            return false;
        }

        private async Task<bool> WaitForSteamMobileConfirmation(string itemName, int salePriceInPence)
        {
            try
            {
                if (!await AskKlivesReadyForSteamMobileAction($"confirm Steam Market listing for {itemName}"))
                {
                    parent.parent.ServiceLogError($"Klives is not ready to confirm Steam Market listing for {itemName}.");
                    return false;
                }

                string confirmResponse = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                    "SendButtonsPromptToKlivesDiscord",
                    "CS2 Arbitrage Bot — Steam Mobile Confirmation Required",
                    $"A Steam Market listing for **{itemName}** at **£{salePriceInPence / 100.0:F2}** needs to be confirmed on your Steam mobile app.\n\n" +
                    $"Please open the Steam app → Confirmations → Confirm the market listing, then press **Confirmed** below.",
                    new Dictionary<string, DSharpPlus.Entities.DiscordButtonStyle>
                    {
                        { "Confirmed", DSharpPlus.Entities.DiscordButtonStyle.Success },
                        { "Failed", DSharpPlus.Entities.DiscordButtonStyle.Danger }
                    },
                    TimeSpan.FromHours(24)
                );

                if (confirmResponse == "Confirmed")
                {
                    parent.parent.ServiceLog($"Mobile confirmation acknowledged for {itemName}.");
                    return true;
                }
                else
                {
                    parent.parent.ServiceLogError($"Mobile confirmation was not completed for {itemName}. Response: {confirmResponse}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, $"Error waiting for mobile confirmation for {itemName}.");
                return false;
            }
        }

        private static string? ExtractSessionIdFromCookies(string cookieString)
        {
            foreach (string part in cookieString.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("sessionid=", StringComparison.OrdinalIgnoreCase))
                {
                    return part["sessionid=".Length..];
                }
            }
            return null;
        }

        public async Task<SteamBalance?> GetSteamBalance()
        {
            SteamBalance bal;
            if (await CheckIfCommunityCookieStringWorks())
            {
                string cookieString = await ProduceCommunityCookieString();
                string url = "https://steamcommunity.com/market/";
                HttpClient client = new();
                client.DefaultRequestHeaders.Add("Cookie", cookieString);
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    try
                    {


                        //string pendingBalanceString = content.Substring(content.IndexOf(">Pending:"), content.IndexOf(">Pending:") + 15).Replace(">Pending: £", "").Trim();
                        string usableBalanceIdentifier = "Wallet balance <span id=\"marketWalletBalanceAmount\">£";
                        string usableBalanceString = content.Substring(content.IndexOf(usableBalanceIdentifier));
                        usableBalanceString = usableBalanceString.Substring(0, usableBalanceString.IndexOf("</span>")).Replace(usableBalanceIdentifier, "").Trim();

                        bal.UsableBalanceInPounds = (float)Convert.ToDouble(usableBalanceString); // Replace with actual parsing logic
                        bal.PendingBalanceInPounds = 0; // Replace with actual parsing logic
                        bal.TotalBalanceInPounds = bal.UsableBalanceInPounds + bal.PendingBalanceInPounds;
                        return bal;
                    }
                    catch (Exception ex)
                    {
                        parent.parent.ServiceLogError($"Failed to parse Steam balance: {ex.Message}");
                        await parent.parent.ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", "Failed to parse Steam balance HTML. Parser could have finally broken?");
                        return null;
                    }
                }
                else
                {
                    string content = await response.Content.ReadAsStringAsync();
                    parent.parent.ServiceLogError($"Failed to retrieve Steam balance, status code was not 200. Code: {response.StatusCode} Response: {content}");
                    return null;
                }
            }
            else
            {
                parent.parent.ServiceLogError("Not logged in, so can't get steam balance.");
                return null;
            }
        }

        public async Task InitialiseLogin()
        {
            if (await EnsureSteamAuthAsync() && await CheckIfCommunityCookieStringWorks(reLogin: true))
            {
                parent.parent.ServiceLog("SteamKit2 refresh-token login is ready for Steam community requests.");
            }
            else
            {
                parent.parent.ServiceLogError("Steam auth did not initialize correctly. Steam community requests may fail.");
            }
        }

        private async Task<bool> EnsureSteamAuthAsync(bool forceRefresh = false)
        {
            await authLock.WaitAsync();
            try
            {
                await LoadSteamAuthStateFromDisk();

                if (!forceRefresh && !string.IsNullOrWhiteSpace(steamAccessToken) && !string.IsNullOrWhiteSpace(steamRefreshToken))
                {
                    return true;
                }

                if (!ulong.TryParse(parent.SteamIDOfSteamClient, out ulong steamId64))
                {
                    parent.parent.ServiceLogError($"Invalid SteamID configured: {parent.SteamIDOfSteamClient}");
                    return false;
                }

                steamId64 = ResolveSteamIdForTokenGeneration(steamId64);

                if (string.IsNullOrWhiteSpace(steamRefreshToken))
                {
                    bool loginWorked = await AcquireRefreshTokenProgrammatically();
                    if (!loginWorked)
                    {
                        return false;
                    }
                }

                bool generatedFromRefreshToken = await TryGenerateAccessTokenFromRefreshToken(steamId64);
                if (!generatedFromRefreshToken)
                {
                    bool reloginWorked = await AcquireRefreshTokenProgrammatically();
                    if (!reloginWorked)
                    {
                        return false;
                    }

                    if (!await TryGenerateAccessTokenFromRefreshToken(steamId64))
                    {
                        parent.parent.ServiceLogError("SteamKit2 failed to mint an access token even after programmatic re-login.");
                        return false;
                    }
                }

                steamSessionId = Guid.NewGuid().ToString("N");
                await SaveSteamAuthStateToDisk();
                return true;
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, "Failed to authenticate with SteamKit2 refresh token.");
                return false;
            }
            finally
            {
                authLock.Release();
            }
        }

        private async Task<bool> TryGenerateAccessTokenFromRefreshToken(ulong steamId64)
        {
            SteamClient client = new();
            try
            {
                if (!await ConnectSteamClientAsync(client, "generate access token from refresh token"))
                {
                    return false;
                }

                var accessTokenResult = await client.Authentication.GenerateAccessTokenForAppAsync(new SteamID(steamId64), steamRefreshToken!, true);
                if (string.IsNullOrWhiteSpace(accessTokenResult.AccessToken))
                {
                    return false;
                }

                steamAccessToken = accessTokenResult.AccessToken;
                if (!string.IsNullOrWhiteSpace(accessTokenResult.RefreshToken))
                {
                    steamRefreshToken = accessTokenResult.RefreshToken;
                }

                authenticatedSteamId64 = TryExtractSteamIdFromJwt(steamAccessToken) ?? TryExtractSteamIdFromJwt(steamRefreshToken) ?? steamId64;
                return true;
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, "Failed generating Steam access token from refresh token.");
                steamAccessToken = null;
                return false;
            }
            finally
            {
                client.Disconnect();
            }
        }

        private async Task<bool> AcquireRefreshTokenProgrammatically()
        {
            SteamClient client = new();
            try
            {
                var credentials = await LoadSteamCredentialsFromDisk();
                if (credentials == null)
                {
                    parent.parent.ServiceLogError("Steam username/password not configured on disk. Cannot programmatically obtain refresh token.");
                    return false;
                }

                if (!await ConnectSteamClientAsync(client, "begin credentials auth session"))
                {
                    return false;
                }

                var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = credentials.Value.Username,
                    Password = credentials.Value.Password,
                    IsPersistentSession = true,
                    GuardData = credentials.Value.GuardData,
                    Authenticator = new SteamMobileOnlyAuthenticator(this),
                    DeviceFriendlyName = "Omnipotent-CS2ArbitrageBot",
                    WebsiteID = "Community"
                });

                AuthPollResult authResult = await authSession.PollingWaitForResultAsync();
                if (string.IsNullOrWhiteSpace(authResult.RefreshToken))
                {
                    parent.parent.ServiceLogError("Steam login succeeded but returned no refresh token.");
                    return false;
                }

                steamRefreshToken = authResult.RefreshToken;
                steamAccessToken = authResult.AccessToken;
                authenticatedSteamId64 = TryExtractSteamIdFromJwt(steamRefreshToken) ?? TryExtractSteamIdFromJwt(steamAccessToken);

                string statePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamAuthState);
                string stateJson = await parent.parent.GetDataHandler().ReadDataFromFile(statePath);
                SteamAuthState state = string.IsNullOrWhiteSpace(stateJson)
                    ? new SteamAuthState()
                    : (JsonConvert.DeserializeObject<SteamAuthState>(stateJson) ?? new SteamAuthState());

                if (!string.IsNullOrWhiteSpace(authResult.NewGuardData))
                {
                    state.GuardData = authResult.NewGuardData;
                }

                state.RefreshToken = steamRefreshToken;
                state.AccessToken = steamAccessToken;
                state.UpdatedAtUtc = DateTime.UtcNow;
                await parent.parent.GetDataHandler().WriteToFile(statePath, JsonConvert.SerializeObject(state, Formatting.Indented));

                parent.parent.ServiceLog("Programmatically obtained Steam refresh token via SteamKit2 credentials flow.");
                return true;
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, "Programmatic Steam refresh-token acquisition failed.");
                return false;
            }
            finally
            {
                client.Disconnect();
            }
        }

        private async Task<bool> ConnectSteamClientAsync(SteamClient client, string operation)
        {
            TaskCompletionSource<bool> connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CallbackManager callbackManager = new(client);

            callbackManager.Subscribe<SteamClient.ConnectedCallback>(callback =>
            {
                connectedTcs.TrySetResult(true);
            });

            callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            {
                connectedTcs.TrySetResult(false);
            });

            client.Connect();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(20));
            try
            {
                while (!connectedTcs.Task.IsCompleted && !timeoutCts.IsCancellationRequested)
                {
                    callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, $"Steam client callback loop failed while trying to {operation}.");
                return false;
            }

            if (!connectedTcs.Task.IsCompleted)
            {
                parent.parent.ServiceLogError($"Timed out connecting Steam client while trying to {operation}.");
                return false;
            }

            bool connected = await connectedTcs.Task;
            if (!connected)
            {
                parent.parent.ServiceLogError($"Steam client connection failed while trying to {operation}.");
            }

            return connected;
        }

        private string GetEffectiveSteamId()
        {
            return (authenticatedSteamId64 ?? 0) > 0 ? authenticatedSteamId64!.Value.ToString() : parent.SteamIDOfSteamClient;
        }

        private ulong ResolveSteamIdForTokenGeneration(ulong configuredSteamId64)
        {
            ulong? tokenSteamId64 = TryExtractSteamIdFromJwt(steamRefreshToken);
            if (!tokenSteamId64.HasValue)
            {
                return configuredSteamId64;
            }

            authenticatedSteamId64 = tokenSteamId64.Value;
            if (tokenSteamId64.Value != configuredSteamId64)
            {
                parent.parent.ServiceLogError($"Configured SteamID ({configuredSteamId64}) differs from refresh-token SteamID ({tokenSteamId64.Value}). Using refresh-token SteamID for token generation.");
                return tokenSteamId64.Value;
            }

            return configuredSteamId64;
        }

        private static ulong? TryExtractSteamIdFromJwt(string? jwt)
        {
            if (string.IsNullOrWhiteSpace(jwt))
            {
                return null;
            }

            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2)
                {
                    return null;
                }

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                string payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                dynamic parsed = JsonConvert.DeserializeObject(payloadJson);
                string? subject = Convert.ToString(parsed?.sub);
                return ulong.TryParse(subject, out ulong parsedSteamId) ? parsedSteamId : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> AskKlivesReadyForSteamMobileAction(string actionDescription)
        {
            try
            {
                string response = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                    "SendButtonsPromptToKlivesDiscord",
                    "CS2 Arbitrage Bot — Are You Ready?",
                    $"Are you ready to {actionDescription} in the Steam mobile app now?",
                    new Dictionary<string, DSharpPlus.Entities.DiscordButtonStyle>
                    {
                        { "Ready", DSharpPlus.Entities.DiscordButtonStyle.Primary },
                        { "Not Ready", DSharpPlus.Entities.DiscordButtonStyle.Secondary }
                    },
                    TimeSpan.FromHours(24)
                );

                return response == "Ready";
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, "Error while asking Klives if he is ready for Steam mobile action.");
                return false;
            }
        }

        private async Task<(string Username, string Password, string? GuardData)?> LoadSteamCredentialsFromDisk()
        {
            string username = await parent.parent.GetStringOmniSetting("CS2ArbitrageBotSteamLoginUsername", "", false, true);
            string password = await parent.parent.GetStringOmniSetting("CS2ArbitrageBotSteamLoginPassword", "", true, true);

            string? guardData = null;
            try
            {
                string statePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamAuthState);
                string stateJson = await parent.parent.GetDataHandler().ReadDataFromFile(statePath);
                if (!string.IsNullOrWhiteSpace(stateJson))
                {
                    SteamAuthState? state = JsonConvert.DeserializeObject<SteamAuthState>(stateJson);
                    guardData = state?.GuardData;
                }
            }
            catch { }

            return (username.Trim(), password.Trim(), guardData);
        }

        private async Task LoadSteamAuthStateFromDisk()
        {
            try
            {
                string statePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamAuthState);
                string stateJson = await parent.parent.GetDataHandler().ReadDataFromFile(statePath);
                if (!string.IsNullOrWhiteSpace(stateJson))
                {
                    SteamAuthState? state = JsonConvert.DeserializeObject<SteamAuthState>(stateJson);
                    if (state is not null)
                    {
                        steamRefreshToken ??= state.RefreshToken;
                        steamAccessToken ??= state.AccessToken;
                    }
                }

                authenticatedSteamId64 ??= TryExtractSteamIdFromJwt(steamAccessToken) ?? TryExtractSteamIdFromJwt(steamRefreshToken);

                if (string.IsNullOrWhiteSpace(steamRefreshToken))
                {
                    string refreshTokenPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamRefreshToken);
                    string rawToken = await parent.parent.GetDataHandler().ReadDataFromFile(refreshTokenPath);
                    if (!string.IsNullOrWhiteSpace(rawToken))
                    {
                        steamRefreshToken = rawToken.Trim();
                        authenticatedSteamId64 ??= TryExtractSteamIdFromJwt(steamRefreshToken);
                    }
                }
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, "Failed loading Steam auth state from disk.");
            }
        }

        private async Task SaveSteamAuthStateToDisk()
        {
            SteamAuthState state = new()
            {
                RefreshToken = steamRefreshToken,
                AccessToken = steamAccessToken,
                UpdatedAtUtc = DateTime.UtcNow
            };

            string stateJson = JsonConvert.SerializeObject(state, Formatting.Indented);
            string statePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamAuthState);
            await parent.parent.GetDataHandler().WriteToFile(statePath, stateJson);

            if (!string.IsNullOrWhiteSpace(steamRefreshToken))
            {
                string refreshTokenPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamRefreshToken);
                await parent.parent.GetDataHandler().WriteToFile(refreshTokenPath, steamRefreshToken);
            }
        }

        public async Task<string> ProduceCommunityCookieString()
        {
            if (!await EnsureSteamAuthAsync())
            {
                return "";
            }

            steamSessionId ??= Guid.NewGuid().ToString("N");
            string steamLoginSecure = Uri.EscapeDataString($"{GetEffectiveSteamId()}||{steamAccessToken}");
            return $"sessionid={steamSessionId}; steamLoginSecure={steamLoginSecure}; steamRememberLogin=true";
        }
        public async Task<bool> CheckIfCommunityCookieStringWorks(bool reLogin = true)
        {
            string url = "https://steamcommunity.com/market/mylistings?start=0&count=1";
            const int maxAttempts = 3;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                HttpClient client = new();
                string cookieString = await ProduceCommunityCookieString();
                if (string.IsNullOrEmpty(cookieString))
                {
                    return false;
                }

                client.DefaultRequestHeaders.Add("Cookie", cookieString);
                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        parent.parent.ServiceLogError("Steam mylistings check rate-limited, waiting 30 seconds...");
                        await Task.Delay(30000);
                        continue;
                    }

                    if (reLogin && attempt == 0)
                    {
                        await EnsureSteamAuthAsync(forceRefresh: true);
                        continue;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    parent.parent.ServiceLogError(ex, $"Error checking Steam community cookie string: {ex.Message}");
                    if (reLogin && attempt == 0)
                    {
                        await EnsureSteamAuthAsync(forceRefresh: true);
                        continue;
                    }
                    return false;
                }
            }

            return false;
        }
    }
}
