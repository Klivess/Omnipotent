using DSharpPlus;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;
using static System.Net.Mime.MediaTypeNames;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public class ChatInterface
    {
        private DiscordInterface parentInterface;

        public ChatInterface(DiscordInterface parentInterface)
        {
            this.parentInterface = parentInterface;
        }

        public async Task<List<OmniDiscordMessage>> GetMessagesAsync(OmniDiscordUser user, long channelID, int limit = 50, bool AutomaticallyDownloadAttachment = true, long? beforeMessageID = null, long? afterMessageID = null)
        {
            string processID = RandomGeneration.GenerateRandomLengthOfNumbers(10);
            string responseString = "";
            try
            {
                WebClient wc = new();
                var client = DiscordInterface.DiscordHttpClient(user);
                string messageEndpoint = $"https://discord.com/api/v9/channels/{channelID}/messages?limit={limit}";
                if (beforeMessageID != null)
                {
                    messageEndpoint += $"&before={beforeMessageID.Value}";
                }
                else if (afterMessageID != null)
                {
                    messageEndpoint += $"&after={afterMessageID.Value}";
                }
                HttpRequestMessage httpMessage = new();
                httpMessage.RequestUri = new Uri(messageEndpoint);
                httpMessage.Method = HttpMethod.Get;
                var response = await parentInterface.SendDiscordRequest(client, httpMessage, false);
                List<OmniDiscordMessage> messages = new();
                if (response.IsSuccessStatusCode)
                {
                    responseString = await response.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(responseString) != true)
                    {
                        dynamic responseJsonn = JsonConvert.DeserializeObject(responseString);
                        foreach (var responseJson in responseJsonn)
                        {
                            messages.Add(await ProcessMessageJSONObjectToOmniDiscordMessage(responseJson.ToString(), true));
                        }
                    }
                }
                else
                {
                    throw new Exception($"Failed to get messages for {user.Username} in channel {channelID}. Response: " + response.ReasonPhrase);
                }
                return messages;
            }
            catch (Exception aex)
            {
                parentInterface.manager.logger.LogStatus("DiscordInterface: ChatInterface", "Failed to get messages for " + user.Username + " in channel " + channelID + ". Exception: " + aex.Message);
                return await GetMessagesAsync(user, channelID, limit, AutomaticallyDownloadAttachment, beforeMessageID, afterMessageID);

            }
        }

        public async Task<OmniDiscordMessage> ProcessMessageJSONObjectToOmniDiscordMessage(string messageText, bool isinDM, bool AutomaticallyDownloadAttachment = true)
        {
            WebClient wc = new();
            dynamic responseJsonn = JsonConvert.DeserializeObject(messageText);
            OmniDiscordMessage message = new();
            message.AuthorUsername = responseJsonn.author.username;
            message.AuthorID = responseJsonn.author.id;
            message.MessageID = responseJsonn.id;
            message.MessageContent = responseJsonn.content;
            message.IsTTS = responseJsonn.tts;
            message.TimeStamp = Convert.ToDateTime(responseJsonn.timestamp);
            message.MentionedEveryone = responseJsonn.mention_everyone;
            message.IsEdited = responseJsonn.edited_timestamp == null;
            List<OmniMessageImageAttachment> imageAttachments = new();
            List<OmniMessageVideoAttachment> videoAttachments = new();
            List<OmniMessageVoiceMessageAttachment> voiceAttachments = new();
            List<OmniMessageReactions> reactions = new();
            foreach (var item in responseJsonn.attachments)
            {
                if (item.content_type != null)
                {
                    if (((string)item.content_type).StartsWith("image"))
                    {
                        OmniMessageImageAttachment image = new();
                        image.ContentType = item.content_type;
                        image.Filename = item.filename;
                        image.ImageHeightpx = item.height;
                        image.ImageWidthpx = item.width;
                        image.ImageSizeBytes = item.size;
                        image.AttachmentID = item.id;
                        image.Placeholder = item.placeholder;
                        image.URL = item.url;
                        image.ProxyURL = item.proxy_url;
                        image.OriginalMessageID = message.MessageID;
                        image.AuthorID = message.AuthorID;
                        if (AutomaticallyDownloadAttachment)
                        {
                            try
                            {
                                string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordImageAttachmentsDirectory), $"{image.AttachmentID}-{image.OriginalMessageID}-{image.Filename}");
                                wc.DownloadFile(new Uri(image.URL), filePath);
                                image.FilePath = filePath;
                            }
                            catch (Exception ex)
                            {
                                parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download image attachment {image.Filename} from {message.AuthorUsername} in channel {message.PostedInChannelID}");
                            }
                        }
                        imageAttachments.Add(image);
                    }
                    else if (((string)item.content_type).StartsWith("audio"))
                    {
                        OmniMessageVoiceMessageAttachment voiceMessage = new();
                        voiceMessage.ContentType = item.content_type;
                        voiceMessage.Filename = item.filename;
                        voiceMessage.AttachmentID = item.id;
                        voiceMessage.ImageSizeBytes = item.size;
                        voiceMessage.URL = item.url;
                        voiceMessage.ProxyURL = item.proxy_url;
                        try
                        {
                            voiceMessage.VoiceMessageDuration = TimeSpan.FromSeconds((float)item.duration_secs);
                        }
                        catch (Exception ex)
                        {
                            voiceMessage.VoiceMessageDuration = TimeSpan.FromSeconds(-1);
                        }
                        voiceMessage.Waveform = item.waveform;
                        voiceMessage.OriginalMessageID = message.MessageID;
                        voiceMessage.AuthorID = message.AuthorID;
                        if (AutomaticallyDownloadAttachment)
                        {
                            try
                            {
                                string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordVoiceAttachmentsDirectory), $"{voiceMessage.AttachmentID}-{voiceMessage.OriginalMessageID}-{voiceMessage.Filename}");
                                wc.DownloadFile(new Uri(voiceMessage.URL), filePath);
                                voiceMessage.FilePath = filePath;
                            }
                            catch (Exception ex)
                            {
                                parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download audio attachment {voiceMessage.Filename} from {message.AuthorUsername} in channel {message.PostedInChannelID}");
                            }
                        }
                        voiceAttachments.Add(voiceMessage);
                    }
                    else if (((string)item.content_type).StartsWith("video"))
                    {
                        OmniMessageVideoAttachment video = new();
                        video.ContentType = item.content_type;
                        video.Filename = item.filename;
                        video.VideoSizeBytes = item.size;
                        video.AttachmentID = item.id;
                        video.Placeholder = item.placeholder;
                        video.URL = item.url;
                        video.ProxyURL = item.proxy_url;
                        video.VideoTitle = item.title;
                        video.OriginalMessageID = message.MessageID;
                        video.AuthorID = message.AuthorID;
                        if (AutomaticallyDownloadAttachment)
                        {
                            try
                            {
                                string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordVideoAttachmentsDirectory), $"{video.AttachmentID}-{video.OriginalMessageID}-{video.Filename}");
                                wc.DownloadFile(new Uri(video.URL), filePath);
                                video.FilePath = filePath;
                            }
                            catch (Exception ex)
                            {
                                parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download video attachment {video.Filename} from {message.AuthorUsername} in channel {message.PostedInChannelID}");
                            }
                        }
                        videoAttachments.Add(video);
                    }
                }
            }
            message.ImageAttachments = imageAttachments.ToArray();
            message.VoiceMessageAttachments = voiceAttachments.ToArray();
            try
            {
                if (responseJsonn.reactions != null)
                {
                    foreach (var item in responseJsonn.reactions)
                    {
                        OmniMessageReactions reaction = new();
                        reaction.EmojiID = item.emoji.id;
                        reaction.EmojiName = item.emoji.name;
                        reaction.Count = item.count;
                        reactions.Add(reaction);
                    }
                }
            }
            catch (Exception ex) { }
            message.MessageReactions = reactions.ToArray();
            message.PostedInChannelID = responseJsonn.channel_id;
            message.MessageType = (OmniMessageType)(responseJsonn.type);
            message.MessageFlag = (OmniMessageFlag)(responseJsonn.flags);
            try
            {
                message.ReferencedMessageID = responseJsonn.referenced_message.id;
            }
            catch (Exception ex) { }
            try
            {
                if (message.MessageType == OmniMessageType.Call)
                {
                    message.CallInformation.EndedTimestamp = DateTime.Parse(responseJsonn.call.ended_timestamp);
                    List<long> participants = new();
                    foreach (var item in responseJsonn.call.participants)
                    {
                        participants.Add(item);
                    }
                    message.CallInformation.Participants = participants.ToArray();
                }
            }
            catch (Exception) { }
            if (isinDM)
            {
                message.IsInDM = true;
                message.ChannelRecipients = new();
                try
                {
                    if (responseJsonn.channel.recipients != null)
                    {
                        foreach (var item in responseJsonn.channel.recipients)
                        {
                            OmniDiscordUserInfo userInfo = new();
                            userInfo.UserID = item.id;
                            userInfo.Username = item.username;
                            userInfo.AvatarID = item.avatar;
                            userInfo.AvatarURL = ConstructAvatarURL(userInfo.UserID.ToString(), userInfo.AvatarID);
                            userInfo.Discriminator = item.discriminator;
                            userInfo.Flags = (UserFlags)item.public_flags;
                            userInfo.AccentColorHex = item.accent_color;
                            userInfo.GlobalName = item.global_name;
                            userInfo.BannerColorHex = item.banner_color;
                            message.ChannelRecipients.Add(userInfo);
                        }
                    }
                }
                catch (RuntimeBinderException bex)
                {
                }
            }
            return message;
        }
        public async Task<List<OmniDiscordMessage>> GetALLMessagesAsync(OmniDiscordUser user, long channelID, long? beforeMessage = null)
        {
            List<OmniDiscordMessage> messages = new();
            long? lastMessage = beforeMessage;
            while (true)
            {
                try
                {
                    var result = await GetMessagesAsync(user, channelID, 100, beforeMessageID: lastMessage).WaitAsync(TimeSpan.FromMinutes(5));
                    if (result.Count == 0)
                    {
                        break;
                    }
                    messages = messages.Concat(result).ToList();
                    if (messages.Count > 0)
                    {
                        lastMessage = messages[messages.Count - 1].MessageID;
                    }
                }
                catch (Exception tex)
                {
                }
            }
            return messages;
        }
        public async Task<List<OmniDiscordMessage>> GetALLMessagesAsyncAfter(OmniDiscordUser user, long channelID, long? afterMessage = null)
        {
            List<OmniDiscordMessage> messages = new();
            long? recentMessage = afterMessage;
            while (true)
            {
                var result = await GetMessagesAsync(user, channelID, 100, afterMessageID: recentMessage);
                if (result.Count == 0)
                {
                    break;
                }
                messages = messages.Concat(result).ToList();
                if (messages.Count > 0)
                {
                    recentMessage = messages[messages.Count - 1].MessageID;
                }
            }
            return messages;
        }
        public async Task<OmniDMChannelLayout[]> GetAllDMChannels(OmniDiscordUser user)
        {
            var allAffinities = await GetAllAffinities(user);
            List<OmniDMChannelLayout> channels = new();
            foreach (var item in allAffinities)
            {
                channels.Add(await GetDMChannel(user, item.Key));
            }
            return channels.ToArray();
        }
        public async Task<OmniDMChannelLayout> GetDMChannel(OmniDiscordUser user, long userID)
        {
            string messageEndpoint = $"https://discord.com/api/v9/users/@me/channels";
            var client = DiscordInterface.DiscordHttpClient(user);
            HttpRequestMessage httpMessage = new();
            httpMessage.RequestUri = new Uri(messageEndpoint);
            httpMessage.Method = HttpMethod.Post;
            CreateDMPayload payload = new();
            string[] recipientss = new string[1];
            recipientss[0] = userID.ToString();
            payload.recipients = recipientss;
            string serialisedPayload = JsonConvert.SerializeObject(payload);
            httpMessage.Content = new StringContent(serialisedPayload, System.Text.Encoding.UTF32, "application/json");
            var response = await parentInterface.SendDiscordRequest(client, httpMessage);
            string responseString = await response.Content.ReadAsStringAsync();
            string test = "";
            if (response.IsSuccessStatusCode)
            {
                dynamic channelData = JsonConvert.DeserializeObject(responseString);
                OmniDMChannelLayout layout = new();
                if (channelData.last_message_id != null)
                    layout.LastMessageID = (long)channelData.last_message_id;
                layout.ChannelType = ConvertChannelTypeToEnum((int)channelData.type);
                layout.ChannelID = channelData.id;
                List<OmniDiscordUserInfo> recipients = new();
                foreach (var recipient in channelData.recipients)
                {
                    long recipientID = (long)recipient.id;
                    recipients.Add(await GetUser(user, recipientID));
                }
                layout.Recipients = recipients.ToArray();
                return layout;
            }
            else
            {
                throw new Exception("Failed to get DM channel. Response: " + response.ReasonPhrase);
            }
        }
        public async Task<OmniDiscordUserInfo> GetUser(OmniDiscordUser user, long userID)
        {

            string messageEndpoint = $"https://discord.com/api/v9/users/{userID}";
            var client = DiscordInterface.DiscordHttpClient(user);
            HttpRequestMessage httpMessage = new();
            httpMessage.RequestUri = new Uri(messageEndpoint);
            httpMessage.Method = HttpMethod.Get;
            var response = await parentInterface.SendDiscordRequest(client, httpMessage);
            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                OmniDiscordUserInfo userInfo = new();
                dynamic responseJson = JsonConvert.DeserializeObject(responseString);
                userInfo.UserID = responseJson.id;
                userInfo.Username = responseJson.username;
                userInfo.AvatarID = responseJson.avatar;
                userInfo.AvatarURL = ConstructAvatarURL(userInfo.UserID.ToString(), userInfo.AvatarID);
                userInfo.Discriminator = responseJson.discriminator;
                userInfo.Flags = (UserFlags)responseJson.public_flags;
                userInfo.AccentColorHex = (responseJson.accent_color);
                userInfo.GlobalName = responseJson.global_name;
                userInfo.BannerColorHex = responseJson.banner_color;
                return userInfo;
            }
            else
            {
                throw new Exception($"Failed to get user {userID}. Response: " + response.ReasonPhrase);
            }

        }
        public async Task<Dictionary<long, float>> GetAllAffinities(OmniDiscordUser user)
        {
            Dictionary<long, float> r = new Dictionary<long, float>();
            string messageEndpoint = $"https://discord.com/api/v9/users/@me/affinities/users";
            var client = DiscordInterface.DiscordHttpClient(user);
            HttpRequestMessage httpMessage = new();
            httpMessage.RequestUri = new Uri(messageEndpoint);
            httpMessage.Method = HttpMethod.Get;
            var response = await parentInterface.SendDiscordRequest(client, httpMessage);
            string responseString = await response.Content.ReadAsStringAsync();
            dynamic responseJson = JsonConvert.DeserializeObject(responseString);
            foreach (var item in responseJson.user_affinities)
            {
                try
                {
                    long userID = (long)item.user_id;
                    long affinity = (long)item.affinity;
                    if (userID != null && userID > 0)
                    {
                        r.Add((long)item.user_id, (float)item.affinity);
                    }
                }
                catch (Exception) { }
            }
            return r;
        }
        public async Task<bool> DirectMessageUser(OmniDiscordUser user, long userIDOfUserToMessage, string message)
        {
            try
            {
                var channel = await GetDMChannel(user, userIDOfUserToMessage);
                string messageEndpoint = $"https://discord.com/api/v9/channels/{channel.ChannelID}/messages";
                var client = DiscordInterface.DiscordHttpClient(user);
                HttpRequestMessage httpMessage = new();
                httpMessage.RequestUri = new Uri(messageEndpoint);
                httpMessage.Method = HttpMethod.Post;
                MessagePayload payload = new();
                payload.mobile_network_type = "unknown";
                payload.content = message;
                payload.nonce = parentInterface.GenerateDiscordSnowflake(DateTime.Now).ToString();
                payload.tts = false;
                payload.flags = 0;
                string serialised = JsonConvert.SerializeObject(payload);
                httpMessage.Content = new StringContent(serialised, System.Text.Encoding.UTF32, "application/json");
                var result = await parentInterface.SendDiscordRequest(client, httpMessage);
                string resultResponse = await result.Content.ReadAsStringAsync();
                string test = "";
                if (result.IsSuccessStatusCode)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to message {userIDOfUserToMessage} from {user.GlobalName}");
                return false;
            }
        }
        public async Task<OmniDiscordChannel> GetGuildChannelInfo(OmniDiscordUser user, string channelID)
        {
            var response = await parentInterface.SendDiscordGetRequest(DiscordHttpClient(user), new Uri($"https://discord.com/api/v9/channels/{channelID}"));
            string responseString = await response.Content.ReadAsStringAsync();
            dynamic responseJson = JsonConvert.DeserializeObject(responseString);
            OmniDiscordChannel channel = new();
            channel.ChannelID = responseJson.id;
            channel.ChannelName = responseJson.name;
            channel.ChannelTopic = responseJson.topic;
            channel.LastMessageID = responseJson.last_message_id;
            channel.ParentChannelID = responseJson.parent_id;
            channel.GuildID = responseJson.guild_id;
            channel.ChannelType = ConvertChannelTypeToEnum((int)responseJson.type);
            channel.ChannelFlags = (ChannelFlags)responseJson.flags;
            channel.Position = responseJson.position;
            channel.IsNSFW = responseJson.nsfw;
            return channel;
        }
        public async Task<OmniDiscordGuild[]> GetAllUserGuilds(OmniDiscordUser user)
        {
            List<OmniDiscordGuild> guilds = new();
            var response = await parentInterface.SendDiscordGetRequest(DiscordHttpClient(user), new Uri("https://discord.com/api/v9/users/@me/guilds"));
            string responseString = await response.Content.ReadAsStringAsync();
            dynamic responseJson = JsonConvert.DeserializeObject(responseString);
            if (response.IsSuccessStatusCode)
            {
                foreach (var item in responseJson)
                {
                    OmniDiscordGuild guild = new();
                    guild.GuildID = item.id;
                    guild.GuildName = item.name;
                    guild.GuildDescription = item.description;
                    guild.IconID = item.icon;
                    guild.GuildIconURL = ConstructIconURL(guild.GuildID.ToString(), guild.IconID);
                    guild.DataAcquired = DateTime.Now;
                    guild.OwnedByOmniDiscordUser = ((bool)item.owner ? user.UserID : null);
                    guilds.Add(guild);
                }
            }
            return guilds.ToArray();
        }

        public enum OmniChannelType
        {
            GuildText = 0,
            DM = 1,
            GuildVoice = 2,
            GroupDM = 3,
            GuildCategory = 4,
            GuildAnnouncement = 5,
            AnnouncementThread = 10,
            PublicThread = 11,
            PrivateThread = 12,
            GuildStageVoice = 13,
            GuildDirectory = 14,
            GuildForum = 15,
            GuildMedia = 16
        }
        public static OmniChannelType ConvertChannelTypeToEnum(int channelType)
        {
            return (OmniChannelType)channelType;
        }
        private struct CreateDMPayload
        {
            public string[] recipients;
        }
        private struct MessagePayload
        {
            public string content;
            public int flags;
            public string mobile_network_type;
            public string nonce;
            public bool tts;
        }
        public struct OmniDMChannelLayout
        {
            public long LastMessageID;
            public OmniChannelType ChannelType;
            public long ChannelID;
            public OmniDiscordUserInfo[] Recipients;
        }
    }
}
