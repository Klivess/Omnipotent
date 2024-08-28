using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using ProtoBuf.WellKnownTypes;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.Formats.Asn1;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Helpers;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public class DiscordInterface
    {
        public const string discordURI = "https://discord.com/api/v9/";
        public OmniServiceManager manager;
        public List<OmniDiscordUser> LinkedDiscordAccounts;
        public ChatInterface ChatInterface;

        public event Action<OmniDiscordUser> NewOmniDiscordUserAdded;

        public DiscordInterface(OmniServiceManager manager)
        {
            this.manager = manager;
            LinkedDiscordAccounts = GetAllLinkedOmniDiscordUsersFromDisk().Result.ToList();

            ChatInterface = new ChatInterface(this);
        }
        private void UpdateLinkedDiscordAccounts(OmniDiscordUser user)
        {
            var query = LinkedDiscordAccounts.Select(k => k.UserID).Where(t => t == user.UserID);
            if (query.Any())
            {
                LinkedDiscordAccounts[LinkedDiscordAccounts.FindIndex(0, k => k.UserID == user.UserID)] = user;
            }
            else
            {
                LinkedDiscordAccounts.Add(user);
            }
        }
        public async Task<OmniDiscordUser> GetOmniDiscordUser(string discordID)
        {
            try
            {
                var accounts = LinkedDiscordAccounts.Where(k => k.UserID == discordID);
                if (accounts.Any())
                {
                    var account = accounts.ToArray()[0];
                    return accounts.ToArray()[0];
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                manager.logger.LogError("Discord Interface Utility", "Couldn't load " + discordID + " on demand!");
                return null;
            }
        }
        public async Task<OmniDiscordUser[]> GetAllLinkedOmniDiscordUsersFromDisk()
        {
            var allDirectories = Directory.GetDirectories(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory));
            List<OmniDiscordUser> users = new();
            foreach (var directory in allDirectories)
            {
                var file = Directory.GetFiles(directory).Where(k => Path.GetExtension(k) == $".omnidiscuser");
                if (file.Any())
                {
                    OmniDiscordUser user = await manager.GetDataHandler().ReadAndDeserialiseDataFromFile<OmniDiscordUser>(file.ToArray()[0]);
                    Directory.CreateDirectory(user.CreateDMDirectoryPathString());
                    users.Add(user);
                }
            }
            return users.ToArray();
        }
        private async Task<string> TryAndRetrieve2FAFromKlives(OmniDiscordUser user, OmniServiceManager manager)
        {
            try
            {
                string twofa = await manager.GetNotificationsService().SendTextPromptToKlivesDiscord("Require 2fa to login to OmniDiscordUser!",
    $"Trying to login to\n\nEmail: {user.Email}\nPassword: Length {user.Password.Length}\n\nBut I require a 2fa code. Please provide the code, or reject this request.",
    TimeSpan.FromDays(3), $"Enter 2FA here for {user.GlobalName}!", "2FA here.");
                return twofa;
            }
            catch (TimeoutException tex)
            {
                return null;
            }
        }
        public static HttpClient DiscordHttpClient()
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Cookie", "dbind=b71290c0-2006-49e9-be3c-060a89250daa; __dcfduid=770ea7b62c2c11efa2ce929b367b09b9; __sdcfduid=770ea7b62c2c11efa2ce929b367b09b9be38094e24430e96a4608a93a3f55e19972f57d23b1cfa2fb2d1a5c81837c7a2; __cfruid=e4f6bb67d999ccaaf49b2b1b3e5f0b0c5a9e73de-1722509011; _cfuvid=WhukXLj_jpo_L2vs15pFJVhoTDDg2CpKUXq8dhbjdUE-1722509011708-0.0.1.1-604800000; cf_clearance=5SdB1QCIaXmxnuOhaIjnxF1h0Vrdg.JVUOfr0moOM20-1722509013-1.0.1.1-sMQIP7OddSu6QxShLUhJSdT8ONjReH2Ogk0SxX42EaALdSjSpUhNibPc4W.xjYinxw6ZcPNazYa6h2.H");
            client.DefaultRequestHeaders.Add("Retry-After", "5");
            return client;
        }
        public static HttpClient DiscordHttpClient(OmniDiscordUser user)
        {
            var client = DiscordHttpClient();
            client.DefaultRequestHeaders.Add("Authorization", user.Token);
            return client;
        }

        public async Task<HttpResponseMessage> SendDiscordGetRequest(HttpClient client, Uri url, bool checkIfJsonParseable = true)
        {
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Get;
            message.RequestUri = url;
            return await SendDiscordRequest(client, message, checkIfJsonParseable);
        }
        public async Task<HttpResponseMessage> SendDiscordRequest(HttpClient client, HttpRequestMessage message, bool checkIfJsonParseable = true)
        {
            try
            {
                var response = await client.SendAsync(message);
                if (response.IsSuccessStatusCode)
                {
                    if (true == false)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        try
                        {
                            if (OmniPaths.IsValidJson(content) == false)
                            {
                                throw new InvalidOperationException("Response is not valid JSON!");
                            }
                        }
                        catch (AggregateException ex)
                        {
                            HttpRequestMessage newMessage = new();
                            newMessage.Content = message.Content;
                            foreach (var header in message.Headers)
                            {
                                newMessage.Headers.Add(header.Key, header.Value);
                            }
                            newMessage.RequestUri = message.RequestUri;
                            newMessage.Method = message.Method;
                            return await SendDiscordRequest(client, newMessage);
                        }
                    }
                    return response;
                }
                else
                {
                    //If ratelimited
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        try
                        {
                            string responseData = await response.Content.ReadAsStringAsync();
                            float retryAfter = 4;
                            if (OmniPaths.IsValidJson(responseData))
                            {
                                dynamic responseJson = JsonConvert.DeserializeObject(responseData);
                                retryAfter = responseJson.retry_after;
                            }
                            TimeSpan time = TimeSpan.FromSeconds(retryAfter) + TimeSpan.FromMilliseconds(10);
                            manager.logger.LogStatus("Omniscience: Discord Interface", $"Client has been ratelimited, retrying in {time.TotalSeconds}");
                            await Task.Delay(time);
                            HttpRequestMessage newMessage = new();
                            newMessage.Content = message.Content;
                            foreach (var header in message.Headers)
                            {
                                newMessage.Headers.Add(header.Key, header.Value);
                            }
                            newMessage.RequestUri = message.RequestUri;
                            newMessage.Method = message.Method;
                            return await SendDiscordRequest(client, newMessage, checkIfJsonParseable);
                        }
                        catch (Exception ex)
                        {
                            ErrorInformation errorInformation = new(ex);
                            throw new Exception($"Discord request failed! Reason: {errorInformation.FullFormattedMessage}");
                        }
                    }
                    else
                    {
                        throw new Exception($"Discord request failed! Reason: {response.ReasonPhrase}");
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                manager.logger.LogStatus("Omniscience: Discord Interface", "Discord request failed! Reason: " + ex.Message);
                HttpRequestMessage newMessage = new();
                newMessage.Content = message.Content;
                foreach (var header in message.Headers)
                {
                    newMessage.Headers.Add(header.Key, header.Value);
                }
                newMessage.RequestUri = message.RequestUri;
                newMessage.Method = message.Method;
                return await SendDiscordRequest(client, newMessage);
            }
        }
        public async Task SaveOmniDiscordUser(OmniDiscordUser user)
        {
            try
            {
                string pathOfUserDirectory = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory), user.FormatDirectoryName());
                string pathOfUserDataFile = Path.Combine(pathOfUserDirectory, user.FormatFileName());
                if (!Directory.Exists(pathOfUserDirectory))
                {
                    await manager.GetDataHandler().CreateDirectory(pathOfUserDirectory);
                }
                string serialisedData = JsonConvert.SerializeObject(user);
                await manager.GetDataHandler().WriteToFile(pathOfUserDataFile, serialisedData);
            }
            catch (Exception ex)
            {
                manager.logger.LogError("Discord Interface Utility", ex, "Error saving omnidiscorduser!");
            }
        }
        public async Task<bool> VerifyTokenWorks(OmniDiscordUser user)
        {
            user.client = DiscordHttpClient();
            HttpRequestMessage message = new();
            message.Method = HttpMethod.Get;
            message.RequestUri = new Uri($"https://discord.com/api/v9/users/@me/affinities/users");
            message.Headers.Add("Authorization", user.Token);
            var response = await SendDiscordRequest(user.client, message);
            return response.IsSuccessStatusCode;
        }

        public static string ConstructIconURL(string guildID, string iconID)
        {
            return $"https://cdn.discordapp.com/icons/{guildID}/{iconID}.png?size=1024";

        }
        public static string ConstructAvatarURL(string userID, string avatarID)
        {
            return $"https://cdn.discordapp.com/avatars/{userID}/{avatarID}.png?size=1024";
        }
        //Kill me now
        public long GenerateDiscordSnowflake(object timestamp, int? shardId = null)
        {
            long EPOCH = new DateTime(2015, 1, 1).Ticks / TimeSpan.TicksPerMillisecond;
            int SEQUENCE = 1;

            long timestampLong = ((DateTime)timestamp).Ticks / TimeSpan.TicksPerMillisecond;

            long result = ((long)timestampLong - EPOCH) << 22;
            if (shardId == null)
            {
                shardId = 1;
            }
            result |= (shardId.Value % 1024) << 12;
            result |= SEQUENCE++ % 4096;

            return result;
        }

        public async Task<OmniDiscordUser> GetAccountInfo(OmniDiscordUser user, bool refreshGuilds = true)
        {
            //https://discord.com/api/v9/users/976648966944989204/profile
            //https://discord.com/api/v9/users/@me/affinities/users

            if (await VerifyTokenWorks(user))
            {
                user.client = DiscordHttpClient();
                HttpRequestMessage message = new();
                message.Method = HttpMethod.Get;
                message.RequestUri = new Uri($"{discordURI}users/@me");
                message.Headers.Add("Authorization", user.Token);
                var profileResponse = await SendDiscordRequest(user.client, message);

                if (profileResponse.IsSuccessStatusCode)
                {
                    dynamic profileJsonResponse = JsonConvert.DeserializeObject(await profileResponse.Content.ReadAsStringAsync());
                    user.UserID = profileJsonResponse.id;
                    if (await GetOmniDiscordUser(user.UserID) != null)
                    {
                        user = await GetOmniDiscordUser(user.UserID);
                        user.client = DiscordHttpClient();
                    }
                    user.Username = profileJsonResponse.username;
                    user.Biography = profileJsonResponse.bio;
                    user.GlobalName = profileJsonResponse.global_name;
                    user.BannerColorHexCode = profileJsonResponse.banner_color;
                    user.Email = profileJsonResponse.email;

                    string pathOfUserDirectory = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory), user.FormatDirectoryName());
                    string pathOfUserDataFile = Path.Combine(pathOfUserDirectory, user.FormatFileName());

                    //Try to download profile picture.
                    try
                    {
                        Uri avatarURI = new Uri($"https://cdn.discordapp.com/avatars/{user.UserID}/{profileJsonResponse.avatar}.png?size=1024");
                        string avatarsPath = Path.Combine(pathOfUserDirectory, "avatars");
                        Directory.CreateDirectory(avatarsPath);
                        string avatarID = profileJsonResponse.avatar + ".png";
                        string avatarPath = Path.Combine(avatarsPath, avatarID);
                        if (File.Exists(avatarPath) != true)
                        {
                            WebClient wc = new();
                            await wc.DownloadFileTaskAsync(avatarURI, avatarPath);
                        }
                        user.AvatarFilePath = avatarPath;
                    }
                    catch (Exception ex) { manager.logger.LogError("Discord Interface Utility", $"Couldn't get avatar for {user.Username}, " + ex.Message); }
                    await SaveOmniDiscordUser(user);
                    UpdateLinkedDiscordAccounts(user);
                    return user;
                }
                else
                {
                    throw new Exception($"Couldn't get account information for {user.UserID}, profile request was denied. Reason: {profileResponse.ReasonPhrase}");
                }
            }
            else
            {
                throw new Exception($"Couldn't get account information for {user.UserID}. Token does not work!");
            }
        }
        public async Task<OmniDiscordUser> CreateNewOmniDiscordUserLink(string token)
        {
            OmniDiscordUser user = new();
            user.Token = token;
            if (await VerifyTokenWorks(user))
            {
                user = await GetAccountInfo(user);
                NewOmniDiscordUserAdded.Invoke(user);
            }
            else
            {
                throw new Exception("Token does not work.");
            }
            return user;
        }
        public async Task<OmniDiscordUser> CreateNewOmniDiscordUserLink(string email, string password)
        {
            OmniDiscordUser user = new();

            var klivebotDiscord = ((KliveBotDiscord)manager.GetServiceByClassType<KliveBotDiscord>()[0]);
            var accounts = await GetAllLinkedOmniDiscordUsersFromDisk();
            if (accounts.Any())
            {
                if (accounts.Where(k => k.Email == email).Any())
                {
                    manager.logger.LogStatus("Discord Interface Utility", "Trying set up account for user, but account already exists. Returning existing account.");
                    var account = accounts.Where(k => k.Email == email).ToArray()[0];
                    user = account;
                    return await GetAccountInfo(user);
                }
            }
            user.client = DiscordHttpClient();
            user.Password = password;
            user.Email = email;
            HttpRequestMessage httpMessageContent = new();
            httpMessageContent.RequestUri = new Uri($"{discordURI}auth/login");
            var data = new JObject();
            data.Add("gift_code_sku_id", null);
            data.Add("login", email);
            data.Add("login_source", null);
            data.Add("password", password);
            data.Add("undelete", false);
            string serialisedData = data.ToString();
            httpMessageContent.Content = new StringContent(serialisedData, Encoding.UTF8, "application/json");
            httpMessageContent.Method = HttpMethod.Post;
            var response = await SendDiscordRequest(user.client, httpMessageContent);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                dynamic responseDeserialised = JsonConvert.DeserializeObject(content);
                user.UserID = responseDeserialised.user_id;
                if (await GetOmniDiscordUser(user.UserID) != null)
                {
                    user = await GetOmniDiscordUser(user.UserID);
                    user.client = DiscordHttpClient();
                    user.Password = password;
                    user.Email = email;
                }
                if (responseDeserialised.mfa == true && responseDeserialised.mfa != null)
                {
                    string ticket = responseDeserialised.ticket;
                    string twoFA = await TryAndRetrieve2FAFromKlives(user, manager);
                    if (twoFA != null && twoFA != string.Empty)
                    {
                        manager.logger.LogStatus("Discord Interface Utility", "2FA Received by Klives: " + twoFA);
                        var twofadata = new JObject();
                        twofadata.Add("code", twoFA);
                        twofadata.Add("gift_code_sku_id", null);
                        twofadata.Add("login_source", null);
                        twofadata.Add("ticket", ticket);
                        HttpRequestMessage httpMessageContent2 = new();
                        string serialisedtwofadata = twofadata.ToString();
                        httpMessageContent2.Content = new StringContent(serialisedtwofadata, Encoding.UTF8, "application/json");
                        httpMessageContent2.RequestUri = new Uri($"{discordURI}auth/mfa/totp");
                        httpMessageContent2.Method = HttpMethod.Post;
                        var response2 = await SendDiscordRequest(user.client, httpMessageContent2);
                        if (response2.IsSuccessStatusCode)
                        {
                            dynamic response2Deserialised = JsonConvert.DeserializeObject(await response2.Content.ReadAsStringAsync());
                            user.Token = response2Deserialised.token;
                            manager.logger.LogStatus("Discord Interface Utility", $"Logged into OmniDiscordAccount {user.Email}.");
                            user = await GetAccountInfo(user);
                            manager.logger.LogStatus("Discord Interface Utility", $"Acquired account info from {user.Email}: {user.GlobalName}.");
                            NewOmniDiscordUserAdded.Invoke(user);
                            return user;
                        }
                        else
                        {
                            manager.logger.LogError("Discord Interface Utility", "Couldn't login for account as 2FA failed. Reason: " + response2.ReasonPhrase);
                            await klivebotDiscord.SendMessageToKlives($"Couldn't login into OmniDiscordAccount ({user.Email}) with 2FA! Reason: " + response2.ReasonPhrase);
                            return null;
                        }
                    }
                    else
                    {
                        manager.logger.LogError("Discord Interface Utility", "Couldn't login for account as Klives declined the 2fa.");
                        await klivebotDiscord.SendMessageToKlives($"Couldn't login into OmniDiscordAccount ({user.Email}) as you cancelled it.");
                        return null;
                    }
                }
                else
                {
                    user.Token = responseDeserialised.token;
                    user.UserID = responseDeserialised.user_id;
                    manager.logger.LogStatus("Discord Interface Utility", $"Logged into OmniDiscordAccount {user.Email}.");
                    user = await GetAccountInfo(user);
                    return user;
                }
            }
            else
            {
                manager.logger.LogError("Discord Interface Utility", "Couldn't login for account with email: " + email);
                await klivebotDiscord.SendMessageToKlives($"Couldn't login into OmniDiscordAccount ({user.Email}) at all! Reason: " + response.ReasonPhrase);
                return null;
            }
        }
        public class OmniDiscordUser
        {
            [JsonIgnore]
            public HttpClient client = new();
            [JsonIgnore]
            public DiscordWebsocketInterface websocketInterface;

            public string UserID;
            public string Username;
            public string Email;
            public string Password;
            public string Token;

            public string Biography;
            public string Pronouns;
            public string GlobalName;

            public string BannerColorHexCode;
            public string AvatarFilePath;

            public OmniDiscordGuild[] Guilds;

            //float: affinity of each friend.
            public KeyValuePair<OmniDiscordUser, float>[] Acquaintances;

            public string FormatDirectoryName()
            {
                return $"{Username}-{UserID}";
            }
            public string GetDirectoryPath()
            {
                string pathOfUserDirectory = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory), FormatDirectoryName());
                return pathOfUserDirectory;
            }
            public string CreateDMDirectoryPathString()
            {
                string DMDirectoryPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordDMMessagesDirectory), $"user{UserID}");
                return DMDirectoryPath;
            }
            public string CreateKnownDMChannelsDirectoryPathString()
            {
                string DMDirectoryPath = Path.Combine(GetDirectoryPath(), "KnownDMChannels");
                return DMDirectoryPath;
            }
            public string CreateDMPath()
            {
                string DMDirectoryPath = Path.Combine(CreateDMDirectoryPathString(), $"{UserID}messages.omnimessages");
                return DMDirectoryPath;
            }

            public string FormatFileName()
            {
                return $"{Username}-data.omnidiscuser";
            }
        }
    }
}
