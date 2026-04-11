using Newtonsoft.Json;
using Omnipotent.Logging;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatRoom
    {
        public string Id { get; }
        public string Name { get; }
        public string CreatedBy { get; }
        public ConcurrentDictionary<string, KliveChatClient> Users { get; } = new();

        public KliveChatRoom(string id, string name, string createdBy)
        {
            Id = id;
            Name = name;
            CreatedBy = createdBy;
        }

        public async Task AddClient(KliveChatClient client, KliveChatService service)
        {
            Users.TryAdd(client.Id, client);
            _ = service.ServiceLog($"Client {client.Name} ({client.Id}) joined room {Id}.");

            // Notify others
            await BroadcastMessage(new KliveChatMessage
            {
                Type = "user-joined",
                SenderId = client.Id,
                Payload = new { id = client.Id, name = client.Name }
            }, client.Id);

            // Send existing users to the new client
            var existingUsers = Users.Values.Where(u => u.Id != client.Id).Select(u => new { id = u.Id, name = u.Name }).ToList();
            await SendMessageToClient(client, new KliveChatMessage
            {
                Type = "room-info",
                Payload = new { users = existingUsers, roomName = Name, roomId = Id }
            });

            await ListenToClient(client, service);
        }

        private async Task ListenToClient(KliveChatClient client, KliveChatService service)
        {
            var buffer = new byte[64 * 1024];
            var messageBuffer = new StringBuilder();

            try
            {
                while (client.Socket.State == WebSocketState.Open)
                {
                    var result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        string json = messageBuffer.ToString();
                        messageBuffer.Clear();

                        try
                        {
                            var msg = JsonConvert.DeserializeObject<KliveChatMessage>(json);
                            if (msg != null)
                            {
                                msg.SenderId = client.Id; // Enforce correct sender
                                await HandleMessage(client, msg, service);
                            }
                        }
                        catch (Exception ex)
                        {
                            _ = _ = service.ServiceLogError(ex, $"Failed to parse WebRTC matching for {client.Id}");
                        }
                    }
                }
            }
            catch (WebSocketException) { /* Typical disconnection */ }
            finally
            {
                RemoveClient(client.Id, service);
                if (client.Socket.State == WebSocketState.Open || client.Socket.State == WebSocketState.CloseReceived)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Left room", CancellationToken.None);
                }
            }
        }

        private void RemoveClient(string clientId, KliveChatService service)
        {
            if (Users.TryRemove(clientId, out _))
            {
                _ = service.ServiceLog($"Client {clientId} left room {Id}.");
                _ = BroadcastMessage(new KliveChatMessage
                {
                    Type = "user-left",
                    SenderId = clientId,
                    Payload = new { id = clientId }
                }, clientId);
            }
        }

        private async Task HandleMessage(KliveChatClient sender, KliveChatMessage msg, KliveChatService service)
        {
            // WebRTC signaling is usually targeted to a specific peer
            if (!string.IsNullOrEmpty(msg.TargetId))
            {
                if (Users.TryGetValue(msg.TargetId, out var target))
                {
                    await SendMessageToClient(target, msg);
                }
            }
            else if (msg.Type == "chat-message")
            {
                // Simple textual chat can be broadcast
                await BroadcastMessage(msg);
            }
        }

        private async Task BroadcastMessage(KliveChatMessage msg, string skipClientId = null)
        {
            var tasks = Users.Values.Select(async client =>
            {
                if (client.Id == skipClientId) return;
                await SendMessageToClient(client, msg);
            });
            await Task.WhenAll(tasks);
        }

        private async Task SendMessageToClient(KliveChatClient client, KliveChatMessage msg)
        {
            await client.SendLock.WaitAsync();
            try
            {
                if (client.Socket.State != WebSocketState.Open) return;
                string json = JsonConvert.SerializeObject(msg);
                byte[] data = Encoding.UTF8.GetBytes(json);
                await client.Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                client.SendLock.Release();
            }
        }
    }
}

