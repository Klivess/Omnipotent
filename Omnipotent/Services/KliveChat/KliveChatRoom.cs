using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.WebSockets;
using System.Text;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>What a room needs from the service that owns it (a fake in tests).</summary>
    public interface IKliveChatHost
    {
        DateTime UtcNow { get; }
        void Log(string message);
        void LogError(Exception error, string context);
        /// <summary>Membership, media state or settings changed. <paramref name="persist"/>: the stored record changed too.</summary>
        void RoomChanged(KliveChatRoom room, bool persist);
    }

    /// <summary>
    /// A call: its seats, its chat and the signalling relay between peers. Media is peer-to-peer; the
    /// server only relays offers, answers and ICE candidates, and keeps everyone's view of the room in step.
    ///
    /// All state changes happen under one lock and only ever enqueue frames (never await a socket), which
    /// gives every client the same order of events — a join is always announced before any signalling from
    /// the joiner — and means one slow client can't hold the room up.
    /// </summary>
    public sealed class KliveChatRoom
    {
        public const int ProtocolVersion = 2;
        public const int MaxParticipants = 12;
        public const int ChatHistoryLimit = 200;
        public const int MaxInboundMessageBytes = 64 * 1024;
        public const int ChatBurst = 8;
        public const int MaxBannedKeys = 500;
        public static readonly TimeSpan ChatWindow = TimeSpan.FromSeconds(10);
        /// <summary>A protocol-2 client pings every 10 seconds; silence for this long means the connection is dead.</summary>
        public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
        /// <summary>How long a dropped participant's seat is held for a reconnect before the room says they left.</summary>
        public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(12);

        private static readonly JsonSerializerSettings WireSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private readonly object gate = new();
        private readonly IKliveChatHost host;
        private readonly KliveChatRoomRecord record;
        private readonly Dictionary<string, KliveChatParticipant> participants = new(StringComparer.Ordinal);
        private readonly LinkedList<KliveChatChatMessage> chat = new();
        private bool closed;

        public KliveChatRoom(KliveChatRoomRecord record, IKliveChatHost host)
        {
            this.record = record;
            this.host = host;
        }

        public string Id => record.Id;

        public string Name
        {
            get { lock (gate) return record.Name; }
        }

        public int ParticipantCount
        {
            get { lock (gate) return participants.Count; }
        }

        public bool IsClosed
        {
            get { lock (gate) return closed; }
        }

        public KliveChatRoomRecord SnapshotRecord()
        {
            lock (gate) return record.Clone();
        }

        public List<KliveChatParticipantSummary> SnapshotParticipants()
        {
            lock (gate) return participants.Values.OrderBy(p => p.JoinedUtc).Select(SummaryLocked).ToList();
        }

        public List<KliveChatChatMessage> SnapshotChat()
        {
            lock (gate) return chat.ToList();
        }

        /// <summary>Whether this profile created the room (rooms from before creator ids were stored match on the profile name).</summary>
        public bool IsCreator(string? userId, string? profileName)
        {
            lock (gate) return IsCreatorLocked(userId, profileName);
        }

        public KliveChatRoomSummary ToSummary(bool canManage)
        {
            lock (gate)
            {
                return new KliveChatRoomSummary
                {
                    RoomId = record.Id,
                    Name = record.Name,
                    CreatedBy = record.CreatedBy,
                    CreatedUtc = record.CreatedUtc,
                    LastActiveUtc = record.LastActiveUtc,
                    Persistent = record.Persistent,
                    Locked = record.Locked,
                    UserCount = participants.Count,
                    Participants = participants.Values.OrderBy(p => p.JoinedUtc).Select(p => new KliveChatLobbyParticipant
                    {
                        Id = p.Id,
                        Name = p.Name,
                        IsGuest = p.IsGuest,
                        IsMuted = p.IsMuted,
                        HasVideo = p.HasVideo,
                        IsScreenSharing = p.IsScreenSharing,
                        Connected = p.Connection != null,
                    }).ToList(),
                    RoomPath = $"/shared/klivechat/{record.Id}",
                    CanManage = canManage,
                };
            }
        }

        // ───────────────────────────── joining and leaving ─────────────────────────────

        /// <summary>Admits the socket and serves it until it closes. Returns when the connection is over.</summary>
        public async Task RunConnectionAsync(KliveChatJoinRequest join, WebSocket socket)
        {
            var connection = new KliveChatConnection(socket);
            var replaced = new List<KliveChatConnection>();
            KliveChatParticipant? participant;
            string? refusal;
            bool resumed;
            lock (gate)
            {
                participant = AdmitLocked(join, connection, replaced, out refusal, out resumed);
            }
            foreach (var old in replaced) _ = old.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Replaced", forRemoval: true);

            if (participant == null)
            {
                await RefuseAsync(socket, refusal ?? "Refused");
                return;
            }

            host.RoomChanged(this, persist: true);
            if (!resumed) host.Log($"{participant.Name} joined room {record.Id}.");

            bool clientClosed = false;
            try
            {
                clientClosed = await ReceiveLoopAsync(participant, connection);
            }
            finally
            {
                Detach(participant, connection, clientClosed);
            }
        }

        private KliveChatParticipant? AdmitLocked(KliveChatJoinRequest join, KliveChatConnection connection, List<KliveChatConnection> replaced,
            out string? refusal, out bool resumed)
        {
            refusal = null;
            resumed = false;
            DateTime now = host.UtcNow;
            if (closed)
            {
                refusal = "Invalid Room ID";
                return null;
            }
            if (IsBannedLocked(join))
            {
                refusal = "Banned from room";
                return null;
            }

            if (participants.TryGetValue(join.ParticipantId, out var participant))
            {
                // The same tab coming back (network blip, server restart, or a duplicated tab): it takes its seat back.
                if (!string.Equals(participant.IdentityKey, join.IdentityKey, StringComparison.Ordinal))
                {
                    refusal = "Session conflict";
                    return null;
                }
                if (participant.Connection != null) replaced.Add(participant.Connection);
                participant.Refresh(join);
                if (participant.IsGuest) participant.Name = UniqueNameLocked(join.Name, participant.Id);
                resumed = true;
            }
            else
            {
                bool privileged = join.HoldsModeratePermission || join.HoldsDeleteAnyPermission || IsCreatorLocked(join.UserId, join.Name);
                if (record.Locked && !privileged)
                {
                    refusal = "Room locked";
                    return null;
                }
                if (participants.Count >= MaxParticipants)
                {
                    refusal = "Room full";
                    return null;
                }
                participant = new KliveChatParticipant(join, join.IsGuest ? UniqueNameLocked(join.Name, join.ParticipantId) : join.Name, now);
                participants[participant.Id] = participant;
            }

            participant.Connection = connection;
            participant.DisconnectedUtc = null;
            participant.LastSeenUtc = now;
            record.LastActiveUtc = now;
            connection.Start();

            SendLocked(participant, RoomInfoLocked(participant, join.IceServers, resumed));
            // Sent for a resume too: peers upsert, and anyone who already gave up on this seat reconnects to it.
            BroadcastLocked(new KliveChatMessage { Type = "user-joined", SenderId = participant.Id, Payload = SummaryLocked(participant) }, except: participant.Id);
            return participant;
        }

        private static async Task RefuseAsync(WebSocket socket, string reason)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
            }
            catch
            {
                try { socket.Abort(); } catch { }
            }
        }

        /// <summary>The socket ended. A clean hang-up leaves the room; a dropped resumable connection holds its seat.</summary>
        private void Detach(KliveChatParticipant participant, KliveChatConnection connection, bool clientClosed)
        {
            bool changed = false;
            lock (gate)
            {
                // Replaced by a newer socket, or already removed (kicked, banned, room closed).
                if (participant.Connection == connection)
                {
                    if (clientClosed || !participant.Resumable || closed)
                    {
                        RemoveLocked(participant, UserLeft(participant), tellTarget: false);
                        host.Log($"{participant.Name} left room {record.Id}.");
                    }
                    else
                    {
                        participant.Connection = null;
                        participant.DisconnectedUtc = host.UtcNow;
                        BroadcastLocked(ParticipantState(participant), except: participant.Id);
                    }
                    changed = true;
                }
            }
            connection.Abort();
            if (changed) host.RoomChanged(this, persist: true);
        }

        private static KliveChatMessage UserLeft(KliveChatParticipant p) =>
            new() { Type = "user-left", SenderId = p.Id, Payload = new { id = p.Id } };

        /// <summary>
        /// Takes a seat out of the room and tells everyone (the target too, first, when it should hear why).
        /// Returns the seat's connection for the caller to close outside the lock.
        /// </summary>
        private KliveChatConnection? RemoveLocked(KliveChatParticipant participant, KliveChatMessage notice, bool tellTarget)
        {
            if (tellTarget) SendLocked(participant, notice);
            participants.Remove(participant.Id);
            BroadcastLocked(notice);
            var connection = participant.Connection;
            participant.Connection = null;
            participant.DisconnectedUtc = null;
            record.LastActiveUtc = host.UtcNow;
            return connection;
        }

        /// <summary>
        /// Run every few seconds: drops connections that stopped answering (their seat enters the grace period)
        /// and frees seats whose grace period ran out.
        /// </summary>
        public void Sweep()
        {
            List<KliveChatConnection>? silent = null;
            bool changed = false;
            lock (gate)
            {
                DateTime now = host.UtcNow;
                foreach (var participant in participants.Values.ToList())
                {
                    if (participant.Connection != null)
                    {
                        // Only protocol-2 clients ping; an older page would be dropped for being quiet.
                        if (participant.Protocol >= ProtocolVersion && now - participant.LastSeenUtc > HeartbeatTimeout)
                            (silent ??= new()).Add(participant.Connection);
                    }
                    else if (participant.DisconnectedUtc is DateTime since && now - since > ReconnectGrace)
                    {
                        RemoveLocked(participant, UserLeft(participant), tellTarget: false);
                        host.Log($"{participant.Name} dropped out of room {record.Id}.");
                        changed = true;
                    }
                }
            }
            if (silent != null)
            {
                foreach (var connection in silent) connection.Abort();
            }
            if (changed) host.RoomChanged(this, persist: true);
        }

        /// <summary>Closes the room for good (deleted, or expired); everyone in it is disconnected with <paramref name="reason"/>.</summary>
        public async Task CloseAsync(string reason)
        {
            var connections = new List<KliveChatConnection>();
            lock (gate)
            {
                closed = true;
                foreach (var participant in participants.Values)
                {
                    if (participant.Connection != null) connections.Add(participant.Connection);
                    participant.Connection = null;
                }
                participants.Clear();
            }
            await Task.WhenAll(connections.Select(c => c.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, forRemoval: true)));
        }

        /// <summary>Atomically closes an empty, non-persistent room that has been idle for <paramref name="emptyLifetime"/>.</summary>
        public bool TryExpire(DateTime nowUtc, TimeSpan emptyLifetime)
        {
            lock (gate)
            {
                if (closed || record.Persistent || participants.Count > 0 || nowUtc - record.LastActiveUtc < emptyLifetime) return false;
                closed = true;
                return true;
            }
        }

        // ───────────────────────────── settings ─────────────────────────────

        /// <summary>Applies a change made outside the call (the lobby). Returns whether anything changed.</summary>
        public bool UpdateSettings(string? name, bool? locked, bool? persistent, string actorName)
        {
            lock (gate)
            {
                bool changed = false;
                if (name != null && name != record.Name)
                {
                    record.Name = name;
                    changed = true;
                }
                if (locked is bool l && l != record.Locked)
                {
                    record.Locked = l;
                    changed = true;
                }
                if (persistent is bool p && p != record.Persistent)
                {
                    record.Persistent = p;
                    changed = true;
                }
                if (changed) BroadcastLocked(RoomUpdatedLocked(actorName));
                return changed;
            }
        }

        // ───────────────────────────── receiving ─────────────────────────────

        /// <returns>True when the client hung up (sent a close frame); false for a dropped or server-ended connection.</returns>
        private async Task<bool> ReceiveLoopAsync(KliveChatParticipant participant, KliveChatConnection connection)
        {
            var socket = connection.Socket;
            byte[] buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (true)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return !connection.IsClosing;

                    if (message.Length + result.Count > MaxInboundMessageBytes)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", forRemoval: false);
                        return false;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        HandleInbound(participant, connection, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                    }
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (WebSocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void HandleInbound(KliveChatParticipant sender, KliveChatConnection connection, string json)
        {
            JObject message;
            try
            {
                message = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            string type = message["type"] is JValue { Type: JTokenType.String } typeToken ? typeToken.Value<string>() ?? string.Empty : string.Empty;
            string? targetId = message["targetId"] is JValue { Type: JTokenType.String } targetToken ? targetToken.Value<string>() : null;
            JToken? payload = message["payload"];
            List<KliveChatConnection>? removed = null;
            string removalReason = string.Empty;
            bool changed = false;
            bool persist = false;

            try
            {
                lock (gate)
                {
                    // Late frames from a socket that has since been replaced or removed.
                    if (sender.Connection != connection) return;
                    sender.LastSeenUtc = host.UtcNow;

                    switch (type)
                    {
                        case "ping":
                            SendLocked(sender, new KliveChatMessage { Type = "pong", Payload = new { t = (payload as JObject)?["t"], serverTime = host.UtcNow } });
                            break;
                        case "offer":
                        case "answer":
                        case "ice-candidate":
                        case "renegotiate":
                            RelayLocked(sender, type, targetId, payload);
                            break;
                        case "media-state":
                            changed = ApplyMediaStateLocked(sender, payload);
                            break;
                        case "chat-message":
                            ChatLocked(sender, payload);
                            break;
                        case "moderate-participant":
                            (changed, persist, removalReason) = ModerateLocked(sender, payload, ref removed);
                            break;
                        case "room-settings":
                            changed = persist = ApplySettingsLocked(sender, payload);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                host.LogError(ex, $"KliveChat: could not handle a '{type}' message in room {record.Id}");
                return;
            }

            if (removed != null)
            {
                foreach (var c in removed) _ = c.CloseAsync(WebSocketCloseStatus.PolicyViolation, removalReason, forRemoval: true);
            }
            if (changed) host.RoomChanged(this, persist);
        }

        /// <summary>Offers, answers and ICE candidates go to their target only, stamped with the real sender.</summary>
        private void RelayLocked(KliveChatParticipant sender, string type, string? targetId, JToken? payload)
        {
            if (string.IsNullOrEmpty(targetId) || targetId == sender.Id) return;
            if (!participants.TryGetValue(targetId, out var target)) return;
            SendLocked(target, new KliveChatMessage { Type = type, SenderId = sender.Id, TargetId = targetId, Payload = payload });
        }

        private bool ApplyMediaStateLocked(KliveChatParticipant participant, JToken? payload)
        {
            if (payload is not JObject state)
            {
                SendErrorLocked(participant, "Invalid media state update.", "invalid");
                return false;
            }
            participant.IsMuted = Flag(state, "isMuted", participant.IsMuted);
            participant.HasVideo = Flag(state, "hasVideo", participant.HasVideo);
            participant.IsScreenSharing = Flag(state, "isScreenSharing", participant.IsScreenSharing);
            participant.HasScreenAudio = participant.IsScreenSharing && Flag(state, "hasScreenAudio", participant.HasScreenAudio);
            BroadcastLocked(ParticipantState(participant), except: participant.Id);
            return true;
        }

        private static bool Flag(JObject source, string key, bool fallback) =>
            source.TryGetValue(key, out var token) && token.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;

        /// <summary>A string field, or null when it is missing or not a string.</summary>
        private static string? Str(JToken? source, string key) =>
            source is JObject obj && obj[key] is JValue { Type: JTokenType.String } value ? value.Value<string>() : null;

        private void ChatLocked(KliveChatParticipant sender, JToken? payload)
        {
            string? raw = payload is JValue { Type: JTokenType.String } plain ? plain.Value<string>() : Str(payload, "text");
            string? text = KliveChatText.CleanChat(raw);
            if (text == null) return;
            if (!sender.AllowChat(host.UtcNow, ChatBurst, ChatWindow))
            {
                SendErrorLocked(sender, "Slow down: too many messages.", "rate-limited");
                return;
            }

            var entry = new KliveChatChatMessage
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                SenderId = sender.Id,
                SenderName = sender.Name,
                SenderIsGuest = sender.IsGuest,
                Text = text,
                Ts = host.UtcNow,
                ClientMsgId = KliveChatText.CleanLine(Str(payload, "clientMsgId"), 64),
            };
            chat.AddLast(entry);
            while (chat.Count > ChatHistoryLimit) chat.RemoveFirst();
            BroadcastLocked(new KliveChatMessage { Type = "chat-message", SenderId = sender.Id, Payload = entry });
        }

        private (bool changed, bool persist, string reason) ModerateLocked(KliveChatParticipant actor, JToken? payload, ref List<KliveChatConnection>? removed)
        {
            if (!CanModerateLocked(actor))
            {
                SendErrorLocked(actor, "Only the host or a moderator can do that.", "forbidden");
                return default;
            }
            string? targetId = Str(payload, "targetId");
            string action = (Str(payload, "action") ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(targetId) || !participants.TryGetValue(targetId, out var target))
            {
                SendErrorLocked(actor, "They're no longer in the room.", "not-found");
                return default;
            }
            if (target.Id == actor.Id)
            {
                SendErrorLocked(actor, "You can't moderate yourself.", "invalid");
                return default;
            }
            if (!OutranksLocked(actor, target))
            {
                SendErrorLocked(actor, $"{target.Name} is ranked the same as or above you.", "forbidden");
                return default;
            }

            switch (action)
            {
                case "mute":
                    target.IsMuted = true;
                    SendLocked(target, new KliveChatMessage { Type = "force-mute", SenderId = actor.Id, Payload = new { byId = actor.Id, byName = actor.Name } });
                    BroadcastLocked(ParticipantState(target), except: target.Id);
                    host.Log($"{actor.Name} muted {target.Name} in room {record.Id}.");
                    return (true, false, string.Empty);

                case "kick":
                case "ban":
                    bool ban = action == "ban";
                    if (ban) AddBanLocked(target);
                    var notice = new KliveChatMessage
                    {
                        Type = "participant-removed",
                        SenderId = actor.Id,
                        Payload = new KliveChatParticipantRemoval { Id = target.Id, Action = action, ById = actor.Id, ByName = actor.Name },
                    };
                    var connection = RemoveLocked(target, notice, tellTarget: true);
                    if (connection != null) (removed ??= new()).Add(connection);
                    host.Log($"{actor.Name} {(ban ? "banned" : "removed")} {target.Name} from room {record.Id}.");
                    return (true, ban, ban ? "Banned from room" : "Removed from room");

                default:
                    SendErrorLocked(actor, "Unsupported moderation action.", "invalid");
                    return default;
            }
        }

        private bool ApplySettingsLocked(KliveChatParticipant actor, JToken? payload)
        {
            if (payload is not JObject settings) return false;
            bool? locked = settings.TryGetValue("locked", out var l) && l.Type == JTokenType.Boolean ? l.Value<bool>() : null;
            string? name = KliveChatText.CleanRoomName(Str(settings, "name"));

            if (locked != null && !CanModerateLocked(actor))
            {
                SendErrorLocked(actor, "Only the host or a moderator can lock the room.", "forbidden");
                return false;
            }
            if (name != null && !CanManageLocked(actor))
            {
                SendErrorLocked(actor, "Only the host can rename the room.", "forbidden");
                return false;
            }

            bool changed = false;
            if (locked is bool lockedValue && lockedValue != record.Locked)
            {
                record.Locked = lockedValue;
                changed = true;
            }
            if (name != null && name != record.Name)
            {
                record.Name = name;
                changed = true;
            }
            if (changed)
            {
                BroadcastLocked(RoomUpdatedLocked(actor.Name));
                host.Log($"{actor.Name} updated room {record.Id} (locked: {record.Locked}, name: {record.Name}).");
            }
            return changed;
        }

        // ───────────────────────────── rules ─────────────────────────────

        private bool IsCreatorLocked(string? userId, string? profileName)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            if (!string.IsNullOrWhiteSpace(record.CreatedByUserId)) return string.Equals(record.CreatedByUserId, userId, StringComparison.Ordinal);
            return !string.IsNullOrWhiteSpace(profileName) && string.Equals(record.CreatedBy, profileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The room's creator moderates their own room: they could delete it (removing everyone), so removing one
        /// person is the smaller power. Everyone else needs klivechat.rooms.moderate.
        /// </summary>
        private bool CanModerateLocked(KliveChatParticipant p) => p.HoldsModeratePermission || IsCreatorLocked(p.UserId, p.Name);

        private bool CanManageLocked(KliveChatParticipant p) => p.HoldsDeleteAnyPermission || IsCreatorLocked(p.UserId, p.Name);

        /// <summary>Anonymous guests can always be moderated; profiles only by someone ranked above them.</summary>
        private static bool OutranksLocked(KliveChatParticipant actor, KliveChatParticipant target) => target.IsGuest || actor.Rank > target.Rank;

        private bool IsBannedLocked(KliveChatJoinRequest join)
        {
            if (record.BannedKeys.Count == 0) return false;
            if (record.BannedKeys.Contains(join.IdentityKey, StringComparer.Ordinal)) return true;
            // Clearing site data mints a new guest identity, so guests are also held to the address they were banned from.
            return join.IsGuest && !string.IsNullOrEmpty(join.IpAddress) && record.BannedKeys.Contains("ip:" + join.IpAddress, StringComparer.Ordinal);
        }

        private void AddBanLocked(KliveChatParticipant target)
        {
            void Add(string key)
            {
                if (!record.BannedKeys.Contains(key, StringComparer.Ordinal)) record.BannedKeys.Add(key);
            }
            Add(target.IdentityKey);
            if (target.IsGuest && !string.IsNullOrEmpty(target.IpAddress)) Add("ip:" + target.IpAddress);
            while (record.BannedKeys.Count > MaxBannedKeys) record.BannedKeys.RemoveAt(0);
        }

        /// <summary>Guests pick their own names, so a clash gets a number rather than two identical people.</summary>
        private string UniqueNameLocked(string desired, string selfId)
        {
            string name = string.IsNullOrWhiteSpace(desired) ? "Guest" : desired;
            bool Taken(string candidate) => participants.Values.Any(p => p.Id != selfId && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (!Taken(name)) return name;
            for (int n = 2; n < 100; n++)
            {
                string suffix = $" {n}";
                string candidate = KliveChatText.Truncate(name, KliveChatText.MaxDisplayNameLength - suffix.Length) + suffix;
                if (!Taken(candidate)) return candidate;
            }
            return name;
        }

        // ───────────────────────────── messages ─────────────────────────────

        private KliveChatParticipantSummary SummaryLocked(KliveChatParticipant p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Rank = (int)p.Rank,
            IsGuest = p.IsGuest,
            IsMuted = p.IsMuted,
            HasVideo = p.HasVideo,
            IsScreenSharing = p.IsScreenSharing,
            HasScreenAudio = p.HasScreenAudio,
            CanModerate = CanModerateLocked(p),
            IsHost = IsCreatorLocked(p.UserId, p.Name),
            Connected = p.Connection != null,
            JoinedUtc = p.JoinedUtc,
        };

        private KliveChatMessage ParticipantState(KliveChatParticipant p) =>
            new() { Type = "participant-state", SenderId = p.Id, Payload = SummaryLocked(p) };

        private KliveChatRoomState RoomStateLocked() => new()
        {
            Id = record.Id,
            Name = record.Name,
            CreatedBy = record.CreatedBy,
            CreatedUtc = record.CreatedUtc,
            Persistent = record.Persistent,
            Locked = record.Locked,
        };

        private KliveChatMessage RoomUpdatedLocked(string byName) =>
            new() { Type = "room-updated", Payload = new { room = RoomStateLocked(), byName } };

        private KliveChatMessage RoomInfoLocked(KliveChatParticipant self, List<KliveChatIceServer> iceServers, bool resumed) => new()
        {
            Type = "room-info",
            Payload = new
            {
                protocol = ProtocolVersion,
                // Protocol-1 field names, kept so a page from before this version still joins.
                roomId = record.Id,
                roomName = record.Name,
                clientId = self.Id,
                users = participants.Values.Where(p => p.Id != self.Id).OrderBy(p => p.JoinedUtc).Select(SummaryLocked).ToList(),
                resumed,
                room = RoomStateLocked(),
                self = SummaryLocked(self),
                chat = chat.ToList(),
                iceServers,
                serverTime = host.UtcNow,
                limits = new
                {
                    maxParticipants = MaxParticipants,
                    maxChatLength = KliveChatText.MaxChatLength,
                    heartbeatSeconds = 10,
                },
            },
        };

        private void SendErrorLocked(KliveChatParticipant p, string message, string code) =>
            SendLocked(p, new KliveChatMessage { Type = "room-error", Payload = new { message, code } });

        private void SendLocked(KliveChatParticipant p, KliveChatMessage message) => EnqueueLocked(p, Serialize(message));

        private void BroadcastLocked(KliveChatMessage message, string? except = null)
        {
            byte[] frame = Serialize(message);
            foreach (var participant in participants.Values)
            {
                if (participant.Id != except) EnqueueLocked(participant, frame);
            }
        }

        private void EnqueueLocked(KliveChatParticipant participant, byte[] frame)
        {
            var connection = participant.Connection;
            if (connection == null || connection.TryEnqueue(frame) || connection.IsClosing) return;
            // A full queue means the client stopped reading: drop the socket (its seat is held for a reconnect).
            host.Log($"KliveChat: {participant.Name} in room {record.Id} fell too far behind; dropping its connection.");
            connection.Abort();
        }

        private static byte[] Serialize(KliveChatMessage message) =>
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, WireSettings));
    }
}
