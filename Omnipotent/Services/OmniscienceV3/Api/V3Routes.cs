using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// The engine routes under <c>/omniscience/v3/</c> (Phase 1). Conventions every v3 route follows:
    /// <list type="bullet">
    /// <item>Registered once, typed, with an explicit permission; answers an honest 503 until the engine is ready.</item>
    /// <item>Reads are indexed lookups of maintained state; nothing heavy is computed on request.</item>
    /// <item>Lists are keyset-paged (<c>cursor</c> → <c>nextCursor</c>) and every filterable field is a query parameter.</item>
    /// <item>Instants in and out are UTC epoch ms (ISO-8601 with an offset is accepted on input).</item>
    /// <item>Commands are POSTs with a JSON body; each is audited in operator.db with the caller.</item>
    /// </list>
    /// </summary>
    public sealed class V3Routes
    {
        public const string Prefix = "/omniscience/v3";

        private readonly V3Host host;
        private readonly RouteRegistrar registrar;
        private readonly EngineQueries queries;

        public V3Routes(V3Host host, RouteRegistrar registrar, EngineQueries queries)
        {
            this.host = host;
            this.registrar = registrar;
            this.queries = queries;
        }

        /// <summary>
        /// Which stream topics a caller may receive. The socket itself needs overview access; raw message content
        /// and person-level intelligence need their own keys.
        /// </summary>
        public static Func<string, bool> TopicFilterFor(KMProfileManager.KMProfile? user) => topic =>
        {
            if (topic.StartsWith("live.", StringComparison.Ordinal)) return AccessEvaluator.Can(user, OmnisciencePerms.MessagesRead);
            if (topic.StartsWith("alert", StringComparison.Ordinal) || topic.StartsWith("tactic.", StringComparison.Ordinal)
                || topic.StartsWith("inquiry.", StringComparison.Ordinal) || topic.StartsWith("person.", StringComparison.Ordinal))
                return AccessEvaluator.Can(user, OmnisciencePerms.PersonsRead);
            if (topic.StartsWith("face.", StringComparison.Ordinal)) return AccessEvaluator.Can(user, OmniscienceV3Perms.FacesRead);
            if (topic.StartsWith("replica.", StringComparison.Ordinal)) return AccessEvaluator.Can(user, OmnisciencePerms.ReplicaRead);
            return AccessEvaluator.Can(user, OmnisciencePerms.OverviewView);
        };

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("status", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, queries.Status()), ct).ConfigureAwait(false);

            await Get("modules", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, queries.Modules()), ct).ConfigureAwait(false);
            await Post("modules/enabled", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<ModuleEnabledRequest>(req);
                await ApiResponses.OkAsync(req, await queries.SetModuleEnabledAsync(body, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("jobs", OmnisciencePerms.OverviewView, req =>
            {
                var q = ApiQuery.Of(req);
                var (from, to) = q.Window();
                var page = queries.Jobs(new JobQuery
                {
                    State = q.Str("state", 16),
                    ModuleId = q.Str("module", 128),
                    Lane = q.Enum<Lane>("lane"),
                    Kind = q.Str("kind", 128),
                    SubjectKind = q.Str("subjectKind", 64),
                    SubjectId = q.Str("subjectId", 256),
                    RunId = q.Str("runId", 128),
                    ParentJobId = q.Long("parentJobId"),
                    ErrorClass = q.Str("errorClass", 32),
                    CreatedFromMs = from,
                    CreatedToMs = to,
                    BeforeJobId = q.Long("cursor"),
                    Limit = q.Int("limit", 100, 1, 1000),
                });
                return ApiResponses.OkAsync(req, page);
            }, ct).ConfigureAwait(false);
            await Get("jobs/get", OmnisciencePerms.OverviewView, req =>
                ApiResponses.OkAsync(req, queries.Job(ApiQuery.Of(req).Long("id") ?? throw ApiException.BadRequest("'id' is required."))), ct).ConfigureAwait(false);
            await Post("jobs/cancel", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<JobActionRequest>(req);
                await ApiResponses.OkAsync(req, await queries.CancelJobAsync(body.JobId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await Post("jobs/retry", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<JobActionRequest>(req);
                await ApiResponses.OkAsync(req, await queries.RetryJobAsync(body.JobId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("llm/calls", OmnisciencePerms.OverviewView, req =>
            {
                var q = ApiQuery.Of(req);
                var (from, to) = q.Window();
                return ApiResponses.OkAsync(req, queries.LlmCalls(new LlmCallQuery
                {
                    ModuleId = q.Str("module", 128),
                    Purpose = q.Str("purpose", 128),
                    WorkClass = q.Str("workClass", 32),
                    Model = q.Str("model", 256),
                    Outcome = q.Str("outcome", 64),
                    JobId = q.Long("jobId"),
                    FromMs = from,
                    ToMs = to,
                    BeforeCallId = q.Long("cursor"),
                    Limit = q.Int("limit", 100, 1, 1000),
                }));
            }, ct).ConfigureAwait(false);
            await Get("llm/summary", OmnisciencePerms.OverviewView, req =>
            {
                var q = ApiQuery.Of(req);
                var (from, to) = q.Window();
                return ApiResponses.OkAsync(req, queries.LlmSummary(new EngineQueries.LlmSummaryFilter(
                    from, to, q.Enum<LlmGroupBy>("groupBy") ?? LlmGroupBy.WorkClass, q.Str("module", 128), q.Str("workClass", 32))));
            }, ct).ConfigureAwait(false);

            await Get("events", OmnisciencePerms.OverviewView, req =>
            {
                var q = ApiQuery.Of(req);
                long since = q.Long("since") ?? 0;
                if (since < 0) throw ApiException.BadRequest("'since' cannot be negative.");
                return ApiResponses.OkAsync(req, queries.Events(since, q.List("topics", 50), TopicFilterFor(req.user), q.Int("limit", 200, 1, 1000)));
            }, ct).ConfigureAwait(false);

            await Get("settings", OmnisciencePerms.OverviewView, req => ApiResponses.OkAsync(req, queries.Settings()), ct).ConfigureAwait(false);
            await Post("settings/set", OmniscienceV3Perms.EngineManage, async req =>
            {
                var body = ApiBody.Read<SetSettingRequest>(req);
                await ApiResponses.OkAsync(req, await queries.SetSettingAsync(body, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await registrar.WebSocketAsync(Prefix + "/stream", OmnisciencePerms.OverviewView, StreamAsync, ct).ConfigureAwait(false);

            await new TacticsRoutes(host, registrar).RegisterAsync(ct).ConfigureAwait(false);
            await new DataRoutes(host, registrar).RegisterAsync(ct).ConfigureAwait(false);
            await new WsCRoutes(host, registrar).RegisterAsync(ct).ConfigureAwait(false);
            await new SignalsRoutes(host, registrar).RegisterAsync(ct).ConfigureAwait(false);
            await new BriefsRoutes(host, registrar).RegisterAsync(ct).ConfigureAwait(false);
        }

        private async Task StreamAsync(HttpListenerContext context, WebSocket socket, NameValueCollection query, KMProfileManager.KMProfile? user)
        {
            var rt = host.Runtime;
            if (rt == null || host.Phase != V3Phase.Ready)
            {
                // Tell the client why and let it retry; a reconnect after the engine is ready resumes normally.
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, $"omniscience v3 {host.Phase.ToString().ToLowerInvariant()}", timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                    {
                        // The client left first.
                        return;
                    }
                }
                return;
            }
            await rt.Stream.HandleAsync(socket, query, TopicFilterFor(user), host.Stopping).ConfigureAwait(false);
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
