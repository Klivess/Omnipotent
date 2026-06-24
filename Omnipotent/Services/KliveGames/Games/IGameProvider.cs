using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games
{
    /// <summary>
    /// Abstraction over a single game. Everything game-specific (how to find versions, download/install,
    /// build the launch command, parse the console, configure the server) lives behind this so new games
    /// (Terraria, Satisfactory, …) can be added without touching the orchestrator. Minecraft is implemented;
    /// other <see cref="GameType"/> values exist as not-yet-implemented placeholders.
    /// </summary>
    public interface IGameProvider
    {
        GameType GameType { get; }
        string DisplayName { get; }

        /// <summary>Whether this provider is fully implemented (false => surfaced as "coming soon").</summary>
        bool Implemented { get; }

        /// <summary>Transport protocol for port-forwarding ("TCP"/"UDP").</summary>
        string Protocol { get; }

        /// <summary>Conventional default port (e.g. 25565 for Minecraft Java).</summary>
        int DefaultPort { get; }

        IReadOnlyList<ServerFlavor> SupportedFlavors { get; }

        // ---- Capabilities (let the orchestrator/UI stay game-agnostic) ----

        /// <summary>Whether deployment requires accepting an EULA (Minecraft true, Terraria false).</summary>
        bool RequiresEula { get; }

        /// <summary>Whether this game uses a configurable memory limit / JVM heap (controls RAM + JVM UI).</summary>
        bool UsesMemoryLimit { get; }

        /// <summary>The config key that mirrors the listen port (e.g. "server-port" / "port") so the
        /// orchestrator can keep <c>inst.Port</c> in sync when the user edits config.</summary>
        string PortConfigKey { get; }

        /// <summary>Player-management actions this game supports (subset of op/deop/kick/ban/pardon/whitelist-*).</summary>
        IReadOnlyList<string> SupportedPlayerActions { get; }

        /// <summary>Extra fields the deploy wizard should collect for this game (e.g. Terraria world
        /// size/difficulty). Returned values arrive back in the create request's Options and are stored
        /// on the instance for <see cref="PrepareServerAsync"/>. Empty for Minecraft.</summary>
        IReadOnlyList<ConfigSchemaField> GetDeployOptionsSchema(ServerFlavor flavor);

        /// <summary>Lists deployable versions (+ latest build/loader) for the deploy wizard.</summary>
        Task<IReadOnlyList<GameVersionInfo>> GetAvailableVersionsAsync(ServerFlavor flavor, CancellationToken ct);

        /// <summary>Downloads/installs everything (server files + Java + loader), accepts EULA, writes
        /// default config. Reports human-readable progress strings.</summary>
        Task PrepareServerAsync(GameServerInstance inst, IProgress<string> progress, CancellationToken ct);

        /// <summary>Resolves the full process launch spec (incl. the provisioned Java path).</summary>
        Task<LaunchSpec> BuildLaunchSpecAsync(GameServerInstance inst, CancellationToken ct);

        string GetGracefulStopCommand();

        // ---- Console parsing ----
        bool TryParseStarted(string line);
        bool TryParsePlayerJoin(string line, out string player);
        bool TryParsePlayerLeave(string line, out string player);
        bool TryParseListReply(string line, out int online, out int max, out string[] names);
        string BuildListCommand();

        /// <summary>Maps a UI action ("op"/"deop"/"kick"/"ban"/"pardon"/"whitelist-add"/"whitelist-remove")
        /// to a console command, or null if unsupported.</summary>
        string? BuildPlayerActionCommand(string action, string player);

        // ---- Configuration ----
        IReadOnlyList<ConfigSchemaField> GetConfigSchema(GameServerInstance inst);
        Task ApplyConfigAsync(GameServerInstance inst, Dictionary<string, string> values);
    }
}
