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
            instance.LaunchTarget = Path.Combine(directory, "RustDedicated.exe");
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

    private static GameServerInstance Instance(string directory) => new()
    {
        Id = "rusttest",
        Name = "Rust Test",
        GameType = GameType.Rust,
        Flavor = ServerFlavor.Vanilla,
        Version = "Latest",
        Port = 28015,
        ServerDirectory = directory,
        MaxPlayers = 50,
    };

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "omnipotent-rust-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string key, string value)
    {
        int index = arguments.ToList().IndexOf(key);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing launch argument {key}.");
        Assert.Equal(value, arguments[index + 1]);
    }
}
