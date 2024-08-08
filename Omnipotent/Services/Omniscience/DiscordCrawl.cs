using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.Omniscience.DiscordInterface;
using System.Diagnostics;
using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;

namespace Omnipotent.Services.Omniscience
{
    public class DiscordCrawl
    {
        OmniServiceManager serviceManager;
        DiscordInterface.DiscordInterface discordInterface;
        public DiscordCrawl(OmniServiceManager manager)
        {
            serviceManager = manager;
            discordInterface = new(serviceManager);
            BeginCrawl();
        }

        public async Task BeginCrawl()
        {
            UpdateDiscordMessageDatabase();
        }

        public async Task UpdateDiscordMessageDatabase()
        {
            serviceManager.logger.LogStatus("Omniscience", "Updating Discord Message Database.");
            var omniUsers = await discordInterface.GetAllLinkedOmniDiscordUsersFromDisk();
            foreach (var item in omniUsers)
            {
                //Download all new messages from channels
                serviceManager.logger.LogStatus("Omniscience", $"Updating messages from discord account: {item.Username}");
                //Create prereq directories
                Directory.CreateDirectory(item.CreateDMDirectoryPathString());
                //Get All Downloaded Messages
                var downloadedMessages = await GetAllDownloadedMessages(item);
                //Scan all DM Channels
                var allDMs = await discordInterface.ChatInterface.GetAllDMChannels(item);
                Stopwatch stopwatch = Stopwatch.StartNew();
                int newMessagesCount = 0;
                foreach (var dmchannel in allDMs)
                {
                    Thread channelThread = new Thread(() =>
                    {
                        int messagesCount = 0;
                        serviceManager.logger.LogStatus("Omniscience", $"Downloading DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                        var messageDivision = downloadedMessages.Where(k => k.PostedInChannelID == dmchannel.ChannelID).OrderBy(k => k.TimeStamp);
                        //Recursively download backwards from the last message
                        List<OmniDiscordMessage> oldmessages = new();
                        if (messageDivision.Any())
                        {
                            oldmessages = discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID, messageDivision.Last().MessageID).Result;
                        }
                        foreach (var oldMessage in oldmessages)
                        {
                            if (!messageDivision.Select(k => k.MessageID).Contains(oldMessage.MessageID))
                            {
                                SaveDiscordMessage(item, oldMessage).Wait();
                            }
                        }
                        //Save memory by clearing lists
                        messagesCount += oldmessages.Count;
                        oldmessages.Clear();
                        GC.Collect();
                        List<OmniDiscordMessage> newMessages = new();
                        if (messageDivision.Any())
                        {
                            newMessages = discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID, messageDivision.First().MessageID).Result;
                        }
                        foreach (var newMessage in newMessages)
                        {
                            if (!messageDivision.Select(k => k.MessageID).Contains(newMessage.MessageID))
                            {
                                SaveDiscordMessage(item, newMessage).Wait();
                            }
                        }
                        messagesCount += newMessages.Count;
                        newMessages.Clear();
                        GC.Collect();
                        newMessagesCount += messagesCount;
                        serviceManager.logger.LogStatus("Omniscience", $"Downloaded {messagesCount} DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                    });
                    channelThread.Start();
                }
                serviceManager.logger.LogStatus("Omniscience", $"Downloaded {newMessagesCount} new messages from discord user: {item.Username}, taking {stopwatch.Elapsed.TotalSeconds} seconds.");
            }
        }

        public async Task SaveDiscordMessage(OmniDiscordUser user, OmniDiscordMessage message)
        {
            string path = Path.Combine(user.CreateDMDirectoryPathString(), message.MessageID + ".omnimessage");
            await serviceManager.fileHandlerService.WriteToFile(path, JsonConvert.SerializeObject(message));
        }

        public async Task<List<OmniDiscordMessage>> GetAllDownloadedMessages(OmniDiscordUser user)
        {
            List<OmniDiscordMessage> messages = new();
            foreach (var item in Directory.GetFiles(user.CreateDMDirectoryPathString()).Where(k => Path.GetExtension(k) == ".omnimessage"))
            {
                var message = JsonConvert.DeserializeObject<OmniDiscordMessage>(await serviceManager.fileHandlerService.ReadDataFromFile(item));
                messages.Add(message);
            }
            return messages;
        }
    }
}
