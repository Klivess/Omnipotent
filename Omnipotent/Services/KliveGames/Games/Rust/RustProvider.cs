using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>Official Facepunch Rust dedicated-server provider for Windows.</summary>
    public sealed class RustProvider : IGameProvider
    {
        /// <summary>Every connection line Rust prints carries the 17-digit SteamID; player names may
        /// contain anything at all, so the ID is what the patterns anchor on. Which of these a build logs
        /// varies, so all of them are recognised and the orchestrator dedupes.</summary>
        private const string SteamIdPattern = @"765\d{14}";
        private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "Klives with steamid 76561198048900350 joined from ip 1.2.3.4:57298"
        private static readonly Regex JoinSteamIdRx = new($@"^\s*(?<name>.{{1,64}}?)\s+with\s+steamid\s+{SteamIdPattern}\s+joined\s+from\s+ip\b", Options);
        // "Klives[76561198048900350] has spawned" / "... has entered the game"
        private static readonly Regex JoinBracketRx = new($@"^\s*(?<name>[^\[\]\r\n]{{1,64}})\[[\d/]*{SteamIdPattern}[\d/]*\]\s+has\s+(?:entered\s+the\s+game|spawned)\b", Options);
        // "1.2.3.4:57298/76561198048900350/Klives joined [windows/76561198048900350]"
        private static readonly Regex JoinSlashRx = new($@"^\s*\S+/{SteamIdPattern}/(?<name>.{{1,64}}?)\s+joined\b", Options);

        private static readonly Regex LeaveBracketRx = new($@"^\s*(?<name>[^\[\]\r\n]{{1,64}})\[[\d/]*{SteamIdPattern}[\d/]*\]\s+(?:disconnecting\b|has\s+left\s+the\s+game|has\s+disconnected)", Options);
        private static readonly Regex LeaveSlashRx = new($@"^\s*\S+/{SteamIdPattern}/(?<name>.{{1,64}}?)\s+disconnecting\b", Options);

        // Table-shaped roster replies ("status", and playerlist on builds that render a table).
        private static readonly Regex StatusPlayersRx = new(@"players\s*:\s*(?<online>\d+)\s*\(\s*(?<max>\d+)\s*max", Options);
        private static readonly string Quote = ((char)34).ToString();
        private static readonly Regex TableRowRx = new($@"^{SteamIdPattern}\s+(?<name>{Quote}[^{Quote}]{{1,64}}{Quote}|\S{{1,64}})", Options);

        private readonly RustInstaller _installer = new();
        private readonly Func<string, Task>? _logError;

        public RustProvider(Func<string, Task> logError) { _logError = logError; }

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

            // RCON is not optional: RustDedicated ignores stdin, so without it nothing can be sent to the
            // server — no commands, no roster poll, not even the graceful stop. It must be passed on the
            // command line (server.cfg is executed too late to configure the listener) and is bound to
            // loopback so it is never reachable from the network.
            var rcon = RustRconSettings.Ensure(inst);

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
                    "+rcon.ip", "127.0.0.1",
                    "+rcon.port", rcon.Port.ToString(CultureInfo.InvariantCulture),
                    "+rcon.password", rcon.Password,
                    "+rcon.web", "1",
                },
            };
            return Task.FromResult(spec);
        }

        public IRemoteConsole? CreateRemoteConsole(GameServerInstance inst)
        {
            // Written by BuildLaunchSpecAsync moments earlier; absent only if this instance was started
            // some other way, in which case we have no password and must not guess one.
            var rcon = RustRconSettings.Load(inst);
            return rcon == null ? null : new RustRconConsole(rcon.Port, rcon.Password, _logError);
        }

        public string GetGracefulStopCommand() => "quit";

        public bool TryParseStarted(string line)
            => line.Contains("Server startup complete", StringComparison.OrdinalIgnoreCase);

        public bool TryParsePlayerJoin(string line, out string player)
            => TryParsePlayer(line, out player, JoinSteamIdRx, JoinBracketRx, JoinSlashRx);

        public bool TryParsePlayerLeave(string line, out string player)
            => TryParsePlayer(line, out player, LeaveBracketRx, LeaveSlashRx);

        /// <summary>
        /// Parses a roster reply. Over RCON the whole reply arrives as one (multi-line) message, so this
        /// takes text rather than a single line: <c>playerlist</c> answers with a JSON array, while
        /// <c>status</c> — and playerlist on some builds — answers with a table.
        /// </summary>
        public bool TryParseListReply(string text, out int online, out int max, out string[] names)
        {
            online = 0;
            max = 0;
            names = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (!TryParseJsonRoster(text, out names) && !TryParseTableRoster(text, ref max, out names)) return false;
            online = names.Length;
            return true;
        }

        public string BuildListCommand() => "playerlist";

        /// <summary>The <c>playerlist</c> JSON array. An empty array is a valid answer — it is how the
        /// roster gets cleared when the last player leaves — so it is accepted only when it is the entire
        /// message, keeping ordinary bracketed log lines from being mistaken for a roster.</summary>
        private static bool TryParseJsonRoster(string text, out string[] names)
        {
            names = Array.Empty<string>();
            string trimmed = text.Trim();
            int start = trimmed.IndexOf('[');
            int end = trimmed.LastIndexOf(']');
            if (start < 0 || end <= start) return false;

            string json = trimmed[start..(end + 1)];
            bool empty = string.IsNullOrWhiteSpace(json[1..^1]);
            if (empty && json.Length != trimmed.Length) return false;
            if (!empty
                && !json.Contains("SteamID", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("DisplayName", StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                names = JArray.Parse(json)
                    .OfType<JObject>()
                    .Select(item => (string?)item["DisplayName"] ?? (string?)item["displayName"] ?? (string?)item["Name"] ?? (string?)item["name"])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return true;
            }
            catch { return false; }
        }

        /// <summary>The tabular roster: one row per player, keyed by SteamID, optionally under a
        /// "players : 1 (50 max)" header. Only ever applied to a multi-line reply, so no single log line
        /// can be mistaken for a roster and wipe the player list.</summary>
        private static bool TryParseTableRoster(string text, ref int max, out string[] names)
        {
            names = Array.Empty<string>();
            if (!text.Contains('\n')) return false;

            var found = new List<string>();
            bool sawHeader = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                var header = StatusPlayersRx.Match(line);
                if (header.Success)
                {
                    sawHeader = true;
                    if (int.TryParse(header.Groups["max"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMax))
                        max = parsedMax;
                    continue;
                }

                var row = TableRowRx.Match(line);
                if (!row.Success) continue;
                string name = row.Groups["name"].Value.Trim((char)34).Trim();
                if (name.Length > 0 && !found.Contains(name, StringComparer.OrdinalIgnoreCase)) found.Add(name);
            }

            if (!sawHeader && found.Count == 0) return false;
            names = found.ToArray();
            return true;
        }

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

        private static bool TryParsePlayer(string line, out string player, params Regex[] patterns)
        {
            player = "";
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (var pattern in patterns)
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                player = match.Groups["name"].Value.Trim();
                if (player.Length > 0) return true;
            }
            player = "";
            return false;
        }
    }
}
