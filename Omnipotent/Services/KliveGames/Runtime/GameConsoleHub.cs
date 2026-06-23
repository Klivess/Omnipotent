using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Per-instance fan-out of console output to any number of connected WebSocket clients.
    /// New clients get a one-shot "replay" of the recent ring buffer, then receive each subsequent
    /// line live. Each socket has its own send lock (WebSocket.SendAsync is not concurrency-safe).
    /// </summary>
    public sealed class GameConsoleHub
    {
        private sealed class Subscriber
        {
            public WebSocket Socket = null!;
            public readonly SemaphoreSlim SendLock = new(1, 1);
        }

        private readonly ConcurrentDictionary<Guid, Subscriber> _subs = new();

        public int SubscriberCount => _subs.Count;

        public Guid AddSubscriber(WebSocket socket)
        {
            var id = Guid.NewGuid();
            _subs[id] = new Subscriber { Socket = socket };
            return id;
        }

        public void RemoveSubscriber(Guid id) => _subs.TryRemove(id, out _);

        /// <summary>Sends the recent buffered lines to a single freshly-connected socket.</summary>
        public async Task SendReplayAsync(Guid subscriberId, IReadOnlyList<string> recentLines, CancellationToken ct)
        {
            if (!_subs.TryGetValue(subscriberId, out var sub)) return;
            var json = JsonConvert.SerializeObject(new { type = "replay", lines = recentLines });
            await SendRawAsync(sub, json, ct);
        }

        /// <summary>Broadcasts one console line to every connected client.</summary>
        public Task BroadcastLineAsync(string line)
        {
            if (_subs.IsEmpty) return Task.CompletedTask;
            var json = JsonConvert.SerializeObject(new { type = "line", data = line });
            return BroadcastRawAsync(json);
        }

        /// <summary>Broadcasts an arbitrary status/event object to every connected client.</summary>
        public Task BroadcastEventAsync(string eventType, object payload)
        {
            if (_subs.IsEmpty) return Task.CompletedTask;
            var json = JsonConvert.SerializeObject(new { type = eventType, data = payload });
            return BroadcastRawAsync(json);
        }

        private async Task BroadcastRawAsync(string json)
        {
            foreach (var kv in _subs)
            {
                try
                {
                    await SendRawAsync(kv.Value, json, CancellationToken.None);
                }
                catch
                {
                    _subs.TryRemove(kv.Key, out _);
                }
            }
        }

        private static async Task SendRawAsync(Subscriber sub, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await sub.SendLock.WaitAsync(ct);
            try
            {
                if (sub.Socket.State == WebSocketState.Open)
                    await sub.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                sub.SendLock.Release();
            }
        }
    }
}
