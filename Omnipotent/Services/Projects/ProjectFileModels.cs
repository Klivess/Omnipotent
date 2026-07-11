namespace Omnipotent.Services.Projects;

/// <summary>The kind of principal responsible for a shared-project filesystem change.</summary>
public enum ProjectFileActorType
{
    Unknown,
    User,
    Commander,
    Agent,
    System,
}

/// <summary>Why a file first appeared in the shared project volume.</summary>
public enum ProjectFileOrigin
{
    Filesystem,
    InitialUpload,
    UserUpload,
    AgentTool,
    System,
}

public enum ProjectFileKind
{
    File,
    Directory,
}

public enum ProjectFileConflictPolicy
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

public enum ProjectUploadPurpose
{
    Initial,
    ExistingProject,
}

public enum ProjectUploadStatus
{
    Open,
    Committed,
    Cancelled,
    Expired,
}

public enum ProjectFileOperation
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
public sealed record ProjectFileActor(
    ProjectFileActorType Type,
    string ID,
    string DisplayName)
{
    public static ProjectFileActor Unknown { get; } = new(ProjectFileActorType.Unknown, "", "Unknown");
    public static ProjectFileActor System { get; } = new(ProjectFileActorType.System, "system", "System");
}

/// <summary>The current metadata record for one file or directory.</summary>
public sealed class ProjectFileEntry
{
    public string FileID { get; init; } = "";
    public string ProjectID { get; init; } = "";
    /// <summary>Unicode-normalized, slash-separated path relative to the project volume.</summary>
    public string Path { get; init; } = "";
    public ProjectFileKind Kind { get; init; }
    public long Size { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Sha256 { get; init; }
    public DateTime FileSystemModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public ProjectFileActor CreatedBy { get; init; } = ProjectFileActor.Unknown;
    public ProjectFileActor ModifiedBy { get; init; } = ProjectFileActor.Unknown;
    public ProjectFileOrigin Origin { get; init; }
    public string? Description { get; init; }
    public bool Important { get; init; }
}

/// <summary>Immutable audit record. Deleted entries remain discoverable here.</summary>
public sealed class ProjectFileEvent
{
    public long EventID { get; init; }
    public string ProjectID { get; init; } = "";
    public ProjectFileOperation Operation { get; init; }
    public string Path { get; init; } = "";
    public string? PreviousPath { get; init; }
    public DateTime TimestampUtc { get; init; }
    public ProjectFileActor Actor { get; init; } = ProjectFileActor.Unknown;
    public string? BatchID { get; init; }
    public long? Size { get; init; }
    public string? Detail { get; init; }
}

public sealed class ProjectFileListRequest
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

public sealed class ProjectFileListResult
{
    public IReadOnlyList<ProjectFileEntry> Entries { get; init; } = Array.Empty<ProjectFileEntry>();
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class ProjectFileReconcileResult
{
    public int Created { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
}

public sealed class ProjectFileUploadSession
{
    public string SessionID { get; init; } = "";
    public string? ProjectID { get; init; }
    public ProjectUploadPurpose Purpose { get; init; }
    public ProjectUploadStatus Status { get; init; }
    public ProjectFileActor Actor { get; init; } = ProjectFileActor.Unknown;
    public DateTime CreatedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
}

public sealed class ProjectFileUploadItem
{
    public string UploadFileID { get; init; } = "";
    public string SessionID { get; init; } = "";
    public string Path { get; init; } = "";
    public long ExpectedSize { get; init; }
    public long ReceivedSize { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Sha256 { get; init; }
}

public sealed class ProjectFileCommitOptions
{
    /// <summary>Default is deliberately fail: callers must opt into destructive replacement.</summary>
    public ProjectFileConflictPolicy ConflictPolicy { get; init; } = ProjectFileConflictPolicy.Fail;
    /// <summary>Optional per-normalized-path overrides, compared case-insensitively.</summary>
    public IReadOnlyDictionary<string, ProjectFileConflictPolicy>? PathPolicies { get; init; }
}

public sealed class ProjectFileCommitItem
{
    public string RequestedPath { get; init; } = "";
    public string? CommittedPath { get; init; }
    public ProjectFileConflictPolicy AppliedPolicy { get; init; }
    public bool Skipped { get; init; }
    public ProjectFileEntry? Entry { get; init; }
}

public sealed class ProjectFileCommitResult
{
    public string SessionID { get; init; } = "";
    public string ProjectID { get; init; } = "";
    public string BatchID { get; init; } = "";
    public IReadOnlyList<ProjectFileCommitItem> Items { get; init; } = Array.Empty<ProjectFileCommitItem>();
    public long TotalBytes { get; init; }
}

/// <summary>Filesystem and safety limits. Tests can supply isolated roots and a deterministic clock.</summary>
public sealed class ProjectFileStoreOptions
{
    public required string VolumesRoot { get; init; }
    public required string MetadataRoot { get; init; }
    /// <summary>Optional direct override; otherwise MetadataRoot/project-files.db.</summary>
    public string? MetadataDatabasePath { get; init; }
    /// <summary>Optional direct override; otherwise MetadataRoot/Staging.</summary>
    public string? StagingRoot { get; init; }
    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxChunkBytes { get; init; } = 8 * 1024 * 1024;
    public long MinimumFreeDiskBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public TimeSpan UploadTimeToLive { get; init; } = TimeSpan.FromHours(24);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public class ProjectFileException : InvalidOperationException
{
    public ProjectFileException(string message) : base(message) { }
    public ProjectFileException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProjectFileConflictException : ProjectFileException
{
    public string Path { get; }

    public ProjectFileConflictException(string path)
        : base($"A file or directory already exists at '{path}'. Choose replace, keep-both, or skip explicitly.")
    {
        Path = path;
    }
}
