using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Modules.Monitors;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    [ApiContract]
    public sealed record MonitorRuleRequest(string WatchId);

    [ApiContract]
    public sealed record MonitorRuleEnabledRequest(string WatchId, bool Enabled);

    [ApiContract]
    public sealed record MonitorRuleChangedDto(string WatchId, bool Changed);

    /// <summary>
    /// Monitor routes under <c>/omniscience/v3/monitors/</c> (X42, X43, X46, X47, X51): the watches, what they found, and the
    /// health of Omniscience's own data. Watches are made with their tactics; here they are listed, switched and removed.
    /// </summary>
    public sealed class MonitorsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public MonitorsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("monitors/rules", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, Module.Rules(ApiQuery.Of(req).Str("kind", 40))), ct).ConfigureAwait(false);

            await Get("monitors/events", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Module.Events(q.Str("kind", 200), q.Str("ruleId", 40), q.Int("limit", 50, 1, 500)));
            }, ct).ConfigureAwait(false);

            await Get("monitors/topic", OmnisciencePerms.PersonsRead, req =>
            {
                string id = ApiQuery.Of(req).Required("watchId", 40);
                try { return ApiResponses.OkAsync(req, Module.TopicStatus(id)); }
                catch (KeyNotFoundException ex) { throw ApiException.NotFound(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("monitors/health", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, Module.Health()), ct).ConfigureAwait(false);

            await Post("monitors/rules/delete", async req =>
            {
                var body = ApiBody.Read<MonitorRuleRequest>(req);
                bool changed;
                try { changed = await Module.DeleteRuleAsync(body.WatchId ?? "", ApiBody.Actor(req), host.Stopping).ConfigureAwait(false); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
                if (!changed) throw ApiException.NotFound($"No watch '{body.WatchId}'.");
                await ApiResponses.OkAsync(req, new MonitorRuleChangedDto(body.WatchId!, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("monitors/rules/enabled", async req =>
            {
                var body = ApiBody.Read<MonitorRuleEnabledRequest>(req);
                bool changed = await Module.SetRuleEnabledAsync(body.WatchId ?? "", body.Enabled, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!changed) throw ApiException.NotFound($"No watch '{body.WatchId}'.");
                await ApiResponses.OkAsync(req, new MonitorRuleChangedDto(body.WatchId!, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        private MonitorsModule Module =>
            host.Require().Host.Modules.OfType<MonitorsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The monitors module is not installed in this build.");

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, OmniscienceV3Perms.AlertsManage, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
