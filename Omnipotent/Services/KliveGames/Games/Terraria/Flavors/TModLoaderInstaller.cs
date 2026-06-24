using System.IO.Compression;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Terraria.Flavors
{
    /// <summary>
    /// Downloads tModLoader from its GitHub release into the instance folder. The release ships its own
    /// launch scripts (which bootstrap the bundled .NET runtime on first run). Mods live in &lt;save&gt;/Mods.
    /// </summary>
    public static class TModLoaderInstaller
    {
        public static async Task InstallAsync(GameServerInstance inst, TerrariaVersionService versions, IProgress<string> progress, CancellationToken ct)
        {
            string tag = await versions.ResolveTModLoaderTagAsync(inst.Version, ct);
            inst.Version = tag;            // pin the resolved tag
            inst.LoaderVersion = tag;

            string url = versions.BuildTModLoaderUrl(tag);
            string zipPath = Path.Combine(inst.ServerDirectory, "_tmodloader-download.zip");

            progress.Report($"Downloading tModLoader {tag}…");
            await versions.DownloadFileAsync(url, zipPath, ct);

            progress.Report("Extracting tModLoader…");
            ZipFile.ExtractToDirectory(zipPath, inst.ServerDirectory, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }

            // Self-contained save dir (passed as -tmlsavedirectory at launch): ensure Mods + enabled.json.
            string modsDir = Path.Combine(inst.ServerDirectory, "Mods");
            Directory.CreateDirectory(modsDir);
            string enabled = Path.Combine(modsDir, "enabled.json");
            if (!File.Exists(enabled)) File.WriteAllText(enabled, "[]");

            string bat = Path.Combine(inst.ServerDirectory, "start-tModLoaderServer.bat");
            if (!File.Exists(bat))
                throw new Exception("start-tModLoaderServer.bat was not found in the tModLoader release.");

            inst.LaunchTarget = bat;
        }
    }
}
