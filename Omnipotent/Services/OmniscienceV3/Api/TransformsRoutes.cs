using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Modules.Transforms;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>A bundle as a document. <c>format</c> markdown or json; the page offers <c>content</c> as a download named <c>fileName</c>.</summary>
    [ApiContract]
    public sealed record BundleExportDto(string BundleId, string Format, string FileName, string Content);

    [ApiContract]
    public sealed record BundleDeleteRequest(string BundleId);

    [ApiContract]
    public sealed record BundleDeletedDto(string BundleId, bool Deleted);

    /// <summary>Evidence bundles (X38) under <c>/omniscience/v3/bundles</c>. A bundle is made and added to by the tactic
    /// <c>x38.evidence_bundle</c>; these routes list, export and delete them.</summary>
    public sealed class TransformsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public TransformsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("bundles", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, new BundleListDto(Transforms.Bundles())), ct).ConfigureAwait(false);

            await Get("bundles/export", OmniscienceV3Perms.DataExport, req =>
            {
                var q = ApiQuery.Of(req);
                string id = q.Required("id", 80), format = (q.Str("format", 16) ?? "markdown").ToLowerInvariant();
                try
                {
                    string content = Transforms.ExportBundle(id, format);
                    return ApiResponses.OkAsync(req, new BundleExportDto(id, format, "bundle-" + id + (format == "json" ? ".json" : ".md"), content));
                }
                catch (KeyNotFoundException ex) { throw ApiException.NotFound(ex.Message); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await registrar.RouteAsync(V3Routes.Prefix + "/bundles/delete", HttpMethod.Post, OmnisciencePerms.PersonsRead, async req =>
            {
                var body = ApiBody.Read<BundleDeleteRequest>(req);
                bool deleted = await Transforms.DeleteBundleAsync(body.BundleId ?? "", ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!deleted) throw ApiException.NotFound($"No bundle '{body.BundleId}'.");
                await ApiResponses.OkAsync(req, new BundleDeletedDto(body.BundleId!, true)).ConfigureAwait(false);
            }, RouteRegistrar.DefaultMaxBodyBytes, ct).ConfigureAwait(false);
        }

        private TransformsModule Transforms =>
            host.Require().Host.Modules.OfType<TransformsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The transforms module is not installed in this build.");

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
