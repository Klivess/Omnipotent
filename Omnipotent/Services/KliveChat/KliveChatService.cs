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
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveChat
{
    public class KliveChatService : OmniService
    {
        public ConcurrentDictionary<string, KliveChatRoom> ActiveRooms { get; } = new();

        public KliveChatService()
        {
            name = "KliveChatService";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            // GET /klivechat/rooms to list basic details or get by id
            await ExecuteServiceMethod<KliveAPI.KliveAPI>("CreateAPIRoute", "/klivechat/rooms", (Func<KliveAPI.KliveAPI.UserRequest, Task>)(async (req) =>
            {
                var id = req.userParameters["id"];
                    if (!string.IsNullOrEmpty(id))
                    {
                        if (ActiveRooms.TryGetValue(id, out var specificRoom))
                        {
                            var response = new 
                            { 
                                roomId = specificRoom.Id, 
                                name = specificRoom.Name, 
                                createdBy = specificRoom.CreatedBy, 
                                userCount = specificRoom.Users.Count 
                            };
                            await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
                        }
                        else
                        {
                            await req.ReturnResponse("Room not found", "text/plain", null, System.Net.HttpStatusCode.NotFound);
                        }
                    }
                    else
                    {
                        var rooms = ActiveRooms.Values.Select(r => new { roomId = r.Id, name = r.Name, createdBy = r.CreatedBy, userCount = r.Users.Count }).ToList();
                        await req.ReturnResponse(JsonConvert.SerializeObject(rooms), "application/json");
                    }
            }), KMPermissions.Anybody);

            // POST /klivechat/create requires Guest or above as per prompt
            await ExecuteServiceMethod<KliveAPI.KliveAPI>("CreateAPIRoute", "/klivechat/create", (Func<KliveAPI.KliveAPI.UserRequest, Task>)(async (req) =>
            {
                    string roomId = Guid.NewGuid().ToString().Substring(0, 8); // Short ID for shareability
                    string roomName = req.userParameters["name"] ?? "KliveChat Room";
                    var room = new KliveChatRoom(roomId, roomName, req.user?.Name ?? "Guest");
                    ActiveRooms.TryAdd(roomId, room);

                    _ = ServiceLog($"Created new room {roomId} by {room.CreatedBy}");
                    var response = new { roomId = roomId, name = roomName, createdBy = room.CreatedBy };
                    await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
            }), KMPermissions.Guest);

            // WebSocket route for connections
            await ExecuteServiceMethod<KliveAPI.KliveAPI>("CreateWebSocketRoute", "/klivechat/ws", (Func<System.Net.HttpListenerContext, WebSocket, System.Collections.Specialized.NameValueCollection, KMProfile?, Task>)(async (context, socket, queryParams, user) =>
            {
                string roomId = queryParams["roomId"];
                if (string.IsNullOrEmpty(roomId) || !ActiveRooms.TryGetValue(roomId, out var room))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid Room ID", CancellationToken.None);
                    return;
                }

                string userName = queryParams["name"] ?? "Guest_" + Guid.NewGuid().ToString().Substring(0, 4);

                var client = new KliveChatClient
                {
                    Name = userName,
                    Socket = socket
                };

                await room.AddClient(client, this);
            }), KMPermissions.Anybody);

            _ = ServiceLog("KliveChatService started. Listening on /klivechat/ endpoints.");
        }
    }
}
