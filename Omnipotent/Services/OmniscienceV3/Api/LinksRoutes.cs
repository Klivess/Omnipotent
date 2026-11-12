using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Links;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// Link routes under <c>/omniscience/v3/links/</c> (N15): a person's media diet, a search over what was shared
    /// ("what did X share about Y"), and the tracker of one address or site (also tactic X23).
    /// </summary>
    public sealed class LinksRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public LinksRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("links/person", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Module.PersonLinks(q.Required("personId", 200), q.Int("days", 90, 1, 3650), q.Int("limit", 30, 1, 200))));
            }, ct).ConfigureAwait(false);

            await Get("links/search", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                long? Ms(string name) => long.TryParse(q.Str(name, 20), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : null;
                return ApiResponses.OkAsync(req, Guard(() => Module.Search(q.Str("q", 200), q.Str("personId", 200), q.Str("site", 200), q.Str("kind", 20), q.Str("topic", 20), Ms("from"), Ms("to"),
                    q.Int("limit", 50, 1, 500))));
            }, ct).ConfigureAwait(false);

            await Get("links/track", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Module.Track(q.Str("url", 2000), q.Str("site", 200), limit: q.Int("limit", 200, 1, 1000))));
            }, ct).ConfigureAwait(false);
        }

        private LinksModule Module =>
            host.Require().Host.Modules.OfType<LinksModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The links module is not installed in this build.");

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
