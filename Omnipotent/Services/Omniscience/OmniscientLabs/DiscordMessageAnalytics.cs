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
            // Time analytics was produced
            public DateTime AnalyticsGeneratedAt { get; set; }

            // ... rest of your properties ...
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

        //I DID NOT PRODUCE OR WRITE THIS LIST!!!! I HAVE A VERY CLEAN MOUTH!!!!!! - Klives
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
            var result = new AnalyticsResult
            {
                AnalyticsGeneratedAt = DateTime.UtcNow
            };

            // ... rest of your method ...
        }
    }
}
