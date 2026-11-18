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
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.Demiurge;

/// <summary>Klives-only upload, browse, download and management API for shared Steward files.</summary>
public sealed class StewardFilesRoutes
{
    private const long ControlBodyLimit = 1024 * 1024;
    private readonly Demiurge parent;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    public StewardFilesRoutes(Demiurge parent) => this.parent = parent;

    public async Task RegisterRoutes()
    {
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/uploads/start", StartUpload,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
        await parent.RegisterHttpRouteAsync("/demiurge/files/uploads/get", GetUpload,
            HttpMethod.Get, DemiurgePerms.FilesRead);
        await parent.RegisterStreamingHttpRouteAsync("/demiurge/files/uploads/chunk", UploadChunk,
            HttpMethod.Put, DemiurgePerms.FilesWrite, parent.Files.Options.MaxChunkBytes);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/uploads/commit", CommitUpload,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/uploads/cancel", CancelUpload,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);

        await parent.RegisterHttpRouteAsync("/demiurge/files/list", ListFiles,
            HttpMethod.Get, DemiurgePerms.FilesRead);
        await parent.RegisterHttpRouteAsync("/demiurge/files/stat", StatFile,
            HttpMethod.Get, DemiurgePerms.FilesRead);
        await parent.RegisterHttpRouteAsync("/demiurge/files/download", DownloadFile,
            HttpMethod.Get, DemiurgePerms.FilesRead);
        await parent.RegisterHttpRouteAsync("/demiurge/files/audit", AuditFiles,
            HttpMethod.Get, DemiurgePerms.FilesRead);

        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/directory", CreateDirectory,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/move", MoveFile,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/copy", CopyFile,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/delete", DeleteFile,
            HttpMethod.Post, DemiurgePerms.FilesDelete, ControlBodyLimit);
        await parent.RegisterBufferedHttpRouteAsync("/demiurge/files/metadata", SetMetadata,
            HttpMethod.Post, DemiurgePerms.FilesWrite, ControlBodyLimit);
    }

    private async Task StartUpload(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            string purposeText = ((string?)body["purpose"] ?? "existingSteward").Trim();
            StewardUploadPurpose purpose = purposeText.Equals("initial", StringComparison.OrdinalIgnoreCase)
                ? StewardUploadPurpose.Initial
                : purposeText.Equals("existing", StringComparison.OrdinalIgnoreCase) ||
                  purposeText.Equals("existingSteward", StringComparison.OrdinalIgnoreCase) ||
                  purposeText.Equals("existingProject", StringComparison.OrdinalIgnoreCase) // a page loaded before the rename
                    ? StewardUploadPurpose.ExistingSteward
                    : throw new StewardFileException("purpose must be 'initial' or 'existingSteward'.");
            string? stewardID = ((string?)body["stewardID"])?.Trim();
            if (purpose == StewardUploadPurpose.ExistingSteward)
                RequireWritableSteward(stewardID ?? "");
            else
                stewardID = null;

            var session = parent.Files.CreateUploadSession(purpose, stewardID, Actor(req));
            await Ok(req, new
            {
                session,
                items = Array.Empty<StewardFileUploadItem>(),
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
                throw new StewardFileException("offset must be a non-negative integer.");
            if (!long.TryParse(req.userParameters?.Get("expectedSize"), NumberStyles.None, CultureInfo.InvariantCulture, out long expectedSize))
                throw new StewardFileException("expectedSize must be a non-negative integer.");
            var actor = Actor(req);
            var session = RequireOwnedSession(sessionID, actor);
            if (session.StewardID != null) RequireWritableSteward(session.StewardID);
            string? contentType = req.userParameters?.Get("contentType") ?? req.req?.ContentType;
            var item = await parent.Files.AppendUploadChunkAsync(sessionID, path, offset, expectedSize,
                contentType, req.RequestBodyStream, actor);
            await Ok(req, new
            {
                item.UploadFileID,
                item.Path,
                item.ReceivedSize,
                item.ExpectedSize,
                item.MimeType,
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
            if (session.Purpose != StewardUploadPurpose.ExistingSteward || string.IsNullOrWhiteSpace(session.StewardID))
                throw new StewardFileException("Initial uploads are committed transactionally by /demiurge/create.");
            Steward steward = RequireWritableSteward(session.StewardID);
            var options = new StewardFileCommitOptions
            {
                ConflictPolicy = ParsePolicy((string?)body["conflictPolicy"]),
                PathPolicies = ParsePathPolicies(body["pathPolicies"] as JObject),
            };
            var result = parent.Files.CommitUploadSession(sessionID, steward.StewardID, actor, options);
            var paths = result.Items.Where(x => !x.Skipped && x.CommittedPath != null)
                .Select(x => x.CommittedPath!).ToList();
            if (paths.Count > 0)
            {
                StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.Upload,
                    paths, result.TotalBytes, result.BatchID);
                NotifyCommander(steward, $"Klives uploaded {paths.Count} shared Steward file(s): {Sample(paths)}");
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
            Steward steward = RequireSteward(req.userParameters?.Get("stewardID") ?? "");
            string cursor = req.userParameters?.Get("cursor") ?? "";
            int offset = DecodeCursor(cursor);
            int limit = int.TryParse(req.userParameters?.Get("limit"), out int parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 500) : 100;
            bool recursive = bool.TryParse(req.userParameters?.Get("recursive"), out bool parsedRecursive) && parsedRecursive;
            var result = parent.Files.List(steward.StewardID, new StewardFileListRequest
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
            Steward steward = RequireSteward(req.userParameters?.Get("stewardID") ?? "");
            string path = Required(req.userParameters?.Get("path"), "path");
            var entry = parent.Files.Stat(steward.StewardID, path)
                ?? throw new FileNotFoundException("Steward file or directory not found.", path);
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task DownloadFile(ApiUserRequest req)
    {
        try
        {
            Steward steward = RequireSteward(req.userParameters?.Get("stewardID") ?? "");
            string path = Required(req.userParameters?.Get("path"), "path");
            var entry = parent.Files.Stat(steward.StewardID, path)
                ?? throw new FileNotFoundException("Steward file not found.", path);
            if (entry.Kind != StewardFileKind.File) throw new StewardFileException("Only files can be downloaded.");
            await using var source = parent.Files.OpenRead(steward.StewardID, path);
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
            Steward steward = RequireSteward(req.userParameters?.Get("stewardID") ?? "");
            int limit = int.TryParse(req.userParameters?.Get("limit"), out int parsed)
                ? Math.Clamp(parsed, 1, 1000) : 200;
            long? before = DecodeLongCursor(req.userParameters?.Get("cursor"));
            var events = parent.Files.ListAudit(steward.StewardID, limit, before);
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
            Steward steward = RequireWritableSteward(Required((string?)body["stewardID"], "stewardID"));
            string path = Required((string?)body["path"], "path");
            var prior = parent.Files.Stat(steward.StewardID, path);
            if (prior?.Kind == StewardFileKind.Directory) { await Ok(req, new { entry = prior, created = false }); return; }
            var actor = Actor(req);
            var entry = parent.Files.CreateDirectory(steward.StewardID, path, actor);
            StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.CreateDirectory, [entry.Path]);
            NotifyCommander(steward, $"Klives created shared directory '{entry.Path}'.");
            await Ok(req, new { entry, created = true });
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task MoveFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Steward steward = RequireWritableSteward(Required((string?)body["stewardID"], "stewardID"));
            string source = Required((string?)body["path"], "path");
            string destination = Required((string?)body["destination"], "destination");
            var actor = Actor(req);
            var entry = parent.Files.Move(steward.StewardID, source, destination, actor);
            StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.Move,
                [entry.Path], entry.Size, previousPath: source);
            NotifyCommander(steward, $"Klives moved shared path '{source}' to '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task CopyFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Steward steward = RequireWritableSteward(Required((string?)body["stewardID"], "stewardID"));
            string source = Required((string?)body["path"], "path");
            string destination = Required((string?)body["destination"], "destination");
            var actor = Actor(req);
            var entry = parent.Files.Copy(steward.StewardID, source, destination, actor);
            long bytes = AffectedBytes(steward.StewardID, entry.Path);
            StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.Copy,
                [entry.Path], bytes, previousPath: source);
            NotifyCommander(steward, $"Klives copied shared path '{source}' to '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private async Task DeleteFile(ApiUserRequest req)
    {
        try
        {
            JObject body = Body(req);
            Steward steward = RequireWritableSteward(Required((string?)body["stewardID"], "stewardID"));
            string path = Required((string?)body["path"], "path");
            bool recursive = (bool?)body["recursive"] ?? false;
            var actor = Actor(req);
            bool deleted = parent.Files.Delete(steward.StewardID, path, recursive, actor);
            if (deleted)
            {
                StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.Delete, [path]);
                NotifyCommander(steward, $"Klives deleted shared path '{path}'.");
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
            Steward steward = RequireWritableSteward(Required((string?)body["stewardID"], "stewardID"));
            string path = Required((string?)body["path"], "path");
            bool? important = body.TryGetValue("important", StringComparison.OrdinalIgnoreCase, out JToken? importantToken)
                && importantToken.Type != JTokenType.Null ? (bool?)importantToken : null;
            string? description = body.TryGetValue("description", StringComparison.OrdinalIgnoreCase, out JToken? descriptionToken)
                ? (descriptionToken.Type == JTokenType.Null ? "" : (string?)descriptionToken ?? "") : null;
            if (!important.HasValue && description == null)
                throw new StewardFileException("Provide important and/or description.");
            var actor = Actor(req);
            var entry = parent.Files.SetMetadata(steward.StewardID, path, important, description, actor);
            StewardFileTimeline.Append(parent.EventLog, steward.StewardID, actor, StewardFileOperation.Metadata, [entry.Path]);
            NotifyCommander(steward, $"Klives updated shared-file metadata for '{entry.Path}'.");
            await Ok(req, entry);
        }
        catch (Exception ex) { await Error(req, ex); }
    }

    private Steward RequireSteward(string stewardID) => parent.Store.GetSteward(stewardID)
        ?? throw new FileNotFoundException("Unknown stewardID.", stewardID);

    private Steward RequireWritableSteward(string stewardID)
    {
        Steward steward = RequireSteward(stewardID);
        if (steward.Status is StewardStatus.Completed or StewardStatus.Archived)
            throw new StewardFilesReadOnlyException();
        return steward;
    }

    private StewardFileUploadSession RequireOwnedSession(string sessionID, StewardFileActor actor)
    {
        var session = parent.Files.GetUploadSession(sessionID)
            ?? throw new FileNotFoundException("Unknown upload session.", sessionID);
        if (session.Actor.Type != actor.Type || !string.Equals(session.Actor.ID, actor.ID, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Upload session belongs to another user.");
        return session;
    }

    private static StewardFileActor Actor(ApiUserRequest req) => new(
        StewardFileActorType.User,
        req.user?.UserID ?? "klives",
        req.user?.Name ?? "Klives");

    private void NotifyCommander(Steward steward, string text)
    {
        if (steward.Status is StewardStatus.Active or StewardStatus.Planning)
            parent.CommanderRunner.Steer(steward, text);
    }

    private long AffectedBytes(string stewardID, string path)
    {
        var root = parent.Files.Stat(stewardID, path, reconcile: false);
        if (root?.Kind == StewardFileKind.File) return root.Size;
        long totalBytes = 0;
        int offset = 0;
        while (true)
        {
            var page = parent.Files.List(stewardID, new StewardFileListRequest
            {
                Directory = path, Recursive = true, Limit = 500, Offset = offset, Reconcile = false,
            });
            totalBytes += page.Entries.Where(x => x.Kind == StewardFileKind.File).Sum(x => x.Size);
            offset += page.Entries.Count;
            if (offset >= page.Total || page.Entries.Count == 0) return totalBytes;
        }
    }

    private static JObject Body(ApiUserRequest req)
    {
        try { return JObject.Parse(string.IsNullOrWhiteSpace(req.userMessageContent) ? "{}" : req.userMessageContent); }
        catch (JsonException ex) { throw new StewardFileException("Request body must be a JSON object.", ex); }
    }

    private static string Required(string? value, string name)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? throw new StewardFileException($"{name} required.") : value;
    }

    private static StewardFileConflictPolicy ParsePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StewardFileConflictPolicy.Fail;
        return Enum.TryParse(value, true, out StewardFileConflictPolicy parsed)
            ? parsed : throw new StewardFileException("Unknown conflictPolicy.");
    }

    private static IReadOnlyDictionary<string, StewardFileConflictPolicy>? ParsePathPolicies(JObject? value)
    {
        if (value == null) return null;
        var result = new Dictionary<string, StewardFileConflictPolicy>(StringComparer.OrdinalIgnoreCase);
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
        throw new StewardFileException("Invalid cursor.");
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
        throw new StewardFileException("Invalid audit cursor.");
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
            case StewardFilesReadOnlyException:
                status = HttpStatusCode.Conflict;
                message = "Completed and archived Stewards are browse/download-only.";
                break;
            case StewardFileConflictException conflict:
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
            case StewardFileException or ArgumentException or JsonException:
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
                _ = parent.ServiceLogError(ex, "Demiurge: shared-file API failed");
                break;
        }
        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = message }, JsonSettings), code: status);
    }

    private sealed class StewardFilesReadOnlyException : StewardFileException
    {
        public StewardFilesReadOnlyException() : base("This Steward's shared filesystem is read-only.") { }
    }
}
