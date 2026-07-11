using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Profiles;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;
using ApiUserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.Projects;

/// <summary>Klives-only upload, browse, download and management API for shared project files.</summary>
public sealed class ProjectFilesRoutes
{
    private const long ControlBodyLimit = 1024 * 1024;
    private readonly Projects parent;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    public ProjectFilesRoutes(Projects parent) => this.parent = parent;

    public async Task RegisterRoutes()
    {
        await parent.CreateBufferedAPIRoute("/projects/files/uploads/start", StartUpload,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateAPIRoute("/projects/files/uploads/get", GetUpload,
            HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        await parent.CreateStreamingAPIRoute("/projects/files/uploads/chunk", UploadChunk,
            HttpMethod.Put, KMProfileManager.KMPermissions.Klives, parent.Files.Options.MaxChunkBytes);
        await parent.CreateBufferedAPIRoute("/projects/files/uploads/commit", CommitUpload,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateBufferedAPIRoute("/projects/files/uploads/cancel", CancelUpload,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);

        await parent.CreateAPIRoute("/projects/files/list", ListFiles,
            HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        await parent.CreateAPIRoute("/projects/files/stat", StatFile,
            HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        await parent.CreateAPIRoute("/projects/files/download", DownloadFile,
            HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
        await parent.CreateAPIRoute("/projects/files/audit", AuditFiles,
            HttpMethod.Get, KMProfileManager.KMPermissions.Klives);

        await parent.CreateBufferedAPIRoute("/projects/files/directory", CreateDirectory,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateBufferedAPIRoute("/projects/files/move", MoveFile,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateBufferedAPIRoute("/projects/files/copy", CopyFile,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateBufferedAPIRoute("/projects/files/delete", DeleteFile,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
        await parent.CreateBufferedAPIRoute("/projects/files/metadata", SetMetadata,
            HttpMethod.Post, KMProfileManager.KMPermissions.Klives, ControlBodyLimit);
    }

    private async Task StartUpload(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            string purposeText = ((string?)body["purpose"] ?? "existingProject").Trim();
            ProjectUploadPurpose purpose = purposeText.Equals("initial", StringComparison.OrdinalIgnoreCase)
                ? ProjectUploadPurpose.Initial
                : purposeText.Equals("existing", StringComparison.OrdinalIgnoreCase) ||
                  purposeText.Equals("existingProject", StringComparison.OrdinalIgnoreCase)
                    ? ProjectUploadPurpose.ExistingProject
                    : throw new ProjectFileException("purpose must be 'initial' or 'existingProject'.");
            string? projectID = ((string?)body["projectID"])?.Trim();
            if (purpose == ProjectUploadPurpose.ExistingProject)
                RequireWritableProject(projectID ?? "");
            else
                projectID = null;

            var session = parent.Files.CreateUploadSession(purpose, projectID, Actor(req));
            await Ok(req, new
            {
                session,
                items = Array.Empty<ProjectFileUploadItem>(),
                maxFileBytes = parent.Files.Options.MaxFileBytes,
                maxChunkBytes = parent.Files.Options.MaxChunkBytes,
            });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task GetUpload(ApiUserRequest req)
    {
        try
        {
            string sessionID = Required(req.userParameters?.Get("sessionID"), "sessionID");
            var session = RequireOwnedSession(sessionID, Actor(req));
            await Ok(req, new
            {
                session,
                items = parent.Files.ListUploadItems(sessionID),
                maxFileBytes = parent.Files.Options.MaxFileBytes,
                maxChunkBytes = parent.Files.Options.MaxChunkBytes,
            });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task UploadChunk(ApiUserRequest req)
    {
        try
        {
            string sessionID = Required(req.userParameters?.Get("sessionID"), "sessionID");
            string path = Required(req.userParameters?.Get("path"), "path");
            if (!long.TryParse(req.userParameters?.Get("offset"), NumberStyles.None, CultureInfo.InvariantCulture, out long offset))
                throw new ProjectFileException("offset must be a non-negative integer.");
            if (!long.TryParse(req.userParameters?.Get("expectedSize"), NumberStyles.None, CultureInfo.InvariantCulture, out long expectedSize))
                throw new ProjectFileException("expectedSize must be a non-negative integer.");
            var actor = Actor(req);
            var session = RequireOwnedSession(sessionID, actor);
            if (session.ProjectID != null) RequireWritableProject(session.ProjectID);
            string? contentType = req.userParameters?.Get("contentType") ?? req.req?.ContentType;
            var item = await parent.Files.AppendUploadChunkAsync(sessionID, path, offset, expectedSize,
                contentType, req.RequestBodyStream, actor);
            await Ok(req, new
            {
                item,
                complete = item.ReceivedSize == item.ExpectedSize,
                nextOffset = item.ReceivedSize,
            });
        }
        catch (ApiService.RequestBodyTooLargeException)
        {
            // Let the central pipeline emit its consistent 413 and audit outcome.
            throw;
        }
        catch (Exception ex)
        {
            // A rejected streaming request may leave unread bytes. Do not reuse that connection.
            try { req.context.Response.KeepAlive = false; } catch { }
            await Error(req, ex);
        }
    }

    private async Task CommitUpload(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            string sessionID = Required((string?)body["sessionID"], "sessionID");
            var actor = Actor(req);
            var session = RequireOwnedSession(sessionID, actor);
            if (session.Purpose != ProjectUploadPurpose.ExistingProject || string.IsNullOrWhiteSpace(session.ProjectID))
                throw new ProjectFileException("Initial uploads are committed transactionally by /projects/create.");
            Project project = RequireWritableProject(session.ProjectID);
            var options = new ProjectFileCommitOptions
            {
                ConflictPolicy = ParsePolicy((string?)body["conflictPolicy"]),
                PathPolicies = ParsePathPolicies(body["pathPolicies"] as JObject),
            };
            var result = parent.Files.CommitUploadSession(sessionID, project.ProjectID, actor, options);
            var paths = result.Items.Where(x => !x.Skipped && x.CommittedPath != null)
                .Select(x => x.CommittedPath!).ToList();
            if (paths.Count > 0)
            {
                ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.Upload,
                    paths, result.TotalBytes, result.BatchID);
                NotifyCommander(project, $"Klives uploaded {paths.Count} shared project file(s): {Sample(paths)}");
            }
            await Ok(req, result);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task CancelUpload(ApiUserRequest req)
    {
        try
        {
            string sessionID = Required((string?)Body(req)["sessionID"], "sessionID");
            bool cancelled = parent.Files.CancelUploadSession(sessionID, Actor(req));
            await Ok(req, new { cancelled });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task ListFiles(ApiUserRequest req)
    {
        try
        {
            Project project = RequireProject(req.userParameters?.Get("projectID") ?? "");
            string cursor = req.userParameters?.Get("cursor") ?? "";
            int offset = DecodeCursor(cursor);
            int limit = int.TryParse(req.userParameters?.Get("limit"), out int parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 500) : 100;
            bool recursive = bool.TryParse(req.userParameters?.Get("recursive"), out bool parsedRecursive) && parsedRecursive;
            var result = parent.Files.List(project.ProjectID, new ProjectFileListRequest
            {
                Directory = req.userParameters?.Get("path") ?? "",
                Recursive = recursive,
                Search = req.userParameters?.Get("query"),
                Glob = req.userParameters?.Get("glob"),
                Offset = offset,
                Limit = limit,
            });
            string? nextCursor = result.Offset + result.Entries.Count < result.Total
                ? EncodeCursor(result.Offset + result.Entries.Count) : null;
            await Ok(req, new { result.Entries, result.Total, result.Limit, nextCursor });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task StatFile(ApiUserRequest req)
    {
        try
        {
            Project project = RequireProject(req.userParameters?.Get("projectID") ?? "");
            string path = Required(req.userParameters?.Get("path"), "path");
            var entry = parent.Files.Stat(project.ProjectID, path)
                ?? throw new FileNotFoundException("Project file or directory not found.", path);
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task DownloadFile(ApiUserRequest req)
    {
        try
        {
            Project project = RequireProject(req.userParameters?.Get("projectID") ?? "");
            string path = Required(req.userParameters?.Get("path"), "path");
            var entry = parent.Files.Stat(project.ProjectID, path)
                ?? throw new FileNotFoundException("Project file not found.", path);
            if (entry.Kind != ProjectFileKind.File) throw new ProjectFileException("Only files can be downloaded.");
            await using var source = parent.Files.OpenRead(project.ProjectID, path);
            string fileName = Path.GetFileName(entry.Path);
            string asciiName = new(fileName.Select(c => c is >= ' ' and <= '~' && c is not '"' and not '\\' ? c : '_').Take(150).ToArray());
            var headers = new NameValueCollection
            {
                ["Content-Disposition"] = $"attachment; filename=\"{asciiName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}",
                ["X-Content-Type-Options"] = "nosniff",
            };
            await using Stream destination = req.PrepareStreamResponse(entry.MimeType, source.Length, headers: headers);
            await source.CopyToAsync(destination);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task AuditFiles(ApiUserRequest req)
    {
        try
        {
            Project project = RequireProject(req.userParameters?.Get("projectID") ?? "");
            int limit = int.TryParse(req.userParameters?.Get("limit"), out int parsed)
                ? Math.Clamp(parsed, 1, 1000) : 200;
            long? before = DecodeLongCursor(req.userParameters?.Get("cursor"));
            var events = parent.Files.ListAudit(project.ProjectID, limit, before);
            string? nextCursor = events.Count == limit && events[^1].EventID > 1
                ? EncodeLongCursor(events[^1].EventID) : null;
            await Ok(req, new { events, nextCursor });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task CreateDirectory(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Project project = RequireWritableProject(Required((string?)body["projectID"], "projectID"));
            string path = Required((string?)body["path"], "path");
            var prior = parent.Files.Stat(project.ProjectID, path);
            if (prior?.Kind == ProjectFileKind.Directory) { await Ok(req, new { entry = prior, created = false }); return; }
            var actor = Actor(req);
            var entry = parent.Files.CreateDirectory(project.ProjectID, path, actor);
            ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.CreateDirectory, [entry.Path]);
            NotifyCommander(project, $"Klives created shared directory '{entry.Path}'.");
            await Ok(req, new { entry, created = true });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task MoveFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Project project = RequireWritableProject(Required((string?)body["projectID"], "projectID"));
            string source = Required((string?)body["path"], "path");
            string destination = Required((string?)body["destination"], "destination");
            var actor = Actor(req);
            var entry = parent.Files.Move(project.ProjectID, source, destination, actor);
            ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.Move,
                [entry.Path], entry.Size, previousPath: source);
            NotifyCommander(project, $"Klives moved shared path '{source}' to '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task CopyFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Project project = RequireWritableProject(Required((string?)body["projectID"], "projectID"));
            string source = Required((string?)body["path"], "path");
            string destination = Required((string?)body["destination"], "destination");
            var actor = Actor(req);
            var entry = parent.Files.Copy(project.ProjectID, source, destination, actor);
            long bytes = AffectedBytes(project.ProjectID, entry.Path);
            ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.Copy,
                [entry.Path], bytes, previousPath: source);
            NotifyCommander(project, $"Klives copied shared path '{source}' to '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task DeleteFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Project project = RequireWritableProject(Required((string?)body["projectID"], "projectID"));
            string path = Required((string?)body["path"], "path");
            bool recursive = (bool?)body["recursive"] ?? false;
            var actor = Actor(req);
            bool deleted = parent.Files.Delete(project.ProjectID, path, recursive, actor);
            if (deleted)
            {
                ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.Delete, [path]);
                NotifyCommander(project, $"Klives deleted shared path '{path}'.");
            }
            await Ok(req, new { deleted });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task SetMetadata(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Project project = RequireWritableProject(Required((string?)body["projectID"], "projectID"));
            string path = Required((string?)body["path"], "path");
            bool? important = body.TryGetValue("important", StringComparison.OrdinalIgnoreCase, out JToken? importantToken)
                && importantToken.Type != JTokenType.Null ? (bool?)importantToken : null;
            string? description = body.TryGetValue("description", StringComparison.OrdinalIgnoreCase, out JToken? descriptionToken)
                ? (descriptionToken.Type == JTokenType.Null ? "" : (string?)descriptionToken ?? "") : null;
            if (!important.HasValue && description == null)
                throw new ProjectFileException("Provide important and/or description.");
            var actor = Actor(req);
            var entry = parent.Files.SetMetadata(project.ProjectID, path, important, description, actor);
            ProjectFileTimeline.Append(parent.EventLog, project.ProjectID, actor, ProjectFileOperation.Metadata, [entry.Path]);
            NotifyCommander(project, $"Klives updated shared-file metadata for '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private Project RequireProject(string projectID) => parent.Store.GetProject(projectID)
        ?? throw new FileNotFoundException("Unknown projectID.", projectID);

    private Project RequireWritableProject(string projectID)
    {
        Project project = RequireProject(projectID);
        if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
            throw new ProjectFilesReadOnlyException();
        return project;
    }

    private ProjectFileUploadSession RequireOwnedSession(string sessionID, ProjectFileActor actor)
    {
        var session = parent.Files.GetUploadSession(sessionID)
            ?? throw new FileNotFoundException("Unknown upload session.", sessionID);
        if (session.Actor.Type != actor.Type || !string.Equals(session.Actor.ID, actor.ID, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Upload session belongs to another user.");
        return session;
    }

    private static ProjectFileActor Actor(ApiUserRequest req) => new(
        ProjectFileActorType.User,
        req.user?.UserID ?? "klives",
        req.user?.Name ?? "Klives");

    private void NotifyCommander(Project project, string text)
    {
        if (project.Status is ProjectStatus.Active or ProjectStatus.Planning)
            parent.CommanderRunner.Steer(project, text);
    }

    private long AffectedBytes(string projectID, string path)
    {
        var root = parent.Files.Stat(projectID, path, reconcile: false);
        if (root?.Kind == ProjectFileKind.File) return root.Size;
        long totalBytes = 0;
        int offset = 0;
        while (true)
        {
            var page = parent.Files.List(projectID, new ProjectFileListRequest
            {
                Directory = path, Recursive = true, Limit = 500, Offset = offset, Reconcile = false,
            });
            totalBytes += page.Entries.Where(x => x.Kind == ProjectFileKind.File).Sum(x => x.Size);
            offset += page.Entries.Count;
            if (offset >= page.Total || page.Entries.Count == 0) return totalBytes;
        }
    }

    private static JObject Body(ApiUserRequest req)
    {
        try { return JObject.Parse(string.IsNullOrWhiteSpace(req.userMessageContent) ? "{}" : req.userMessageContent); }
        catch (JsonException ex) { throw new ProjectFileException("Request body must be a JSON object.", ex); }
    }

    private static string Required(string? value, string name)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? throw new ProjectFileException($"{name} required.") : value;
    }

    private static ProjectFileConflictPolicy ParsePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ProjectFileConflictPolicy.Fail;
        return Enum.TryParse(value, true, out ProjectFileConflictPolicy parsed)
            ? parsed : throw new ProjectFileException("Unknown conflictPolicy.");
    }

    private static IReadOnlyDictionary<string, ProjectFileConflictPolicy>? ParsePathPolicies(JObject? value)
    {
        if (value == null) return null;
        var result = new Dictionary<string, ProjectFileConflictPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.Properties()) result[property.Name] = ParsePolicy((string?)property.Value);
        return result;
    }

    private static int DecodeCursor(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) && offset >= 0) return offset;
        }
        catch { }
        throw new ProjectFileException("Invalid cursor.");
    }

    private static string EncodeCursor(int offset) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static long? DecodeLongCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (long.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0) return value;
        }
        catch { }
        throw new ProjectFileException("Invalid audit cursor.");
    }

    private static string EncodeLongCursor(long value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));

    private static string Sample(IReadOnlyList<string> paths) => paths.Count == 0 ? "(none)" :
        string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? ", …" : "");

    private static Task Ok(ApiUserRequest req, object value) =>
        req.ReturnResponse(JsonConvert.SerializeObject(value, JsonSettings));

    private async Task Error(ApiUserRequest req, Exception ex)
    {
        HttpStatusCode status;
        string message;
        switch (ex)
        {
            case ProjectFilesReadOnlyException:
                status = HttpStatusCode.Conflict;
                message = "Completed and archived projects are browse/download-only.";
                break;
            case ProjectFileConflictException conflict:
                status = HttpStatusCode.Conflict;
                message = conflict.Message;
                break;
            case FileNotFoundException:
                status = HttpStatusCode.NotFound;
                message = ex.Message;
                break;
            case UnauthorizedAccessException:
                status = HttpStatusCode.Forbidden;
                message = ex.Message;
                break;
            case ProjectFileException or ArgumentException or JsonException:
                status = HttpStatusCode.BadRequest;
                message = ex.Message;
                break;
            case IOException:
                status = (HttpStatusCode)507;
                message = "The file operation could not be completed because storage is unavailable or busy.";
                break;
            default:
                status = HttpStatusCode.InternalServerError;
                message = "The shared-file operation failed.";
                _ = parent.ServiceLogError(ex, "Projects: shared-file API failed");
                break;
        }
        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = message }, JsonSettings), code: status);
    }

    private sealed class ProjectFilesReadOnlyException : ProjectFileException
    {
        public ProjectFilesReadOnlyException() : base("This project's shared filesystem is read-only.") { }
    }
}
