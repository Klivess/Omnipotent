using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveLLM.Telemetry;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// The /klivellm routes behind the KliveLLM page: reads of the ledger, the routing in force and
    /// router health, and the writes that change routing, routers, prices and the ledger's settings.
    /// Every response is live data, so none of it is ever served from the shared response cache.
    /// </summary>
    internal static class KliveLLMRoutes
    {
        private const long MaxBodyBytes = 512 * 1024;

        private static readonly JsonSerializerSettings Json = new()
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true },
            },
            Converters = { new StringEnumConverter() },
        };

        private static string Ser(object value) => JsonConvert.SerializeObject(value, Json);

        private sealed class RouteError : Exception
        {
            public RouteError(string message, HttpStatusCode status = HttpStatusCode.BadRequest, object? extra = null) : base(message)
            {
                Status = status;
                Extra = extra;
            }
            public HttpStatusCode Status { get; }
            public object? Extra { get; }
        }

        public static async Task RegisterAsync(KliveLLM llm)
        {
            var apis = await llm.GetServicesByType<KliveAPI.KliveAPI>();
            if (apis == null || apis.Length == 0) throw new InvalidOperationException("KliveAPI is not running.");
            var api = (KliveAPI.KliveAPI)apis[0];

            Func<UserRequest, Task> Guard(Func<UserRequest, Task> handler) => async req =>
            {
                CacheDeps.MarkUncacheable("live KliveLLM data");
                try { await handler(req); }
                catch (RouteError error)
                {
                    await req.ReturnResponse(Ser(new { error = error.Message, detail = error.Extra }), "application/json", null, error.Status);
                }
                catch (Exception ex)
                {
                    try { _ = llm.ServiceLog($"KliveLLM route {req.route} failed: {ex.Message}"); } catch { }
                    await req.ReturnResponse(Ser(new { error = "KliveLLM could not answer: " + ex.Message }), "application/json", null, HttpStatusCode.InternalServerError);
                }
            };
            Task Get(string path, Func<UserRequest, Task> handler, PermissionDef permission) => api.CreateRoute(path, Guard(handler), HttpMethod.Get, permission);
            Task Post(string path, Func<UserRequest, Task> handler, PermissionDef permission) => api.CreateBufferedRoute(path, Guard(handler), HttpMethod.Post, permission, MaxBodyBytes);
            Task Ok(UserRequest req, object body) => req.ReturnResponse(Ser(body), "application/json");

            // ── reads ──

            await Get("/klivellm/overview", async req =>
            {
                var (ledger, queries) = Require(llm);
                var spec = Spec(req, ledger, "24h");
                await Ok(req, new
                {
                    asOf = Now(),
                    ledger = ledger.Stats(),
                    routing = RoutingSummary(llm),
                    data = queries.Overview(spec),
                    // Routing changes, restarts and imports in the window, for the charts to mark.
                    marks = ledger.Db.ReadMarks(spec.FromMs, spec.ToMs, 300)
                        .Select(m => new { id = m.Id, ts = m.Ts, kind = m.Kind, title = m.Title, detail = m.Detail }).ToList(),
                });
            }, KliveLLMPerms.OverviewView);

            await Get("/klivellm/live", async req => await Ok(req, Live(llm)), KliveLLMPerms.OverviewView);

            await Get("/klivellm/series", async req =>
            {
                var (ledger, queries) = Require(llm);
                await Ok(req, queries.Series(Spec(req, ledger, "24h")));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/breakdown", async req =>
            {
                var (ledger, queries) = Require(llm);
                var spec = Spec(req, ledger, "24h", points: 48);
                string? spark = req.userParameters.Get("spark");
                if (spark != null && !LlmAggregate.IsMetric(spark)) throw new RouteError($"Unknown spark metric '{spark}'.");
                await Ok(req, queries.Breakdown(spec, spark));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/distribution", async req =>
            {
                var (ledger, queries) = Require(llm);
                await Ok(req, queries.Distribution(Spec(req, ledger, "24h")));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/heatmap", async req =>
            {
                var (ledger, queries) = Require(llm);
                string metric = req.userParameters.Get("metric") ?? "calls";
                if (!LlmAggregate.IsMetric(metric)) throw new RouteError($"Unknown metric '{metric}'.");
                int tz = int.TryParse(req.userParameters.Get("tz"), out int t) ? Math.Clamp(t, -14 * 60, 14 * 60) : 0;
                await Ok(req, queries.Heatmap(Spec(req, ledger, "30d"), metric, tz));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/simulator", async req =>
            {
                var (ledger, queries) = Require(llm);
                await Ok(req, queries.Simulator(Spec(req, ledger, "30d")));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/calls", async req =>
            {
                var (ledger, _) = Require(llm);
                var filter = CallFilter(req.userParameters, ledger);
                var calls = ledger.Db.QueryCalls(filter, out bool capped);
                string? next = null;
                if (calls.Count >= filter.Limit)
                {
                    var last = calls[^1];
                    next = filter.Sort is "newest" or "oldest" ? $"{last.EndedMs}:{last.Id}" : (filter.Offset + calls.Count).ToString(CultureInfo.InvariantCulture);
                }
                await Ok(req, new { from = filter.FromMs, to = filter.ToMs, sort = filter.Sort, capped, next, calls = calls.Select(c => CallView(llm, c)).ToList() });
            }, KliveLLMPerms.CallsRead);

            await Get("/klivellm/call", async req =>
            {
                var (ledger, _) = Require(llm);
                if (!long.TryParse(req.userParameters.Get("id"), out long id)) throw new RouteError("Which call? Pass id.");
                var record = ledger.Db.GetCall(id) ?? ledger.Recent(500).FirstOrDefault(r => r.Id == id)
                             ?? throw new RouteError("That call is not in the ledger (it may be older than the retention).", HttpStatusCode.NotFound);
                await Ok(req, new
                {
                    call = CallView(llm, record, detail: true),
                    canReadText = req.Can(KliveLLMPerms.ContentRead),
                });
            }, KliveLLMPerms.CallsRead);

            await Get("/klivellm/call/content", async req =>
            {
                var (ledger, _) = Require(llm);
                if (!long.TryParse(req.userParameters.Get("id"), out long id)) throw new RouteError("Which call? Pass id.");
                var content = ledger.Content?.Read(id) ?? throw new RouteError("No text was kept for that call.", HttpStatusCode.NotFound);
                try { _ = llm.ServiceLog($"KliveLLM: {req.user?.Name ?? "someone"} read the kept text of call {id}."); } catch { }
                await Ok(req, new { id, request = content.Request, response = content.Response, keptAt = content.Ts });
            }, KliveLLMPerms.ContentRead);

            await Get("/klivellm/routers", async req => await Ok(req, await RoutersView(llm)), KliveLLMPerms.OverviewView);

            await Get("/klivellm/router", async req =>
            {
                var (ledger, queries) = Require(llm);
                string id = req.userParameters.Get("id") ?? throw new RouteError("Which router? Pass id.");
                var router = llm.RouterById(id) ?? throw new RouteError($"There is no router '{id}'.", HttpStatusCode.NotFound);
                await Ok(req, await RouterDetail(llm, ledger, queries, router, req.userParameters));
            }, KliveLLMPerms.AnalyticsRead);

            await Get("/klivellm/routing", async req => await Ok(req, RoutingView(llm, llm.Routing.Current)), KliveLLMPerms.OverviewView);

            await Get("/klivellm/routing/history", async req =>
            {
                int limit = int.TryParse(req.userParameters.Get("limit"), out int l) ? l : 50;
                await Ok(req, new { revision = llm.Routing.Revision, history = llm.Routing.History(limit) });
            }, KliveLLMPerms.OverviewView);

            await Get("/klivellm/prices", async req =>
            {
                var entries = llm.Prices.All();
                List<object> missing = new();
                if (llm.Queries != null && llm.Ledger != null)
                {
                    var spec = Spec(req, llm.Ledger, "30d", points: 30, groupBy: "model");
                    var models = llm.Ledger.Db.ReadAggregates(LlmTier.D1, LlmTiers.Align(spec.FromMs, LlmTier.D1), spec.ToMs);
                    foreach (var group in models.GroupBy(r => (r.Router, r.Model)))
                    {
                        if (string.IsNullOrEmpty(group.Key.Model) || llm.Prices.Find(group.Key.Router, group.Key.Model) != null) continue;
                        long tokens = group.Sum(r => r.Agg.TotalTokens);
                        if (tokens > 0) missing.Add(new { router = group.Key.Router, model = group.Key.Model, tokens, billing = llm.BillingOf(group.Key.Router) });
                    }
                }
                await Ok(req, new { entries, missing });
            }, KliveLLMPerms.OverviewView);

            await Get("/klivellm/settings", async req =>
            {
                await Ok(req, new
                {
                    callRetentionDays = await llm.GetIntOmniSetting("KliveLLM_LedgerCallRetentionDays", 90),
                    keptTextRetentionDays = await llm.GetIntOmniSetting("KliveLLM_KeptTextRetentionDays", 3),
                    routerProbes = await llm.GetBoolOmniSetting("KliveLLM_RouterProbes", true),
                    ledger = llm.Ledger?.Stats(),
                    ledgerError = llm.LedgerError,
                    retention = llm.Ledger?.Options,
                });
            }, KliveLLMPerms.OverviewView);

            await Get("/klivellm/export", async req =>
            {
                var (ledger, queries) = Require(llm);
                string kind = (req.userParameters.Get("kind") ?? "calls").ToLowerInvariant();
                string csv = kind == "series" ? SeriesCsv(queries.Series(Spec(req, ledger, "7d"))) : CallsCsv(llm, ledger, req.userParameters);
                var headers = new NameValueCollection { ["Content-Disposition"] = $"attachment; filename=\"klivellm-{kind}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv\"" };
                await req.ReturnResponse(csv, "text/csv; charset=utf-8", headers);
            }, KliveLLMPerms.DataExport);

            // ── routing writes ──

            await Post("/klivellm/routing/preview", async req =>
            {
                var body = Body(req);
                var next = ReadDocument(body);
                long? baseRevision = body.Value<long?>("baseRevision");
                string? invalid = Validate(llm, next);
                var current = llm.Routing.Current;
                var nextRouters = LlmRouterRegistry.Merge(next.Routers);
                var sessions = llm.SessionsByLane();
                var lanes = new List<object>();
                foreach (var lane in KnownLanes(llm, current, next))
                {
                    var caller = new LlmCaller(lane.Service, lane.Lane);
                    var before = llm.ResolveRouteNow(caller);
                    var after = LlmRouteResolver.Resolve(next, nextRouters, llm.DefaultRouterIdNow(), caller, false, DateTime.UtcNow, key => llm.Ledger?.Usage(key));
                    bool routerChanged = !string.Equals(before.Primary?.Id, after.Primary?.Id, StringComparison.OrdinalIgnoreCase);
                    bool changed = routerChanged || !string.Equals(before.Model, after.Model, StringComparison.Ordinal) || before.Pin != after.Pin
                                   || before.IsBlocked != after.IsBlocked || before.Spending != after.Spending
                                   || !before.Fallbacks.Select(f => f.Router.Id + f.Model).SequenceEqual(after.Fallbacks.Select(f => f.Router.Id + f.Model));
                    if (!changed) continue;
                    int cold = routerChanged && sessions.TryGetValue(caller.Key, out var held) ? held.Count(s => s.Warm) : 0;
                    lanes.Add(new
                    {
                        key = caller.Key,
                        label = $"{lane.Service} › {lane.Label}",
                        before = DecisionView(before),
                        after = DecisionView(after),
                        warmSessionsGoingCold = cold,
                    });
                }
                await Ok(req, new
                {
                    valid = invalid == null,
                    error = invalid,
                    conflict = baseRevision.HasValue && baseRevision.Value != current.Revision,
                    revision = current.Revision,
                    changes = LlmRoutingStore.Diff(current, next),
                    lanes,
                });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/routing/apply", async req =>
            {
                var body = Body(req);
                var next = ReadDocument(body);
                long? baseRevision = body.Value<long?>("baseRevision");
                var result = llm.Routing.Apply(next, baseRevision, By(req), body.Value<string?>("note"), doc => Validate(llm, doc));
                if (!result.Ok)
                    throw new RouteError(result.Error ?? "The routing could not be applied.", result.Conflict ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest,
                        new { revision = result.Document.Revision, conflict = result.Conflict });
                await Ok(req, new { ok = true, revision = result.Document.Revision, entry = Summary(result.Entry), routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/routing/revert", async req =>
            {
                var body = Body(req);
                long revision = body.Value<long?>("revision") ?? throw new RouteError("Which revision?");
                var entry = llm.Routing.HistoryEntry(revision) ?? throw new RouteError($"Revision {revision} is not in the history.", HttpStatusCode.NotFound);
                var target = (body.Value<bool?>("before") == true ? entry.Before : entry.After) ?? throw new RouteError("That history entry has no document to restore.");
                var result = llm.Routing.Apply(target, llm.Routing.Revision, By(req), body.Value<string?>("note") ?? $"Restored revision {revision}", doc => Validate(llm, doc));
                if (!result.Ok) throw new RouteError(result.Error ?? "The routing could not be restored.");
                await Ok(req, new { ok = true, revision = result.Document.Revision, entry = Summary(result.Entry), routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/routing/override", async req =>
            {
                var body = Body(req);
                bool clear = body.Value<bool?>("clear") == true;
                var result = llm.Routing.Mutate(doc =>
                {
                    if (clear) { doc.Override = null; return null; }
                    string router = body.Value<string?>("router") ?? "";
                    if (llm.RouterById(router) == null) return $"There is no router '{router}'.";
                    int minutes = Math.Clamp(body.Value<int?>("minutes") ?? 60, 5, 7 * 24 * 60);
                    doc.Override = new LlmEmergencyOverride
                    {
                        Router = router,
                        Model = string.IsNullOrWhiteSpace(body.Value<string?>("model")) ? null : body.Value<string>("model")!.Trim(),
                        UntilUtc = DateTime.UtcNow.AddMinutes(minutes),
                        Reason = body.Value<string?>("reason"),
                        SetBy = By(req),
                        Services = (body["services"] as JArray)?.Select(s => (string?)s).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
                    };
                    return null;
                }, By(req), clear ? "Override cleared" : body.Value<string?>("reason") ?? "Override set", doc => Validate(llm, doc));
                if (!result.Ok) throw new RouteError(result.Error ?? "The override could not be saved.");
                await Ok(req, new { ok = true, revision = result.Document.Revision, routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/service/pause", async req =>
            {
                var body = Body(req);
                string service = body.Value<string?>("service") ?? throw new RouteError("Which service?");
                string? lane = body.Value<string?>("lane");
                bool paused = body.Value<bool?>("paused") ?? true;
                string? reason = body.Value<string?>("reason");
                var result = llm.Routing.Mutate(doc =>
                {
                    var map = lane == null ? doc.Services : doc.Lanes;
                    string key = lane == null ? service : service + "/" + lane;
                    if (!map.TryGetValue(key, out var policy)) map[key] = policy = new LlmRoutePolicy();
                    policy.Paused = paused ? true : null;
                    policy.PausedReason = paused ? reason : null;
                    policy.PausedUtc = paused ? DateTime.UtcNow : null;
                    return null;
                }, By(req), $"{(paused ? "Paused" : "Resumed")} {service}{(lane != null ? " › " + lane : "")}{(reason != null ? ": " + reason : "")}");
                if (!result.Ok) throw new RouteError(result.Error ?? "Could not change the pause.");
                await Ok(req, new { ok = true, revision = result.Document.Revision, routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/budgets/save", async req =>
            {
                var body = Body(req);
                string scope = body.Value<string?>("scope") ?? throw new RouteError("Whose budget? Pass scope (a service or Service/lane).");
                var budget = body["budget"] is JObject b ? b.ToObject<LlmBudget>() : null;
                var result = llm.Routing.Mutate(doc =>
                {
                    var map = scope.Contains('/') ? doc.Lanes : doc.Services;
                    if (!map.TryGetValue(scope, out var policy)) map[scope] = policy = new LlmRoutePolicy();
                    policy.Budget = budget;
                    return null;
                }, By(req), budget == null ? $"Budget removed for {scope}" : $"Budget set for {scope}");
                if (!result.Ok) throw new RouteError(result.Error ?? "Could not save the budget.");
                await Ok(req, new { ok = true, revision = result.Document.Revision, routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/content/capture", async req =>
            {
                if (!req.Can(KliveLLMPerms.ContentRead)) throw new RouteError("Keeping prompt text needs the 'Read kept prompt and reply text' permission too.", HttpStatusCode.Forbidden);
                var body = Body(req);
                string scope = body.Value<string?>("scope") ?? throw new RouteError("Whose text? Pass scope (a service or Service/lane).");
                int minutes = body.Value<int?>("minutes") ?? 60;
                var result = llm.Routing.Mutate(doc =>
                {
                    if (minutes <= 0) { doc.Capture.Remove(scope); return null; }
                    doc.Capture[scope] = new LlmCaptureWindow
                    {
                        StartedUtc = DateTime.UtcNow,
                        UntilUtc = DateTime.UtcNow.AddMinutes(Math.Min(minutes, 14 * 24 * 60)),
                        SetBy = By(req),
                    };
                    return null;
                }, By(req), minutes <= 0 ? $"Text capture stopped for {scope}" : $"Text capture on for {scope} for {minutes} minutes");
                if (!result.Ok) throw new RouteError(result.Error ?? "Could not change text capture.");
                await Ok(req, new { ok = true, revision = result.Document.Revision, routing = RoutingView(llm, result.Document) });
            }, KliveLLMPerms.RoutingManage);

            await Post("/klivellm/circuits/reset", async req =>
            {
                var body = Body(req);
                llm.Circuits.Reset(body.Value<string?>("lane"), body.Value<string?>("router"));
                await Ok(req, new { ok = true, circuits = llm.Circuits.Describe() });
            }, KliveLLMPerms.RoutingManage);

            // ── routers ──

            await Post("/klivellm/routers/save", async req =>
            {
                var body = Body(req);
                var input = (body["router"] as JObject ?? body).ToObject<LlmRouterDefinition>() ?? throw new RouteError("No router.");
                string? savedId = null;
                var result = llm.Routing.Mutate(doc =>
                {
                    var known = LlmRouterRegistry.Merge(doc.Routers);
                    bool builtIn = LlmRouterIds.IsBuiltIn(input.Id);
                    if (builtIn)
                    {
                        var existing = doc.Routers.FirstOrDefault(r => string.Equals(r.Id, input.Id, StringComparison.OrdinalIgnoreCase));
                        var entry = existing ?? new LlmRouterDefinition { Id = input.Id.ToLowerInvariant(), BuiltIn = true, Name = known[input.Id].Name };
                        entry.MonthlyFeeUsd = input.MonthlyFeeUsd is >= 0 ? input.MonthlyFeeUsd : null;
                        entry.Enabled = input.Enabled;
                        entry.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
                        entry.Billing = input.Billing;
                        entry.UpdatedUtc = DateTime.UtcNow;
                        if (existing == null) doc.Routers.Add(entry);
                        savedId = entry.Id;
                        return null;
                    }
                    input.BuiltIn = false;
                    input.Kind = "openai";
                    input.Name = (input.Name ?? "").Trim();
                    input.BaseUrl = input.BaseUrl?.Trim();
                    input.DefaultModel = input.DefaultModel?.Trim();
                    string? error = LlmRouterRegistry.Validate(input);
                    if (error != null) return error;
                    if (string.IsNullOrWhiteSpace(input.Id))
                    {
                        input.Id = LlmRouterRegistry.NewId(input.Name, known.Keys.ToList());
                        input.CreatedUtc = DateTime.UtcNow;
                    }
                    else if (!doc.Routers.Any(r => string.Equals(r.Id, input.Id, StringComparison.OrdinalIgnoreCase)))
                        return $"There is no added router '{input.Id}' to change.";
                    input.UpdatedUtc = DateTime.UtcNow;
                    var previous = doc.Routers.FirstOrDefault(r => string.Equals(r.Id, input.Id, StringComparison.OrdinalIgnoreCase));
                    if (previous != null) { input.CreatedUtc ??= previous.CreatedUtc; doc.Routers.Remove(previous); }
                    doc.Routers.Add(input);
                    savedId = input.Id;
                    return null;
                }, By(req), $"Router {(string.IsNullOrWhiteSpace(input.Id) ? "added" : "changed")}: {input.Name ?? input.Id}");
                if (!result.Ok) throw new RouteError(result.Error ?? "The router could not be saved.");
                await Ok(req, new { ok = true, id = savedId, revision = result.Document.Revision, routers = await RoutersView(llm) });
            }, KliveLLMPerms.RoutersManage);

            await Post("/klivellm/routers/delete", async req =>
            {
                var body = Body(req);
                string id = body.Value<string?>("id") ?? throw new RouteError("Which router?");
                if (LlmRouterIds.IsBuiltIn(id)) throw new RouteError("A built-in router can be switched off, not removed.");
                var result = llm.Routing.Mutate(doc =>
                {
                    var users = new List<string>();
                    void Check(string where, LlmRoutePolicy p)
                    {
                        if (string.Equals(p.Router, id, StringComparison.OrdinalIgnoreCase)) users.Add(where);
                        if (p.Fallbacks?.Any(f => string.Equals(f.Router, id, StringComparison.OrdinalIgnoreCase)) == true) users.Add(where + " (fallback)");
                    }
                    Check("the default route", doc.Default);
                    foreach (var (k, p) in doc.Services) Check(k, p);
                    foreach (var (k, p) in doc.Lanes) Check(k, p);
                    if (string.Equals(doc.Override?.Router, id, StringComparison.OrdinalIgnoreCase)) users.Add("the override");
                    if (users.Count > 0) return $"Still in use by {string.Join(", ", users)}: route them elsewhere first.";
                    int removed = doc.Routers.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    return removed == 0 ? $"There is no added router '{id}'." : null;
                }, By(req), $"Router removed: {id}");
                if (!result.Ok) throw new RouteError(result.Error ?? "The router could not be removed.");
                try
                {
                    var settings = await llm.GetOmniGlobalSettingsManager();
                    await settings.DeleteOmniSetting(LlmRouterRegistry.KeySettingFor(id), llm.serviceID);
                }
                catch { /* the key may never have been set */ }
                await Ok(req, new { ok = true, revision = result.Document.Revision, routers = await RoutersView(llm) });
            }, KliveLLMPerms.RoutersManage);

            await Post("/klivellm/routers/key", async req =>
            {
                var body = Body(req);
                string id = body.Value<string?>("id") ?? throw new RouteError("Which router?");
                var router = llm.RouterById(id) ?? throw new RouteError($"There is no router '{id}'.", HttpStatusCode.NotFound);
                if (router.IsLocal) throw new RouteError("The local model has no key.");
                string key = (body.Value<string?>("key") ?? "").Trim();
                if (key.Length > 4096) throw new RouteError("That key is too long.");
                var settings = await llm.GetOmniGlobalSettingsManager();
                bool saved = key.Length == 0
                    ? await settings.DeleteOmniSetting(router.KeySetting, llm.serviceID)
                    : await settings.SetOmniSetting(router.KeySetting, key, llm.serviceID, llm.GetName(), OmniSettingType.String,
                        fulfilledViaApi: true, sensitive: true, changedBy: By(req));
                try { _ = llm.ServiceLog($"KliveLLM: {By(req)} {(key.Length == 0 ? "removed" : "replaced")} the key of {router.Name}."); } catch { }
                // Never echo the key: the page only learns whether one is set.
                await Ok(req, new { ok = saved || key.Length == 0, id = router.Id, keySet = key.Length > 0 });
            }, KliveLLMPerms.RoutersManage);

            await Post("/klivellm/routers/test", async req =>
            {
                var body = Body(req);
                string id = body.Value<string?>("id") ?? throw new RouteError("Which router?");
                var router = llm.RouterById(id) ?? throw new RouteError($"There is no router '{id}'.", HttpStatusCode.NotFound);
                await Ok(req, await TestRouterAsync(llm, router, body.Value<string?>("model"), body.Value<bool?>("completion") == true));
            }, KliveLLMPerms.RoutersManage);

            // ── prices and settings ──

            await Post("/klivellm/prices/save", async req =>
            {
                var body = Body(req);
                var entries = (body["entries"] as JArray)?.ToObject<List<LlmPriceEntry>>() ?? throw new RouteError("No prices.");
                if (entries.Count > 500) throw new RouteError("500 prices at a time at most.");
                llm.Prices.Save(entries);
                await Ok(req, new { ok = true, entries = llm.Prices.All() });
            }, KliveLLMPerms.PricesManage);

            await Post("/klivellm/prices/refresh", async req =>
            {
                int n = await llm.RefreshCataloguePricesAsync();
                await Ok(req, new { ok = n > 0, imported = n, entries = llm.Prices.All() });
            }, KliveLLMPerms.PricesManage);

            await Post("/klivellm/settings", async req =>
            {
                var body = Body(req);
                var settings = await llm.GetOmniGlobalSettingsManager();
                if (body.Value<int?>("callRetentionDays") is int days)
                {
                    days = Math.Clamp(days, 7, 730);
                    await settings.SetOmniSetting("KliveLLM_LedgerCallRetentionDays", days.ToString(CultureInfo.InvariantCulture), llm.serviceID, llm.GetName(), OmniSettingType.Int, fulfilledViaApi: true, changedBy: By(req));
                    if (llm.Ledger != null) llm.Ledger.Options.CallRetentionDays = days;
                }
                if (body.Value<int?>("keptTextRetentionDays") is int textDays)
                {
                    textDays = Math.Clamp(textDays, 1, 14);
                    await settings.SetOmniSetting("KliveLLM_KeptTextRetentionDays", textDays.ToString(CultureInfo.InvariantCulture), llm.serviceID, llm.GetName(), OmniSettingType.Int, fulfilledViaApi: true, changedBy: By(req));
                    if (llm.Ledger?.Content != null) llm.Ledger.Content.RetentionDays = textDays;
                }
                if (body.Value<bool?>("routerProbes") is bool probes)
                    await settings.SetOmniSetting("KliveLLM_RouterProbes", probes ? "true" : "false", llm.serviceID, llm.GetName(), OmniSettingType.Bool, fulfilledViaApi: true, changedBy: By(req));
                await Ok(req, new { ok = true });
            }, KliveLLMPerms.SettingsManage);

            await llm.ServiceLog("KliveLLM: /klivellm routes registered.");
        }

        // ── helpers: parsing ──

        private static (LlmTelemetry Ledger, LlmQueryEngine Queries) Require(KliveLLM llm)
        {
            var ledger = llm.Ledger;
            var queries = llm.Queries;
            if (ledger == null || queries == null)
                throw new RouteError(llm.LedgerError != null ? "The KliveLLM ledger did not start: " + llm.LedgerError : "The KliveLLM ledger is starting.", HttpStatusCode.ServiceUnavailable);
            return (ledger, queries);
        }

        private static long earliestCacheMs = -1;
        private static DateTime earliestCheckedUtc = DateTime.MinValue;

        private static long Earliest(LlmTelemetry ledger)
        {
            if (DateTime.UtcNow - earliestCheckedUtc > TimeSpan.FromMinutes(10))
            {
                earliestCheckedUtc = DateTime.UtcNow;
                try { earliestCacheMs = ledger.Db.EarliestMs("agg_1d") ?? -1; } catch { }
            }
            return earliestCacheMs;
        }

        private static LlmQuerySpec Spec(UserRequest req, LlmTelemetry ledger, string defaultRange, int? points = null, string? groupBy = null)
        {
            var parameters = new NameValueCollection(req.userParameters ?? new NameValueCollection());
            if (points.HasValue && parameters["points"] == null) parameters["points"] = points.Value.ToString(CultureInfo.InvariantCulture);
            if (groupBy != null && parameters["groupBy"] == null) parameters["groupBy"] = groupBy;
            if (!LlmQuerySpec.TryParse(parameters, Now(), Earliest(ledger), ledger.Options, out var spec, out string? error, defaultRange))
                throw new RouteError(error ?? "Invalid query.");
            return spec;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static JObject Body(UserRequest req)
        {
            try { return string.IsNullOrWhiteSpace(req.userMessageContent) ? new JObject() : JObject.Parse(req.userMessageContent); }
            catch (JsonException) { throw new RouteError("The request body is not JSON."); }
        }

        private static LlmRoutingDocument ReadDocument(JObject body)
        {
            var token = body["document"] as JObject ?? throw new RouteError("No routing document.");
            try
            {
                var doc = token.ToObject<LlmRoutingDocument>(JsonSerializer.Create(new JsonSerializerSettings { Converters = { new StringEnumConverter() } }))
                          ?? throw new RouteError("No routing document.");
                doc.Normalize();
                return doc;
            }
            catch (JsonException ex) { throw new RouteError("The routing document is malformed: " + ex.Message); }
        }

        private static string? Validate(KliveLLM llm, LlmRoutingDocument doc)
        {
            var routers = LlmRouterRegistry.Merge(doc.Routers);
            foreach (var added in doc.Routers.Where(r => !LlmRouterIds.IsBuiltIn(r.Id)))
                if (LlmRouterRegistry.Validate(added) is { } error) return $"{added.Name}: {error}";
            return LlmRouteResolver.Validate(doc, routers);
        }

        private static string By(UserRequest req) => string.IsNullOrWhiteSpace(req.user?.Name) ? "KliveLLM page" : req.user!.Name;

        private static LlmCallFilter CallFilter(NameValueCollection p, LlmTelemetry ledger)
        {
            long now = Now();
            var filter = new LlmCallFilter();
            string? from = p["from"], to = p["to"];
            if (!string.IsNullOrWhiteSpace(from) && long.TryParse(from, out long f)) filter.FromMs = f;
            else if (!string.IsNullOrWhiteSpace(from) && DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fd)) filter.FromMs = fd.ToUnixTimeMilliseconds();
            else
            {
                string range = p["range"] ?? "24h";
                long span = LlmQuerySpec.PresetMs(range);
                filter.FromMs = span == long.MaxValue ? 0 : now - span;
            }
            if (!string.IsNullOrWhiteSpace(to) && long.TryParse(to, out long t)) filter.ToMs = t;
            else if (!string.IsNullOrWhiteSpace(to) && DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var td)) filter.ToMs = td.ToUnixTimeMilliseconds();
            else filter.ToMs = now + 60_000;
            string[]? List(string name) => string.IsNullOrWhiteSpace(p[name]) ? null
                : p[name]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filter.Services = List("service");
            filter.Lanes = List("lane");
            filter.Routers = List("router");
            filter.Models = List("model");
            filter.Outcomes = List("outcome");
            filter.Kinds = List("kind");
            filter.Subject = string.IsNullOrWhiteSpace(p["subject"]) ? null : p["subject"]!.Trim();
            filter.Session = string.IsNullOrWhiteSpace(p["session"]) ? null : p["session"]!.Trim();
            filter.FailedOnly = p["failed"] is "1" or "true";
            filter.FallbackOnly = p["fallback"] is "1" or "true";
            filter.ContentOnly = p["content"] is "1" or "true";
            filter.MinTotalMs = long.TryParse(p["minMs"], out long minMs) ? minMs : null;
            filter.Sort = (p["sort"] ?? "newest").ToLowerInvariant() switch
            {
                "oldest" => "oldest", "slowest" => "slowest", "largest" => "largest", "costliest" => "costliest", "ttft" => "ttft", _ => "newest",
            };
            filter.Limit = int.TryParse(p["limit"], out int limit) ? Math.Clamp(limit, 1, 500) : 100;
            string? cursor = p["next"] ?? p["before"];
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (filter.Sort is "newest" or "oldest")
                {
                    var parts = cursor.Split(':');
                    if (parts.Length == 2 && long.TryParse(parts[0], out long bms) && long.TryParse(parts[1], out long bid)) { filter.BeforeMs = bms; filter.BeforeId = bid; }
                }
                else if (int.TryParse(cursor, out int offset)) filter.Offset = offset;
            }
            return filter;
        }

        // ── helpers: views ──

        private static object RoutingSummary(KliveLLM llm)
        {
            var doc = llm.Routing.Current;
            string defaultRouter = doc.Default.Router ?? llm.DefaultRouterIdNow();
            return new
            {
                revision = doc.Revision,
                updatedUtc = doc.UpdatedUtc,
                updatedBy = doc.UpdatedBy,
                defaultRouter,
                defaultRouterName = llm.RouterNameOf(defaultRouter),
                followsSetting = doc.Default.Router == null,
                @override = doc.Override is { } o && o.UntilUtc > DateTime.UtcNow ? o : null,
                capture = doc.Capture.Where(kv => kv.Value.UntilUtc > DateTime.UtcNow).ToDictionary(kv => kv.Key, kv => kv.Value),
                paused = doc.Services.Where(kv => kv.Value.Paused == true).Select(kv => kv.Key)
                    .Concat(doc.Lanes.Where(kv => kv.Value.Paused == true).Select(kv => kv.Key)).ToList(),
            };
        }

        private static object Live(KliveLLM llm)
        {
            var ledger = llm.Ledger;
            var air = llm.FairUse.Describe();
            var meter = PrefixCacheMeter.Shared.Describe();
            return new
            {
                asOf = Now(),
                ledger = ledger?.Stats(),
                ledgerError = llm.LedgerError,
                inFlight = ledger?.InFlight().Select(t => new
                {
                    id = t.Id,
                    service = t.Caller.Service,
                    lane = t.Caller.Lane,
                    laneLabel = LlmConsumerCatalog.Find(t.Caller.Service, t.Caller.Lane)?.Label ?? t.Caller.Lane,
                    laneKey = t.Caller.Key,
                    subject = t.Caller.Subject,
                    kind = t.Kind,
                    router = t.Router,
                    routerName = t.RouterName,
                    model = t.Model,
                    state = t.State.ToString(),
                    elapsedMs = t.ElapsedMs,
                    stateMs = t.StateElapsedMs,
                    queueMs = t.QueueMs,
                    ttftMs = t.TtftMs,
                    streamedChars = t.StreamedChars,
                    attempts = t.AttemptCount,
                    fallbackFrom = t.FallbackFrom,
                    sessionId = t.SessionId,
                    workClass = t.WorkClass,
                }).ToList(),
                recent = ledger?.Recent(60).Select(r => CallView(llm, r)).ToList(),
                routers = ledger?.Health.Live(),
                airouter = new
                {
                    inFlight = air.InFlight,
                    maxParallel = AIRouterFairUseLimiter.PolicyMaxParallelRequests,
                    waiting = air.Waiting,
                    requestsInWindow = air.RequestsInWindow,
                    requestCeiling = air.RequestCeiling,
                    tokensInWindow = air.TokensInWindow,
                    tokenCeiling = air.TokenCeiling,
                    coolOffSeconds = Math.Round(air.PenaltyRemaining.TotalSeconds, 1),
                    totalAdmitted = air.TotalAdmitted,
                    totalWaitedMs = air.TotalWaitedMs,
                    totalPenalties = air.TotalPenalties,
                    warmCohort = air.WarmCohortSize,
                    residents = air.Residents,
                    trackedConversations = air.TrackedConversations,
                    maxWaitedMs = air.MaxWaitedMs,
                    prefixLifetimeSeconds = KliveLLM.AIRouterMeasuredPrefixLifetime()?.TotalSeconds,
                    conversationBudget = KliveLLM.AIRouterConversationBudget(),
                    cacheHitPct1h = meter.Requests > 0 ? Math.Round(meter.HitRate * 100, 2) : (double?)null,
                    cacheRequests1h = meter.Requests,
                },
                circuits = llm.Circuits.Describe(),
                routing = RoutingSummary(llm),
            };
        }

        private static object CallView(KliveLLM llm, LlmCallRecord r, bool detail = false)
        {
            var flags = Enum.GetValues<LlmCallFlags>().Where(f => f != LlmCallFlags.None && r.Has(f)).Select(f => f.ToString()).ToList();
            var lane = LlmConsumerCatalog.Find(r.Service, r.Lane);
            return new
            {
                id = r.Id,
                startedMs = r.StartedMs,
                endedMs = r.EndedMs,
                service = r.Service,
                lane = r.Lane,
                laneLabel = lane?.Label ?? r.Lane,
                subject = r.Subject,
                operation = r.Operation,
                session = r.SessionId,
                kind = r.Kind,
                workClass = r.WorkClass,
                router = r.Router,
                routerName = r.RouterName ?? llm.RouterNameOf(r.Router),
                provider = r.Provider,
                modelRequested = r.ModelRequested,
                model = r.Model,
                stream = r.Stream,
                outcome = r.Outcome,
                httpStatus = r.HttpStatus,
                finishReason = r.FinishReason,
                attempts = r.Attempts,
                promptTokens = r.PromptTokens,
                cachedTokens = r.CachedTokens,
                cacheWriteTokens = r.CacheWriteTokens,
                completionTokens = r.CompletionTokens,
                reasoningTokens = r.ReasoningTokens,
                cacheMetrics = r.CacheMetrics,
                costUsd = r.CostUsd,
                upstreamCostUsd = r.UpstreamCostUsd,
                estCostUsd = r.EstimatedCostUsd ?? LlmCosting.CallCost(r, llm.Prices),
                listCostUsd = LlmCosting.ListCost(r, llm.Prices),
                billing = r.Billing,
                queueMs = r.QueueMs,
                providerMs = r.ProviderMs,
                totalMs = r.TotalMs,
                ttftMs = r.TtftMs,
                requestBytes = r.RequestBytes,
                toolCalls = r.ToolCalls,
                flags,
                fallbackFrom = r.FallbackFrom,
                epoch = detail ? r.CacheEpoch : null,
                epochTurn = r.CacheEpochTurn,
                revision = r.RoutingRevision,
                error = r.Error,
                attemptLog = detail ? r.AttemptLog : null,
                textKept = r.Has(LlmCallFlags.ContentKept),
            };
        }

        private static object DecisionView(LlmRouteDecision d) => new
        {
            router = d.Primary?.Id,
            routerName = d.Primary?.Name,
            billing = d.Primary?.Billing,
            model = d.Model,
            pin = d.Pin,
            source = d.Source,
            fallbacks = d.Fallbacks.Select(f => new { router = f.Router.Id, name = f.Router.Name, model = f.Model, billing = f.Router.Billing }).ToList(),
            spending = d.Spending,
            thinking = d.Thinking,
            serviceTier = d.ServiceTier,
            maxParallel = d.MaxParallel,
            budget = d.Budget,
            budgetScope = d.BudgetScope,
            budgetStatus = d.BudgetStatus,
            capture = d.Capture,
            blocked = d.Block?.ToString(),
            blockReason = d.BlockReason,
            chain = d.Chain,
            warnings = d.Warnings,
        };

        private static object? Summary(LlmRoutingHistoryEntry? entry)
            => entry == null ? null : new { revision = entry.Revision, atUtc = entry.AtUtc, by = entry.By, note = entry.Note, changes = entry.Changes };

        private sealed record LaneRow(string Service, string Lane, string Label, LlmLaneDescriptor? Descriptor);

        /// <summary>Every lane worth showing: the catalogue, any lane the ledger has seen lately, and any a document names.</summary>
        private static List<LaneRow> KnownLanes(KliveLLM llm, params LlmRoutingDocument[] docs)
        {
            var rows = LlmConsumerCatalog.Lanes.Select(l => new LaneRow(l.Service, l.Lane, l.Label, l)).ToList();
            var seen = new HashSet<string>(rows.Select(r => r.Service + "/" + r.Lane), StringComparer.OrdinalIgnoreCase);
            void Add(string service, string lane)
            {
                if (string.IsNullOrWhiteSpace(service) || !seen.Add(service + "/" + lane)) return;
                rows.Add(new LaneRow(service, lane, lane, null));
            }
            try
            {
                if (llm.Ledger != null)
                {
                    long now = Now();
                    foreach (var row in llm.Ledger.Db.ReadAggregates(LlmTier.D1, LlmTiers.Align(now - 30L * 86_400_000, LlmTier.D1), now + 86_400_000))
                        Add(row.Service, row.Lane);
                }
            }
            catch { }
            foreach (var doc in docs)
                foreach (string key in doc.Lanes.Keys)
                {
                    var parts = key.Split('/');
                    if (parts.Length == 2) Add(parts[0], parts[1]);
                }
            return rows;
        }

        private static object RoutingView(KliveLLM llm, LlmRoutingDocument doc)
        {
            var routers = llm.Routers();
            var sessions = llm.SessionsByLane();
            var known = KnownLanes(llm, doc);
            var lanes = known.Select(row =>
            {
                var caller = new LlmCaller(row.Service, row.Lane);
                var decision = llm.ResolveRouteNow(caller);
                sessions.TryGetValue(caller.Key, out var held);
                return (object)new
                {
                    key = caller.Key,
                    service = row.Service,
                    lane = row.Lane,
                    label = row.Label,
                    description = row.Descriptor?.Description,
                    catalogued = row.Descriptor != null,
                    workClass = row.Descriptor?.WorkClass.ToString(),
                    needsToolCalling = row.Descriptor?.NeedsToolCalling ?? false,
                    defaultSpending = row.Descriptor?.DefaultSpending ?? LlmSpending.PrimaryOnly,
                    modelSource = row.Descriptor?.ModelSource,
                    modelSourceLink = row.Descriptor?.ModelSourceLink,
                    fixedRouter = row.Descriptor?.FixedRouter,
                    sessions = held?.Count ?? 0,
                    warmSessions = held?.Count(s => s.Warm) ?? 0,
                    effective = DecisionView(decision),
                };
            }).ToList();
            var services = known.Select(l => l.Service).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new
            {
                revision = doc.Revision,
                document = doc,
                defaultRouterFromSetting = llm.DefaultRouterIdNow(),
                routers = routers.Values.OrderBy(r => r.BuiltIn ? 0 : 1).ThenBy(r => r.Name).Select(r => new
                {
                    r.Id, r.Name, r.Kind, r.BuiltIn, r.Billing, r.Enabled, r.MonthlyFeeUsd, r.DefaultModel, r.BaseUrl, r.MaxParallel, r.RequestsPerMinute, r.Notes,
                }).ToList(),
                lanes,
                services,
                spendingOptions = Enum.GetNames<LlmSpending>(),
                thinkingOptions = new[] { "off", "low", "medium", "high" },
                serviceTierOptions = new[] { "default", "flex", "priority" },
                circuits = llm.Circuits.Describe(),
            };
        }

        private static async Task<object> RoutersView(KliveLLM llm)
        {
            var ledger = llm.Ledger;
            var live = ledger?.Health.Live().ToDictionary(l => l.Router, StringComparer.OrdinalIgnoreCase) ?? new();
            long now = Now();
            var hours = ledger?.Db.ReadRouterRows("router_1h", now - 30L * 86_400_000, now + 3_600_000) ?? new();
            var byRouter = hours.GroupBy(h => h.Router, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var traffic = new Dictionary<string, (long Calls, long Tokens)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in ledger?.Db.ReadAggregates(LlmTier.H1, LlmTiers.Align(now - 86_400_000, LlmTier.H1), now + 3_600_000) ?? new())
                {
                    traffic.TryGetValue(row.Router, out var t);
                    traffic[row.Router] = (t.Calls + row.Agg.Calls, t.Tokens + row.Agg.TotalTokens);
                }
            }
            catch { }
            long totalCalls = Math.Max(1, traffic.Values.Sum(t => t.Calls));
            var list = new List<object>();
            foreach (var router in llm.Routers().Values.OrderBy(r => r.BuiltIn ? 0 : 1).ThenBy(r => r.Name))
            {
                bool keySet = false;
                string? configError = null;
                string? defaultModel = null;
                string? endpoint = router.BaseUrl;
                if (!router.IsLocal)
                {
                    try
                    {
                        var config = await llm.BuildRouterConfigurationAsync(router);
                        keySet = config != null;
                        defaultModel = config?.Model;
                        endpoint = config?.ChatCompletionsEndpoint ?? endpoint;
                        if (config == null && router.Id == LlmRouterIds.OpenRouterFree) configError = "FreeOpenRouterModelID is switched off.";
                    }
                    catch (InvalidOperationException ex) { configError = ex.Message; }
                }
                else keySet = true;
                byRouter.TryGetValue(router.Id, out var rows);
                live.TryGetValue(router.Id, out var state);
                traffic.TryGetValue(router.Id, out var t);
                list.Add(new
                {
                    router.Id, router.Name, router.Kind, router.BuiltIn, router.Billing, router.Enabled, router.MonthlyFeeUsd,
                    router.MaxParallel, router.RequestsPerMinute, router.Notes, router.BaseUrl,
                    keySet,
                    configError,
                    defaultModel = defaultModel ?? router.DefaultModel,
                    endpoint,
                    live = state,
                    uptime = new { h24 = Uptime(rows, now - 86_400_000), d7 = Uptime(rows, now - 7 * 86_400_000L), d30 = Uptime(rows, now - 30 * 86_400_000L) },
                    latency24h = Latency(rows, now - 86_400_000),
                    traffic24h = new { calls = t.Calls, tokens = t.Tokens, share = Math.Round((double)t.Calls / totalCalls, 4) },
                    circuits = llm.Circuits.Describe().Where(c => string.Equals(c.Router, router.Id, StringComparison.OrdinalIgnoreCase)).ToList(),
                    keySetting = router.IsLocal ? null : router.KeySetting,
                });
            }
            return new { asOf = now, routers = list, defaultRouter = llm.Routing.Current.Default.Router ?? llm.DefaultRouterIdNow() };
        }

        private static object Uptime(List<(long Ts, string Router, string Json)>? rows, long sinceMs)
        {
            int up = 0, degraded = 0, down = 0, none = 0;
            foreach (var row in rows ?? new())
            {
                if (row.Ts < LlmTiers.Align(sinceMs, LlmTier.H1)) continue;
                var hour = JsonConvert.DeserializeObject<LlmRouterHour>(row.Json);
                if (hour == null) continue;
                up += hour.Up; degraded += hour.Degraded; down += hour.Down; none += hour.NoData;
            }
            int measured = up + degraded + down;
            return new
            {
                upMinutes = up,
                degradedMinutes = degraded,
                downMinutes = down,
                noDataMinutes = none,
                availabilityPct = measured > 0 ? Math.Round(100.0 * (up + degraded) / measured, 3) : (double?)null,
                cleanPct = measured > 0 ? Math.Round(100.0 * up / measured, 3) : (double?)null,
            };
        }

        private static object Latency(List<(long Ts, string Router, string Json)>? rows, long sinceMs)
        {
            var merged = new KliveAPI.Telemetry.LogHistogram();
            foreach (var row in rows ?? new())
            {
                if (row.Ts < LlmTiers.Align(sinceMs, LlmTier.H1)) continue;
                var hour = JsonConvert.DeserializeObject<LlmRouterHour>(row.Json);
                if (hour?.LatencyHist != null) merged.Merge(LlmRouterHealth.Decode(hour.LatencyHist));
            }
            return merged.Count == 0 ? new { count = 0L, p50 = (double?)null, p95 = (double?)null, p99 = (double?)null }
                : new { count = merged.Count, p50 = (double?)Math.Round(merged.Percentile(0.5)), p95 = (double?)Math.Round(merged.Percentile(0.95)), p99 = (double?)Math.Round(merged.Percentile(0.99)) };
        }

        private static async Task<object> RouterDetail(KliveLLM llm, LlmTelemetry ledger, LlmQueryEngine queries, LlmRouterDefinition router, NameValueCollection p)
        {
            var parameters = new NameValueCollection(p) { ["router"] = router.Id };
            if (parameters["range"] == null && parameters["from"] == null) parameters["range"] = "7d";
            if (parameters["metrics"] == null) parameters["metrics"] = "calls,attempts,p50ProviderMs,p95ProviderMs,p99ProviderMs,p95TtftMs,attemptFailurePct,attemptRateLimited,tokensPerSec,cacheHitPct";
            if (!LlmQuerySpec.TryParse(parameters, Now(), Earliest(ledger), ledger.Options, out var spec, out string? error))
                throw new RouteError(error ?? "Invalid query.");
            long now = Now();
            var strip = ledger.Db.ReadRouterRows("router_1h", LlmTiers.Align(now - 90L * 86_400_000, LlmTier.H1), now + 3_600_000, router.Id)
                .Select(r => JsonConvert.DeserializeObject<LlmRouterHour>(r.Json))
                .Where(h => h != null)
                .Select(h => new { ts = h!.Ts, up = h.Up, degraded = h.Degraded, down = h.Down, noData = h.NoData, probeOk = h.ProbeOk, probeFail = h.ProbeFail })
                .ToList();
            var minutes = ledger.Db.ReadRouterRows("router_1m", now - 6 * 3_600_000L, now + 60_000, router.Id)
                .Select(r => JsonConvert.DeserializeObject<LlmRouterMinute>(r.Json))
                .Where(m => m != null)
                .Select(m => new { ts = m!.Ts, state = m.State, probeMs = m.ProbeOk + m.ProbeFail > 0 ? Math.Round(m.ProbeMsSum / (m.ProbeOk + m.ProbeFail)) : (double?)null, gauges = m.Gauges })
                .ToList();
            var providerSpec = Clone(spec, "provider");
            var modelSpec = Clone(spec, "model");
            var laneSpec = Clone(spec, "lane");
            return new
            {
                router = new { router.Id, router.Name, router.Kind, router.BuiltIn, router.Billing, router.Enabled, router.MonthlyFeeUsd, router.BaseUrl, router.DefaultModel, router.Notes },
                live = ledger.Health.Live().FirstOrDefault(l => string.Equals(l.Router, router.Id, StringComparison.OrdinalIgnoreCase)),
                hours = strip,
                minutes,
                series = queries.Series(spec),
                distribution = queries.Distribution(spec),
                providers = queries.Breakdown(providerSpec),
                models = queries.Breakdown(modelSpec),
                lanes = queries.Breakdown(laneSpec),
                catalogue = ledger.Health.Models(router.Id)?.OrderBy(m => m).Take(400).ToList(),
                prices = llm.Prices.All().Where(e => string.Equals(e.Router, router.Id, StringComparison.OrdinalIgnoreCase)).Take(400).ToList(),
                circuits = llm.Circuits.Describe().Where(c => string.Equals(c.Router, router.Id, StringComparison.OrdinalIgnoreCase)).ToList(),
            };
        }

        private static LlmQuerySpec Clone(LlmQuerySpec spec, string groupBy)
        {
            var copy = new LlmQuerySpec
            {
                Range = spec.Range, FromMs = spec.FromMs, ToMs = spec.ToMs, BucketMs = spec.BucketMs, Tier = spec.Tier, GroupBy = groupBy, Top = 20,
            };
            foreach (var (k, v) in spec.Filters) copy.Filters[k] = v;
            copy.Metrics.AddRange(LlmAggregate.DefaultSummary);
            return copy;
        }

        private static async Task<object> TestRouterAsync(KliveLLM llm, LlmRouterDefinition router, string? model, bool completion)
        {
            if (router.IsLocal) return new { ok = true, note = "The local model is in-process; nothing to reach." };
            KliveLLM.RemoteLLMProviderConfiguration? config;
            try { config = await llm.BuildRouterConfigurationAsync(router); }
            catch (InvalidOperationException ex) { return new { ok = false, stage = "configuration", error = ex.Message }; }
            if (config == null) return new { ok = false, stage = "configuration", error = "The free model is switched off." };

            var timer = System.Diagnostics.Stopwatch.StartNew();
            object? reply = null;
            string? error = null;
            int? status = null;
            if (completion)
            {
                // One real request with a one-token answer: proves the key, the model and the route end to end.
                var caller = new LlmCaller("KliveLLM", "router-test", router.Id, "Router test from the KliveLLM page");
                using var call = llm.BeginCall(caller, "chat", null, AIRouterWorkClass.Interactive, stream: false);
                try
                {
                    string chosen = string.IsNullOrWhiteSpace(model) ? config.Model : model.Trim();
                    call.Trace.SetRoute(router.Id, router.Name, chosen, LlmCosting.BillingName(router.Billing));
                    var payload = new HFWrapper.HFLLMInferenceRequest { model = chosen, stream = false, max_tokens = 1 };
                    payload.BuildMessagesFromList(new List<HFWrapper.HFMessage> { new() { role = "user", content = "Reply with one word: ok" } });
                    var response = await llm.SendPayloadWithRetryAsync(config, payload, CancellationToken.None, workClass: AIRouterWorkClass.Interactive);
                    call.Succeed(response);
                    reply = new
                    {
                        model = response.model,
                        content = HFWrapper.ContentToText(response.choices[0].message?.content),
                        promptTokens = response.usage?.prompt_tokens,
                        completionTokens = response.usage?.completion_tokens,
                        costUsd = response.usage?.cost,
                    };
                }
                catch (Exception ex)
                {
                    call.Fail(ex, cancelled: false);
                    error = ex.Message;
                    status = (ex as HttpRequestException)?.StatusCode is { } s ? (int)s : null;
                }
            }
            else
            {
                string url = router.Kind == "openrouter" ? "https://openrouter.ai/api/v1/key"
                    : config.ChatCompletionsEndpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                        ? config.ChatCompletionsEndpoint[..^"/chat/completions".Length] + "/models"
                        : config.ChatCompletionsEndpoint.TrimEnd('/') + "/models";
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
                    using var response = await http.SendAsync(request);
                    status = (int)response.StatusCode;
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) error = $"HTTP {status}: {(body.Length > 300 ? body[..300] : body)}";
                    else
                    {
                        int models = 0;
                        try { models = (JObject.Parse(body)["data"] as JArray)?.Count ?? 0; } catch { }
                        reply = new { models };
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            }
            timer.Stop();
            llm.Ledger?.Health.NoteProbe(router.Id, error == null, timer.Elapsed.TotalMilliseconds, error);
            return new { ok = error == null, stage = completion ? "completion" : "reachability", ms = timer.ElapsedMilliseconds, status, error, reply };
        }

        // ── exports ──

        private static string Csv(object? value)
        {
            string text = value switch
            {
                null => "",
                double d => d.ToString("0.########", CultureInfo.InvariantCulture),
                float f => f.ToString("0.########", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
            return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        private static string CallsCsv(KliveLLM llm, LlmTelemetry ledger, NameValueCollection p)
        {
            var filter = CallFilter(p, ledger);
            filter.Limit = 500;
            var sb = new StringBuilder();
            sb.AppendLine("id,ended_utc,service,lane,subject,kind,router,provider,model_requested,model,outcome,http_status,attempts,prompt_tokens,cached_tokens,cache_write_tokens,completion_tokens,reasoning_tokens,cost_usd,est_cost_usd,billing,queue_ms,provider_ms,total_ms,ttft_ms,flags,error");
            int rows = 0;
            while (rows < 50_000)
            {
                var page = ledger.Db.QueryCalls(filter, out _);
                foreach (var r in page)
                {
                    sb.AppendJoin(',', new[]
                    {
                        Csv(r.Id), Csv(DateTimeOffset.FromUnixTimeMilliseconds(r.EndedMs).UtcDateTime.ToString("o")), Csv(r.Service), Csv(r.Lane), Csv(r.Subject),
                        Csv(r.Kind), Csv(r.Router), Csv(r.Provider), Csv(r.ModelRequested), Csv(r.Model), Csv(r.Outcome), Csv(r.HttpStatus), Csv(r.Attempts),
                        Csv(r.PromptTokens), Csv(r.CachedTokens), Csv(r.CacheWriteTokens), Csv(r.CompletionTokens), Csv(r.ReasoningTokens), Csv(r.CostUsd),
                        Csv(r.EstimatedCostUsd ?? LlmCosting.CallCost(r, llm.Prices)), Csv(r.Billing), Csv(r.QueueMs), Csv(r.ProviderMs), Csv(r.TotalMs), Csv(r.TtftMs),
                        Csv(r.Flags.ToString()), Csv(r.Error),
                    });
                    sb.AppendLine();
                }
                rows += page.Count;
                if (page.Count < filter.Limit) break;
                if (filter.Sort is "newest" or "oldest") { filter.BeforeMs = page[^1].EndedMs; filter.BeforeId = page[^1].Id; }
                else filter.Offset += page.Count;
            }
            return sb.ToString();
        }

        private static string SeriesCsv(object series)
        {
            var json = JObject.FromObject(series, JsonSerializer.Create(Json));
            long from = json.Value<long>("from"), step = json.Value<long>("step");
            int count = json.Value<int>("count");
            var metrics = (json["metrics"] as JArray)?.Select(m => (string)m!).ToList() ?? new();
            var groups = (json["groups"] as JArray)?.OfType<JObject>().ToList() ?? new();
            var sb = new StringBuilder("bucket_utc");
            var columns = new List<(string Header, JArray? Values)>();
            if (groups.Count == 0)
                foreach (string metric in metrics) columns.Add((metric, json["totalSeries"]?[metric] as JArray));
            else
                foreach (var g in groups)
                    foreach (string metric in metrics) columns.Add(($"{g.Value<string>("label")} {metric}", g["series"]?[metric] as JArray));
            foreach (var c in columns) sb.Append(',').Append(Csv(c.Header));
            sb.AppendLine();
            for (int i = 0; i < count; i++)
            {
                sb.Append(Csv(DateTimeOffset.FromUnixTimeMilliseconds(from + i * step).UtcDateTime.ToString("o")));
                foreach (var c in columns) sb.Append(',').Append(Csv(c.Values != null && i < c.Values.Count ? c.Values[i]?.ToObject<double?>() : null));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
