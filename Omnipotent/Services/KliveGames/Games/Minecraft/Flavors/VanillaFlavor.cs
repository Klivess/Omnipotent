using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames.Games.Minecraft.Flavors
{
    public sealed class VanillaFlavor : IMinecraftFlavor
    {
        public ServerFlavor Flavor => ServerFlavor.Vanilla;

        public Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(MinecraftVersionService versions, CancellationToken ct)
            => versions.GetVanillaVersionsAsync(ct);

        public async Task InstallAsync(GameServerInstance inst, MinecraftVersionService versions, JavaProvisioner java, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report($"Resolving Vanilla {inst.Version} server download…");
            var (url, sha1, javaMajor) = await versions.GetVanillaServerAsync(inst.Version, ct);
            inst.JavaMajor = javaMajor;

            string jarPath = Path.Combine(inst.ServerDirectory, "server.jar");
            progress.Report("Downloading Vanilla server.jar…");
            await versions.DownloadFileAsync(url, jarPath, ct);
            FileHashing.VerifyOrThrow(jarPath, expectedSha1: sha1);

            inst.LaunchTarget = "server.jar";
            inst.FlavorBuild = null;
        }
    }
}
