using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Demiurge;

/// <summary>
/// Managed metadata and safe operations over the existing per-Steward shared volume. The bytes
/// remain ordinary files so containers and CLI tools can use them; SQLite supplies provenance,
/// paging, upload sessions and an audit trail. Direct filesystem changes are reconciled as Unknown.
/// </summary>
public sealed class StewardFileStore
{
    private static readonly string[] ScaffoldDirectories = ["inputs", "shared", "work", "outputs"];
    private static readonly HashSet<string> UnixTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".bash", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".dockerfile", ".env", ".yaml", ".yml", ".toml", ".ini", ".conf",
    };
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly StewardFileStoreOptions options;
    private readonly Action<string> log;
    private readonly string volumesRoot;
    private readonly string metadataRoot;
    private readonly string databasePath;
    private readonly string stagingRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> operationGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UploadConcurrency> uploadConcurrency = new(StringComparer.Ordinal);

    private sealed class UploadConcurrency
    {
        public const int ParallelFiles = 3;
        public SemaphoreSlim Slots { get; } = new(ParallelFiles, ParallelFiles);
        public ConcurrentDictionary<string, SemaphoreSlim> FileGates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public StewardFileStore(StewardFileStoreOptions options, Action<string>? log = null,
        bool cleanupExpiredUploadsOnStart = true)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.log = log ?? (_ => { });
        if (options.MaxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxFileBytes));
        if (options.MaxChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxChunkBytes));
        if (options.MinimumFreeDiskBytes < 0) throw new ArgumentOutOfRangeException(nameof(options.MinimumFreeDiskBytes));
        if (options.UploadTimeToLive <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.UploadTimeToLive));

        volumesRoot = Path.GetFullPath(options.VolumesRoot);
        metadataRoot = Path.GetFullPath(options.MetadataRoot);
        databasePath = Path.GetFullPath(options.MetadataDatabasePath ?? Path.Combine(metadataRoot, "steward-files.db"));
        stagingRoot = Path.GetFullPath(options.StagingRoot ?? Path.Combine(metadataRoot, "staging"));
        Directory.CreateDirectory(volumesRoot);
        Directory.CreateDirectory(metadataRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        InitializeSchema();
        if (cleanupExpiredUploadsOnStart) CleanupExpiredUploads();
    }

    public StewardFileStoreOptions Options => options;

    public static StewardFileStoreOptions CreateDefaultOptions(
        long maxFileBytes = 10L * 1024 * 1024 * 1024,
        int maxChunkBytes = 8 * 1024 * 1024,
        long minimumFreeDiskBytes = 10L * 1024 * 1024 * 1024) => new()
    {
        VolumesRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeVolumesDirectory),
        MetadataRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeFileMetadataDirectory),
        MetadataDatabasePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeFileMetadataDatabase),
        StagingRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeFileStagingDirectory),
        MaxFileBytes = maxFileBytes,
        MaxChunkBytes = maxChunkBytes,
        MinimumFreeDiskBytes = minimumFreeDiskBytes,
    };

    public StewardFileUploadSession CreateUploadSession(StewardUploadPurpose purpose, string? stewardID, StewardFileActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (purpose == StewardUploadPurpose.ExistingSteward) ValidateStewardID(stewardID ?? "");
        if (purpose == StewardUploadPurpose.Initial && !string.IsNullOrWhiteSpace(stewardID))
            throw new StewardFileException("An initial upload session is not attached to a Steward until Steward creation.");

        DateTime now = UtcNow();
        var session = new StewardFileUploadSession
        {
            SessionID = Guid.NewGuid().ToString("N"),
            StewardID = string.IsNullOrWhiteSpace(stewardID) ? null : stewardID,
            Purpose = purpose,
            Status = StewardUploadStatus.Open,
            Actor = actor,
            CreatedUtc = now,
            ExpiresUtc = now + options.UploadTimeToLive,
        };
        string sessionDirectory = SessionDirectory(session.SessionID);
        Directory.CreateDirectory(sessionDirectory);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO upload_sessions
                (session_id,steward_id,purpose,status,actor_type,actor_id,actor_name,created_utc,expires_utc)
                VALUES($id,$steward,$purpose,$status,$actorType,$actorId,$actorName,$created,$expires)";
            command.Parameters.AddWithValue("$id", session.SessionID);
            command.Parameters.AddWithValue("$steward", (object?)session.StewardID ?? DBNull.Value);
            command.Parameters.AddWithValue("$purpose", (int)session.Purpose);
            command.Parameters.AddWithValue("$status", (int)session.Status);
            AddActor(command, "actor", actor);
            command.Parameters.AddWithValue("$created", Iso(session.CreatedUtc));
            command.Parameters.AddWithValue("$expires", Iso(session.ExpiresUtc));
            command.ExecuteNonQuery();
        }
        catch { TryDeleteDirectory(sessionDirectory); throw; }
        return session;
    }

    public StewardFileUploadSession? GetUploadSession(string sessionID)
    {
        using var connection = OpenConnection();
        return LoadUploadSession(connection, sessionID);
    }

    public IReadOnlyList<StewardFileUploadItem> ListUploadItems(string sessionID)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT upload_file_id,session_id,path,expected_size,received_size,mime_type,sha256
                                FROM upload_items WHERE session_id=$id ORDER BY path COLLATE NOCASE";
        command.Parameters.AddWithValue("$id", sessionID);
        using var reader = command.ExecuteReader();
        var results = new List<StewardFileUploadItem>();
        while (reader.Read()) results.Add(ReadUploadItem(reader));
        return results;
    }

    /// <summary>Appends one sequential chunk and consumes the supplied stream through EOF.</summary>
    public async Task<StewardFileUploadItem> AppendUploadChunkAsync(
        string sessionID,
        string path,
        long offset,
        long expectedSize,
        string? mimeType,
        Stream body,
        StewardFileActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        string normalized = NormalizeRelativePath(path);
        if (offset < 0) throw new StewardFileException("offset must be non-negative.");
        if (expectedSize < 0 || expectedSize > options.MaxFileBytes)
            throw new StewardFileException($"expectedSize must be between 0 and {options.MaxFileBytes} bytes.");

        _ = SessionDirectory(sessionID); // validate before using it as a concurrency-map key
        var concurrency = uploadConcurrency.GetOrAdd(sessionID, _ => new UploadConcurrency());
        var fileGate = concurrency.FileGates.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await fileGate.WaitAsync(ct);
        bool slotHeld = false;
        try
        {
            await concurrency.Slots.WaitAsync(ct);
            slotHeld = true;
            StewardFileUploadSession session;
            StewardFileUploadItem? existing;
            using (var connection = OpenConnection())
            {
                session = RequireOpenOwnedSession(connection, sessionID, actor);
                using var query = connection.CreateCommand();
                query.CommandText = @"SELECT upload_file_id,session_id,path,expected_size,received_size,mime_type,sha256
                                      FROM upload_items WHERE session_id=$session AND path=$path COLLATE NOCASE";
                query.Parameters.AddWithValue("$session", sessionID);
                query.Parameters.AddWithValue("$path", normalized);
                using var reader = query.ExecuteReader();
                existing = reader.Read() ? ReadUploadItem(reader) : null;
            }

            if (existing != null && existing.ExpectedSize != expectedSize)
                throw new StewardFileException("expectedSize changed between chunks for the same file.");
            long received = existing?.ReceivedSize ?? 0;
            if (offset != received)
                throw new StewardFileException($"Chunk offset {offset} does not match the next resumable offset {received}.");

            string uploadFileID = existing?.UploadFileID ?? Guid.NewGuid().ToString("N");
            string stagingPath = StagingFilePath(sessionID, uploadFileID);
            EnsureFreeDiskSpace(Math.Min(options.MaxChunkBytes, Math.Max(0, expectedSize - received)));
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);

            long chunkBytes = 0;
            await using (var output = new FileStream(stagingPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (output.Length != received)
                    throw new StewardFileException("The staged file length does not match its upload metadata; restart this file upload.");
                output.Position = received;
                byte[] buffer = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        int read = await body.ReadAsync(buffer.AsMemory(), ct);
                        if (read == 0) break;
                        chunkBytes += read;
                        if (chunkBytes > options.MaxChunkBytes || received + chunkBytes > expectedSize)
                            throw new StewardFileException($"Upload chunk exceeds its allowed size or the declared file size ({options.MaxChunkBytes} bytes per chunk).");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await output.FlushAsync(ct);
                }
                catch
                {
                    output.SetLength(received);
                    throw;
                }
            }

            long newReceived = received + chunkBytes;
            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"INSERT INTO upload_items
                        (upload_file_id,session_id,path,expected_size,received_size,mime_type,sha256)
                        VALUES($file,$session,$path,$expected,$received,$mime,NULL)
                        ON CONFLICT(session_id,path) DO UPDATE SET received_size=excluded.received_size,mime_type=excluded.mime_type";
                command.Parameters.AddWithValue("$file", uploadFileID);
                command.Parameters.AddWithValue("$session", sessionID);
                command.Parameters.AddWithValue("$path", normalized);
                command.Parameters.AddWithValue("$expected", expectedSize);
                command.Parameters.AddWithValue("$received", newReceived);
                command.Parameters.AddWithValue("$mime", NormalizeMimeType(mimeType));
                command.ExecuteNonQuery();
            }
            catch
            {
                try { using var rollback = new FileStream(stagingPath, FileMode.Open, FileAccess.Write, FileShare.None); rollback.SetLength(received); }
                catch { }
                throw;
            }
            return new StewardFileUploadItem
            {
                UploadFileID = uploadFileID,
                SessionID = sessionID,
                Path = normalized,
                ExpectedSize = expectedSize,
                ReceivedSize = newReceived,
                MimeType = NormalizeMimeType(mimeType),
            };
        }
        finally
        {
            if (slotHeld) concurrency.Slots.Release();
            fileGate.Release();
        }
    }

    public StewardFileCommitResult CommitUploadSession(
        string sessionID,
        string stewardID,
        StewardFileActor actor,
        StewardFileCommitOptions? commitOptions = null)
    {
        ValidateStewardID(stewardID);
        _ = SessionDirectory(sessionID);
        commitOptions ??= new StewardFileCommitOptions();
        var sessionGate = Gate("session:" + sessionID);
        var stewardGate = Gate("steward:" + stewardID);
        var concurrency = uploadConcurrency.GetOrAdd(sessionID, _ => new UploadConcurrency());
        sessionGate.Wait();
        int heldSlots = AcquireAllUploadSlots(concurrency);
        stewardGate.Wait();
        bool terminal = false;
        try
        {
            StewardFileUploadSession session;
            List<StewardFileUploadItem> uploads;
            using (var connection = OpenConnection())
            {
                session = RequireOpenOwnedSession(connection, sessionID, actor);
                if (session.Purpose == StewardUploadPurpose.ExistingSteward &&
                    !string.Equals(session.StewardID, stewardID, StringComparison.Ordinal))
                    throw new StewardFileException("This upload session belongs to a different Steward.");
                uploads = ListUploadItems(sessionID).ToList();
            }
            if (uploads.Count == 0) throw new StewardFileException("The upload session contains no files.");
            var incomplete = uploads.FirstOrDefault(x => x.ReceivedSize != x.ExpectedSize);
            if (incomplete != null)
                throw new StewardFileException($"'{incomplete.Path}' is incomplete ({incomplete.ReceivedSize}/{incomplete.ExpectedSize} bytes).");

            string root = EnsureStewardRoot(stewardID);
            EnsureNoReparsePoints(root, root);
            ReconcileCore(stewardID);
            var decisions = new List<(StewardFileUploadItem upload, string requested, string destination, StewardFileConflictPolicy policy, bool skipped)>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var upload in uploads)
            {
                string requested = session.Purpose == StewardUploadPurpose.Initial &&
                    !upload.Path.StartsWith("inputs/", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeRelativePath("inputs/" + upload.Path)
                    : upload.Path;
                string destination = requested;
                StewardFileConflictPolicy policy = PolicyFor(commitOptions, upload.Path, requested);
                string physical = ResolvePhysicalPath(stewardID, destination, allowMissingLeaf: true);
                bool exists = File.Exists(physical) || Directory.Exists(physical) || claimed.Contains(destination);
                if (exists)
                {
                    switch (policy)
                    {
                        case StewardFileConflictPolicy.Fail: throw new StewardFileConflictException(destination);
                        case StewardFileConflictPolicy.Skip:
                            decisions.Add((upload, requested, destination, policy, true));
                            continue;
                        case StewardFileConflictPolicy.KeepBoth:
                            destination = FindKeepBothPath(stewardID, destination, claimed);
                            break;
                        case StewardFileConflictPolicy.Replace:
                            if (Directory.Exists(physical)) throw new StewardFileException($"Cannot replace directory '{destination}' with a file.");
                            break;
                    }
                }
                if (!claimed.Add(destination)) throw new StewardFileConflictException(destination);
                decisions.Add((upload, requested, destination, policy, false));
            }

            var applied = new List<(string destination, string staged, string? backup)>();
            var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var committed = new List<StewardFileCommitItem>();
            string batchID = Guid.NewGuid().ToString("N");
            long totalBytes = 0;
            try
            {
                foreach (var decision in decisions.Where(x => !x.skipped))
                {
                    string staged = StagingFilePath(sessionID, decision.upload.UploadFileID);
                    if (!File.Exists(staged)) throw new StewardFileException($"Staged bytes are missing for '{decision.upload.Path}'.");
                    string destination = ResolvePhysicalPath(stewardID, decision.destination, allowMissingLeaf: true);
                    string parentDirectory = Path.GetDirectoryName(destination)!;
                    for (string? missing = parentDirectory;
                         missing != null && !Directory.Exists(missing) && !string.Equals(missing, root, StringComparison.OrdinalIgnoreCase);
                         missing = Path.GetDirectoryName(missing))
                        createdDirectories.Add(missing);
                    Directory.CreateDirectory(parentDirectory);
                    EnsureNoReparsePoints(root, parentDirectory);
                    string temp = destination + "." + Guid.NewGuid().ToString("N") + ".uploading";
                    File.Move(staged, temp);
                    string? backup = null;
                    try
                    {
                        if (File.Exists(destination))
                        {
                            backup = destination + "." + Guid.NewGuid().ToString("N") + ".replaced";
                            File.Move(destination, backup);
                        }
                        File.Move(temp, destination);
                        applied.Add((destination, staged, backup));
                    }
                    catch
                    {
                        try { if (File.Exists(temp)) File.Move(temp, staged, overwrite: true); } catch { }
                        try { if (backup != null && File.Exists(backup)) File.Move(backup, destination, overwrite: true); } catch { }
                        throw;
                    }
                }

                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                foreach (var decision in decisions)
                {
                    if (decision.skipped)
                    {
                        committed.Add(new StewardFileCommitItem
                        {
                            RequestedPath = decision.requested,
                            CommittedPath = null,
                            AppliedPolicy = decision.policy,
                            Skipped = true,
                        });
                        continue;
                    }
                    string physical = ResolvePhysicalPath(stewardID, decision.destination);
                    string hash = ComputeSha256(physical);
                    StewardFileEntry? prior = LoadEntry(connection, stewardID, decision.destination, transaction);
                    StewardFileOrigin origin = session.Purpose == StewardUploadPurpose.Initial
                        ? StewardFileOrigin.InitialUpload : StewardFileOrigin.UserUpload;
                    EnsureParentEntries(connection, transaction, stewardID, decision.destination, actor, origin, batchID);
                    var entry = EntryForFile(stewardID, decision.destination, physical, decision.upload.MimeType, hash, actor, origin, prior, UtcNow());
                    UpsertEntry(connection, transaction, entry);
                    TouchParentEntries(connection, transaction, stewardID, decision.destination, actor);
                    InsertAudit(connection, transaction, stewardID, StewardFileOperation.Upload, decision.destination,
                        actor, batchID, entry.Size, prior == null ? null : "replaced existing file");
                    committed.Add(new StewardFileCommitItem
                    {
                        RequestedPath = decision.requested,
                        CommittedPath = decision.destination,
                        AppliedPolicy = decision.policy,
                        Entry = entry,
                    });
                    totalBytes += entry.Size;
                }
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE upload_sessions SET status=$status WHERE session_id=$id";
                    update.Parameters.AddWithValue("$status", (int)StewardUploadStatus.Committed);
                    update.Parameters.AddWithValue("$id", sessionID);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                foreach (var item in applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(item.destination)) File.Move(item.destination, item.staged, overwrite: true);
                        if (item.backup != null && File.Exists(item.backup)) File.Move(item.backup, item.destination, overwrite: true);
                    }
                    catch { }
                }
                foreach (string directory in createdDirectories.OrderByDescending(x => x.Length))
                {
                    try
                    {
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory);
                    }
                    catch { }
                }
                throw;
            }

            foreach (var item in applied)
                if (item.backup != null) TryDeleteFile(item.backup);
            RetouchParents(stewardID, committed.Where(x => !x.Skipped && x.CommittedPath != null)
                .Select(x => x.CommittedPath!), actor);
            TryDeleteDirectory(SessionDirectory(sessionID));
            GenerateManifest(stewardID);
            terminal = true;
            return new StewardFileCommitResult
            {
                SessionID = sessionID,
                StewardID = stewardID,
                BatchID = batchID,
                Items = committed,
                TotalBytes = totalBytes,
            };
        }
        finally
        {
            stewardGate.Release();
            ReleaseUploadSlots(concurrency, heldSlots);
            sessionGate.Release();
            if (terminal)
            {
                uploadConcurrency.TryRemove(sessionID, out _);
                operationGates.TryRemove("session:" + sessionID, out _);
            }
        }
    }

    public bool CancelUploadSession(string sessionID, StewardFileActor actor)
    {
        _ = SessionDirectory(sessionID);
        var gate = Gate("session:" + sessionID);
        var concurrency = uploadConcurrency.GetOrAdd(sessionID, _ => new UploadConcurrency());
        gate.Wait();
        int heldSlots = AcquireAllUploadSlots(concurrency);
        bool terminal = false;
        try
        {
            using var connection = OpenConnection();
            var session = LoadUploadSession(connection, sessionID);
            if (session == null) { terminal = true; return false; }
            RequireSameActor(session.Actor, actor);
            if (session.Status != StewardUploadStatus.Open) { terminal = true; return false; }
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE upload_sessions SET status=$status WHERE session_id=$id";
                command.Parameters.AddWithValue("$status", (int)StewardUploadStatus.Cancelled);
                command.Parameters.AddWithValue("$id", sessionID);
                command.ExecuteNonQuery();
            }
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM upload_items WHERE session_id=$id";
                delete.Parameters.AddWithValue("$id", sessionID);
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
            TryDeleteDirectory(SessionDirectory(sessionID));
            terminal = true;
            return true;
        }
        finally
        {
            ReleaseUploadSlots(concurrency, heldSlots);
            gate.Release();
            if (terminal)
            {
                uploadConcurrency.TryRemove(sessionID, out _);
                operationGates.TryRemove("session:" + sessionID, out _);
            }
        }
    }

    public int CleanupExpiredUploads()
    {
        DateTime now = UtcNow();
        var candidates = new List<string>();
        using (var connection = OpenConnection())
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT session_id FROM upload_sessions WHERE status=$open AND expires_utc <= $now";
            query.Parameters.AddWithValue("$open", (int)StewardUploadStatus.Open);
            query.Parameters.AddWithValue("$now", Iso(now));
            using var reader = query.ExecuteReader();
            while (reader.Read()) candidates.Add(reader.GetString(0));
        }
        int expired = 0;
        foreach (string id in candidates)
        {
            var gate = Gate("session:" + id);
            var concurrency = uploadConcurrency.GetOrAdd(id, _ => new UploadConcurrency());
            gate.Wait();
            int heldSlots = AcquireAllUploadSlots(concurrency);
            bool terminal = false;
            try
            {
                using var connection = OpenConnection();
                var session = LoadUploadSession(connection, id);
                if (session?.Status != StewardUploadStatus.Open) { terminal = true; continue; }
                if (session.ExpiresUtc > UtcNow()) continue;
                using var transaction = connection.BeginTransaction();
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE upload_sessions SET status=$status WHERE session_id=$id AND status=$open";
                    update.Parameters.AddWithValue("$status", (int)StewardUploadStatus.Expired);
                    update.Parameters.AddWithValue("$open", (int)StewardUploadStatus.Open);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                }
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM upload_items WHERE session_id=$id";
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }
                transaction.Commit();
                TryDeleteDirectory(SessionDirectory(id));
                terminal = true;
                expired++;
            }
            finally
            {
                ReleaseUploadSlots(concurrency, heldSlots);
                gate.Release();
                if (terminal)
                {
                    uploadConcurrency.TryRemove(id, out _);
                    operationGates.TryRemove("session:" + id, out _);
                }
            }
        }
        return expired;
    }

    public void EnsureStewardScaffold(string stewardID)
    {
        ValidateStewardID(stewardID);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            EnsureStewardRoot(stewardID);
            foreach (string path in ScaffoldDirectories)
                CreateDirectoryCore(stewardID, path, StewardFileActor.System, writeManifest: false);
            GenerateManifest(stewardID);
        }
        finally { gate.Release(); }
    }

    public StewardFileListResult List(string stewardID, StewardFileListRequest? request = null)
    {
        ValidateStewardID(stewardID);
        request ??= new StewardFileListRequest();
        if (request.Reconcile) Reconcile(stewardID);
        string directory = NormalizeStewardPath(stewardID, request.Directory, allowRoot: true, allowManagedMetadata: true);
        int limit = Math.Clamp(request.Limit, 1, 500);
        int offset = Math.Max(0, request.Offset);
        var all = LoadEntries(stewardID);
        // Managed implementation metadata remains available by explicit path, but it is not
        // Steward work and should not consume/default-pollute root list_files results.
        if (directory.Equals(".klive", StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(".klive/", StringComparison.OrdinalIgnoreCase))
            all.AddRange(LoadManagedMetadataEntries(stewardID));
        string prefix = directory.Length == 0 ? "" : directory + "/";
        IEnumerable<StewardFileEntry> filtered = all.Where(entry =>
        {
            if (directory.Length > 0 && !entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string remainder = directory.Length == 0 ? entry.Path : entry.Path[prefix.Length..];
            if (!request.Recursive && remainder.Contains('/')) return false;
            if (!string.IsNullOrWhiteSpace(request.Search) &&
                !entry.Path.Contains(request.Search.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(request.Glob) || GlobMatches(request.Glob!, remainder);
        });
        var ordered = filtered.OrderByDescending(e => e.Kind == StewardFileKind.Directory)
            .ThenByDescending(e => e.Important)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StewardFileListResult
        {
            Entries = ordered.Skip(offset).Take(limit).ToList(),
            Total = ordered.Count,
            Offset = offset,
            Limit = limit,
        };
    }

    public StewardFileEntry? Stat(string stewardID, string path, bool reconcile = true)
    {
        string normalized = NormalizeStewardPath(stewardID, path, allowManagedMetadata: true);
        if (reconcile) Reconcile(stewardID);
        if (normalized.Equals(".klive", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".klive/", StringComparison.OrdinalIgnoreCase))
            return LoadManagedMetadataEntries(stewardID).FirstOrDefault(x =>
                string.Equals(x.Path, normalized, StringComparison.OrdinalIgnoreCase));
        using var connection = OpenConnection();
        return LoadEntry(connection, stewardID, normalized);
    }

    public FileStream OpenRead(string stewardID, string path)
    {
        string physical = ResolvePhysicalPath(stewardID, path, allowManagedMetadata: true);
        if (!File.Exists(physical)) throw new FileNotFoundException("Steward file not found.", path);
        return new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public string GetPhysicalFilePath(string stewardID, string path)
    {
        string physical = ResolvePhysicalPath(stewardID, path, allowManagedMetadata: true);
        if (!File.Exists(physical)) throw new FileNotFoundException("Steward file not found.", path);
        return physical;
    }

    public async Task<string> ReadTextAsync(string stewardID, string path, int maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        await using var stream = OpenRead(stewardID, path);
        int count = (int)Math.Min(stream.Length, maxBytes);
        byte[] bytes = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(read, count - read), ct);
            if (n == 0) break;
            read += n;
        }
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes, 0, read);
            if (text.IndexOf('\0') >= 0) throw new DecoderFallbackException();
            return stream.Length > maxBytes ? text + $"\n[truncated after {maxBytes} bytes]" : text;
        }
        catch (DecoderFallbackException)
        {
            throw new StewardFileException("This is not a UTF-8 text file. Use the shared desktop/CLI or download endpoint for binary files.");
        }
    }

    public async Task<StewardFileEntry> WriteTextAsync(string stewardID, string path, string content, StewardFileActor actor, CancellationToken ct = default)
    {
        string normalized = NormalizeStewardPath(stewardID, path);
        string normalizedContent = NormalizeUnixText(normalized, content ?? "");
        byte[] bytes = Encoding.UTF8.GetBytes(normalizedContent);
        if (bytes.LongLength > options.MaxFileBytes) throw new StewardFileException("File exceeds the configured maximum size.");
        EnsureFreeDiskSpace(bytes.LongLength);
        var gate = Gate("steward:" + stewardID);
        await gate.WaitAsync(ct);
        try
        {
            ReconcileCore(stewardID);
            string physical = ResolvePhysicalPath(stewardID, normalized, allowMissingLeaf: true);
            Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
            EnsureNoReparsePoints(EnsureStewardRoot(stewardID), Path.GetDirectoryName(physical)!);
            string temp = physical + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string? backup = null;
            await File.WriteAllBytesAsync(temp, bytes, ct);
            try
            {
                if (File.Exists(physical))
                {
                    backup = physical + "." + Guid.NewGuid().ToString("N") + ".replaced";
                    File.Move(physical, backup);
                }
                File.Move(temp, physical);
                StewardFileEntry entry;
                try
                {
                    using var connection = OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    EnsureParentEntries(connection, transaction, stewardID, normalized, actor, OriginFor(actor), null);
                    var prior = LoadEntry(connection, stewardID, normalized, transaction);
                    entry = EntryForFile(stewardID, normalized, physical, "text/plain; charset=utf-8",
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actor, OriginFor(actor), prior, UtcNow());
                    UpsertEntry(connection, transaction, entry);
                    TouchParentEntries(connection, transaction, stewardID, normalized, actor);
                    InsertAudit(connection, transaction, stewardID, StewardFileOperation.Write, normalized, actor, null, entry.Size, null);
                    transaction.Commit();
                }
                catch
                {
                    TryDeleteFile(physical);
                    if (backup != null && File.Exists(backup)) File.Move(backup, physical);
                    throw;
                }
                TryDeleteFile(backup ?? "");
                RetouchParents(stewardID, [normalized], actor);
                GenerateManifest(stewardID);
                return entry;
            }
            finally { TryDeleteFile(temp); }
        }
        finally { gate.Release(); }
    }

    public StewardFileEntry CreateDirectory(string stewardID, string path, StewardFileActor actor)
    {
        string normalized = NormalizeStewardPath(stewardID, path);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            ReconcileCore(stewardID);
            StewardFileEntry? result = null;
            string current = "";
            foreach (string segment in normalized.Split('/'))
            {
                current = current.Length == 0 ? segment : current + "/" + segment;
                using var connection = OpenConnection();
                var existing = LoadEntry(connection, stewardID, current);
                if (existing != null)
                {
                    if (existing.Kind != StewardFileKind.Directory) throw new StewardFileConflictException(current);
                    result = existing;
                    continue;
                }
                result = CreateDirectoryCore(stewardID, current, actor, writeManifest: false);
            }
            GenerateManifest(stewardID);
            return result!;
        }
        finally { gate.Release(); }
    }

    public StewardFileEntry Move(string stewardID, string sourcePath, string destinationPath, StewardFileActor actor)
    {
        string source = NormalizeStewardPath(stewardID, sourcePath);
        string destination = NormalizeStewardPath(stewardID, destinationPath);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            ReconcileCore(stewardID);
            string sourcePhysical = ResolvePhysicalPath(stewardID, source);
            string destinationPhysical = ResolvePhysicalPath(stewardID, destination, allowMissingLeaf: true);
            if (!File.Exists(sourcePhysical) && !Directory.Exists(sourcePhysical)) throw new FileNotFoundException("Source does not exist.", source);
            if (Directory.Exists(sourcePhysical) && destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
                throw new StewardFileException("A directory cannot be moved inside itself.");
            if (File.Exists(destinationPhysical) || Directory.Exists(destinationPhysical)) throw new StewardFileConflictException(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPhysical)!);
            bool sourceIsFile = File.Exists(sourcePhysical);
            if (sourceIsFile) File.Move(sourcePhysical, destinationPhysical);
            else Directory.Move(sourcePhysical, destinationPhysical);
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                EnsureParentEntries(connection, transaction, stewardID, destination, actor, OriginFor(actor), null);
                var affected = LoadEntriesByPrefix(connection, stewardID, source, transaction);
                foreach (var old in affected.OrderBy(e => e.Path.Length))
                {
                    string suffix = old.Path.Length == source.Length ? "" : old.Path[source.Length..];
                    string newPath = destination + suffix;
                    DeleteEntry(connection, transaction, stewardID, old.Path);
                    string movedPhysical = ResolvePhysicalPath(stewardID, newPath);
                    var updated = CloneEntry(old, path: newPath, modifiedBy: actor, modifiedUtc: UtcNow(),
                        fileSystemModifiedUtc: FileSystemTime(movedPhysical, old.Kind));
                    UpsertEntry(connection, transaction, updated);
                }
                TouchParentEntries(connection, transaction, stewardID, source, actor);
                TouchParentEntries(connection, transaction, stewardID, destination, actor);
                InsertAudit(connection, transaction, stewardID, StewardFileOperation.Move, destination, actor, null, null, source);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    if (sourceIsFile && File.Exists(destinationPhysical)) File.Move(destinationPhysical, sourcePhysical);
                    else if (!sourceIsFile && Directory.Exists(destinationPhysical)) Directory.Move(destinationPhysical, sourcePhysical);
                }
                catch { }
                throw;
            }
            GenerateManifest(stewardID);
            return Stat(stewardID, destination, reconcile: false)!;
        }
        finally { gate.Release(); }
    }

    public StewardFileEntry Copy(string stewardID, string sourcePath, string destinationPath, StewardFileActor actor)
    {
        string source = NormalizeStewardPath(stewardID, sourcePath);
        string destination = NormalizeStewardPath(stewardID, destinationPath);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            ReconcileCore(stewardID);
            string sourcePhysical = ResolvePhysicalPath(stewardID, source);
            string destinationPhysical = ResolvePhysicalPath(stewardID, destination, allowMissingLeaf: true);
            if (Directory.Exists(sourcePhysical) && destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
                throw new StewardFileException("A directory cannot be copied inside itself.");
            if (File.Exists(destinationPhysical) || Directory.Exists(destinationPhysical)) throw new StewardFileConflictException(destination);
            if (File.Exists(sourcePhysical))
            {
                EnsureFreeDiskSpace(new FileInfo(sourcePhysical).Length);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPhysical)!);
                File.Copy(sourcePhysical, destinationPhysical);
            }
            else if (Directory.Exists(sourcePhysical))
            {
                long total = CalculateDirectorySize(sourcePhysical);
                EnsureFreeDiskSpace(total);
                try { CopyDirectory(sourcePhysical, destinationPhysical); }
                catch { TryDeleteDirectory(destinationPhysical); throw; }
            }
            else throw new FileNotFoundException("Source does not exist.", source);
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                EnsureParentEntries(connection, transaction, stewardID, destination, actor, OriginFor(actor), null);
                var sourceEntries = LoadEntriesByPrefix(connection, stewardID, source, transaction);
                if (sourceEntries.Count == 0) throw new StewardFileException("Source metadata could not be reconciled.");
                DateTime now = UtcNow();
                long copiedBytes = 0;
                foreach (var sourceEntry in sourceEntries)
                {
                    string suffix = sourceEntry.Path.Length == source.Length ? "" : sourceEntry.Path[source.Length..];
                    string copiedPath = destination + suffix;
                    string copiedPhysical = ResolvePhysicalPath(stewardID, copiedPath);
                    long size = sourceEntry.Kind == StewardFileKind.File ? new FileInfo(copiedPhysical).Length : 0;
                    string? sha = sourceEntry.Kind == StewardFileKind.File && sourceEntry.Sha256 != null
                        ? ComputeSha256(copiedPhysical) : null;
                    var attributed = new StewardFileEntry
                    {
                        FileID = Guid.NewGuid().ToString("N"), StewardID = stewardID, Path = copiedPath,
                        Kind = sourceEntry.Kind, Size = size, MimeType = sourceEntry.MimeType, Sha256 = sha,
                        FileSystemModifiedUtc = FileSystemTime(copiedPhysical, sourceEntry.Kind),
                        CreatedUtc = now, ModifiedUtc = now, CreatedBy = actor, ModifiedBy = actor,
                        Origin = OriginFor(actor), Description = sourceEntry.Description, Important = sourceEntry.Important,
                    };
                    UpsertEntry(connection, transaction, attributed);
                    if (attributed.Kind == StewardFileKind.File) copiedBytes += attributed.Size;
                }
                TouchParentEntries(connection, transaction, stewardID, destination, actor);
                InsertAudit(connection, transaction, stewardID, StewardFileOperation.Copy, destination, actor, null,
                    copiedBytes, source);
                transaction.Commit();
            }
            catch
            {
                if (File.Exists(destinationPhysical)) TryDeleteFile(destinationPhysical);
                else TryDeleteDirectory(destinationPhysical);
                throw;
            }
            GenerateManifest(stewardID);
            return Stat(stewardID, destination, reconcile: false)!;
        }
        finally { gate.Release(); }
    }

    public bool Delete(string stewardID, string path, bool recursive, StewardFileActor actor)
    {
        string normalized = NormalizeStewardPath(stewardID, path);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            ReconcileCore(stewardID);
            string physical = ResolvePhysicalPath(stewardID, normalized);
            bool existed = File.Exists(physical) || Directory.Exists(physical);
            if (!existed) return false;
            bool isFile = File.Exists(physical);
            if (!isFile && !recursive && Directory.EnumerateFileSystemEntries(physical).Any())
                throw new StewardFileException("Directory is not empty; set recursive=true to delete it.");
            string tombstone = physical + "." + Guid.NewGuid().ToString("N") + ".deleting";
            if (isFile) File.Move(physical, tombstone);
            else Directory.Move(physical, tombstone);
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                foreach (var entry in LoadEntriesByPrefix(connection, stewardID, normalized, transaction))
                    DeleteEntry(connection, transaction, stewardID, entry.Path);
                TouchParentEntries(connection, transaction, stewardID, normalized, actor);
                InsertAudit(connection, transaction, stewardID, StewardFileOperation.Delete, normalized, actor, null, null, null);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    if (isFile && File.Exists(tombstone)) File.Move(tombstone, physical);
                    else if (!isFile && Directory.Exists(tombstone)) Directory.Move(tombstone, physical);
                }
                catch { }
                throw;
            }
            if (isFile) TryDeleteFile(tombstone); else TryDeleteDirectory(tombstone);
            RetouchParents(stewardID, [normalized], actor);
            GenerateManifest(stewardID);
            return true;
        }
        finally { gate.Release(); }
    }

    public StewardFileEntry SetMetadata(string stewardID, string path, bool? important, string? description, StewardFileActor actor)
    {
        string normalized = NormalizeStewardPath(stewardID, path);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            ReconcileCore(stewardID);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = LoadEntry(connection, stewardID, normalized, transaction)
                ?? throw new FileNotFoundException("Steward file or directory not found.", normalized);
            var updated = new StewardFileEntry
            {
                FileID = existing.FileID, StewardID = existing.StewardID, Path = existing.Path, Kind = existing.Kind,
                Size = existing.Size, MimeType = existing.MimeType, Sha256 = existing.Sha256,
                FileSystemModifiedUtc = existing.FileSystemModifiedUtc, CreatedUtc = existing.CreatedUtc, ModifiedUtc = UtcNow(),
                CreatedBy = existing.CreatedBy, ModifiedBy = actor, Origin = existing.Origin,
                Description = description == null ? existing.Description : NormalizeDescription(description),
                Important = important ?? existing.Important,
            };
            UpsertEntry(connection, transaction, updated);
            InsertAudit(connection, transaction, stewardID, StewardFileOperation.Metadata, normalized, actor, null, existing.Size, null);
            transaction.Commit();
            GenerateManifest(stewardID);
            return updated;
        }
        finally { gate.Release(); }
    }

    public StewardFileReconcileResult Reconcile(string stewardID)
    {
        ValidateStewardID(stewardID);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            var result = ReconcileCore(stewardID);
            GenerateManifest(stewardID);
            return result;
        }
        finally { gate.Release(); }
    }

    public string DescribeForPrompt(string stewardID, int maxItems = 18)
    {
        try { Reconcile(stewardID); } catch (Exception ex) { log($"Steward files reconcile failed for {stewardID}: {ex.Message}"); }
        var entries = LoadEntries(stewardID);
        long bytes = entries.Where(e => e.Kind == StewardFileKind.File).Sum(e => e.Size);
        var files = entries.Where(e => e.Kind == StewardFileKind.File).ToList();
        var selected = entries.Where(e => e.Important)
            .OrderByDescending(e => e.Kind == StewardFileKind.Directory)
            .ThenByDescending(e => e.ModifiedUtc)
            .Concat(files.Where(e => e.Origin == StewardFileOrigin.InitialUpload).OrderByDescending(e => e.ModifiedUtc))
            .Concat(entries.OrderByDescending(e => e.ModifiedUtc))
            .DistinctBy(e => e.FileID)
            .Take(Math.Clamp(maxItems, 1, 50))
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{files.Count} files · {entries.Count(e => e.Kind == StewardFileKind.Directory)} directories · {StewardFileTimeline.FormatBytes(bytes)} total.");
        if (selected.Count == 0) { sb.Append("(empty — use shared paths for durable team work)"); return sb.ToString(); }
        foreach (var entry in selected)
        {
            string marker = entry.Important ? "★ " : "";
            string type = entry.Kind == StewardFileKind.Directory ? "[dir]" : StewardFileTimeline.FormatBytes(entry.Size);
            string by = entry.CreatedBy.Type == StewardFileActorType.Unknown ? "actor unknown" : $"added by {entry.CreatedBy.DisplayName}";
            string changed = entry.ModifiedUtc - entry.CreatedUtc > TimeSpan.FromSeconds(1) || entry.ModifiedBy != entry.CreatedBy
                ? $"; changed by {(entry.ModifiedBy.Type == StewardFileActorType.Unknown ? "Unknown" : entry.ModifiedBy.DisplayName)} {entry.ModifiedUtc:yyyy-MM-dd HH:mm}Z"
                : "";
            sb.AppendLine($"- {marker}{entry.Path} · {type} · {by} {entry.CreatedUtc:yyyy-MM-dd HH:mm}Z" +
                changed +
                (string.IsNullOrWhiteSpace(entry.Description) ? "" : $" · {entry.Description}"));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Only for rolling back a failed new-Steward transaction.</summary>
    public void RollbackStewardInitialization(string stewardID)
    {
        ValidateStewardID(stewardID);
        var gate = Gate("steward:" + stewardID);
        gate.Wait();
        try
        {
            string root = StewardRoot(stewardID);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (string table in new[] { "file_entries", "file_events" })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE steward_id=$steward";
                command.Parameters.AddWithValue("$steward", stewardID);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<StewardFileEvent> ListAudit(string stewardID, int limit = 200, long? beforeEventID = null)
    {
        ValidateStewardID(stewardID);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT event_id,steward_id,operation,path,previous_path,timestamp_utc,
                                       actor_type,actor_id,actor_name,batch_id,size,detail
                                FROM file_events WHERE steward_id=$steward
                                  AND ($before IS NULL OR event_id < $before)
                                ORDER BY event_id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$steward", stewardID);
        command.Parameters.AddWithValue("$before", (object?)beforeEventID ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var results = new List<StewardFileEvent>();
        while (reader.Read())
        {
            results.Add(new StewardFileEvent
            {
                EventID = reader.GetInt64(0), StewardID = reader.GetString(1), Operation = (StewardFileOperation)reader.GetInt32(2),
                Path = reader.GetString(3), PreviousPath = NullableString(reader, 4), TimestampUtc = ParseTime(reader.GetString(5)),
                Actor = ReadActor(reader, 6), BatchID = NullableString(reader, 9), Size = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Detail = NullableString(reader, 11),
            });
        }
        return results;
    }

    public string NormalizeRelativePath(string? path, bool allowRoot = false, bool allowManagedMetadata = false)
    {
        string value = (path ?? "").Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC);

        // `/project` is the single path agents see inside every desktop. Accept that exact virtual
        // mount everywhere the host-side file API accepts a relative path. Also translate the
        // common drive-qualified compatibility spelling (`D:/project/...`) into the same safe
        // namespace; the suffix is still resolved under this Steward's real volume root.
        var driveSteward = Regex.Match(value, "^[A-Za-z]:/project(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (driveSteward.Success) value = driveSteward.Groups[1].Success ? driveSteward.Groups[1].Value : "";
        else if (value.Equals("/project", StringComparison.OrdinalIgnoreCase)) value = "";
        else if (value.StartsWith("/project/", StringComparison.OrdinalIgnoreCase)) value = value[9..];
        // A model that has been told its files live at /project routinely drops the leading slash
        // and writes "steward/work/x.md". Left alone that silently forks the workspace into a second
        // tree, so a teammate reading "work/x.md" gets "not found" and redoes delivered work. The
        // Steward root is never itself named "Steward", so this spelling is unambiguous.
        else if (value.Equals("steward", StringComparison.OrdinalIgnoreCase)) value = "";
        else if (value.StartsWith("steward/", StringComparison.OrdinalIgnoreCase)) value = value[8..];

        if (value.Length == 0 || value == ".")
        {
            if (allowRoot) return "";
            throw new StewardFileException("A non-empty Steward-relative path is required.");
        }
        if (value.StartsWith('/') || value.StartsWith("//") || Path.IsPathRooted(value) || Regex.IsMatch(value, "^[A-Za-z]:"))
            throw new StewardFileException("Absolute, drive-qualified and UNC paths are not allowed.");
        string[] segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(s => s.Length == 0 || s is "." or ".."))
            throw new StewardFileException("Path traversal and empty path segments are not allowed.");
        foreach (string segment in segments)
        {
            if (segment.Length > 200) throw new StewardFileException("Path segments cannot exceed 200 characters.");
            if (segment.EndsWith(' ') || segment.EndsWith('.')) throw new StewardFileException("Path segments cannot end with a space or dot.");
            if (segment.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\0'))
                throw new StewardFileException("Path contains characters that are unsafe on the Steward host.");
            string stem = segment.Split('.')[0];
            if (WindowsReservedNames.Contains(stem)) throw new StewardFileException($"'{segment}' is a reserved host filename.");
        }
        if (!allowManagedMetadata && segments[0].Equals(".klive", StringComparison.OrdinalIgnoreCase))
            throw new StewardFileException(".klive is managed Steward metadata: it is readable but cannot be changed directly.");
        string normalized = string.Join('/', segments);
        if (normalized.Length > 1024) throw new StewardFileException("Path is too long.");
        return normalized;
    }

    /// <summary>
    /// Normalizes every path spelling exposed by the Steward harness: Steward-relative,
    /// container-mounted (/project/...), compatibility drive-mounted (D:/project/...), or the
    /// exact host path belonging to this Steward's own volume. An absolute path outside that one
    /// volume remains invalid, preserving Steward isolation.
    /// </summary>
    public string NormalizeStewardPath(string stewardID, string? path, bool allowRoot = false,
        bool allowManagedMetadata = false)
    {
        ValidateStewardID(stewardID);
        string supplied = (path ?? "").Trim();
        if (Path.IsPathRooted(supplied))
        {
            string root = EnsureStewardRoot(stewardID);
            string full = Path.GetFullPath(supplied);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
                supplied = "";
            else if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                supplied = Path.GetRelativePath(root, full);
        }
        return NormalizeRelativePath(supplied, allowRoot, allowManagedMetadata);
    }

    private StewardFileEntry CreateDirectoryCore(string stewardID, string path, StewardFileActor actor, bool writeManifest)
    {
        string normalized = NormalizeRelativePath(path);
        string physical = ResolvePhysicalPath(stewardID, normalized, allowMissingLeaf: true);
        if (File.Exists(physical)) throw new StewardFileConflictException(normalized);
        Directory.CreateDirectory(physical);
        EnsureNoReparsePoints(EnsureStewardRoot(stewardID), physical);
        StewardFileEntry entry;
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            var prior = LoadEntry(connection, stewardID, normalized, transaction);
            DateTime now = UtcNow();
            entry = new StewardFileEntry
            {
                FileID = prior?.FileID ?? Guid.NewGuid().ToString("N"), StewardID = stewardID, Path = normalized,
                Kind = StewardFileKind.Directory, Size = 0, MimeType = "inode/directory", FileSystemModifiedUtc = Directory.GetLastWriteTimeUtc(physical),
                CreatedUtc = prior?.CreatedUtc ?? now, ModifiedUtc = now, CreatedBy = prior?.CreatedBy ?? actor, ModifiedBy = actor,
                Origin = prior?.Origin ?? OriginFor(actor), Description = prior?.Description, Important = prior?.Important ?? false,
            };
            UpsertEntry(connection, transaction, entry);
            TouchParentEntries(connection, transaction, stewardID, normalized, actor);
            if (prior == null) InsertAudit(connection, transaction, stewardID, StewardFileOperation.CreateDirectory, normalized, actor, null, 0, null);
            transaction.Commit();
        }
        if (writeManifest) GenerateManifest(stewardID);
        return entry;
    }

    private void EnsureParentEntries(SqliteConnection connection, SqliteTransaction transaction, string stewardID,
        string childPath, StewardFileActor actor, StewardFileOrigin origin, string? batchID)
    {
        int slash = childPath.LastIndexOf('/');
        if (slash < 0) return;
        string current = "";
        foreach (string segment in childPath[..slash].Split('/'))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            if (LoadEntry(connection, stewardID, current, transaction) != null) continue;
            string physical = ResolvePhysicalPath(stewardID, current);
            if (!Directory.Exists(physical)) throw new StewardFileException($"Parent directory '{current}' is missing.");
            DateTime now = UtcNow();
            var entry = new StewardFileEntry
            {
                FileID = Guid.NewGuid().ToString("N"), StewardID = stewardID, Path = current,
                Kind = StewardFileKind.Directory, Size = 0, MimeType = "inode/directory",
                FileSystemModifiedUtc = Directory.GetLastWriteTimeUtc(physical), CreatedUtc = now, ModifiedUtc = now,
                CreatedBy = actor, ModifiedBy = actor, Origin = origin,
            };
            UpsertEntry(connection, transaction, entry);
            InsertAudit(connection, transaction, stewardID, StewardFileOperation.CreateDirectory,
                current, actor, batchID, 0, "created implicitly");
        }
    }

    private void TouchParentEntries(SqliteConnection connection, SqliteTransaction transaction,
        string stewardID, string childPath, StewardFileActor actor)
    {
        int slash = childPath.LastIndexOf('/');
        if (slash < 0) return;
        string current = "";
        foreach (string segment in childPath[..slash].Split('/'))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            var existing = LoadEntry(connection, stewardID, current, transaction);
            if (existing?.Kind != StewardFileKind.Directory) continue;
            string physical = ResolvePhysicalPath(stewardID, current);
            if (!Directory.Exists(physical)) continue;
            UpsertEntry(connection, transaction, CloneEntry(existing,
                fileSystemModifiedUtc: Directory.GetLastWriteTimeUtc(physical),
                modifiedUtc: UtcNow(), modifiedBy: actor));
        }
    }

    private void RetouchParents(string stewardID, IEnumerable<string> childPaths, StewardFileActor actor)
    {
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (string path in childPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                TouchParentEntries(connection, transaction, stewardID, path, actor);
            transaction.Commit();
        }
        catch (Exception ex) { log($"Steward file parent metadata refresh failed for {stewardID}: {ex.Message}"); }
    }

    private StewardFileReconcileResult ReconcileCore(string stewardID)
    {
        string root = EnsureStewardRoot(stewardID);
        EnsureNoReparsePoints(root, root);
        var current = new Dictionary<string, (StewardFileKind kind, long size, DateTime modified)>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string physical in Directory.EnumerateFileSystemEntries(root, "*", enumeration))
        {
            string relative = Path.GetRelativePath(root, physical).Replace('\\', '/');
            if (relative.Equals(".klive", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".klive/", StringComparison.OrdinalIgnoreCase)) continue;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(physical); } catch { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            current[relative] = directory
                ? (StewardFileKind.Directory, 0, Directory.GetLastWriteTimeUtc(physical))
                : (StewardFileKind.File, new FileInfo(physical).Length, File.GetLastWriteTimeUtc(physical));
        }

        var existing = LoadEntries(stewardID).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        int created = 0, modified = 0, deleted = 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var pair in current)
        {
            if (!existing.TryGetValue(pair.Key, out var old))
            {
                DateTime now = UtcNow();
                var entry = new StewardFileEntry
                {
                    FileID = Guid.NewGuid().ToString("N"), StewardID = stewardID, Path = pair.Key, Kind = pair.Value.kind,
                    Size = pair.Value.size, MimeType = pair.Value.kind == StewardFileKind.Directory ? "inode/directory" : GuessMimeType(pair.Key),
                    FileSystemModifiedUtc = pair.Value.modified, CreatedUtc = pair.Value.modified == default ? now : pair.Value.modified,
                    ModifiedUtc = now, CreatedBy = StewardFileActor.Unknown, ModifiedBy = StewardFileActor.Unknown,
                    Origin = StewardFileOrigin.Filesystem,
                };
                UpsertEntry(connection, transaction, entry);
                InsertAudit(connection, transaction, stewardID, StewardFileOperation.ReconcileCreate, entry.Path, StewardFileActor.Unknown, null, entry.Size, null);
                created++;
            }
            else if (old.Kind != pair.Value.kind || old.Size != pair.Value.size || old.FileSystemModifiedUtc != pair.Value.modified)
            {
                var updated = new StewardFileEntry
                {
                    FileID = old.FileID, StewardID = old.StewardID, Path = old.Path, Kind = pair.Value.kind, Size = pair.Value.size,
                    MimeType = pair.Value.kind == StewardFileKind.Directory ? "inode/directory" : old.MimeType,
                    Sha256 = null, FileSystemModifiedUtc = pair.Value.modified, CreatedUtc = old.CreatedUtc, ModifiedUtc = UtcNow(),
                    CreatedBy = old.CreatedBy, ModifiedBy = StewardFileActor.Unknown, Origin = old.Origin,
                    Description = old.Description, Important = old.Important,
                };
                UpsertEntry(connection, transaction, updated);
                InsertAudit(connection, transaction, stewardID, StewardFileOperation.ReconcileModify, updated.Path, StewardFileActor.Unknown, null, updated.Size, null);
                modified++;
            }
        }
        foreach (var old in existing.Values.Where(e => !current.ContainsKey(e.Path)))
        {
            DeleteEntry(connection, transaction, stewardID, old.Path);
            InsertAudit(connection, transaction, stewardID, StewardFileOperation.ReconcileDelete, old.Path, StewardFileActor.Unknown, null, old.Size, null);
            deleted++;
        }
        transaction.Commit();
        return new StewardFileReconcileResult { Created = created, Modified = modified, Deleted = deleted };
    }

    private void GenerateManifest(string stewardID)
    {
        try
        {
            string root = EnsureStewardRoot(stewardID);
            string managed = Path.Combine(root, ".klive");
            Directory.CreateDirectory(managed);
            string path = Path.Combine(managed, "manifest.json");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var entries = LoadEntries(stewardID).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(temp, JsonConvert.SerializeObject(new
            {
                generatedUtc = UtcNow(),
                note = "Derived metadata. Use Steward file tools/API to make managed changes.",
                entries,
            }, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) { log($"Steward file manifest generation failed for {stewardID}: {ex.Message}"); }
    }

    private void InitializeSchema()
    {
        using var connection = OpenConnection();
        RenameLegacySchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS file_entries (
    file_id TEXT PRIMARY KEY,
    steward_id TEXT NOT NULL,
    path TEXT NOT NULL COLLATE NOCASE,
    kind INTEGER NOT NULL,
    size INTEGER NOT NULL,
    mime_type TEXT NOT NULL,
    sha256 TEXT NULL,
    fs_modified_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    created_actor_type INTEGER NOT NULL,
    created_actor_id TEXT NOT NULL,
    created_actor_name TEXT NOT NULL,
    modified_actor_type INTEGER NOT NULL,
    modified_actor_id TEXT NOT NULL,
    modified_actor_name TEXT NOT NULL,
    origin INTEGER NOT NULL,
    description TEXT NULL,
    important INTEGER NOT NULL DEFAULT 0,
    UNIQUE(steward_id,path)
);
CREATE INDEX IF NOT EXISTS idx_steward_files_path ON file_entries(steward_id,path);
CREATE INDEX IF NOT EXISTS idx_steward_files_modified ON file_entries(steward_id,modified_utc DESC);
CREATE TABLE IF NOT EXISTS file_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    steward_id TEXT NOT NULL,
    operation INTEGER NOT NULL,
    path TEXT NOT NULL,
    previous_path TEXT NULL,
    timestamp_utc TEXT NOT NULL,
    actor_type INTEGER NOT NULL,
    actor_id TEXT NOT NULL,
    actor_name TEXT NOT NULL,
    batch_id TEXT NULL,
    size INTEGER NULL,
    detail TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_steward_file_events ON file_events(steward_id,event_id DESC);
CREATE TABLE IF NOT EXISTS upload_sessions (
    session_id TEXT PRIMARY KEY,
    steward_id TEXT NULL,
    purpose INTEGER NOT NULL,
    status INTEGER NOT NULL,
    actor_type INTEGER NOT NULL,
    actor_id TEXT NOT NULL,
    actor_name TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    expires_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS upload_items (
    upload_file_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    path TEXT NOT NULL COLLATE NOCASE,
    expected_size INTEGER NOT NULL,
    received_size INTEGER NOT NULL,
    mime_type TEXT NOT NULL,
    sha256 TEXT NULL,
    UNIQUE(session_id,path)
);
CREATE INDEX IF NOT EXISTS idx_upload_items_session ON upload_items(session_id,path);
";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A database created before Projects became Demiurge names the owner column project_id and its
    /// indexes idx_project_*. The column is renamed in place (SQLite rewrites the UNIQUE constraint
    /// with it) and the old indexes are dropped for the schema below to rebuild under today's names,
    /// all in one transaction, so a crash leaves either the old schema or the new one.
    /// </summary>
    internal static void RenameLegacySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (string table in new[] { "file_entries", "file_events", "upload_sessions" })
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                info.Transaction = transaction;
                info.CommandText = $"PRAGMA table_info({table})";
                using var reader = info.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            if (!columns.Contains("project_id") || columns.Contains("steward_id")) continue;
            using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = $"ALTER TABLE {table} RENAME COLUMN project_id TO steward_id";
            rename.ExecuteNonQuery();
        }
        foreach (string index in new[] { "idx_project_files_path", "idx_project_files_modified", "idx_project_file_events" })
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP INDEX IF EXISTS {index}";
            drop.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        // Private cache: a shared one reintroduces table-level locks between connections, so file
        // listings queued behind every write despite WAL.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private List<StewardFileEntry> LoadEntries(string stewardID)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectEntrySql + " WHERE steward_id=$steward ORDER BY path COLLATE NOCASE";
        command.Parameters.AddWithValue("$steward", stewardID);
        using var reader = command.ExecuteReader();
        var entries = new List<StewardFileEntry>();
        while (reader.Read()) entries.Add(ReadEntry(reader));
        return entries;
    }

    private const string SelectEntrySql = @"SELECT file_id,steward_id,path,kind,size,mime_type,sha256,fs_modified_utc,
        created_utc,modified_utc,created_actor_type,created_actor_id,created_actor_name,
        modified_actor_type,modified_actor_id,modified_actor_name,origin,description,important FROM file_entries";

    private static StewardFileEntry? LoadEntry(SqliteConnection connection, string stewardID, string path, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectEntrySql + " WHERE steward_id=$steward AND path=$path COLLATE NOCASE";
        command.Parameters.AddWithValue("$steward", stewardID);
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static List<StewardFileEntry> LoadEntriesByPrefix(SqliteConnection connection, string stewardID, string path, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectEntrySql + " WHERE steward_id=$steward AND (path=$path COLLATE NOCASE OR path LIKE $prefix ESCAPE '\\') ORDER BY length(path)";
        command.Parameters.AddWithValue("$steward", stewardID);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$prefix", EscapeLike(path) + "/%");
        using var reader = command.ExecuteReader();
        var entries = new List<StewardFileEntry>();
        while (reader.Read()) entries.Add(ReadEntry(reader));
        return entries;
    }

    private static StewardFileEntry ReadEntry(SqliteDataReader reader) => new()
    {
        FileID = reader.GetString(0), StewardID = reader.GetString(1), Path = reader.GetString(2),
        Kind = (StewardFileKind)reader.GetInt32(3), Size = reader.GetInt64(4), MimeType = reader.GetString(5),
        Sha256 = NullableString(reader, 6), FileSystemModifiedUtc = ParseTime(reader.GetString(7)),
        CreatedUtc = ParseTime(reader.GetString(8)), ModifiedUtc = ParseTime(reader.GetString(9)),
        CreatedBy = ReadActor(reader, 10), ModifiedBy = ReadActor(reader, 13), Origin = (StewardFileOrigin)reader.GetInt32(16),
        Description = NullableString(reader, 17), Important = reader.GetInt32(18) != 0,
    };

    private static StewardFileUploadSession? LoadUploadSession(SqliteConnection connection, string sessionID)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT session_id,steward_id,purpose,status,actor_type,actor_id,actor_name,created_utc,expires_utc
                                FROM upload_sessions WHERE session_id=$id";
        command.Parameters.AddWithValue("$id", sessionID);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new StewardFileUploadSession
        {
            SessionID = reader.GetString(0), StewardID = NullableString(reader, 1), Purpose = (StewardUploadPurpose)reader.GetInt32(2),
            Status = (StewardUploadStatus)reader.GetInt32(3), Actor = ReadActor(reader, 4), CreatedUtc = ParseTime(reader.GetString(7)),
            ExpiresUtc = ParseTime(reader.GetString(8)),
        };
    }

    private StewardFileUploadSession RequireOpenOwnedSession(SqliteConnection connection, string sessionID, StewardFileActor actor)
    {
        var session = LoadUploadSession(connection, sessionID) ?? throw new StewardFileException("Unknown upload session.");
        RequireSameActor(session.Actor, actor);
        if (session.ExpiresUtc <= UtcNow()) throw new StewardFileException("Upload session expired.");
        if (session.Status != StewardUploadStatus.Open) throw new StewardFileException($"Upload session is {session.Status}.");
        return session;
    }

    private static StewardFileUploadItem ReadUploadItem(SqliteDataReader reader) => new()
    {
        UploadFileID = reader.GetString(0), SessionID = reader.GetString(1), Path = reader.GetString(2),
        ExpectedSize = reader.GetInt64(3), ReceivedSize = reader.GetInt64(4), MimeType = reader.GetString(5),
        Sha256 = NullableString(reader, 6),
    };

    private static void UpsertEntry(SqliteConnection connection, SqliteTransaction transaction, StewardFileEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO file_entries
            (file_id,steward_id,path,kind,size,mime_type,sha256,fs_modified_utc,created_utc,modified_utc,
             created_actor_type,created_actor_id,created_actor_name,modified_actor_type,modified_actor_id,modified_actor_name,
             origin,description,important)
            VALUES($file,$steward,$path,$kind,$size,$mime,$sha,$fsModified,$created,$modified,
                   $createdType,$createdId,$createdName,$modifiedType,$modifiedId,$modifiedName,$origin,$description,$important)
            ON CONFLICT(steward_id,path) DO UPDATE SET
                file_id=excluded.file_id,kind=excluded.kind,size=excluded.size,mime_type=excluded.mime_type,sha256=excluded.sha256,
                fs_modified_utc=excluded.fs_modified_utc,created_utc=excluded.created_utc,modified_utc=excluded.modified_utc,
                created_actor_type=excluded.created_actor_type,created_actor_id=excluded.created_actor_id,created_actor_name=excluded.created_actor_name,
                modified_actor_type=excluded.modified_actor_type,modified_actor_id=excluded.modified_actor_id,modified_actor_name=excluded.modified_actor_name,
                origin=excluded.origin,description=excluded.description,important=excluded.important";
        command.Parameters.AddWithValue("$file", entry.FileID);
        command.Parameters.AddWithValue("$steward", entry.StewardID);
        command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$size", entry.Size);
        command.Parameters.AddWithValue("$mime", entry.MimeType);
        command.Parameters.AddWithValue("$sha", (object?)entry.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$fsModified", Iso(entry.FileSystemModifiedUtc));
        command.Parameters.AddWithValue("$created", Iso(entry.CreatedUtc));
        command.Parameters.AddWithValue("$modified", Iso(entry.ModifiedUtc));
        command.Parameters.AddWithValue("$createdType", (int)entry.CreatedBy.Type);
        command.Parameters.AddWithValue("$createdId", entry.CreatedBy.ID);
        command.Parameters.AddWithValue("$createdName", entry.CreatedBy.DisplayName);
        command.Parameters.AddWithValue("$modifiedType", (int)entry.ModifiedBy.Type);
        command.Parameters.AddWithValue("$modifiedId", entry.ModifiedBy.ID);
        command.Parameters.AddWithValue("$modifiedName", entry.ModifiedBy.DisplayName);
        command.Parameters.AddWithValue("$origin", (int)entry.Origin);
        command.Parameters.AddWithValue("$description", (object?)entry.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$important", entry.Important ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void DeleteEntry(SqliteConnection connection, SqliteTransaction transaction, string stewardID, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM file_entries WHERE steward_id=$steward AND path=$path COLLATE NOCASE";
        command.Parameters.AddWithValue("$steward", stewardID);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private void InsertAudit(SqliteConnection connection, SqliteTransaction transaction, string stewardID,
        StewardFileOperation operation, string path, StewardFileActor actor, string? batchID, long? size, string? detail)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO file_events
            (steward_id,operation,path,previous_path,timestamp_utc,actor_type,actor_id,actor_name,batch_id,size,detail)
            VALUES($steward,$operation,$path,$previous,$timestamp,$actorType,$actorId,$actorName,$batch,$size,$detail)";
        command.Parameters.AddWithValue("$steward", stewardID);
        command.Parameters.AddWithValue("$operation", (int)operation);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$previous", operation is StewardFileOperation.Move or StewardFileOperation.Copy ? (object?)detail ?? DBNull.Value : DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", Iso(UtcNow()));
        AddActor(command, "actor", actor);
        command.Parameters.AddWithValue("$batch", (object?)batchID ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", operation is StewardFileOperation.Move or StewardFileOperation.Copy ? DBNull.Value : (object?)detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static StewardFileEntry EntryForFile(string stewardID, string path, string physical, string mimeType, string hash,
        StewardFileActor actor, StewardFileOrigin origin, StewardFileEntry? prior, DateTime? timestamp = null)
    {
        DateTime now = timestamp ?? DateTime.UtcNow;
        var info = new FileInfo(physical);
        return new StewardFileEntry
        {
            FileID = prior?.FileID ?? Guid.NewGuid().ToString("N"), StewardID = stewardID, Path = path, Kind = StewardFileKind.File,
            Size = info.Length, MimeType = mimeType, Sha256 = hash, FileSystemModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = prior?.CreatedUtc ?? now, ModifiedUtc = now, CreatedBy = prior?.CreatedBy ?? actor, ModifiedBy = actor,
            Origin = prior?.Origin ?? origin, Description = prior?.Description, Important = prior?.Important ?? false,
        };
    }

    private static StewardFileEntry CloneEntry(StewardFileEntry source,
        string? path = null, StewardFileKind? kind = null, long? size = null, string? mimeType = null,
        string? sha256 = null, DateTime? fileSystemModifiedUtc = null, DateTime? createdUtc = null, DateTime? modifiedUtc = null,
        StewardFileActor? createdBy = null, StewardFileActor? modifiedBy = null, StewardFileOrigin? origin = null,
        string? description = null, bool? important = null) => new()
    {
        FileID = source.FileID, StewardID = source.StewardID, Path = path ?? source.Path, Kind = kind ?? source.Kind,
        Size = size ?? source.Size, MimeType = mimeType ?? source.MimeType, Sha256 = sha256 ?? source.Sha256,
        FileSystemModifiedUtc = fileSystemModifiedUtc ?? source.FileSystemModifiedUtc,
        CreatedUtc = createdUtc ?? source.CreatedUtc, ModifiedUtc = modifiedUtc ?? source.ModifiedUtc,
        CreatedBy = createdBy ?? source.CreatedBy, ModifiedBy = modifiedBy ?? source.ModifiedBy,
        Origin = origin ?? source.Origin, Description = description ?? source.Description, Important = important ?? source.Important,
    };

    private string ResolvePhysicalPath(string stewardID, string path, bool allowMissingLeaf = false, bool allowManagedMetadata = false)
    {
        ValidateStewardID(stewardID);
        string normalized = NormalizeStewardPath(stewardID, path, allowManagedMetadata: allowManagedMetadata);
        string root = EnsureStewardRoot(stewardID);
        string physical = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, physical);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new StewardFileException("Path escapes the Steward volume.");
        string check = allowMissingLeaf ? Path.GetDirectoryName(physical)! : physical;
        EnsureNoReparsePoints(root, check);
        return physical;
    }

    private List<StewardFileEntry> LoadManagedMetadataEntries(string stewardID)
    {
        string root = EnsureStewardRoot(stewardID);
        string managedRoot = Path.Combine(root, ".klive");
        if (!Directory.Exists(managedRoot)) return new();
        var results = new List<StewardFileEntry>();
        foreach (string physical in Directory.EnumerateFileSystemEntries(managedRoot, "*", SearchOption.AllDirectories).Prepend(managedRoot))
        {
            if ((File.GetAttributes(physical) & FileAttributes.ReparsePoint) != 0) continue;
            bool directory = Directory.Exists(physical);
            var info = directory ? null : new FileInfo(physical);
            DateTime modified = directory ? Directory.GetLastWriteTimeUtc(physical) : info!.LastWriteTimeUtc;
            string relative = Path.GetRelativePath(root, physical).Replace('\\', '/');
            results.Add(new StewardFileEntry
            {
                FileID = "managed:" + relative.ToLowerInvariant(),
                StewardID = stewardID,
                Path = relative,
                Kind = directory ? StewardFileKind.Directory : StewardFileKind.File,
                Size = info?.Length ?? 0,
                MimeType = directory ? "inode/directory" : "application/json",
                FileSystemModifiedUtc = modified,
                CreatedUtc = modified,
                ModifiedUtc = modified,
                CreatedBy = StewardFileActor.System,
                ModifiedBy = StewardFileActor.System,
                Origin = StewardFileOrigin.System,
                Description = "Read-only derived Steward metadata.",
            });
        }
        return results;
    }

    private static string NormalizeUnixText(string path, string content)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        bool unixExecutedText = UnixTextExtensions.Contains(extension)
            || fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase);
        return unixExecutedText ? content.Replace("\r\n", "\n").Replace('\r', '\n') : content;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        string current = Path.GetFullPath(root);
        string target = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(current, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new StewardFileException("Path escapes the Steward volume.");
        if (File.Exists(current) || Directory.Exists(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new StewardFileException("Steward volume cannot be a symlink or junction.");
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StewardFileException("Symlink and junction paths are not allowed in managed steward-file operations.");
        }
    }

    private string EnsureStewardRoot(string stewardID)
    {
        string root = StewardRoot(stewardID);
        Directory.CreateDirectory(root);
        return root;
    }

    private string StewardRoot(string stewardID)
    {
        ValidateStewardID(stewardID);
        string root = Path.GetFullPath(Path.Combine(volumesRoot, stewardID));
        if (!string.Equals(Path.GetDirectoryName(root), volumesRoot, StringComparison.OrdinalIgnoreCase))
            throw new StewardFileException("Invalid Steward ID.");
        return root;
    }

    private string SessionDirectory(string sessionID)
    {
        if (!Regex.IsMatch(sessionID ?? "", "^[a-fA-F0-9]{32}$")) throw new StewardFileException("Invalid upload session ID.");
        string directory = Path.GetFullPath(Path.Combine(stagingRoot, sessionID!));
        if (!string.Equals(Path.GetDirectoryName(directory), stagingRoot, StringComparison.OrdinalIgnoreCase))
            throw new StewardFileException("Invalid upload session path.");
        return directory;
    }

    private string StagingFilePath(string sessionID, string uploadFileID)
    {
        if (!Regex.IsMatch(uploadFileID ?? "", "^[a-fA-F0-9]{32}$")) throw new StewardFileException("Invalid upload file ID.");
        return Path.Combine(SessionDirectory(sessionID), uploadFileID + ".part");
    }

    private string FindKeepBothPath(string stewardID, string requested, HashSet<string> claimed)
    {
        string directory = Path.GetDirectoryName(requested.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        string fileName = Path.GetFileNameWithoutExtension(requested);
        string extension = Path.GetExtension(requested);
        for (int n = 2; n < 100_000; n++)
        {
            string candidateName = $"{fileName} ({n}){extension}";
            string candidate = directory.Length == 0 ? candidateName : directory + "/" + candidateName;
            string physical = ResolvePhysicalPath(stewardID, candidate, allowMissingLeaf: true);
            if (!File.Exists(physical) && !Directory.Exists(physical) && !claimed.Contains(candidate)) return candidate;
        }
        throw new StewardFileException("Could not choose an unused keep-both filename.");
    }

    private static StewardFileConflictPolicy PolicyFor(StewardFileCommitOptions options, string uploadPath, string destinationPath)
    {
        if (options.PathPolicies != null)
            foreach (var pair in options.PathPolicies)
                if (string.Equals(pair.Key, uploadPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pair.Key, destinationPath, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return options.ConflictPolicy;
    }

    private void EnsureFreeDiskSpace(long incomingBytes)
    {
        try
        {
            string root = Path.GetPathRoot(volumesRoot) ?? volumesRoot;
            long available = new DriveInfo(root).AvailableFreeSpace;
            if (available - Math.Max(0, incomingBytes) < options.MinimumFreeDiskBytes)
                throw new StewardFileException($"Upload would breach the configured free-disk reserve of {options.MinimumFreeDiskBytes} bytes.");
        }
        catch (StewardFileException) { throw; }
        catch (Exception ex)
        {
            throw new StewardFileException("Could not verify free disk space for the Steward volume.", ex);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new StewardFileException("Cannot copy a symlink or junction.");
        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new StewardFileException("Cannot copy a symlink or junction.");
            file.CopyTo(Path.Combine(destination, file.Name));
        }
        foreach (var directory in sourceInfo.EnumerateDirectories()) CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name));
    }

    private static long CalculateDirectorySize(string source)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new StewardFileException("Cannot copy a symlink or junction.");
        long total = 0;
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new StewardFileException("Cannot copy a symlink or junction.");
            total = checked(total + file.Length);
        }
        foreach (var directory in sourceInfo.EnumerateDirectories())
            total = checked(total + CalculateDirectorySize(directory.FullName));
        return total;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool GlobMatches(string pattern, string value)
    {
        string glob = pattern.Trim().Replace('\\', '/');
        var builder = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                    builder.Append("(?:.*/)?"); // **/ also matches the current directory
                }
                else builder.Append(".*");
            }
            else if (glob[i] == '*') builder.Append("[^/]*");
            else if (glob[i] == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(glob[i].ToString()));
        }
        builder.Append('$');
        string regex = builder.ToString();
        return Regex.IsMatch(value.Replace('\\', '/'), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    private static string GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".csv" or ".log" => "text/plain",
            ".json" => "application/json", ".html" or ".htm" => "text/html", ".css" => "text/css",
            ".js" => "text/javascript", ".xml" => "application/xml", ".pdf" => "application/pdf",
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
            ".svg" => "image/svg+xml", ".webp" => "image/webp", ".zip" => "application/zip",
            ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".wav" => "audio/wav",
            _ => "application/octet-stream",
        };
    }

    private static string NormalizeMimeType(string? mimeType)
    {
        string value = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType.Trim();
        if (value.Length > 200 || value.Any(c => c is '\r' or '\n' or '\0')) return "application/octet-stream";
        return value;
    }

    private static string? NormalizeDescription(string description)
    {
        string value = Regex.Replace(description.Trim(), @"\s+", " ");
        if (value.Length == 0) return null;
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static void RequireSameActor(StewardFileActor owner, StewardFileActor actor)
    {
        if (owner.Type != actor.Type || !string.Equals(owner.ID, actor.ID, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Upload session belongs to another user.");
    }

    private static void ValidateStewardID(string stewardID)
    {
        if (string.IsNullOrWhiteSpace(stewardID) || stewardID.Length > 100 ||
            stewardID.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || stewardID is "." or "..")
            throw new StewardFileException("Invalid Steward ID.");
    }

    private static void AddActor(SqliteCommand command, string prefix, StewardFileActor actor)
    {
        command.Parameters.AddWithValue("$" + prefix + "Type", (int)actor.Type);
        command.Parameters.AddWithValue("$" + prefix + "Id", actor.ID ?? "");
        command.Parameters.AddWithValue("$" + prefix + "Name", actor.DisplayName ?? "");
    }

    private static StewardFileActor ReadActor(SqliteDataReader reader, int start) =>
        new((StewardFileActorType)reader.GetInt32(start), reader.GetString(start + 1), reader.GetString(start + 2));

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("O");
    private static DateTime ParseTime(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    private DateTime UtcNow() => options.TimeProvider.GetUtcNow().UtcDateTime;
    private SemaphoreSlim Gate(string key) => operationGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    private static int AcquireAllUploadSlots(UploadConcurrency concurrency)
    {
        int held = 0;
        try
        {
            while (held < UploadConcurrency.ParallelFiles) { concurrency.Slots.Wait(); held++; }
            return held;
        }
        catch
        {
            if (held > 0) concurrency.Slots.Release(held);
            throw;
        }
    }
    private static void ReleaseUploadSlots(UploadConcurrency concurrency, int held)
    {
        if (held > 0) concurrency.Slots.Release(held);
    }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static StewardFileOrigin OriginFor(StewardFileActor actor) => actor.Type switch
    {
        StewardFileActorType.Commander or StewardFileActorType.Agent => StewardFileOrigin.AgentTool,
        StewardFileActorType.User => StewardFileOrigin.UserUpload,
        StewardFileActorType.System => StewardFileOrigin.System,
        _ => StewardFileOrigin.Filesystem,
    };
    private static DateTime FileSystemTime(string physicalRoot, StewardFileKind kind) => kind == StewardFileKind.Directory
        ? Directory.GetLastWriteTimeUtc(physicalRoot) : File.GetLastWriteTimeUtc(physicalRoot);
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
