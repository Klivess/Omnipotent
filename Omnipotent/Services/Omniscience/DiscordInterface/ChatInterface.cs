using Microsoft.AspNetCore.Mvc.Diagnostics;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.AspNetCore.Routing.Matching;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
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
            var response = await parentInterface.SendDiscordRequest(client, httpMessage);
            List<OmniDiscordMessage> messages = new();
            if (response.IsSuccessStatusCode)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                dynamic responseJsonn = JsonConvert.DeserializeObject(responseString);
                foreach (var responseJson in responseJsonn)
                {
                    OmniDiscordMessage message = new();
                    message.AuthorUsername = responseJson.author.username;
                    message.AuthorID = responseJson.author.id;
                    message.MessageID = responseJson.id;
                    message.MessageContent = responseJson.content;
                    message.IsTTS = responseJson.tts;
                    message.TimeStamp = Convert.ToDateTime(responseJson.timestamp);
                    message.MentionedEveryone = responseJson.mention_everyone;
                    message.IsEdited = responseJson.edited_timestamp == null;
                    List<OmniMessageImageAttachment> imageAttachments = new();
                    List<OmniMessageVideoAttachment> videoAttachments = new();
                    List<OmniMessageVoiceMessageAttachment> voiceAttachments = new();
                    List<OmniMessageReactions> reactions = new();
                    foreach (var item in responseJson.attachments)
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
                                    wc.DownloadFile(image.URL, filePath);
                                    image.FilePath = filePath;
                                }
                                catch (Exception ex)
                                {
                                    parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download image attachment {image.Filename} from {message.AuthorUsername} in channel {channelID}");
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
                            voiceMessage.VoiceMessageDuration = TimeSpan.FromSeconds((float)item.duration_secs);
                            voiceMessage.Waveform = item.waveform;
                            if (AutomaticallyDownloadAttachment)
                            {
                                try
                                {
                                    string filePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDiscordVoiceAttachmentsDirectory), $"{voiceMessage.AttachmentID}-{voiceMessage.OriginalMessageID}-{voiceMessage.Filename}");
                                    wc.DownloadFile(voiceMessage.URL, filePath);
                                    voiceMessage.FilePath = filePath;
                                }
                                catch (Exception ex)
                                {
                                    parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download voice attachment {voiceMessage.Filename} from {message.AuthorUsername} in channel {channelID}");
                                }
                            }
                            voiceAttachments.Add(voiceMessage);
                        }
                        else if (((string)item.content_type).StartsWith("video"))
                        {
                            OmniMessageVideoAttachment video = new();
                            video.ContentType = item.content_type;
                            video.Filename = item.filename;
                            video.ImageHeightpx = item.height;
                            video.ImageWidthpx = item.width;
                            video.ImageSizeBytes = item.size;
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
                                    wc.DownloadFile(video.URL, filePath);
                                    video.FilePath = filePath;
                                }
                                catch (Exception ex)
                                {
                                    parentInterface.manager.logger.LogError("DiscordInterface: ChatInterface", ex, $"Failed to download video attachment {video.Filename} from {message.AuthorUsername} in channel {channelID}");
                                }
                            }
                            videoAttachments.Add(video);
                        }
                    }
                    message.ImageAttachments = imageAttachments.ToArray();
                    message.VoiceMessageAttachments = voiceAttachments.ToArray();
                    try
                    {
                        if (responseJson.reactions != null)
                        {
                            foreach (var item in responseJson.reactions)
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
                    message.PostedInChannelID = responseJson.channel_id;
                    message.MessageType = (OmniMessageType)(responseJson.type);
                    message.MessageFlag = (OmniMessageFlag)(responseJson.flags);
                    message.IsInDM = true;
                    try
                    {
                        message.ReferencedMessageID = responseJson.referenced_message.id;
                    }
                    catch (Exception ex) { }
                    try
                    {
                        if (message.MessageType == OmniMessageType.Call)
                        {
                            message.CallInformation.EndedTimestamp = DateTime.Parse(responseJson.call.ended_timestamp);
                            List<long> participants = new();
                            foreach (var item in responseJson.call.participants)
                            {
                                participants.Add(item);
                            }
                            message.CallInformation.Participants = participants.ToArray();
                        }
                    }
                    catch (Exception) { }
                    messages.Add(message);
                }
            }
            else
            {
                throw new Exception($"Failed to get messages for {user.Username} in channel {channelID}. Response: " + response.ReasonPhrase);
            }
            return messages;
        }

        public async Task<List<OmniDiscordMessage>> GetALLMessagesAsync(OmniDiscordUser user, long channelID, long? beforeMessage = null)
        {
            List<OmniDiscordMessage> messages = new();
            long? lastMessage = beforeMessage;
            while (true)
            {
                var result = await GetMessagesAsync(user, channelID, 100, beforeMessageID: lastMessage);
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
