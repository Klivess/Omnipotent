namespace Omnipotent.Services.KliveGames.Games.Minecraft
{
    /// <summary>
    /// Maps player-management actions to the Minecraft console commands that effect them (sent over stdin).
    /// </summary>
    public static class MinecraftPlayerManager
    {
        public static string? BuildCommand(string action, string player)
        {
            if (string.IsNullOrWhiteSpace(player)) return null;
            // Player names are validated against [A-Za-z0-9_] before reaching here.
            return action?.ToLowerInvariant() switch
            {
                "op" => $"op {player}",
                "deop" => $"deop {player}",
                "kick" => $"kick {player}",
                "ban" => $"ban {player}",
                "pardon" or "unban" => $"pardon {player}",
                "whitelist-add" => $"whitelist add {player}",
                "whitelist-remove" => $"whitelist remove {player}",
                _ => null,
            };
        }

        public static bool IsValidPlayerName(string player) =>
            !string.IsNullOrWhiteSpace(player)
            && player.Length <= 16
            && player.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
