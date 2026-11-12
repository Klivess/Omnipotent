using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Briefs;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>Asks for a digest of <c>all</c>, a <c>guild</c>, a <c>conversation</c> or a <c>person</c>. Without <c>fromMs</c> it starts where Klives last looked.</summary>
    [ApiContract]
    public sealed record DigestRunRequest(string ScopeKind, string? ScopeId = null, long? FromMs = null, long? ToMs = null);

    /// <summary>Klives has read a scope up to <c>atMs</c> (default: now).</summary>
    [ApiContract]
    public sealed record DigestSeenRequest(string ScopeKind, string? ScopeId = null, long? AtMs = null);

    [ApiContract]
    public sealed record DigestSeenDto(string ScopeKind, string? ScopeId, bool Seen);

    [ApiContract]
    public sealed record DigestSubscriptionListDto(IReadOnlyList<DigestSubscriptionDto> Subscriptions);

    [ApiContract]
    public sealed record DigestSubscriptionSaveRequest(string Name, string ScopeKind, string Cadence, string At, string? SubscriptionId = null, string? ScopeId = null, int? Weekday = null,
        bool Enabled = true);

    [ApiContract]
    public sealed record DigestSubscriptionDeleteRequest(string SubscriptionId);

    [ApiContract]
    public sealed record DigestSubscriptionDeletedDto(string SubscriptionId, bool Deleted);

    [ApiContract]
    public sealed record PersonBriefRequest(string PersonId);

    /// <summary>
    /// Digest and brief routes under <c>/omniscience/v3/</c>. A digest or a brief is written by a job: the request answers
    /// 202 with the queued item, the finished one arrives as read model <c>briefs.digest</c> / <c>briefs.person</c> (and on
    /// the live stream as <c>readmodel.updated</c>), and <c>digests/item</c> or <c>briefs/person</c> reads it.
    /// </summary>
    public sealed class BriefsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public BriefsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("digests", OmnisciencePerms.MessagesRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Briefs.Digests(q.Str("scopeKind", 16)?.ToLowerInvariant(), q.Str("scopeId", 200), q.Str("cursor", 200), q.Int("limit", 20, 1, 100))));
            }, ct).ConfigureAwait(false);

            await Get("digests/item", OmnisciencePerms.MessagesRead, req =>
            {
                string id = ApiQuery.Of(req).Required("id", 80);
                return ApiResponses.OkAsync(req, Briefs.Digest(id) ?? throw ApiException.NotFound($"No digest '{id}'."));
            }, ct).ConfigureAwait(false);

            await Post("digests/run", OmnisciencePerms.AnalysisRun, async req =>
            {
                var body = ApiBody.Read<DigestRunRequest>(req);
                var digest = await GuardAsync(() => Briefs.RequestDigestAsync(body.ScopeKind, body.ScopeId, body.FromMs, body.ToMs, ApiBody.Actor(req), "on_demand", null, interactive: true, host.Stopping))
                    .ConfigureAwait(false);
                await ApiResponses.OkAsync(req, digest, HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("digests/seen", OmnisciencePerms.MessagesRead, async req =>
            {
                var body = ApiBody.Read<DigestSeenRequest>(req);
                await GuardAsync(async () =>
                {
                    await Briefs.MarkSeenAsync(body.ScopeKind, body.ScopeId, body.AtMs, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new DigestSeenDto(body.ScopeKind, body.ScopeId, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("digests/subscriptions", OmnisciencePerms.MessagesRead, req => ApiResponses.OkAsync(req, new DigestSubscriptionListDto(Briefs.Subscriptions())), ct).ConfigureAwait(false);

            await Post("digests/subscriptions/save", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<DigestSubscriptionSaveRequest>(req);
                var saved = await GuardAsync(() => Briefs.SaveSubscriptionAsync(body.SubscriptionId, body.Name, body.ScopeKind, body.ScopeId, body.Cadence, body.At, body.Weekday, body.Enabled,
                    ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, saved).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("digests/subscriptions/delete", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<DigestSubscriptionDeleteRequest>(req);
                bool deleted = await Briefs.DeleteSubscriptionAsync(body.SubscriptionId ?? "", ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!deleted) throw ApiException.NotFound($"No digest subscription '{body.SubscriptionId}'.");
                await ApiResponses.OkAsync(req, new DigestSubscriptionDeletedDto(body.SubscriptionId!, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("briefs/person", OmnisciencePerms.PersonsRead, req =>
            {
                string person = ApiQuery.Of(req).Required("personId", 200);
                return ApiResponses.OkAsync(req, Guard(() => Briefs.PersonBrief(person)) ?? throw ApiException.NotFound("No brief has been written for this person yet."));
            }, ct).ConfigureAwait(false);

            await Post("briefs/person", OmnisciencePerms.AnalysisRun, async req =>
            {
                var body = ApiBody.Read<PersonBriefRequest>(req);
                var brief = await GuardAsync(() => Briefs.RequestPersonBriefAsync(body.PersonId ?? "", ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, brief, HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        private BriefsModule Briefs =>
            host.Require().Host.Modules.OfType<BriefsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The digests and briefs module is not installed in this build.");

        private static ApiException Translate(Exception ex) => ex switch
        {
            TacticRefusedException refused => new ApiException(HttpStatusCode.UnprocessableEntity, "refused", refused.Message),
            KeyNotFoundException missing => ApiException.NotFound(missing.Message),
            _ => ApiException.BadRequest(ex.Message),
        };

        private static T Guard<T>(Func<T> work)
        {
            try { return work(); }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or TacticRefusedException) { throw Translate(ex); }
        }

        private static async Task<T> GuardAsync<T>(Func<Task<T>> work)
        {
            try { return await work().ConfigureAwait(false); }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or TacticRefusedException) { throw Translate(ex); }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
