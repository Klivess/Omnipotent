using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Omnipotent.Data_Handling;
using Newtonsoft.Json;
using System.Management.Automation;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.DevTools;
using Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs;
using Microsoft.AspNetCore.Components;

namespace Omnipotent.Services.CS2ArbitrageBot.Steam
{
    public class SteamAPIProfileWrapper
    {
        public List<string> LoginCookies;
        private SteamAPIWrapper parent;
        public string SteamPassword;
        public SteamAPIProfileWrapper(SteamAPIWrapper parent)
        {
            LoginCookies = new List<string>();
            this.parent = parent;
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
            string steamID = parent.SteamIDOfSteamClient;
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
            client.DefaultRequestHeaders.Add("Referer", $"https://steamcommunity.com/profiles/{parent.SteamIDOfSteamClient}/inventory");

            var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "sessionid", sessionId },
                { "appid", SteamAPIWrapper.CS2APPID },
                { "contextid", "2" },
                { "assetid", assetId },
                { "amount", "1" },
                { "price", sellerReceivesInPence.ToString() }
            });

            int retryCount = 0;
            const int maxRetries = 5;
            const int baseDelay = 2000; // 2 seconds

            while (retryCount < maxRetries)
            {
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
                string confirmResponse = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                    "SendButtonsPromptToKlivesDiscord",
                    "CS2 Arbitrage Bot — Steam Mobile Confirmation Required",
                    $"A Steam Market listing for **{itemName}** at **£{salePriceInPence / 100.0:F2}** needs to be confirmed on your Steam mobile app.\n\n" +
                    $"Please open the Steam app → Confirmations → Confirm the market listing, then press **Confirmed** below.",
                    new Dictionary<string, DSharpPlus.ButtonStyle>
                    {
                        { "Confirmed", DSharpPlus.ButtonStyle.Success },
                        { "Failed", DSharpPlus.ButtonStyle.Danger }
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
            await LoadSteamPassword();
            if (await CheckIfCommunityCookieStringWorks())
            {
                parent.parent.ServiceLog("Login to Steam via saved cookie is successful.");
            }
            else
            {
                await LoginToSteam();
            }
        }
        public async Task LoginToSteam()
        {
            string loginUrl = "https://steamcommunity.com/login/home/";


            string canStartLogin = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendButtonsPromptToKlivesDiscord",
        "CS2 Arbitrage Bot",
        $"The bot requires you to accept a steam mobile login confirmation, Are you ready to receive it?.",
        new Dictionary<string, DSharpPlus.ButtonStyle>
        {
                           { "Yes", DSharpPlus.ButtonStyle.Success },
        },
        TimeSpan.FromDays(31)
    );

            // Initialize Selenium WebDriver  
            var seleniumObject = (await parent.parent.GetSeleniumManager()).CreateSeleniumObject("SteamLogin");
            using (var driver = seleniumObject.UseChromeDriver())
            {
                try
                {
                    // Navigate to the login page  
                    await driver.Navigate().GoToUrlAsync(loginUrl);
                    await Task.Delay(6500);
                    // Wait for the page to load and locate the username field  
                    WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                    IWebElement usernameField = wait.Until(d => d.FindElement(By.CssSelector("input[type='text']._2GBWeup5cttgbTw8FM3tfx")));

                    // Enter the username  
                    usernameField.SendKeys("gaberhakim76");

                    // Locate the password field and enter the password  
                    IWebElement passwordField = driver.FindElement(By.CssSelector("input[type='password']._2GBWeup5cttgbTw8FM3tfx"));
                    passwordField.SendKeys(SteamPassword);

                    // Dismiss cookie consent popup if present
                    try
                    {
                        var cookiePopup = driver.FindElements(By.CssSelector("div.cookiepreferences_popup_content"));
                        if (cookiePopup.Count > 0)
                        {
                            var acceptButton = cookiePopup[0].FindElement(By.CssSelector("div.btn_green_white_innerfade, button"));
                            acceptButton.Click();
                            await Task.Delay(1000);
                        }
                    }
                    catch { }

                    // Locate and click the login button  
                    IWebElement loginButton = driver.FindElement(By.CssSelector("button.DjSvCZoKKfoNSmarsEcTS[type='submit']"));
                    try
                    {
                        loginButton.Click();
                    }
                    catch (ElementClickInterceptedException)
                    {
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", loginButton);
                    }
                    await Task.Delay(2000);

                    bool confirmed = false;
                    try
                    {
                        string response = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>(
                            "SendButtonsPromptToKlivesDiscord",
                                "CS2 Arbitrage Bot",
                                $"The bot requires you to accept a steam mobile login confirmation, which was sent to you at {DateTime.Now.ToString()}.",
                                new Dictionary<string, DSharpPlus.ButtonStyle>
                                {
                           { "Confirmed", DSharpPlus.ButtonStyle.Success },
                                    { "Retry", DSharpPlus.ButtonStyle.Secondary},
                           { "Deny", DSharpPlus.ButtonStyle.Danger }
                                },
                                TimeSpan.FromDays(3)
                            );

                        if (response == "Confirmed")
                        {
                            confirmed = true;
                        }
                        else if (response == "Retry")
                        {
                            driver.Close(); // Close the current driver instance
                            driver.Quit(); // Ensure the driver is properly disposed
                            await LoginToSteam(); // Retry login
                            return; // Exit the current method to avoid further processing
                        }
                        else if (response == "Deny")
                        {
                            throw new Exception("User denied the login confirmation request from Klives.");
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("Failed to send confirmation request to Klives.", ex);
                    }

                    if (confirmed)
                    {
                        await Task.Delay(5000);

                        // Save cookies after successful login  
                        await SaveSteamCommunityCookiesAsync(driver);
                    }
                    else
                    {
                        throw new Exception("User denied the login confirmation request from Klives.");
                    }
                }
                catch (Exception ex)
                {
                    // Handle exceptions (e.g., log errors)  
                    parent.parent.ServiceLogError(ex, $"Error during Steam login: {ex.Message}");
                }
                // Quit the WebDriver  
                (await parent.parent.GetSeleniumManager()).StopUsingSeleniumObject(seleniumObject);
            }
        }
        public async Task LoadSteamPassword()
        {
            string password = await parent.parent.GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginPassword));
            if (string.IsNullOrEmpty(password))
            {
                //Ask klives for a password using the Notification system
                string result = (string)await parent.parent.ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord", "CS2 Arbitrage Bot Steam Login Password",
                    "Please enter your Steam login password for the CS2 Arbitrage Bot.", TimeSpan.FromDays(3), "Steam", "password");
                if (string.IsNullOrEmpty(result))
                {
                    //If the user did not enter a password, we will try to load it again
                    await parent.parent.ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", "Steam login password for the CS2 Arbitrage Bot was null or empty, try again.");
                    await LoadSteamPassword();
                }
                else
                {
                    SteamPassword = result;
                    await parent.parent.GetDataHandler().WriteToFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginPassword), result);
                }
            }
            else
            {
                SteamPassword = password;
            }
        }
        public async Task SaveSteamCommunityCookiesAsync(IWebDriver driver)
        {
            var cookies = driver.Manage().Cookies.AllCookies;
            string filePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginCookies);
            var cookieList = new List<object>();

            foreach (var cookie in cookies)
            {
                cookieList.Add(new
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    Expiry = cookie.Expiry,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.IsHttpOnly
                });
            }

            var json = JsonConvert.SerializeObject(cookieList);
            await parent.parent.GetDataHandler().WriteToFile(filePath, json);
        }
        public async Task LoadSteamCommunityCookiesAsync(IWebDriver driver, string gotourl)
        {
            driver.Navigate().GoToUrl(gotourl); // must visit domain first  
            string filePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginCookies);

            if (File.Exists(filePath))
            {
                var json = await parent.parent.GetDataHandler().ReadDataFromFile(filePath);
                var cookieList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(json);

                foreach (var cookieData in cookieList)
                {
                    var cookie = new Cookie(
                        cookieData["Name"].ToString(),
                        cookieData["Value"].ToString(),
                        cookieData["Domain"].ToString(),
                        cookieData["Path"].ToString(),
                        cookieData.ContainsKey("Expiry") ? (DateTime?)DateTime.Parse(cookieData["Expiry"].ToString()) : null
                    );
                    driver.Manage().Cookies.AddCookie(cookie);
                }
            }

            driver.Navigate().Refresh();
        }
        public async Task<string> ProduceCommunityCookieString()
        {
            string filePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginCookies);
            string data = await parent.parent.GetDataHandler().ReadDataFromFile(filePath);
            if (string.IsNullOrEmpty(data))
            {
                return "";
            }
            string cookieString = "";
            var cookieList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(data);
            foreach (var cookie in cookieList)
            {
                cookieString += $"{cookie["Name"].ToString()}={cookie["Value"].ToString()}; ";
            }
            return cookieString.Trim();
        }
        public async Task<bool> CheckIfCommunityCookieStringWorks(bool reLogin = true)
        {
            string url = "https://steamcommunity.com/market/mylistings?start=0&count=1";
            HttpClient client = new();
            string cookieString = await ProduceCommunityCookieString();
            if (string.IsNullOrEmpty(cookieString))
            {
                return false; // No cookies available
            }
            client.DefaultRequestHeaders.Add("Cookie", cookieString);
            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    parent.parent.ServiceLogError("CheckIfCookieWorks got ratelimited, retrying in 30 seconds...");
                    await Task.Delay(30000);
                    return await CheckIfCommunityCookieStringWorks(); // Retry after delay
                }
                else
                {
                    if (reLogin)
                    {
                        await LoginToSteam();
                        return await CheckIfCommunityCookieStringWorks();
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                parent.parent.ServiceLogError(ex, $"Error checking cookie string: {ex.Message}");
                if (reLogin)
                {
                    await LoginToSteam();
                    return await CheckIfCommunityCookieStringWorks();
                }
                else
                {
                    return false;
                }
            }
        }
    }
}
