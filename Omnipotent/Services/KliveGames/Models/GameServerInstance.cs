using Newtonsoft.Json;

namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// Persisted record describing one deployed game server. Serialized to
    /// <c>Instances/{Id}/instance.json</c>. Fields marked [JsonIgnore] are runtime-only live state
    /// that is recomputed by the monitor loop and never persisted.
    /// </summary>
    public class GameServerInstance
    {
        // ---- Identity ----
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        // ---- What it runs ----
        public GameType GameType { get; set; } = GameType.Minecraft;
        public ServerFlavor Flavor { get; set; } = ServerFlavor.Paper;
        /// <summary>Minecraft version, e.g. "1.21.4".</summary>
        public string Version { get; set; } = "";
        /// <summary>Flavor build number where applicable (Paper build, etc.). Null for Vanilla.</summary>
        public int? FlavorBuild { get; set; }
        /// <summary>Loader version for Fabric/Forge (e.g. Fabric loader "0.16.9" or Forge "54.1.0").</summary>
        public string? LoaderVersion { get; set; }
        /// <summary>Required Java major version (8/16/17/21), resolved at provisioning.</summary>
        public int JavaMajor { get; set; } = 21;

        // ---- Runtime configuration ----
        /// <summary>Local TCP port the server binds to (also mirrored into server.properties server-port).</summary>
        public int Port { get; set; } = 25565;
        /// <summary>Max heap in MB (-Xmx). -Xms is set to the same value.</summary>
        public int RamMb { get; set; } = 2048;
        /// <summary>Extra raw JVM args appended by the user.</summary>
        public string JvmArgs { get; set; } = "";
        /// <summary>Apply Aikar's GC flags (recommended for Paper/large servers).</summary>
        public bool UseAikarFlags { get; set; } = true;

        // ---- Behaviour ----
        public bool AutoStart { get; set; } = false;
        public bool AutoRestart { get; set; } = true;
        /// <summary>When true, KliveGames keeps a UPnP port-forward open for this server's port.</summary>
        public bool Public { get; set; } = false;

        // ---- State ----
        public GameServerStatus Status { get; set; } = GameServerStatus.Stopped;
        /// <summary>PID of the last spawned game process, used to reconcile orphans on app restart.</summary>
        public int? ChildPid { get; set; }

        // ---- Paths (relative to AppDomain base via OmniPaths.GetPath) ----
        /// <summary>Absolute path to the server's working directory (where the jar + world live).</summary>
        public string ServerDirectory { get; set; } = "";
        /// <summary>Launch target — the jar filename or args-file the launch command runs.</summary>
        public string LaunchTarget { get; set; } = "";

        /// <summary>Game-specific deploy-time options collected by the wizard (e.g. Terraria world size/
        /// difficulty). Read by the provider during <c>PrepareServerAsync</c>. Empty for Minecraft.</summary>
        public Dictionary<string, string> DeployOptions { get; set; } = new();

        // ---- Timestamps ----
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastStartedUtc { get; set; }

        // ---- Live runtime state (recomputed by the monitor loop; included in API responses).
        //      Persisted too, but never trusted across an app restart — Status is reconciled on boot. ----
        public List<string> OnlinePlayers { get; set; } = new();
        public int MaxPlayers { get; set; }
        public double CpuPercent { get; set; }
        public long RamUsedBytes { get; set; }
        public DateTime? RunningSinceUtc { get; set; }
        public string? LastError { get; set; }
        public string? PublicJoinAddress { get; set; }
    }
}
