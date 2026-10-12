using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveChat;

namespace Omnipotent.Tests.KliveChat;

public class KliveChatRoomTests
{
    private readonly FakeChatHost host = new();

    private KliveChatRoom NewRoom(Action<KliveChatRoomRecord>? configure = null)
    {
        var record = new KliveChatRoomRecord
        {
            Id = "room1abcde",
            Name = "General",
            CreatedBy = "Klives",
            CreatedByUserId = "klives",
            CreatedUtc = host.Now,
            LastActiveUtc = host.Now,
        };
        configure?.Invoke(record);
        return new KliveChatRoom(record, host);
    }

    private static KliveChatJoinRequest Guest(string name, string? secret = null, string ip = "10.0.0.9") => new()
    {
        ParticipantId = secret != null ? KliveChatText.ParticipantIdFromSecret("room1abcde", secret) : KliveChatText.RandomParticipantId(),
        Resumable = secret != null,
        Name = name,
        IdentityKey = $"guest:{name.ToLowerInvariant()}-browser",
        IpAddress = ip,
        Protocol = 2,
    };

    private static KliveChatJoinRequest Member(string userId, string name, ProfileRank rank, bool moderate = false, string? secret = null) => new()
    {
        ParticipantId = KliveChatText.ParticipantIdFromSecret("room1abcde", secret ?? $"{userId}-secret-0123456789"),
        Resumable = true,
        Name = name,
        UserId = userId,
        Rank = rank,
        HoldsModeratePermission = moderate,
        IdentityKey = $"profile:{userId}",
        IpAddress = "10.0.0.1",
        Protocol = 2,
    };

    private sealed record Seat(FakeWebSocket Socket, Task Run, JObject Info)
    {
        public string Id => (string)Info["payload"]!["clientId"]!;
    }

    private static async Task<Seat> JoinAsync(KliveChatRoom room, KliveChatJoinRequest join)
    {
        var socket = new FakeWebSocket();
        var run = room.RunConnectionAsync(join, socket);
        var info = await socket.ExpectAsync("room-info");
        return new Seat(socket, run, info);
    }

    private static async Task<FakeWebSocket> RefusedAsync(KliveChatRoom room, KliveChatJoinRequest join)
    {
        var socket = new FakeWebSocket();
        await room.RunConnectionAsync(join, socket).WaitAsync(TimeSpan.FromSeconds(3));
        return socket;
    }

    [Fact]
    public async Task Join_SendsRoomInfo_AndAnnouncesTheNewcomerToEveryoneElse()
    {
        var room = NewRoom();
        var join = Member("klives", "Klives", ProfileRank.Klives);
        join.IceServers = KliveChatIce.Build(null, null, null, null, join.ParticipantId, host.Now);
        var klives = await JoinAsync(room, join);
        var payload = klives.Info["payload"]!;
        Assert.Equal(2, (int)payload["protocol"]!);
        Assert.Equal("General", (string)payload["roomName"]!);
        Assert.Equal(klives.Id, (string)payload["self"]!["id"]!);
        Assert.Empty((JArray)payload["users"]!);
        Assert.True((bool)payload["self"]!["isHost"]!);
        Assert.Equal("stun:stun.l.google.com:19302", (string)payload["iceServers"]![0]!["urls"]![0]!);

        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        var users = (JArray)sam.Info["payload"]!["users"]!;
        Assert.Single(users);
        Assert.Equal(klives.Id, (string)users[0]["id"]!);

        var joined = await klives.Socket.ExpectAsync("user-joined");
        Assert.Equal(sam.Id, (string)joined["payload"]!["id"]!);
        Assert.True((bool)joined["payload"]!["isGuest"]!);
        Assert.Equal(2, room.ParticipantCount);
    }

    [Fact]
    public async Task ParticipantIds_AreDerivedFromTheTabSecret()
    {
        string a = KliveChatText.ParticipantIdFromSecret("room1abcde", "tab-secret-aaaaaaaaaaaa");
        Assert.Equal(a, KliveChatText.ParticipantIdFromSecret("room1abcde", "tab-secret-aaaaaaaaaaaa"));
        Assert.NotEqual(a, KliveChatText.ParticipantIdFromSecret("room2abcde", "tab-secret-aaaaaaaaaaaa"));
        Assert.Matches("^[0-9a-f]{16}$", a);

        var room = NewRoom();
        var seat = await JoinAsync(room, Guest("Sam", "tab-secret-aaaaaaaaaaaa"));
        Assert.Equal(a, seat.Id);
    }

    [Fact]
    public async Task Reconnecting_WithTheSameSecret_TakesTheSeatBack_WithoutAnyoneSeeingALeave()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var first = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        await klives.Socket.ExpectAsync("user-joined");

        var second = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        Assert.Equal(first.Id, second.Id);
        Assert.True((bool)second.Info["payload"]!["resumed"]!);

        await first.Socket.WaitClosedAsync();
        Assert.Equal("Replaced", first.Socket.ServerCloseReason);
        await first.Run.WaitAsync(TimeSpan.FromSeconds(3));

        await klives.Socket.ExpectAsync("user-joined", m => (string?)m["payload"]?["id"] == first.Id);
        await klives.Socket.AssertNothingAsync("user-left");
        Assert.Equal(2, room.ParticipantCount);

        // The replacement socket is live.
        second.Socket.ClientSend(new { type = "chat-message", payload = new { text = "back" } });
        await klives.Socket.ExpectAsync("chat-message");
    }

    [Fact]
    public async Task ADifferentPerson_CannotClaimAnotherParticipantsSeat()
    {
        var room = NewRoom();
        await JoinAsync(room, Guest("Sam", "shared-secret-0123456789"));
        var impostor = Guest("Mallory", "shared-secret-0123456789");
        var socket = await RefusedAsync(room, impostor);
        Assert.Equal("Session conflict", socket.ServerCloseReason);
        Assert.Equal(1, room.ParticipantCount);
    }

    [Fact]
    public async Task ADroppedConnection_HoldsTheSeat_ThenLeavesWhenTheGracePeriodRunsOut()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        await klives.Socket.ExpectAsync("user-joined");

        sam.Socket.Drop();
        await sam.Run.WaitAsync(TimeSpan.FromSeconds(3));
        var held = await klives.Socket.ExpectAsync("participant-state", m => (string?)m["payload"]?["id"] == sam.Id);
        Assert.False((bool)held["payload"]!["connected"]!);
        Assert.Equal(2, room.ParticipantCount);

        host.Advance(KliveChatRoom.ReconnectGrace - TimeSpan.FromSeconds(1));
        room.Sweep();
        await klives.Socket.AssertNothingAsync("user-left");

        host.Advance(TimeSpan.FromSeconds(2));
        room.Sweep();
        var left = await klives.Socket.ExpectAsync("user-left");
        Assert.Equal(sam.Id, (string)left["payload"]!["id"]!);
        Assert.Equal(1, room.ParticipantCount);
    }

    [Fact]
    public async Task ADroppedConnection_ThatComesBackInTime_KeepsItsSeat()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        sam.Socket.Drop();
        await sam.Run.WaitAsync(TimeSpan.FromSeconds(3));

        host.Advance(TimeSpan.FromSeconds(5));
        var back = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        Assert.Equal(sam.Id, back.Id);

        host.Advance(TimeSpan.FromSeconds(20));
        room.Sweep();
        await klives.Socket.AssertNothingAsync("user-left");
        Assert.Equal(2, room.ParticipantCount);
    }

    [Fact]
    public async Task HangingUp_LeavesImmediately()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        sam.Socket.ClientClose();
        await sam.Run.WaitAsync(TimeSpan.FromSeconds(3));
        await klives.Socket.ExpectAsync("user-left", m => (string?)m["payload"]?["id"] == sam.Id);
        Assert.Equal(1, room.ParticipantCount);
    }

    [Fact]
    public async Task Signalling_IsRelayedOnlyToItsTarget_StampedWithTheRealSender()
    {
        var room = NewRoom();
        var a = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var b = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        var c = await JoinAsync(room, Guest("Alex", "alex-secret-0123456789"));

        a.Socket.ClientSend(new { type = "offer", targetId = b.Id, senderId = c.Id, payload = new { pcId = "p1", sdp = new { type = "offer", sdp = "v=0" } } });
        var offer = await b.Socket.ExpectAsync("offer");
        Assert.Equal(a.Id, (string)offer["senderId"]!);
        Assert.Equal("v=0", (string)offer["payload"]!["sdp"]!["sdp"]!);
        await c.Socket.AssertNothingAsync("offer");

        // Anything that isn't signalling is not relayed peer-to-peer.
        a.Socket.ClientSend(new { type = "participant-removed", targetId = b.Id, payload = new { id = b.Id } });
        await b.Socket.AssertNothingAsync("participant-removed");
    }

    [Fact]
    public async Task MediaState_IsBroadcastToTheOthers()
    {
        var room = NewRoom();
        var a = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var b = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        b.Socket.ClientSend(new { type = "media-state", payload = new { isMuted = true, hasVideo = true, isScreenSharing = true, hasScreenAudio = true } });
        var state = await a.Socket.ExpectAsync("participant-state", m => (bool?)m["payload"]?["isMuted"] == true);
        Assert.True((bool)state["payload"]!["hasVideo"]!);
        Assert.True((bool)state["payload"]!["hasScreenAudio"]!);
    }

    [Fact]
    public async Task Chat_IsCleaned_Stamped_Broadcast_AndKeptForLateJoiners()
    {
        var room = NewRoom();
        var a = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var b = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));

        a.Socket.ClientSend(new { type = "chat-message", payload = new { text = "  hello ‮world\u0007 ", clientMsgId = "c-1" } });
        var mine = await a.Socket.ExpectAsync("chat-message");
        var theirs = await b.Socket.ExpectAsync("chat-message");
        Assert.Equal("hello world", (string)theirs["payload"]!["text"]!);
        Assert.Equal("Klives", (string)theirs["payload"]!["senderName"]!);
        Assert.Equal("c-1", (string)mine["payload"]!["clientMsgId"]!);
        Assert.Equal((string)mine["payload"]!["id"]!, (string)theirs["payload"]!["id"]!);

        a.Socket.ClientSend(new { type = "chat-message", payload = new { text = "   " } });
        await b.Socket.AssertNothingAsync("chat-message");

        var late = await JoinAsync(room, Guest("Alex", "alex-secret-0123456789"));
        var history = (JArray)late.Info["payload"]!["chat"]!;
        Assert.Single(history);
        Assert.Equal("hello world", (string)history[0]["text"]!);
    }

    [Fact]
    public async Task Chat_IsRateLimited()
    {
        var room = NewRoom();
        var a = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        for (int i = 0; i < KliveChatRoom.ChatBurst + 2; i++) a.Socket.ClientSend(new { type = "chat-message", payload = new { text = $"m{i}" } });
        var error = await a.Socket.ExpectAsync("room-error");
        Assert.Equal("rate-limited", (string)error["payload"]!["code"]!);
        Assert.Equal(KliveChatRoom.ChatBurst, a.Socket.All("chat-message").Count);

        host.Advance(KliveChatRoom.ChatWindow + TimeSpan.FromSeconds(1));
        a.Socket.ClientSend(new { type = "chat-message", payload = new { text = "later" } });
        await a.Socket.ExpectAsync("chat-message", m => (string?)m["payload"]?["text"] == "later");
    }

    [Fact]
    public async Task Moderation_NeedsTheHostOrThePermission()
    {
        var room = NewRoom();
        var host1 = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        var alex = await JoinAsync(room, Guest("Alex", "alex-secret-0123456789"));

        sam.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = alex.Id, action = "kick" } });
        var refused = await sam.Socket.ExpectAsync("room-error");
        Assert.Equal("forbidden", (string)refused["payload"]!["code"]!);
        Assert.Equal(3, room.ParticipantCount);

        // The room's creator moderates their own room without klivechat.rooms.moderate.
        host1.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = alex.Id, action = "kick" } });
        var removal = await alex.Socket.ExpectAsync("participant-removed");
        Assert.Equal("kick", (string)removal["payload"]!["action"]!);
        await alex.Socket.WaitClosedAsync();
        Assert.Equal("Removed from room", alex.Socket.ServerCloseReason);
        await sam.Socket.ExpectAsync("participant-removed", m => (string?)m["payload"]?["id"] == alex.Id);
        Assert.Equal(2, room.ParticipantCount);
        await alex.Run.WaitAsync(TimeSpan.FromSeconds(3));
        await sam.Socket.AssertNothingAsync("user-left");
    }

    [Fact]
    public async Task Mute_TellsTheTargetToMute_AndEveryoneElseSeesIt()
    {
        var room = NewRoom(r => r.CreatedByUserId = "someone-else");
        var mod = await JoinAsync(room, Member("sam", "Sam", ProfileRank.Associate, moderate: true));
        var guest = await JoinAsync(room, Guest("Noisy", "noisy-secret-0123456789"));
        var other = await JoinAsync(room, Guest("Alex", "alex-secret-0123456789"));

        mod.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = guest.Id, action = "mute" } });
        var forced = await guest.Socket.ExpectAsync("force-mute");
        Assert.Equal("Sam", (string)forced["payload"]!["byName"]!);
        await other.Socket.ExpectAsync("participant-state", m => (string?)m["payload"]?["id"] == guest.Id && (bool?)m["payload"]?["isMuted"] == true);
        Assert.Equal(3, room.ParticipantCount);
    }

    [Fact]
    public async Task Moderators_CannotTouchSomeoneRankedTheSameOrAbove()
    {
        var room = NewRoom(r => r.CreatedByUserId = "someone-else");
        var mod = await JoinAsync(room, Member("sam", "Sam", ProfileRank.Associate, moderate: true));
        var peer = await JoinAsync(room, Member("alex", "Alex", ProfileRank.Associate));
        var boss = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));

        mod.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = peer.Id, action = "kick" } });
        await mod.Socket.ExpectAsync("room-error");
        mod.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = boss.Id, action = "mute" } });
        await mod.Socket.ExpectAsync("room-error");
        mod.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = mod.Id, action = "kick" } });
        await mod.Socket.ExpectAsync("room-error");
        Assert.Equal(3, room.ParticipantCount);
        await boss.Socket.AssertNothingAsync("force-mute");
    }

    [Fact]
    public async Task ABan_KeepsThemOut_EvenWithANewBrowserIdentity()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var troll = await JoinAsync(room, Guest("Troll", "troll-secret-0123456789", ip: "203.0.113.7"));
        klives.Socket.ClientSend(new { type = "moderate-participant", payload = new { targetId = troll.Id, action = "ban" } });
        await troll.Socket.WaitClosedAsync();
        Assert.Equal("Banned from room", troll.Socket.ServerCloseReason);

        var again = await RefusedAsync(room, Guest("Troll", "troll-secret-0123456789", ip: "203.0.113.7"));
        Assert.Equal("Banned from room", again.ServerCloseReason);

        // Cleared site data (new identity, new secret) from the same address.
        var disguised = Guest("Totally New", "fresh-secret-0123456789", ip: "203.0.113.7");
        var refused = await RefusedAsync(room, disguised);
        Assert.Equal("Banned from room", refused.ServerCloseReason);

        Assert.Contains(room.SnapshotRecord().BannedKeys, k => k == "ip:203.0.113.7");
        Assert.Equal(1, room.ParticipantCount);
    }

    [Fact]
    public async Task ALockedRoom_RefusesNewcomers_ButNotModerators_OrSomeoneReconnecting()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));

        klives.Socket.ClientSend(new { type = "room-settings", payload = new { locked = true } });
        var updated = await sam.Socket.ExpectAsync("room-updated");
        Assert.True((bool)updated["payload"]!["room"]!["locked"]!);

        var stranger = await RefusedAsync(room, Guest("Stranger", "stranger-secret-0123456789"));
        Assert.Equal("Room locked", stranger.ServerCloseReason);

        await JoinAsync(room, Member("mod", "Mod", ProfileRank.Admin, moderate: true));

        sam.Socket.Drop();
        await sam.Run.WaitAsync(TimeSpan.FromSeconds(3));
        var back = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        Assert.Equal(sam.Id, back.Id);

        // Guests can't unlock it.
        back.Socket.ClientSend(new { type = "room-settings", payload = new { locked = false } });
        await back.Socket.ExpectAsync("room-error");
        Assert.True(room.SnapshotRecord().Locked);
    }

    [Fact]
    public async Task AFullRoom_RefusesTheNextPerson()
    {
        var room = NewRoom();
        for (int i = 0; i < KliveChatRoom.MaxParticipants; i++)
        {
            await JoinAsync(room, Guest($"Guest {i}", $"guest-{i:00}-secret-0123456789"));
        }
        var refused = await RefusedAsync(room, Guest("One too many", "late-secret-0123456789"));
        Assert.Equal("Room full", refused.ServerCloseReason);
    }

    [Fact]
    public async Task ASilentProtocol2Connection_IsDropped_ButAnOldPageIsNot()
    {
        var room = NewRoom();
        var watcher = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var modern = await JoinAsync(room, Guest("Modern", "modern-secret-0123456789"));
        var oldJoin = Guest("Old page");
        oldJoin.Protocol = 1;
        var old = await JoinAsync(room, oldJoin);

        // Klives keeps pinging; the others go quiet.
        host.Advance(TimeSpan.FromSeconds(20));
        watcher.Socket.ClientSend(new { type = "ping", payload = new { t = 1 } });
        await watcher.Socket.ExpectAsync("pong");
        host.Advance(KliveChatRoom.HeartbeatTimeout - TimeSpan.FromSeconds(10));
        room.Sweep();

        await modern.Run.WaitAsync(TimeSpan.FromSeconds(3));
        await watcher.Socket.ExpectAsync("participant-state", m => (string?)m["payload"]?["id"] == modern.Id && (bool?)m["payload"]?["connected"] == false);
        Assert.False(old.Run.IsCompleted);
        Assert.False(watcher.Run.IsCompleted);
    }

    [Fact]
    public async Task AnOversizedMessage_ClosesTheConnection()
    {
        var room = NewRoom();
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        sam.Socket.ClientSendRaw("{\"type\":\"chat-message\",\"payload\":{\"text\":\"" + new string('x', KliveChatRoom.MaxInboundMessageBytes) + "\"}}");
        await sam.Socket.WaitClosedAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, sam.Socket.ServerCloseStatus);
    }

    [Fact]
    public async Task GuestNames_AreDeduplicated()
    {
        var room = NewRoom();
        await JoinAsync(room, Guest("Sam", "sam1-secret-0123456789"));
        var second = await JoinAsync(room, Guest("sam", "sam2-secret-0123456789"));
        Assert.Equal("sam 2", (string)second.Info["payload"]!["self"]!["name"]!);
    }

    [Fact]
    public async Task ClosingTheRoom_DisconnectsEveryoneWithTheReason()
    {
        var room = NewRoom();
        var a = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        var b = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        await room.CloseAsync("Room deleted");
        await a.Socket.WaitClosedAsync();
        await b.Socket.WaitClosedAsync();
        Assert.Equal("Room deleted", a.Socket.ServerCloseReason);
        Assert.Equal("Room deleted", b.Socket.ServerCloseReason);
        Assert.Equal(0, room.ParticipantCount);

        var late = await RefusedAsync(room, Guest("Late", "late-secret-0123456789"));
        Assert.Equal("Invalid Room ID", late.ServerCloseReason);
    }

    [Fact]
    public async Task EmptyRooms_Expire_UnlessKept()
    {
        var temporary = NewRoom();
        var kept = NewRoom(r => r.Persistent = true);
        var busy = NewRoom();
        await JoinAsync(busy, Guest("Sam", "sam-secret-0123456789"));

        host.Advance(TimeSpan.FromHours(23));
        Assert.False(temporary.TryExpire(host.Now, TimeSpan.FromHours(24)));
        host.Advance(TimeSpan.FromHours(2));
        Assert.True(temporary.TryExpire(host.Now, TimeSpan.FromHours(24)));
        Assert.True(temporary.IsClosed);
        Assert.False(kept.TryExpire(host.Now, TimeSpan.FromHours(24)));
        Assert.False(busy.TryExpire(host.Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task TheLobbySummary_ListsWhoIsInTheRoom()
    {
        var room = NewRoom();
        var klives = await JoinAsync(room, Member("klives", "Klives", ProfileRank.Klives));
        host.Advance(TimeSpan.FromSeconds(1));
        var sam = await JoinAsync(room, Guest("Sam", "sam-secret-0123456789"));
        sam.Socket.ClientSend(new { type = "media-state", payload = new { isMuted = true, hasVideo = false, isScreenSharing = false } });
        await klives.Socket.ExpectAsync("participant-state", m => (bool?)m["payload"]?["isMuted"] == true);

        var summary = room.ToSummary(canManage: false);
        Assert.Equal(2, summary.UserCount);
        Assert.Equal(new[] { "Klives", "Sam" }, summary.Participants.Select(p => p.Name));
        Assert.True(summary.Participants[1].IsGuest);
        Assert.True(summary.Participants[1].IsMuted);
        Assert.Equal("/shared/klivechat/room1abcde", summary.RoomPath);
    }

    [Fact]
    public void Creator_IsMatchedById_OrByNameForOlderRooms()
    {
        var room = NewRoom();
        Assert.True(room.IsCreator("klives", "Whatever"));
        Assert.False(room.IsCreator("sam", "Klives"));
        Assert.False(room.IsCreator(null, "Klives"));

        var legacy = NewRoom(r => r.CreatedByUserId = null);
        Assert.True(legacy.IsCreator("anything", "klives"));
    }
}
