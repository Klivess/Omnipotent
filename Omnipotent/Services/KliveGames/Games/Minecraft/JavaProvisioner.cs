using System.IO.Compression;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveGames.Games.Minecraft
{
    /// <summary>
    /// Downloads and caches an Eclipse Temurin JRE per Java major version (Windows x64) from the Adoptium
    /// API, so users never have to install Java. Each major is cached under Runtimes/jre-{major}/ and reused.
    /// </summary>
    public sealed class JavaProvisioner
    {
        private readonly HttpClient _http;
        private readonly Func<string, Task> _logError;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public JavaProvisioner(Func<string, Task> logError)
        {
            _logError = logError;
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("KliveGames/1.0 (+https://klive.dev)");
        }

        /// <summary>Best-effort mapping of a Minecraft version to its required Java major when the
        /// authoritative Mojang value is unavailable (e.g. for Paper/Fabric/Forge installs).</summary>
        public static int FallbackJavaMajor(string mcVersion)
        {
            // Parse "1.MINOR(.PATCH)".
            try
            {
                var parts = mcVersion.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int minor))
                {
                    int patch = parts.Length >= 3 && int.TryParse(parts[2], out int p) ? p : 0;
                    if (minor >= 21) return 21;
                    if (minor == 20) return patch >= 5 ? 21 : 17;
                    if (minor >= 18) return 17;
                    if (minor == 17) return 16;
                    return 8;
                }
            }
            catch { }
            return 17; // safe modern default
        }

        /// <summary>Ensures a Temurin JRE of the given major is available locally; returns the java.exe path.</summary>
        public async Task<string> EnsureJavaAsync(int major, IProgress<string>? progress, CancellationToken ct)
        {
            string baseDir = OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesRuntimesDirectory, $"jre-{major}"));

            string? existing = FindJavaExe(baseDir);
            if (existing != null) return existing;

            await _gate.WaitAsync(ct);
            try
            {
                existing = FindJavaExe(baseDir);
                if (existing != null) return existing;

                Directory.CreateDirectory(baseDir);
                progress?.Report($"Downloading Java {major} runtime…");

                string url = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
                string tempZip = Path.Combine(baseDir, $"temurin-{major}.zip");

                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                    await resp.Content.CopyToAsync(fs, ct);
                }

                progress?.Report($"Extracting Java {major} runtime…");
                ZipFile.ExtractToDirectory(tempZip, baseDir, overwriteFiles: true);
                try { File.Delete(tempZip); } catch { }

                existing = FindJavaExe(baseDir);
                if (existing == null)
                    throw new Exception($"Java {major} was downloaded but java.exe could not be located under {baseDir}.");

                return existing;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string? FindJavaExe(string baseDir)
        {
            if (!Directory.Exists(baseDir)) return null;
            try
            {
                // Prefer one under a /bin/ folder.
                return Directory.EnumerateFiles(baseDir, "java.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Replace('\\', '/').Contains("/bin/"))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
