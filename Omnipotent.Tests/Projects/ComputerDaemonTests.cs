using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// klive-computerd: the in-container control plane that took docker exec off the agent's hot path.
    ///
    /// The properties that matter are about ROUTES, not speed: a command reaches the container through
    /// the daemon whenever it can; a request the daemon never received may be re-run through docker
    /// exec; a request it did receive is never re-run, however it ended; and a desktop whose VNC server
    /// has wedged still has eyes and hands.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ComputerDaemonTests : IDisposable
    {
        public ComputerDaemonTests()
        {
            ComputerDaemonToken.KeyOverride = () => Encoding.UTF8.GetBytes("unit-test-host-key-0123456789abcd");
            ComputerDaemonClient.ResetForTests();
            BrowserServiceClient.ResetRejectionsForTests();
        }

        public void Dispose()
        {
            ComputerDaemonToken.KeyOverride = null;
            ComputerDaemonClient.ResetForTests();
            GC.SuppressFinalize(this);
        }

        // ── the real daemon ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task RealDaemon_ServesHealthAndExec_ToTheRealClient()
        {
            string? python = FindPython();
            if (python == null) return;
            const string containerID = "real-daemon-container";
            int port = FreePort();
            using var daemonProcess = StartDaemon(python, port, ComputerDaemonToken.For(containerID));
            try
            {
                var client = new ComputerDaemonClient("127.0.0.1", port, containerID);
                ComputerDaemonHealth? health = null;
                for (int attempt = 0; attempt < 60 && health == null; attempt++)
                {
                    health = await client.HealthAsync(CancellationToken.None, fresh: true);
                    if (health == null) await Task.Delay(100);
                }

                Assert.NotNull(health);
                Assert.True(health!.IsControlPlane);
                Assert.True(health.AuthConfigured);
                Assert.Contains("exec", health.Capabilities);

                var exec = await client.ExecAsync(null, new[] { python, "-c", "import sys; print('from-daemon'); sys.exit(4)" },
                    null, 20, CancellationToken.None);
                Assert.True(exec.Reached, exec.Problem);
                Assert.Equal(4, exec.Result!.ExitCode);
                Assert.Contains("from-daemon", exec.Result.Stdout);
            }
            finally
            {
                try { daemonProcess.Kill(entireProcessTree: true); } catch { }
            }
        }

        [Fact]
        public async Task RealDaemon_WithAnotherContainersToken_IsNotReached_SoExecFallsBack()
        {
            string? python = FindPython();
            if (python == null) return;
            int port = FreePort();
            using var daemonProcess = StartDaemon(python, port, ComputerDaemonToken.For("some-other-container"));
            try
            {
                var client = new ComputerDaemonClient("127.0.0.1", port, "this-container");
                for (int attempt = 0; attempt < 60 && await client.HealthAsync(CancellationToken.None, fresh: true) == null; attempt++)
                    await Task.Delay(100);

                var exec = await client.ExecAsync(null, new[] { python, "-c", "print(1)" }, null, 10, CancellationToken.None);

                Assert.False(exec.Reached);   // 401: nothing ran, so docker exec may run it instead
            }
            finally
            {
                try { daemonProcess.Kill(entireProcessTree: true); } catch { }
            }
        }

        // ── routing in the orchestrator ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ShellCommands_GoThroughTheDaemon_AndNeverTouchDocker()
        {
            using var daemon = new FakeDaemon();
            daemon.OnExec = _ => (200, "{\"exitCode\":0,\"stdout\":\"served-by-daemon\",\"stderr\":\"\",\"timedOut\":false,\"truncated\":false}");
            var orchestrator = OrchestratorWith(daemon);

            var result = await orchestrator.ExecuteDesktopShellAsync("c1", "echo hi", "/project", 10);

            Assert.True(result.Success);
            Assert.Equal("served-by-daemon", result.Stdout);
            Assert.Equal((1L, 0L), orchestrator.ExecRouteCounts);
        }

        [Fact]
        public async Task AnAgentCommandTheDaemonReceived_IsNeverReRunThroughDocker_EvenWhenItTimesOut()
        {
            using var daemon = new FakeDaemon();
            daemon.OnExec = _ => { Thread.Sleep(3000); return (200, "{\"exitCode\":0}"); };
            var orchestrator = OrchestratorWith(daemon);
            ComputerDaemonClient.ExecResponseMargin = TimeSpan.FromSeconds(1);
            try
            {
                var result = await orchestrator.ExecuteDesktopShellAsync("c1", "make-a-purchase", "/project", 1);

                // The docker client in OrchestratorWith points nowhere: had the command been re-run
                // there, this would be a daemon-timeout exception, not a reported timeout.
                Assert.True(result.TimedOut);
                Assert.Contains("may or may not have run", result.Stderr);
                Assert.Equal((1L, 0L), orchestrator.ExecRouteCounts);
            }
            finally { ComputerDaemonClient.ExecResponseMargin = TimeSpan.FromSeconds(25); }
        }

        [Fact]
        public async Task ADaemonWithoutTheToken_IsSkipped_AndTheCommandFallsBack()
        {
            using var daemon = new FakeDaemon { AuthConfigured = false };
            var orchestrator = OrchestratorWith(daemon);

            // No token on the daemon: it is never sent the command, docker exec gets it (and, with no
            // Docker here, fails — which proves the fallback was taken).
            ContainerShellResult? result = null;
            var error = await Record.ExceptionAsync(async () =>
                result = await orchestrator.ExecuteDesktopShellAsync("c1", "echo hi", "/project", 2));

            Assert.Equal(0, daemon.ExecCalls);
            Assert.Equal((0L, 1L), orchestrator.ExecRouteCounts);
            Assert.True(error != null || result?.Success == false);
        }

        [Fact]
        public void TokenFile_IsRecognisedFromSha256sumOutput()
        {
            string token = ComputerDaemonToken.For("abc");
            string output = $"{ComputerDaemonToken.FileHash(token)}  /etc/klive-computerd.token\n0000  /usr/local/bin/browser-service.py";

            Assert.True(ContainerOrchestrator.HasToken(output, token));
            Assert.False(ContainerOrchestrator.HasToken(output, ComputerDaemonToken.For("other")));
            Assert.False(ContainerOrchestrator.HasToken("", token));
        }

        [Fact]
        public void Tokens_AreStablePerContainer_AndDistinctAcrossContainers()
        {
            Assert.Equal(ComputerDaemonToken.For("a"), ComputerDaemonToken.For("a"));
            Assert.NotEqual(ComputerDaemonToken.For("a"), ComputerDaemonToken.For("b"));
            Assert.Equal(48, ComputerDaemonToken.For("a").Length);
        }

        // ── readiness rules ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Readiness_TreatsPanelAndSessionAsCosmetic_NotAsReasonsToRestart()
        {
            var caps = new Dictionary<string, string>
            {
                ["display"] = "up", ["window-manager"] = "up", ["vnc"] = "up", ["frame"] = "usable",
                ["panel"] = "down", ["desktop-shell"] = "down", ["computerd"] = "up",
            };

            Assert.True(ContainerDesktopManager.DesktopControlIsReady(caps));
            Assert.Equal(new[] { "desktop-shell", "panel" }, ContainerDesktopManager.DegradedCapabilities(caps).OrderBy(x => x));
            Assert.False(ContainerDesktopManager.NeedsContainerRestart(caps));
        }

        [Fact]
        public void Readiness_AcceptsTheDaemonAsTheInputRoute_WhenVncIsDown()
        {
            var caps = new Dictionary<string, string>
            {
                ["display"] = "up", ["window-manager"] = "up", ["vnc"] = "down", ["frame"] = "usable",
                ["input"] = "up", ["input-route"] = "computerd", ["computerd"] = "up",
            };

            Assert.True(ContainerDesktopManager.DesktopControlIsReady(caps));
            Assert.False(ContainerDesktopManager.NeedsContainerRestart(caps));
        }

        [Fact]
        public void OnlyADeadDisplayOrAnUnreachableDesktop_JustifiesARestart()
        {
            Assert.True(ContainerDesktopManager.NeedsContainerRestart(new Dictionary<string, string>
            { ["display"] = "down", ["computerd"] = "up", ["vnc"] = "down" }));
            Assert.True(ContainerDesktopManager.NeedsContainerRestart(new Dictionary<string, string>
            { ["display"] = "up", ["computerd"] = "down", ["vnc"] = "down" }));
            Assert.False(ContainerDesktopManager.NeedsContainerRestart(new Dictionary<string, string>
            { ["display"] = "up", ["computerd"] = "down", ["vnc"] = "up", ["window-manager"] = "down" }));
        }

        [Fact]
        public void HealthMapsOntoTheReadinessVocabulary()
        {
            var health = JsonSerializer.Deserialize<ComputerDaemonHealth>(FakeDaemon.HealthJson(true, 1280, 800),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var caps = ContainerDesktopManager.CapabilitiesFromHealth(health);

            Assert.Equal("up", caps["display"]);
            Assert.Equal("up", caps["window-manager"]);
            Assert.Equal("down", caps["panel"]);
            Assert.Equal("14", caps["image-version"]);
            Assert.Equal("up", caps["computerd"]);
            Assert.Equal("2", caps["oom-kills"]);
        }

        // ── the adapter keeps eyes and hands when VNC is gone ────────────────────────────────────

        [Fact]
        public async Task WithVncDown_ClicksAndScreenshotsGoThroughTheDaemon()
        {
            using var daemon = new FakeDaemon();
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });   // nothing listens on port 1
            var adapter = new ContainerToolAdapter(transport, "c-vnc-down", "agent", new SemaphoreSlim(1, 1),
                browserServiceHostPort: daemon.Port, desktopWidth: 1280, desktopHeight: 800);

            var click = await adapter.ExecuteAsync("computer_click", "{\"x\":200,\"y\":150}");

            Assert.True(click.Success, click.Text);
            Assert.True(daemon.InputCalls > 0);
            Assert.Contains(daemon.InputEvents, e => e.Contains("\"down\"", StringComparison.Ordinal));
            Assert.Equal(1280, click.Width);   // the post-click frame came from /screenshot
            Assert.True(daemon.ScreenshotCalls > 0);
        }

        [Fact]
        public async Task ClipboardReads_ComeFromTheRealSelection()
        {
            using var daemon = new FakeDaemon { Clipboard = "copied text" };
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var adapter = new ContainerToolAdapter(transport, "c-clip", "agent", new SemaphoreSlim(1, 1),
                browserServiceHostPort: daemon.Port);

            var result = await adapter.ExecuteAsync("computer_clipboard_get", "{}");

            Assert.True(result.Success);
            Assert.Contains("copied text", result.Text);
        }

        // ── the browser helper never re-runs an action that timed out ───────────────────────────

        [Fact]
        public async Task ATimedOutBrowserAction_IsReported_NotReRunThroughDockerExec()
        {
            using var listener = new HttpListener();
            int port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    await Task.Delay(8000);
                    try { context.Response.Close(); } catch { }
                }
            });

            var client = new BrowserServiceClient("127.0.0.1", port);
            var outcome = await client.RunAsync("action", "e30", null, 5, CancellationToken.None);

            Assert.NotNull(outcome);                       // null would mean "go run it through exec"
            Assert.False(outcome!.Value.Ok);
            Assert.Contains("may or may not have been applied", outcome.Value.Error);
            listener.Stop();
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────

        private static ContainerOrchestrator OrchestratorWith(FakeDaemon daemon)
        {
            string registryPath = Path.Combine(Path.GetTempPath(), "computerd-registry-" + Guid.NewGuid().ToString("N") + ".json");
            var registry = new ContainerRegistry(_ => { }, registryPath);
            registry.Add(new DesktopContainerRecord { ContainerID = "c1", ProjectID = "p", BrowserServiceHostPort = daemon.Port });
            // A Docker endpoint nobody listens on: any docker-exec fallback fails fast and visibly.
            var docker = new Docker.DotNet.DockerClientConfiguration(new Uri("http://127.0.0.1:1"),
                defaultTimeout: TimeSpan.FromSeconds(2)).CreateClient();
            return new ContainerOrchestrator(registry, docker);
        }

        private static Process StartDaemon(string python, int port, string token)
        {
            var start = new ProcessStartInfo(python, $"-I \"{ServicePath()}\"")
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            start.Environment["KLIVE_BROWSER_SERVICE_PORT"] = port.ToString();
            start.Environment["KLIVE_COMPUTERD_TOKEN"] = token;
            start.Environment["KLIVE_COMPUTERD_EXEC_WRAPPER"] = "none";
            start.Environment["KLIVE_CDP_ROOT"] = "http://127.0.0.1:1";
            start.Environment["KLIVE_COMPUTERD_TOKEN_FILE"] = Path.Combine(Path.GetTempPath(), "no-such-token-" + Guid.NewGuid().ToString("N"));
            return Process.Start(start)!;
        }

        private static string ServicePath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Omnipotent", "Services", "Projects", "Containers", "browser-service.py"));

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static string? FindPython()
        {
            foreach (string candidate in new[] { "python3", "python" })
            {
                try
                {
                    using var probe = Process.Start(new ProcessStartInfo(candidate, "-c \"import http.server\"")
                    {
                        UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
                    });
                    if (probe != null && probe.WaitForExit(15000) && probe.ExitCode == 0) return candidate;
                }
                catch { }
            }
            return null;
        }

        /// <summary>A stand-in klive-computerd speaking the real wire format.</summary>
        internal sealed class FakeDaemon : IDisposable
        {
            private readonly HttpListener listener = new();
            public int Port { get; }
            public bool AuthConfigured { get; init; } = true;
            public string Clipboard { get; init; } = "";
            public int Width { get; init; } = 1280;
            public int Height { get; init; } = 800;
            public Func<string, (int Status, string Body)>? OnExec { get; set; }
            public int ExecCalls;
            public int InputCalls;
            public int ScreenshotCalls;
            public readonly List<string> InputEvents = new();

            public FakeDaemon()
            {
                Port = FreePort();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                listener.Start();
                _ = Task.Run(ServeAsync);
            }

            internal static string HealthJson(bool auth, int width, int height) => JsonSerializer.Serialize(new
            {
                ok = true, browserUp = false, service = "klive-computerd", protocol = 2, uptimeSeconds = 5,
                screen = new { width, height },
                components = new Dictionary<string, bool>
                {
                    ["display"] = true, ["xvfb"] = true, ["x11vnc"] = false, ["windowManager"] = true,
                    ["panel"] = false, ["session"] = true, ["desktop"] = true, ["browserProcess"] = false, ["browserCdp"] = false,
                },
                browser = new { process = false, cdp = false, tabs = (int?)null, processCount = 0, watch = new { } },
                resources = new { memory = new { limitMB = 1536.0, usedMB = 400.0, availableMB = 1136.0, oomKills = 2, cgroup = "v2" }, load1 = 0.5, cpus = 4 },
                tools = new Dictionary<string, bool> { ["xdotool"] = true, ["ffmpeg"] = true, ["xclip"] = true },
                image = new { version = "14", capabilities = new[] { "chromium", "firefox" } },
                capabilities = new[] { "run", "exec", "input", "screenshot", "clipboard", "windows", "browser-state", "diag" },
                authConfigured = auth,
                events = Array.Empty<object>(),
            });

            private async Task ServeAsync()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    _ = Task.Run(() => Handle(context));
                }
            }

            private void Handle(HttpListenerContext context)
            {
                try
                {
                    string path = context.Request.Url?.AbsolutePath ?? "";
                    string body = "";
                    using (var reader = new StreamReader(context.Request.InputStream)) body = reader.ReadToEnd();
                    if (path == "/health") { Reply(context, 200, HealthJson(AuthConfigured, Width, Height)); return; }
                    if (!AuthConfigured) { Reply(context, 403, "{\"error\":\"no-token-configured\"}"); return; }
                    switch (path)
                    {
                        case "/exec":
                            Interlocked.Increment(ref ExecCalls);
                            var (status, text) = OnExec?.Invoke(body) ?? (200, "{\"exitCode\":0,\"stdout\":\"\",\"stderr\":\"\"}");
                            Reply(context, status, text);
                            return;
                        case "/input":
                            Interlocked.Increment(ref InputCalls);
                            lock (InputEvents) InputEvents.Add(body);
                            Reply(context, 200, "{\"ok\":true,\"durationMs\":3,\"pointer\":{\"x\":200,\"y\":150}}");
                            return;
                        case "/screenshot":
                            Interlocked.Increment(ref ScreenshotCalls);
                            var frame = new byte[Width * Height * 4];
                            for (int i = 3; i < frame.Length; i += 4) frame[i] = 255;
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/octet-stream";
                            context.Response.Headers["X-Frame-Width"] = Width.ToString();
                            context.Response.Headers["X-Frame-Height"] = Height.ToString();
                            context.Response.Headers["X-Image-Width"] = Width.ToString();
                            context.Response.Headers["X-Image-Height"] = Height.ToString();
                            context.Response.ContentLength64 = frame.Length;
                            context.Response.OutputStream.Write(frame);
                            context.Response.Close();
                            return;
                        case "/clipboard":
                            Reply(context, 200, JsonSerializer.Serialize(new { ok = true, text = Clipboard, empty = Clipboard.Length == 0 }));
                            return;
                        default:
                            Reply(context, 404, "{\"error\":\"not found\"}");
                            return;
                    }
                }
                catch { try { context.Response.Abort(); } catch { } }
            }

            private static void Reply(HttpListenerContext context, int status, string json)
            {
                byte[] raw = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = raw.Length;
                context.Response.OutputStream.Write(raw);
                context.Response.Close();
            }

            public void Dispose()
            {
                try { listener.Stop(); } catch { }
                ((IDisposable)listener).Dispose();
            }
        }
    }
}
