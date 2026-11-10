using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Ingest.Imports;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    [ApiContract]
    public sealed record ImportFormatDto(string Id, string Title, bool NeedsZone);

    /// <summary>A file or folder waiting in the import inbox.</summary>
    [ApiContract]
    public sealed record ImportInboxEntryDto(string Name, bool IsFolder, long Bytes, long ModifiedAtMs);

    /// <summary>One export the ledger knows, by content hash. <see cref="CountsJson"/> is the last run's <see cref="ImportResult"/>.</summary>
    [ApiContract]
    public sealed record ImportLedgerEntryDto(string ImportId, string Format, string Sha256, string? Zone, string State, long CreatedAtMs, long? CommittedAtMs,
        string? Error, string? CountsJson);

    [ApiContract]
    public sealed record ImportListDto(string InboxPath, IReadOnlyList<ImportInboxEntryDto> Inbox, IReadOnlyList<ImportLedgerEntryDto> Ledger, IReadOnlyList<ImportFormatDto> Formats);

    /// <summary>The job doing the work; its result (an <see cref="ImportPreview"/> or <see cref="ImportResult"/>) is the job's <c>resultJson</c>.</summary>
    [ApiContract]
    public sealed record ImportJobStartedDto(long JobId);

    /// <summary>Routes of the ingest workstream: imports today; sources, media and faces as they land.</summary>
    public sealed class IngestRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public IngestRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        private ImportModule Imports => host.Require().Host.Modules.OfType<ImportModule>().FirstOrDefault()
                                        ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The import module is not part of this build.");

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("imports", OmnisciencePerms.SourcesView, req =>
            {
                var module = Imports;
                var inbox = new List<ImportInboxEntryDto>();
                var dir = new DirectoryInfo(module.InboxPath);
                if (dir.Exists)
                {
                    foreach (var entry in dir.EnumerateFileSystemInfos().OrderByDescending(e => e.LastWriteTimeUtc).Take(500))
                    {
                        bool folder = entry is DirectoryInfo;
                        // A folder's size is not summed here: an unpacked export can hold a hundred thousand files.
                        inbox.Add(new ImportInboxEntryDto(entry.Name, folder, folder ? 0 : ((FileInfo)entry).Length, new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()));
                    }
                }
                List<ImportLedgerEntryDto> ledger;
                using (var c = host.Require().Stores.Corpus.OpenRead())
                    ledger = c.Query(null, """
                        SELECT import_id, format, file_sha256, zone, state, created_at_ms, committed_at_ms, error, counts_json
                        FROM import_ledger ORDER BY created_at_ms DESC LIMIT 500
                        """, r => new ImportLedgerEntryDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4), r.GetInt64(5),
                        r.GetInt64OrNull(6), r.GetStringOrNull(7), r.GetStringOrNull(8)));
                var formats = ImportService.Formats.Select(f => new ImportFormatDto(f.Id, f.Title, f.NeedsZone)).ToList();
                return ApiResponses.OkAsync(req, new ImportListDto(module.InboxPath, inbox, ledger, formats));
            }, ct).ConfigureAwait(false);

            await Post("imports/preview", OmnisciencePerms.SourcesManage, req => StartAsync(req, ImportModule.PreviewKind), ct).ConfigureAwait(false);
            await Post("imports/commit", OmnisciencePerms.SourcesManage, req => StartAsync(req, ImportModule.CommitKind), ct).ConfigureAwait(false);
        }

        private async Task StartAsync(UserRequest req, string kind)
        {
            var body = ApiBody.Read<ImportJobPayload>(req);
            try
            {
                long jobId = await Imports.EnqueueAsync(kind, body, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ImportJobStartedDto(jobId), HttpStatusCode.Accepted).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                throw ApiException.NotFound(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                throw ApiException.BadRequest(ex.Message);
            }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
