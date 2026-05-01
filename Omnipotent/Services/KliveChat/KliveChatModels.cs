using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Threading;
using System;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatRoomSummary
    {
        [JsonProperty("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("createdBy")]
        public string CreatedBy { get; set; } = string.Empty;

        [JsonProperty("userCount")]
        public int UserCount { get; set; }

        [JsonProperty("roomPath")]
        public string RoomPath { get; set; } = string.Empty;
    }

    public class KliveChatRoomMutationResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("room")]
        public KliveChatRoomSummary? Room { get; set; }
    }

    public class KliveChatClient
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Guest";
        public string? UserId { get; set; }
        public KMPermissions Rank { get; set; } = KMPermissions.Anybody;
        public bool IsMuted { get; set; }
        public bool HasVideo { get; set; }
        public bool IsScreenSharing { get; set; }
        [JsonIgnore]
        public WebSocket? Socket { get; set; }
        [JsonIgnore]
        public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
        [JsonIgnore]
        public string IdentityKey => !string.IsNullOrWhiteSpace(UserId)
            ? $"profile:{UserId}"
            : $"guest:{(Name ?? "Guest").Trim().ToLowerInvariant()}";
        [JsonIgnore]
        public bool CanModerate => Rank >= KMPermissions.Associate;
    }

    public class KliveChatParticipantSummary
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("rank")]
        public int Rank { get; set; }

        [JsonProperty("isGuest")]
        public bool IsGuest { get; set; }

        [JsonProperty("isMuted")]
        public bool IsMuted { get; set; }

        [JsonProperty("hasVideo")]
        public bool HasVideo { get; set; }

        [JsonProperty("isScreenSharing")]
        public bool IsScreenSharing { get; set; }

        [JsonProperty("canModerate")]
        public bool CanModerate { get; set; }
    }

    public class KliveChatMediaStateUpdate
    {
        [JsonProperty("isMuted")]
        public bool IsMuted { get; set; }

        [JsonProperty("hasVideo")]
        public bool HasVideo { get; set; }

        [JsonProperty("isScreenSharing")]
        public bool IsScreenSharing { get; set; }
    }

    public class KliveChatModerationRequest
    {
        [JsonProperty("targetId")]
        public string TargetId { get; set; } = string.Empty;

        [JsonProperty("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class KliveChatParticipantRemoval
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("action")]
        public string Action { get; set; } = string.Empty;

        [JsonProperty("byId")]
        public string ById { get; set; } = string.Empty;

        [JsonProperty("byName")]
        public string ByName { get; set; } = string.Empty;
    }

    public class KliveChatMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("senderId")]
        public string SenderId { get; set; }

        [JsonProperty("targetId")]
        public string TargetId { get; set; }

        [JsonProperty("payload")]
        public object Payload { get; set; }
    }
}
