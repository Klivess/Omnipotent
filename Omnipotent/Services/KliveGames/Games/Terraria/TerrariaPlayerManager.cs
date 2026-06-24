namespace Omnipotent.Services.KliveGames.Games.Terraria
{
    /// <summary>
    /// Maps player actions to Terraria console commands. Terraria only supports kick/ban (no op/whitelist).
    /// </summary>
    public static class TerrariaPlayerManager
    {
        public static string? BuildCommand(string action, string player)
        {
            string name = (player ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (name.Length == 0) return null;
            return action?.ToLowerInvariant() switch
            {
                "kick" => $"kick {name}",
                "ban" => $"ban {name}",
                _ => null,
            };
        }
    }
}
