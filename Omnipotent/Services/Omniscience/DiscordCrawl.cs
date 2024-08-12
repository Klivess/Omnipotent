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
                if (!Directory.Exists(item.CreateDMDirectoryPathString()))
                {
                    Directory.CreateDirectory(item.CreateDMDirectoryPathString());
                }
                //Download all new messages from channels
                serviceManager.logger.LogStatus("Omniscience", $"Updating messages from discord account: {item.Username}");
                //Get All Downloaded Messages
                var downloadedMessages = await GetAllDownloadedMessages(item);
                serviceManager.logger.LogStatus("Omniscience", $"{downloadedMessages.Count} discord messages in user '{item.Username}' message database as of now.");
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
                        var messageDivision = downloadedMessages.Where(k => k.PostedInChannelID == dmchannel.ChannelID).OrderBy(k => k.TimeStamp);
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
                            }
                            foreach (var message in oldmessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                            {
                                await SaveDiscordMessage(item, message);
                            }
                            messagesCount += oldmessages.Count + newMessages.Count;
                        }
                        else
                        {
                            newMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID);
                            //Save only messages that are not already saved
                            serviceManager.logger.LogStatus("Omniscience", $"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                            foreach (var message in newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                            {
                                await SaveDiscordMessage(item, message);
                            }
                            messagesCount += newMessages.Count;
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

        public async Task SaveDiscordMessage(OmniDiscordUser user, OmniDiscordMessage message)
        {
            string path = Path.Combine(user.CreateDMDirectoryPathString(), message.MessageID + ".omnimessage");
            await serviceManager.fileHandlerService.WriteToFile(path, JsonConvert.SerializeObject(message));
        }

        public async Task<List<OmniDiscordMessage>> GetAllDownloadedMessages(OmniDiscordUser user)
        {
            Stopwatch debug = Stopwatch.StartNew();
            List<OmniDiscordMessage> messages = new();
            //Multithreaded file reading to speed up retrieval
            var files = Directory.GetFiles(user.CreateDMDirectoryPathString()).Where(k => Path.GetExtension(k) == ".omnimessage").ToList();
            //Perform DIV operation to find ideal thread count;
            int threads = files.Count / 1000;
            if (threads == 0)
                threads = 1;
            int excess = files.Count - (threads * 10);
            if (excess < 0)
                excess = 0;
            for (int i = 0; i < threads; i++)
            {
                Thread task = new(async () =>
                {
                    foreach (var file in files.Skip(i * threads).SkipLast(files.Count - (i * threads)))
                    {
                        var message = JsonConvert.DeserializeObject<OmniDiscordMessage>(await serviceManager.fileHandlerService.ReadDataFromFile(file));
                        messages.Add(message);
                        Console.WriteLine(messages.Count);
                    }
                });
                task.Start();
            }
            Thread excesstask = new(async () =>
            {
                foreach (var file in files.Skip(threads))
                {
                    var message = JsonConvert.DeserializeObject<OmniDiscordMessage>(await serviceManager.fileHandlerService.ReadDataFromFile(file));
                    messages.Add(message);
                    Console.WriteLine(messages.Count);
                }
            });
            excesstask.Start();
            Console.WriteLine($"Took {debug.Elapsed.TotalSeconds} seconds to load {messages.Count} messages.");
            return messages;
        }
    }
}
