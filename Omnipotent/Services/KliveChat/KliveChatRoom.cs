using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Logging;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatRoom
    {
        public string Id { get; }
        public string Name { get; }
        public string CreatedBy { get; }
        public ConcurrentDictionary<string, KliveChatClient> Users { get; } = new();
        private ConcurrentDictionary<string, byte> BannedIdentities { get; } = new(StringComparer.OrdinalIgnoreCase);

        public KliveChatRoom(string id, string name, string createdBy)
        {
            Id = id;
            Name = name;
            CreatedBy = createdBy;
        }

        public async Task AddClient(KliveChatClient client, KliveChatService service)
        {
            if (BannedIdentities.ContainsKey(client.IdentityKey))
            {
                if (client.Socket != null && (client.Socket.State == WebSocketState.Open || client.Socket.State == WebSocketState.CloseReceived))
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Banned from room", CancellationToken.None);
                }
                return;
            }

            Users.TryAdd(client.Id, client);
            _ = service.ServiceLog($"Client {client.Name} ({client.Id}) joined room {Id}.");

            // Send existing users to the new client
            await SendMessageToClient(client, new KliveChatMessage
            {
                Type = "room-info",
                Payload = new
                {
                    users = Users.Values.Where(u => u.Id != client.Id).Select(ToParticipantSummary).ToList(),
                    roomName = Name,
                    roomId = Id,
                    clientId = client.Id
                }
            });

            // Notify others
            await BroadcastMessage(new KliveChatMessage
            {
                Type = "user-joined",
                SenderId = client.Id,
                Payload = ToParticipantSummary(client)
            }, client.Id);

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
            else if (msg.Type == "media-state")
            {
                var state = ConvertPayload<KliveChatMediaStateUpdate>(msg.Payload);
                if (state == null)
                {
                    await SendRoomError(sender, "Invalid media state update.");
                    return;
                }

                sender.IsMuted = state.IsMuted;
                sender.HasVideo = state.HasVideo;
                sender.IsScreenSharing = state.IsScreenSharing;

                await BroadcastMessage(new KliveChatMessage
                {
                    Type = "participant-state",
                    SenderId = sender.Id,
                    Payload = ToParticipantSummary(sender)
                }, sender.Id);
            }
            else if (msg.Type == "moderate-participant")
            {
                await HandleModerationMessage(sender, msg.Payload, service);
            }
        }

        private async Task HandleModerationMessage(KliveChatClient actor, object payload, KliveChatService service)
        {
            if (actor.Rank < KMPermissions.Associate)
            {
                await SendRoomError(actor, "Associate or above is required to moderate a call.");
                return;
            }

            var request = ConvertPayload<KliveChatModerationRequest>(payload);
            if (request == null || string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.Action))
            {
                await SendRoomError(actor, "Invalid moderation request.");
                return;
            }

            if (!Users.TryGetValue(request.TargetId, out var target))
            {
                await SendRoomError(actor, "Participant not found.");
                return;
            }

            if (target.Id == actor.Id)
            {
                await SendRoomError(actor, "You cannot moderate yourself.");
                return;
            }

            if (!CanModerateTarget(actor, target))
            {
                await SendRoomError(actor, "You cannot moderate a participant with equal or higher clearance.");
                return;
            }

            string action = request.Action.Trim().ToLowerInvariant();
            if (action != "kick" && action != "ban")
            {
                await SendRoomError(actor, "Unsupported moderation action.");
                return;
            }

            if (action == "ban")
            {
                BannedIdentities[target.IdentityKey] = 0;
            }

            await BroadcastMessage(new KliveChatMessage
            {
                Type = "participant-removed",
                SenderId = actor.Id,
                Payload = new KliveChatParticipantRemoval
                {
                    Id = target.Id,
                    Action = action,
                    ById = actor.Id,
                    ByName = actor.Name
                }
            });

            string closeReason = action == "ban" ? "Banned from room" : "Removed from room";
            await CloseClientConnectionAsync(target, closeReason);
            _ = service.ServiceLog($"{actor.Name} {action}ed {target.Name} from room {Id}.");
        }

        private async Task CloseClientConnectionAsync(KliveChatClient client, string reason)
        {
            if (client.Socket == null)
            {
                return;
            }

            if (client.Socket.State == WebSocketState.Open || client.Socket.State == WebSocketState.CloseReceived)
            {
                await client.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
            }
        }

        private async Task SendRoomError(KliveChatClient client, string message)
        {
            await SendMessageToClient(client, new KliveChatMessage
            {
                Type = "room-error",
                Payload = new { message }
            });
        }

        private static bool CanModerateTarget(KliveChatClient actor, KliveChatClient target)
        {
            if (string.IsNullOrWhiteSpace(target.UserId))
            {
                return true;
            }

            return actor.Rank > target.Rank;
        }

        private static T? ConvertPayload<T>(object payload) where T : class
        {
            if (payload == null)
            {
                return null;
            }

            if (payload is T typed)
            {
                return typed;
            }

            return JToken.FromObject(payload).ToObject<T>();
        }

        private static KliveChatParticipantSummary ToParticipantSummary(KliveChatClient client)
        {
            return new KliveChatParticipantSummary
            {
                Id = client.Id,
                Name = client.Name,
                Rank = (int)client.Rank,
                IsGuest = string.IsNullOrWhiteSpace(client.UserId),
                IsMuted = client.IsMuted,
                HasVideo = client.HasVideo,
                IsScreenSharing = client.IsScreenSharing,
                CanModerate = client.CanModerate
            };
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

