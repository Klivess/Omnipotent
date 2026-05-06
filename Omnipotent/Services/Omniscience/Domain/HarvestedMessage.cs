using System;
using System.Collections.Generic;

namespace Omnipotent.Services.Omniscience.Domain
{
    /// <summary>
    /// Canonical, platform-agnostic representation of a single harvested message,
    /// produced by an <see cref="Ingest.IPlatformIngester"/> and handed to <see cref="Ingest.IngestPipeline"/>.
    /// </summary>
    public class HarvestedMessage
    {
        public string Platform { get; set; } = "";
        public string PlatformMessageId { get; set; } = "";
        public DateTime SentAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public string? Content { get; set; }
        public string? ReplyToPlatformMessageId { get; set; }

        // Conversation
        public string ConversationKind { get; set; } = "";   // 'guild_channel' | 'dm' | 'group_dm'
        public string? GuildId { get; set; }
        public string? GuildName { get; set; }
        public string ChannelId { get; set; } = "";
        public string? ChannelTitle { get; set; }

        // Author
        public HarvestedIdentity Author { get; set; } = new();

        // Optional extra recipients seen on this message (DM members, mentions for participant tracking)
        public List<HarvestedIdentity> Participants { get; set; } = new();

        public List<HarvestedAttachment> Attachments { get; set; } = new();

        public string? RawJson { get; set; }
    }

    public class HarvestedIdentity
    {
        public string Platform { get; set; } = "";
        public string PlatformUserId { get; set; } = "";
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Bio { get; set; }
    }

    public class HarvestedAttachment
    {
        public string OriginalUrl { get; set; } = "";
        public string? Mime { get; set; }
        public long? SizeBytes { get; set; }
        public string? Kind { get; set; }     // 'image' | 'video' | 'voice' | 'file'
        public string? Filename { get; set; }
    }
}
