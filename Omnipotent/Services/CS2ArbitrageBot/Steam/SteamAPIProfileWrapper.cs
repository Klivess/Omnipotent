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
                    // One prompt (it used to be "are you ready?" then "confirmed"): approve in the app, tap Done.
                    string response = (string)await wrapper.parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                        "SendButtonsPromptToKlivesDiscord",
                        "CS2 Arbitrage — approve the Steam login",
                        "The bot is logging in to Steam. Approve the sign-in request in your Steam app, then tap **Done**.",
                        new Dictionary<string, DSharpPlus.ButtonStyle>
                        {
                            { "Done", DSharpPlus.ButtonStyle.Success },
                            { "Not now", DSharpPlus.ButtonStyle.Secondary }
                        },
                        TimeSpan.FromHours(24)
                    );

                    return response == "Done";
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

            int throttled = 0;
            while (moreItems)
            {
                string url = $"https://steamcommunity.com/inventory/{steamID}/{SteamAPIWrapper.CS2APPID}/2?l=english&count=75";
                if (lastAssetId != null)
                {
                    url += $"&start_assetid={lastAssetId}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Cookie", cookieString);
                using HttpResponseMessage response = await CommunityHttp.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (++throttled > 5)
                    {
                        parent.parent.ServiceLogError("Steam inventory stayed rate limited after 5 attempts; giving up for now.");
                        return null;
                    }
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

            // sellitem's "price" is what the seller receives. Steam's fee rounding means floor(price/1.15)
            // can land a penny above the buy order and leave the item listed instead of sold.
            int sellerReceivesInPence = Omnipotent.Services.CS2ArbitrageBot.Engine.ArbitrageMath.SteamSellerReceives(salePriceInPence);
            if (sellerReceivesInPence <= 0)
            {
                parent.parent.ServiceLogError($"Refusing to list {purchasedListing.ItemMarketHashName}: sale price {salePriceInPence}p leaves nothing after Steam fees.");
                return false;
            }
            string url = "https://steamcommunity.com/market/sellitem/";

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
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

        /// <summary>
        /// One Discord prompt per listing (it used to be "are you ready?" followed by "confirmed?"):
        /// confirm it in the Steam app, then tap Done.
        /// </summary>
        private async Task<bool> WaitForSteamMobileConfirmation(string itemName, int salePriceInPence)
        {
            try
            {
                string confirmResponse = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                    "SendButtonsPromptToKlivesDiscord",
                    "CS2 Arbitrage — confirm the Steam listing",
                    $"**{itemName}** is listed on the Steam Market at **£{salePriceInPence / 100.0:F2}** and needs your confirmation.\n" +
                    "Steam app → Confirmations → Confirm, then tap **Done**.",
                    new Dictionary<string, DSharpPlus.ButtonStyle>
                    {
                        { "Done", DSharpPlus.ButtonStyle.Success },
                        { "Skip", DSharpPlus.ButtonStyle.Secondary }
                    },
                    TimeSpan.FromHours(24)
                );

                if (confirmResponse == "Done")
                {
                    parent.parent.ServiceLog($"Mobile confirmation acknowledged for {itemName}.");
                    return true;
                }
                parent.parent.ServiceLogError($"Mobile confirmation was not completed for {itemName}. Response: {confirmResponse}");
                return false;
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

        /// <summary>Shared pooled client for logged-in community requests (cookies are set per request).</summary>
        private static readonly HttpClient CommunityHttp = new(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false,
            AllowAutoRedirect = false,
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Wallet balance. Primary source is the Web API the redesigned Steam site itself uses
        /// (IUserAccountService/GetClientWalletDetails, authorised by the SteamKit2 access token); the old
        /// market-page HTML scrape is the fallback. Returns null (never throws) when both fail.
        /// </summary>
        public async Task<SteamBalance?> GetSteamBalance()
        {
            try
            {
                var viaApi = await GetSteamBalanceViaWebApi();
                if (viaApi != null) return viaApi;
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError($"Steam wallet Web API lookup failed: {ex.Message}");
            }

            if (!await CheckIfCommunityCookieStringWorks())
            {
                parent.parent.ServiceLogError("Not logged in, so can't get steam balance.");
                return null;
            }
            try
            {
                string cookieString = await ProduceCommunityCookieString();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/market/");
                request.Headers.TryAddWithoutValidation("Cookie", cookieString);
                using var response = await CommunityHttp.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    parent.parent.ServiceLogError($"Failed to retrieve Steam balance page ({(int)response.StatusCode}).");
                    return null;
                }
                double? pounds = ParseWalletBalanceFromMarketHtml(content);
                if (pounds == null)
                {
                    parent.parent.ServiceLogError("Steam balance not found in the market page HTML (layout changed?).");
                    return null;
                }
                return new SteamBalance { UsableBalanceInPounds = pounds.Value, PendingBalanceInPounds = 0, TotalBalanceInPounds = pounds.Value };
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError($"Failed to read Steam balance: {ex.Message}");
                return null;
            }
        }

        private async Task<SteamBalance?> GetSteamBalanceViaWebApi()
        {
            if (!await EnsureSteamAuthAsync() || string.IsNullOrWhiteSpace(steamAccessToken)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.steampowered.com/IUserAccountService/GetClientWalletDetails/v1/?access_token=" + Uri.EscapeDataString(steamAccessToken));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["include_balance_in_usd"] = "false",
                ["wallet_region"] = "1",
                ["include_formatted_balance"] = "true",
            });
            using var response = await CommunityHttp.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return null;
            return ParseWalletDetails(body);
        }

        /// <summary>Parses GetClientWalletDetails: balance / delayed_balance are minor units (pence for a GBP wallet).</summary>
        internal static SteamBalance? ParseWalletDetails(string body)
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(body);
            var r = root["response"] as Newtonsoft.Json.Linq.JObject ?? root;
            if (r["balance"] == null) return null;
            if (r["has_wallet"] != null && r.Value<bool?>("has_wallet") == false) return null;
            long balance = long.TryParse(r["balance"]!.ToString(), out var b) ? b : 0;
            long delayed = r["delayed_balance"] != null && long.TryParse(r["delayed_balance"]!.ToString(), out var d) ? d : 0;
            return new SteamBalance
            {
                UsableBalanceInPounds = balance / 100.0,
                PendingBalanceInPounds = delayed / 100.0,
                TotalBalanceInPounds = (balance + delayed) / 100.0,
            };
        }

        internal static double? ParseWalletBalanceFromMarketHtml(string content)
        {
            var match = System.Text.RegularExpressions.Regex.Match(content, @"id=""marketWalletBalanceAmount""[^>]*>\s*[^\d<]*([\d.,]+)");
            if (!match.Success) return null;
            string number = match.Groups[1].Value.Replace(",", "");
            return double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pounds) ? pounds : null;
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

                if (string.IsNullOrWhiteSpace(credentials.Value.Username) || string.IsNullOrWhiteSpace(credentials.Value.Password))
                {
                    parent.parent.ServiceLogError("Steam username/password settings are empty. Cannot log in to Steam.");
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
                await parent.parent.GetDataHandler().WriteToFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamRefreshToken), steamRefreshToken);

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
            // Merge: GuardData (it spares a re-approval of this machine on the next login) must survive token refreshes.
            string statePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamAuthState);
            SteamAuthState state;
            try
            {
                string existing = await parent.parent.GetDataHandler().ReadDataFromFile(statePath);
                state = string.IsNullOrWhiteSpace(existing) ? new SteamAuthState() : JsonConvert.DeserializeObject<SteamAuthState>(existing) ?? new SteamAuthState();
            }
            catch
            {
                state = new SteamAuthState();
            }
            state.RefreshToken = steamRefreshToken;
            state.AccessToken = steamAccessToken;
            state.UpdatedAtUtc = DateTime.UtcNow;

            string stateJson = JsonConvert.SerializeObject(state, Formatting.Indented);
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
        private static async Task<bool> ProfileRedirectProvesLogin(string cookieString)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/my/");
                request.Headers.TryAddWithoutValidation("Cookie", cookieString);
                using var response = await CommunityHttp.SendAsync(request);
                string location = response.Headers.Location?.ToString() ?? "";
                return (int)response.StatusCode is >= 300 and < 400
                       && (location.Contains("/profiles/", StringComparison.OrdinalIgnoreCase) || location.Contains("/id/", StringComparison.OrdinalIgnoreCase))
                       && !location.Contains("login", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> CheckIfCommunityCookieStringWorks(bool reLogin = true)
        {
            string url = "https://steamcommunity.com/market/mylistings?start=0&count=1";
            const int maxAttempts = 3;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                string cookieString = await ProduceCommunityCookieString();
                if (string.IsNullOrEmpty(cookieString))
                {
                    return false;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Cookie", cookieString);
                    using HttpResponseMessage response = await CommunityHttp.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                    // The May 2026 market redesign may retire this probe; a logged-in profile redirect
                    // (/my/ → /profiles/{id} or /id/{name}, never /login) proves the cookies still work.
                    if (await ProfileRedirectProvesLogin(cookieString))
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
