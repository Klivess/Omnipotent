using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Insights;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    [ApiContract]
    public sealed record InsightsRefreshDto(long JobId);

    /// <summary>
    /// Insights routes under <c>/omniscience/v3/insights/</c>: relationship dynamics and communities (N12). The communities
    /// are also read model <c>insights.communities</c>; a person's relationships and a single pair are read from the tables
    /// the nightly job maintains.
    /// </summary>
    public sealed class InsightsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public InsightsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("insights/communities", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, Insights.Communities()), ct).ConfigureAwait(false);

            await Get("insights/person", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Insights.PersonRelationships(q.Required("personId", 200), q.Int("limit", 25, 1, 100))));
            }, ct).ConfigureAwait(false);

            await Get("insights/pair", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                string a = q.Required("a", 200), b = q.Required("b", 200);
                return ApiResponses.OkAsync(req, Guard(() => Insights.Pair(a, b)) ?? throw ApiException.NotFound("These two have too few interactions to be tracked as a relationship."));
            }, ct).ConfigureAwait(false);

            await registrar.RouteAsync(V3Routes.Prefix + "/insights/refresh", HttpMethod.Post, OmniscienceV3Perms.EngineManage, async req =>
            {
                long jobId = await Insights.RefreshAsync(host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new InsightsRefreshDto(jobId), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, RouteRegistrar.DefaultMaxBodyBytes, ct).ConfigureAwait(false);
        }

        private InsightsModule Insights =>
            host.Require().Host.Modules.OfType<InsightsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The insights module is not installed in this build.");

        private static T Guard<T>(Func<T> work)
        {
            try { return work(); }
            catch (TacticRefusedException ex) { throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message); }
            catch (KeyNotFoundException ex) { throw ApiException.NotFound(ex.Message); }
            catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
