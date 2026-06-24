using System.IO.Compression;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Terraria.Flavors
{
    /// <summary>
    /// Downloads the official Terraria dedicated server zip from terraria.org and flattens the bundled
    /// Windows build into the instance folder, so TerrariaServer.exe sits beside serverconfig.txt + worlds/.
    /// </summary>
    public static class VanillaTerrariaInstaller
    {
        public static async Task InstallAsync(GameServerInstance inst, TerrariaVersionService versions, IProgress<string> progress, CancellationToken ct)
        {
            string code = versions.GetVanillaCode(inst.Version);
            string url = versions.BuildVanillaUrl(code);
            string zipPath = Path.Combine(inst.ServerDirectory, "_terraria-download.zip");
            string tmp = Path.Combine(inst.ServerDirectory, "_extract");

            progress.Report($"Downloading Terraria dedicated server {inst.Version}…");
            await versions.DownloadFileAsync(url, zipPath, ct);

            progress.Report("Extracting server files…");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zipPath, tmp);

            // The zip lays out <code>/Windows/TerrariaServer.exe (+ Linux/Mac). Flatten the Windows build.
            string? winDir = Directory.EnumerateDirectories(tmp, "Windows", SearchOption.AllDirectories).FirstOrDefault();
            if (winDir == null)
            {
                // Some builds put the exe directly under the version folder.
                winDir = Directory.EnumerateFiles(tmp, "TerrariaServer.exe", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName).FirstOrDefault();
            }
            if (winDir == null)
                throw new Exception("Could not find the Windows TerrariaServer.exe in the downloaded archive.");

            CopyDirectory(winDir, inst.ServerDirectory);

            try { Directory.Delete(tmp, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            string exe = Path.Combine(inst.ServerDirectory, "TerrariaServer.exe");
            if (!File.Exists(exe))
                throw new Exception("TerrariaServer.exe was not present after extraction.");

            inst.LaunchTarget = exe;
            inst.FlavorBuild = int.TryParse(code, out var c) ? c : null;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
