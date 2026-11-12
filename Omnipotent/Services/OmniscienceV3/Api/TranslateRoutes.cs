using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Translate;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>At most 200 message ids.</summary>
    [ApiContract]
    public sealed record TranslateRequest(IReadOnlyList<string> MessageIds);

    /// <summary>
    /// Translation routes under <c>/omniscience/v3/translate/</c> (N19): translate messages now (cached per message
    /// version; also tactic X33), search the translations, and see the background queue.
    /// </summary>
    public sealed class TranslateRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public TranslateRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await registrar.RouteAsync(V3Routes.Prefix + "/translate/messages", HttpMethod.Post, OmnisciencePerms.MessagesRead, async req =>
            {
                var body = ApiBody.Read<TranslateRequest>(req);
                try
                {
                    await ApiResponses.OkAsync(req, await Module.TranslateAsync(body.MessageIds ?? Array.Empty<string>(), null, host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex) { throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
                catch (JobFailureException ex) { throw ApiException.Unavailable(ApiErrors.Unavailable, ex.Message); }
            }, RouteRegistrar.DefaultMaxBodyBytes, ct).ConfigureAwait(false);

            await Get("translate/search", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                try { return ApiResponses.OkAsync(req, Module.Search(q.Required("q", 200), q.Int("limit", 50, 1, 500))); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("translate/status", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, Module.Status()), ct).ConfigureAwait(false);
        }

        private TranslateModule Module =>
            host.Require().Host.Modules.OfType<TranslateModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The translation module is not installed in this build.");

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
