namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// A selectable version (and optional build/loader) presented to the deploy wizard.
    /// </summary>
    public class GameVersionInfo
    {
        /// <summary>Minecraft version id, e.g. "1.21.4".</summary>
        public string Version { get; set; } = "";

        /// <summary>"release" or "snapshot" (Vanilla); flavors generally only surface releases.</summary>
        public string Type { get; set; } = "release";

        /// <summary>Newest stable build number for this version (Paper) — null when not applicable.</summary>
        public int? LatestBuild { get; set; }

        /// <summary>Newest stable loader version (Fabric/Forge) — null when not applicable.</summary>
        public string? LatestLoaderVersion { get; set; }

        /// <summary>Java major required to run this version (best-effort; resolved precisely at deploy).</summary>
        public int? RecommendedJavaMajor { get; set; }
    }
}
