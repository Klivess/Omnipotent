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

        public async Task InitialiseLogin()
        {
            await LoadSteamPassword();
            if (await CheckIfCookieStringWorks())
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

            // Initialize Selenium WebDriver  
            var options = new ChromeOptions();
            options.AddArgument("--headless"); // Run in headless mode  
            using (IWebDriver driver = new ChromeDriver(options))
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

                    // Locate and click the login button  
                    IWebElement loginButton = driver.FindElement(By.CssSelector("button.DjSvCZoKKfoNSmarsEcTS[type='submit']"));
                    loginButton.Click();
                    await Task.Delay(2000);

                    bool confirmed = false;
                    try
                    {
                        string response = await (await parent.parent.serviceManager.GetNotificationsService())
                            .SendButtonsPromptToKlivesDiscord(
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
                        await SaveSteamCookiesAsync(driver);
                        driver.Close();
                        driver.Quit();
                    }
                    else
                    {
                        throw new Exception("User denied the login confirmation request from Klives.");
                    }
                }
                catch (Exception ex)
                {
                    // Handle exceptions (e.g., log errors)  
                    Console.WriteLine($"Error during Steam login: {ex.Message}");
                }
                finally
                {
                    // Quit the WebDriver  
                    driver.Quit();
                }
            }
        }
        public async Task LoadSteamPassword()
        {
            string password = await parent.parent.GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.CS2ArbitrageBotSteamLoginPassword));
            if (string.IsNullOrEmpty(password))
            {
                //Ask klives for a password using the Notification system
                string result = await (await parent.parent.serviceManager.GetNotificationsService()).SendTextPromptToKlivesDiscord("CS2 Arbitrage Bot Steam Login Password",
                    "Please enter your Steam login password for the CS2 Arbitrage Bot.", TimeSpan.FromDays(3), "Steam", "password");
                if (string.IsNullOrEmpty(result))
                {
                    //If the user did not enter a password, we will try to load it again
                    await (await parent.parent.serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Steam login password for the CS2 Arbitrage Bot was null or empty, try again.");
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
        public async Task SaveSteamCookiesAsync(IWebDriver driver)
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
        public async Task LoadSteamCookiesAsync(IWebDriver driver, string gotourl)
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
        public async Task<string> ProduceCookieString()
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
        public async Task<bool> CheckIfCookieStringWorks()
        {
            string url = "https://steamcommunity.com/market/mylistings?start=0&count=1";
            HttpClient client = new();
            string cookieString = await ProduceCookieString();
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
                    return await CheckIfCookieStringWorks(); // Retry after delay
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking cookie string: {ex.Message}");
                return false;
            }
        }
    }
}
