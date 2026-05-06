using System;

namespace Omnipotent.Services.Omniscience.Domain
{
    /// <summary>Row from the persons table.</summary>
    public class PersonRecord
    {
        public string PersonId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? MergedIntoPersonId { get; set; }
        public string? AvatarPath { get; set; }
    }

    /// <summary>Row from the platform_identities table.</summary>
    public class PlatformIdentityRecord
    {
        public string IdentityId { get; set; } = "";
        public string PersonId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string PlatformUserId { get; set; } = "";
        public string? PlatformUsername { get; set; }
        public string? DisplayName { get; set; }
        public string? AvatarPath { get; set; }
        public string? Bio { get; set; }
        public DateTime? FirstSeen { get; set; }
        public DateTime? LastSeen { get; set; }
    }

    /// <summary>Row from the conversations table.</summary>
    public class ConversationRecord
    {
        public string ConversationId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? GuildId { get; set; }
        public string? GuildName { get; set; }
        public string? ChannelId { get; set; }
        public string? Title { get; set; }
        public DateTime? FirstSeen { get; set; }
        public DateTime? LastSeen { get; set; }
    }

    /// <summary>Row from harvest_sources.</summary>
    public class HarvestSourceRecord
    {
        public string SourceId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string? Label { get; set; }
        public byte[]? TokenEncrypted { get; set; }
        public string? SelfPlatformUserId { get; set; }
        public string? SelfUsername { get; set; }
        public string Status { get; set; } = "active";
        public string? LastStatusMessage { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? LastFullSyncAt { get; set; }
        public DateTime? LastEventAt { get; set; }
    }
}
