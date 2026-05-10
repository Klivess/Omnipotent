using System.Diagnostics;
using System.IO.Compression;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Locates / installs the native CLI tools used by Stratum's Simulation Agent:
    ///   * gmsh         — STEP → mesh
    ///   * CalculiX ccx — FEA solver
    ///   * (OpenFOAM    — CFD; not auto-installable on Windows; status surfaced for UI only)
    ///
    /// The strategy is identical to <see cref="StratumPythonRunner"/>: prefer system-PATH binaries,
    /// fall back to a previously-installed self-contained copy under <c>StratumToolsDirectory</c>,
    /// and on Windows attempt a one-time download of the official binary archive. On non-Windows
    /// hosts, surface a clear error directing the user to install via their package manager.
    /// </summary>
    public class StratumToolManager
    {
        private const string GmshVersion = "4.13.1";
        private const string CalculixVersion = "2.20";

        private static readonly string GmshWinUrl = $"https://gmsh.info/bin/Windows/gmsh-{GmshVersion}-Windows64.zip";

        // SourceForge "direct download" URL via prdownloads (auto-picks a mirror).
        private static readonly string CalculixWinUrl = $"https://prdownloads.sourceforge.net/calculix/ccx_{CalculixVersion}.win64.zip";

        private readonly SemaphoreSlim installLock = new(1, 1);

        public string ToolsRoot => OmniPaths.GlobalPaths.StratumToolsDirectory;
        public string GmshDir => Path.Combine(ToolsRoot, "gmsh-" + GmshVersion);
        public string CalculixDir => Path.Combine(ToolsRoot, "calculix-" + CalculixVersion);

        public StratumToolManager()
        {
            Directory.CreateDirectory(ToolsRoot);
        }

        // ── status ──
        public ToolsStatus Status()
        {
            var gmsh = ResolveGmsh();
            var ccx = ResolveCalculix();
            return new ToolsStatus
            {
                GmshPath = gmsh,
                GmshVersion = gmsh != null ? TryGetVersion(gmsh, "--version") : null,
                CalculixPath = ccx,
                // ccx prints version banner to stdout/stderr when run with no input; bounded probe.
                CalculixVersion = ccx != null ? TryGetCcxVersion(ccx) : null,
                OpenFoamConfigured = false, // Phase 4: not auto-installed.
            };
        }

        public string ResolveGmshOrThrow() => ResolveGmsh() ?? throw new Exception("gmsh not installed. Trigger /stratum/tools/install or call EnsureGmshAsync first.");
        public string ResolveCalculixOrThrow() => ResolveCalculix() ?? throw new Exception("CalculiX (ccx) not installed. Trigger /stratum/tools/install or call EnsureCalculixAsync first.");

        // ── ensure (lazy install) ──

        public async Task EnsureGmshAsync(Action<string> progress, CancellationToken ct)
        {
            if (ResolveGmsh() != null) return;
            await installLock.WaitAsync(ct);
            try
            {
                if (ResolveGmsh() != null) return;
                if (!OperatingSystem.IsWindows())
                    throw new Exception("gmsh auto-install is only implemented on Windows. Install gmsh via your package manager and ensure it is on PATH.");

                progress($"Downloading gmsh {GmshVersion} (~25 MB)…");
                string zip = Path.Combine(ToolsRoot, $"gmsh-{GmshVersion}.zip");
                await DownloadAsync(GmshWinUrl, zip, ct);

                progress("Extracting gmsh…");
                if (Directory.Exists(GmshDir)) Directory.Delete(GmshDir, true);
                Directory.CreateDirectory(GmshDir);
                ZipFile.ExtractToDirectory(zip, GmshDir, true);
                File.Delete(zip);

                if (ResolveGmsh() == null)
                    throw new Exception("gmsh extracted but gmsh.exe could not be located.");
                progress("gmsh installed.");
            }
            finally { installLock.Release(); }
        }

        public async Task EnsureCalculixAsync(Action<string> progress, CancellationToken ct)
        {
            if (ResolveCalculix() != null) return;
            await installLock.WaitAsync(ct);
            try
            {
                if (ResolveCalculix() != null) return;
                if (!OperatingSystem.IsWindows())
                    throw new Exception("CalculiX auto-install is only implemented on Windows. Install ccx via your package manager.");

                progress($"Downloading CalculiX {CalculixVersion} (~30 MB)…");
                string zip = Path.Combine(ToolsRoot, $"calculix-{CalculixVersion}.zip");
                await DownloadAsync(CalculixWinUrl, zip, ct);

                progress("Extracting CalculiX…");
                if (Directory.Exists(CalculixDir)) Directory.Delete(CalculixDir, true);
                Directory.CreateDirectory(CalculixDir);
                ZipFile.ExtractToDirectory(zip, CalculixDir, true);
                File.Delete(zip);

                if (ResolveCalculix() == null)
                    throw new Exception("CalculiX extracted but ccx.exe could not be located.");
                progress("CalculiX installed.");
            }
            finally { installLock.Release(); }
        }

        // ── resolution ──

        private string? ResolveGmsh()
        {
            // 1. System PATH.
            string? sys = WhichOnPath(OperatingSystem.IsWindows() ? "gmsh.exe" : "gmsh");
            if (sys != null) return sys;
            // 2. Local install.
            if (Directory.Exists(GmshDir))
            {
                var found = Directory.EnumerateFiles(GmshDir, OperatingSystem.IsWindows() ? "gmsh.exe" : "gmsh", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            return null;
        }

        private string? ResolveCalculix()
        {
            string exeName = OperatingSystem.IsWindows() ? "ccx.exe" : "ccx";
            string? sys = WhichOnPath(exeName);
            if (sys != null) return sys;
            // CalculiX Windows zip lays out as bin/ccx*.exe — name varies slightly by build.
            if (Directory.Exists(CalculixDir))
            {
                foreach (var path in Directory.EnumerateFiles(CalculixDir, "ccx*.exe", SearchOption.AllDirectories))
                {
                    return path;
                }
                if (!OperatingSystem.IsWindows())
                {
                    var fallback = Directory.EnumerateFiles(CalculixDir, "ccx*", SearchOption.AllDirectories).FirstOrDefault();
                    if (fallback != null) return fallback;
                }
            }
            return null;
        }

        // ── helpers ──
        private static string? WhichOnPath(string exeName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string full;
                try { full = Path.Combine(dir, exeName); } catch { continue; }
                if (File.Exists(full)) return full;
            }
            return null;
        }

        private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(15),
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Stratum/1.0");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(dest);
            await resp.Content.CopyToAsync(fs, ct);
        }

        private static string? TryGetVersion(string exe, string arg)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arg)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
                string s = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                return string.IsNullOrEmpty(s) ? null : s.Split('\n')[0].Trim();
            }
            catch { return null; }
        }

        private static string? TryGetCcxVersion(string exe)
        {
            // ccx with no args prints "You are using an executable made on <date>\n Usage: CalculiX.exe -i jobname"
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
                string s = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                if (string.IsNullOrEmpty(s)) return null;
                var firstLine = s.Split('\n')[0].Trim();
                return string.IsNullOrEmpty(firstLine) ? "ccx" : firstLine;
            }
            catch { return null; }
        }
    }

    public class ToolsStatus
    {
        public string? GmshPath;
        public string? GmshVersion;
        public string? CalculixPath;
        public string? CalculixVersion;
        public bool OpenFoamConfigured;
    }
}
