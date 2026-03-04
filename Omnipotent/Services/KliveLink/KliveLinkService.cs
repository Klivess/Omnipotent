using Newtonsoft.Json;
using Omnipotent.Service_Manager;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Omnipotent.Services.KliveLink
{
    /// <summary>
    /// Server-side KliveLink service that manages WebSocket connections from KliveLink agents.
    /// Agents connect via /klivelink/ws and are tracked by agent ID.
    /// Commands can be sent to any connected agent and responses are routed back.
    /// </summary>
    public class KliveLinkService : OmniService
    {
        public ConcurrentDictionary<string, ConnectedAgent> ConnectedAgents { get; } = new();
        private KliveLinkServer? _server;

        /// <summary>
        /// Active screen capture viewers per agent ID.
        /// Frontend WebSocket connections that receive forwarded ScreenCaptureFrame data.
        /// </summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _screenViewers = new();

        public class ConnectedAgent
        {
            public string AgentId { get; set; } = "";
            public string MachineName { get; set; } = "";
            public WebSocket Socket { get; set; } = null!;
            public DateTime ConnectedAt { get; set; }
            public CancellationTokenSource Cts { get; set; } = new();

            /// <summary>
            /// Ensures only one WebSocket send at a time.
            /// WebSocket does not support concurrent writes.
            /// </summary>
            public SemaphoreSlim SendLock { get; } = new(1, 1);

            /// <summary>
            /// Pending response handlers keyed by message ID.
            /// When we send a command, we register a TaskCompletionSource here;
            /// the receive loop completes it when the response arrives.
            /// </summary>
            public ConcurrentDictionary<string, TaskCompletionSource<KliveLinkMessage>> PendingResponses { get; } = new();
        }

        public KliveLinkService()
        {
            name = "KliveLinkService";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            // Start the dedicated WebSocket server for agent connections
            _server = new KliveLinkServer(this);
            _server.Start();
            ServiceQuitRequest += () => _server.Stop();

            // Register HTTP API routes on KliveAPI for controlling agents
            var routes = new KliveLinkRoutes(this);
            routes.CreateRoutes();
            ServiceLog($"KliveLinkService started. WebSocket server on port {KliveLinkServer.Port}. Waiting for agent connections.");
        }

        /// <summary>
        /// Called when an agent WebSocket connects. Runs the receive loop on a background task.
        /// </summary>
        public async Task HandleAgentConnection(WebSocket socket, string agentId)
        {
            var agent = new ConnectedAgent
            {
                AgentId = agentId,
                MachineName = agentId,
                Socket = socket,
                ConnectedAt = DateTime.UtcNow
            };

            ConnectedAgents[agentId] = agent;
            ServiceLog($"Agent connected: {agentId}");

            try
            {
                await ReceiveLoop(agent);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Agent {agentId} connection error");
            }
            finally
            {
                ConnectedAgents.TryRemove(agentId, out _);
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing connection", CancellationToken.None);
                    }
                    catch { }
                }
                ServiceLog($"Agent disconnected: {agentId}");
                agent.Cts.Cancel();
            }
        }

        private async Task ReceiveLoop(ConnectedAgent agent)
        {
            var buffer = new byte[64 * 1024];
            var messageBuffer = new StringBuilder();

            while (agent.Socket.State == WebSocketState.Open && !agent.Cts.IsCancellationRequested)
            {
                try
                {
                    var result = await agent.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), agent.Cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ServiceLog($"Agent {agent.AgentId}: received Close frame.");
                        break;
                    }

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        string json = messageBuffer.ToString();
                        messageBuffer.Clear();

                        try
                        {
                            var msg = KliveLinkMessage.Deserialize(json);
                            if (msg != null)
                            {
                                await HandleAgentMessage(agent, msg);
                            }
                        }
                        catch (Exception ex)
                        {
                            ServiceLogError(ex, $"Error handling message from agent {agent.AgentId}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    ServiceLog($"Agent {agent.AgentId}: receive cancelled.");
                    break;
                }
                catch (WebSocketException ex)
                {
                    ServiceLogError(ex, $"Agent {agent.AgentId}: WebSocket error in receive loop");
                    break;
                }
            }

            ServiceLog($"Agent {agent.AgentId}: receive loop exited (State={agent.Socket.State}).");
        }

        private async Task HandleAgentMessage(ConnectedAgent agent, KliveLinkMessage msg)
        {
            switch (msg.Command)
            {
                case KliveLinkCommandType.Heartbeat:
                    await SendToAgent(agent, new KliveLinkMessage { Command = KliveLinkCommandType.HeartbeatAck });
                    break;

                case KliveLinkCommandType.Pong:
                    break;

                case KliveLinkCommandType.ScreenCaptureFrame:
                    _ = ForwardFrameToViewers(agent.AgentId, msg.Payload ?? "");
                    break;

                default:
                    // Route response to the matching pending request via ReplyToMessageId
                    if (!string.IsNullOrEmpty(msg.ReplyToMessageId)
                        && agent.PendingResponses.TryRemove(msg.ReplyToMessageId, out var tcs))
                    {
                        tcs.TrySetResult(msg);
                    }
                    break;
            }
        }

        /// <summary>
        /// Send a command to a connected agent and wait for the response.
        /// </summary>
        public async Task<KliveLinkMessage?> SendCommandAndWaitAsync(string agentId, KliveLinkMessage command, int timeoutSeconds = 30)
        {
            if (!ConnectedAgents.TryGetValue(agentId, out var agent))
                return null;

            var tcs = new TaskCompletionSource<KliveLinkMessage>();
            agent.PendingResponses[command.MessageId] = tcs;

            await SendToAgent(agent, command);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                agent.PendingResponses.TryRemove(command.MessageId, out _);
                return null;
            }
        }

        /// <summary>
        /// Send a fire-and-forget command to an agent.
        /// </summary>
        public async Task SendCommandAsync(string agentId, KliveLinkMessage command)
        {
            if (ConnectedAgents.TryGetValue(agentId, out var agent))
            {
                await SendToAgent(agent, command);
            }
        }

        private async Task SendToAgent(ConnectedAgent agent, KliveLinkMessage msg)
        {
            if (agent.Socket.State != WebSocketState.Open) return;
            await agent.SendLock.WaitAsync(agent.Cts.Token);
            try
            {
                if (agent.Socket.State != WebSocketState.Open) return;
                byte[] data = Encoding.UTF8.GetBytes(msg.Serialize());
                await agent.Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, agent.Cts.Token);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Error sending to agent {agent.AgentId}");
            }
            finally
            {
                agent.SendLock.Release();
            }
        }

        // --- Screen capture viewer management ---

        public string AddScreenViewer(string agentId, WebSocket viewerSocket)
        {
            string viewerId = Guid.NewGuid().ToString("N");
            var viewers = _screenViewers.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, WebSocket>());
            viewers[viewerId] = viewerSocket;
            ServiceLog($"Screen viewer {viewerId} added for agent {agentId} (total: {viewers.Count})");
            return viewerId;
        }

        public void RemoveScreenViewer(string agentId, string viewerId)
        {
            if (_screenViewers.TryGetValue(agentId, out var viewers))
            {
                viewers.TryRemove(viewerId, out _);
                ServiceLog($"Screen viewer {viewerId} removed for agent {agentId} (remaining: {viewers.Count})");
            }
        }

        internal async Task ForwardFrameToViewers(string agentId, string frameJson)
        {
            if (!_screenViewers.TryGetValue(agentId, out var viewers) || viewers.IsEmpty)
                return;

            byte[] data = Encoding.UTF8.GetBytes(frameJson);
            var segment = new ArraySegment<byte>(data);

            foreach (var (viewerId, socket) in viewers)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        viewers.TryRemove(viewerId, out _);
                    }
                }
                catch
                {
                    viewers.TryRemove(viewerId, out _);
                }
            }
        }
    }
}
