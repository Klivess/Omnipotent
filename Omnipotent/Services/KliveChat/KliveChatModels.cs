using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Threading;
using System;

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
        [JsonIgnore]
        public WebSocket? Socket { get; set; }
        [JsonIgnore]
        public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
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
