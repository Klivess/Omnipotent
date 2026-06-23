using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.Stratum;

namespace Omnipotent.Services.KliveGames.Games.Minecraft.Flavors
{
    /// <summary>
    /// Forge is the most involved flavor: we download the installer and run it with --installServer,
    /// then detect how this Forge version expects to be launched. Modern Forge (1.17+) generates an
    /// "@args" file under libraries/; legacy Forge produces a runnable universal jar.
    /// </summary>
    public sealed class ForgeFlavor : IMinecraftFlavor
    {
        public ServerFlavor Flavor => ServerFlavor.Forge;

        public Task<IReadOnlyList<GameVersionInfo>> GetVersionsAsync(MinecraftVersionService versions, CancellationToken ct)
            => versions.GetForgeVersionsAsync(ct);

        public async Task InstallAsync(GameServerInstance inst, MinecraftVersionService versions, JavaProvisioner java, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("Resolving Forge version…");
            var promos = await versions.GetForgePromotionsAsync(ct);
            if (!promos.TryGetValue(inst.Version, out var forgeVersion) || string.IsNullOrWhiteSpace(forgeVersion))
                throw new Exception($"No Forge build is available for Minecraft {inst.Version}.");

            inst.LoaderVersion = forgeVersion;
            inst.JavaMajor = JavaProvisioner.FallbackJavaMajor(inst.Version);

            // Forge's installer is a Java program, so we need Java before we can install.
            progress.Report("Preparing Java for the Forge installer…");
            string javaExe = await java.EnsureJavaAsync(inst.JavaMajor, progress, ct);

            string installerUrl = versions.BuildForgeInstallerUrl(inst.Version, forgeVersion);
            string installerPath = Path.Combine(inst.ServerDirectory, "forge-installer.jar");
            progress.Report($"Downloading Forge {inst.Version}-{forgeVersion} installer…");
            await versions.DownloadFileAsync(installerUrl, installerPath, ct);

            progress.Report("Running Forge installer (--installServer)… this can take a few minutes.");
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(
                javaExe,
                new[] { "-jar", "forge-installer.jar", "--installServer" },
                inst.ServerDirectory,
                TimeSpan.FromMinutes(10),
                line => progress.Report(line),
                line => progress.Report(line),
                ct);

            if (exit != 0)
                throw new Exception($"Forge installer exited with code {exit}. Tail: {Tail(stderr + "\n" + stdout)}");

            inst.LaunchTarget = DetectLaunchTarget(inst, forgeVersion)
                ?? throw new Exception("Forge installed but no launch target (args file or universal jar) was found.");

            // Tidy up the installer + its log.
            TryDelete(installerPath);
            TryDelete(installerPath + ".log");
        }

        /// <summary>Finds the modern "@args" file or the legacy universal jar produced by the installer.</summary>
        private static string? DetectLaunchTarget(GameServerInstance inst, string forgeVersion)
        {
            string dir = inst.ServerDirectory;

            // Modern Forge (1.17+): libraries/net/minecraftforge/forge/{mc}-{forge}/win_args.txt
            string relArgs = Path.Combine("libraries", "net", "minecraftforge", "forge", $"{inst.Version}-{forgeVersion}", "win_args.txt");
            if (File.Exists(Path.Combine(dir, relArgs)))
                return "@" + relArgs.Replace('\\', '/');

            // Some versions use unix_args.txt naming even on Windows — accept either.
            string relUnix = Path.Combine("libraries", "net", "minecraftforge", "forge", $"{inst.Version}-{forgeVersion}", "unix_args.txt");
            if (File.Exists(Path.Combine(dir, relUnix)))
                return "@" + relUnix.Replace('\\', '/');

            // Legacy Forge: a runnable forge-*.jar (universal) in the server root.
            var jar = Directory.EnumerateFiles(dir, "forge-*.jar", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(f => f != null && !f.Contains("installer", StringComparison.OrdinalIgnoreCase));
            return jar;
        }

        private static string Tail(string s, int max = 600) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(s.Length - max));

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
