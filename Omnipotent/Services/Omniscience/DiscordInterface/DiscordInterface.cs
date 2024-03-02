using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.ComponentModel.DataAnnotations;
using System.Formats.Asn1;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Helpers;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public class DiscordInterface
    {
        public const string discordURI = "https://discord.com/api/v9/";
        private OmniServiceManager manager;

        public DiscordInterface(OmniServiceManager manager)
        {
            this.manager = manager;
        }
        public async Task<OmniDiscordUser> LoadFromDisk(string discordID)
        {
            try
            {
                var accounts = (await GetAllOmniDiscordUsers()).Where(k=>k.UserID==discordID);
                if(accounts.Any())
                {
                    return accounts.ToArray()[0];
                }
                else
                {
                    manager.logger.LogError("Discord Interface Utility", $"Discord account with ID {discordID} doesn't exist!");
                    return null;
                }
            }
            catch(Exception ex)
            {
                manager.logger.LogError("Discord Interface Utility", "Couldn't load " + discordID + " on demand!");
                return null;
            }
        }
        public async Task<OmniDiscordUser[]> GetAllOmniDiscordUsers()
        {
            var allDirectories = Directory.GetDirectories(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory));
            List<OmniDiscordUser> users = new();
            foreach(var directory in allDirectories)
            {
                var file = Directory.GetFiles(directory).Where(k => Path.GetExtension(k) == $".omnidiscuser");
                if (file.Any())
                {
                    OmniDiscordUser user = await manager.fileHandlerService.ReadAndDeserialiseDataFromFile<OmniDiscordUser>(file.ToArray()[0]);
                    users.Add(user);
                }
            }
            return users.ToArray();
        }
        private async Task<string> TryAndRetrieve2FAFromKlives(OmniDiscordUser user)
        {
            var klivebotDiscord = ((KliveBotDiscord)manager.GetServiceByClassType<KliveBotDiscord>()[0]);
            var embedBuilder = KliveBotDiscord.MakeSimpleEmbed("Require 2fa to login to OmniDiscordUser!",
                $"Trying to login to\n\nEmail: {user.Email}\nPassword: Length {user.Password.Length}\n\nBut I require a 2fa code. Please provide the code, or reject this request.",
                DSharpPlus.Entities.DiscordColor.DarkBlue);

            embedBuilder.AddComponents(new DSharpPlus.Entities.DiscordComponent[]
            {
                new DSharpPlus.Entities.TextInputComponent("2FA Code", $"2fa{user.Email}", "Enter your 2fa code here!"),
                new DSharpPlus.Entities.DiscordButtonComponent(DSharpPlus.ButtonStyle.Danger, $"2facancel{user.Email}", "Cancel", emoji: new DiscordComponentEmoji(DiscordEmoji.FromName(klivebotDiscord.Client, ":x:")))
            });
            string submitted = "";

            klivebotDiscord.Client.ComponentInteractionCreated += async (s, e) =>
            {
                if (e.Id == $"2facancel{user.Email}")
                {
                    submitted = "";
                }
                else if(e.Id == $"2fa{user.Email}")
                {
                    submitted = e.Values[0];
                }
            };

            if (submitted=="")
            {
                return null;
            }
            else
            {
                return submitted;
            }
        }

        public HttpClient DiscordHttpClient()
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Content-Type", "application/json");
            return client;
        }
        
        public async void SaveOmniDiscordUser(OmniDiscordUser user)
        {
            try
            {
                string pathOfUserDirectory = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory), user.FormatDirectoryName());
                string pathOfUserDataFile = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordUsersDirectory), user.FormatFileName());
                if (!Directory.Exists(pathOfUserDirectory))
                {
                    await manager.fileHandlerService.CreateDirectory(pathOfUserDirectory);
                }
                string serialisedData = JsonConvert.SerializeObject(user);
                await manager.fileHandlerService.WriteToFile(pathOfUserDataFile, serialisedData);
            }
            catch(Exception ex)
            {
                manager.logger.LogError("Discord Interface Utility", ex, "Error saving omnidiscorduser!");
            }
        }

        public async void LoginToDiscord(OmniDiscordUser user, string email, string password)
        {
            var accounts = await GetAllOmniDiscordUsers();
            if (accounts.Any())
            {
                if (accounts.Where(k => k.Email == email).Any())
                {
                    manager.logger.LogStatus("Discord Interface Utility", "Trying set up account for user, but account already exists. Returning existing account.");
                    var account = accounts.Where(k => k.Email == email).ToArray()[0];
                    user = account;
                    return;
                }
            }
            user.client = DiscordHttpClient();
            user.Username = email;
            user.Email = password;
            HttpRequestMessage httpMessageContent = new();
            httpMessageContent.RequestUri = new Uri($"{discordURI}auth/login");
            (string gift_code_sku_id, string login, string login_source, string password, bool undelete) data;
            data.gift_code_sku_id = null;
            data.login = email;
            data.password = password;
            data.login_source = null;
            data.undelete = false;
            httpMessageContent.Content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var response = await user.client.SendAsync(httpMessageContent);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                dynamic responseDeserialised = JsonConvert.DeserializeObject(content);
            }
            else
            {
                manager.logger.LogError("Discord Interface Utility", "Couldn't login for account with email: "+email);
            }

        }
        public class OmniDiscordUser
        {
            public HttpClient client = new();

            public string UserID;
            public string Username;
            public string Email;
            public string Password;
            public string Token;

            public string FormatDirectoryName()
            {
                return $"{Username}-{UserID}";
            }

            public string FormatFileName()
            {
                return $"{Username}-data.omnidiscuser";
            }
        }
    }
}
