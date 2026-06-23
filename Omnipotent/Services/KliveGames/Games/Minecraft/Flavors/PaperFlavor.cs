using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames.Games.Minecraft.Flavors
{
    public sealed class PaperFlavor : IMinecraftFlavor
    {
        public ServerFlavor Flavor => ServerFlavor.Paper;

        public Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(MinecraftVersionService versions, CancellationToken ct)
            => versions.GetPaperVersionsAsync(ct);

        public async Task InstallAsync(GameServerInstance inst, MinecraftVersionService versions, JavaProvisioner java, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report($"Resolving latest Paper build for {inst.Version}…");
            var (build, url, sha256) = await versions.GetPaperLatestBuildAsync(inst.Version, ct);
            inst.FlavorBuild = build;
            inst.JavaMajor = await versions.GetJavaMajorForVersionAsync(inst.Version, ct);

            string jarPath = Path.Combine(inst.ServerDirectory, "server.jar");
            progress.Report($"Downloading Paper {inst.Version} build {build}…");
            await versions.DownloadFileAsync(url, jarPath, ct);
            FileHashing.VerifyOrThrow(jarPath, expectedSha256: sha256);

            inst.LaunchTarget = "server.jar";
        }
    }
}
