using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Modules.DataControls;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary><c>level</c>: <c>derived</c> (forget what was worked out about them, keep their messages; can be undone) or
    /// <c>erase</c> (remove everything; cannot be undone).</summary>
    [ApiContract]
    public sealed record ForgetPreviewRequest(string PersonId, bool IncludeDirectChats = true, string Level = "erase");

    [ApiContract]
    public sealed record AnalysisOnRequest(string PersonId);

    /// <summary><c>changed</c> is false when analysis was not off for them.</summary>
    [ApiContract]
    public sealed record AnalysisOnDto(string PersonId, bool Changed);

    [ApiContract]
    public sealed record AnalysisOffListDto(IReadOnlyList<AnalysisOffPersonDto> People);

    /// <summary>Confirms a forget with the token of its preview. There is no way back.</summary>
    [ApiContract]
    public sealed record ForgetConfirmRequest(string PersonId, string ConfirmToken);

    [ApiContract]
    public sealed record BackupRequestedDto(long JobId);

    [ApiContract]
    public sealed record BackupRestoreRequest(string BackupId);

    [ApiContract]
    public sealed record BackupKeyExportRequest(string Passphrase);

    /// <summary>The backup key wrapped with the passphrase. Keep it somewhere safe: with the passphrase it opens every backup.</summary>
    [ApiContract]
    public sealed record BackupKeyDto(string KeyId, string? Exported);

    [ApiContract]
    public sealed record BackupKeyImportRequest(string Exported, string Passphrase);

    /// <summary><c>format</c>: json or markdown.</summary>
    [ApiContract]
    public sealed record PersonExportRequest(string PersonId, string Format = "json");

    /// <summary>
    /// Data-control routes under <c>/omniscience/v3/data/</c> (N20). Forgetting is two calls: a preview that says what would
    /// go and returns a token, then a confirmation with that token; it has two levels (see <see cref="ForgetPreviewRequest"/>).
    /// <c>data/analysis</c> lists the people whose analysis is switched off and switches it back on. The export answers with
    /// the file itself.
    /// </summary>
    public sealed class DataControlsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public DataControlsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Post("data/forget/preview", OmniscienceV3Perms.EngineManage, req =>
            {
                var body = ApiBody.Read<ForgetPreviewRequest>(req);
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("omniscience-v3: forget preview");
                return ApiResponses.OkAsync(req, Guard(() => Module.PreviewForget(body.PersonId ?? "", body.IncludeDirectChats, body.Level ?? DataControlsModule.LevelErase)));
            }, ct).ConfigureAwait(false);

            await Post("data/forget/confirm", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<ForgetConfirmRequest>(req);
                var run = await GuardAsync(() => Module.StartForgetAsync(body.PersonId ?? "", body.ConfirmToken ?? "", ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, run, HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("data/forget/run", OmniscienceV3Perms.EngineManage, req =>
            {
                string id = ApiQuery.Of(req).Required("id", 80);
                return ApiResponses.OkAsync(req, Module.ForgetRun(id) ?? throw ApiException.NotFound($"No forget run '{id}'."));
            }, ct).ConfigureAwait(false);

            await Get("data/analysis", OmniscienceV3Perms.EngineManage, req => ApiResponses.OkAsync(req, new AnalysisOffListDto(Module.AnalysisOffList())), ct).ConfigureAwait(false);

            await Post("data/analysis/on", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<AnalysisOnRequest>(req);
                bool changed = await GuardAsync(() => Module.SetAnalysisOnAsync(body.PersonId ?? "", ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new AnalysisOnDto(body.PersonId ?? "", changed)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("data/retention", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, Module.Retention()), ct).ConfigureAwait(false);

            await Get("data/backups", OmniscienceV3Perms.EngineManage, req => ApiResponses.OkAsync(req, Guard(() => Module.Backups())), ct).ConfigureAwait(false);

            await Post("data/backups/run", OmniscienceV3Perms.EngineManage, async req =>
            {
                long jobId = await Module.RequestBackupAsync(host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new BackupRequestedDto(jobId), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("data/backups/restore", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<BackupRestoreRequest>(req);
                await ApiResponses.OkAsync(req, await GuardAsync(() => Module.RestoreAsync(body.BackupId ?? "", host.Stopping)).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("data/backups/export-key", OmniscienceV3Perms.EngineManage, req =>
            {
                var body = ApiBody.Read<BackupKeyExportRequest>(req);
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("omniscience-v3: backup key");
                string exported = Guard(() => Module.ExportKey(body.Passphrase ?? ""));
                return ApiResponses.OkAsync(req, new BackupKeyDto(exported.Split(':')[1], exported));
            }, ct).ConfigureAwait(false);

            await Post("data/backups/import-key", OmniscienceV3Perms.EngineManage, req =>
            {
                var body = ApiBody.Read<BackupKeyImportRequest>(req);
                return ApiResponses.OkAsync(req, new BackupKeyDto(Guard(() => Module.ImportKey(body.Exported ?? "", body.Passphrase ?? "")), null));
            }, ct).ConfigureAwait(false);

            await Post("data/export/person", OmniscienceV3Perms.DataExport, async req =>
            {
                var body = ApiBody.Read<PersonExportRequest>(req);
                string format = (body.Format ?? "json").Trim().ToLowerInvariant();
                string path = Path.Combine(Path.GetTempPath(), "omniv3-person-" + Guid.NewGuid().ToString("N") + (format == "markdown" ? ".md" : ".json"));
                try
                {
                    await GuardAsync(async () =>
                    {
                        await Module.ExportPersonAsync(body.PersonId ?? "", format, path, host.Stopping).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("omniscience-v3: person export");
                    var info = new FileInfo(path);
                    var headers = new System.Collections.Specialized.NameValueCollection
                    {
                        ["Content-Disposition"] = $"attachment; filename=\"omniscience-person.{(format == "markdown" ? "md" : "json")}\"", ["Cache-Control"] = "no-store",
                    };
                    var stream = req.PrepareStreamResponse(format == "markdown" ? "text/markdown; charset=utf-8" : "application/json; charset=utf-8", info.Length, HttpStatusCode.OK, headers);
                    await using (var file = File.OpenRead(path)) await file.CopyToAsync(stream, host.Stopping).ConfigureAwait(false);
                    await stream.FlushAsync(host.Stopping).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(path); }
                    catch (IOException ex) { System.Diagnostics.Debug.WriteLine("person export temp file not deleted: " + ex.Message); }
                }
            }, ct).ConfigureAwait(false);
        }

        private DataControlsModule Module =>
            host.Require().Host.Modules.OfType<DataControlsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The data-controls module is not installed in this build.");

        private static ApiException Translate(Exception ex) => ex switch
        {
            KeyNotFoundException missing => ApiException.NotFound(missing.Message),
            InvalidDataException damaged => ApiException.Conflict(damaged.Message),
            _ => ApiException.BadRequest(ex.Message),
        };

        private static T Guard<T>(Func<T> work)
        {
            try { return work(); }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidDataException) { throw Translate(ex); }
        }

        private static async Task<T> GuardAsync<T>(Func<Task<T>> work)
        {
            try { return await work().ConfigureAwait(false); }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidDataException) { throw Translate(ex); }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
