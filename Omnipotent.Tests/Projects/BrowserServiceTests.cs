using System.Net;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The browser hot path must not touch the Docker daemon.
    ///
    /// Reaching browser-inspect.py through `docker exec` is what let a busy host park a single
    /// browser action for 26 minutes on 2026-09-16 — an exec attach is a hijacked stream that
    /// ignores its cancellation token, and three projects sharing one daemon stalled together.
    /// Serving the helper over the container's own published loopback port removes the daemon from
    /// the path entirely, so these tests are about WHICH path is taken, not about timeouts.
    /// </summary>
    public class BrowserServiceTests : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly int port;
        private readonly List<string> received = new();
        private bool browserUp = true;

        public BrowserServiceTests()
        {
            port = FreeLoopbackPort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch { }
            ((IDisposable)listener).Dispose();
            GC.SuppressFinalize(this);
        }

        private static int FreeLoopbackPort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int chosen = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return chosen;
        }

        private async Task ServeAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { return; }

                string path = context.Request.Url?.AbsolutePath ?? "";
                string body;
                if (path == "/health")
                {
                    lock (received) received.Add("health");
                    body = JsonSerializer.Serialize(new { ok = true, browserUp });
                }
                else
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    string request = await reader.ReadToEndAsync();
                    lock (received) received.Add("run:" + request);
                    body = JsonSerializer.Serialize(new
                    {
                        exitCode = 0,
                        stdout = JsonSerializer.Serialize(new { served = true, tabCount = 1 }),
                        stderr = "",
                    });
                }
                byte[] raw = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = raw.Length;
                await context.Response.OutputStream.WriteAsync(raw);
                context.Response.Close();
            }
        }

        private ContainerToolAdapter Adapter(int servicePort, Action<string>? onExec = null)
        {
            var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var gate = new SemaphoreSlim(1, 1);
            return new ContainerToolAdapter(
                transport, "container", "agent", gate,
                dockerControlAsync: (_, _, _) => { onExec?.Invoke("launch"); return Task.CompletedTask; },
                terminalAsync: (command, _, _, _) =>
                {
                    onExec?.Invoke(command);
                    return Task.FromResult(new ContainerShellResult(0, "{\"viaExec\":true}", "", false, false));
                },
                browserServiceHostPort: servicePort);
        }

        [Fact]
        public async Task Inspection_GoesToTheContainerServiceAndNeverToDockerExec()
        {
            var execCalls = new List<string>();
            var adapter = Adapter(port, execCalls.Add);

            var result = await adapter.ExecuteAsync("computer_browser_inspect", "{\"mode\":\"dom\",\"maxItems\":40}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("served", result.Text, StringComparison.Ordinal);
            Assert.Empty(execCalls);   // the whole point: no daemon round trip
            lock (received) Assert.Contains(received, entry => entry.StartsWith("run:", StringComparison.Ordinal));
        }

        /// <summary>
        /// The redundant call: every browser action used to open with an unconditional `docker exec`
        /// launch to check a browser that is already running. A loopback GET answers it instead.
        /// </summary>
        [Fact]
        public async Task AnAlreadyRunningBrowser_IsNotRelaunchedThroughDocker()
        {
            browserUp = true;
            var execCalls = new List<string>();
            var adapter = Adapter(port, execCalls.Add);

            await adapter.ExecuteAsync("computer_browser_action",
                "{\"op\":\"click\",\"name\":\"Send\",\"timeoutMs\":1400}");

            Assert.DoesNotContain("launch", execCalls);
            lock (received) Assert.Contains("health", received);
        }

        /// <summary>Not knowing is not the same as knowing it is down: an unreachable service must
        /// still produce the old unconditional launch rather than assuming the browser is fine.</summary>
        [Fact]
        public async Task AnUnreachableService_FallsBackToDockerExecSoOldDesktopsStillWork()
        {
            var execCalls = new List<string>();
            var adapter = Adapter(servicePort: 0, onExec: execCalls.Add);   // container predates the service

            var result = await adapter.ExecuteAsync("computer_browser_inspect", "{\"mode\":\"dom\"}");

            Assert.True(result.Success, result.Text);
            Assert.Contains("viaExec", result.Text, StringComparison.Ordinal);
            Assert.Contains(execCalls, c => c.Contains("browser-inspect.py", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ADownBrowser_IsStillLaunchedThroughDocker()
        {
            browserUp = false;
            var execCalls = new List<string>();
            var adapter = Adapter(port, execCalls.Add);

            await adapter.ExecuteAsync("computer_browser_action",
                "{\"op\":\"click\",\"name\":\"Send\",\"timeoutMs\":1400}");

            Assert.Contains("launch", execCalls);
        }
    }
}
