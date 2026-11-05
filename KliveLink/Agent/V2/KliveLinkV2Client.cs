using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using KliveLink.Agent;
using KliveLink.Contracts;
using KliveLink.Protocol;

namespace KliveLink.Agent.V2;

public sealed class KliveLinkV2Client : IDisposable
{
    private readonly string _serverUri;
    private readonly string _agentId;
    private readonly string _token;
    private readonly string? _pinSha256;
    private readonly V2CommandDispatcher _dispatcher;
    private readonly ScreenCaptureService _screenCapture;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _commandSlots = new(4, 4);
    private V2ShellSession? _shell;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private long _lastActivityUtcTicks;

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public event Action<string>? OnLog;

    public KliveLinkV2Client(string serverUri, string agentId, string token,
        V2CommandDispatcher dispatcher, ScreenCaptureService screenCapture, string? pinSha256 = null)
    {
        _serverUri = serverUri;
        _agentId = agentId;
        _token = token;
        _pinSha256 = pinSha256;
        _dispatcher = dispatcher;
        _screenCapture = screenCapture;
        _screenCapture.OnFrameCaptured += frame => _ = SendScreenFrameAsync(frame);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int attempt = 0;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _socket = new ClientWebSocket();
                _socket.Options.SetRequestHeader("X-Agent-Id", _agentId);
                _socket.Options.SetRequestHeader("X-Enrollment-Token", _token);
                if (!string.IsNullOrWhiteSpace(_pinSha256))
                {
                    _socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate == null) return false;
                        string hash = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).ToLowerInvariant();
                        return string.Equals(hash, _pinSha256!.Replace(":", "").Replace(" ", "").ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
                    };
                }
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                Log($"Connecting to {_serverUri}...");
                await _socket.ConnectAsync(new Uri(_serverUri), _cts.Token);
                await HandshakeAsync(_cts.Token);
                Log("Connected and authenticated to KliveLink v2.");
                Interlocked.Exchange(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                attempt = 0;
                _receiveTask = ReceiveLoopAsync(_cts.Token);
                _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
                Task completed = await Task.WhenAny(_receiveTask, _heartbeatTask);
                Log(completed == _receiveTask
                    ? $"Receive loop ended ({_socket?.State})."
                    : $"Heartbeat loop ended ({_socket?.State}).");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Connection error: {ex.Message}");
            }
            finally
            {
                _screenCapture.StopCapture();
                if (_socket?.State == WebSocketState.Open)
                {
                    try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None); } catch { }
                }
                _socket?.Dispose();
                _socket = null;
            }

            if (_cts.Token.IsCancellationRequested) break;
            attempt++;
            int seconds = Math.Min(300, (int)Math.Pow(2, Math.Min(attempt, 8)));
            seconds += Random.Shared.Next(0, Math.Max(1, seconds / 3));
            Log($"Reconnecting in {seconds}s...");
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), _cts.Token); } catch { break; }
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        string clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var hello = new V2Hello
        {
            AgentId = _agentId,
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            AgentVersion = "2.0.0",
            Nonce = clientNonce,
            Capabilities = V2Capabilities.All,
        };
        await SendFrameAsync(V2FrameType.Hello, hello, ct);
        V2Frame challengeFrame = await V2FrameCodec.ReadFrameAsync(_socket!, ct);
        if (challengeFrame.Type != V2FrameType.Challenge)
            throw new InvalidDataException("Expected challenge.");
        V2Challenge challenge = V2Json.Deserialize<V2Challenge>(challengeFrame.Payload) ?? throw new InvalidDataException("Invalid challenge.");
        var auth = new V2Auth { Proof = V2Proof.Compute(_token, _agentId, clientNonce, challenge.ServerNonce) };
        await SendFrameAsync(V2FrameType.Auth, auth, ct);
        V2Frame welcomeFrame = await V2FrameCodec.ReadFrameAsync(_socket!, ct);
        if (welcomeFrame.Type != V2FrameType.Welcome)
        {
            V2Error? error = V2Json.Deserialize<V2Error>(welcomeFrame.Payload);
            throw new InvalidDataException(error?.Message ?? "Authentication rejected.");
        }
        Log(V2Json.Deserialize<V2Welcome>(welcomeFrame.Payload)?.SessionId ?? "session established");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
        {
            try
            {
                V2Frame frame = await V2FrameCodec.ReadFrameAsync(_socket, ct);
                Interlocked.Exchange(ref _lastActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                switch (frame.Type)
                {
                    case V2FrameType.Ping:
                        await SendFrameAsync(V2FrameType.Pong, new V2Pong { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
                        break;
                    case V2FrameType.Pong:
                        break;
                    case V2FrameType.Command:
                        V2Command? command = V2Json.Deserialize<V2Command>(frame.Payload);
                        if (command != null) _ = Task.Run(() => HandleCommandAsync(command), ct);
                        break;
                    case V2FrameType.Cancel:
                        break;
                    case V2FrameType.Error:
                        V2Error? error = V2Json.Deserialize<V2Error>(frame.Payload);
                        Log("Server error: " + (error?.Message ?? frame.Payload));
                        break;
                    case V2FrameType.ShellInput:
                        V2ShellInput? input = V2Json.Deserialize<V2ShellInput>(frame.Payload);
                        if (input != null) _shell?.Write(input.Text);
                        break;
                    case V2FrameType.ShellClose:
                        _shell?.Stop();
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                Log("WebSocket closed: " + ex.Message);
                break;
            }
            catch (Exception ex)
            {
                Log("Receive error: " + ex.Message);
                break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                long last = Interlocked.Read(ref _lastActivityUtcTicks);
                if (last != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(last, TimeSpan.Zero) > TimeSpan.FromSeconds(75))
                {
                    Log("Heartbeat timed out; reconnecting.");
                    break;
                }
                await SendFrameAsync(V2FrameType.Ping, new V2Ping(), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("Heartbeat error: " + ex.Message);
                break;
            }
        }
    }

    private async Task HandleCommandAsync(V2Command command)
    {
        await _commandSlots.WaitAsync();
        try
        {
            V2Result result;
            using JsonDocument payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(command.Payload) ? "{}" : command.Payload);
            switch (command.Operation)
            {
                case V2CommandOperation.GetSystemInfo:
                    result = Ok(command, _dispatcher.GetSystemInfo());
                    break;
                case V2CommandOperation.RunProcess:
                    result = Ok(command, await _dispatcher.RunProcessAsync(Read<V2RunProcessRequest>(payload), CancellationToken.None));
                    break;
                case V2CommandOperation.RunTerminalCommand:
                    result = Ok(command, await _dispatcher.RunTerminalAsync(Read<V2TerminalRequest>(payload), CancellationToken.None));
                    break;
                case V2CommandOperation.ListProcesses:
                    result = Ok(command, _dispatcher.ListProcesses());
                    break;
                case V2CommandOperation.KillProcess:
                    result = Ok(command, _dispatcher.KillProcess(Read<V2KillProcessRequest>(payload)));
                    break;
                case V2CommandOperation.ListDirectory:
                    result = Ok(command, _dispatcher.ListDirectory(Read<V2ListDirectoryRequest>(payload)));
                    break;
                case V2CommandOperation.DownloadFile:
                    result = Ok(command, _dispatcher.DownloadFile(Read<V2DownloadFileRequest>(payload)));
                    break;
                case V2CommandOperation.UploadFile:
                    result = Ok(command, _dispatcher.UploadFile(Read<V2UploadFileRequest>(payload)));
                    break;
                case V2CommandOperation.ScreenStart:
                    _screenCapture.StartCapture(new ScreenCaptureRequestPayload
                    {
                        MonitorIndex = payload.RootElement.TryGetProperty("monitorIndex", out var mi) ? mi.GetInt32() : 0,
                        Quality = payload.RootElement.TryGetProperty("quality", out var q) ? q.GetInt32() : 50,
                        IntervalMs = payload.RootElement.TryGetProperty("intervalMs", out var ii) ? ii.GetInt32() : 1000,
                    });
                    await SendFrameAsync(V2FrameType.StreamOpen, new V2StreamOpen { StreamId = 1, Kind = "screen", Metadata = "{}" }, CancellationToken.None);
                    result = Ok(command, new { started = true, streamId = 1 });
                    break;
                case V2CommandOperation.ScreenStop:
                    _screenCapture.StopCapture();
                    await SendFrameAsync(V2FrameType.StreamEnd, new V2StreamEnd { StreamId = 1 }, CancellationToken.None);
                    result = Ok(command, new { stopped = true });
                    break;
                case V2CommandOperation.ShellOpen:
                    _shell?.Stop();
                    _shell = new V2ShellSession(text => _ = SendFrameAsync(V2FrameType.ShellOutput, new V2ShellOutput { Text = text }, CancellationToken.None));
                    bool started = _shell.Start();
                    result = Ok(command, new { started });
                    break;
                case V2CommandOperation.ShellClose:
                    _shell?.Stop();
                    _shell = null;
                    result = Ok(command, new { stopped = true });
                    break;
                case V2CommandOperation.StartCollection:
                    result = await RunCollectionAsync(command, Read<V2CollectionRequest>(payload));
                    break;
                case V2CommandOperation.UpdateAgent:
                    result = Ok(command, await V2Updater.UpdateAsync(Read<V2UpdateManifest>(payload), CancellationToken.None));
                    break;
                case V2CommandOperation.SelfDestruct:
                    var destroyed = V2SelfDestruct.Run();
                    result = Ok(command, destroyed);
                    await SendResultAsync(result);
                    await Task.Delay(1200);
                    _cts?.Cancel();
                    return;
                case V2CommandOperation.Disconnect:
                    result = Ok(command, new { disconnected = true });
                    await SendResultAsync(result);
                    _cts?.Cancel();
                    return;
                default:
                    result = new V2Result { CommandId = command.CommandId, Status = V2CommandStatus.Unsupported, Error = "Unsupported operation." };
                    break;
            }
            await SendResultAsync(result);
        }
        catch (Exception ex)
        {
            await SendResultAsync(new V2Result { CommandId = command.CommandId, Status = V2CommandStatus.Failed, Error = ex.Message });
        }
        finally
        {
            _commandSlots.Release();
        }
    }

    private async Task<V2Result> RunCollectionAsync(V2Command command, V2CollectionRequest request)
    {
        var bundle = await new V2Collector(_agentId, request.MaxBytes).CollectAsync(request, CancellationToken.None);
        var begin = new V2CollectionBegin
        {
            SnapshotId = bundle.Manifest.SnapshotId,
            Tier = bundle.Manifest.Tier,
            Profile = bundle.Manifest.Profile,
            ManifestJson = V2Json.Serialize(bundle.Manifest),
            RecordCount = bundle.Manifest.Records.Count,
        };
        await SendFrameAsync(V2FrameType.CollectionBegin, begin, CancellationToken.None);
        const int chunkSize = 512 * 1024;
        int sequence = 0;
        for (int offset = 0; offset < bundle.ZipBytes.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, bundle.ZipBytes.Length - offset);
            await SendFrameAsync(V2FrameType.CollectionChunk, new V2CollectionChunk
            {
                SnapshotId = bundle.Manifest.SnapshotId,
                Sequence = sequence++,
                Base64 = Convert.ToBase64String(bundle.ZipBytes, offset, count),
            }, CancellationToken.None);
        }
        await SendFrameAsync(V2FrameType.CollectionEnd, new V2CollectionEnd
        {
            SnapshotId = bundle.Manifest.SnapshotId,
            Sha256 = bundle.Sha256,
            Bytes = bundle.ZipBytes.LongLength,
            RecordCount = bundle.Manifest.Records.Count,
        }, CancellationToken.None);
        return Ok(command, new
        {
            snapshotId = bundle.Manifest.SnapshotId,
            bytes = bundle.ZipBytes.LongLength,
            records = bundle.Manifest.Records.Count,
            sha256 = bundle.Sha256,
        });
    }

    private async Task SendScreenFrameAsync(ScreenCaptureFramePayload frame)
    {
        if (!IsConnected) return;
        try
        {
            await SendFrameAsync(V2FrameType.StreamData, new V2StreamData
            {
                StreamId = 1,
                Base64 = frame.Base64JpegData,
                Metadata = JsonSerializer.Serialize(new
                {
                    width = frame.Width,
                    height = frame.Height,
                    monitor = frame.MonitorIndex,
                    at = DateTimeOffset.UtcNow,
                }, V2Json.Options),
            }, _cts?.Token ?? CancellationToken.None);
        }
        catch { }
    }

    private async Task SendResultAsync(V2Result result)
    {
        await SendFrameAsync(V2FrameType.Result, result, _cts?.Token ?? CancellationToken.None);
    }

    private async Task SendFrameAsync(V2FrameType type, object payload, CancellationToken ct)
    {
        if (_socket?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket?.State != WebSocketState.Open) return;
            await V2FrameCodec.SendAsync(_socket, new V2Frame
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload, V2Json.Options),
            }, ct);
        }
        catch (Exception ex)
        {
            Log("Send error: " + ex.Message);
        }
        finally { _sendLock.Release(); }
    }

    private static V2Result Ok(V2Command command, object payload) => new()
    {
        CommandId = command.CommandId,
        Status = V2CommandStatus.Ok,
        Payload = JsonSerializer.Serialize(payload, V2Json.Options),
    };

    private static T Read<T>(JsonDocument document) => V2Json.Deserialize<T>(document.RootElement.GetRawText()) ?? Activator.CreateInstance<T>();

    private void Log(string message)
    {
        V2Log.Write(message);
        OnLog?.Invoke($"[KliveLink v2 {DateTime.Now:HH:mm:ss}] {message}");
    }

    public void Dispose()
    {
        _shell?.Stop();
        _cts?.Cancel();
        _screenCapture.Dispose();
        _socket?.Dispose();
    }
}
