using System.Net;
using System.Text.Json;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// Read-model serving and the Explore API. Read models are one indexed row per request and the only thing page
    /// routes read; each model declares the permission that gates it, checked here on top of the route's own key.
    /// Explore validates every query against the declared datasets before the engine (the analytics workstream's
    /// DuckDB mirror) sees it; until that module is installed the query routes answer 503 with that reason.
    /// </summary>
    public sealed class DataRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public DataRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("rm/catalog", OmnisciencePerms.OverviewView, req =>
                ApiResponses.OkAsync(req, new ReadModelCatalogDto(Descriptors().ToList())), ct).ConfigureAwait(false);

            await Get("rm", OmnisciencePerms.OverviewView, req =>
            {
                var q = ApiQuery.Of(req);
                string model = q.Required("model", 128), key = q.Required("key", 512);
                var descriptor = Descriptors().FirstOrDefault(d => d.Id == model) ?? throw ApiException.NotFound($"No read model '{model}'.");
                RequirePermission(req, descriptor.RequiredPermission, descriptor.Title);
                var rt = host.Require();
                var row = rt.Host.ReadModels.Get(model, key)
                          ?? throw new ApiException(HttpStatusCode.NotFound, "not_built", $"'{descriptor.Title}' has not been built for '{key}' yet; it is computed in the background.");
                // Defence in depth: a model whose body depends on policy is re-filtered with the current policy when served.
                foreach (var filter in rt.Host.Modules.OfType<IReadModelServingFilter>().Where(f => f.Handles(model)))
                    row = row with { BodyJson = filter.Filter(row, rt.Host.Policy, rt.Clock.NowMs) };
                return ServeAsync(req, row);
            }, ct).ConfigureAwait(false);

            await Get("explore/datasets", OmnisciencePerms.OverviewView, req =>
            {
                var datasets = Datasets().Where(d => May(req, d.RequiredPermission)).ToList();
                var dims = datasets.SelectMany(d => d.Dimensions.Select(b => b.DimensionId)).Distinct(StringComparer.Ordinal)
                    .Select(DimensionCatalog.Get).ToList();
                return ApiResponses.OkAsync(req, new DatasetCatalogDto(datasets, dims));
            }, ct).ConfigureAwait(false);

            await Post("explore/query", OmnisciencePerms.OverviewView, async req =>
            {
                var query = ApiBody.Read<ExploreQuery>(req);
                var datasets = Datasets().ToList();
                if (ExploreValidation.Validate(query, datasets) is { } problem) throw ApiException.BadRequest(problem);
                var ds = datasets.First(d => d.Id == query.Dataset);
                RequirePermission(req, ds.RequiredPermission, ds.Title);
                var engine = host.Require().Explore ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The Explore engine is not installed in this build yet.");
                var result = await engine.QueryAsync(query, permission => May(req, permission), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, result).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("explore/records", OmnisciencePerms.OverviewView, async req =>
            {
                var request = ApiBody.Read<ExploreRecordsRequest>(req);
                if (string.IsNullOrWhiteSpace(request.DrillToken)) throw ApiException.BadRequest("'drillToken' is required.");
                if (request.Limit is < 1 or > 1000) throw ApiException.BadRequest("Limit must be between 1 and 1000.");
                var engine = host.Require().Explore ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The Explore engine is not installed in this build yet.");
                var page = await engine.RecordsAsync(request, permission => May(req, permission), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, page).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        private IEnumerable<ReadModelDescriptor> Descriptors() => host.Require().Host.Modules.SelectMany(m => m.Manifest.ReadModels);

        private IEnumerable<DatasetDescriptor> Datasets() => host.Require().Host.Modules.SelectMany(m => m.Manifest.Datasets);

        private static bool May(UserRequest req, string permissionKey) =>
            PermissionCatalog.Get(permissionKey) is { } p && req.Can(p);

        private static void RequirePermission(UserRequest req, string permissionKey, string what)
        {
            var permission = PermissionCatalog.Get(permissionKey) ?? throw new InvalidOperationException($"'{what}' names an unknown permission '{permissionKey}'.");
            if (!req.Can(permission)) throw ApiException.Forbidden($"'{what}' needs the '{permission.Title}' permission.");
        }

        /// <summary>
        /// Serves a row without re-serialising its body. Unlike other v3 routes this response is cacheable by KliveAPI:
        /// the read noted the row's dependency key (bumped by every write of it) and the policy key.
        /// </summary>
        internal static string Envelope(ReadModelRow row) =>
            "{\"model\":" + JsonSerializer.Serialize(row.ModelId) + ",\"key\":" + JsonSerializer.Serialize(row.Key) +
            ",\"version\":" + row.Version + ",\"schemaVersion\":" + row.SchemaVersion + ",\"asOfMs\":" + row.AsOfMs +
            ",\"sourceSeq\":" + row.SourceSeq + ",\"computedAtMs\":" + row.ComputedAtMs + ",\"body\":" + row.BodyJson + "}";

        private static Task ServeAsync(UserRequest req, ReadModelRow row) =>
            ApiResponses.JsonAsync(req, Envelope(row));

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
