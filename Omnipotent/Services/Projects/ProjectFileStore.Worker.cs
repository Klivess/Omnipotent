using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Services.Projects;

public sealed partial class ProjectFileStore
{
    public Func<string, IProjectWorkspaceBackend?>? WorkspaceBackend { get; set; }
    public bool IsRemote(string projectID) => WorkspaceBackend?.Invoke(projectID) != null;
    private IProjectWorkspaceBackend? Backend(string projectID) => WorkspaceBackend?.Invoke(projectID);

    private ProjectFileReconcileResult ReconcileWorker(string projectID, IProjectWorkspaceBackend backend)
    {
        // Fetch a complete inventory before changing metadata. A timeout never means deletion.
        var inventory = backend.List(projectID, "", true);
        var prior = LoadEntries(projectID).ToDictionary(e => e.Path, StringComparer.Ordinal);
        int created = 0, modified = 0, deleted = 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var file in inventory)
        {
            prior.Remove(file.Path, out var old);
            bool changed = old == null || old.Size != file.Size || old.FileSystemModifiedUtc != file.FileSystemModifiedUtc;
            var entry = new ProjectFileEntry
            {
                FileID = old?.FileID ?? file.FileID, ProjectID = projectID, Path = file.Path,
                Kind = file.Kind, Size = file.Size, MimeType = old?.MimeType ?? file.MimeType,
                FileSystemModifiedUtc = file.FileSystemModifiedUtc, CreatedUtc = old?.CreatedUtc ?? file.CreatedUtc,
                ModifiedUtc = changed ? file.ModifiedUtc : old!.ModifiedUtc,
                CreatedBy = old?.CreatedBy ?? ProjectFileActor.Unknown,
                ModifiedBy = changed ? ProjectFileActor.Unknown : old!.ModifiedBy,
                Origin = old?.Origin ?? file.Origin, Description = old?.Description, Important = old?.Important ?? false,
                Sha256 = changed ? null : old?.Sha256,
            };
            UpsertEntry(connection, transaction, entry);
            if (changed)
            {
                InsertAudit(connection, transaction, projectID, old == null ? ProjectFileOperation.ReconcileCreate : ProjectFileOperation.ReconcileModify,
                    file.Path, ProjectFileActor.Unknown, null, file.Size, "Observed on Linux worker");
                if (old == null) created++; else modified++;
            }
        }
        foreach (var entry in prior.Values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM file_entries WHERE project_id=$project AND path=$path";
            command.Parameters.AddWithValue("$project", projectID);
            command.Parameters.AddWithValue("$path", entry.Path);
            command.ExecuteNonQuery();
            InsertAudit(connection, transaction, projectID, ProjectFileOperation.ReconcileDelete, entry.Path,
                ProjectFileActor.Unknown, null, entry.Size, "Absent from complete Linux worker inventory");
            deleted++;
        }
        transaction.Commit();
        return new ProjectFileReconcileResult { Created = created, Modified = modified, Deleted = deleted };
    }

    private ProjectFileEntry AttributeWorker(string projectID, string path, ProjectFileActor actor, ProjectFileOperation operation)
    {
        var backend = Backend(projectID)!;
        ReconcileWorker(projectID, backend);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var prior = LoadEntry(connection, projectID, path, transaction) ?? throw new FileNotFoundException(path);
        var entry = new ProjectFileEntry
        {
            FileID = prior.FileID, ProjectID = projectID, Path = path, Kind = prior.Kind, Size = prior.Size,
            MimeType = prior.MimeType, FileSystemModifiedUtc = prior.FileSystemModifiedUtc,
            CreatedUtc = prior.CreatedUtc, ModifiedUtc = UtcNow(), CreatedBy = prior.CreatedBy.Type == ProjectFileActorType.Unknown ? actor : prior.CreatedBy,
            ModifiedBy = actor, Origin = operation == ProjectFileOperation.Upload ? ProjectFileOrigin.UserUpload : ProjectFileOrigin.AgentTool,
            Description = prior.Description, Important = prior.Important,
        };
        UpsertEntry(connection, transaction, entry);
        InsertAudit(connection, transaction, projectID, operation, path, actor, null, entry.Size, "Linux worker operation acknowledged");
        transaction.Commit();
        return entry;
    }

    private ProjectFileEntry WriteWorker(string projectID, string path, string content, ProjectFileActor actor)
    {
        string normalized = NormalizeProjectPath(projectID, path);
        byte[] bytes = Encoding.UTF8.GetBytes(NormalizeUnixText(normalized, content));
        if (bytes.LongLength > options.MaxFileBytes) throw new ProjectFileException("File exceeds configured maximum size");
        var backend = Backend(projectID)!;
        string parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
        if (parent.Length > 0) backend.Mutate(projectID, "mkdir", parent);
        string? version = backend.Version(projectID, normalized);
        using var stream = new MemoryStream(bytes);
        backend.Import(projectID, normalized, stream, version, Guid.NewGuid().ToString("N"));
        return AttributeWorker(projectID, normalized, actor, ProjectFileOperation.Write);
    }

    private ProjectFileCommitResult CommitWorkerUpload(string sessionID, string projectID, ProjectFileActor actor,
        ProjectFileCommitOptions? commitOptions)
    {
        var gate = Gate("session:" + sessionID);
        gate.Wait();
        var concurrency = uploadConcurrency.GetOrAdd(sessionID, _ => new UploadConcurrency());
        int heldSlots = AcquireAllUploadSlots(concurrency);
        try
        {
            using var connection = OpenConnection();
            var session = RequireOpenOwnedSession(connection, sessionID, actor);
            if (session.ProjectID != projectID) throw new ProjectFileException("Upload belongs to another project");
            var uploads = ListUploadItems(sessionID);
            if (uploads.Count == 0 || uploads.Any(u => u.ExpectedSize != u.ReceivedSize))
                throw new ProjectFileException("Upload is empty or incomplete");
            var backend = Backend(projectID)!;
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE IF NOT EXISTS worker_upload_targets (upload_id TEXT PRIMARY KEY, destination TEXT NOT NULL, version TEXT, policy INTEGER NOT NULL)";
                schema.ExecuteNonQuery();
            }
            var committed = new List<ProjectFileCommitItem>();
            long bytes = 0;
            foreach (var upload in uploads)
            {
                string destination = upload.Path;
                var policy = PolicyFor(commitOptions ?? new ProjectFileCommitOptions(), upload.Path, upload.Path);
                string? version = backend.Version(projectID, destination);
                bool savedTarget = false;
                using (var lookup = connection.CreateCommand())
                {
                    lookup.CommandText = "SELECT destination,version,policy FROM worker_upload_targets WHERE upload_id=$id";
                    lookup.Parameters.AddWithValue("$id", upload.UploadFileID);
                    using var reader = lookup.ExecuteReader();
                    if (reader.Read())
                    {
                        destination = reader.GetString(0);
                        version = reader.IsDBNull(1) ? null : reader.GetString(1);
                        policy = (ProjectFileConflictPolicy)reader.GetInt32(2);
                        savedTarget = true;
                    }
                }
                using var stream = File.OpenRead(StagingFilePath(sessionID, upload.UploadFileID));
                string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                stream.Position = 0;
                if (!savedTarget && version != null && version != hash)
                {
                    if (policy == ProjectFileConflictPolicy.Fail) throw new ProjectFileConflictException(destination);
                    if (policy == ProjectFileConflictPolicy.Skip)
                    {
                        committed.Add(new ProjectFileCommitItem { RequestedPath = upload.Path, Skipped = true, AppliedPolicy = policy });
                        continue;
                    }
                    if (policy == ProjectFileConflictPolicy.KeepBoth)
                    {
                        string extension = Path.GetExtension(destination);
                        string stem = destination[..^extension.Length];
                        int number = 1;
                        do { destination = $"{stem} ({number++}){extension}"; } while (backend.Version(projectID, destination) != null);
                        version = null;
                    }
                }
                if (!savedTarget)
                {
                    // Persist the destination and comparison version before the remote write.
                    // A lost reply must not select another KeepBoth name or overwrite newer edits.
                    using var save = connection.CreateCommand();
                    save.CommandText = "INSERT INTO worker_upload_targets VALUES($id,$destination,$version,$policy)";
                    save.Parameters.AddWithValue("$id", upload.UploadFileID);
                    save.Parameters.AddWithValue("$destination", destination);
                    save.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
                    save.Parameters.AddWithValue("$policy", (int)policy);
                    save.ExecuteNonQuery();
                }
                string parent = destination.Contains('/') ? destination[..destination.LastIndexOf('/')] : "";
                if (parent.Length > 0) backend.Mutate(projectID, "mkdir", parent);
                // A partially committed batch is resumable. Never undo acknowledged remote writes.
                backend.Import(projectID, destination, stream, version, upload.UploadFileID);
                var entry = AttributeWorker(projectID, destination, actor, ProjectFileOperation.Upload);
                committed.Add(new ProjectFileCommitItem { RequestedPath = upload.Path, CommittedPath = destination, Entry = entry, AppliedPolicy = policy });
                bytes += upload.ExpectedSize;
            }
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE upload_sessions SET status=$status WHERE session_id=$id";
            update.Parameters.AddWithValue("$status", (int)ProjectUploadStatus.Committed);
            update.Parameters.AddWithValue("$id", sessionID);
            update.ExecuteNonQuery();
            return new ProjectFileCommitResult { SessionID = sessionID, ProjectID = projectID, BatchID = sessionID, Items = committed, TotalBytes = bytes };
        }
        finally { for (int i = 0; i < heldSlots; i++) concurrency.Slots.Release(); gate.Release(); }
    }
}
