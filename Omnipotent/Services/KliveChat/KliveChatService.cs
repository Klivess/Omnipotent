using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>
    /// Voice/video rooms. Rooms are stored on disk; calls are peer-to-peer WebRTC with this service as the
    /// signalling relay (/klivechat/ws), and the lobby is pushed live over /klivechat/lobby.
    /// </summary>
    public class KliveChatService : OmniService, IKliveChatHost
    {
        /// <summary>A room nobody has been in for this long is removed, unless it is kept (persistent).</summary>
        public static readonly TimeSpan EmptyRoomLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LobbySilenceLimit = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan IceSettingsTtl = TimeSpan.FromMinutes(1);

        private static readonly JsonSerializerSettings WireSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        public ConcurrentDictionary<string, KliveChatRoom> ActiveRooms { get; } = new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<Guid, LobbySubscriber> lobbySubscribers = new();
        private readonly SemaphoreSlim lobbySignal = new(0);
        private readonly SemaphoreSlim iceSettingsLock = new(1, 1);
        private int lobbyDirty;
        private int saveRequested;
        private KliveChatRoomStore? store;
        private KMProfileManager? profileManager;
        private CancellationTokenSource? loops;
        private IceSettings? iceSettings;

        private sealed record IceSettings(DateTime ReadUtc, string? Urls, string? Secret, string? Username, string? Password);

        private sealed class LobbySubscriber
        {
            public LobbySubscriber(KMProfile profile, KliveChatConnection connection)
            {
                Profile = profile;
                Connection = connection;
                LastSeenUtc = DateTime.UtcNow;
            }

            public Guid Id { get; } = Guid.NewGuid();
            public KMProfile Profile { get; }
            public KliveChatConnection Connection { get; }
            public DateTime LastSeenUtc { get; set; }
            public bool Pings { get; set; }
        }

        public KliveChatService()
        {
            name = "KliveChatService";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        DateTime IKliveChatHost.UtcNow => DateTime.UtcNow;

        void IKliveChatHost.Log(string message) => TryLog(message);

        void IKliveChatHost.LogError(Exception error, string context)
        {
            try { _ = ServiceLogError(error, context); } catch { }
        }

        void IKliveChatHost.RoomChanged(KliveChatRoom room, bool persist)
        {
            if (persist) Interlocked.Exchange(ref saveRequested, 1);
            SignalLobby();
        }

        protected override async void ServiceMain()
        {
            loops?.Cancel();
            loops = new CancellationTokenSource();

            store = new KliveChatRoomStore(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveChatRoomsFile));
            var saved = store.Load(out string? problem);
            foreach (var record in saved)
            {
                ActiveRooms.TryAdd(record.Id, new KliveChatRoom(record, this));
            }
            if (problem != null) TryLog($"KliveChat: the saved rooms could not be read: {problem}");

            await RegisterRoutesAsync();

            var token = loops.Token;
            _ = Task.Run(() => MaintenanceLoopAsync(token));
            _ = Task.Run(() => LobbyPushLoopAsync(token));
            TryLog($"KliveChat ready with {ActiveRooms.Count} room(s).");
        }

        // ───────────────────────────── routes ─────────────────────────────

        private async Task RegisterRoutesAsync()
        {
            await Route("/klivechat/rooms", HttpMethod.Get, Perms.Public, async req =>
            {
                // Live, in-memory state: never replay a cached copy (req.Can below would otherwise make it cacheable).
                CacheDeps.MarkUncacheable("klivechat: live room state");
                string? id = req.userParameters["id"];
                if (!string.IsNullOrEmpty(id))
                {
                    if (ActiveRooms.TryGetValue(id, out var room) && !room.IsClosed)
                        await ReturnJsonAsync(req, room.ToSummary(CanManage(req.user, room)));
                    else
                        await req.ReturnResponse("Room not found", "text/plain", null!, HttpStatusCode.NotFound);
                    return;
                }
                // The directory is for signed-in profiles; guests join by link.
                await ReturnJsonAsync(req, req.user == null ? new List<KliveChatRoomSummary>() : ListRoomSummaries(req.user));
            });

            await Route("/klivechat/me", HttpMethod.Get, Perms.Public, async req =>
            {
                CacheDeps.MarkUncacheable("klivechat: caller identity");
                if (req.user == null)
                {
                    await req.ReturnResponse("{}", "application/json", null!, HttpStatusCode.Unauthorized);
                    return;
                }
                await ReturnJsonAsync(req, new
                {
                    name = req.user.Name,
                    rank = (int)req.user.Rank,
                    userId = req.user.UserID,
                    canCreate = AccessEvaluator.Can(req.user, KliveChatPerms.RoomsManage),
                    canModerate = AccessEvaluator.Can(req.user, KliveChatPerms.RoomsModerate),
                    canDeleteAnyRoom = AccessEvaluator.Can(req.user, KliveChatPerms.RoomsDeleteAny),
                });
            });

            await Route("/klivechat/create", HttpMethod.Post, KliveChatPerms.RoomsManage, async req =>
            {
                var result = CreateRoom(req.userParameters["name"], req.user?.Name, req.user?.UserID, ParseBool(req.userParameters["persistent"]) ?? false);
                await SaveRoomsAsync();
                SignalLobby();
                await ReturnJsonAsync(req, result.Room);
            });

            await Route("/klivechat/delete", HttpMethod.Post, KliveChatPerms.RoomsManage, async req =>
            {
                string? roomId = req.userParameters["id"];
                if (string.IsNullOrEmpty(roomId))
                {
                    await req.ReturnResponse("Missing room ID", "text/plain", null!, HttpStatusCode.BadRequest);
                    return;
                }
                var result = await DeleteRoomAsync(roomId, req.user);
                if (result.Success)
                {
                    await ReturnJsonAsync(req, new { success = true, roomId });
                }
                else
                {
                    var status = result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
                    await req.ReturnResponse(result.Message, "text/plain", null!, status);
                }
            });

            await Route("/klivechat/update", HttpMethod.Post, KliveChatPerms.RoomsManage, async req =>
            {
                string? roomId = req.userParameters["id"];
                if (string.IsNullOrEmpty(roomId) || !ActiveRooms.TryGetValue(roomId, out var room) || room.IsClosed)
                {
                    await req.ReturnResponse("Room not found", "text/plain", null!, HttpStatusCode.NotFound);
                    return;
                }
                if (!CanManage(req.user, room))
                {
                    await req.ReturnResponse("Only the room's creator can change it.", "text/plain", null!, HttpStatusCode.Forbidden);
                    return;
                }
                string? rawName = req.userParameters["name"];
                string? newName = rawName == null ? null : KliveChatText.CleanRoomName(rawName);
                if (rawName != null && newName == null)
                {
                    await req.ReturnResponse("A room needs a name.", "text/plain", null!, HttpStatusCode.BadRequest);
                    return;
                }
                if (room.UpdateSettings(newName, ParseBool(req.userParameters["locked"]), ParseBool(req.userParameters["persistent"]), req.user?.Name ?? "Someone"))
                {
                    await SaveRoomsAsync();
                    SignalLobby();
                }
                await ReturnJsonAsync(req, room.ToSummary(true));
            });

            await WebSocketRoute("/klivechat/ws", Perms.Public, async (context, socket, query, user) =>
            {
                string? roomId = query["roomId"];
                if (string.IsNullOrEmpty(roomId) || !ActiveRooms.TryGetValue(roomId, out var room) || room.IsClosed)
                {
                    await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Invalid Room ID");
                    return;
                }
                KMProfile? profile = user ?? await ResolveSocketUserAsync(query["authorization"]);
                var join = BuildJoinRequest(roomId, query, profile, context);
                join.IceServers = await GetIceServersAsync(join.ParticipantId);
                await room.RunConnectionAsync(join, socket);
            });

            await WebSocketRoute("/klivechat/lobby", Perms.SignedIn, async (context, socket, query, user) =>
            {
                KMProfile? profile = user ?? await ResolveSocketUserAsync(query["authorization"]);
                if (profile == null)
                {
                    await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Sign in to see rooms");
                    return;
                }
                await RunLobbySubscriberAsync(profile, socket);
            });
        }

        private async Task Route(string path, HttpMethod method, PermissionDef permission, Func<KliveAPI.KliveAPI.UserRequest, Task> handler)
        {
            try
            {
                await CreateAPIRoute(path, handler, method, permission);
            }
            catch (Exception ex)
            {
                // Route registration fails quietly otherwise, leaving a route that just 404s.
                try { _ = ServiceLogError(ex, $"KliveChat: could not register {path}"); } catch { }
            }
        }

        private async Task WebSocketRoute(string path, PermissionDef permission, Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfile?, Task> handler)
        {
            try
            {
                await ExecuteServiceMethod<KliveAPI.KliveAPI>("CreateWebSocketRoute", path, handler, permission);
            }
            catch (Exception ex)
            {
                try { _ = ServiceLogError(ex, $"KliveChat: could not register the {path} socket"); } catch { }
            }
        }

        private static Task ReturnJsonAsync(KliveAPI.KliveAPI.UserRequest req, object? value) =>
            req.ReturnResponse(JsonConvert.SerializeObject(value, WireSettings), "application/json");

        private static bool? ParseBool(string? value) => bool.TryParse(value, out bool parsed) ? parsed : null;

        // ───────────────────────────── rooms ─────────────────────────────

        /// <summary>Rooms with people in them first (busiest first), then the rest by most recently used.</summary>
        public List<KliveChatRoomSummary> ListRoomSummaries(KMProfile? viewer)
        {
            return ActiveRooms.Values
                .Where(room => !room.IsClosed)
                .Select(room => room.ToSummary(CanManage(viewer, room)))
                .OrderByDescending(summary => summary.UserCount > 0)
                .ThenByDescending(summary => summary.Persistent)
                .ThenByDescending(summary => summary.LastActiveUtc)
                .ThenBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public KliveChatRoomMutationResult CreateRoom(string? requestedName, string? createdBy = null) =>
            CreateRoom(requestedName, createdBy, null, persistent: false);

        public KliveChatRoomMutationResult CreateRoom(string? requestedName, string? createdBy, string? createdByUserId, bool persistent)
        {
            string roomId;
            do roomId = KliveChatText.NewRoomId(); while (ActiveRooms.ContainsKey(roomId));

            DateTime now = DateTime.UtcNow;
            string creator = KliveChatText.CleanDisplayName(createdBy) ?? "Guest";
            var record = new KliveChatRoomRecord
            {
                Id = roomId,
                Name = KliveChatText.CleanRoomName(requestedName) ?? $"{creator}'s room",
                CreatedBy = creator,
                CreatedByUserId = string.IsNullOrWhiteSpace(createdByUserId) ? null : createdByUserId,
                CreatedUtc = now,
                LastActiveUtc = now,
                Persistent = persistent,
            };
            var room = new KliveChatRoom(record, this);
            ActiveRooms[roomId] = room;
            TryLog($"{creator} created room {roomId} ({record.Name}).");

            return new KliveChatRoomMutationResult
            {
                Success = true,
                Message = $"Created room {roomId}.",
                Room = room.ToSummary(canManage: true),
            };
        }

        public async Task<KliveChatRoomMutationResult> DeleteRoomAsync(string roomId, KMProfile? requestedBy)
        {
            if (!ActiveRooms.TryGetValue(roomId, out var room))
            {
                return new KliveChatRoomMutationResult { Success = false, Message = "Room not found" };
            }
            if (!CanManage(requestedBy, room))
            {
                return new KliveChatRoomMutationResult { Success = false, Message = "Only the room's creator can delete it." };
            }

            var summary = room.ToSummary(canManage: true);
            ActiveRooms.TryRemove(roomId, out _);
            await room.CloseAsync("Room deleted");
            await SaveRoomsAsync();
            SignalLobby();
            TryLog($"Room {roomId} ({summary.Name}) deleted by {requestedBy?.Name ?? "someone"}.");
            return new KliveChatRoomMutationResult { Success = true, Message = $"Deleted room {roomId}.", Room = summary };
        }

        /// <summary>Its creator, or anyone holding klivechat.rooms.delete-any, may rename, keep or delete a room.</summary>
        private static bool CanManage(KMProfile? profile, KliveChatRoom room) =>
            profile != null && (AccessEvaluator.Can(profile, KliveChatPerms.RoomsDeleteAny) || room.IsCreator(profile.UserID, profile.Name));

        private async Task SaveRoomsAsync()
        {
            if (store == null) return;
            Interlocked.Exchange(ref saveRequested, 0);
            try
            {
                var records = ActiveRooms.Values
                    .Where(room => !room.IsClosed)
                    .Select(room => room.SnapshotRecord())
                    .OrderBy(record => record.CreatedUtc)
                    .ToList();
                await store.SaveAsync(records);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref saveRequested, 1);
                try { _ = ServiceLogError(ex, "KliveChat: could not save rooms"); } catch { }
            }
        }

        private async Task MaintenanceLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(MaintenanceInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    try
                    {
                        DateTime now = DateTime.UtcNow;
                        foreach (var room in ActiveRooms.Values) room.Sweep();
                        foreach (var room in ActiveRooms.Values)
                        {
                            if (room.TryExpire(now, EmptyRoomLifetime) && ActiveRooms.TryRemove(room.Id, out _))
                            {
                                TryLog($"Room {room.Id} ({room.Name}) expired after {EmptyRoomLifetime.TotalHours:0} hours empty.");
                                Interlocked.Exchange(ref saveRequested, 1);
                                SignalLobby();
                            }
                        }
                        SweepLobbySubscribers(now);
                        if (Volatile.Read(ref saveRequested) == 1) await SaveRoomsAsync();
                    }
                    catch (Exception ex)
                    {
                        try { _ = ServiceLogError(ex, "KliveChat maintenance pass failed"); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ───────────────────────────── joining ─────────────────────────────

        private static KliveChatJoinRequest BuildJoinRequest(string roomId, NameValueCollection query, KMProfile? profile, HttpListenerContext context)
        {
            string? secret = query["secret"];
            bool resumable = KliveChatText.IsValidSecret(secret);
            string ip = context.Request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
            string? guestIdentity = KliveChatText.CleanLine(query["guestIdentity"], 96);
            return new KliveChatJoinRequest
            {
                ParticipantId = resumable ? KliveChatText.ParticipantIdFromSecret(roomId, secret!) : KliveChatText.RandomParticipantId(),
                Resumable = resumable,
                Name = profile != null && !string.IsNullOrWhiteSpace(profile.Name)
                    ? profile.Name.Trim()
                    : KliveChatText.CleanDisplayName(query["name"]) ?? "Guest",
                UserId = profile?.UserID,
                Rank = profile?.Rank ?? ProfileRank.None,
                HoldsModeratePermission = profile != null && AccessEvaluator.Can(profile, KliveChatPerms.RoomsModerate),
                HoldsDeleteAnyPermission = profile != null && AccessEvaluator.Can(profile, KliveChatPerms.RoomsDeleteAny),
                IdentityKey = profile != null
                    ? $"profile:{profile.UserID}"
                    : $"guest:{(guestIdentity ?? $"{ip}|{context.Request.UserAgent}").ToLowerInvariant()}",
                IpAddress = string.IsNullOrEmpty(ip) ? null : ip,
                Protocol = int.TryParse(query["protocol"], out int protocol) ? Math.Clamp(protocol, 1, KliveChatRoom.ProtocolVersion) : 1,
            };
        }

        private async Task<List<KliveChatIceServer>> GetIceServersAsync(string participantId)
        {
            var settings = iceSettings;
            if (settings == null || DateTime.UtcNow - settings.ReadUtc > IceSettingsTtl)
            {
                await iceSettingsLock.WaitAsync();
                try
                {
                    settings = iceSettings;
                    if (settings == null || DateTime.UtcNow - settings.ReadUtc > IceSettingsTtl)
                    {
                        settings = await ReadIceSettingsAsync();
                        iceSettings = settings;
                    }
                }
                finally
                {
                    iceSettingsLock.Release();
                }
            }
            return KliveChatIce.Build(settings.Urls, settings.Secret, settings.Username, settings.Password, participantId, DateTime.UtcNow);
        }

        private async Task<IceSettings> ReadIceSettingsAsync()
        {
            try
            {
                var read = Task.WhenAll(
                    GetStringOmniSetting("KliveChat_TurnUrls", ""),
                    GetStringOmniSetting("KliveChat_TurnSecret", "", sensitive: true),
                    GetStringOmniSetting("KliveChat_TurnUsername", ""),
                    GetStringOmniSetting("KliveChat_TurnPassword", "", sensitive: true));
                // A slow settings service must never hold a join up; STUN-only still works for most networks.
                string[] values = await read.WaitAsync(TimeSpan.FromSeconds(3));
                return new IceSettings(DateTime.UtcNow, values[0], values[1], values[2], values[3]);
            }
            catch (Exception ex)
            {
                try { _ = ServiceLogError(ex, "KliveChat: could not read the TURN settings; using STUN only"); } catch { }
                return new IceSettings(DateTime.UtcNow, null, null, null, null);
            }
        }

        private async Task<KMProfile?> ResolveSocketUserAsync(string? authorization)
        {
            if (string.IsNullOrWhiteSpace(authorization)) return null;
            if (profileManager == null)
            {
                var services = await GetServicesByType<KMProfileManager>();
                if (services != null && services.Any()) profileManager = (KMProfileManager)services[0];
            }
            return profileManager == null ? null : await profileManager.GetProfileByPassword(authorization);
        }

        private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(status, reason, cts.Token);
            }
            catch
            {
                try { socket.Abort(); } catch { }
            }
        }

        // ───────────────────────────── lobby ─────────────────────────────

        private void SignalLobby()
        {
            if (Interlocked.Exchange(ref lobbyDirty, 1) == 0) lobbySignal.Release();
        }

        private byte[] LobbySnapshot(KMProfile viewer) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
        {
            type = "rooms",
            rooms = ListRoomSummaries(viewer),
            serverTime = DateTime.UtcNow,
        }, WireSettings));

        private async Task RunLobbySubscriberAsync(KMProfile profile, WebSocket socket)
        {
            var connection = new KliveChatConnection(socket);
            var subscriber = new LobbySubscriber(profile, connection);
            lobbySubscribers[subscriber.Id] = subscriber;
            connection.Start();
            connection.TryEnqueue(LobbySnapshot(profile));
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    subscriber.LastSeenUtc = DateTime.UtcNow;
                    if (result.EndOfMessage && result.Count < buffer.Length && Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("\"ping\"", StringComparison.Ordinal))
                    {
                        subscriber.Pings = true;
                        connection.TryEnqueue(Encoding.UTF8.GetBytes("{\"type\":\"pong\"}"));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                lobbySubscribers.TryRemove(subscriber.Id, out _);
                connection.Abort();
            }
        }

        private void SweepLobbySubscribers(DateTime now)
        {
            foreach (var subscriber in lobbySubscribers.Values)
            {
                if (subscriber.Pings && now - subscriber.LastSeenUtc > LobbySilenceLimit) subscriber.Connection.Abort();
            }
        }

        private async Task LobbyPushLoopAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await lobbySignal.WaitAsync(token);
                    // A join changes several things at once; send one snapshot for the lot.
                    await Task.Delay(250, token);
                    Interlocked.Exchange(ref lobbyDirty, 0);
                    foreach (var subscriber in lobbySubscribers.Values)
                    {
                        try { subscriber.Connection.TryEnqueue(LobbySnapshot(subscriber.Profile)); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void TryLog(string message)
        {
            try { _ = ServiceLog(message); } catch { }
        }
    }
}
