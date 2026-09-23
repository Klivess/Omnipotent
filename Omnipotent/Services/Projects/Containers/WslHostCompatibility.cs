using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Host-side WSL facts and the two repairs Docker Desktop's WSL backend needs on this host.
    ///
    /// 1. <b>Incompatible WSL package.</b> On 2026-09-18 a project agent installed the standalone
    ///    WSL 2.x package on Windows 10 build 19042. That package does not run below build 19044:
    ///    every wsl.exe call then answers WSL_E_OS_NOT_SUPPORTED, so Docker Desktop's engine
    ///    "fails to start" forever. The package cannot execute on such a host, so removing it loses
    ///    nothing and returns WSL to the built-in component that ran Docker before. Only the
    ///    package named exactly "Windows Subsystem for Linux" is removed; the WSL2 kernel update
    ///    ("Windows Subsystem for Linux Update") that the built-in WSL needs is never touched, and
    ///    no distribution or disk is unregistered. Windows must restart once afterwards — Omnipotent
    ///    never restarts the host itself.
    ///
    /// 2. <b>Unbounded VM memory.</b> <see cref="EnsureMemoryCap"/> writes a [wsl2] memory ceiling
    ///    only when the owner has not set one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class WslHostCompatibility
    {
        public const string UnsupportedOsSignature = "WSL_E_OS_NOT_SUPPORTED";
        /// <summary>First Windows 10 build the standalone WSL package supports.</summary>
        public const int MinimumBuildForStandaloneWsl = 19044;
        private const string StandalonePackageName = "Windows Subsystem for Linux";
        private const string StoreAppxName = "MicrosoftCorporationII.WindowsSubsystemForLinux";

        /// <summary>Pure: does this wsl.exe output + OS build prove the standalone package is unusable?</summary>
        internal static bool IndicatesIncompatiblePackage(string? wslOutput, int windowsBuild) =>
            windowsBuild > 0 && windowsBuild < MinimumBuildForStandaloneWsl
            && (wslOutput ?? "").Replace("\0", "").Contains(UnsupportedOsSignature, StringComparison.OrdinalIgnoreCase);

        /// <summary>Pure: only the standalone package qualifies, never the kernel update MSI.</summary>
        internal static bool IsStandaloneWslPackage(string? displayName, string? publisher) =>
            string.Equals((displayName ?? "").Trim(), StandalonePackageName, StringComparison.Ordinal)
            && (publisher ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        public static int WindowsBuild => Environment.OSVersion.Version.Build;

        /// <summary>MSI product codes of installed standalone WSL packages (machine-wide).</summary>
        internal static List<(string ProductCode, string Version)> FindStandalonePackages()
        {
            var found = new List<(string, string)>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry == null) continue;
                        if (!IsStandaloneWslPackage(entry.GetValue("DisplayName") as string, entry.GetValue("Publisher") as string))
                            continue;
                        if (!Regex.IsMatch(name, @"^\{[0-9A-Fa-f-]{36}\}$")) continue; // MSI product code only
                        found.Add((name, entry.GetValue("DisplayVersion") as string ?? "?"));
                    }
                }
                catch { }
            }
            return found.DistinctBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Removes the standalone WSL package when, and only when, WSL itself has said this Windows
        /// build cannot run it. Returns a status line for the recovery log, or null when there was
        /// nothing to repair. One attempt per package version (a marker file prevents retries).
        /// </summary>
        public static async Task<string?> RepairIncompatiblePackageAsync(string wslOutput, string markerDirectory,
            Action<string> log, CancellationToken ct)
        {
            if (!IndicatesIncompatiblePackage(wslOutput, WindowsBuild)) return null;
            var packages = FindStandalonePackages();
            var results = new List<string>();
            Directory.CreateDirectory(markerDirectory);
            foreach (var (code, version) in packages)
            {
                string marker = Path.Combine(markerDirectory, $"wsl-package-removed-{code.Trim('{', '}')}-{version}.txt");
                if (File.Exists(marker)) { results.Add($"WSL package {version} was already removed; waiting for the Windows restart"); continue; }
                log($"Desktop bootstrap: removing WSL package {version} ({code}) — Windows build {WindowsBuild} cannot run it ({UnsupportedOsSignature}).");
                var result = await ContainerDependencyBootstrapper.RunProcessAsync(
                    Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                    new[] { "/x", code, "/qn", "/norestart" }, TimeSpan.FromMinutes(10), ct);
                // 0 = removed, 3010 = removed and a restart completes it, 1605 = already gone.
                if (result.ExitCode is 0 or 3010 or 1605)
                {
                    File.WriteAllText(marker, $"{DateTime.UtcNow:o} msiexec exit {result.ExitCode}");
                    results.Add($"removed WSL package {version} (msiexec exit {result.ExitCode})");
                }
                else results.Add($"could not remove WSL package {version} (msiexec exit {result.ExitCode}: {result.Output})");
            }

            // A Microsoft Store copy fails the same way; remove it for every user.
            var appx = await ContainerDependencyBootstrapper.RunProcessAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                new[] { "-NoProfile", "-NonInteractive", "-Command",
                    $"$p = Get-AppxPackage -AllUsers -Name '{StoreAppxName}' -ErrorAction SilentlyContinue; " +
                    "if ($p) { $p | Remove-AppxPackage -AllUsers -ErrorAction Stop; 'removed' } else { 'absent' }" },
                TimeSpan.FromMinutes(5), ct);
            if (appx.Output.Contains("removed", StringComparison.OrdinalIgnoreCase)) results.Add("removed the Store WSL package");

            if (results.Count == 0)
                return $"WSL reports {UnsupportedOsSignature} on Windows build {WindowsBuild}, but no standalone WSL package was found to remove. " +
                       "Updating Windows to 21H2/22H2 (build 19044+) also resolves it.";
            return string.Join("; ", results) +
                   $". WSL could not run on Windows build {WindowsBuild}; the built-in WSL (which ran Docker before 2026-09-18) returns after ONE Windows restart. " +
                   "Omnipotent never restarts the host — restart it in a maintenance window and Docker Desktop will start automatically.";
        }

        /// <summary>
        /// Adds a [wsl2] memory ceiling to the Omnipotent user's .wslconfig when none is set. Existing
        /// owner settings are never overwritten. Takes effect the next time the WSL VM starts.
        /// </summary>
        public static string? EnsureMemoryCap(long hostTotalBytes, string? profileDirectory = null)
        {
            try
            {
                string dir = profileDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string path = Path.Combine(dir, ".wslconfig");
                string existing = File.Exists(path) ? File.ReadAllText(path) : "";
                string updated = WithMemoryCap(existing, DesktopCapacityPolicy.WslMemoryCapBytes(hostTotalBytes));
                if (updated == existing) return null;
                File.WriteAllText(path, updated);
                return $"capped Docker's WSL VM at {DesktopCapacityPolicy.WslMemoryCapBytes(hostTotalBytes) / (1024L * 1024 * 1024)} GB in {path}";
            }
            catch (Exception ex) { return $"could not write the WSL memory cap ({ex.Message})"; }
        }

        /// <summary>Pure: returns the config with a [wsl2] memory/swap cap added unless memory is already set.</summary>
        internal static string WithMemoryCap(string config, long capBytes)
        {
            string nl = config.Contains("\r\n") ? "\r\n" : "\n";
            var lines = config.Replace("\r\n", "\n").Split('\n').ToList();
            int section = lines.FindIndex(l => l.Trim().Equals("[wsl2]", StringComparison.OrdinalIgnoreCase));
            if (section >= 0)
            {
                for (int i = section + 1; i < lines.Count && !lines[i].TrimStart().StartsWith('['); i++)
                    if (Regex.IsMatch(lines[i], @"^\s*memory\s*=", RegexOptions.IgnoreCase)) return config;
            }
            var insert = new List<string>
            {
                "# Added by Omnipotent: bounds Docker's VM so Windows and Omnipotent are never starved.",
                $"memory={capBytes / (1024L * 1024 * 1024)}GB",
            };
            if (section >= 0)
            {
                bool hasSwap = false;
                for (int i = section + 1; i < lines.Count && !lines[i].TrimStart().StartsWith('['); i++)
                    if (Regex.IsMatch(lines[i], @"^\s*swap\s*=", RegexOptions.IgnoreCase)) hasSwap = true;
                if (!hasSwap) insert.Add("swap=2GB");
                lines.InsertRange(section + 1, insert);
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add("[wsl2]");
                lines.AddRange(insert);
                lines.Add("swap=2GB");
                lines.Add("");
            }
            return string.Join(nl, lines);
        }
    }
}
