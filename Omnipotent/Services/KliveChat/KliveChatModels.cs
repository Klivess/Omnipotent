using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>A room as stored on disk: everything that should survive a restart.</summary>
    public class KliveChatRoomRecord
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("createdBy")] public string CreatedBy { get; set; } = string.Empty;
        [JsonProperty("createdByUserId")] public string? CreatedByUserId { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        /// <summary>When someone last joined or left. An empty room expires a day after this unless it is persistent.</summary>
        [JsonProperty("lastActiveUtc")] public DateTime LastActiveUtc { get; set; }
        [JsonProperty("persistent")] public bool Persistent { get; set; }
        [JsonProperty("locked")] public bool Locked { get; set; }
        /// <summary>Identity keys (profile:…, guest:…, ip:…) refused at the door.</summary>
        [JsonProperty("bannedKeys")] public List<string> BannedKeys { get; set; } = new();

        public KliveChatRoomRecord Clone() => new()
        {
            Id = Id,
            Name = Name,
            CreatedBy = CreatedBy,
            CreatedByUserId = CreatedByUserId,
            CreatedUtc = CreatedUtc,
            LastActiveUtc = LastActiveUtc,
            Persistent = Persistent,
            Locked = Locked,
            BannedKeys = new List<string>(BannedKeys),
        };
    }

    /// <summary>A room as the lobby sees it.</summary>
    public class KliveChatRoomSummary
    {
        [JsonProperty("roomId")] public string RoomId { get; set; } = string.Empty;
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("createdBy")] public string CreatedBy { get; set; } = string.Empty;
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("lastActiveUtc")] public DateTime LastActiveUtc { get; set; }
        [JsonProperty("persistent")] public bool Persistent { get; set; }
        [JsonProperty("locked")] public bool Locked { get; set; }
        [JsonProperty("userCount")] public int UserCount { get; set; }
        [JsonProperty("participants")] public List<KliveChatLobbyParticipant> Participants { get; set; } = new();
        [JsonProperty("roomPath")] public string RoomPath { get; set; } = string.Empty;
        /// <summary>Whether the viewer may rename, pin or delete this room (its creator, or klivechat.rooms.delete-any).</summary>
        [JsonProperty("canManage")] public bool CanManage { get; set; }
    }

    public class KliveChatLobbyParticipant
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("isGuest")] public bool IsGuest { get; set; }
        [JsonProperty("isMuted")] public bool IsMuted { get; set; }
        [JsonProperty("hasVideo")] public bool HasVideo { get; set; }
        [JsonProperty("isScreenSharing")] public bool IsScreenSharing { get; set; }
        [JsonProperty("connected")] public bool Connected { get; set; }
    }

    public class KliveChatRoomMutationResult
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = string.Empty;
        [JsonProperty("room")] public KliveChatRoomSummary? Room { get; set; }
    }

    /// <summary>A room's live settings as participants see them.</summary>
    public class KliveChatRoomState
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("createdBy")] public string CreatedBy { get; set; } = string.Empty;
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("persistent")] public bool Persistent { get; set; }
        [JsonProperty("locked")] public bool Locked { get; set; }
    }

    public class KliveChatParticipantSummary
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("isGuest")] public bool IsGuest { get; set; }
        [JsonProperty("isMuted")] public bool IsMuted { get; set; }
        [JsonProperty("hasVideo")] public bool HasVideo { get; set; }
        [JsonProperty("isScreenSharing")] public bool IsScreenSharing { get; set; }
        [JsonProperty("hasScreenAudio")] public bool HasScreenAudio { get; set; }
        [JsonProperty("canModerate")] public bool CanModerate { get; set; }
        [JsonProperty("isHost")] public bool IsHost { get; set; }
        /// <summary>False while the participant's connection dropped and the room is holding their place.</summary>
        [JsonProperty("connected")] public bool Connected { get; set; } = true;
        [JsonProperty("joinedUtc")] public DateTime JoinedUtc { get; set; }
    }

    public class KliveChatChatMessage
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("senderId")] public string SenderId { get; set; } = string.Empty;
        [JsonProperty("senderName")] public string SenderName { get; set; } = string.Empty;
        [JsonProperty("senderIsGuest")] public bool SenderIsGuest { get; set; }
        [JsonProperty("text")] public string Text { get; set; } = string.Empty;
        [JsonProperty("ts")] public DateTime Ts { get; set; }
        /// <summary>Echoed back so the sender can match its optimistic copy.</summary>
        [JsonProperty("clientMsgId", NullValueHandling = NullValueHandling.Ignore)] public string? ClientMsgId { get; set; }
    }

    public class KliveChatIceServer
    {
        [JsonProperty("urls")] public List<string> Urls { get; set; } = new();
        [JsonProperty("username", NullValueHandling = NullValueHandling.Ignore)] public string? Username { get; set; }
        [JsonProperty("credential", NullValueHandling = NullValueHandling.Ignore)] public string? Credential { get; set; }
    }

    public class KliveChatParticipantRemoval
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("action")] public string Action { get; set; } = string.Empty;
        [JsonProperty("byId")] public string ById { get; set; } = string.Empty;
        [JsonProperty("byName")] public string ByName { get; set; } = string.Empty;
    }

    /// <summary>The wire envelope. Signalling payloads are relayed as-is (<see cref="JToken"/>), never re-modelled.</summary>
    public class KliveChatMessage
    {
        [JsonProperty("type")] public string Type { get; set; } = string.Empty;
        [JsonProperty("senderId", NullValueHandling = NullValueHandling.Ignore)] public string? SenderId { get; set; }
        [JsonProperty("targetId", NullValueHandling = NullValueHandling.Ignore)] public string? TargetId { get; set; }
        [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)] public object? Payload { get; set; }
    }

    /// <summary>Who is knocking: resolved by the service from the socket's query and credential, admitted by the room.</summary>
    public class KliveChatJoinRequest
    {
        public string ParticipantId { get; set; } = string.Empty;
        /// <summary>The id came from a browser secret, so the same tab may reconnect into the same seat.</summary>
        public bool Resumable { get; set; }
        public string Name { get; set; } = "Guest";
        public string? UserId { get; set; }
        public ProfileRank Rank { get; set; } = ProfileRank.None;
        public bool HoldsModeratePermission { get; set; }
        public bool HoldsDeleteAnyPermission { get; set; }
        public string IdentityKey { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public int Protocol { get; set; } = 1;
        public List<KliveChatIceServer> IceServers { get; set; } = new();

        public bool IsGuest => string.IsNullOrWhiteSpace(UserId);
    }
}
