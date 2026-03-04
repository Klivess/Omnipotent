using System.Net.WebSockets;
using System.Text;
using KliveLink.Protocol;
using Newtonsoft.Json;

namespace KliveLink.Agent
{
    /// <summary>
    /// WebSocket client that maintains a persistent connection to the Omnipotent server.
    /// Dispatches incoming commands to the appropriate executor and sends responses back.
    /// </summary>
    public class KliveLinkClient : IDisposable
    {
        private readonly string _serverUri;
        private readonly string _agentId;
        private readonly string _authToken;
        private readonly CommandExecutor _executor;
        private readonly ScreenCaptureService _screenCapture;
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;

        public bool IsConnected => _socket?.State == WebSocketState.Open;

        public event Action<string>? OnLog;

        public KliveLinkClient(string serverUri, string agentId, string authToken,
             CommandExecutor executor, ScreenCaptureService screenCapture)
        {
            _serverUri = serverUri;
            _agentId = agentId;
            _authToken = authToken;
            _executor = executor;
            _screenCapture = screenCapture;

            _screenCapture.OnFrameCaptured += async (frame) =>
            {
                await SendMessage(new KliveLinkMessage
                {
                    Command = KliveLinkCommandType.ScreenCaptureFrame,
                    Payload = JsonConvert.SerializeObject(frame)
                });
            };
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    _socket = new ClientWebSocket();
                    _socket.Options.SetRequestHeader("X-Agent-Id", _agentId);
                    _socket.Options.SetRequestHeader("X-Auth-Token", _authToken);

                    Log($"Connecting to {_serverUri}...");
                    await _socket.ConnectAsync(new Uri(_serverUri), _cts.Token);
                    Log("Connected to Omnipotent server.");

                    _receiveTask = ReceiveLoop(_cts.Token);
                    _heartbeatTask = HeartbeatLoop(_cts.Token);

                    await Task.WhenAny(_receiveTask, _heartbeatTask);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"Connection error: {ex.Message}. Retrying in 10s...");
                }
                finally
                {
                    _screenCapture.StopCapture();
                    if (_socket?.State == WebSocketState.Open)
                    {
                        try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None); }
                        catch { }
                    }
                    _socket?.Dispose();
                    _socket = null;
                }

                if (!_cts.Token.IsCancellationRequested)
                    await Task.Delay(10_000, _cts.Token);
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            var messageBuffer = new StringBuilder();

            while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("Server closed connection.");
                        break;
                    }

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        string json = messageBuffer.ToString();
                        messageBuffer.Clear();
                        _ = Task.Run(() => HandleMessage(json), token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException ex)
                {
                    Log($"WebSocket error: {ex.Message}");
                    break;
                }
            }
        }

        private async Task HeartbeatLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                try
                {
                    await SendMessage(new KliveLinkMessage { Command = KliveLinkCommandType.Heartbeat });
                    await Task.Delay(15_000, token);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }

        private async Task HandleMessage(string json)
        {
            KliveLinkMessage? msg;
            try
            {
                msg = KliveLinkMessage.Deserialize(json);
                if (msg == null) return;
            }
            catch { return; }

            try
            {
                switch (msg.Command)
                {
                    case KliveLinkCommandType.Ping:
                        await SendMessage(new KliveLinkMessage { Command = KliveLinkCommandType.Pong, ReplyToMessageId = msg.MessageId });
                        break;

                    case KliveLinkCommandType.HeartbeatAck:
                        break;

                    case KliveLinkCommandType.GetSystemInfo:
                        var sysInfo = _executor.GetSystemInfo();
                        await SendResponse(KliveLinkCommandType.SystemInfoResponse, sysInfo, msg.MessageId);
                        break;

                    case KliveLinkCommandType.RunProcess:
                        var runReq = JsonConvert.DeserializeObject<RunProcessPayload>(msg.Payload ?? "{}");
                        var runResult = _executor.RunProcess(runReq!);
                        await SendResponse(KliveLinkCommandType.RunProcessResult, runResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.RunTerminalCommand:
                        var termReq = JsonConvert.DeserializeObject<TerminalCommandPayload>(msg.Payload ?? "{}");
                        var termResult = _executor.RunTerminalCommand(termReq!);
                        await SendResponse(KliveLinkCommandType.TerminalCommandResult, termResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.ListProcesses:
                        var procs = _executor.ListProcesses();
                        await SendResponse(KliveLinkCommandType.ListProcessesResult, procs, msg.MessageId);
                        break;

                    case KliveLinkCommandType.KillProcess:
                        var killReq = JsonConvert.DeserializeObject<KillProcessPayload>(msg.Payload ?? "{}");
                        var killResult = _executor.KillProcess(killReq!);
                        await SendResponse(KliveLinkCommandType.KillProcessResult, killResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.RequestScreenCapture:
                        var scReq = JsonConvert.DeserializeObject<ScreenCaptureRequestPayload>(msg.Payload ?? "{}");
                        _screenCapture.StartCapture(scReq!);
                        break;

                    case KliveLinkCommandType.StopScreenCapture:
                        _screenCapture.StopCapture();
                        break;

                    case KliveLinkCommandType.ListDirectory:
                        var lsReq = JsonConvert.DeserializeObject<ListDirectoryPayload>(msg.Payload ?? "{}");
                        var lsResult = _executor.ListDirectory(lsReq!);
                        await SendResponse(KliveLinkCommandType.ListDirectoryResult, lsResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.DownloadFile:
                        var dlReq = JsonConvert.DeserializeObject<DownloadFilePayload>(msg.Payload ?? "{}");
                        var dlResult = _executor.ReadFile(dlReq!);
                        await SendResponse(KliveLinkCommandType.DownloadFileResult, dlResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.UploadFile:
                        var ulReq = JsonConvert.DeserializeObject<UploadFilePayload>(msg.Payload ?? "{}");
                        var ulResult = _executor.WriteFile(ulReq!);
                        await SendResponse(KliveLinkCommandType.UploadFileAck, ulResult, msg.MessageId);
                        break;

                    case KliveLinkCommandType.GetAgentStatus:
                        var status = new AgentStatusPayload
                        {
                            AgentId = _agentId,
                            MachineName = Environment.MachineName,
                            ConnectedSince = DateTime.UtcNow,
                            IsScreenCaptureActive = _screenCapture.IsCapturing
                        };
                        await SendResponse(KliveLinkCommandType.AgentStatusResponse, status, msg.MessageId);
                        break;

                    case KliveLinkCommandType.DisconnectAgent:
                        Log("Server requested disconnect.");
                        _cts?.Cancel();
                        break;

                    default:
                        await SendError(msg.MessageId, $"Unknown command: {msg.Command}");
                        break;
                }
            }
            catch (Exception ex)
            {
                await SendError(msg.MessageId, ex.Message);
            }
        }

        private async Task SendResponse(KliveLinkCommandType command, object payload, string? replyToMessageId = null)
        {
            await SendMessage(new KliveLinkMessage
            {
                Command = command,
                ReplyToMessageId = replyToMessageId,
                Payload = JsonConvert.SerializeObject(payload)
            });
        }

        private async Task SendError(string replyTo, string message)
        {
            await SendMessage(new KliveLinkMessage
            {
                Command = KliveLinkCommandType.Error,
                Payload = JsonConvert.SerializeObject(new ErrorPayload { Message = message })
            });
        }

        private async Task SendMessage(KliveLinkMessage msg)
        {
            if (_socket?.State != WebSocketState.Open) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(msg.Serialize());
                await _socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"Send error: {ex.Message}");
            }
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[KliveLink {DateTime.Now:HH:mm:ss}] {message}");
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _screenCapture.Dispose();
            _socket?.Dispose();
        }
    }
}
