using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.KliveChat;

namespace Omnipotent.Tests.KliveChat;

public class KliveChatTextTests
{
    [Theory]
    [InlineData("  Sam   Carter ", "Sam Carter")]
    [InlineData("Sam‮retrac", "Samretrac")]
    [InlineData("Na\u0000me\u0007", "Name")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void DisplayNames_AreOneCleanLine(string? raw, string? expected)
    {
        Assert.Equal(expected, KliveChatText.CleanDisplayName(raw));
    }

    [Fact]
    public void Names_AreCappedWithoutSplittingAnEmoji()
    {
        string name = new string('a', KliveChatText.MaxDisplayNameLength - 1) + "😀😀";
        string cleaned = KliveChatText.CleanDisplayName(name)!;
        Assert.EndsWith("😀", cleaned);
        Assert.False(char.IsHighSurrogate(cleaned[^1]));
    }

    [Fact]
    public void Chat_KeepsLinesAndEmoji_DropsControlAndBidiCharacters()
    {
        Assert.Equal("line one\nline two 👨‍👩‍👧", KliveChatText.CleanChat("line one\r\nline two‮ 👨‍👩‍👧\u0007"));
        Assert.Null(KliveChatText.CleanChat(" \n\t "));
        Assert.Equal(KliveChatText.MaxChatLength, KliveChatText.CleanChat(new string('x', 5000))!.Length);
        Assert.Equal(KliveChatText.MaxChatLines, KliveChatText.CleanChat(string.Join('\n', Enumerable.Range(0, 100)))!.Split('\n').Length);
    }

    [Fact]
    public void RoomIds_AreShortReadableAndUnique()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => KliveChatText.NewRoomId()).ToList();
        Assert.All(ids, id => Assert.Matches("^[a-km-z2-9]{10}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(KliveChatText.IsValidRoomId("a1b2c3d4"));
        Assert.False(KliveChatText.IsValidRoomId("../etc"));
        Assert.False(KliveChatText.IsValidRoomId(""));
    }

    [Theory]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("short", false)]
    [InlineData("has spaces in it ok?", false)]
    [InlineData("k3y_with-dashes_and_underscores", true)]
    public void Secrets_AreValidated(string secret, bool valid)
    {
        Assert.Equal(valid, KliveChatText.IsValidSecret(secret));
    }
}

public class KliveChatRoomStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "klivechat-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task Rooms_SurviveARoundTrip()
    {
        var store = new KliveChatRoomStore(Path.Combine(directory, "Rooms.json"));
        var created = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        await store.SaveAsync(new[]
        {
            new KliveChatRoomRecord { Id = "abcdefghjk", Name = "General", CreatedBy = "Klives", CreatedByUserId = "klives", CreatedUtc = created, LastActiveUtc = created, Persistent = true, Locked = true, BannedKeys = { "ip:203.0.113.7" } },
            new KliveChatRoomRecord { Id = "mnpqrstuvw", Name = "Movie night", CreatedBy = "Sam", CreatedUtc = created, LastActiveUtc = created },
        });

        var loaded = store.Load(out string? problem);
        Assert.Null(problem);
        Assert.Equal(2, loaded.Count);
        var general = loaded.Single(r => r.Id == "abcdefghjk");
        Assert.True(general.Persistent);
        Assert.True(general.Locked);
        Assert.Equal("klives", general.CreatedByUserId);
        Assert.Equal(created, general.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, general.CreatedUtc.Kind);
        Assert.Equal(new[] { "ip:203.0.113.7" }, general.BannedKeys);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void AMissingFile_IsNoRooms()
    {
        var store = new KliveChatRoomStore(Path.Combine(directory, "Rooms.json"));
        Assert.Empty(store.Load(out string? problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AnUnreadableFile_IsSetAside_NotOverwritten()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Rooms.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new KliveChatRoomStore(path);
        Assert.Empty(store.Load(out string? problem));
        Assert.NotNull(problem);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory, "Rooms.json.unreadable-*"));
    }

    [Fact]
    public void RecordsWithBadIdsOrNames_AreCleanedOnLoad()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Rooms.json");
        File.WriteAllText(path, """
            [
              { "id": "../../evil", "name": "x" },
              { "id": "abcdefghjk", "name": "  ‮spaced   out  " },
              { "id": "abcdefghjk", "name": "duplicate" }
            ]
            """);
        var loaded = new KliveChatRoomStore(path).Load(out _);
        var room = Assert.Single(loaded);
        Assert.Equal("spaced out", room.Name);
        Assert.NotNull(room.BannedKeys);
        Assert.NotEqual(default, room.LastActiveUtc);
    }
}

public class KliveChatIceTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WithoutTurn_OnlyStunIsOffered()
    {
        var servers = KliveChatIce.Build("", "secret", null, null, "abc", Now);
        var stun = Assert.Single(servers);
        Assert.All(stun.Urls, url => Assert.StartsWith("stun:", url));
        Assert.Null(stun.Username);
    }

    [Fact]
    public void ASharedSecret_IssuesShortLivedCredentials_TheWayCoturnChecksThem()
    {
        var servers = KliveChatIce.Build("turn:turn.example.com:3478?transport=udp, turns:turn.example.com:5349, stun:ignored", "s3cret", null, null, "abc123", Now);
        Assert.Equal(2, servers.Count);
        var turn = servers[1];
        Assert.Equal(new[] { "turn:turn.example.com:3478?transport=udp", "turns:turn.example.com:5349" }, turn.Urls);

        long expiry = new DateTimeOffset(Now + KliveChatIce.CredentialLifetime).ToUnixTimeSeconds();
        Assert.Equal($"{expiry}:abc123", turn.Username);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("s3cret"));
        Assert.Equal(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(turn.Username!))), turn.Credential);
    }

    [Fact]
    public void FixedCredentials_AreUsedWhenThereIsNoSecret()
    {
        var servers = KliveChatIce.Build("turn:relay.example.net:80", null, "user", "pass", "abc", Now);
        Assert.Equal("user", servers[1].Username);
        Assert.Equal("pass", servers[1].Credential);
        Assert.Single(KliveChatIce.Build("turn:relay.example.net:80", null, "user", null, "abc", Now));
    }
}
