using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.Omniscience.DiscordInterface;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;
using System.Web.Mvc.Async;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.Omniscience.DiscordInterface.ChatInterface;
using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;

namespace Omnipotent.Services.Omniscience
{
    public class DiscordCrawl : OmniService
    {
        public DiscordInterface.DiscordInterface discordInterface;

        List<OmniDiscordUser> LinkedUsers;
        SynchronizedCollection<OmniDiscordMessage> AllCapturedMessages;
        public DiscordCrawl()
        {
            name = "Omniscience: DiscordCrawl";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            discordInterface = new(this);
            ServiceLog("Starting Discord Crawl.");
            LinkedUsers = (await discordInterface.GetAllLinkedOmniDiscordUsersFromDisk()).ToList();

            ServiceLog($"{LinkedUsers.Count} OmniDiscordUsers linked. Loading all saved messages from disk into memory..");
            Stopwatch time = Stopwatch.StartNew();
            await LoadAllMessagesFromDisk(LinkedUsers.ToArray());

            List<string> brokenAccountNames = new();
            foreach (var item in LinkedUsers)
            {
                if (await discordInterface.VerifyTokenWorks(item) == false)
                {
                    brokenAccountNames.Add(item.Username);
                }
            }

            if (brokenAccountNames.Any())
            {
                ServiceLog($"The following accounts have invalid tokens and will not be monitored for this session: {string.Join(", ", brokenAccountNames)}");
                LinkedUsers.RemoveAll(k => brokenAccountNames.Contains(k.Username));
                try
                {
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives($"The following accounts have invalid tokens and will not be monitored for this session: {string.Join(", ", brokenAccountNames)}");
                }
                catch (Exception) { }
            }
            time.Stop();
            ServiceLog($"Loaded {AllCapturedMessages.Count} messages from disk in {time.Elapsed.TotalSeconds} seconds.");
            UpdateDiscordMessageDatabase();
            foreach (var item in LinkedUsers)
            {
                item.websocketInterface = new(this, item);
                await item.websocketInterface.BeginInitialisation();
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


            CreateRoutes();
        }
        private async Task CreateRoutes()
        {
            //Set up controllers
            Action<UserRequest> createNewOmniUser = async (request) =>
            {
                try
                {
                    var token = request.userParameters.Get("token");
                    var account = await discordInterface.CreateNewOmniDiscordUserLink(token);
                    UpdateIndividualUserMessageDatabase(account);
                    await request.ReturnResponse(JsonConvert.SerializeObject(account), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            };
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/omniscience/createOmniUser", createNewOmniUser, HttpMethod.Post, KMPermissions.Associate);
            Action<KliveAPI.KliveAPI.UserRequest> getMessageCount = async (request) =>
            {
                await request.ReturnResponse(AllCapturedMessages.Count.ToString(), "application/json");
            };
            await (await serviceManager.GetKliveAPIService()).CreateRoute("/omniscience/getmessagecount", getMessageCount, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Anybody);
        }
        private OmniDiscordUser SelectUser(string username)
        {
            return LinkedUsers.FirstOrDefault(k => k.Username == username);
        }

        private async Task UpdateIndividualUserMessageDatabase(OmniDiscordUser item)
        {
            try
            {
                if (!Directory.Exists(item.CreateDMDirectoryPathString()))
                {
                    Directory.CreateDirectory(item.CreateDMDirectoryPathString());
                }
                //Download all new messages from channels
                ServiceLog($"Updating messages from discord account: {item.Username}");
                //Scan all DM Channels
                var allDMs = (await discordInterface.ChatInterface.GetAllDMChannels(item));
                Stopwatch stopwatch = Stopwatch.StartNew();
                int newMessagesCount = 0;
                CancellationTokenSource token = new();
                ParallelOptions parallelOptions = new()
                {
                    MaxDegreeOfParallelism = 10
                };
                var result = Parallel.ForEachAsync(allDMs, parallelOptions, async (dmchannel, token) =>
                {
                    Stopwatch individualDMStopwatch = Stopwatch.StartNew();
                    int messagesCount = 0;
                    ServiceLog($"Scanning DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                    var messageDivision = AllCapturedMessages.Where(k => k.PostedInChannelID == dmchannel.ChannelID).OrderBy(k => k.TimeStamp);
                    //Recursively download backwards from the last message
                    List<OmniDiscordMessage> oldmessages = new();
                    List<OmniDiscordMessage> newMessages = new();

                    List<long> updatedUserIDs = new();

                    if (messageDivision.Any())
                    {
                        oldmessages = await discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID, messageDivision.First().MessageID);
                        newMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID, messageDivision.Last().MessageID);
                        //Save only messages that are not already saved
                        var newMessagesFiltered = newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList();
                        var oldMessagesFiltered = oldmessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList();
                        var saveDMProgress = await ServiceLog($"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))} Progress: 0%");
                        foreach (var message in newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                        {
                            await SaveDiscordMessage(item, message);
                            if (!updatedUserIDs.Contains(message.AuthorID))
                            {
                                await SaveKnownDiscordUser(await discordInterface.ChatInterface.GetUser(item, message.AuthorID));
                                updatedUserIDs.Add(message.AuthorID);
                            }
                            messagesCount++;
                            float percentage = (float)messagesCount / (newMessagesFiltered.Count + oldMessagesFiltered.Count);
                            ServiceUpdateLoggedMessage(saveDMProgress, $"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))} Progress: {percentage}%");
                        }
                        foreach (var message in oldmessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                        {
                            if (!updatedUserIDs.Contains(message.AuthorID))
                            {
                                await SaveKnownDiscordUser(await discordInterface.ChatInterface.GetUser(item, message.AuthorID));
                                updatedUserIDs.Add(message.AuthorID);
                            }
                            await SaveDiscordMessage(item, message);
                            await SaveKnownDiscordUser(await discordInterface.ChatInterface.GetUser(item, message.AuthorID));
                            messagesCount++;
                            float percentage = (float)messagesCount / (newMessagesFiltered.Count + oldMessagesFiltered.Count);
                            ServiceUpdateLoggedMessage(saveDMProgress, $"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))} Progress: {percentage}%");
                        }
                    }
                    else
                    {
                        newMessages = await discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID);
                        //Save only messages that are not already saved
                        //ServiceLog($"Saving DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                        foreach (var message in newMessages.Where(k => (!(messageDivision.Select(n => n.MessageID).Contains(k.MessageID)))).ToList())
                        {
                            await SaveDiscordMessage(item, message);
                            messagesCount++;
                            if (!updatedUserIDs.Contains(message.AuthorID))
                            {
                                await SaveKnownDiscordUser(await discordInterface.ChatInterface.GetUser(item, message.AuthorID));
                                updatedUserIDs.Add(message.AuthorID);
                            }
                        }
                    }
                    newMessagesCount += messagesCount;
                    individualDMStopwatch.Stop();
                    if (messagesCount > 0)
                    {
                        ServiceLog($"Downloaded {messagesCount} DMs from DM containing users: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))} in {individualDMStopwatch.Elapsed.TotalSeconds} seconds");
                    }
                });
                await result;
                stopwatch.Stop();
                ServiceLog($"Downloaded {newMessagesCount} new messages from discord user: {item.Username}, taking {stopwatch.Elapsed.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                try
                {
                    (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Catastrophic error updating individual user messages in DiscordCrawl: " + ex.Message);
                }
                catch (Exception) { }
            }
        }
        public async Task UpdateDiscordMessageDatabase()
        {
            ServiceLog("Updating Discord Message Database.");
            foreach (var item in LinkedUsers)
            {
                await UpdateIndividualUserMessageDatabase(item);
            }
        }
        private async Task LoadAllMessagesFromDisk(OmniDiscordUser[] users)
        {
            AllCapturedMessages = new();
            foreach (var user in users)
            {
                var messages = await GetAllDownloadedMessages(user);
                foreach (var item in messages)
                {
                    AllCapturedMessages.Add(item);
                }
            }
        }
        public async Task SaveDiscordMessage(OmniDiscordUser user, OmniDiscordMessage message)
        {
            string path = Path.Combine(user.CreateDMDirectoryPathString(), message.MessageID + ".omnimessage");
            if (string.IsNullOrEmpty(message.MessageID.ToString()))
            {
                ServiceLogError($"Message from '{message.AuthorUsername}': Message ID is null, cannot save message.");
                return;
            }
            if (!AllCapturedMessages.Select(k => k.MessageID).Contains(message.MessageID))
            {
                AllCapturedMessages.Add(message);
                await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(message));
            }
            else
            {
                return;
            }
        }
        public async Task<List<OmniDiscordMessage>> GetAllDownloadedMessages(OmniDiscordUser user)
        {
            Stopwatch debug = Stopwatch.StartNew();
            List<OmniDiscordMessage> messages = new();
            var files = Directory.GetFiles(user.CreateDMDirectoryPathString()).Where(k => Path.GetExtension(k) == ".omnimessage").ToList();
            var cancellationTokenSource = new CancellationTokenSource();
            var prog = await ServiceLog($"Starting disk load of discord messages: {messages.Count} out of {files.Count} message files.");
            var token = cancellationTokenSource.Token;
            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = 50
            };
            //var result = Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
            int errorCount = 0;
            foreach (var file in files)
            {
                try
                {
                    messages.Add(JsonConvert.DeserializeObject<OmniDiscordMessage>(await GetDataHandler().ReadDataFromFile(file, true)));
                    if (messages.Count % 1000 == 0)
                    {
                        ServiceUpdateLoggedMessage(prog, $"Starting disk load of discord messages: {messages.Count} out of {files.Count} message files.");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                }
            };
            var token2 = new CancellationTokenSource();
            //await result;
            token2.Cancel();
            await ServiceUpdateLoggedMessage(prog, $"Starting disk load of discord messages: {messages.Count} out of {files.Count} message files. {files.Count - messages.Count} files lost. {errorCount} messages lost due to errors.");
            return messages;
        }

        public async Task SaveDiscordGuild(OmniDiscordGuild guild)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordGuildsDirectory), guild.GuildID + ".omniguild");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(guild));
        }

        public async Task<OmniDiscordGuild[]> GetAllDownloadedGuilds()
        {

            List<OmniDiscordGuild> guilds = new();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordGuildsDirectory)).Where(k => Path.GetExtension(k) == ".omniguild").ToList();
            foreach (var file in files)
            {
                guilds.Add(JsonConvert.DeserializeObject<OmniDiscordGuild>(await GetDataHandler().ReadDataFromFile(file)));
            }
            return guilds.ToArray();
        }

        public async Task SaveKnownDiscordDMChannel(OmniDiscordUser user, OmniDMChannelLayout dmChannel)
        {
            string path = Path.Combine(OmniPaths.GetPath(user.CreateKnownDMChannelsDirectoryPathString()), dmChannel.ChannelID + ".omnidmchannel");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(dmChannel));
        }

        public async Task<OmniDMChannelLayout[]> GetAllKnownDiscordDMChannels(OmniDiscordUser user)
        {

            List<OmniDMChannelLayout> guilds = new();
            var files = Directory.GetFiles(user.CreateKnownDMChannelsDirectoryPathString()).Where(k => Path.GetExtension(k) == ".omnidmchannel").ToList();
            foreach (var file in files)
            {
                guilds.Add(JsonConvert.DeserializeObject<OmniDMChannelLayout>(await GetDataHandler().ReadDataFromFile(file)));
            }
            return guilds.ToArray();
        }

        public async Task SaveKnownDiscordUser(OmniDiscordUserInfo user)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordKnownUsersDirectory), user.Username + ".omniuserinfo");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(user));
        }

        public async Task<OmniDiscordUserInfo[]> GetAllDownloadedKnownUsers()
        {

            List<OmniDiscordUserInfo> users = new();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordKnownUsersDirectory)).Where(k => Path.GetExtension(k) == ".omniuserinfo").ToList();
            foreach (var file in files)
            {
                users.Add(JsonConvert.DeserializeObject<OmniDiscordUserInfo>(await GetDataHandler().ReadDataFromFile(file)));
            }
            return users.ToArray();
        }
    }
}
