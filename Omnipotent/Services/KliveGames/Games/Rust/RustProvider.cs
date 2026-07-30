using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>Official Facepunch Rust dedicated-server provider for Windows.</summary>
    public sealed class RustProvider : IGameProvider
    {
        private static readonly Regex JoinRx = new(@"([^\[\]\r\n]{1,64})\[765\d{14}\]\s+has entered the game", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeaveRx = new(@"([^\[\]\r\n]{1,64})\[765\d{14}\]\s+(?:disconnecting|has left the game)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly RustInstaller _installer = new();

        public RustProvider(Func<string, Task> logError) { }

        public GameType GameType => GameType.Rust;
        public string DisplayName => "Rust";
        public bool Implemented => true;
        public string Protocol => "UDP";
        public int DefaultPort => 28015;
        public IReadOnlyList<GameNetworkPort> GetNetworkPorts(int primaryPort) => new[]
        {
            new GameNetworkPort { Port = primaryPort, Protocol = "UDP", Purpose = "Game" },
            new GameNetworkPort { Port = primaryPort + 1, Protocol = "UDP", Purpose = "Server browser query" },
        };
        public IReadOnlyList<ServerFlavor> SupportedFlavors => new[] { ServerFlavor.Vanilla };
        public bool RequiresEula => false;
        public bool UsesMemoryLimit => false;
        public string PortConfigKey => "server.port";
        public IReadOnlyList<string> SupportedPlayerActions => new[] { "kick" };

        public IReadOnlyList<ConfigSchemaField> GetDeployOptionsSchema(ServerFlavor flavor)
        {
            ConfigSchemaField F(string key, string label, ConfigFieldType type, string value, string? description = null)
                => new() { Key = key, Label = label, Type = type, Category = "World", Description = description, Value = value };

            return new List<ConfigSchemaField>
            {
                F("worldSize", "World Size", ConfigFieldType.Number, "3500", "1000–6000. Larger maps need more memory and disk space."),
                F("seed", "World Seed", ConfigFieldType.Number, "", "Optional. Leave blank to generate a random seed."),
                F("maxPlayers", "Max Players", ConfigFieldType.Number, "50", "1–500."),
                F("description", "Description", ConfigFieldType.Text, "", "Optional server-browser description."),
            };
        }

        public Task<IReadOnlyList<GameVersionInfo>> GetAvailableVersionsAsync(ServerFlavor flavor, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<GameVersionInfo>>(new[] { new GameVersionInfo { Version = "Latest" } });

        public async Task PrepareServerAsync(GameServerInstance inst, IProgress<string> progress, CancellationToken ct)
        {
            if (inst.Flavor != ServerFlavor.Vanilla)
                throw new InvalidOperationException($"Unsupported Rust flavor: {inst.Flavor}.");

            Directory.CreateDirectory(inst.ServerDirectory);
            await _installer.InstallAsync(inst, progress, ct);

            var options = inst.DeployOptions ?? new();
            int worldSize = options.TryGetValue("worldSize", out string? world) && int.TryParse(world, out int parsedWorld) ? parsedWorld : 3500;
            int seed = options.TryGetValue("seed", out string? seedText) && int.TryParse(seedText, out int parsedSeed)
                ? parsedSeed
                : Random.Shared.Next(0, int.MaxValue);
            int maxPlayers = options.TryGetValue("maxPlayers", out string? maxText) && int.TryParse(maxText, out int parsedMax) ? parsedMax : 50;
            string description = options.TryGetValue("description", out string? desc) && !string.IsNullOrWhiteSpace(desc)
                ? desc.Trim()
                : $"{inst.Name} — powered by KliveGames";

            RustServerConfig.WriteDefault(inst, worldSize, seed, maxPlayers, description);
            inst.MaxPlayers = Math.Clamp(maxPlayers, 1, 500);
            progress.Report("Rust server provisioned and ready to start.");
        }

        public Task<LaunchSpec> BuildLaunchSpecAsync(GameServerInstance inst, CancellationToken ct)
        {
            string executable = !string.IsNullOrWhiteSpace(inst.LaunchTarget)
                ? inst.LaunchTarget
                : Path.Combine(inst.ServerDirectory, "RustDedicated.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("RustDedicated.exe is missing. Redeploy or repair the server files.", executable);

            var config = RustServerConfig.Load(inst);
            string V(string key, string fallback) => config.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
            int queryPort = inst.Port + 1;
            if (int.TryParse(V("server.queryport", queryPort.ToString(CultureInfo.InvariantCulture)), out int configuredQuery)) queryPort = configuredQuery;
            if (int.TryParse(V("server.maxplayers", inst.MaxPlayers.ToString(CultureInfo.InvariantCulture)), out int configuredMax)) inst.MaxPlayers = configuredMax;

            var spec = new LaunchSpec
            {
                Executable = executable,
                WorkingDirectory = inst.ServerDirectory,
                GracefulStopCommand = "quit",
                Arguments = new List<string>
                {
                    "-batchmode",
                    "-nographics",
                    "+server.port", inst.Port.ToString(CultureInfo.InvariantCulture),
                    "+server.queryport", queryPort.ToString(CultureInfo.InvariantCulture),
                    "+server.identity", RustServerConfig.GetIdentity(inst),
                    "+server.level", V("server.level", "Procedural Map"),
                    "+server.seed", V("server.seed", "0"),
                    "+server.worldsize", V("server.worldsize", "3500"),
                    "-logfile", "rustserverlog.txt",
                },
            };
            return Task.FromResult(spec);
        }

        public string GetGracefulStopCommand() => "quit";

        public bool TryParseStarted(string line)
            => line.Contains("Server startup complete", StringComparison.OrdinalIgnoreCase);

        public bool TryParsePlayerJoin(string line, out string player)
            => TryParsePlayer(JoinRx, line, out player);

        public bool TryParsePlayerLeave(string line, out string player)
            => TryParsePlayer(LeaveRx, line, out player);

        public bool TryParseListReply(string line, out int online, out int max, out string[] names)
        {
            online = 0;
            max = 0;
            names = Array.Empty<string>();
            int start = line.IndexOf('[');
            int end = line.LastIndexOf(']');
            if (start < 0 || end <= start || !line.Contains("SteamID", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var array = JArray.Parse(line[start..(end + 1)]);
                names = array
                    .OfType<JObject>()
                    .Select(item => (string?)item["DisplayName"] ?? (string?)item["displayName"] ?? (string?)item["Name"] ?? (string?)item["name"])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                online = names.Length;
                return true;
            }
            catch { return false; }
        }

        public string BuildListCommand() => "playerlist";

        public string? BuildPlayerActionCommand(string action, string player)
        {
            if (!string.Equals(action, "kick", StringComparison.OrdinalIgnoreCase)) return null;
            string safe = new string((player ?? string.Empty)
                .Where(character => character != (char)13 && character != (char)10 && character != (char)34)
                .ToArray()).Trim();
            char quote = (char)34;
            return safe.Length == 0 ? null : $"kick {quote}{safe}{quote}";
        }

        public IReadOnlyList<ConfigSchemaField> GetConfigSchema(GameServerInstance inst)
            => RustServerConfig.GetSchema(inst);

        public Task ApplyConfigAsync(GameServerInstance inst, Dictionary<string, string> values)
        {
            RustServerConfig.Apply(inst, values);
            return Task.CompletedTask;
        }

        private static bool TryParsePlayer(Regex regex, string line, out string player)
        {
            var match = regex.Match(line);
            player = match.Success ? match.Groups[1].Value.Trim() : "";
            return match.Success && player.Length > 0;
        }
    }
}
