using System.Text.RegularExpressions;
using Omnipotent.Services.KliveGames.Games.Minecraft.Flavors;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Minecraft
{
    /// <summary>
    /// Minecraft (Java Edition) provider. Delegates flavor-specific install/launch-target work to
    /// <see cref="IMinecraftFlavor"/> strategies, and owns everything common: Java provisioning, the EULA,
    /// server.properties, building the uniform Java launch command, and parsing the server console.
    /// </summary>
    public sealed class MinecraftProvider : IGameProvider
    {
        private static readonly Regex StartedRx = new(@"Done \(([0-9.]+)s\)!", RegexOptions.Compiled);
        private static readonly Regex JoinRx = new(@"([A-Za-z0-9_]{1,16})\[/[^\]]+\] logged in with entity id", RegexOptions.Compiled);
        private static readonly Regex LeaveRx = new(@"([A-Za-z0-9_]{1,16}) (?:left the game|lost connection)", RegexOptions.Compiled);
        private static readonly Regex ListRx = new(@"There are (\d+) of a max of (\d+) players online:\s*(.*)$", RegexOptions.Compiled);

        // Aikar's flags — the community-standard GC tuning for Minecraft servers.
        private static readonly string[] AikarFlags =
        {
            "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions", "-XX:+DisableExplicitGC", "-XX:+AlwaysPreTouch",
            "-XX:G1NewSizePercent=30", "-XX:G1MaxNewSizePercent=40", "-XX:G1HeapRegionSize=8M",
            "-XX:G1ReservePercent=20", "-XX:G1HeapWastePercent=5", "-XX:G1MixedGCCountTarget=4",
            "-XX:InitiatingHeapOccupancyPercent=15", "-XX:G1MixedGCLiveThresholdPercent=90",
            "-XX:G1RSetUpdatingPauseTimePercent=5", "-XX:SurvivorRatio=32", "-XX:+PerfDisableSharedMem",
            "-XX:MaxTenuringThreshold=1", "-Dusing.aikars.flags=https://mcflags.emc.gs", "-Daikars.new.flags=true",
        };

        private readonly MinecraftVersionService _versions;
        private readonly JavaProvisioner _java;
        private readonly Dictionary<ServerFlavor, IMinecraftFlavor> _flavors;

        public MinecraftProvider(Func<string, Task> logError)
        {
            _versions = new MinecraftVersionService(logError);
            _java = new JavaProvisioner(logError);
            _flavors = new IMinecraftFlavor[]
            {
                new VanillaFlavor(), new PaperFlavor(), new FabricFlavor(), new ForgeFlavor(),
            }.ToDictionary(f => f.Flavor);
        }

        public GameType GameType => GameType.Minecraft;
        public string DisplayName => "Minecraft (Java Edition)";
        public bool Implemented => true;
        public string Protocol => "TCP";
        public int DefaultPort => 25565;
        public IReadOnlyList<ServerFlavor> SupportedFlavors => _flavors.Keys.ToList();

        public Task<IReadOnlyList<GameVersionInfo>> GetAvailableVersionsAsync(ServerFlavor flavor, CancellationToken ct)
            => GetFlavor(flavor).GetVersionsAsync(_versions, ct);

        public async Task PrepareServerAsync(GameServerInstance inst, IProgress<string> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(inst.ServerDirectory);

            // 1. Flavor install (downloads jars / runs loader installer, sets LaunchTarget + JavaMajor).
            await GetFlavor(inst.Flavor).InstallAsync(inst, _versions, _java, progress, ct);

            // 2. Pre-cache Java so the first start is instant.
            progress.Report($"Ensuring Java {inst.JavaMajor} runtime…");
            await _java.EnsureJavaAsync(inst.JavaMajor, progress, ct);

            // 3. Accept the EULA (the operator accepted it in the deploy wizard).
            File.WriteAllText(Path.Combine(inst.ServerDirectory, "eula.txt"),
                "#EULA accepted via KliveGames deploy wizard\n#https://aka.ms/MinecraftEULA\neula=true\n");

            // 4. Write a sensible default server.properties if one doesn't exist yet.
            string propsPath = Path.Combine(inst.ServerDirectory, MinecraftServerProperties.FileName);
            if (!File.Exists(propsPath))
                MinecraftServerProperties.WriteDefault(inst.ServerDirectory, inst.Port, $"{inst.Name} — powered by KliveGames");

            progress.Report("Server provisioned and ready to start.");
        }

        public async Task<LaunchSpec> BuildLaunchSpecAsync(GameServerInstance inst, CancellationToken ct)
        {
            // Reconcile the required Java major against the authoritative source on every launch, so a
            // server can never start under the wrong runtime (self-heals instances provisioned earlier).
            try
            {
                int major = await _versions.GetJavaMajorForVersionAsync(inst.Version, ct);
                if (major > 0) inst.JavaMajor = major;
            }
            catch { /* offline — fall back to the stored major */ }

            string javaExe = await _java.EnsureJavaAsync(inst.JavaMajor, null, ct);

            var args = new List<string>();
            args.Add($"-Xmx{inst.RamMb}M");
            args.Add($"-Xms{inst.RamMb}M");
            if (inst.UseAikarFlags) args.AddRange(AikarFlags);
            if (!string.IsNullOrWhiteSpace(inst.JvmArgs))
                args.AddRange(SplitArgs(inst.JvmArgs));

            // LaunchTarget is either an "@argfile" token (modern Forge) or a jar filename.
            if (inst.LaunchTarget.StartsWith("@"))
            {
                args.Add(inst.LaunchTarget);
                args.Add("nogui");
            }
            else
            {
                args.Add("-jar");
                args.Add(inst.LaunchTarget);
                args.Add("nogui");
            }

            return new LaunchSpec
            {
                Executable = javaExe,
                Arguments = args,
                WorkingDirectory = inst.ServerDirectory,
                GracefulStopCommand = "stop",
            };
        }

        public string GetGracefulStopCommand() => "stop";

        public bool TryParseStarted(string line) => StartedRx.IsMatch(line);

        public bool TryParsePlayerJoin(string line, out string player)
        {
            var m = JoinRx.Match(line);
            player = m.Success ? m.Groups[1].Value : "";
            return m.Success;
        }

        public bool TryParsePlayerLeave(string line, out string player)
        {
            var m = LeaveRx.Match(line);
            player = m.Success ? m.Groups[1].Value : "";
            return m.Success;
        }

        public bool TryParseListReply(string line, out int online, out int max, out string[] names)
        {
            online = 0; max = 0; names = Array.Empty<string>();
            var m = ListRx.Match(line);
            if (!m.Success) return false;
            int.TryParse(m.Groups[1].Value, out online);
            int.TryParse(m.Groups[2].Value, out max);
            names = m.Groups[3].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(MinecraftPlayerManager.IsValidPlayerName)
                .ToArray();
            return true;
        }

        public string BuildListCommand() => "list";

        public string? BuildPlayerActionCommand(string action, string player)
            => MinecraftPlayerManager.IsValidPlayerName(player) ? MinecraftPlayerManager.BuildCommand(action, player) : null;

        public IReadOnlyList<ConfigSchemaField> GetConfigSchema(GameServerInstance inst)
            => MinecraftServerProperties.GetSchema(inst.ServerDirectory);

        public Task ApplyConfigAsync(GameServerInstance inst, Dictionary<string, string> values)
        {
            // Keep query.port aligned with server-port when the latter is edited.
            if (values.TryGetValue("server-port", out var sp) && int.TryParse(sp, out _))
                values["query.port"] = sp;

            MinecraftServerProperties.Save(inst.ServerDirectory, values);
            return Task.CompletedTask;
        }

        private IMinecraftFlavor GetFlavor(ServerFlavor flavor)
        {
            if (!_flavors.TryGetValue(flavor, out var f))
                throw new InvalidOperationException($"Unsupported Minecraft flavor: {flavor}.");
            return f;
        }

        /// <summary>Splits a raw JVM-args string on whitespace, honoring simple double-quoted groups.</summary>
        private static IEnumerable<string> SplitArgs(string raw)
        {
            var matches = Regex.Matches(raw, @"[\""].+?[\""]|[^ ]+");
            foreach (Match m in matches)
                yield return m.Value.Trim('"');
        }
    }
}
