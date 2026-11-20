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

            // Filters: the shared ones (ScopeFilter: who, platform, server, conversation, place, when), and any of several
            // sites, kinds and topics. The one-value "personId", "site", "kind" and "topic" still work.
            await Get("links/search", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                IReadOnlyList<string> Many(string list, string single) => q.Str(single, 200) is { } one ? q.List(list, ScopeFilter.MaxValues).Append(one).ToList() : q.List(list, ScopeFilter.MaxValues);
                var filter = ScopeFilter.Parse(q, singlePerson: new[] { "personId" });
                return ApiResponses.OkAsync(req, Guard(() => Module.Search(new LinkSearchQuery(q.Str("q", 200), q.Int("limit", 50, 1, 500))
                {
                    Filter = filter, Sites = Many("sites", "site"), Kinds = Many("kinds", "kind"), Topics = Many("topics", "topic"),
                })));
            }, ct).ConfigureAwait(false);

            await Get("links/track", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                var filter = ScopeFilter.Parse(q);
                return ApiResponses.OkAsync(req, Guard(() => Module.Track(q.Str("url", 2000), q.Str("site", 200), limit: q.Int("limit", 200, 1, 1000), scope: filter)));
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
