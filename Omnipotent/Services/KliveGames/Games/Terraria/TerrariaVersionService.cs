using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Terraria
{
    /// <summary>
    /// Resolves Terraria versions/downloads. Vanilla has no public version API, so we keep a curated list
    /// of dedicated-server version codes (newest first). tModLoader versions come from its GitHub releases.
    /// </summary>
    public sealed class TerrariaVersionService
    {
        private const string UserAgent = "KliveGames/1.0 (+https://klive.dev)";

        // Display version -> terraria.org dedicated-server version code (e.g. 1.4.4.9 -> 1449). Newest first.
        private static readonly (string Version, string Code)[] VanillaVersions =
        {
            ("1.4.4.9", "1449"),
            ("1.4.4", "1444"),
            ("1.4.3.6", "1436"),
            ("1.4.2.3", "1423"),
            ("1.4.0.5", "1405"),
            ("1.3.5.3", "1353"),
        };

        private readonly HttpClient _http;
        private readonly Func<string, Task> _logError;
        private (DateTime at, IReadOnlyList<GameVersionInfo> list)? _tmlCache;
        private readonly object _lock = new();

        public TerrariaVersionService(Func<string, Task> logError)
        {
            _logError = logError;
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        // ---------------- Vanilla ----------------

        public IReadOnlyList<GameVersionInfo> GetVanillaVersions()
            => VanillaVersions.Select(v => new GameVersionInfo { Version = v.Version }).ToList();

        public string GetVanillaCode(string version)
        {
            foreach (var v in VanillaVersions)
                if (string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase)) return v.Code;
            // Fallback: strip dots ("1.4.4.9" -> "1449").
            var digits = new string(version.Where(char.IsDigit).ToArray());
            if (digits.Length >= 3) return digits;
            return VanillaVersions[0].Code;
        }

        public string BuildVanillaUrl(string code)
            => $"https://terraria.org/api/download/pc-dedicated-server/terraria-server-{code}.zip";

        // ---------------- tModLoader ----------------

        public async Task<IReadOnlyList<GameVersionInfo>> GetTModLoaderVersionsAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_tmlCache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(15)) return c.list;
            }

            try
            {
                var text = await _http.GetStringAsync("https://api.github.com/repos/tModLoader/tModLoader/releases?per_page=15", ct);
                var arr = JArray.Parse(text);
                var list = arr
                    .Where(r => (bool?)r["prerelease"] != true && (bool?)r["draft"] != true)
                    .Select(r => (string?)r["tag_name"])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => new GameVersionInfo { Version = t! })
                    .ToList();
                lock (_lock) { _tmlCache = (DateTime.UtcNow, list); }
                return list;
            }
            catch (Exception ex)
            {
                await _logError($"Failed to list tModLoader releases: {ex.Message}");
                return Array.Empty<GameVersionInfo>();
            }
        }

        /// <summary>Resolves a requested tModLoader version ("Latest"/blank => newest release tag).</summary>
        public async Task<string> ResolveTModLoaderTagAsync(string requested, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(requested) && !requested.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                return requested;
            var versions = await GetTModLoaderVersionsAsync(ct);
            if (versions.Count > 0) return versions[0].Version;
            // Last resort: query the latest release directly.
            var text = await _http.GetStringAsync("https://api.github.com/repos/tModLoader/tModLoader/releases/latest", ct);
            return (string?)JObject.Parse(text)["tag_name"] ?? throw new Exception("Could not resolve a tModLoader version.");
        }

        public string BuildTModLoaderUrl(string tag)
            => $"https://github.com/tModLoader/tModLoader/releases/download/{tag}/tModLoader.zip";

        // ---------------- shared ----------------

        public async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs, ct);
        }
    }
}
