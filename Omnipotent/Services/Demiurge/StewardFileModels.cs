namespace Omnipotent.Services.Demiurge;

/// <summary>The kind of principal responsible for a shared-Steward filesystem change.</summary>
public enum StewardFileActorType
{
    Unknown,
    User,
    Commander,
    Agent,
    System,
}

/// <summary>Why a file first appeared in the shared Steward volume.</summary>
public enum StewardFileOrigin
{
    Filesystem,
    InitialUpload,
    UserUpload,
    AgentTool,
    System,
}

public enum StewardFileKind
{
    File,
    Directory,
}

public enum StewardFileConflictPolicy
{
    /// <summary>Abort before changing the destination when it already exists.</summary>
    Fail,
    /// <summary>Atomically replace the existing destination.</summary>
    Replace,
    /// <summary>Choose an unused "name (n).ext" destination.</summary>
    KeepBoth,
    /// <summary>Leave the existing destination unchanged.</summary>
    Skip,
}

public enum StewardUploadPurpose
{
    Initial,
    ExistingSteward,
}

public enum StewardUploadStatus
{
    Open,
    Committed,
    Cancelled,
    Expired,
}

public enum StewardFileOperation
{
    Upload,
    Write,
    CreateDirectory,
    Move,
    Copy,
    Delete,
    Metadata,
    ReconcileCreate,
    ReconcileModify,
    ReconcileDelete,
}

/// <summary>Stable actor identity stored on current entries and immutable audit events.</summary>
public sealed record StewardFileActor(
    StewardFileActorType Type,
    string ID,
    string DisplayName)
{
    public static StewardFileActor Unknown { get; } = new(StewardFileActorType.Unknown, "", "Unknown");
    public static StewardFileActor System { get; } = new(StewardFileActorType.System, "system", "System");
}

/// <summary>The current metadata record for one file or directory.</summary>
public sealed class StewardFileEntry
{
    public string FileID { get; init; } = "";
    public string StewardID { get; init; } = "";
    /// <summary>Unicode-normalized, slash-separated path relative to the Steward volume.</summary>
    public string Path { get; init; } = "";
    public StewardFileKind Kind { get; init; }
    public long Size { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Sha256 { get; init; }
    public DateTime FileSystemModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public StewardFileActor CreatedBy { get; init; } = StewardFileActor.Unknown;
    public StewardFileActor ModifiedBy { get; init; } = StewardFileActor.Unknown;
    public StewardFileOrigin Origin { get; init; }
    public string? Description { get; init; }
    public bool Important { get; init; }
}

/// <summary>Immutable audit record. Deleted entries remain discoverable here.</summary>
public sealed class StewardFileEvent
{
    public long EventID { get; init; }
    public string StewardID { get; init; } = "";
    public StewardFileOperation Operation { get; init; }
    public string Path { get; init; } = "";
    public string? PreviousPath { get; init; }
    public DateTime TimestampUtc { get; init; }
    public StewardFileActor Actor { get; init; } = StewardFileActor.Unknown;
    public string? BatchID { get; init; }
    public long? Size { get; init; }
    public string? Detail { get; init; }
}

public sealed class StewardFileListRequest
{
    public string Directory { get; init; } = "";
    public bool Recursive { get; init; }
    public string? Search { get; init; }
    /// <summary>Optional slash-aware wildcard filter; supports *, ?, and **.</summary>
    public string? Glob { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 200;
    /// <summary>Detect direct shell/container changes before returning the listing.</summary>
    public bool Reconcile { get; init; } = true;
}

public sealed class StewardFileListResult
{
    public IReadOnlyList<StewardFileEntry> Entries { get; init; } = Array.Empty<StewardFileEntry>();
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class StewardFileReconcileResult
{
    public int Created { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
}

public sealed class StewardFileUploadSession
{
    public string SessionID { get; init; } = "";
    public string? StewardID { get; init; }
    public StewardUploadPurpose Purpose { get; init; }
    public StewardUploadStatus Status { get; init; }
    public StewardFileActor Actor { get; init; } = StewardFileActor.Unknown;
    public DateTime CreatedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
}

public sealed class StewardFileUploadItem
{
    public string UploadFileID { get; init; } = "";
    public string SessionID { get; init; } = "";
    public string Path { get; init; } = "";
    public long ExpectedSize { get; init; }
    public long ReceivedSize { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Sha256 { get; init; }
}

public sealed class StewardFileCommitOptions
{
    /// <summary>Default is deliberately fail: callers must opt into destructive replacement.</summary>
    public StewardFileConflictPolicy ConflictPolicy { get; init; } = StewardFileConflictPolicy.Fail;
    /// <summary>Optional per-normalized-path overrides, compared case-insensitively.</summary>
    public IReadOnlyDictionary<string, StewardFileConflictPolicy>? PathPolicies { get; init; }
}

public sealed class StewardFileCommitItem
{
    public string RequestedPath { get; init; } = "";
    public string? CommittedPath { get; init; }
    public StewardFileConflictPolicy AppliedPolicy { get; init; }
    public bool Skipped { get; init; }
    public StewardFileEntry? Entry { get; init; }
}

public sealed class StewardFileCommitResult
{
    public string SessionID { get; init; } = "";
    public string StewardID { get; init; } = "";
    public string BatchID { get; init; } = "";
    public IReadOnlyList<StewardFileCommitItem> Items { get; init; } = Array.Empty<StewardFileCommitItem>();
    public long TotalBytes { get; init; }
}

/// <summary>Filesystem and safety limits. Tests can supply isolated roots and a deterministic clock.</summary>
public sealed class StewardFileStoreOptions
{
    public required string VolumesRoot { get; init; }
    public required string MetadataRoot { get; init; }
    /// <summary>Optional direct override; otherwise MetadataRoot/steward-files.db.</summary>
    public string? MetadataDatabasePath { get; init; }
    /// <summary>Optional direct override; otherwise MetadataRoot/Staging.</summary>
    public string? StagingRoot { get; init; }
    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxChunkBytes { get; init; } = 8 * 1024 * 1024;
    public long MinimumFreeDiskBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public TimeSpan UploadTimeToLive { get; init; } = TimeSpan.FromHours(24);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public class StewardFileException : InvalidOperationException
{
    public StewardFileException(string message) : base(message) { }
    public StewardFileException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class StewardFileConflictException : StewardFileException
{
    public string Path { get; }

    public StewardFileConflictException(string path)
        : base($"A file or directory already exists at '{path}'. Choose replace, keep-both, or skip explicitly.")
    {
        Path = path;
    }
}
