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
