using System.Text.RegularExpressions;
using Omnipotent.Services.KliveGames.Games.Terraria.Flavors;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Terraria
{
    /// <summary>
    /// Terraria provider — official vanilla dedicated server (TerrariaServer.exe) and tModLoader (modded).
    /// Native processes: no Java, no EULA, no heap arg. Worlds are auto-created on first boot from the
    /// deploy options. Reuses all the game-agnostic KliveGames machinery via <see cref="IGameProvider"/>.
    /// </summary>
    public sealed class TerrariaProvider : IGameProvider
    {
        private static readonly Regex JoinRx = new(@"^(.+?) has joined\.?\s*$", RegexOptions.Compiled);
        private static readonly Regex LeaveRx = new(@"^(.+?) has left\.?\s*$", RegexOptions.Compiled);

        private readonly TerrariaVersionService _versions;

        public TerrariaProvider(Func<string, Task> logError)
        {
            _versions = new TerrariaVersionService(logError);
        }

        public GameType GameType => GameType.Terraria;
        public string DisplayName => "Terraria";
        public bool Implemented => true;
        public string Protocol => "TCP";
        public int DefaultPort => 7777;
        public IReadOnlyList<ServerFlavor> SupportedFlavors => new[] { ServerFlavor.Vanilla, ServerFlavor.TModLoader };

        public bool RequiresEula => false;
        public bool UsesMemoryLimit => false;
        public string PortConfigKey => "port";
        public IReadOnlyList<string> SupportedPlayerActions => new[] { "kick", "ban" };

        public IReadOnlyList<ConfigSchemaField> GetDeployOptionsSchema(ServerFlavor flavor)
        {
            ConfigSchemaField F(string key, string label, ConfigFieldType type, string value, string? desc = null, params string[] options) =>
                new() { Key = key, Label = label, Type = type, Category = "World", Description = desc, Options = options.ToList(), Value = value };

            return new List<ConfigSchemaField>
            {
                F("worldName", "World Name", ConfigFieldType.Text, "", "Defaults to the server name."),
                F("worldSize", "World Size", ConfigFieldType.Dropdown, "Medium", null, "Small", "Medium", "Large"),
                F("difficulty", "Difficulty", ConfigFieldType.Dropdown, "Classic", null, "Classic", "Expert", "Master", "Journey"),
                F("maxPlayers", "Max Players", ConfigFieldType.Number, "8", "1–255."),
                F("password", "Password", ConfigFieldType.Text, "", "Optional — leave blank for open join."),
            };
        }

        public async Task<IReadOnlyList<GameVersionInfo>> GetAvailableVersionsAsync(ServerFlavor flavor, CancellationToken ct)
        {
            if (flavor == ServerFlavor.TModLoader)
            {
                var list = new List<GameVersionInfo> { new() { Version = "Latest" } };
                list.AddRange(await _versions.GetTModLoaderVersionsAsync(ct));
                return list;
            }
            return _versions.GetVanillaVersions();
        }

        public async Task PrepareServerAsync(GameServerInstance inst, IProgress<string> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(inst.ServerDirectory);

            if (inst.Flavor == ServerFlavor.TModLoader)
                await TModLoaderInstaller.InstallAsync(inst, _versions, progress, ct);
            else
                await VanillaTerrariaInstaller.InstallAsync(inst, _versions, progress, ct);

            // Build a fully non-interactive serverconfig.txt from the deploy options (world auto-creates).
            var opt = inst.DeployOptions ?? new();
            string worldName = opt.TryGetValue("worldName", out var wn) && !string.IsNullOrWhiteSpace(wn) ? wn : inst.Name;
            int size = opt.TryGetValue("worldSize", out var ws) ? ws.ToLowerInvariant() switch { "small" => 1, "large" => 3, _ => 2 } : 2;
            int diff = opt.TryGetValue("difficulty", out var d) ? d.ToLowerInvariant() switch { "expert" => 1, "master" => 2, "journey" => 3, _ => 0 } : 0;
            int maxp = opt.TryGetValue("maxPlayers", out var mp) && int.TryParse(mp, out var mpi) ? mpi : 8;
            string? pw = opt.TryGetValue("password", out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;
            string? seed = opt.TryGetValue("seed", out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

            TerrariaServerConfig.WriteDefault(inst.ServerDirectory, inst.Port, worldName, size, diff, maxp, pw, seed);

            progress.Report("Server provisioned and ready to start.");
        }

        public Task<LaunchSpec> BuildLaunchSpecAsync(GameServerInstance inst, CancellationToken ct)
        {
            LaunchSpec spec;
            if (inst.Flavor == ServerFlavor.TModLoader)
            {
                // The tModLoader launch script bootstraps the bundled .NET runtime; run it through cmd so
                // stdin (console commands incl. "exit") is forwarded to the child dotnet process.
                spec = new LaunchSpec
                {
                    Executable = "cmd.exe",
                    Arguments = new List<string>
                    {
                        "/c", inst.LaunchTarget,
                        "-config", TerrariaServerConfig.FileName,
                        "-nosteam",
                        "-tmlsavedirectory", inst.ServerDirectory,
                    },
                    WorkingDirectory = inst.ServerDirectory,
                    GracefulStopCommand = "exit",
                };
            }
            else
            {
                spec = new LaunchSpec
                {
                    Executable = inst.LaunchTarget, // absolute path to TerrariaServer.exe
                    Arguments = new List<string> { "-config", TerrariaServerConfig.FileName },
                    WorkingDirectory = inst.ServerDirectory,
                    GracefulStopCommand = "exit",
                };
            }
            return Task.FromResult(spec);
        }

        public string GetGracefulStopCommand() => "exit";

        public bool TryParseStarted(string line)
            => line.Contains("Server started", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Listening on", StringComparison.OrdinalIgnoreCase);

        public bool TryParsePlayerJoin(string line, out string player)
        {
            var m = JoinRx.Match(line);
            player = m.Success ? m.Groups[1].Value.Trim() : "";
            return m.Success;
        }

        public bool TryParsePlayerLeave(string line, out string player)
        {
            var m = LeaveRx.Match(line);
            player = m.Success ? m.Groups[1].Value.Trim() : "";
            return m.Success;
        }

        // Terraria's "playing" reply isn't cleanly structured, so roster comes from join/leave parsing.
        public bool TryParseListReply(string line, out int online, out int max, out string[] names)
        {
            online = 0; max = 0; names = Array.Empty<string>();
            return false;
        }

        public string BuildListCommand() => ""; // disables periodic roster polling

        public string? BuildPlayerActionCommand(string action, string player)
            => TerrariaPlayerManager.BuildCommand(action, player);

        public IReadOnlyList<ConfigSchemaField> GetConfigSchema(GameServerInstance inst)
            => TerrariaServerConfig.GetSchema(inst.ServerDirectory);

        public Task ApplyConfigAsync(GameServerInstance inst, Dictionary<string, string> values)
        {
            TerrariaServerConfig.Save(inst.ServerDirectory, values);
            return Task.CompletedTask;
        }
    }
}
