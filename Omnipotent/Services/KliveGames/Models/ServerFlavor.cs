namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// The server software flavor for a Minecraft instance. Each flavor knows how to resolve
    /// versions, download/install itself, and build its launch command (see the Minecraft Flavors folder).
    /// </summary>
    public enum ServerFlavor
    {
        /// <summary>Official Mojang server jar.</summary>
        Vanilla = 0,
        /// <summary>PaperMC — high-performance fork with Bukkit/Spigot plugin support.</summary>
        Paper = 1,
        /// <summary>Fabric mod loader (lightweight, modern mods).</summary>
        Fabric = 2,
        /// <summary>Minecraft Forge mod loader (classic modpacks).</summary>
        Forge = 3,
    }
}
