using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using Omnipotent.Profiles;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatService : OmniService
    {
        public ConcurrentDictionary<string, KliveChatRoom> ActiveRooms { get; } = new();
        private KMProfileManager? profileManager;

        public KliveChatService()
        {
            name = "KliveChatService";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            // GET /klivechat/rooms to list basic details or get by id
            await CreateAPIRoute("/klivechat/rooms", async (req) =>
            {
                var id = req.userParameters["id"];
                if (!string.IsNullOrEmpty(id))
                {
                    if (ActiveRooms.TryGetValue(id, out var specificRoom))
                    {
                        var response = ToRoomSummary(specificRoom);
                        await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
                    }
                    else
                    {
                        await req.ReturnResponse("Room not found", "text/plain", null!, System.Net.HttpStatusCode.NotFound);
                    }
                }
                else
                {
                    var rooms = ListRoomSummaries();
                    await req.ReturnResponse(JsonConvert.SerializeObject(rooms), "application/json");
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            // GET /klivechat/me returns the current user's profile details
            await CreateAPIRoute("/klivechat/me", async (req) =>
            {
                if (req.user != null)
                {
                    var response = new
                    {
                        name = req.user.Name,
                        rank = (int)req.user.KlivesManagementRank,
                        userId = req.user.UserID,
                        canModerate = req.user.KlivesManagementRank >= KMPermissions.Associate
                    };
                    await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
                }
                else
                {
                    await req.ReturnResponse("{}", "application/json", null!, System.Net.HttpStatusCode.Unauthorized);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            // POST /klivechat/create requires Guest or above as per prompt
            await CreateAPIRoute("/klivechat/create", async (req) =>
            {
                var response = CreateRoom(req.userParameters["name"], req.user?.Name);
                await req.ReturnResponse(JsonConvert.SerializeObject(response.Room), "application/json");
            }, HttpMethod.Post, KMPermissions.Guest);

            // POST /klivechat/delete requires Guest or above
            await CreateAPIRoute("/klivechat/delete", async (req) =>
            {
                string? roomId = req.userParameters["id"];
                if (string.IsNullOrEmpty(roomId))
                {
                    await req.ReturnResponse("Missing room ID", "text/plain", null!, System.Net.HttpStatusCode.BadRequest);
                    return;
                }

                var result = await DeleteRoomAsync(
                    roomId,
                    req.user?.Name,
                    (req.user?.KlivesManagementRank ?? KMPermissions.Anybody) >= KMPermissions.Admin);

                if (result.Success)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, roomId }), "application/json");
                }
                else
                {
                    var statusCode = result.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                        ? System.Net.HttpStatusCode.Unauthorized
                        : System.Net.HttpStatusCode.NotFound;
                    await req.ReturnResponse(result.Message, "text/plain", null!, statusCode);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // WebSocket route for connections
            await ExecuteServiceMethod<KliveAPI.KliveAPI>("CreateWebSocketRoute", "/klivechat/ws", (Func<System.Net.HttpListenerContext, WebSocket, System.Collections.Specialized.NameValueCollection, KMProfile?, Task>)(async (context, socket, queryParams, user) =>
            {
                string? roomId = queryParams["roomId"];
                if (string.IsNullOrEmpty(roomId) || !ActiveRooms.TryGetValue(roomId, out var room))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid Room ID", CancellationToken.None);
                    return;
                }

                KMProfile? resolvedUser = user ?? await ResolveSocketUserAsync(queryParams["authorization"]);
                string userName = !string.IsNullOrWhiteSpace(resolvedUser?.Name)
                    ? resolvedUser.Name
                    : queryParams["name"] ?? "Guest_" + Guid.NewGuid().ToString().Substring(0, 4);

                var client = new KliveChatClient
                {
                    Name = userName,
                    Socket = socket,
                    UserId = resolvedUser?.UserID,
                    Rank = resolvedUser?.KlivesManagementRank ?? KMPermissions.Anybody,
                    GuestIdentity = string.IsNullOrWhiteSpace(queryParams["guestIdentity"])
                        ? $"{context.Request.RemoteEndPoint?.Address}|{context.Request.UserAgent}"
                        : queryParams["guestIdentity"]
                };

                await room.AddClient(client, this);
            }), KMPermissions.Anybody);

            _ = ServiceLog("KliveChatService started. Listening on /klivechat/ endpoints.");
        }

        public List<KliveChatRoomSummary> ListRoomSummaries()
        {
            return ActiveRooms.Values
                .OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToRoomSummary)
                .ToList();
        }

        public KliveChatRoomMutationResult CreateRoom(string? requestedName, string? createdBy = null)
        {
            string roomId = Guid.NewGuid().ToString("N")[..8];
            string roomName = string.IsNullOrWhiteSpace(requestedName) ? "KliveChat Room" : requestedName.Trim();
            string creator = string.IsNullOrWhiteSpace(createdBy) ? "Guest" : createdBy.Trim();

            var room = new KliveChatRoom(roomId, roomName, creator);
            ActiveRooms[roomId] = room;

            TryLog($"Created new room {roomId} by {creator}");

            return new KliveChatRoomMutationResult
            {
                Success = true,
                Message = $"Created room {roomId}.",
                Room = ToRoomSummary(room)
            };
        }

        public async Task<KliveChatRoomMutationResult> DeleteRoomAsync(string roomId, string? requestedBy, bool hasElevatedPermissions = false)
        {
            if (!ActiveRooms.TryGetValue(roomId, out var room))
            {
                return new KliveChatRoomMutationResult
                {
                    Success = false,
                    Message = "Room not found"
                };
            }

            string actor = string.IsNullOrWhiteSpace(requestedBy) ? "Guest" : requestedBy.Trim();
            if (!hasElevatedPermissions && !string.Equals(room.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                return new KliveChatRoomMutationResult
                {
                    Success = false,
                    Message = "Unauthorized to delete this room"
                };
            }

            ActiveRooms.TryRemove(roomId, out _);
            TryLog($"Room {roomId} deleted by {actor}");

            foreach (var client in room.Users.Values)
            {
                if (client.Socket != null && client.Socket.State == WebSocketState.Open)
                {
                    await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Room deleted", CancellationToken.None);
                }
            }

            return new KliveChatRoomMutationResult
            {
                Success = true,
                Message = $"Deleted room {roomId}.",
                Room = ToRoomSummary(room)
            };
        }

        private static KliveChatRoomSummary ToRoomSummary(KliveChatRoom room)
        {
            return new KliveChatRoomSummary
            {
                RoomId = room.Id,
                Name = room.Name,
                CreatedBy = room.CreatedBy,
                UserCount = room.Users.Count,
                RoomPath = $"/shared/klivechat/{room.Id}"
            };
        }

        private void TryLog(string message)
        {
            try
            {
                _ = ServiceLog(message);
            }
            catch
            {
            }
        }

        private async Task<KMProfile?> ResolveSocketUserAsync(string? authorization)
        {
            if (string.IsNullOrWhiteSpace(authorization))
            {
                return null;
            }

            if (profileManager == null)
            {
                var services = await GetServicesByType<KMProfileManager>();
                if (services != null && services.Any())
                {
                    profileManager = (KMProfileManager)services[0];
                }
            }

            if (profileManager == null)
            {
                return null;
            }

            return await profileManager.GetProfileByPassword(authorization);
        }
    }
}
