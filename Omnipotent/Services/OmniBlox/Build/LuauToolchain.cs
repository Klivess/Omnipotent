using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>
    /// The real Luau type checker: luau-lsp's standalone analyzer (open source, MIT) with Roblox's API type
    /// definitions. Installed on first use under the studio's data folder from a pinned release, and only
    /// after its SHA-256 matches the hash pinned here, so the server never runs anything it did not expect.
    /// Without it the studio still builds, with only its own syntax checker (and says so in build reports).
    /// </summary>
    public sealed class LuauToolchain
    {
        public const string Version = "1.70.1";
        private const string ReleaseBase = "https://github.com/JohnnyMorganz/luau-lsp/releases/download/" + Version + "/";
        private const string DefinitionsUrl = "https://raw.githubusercontent.com/JohnnyMorganz/luau-lsp/" + Version + "/scripts/globalTypes.None.d.luau";

        private static readonly (string File, string Sha256) WindowsBuild = ("luau-lsp-win64.zip", "64225b9738102a30de6ee09fb28e5a3827edc402ecf2a6364af570312468159f");
        private static readonly (string File, string Sha256) LinuxBuild = ("luau-lsp-linux-x86_64.zip", "1a2ea1ae4f98f8946cefd970a4b54853e11ab4775e6932faa7f60cf920346567");
        private const string DefinitionsSha256 = "9ad75109b8dc9197f6d1726208bfa0b12de47c3a43672a4fa43227f0cec609ee";

        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

        private readonly string directory;
        private readonly Action<string> log;
        private readonly SemaphoreSlim gate = new(1, 1);
        private DateTime lastFailureUtc = DateTime.MinValue;

        public LuauToolchain(string toolsDirectory, Action<string>? log = null)
        {
            directory = Path.Combine(Path.GetFullPath(toolsDirectory), "luau-lsp-" + Version);
            this.log = log ?? (_ => { });
        }

        /// <summary>A toolchain at fixed paths (tests point this at a copy they downloaded themselves).</summary>
        public static LuauToolchain At(string executable, string definitions)
        {
            var t = new LuauToolchain(Path.GetDirectoryName(executable)!);
            t.fixedExecutable = executable;
            t.fixedDefinitions = definitions;
            return t;
        }

        private string? fixedExecutable;
        private string? fixedDefinitions;

        public string ExecutablePath => fixedExecutable ?? Path.Combine(directory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "luau-lsp.exe" : "luau-lsp");
        public string DefinitionsPath => fixedDefinitions ?? Path.Combine(directory, "globalTypes.None.d.luau");

        public bool Installed => File.Exists(ExecutablePath) && File.Exists(DefinitionsPath);

        /// <summary>Why it is not available (null when it is).</summary>
        public string? Problem { get; private set; }

        /// <summary>Downloads and verifies the pinned release if it is not there yet. False (and <see cref="Problem"/>) when it cannot.</summary>
        public async Task<bool> EnsureInstalledAsync(CancellationToken ct)
        {
            if (Installed) return true;
            if (fixedExecutable != null) { Problem = "the type checker is not at " + fixedExecutable; return false; }
            if (DateTime.UtcNow - lastFailureUtc < RetryAfterFailure) return false;
            await gate.WaitAsync(ct);
            try
            {
                if (Installed) return true;
                var build = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsBuild
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64 ? LinuxBuild
                    : default;
                if (build.File == null)
                {
                    Problem = "no pinned luau-lsp build for this platform";
                    lastFailureUtc = DateTime.UtcNow;
                    return false;
                }
                Directory.CreateDirectory(directory);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniBlox/1.0 (+https://klive.dev)");
                byte[] zip = await http.GetByteArrayAsync(ReleaseBase + build.File, ct);
                if (!Verify(zip, build.Sha256)) throw new InvalidDataException($"{build.File} did not match its pinned SHA-256");
                byte[] defs = await http.GetByteArrayAsync(DefinitionsUrl, ct);
                if (!Verify(defs, DefinitionsSha256)) throw new InvalidDataException("the Roblox type definitions did not match their pinned SHA-256");

                string staging = directory + ".tmp";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string name = Path.GetFileName(entry.FullName);
                        if (name.Length == 0 || !name.StartsWith("luau-lsp", StringComparison.OrdinalIgnoreCase)) continue;
                        entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                    }
                }
                File.WriteAllBytes(Path.Combine(staging, "globalTypes.None.d.luau"), defs);
                foreach (var file in Directory.EnumerateFiles(staging))
                    File.Move(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
                Directory.Delete(staging, recursive: true);
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                Problem = null;
                log($"[OmniBlox] installed the Luau type checker (luau-lsp {Version}).");
                return Installed;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
            {
                Problem = "the Luau type checker could not be installed: " + ex.Message;
                lastFailureUtc = DateTime.UtcNow;
                log("[OmniBlox] " + Problem);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool Verify(byte[] content, string sha256) =>
            Convert.ToHexString(SHA256.HashData(content)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
