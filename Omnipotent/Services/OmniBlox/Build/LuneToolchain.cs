using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>
    /// Lune (open source, MPL-2.0): a standalone Luau runtime that reads Roblox place files, which the studio's
    /// local engine runs on (Pipeline/LocalEngine.cs). Installed on first use under the studio's tools folder from
    /// a pinned release, and only after its SHA-256 matches the hash pinned here. Without it (offline, or no pinned
    /// build for this platform) a game is simply not run locally; that is never held against the game.
    /// </summary>
    public sealed class LuneToolchain
    {
        public const string Version = "0.10.5";
        private const string ReleaseBase = "https://github.com/lune-org/lune/releases/download/v" + Version + "/";

        private static readonly (string File, string Sha256) WindowsBuild = ("lune-" + Version + "-windows-x86_64.zip", "ad0305f5cc6d7ff20996644b40bf7de0de613812f431ca241456e16f9fc89cda");
        private static readonly (string File, string Sha256) LinuxBuild = ("lune-" + Version + "-linux-x86_64.zip", "1fb5dee6a1afa1d300092805c6e660fe06144d29dd68c45cf6956f040667f791");

        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

        private readonly string directory;
        private readonly Action<string> log;
        private readonly HttpMessageHandler? handler;
        private readonly SemaphoreSlim gate = new(1, 1);
        private DateTime lastFailureUtc = DateTime.MinValue;
        private string? fixedExecutable;

        /// <param name="handler">Where downloads go (tests); the internet when null.</param>
        public LuneToolchain(string toolsDirectory, Action<string>? log = null, HttpMessageHandler? handler = null)
        {
            directory = Path.Combine(Path.GetFullPath(toolsDirectory), "lune-" + Version);
            this.log = log ?? (_ => { });
            this.handler = handler;
        }

        /// <summary>Lune at a fixed path (tests point this at a copy they downloaded themselves).</summary>
        public static LuneToolchain At(string executable)
        {
            var t = new LuneToolchain(Path.GetDirectoryName(Path.GetFullPath(executable))!);
            t.fixedExecutable = executable;
            return t;
        }

        public string ExecutablePath => fixedExecutable ?? Path.Combine(directory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "lune.exe" : "lune");

        public bool Installed => File.Exists(ExecutablePath);

        /// <summary>A run can be tried now: Lune is installed, or will be on first use (a pinned build for this platform, no failed attempt within the hour).</summary>
        public bool Available => Installed || (fixedExecutable == null && PinnedBuild.File != null && DateTime.UtcNow - lastFailureUtc >= RetryAfterFailure);

        /// <summary>Why it is not available (null when it is).</summary>
        public string? Problem { get; private set; }

        private static (string File, string Sha256) PinnedBuild =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsBuild
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64 ? LinuxBuild
            : default;

        /// <summary>Downloads and verifies the pinned release if it is not there yet. False (and <see cref="Problem"/>) when it cannot.</summary>
        public async Task<bool> EnsureInstalledAsync(CancellationToken ct)
        {
            if (Installed) return true;
            if (fixedExecutable != null) { Problem = "Lune is not at " + fixedExecutable; return false; }
            if (DateTime.UtcNow - lastFailureUtc < RetryAfterFailure) return false;
            await gate.WaitAsync(ct);
            try
            {
                if (Installed) return true;
                var build = PinnedBuild;
                if (build.File == null)
                {
                    Problem = "no pinned Lune build for this platform";
                    lastFailureUtc = DateTime.UtcNow;
                    return false;
                }
                using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
                http.Timeout = TimeSpan.FromMinutes(3);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniBlox/1.0 (+https://klive.dev)");
                byte[] zip = await http.GetByteArrayAsync(ReleaseBase + build.File, ct);
                if (!Verify(zip, build.Sha256)) throw new InvalidDataException($"{build.File} did not match its pinned SHA-256");

                string name = Path.GetFileName(ExecutablePath);
                string staging = directory + ".tmp";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    var entry = archive.Entries.FirstOrDefault(e => Path.GetFileName(e.FullName) == name) ?? throw new InvalidDataException($"{build.File} has no {name}");
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }
                Directory.CreateDirectory(directory);
                File.Move(Path.Combine(staging, name), ExecutablePath, overwrite: true);
                Directory.Delete(staging, recursive: true);
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                Problem = null;
                log($"[OmniBlox] installed the local engine's runtime (Lune {Version}).");
                return Installed;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                Problem = "Lune could not be installed: " + ex.Message;
                lastFailureUtc = DateTime.UtcNow;
                log("[OmniBlox] " + Problem + " (games are not run locally until it is)");
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
