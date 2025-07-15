using Omnipotent.Services.Omniscience.DiscordInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Omniscience.OmniscientLabs
{
    public class DiscordMessageAnalytics
    {
        public class AnalyticsResult
        {
            // General
            public int TotalMessages { get; set; }
            public int TotalUsers { get; set; }
            public int TotalGuilds { get; set; }
            public int TotalChannels { get; set; }
            public int TotalDMs { get; set; }
            public int TotalEditedMessages { get; set; }
            public int TotalTTSMessages { get; set; }
            public int TotalMentionsEveryone { get; set; }
            public int TotalMessagesWithImages { get; set; }
            public int TotalMessagesWithVideos { get; set; }
            public int TotalMessagesWithVoice { get; set; }
            public int TotalMessagesWithReactions { get; set; }
            public int TotalReplies { get; set; }
            public int TotalCalls { get; set; }
            public int TotalWords { get; set; }
            public int TotalCharacters { get; set; }
            public int TotalSwearWords { get; set; }
            public int TotalQuestions { get; set; }
            public int TotalExclamations { get; set; }
            public int TotalLinks { get; set; }
            public int TotalEmojis { get; set; }
            public int TotalUniqueEmojis { get; set; }
            public int TotalMessagesWithLinks { get; set; }
            public int TotalMessagesWithEmojis { get; set; }
            public int TotalEmptyMessages { get; set; }

            // Per-user
            public Dictionary<long, UserAnalytics> UserStats { get; set; } = new();
            // Per-guild
            public Dictionary<long, GuildAnalytics> GuildStats { get; set; } = new();
            // Per-channel
            public Dictionary<long, ChannelAnalytics> ChannelStats { get; set; } = new();

            // Time-based
            public Dictionary<DateTime, int> MessagesPerDay { get; set; } = new();
            public Dictionary<int, int> MessagesPerHour { get; set; } = new();
            public Dictionary<DayOfWeek, int> MessagesPerWeekday { get; set; } = new();

            // Top lists
            public List<UserAnalytics> TopMessageSenders { get; set; } = new();
            public List<UserAnalytics> TopSwearers { get; set; } = new();
            public List<UserAnalytics> TopQuestionAskers { get; set; } = new();
            public List<UserAnalytics> TopReactedUsers { get; set; } = new();
            public List<UserAnalytics> TopImageSenders { get; set; } = new();
            public List<UserAnalytics> TopLinkSenders { get; set; } = new();
            public List<UserAnalytics> TopEmojiUsers { get; set; } = new();
            public List<UserAnalytics> TopCallParticipants { get; set; } = new();
            public List<UserAnalytics> TopMentioners { get; set; } = new();
            //Time analytics was produced
            public DateTime AnalyticsGeneratedAt { get; set; }
        }

        public class UserAnalytics
        {
            public long UserID { get; set; }
            public string Username { get; set; }
            public int MessageCount { get; set; }
            public int WordCount { get; set; }
            public int CharacterCount { get; set; }
            public int EditedMessages { get; set; }
            public int TTSMessages { get; set; }
            public int EveryoneMentions { get; set; }
            public int ImageMessages { get; set; }
            public int VideoMessages { get; set; }
            public int VoiceMessages { get; set; }
            public int ReactionCount { get; set; }
            public int Replies { get; set; }
            public int Calls { get; set; }
            public int SwearWords { get; set; }
            public int Questions { get; set; }
            public int Exclamations { get; set; }
            public int Links { get; set; }
            public int Emojis { get; set; }
            public HashSet<string> UniqueEmojis { get; set; } = new();
            public int MessagesWithLinks { get; set; }
            public int MessagesWithEmojis { get; set; }
            public int EmptyMessages { get; set; }
            public Dictionary<DateTime, int> MessagesPerDay { get; set; } = new();
            public Dictionary<int, int> MessagesPerHour { get; set; } = new();
            public Dictionary<DayOfWeek, int> MessagesPerWeekday { get; set; } = new();
        }

        public class GuildAnalytics
        {
            public long GuildID { get; set; }
            public int MessageCount { get; set; }
            public int UserCount { get; set; }
            public HashSet<long> UserIDs { get; set; } = new();
            public Dictionary<long, int> ChannelMessageCounts { get; set; } = new();
        }

        public class ChannelAnalytics
        {
            public long ChannelID { get; set; }
            public int MessageCount { get; set; }
            public HashSet<long> UserIDs { get; set; } = new();
        }

        // Swear words list (expand as needed)
        private static readonly string[] SwearWords = new[]
        {
                "fuck", "shit", "bitch", "asshole", "bastard", "damn", "crap", "dick", "piss", "cock", "pussy", "slut", "whore", "fag", "cunt", "motherfucker", "nigger", "retard"
            };

        // Emoji regex (Unicode emoji ranges)
        private static readonly Regex EmojiRegex = new(@"[\u203C-\u3299\u1F000-\u1F9FF\u1FA70-\u1FAFF\u1F300-\u1F5FF\u1F600-\u1F64F\u1F680-\u1F6FF\u1F700-\u1F77F\u1F780-\u1F7FF\u1F800-\u1F8FF\u1F900-\u1F9FF\u1FA00-\u1FA6F\u1FA70-\u1FAFF\u2600-\u26FF\u2700-\u27BF]", RegexOptions.Compiled);

        // Link regex
        private static readonly Regex LinkRegex = new(@"https?://[^\s]+", RegexOptions.Compiled);

        // Question regex
        private static readonly Regex QuestionRegex = new(@"\?\s*$", RegexOptions.Compiled);

        // Exclamation regex
        private static readonly Regex ExclamationRegex = new(@"!\s*$", RegexOptions.Compiled);

        public AnalyticsResult Analyze(List<OmniDiscordMessage> messages)
        {
            var result = new AnalyticsResult();

            var userStats = new Dictionary<long, UserAnalytics>();
            var guildStats = new Dictionary<long, GuildAnalytics>();
            var channelStats = new Dictionary<long, ChannelAnalytics>();

            var allEmojis = new HashSet<string>();

            foreach (var msg in messages)
            {
                // General
                result.TotalMessages++;
                if (msg.IsEdited) result.TotalEditedMessages++;
                if (msg.IsTTS) result.TotalTTSMessages++;
                if (msg.MentionedEveryone) result.TotalMentionsEveryone++;
                if (msg.ImageAttachments != null && msg.ImageAttachments.Length > 0) result.TotalMessagesWithImages++;
                if (msg.VideoAttachments != null && msg.VideoAttachments.Length > 0) result.TotalMessagesWithVideos++;
                if (msg.VoiceMessageAttachments != null && msg.VoiceMessageAttachments.Length > 0) result.TotalMessagesWithVoice++;
                if (msg.MessageReactions != null && msg.MessageReactions.Length > 0) result.TotalMessagesWithReactions++;
                if (msg.ReferencedMessageID.HasValue) result.TotalReplies++;
                if (msg.CallInformation != null) result.TotalCalls++;
                if (msg.IsInDM) result.TotalDMs++;
                if (msg.PostedInChannelID.HasValue)
                {
                    if (!channelStats.TryGetValue(msg.PostedInChannelID.Value, out var ch))
                    {
                        ch = new ChannelAnalytics { ChannelID = msg.PostedInChannelID.Value };
                        channelStats[msg.PostedInChannelID.Value] = ch;
                    }
                    ch.MessageCount++;
                    ch.UserIDs.Add(msg.AuthorID);
                }
                if (msg.GuildID.HasValue)
                {
                    if (!guildStats.TryGetValue(msg.GuildID.Value, out var g))
                    {
                        g = new GuildAnalytics { GuildID = msg.GuildID.Value };
                        guildStats[msg.GuildID.Value] = g;
                    }
                    g.MessageCount++;
                    g.UserIDs.Add(msg.AuthorID);
                    if (msg.PostedInChannelID.HasValue)
                    {
                        if (!g.ChannelMessageCounts.ContainsKey(msg.PostedInChannelID.Value))
                            g.ChannelMessageCounts[msg.PostedInChannelID.Value] = 0;
                        g.ChannelMessageCounts[msg.PostedInChannelID.Value]++;
                    }
                }

                // Per-user
                if (!userStats.TryGetValue(msg.AuthorID, out var user))
                {
                    user = new UserAnalytics
                    {
                        UserID = msg.AuthorID,
                        Username = msg.AuthorUsername
                    };
                    userStats[msg.AuthorID] = user;
                }
                user.MessageCount++;
                if (msg.IsEdited) user.EditedMessages++;
                if (msg.IsTTS) user.TTSMessages++;
                if (msg.MentionedEveryone) user.EveryoneMentions++;
                if (msg.ImageAttachments != null && msg.ImageAttachments.Length > 0) user.ImageMessages++;
                if (msg.VideoAttachments != null && msg.VideoAttachments.Length > 0) user.VideoMessages++;
                if (msg.VoiceMessageAttachments != null && msg.VoiceMessageAttachments.Length > 0) user.VoiceMessages++;
                if (msg.MessageReactions != null && msg.MessageReactions.Length > 0) user.ReactionCount += msg.MessageReactions.Length;
                if (msg.ReferencedMessageID.HasValue) user.Replies++;
                if (msg.CallInformation != null) user.Calls++;

                // Message content analytics
                var content = msg.MessageContent ?? string.Empty;
                var wordCount = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                var charCount = content.Length;
                user.WordCount += wordCount;
                user.CharacterCount += charCount;
                result.TotalWords += wordCount;
                result.TotalCharacters += charCount;

                // Swear words
                int swears = 0;
                foreach (var swear in SwearWords)
                {
                    var matches = Regex.Matches(content, $@"\b{Regex.Escape(swear)}\b", RegexOptions.IgnoreCase);
                    swears += matches.Count;
                }
                user.SwearWords += swears;
                result.TotalSwearWords += swears;

                // Questions
                if (QuestionRegex.IsMatch(content))
                {
                    user.Questions++;
                    result.TotalQuestions++;
                }

                // Exclamations
                if (ExclamationRegex.IsMatch(content))
                {
                    user.Exclamations++;
                    result.TotalExclamations++;
                }

                // Links
                var linkMatches = LinkRegex.Matches(content);
                if (linkMatches.Count > 0)
                {
                    user.Links += linkMatches.Count;
                    user.MessagesWithLinks++;
                    result.TotalLinks += linkMatches.Count;
                    result.TotalMessagesWithLinks++;
                }

                // Emojis
                var emojiMatches = EmojiRegex.Matches(content);
                if (emojiMatches.Count > 0)
                {
                    user.Emojis += emojiMatches.Count;
                    user.MessagesWithEmojis++;
                    foreach (Match m in emojiMatches)
                    {
                        user.UniqueEmojis.Add(m.Value);
                        allEmojis.Add(m.Value);
                    }
                    result.TotalEmojis += emojiMatches.Count;
                    result.TotalMessagesWithEmojis++;
                }

                // Empty messages
                if (string.IsNullOrWhiteSpace(content))
                {
                    user.EmptyMessages++;
                    result.TotalEmptyMessages++;
                }

                // Time-based
                var day = msg.TimeStamp.Date;
                if (!user.MessagesPerDay.ContainsKey(day)) user.MessagesPerDay[day] = 0;
                user.MessagesPerDay[day]++;
                if (!result.MessagesPerDay.ContainsKey(day)) result.MessagesPerDay[day] = 0;
                result.MessagesPerDay[day]++;

                var hour = msg.TimeStamp.Hour;
                if (!user.MessagesPerHour.ContainsKey(hour)) user.MessagesPerHour[hour] = 0;
                user.MessagesPerHour[hour]++;
                if (!result.MessagesPerHour.ContainsKey(hour)) result.MessagesPerHour[hour] = 0;
                result.MessagesPerHour[hour]++;

                var weekday = msg.TimeStamp.DayOfWeek;
                if (!user.MessagesPerWeekday.ContainsKey(weekday)) user.MessagesPerWeekday[weekday] = 0;
                user.MessagesPerWeekday[weekday]++;
                if (!result.MessagesPerWeekday.ContainsKey(weekday)) result.MessagesPerWeekday[weekday] = 0;
                result.MessagesPerWeekday[weekday]++;
            }

            // Finalize
            result.UserStats = userStats;
            result.GuildStats = guildStats;
            result.ChannelStats = channelStats;
            result.TotalUsers = userStats.Count;
            result.TotalGuilds = guildStats.Count;
            result.TotalChannels = channelStats.Count;
            result.TotalUniqueEmojis = allEmojis.Count;

            // Top lists
            result.TopMessageSenders = userStats.Values.OrderByDescending(u => u.MessageCount).Take(10).ToList();
            result.TopSwearers = userStats.Values.OrderByDescending(u => u.SwearWords).Take(10).ToList();
            result.TopQuestionAskers = userStats.Values.OrderByDescending(u => u.Questions).Take(10).ToList();
            result.TopReactedUsers = userStats.Values.OrderByDescending(u => u.ReactionCount).Take(10).ToList();
            result.TopImageSenders = userStats.Values.OrderByDescending(u => u.ImageMessages).Take(10).ToList();
            result.TopLinkSenders = userStats.Values.OrderByDescending(u => u.Links).Take(10).ToList();
            result.TopEmojiUsers = userStats.Values.OrderByDescending(u => u.Emojis).Take(10).ToList();
            result.TopCallParticipants = userStats.Values.OrderByDescending(u => u.Calls).Take(10).ToList();
            result.TopMentioners = userStats.Values.OrderByDescending(u => u.EveryoneMentions).Take(10).ToList();

            result.AnalyticsGeneratedAt = DateTime.UtcNow;

            return result;
        }
    }
}
