using System.Net;
using System.Net.WebSockets;

namespace Omnipotent.Services.KliveLink
{
    /// <summary>
    /// Dedicated WebSocket server for KliveLink agent connections.
    /// Runs on its own port, separate from KliveAPI, so agents can connect
    /// without going through the HTTP API infrastructure.
    /// </summary>
    public class KliveLinkServer
    {
        public static int Port = 5100;

        private readonly KliveLinkService _service;
        private readonly HttpListener _listener;
        private bool _running;

        public KliveLinkServer(KliveLinkService service)
        {
            _service = service;
            _listener = new HttpListener();
        }

        public void Start()
        {
            _listener.Prefixes.Add($"http://+:{Port}/");
            _listener.Start();
            _running = true;
            _service.ServiceLog($"KliveLink WebSocket server listening on port {Port}");
            _ = Task.Run(ListenLoop);
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
        }

        private async Task ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _service.ServiceLogError(ex, "KliveLink server listen error");
                    }
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string agentId = context.Request.Headers["X-Agent-Id"] ?? "";
                string authToken = context.Request.Headers["X-Auth-Token"] ?? "";

                if (string.IsNullOrEmpty(agentId))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    _service.ServiceLog("KliveLink rejected connection: missing X-Agent-Id header");
                    return;
                }

                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                _service.ServiceLog($"KliveLink WebSocket accepted for agent: {agentId}");

                await _service.HandleAgentConnection(wsContext.WebSocket, agentId);
            }
            catch (Exception ex)
            {
                _service.ServiceLogError(ex, "KliveLink WebSocket connection error");
                try { context.Response.Close(); } catch { }
            }
        }
    }
}
