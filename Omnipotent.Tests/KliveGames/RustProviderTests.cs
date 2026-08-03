using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Games;
using Omnipotent.Services.KliveGames.Games.Rust;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Tests.KliveGames;

public sealed class RustProviderTests
{
    [Fact]
    public void Registry_ExposesImplementedRustProviderWithGameAndQueryPorts()
    {
        var registry = new GameProviderRegistry(_ => Task.CompletedTask);

        var provider = registry.Get(GameType.Rust);
        var ports = provider.GetNetworkPorts(28015);

        Assert.True(provider.Implemented);
        Assert.Equal("Rust", provider.DisplayName);
        Assert.Equal("UDP", provider.Protocol);
        Assert.Equal(new[] { 28015, 28016 }, ports.Select(port => port.Port));
        Assert.All(ports, port => Assert.Equal("UDP", port.Protocol));
    }

    [Fact]
    public void Config_RoundTripsAndKeepsQueryPortNextToGamePort()
    {
        string directory = CreateTempDirectory();
        try
        {
            var instance = Instance(directory);
            RustServerConfig.WriteDefault(instance, 3500, 12345, 50, "Klive's Rust server");

            var loaded = RustServerConfig.Load(instance);
            Assert.Equal("Klive's Rust server", loaded["server.description"]);
            Assert.Equal("28015", loaded["server.port"]);
            Assert.Equal("28016", loaded["server.queryport"]);

            RustServerConfig.Apply(instance, new Dictionary<string, string> { ["server.port"] = "28100" });
            loaded = RustServerConfig.Load(instance);
            Assert.Equal("28100", loaded["server.port"]);
            Assert.Equal("28101", loaded["server.queryport"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchSpec_UsesOfficialExecutableIdentityAndManagedUdpPorts()
    {
        string directory = CreateTempDirectory();
        try
        {
            var instance = Instance(directory);
            instance.LaunchTarget = Path.Combine(instance.ServerDirectory, "RustDedicated.exe");
            File.WriteAllText(instance.LaunchTarget, string.Empty);
            RustServerConfig.WriteDefault(instance, 4000, 67890, 75, "Test server");

            var provider = new RustProvider(_ => Task.CompletedTask);
            var spec = await provider.BuildLaunchSpecAsync(instance, CancellationToken.None);

            Assert.Equal(instance.LaunchTarget, spec.Executable);
            Assert.Equal("quit", spec.GracefulStopCommand);
            AssertArgumentPair(spec.Arguments, "+server.port", "28015");
            AssertArgumentPair(spec.Arguments, "+server.queryport", "28016");
            AssertArgumentPair(spec.Arguments, "+server.identity", "klivegames-rusttest");
            AssertArgumentPair(spec.Arguments, "+server.worldsize", "4000");
            Assert.DoesNotContain("-logfile", spec.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>RustDedicated ignores stdin, so a launch without a loopback RCON console is a server that
    /// can never be sent a command — not even its own graceful stop.</summary>
    [Fact]
    public async Task LaunchSpec_EnablesLoopbackWebRconAndReusesItsPersistedCredentials()
    {
        string directory = CreateTempDirectory();
        try
        {
            var instance = Instance(directory);
            instance.LaunchTarget = Path.Combine(instance.ServerDirectory, "RustDedicated.exe");
            File.WriteAllText(instance.LaunchTarget, string.Empty);
            RustServerConfig.WriteDefault(instance, 3500, 1, 50, "Test server");

            var provider = new RustProvider(_ => Task.CompletedTask);
            var spec = await provider.BuildLaunchSpecAsync(instance, CancellationToken.None);

            var settings = RustRconSettings.Load(instance);
            Assert.NotNull(settings);
            Assert.True(settings!.Port >= 1024);
            Assert.NotEmpty(settings.Password);
            Assert.NotEqual(instance.Port, settings.Port);

            AssertArgumentPair(spec.Arguments, "+rcon.ip", "127.0.0.1");
            AssertArgumentPair(spec.Arguments, "+rcon.web", "1");
            AssertArgumentPair(spec.Arguments, "+rcon.port", settings.Port.ToString());
            AssertArgumentPair(spec.Arguments, "+rcon.password", settings.Password);

            // A restart keeps the password so the console reconnects to the same server.
            var second = await provider.BuildLaunchSpecAsync(instance, CancellationToken.None);
            AssertArgumentPair(second.Arguments, "+rcon.password", settings.Password);

            using var console = provider.CreateRemoteConsole(instance);
            Assert.NotNull(console);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConsoleParsing_RecognizesStartupPlayerListAndSafeKick()
    {
        var provider = new RustProvider(_ => Task.CompletedTask);
        string json = new JArray(
            new JObject { ["SteamID"] = "76561198000000000", ["DisplayName"] = "Klive" },
            new JObject { ["SteamID"] = "76561198000000001", ["DisplayName"] = "Builder" })
            .ToString(Formatting.None);

        Assert.True(provider.TryParseStarted("Server startup complete"));
        Assert.True(provider.TryParseListReply(json, out int online, out int max, out string[] names));
        Assert.Equal(2, online);
        Assert.Equal(0, max);
        Assert.Equal(new[] { "Klive", "Builder" }, names);
        Assert.Equal($"kick {(char)34}Klive{(char)34}", provider.BuildPlayerActionCommand("kick", "Klive"));
        Assert.Null(provider.BuildPlayerActionCommand("ban", "Klive"));
    }

    /// <summary>The lines a live server actually prints. The original patterns only matched "has entered
    /// the game", so a real join never registered and the players panel stayed empty.</summary>
    [Theory]
    [InlineData("Klives with steamid 76561198048900350 joined from ip 176.253.7.246:57298", "Klives")]
    [InlineData("Klives[76561198048900350] has spawned", "Klives")]
    [InlineData("Klives[76561198048900350] has entered the game", "Klives")]
    [InlineData("Klive Games[502984/76561198048900350] has spawned", "Klive Games")]
    [InlineData("176.253.7.246:57298/76561198048900350/Klives joined [windows/76561198048900350]", "Klives")]
    public void ConsoleParsing_RecognizesEveryJoinFormatRustLogs(string line, string expected)
    {
        var provider = new RustProvider(_ => Task.CompletedTask);

        Assert.True(provider.TryParsePlayerJoin(line, out string player));
        Assert.Equal(expected, player);
        Assert.False(provider.TryParsePlayerLeave(line, out _));
    }

    [Theory]
    [InlineData("Klives[76561198048900350] disconnecting: disconnect", "Klives")]
    [InlineData("Klives[502984/76561198048900350] has left the game", "Klives")]
    [InlineData("176.253.7.246:57298/76561198048900350/Klives disconnecting: closing", "Klives")]
    public void ConsoleParsing_RecognizesEveryLeaveFormatRustLogs(string line, string expected)
    {
        var provider = new RustProvider(_ => Task.CompletedTask);

        Assert.True(provider.TryParsePlayerLeave(line, out string player));
        Assert.Equal(expected, player);
        Assert.False(provider.TryParsePlayerJoin(line, out _));
    }

    /// <summary>Over RCON the whole reply lands as one multi-line message, which is what makes the
    /// pretty-printed playerlist document readable at all.</summary>
    [Fact]
    public void RosterParsing_ReadsMultiLineJsonAndClearsOnAnEmptyArray()
    {
        var provider = new RustProvider(_ => Task.CompletedTask);
        string json = new JArray(
            new JObject { ["SteamID"] = "76561198048900350", ["DisplayName"] = "Klives", ["Health"] = 100 })
            .ToString(Formatting.Indented);

        Assert.True(provider.TryParseListReply(json, out int online, out _, out string[] names));
        Assert.Equal(1, online);
        Assert.Equal(new[] { "Klives" }, names);

        Assert.True(provider.TryParseListReply("[]", out int empty, out _, out string[] none));
        Assert.Equal(0, empty);
        Assert.Empty(none);
    }

    [Fact]
    public void RosterParsing_ReadsTheTabularStatusReplyIncludingMaxPlayers()
    {
        var provider = new RustProvider(_ => Task.CompletedTask);
        char quote = (char)34;
        string status = string.Join('\n',
            "hostname: KliveWorld",
            "players : 1 (50 max) (0 queued) (0 joining)",
            "",
            "id                name      ping connected addr",
            $"76561198048900350 {quote}Klives{quote} 30   240.5s    176.253.7.246:57298");

        Assert.True(provider.TryParseListReply(status, out int online, out int max, out string[] names));
        Assert.Equal(1, online);
        Assert.Equal(50, max);
        Assert.Equal(new[] { "Klives" }, names);
    }

    /// <summary>A roster match replaces the entire player list, so an ordinary log line must never look
    /// like one — a false positive silently empties the panel.</summary>
    [Theory]
    [InlineData("Klives[76561198048900350] has spawned")]
    [InlineData("NetworkId 76561198048900350 is 502984 (Klives)")]
    [InlineData("176.253.7.246:57298/76561198048900350/Klives joined [windows/76561198048900350]")]
    [InlineData("Server startup complete")]
    public void RosterParsing_IgnoresOrdinaryConsoleLines(string line)
    {
        var provider = new RustProvider(_ => Task.CompletedTask);

        Assert.False(provider.TryParseListReply(line, out _, out _, out _));
    }

    private static GameServerInstance Instance(string directory) => new()
    {
        Id = "rusttest",
        Name = "Rust Test",
        GameType = GameType.Rust,
        Flavor = ServerFlavor.Vanilla,
        Version = "Latest",
        Port = 28015,
        ServerDirectory = Path.Combine(directory, "server"),
        MaxPlayers = 50,
    };

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "omnipotent-rust-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "server"));
        return directory;
    }

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string key, string value)
    {
        int index = arguments.ToList().IndexOf(key);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing launch argument {key}.");
        Assert.Equal(value, arguments[index + 1]);
    }
}
