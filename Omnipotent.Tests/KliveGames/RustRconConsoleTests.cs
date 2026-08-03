using Newtonsoft.Json;
using Omnipotent.Services.KliveGames.Games.Rust;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Tests.KliveGames;

public sealed class RustRconConsoleTests
{
    /// <summary>Rust pushes the whole server log down RCON as well, which stdout already streams into the
    /// console. Those must be parsed but not shown, or every line would appear twice.</summary>
    [Fact]
    public void Dispatch_TreatsServerBroadcastsAsSilentAndCommandRepliesAsVisible()
    {
        using var console = new RustRconConsole(28017, "secret");
        var received = new List<RemoteConsoleMessage>();
        console.OnMessage += message => received.Add(message);

        console.Dispatch(Envelope("Klives[76561198048900350] has spawned", identifier: 0));
        console.Dispatch(Envelope("Added owner 76561198048900350", identifier: 7));

        Assert.Equal(2, received.Count);
        Assert.Equal(RemoteConsoleMessageKind.Broadcast, received[0].Kind);
        Assert.Equal("Klives[76561198048900350] has spawned", received[0].Text);
        Assert.Equal(RemoteConsoleMessageKind.Reply, received[1].Kind);
        Assert.Equal("Added owner 76561198048900350", received[1].Text);
    }

    [Fact]
    public void Dispatch_KeepsEmptyRepliesOutOfTheConsoleAndSurfacesUnwrappedPayloads()
    {
        using var console = new RustRconConsole(28017, "secret");
        var received = new List<RemoteConsoleMessage>();
        console.OnMessage += message => received.Add(message);

        console.Dispatch(Envelope("   ", identifier: 3));
        console.Dispatch("not the documented envelope");

        Assert.Single(received);
        Assert.Equal("not the documented envelope", received[0].Text);
    }

    [Fact]
    public void SendAsync_ReportsFailureWhileTheConsoleIsNotConnected()
    {
        using var console = new RustRconConsole(28017, "secret");

        Assert.False(console.IsConnected);
        Assert.False(console.SendAsync("status").GetAwaiter().GetResult());
    }

    private static string Envelope(string message, int identifier) => JsonConvert.SerializeObject(new
    {
        Message = message,
        Identifier = identifier,
        Type = "Generic",
        Stacktrace = "",
    });
}
