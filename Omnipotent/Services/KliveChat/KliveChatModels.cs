using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Threading;
using System;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatClient
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Guest";
        [JsonIgnore]
        public WebSocket Socket { get; set; }
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
