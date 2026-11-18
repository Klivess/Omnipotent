using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.Demiurge.Stimulus;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// REST routes for Demiurge. Everything is Klives-only — Demiurge is Klives' personal
    /// autonomous task force, the same trust boundary as KliveAgent (not Stratum's
    /// multi-user Guest+ownership model).
    ///
    /// All responses serialize camelCase (JS-idiomatic) via <see cref="Json"/>, so the website
    /// reads fields as `e.type` / `p.stewardID` directly — real classes (StewardEvent, StewardSettings,
    /// …) would otherwise emit PascalCase under Newtonsoft's default and read as undefined.
    /// </summary>
    public class DemiurgeRoutes
    {
        private readonly Demiurge parent;
        private readonly ConcurrentDictionary<string, Queue<DateTime>> webhookRateWindows = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> webhookDeliveryIds = new(StringComparer.Ordinal);
        private const int WebhookRequestsPerMinute = 60;

        private static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            // Enums travel as strings ("Active", not 1) — the website compares/lowercases them.
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };
        internal static string Json(object o) => JsonConvert.SerializeObject(o, CamelCase);

        /// <summary>The request's JSON body as an object, or null when there isn't one.</summary>
        private static Newtonsoft.Json.Linq.JObject? ParseBody(Services.KliveAPI.KliveAPI.UserRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.userMessageContent)) return null;
            try { return Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent); }
            catch { return null; }
        }

        public DemiurgeRoutes(Demiurge parent)
        {
            this.parent = parent;
        }

        /// <summary>How long a lifecycle route waits for its action before answering that it is still settling.</summary>
        internal static TimeSpan LifecycleWait { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>How long an analytics request waits for a report never built before answering 202 {pending}.</summary>
        internal static TimeSpan AnalyticsWait { get; set; } = TimeSpan.FromSeconds(6);

        private StewardActionTrace NewTrace(string action) => new(action, message => _ = parent.ServiceLog(message));

        /// <summary>
        /// Runs a lifecycle action (pause, resume, archive, halt-all, unhalt-all, clearing the cache
        /// halt, a message) off the request thread and waits for it at most <see cref="LifecycleWait"/>.
        /// Healthy, these take milliseconds; in October every one Klives clicked held its request for
        /// an hour or more and the page gave up (see <see cref="StewardActionTrace"/>). The action is
        /// never abandoned: if it outlives the wait the route answers 202 with pending:true and the
        /// step it is in, the work finishes in the background, and the trace still logs a stuck step.
        /// </summary>
        internal Task<(bool Completed, T? Result)> RunLifecycleActionAsync<T>(
            Services.KliveAPI.KliveAPI.UserRequest req, StewardActionTrace trace, Func<T> action)
            => RunBoundedAsync(req.Trace, trace, action, LifecycleWait, ex =>
                _ = parent.ServiceLogError(ex, "Demiurge: a lifecycle action failed after its request had already answered"));

        internal static async Task<(bool Completed, T? Result)> RunBoundedAsync<T>(
            Services.KliveAPI.Telemetry.RequestTrace? requestTrace, StewardActionTrace trace, Func<T> action,
            TimeSpan wait, Action<Exception>? lateFailure)
        {
            Task<T> work;
            // Nothing ambient (request trace, response-cache scope) may follow the work past the response.
            using (ExecutionContext.SuppressFlow()) work = Task.Run(action);
            var finished = await Task.WhenAny(work, Task.Delay(wait));
            if (finished == work)
            {
                try { return (true, await work); }
                finally
                {
                    trace.AttachSpans(requestTrace);
                    trace.Dispose();
                }
            }
            _ = work.ContinueWith(t =>
            {
                if (t.IsFaulted) lateFailure?.Invoke(t.Exception!.GetBaseException());
                trace.Dispose();
            }, TaskScheduler.Default);
            return (false, default);
        }

        private static Task ReturnPendingAsync(Services.KliveAPI.KliveAPI.UserRequest req, string action,
            StewardActionTrace trace, string stewardID)
            => req.ReturnResponse(Json(new
            {
                ok = true,
                pending = true,
                action,
                stewardID,
                step = trace.CurrentStep,
                message = $"The {action} is still being applied and will finish by itself.",
            }), code: HttpStatusCode.Accepted);

        public async Task RegisterRoutes()
        {
            await parent.RegisterHttpRouteAsync("/demiurge/overview", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("Steward overview materialized response");
                    string? json = parent.OverviewSnapshots.Get(req.userParameters?.Get("range") ?? "24h");
                    await req.ReturnResponse(json ?? "{\"pending\":true}",
                        code: json == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            await parent.RegisterHttpRouteAsync("/demiurge/result-pin", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = ParseBody(req) ?? throw new ArgumentException("JSON body required.");
                    if (!body.ContainsKey("observableID")) throw new ArgumentException("Provide observableID, or null to clear the pin.");
                    string? id = (string?)body["observableID"];
                    var selection = id == null ? null : StewardOverviewService.ValidateSelection(
                        parent.Observables.List(steward!.StewardID), id, (string?)body["direction"] ?? "neutral",
                        "Selected by Klives");
                    parent.Store.SetResultSelection(steward!.StewardID, selection, userPin: true);
                    parent.EventLog.Append(new StewardEvent { StewardID = steward.StewardID, Type = StewardEventTypes.ObservableChanged,
                        Author = "klives", Text = selection == null ? "Using Commander result selection." : "Primary result pinned by Klives." });
                    await req.ReturnResponse(Json(new { saved = true }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            // ── Demiurge ──
            await parent.RegisterHttpRouteAsync("/demiurge/list", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("Steward list materialized response");
                    string? json = parent.ListSnapshots.Get();
                    await req.ReturnResponse(json ?? "{\"pending\":true}",
                        code: json == null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            await parent.RegisterHttpRouteAsync("/demiurge/create", async req =>
            {
                try
                {
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string name = (string?)body?.name ?? "";
                    string goal = (string?)body?.goal ?? "";
                    double tokenBudget = (double?)body?.tokenBudgetUsd ?? 0;
                    double moneyBudget = (double?)body?.moneyBudgetUsd ?? 0;
                    double moneyThreshold = (double?)body?.moneyAutonomousThresholdUsd ?? 0;
                    int agentCap = (int?)body?.subAgentCap ?? 12;
                    string? initialUploadSessionID = ((string?)body?.initialUploadSessionID)?.Trim();

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(goal))
                    {
                        await req.ReturnResponse("name and goal required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (tokenBudget <= 0)
                    {
                        await req.ReturnResponse("tokenBudgetUsd must be > 0 — a Steward is a goal AND a budget", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (!double.IsFinite(tokenBudget) || !double.IsFinite(moneyBudget) || !double.IsFinite(moneyThreshold) ||
                        moneyBudget < 0 || moneyThreshold < 0 || agentCap < 1)
                    {
                        await req.ReturnResponse("budgets must be finite/non-negative and subAgentCap must be at least 1", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    // Optional per-Steward settings configured on the new-Steward page (model routing,
                    // vision/containers, desktop image), applied at creation before the first wake.
                    Dictionary<string, Newtonsoft.Json.Linq.JToken>? settingsPatch = null;
                    if (body?.settings != null)
                    {
                        try { settingsPatch = ((Newtonsoft.Json.Linq.JObject)body.settings).Properties()
                            .ToDictionary(p => p.Name, p => p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase); }
                        catch (Exception sex) { _ = parent.ServiceLogError(sex, "Demiurge: parsing create-time settings failed (using defaults)"); }
                    }

                    var creator = new StewardFileActor(StewardFileActorType.User,
                        req.user!.UserID, req.user.Name ?? "Klives");
                    var p = await parent.CreateStewardAsync(name, goal, tokenBudget, moneyBudget, moneyThreshold,
                        agentCap, settingsPatch, initialUploadSessionID, creator);
                    await req.ReturnResponse(Json(p));
                }
                catch (StewardFileConflictException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.Conflict); }
                catch (StewardFileException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            await parent.RegisterHttpRouteAsync("/demiurge/get", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(steward));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/state", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(new
                    {
                        Steward = steward,
                        Runtime = parent.RuntimeState.Get(steward!.StewardID),
                        GrandPlan = parent.GrandPlans.GetCurrentApproved(steward.StewardID),
                        Budget = parent.Budget.GetLedger(steward.StewardID),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/pause", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string pid = steward!.StewardID;
                    var trace = NewTrace("pause");
                    var (done, paused) = await RunLifecycleActionAsync(req, trace, () =>
                    {
                        StewardStatus fromStatus = steward.Status;
                        steward.Status = StewardStatus.Paused;
                        steward.HaltedFromStatus = null; // individual pause overrides any remembered fleet-halt state
                        Demiurge.Timed(trace, pid, "save-status", () => parent.Store.SaveSteward(steward));
                        if (parent.DiscordManager != null)
                            _ = parent.DiscordManager.SyncStewardChannelAsync(steward);
                        Demiurge.Timed(trace, pid, "set-pausing", () => parent.RuntimeState.SetDisposition(pid, StewardExecutionDisposition.Pausing));
                        // Halt the in-flight wake too, so "pause" stops work promptly rather than only
                        // preventing the NEXT wake (item 1: halt progression).
                        bool halted = Demiurge.Timed(trace, pid, "cancel-wake", () => parent.CommanderRunner.CancelActiveWake(pid));
                        Demiurge.Timed(trace, pid, "cancel-agents", () => parent.SubAgentRunner.CancelSteward(pid));
                        // Only a cancelled wake's ending settles Pausing; with none, settle it here.
                        if (!halted)
                            Demiurge.Timed(trace, pid, "set-paused", () => parent.RuntimeState.SetDisposition(pid, StewardExecutionDisposition.Paused));
                        Demiurge.Timed(trace, pid, "record-event", () => parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = pid,
                            Type = StewardEventTypes.Status,
                            Author = "klives",
                            PayloadJson = StewardLifecycleEvents.Payload(
                                fromStatus, StewardStatus.Paused, "manual-pause"),
                            Text = halted ? "Steward paused by Klives — in-flight wake halted." : "Steward paused by Klives.",
                        }));
                        return steward;
                    });
                    if (done) await req.ReturnResponse(Json(paused!));
                    else await ReturnPendingAsync(req, "pause", trace, pid);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // ── Archive / unarchive (shelving, item 2) ──
            await parent.RegisterHttpRouteAsync("/demiurge/archive", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string pid = steward!.StewardID;
                    var trace = NewTrace("archive");
                    var (done, archived) = await RunLifecycleActionAsync(req, trace, () =>
                    {
                        StewardStatus fromStatus = steward.Status;
                        steward.Status = StewardStatus.Archived;
                        steward.HaltedFromStatus = null; // shelving overrides any remembered fleet-halt state
                        Demiurge.Timed(trace, pid, "save-status", () => parent.Store.SaveSteward(steward));
                        if (parent.DiscordManager != null)
                            _ = parent.DiscordManager.SyncStewardChannelAsync(steward);
                        Demiurge.Timed(trace, pid, "set-archived", () => parent.RuntimeState.SetDisposition(pid, StewardExecutionDisposition.Archived));
                        Demiurge.Timed(trace, pid, "cancel-wake", () => parent.CommanderRunner.CancelActiveWake(pid)); // shelved Stewards do no work
                        Demiurge.Timed(trace, pid, "cancel-agents", () => parent.SubAgentRunner.CancelSteward(pid));
                        Demiurge.Timed(trace, pid, "record-event", () => parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = pid,
                            Type = StewardEventTypes.Status,
                            Author = "klives",
                            PayloadJson = StewardLifecycleEvents.Payload(
                                fromStatus, StewardStatus.Archived, "archive"),
                            Text = "Steward archived (shelved) by Klives.",
                        }));
                        return steward;
                    });
                    if (done) await req.ReturnResponse(Json(archived!));
                    else await ReturnPendingAsync(req, "archive", trace, pid);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            await parent.RegisterHttpRouteAsync("/demiurge/unarchive", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    // Restore to Paused (not Active) — Klives explicitly resumes when ready, so
                    // unshelving never silently sets the fleet back to work.
                    StewardStatus fromStatus = steward!.Status;
                    steward.Status = StewardStatus.Paused;
                    steward.HaltedFromStatus = null; // unshelving overrides any remembered fleet-halt state
                    parent.Store.SaveSteward(steward);
                    if (parent.DiscordManager != null)
                        await parent.DiscordManager.SyncStewardChannelAsync(steward);
                    parent.RuntimeState.SetDisposition(steward.StewardID, StewardExecutionDisposition.Paused);
                    parent.EventLog.Append(new StewardEvent
                    {
                        StewardID = steward.StewardID,
                        Type = StewardEventTypes.Status,
                        Author = "klives",
                        PayloadJson = StewardLifecycleEvents.Payload(
                            fromStatus, StewardStatus.Paused, "unarchive-to-paused"),
                        Text = "Steward unshelved by Klives (paused — resume to set it working).",
                    });
                    await req.ReturnResponse(Json(steward));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // ── Rename (agents learn of it via the log + wake seed, item 4) ──
            await parent.RegisterHttpRouteAsync("/demiurge/rename", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string newName = ((string?)body?.name ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        await req.ReturnResponse("name required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    string oldName = steward!.Name;
                    if (newName == oldName) { await req.ReturnResponse(Json(steward)); return; }
                    steward.Name = newName;
                    parent.Store.SaveSteward(steward);
                    // Logged as a Klives message so it lands in the Commander's recent-events window
                    // and it registers the rename on its next wake (the wake seed reads steward.Name).
                    parent.EventLog.Append(new StewardEvent
                    {
                        StewardID = steward.StewardID,
                        Type = StewardEventTypes.KlivesMessage,
                        Author = "klives",
                        Text = $"Steward renamed from \"{oldName}\" to \"{newName}\".",
                    });
                    if (parent.DiscordManager != null)
                    {
                        try { await parent.DiscordManager.RenameStewardChannelAsync(steward); }
                        catch (Exception dex) { _ = parent.ServiceLogError(dex, "Demiurge: rename Discord channel failed"); }
                    }
                    await req.ReturnResponse(Json(steward));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // Klives-side budget editing (the Commander's own path stays request_budget_increase).
            // POST { stewardID, tokenBudgetUsd?, moneyBudgetUsd?, moneyAutonomousThresholdUsd?, subAgentCap? }
            // Only supplied fields change. Raising the token budget above current spend un-pauses a
            // BudgetPaused Steward; the change is logged as a KlivesMessage so the Commander sees it.
            await parent.RegisterHttpRouteAsync("/demiurge/budget/update", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    double? tokenBudget = (double?)body?.tokenBudgetUsd;
                    double? moneyBudget = (double?)body?.moneyBudgetUsd;
                    double? moneyThreshold = (double?)body?.moneyAutonomousThresholdUsd;
                    int? agentCap = (int?)body?.subAgentCap;

                    if (tokenBudget is <= 0)
                    {
                        await req.ReturnResponse("tokenBudgetUsd must be > 0 — a Steward is a goal AND a budget", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if ((tokenBudget.HasValue && !double.IsFinite(tokenBudget.Value)) ||
                        (moneyBudget.HasValue && !double.IsFinite(moneyBudget.Value)) ||
                        (moneyThreshold.HasValue && !double.IsFinite(moneyThreshold.Value)) ||
                        moneyBudget is < 0 || moneyThreshold is < 0 || agentCap is < 1)
                    {
                        await req.ReturnResponse("budgets must be finite, tokenBudgetUsd must be > 0, money limits must be ≥ 0, and subAgentCap ≥ 1", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    // Lowering the cap under a live roster used to be a 409 ("retire agents first"),
                    // which made the cap un-lowerable in exactly the case it exists for. It now applies
                    // immediately: SetAgentCapAsync reclaims the excess slots, preserves every retired
                    // agent's work as a handover, destroys their containers, and wakes the Commander.
                    var changes = new List<string>();
                    if (tokenBudget.HasValue && tokenBudget.Value != steward!.TokenBudgetUsd)
                    { changes.Add($"token budget ${steward.TokenBudgetUsd:0.##} → ${tokenBudget.Value:0.##}"); steward.TokenBudgetUsd = tokenBudget.Value; }
                    if (moneyBudget.HasValue && moneyBudget.Value != steward!.MoneyBudgetUsd)
                    { changes.Add($"money budget ${steward.MoneyBudgetUsd:0.##} → ${moneyBudget.Value:0.##}"); steward.MoneyBudgetUsd = moneyBudget.Value; }
                    if (moneyThreshold.HasValue && moneyThreshold.Value != steward!.MoneyAutonomousThresholdUsd)
                    { changes.Add($"autonomous threshold ${steward.MoneyAutonomousThresholdUsd:0.##} → ${moneyThreshold.Value:0.##}"); steward.MoneyAutonomousThresholdUsd = moneyThreshold.Value; }
                    // Recorded, but NOT applied here — the cap moves through SetAgentCapAsync below so a
                    // reduction and the retirements it forces are one operation, never a saved cap with
                    // an over-cap roster still running under it.
                    int requestedCap = agentCap.HasValue ? Math.Max(1, agentCap.Value) : steward!.SubAgentCap;
                    bool capChanging = agentCap.HasValue && requestedCap != steward!.SubAgentCap;
                    if (capChanging) changes.Add($"agent cap {steward!.SubAgentCap} → {requestedCap}");

                    if (changes.Count == 0) { await req.ReturnResponse(Json(steward)); return; }
                    parent.Store.SaveSteward(steward!);

                    Demiurge.AgentCapChangeResult? capChange = null;
                    if (capChanging)
                    {
                        capChange = await parent.SetAgentCapAsync(steward!, requestedCap, "klives");
                        if (capChange.Handovers.Count > 0)
                            changes.Add($"{capChange.Handovers.Count} agent(s) retired to fit the new cap "
                                + $"({string.Join(", ", capChange.RetiredAgentIDs)})");
                    }

                    // Re-arm the 80% warning for the new budget; un-pause if spend is back within it.
                    bool withinBudget = parent.Budget.NotifyBudgetChanged(steward!.StewardID);
                    StewardStatus? resumedFromStatus = null;
                    if (steward.Status == StewardStatus.BudgetPaused && withinBudget && tokenBudget.HasValue)
                    {
                        // Return to where it was paused — a never-approved plan resumes to Planning.
                        resumedFromStatus = steward.Status;
                        steward.Status = parent.GrandPlans.HasApprovedPlan(steward.StewardID)
                            ? StewardStatus.Active : StewardStatus.Planning;
                        steward.HaltedFromStatus = null; // budget-resume overrides any remembered fleet-halt state
                        parent.Store.SaveSteward(steward);
                        if (parent.DiscordManager != null)
                            await parent.DiscordManager.SyncStewardChannelAsync(steward);
                        parent.RuntimeState.SetDisposition(steward.StewardID, StewardExecutionDisposition.Running);
                        parent.RuntimeState.ClearBlocker(steward.StewardID);
                        changes.Add("Steward resumed from budget-pause");
                    }

                    // KlivesMessage (not Status) so it lands in the Commander's recent-events window.
                    parent.EventLog.Append(new StewardEvent
                    {
                        StewardID = steward.StewardID,
                        Type = StewardEventTypes.KlivesMessage,
                        Author = "klives",
                        Text = $"Budgets updated by Klives: {string.Join(", ", changes)}.",
                        PayloadJson = resumedFromStatus.HasValue
                            ? StewardLifecycleEvents.Payload(
                                resumedFromStatus.Value,
                                steward.Status,
                                "budget-increase-resume")
                            : null,
                    });
                    // Still the Steward object itself, with `retiredAgents` added ALONGSIDE its fields
                    // rather than wrapping it. A cap reduction has to be able to say who lost their
                    // slot — silently returning a smaller number is how a destructive action becomes
                    // invisible — but a wrapper would break every existing reader of this response,
                    // which reads it as the Steward. Additive is both.
                    var payload = JObject.Parse(Json(steward));
                    payload["retiredAgents"] = JArray.FromObject(
                        (capChange?.Handovers ?? Array.Empty<StewardAgentHandover>())
                            .Select(DescribeHandover).ToList(), JsonSerializer.Create(CamelCase));
                    await req.ReturnResponse(payload.ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // Klives removes specific agents by hand. Same instant, work-preserving path as a cap
            // reduction: wake cancelled, work snapshotted to a handover, milestones released,
            // agent-scoped directives re-pointed at the Commander, containers destroyed, Commander
            // woken. The cap itself is untouched — the freed slots stay available for re-staffing.
            // POST { stewardID, agentIDs: [..] } (or agentID: ".."), optional note.
            await parent.RegisterHttpRouteAsync("/demiurge/agents/retire", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    var requested = ParseStringArray(body["agentIDs"]).ToList();
                    string single = ((string?)body["agentID"] ?? "").Trim();
                    if (single.Length > 0) requested.Add(single);
                    requested = requested.Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (requested.Count == 0)
                    {
                        await req.ReturnResponse("agentID or agentIDs required", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var roster = parent.SubAgents.ListActive(steward!.StewardID);
                    var commanderAsked = requested.Where(id => roster.Any(a =>
                        string.Equals(a.AgentID, id, StringComparison.OrdinalIgnoreCase)
                        && StewardSubAgentManager.IsCommander(a))).ToList();
                    if (commanderAsked.Count > 0)
                    {
                        await req.ReturnResponse("the Commander cannot be retired — it is the agent the work is handed back to; "
                            + "pause or archive the Steward instead", code: HttpStatusCode.Conflict);
                        return;
                    }

                    // Helpers go with their parent, deepest first, so no orphan keeps a slot.
                    var ordered = StewardAgentRetirementPolicy.ExpandWithDescendants(roster, requested);
                    if (ordered.Count == 0)
                    {
                        await req.ReturnResponse("no active agent matched", code: HttpStatusCode.NotFound);
                        return;
                    }
                    string? note = ((string?)body["note"])?.Trim();
                    var handovers = await parent.RetireAgentsAsync(steward!, ordered.Select(a => a.AgentID),
                        StewardAgentRetirementReason.KlivesRemoved, "klives", notifyCommander: true,
                        context: string.IsNullOrWhiteSpace(note)
                            ? "Klives removed these agents directly. The slots stay available — re-staff them when the work warrants it."
                            : $"Klives removed these agents directly, saying: {note}");
                    await req.ReturnResponse(Json(new
                    {
                        ok = handovers.Count > 0,
                        retired = handovers.Select(DescribeHandover).ToList(),
                        slotsFree = Math.Max(0, steward!.SubAgentCap - parent.SubAgents.ListActive(steward.StewardID).Count),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // The unfinished work of retired agents. ?open=true is what the UI badges.
            await parent.RegisterHttpRouteAsync("/demiurge/agents/handovers", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    bool openOnly = string.Equals(req.userParameters?.Get("open"), "true", StringComparison.OrdinalIgnoreCase);
                    await req.ReturnResponse(Json(parent.Handovers.List(steward!.StewardID, openOnly)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/resume", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    if (steward!.Status == StewardStatus.Completed)
                    {
                        await req.ReturnResponse("completed Stewards cannot be resumed", code: HttpStatusCode.Conflict);
                        return;
                    }
                    if (steward.Status == StewardStatus.Archived)
                    {
                        await req.ReturnResponse("unarchive the Steward before resuming it", code: HttpStatusCode.Conflict);
                        return;
                    }
                    if (steward.Status == StewardStatus.BudgetPaused && !parent.Budget.IsWithinTokenBudget(steward.StewardID))
                    {
                        await req.ReturnResponse("raise the token budget above current spend before resuming", code: HttpStatusCode.Conflict);
                        return;
                    }
                    string pid = steward!.StewardID;
                    var trace = NewTrace("resume");
                    var (done, resumed) = await RunLifecycleActionAsync(req, trace, () =>
                    {
                        // A Steward paused mid-planning resumes to Planning (the Grand Plan gate still stands).
                        bool wasPlanning = !parent.GrandPlans.HasApprovedPlan(pid);
                        StewardStatus fromStatus = steward.Status;
                        steward.Status = wasPlanning ? StewardStatus.Planning : StewardStatus.Active;
                        bool wasBlocked = steward.BlockedAt.HasValue || !string.IsNullOrWhiteSpace(steward.BlockedReason);
                        steward.BlockedAt = null;
                        steward.BlockedReason = null;
                        steward.HaltedFromStatus = null; // individual resume overrides any remembered fleet-halt state
                        Demiurge.Timed(trace, pid, "save-status", () => parent.Store.SaveSteward(steward));
                        Demiurge.Timed(trace, pid, "set-running", () =>
                        {
                            parent.RuntimeState.SetDisposition(pid, StewardExecutionDisposition.Running);
                            parent.RuntimeState.ClearBlocker(pid);
                            parent.RuntimeState.CloseCircuit(pid);
                            // Closing the shared circuit is not enough: a wake is also refused while the
                            // per-actor provider-admission deadline stands (up to 15 minutes, or whatever
                            // Retry-After the router asked for). Klives pressing resume IS the instruction
                            // to try again now, so the deadline goes with the circuit — otherwise the
                            // resume wake is silently deferred and nothing happens until it expires.
                            parent.RuntimeState.ClearProviderAdmission(pid);
                        });
                        // A pause that landed in the wake's startup window armed a cancel-on-birth flag.
                        // Left set, it kills the very wake this resume is about to start.
                        parent.CommanderRunner.ClearPendingCancellation(pid);
                        // Agents may already be attached to Docker desktops that survived the pause or
                        // process restart, so the Desktops menu should see the same live fleet the agents
                        // use. But a reconcile lists Docker, inspects every container, restarts stopped
                        // ones and stops duplicates (15s grace each) — awaiting that here delayed the
                        // wake itself by however long Docker took. It runs alongside the resume instead.
                        _ = parent.RefreshDesktopRegistryAsync();
                        Demiurge.Timed(trace, pid, "record-event", () => parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = pid,
                            Type = wasBlocked ? StewardEventTypes.StewardUnblocked : StewardEventTypes.Status,
                            Author = "klives",
                            Text = "Steward resumed by Klives.",
                            PayloadJson = StewardLifecycleEvents.Payload(
                                fromStatus, steward.Status, wasBlocked ? "unblock" : "manual-resume"),
                        }));
                        // Urgent: Klives is watching this Steward restart, so the Commander skips the fleet's
                        // conversation budget and its first turn goes at interactive priority.
                        Demiurge.Timed(trace, pid, "wake-commander", () => parent.CommanderRunner.Wake(steward, wasPlanning
                            ? "Steward resumed by Klives — still in PLANNING. Continue converging on a Grand Plan and submit it for approval."
                            : "Steward resumed by Klives. Rehydrate current state and continue with the next concrete step.",
                            urgent: true));
                        // Pause cancelled every worker's in-flight wake too, and nothing else brings them
                        // back: the heartbeat only nudges an agent once its quiet period (20 min, doubling
                        // to 4 h) has elapsed since its LAST wake — which the cancelled one just reset.
                        // Resuming therefore has to re-wake the task force, or the Commander comes back
                        // alone and the Steward looks stalled for the whole interval.
                        Demiurge.Timed(trace, pid, "wake-workers", () => parent.ResumeWorkers(steward));
                        // Commands sent while paused are not discarded or falsely marked delivered;
                        // their durable directive records are re-injected as soon as work resumes.
                        Demiurge.Timed(trace, pid, "deliver-directives", () => parent.DeliverPendingDirectives(pid));
                        return steward;
                    });
                    if (done) await req.ReturnResponse(Json(resumed!));
                    else await ReturnPendingAsync(req, "resume", trace, pid);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // ── Fleet-wide controls (broadcast + global halt) ──

            // Durable broadcast. Default is a concrete task to each Commander; scope=all-agents
            // creates individually tracked tasks for every active worker too. The response is a
            // recipient-level receipt, never the old optimistic "delivered=N" claim.
            await parent.RegisterHttpRouteAsync("/demiurge/broadcast", async req =>
            {
                try
                {
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string text = ((string?)body["text"] ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    string scope = ((string?)body["scope"] ?? "commander").Trim();
                    bool remember = (bool?)body["remember"] ?? false;
                    int priority = Math.Clamp((int?)body["priority"] ?? 100, -1000, 1000);
                    var kind = ParseDirectiveKind((string?)body["kind"], StewardDirectiveKind.Task);
                    var expected = ParseStringArray(body["expectedArtifactPaths"]);
                    string broadcastID = Guid.NewGuid().ToString("N");
                    var receipts = parent.BroadcastMessageWithReceipts(text, scope, kind, remember, priority, expected, broadcastID);
                    await req.ReturnResponse(Json(new
                    {
                        ok = receipts.Any(x => x.Accepted),
                        broadcastID,
                        scope,
                        accepted = receipts.Count(x => x.Accepted),
                        delivered = receipts.Count(x => x.Status == "delivered"),
                        deferred = receipts.Count(x => x.Status == "deferred"),
                        rejected = receipts.Count(x => x.Status == "rejected"),
                        receipts,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.AgentsMessage);

            // Durable broadcast reconciliation. A caller can refresh this after a page reload to
            // answer “did every Steward actually finish its report?” without retaining receipts client-side.
            await parent.RegisterHttpRouteAsync("/demiurge/broadcast/status", async req =>
            {
                try
                {
                    string broadcastID = (req.userParameters?.Get("broadcastID") ?? "").Trim();
                    if (broadcastID.Length == 0)
                    {
                        await req.ReturnResponse("broadcastID required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var directives = parent.GetBroadcastDirectives(broadcastID);
                    if (directives.Count == 0)
                    {
                        await req.ReturnResponse("unknown broadcastID", code: HttpStatusCode.NotFound);
                        return;
                    }
                    await req.ReturnResponse(Json(new
                    {
                        broadcastID,
                        total = directives.Count,
                        active = directives.Count(x => x.Status is StewardDirectiveStatus.Active or StewardDirectiveStatus.Delivered),
                        acknowledged = directives.Count(x => x.Status == StewardDirectiveStatus.Acknowledged),
                        completed = directives.Count(x => x.Status == StewardDirectiveStatus.Completed),
                        failed = directives.Count(x => x.Status == StewardDirectiveStatus.Failed),
                        revoked = directives.Count(x => x.Status == StewardDirectiveStatus.Revoked),
                        directives,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            // Halt every Steward that isn't already halted (or terminal), remembering each Steward's
            // pre-halt status so unhalt-all restores it exactly. POST (no body).
            await parent.RegisterHttpRouteAsync("/demiurge/halt-all", async req =>
            {
                try
                {
                    var trace = NewTrace("halt-all");
                    var halted = new ConcurrentQueue<string>();
                    var (done, _) = await RunLifecycleActionAsync(req, trace, () =>
                    {
                        foreach (var p in parent.Store.ListStewards())
                            if (parent.HaltSteward(p.StewardID, trace: trace)) halted.Enqueue(p.StewardID);
                        return true;
                    });
                    var ids = halted.ToList();
                    await req.ReturnResponse(Json(new
                    {
                        ok = true,
                        pending = !done,
                        halted = ids.Count,
                        stewardIDs = ids,
                        step = done ? null : trace.CurrentStep,
                    }), code: done ? HttpStatusCode.OK : HttpStatusCode.Accepted);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.FleetControl);

            // Prompt-cache kill switch: the live trailing-window measurement plus the latch state.
            // Answers both "is the fleet stopped, and why" and — via the verdict's summary — "why
            // hasn't it fired", which is the question that matters while tuning the floor.
            await parent.RegisterHttpRouteAsync("/demiurge/cache-health", async req =>
            {
                try
                {
                    var options = parent.CacheHealth.Options;
                    var admission = parent.WakeAdmission.Describe();
                    await req.ReturnResponse(Json(new
                    {
                        halt = parent.CacheHealth.GetState(),
                        window = parent.CacheHealth.Describe(),
                        // The other half of the question "why is my Steward not running". A halt stops
                        // the fleet; the wake budget decides how much of it may be live at once, and a
                        // Steward can be perfectly healthy and simply waiting for a permit.
                        admission = DescribeAdmission(admission),
                        settings = new
                        {
                            enabled = options.Enabled,
                            windowMinutes = options.Window.TotalMinutes,
                            minimumWeightedHitRatePct = options.MinimumWeightedHitRatePct,
                            minimumMeasuredRequests = options.MinimumMeasuredRequests,
                            minimumMeasuredPromptTokens = options.MinimumMeasuredPromptTokens,
                            minimumObservationSpanMinutes = options.MinimumObservationSpan.TotalMinutes,
                            minimumReusablePrefixEfficiencyPct = options.MinimumReusablePrefixEfficiencyPct,
                            assumedPrefixLifetimeSeconds = options.AssumedPrefixLifetime.TotalSeconds,
                            minimumReusablePrefixSamples = options.MinimumReusablePrefixSamples,
                        },
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            // Release the prompt-cache halt. Body: {"unhalt": true} also restores every Steward to
            // the status it held before the halt; without it the latch opens but nothing resumes on
            // its own, which is the safer default while the cause is still being checked.
            await parent.RegisterHttpRouteAsync("/demiurge/cache-health/clear", async req =>
            {
                try
                {
                    bool unhalt = (bool?)ParseBody(req)?["unhalt"] ?? false;
                    string by = req.user?.Name ?? "klives";
                    var trace = NewTrace("cache-halt clear");
                    var (done, result) = await RunLifecycleActionAsync(req, trace, () => parent.ClearCacheHalt(by, unhalt, trace));
                    await req.ReturnResponse(Json(new
                    {
                        ok = true,
                        pending = !done,
                        step = done ? null : trace.CurrentStep,
                        cleared = done ? result.cleared : (bool?)null,
                        restored = done ? result.restored : 0,
                        stewardIDs = done ? result.stewardIDs : new List<string>(),
                        halt = parent.CacheHealth.GetState(),
                    }), code: done ? HttpStatusCode.OK : HttpStatusCode.Accepted);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.FleetControl);

            // Restore every globally-halted Steward to the exact status it held before the halt.
            // POST (no body).
            await parent.RegisterHttpRouteAsync("/demiurge/unhalt-all", async req =>
            {
                try
                {
                    var trace = NewTrace("unhalt-all");
                    var restoredQueue = new ConcurrentQueue<string>();
                    var (done, _) = await RunLifecycleActionAsync(req, trace, () =>
                    {
                        foreach (var p in parent.Store.ListStewards())
                            if (parent.UnhaltSteward(p.StewardID, trace)) restoredQueue.Enqueue(p.StewardID);
                        return true;
                    });
                    var restored = restoredQueue.ToList();
                    // Restoring statuses does not re-open LLM admission while the prompt-cache latch
                    // is still closed: every restored Steward would wake and immediately defer. Say
                    // so, rather than leaving Klives to infer it from a wave of deferred wakes.
                    string? cacheHalt = parent.CacheHealth.IsHalted
                        ? "The prompt-cache halt is still engaged, so no agent will send a request. "
                          + "Clear it with POST /demiurge/cache-health/clear."
                        : null;
                    await req.ReturnResponse(Json(new
                    {
                        ok = true,
                        pending = !done,
                        step = done ? null : trace.CurrentStep,
                        restored = restored.Count,
                        stewardIDs = restored,
                        warning = cacheHalt,
                    }), code: done ? HttpStatusCode.OK : HttpStatusCode.Accepted);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.FleetControl);

            // ── Timeline ──
            // Read for the website's timeline/conversation panels:
            //   GET /demiurge/events?stewardID=..&tail=true&max=..  — initial backlog (most-recent N)
            //   GET /demiurge/events?stewardID=..&since=..&max=..    — incremental page-forward
            // tail=true is load-bearing for the initial load: reading forward from since=0 returns the
            // OLDEST `max` events while the client advances its cursor to lastSequence, so any Steward
            // with more than `max` events showed ancient history and silently skipped everything after
            // it. The tail path returns the newest events so the panels open on current activity.
            await parent.RegisterHttpRouteAsync("/demiurge/events", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    long since = long.TryParse(req.userParameters?.Get("since"), out var s) ? s : 0;
                    int max = int.TryParse(req.userParameters?.Get("max"), out var m) ? Math.Clamp(m, 1, 2000) : 500;
                    bool tail = bool.TryParse(req.userParameters?.Get("tail"), out var t) && t;
                    var events = (tail && since <= 0)
                        ? parent.EventLog.ReadTail(steward!.StewardID, max)
                        : parent.EventLog.ReadSince(steward!.StewardID, since, max);
                    await req.ReturnResponse(Json(new
                    {
                        events,
                        // This is the last sequence INCLUDED in the response, not a separately-read
                        // high-water mark. If an append races this request, the client's HTTP/WS
                        // cursor remains behind it and the next replay delivers it instead of
                        // silently skipping an event that was never in this batch.
                        lastSequence = events.LastOrDefault()?.Sequence ?? since,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Lossless CSV export of the whole event log over a timeframe — the website's
            // "Download history" button. Every stored field is a column, so nothing is lost
            // relative to the on-disk JSONL (PayloadJson keeps its 32 KB write-time truncation;
            // media is referenced by artifact ID exactly as the log stores it).
            //   GET /demiurge/events/export?stewardID=..&from=<ISO8601>&to=<ISO8601>
            // from/to are optional and open-ended when omitted; both are treated as UTC.
            await parent.RegisterHttpRouteAsync("/demiurge/events/export", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    DateTime? from = ParseTimestamp(req.userParameters?.Get("from"));
                    DateTime? to = ParseTimestamp(req.userParameters?.Get("to"));

                    var sb = new StringBuilder();
                    sb.Append("sequence,eventID,timestampUtc,type,author,agentID,wakeID,toolName,toolCallId,gateID,stimulusID,artifactIDs,text,payloadJson\r\n");
                    long count = 0;
                    foreach (var e in parent.EventLog.EnumerateRange(steward!.StewardID, from, to))
                    {
                        sb.Append(Csv(e.Sequence.ToString(CultureInfo.InvariantCulture))).Append(',');
                        sb.Append(Csv(e.EventID)).Append(',');
                        sb.Append(Csv(e.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))).Append(',');
                        sb.Append(Csv(e.Type)).Append(',');
                        sb.Append(Csv(e.Author)).Append(',');
                        sb.Append(Csv(e.AgentID)).Append(',');
                        sb.Append(Csv(e.WakeID)).Append(',');
                        sb.Append(Csv(e.ToolName)).Append(',');
                        sb.Append(Csv(e.ToolCallId)).Append(',');
                        sb.Append(Csv(e.GateID)).Append(',');
                        sb.Append(Csv(e.StimulusID)).Append(',');
                        sb.Append(Csv(e.ArtifactIDs != null ? string.Join(";", e.ArtifactIDs) : "")).Append(',');
                        sb.Append(Csv(e.Text)).Append(',');
                        sb.Append(Csv(e.PayloadJson)).Append("\r\n");
                        count++;
                    }

                    string fileName = $"steward-{Sanitize(steward.Name)}-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
                    var headers = new NameValueCollection
                    {
                        ["Content-Disposition"] = $"attachment; filename=\"{fileName}\"",
                        ["X-Content-Type-Options"] = "nosniff",
                        ["X-Export-Event-Count"] = count.ToString(CultureInfo.InvariantCulture),
                    };
                    // Lead with a UTF-8 BOM so Excel opens non-ASCII text (emoji, accents) correctly.
                    byte[] bom = { 0xEF, 0xBB, 0xBF };
                    byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
                    byte[] payload = new byte[bom.Length + body.Length];
                    Buffer.BlockCopy(bom, 0, payload, 0, bom.Length);
                    Buffer.BlockCopy(body, 0, payload, bom.Length, body.Length);
                    await req.ReturnBinaryResponse(payload, "text/csv; charset=utf-8", headers: headers);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/digest", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(parent.Digests.GetDigest(steward!.StewardID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/ledger", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(parent.Budget.GetLedger(steward!.StewardID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Analytics answers never hold a request for a cold scan (see StewardAnalyticsService.ReadPortfolioAsync):
            // the last report at once (stale: true while one background build replaces it), or 202 {pending}.
            static async Task ReturnAnalyticsRead(Services.KliveAPI.KliveAPI.UserRequest req, object? value, bool stale, bool building)
            {
                CacheDeps.MarkUncacheable("analytics answered from a background build");
                if (value == null)
                {
                    await req.ReturnResponse(Json(new { pending = true, retryAfterSeconds = 4 }), code: HttpStatusCode.Accepted);
                    return;
                }
                var body = Newtonsoft.Json.Linq.JObject.FromObject(value, Newtonsoft.Json.JsonSerializer.Create(CamelCase));
                body["stale"] = stale;
                body["rebuilding"] = building;
                await req.ReturnResponse(body.ToString(Newtonsoft.Json.Formatting.None));
            }

            // Server-side analytics keep the browser from paging/scanning every Steward's raw log.
            // Lifetime ledger totals remain authoritative; the response also describes the coverage
            // of historical per-wake attribution for older records.
            await parent.RegisterHttpRouteAsync("/demiurge/analytics", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string range = req.userParameters?.Get("range") ?? "30d";
                    bool fresh = string.Equals(req.userParameters?.Get("fresh"), "1", StringComparison.Ordinal);
                    var read = await parent.Analytics.ReadStewardAsync(steward!.StewardID, range, fresh,
                        req.userParameters?.Get("from"), req.userParameters?.Get("to"), req.userParameters?.Get("bucket"), AnalyticsWait);
                    await ReturnAnalyticsRead(req, read.Value, read.Stale, read.Building);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/analytics/all", async req =>
            {
                try
                {
                    string range = req.userParameters?.Get("range") ?? "30d";
                    bool fresh = string.Equals(req.userParameters?.Get("fresh"), "1", StringComparison.Ordinal);
                    var read = await parent.Analytics.ReadPortfolioAsync(range, fresh,
                        req.userParameters?.Get("from"), req.userParameters?.Get("to"), req.userParameters?.Get("bucket"), AnalyticsWait);
                    await ReturnAnalyticsRead(req, read.Value, read.Stale, read.Building);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Cost simulator: recorded token counts, bucketed the way providers bill them, per
            // Steward and per agent. No prices here on purpose — the website multiplies, so Klives
            // can retune a price and see the answer without another scan of the usage journals.
            await parent.RegisterHttpRouteAsync("/demiurge/cost-simulator", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("Steward cost simulator materialized response");
                    string range = req.userParameters?.Get("range") ?? "30d";
                    bool fresh = string.Equals(req.userParameters?.Get("fresh"), "1", StringComparison.Ordinal);
                    bool includeArchived = !string.Equals(
                        req.userParameters?.Get("includeArchived"), "0", StringComparison.Ordinal);
                    long.TryParse(req.userParameters?.Get("after"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long after);
                    var result = parent.CostSimulatorSnapshots.Read(range,
                        req.userParameters?.Get("from"), req.userParameters?.Get("to"),
                        includeArchived, fresh, after);
                    if (result.Busy)
                        await req.ReturnResponse(Json(new { error = "Too many cost simulator builds are queued. Try again shortly." }),
                            code: HttpStatusCode.TooManyRequests);
                    else if (result.Pending)
                        await req.ReturnResponse(Json(new { pending = true,
                            after = result.After.ToString(CultureInfo.InvariantCulture),
                            from = result.FromUtc, to = result.ToUtc }),
                            code: HttpStatusCode.Accepted);
                    else
                        await req.ReturnResponse(result.Json!);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Agent roster (org chart) for the workspace's Agents panel.
            await parent.RegisterHttpRouteAsync("/demiurge/agents", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var roster = parent.SubAgents.ListActive(steward!.StewardID);
                    var now = DateTime.UtcNow;
                    var agents = roster.Select(a => new
                    {
                        a.AgentID,
                        a.Role,
                        Tier = a.Tier.ToString(),
                        a.ParentAgentID,
                        a.CreatedAt,
                        // Klives can now remove any of these by hand, so the list has to say what
                        // removing one would actually interrupt rather than just naming it.
                        Mission = a.MissionKind.ToString(),
                        WorkStatus = a.WorkStatus.ToString(),
                        a.Objective,
                        a.ActiveMilestoneIDs,
                        a.LastReport,
                        a.LastReportAt,
                        a.LastWakeAt,
                        IsCommander = StewardSubAgentManager.IsCommander(a),
                        Awake = parent.SubAgentRunner.IsAwake(steward.StewardID, a.AgentID),
                        Reclaimable = StewardSubAgentManager.IsReclaimable(a, now),
                        // Removing this agent takes its helpers with it; the UI should say so before
                        // Klives clicks, not after three agents disappear.
                        HelperAgentIDs = roster.Where(c =>
                            string.Equals(c.ParentAgentID, a.AgentID, StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.AgentID).ToList(),
                    }).ToList();
                    // Still a bare array: the website reads this as one, and the added fields are
                    // additive. Slot arithmetic and the open-handover count live on
                    // /demiurge/agents/handovers rather than changing this shape underneath it.
                    await req.ReturnResponse(Json(agents));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Who is mid-turn right now. The event stream pushes this live; this route only exists so
            // a panel can paint the indicator before its socket is up (or if the socket is down).
            await parent.RegisterHttpRouteAsync("/demiurge/activity", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(parent.Activity.ListForSteward(steward!.StewardID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Observables (the agents' live dashboard for this Steward). History is trimmed to the
            // last N samples server-side so the 1s-debounced refresh stays cheap; ?history=0 = values only.
            await parent.RegisterHttpRouteAsync("/demiurge/observables", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    int history = 120;
                    if (int.TryParse(req.userParameters?.Get("history"), out var h))
                        history = Math.Clamp(h, 0, StewardObservableStore.MaxHistorySamples);
                    var list = parent.Observables.List(steward!.StewardID).Select(o => new
                    {
                        o.ObservableID,
                        o.Name,
                        Type = o.Type.ToString(),
                        Format = o.Format.ToString(),
                        o.Unit,
                        o.Description,
                        o.NumericValue,
                        o.TextValue,
                        DisplayValue = StewardObservableStore.FormatValue(o),
                        o.CreatedBy,
                        o.UpdatedBy,
                        o.CreatedAt,
                        o.UpdatedAt,
                        History = history <= 0
                            ? new List<ObservableSample>()
                            : o.History.Skip(Math.Max(0, o.History.Count - history)).ToList(),
                    }).ToList();
                    await req.ReturnResponse(Json(list));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Manual cleanup of an agent-created observable (agents own the values; Klives can only prune).
            await parent.RegisterHttpRouteAsync("/demiurge/observables/delete", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string name = req.userParameters?.Get("name") ?? "";
                    if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        try { name = (string?)Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent)["name"] ?? ""; }
                        catch { /* body isn't a JSON object with name */ }
                    }
                    bool deleted = parent.Observables.Delete(steward!.StewardID, name);
                    if (deleted)
                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = steward.StewardID,
                            Type = StewardEventTypes.ObservableChanged,
                            Author = "klives",
                            Text = $"{name}: deleted by Klives",
                        });
                    await req.ReturnResponse(Json(new { deleted }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            // ── Step ledger (the Steward's linear path) ──
            // Reads go through RuntimeState.Get, so these responses participate in that store's existing
            // cache dependency and are invalidated by any checkpoint write.
            await parent.RegisterHttpRouteAsync("/demiurge/steps", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var steps = parent.RuntimeState.ListSteps(steward!.StewardID).Select(s => new
                    {
                        s.StepID,
                        s.Title,
                        s.MilestoneID,
                        Status = s.Status.ToString(),
                        s.Order,
                        s.Attempts,
                        s.LastAttemptOutcome,
                        s.NextConcreteAction,
                        s.OwnerAgentID,
                        s.CreatedAt,
                        s.StartedAt,
                        s.UpdatedAt,
                        s.ClosedAt,
                        s.ClosureReason,
                        Evidence = s.Evidence.Select(e => e.Reference).ToList(),
                    }).ToList();
                    await req.ReturnResponse(Json(new
                    {
                        activeStepID = steps.FirstOrDefault(s => s.Status == nameof(StewardStepStatus.Active))?.StepID,
                        steps,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Klives can add steps to the path himself — the point of the panel is that the linear path is
            // steerable, not just observable.
            await parent.RegisterHttpRouteAsync("/demiurge/steps/add", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = ParseBody(req);
                    string title = ((string?)body?["title"] ?? "").Trim();
                    if (title.Length == 0) { await req.ReturnResponse(Json(new { error = "title required" })); return; }
                    var created = parent.RuntimeState.QueueSteps(steward!.StewardID, new[]
                    {
                        new StewardStep
                        {
                            Title = title,
                            MilestoneID = ((string?)body?["milestoneID"] ?? "").Trim() is { Length: > 0 } m ? m : null,
                            NextConcreteAction = ((string?)body?["nextAction"] ?? "").Trim() is { Length: > 0 } n ? n : null,
                        },
                    });
                    if (created.Count > 0)
                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = steward.StewardID,
                            Type = StewardEventTypes.CheckpointChanged,
                            Author = "klives",
                            Text = $"Klives queued step {created[0].StepID}: {created[0].Title}",
                        });
                    await req.ReturnResponse(Json(new { added = created.Select(s => s.StepID).ToList() }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            await parent.RegisterHttpRouteAsync("/demiurge/steps/reorder", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var ids = (ParseBody(req)?["stepIDs"] as Newtonsoft.Json.Linq.JArray)?.Values<string>()
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? new();
                    var result = parent.RuntimeState.ReorderSteps(steward!.StewardID, ids);
                    await req.ReturnResponse(Json(new { applied = result.Applied, reason = result.Reason }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            await parent.RegisterHttpRouteAsync("/demiurge/steps/activate", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string stepID = ((string?)ParseBody(req)?["stepID"] ?? "").Trim();
                    var result = parent.RuntimeState.ActivateStep(steward!.StewardID, stepID);
                    if (result.Applied)
                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = steward.StewardID,
                            Type = StewardEventTypes.CheckpointChanged,
                            Author = "klives",
                            Text = $"Klives made step {stepID} the active one.",
                        });
                    await req.ReturnResponse(Json(new { applied = result.Applied, reason = result.Reason }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            // Klives closing a step is a steer, so it needs no evidence gate for 'abandoned'/'blocked'; a
            // 'done' still does, since the evidence rule protects the record rather than the agent.
            await parent.RegisterHttpRouteAsync("/demiurge/steps/close", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = ParseBody(req);
                    string stepID = ((string?)body?["stepID"] ?? "").Trim();
                    string reason = ((string?)body?["reason"] ?? "Closed by Klives.").Trim();
                    var status = ((string?)body?["result"] ?? "abandoned").Trim().ToLowerInvariant() switch
                    {
                        "done" => StewardStepStatus.Done,
                        "blocked" => StewardStepStatus.Blocked,
                        _ => StewardStepStatus.Abandoned,
                    };
                    var evidence = status == StewardStepStatus.Done
                        ? new List<StewardEvidenceReference>
                        {
                            new() { Kind = StewardEvidenceKind.UserConfirmation, Reference = "klives-confirmed", Description = reason },
                        }
                        : null;
                    var result = parent.RuntimeState.CloseStep(steward!.StewardID, stepID, status, reason, evidence);
                    if (result.Applied)
                        parent.EventLog.Append(new StewardEvent
                        {
                            StewardID = steward.StewardID,
                            Type = StewardEventTypes.CheckpointChanged,
                            Author = "klives",
                            Text = $"Klives closed step {stepID} as {status}: {reason}",
                            PayloadJson = JsonConvert.SerializeObject(new { op = "close_step", stepID, result = status.ToString() }),
                        });
                    await req.ReturnResponse(Json(new { applied = result.Applied, reason = result.Reason }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            // ── Councils (adversarial deliberation transcripts) ──
            await parent.RegisterHttpRouteAsync("/demiurge/councils", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var list = parent.Councils.List(steward!.StewardID).Select(c => new
                    {
                        c.CouncilID,
                        c.Topic,
                        c.Purpose,
                        c.Urgency,
                        Status = c.Status.ToString(),
                        c.Roles,
                        c.Model,
                        StatementCount = c.Statements.Count,
                        c.TotalCostUsd,
                        c.CreatedAt,
                        c.CompletedAt,
                        c.Error,
                        VerdictExcerpt = string.IsNullOrEmpty(c.VerdictText) ? "" :
                            (c.VerdictText.Length <= 300 ? c.VerdictText : c.VerdictText[..300] + "…"),
                    }).ToList();
                    await req.ReturnResponse(Json(list));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/councils/get", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string councilID = req.userParameters?.Get("councilID") ?? "";
                    var council = parent.Councils.Get(steward!.StewardID, councilID);
                    if (council == null) { await req.ReturnResponse("no such council", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(Json(council));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // ── Grand Plan (the approved strategic north star + version history) ──
            await parent.RegisterHttpRouteAsync("/demiurge/grandplan", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var doc = parent.GrandPlans.Get(steward!.StewardID);
                    var current = parent.GrandPlans.GetCurrentApproved(steward.StewardID);
                    await req.ReturnResponse(Json(new
                    {
                        current,
                        versions = doc.Versions.OrderByDescending(v => v.Version).Select(v => new
                        {
                            v.Version,
                            v.Content,
                            v.Markdown,
                            v.Summary,
                            v.ChangeNote,
                            v.Material,
                            Status = v.Status.ToString(),
                            v.KlivesComment,
                            v.SubmittedAt,
                            v.ResolvedAt,
                        }).ToList(),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/computers/health", async req =>
            {
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("Live computer host health");
                try
                {
                    await req.ReturnResponse(Json(parent.Desktops == null
                        ? new { available = false, reason = "Desktop subsystem is disabled." }
                        : await parent.Desktops.GetHostHealthAsync()));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            // ── Site playbooks (global, shared by every Steward) ──
            await parent.RegisterHttpRouteAsync("/demiurge/playbooks", async req =>
            {
                try
                {
                    Services.KliveAPI.Caching.CacheDeps.NoteRead("demiurge:playbooks");
                    await req.ReturnResponse(Json(parent.Playbooks.List()));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            await parent.RegisterHttpRouteAsync("/demiurge/playbooks/save", async req =>
            {
                try
                {
                    var body = ParseBody(req) ?? new JObject();
                    static List<string>? Lines(JToken? token) => token is JArray array
                        ? array.Select(t => (string?)t ?? "").ToList()
                        : token?.Type == JTokenType.String
                            ? ((string?)token ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                            : null;
                    var urls = body["urls"] is JArray urlArray
                        ? urlArray.OfType<JObject>().Select(u => new SitePlaybookUrl { Label = (string?)u["label"] ?? "", Url = (string?)u["url"] ?? "" }).ToList()
                        : null;
                    var saved = parent.Playbooks.Save((string?)body["domain"] ?? "", "Klives", (string?)body["summary"],
                        Lines(body["steps"]), Lines(body["pitfalls"]), urls, (bool?)body["verified"] == true);
                    await req.ReturnResponse(Json(saved));
                }
                catch (InvalidOperationException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            await parent.RegisterHttpRouteAsync("/demiurge/playbooks/delete", async req =>
            {
                try
                {
                    string domain = (string?)ParseBody(req)?["domain"] ?? "";
                    await req.ReturnResponse(Json(new { deleted = parent.Playbooks.Delete(domain) }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // Every computer (or one Steward's) with what its in-container daemon reports: components,
            // browser, memory/OOM, recent events. Live by definition, so never cached.
            await parent.RegisterHttpRouteAsync("/demiurge/computers/list", async req =>
            {
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("Live computer inventory with daemon health");
                try
                {
                    if (parent.Desktops == null) { await req.ReturnResponse(Json(Array.Empty<object>())); return; }
                    string? stewardID = req.userParameters.Get("stewardID");
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await req.ReturnResponse(Json(await parent.Desktops.DescribeComputersAsync(
                        string.IsNullOrWhiteSpace(stewardID) ? null : stewardID, budget.Token)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.OverviewView);

            // The daemon's diagnostics for one computer: log tails, top memory users, its event journal.
            await parent.RegisterHttpRouteAsync("/demiurge/computers/diagnostics", async req =>
            {
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("Live computer diagnostics");
                try
                {
                    string? id = req.userParameters.Get("containerID");
                    if (parent.Desktops == null || string.IsNullOrWhiteSpace(id)
                        || parent.Desktops.Registry.All().All(r => r.ContainerID != id))
                    {
                        await req.ReturnResponse("Computer not found.", code: HttpStatusCode.NotFound);
                        return;
                    }
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    string? diag = await parent.Desktops.ComputerDiagnosticsAsync(id!, budget.Token);
                    await req.ReturnResponse(diag ?? "{\"ok\":false,\"error\":\"The computer's control daemon did not answer (asleep, outdated, or not yet authorised).\"}",
                        "application/json");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Named repairs, gentlest first: restart-browser, restart-components, restart-computer, refresh-helpers.
            await parent.RegisterHttpRouteAsync("/demiurge/computers/repair", async req =>
            {
                try
                {
                    var body = ParseBody(req);
                    string? id = (string?)body?["containerID"];
                    string action = (string?)body?["action"] ?? "";
                    var record = parent.Desktops?.Registry.All().LastOrDefault(r => r.ContainerID == id && !r.Lost);
                    if (record == null)
                    {
                        await req.ReturnResponse("Computer not found.", code: HttpStatusCode.NotFound);
                        return;
                    }
                    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    string outcome = await parent.Desktops!.RepairComputerAsync(record.ContainerID, action, budget.Token);
                    parent.NoteComputerRepair(record, action, outcome);
                    await req.ReturnResponse(Json(new { ok = true, record.ContainerID, action, outcome }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            await parent.RegisterHttpRouteAsync("/demiurge/computers/resume", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string? id = (string?)ParseBody(req)?["containerID"];
                    var record = parent.Desktops?.Registry.ForSteward(steward!.StewardID)
                        .FirstOrDefault(r => r.ContainerID == id && !r.Lost);
                    if (record == null)
                    {
                        await req.ReturnResponse("Computer not found in this Steward.", code: HttpStatusCode.NotFound);
                        return;
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    await parent.Desktops!.ResumeDesktopAsync(record.ContainerID, timeout.Token);
                    await req.ReturnResponse(Json(new { resumed = true, record.ContainerID }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // A Steward's desktop containers, so the live-view can offer them (and map agent → desktop).
            await parent.RegisterHttpRouteAsync("/demiurge/containers", async req =>
            {
                Services.KliveAPI.Caching.CacheDeps.MarkUncacheable("Live computer inventory");
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    // Never serve the persisted pre-startup snapshot. This also adopts a surviving
                    // labelled Docker desktop whose registry entry was lost, which is the state in
                    // which an agent can use a desktop that the menu cannot discover.
                    await parent.RefreshDesktopRegistryAsync();
                    var containers = (parent.Desktops?.Registry.ForSteward(steward!.StewardID) ?? new())
                        .Select(c => new { c.ContainerID, c.AgentID, c.Width, c.Height, c.Lost, c.Suspended }).ToList();
                    await req.ReturnResponse(Json(containers));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // ── Per-Steward settings (Demiurge's own setting system, not OmniSettings) ──
            await parent.RegisterHttpRouteAsync("/demiurge/settings", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(parent.Settings.Get(steward!.StewardID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Patch one or more settings. Route values are ordered JSON arrays; scalar settings
            // retain their natural JSON type.
            await parent.RegisterHttpRouteAsync("/demiurge/settings/update", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var patch = JObject.Parse(req.userMessageContent ?? "{}");
                    var settings = parent.Settings.Get(steward!.StewardID);
                    var applied = new List<string>();
                    var unknown = new List<string>();
                    bool stewardChanged = false;
                    foreach (var kv in patch.Properties())
                    {
                        if (kv.Name.Equals("stewardID", StringComparison.OrdinalIgnoreCase)) continue; // routing field, not a setting
                        if (kv.Name.Equals(StewardDesktopAllocation.SettingKey, StringComparison.OrdinalIgnoreCase))
                        {
                            if (StewardDesktopAllocation.TryParse(kv.Value.ToString(), out var allocation))
                            {
                                if (steward.DesktopAllocation != allocation)
                                {
                                    steward.DesktopAllocation = allocation;
                                    stewardChanged = true;
                                }
                                applied.Add(kv.Name);
                            }
                            else unknown.Add(kv.Name);
                            continue;
                        }
                        (settings.TrySet(kv.Name, kv.Value) ? applied : unknown).Add(kv.Name);
                    }
                    parent.Settings.Save(settings);
                    if (stewardChanged) parent.Store.SaveSteward(steward);
                    parent.EventLog.Append(new StewardEvent
                    {
                        StewardID = steward.StewardID,
                        Type = StewardEventTypes.Status,
                        Author = "klives",
                        Text = $"Settings updated: {string.Join(", ", applied)}." + (unknown.Count > 0 ? $" Unknown keys ignored: {string.Join(", ", unknown)}." : ""),
                    });
                    await req.ReturnResponse(Json(new { applied, unknown, settings, desktopAllocation = steward.DesktopAllocation }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.LifecycleManage);

            // Which request parameters the models on a route can actually be given, resolved LIVE from
            // OpenRouter's per-model `supported_parameters`. GET ?models=a,b,c (a route's ordered list).
            //
            // A route sends one primary plus OpenRouter's fallback models, and any of them may serve the
            // request, so `supportedByAll` marks the parameters that hold for the whole route — those are
            // the ones the UI offers without a caveat. When the catalog can't be reached every parameter
            // is still offered (fromCatalog=false): OpenRouter ignores a parameter a model doesn't
            // implement, so offering one is harmless, whereas hiding it would silently drop configuration.
            await parent.RegisterHttpRouteAsync("/demiurge/models/parameters", async req =>
            {
                try
                {
                    var models = (req.userParameters?.Get("models") ?? "")
                        .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(m => m.Trim()).Where(m => m.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

                    Task<IReadOnlyList<OpenRouterModelParameterSupport>> parameterSupportTask =
                        models.Count == 0 || parent.ProviderContexts == null
                            ? Task.FromResult<IReadOnlyList<OpenRouterModelParameterSupport>>([])
                            : parent.ProviderContexts.ResolveParametersAsync(models);
                    Task<IReadOnlyList<OpenRouterModelProviderSupport>> providerSupportTask =
                        models.Count == 0 || parent.ProviderContexts == null
                            ? Task.FromResult<IReadOnlyList<OpenRouterModelProviderSupport>>([])
                            : parent.ProviderContexts.ResolveProvidersAsync(models);
                    await Task.WhenAll(parameterSupportTask, providerSupportTask);
                    IReadOnlyList<OpenRouterModelParameterSupport> support = await parameterSupportTask;
                    IReadOnlyList<OpenRouterModelProviderSupport> providerSupport = await providerSupportTask;
                    var resolved = support.Where(s => s.FromCatalog).ToList();
                    var providerOptions = providerSupport
                        .Where(s => s.FromCatalog)
                        .SelectMany(s => s.Providers)
                        .GroupBy(p => p.Tag, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new
                        {
                            value = group.First().Tag,
                            label = group.First().Name,
                        })
                        .OrderBy(p => p.label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.value, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Every definition is returned so the UI can render a value already saved against a
                    // model the catalog no longer lists; `supportedBy` is what drives the dropdown.
                    var parameters = ModelParameterCatalog.Definitions.Select(d => new
                    {
                        name = d.Name,
                        label = d.Label,
                        kind = d.Kind == ModelParameterKind.MultiEnum
                            ? "multi-enum"
                            : d.Kind.ToString().ToLowerInvariant(),
                        min = d.Min,
                        max = d.Max,
                        step = d.Step,
                        description = d.Description,
                        defaultHint = d.DefaultHint,
                        openRouterOnly = d.OpenRouterOnly,
                        // Endpoint provider choices are fetched live for this exact model/fallback
                        // set. Their stored values are provider tags, while labels remain human-friendly.
                        options = string.Equals(d.Name, "provider", StringComparison.OrdinalIgnoreCase)
                            ? (object)providerOptions
                            : d.Options,
                        supportedBy = !d.ModelCapabilityGated
                            ? models
                            : resolved
                                .Where(s => s.SupportedParameters.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                                .Select(s => s.RequestedModel).ToList(),
                        // With no catalog answer at all, "unknown" is reported as supported so the
                        // parameter stays configurable rather than vanishing on a transient fetch failure.
                        supportedByAll = !d.ModelCapabilityGated || resolved.Count == 0 || resolved.All(
                            s => s.SupportedParameters.Contains(d.Name, StringComparer.OrdinalIgnoreCase)),
                    }).ToList();

                    await req.ReturnResponse(Json(new
                    {
                        models = support.Select(s => new
                        {
                            model = s.RequestedModel,
                            resolvedModel = s.ResolvedModel,
                            fromCatalog = s.FromCatalog,
                            supported = s.SupportedParameters,
                        }).ToList(),
                        parameters,
                        fromCatalog = resolved.Count > 0,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // ── System default settings (what NEW Stewards inherit) — Demiurge's own config, not OmniSettings ──
            await parent.RegisterHttpRouteAsync("/demiurge/system/settings", async req =>
            {
                try { await req.ReturnResponse(Json(parent.Settings.GetSystemDefaults())); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.FleetControl);

            await parent.RegisterHttpRouteAsync("/demiurge/system/settings/update", async req =>
            {
                try
                {
                    var patch = JObject.Parse(req.userMessageContent ?? "{}");
                    var defaults = parent.Settings.GetSystemDefaults();
                    var applied = new List<string>();
                    var unknown = new List<string>();
                    foreach (var kv in patch.Properties())
                    {
                        if (kv.Name.Equals("stewardID", StringComparison.OrdinalIgnoreCase)) continue;
                        (defaults.TrySet(kv.Name, kv.Value) ? applied : unknown).Add(kv.Name);
                    }
                    parent.Settings.SaveSystemDefaults(defaults);
                    await req.ReturnResponse(Json(new { applied, unknown, settings = defaults }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.FleetControl);

            // ── Durable Steward memory + steering ──

            // GET ?stewardID&includeResolved=false. Rules are visible and editable as explicit
            // Steward memory instead of being hidden in a compacted message timeline.
            await parent.RegisterHttpRouteAsync("/demiurge/memory", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    bool includeResolved = !bool.TryParse(req.userParameters?.Get("includeResolved"), out var parsed) || parsed;
                    await req.ReturnResponse(Json(parent.Directives.List(steward!.StewardID, includeResolved)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // POST { stewardID, text, key?, priority? }. Rules are all-agent, durable and are
            // immediately injected into the Commander; every future worker receives them too.
            await parent.RegisterHttpRouteAsync("/demiurge/memory/upsert", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string text = ((string?)body["text"] ?? "").Trim();
                    if (text.Length == 0)
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    string? key = ((string?)body["key"])?.Trim();
                    int priority = Math.Clamp((int?)body["priority"] ?? 100, -1000, 1000);
                    var receipt = parent.MessageStewardWithReceipt(steward!.StewardID, text,
                        StewardDirectiveKind.Rule, remember: true, key: key, priority: priority);
                    await req.ReturnResponse(Json(new { ok = receipt.Accepted, receipt }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            await parent.RegisterHttpRouteAsync("/demiurge/memory/revoke", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string directiveID = ((string?)body["directiveID"] ?? "").Trim();
                    if (directiveID.Length == 0)
                    {
                        await req.ReturnResponse("directiveID required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var revoked = parent.Directives.Revoke(steward!.StewardID, directiveID, (string?)body["reason"]);
                    if (revoked == null)
                    {
                        await req.ReturnResponse("unknown directiveID", code: HttpStatusCode.NotFound);
                        return;
                    }
                    parent.EventLog.Append(new StewardEvent
                    {
                        StewardID = steward.StewardID, Type = StewardEventTypes.DirectiveRevoked,
                        Author = "klives", StimulusID = directiveID,
                        Text = $"Directive {directiveID} revoked by Klives.",
                    });
                    await req.ReturnResponse(Json(new { ok = true, directive = revoked }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.PlanEdit);

            // Klives → one live sub-agent. It has the same durable receipt/lifecycle as Commander
            // steering instead of relying on the internal-only inter-agent bus.
            await parent.RegisterHttpRouteAsync("/demiurge/agents/message", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string agentID = ((string?)body["agentID"] ?? "").Trim();
                    string text = ((string?)body["text"] ?? "").Trim();
                    if (agentID.Length == 0 || text.Length == 0)
                    {
                        await req.ReturnResponse("agentID and text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var kind = ParseDirectiveKind((string?)body["kind"], StewardDirectiveKind.Task);
                    int priority = Math.Clamp((int?)body["priority"] ?? 100, -1000, 1000);
                    var receipt = parent.MessageAgentWithReceipt(steward!.StewardID, agentID, text, kind,
                        priority, ParseStringArray(body["expectedArtifactPaths"]));
                    await req.ReturnResponse(Json(new { ok = receipt.Accepted, receipt }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.AgentsMessage);

            // Klives → Commander message. A message is now a durable steering record first,
            // then a low-latency live injection; returning {ok:true} never again implies a wake
            // actually accepted it.
            await parent.RegisterHttpRouteAsync("/demiurge/message", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var body = JObject.Parse(req.userMessageContent ?? "{}");
                    string text = ((string?)body["text"] ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    bool remember = (bool?)body["remember"] ?? false;
                    // Do not infer "one-off" from punctuation: “Can you send a PDF report?” is
                    // still a deliverable. Callers that truly want transient chat can set kind=steering.
                    var defaultKind = StewardDirectiveKind.Task;
                    var kind = ParseDirectiveKind((string?)body["kind"], defaultKind);
                    string? key = ((string?)body["key"])?.Trim();
                    int priority = Math.Clamp((int?)body["priority"] ?? 100, -1000, 1000);
                    string pid = steward!.StewardID;
                    var expected = ParseStringArray(body["expectedArtifactPaths"]);
                    var trace = NewTrace("message");
                    var (done, receipt) = await RunLifecycleActionAsync(req, trace, () =>
                        Demiurge.Timed(trace, pid, "deliver-message", () =>
                            parent.MessageStewardWithReceipt(pid, text, kind, remember, key, priority, expected)));
                    if (done) await req.ReturnResponse(Json(new { ok = receipt!.Accepted, receipt }));
                    else await ReturnPendingAsync(req, "message", trace, pid);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.AgentsMessage);

            // What each agent is doing, or why it is not and when it next runs. Live state (admission
            // queue, provider backoff, the wall clock), so it is never served from the response cache.
            await parent.RegisterHttpRouteAsync("/demiurge/schedule", async req =>
            {
                CacheDeps.MarkUncacheable("Live wake schedule");
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(StewardSchedule.Describe(parent, steward!.StewardID, DateTime.UtcNow)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // ── Approvals ──
            await parent.RegisterHttpRouteAsync("/demiurge/gates", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    await req.ReturnResponse(Json(parent.Gates.ListPending(steward!.StewardID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/gates/resolve", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string gateID = (string?)body?.gateID ?? "";
                    string decisionStr = (string?)body?.decision ?? "";
                    string comment = (string?)body?.comment ?? "";
                    if (!Enum.TryParse<GateDecision>(decisionStr, ignoreCase: true, out var decision))
                    {
                        await req.ReturnResponse("decision must be Approve, Deny, or Discuss", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (decision == GateDecision.Discuss)
                    {
                        string discussion = string.IsNullOrWhiteSpace(comment)
                            ? $"I want to discuss the pending approval: {gateID}."
                            : comment;
                        bool opened = parent.Gates.BeginDiscussion(steward!.StewardID, gateID, discussion);
                        if (!opened)
                        {
                            await req.ReturnResponse(Json(new { ok = false, pending = false }));
                            return;
                        }
                        parent.MessageSteward(steward!.StewardID, discussion);
                        await req.ReturnResponse(Json(new { ok = true, pending = true }));
                        return;
                    }
                    bool ok = parent.Gates.ResolveGate(steward!.StewardID, gateID, new GateResolution(decision, comment, "klives"));
                    // A denial with a reason is an instruction, and it used to leave nothing behind but an
                    // approval-resolved event that aged out of the seed — so the Commander re-asked the same
                    // question a wake later. Route it through the normal directive path so it becomes durable
                    // (and is classified as a standing rule when that is what it is).
                    if (ok && decision == GateDecision.Deny && !string.IsNullOrWhiteSpace(comment))
                        parent.MessageSteward(steward.StewardID, comment);
                    await req.ReturnResponse(Json(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.GatesResolve);

            // ── Stimulus hooks (Klives-side CRUD; the Commander does the same via tools in a later build) ──
            await parent.RegisterHttpRouteAsync("/demiurge/hooks", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    // A webhook's ingress token lets anyone post a stimulus that wakes an agent with
                    // text of their choosing (/demiurge/hooks/webhook is public). This route needs only
                    // details.read, so the token goes only to a caller who could create the hook.
                    bool showTokens = req.Can(DemiurgePerms.HooksManage);
                    var enriched = parent.Hooks.List(steward!.StewardID).Select(h =>
                    {
                        var arm = h.Enabled ? parent.Adapters.GetArmInfo(h.HookID) : null;
                        return new
                        {
                            h.HookID,
                            h.StewardID,
                            h.OwningAgentID,
                            h.SourceKind,
                            h.SourceSpecJson,
                            h.RecognitionCriterion,
                            h.DestinationAgentID,
                            IngressToken = h.SourceKind == "webhook" && showTokens ? h.IngressToken : null,
                            h.Priority,
                            h.Durability,
                            h.Enabled,
                            h.CreatedAt,
                            ArmState = arm?.State.ToString() ?? (h.Enabled ? "Unknown" : "Disabled"),
                            ArmDetail = arm?.Detail ?? "",
                        };
                    });
                    await req.ReturnResponse(Json(enriched));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            await parent.RegisterHttpRouteAsync("/demiurge/hooks/create", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    var hook = JsonConvert.DeserializeObject<StimulusHookRecord>(req.userMessageContent ?? "{}") ?? new StimulusHookRecord();
                    hook.StewardID = steward!.StewardID;
                    if (string.IsNullOrWhiteSpace(hook.SourceKind))
                    {
                        await req.ReturnResponse("sourceKind required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var created = parent.Hooks.Create(hook);
                    parent.Adapters.ArmAll();
                    await req.ReturnResponse(Json(created));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.HooksManage);

            await parent.RegisterHttpRouteAsync("/demiurge/hooks/delete", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    bool ok = parent.Hooks.Delete(steward!.StewardID, hookID);
                    parent.Adapters.ArmAll();
                    await req.ReturnResponse(Json(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.HooksManage);

            await parent.RegisterHttpRouteAsync("/demiurge/hooks/token/rotate", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    if (string.IsNullOrWhiteSpace(hookID) && !string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        try { hookID = (string?)Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent)["hookID"] ?? ""; }
                        catch { }
                    }
                    string token = parent.Hooks.RotateIngressToken(steward!.StewardID, hookID);
                    await req.ReturnResponse(Json(new { hookID, ingressToken = token }));
                }
                catch (InvalidOperationException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.NotFound); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, DemiurgePerms.HooksManage);

            // ── Artifacts (screenshots/clips referenced by timeline events) ──
            await parent.RegisterHttpRouteAsync("/demiurge/artifacts/get", async req =>
            {
                try
                {
                    if (!RequireSteward(req, out var steward)) return;
                    string artifactID = req.userParameters?.Get("artifactID") ?? "";
                    var record = parent.Artifacts.GetRecord(steward!.StewardID, artifactID);
                    if (record == null)
                    {
                        await req.ReturnResponse("artifact not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    var bytes = parent.Artifacts.GetBytes(steward.StewardID, artifactID);
                    if (bytes == null)
                    {
                        // Past the 48h raw retention: the capture-time description IS the record now.
                        await req.ReturnResponse(Json(new
                        {
                            degraded = true,
                            record.Description,
                            record.CapturedAt,
                        }), code: HttpStatusCode.Gone);
                        return;
                    }
                    await req.ReturnBinaryResponse(bytes, record.ContentType);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, DemiurgePerms.DetailsRead);

            // Webhook ingest: POST /demiurge/hooks/webhook?stewardID=..&hookID=.. with a raw body.
            // Guest-level so external services can call it; the hook's criterion + triage gate it.
            // External services registered before Projects became Demiurge keep calling
            // /projects/hooks/webhook?projectID=.. with X-Project-Hook-Token, so that address stays live.
            async Task Webhook(Services.KliveAPI.KliveAPI.UserRequest req)
            {
                try
                {
                    string stewardID = req.userParameters?.Get("stewardID") ?? req.userParameters?.Get("projectID") ?? "";
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    var hook = parent.Hooks.Get(stewardID, hookID);
                    if (hook == null || hook.SourceKind != "webhook")
                    {
                        await req.ReturnResponse("unknown webhook", code: HttpStatusCode.NotFound);
                        return;
                    }
                    string suppliedToken = req.req?.Headers?["X-Steward-Hook-Token"]
                        ?? req.req?.Headers?["X-Project-Hook-Token"]
                        ?? req.userParameters?.Get("token") ?? "";
                    if (!SecureEquals(suppliedToken, hook.IngressToken))
                    {
                        await req.ReturnResponse("invalid webhook token", code: HttpStatusCode.Unauthorized);
                        return;
                    }
                    if (!AllowWebhookRequest(stewardID + "/" + hookID))
                    {
                        await req.ReturnResponse("webhook rate limit exceeded", code: HttpStatusCode.TooManyRequests);
                        return;
                    }
                    string? deliveryID = req.req?.Headers?["X-Webhook-Delivery"];
                    if (!string.IsNullOrWhiteSpace(deliveryID) && !AcceptDeliveryID(stewardID, hookID, deliveryID))
                    {
                        await req.ReturnResponse(Json(new { accepted = true, duplicate = true }));
                        return;
                    }
                    await parent.Adapters.IngestForHookAsync(stewardID, hookID, req.userMessageContent ?? "");
                    await req.ReturnResponse(Json(new { accepted = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }
            await parent.RegisterHttpRouteAsync("/demiurge/hooks/webhook", Webhook, HttpMethod.Post, Perms.Public);
            await parent.RegisterHttpRouteAsync("/projects/hooks/webhook", Webhook, HttpMethod.Post, Perms.Public);
        }

        private bool AllowWebhookRequest(string key)
        {
            var queue = webhookRateWindows.GetOrAdd(key, _ => new Queue<DateTime>());
            lock (queue)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
                if (queue.Count >= WebhookRequestsPerMinute) return false;
                queue.Enqueue(DateTime.UtcNow);
                return true;
            }
        }

        private bool AcceptDeliveryID(string stewardID, string hookID, string deliveryID)
        {
            DateTime now = DateTime.UtcNow;
            if (webhookDeliveryIds.Count > 5000)
                foreach (var old in webhookDeliveryIds.Where(kv => now - kv.Value > TimeSpan.FromHours(24)).Take(1000))
                    webhookDeliveryIds.TryRemove(old.Key, out _);
            return webhookDeliveryIds.TryAdd($"{stewardID}/{hookID}/{deliveryID}", now);
        }

        private static bool SecureEquals(string supplied, string expected)
        {
            if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
            byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private bool RequireSteward(Services.KliveAPI.KliveAPI.UserRequest req, out Steward? steward)
        {
            // stewardID may arrive on the query string (GETs, some POSTs) OR in the JSON body
            // (the website's POSTs send it there). userParameters is query-only, so fall back to
            // the body — otherwise every body-stewardID POST 404s.
            // projectID is the name a page loaded before Projects became Demiurge still sends.
            string stewardID = req.userParameters?.Get("stewardID") ?? req.userParameters?.Get("projectID") ?? "";
            if (string.IsNullOrWhiteSpace(stewardID) && !string.IsNullOrWhiteSpace(req.userMessageContent))
            {
                try
                {
                    var body = Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent);
                    stewardID = (string?)body["stewardID"] ?? (string?)body["projectID"] ?? "";
                }
                catch { /* body isn't a JSON object with stewardID */ }
            }
            steward = string.IsNullOrWhiteSpace(stewardID) ? null : parent.Store.GetSteward(stewardID);
            if (steward == null)
            {
                _ = req.ReturnResponse("unknown stewardID", code: HttpStatusCode.NotFound);
                return false;
            }
            return true;
        }

        /// <summary>
        /// One row of the fleet list, built by the materializer off the HTTP path. Most fields
        /// come from store projections; cold stores can still open files in the background.
        /// </summary>
        internal string BuildListJson() => Json(parent.Store.ListStewards().Select(ToSummary).ToList());

        private object ToSummary(Steward p)
        {
            var spend = parent.Budget.GetSpend(p.StewardID);
            var runtime = parent.RuntimeState.GetSummary(p.StewardID);
            return new
            {
                p.StewardID,
                p.Name,
                p.Goal,
                Status = p.Status.ToString(),
                Halted = p.HaltedFromStatus.HasValue,
                HaltedFromStatus = p.HaltedFromStatus?.ToString(),
                p.CreatedAt,
                p.TokenBudgetUsd,
                p.MoneyBudgetUsd,
                p.SubAgentCap,
                TokenSpendUsd = spend.TokenSpendUsd,
                MoneySpendUsd = spend.MoneySpendUsd,
                PendingApprovals = parent.Gates.CountPending(p.StewardID),
                ExecutionDisposition = runtime.Disposition.ToString(),
                ExecutionHealth = runtime.HealthStatus.ToString(),
                Blocker = runtime.BlockerSummary ?? p.BlockedReason,
                NextRetryAt = runtime.NextRetryAt,
                ActiveMilestoneIDs = runtime.ActiveMilestoneIDs,
                CheckpointRevision = runtime.CheckpointRevision,
                lastSequence = parent.EventLog.GetLastSequence(p.StewardID),
            };
        }

        private async Task Err(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            // Route handlers used to turn exceptions into an unlogged plain-text 500. That made a
            // production failure impossible to diagnose from Omnipotent and encouraged the website
            // to replace the only useful detail with "HTTP 500". Keep a full server-side stack and
            // return a small structured error to the Klives-only client.
            try { _ = parent.ServiceLogError(ex, $"Demiurge HTTP route failed: {req.route}"); }
            catch { /* logging must never delay or replace the HTTP error response */ }
            await req.ReturnResponse(Json(new { error = ex.Message, route = req.route }),
                "application/json", code: HttpStatusCode.InternalServerError);
        }

        // RFC 4180 CSV field: quote when the value contains a comma, quote, CR or LF; escape
        // inner quotes by doubling them. Null/empty becomes an empty (unquoted) field.
        private static string Csv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            bool mustQuote = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return mustQuote ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
        }

        // Parses an ISO-8601 export bound as UTC; unparseable/empty means "open-ended".
        private static DateTime? ParseTimestamp(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto.UtcDateTime : null;
        }

        private static StewardDirectiveKind ParseDirectiveKind(string? raw, StewardDirectiveKind fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            return raw.Trim().ToLowerInvariant() switch
            {
                "rule" or "memory" => StewardDirectiveKind.Rule,
                "task" or "command" or "report" => StewardDirectiveKind.Task,
                "steering" or "message" => StewardDirectiveKind.Steering,
                _ => fallback,
            };
        }

        /// <summary>The UI-facing shape of a retirement: who went, what they were holding, and whether
        /// anyone still has to pick it up. Deliberately not the whole record — the full handover, with
        /// the checkpointed next action and directive texts, is /demiurge/agents/handovers.</summary>
        private static object DescribeHandover(StewardAgentHandover h) => new
        {
            h.HandoverID,
            h.AgentID,
            h.Role,
            h.Tier,
            mission = h.MissionKind.ToString(),
            reason = h.Reason.ToString(),
            status = h.Status.ToString(),
            h.Objective,
            h.ActiveMilestoneIDs,
            h.DeliverablePaths,
            h.InterruptedMidWake,
            disposedContainers = h.DisposedContainerIDs.Count,
            openDirectives = h.OpenDirectives.Count,
            h.RetiredAt,
        };

        internal static object DescribeAdmission(StewardWakeAdmission.AdmissionSnapshot admission) => new
        {
            admission.Enabled,
            admission.Live,
            admission.Active,
            admission.Budget,
            admission.Source,
            admission.MeasuredBudget,
            admission.ConfiguredCap,
            admission.Fallback,
            deferredCount = admission.Deferred,
            admission.RampStartedAt,
            liveKeys = admission.LiveKeys,
            deferred = admission.DeferredWakes.Select(wake => new
            {
                wake.StewardID,
                wake.AgentID,
                wake.QueuedAt,
                waitingSeconds = Math.Max(0, (int)(DateTime.UtcNow - wake.QueuedAt).TotalSeconds),
            }),
        };

        private static List<string> ParseStringArray(JToken? token)
        {
            if (token is not JArray array) return new List<string>();
            return array.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        }

        // Reduces a Steward name to a filesystem-safe slug for the Content-Disposition filename.
        private static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "steward";
            var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return string.IsNullOrEmpty(slug) ? "steward" : (slug.Length > 60 ? slug[..60] : slug);
        }
    }
}
