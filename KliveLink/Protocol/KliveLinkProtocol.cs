using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace KliveLink.Protocol
{
    /// <summary>
    /// All communication between Omnipotent server and KliveLink agent uses these models.
    /// Every message is JSON-serialized and sent over WebSocket.
    /// </summary>

    [JsonConverter(typeof(StringEnumConverter))]
    public enum KliveLinkCommandType
    {
        Ping,
        Pong,
        Heartbeat,
        HeartbeatAck,

        // System info
        GetSystemInfo,
        SystemInfoResponse,

        // Process management
        RunProcess,
        RunProcessResult,
        ListProcesses,
        ListProcessesResult,
        KillProcess,
        KillProcessResult,

        // Terminal
        RunTerminalCommand,
        TerminalCommandResult,

        // Screen capture
        RequestScreenCapture,
        ScreenCaptureFrame,
        StopScreenCapture,

        // File operations
        ListDirectory,
        ListDirectoryResult,
        DownloadFile,
        DownloadFileResult,
        UploadFile,
        UploadFileAck,

        // Agent management
        GetAgentStatus,
        AgentStatusResponse,
        DisconnectAgent,

        // Error
        Error,
    }

    public class KliveLinkMessage
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
        public KliveLinkCommandType Command { get; set; }
        public string? Payload { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string Serialize() => JsonConvert.SerializeObject(this);
        public static KliveLinkMessage? Deserialize(string json) => JsonConvert.DeserializeObject<KliveLinkMessage>(json);
    }

    // --- Payloads ---

    public class SystemInfoPayload
    {
        public string MachineName { get; set; } = "";
        public string OSVersion { get; set; } = "";
        public string UserName { get; set; } = "";
        public int ProcessorCount { get; set; }
        public long TotalMemoryMB { get; set; }
        public DateTime AgentStartTime { get; set; }
        public string AgentVersion { get; set; } = "";
    }

    public class RunProcessPayload
    {
        public string FileName { get; set; } = "";
        public string Arguments { get; set; } = "";
        public bool WaitForExit { get; set; } = false;
        public int TimeoutSeconds { get; set; } = 30;
    }

    public class RunProcessResultPayload
    {
        public int? ExitCode { get; set; }
        public string StandardOutput { get; set; } = "";
        public string StandardError { get; set; } = "";
        public bool TimedOut { get; set; }
        public int ProcessId { get; set; }
    }

    public class TerminalCommandPayload
    {
        public string Command { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;
    }

    public class TerminalCommandResultPayload
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public bool TimedOut { get; set; }
    }

    public class ProcessInfoPayload
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public long MemoryMB { get; set; }
    }

    public class KillProcessPayload
    {
        public int ProcessId { get; set; }
    }

    public class KillProcessResultPayload
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public class ScreenCaptureRequestPayload
    {
        public int MonitorIndex { get; set; } = 0;
        public int Quality { get; set; } = 50;
        public int IntervalMs { get; set; } = 1000;
    }

    public class ScreenCaptureFramePayload
    {
        public int MonitorIndex { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Base64JpegData { get; set; } = "";
    }

    public class ListDirectoryPayload
    {
        public string Path { get; set; } = "";
    }

    public class DirectoryEntryPayload
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }
    }

    public class ListDirectoryResultPayload
    {
        public string Path { get; set; } = "";
        public List<DirectoryEntryPayload> Entries { get; set; } = new();
        public string? Error { get; set; }
    }

    public class DownloadFilePayload
    {
        public string FilePath { get; set; } = "";
    }

    public class DownloadFileResultPayload
    {
        public string FilePath { get; set; } = "";
        public string Base64Data { get; set; } = "";
        public long SizeBytes { get; set; }
        public string? Error { get; set; }
    }

    public class UploadFilePayload
    {
        public string DestinationPath { get; set; } = "";
        public string Base64Data { get; set; } = "";
    }

    public class UploadFileAckPayload
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public class AgentStatusPayload
    {
        public string AgentId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public bool ConsentGranted { get; set; }
        public DateTime ConnectedSince { get; set; }
        public bool IsScreenCaptureActive { get; set; }
    }

    public class ErrorPayload
    {
        public string Message { get; set; } = "";
        public string? Details { get; set; }
    }
}
