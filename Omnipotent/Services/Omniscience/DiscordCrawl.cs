using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.Omniscience.DiscordInterface;
using Omnipotent.Services.Omniscience.OmniscientLabs;
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
        private HashSet<long> CapturedMessageIDs;
        private readonly object _messageIdLock = new();

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
                var guilds = await discordInterface.ChatInterface.GetAllUserGuilds(user);
                foreach (var guild in guilds)
                {
                    await SaveDiscordGuild(guild);
                }
            });

            CreateRoutes();
            UpdateAllMessages();
        }

        private async Task CreateRoutes()
        {
            var api = await serviceManager.GetKliveAPIService();

            await api.CreateRoute("/omniscience/createOmniUser", async (request) =>
            {
                try
                {
                    var token = request.userParameters.Get("token");
                    var account = await discordInterface.CreateNewOmniDiscordUserLink(token);
                    _ = Task.Run(() => UpdateAllMessagesForUser(account));
                    await request.ReturnResponse(JsonConvert.SerializeObject(account), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Associate);

            await api.CreateRoute("/omniscience/getanalytics", async (request) =>
            {
                try
                {
                    DiscordMessageAnalytics discordMessageAnalytics = new();
                    var analysis = discordMessageAnalytics.Analyze(AllCapturedMessages.ToList());
                    await request.ReturnResponse(JsonConvert.SerializeObject(analysis), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/messages", async (request) =>
            {
                try
                {
                    var channelIdParam = request.userParameters.Get("channelId");
                    var userIdParam = request.userParameters.Get("userId");
                    var guildIdParam = request.userParameters.Get("guildId");
                    var limitParam = request.userParameters.Get("limit");
                    var offsetParam = request.userParameters.Get("offset");
                    var dmOnlyParam = request.userParameters.Get("dmOnly");

                    int limit = 100;
                    int offset = 0;
                    if (!string.IsNullOrEmpty(limitParam)) int.TryParse(limitParam, out limit);
                    if (!string.IsNullOrEmpty(offsetParam)) int.TryParse(offsetParam, out offset);
                    limit = Math.Clamp(limit, 1, 1000);

                    IEnumerable<OmniDiscordMessage> query = AllCapturedMessages;
                    if (!string.IsNullOrEmpty(channelIdParam) && long.TryParse(channelIdParam, out long channelId))
                        query = query.Where(m => m.PostedInChannelID == channelId);
                    if (!string.IsNullOrEmpty(userIdParam) && long.TryParse(userIdParam, out long userId))
                        query = query.Where(m => m.AuthorID == userId);
                    if (!string.IsNullOrEmpty(guildIdParam) && long.TryParse(guildIdParam, out long guildId))
                        query = query.Where(m => m.GuildID == guildId);
                    if (!string.IsNullOrEmpty(dmOnlyParam) && bool.TryParse(dmOnlyParam, out bool dmOnly))
                        query = query.Where(m => m.IsInDM == dmOnly);

                    var results = query.OrderByDescending(m => m.TimeStamp).Skip(offset).Take(limit).ToList();
                    await request.ReturnResponse(JsonConvert.SerializeObject(results), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/search", async (request) =>
            {
                try
                {
                    var queryParam = request.userParameters.Get("q");
                    var limitParam = request.userParameters.Get("limit");
                    int limit = 100;
                    if (!string.IsNullOrEmpty(limitParam)) int.TryParse(limitParam, out limit);
                    limit = Math.Clamp(limit, 1, 500);

                    if (string.IsNullOrWhiteSpace(queryParam))
                    {
                        await request.ReturnResponse("Missing 'q' parameter.", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var results = AllCapturedMessages
                        .Where(m => !string.IsNullOrEmpty(m.MessageContent) && m.MessageContent.Contains(queryParam, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.TimeStamp)
                        .Take(limit)
                        .ToList();
                    await request.ReturnResponse(JsonConvert.SerializeObject(results), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/guilds", async (request) =>
            {
                try
                {
                    var guilds = await GetAllDownloadedGuilds();
                    await request.ReturnResponse(JsonConvert.SerializeObject(guilds), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/channels", async (request) =>
            {
                try
                {
                    var guildIdParam = request.userParameters.Get("guildId");
                    if (string.IsNullOrEmpty(guildIdParam))
                    {
                        await request.ReturnResponse("Missing 'guildId' parameter.", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!LinkedUsers.Any())
                    {
                        await request.ReturnResponse("No linked users available.", code: HttpStatusCode.ServiceUnavailable);
                        return;
                    }
                    var channels = await discordInterface.ChatInterface.GetAllGuildChannels(LinkedUsers.First(), guildIdParam);
                    await request.ReturnResponse(JsonConvert.SerializeObject(channels), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/users", async (request) =>
            {
                try
                {
                    var users = await GetAllDownloadedKnownUsers();
                    await request.ReturnResponse(JsonConvert.SerializeObject(users), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/stats", async (request) =>
            {
                try
                {
                    var messages = AllCapturedMessages.ToList();
                    var stats = new
                    {
                        TotalMessages = messages.Count,
                        DMMessages = messages.Count(m => m.IsInDM),
                        GuildMessages = messages.Count(m => !m.IsInDM),
                        UniqueAuthors = messages.Select(m => m.AuthorID).Distinct().Count(),
                        UniqueChannels = messages.Select(m => m.PostedInChannelID).Distinct().Count(),
                        UniqueGuilds = messages.Where(m => m.GuildID.HasValue).Select(m => m.GuildID.Value).Distinct().Count(),
                        TotalGuildsInDB = (await GetAllDownloadedGuilds()).Length,
                        TotalKnownUsers = (await GetAllDownloadedKnownUsers()).Length,
                        LinkedAccounts = LinkedUsers.Count,
                        OldestMessage = messages.Any() ? messages.Min(m => m.TimeStamp) : (DateTime?)null,
                        NewestMessage = messages.Any() ? messages.Max(m => m.TimeStamp) : (DateTime?)null,
                        MessagesToday = messages.Count(m => m.TimeStamp.Date == DateTime.Today),
                    };
                    await request.ReturnResponse(JsonConvert.SerializeObject(stats), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await api.CreateRoute("/omniscience/linkedAccounts", async (request) =>
            {
                try
                {
                    var accounts = LinkedUsers.Select(u => new { u.UserID, u.Username, u.GlobalName }).ToList();
                    await request.ReturnResponse(JsonConvert.SerializeObject(accounts), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Get, KMPermissions.Associate);

            await api.CreateRoute("/omniscience/resync", async (request) =>
            {
                try
                {
                    _ = Task.Run(() => UpdateAllMessages());
                    await request.ReturnResponse(JsonConvert.SerializeObject(new { status = "Resync started." }), "application/json");
                }
                catch (Exception ex)
                {
                    await request.ReturnResponse((new ErrorInformation(ex)).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                    ServiceLogError(ex);
                }
            }, HttpMethod.Post, KMPermissions.Associate);
        }

        private OmniDiscordUser SelectUser(string username)
        {
            return LinkedUsers.FirstOrDefault(k => k.Username == username);
        }

        private bool IsMessageAlreadyCaptured(long messageID)
        {
            lock (_messageIdLock)
            {
                return CapturedMessageIDs.Contains(messageID);
            }
        }

        private async Task UpdateAllMessages()
        {
            ServiceLog("Updating all messages (DMs + Guilds) for all linked users.");
            foreach (var item in LinkedUsers)
            {
                await UpdateAllMessagesForUser(item);
            }
            ServiceLog($"Full message sync complete. Total messages in memory: {AllCapturedMessages.Count}");
        }

        private async Task UpdateAllMessagesForUser(OmniDiscordUser user)
        {
            await UpdateDMMessagesForUser(user);
            await UpdateGuildMessagesForUser(user);
        }

        private async Task UpdateDMMessagesForUser(OmniDiscordUser item)
        {
            try
            {
                Directory.CreateDirectory(item.CreateDMDirectoryPathString());
                ServiceLog($"Updating DM messages from discord account: {item.Username}");
                var allDMs = await discordInterface.ChatInterface.GetAllDMChannels(item);
                Stopwatch stopwatch = Stopwatch.StartNew();
                int newMessagesCount = 0;
                ParallelOptions parallelOptions = new()
                {
                    MaxDegreeOfParallelism = 10
                };
                var result = Parallel.ForEachAsync(allDMs, parallelOptions, async (dmchannel, token) =>
                {
                    int messagesCount = 0;
                    List<long> updatedUserIDs = new();
                    var existingInChannel = AllCapturedMessages.Where(k => k.PostedInChannelID == dmchannel.ChannelID).OrderBy(k => k.TimeStamp);

                    List<OmniDiscordMessage> downloadedMessages = new();
                    if (existingInChannel.Any())
                    {
                        var oldMessages = await discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID, existingInChannel.First().MessageID);
                        var newMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(item, dmchannel.ChannelID, existingInChannel.Last().MessageID);
                        downloadedMessages.AddRange(oldMessages);
                        downloadedMessages.AddRange(newMessages);
                    }
                    else
                    {
                        downloadedMessages = await discordInterface.ChatInterface.GetALLMessagesAsync(item, dmchannel.ChannelID);
                    }

                    foreach (var message in downloadedMessages)
                    {
                        if (IsMessageAlreadyCaptured(message.MessageID)) continue;
                        await SaveDiscordMessage(item, message);
                        messagesCount++;
                        if (!updatedUserIDs.Contains(message.AuthorID))
                        {
                            try
                            {
                                await SaveKnownDiscordUser(await discordInterface.ChatInterface.GetUser(item, message.AuthorID));
                            }
                            catch (Exception) { }
                            updatedUserIDs.Add(message.AuthorID);
                        }
                    }

                    Interlocked.Add(ref newMessagesCount, messagesCount);
                    if (messagesCount > 0)
                    {
                        ServiceLog($"Downloaded {messagesCount} new DMs from channel containing: {string.Join(", ", dmchannel.Recipients.Select(k => k.Username))}");
                    }
                });
                await result;
                stopwatch.Stop();
                ServiceLog($"DM sync for {item.Username}: {newMessagesCount} new messages in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                try { (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Error updating DM messages in DiscordCrawl: " + ex.Message); }
                catch (Exception) { }
            }
        }

        private async Task UpdateGuildMessagesForUser(OmniDiscordUser user)
        {
            try
            {
                Directory.CreateDirectory(user.CreateGuildMessagesDirectoryPathString());
                var guilds = await discordInterface.ChatInterface.GetAllUserGuilds(user);
                ServiceLog($"Updating guild messages for {user.Username} across {guilds.Length} guilds.");
                Stopwatch totalStopwatch = Stopwatch.StartNew();
                int totalNewMessages = 0;

                foreach (var guild in guilds)
                {
                    await SaveDiscordGuild(guild);
                    try
                    {
                        var channels = await discordInterface.ChatInterface.GetAllGuildChannels(user, guild.GuildID);
                        var textChannels = channels.Where(c =>
                            c.ChannelType == OmniChannelType.GuildText ||
                            c.ChannelType == OmniChannelType.GuildAnnouncement ||
                            c.ChannelType == OmniChannelType.GuildForum ||
                            c.ChannelType == OmniChannelType.GuildMedia ||
                            c.ChannelType == OmniChannelType.PublicThread ||
                            c.ChannelType == OmniChannelType.PrivateThread ||
                            c.ChannelType == OmniChannelType.AnnouncementThread
                        ).ToList();

                        int guildNewMessages = 0;
                        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = 5 };
                        var result = Parallel.ForEachAsync(textChannels, parallelOptions, async (channel, token) =>
                        {
                            try
                            {
                                long guildIDLong = long.Parse(guild.GuildID);
                                var existingInChannel = AllCapturedMessages
                                    .Where(k => k.PostedInChannelID == channel.ChannelID)
                                    .OrderBy(k => k.TimeStamp);

                                List<OmniDiscordMessage> downloadedMessages = new();
                                if (existingInChannel.Any())
                                {
                                    var olderMessages = await discordInterface.ChatInterface.GetALLMessagesAsync(user, channel.ChannelID, existingInChannel.First().MessageID);
                                    var newerMessages = await discordInterface.ChatInterface.GetALLMessagesAsyncAfter(user, channel.ChannelID, existingInChannel.Last().MessageID);
                                    downloadedMessages.AddRange(olderMessages);
                                    downloadedMessages.AddRange(newerMessages);
                                }
                                else
                                {
                                    downloadedMessages = await discordInterface.ChatInterface.GetALLMessagesAsync(user, channel.ChannelID);
                                }

                                int channelNewCount = 0;
                                foreach (var message in downloadedMessages)
                                {
                                    if (IsMessageAlreadyCaptured(message.MessageID)) continue;
                                    var msg = message;
                                    msg.GuildID = guildIDLong;
                                    msg.IsInDM = false;
                                    await SaveDiscordMessage(user, msg);
                                    channelNewCount++;
                                }
                                Interlocked.Add(ref guildNewMessages, channelNewCount);
                                if (channelNewCount > 0)
                                {
                                    ServiceLog($"Downloaded {channelNewCount} messages from #{channel.ChannelName} in {guild.GuildName}");
                                }
                            }
                            catch (Exception ex)
                            {
                                ServiceLogError(ex, $"Error downloading messages from #{channel.ChannelName} in {guild.GuildName}");
                            }
                        });
                        await result;
                        Interlocked.Add(ref totalNewMessages, guildNewMessages);
                        if (guildNewMessages > 0)
                        {
                            ServiceLog($"Guild '{guild.GuildName}': {guildNewMessages} new messages downloaded.");
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, $"Error processing guild {guild.GuildName}");
                    }
                }
                totalStopwatch.Stop();
                ServiceLog($"Guild sync for {user.Username}: {totalNewMessages} new messages across {guilds.Length} guilds in {totalStopwatch.Elapsed.TotalSeconds:F1}s.");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                try { (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Error updating guild messages in DiscordCrawl: " + ex.Message); }
                catch (Exception) { }
            }
        }

        private async Task LoadAllMessagesFromDisk(OmniDiscordUser[] users)
        {
            AllCapturedMessages = new();
            CapturedMessageIDs = new();
            foreach (var user in users)
            {
                var dmMessages = await LoadMessagesFromDirectory(user.CreateDMDirectoryPathString());
                foreach (var item in dmMessages)
                {
                    AllCapturedMessages.Add(item);
                    lock (_messageIdLock) { CapturedMessageIDs.Add(item.MessageID); }
                }
                string guildDir = user.CreateGuildMessagesDirectoryPathString();
                if (Directory.Exists(guildDir))
                {
                    var guildMessages = await LoadMessagesFromDirectory(guildDir);
                    foreach (var item in guildMessages)
                    {
                        if (!CapturedMessageIDs.Contains(item.MessageID))
                        {
                            AllCapturedMessages.Add(item);
                            lock (_messageIdLock) { CapturedMessageIDs.Add(item.MessageID); }
                        }
                    }
                }
            }
        }

        private async Task<List<OmniDiscordMessage>> LoadMessagesFromDirectory(string directoryPath)
        {
            List<OmniDiscordMessage> messages = new();
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                return messages;
            }
            var files = Directory.GetFiles(directoryPath).Where(k => Path.GetExtension(k) == ".omnimessage").ToList();
            var prog = await ServiceLog($"Loading messages from {directoryPath}: 0/{files.Count}");
            int errorCount = 0;
            foreach (var file in files)
            {
                try
                {
                    messages.Add(JsonConvert.DeserializeObject<OmniDiscordMessage>(await GetDataHandler().ReadDataFromFile(file, true)));
                    if (messages.Count % 1000 == 0)
                    {
                        ServiceUpdateLoggedMessage(prog, $"Loading messages from {Path.GetFileName(directoryPath)}: {messages.Count}/{files.Count}");
                    }
                }
                catch (Exception)
                {
                    errorCount++;
                }
            }
            await ServiceUpdateLoggedMessage(prog, $"Loaded {messages.Count}/{files.Count} messages from {Path.GetFileName(directoryPath)}. {errorCount} errors.");
            return messages;
        }

        public async Task SaveDiscordMessage(OmniDiscordUser user, OmniDiscordMessage message)
        {
            if (message.MessageID == 0)
            {
                ServiceLogError($"Message from '{message.AuthorUsername}': Message ID is null/zero, cannot save message.");
                return;
            }

            bool alreadyExists;
            lock (_messageIdLock)
            {
                alreadyExists = !CapturedMessageIDs.Add(message.MessageID);
            }
            if (alreadyExists) return;

            AllCapturedMessages.Add(message);

            string directory = message.IsInDM ? user.CreateDMDirectoryPathString() : user.CreateGuildMessagesDirectoryPathString();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, message.MessageID + ".omnimessage");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(message));
        }

        public async Task<List<OmniDiscordMessage>> GetAllDownloadedMessages(OmniDiscordUser user)
        {
            var dmMessages = await LoadMessagesFromDirectory(user.CreateDMDirectoryPathString());
            string guildDir = user.CreateGuildMessagesDirectoryPathString();
            if (Directory.Exists(guildDir))
            {
                dmMessages.AddRange(await LoadMessagesFromDirectory(guildDir));
            }
            return dmMessages;
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
