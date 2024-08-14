﻿using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.Omniscience.DiscordInterface;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;

namespace Omnipotent.Services.Omniscience
{
    public class DiscordCrawl : OmniService
    {
        public DiscordInterface.DiscordInterface discordInterface;

        List<OmniDiscordUser> LinkedUsers;
        List<OmniDiscordMessage> AllCapturedMessages;
        public DiscordCrawl()
        {
            name = "Omniscience: DiscordCrawl";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            discordInterface = new(serviceManager);
            serviceManager.logger.LogStatus("Omniscience", "Starting Discord Crawl.");
            LinkedUsers = (await discordInterface.GetAllLinkedOmniDiscordUsersFromDisk()).ToList();
            serviceManager.logger.LogStatus("Omniscience", $"{LinkedUsers.Count} OmniDiscordUsers linked. Loading all saved messages from disk into memory..");
            Stopwatch time = Stopwatch.StartNew();
            await LoadAllMessagesFromDisk(LinkedUsers.ToArray());
            time.Stop();
            serviceManager.logger.LogStatus("Omniscience", $"Loaded {AllCapturedMessages.Count} messages from disk in {time.Elapsed.TotalSeconds} seconds.");
            if (OmniPaths.CheckIfOnServer())
            {
                UpdateDiscordMessageDatabase();
            }
            foreach (var item in LinkedUsers)
            {
                item.websocketInterface = new(this, item);
                var response = await discordInterface.ChatInterface.GetAllUserGuilds(item);
                foreach (var guild in response)
                {
                    await SaveDiscordGuild(guild);
                }
            }
            ServiceLog($"{(await GetAllDownloadedGuilds()).Length} guilds in database.");
            discordInterface.NewOmniDiscordUserAdded += (async (user) =>
                {
                    LinkedUsers.Add(user);
                    user.websocketInterface = new(this, user);
                    await user.websocketInterface.BeginInitialisation();
                });


            //Set up controllers
            Action<KliveAPI.KliveAPI.UserRequest> getMessageCount = async (request) =>
            {
                await request.ReturnResponse(RandomGeneration.GenerateRandomLengthOfNumbers(100));
            };
            Action<KliveAPI.KliveAPI.UserRequest> lengthyBuffer = async (request) =>
            {
                await Task.Delay(10000);
                await request.ReturnResponse("BLAHAHHH" + RandomGeneration.GenerateRandomLengthOfNumbers(10));
            };
            await serviceManager.GetKliveAPIService().CreateRoute("/omniscience/getmessagecount", getMessageCount);
            await serviceManager.GetKliveAPIService().CreateRoute("/omniscience/buffer", lengthyBuffer);

        }

        private OmniDiscordUser SelectUser(string username)
        {
            return LinkedUsers.FirstOrDefault(k => k.Username == username);
        }
        public async Task UpdateDiscordMessageDatabase()
        {
            serviceManager.logger.LogStatus("Omniscience", "Updating Discord Message Database.");
            foreach (var item in LinkedUsers)
            {
                if (!Directory.Exists(item.CreateDMDirectoryPathString()))
                {
                    Directory.CreateDirectory(item.CreateDMDirectoryPathString());
                }
                //Download all new messages from channels
                serviceManager.logger.LogStatus("Omniscience", $"Updating messages from discord account: {item.Username}");
                //Scan all DM Channels
                var allDMs = await discordInterface.ChatInterface.GetAllDMChannels(item);
                Stopwatch stopwatch = Stopwatch.StartNew();
                int newMessagesCount = 0;
                foreach (var dmchannel in allDMs)
                {
                    Task task = new(async () =>
                    {
                        Stopwatch individualDMStopwatch = Stopwatch.StartNew();
                        int messagesCount = 0;
                        serviceManager.logger.LogStatus("Omniscience", $"Scanning DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                        var messageDivision = AllCapturedMessages.Where(k => k.PostedInChannelID == dmchannel.ChannelID).OrderBy(k => k.TimeStamp);
                        //Recursively download backwards from the last message
                        List<OmniDiscordMessage> oldmessages = new();
                        List<OmniDiscordMessage> newMessages = new();
                        if (messageDivision.Any())
                        {
                            oldmessages = await discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID, messageDivision.Last().MessageID);
                            newMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID, messageDivision.First().MessageID);
                            //Save only messages that are not already saved
                            serviceManager.logger.LogStatus("Omniscience", $"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                            foreach (var message in newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                            {
                                await SaveDiscordMessage(item, message);
                                messagesCount++;
                            }
                            foreach (var message in oldmessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                            {
                                await SaveDiscordMessage(item, message);
                                messagesCount++;
                            }
                        }
                        else
                        {
                            newMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID);
                            //Save only messages that are not already saved
                            serviceManager.logger.LogStatus("Omniscience", $"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                            foreach (var message in newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                            {
                                await SaveDiscordMessage(item, message);
                                messagesCount++;
                            }
                        }
                        newMessagesCount += messagesCount;
                        individualDMStopwatch.Stop();
                        serviceManager.logger.LogStatus("Omniscience", $"Downloaded {messagesCount} DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))} in {individualDMStopwatch.Elapsed.TotalSeconds} seconds");
                    });
                    task.Start();
                }
                stopwatch.Stop();
                serviceManager.logger.LogStatus("Omniscience", $"Downloaded {newMessagesCount} new messages from discord user: {item.Username}, taking {stopwatch.Elapsed.TotalSeconds} seconds.");
            }
        }
        private async Task LoadAllMessagesFromDisk(OmniDiscordUser[] users)
        {
            AllCapturedMessages = new();
            foreach (var user in users)
            {
                var messages = await GetAllDownloadedMessages(user);
                if (messages.Any())
                {
                    AllCapturedMessages = AllCapturedMessages.Concat(messages).ToList();
                }
            }
        }
        public async Task SaveDiscordMessage(OmniDiscordUser user, OmniDiscordMessage message)
        {
            string path = Path.Combine(user.CreateDMDirectoryPathString(), message.MessageID + ".omnimessage");
            await serviceManager.fileHandlerService.WriteToFile(path, JsonConvert.SerializeObject(message));
            if (!AllCapturedMessages.Where(k => k.MessageID == message.MessageID).Any())
            {
                AllCapturedMessages.Add(message);
            }
        }
        public async Task<List<OmniDiscordMessage>> GetAllDownloadedMessages(OmniDiscordUser user)
        {
            Stopwatch debug = Stopwatch.StartNew();
            List<OmniDiscordMessage> messages = new();
            var files = Directory.GetFiles(user.CreateDMDirectoryPathString()).Where(k => Path.GetExtension(k) == ".omnimessage").ToList();
            var result = Parallel.ForEach(files, (file) =>
            {
                messages.Add(JsonConvert.DeserializeObject<OmniDiscordMessage>(serviceManager.fileHandlerService.ReadDataFromFile(file, true).Result));
                Console.WriteLine(messages.Count);
            });
            while (!result.IsCompleted) { }
            Console.WriteLine($"Took {debug.Elapsed.TotalSeconds} seconds to load {messages.Count} messages. {debug.Elapsed.TotalSeconds / messages.Count} seconds per message");
            return messages;
        }

        public async Task SaveDiscordGuild(OmniDiscordGuild guild)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordGuilds), guild.GuildID + ".omniguild");
            await serviceManager.fileHandlerService.WriteToFile(path, JsonConvert.SerializeObject(guild));
        }

        public async Task<OmniDiscordGuild[]> GetAllDownloadedGuilds()
        {

            List<OmniDiscordGuild> guilds = new();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordGuilds)).Where(k => Path.GetExtension(k) == ".omniguild").ToList();
            foreach (var file in files)
            {
                guilds.Add(JsonConvert.DeserializeObject<OmniDiscordGuild>(await serviceManager.fileHandlerService.ReadDataFromFile(file)));
            }
            return guilds.ToArray();
        }
    }
}
