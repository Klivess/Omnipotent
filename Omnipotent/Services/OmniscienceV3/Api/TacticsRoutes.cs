using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// The Tactics API (plan X-engine): the catalogue (filtered by what a selection accepts), estimates, runs (start,
    /// list, inspect, stream, cancel, pause, resume, re-run), instant set algebra over selections, saved cohorts and
    /// playbooks. Every run is a durable job; nothing heavy runs inside a request.
    /// </summary>
    public sealed class TacticsRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public TacticsRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        private TacticRunner Runner => host.Require().Tactics;

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("tactics", OmnisciencePerms.OverviewView, req =>
            {
                var accepts = ApiQuery.Of(req).List("accepts", 20)
                    .Select(a => Enum.TryParse<SelectionKind>(a, true, out var k) ? k : throw ApiException.BadRequest($"Unknown selection kind '{a}'.")).ToList();
                return ApiResponses.OkAsync(req, new TacticCatalogDto(Runner.Catalog(accepts)));
            }, ct).ConfigureAwait(false);

            await Post("tactics/estimate", OmniscienceV3Perms.TacticsRun, req =>
            {
                var body = ApiBody.Read<TacticRunRequest>(req);
                var tactic = Runner.Find(body.Tactic) ?? throw ApiException.NotFound($"No tactic '{body.Tactic}'.");
                var input = ToInput(body);
                string? problem = TacticRunner.CheckShape(tactic.Descriptor, input) ?? tactic.Validate(input);
                if (problem != null) throw ApiException.BadRequest(problem);
                return ApiResponses.OkAsync(req, tactic.Estimate(input));
            }, ct).ConfigureAwait(false);

            await Post("tactics/run", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<TacticRunRequest>(req);
                var tactic = Runner.Find(body.Tactic) ?? throw ApiException.NotFound($"No tactic '{body.Tactic}'.");
                var d = tactic.Descriptor;
                var needed = PermissionCatalog.Get(d.RequiredPermission) ?? throw new InvalidOperationException($"Tactic {d.Key} names an unknown permission '{d.RequiredPermission}'.");
                if (!req.Can(needed)) throw ApiException.Forbidden($"{d.Title} needs the '{needed.Title}' permission.");
                if (d.Outbound && string.IsNullOrWhiteSpace(body.ConfirmationToken))
                    throw new ApiException(HttpStatusCode.Conflict, "confirmation_required", $"{d.Title} acts on the outside world: preview it and confirm before running.");
                try
                {
                    string runId = await Runner.StartAsync(d.Key, ToInput(body), ApiBody.Actor(req), ct: host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, new TacticRunStartedDto(runId), HttpStatusCode.Accepted).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex)
                {
                    throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message);
                }
            }, ct).ConfigureAwait(false);

            await Get("tactics/runs", OmniscienceV3Perms.TacticsRun, req =>
            {
                var q = ApiQuery.Of(req);
                var (from, to) = q.Window();
                TacticRunPage page;
                try
                {
                    page = Runner.List(new TacticRunQuery
                    {
                        TacticKey = q.Str("tactic", 128),
                        State = q.Str("state", 16),
                        SubjectKind = q.Str("subjectKind", 32),
                        SubjectId = q.Str("subjectId", 256),
                        PlaybookRunId = q.Str("playbookRunId", 128),
                        FromMs = from,
                        ToMs = to,
                        Cursor = q.Str("cursor", 128),
                        Limit = q.Int("limit", 50, 1, 500),
                    });
                }
                catch (ArgumentException ex)
                {
                    throw ApiException.BadRequest(ex.Message);
                }
                return ApiResponses.OkAsync(req, new TacticRunPageDto(page.Items.Select(r => ToDto(r, stale: false)).ToList(), page.NextCursor));
            }, ct).ConfigureAwait(false);

            await Get("tactics/runs/get", OmniscienceV3Perms.TacticsRun, req =>
            {
                var run = Runner.Get(ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such run.");
                return ApiResponses.OkAsync(req, ToDto(run, Runner.IsStale(run)));
            }, ct).ConfigureAwait(false);

            await Get("tactics/runs/stream", OmniscienceV3Perms.TacticsRun, req => StreamRunAsync(req, ApiQuery.Of(req).Required("id", 128)), ct).ConfigureAwait(false);

            await Post("tactics/runs/cancel", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<RunActionRequest>(req);
                bool changed = await Runner.CancelAsync(body.RunId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new RunActionResultDto(body.RunId, changed, Runner.Get(body.RunId)!.State)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await Post("tactics/runs/pause", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<RunActionRequest>(req);
                bool changed = await Runner.PauseAsync(body.RunId, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new RunActionResultDto(body.RunId, changed, Runner.Get(body.RunId)!.State)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await Post("tactics/runs/resume", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<RunActionRequest>(req);
                try
                {
                    bool changed = await Runner.ResumeAsync(body.RunId, host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, new RunActionResultDto(body.RunId, changed, Runner.Get(body.RunId)!.State)).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex)
                {
                    throw ApiException.Conflict(ex.Message);
                }
            }, ct).ConfigureAwait(false);
            await Post("tactics/runs/rerun", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<RerunRequest>(req);
                try
                {
                    string runId = await Runner.RerunAsync(body.RunId, ApiBody.Actor(req), body.Parameters, body.AsOfMs, body.ClearAsOf, host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, new TacticRunStartedDto(runId), HttpStatusCode.Accepted).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex)
                {
                    throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message);
                }
            }, ct).ConfigureAwait(false);

            await Post("selections/combine", OmniscienceV3Perms.TacticsRun, req =>
            {
                var body = ApiBody.Read<SelectionCombineRequest>(req);
                var rt = host.Require();
                try
                {
                    Selection result = body.Op.ToLowerInvariant() switch
                    {
                        "union" => SetAlgebra.Combine(body.A, Need(body.B), SetOperation.Union, rt.Host.Policy.Canonical),
                        "intersect" => SetAlgebra.Combine(body.A, Need(body.B), SetOperation.Intersect, rt.Host.Policy.Canonical),
                        "difference" => SetAlgebra.Combine(body.A, Need(body.B), SetOperation.Difference, rt.Host.Policy.Canonical),
                        "sort" => SetAlgebra.Sort(body.A, body.Order ?? SortOrder.ScoreDescending),
                        "topk" => SetAlgebra.TopK(body.A, body.K is > 0 ? body.K.Value : throw ApiException.BadRequest("'k' must be at least 1.")),
                        _ => throw ApiException.BadRequest("'op' must be union, intersect, difference, sort or topk."),
                    };
                    return ApiResponses.OkAsync(req, result);
                }
                catch (ArgumentException ex)
                {
                    throw ApiException.BadRequest(ex.Message);
                }
            }, ct).ConfigureAwait(false);

            await Get("cohorts", OmnisciencePerms.PersonsRead, req =>
                ApiResponses.OkAsync(req, Cohorts.List(host.Require().Stores).Select(ToDto).ToList()), ct).ConfigureAwait(false);
            await Get("cohorts/get", OmnisciencePerms.PersonsRead, req =>
            {
                var cohort = Cohorts.Get(host.Require().Stores, ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such cohort.");
                return ApiResponses.OkAsync(req, new CohortSelectionDto(ToDto(cohort), Cohorts.ToSelection(cohort)));
            }, ct).ConfigureAwait(false);

            await Get("playbooks", OmniscienceV3Perms.TacticsRun, req => ApiResponses.OkAsync(req, new PlaybookListDto(Runner.Playbooks.List())), ct).ConfigureAwait(false);
            await Post("playbooks/validate", OmniscienceV3Perms.TacticsRun, req =>
            {
                var body = ApiBody.Read<PlaybookSaveRequest>(req);
                var problems = Runner.Playbooks.Validate(body.Definition, body.Trigger);
                var rt = host.Require();
                long now = rt.Clock.NowMs;
                long? next = problems.Count == 0
                    ? PlaybookEngine.NextDue(new Playbook(body.Id ?? "draft", body.Name, body.Definition, body.Trigger, body.Enabled, null, now, now), rt.Host.Settings.KlivesZone)
                    : null;
                return ApiResponses.OkAsync(req, new PlaybookValidationDto(problems, next));
            }, ct).ConfigureAwait(false);
            await Post("playbooks/save", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<PlaybookSaveRequest>(req);
                try
                {
                    var saved = await Runner.Playbooks.SaveAsync(body.Id, body.Name, body.Definition, body.Trigger, body.Enabled, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, saved).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex)
                {
                    throw ApiException.BadRequest(ex.Message);
                }
            }, ct).ConfigureAwait(false);
            await Post("playbooks/run", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<PlaybookRunRequest>(req);
                string runId = await Runner.Playbooks.StartAsync(body.PlaybookId, ApiBody.Actor(req), null, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new TacticRunStartedDto(runId), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            await Get("playbooks/runs", OmniscienceV3Perms.TacticsRun, req =>
                ApiResponses.OkAsync(req, Runner.Playbooks.Runs(ApiQuery.Of(req).Required("id", 128), ApiQuery.Of(req).Int("limit", 50, 1, 500))), ct).ConfigureAwait(false);
        }

        private static Selection Need(Selection? b) => b ?? throw ApiException.BadRequest("This operation needs a second selection 'b'.");

        private static TacticInput ToInput(TacticRunRequest body) =>
            new(body.Selections ?? (IReadOnlyList<Selection>)Array.Empty<Selection>(), body.Parameters, body.AsOfMs, body.ConfirmationToken);

        private static CohortDto ToDto(Cohorts.Cohort c) => new(c.CohortId, c.Name, c.Members.Count, c.CreatedAtMs, c.UpdatedAtMs);

        internal static TacticRunDto ToDto(TacticRunRecord r, bool stale)
        {
            static JsonElement? Parse(string? json)
            {
                if (json == null) return null;
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            // Never echo a confirmation token back.
            JsonElement input;
            using (var doc = JsonDocument.Parse(r.InputJson))
            {
                var copy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText()) ?? new Dictionary<string, JsonElement>();
                foreach (var key in copy.Keys.Where(k => k.Equals("confirmationToken", StringComparison.OrdinalIgnoreCase)).ToList()) copy.Remove(key);
                input = JsonSerializer.SerializeToElement(copy);
            }
            return new TacticRunDto(r.RunId, r.TacticKey, r.TacticVersion, r.Actor, r.State, input, r.AsOfMs, r.CurrentStage, Parse(r.ProgressJson),
                Parse(r.OutputsJson), r.Error, r.ParentRunId, r.PlaybookRunId, r.CreatedAtMs, r.StartedAtMs, r.FinishedAtMs, stale);
        }

        /// <summary>NDJSON: the run as it stands, then every progress event, then the finished run.</summary>
        private async Task StreamRunAsync(UserRequest req, string runId)
        {
            var rt = host.Require();
            var first = rt.Tactics.Get(runId) ?? throw ApiException.NotFound("No such run.");
            var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            using var subscription = rt.Host.Events.Subscribe(ev =>
            {
                if ((ev.Topic == Topics.TacticProgress || ev.Topic == Topics.TacticFinished) && ev.PayloadJson.Contains(runId, StringComparison.Ordinal))
                    channel.Writer.TryWrite(ev);
                return ValueTask.CompletedTask;
            });
            await NdjsonStream.WriteAsync(req, Events(), host.Stopping).ConfigureAwait(false);

            async IAsyncEnumerable<object> Events()
            {
                yield return new { type = "run", run = ToDto(first, false) };
                if (TacticRunStates.IsTerminal(first.State)) yield break;
                var deadline = TimeSpan.FromHours(1);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
                timeout.CancelAfter(deadline);
                while (true)
                {
                    StreamEvent ev;
                    try
                    {
                        // A finished run whose event raced the subscription is caught by the periodic re-check.
                        using var tick = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        tick.CancelAfter(TimeSpan.FromSeconds(5));
                        ev = await channel.Reader.ReadAsync(tick.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                    {
                        var now = rt.Tactics.Get(runId);
                        if (now == null || TacticRunStates.IsTerminal(now.State)) break;
                        continue;
                    }
                    using var doc = JsonDocument.Parse(ev.PayloadJson);
                    yield return new { type = ev.Topic, id = ev.EventId, at = ev.AtMs, detail = doc.RootElement.Clone() };
                    if (ev.Topic == Topics.TacticFinished) break;
                }
                var last = rt.Tactics.Get(runId);
                if (last != null) yield return new { type = "run", run = ToDto(last, rt.Tactics.IsStale(last)) };
            }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
