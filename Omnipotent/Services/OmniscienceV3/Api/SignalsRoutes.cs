using System.Net;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Signalling;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>Marks the named alerts, or with <c>all</c> every unseen one (optionally of one kind), in one call.</summary>
    [ApiContract]
    public sealed record AlertStateRequest(AlertState State, IReadOnlyList<string>? AlertIds = null, bool All = false, string? Kind = null);

    [ApiContract]
    public sealed record AlertStateResultDto(int Changed, long Unseen);

    /// <summary>Klives's verdict on an alert, or on one message of a grouped alert.</summary>
    [ApiContract]
    public sealed record AlertFeedbackRequest(string AlertId, AlertVerdict Verdict, string? MessageId = null, string? Note = null);

    [ApiContract]
    public sealed record AlertFeedbackResultDto(AlertFeedbackDto Feedback, bool ClassifierCorrected);

    /// <summary>Marks the named notices, or with <c>all</c> every unseen one (optionally of one kind), in one call.</summary>
    [ApiContract]
    public sealed record NoticeStateRequest(AlertState State, IReadOnlyList<string>? NoticeIds = null, bool All = false, string? Kind = null);

    [ApiContract]
    public sealed record NoticeStateResultDto(int Changed, long Unseen);

    [ApiContract]
    public sealed record WatchListDto(IReadOnlyList<WatchRuleDto> Rules);

    [ApiContract]
    public sealed record WatchSaveRequest(string Name, string Text, string? WatchId = null, IReadOnlyList<string>? Persons = null, IReadOnlyList<string>? Conversations = null,
        IReadOnlyList<string>? Guilds = null, bool Enabled = true);

    [ApiContract]
    public sealed record WatchEnabledRequest(string WatchId, bool Enabled);

    [ApiContract]
    public sealed record WatchDeleteRequest(string WatchId);

    [ApiContract]
    public sealed record WatchDeletedDto(string WatchId, bool Deleted);

    [ApiContract]
    public sealed record InboxResolveRequest(IReadOnlyList<string> ItemIds, InboxAction Action, long? UntilMs = null);

    [ApiContract]
    public sealed record InboxResolveResultDto(int Changed, long Open);

    /// <summary>
    /// Signals routes under <c>/omniscience/v3/signals/</c>: alerts (radar and watch rules), the watch rules themselves, and
    /// the reply inbox. The counts for a badge are the read model <c>signals.summary</c>; new alerts arrive on the live stream
    /// (topic <c>alert</c>), so nothing here needs polling (F158).
    /// </summary>
    public sealed class SignalsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public SignalsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("signals/alerts", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Signals.Alerts(q.Enum<AlertState>("state"), q.Str("kind", 16), q.Str("rule", 64), q.Str("person", 200), q.Str("cursor", 400),
                    q.Int("limit", 50, 1, 200))));
            }, ct).ConfigureAwait(false);

            await Post("signals/alerts/state", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<AlertStateRequest>(req);
                var signals = Signals;
                int changed = await GuardAsync(() => signals.SetAlertStateAsync(body.AlertIds, body.All, body.Kind, body.State, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new AlertStateResultDto(changed, signals.UnseenAlerts())).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("signals/alerts/feedback", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<AlertFeedbackRequest>(req);
                string actor = ApiBody.Actor(req);
                var feedback = await GuardAsync(() => Signals.FeedbackAsync(body.AlertId, body.MessageId, body.Verdict, body.Note, actor, host.Stopping)).ConfigureAwait(false);
                // A verdict on a radar alert is also a correction of what the ensemble read in the message, so the ensemble learns from it.
                bool corrected = false;
                if (feedback.Kind == "radar" && host.Require().Host.Modules.OfType<ClassificationModule>().FirstOrDefault() is { } classification)
                {
                    string label = feedback.Verdict == AlertVerdict.Wrong ? "none" : feedback.Reason == "indirect" ? "indirect" : "mentioned";
                    await classification.CorrectAsync(feedback.MessageId, "about_klives", label, actor, body.Note, host.Stopping).ConfigureAwait(false);
                    corrected = true;
                }
                await ApiResponses.OkAsync(req, new AlertFeedbackResultDto(feedback, corrected)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("signals/notices", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Signals.Notices(q.Enum<AlertState>("state"), q.Str("kind", 32), q.Str("person", 200), q.Str("cursor", 400), q.Int("limit", 50, 1, 200))));
            }, ct).ConfigureAwait(false);

            await Post("signals/notices/state", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<NoticeStateRequest>(req);
                var signals = Signals;
                int changed = await GuardAsync(() => signals.SetNoticeStateAsync(body.NoticeIds, body.All, body.Kind, body.State, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new NoticeStateResultDto(changed, signals.UnseenNotices())).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("signals/watchlists", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, new WatchListDto(Signals.Watches())), ct).ConfigureAwait(false);

            await Post("signals/watchlists/save", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<WatchSaveRequest>(req);
                var rule = await GuardAsync(() => Signals.SaveWatchAsync(body.WatchId, body.Name, new WatchDefinition(body.Text ?? "", body.Persons, body.Conversations, body.Guilds), body.Enabled,
                    ApiBody.Actor(req), policyChecked: false, host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, rule).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("signals/watchlists/enabled", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<WatchEnabledRequest>(req);
                var rule = await GuardAsync(() => Signals.SetWatchEnabledAsync(body.WatchId ?? "", body.Enabled, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, rule).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("signals/watchlists/delete", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<WatchDeleteRequest>(req);
                bool deleted = await GuardAsync(() => Signals.DeleteWatchAsync(body.WatchId ?? "", ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                if (!deleted) throw ApiException.NotFound($"No watch rule '{body.WatchId}'.");
                await ApiResponses.OkAsync(req, new WatchDeletedDto(body.WatchId!, true)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("signals/inbox", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Guard(() => Signals.Inbox(q.Str("state", 16) ?? "open", q.Int("limit", 100, 1, 500))));
            }, ct).ConfigureAwait(false);

            await Post("signals/inbox/resolve", OmniscienceV3Perms.AlertsManage, async req =>
            {
                var body = ApiBody.Read<InboxResolveRequest>(req);
                var signals = Signals;
                int changed = await GuardAsync(() => signals.ResolveInboxAsync(body.ItemIds, body.Action, body.UntilMs, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new InboxResolveResultDto(changed, signals.Inbox("open", 1).Open)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        private SignalsModule Signals =>
            host.Require().Host.Modules.OfType<SignalsModule>().FirstOrDefault() ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The signals module is not installed in this build.");

        /// <summary>The module says what is wrong in its own words; here that becomes the classified response.</summary>
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
