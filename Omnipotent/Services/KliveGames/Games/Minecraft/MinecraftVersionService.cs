using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Minecraft
{
    /// <summary>
    /// Talks to the upstream metadata APIs to resolve versions and download URLs:
    ///   Vanilla → Mojang piston-meta version manifest v2
    ///   Paper   → fill.papermc.io v3
    ///   Fabric  → meta.fabricmc.net v2
    ///   Forge   → files.minecraftforge.net promotions + maven.minecraftforge.net
    /// Results are cached in-memory briefly. All requests carry an identifying User-Agent (required by Paper).
    /// </summary>
    public sealed class MinecraftVersionService
    {
        private const string UserAgent = "KliveGames/1.0 (+https://klive.dev)";
        private const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        private readonly HttpClient _http;
        private readonly Func<string, Task> _logError;
        private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(15);

        private readonly Dictionary<string, (DateTime at, object value)> _cache = new();
        private readonly object _cacheLock = new();

        public MinecraftVersionService(Func<string, Task> logError)
        {
            _logError = logError;
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        private async Task<T> Cached<T>(string key, Func<Task<T>> factory)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < _cacheTtl)
                    return (T)hit.value;
            }
            var value = await factory();
            lock (_cacheLock) { _cache[key] = (DateTime.UtcNow, value!); }
            return value;
        }

        private async Task<JToken> GetJsonAsync(string url, CancellationToken ct)
        {
            var text = await _http.GetStringAsync(url, ct);
            return JToken.Parse(text);
        }

        // ---------------- Vanilla (Mojang) ----------------

        public Task<IReadOnlyList<GameVersionInfo>> GetVanillaVersionsAsync(CancellationToken ct) =>
            Cached("vanilla-versions", async () =>
            {
                var json = await GetJsonAsync(MojangManifest, ct);
                var list = new List<GameVersionInfo>();
                foreach (var v in json["versions"] ?? new JArray())
                {
                    var type = (string?)v["type"] ?? "release";
                    if (type != "release") continue; // wizard surfaces stable releases only
                    list.Add(new GameVersionInfo { Version = (string?)v["id"] ?? "", Type = type });
                }
                return (IReadOnlyList<GameVersionInfo>)list;
            });

        /// <summary>Resolves a vanilla version's server.jar URL + sha1 + required Java major.</summary>
        public async Task<(string url, string sha1, int javaMajor)> GetVanillaServerAsync(string version, CancellationToken ct)
        {
            var manifest = await GetJsonAsync(MojangManifest, ct);
            var entry = (manifest["versions"] as JArray)?.FirstOrDefault(v => (string?)v["id"] == version)
                        ?? throw new Exception($"Minecraft version '{version}' not found in the Mojang manifest.");
            var versionJson = await GetJsonAsync((string)entry["url"]!, ct);
            var server = versionJson["downloads"]?["server"]
                         ?? throw new Exception($"Version '{version}' has no dedicated server download.");
            int javaMajor = (int?)versionJson["javaVersion"]?["majorVersion"] ?? JavaProvisioner.FallbackJavaMajor(version);
            return ((string)server["url"]!, (string?)server["sha1"] ?? "", javaMajor);
        }

        // ---------------- Paper ----------------

        public Task<IReadOnlyList<GameVersionInfo>> GetPaperVersionsAsync(CancellationToken ct) =>
            Cached("paper-versions", async () =>
            {
                var json = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", ct);
                // v3 exposes versions grouped by major; flatten to a descending list of version ids.
                var ids = new List<string>();
                var versionsNode = json["versions"];
                if (versionsNode is JObject grouped)
                {
                    foreach (var prop in grouped.Properties())
                        foreach (var ver in prop.Value)
                            ids.Add((string)ver!);
                }
                else if (versionsNode is JArray arr)
                {
                    foreach (var ver in arr) ids.Add((string)ver!);
                }
                var list = ids.Where(IsStableReleaseId)
                              .Distinct()
                              .OrderByDescending(v => v, VersionComparer)
                              .Select(v => new GameVersionInfo { Version = v })
                              .ToList();
                return (IReadOnlyList<GameVersionInfo>)list;
            });

        /// <summary>Picks the highest STABLE build for a Paper version and returns its download URL + sha256.</summary>
        public async Task<(int build, string url, string sha256)> GetPaperLatestBuildAsync(string version, CancellationToken ct)
        {
            var json = await GetJsonAsync($"https://fill.papermc.io/v3/projects/paper/versions/{version}/builds", ct);
            if (json is not JArray builds || builds.Count == 0)
                throw new Exception($"No Paper builds found for version '{version}'.");

            JToken? best = builds
                .Where(b => string.Equals((string?)b["channel"], "STABLE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => (int?)b["id"] ?? 0)
                .FirstOrDefault()
                ?? builds.OrderByDescending(b => (int?)b["id"] ?? 0).First();

            int buildId = (int?)best["id"] ?? 0;
            var dl = best["downloads"]?["server:default"]
                     ?? throw new Exception($"Paper build {buildId} has no server download.");
            return (buildId, (string)dl["url"]!, (string?)dl["checksums"]?["sha256"] ?? "");
        }

        // ---------------- Fabric ----------------

        public Task<IReadOnlyList<GameVersionInfo>> GetFabricVersionsAsync(CancellationToken ct) =>
            Cached("fabric-versions", async () =>
            {
                var json = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/game", ct);
                var list = new List<GameVersionInfo>();
                foreach (var v in json as JArray ?? new JArray())
                {
                    if ((bool?)v["stable"] != true) continue;
                    list.Add(new GameVersionInfo { Version = (string?)v["version"] ?? "" });
                }
                return (IReadOnlyList<GameVersionInfo>)list;
            });

        public async Task<string> GetFabricLatestLoaderAsync(CancellationToken ct)
        {
            var json = await Cached("fabric-loader", async () => await GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader", ct));
            var stable = (json as JArray)?.FirstOrDefault(l => (bool?)l["stable"] == true)
                         ?? (json as JArray)?.FirstOrDefault();
            return (string?)stable?["version"] ?? throw new Exception("No Fabric loader version available.");
        }

        public async Task<string> GetFabricLatestInstallerAsync(CancellationToken ct)
        {
            var json = await Cached("fabric-installer", async () => await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct));
            var stable = (json as JArray)?.FirstOrDefault(l => (bool?)l["stable"] == true)
                         ?? (json as JArray)?.FirstOrDefault();
            return (string?)stable?["version"] ?? throw new Exception("No Fabric installer version available.");
        }

        public string BuildFabricServerLauncherUrl(string game, string loader, string installer) =>
            $"https://meta.fabricmc.net/v2/versions/loader/{game}/{loader}/{installer}/server/jar";

        // ---------------- Forge ----------------

        /// <summary>mc version → best forge version (prefers "recommended", falls back to "latest").</summary>
        public Task<IReadOnlyDictionary<string, string>> GetForgePromotionsAsync(CancellationToken ct) =>
            Cached("forge-promos", async () =>
            {
                var json = await GetJsonAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
                var promos = json["promos"] as JObject ?? new JObject();
                var recommended = new Dictionary<string, string>();
                var latest = new Dictionary<string, string>();
                foreach (var p in promos.Properties())
                {
                    int dash = p.Name.LastIndexOf('-');
                    if (dash <= 0) continue;
                    string mc = p.Name.Substring(0, dash);
                    string kind = p.Name.Substring(dash + 1);
                    string forge = (string?)p.Value ?? "";
                    if (kind == "recommended") recommended[mc] = forge;
                    else if (kind == "latest") latest[mc] = forge;
                }
                var result = new Dictionary<string, string>(latest);
                foreach (var kv in recommended) result[kv.Key] = kv.Value; // recommended wins
                return (IReadOnlyDictionary<string, string>)result;
            });

        public async Task<IReadOnlyList<GameVersionInfo>> GetForgeVersionsAsync(CancellationToken ct)
        {
            var promos = await GetForgePromotionsAsync(ct);
            return promos
                .OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new GameVersionInfo { Version = kv.Key, LatestLoaderVersion = kv.Value })
                .ToList();
        }

        public string BuildForgeInstallerUrl(string mc, string forge) =>
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{forge}/forge-{mc}-{forge}-installer.jar";

        // Matches real Minecraft release ids like "1.21" or "1.21.4" (excludes rc/pre/snapshot/odd ids).
        private static bool IsStableReleaseId(string id)
            => !string.IsNullOrWhiteSpace(id) && System.Text.RegularExpressions.Regex.IsMatch(id, @"^1\.\d+(\.\d+)?$");

        private static readonly IComparer<string> VersionComparer = Comparer<string>.Create((a, b) =>
        {
            static int[] Parse(string s) => s.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pa = Parse(a); var pb = Parse(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        });

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
