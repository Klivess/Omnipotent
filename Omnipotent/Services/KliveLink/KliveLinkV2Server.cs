using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KliveLink.Contracts;

namespace Omnipotent.Services.KliveLink;

/// <summary>
/// Dedicated v2 WebSocket listener. It binds loopback by default and is intended to sit
/// behind Caddy/443 (or a direct TLS reverse proxy). The listener owns sessions, command
/// correlation, screen fan-out, collection uploads and live events.
/// </summary>
public sealed class KliveLinkV2Server : IDisposable
{
    private readonly KliveLinkV2Service _service;
    private readonly KliveLinkV2Options _options;
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, KliveLinkV2Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _screenViewers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _shellViewers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WebSocket> _eventViewers = new();
    private bool _running;
    public string? LastError { get; private set; }

    public KliveLinkV2Server(KliveLinkV2Service service, KliveLinkV2Options options)
    {
        _service = service;
        _options = options;
        _listener = new HttpListener();
    }

    public int SessionCount => _sessions.Count;

    public void Start()
    {
        _listener.Prefixes.Add(_options.ListenPrefix);
        _listener.Start();
        _running = true;
        _ = Task.Run(ListenLoop);
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        foreach (var session in _sessions.Values) session.Abort();
        _sessions.Clear();
        try { _listener.Close(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (_running)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (Exception ex)
            {
                if (_running) await _service.ServiceLogError(ex, "KliveLink v2 listener error");
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
            string token = context.Request.Headers["X-Enrollment-Token"] ?? "";
            if (string.IsNullOrWhiteSpace(agentId) || !_options.IsTokenValid(token))
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await HandleAgentSocketAsync(wsContext.WebSocket, agentId, token);
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            await _service.ServiceLogError(ex, "KliveLink v2 connection error");
            try { context.Response.Close(); } catch { }
        }
    }

    public async Task HandleAgentSocketAsync(WebSocket socket, string agentId, string token)
    {
        if (string.IsNullOrWhiteSpace(agentId) || !_options.IsTokenValid(token))
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "agent auth rejected", CancellationToken.None); } catch { }
            socket.Dispose();
            return;
        }
        var session = new KliveLinkV2Session(this, socket, agentId, token);
        await session.RunAsync();
    }

    internal async Task RegisterSessionAsync(KliveLinkV2Session session, V2Hello hello, string token)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var existing = _service.Store.GetAgent(session.AgentId);
            _service.Store.UpsertAgent(new KliveLinkV2AgentRecord
            {
                AgentId = session.AgentId,
                MachineName = hello.MachineName,
                OsVersion = hello.OsVersion,
                AgentVersion = hello.AgentVersion,
                FirstSeen = existing?.FirstSeen ?? now,
                LastSeen = now,
                ConnectedAt = now,
                CapabilitiesJson = JsonSerializer.Serialize(hello.Capabilities ?? Array.Empty<string>(), V2Json.Options),
                TagsJson = existing?.TagsJson ?? "[]",
                GroupName = existing?.GroupName ?? "",
                CollectionStatus = existing?.CollectionStatus ?? "none",
                HealthJson = existing?.HealthJson ?? "{}",
                TokenHash = KliveLinkV2Options.NormalizeHash(token),
                IsConnected = true,
            });
            _sessions[session.AgentId] = session;
            _service.Store.Audit(session.AgentId, "agent.connect", session.AgentId, new { hello.MachineName, hello.OsVersion, hello.AgentVersion });
            await BroadcastEventAsync(new { type = "agent.connected", agentId = session.AgentId, at = now });
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            throw;
        }
    }

    internal async Task UnregisterSessionAsync(KliveLinkV2Session session)
    {
        if (_sessions.TryGetValue(session.AgentId, out var current) && ReferenceEquals(current, session))
        {
            _sessions.TryRemove(session.AgentId, out _);
            _service.Store.SetConnected(session.AgentId, false);
            _service.Store.Audit(session.AgentId, "agent.disconnect", session.AgentId, new { });
            await CloseScreenViewersAsync(session.AgentId);
            await CloseShellViewersAsync(session.AgentId);
            await BroadcastEventAsync(new { type = "agent.disconnected", agentId = session.AgentId, at = DateTimeOffset.UtcNow });
        }
    }

    internal async Task BroadcastEventAsync(object payload)
    {
        string json = JsonSerializer.Serialize(payload, V2Json.Options);
        foreach (var pair in _eventViewers.ToArray())
        {
            try
            {
                if (pair.Value.State == WebSocketState.Open)
                    await pair.Value.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    _eventViewers.TryRemove(pair.Key, out _);
            }
            catch
            {
                _eventViewers.TryRemove(pair.Key, out _);
            }
        }
    }

    public async Task<V2Result> SendCommandAsync(string agentId, V2CommandOperation operation, object? payload, int timeoutMs, string actor = "")
    {
        if (!_sessions.TryGetValue(agentId, out var session) || !session.IsOpen)
        {
            return new V2Result
            {
                CommandId = "",
                Status = V2CommandStatus.Failed,
                Error = "Agent is not connected.",
            };
        }

        string commandId = Guid.NewGuid().ToString("N");
        string payloadJson = payload == null ? "{}" : JsonSerializer.Serialize(payload, V2Json.Options);
        _service.Store.RecordCommand(commandId, agentId, actor, operation.ToString(), payloadJson, "pending", DateTimeOffset.UtcNow);
        var command = new V2Command
        {
            CommandId = commandId,
            Operation = operation,
            TimeoutMs = Math.Clamp(timeoutMs, 1_000, 600_000),
            Payload = payloadJson,
        };

        var tcs = new TaskCompletionSource<V2Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[commandId] = tcs;
        try
        {
            await session.SendAsync(V2FrameType.Command, command, CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(command.TimeoutMs));
            cts.Token.Register(() => tcs.TrySetResult(new V2Result
            {
                CommandId = commandId,
                Status = V2CommandStatus.Timeout,
                Error = "Command timed out.",
            }));
            V2Result result = await tcs.Task;
            _service.Store.CompleteCommand(commandId, result.Status.ToString(), result.Payload, result.Error);
            _service.Store.Audit(actor, "command." + operation, agentId, new { commandId, status = result.Status.ToString(), error = result.Error });
            return result;
        }
        catch (Exception ex)
        {
            _service.Store.CompleteCommand(commandId, "failed", null, ex.Message);
            return new V2Result { CommandId = commandId, Status = V2CommandStatus.Failed, Error = ex.Message };
        }
        finally
        {
            session.Pending.TryRemove(commandId, out _);
        }
    }

    internal void CompleteResult(V2Result result)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Pending.TryGetValue(result.CommandId, out var tcs))
            {
                tcs.TrySetResult(result);
                return;
            }
        }
    }

    internal void TouchAgent(string agentId)
    {
        try { _service.Store.TouchAgent(agentId); } catch { }
    }

    internal async Task HandleStreamDataAsync(string agentId, V2StreamData data)
    {
        if (!_screenViewers.TryGetValue(agentId, out var viewers) || viewers.IsEmpty) return;
        string json = JsonSerializer.Serialize(new
        {
            type = "frame",
            streamId = data.StreamId,
            base64 = data.Base64,
            metadata = SafeJson(data.Metadata),
        }, V2Json.Options);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        foreach (var pair in viewers.ToArray())
        {
            try
            {
                if (pair.Value.State == WebSocketState.Open)
                    await pair.Value.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    viewers.TryRemove(pair.Key, out _);
            }
            catch
            {
                viewers.TryRemove(pair.Key, out _);
            }
        }
    }

    internal async Task HandleShellOutputAsync(string agentId, V2ShellOutput output)
    {
        if (!_shellViewers.TryGetValue(agentId, out var viewers) || viewers.IsEmpty) return;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "output", text = output.Text }, V2Json.Options));
        foreach (var pair in viewers.ToArray())
        {
            try
            {
                if (pair.Value.State == WebSocketState.Open)
                    await pair.Value.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    viewers.TryRemove(pair.Key, out _);
            }
            catch { viewers.TryRemove(pair.Key, out _); }
        }
    }

    public string AddShellViewer(string agentId, WebSocket socket)
    {
        string id = Guid.NewGuid().ToString("N");
        var viewers = _shellViewers.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, WebSocket>());
        viewers[id] = socket;
        return id;
    }

    public void RemoveShellViewer(string agentId, string id)
    {
        if (_shellViewers.TryGetValue(agentId, out var viewers)) viewers.TryRemove(id, out _);
    }

    public async Task SendShellInputAsync(string agentId, string text)
    {
        if (_sessions.TryGetValue(agentId, out var session))
            await session.SendAsync(V2FrameType.ShellInput, new V2ShellInput { Text = text }, CancellationToken.None);
    }

    internal void HandleCollectionCompleted(KliveLinkV2Session session, KliveLinkV2SnapshotRecord snapshot, List<V2CollectionRecord> records)
    {
        try
        {
            string dir = Path.Combine(_options.CollectionDirectory, session.AgentId);
            Directory.CreateDirectory(dir);
            string finalPath = Path.Combine(dir, $"{snapshot.SnapshotId}.zip");
            if (File.Exists(snapshot.BundlePath))
                File.Move(snapshot.BundlePath, finalPath, true);
            snapshot.BundlePath = _service.Store.EncryptBundle(finalPath);
            _service.Store.CreateSnapshot(snapshot, records ?? new List<V2CollectionRecord>(), snapshot.BundlePath);
            _ = BroadcastEventAsync(new
            {
                type = "collection.completed",
                agentId = session.AgentId,
                snapshotId = snapshot.SnapshotId,
                tier = snapshot.Tier,
                bytes = snapshot.Bytes,
            });
        }
        catch (Exception ex)
        {
            _ = _service.ServiceLogError(ex, "KliveLink v2 collection persistence failed");
        }
    }

    public string AddScreenViewer(string agentId, WebSocket socket)
    {
        string id = Guid.NewGuid().ToString("N");
        var viewers = _screenViewers.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, WebSocket>());
        viewers[id] = socket;
        return id;
    }

    public void RemoveScreenViewer(string agentId, string id)
    {
        if (_screenViewers.TryGetValue(agentId, out var viewers)) viewers.TryRemove(id, out _);
    }

    public string AddEventViewer(WebSocket socket)
    {
        string id = Guid.NewGuid().ToString("N");
        _eventViewers[id] = socket;
        return id;
    }

    public void RemoveEventViewer(string id) => _eventViewers.TryRemove(id, out _);

    private async Task CloseScreenViewersAsync(string agentId)
    {
        if (!_screenViewers.TryRemove(agentId, out var viewers)) return;
        foreach (var socket in viewers.Values)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent disconnected", CancellationToken.None);
            }
            catch { }
        }
    }

    private async Task CloseShellViewersAsync(string agentId)
    {
        if (!_shellViewers.TryRemove(agentId, out var viewers)) return;
        foreach (var socket in viewers.Values)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent disconnected", CancellationToken.None);
            }
            catch { }
        }
    }

    private static object SafeJson(string json)
    {
        try { return JsonSerializer.Deserialize<object>(json, V2Json.Options) ?? new { }; }
        catch { return new { }; }
    }

    public void Dispose()
    {
        Stop();
    }
}

public sealed class KliveLinkV2Session
{
    private readonly KliveLinkV2Server _server;
    private readonly WebSocket _socket;
    private readonly string _token;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, CollectionUpload> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastTouch = DateTimeOffset.MinValue;

    public string AgentId { get; }
    public ConcurrentDictionary<string, TaskCompletionSource<V2Result>> Pending { get; } = new();
    public bool IsOpen => _socket.State == WebSocketState.Open;

    public KliveLinkV2Session(KliveLinkV2Server server, WebSocket socket, string agentId, string token)
    {
        _server = server;
        _socket = socket;
        AgentId = agentId;
        _token = token;
    }

    public async Task RunAsync()
    {
        try
        {
            await HandshakeAsync();
            Task receive = ReceiveLoopAsync();
            Task heartbeat = HeartbeatLoopAsync();
            await Task.WhenAny(receive, heartbeat);
        }
        catch (Exception)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "session ended", CancellationToken.None); } catch { }
        }
        finally
        {
            _cts.Cancel();
            foreach (var pending in Pending.Values)
                pending.TrySetResult(new V2Result { Status = V2CommandStatus.Failed, Error = "Agent disconnected." });
            Pending.Clear();
            await _server.UnregisterSessionAsync(this);
            _socket.Dispose();
        }
    }

    private async Task HandshakeAsync()
    {
        V2Frame first = await V2FrameCodec.ReadFrameAsync(_socket, _cts.Token);
        if (first.Type != V2FrameType.Hello)
            throw new InvalidDataException("Expected HELLO.");
        V2Hello hello = V2Json.Deserialize<V2Hello>(first.Payload) ?? throw new InvalidDataException("Invalid HELLO.");
        if (!string.Equals(hello.AgentId, AgentId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HELLO agent id does not match header.");

        string serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await SendAsync(V2FrameType.Challenge, new V2Challenge { ServerNonce = serverNonce }, _cts.Token);

        V2Frame authFrame = await V2FrameCodec.ReadFrameAsync(_socket, _cts.Token);
        if (authFrame.Type != V2FrameType.Auth) throw new InvalidDataException("Expected AUTH.");
        V2Auth auth = V2Json.Deserialize<V2Auth>(authFrame.Payload) ?? throw new InvalidDataException("Invalid AUTH.");
        string expected = V2Proof.Compute(_token, AgentId, hello.Nonce, serverNonce);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(auth.Proof)))
            throw new InvalidDataException("AUTH proof rejected.");

        await _server.RegisterSessionAsync(this, hello, _token);
        await SendAsync(V2FrameType.Welcome, new V2Welcome
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ServerTime = DateTimeOffset.UtcNow.ToString("O"),
            HeartbeatSeconds = 30,
            MaxPayload = V2Frame.MaxPayload,
        }, _cts.Token);
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested && IsOpen)
        {
            try
            {
                V2Frame frame = await V2FrameCodec.ReadFrameAsync(_socket, _cts.Token);
                await HandleFrameAsync(frame);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception ex)
            {
                await SendAsync(V2FrameType.Error, new V2Error
                {
                    Code = "frame_error",
                    Message = ex.Message,
                    Retryable = false,
                }, CancellationToken.None);
                break;
            }
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_cts.IsCancellationRequested && IsOpen)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                await SendAsync(V2FrameType.Ping, new V2Ping(), _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    private async Task HandleFrameAsync(V2Frame frame)
    {
        if (DateTimeOffset.UtcNow - _lastTouch > TimeSpan.FromSeconds(10))
        {
            _lastTouch = DateTimeOffset.UtcNow;
            _server.TouchAgent(AgentId);
        }
        switch (frame.Type)
        {
            case V2FrameType.Ping:
                await SendAsync(V2FrameType.Pong, new V2Pong { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, _cts.Token);
                break;
            case V2FrameType.Pong:
                break;
            case V2FrameType.Result:
                V2Result? result = V2Json.Deserialize<V2Result>(frame.Payload);
                if (result != null) _server.CompleteResult(result);
                break;
            case V2FrameType.Error:
                V2Error? error = V2Json.Deserialize<V2Error>(frame.Payload);
                if (error?.CommandId != null)
                    _server.CompleteResult(new V2Result { CommandId = error.CommandId, Status = V2CommandStatus.Failed, Error = error.Message });
                break;
            case V2FrameType.StreamData:
                V2StreamData? data = V2Json.Deserialize<V2StreamData>(frame.Payload);
                if (data != null) await _server.HandleStreamDataAsync(AgentId, data);
                break;
            case V2FrameType.ShellOutput:
                V2ShellOutput? shell = V2Json.Deserialize<V2ShellOutput>(frame.Payload);
                if (shell != null) await _server.HandleShellOutputAsync(AgentId, shell);
                break;
            case V2FrameType.CollectionBegin:
                await BeginCollectionAsync(V2Json.Deserialize<V2CollectionBegin>(frame.Payload));
                break;
            case V2FrameType.CollectionChunk:
                await AppendCollectionAsync(V2Json.Deserialize<V2CollectionChunk>(frame.Payload));
                break;
            case V2FrameType.CollectionEnd:
                await EndCollectionAsync(V2Json.Deserialize<V2CollectionEnd>(frame.Payload));
                break;
            case V2FrameType.Ack:
                break;
        }
    }

    private async Task BeginCollectionAsync(V2CollectionBegin? begin)
    {
        if (begin == null || string.IsNullOrWhiteSpace(begin.SnapshotId)) return;
        string dir = Path.Combine(Path.GetTempPath(), "klive-v2-collections");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{AgentId}-{begin.SnapshotId}.zip");
        var upload = new CollectionUpload
        {
            SnapshotId = begin.SnapshotId,
            Manifest = SafeManifest(begin.ManifestJson),
            File = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            Path = path,
        };
        _uploads[begin.SnapshotId] = upload;
        await Task.CompletedTask;
    }

    private async Task AppendCollectionAsync(V2CollectionChunk? chunk)
    {
        if (chunk == null || !_uploads.TryGetValue(chunk.SnapshotId, out var upload)) return;
        byte[] data = Convert.FromBase64String(chunk.Base64 ?? "");
        await upload.File.WriteAsync(data);
        upload.Bytes += data.Length;
    }

    private async Task EndCollectionAsync(V2CollectionEnd? end)
    {
        if (end == null || !_uploads.Remove(end.SnapshotId, out var upload)) return;
        await upload.File.FlushAsync();
        upload.File.Dispose();
        if (!string.IsNullOrWhiteSpace(end.Error))
        {
            try { File.Delete(upload.Path); } catch { }
            return;
        }

        string finalDir = Path.Combine(Path.GetTempPath(), "klive-v2-collections");
        var manifest = upload.Manifest;
        var snapshot = new KliveLinkV2SnapshotRecord
        {
            SnapshotId = end.SnapshotId,
            AgentId = AgentId,
            Tier = manifest.Tier.ToString(),
            Profile = manifest.Profile,
            Status = "complete",
            StartedAt = manifest.StartedAt,
            CompletedAt = manifest.CompletedAt == default ? DateTimeOffset.UtcNow : manifest.CompletedAt,
            Bytes = end.Bytes > 0 ? end.Bytes : upload.Bytes,
            Sha256 = end.Sha256,
            ManifestJson = V2Json.Serialize(new
            {
                manifest.SnapshotId,
                manifest.AgentId,
                manifest.Tier,
                manifest.Profile,
                manifest.StartedAt,
                manifest.CompletedAt,
                RecordCount = manifest.Records.Count,
                FileCount = manifest.Files.Count,
                manifest.Errors,
            }),
            BundlePath = upload.Path,
        };
        upload.Snapshot = snapshot;
        _server.HandleCollectionCompleted(this, snapshot, manifest.Records);
    }

    private static V2CollectionManifest SafeManifest(string json)
    {
        try { return V2Json.Deserialize<V2CollectionManifest>(json) ?? new V2CollectionManifest(); }
        catch { return new V2CollectionManifest(); }
    }

    public async Task SendAsync(V2FrameType type, object payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await V2FrameCodec.SendAsync(_socket, new V2Frame
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload, V2Json.Options),
            }, ct);
        }
        finally { _sendLock.Release(); }
    }

    public void Abort()
    {
        _cts.Cancel();
        try { _socket.Abort(); } catch { }
    }

    private sealed class CollectionUpload
    {
        public string SnapshotId { get; set; } = "";
        public V2CollectionManifest Manifest { get; set; } = new();
        public FileStream File { get; set; } = null!;
        public string Path { get; set; } = "";
        public long Bytes { get; set; }
        public KliveLinkV2SnapshotRecord? Snapshot { get; set; }
    }
}
