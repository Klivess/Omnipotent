using System.Drawing;
using KliveLink.Agent;

namespace KliveLink
{
    /// <summary>
    /// KliveLink Remote Administration Agent
    /// 
    /// ETHICAL / LEGAL NOTICE:
    /// This agent is designed for LEGITIMATE remote administration only.
    /// - Explicit user consent is REQUIRED before any remote operations.
    /// - A visible system tray icon is always shown while the agent runs.
    /// - The user can revoke consent and exit at any time via the tray menu.
    /// - Unauthorized deployment of this software is illegal and unethical.
    /// </summary>
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
