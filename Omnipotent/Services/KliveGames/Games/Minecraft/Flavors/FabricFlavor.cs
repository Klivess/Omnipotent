using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Minecraft.Flavors
{
    public sealed class FabricFlavor : IMinecraftFlavor
    {
        public ServerFlavor Flavor => ServerFlavor.Fabric;

        public Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(MinecraftVersionService versions, CancellationToken ct)
            => versions.GetFabricVersionsAsync(ct);

        public async Task InstallAsync(GameServerInstance inst, MinecraftVersionService versions, JavaProvisioner java, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("Resolving Fabric loader/installer…");
            string loader = await versions.GetFabricLatestLoaderAsync(ct);
            string installer = await versions.GetFabricLatestInstallerAsync(ct);
            inst.LoaderVersion = loader;
            inst.JavaMajor = JavaProvisioner.FallbackJavaMajor(inst.Version);

            // The Fabric "server launcher" jar is self-contained: it downloads the matching vanilla
            // server jar and the loader libraries on its first launch (needs internet on first start).
            string url = versions.BuildFabricServerLauncherUrl(inst.Version, loader, installer);
            string jarPath = Path.Combine(inst.ServerDirectory, "fabric-server-launch.jar");
            progress.Report($"Downloading Fabric server launcher ({inst.Version}, loader {loader})…");
            await versions.DownloadFileAsync(url, jarPath, ct);

            inst.LaunchTarget = "fabric-server-launch.jar";
        }
    }
}
