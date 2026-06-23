using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Minecraft.Flavors
{
    /// <summary>
    /// Per-flavor strategy (Vanilla/Paper/Fabric/Forge). Responsible only for resolving versions and
    /// installing the server files; it sets <see cref="GameServerInstance.LaunchTarget"/>,
    /// <see cref="GameServerInstance.JavaMajor"/>, FlavorBuild/LoaderVersion. The provider builds the
    /// uniform Java launch command from LaunchTarget (a jar filename, or an "@argfile" token).
    /// </summary>
    public interface IMinecraftFlavor
    {
        ServerFlavor Flavor { get; }

        Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(MinecraftVersionService versions, CancellationToken ct);

        Task InstallAsync(
            GameServerInstance inst,
            MinecraftVersionService versions,
            JavaProvisioner java,
            IProgress<string> progress,
            CancellationToken ct);
    }
}
