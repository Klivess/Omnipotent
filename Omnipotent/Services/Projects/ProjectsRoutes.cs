using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Services.Projects.Stimulus;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// REST routes for Projects. Everything is Klives-only — Projects is Klives' personal
    /// autonomous task force, the same trust boundary as KliveAgent (not Stratum's
    /// multi-user Guest+ownership model).
    ///
    /// All responses serialize camelCase (JS-idiomatic) via <see cref="Json"/>, so the website
    /// reads fields as `e.type` / `p.projectID` directly — real classes (ProjectEvent, ProjectSettings,
    /// …) would otherwise emit PascalCase under Newtonsoft's default and read as undefined.
    /// </summary>
    public class ProjectsRoutes
    {
        private readonly Projects parent;
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
        private static string Json(object o) => JsonConvert.SerializeObject(o, CamelCase);

        public ProjectsRoutes(Projects parent)
        {
            this.parent = parent;
        }

        public async Task RegisterRoutes()
        {
            // ── Projects ──
            await parent.CreateAPIRoute("/projects/list", async req =>
            {
                try
                {
                    var list = parent.Store.ListProjects().Select(ToSummary).ToList();
                    await req.ReturnResponse(Json(list));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/create", async req =>
            {
                try
                {
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string name = (string?)body?.name ?? "";
                    string goal = (string?)body?.goal ?? "";
                    double tokenBudget = (double?)body?.tokenBudgetUsd ?? 0;
                    double moneyBudget = (double?)body?.moneyBudgetUsd ?? 0;
                    double moneyThreshold = (double?)body?.moneyAutonomousThresholdUsd ?? 0;
                    int agentCap = (int?)body?.subAgentCap ?? 5;
                    string? initialUploadSessionID = ((string?)body?.initialUploadSessionID)?.Trim();

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(goal))
                    {
                        await req.ReturnResponse("name and goal required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (tokenBudget <= 0)
                    {
                        await req.ReturnResponse("tokenBudgetUsd must be > 0 — a Project is a goal AND a budget", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (!double.IsFinite(tokenBudget) || !double.IsFinite(moneyBudget) || !double.IsFinite(moneyThreshold) ||
                        moneyBudget < 0 || moneyThreshold < 0 || agentCap < 1)
                    {
                        await req.ReturnResponse("budgets must be finite/non-negative and subAgentCap must be at least 1", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    // Optional per-project settings configured on the new-project page (model routing,
                    // vision/containers, desktop image), applied at creation before the first wake.
                    Dictionary<string, Newtonsoft.Json.Linq.JToken>? settingsPatch = null;
                    if (body?.settings != null)
                    {
                        try { settingsPatch = ((Newtonsoft.Json.Linq.JObject)body.settings).Properties()
                            .ToDictionary(p => p.Name, p => p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase); }
                        catch (Exception sex) { _ = parent.ServiceLogError(sex, "Projects: parsing create-time settings failed (using defaults)"); }
                    }

                    var creator = new ProjectFileActor(ProjectFileActorType.User,
                        req.user!.UserID, req.user.Name ?? "Klives");
                    var p = await parent.CreateProjectAsync(name, goal, tokenBudget, moneyBudget, moneyThreshold,
                        agentCap, settingsPatch, initialUploadSessionID, creator);
                    await req.ReturnResponse(Json(p));
                }
                catch (ProjectFileConflictException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.Conflict); }
                catch (ProjectFileException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/get", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/state", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(new
                    {
                        Project = project,
                        Runtime = parent.RuntimeState.Get(project!.ProjectID),
                        GrandPlan = parent.GrandPlans.GetCurrentApproved(project.ProjectID),
                        Budget = parent.Budget.GetLedger(project.ProjectID),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/pause", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    project!.Status = ProjectStatus.Paused;
                    project.HaltedFromStatus = null; // individual pause overrides any remembered fleet-halt state
                    parent.Store.SaveProject(project);
                    parent.RuntimeState.SetDisposition(project.ProjectID, ProjectExecutionDisposition.Pausing);
                    // Halt the in-flight wake too, so "pause" stops work promptly rather than only
                    // preventing the NEXT wake (item 1: halt progression).
                    bool halted = parent.CommanderRunner.CancelActiveWake(project.ProjectID);
                    parent.SubAgentRunner.CancelProject(project.ProjectID);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = halted ? "Project paused by Klives — in-flight wake halted." : "Project paused by Klives.",
                    });
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Archive / unarchive (shelving, item 2) ──
            await parent.CreateAPIRoute("/projects/archive", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    project!.Status = ProjectStatus.Archived;
                    project.HaltedFromStatus = null; // shelving overrides any remembered fleet-halt state
                    parent.Store.SaveProject(project);
                    parent.RuntimeState.SetDisposition(project.ProjectID, ProjectExecutionDisposition.Archived);
                    parent.CommanderRunner.CancelActiveWake(project.ProjectID); // shelved projects do no work
                    parent.SubAgentRunner.CancelProject(project.ProjectID);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = "Project archived (shelved) by Klives.",
                    });
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/unarchive", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    // Restore to Paused (not Active) — Klives explicitly resumes when ready, so
                    // unshelving never silently sets the fleet back to work.
                    project!.Status = ProjectStatus.Paused;
                    project.HaltedFromStatus = null; // unshelving overrides any remembered fleet-halt state
                    parent.Store.SaveProject(project);
                    parent.RuntimeState.SetDisposition(project.ProjectID, ProjectExecutionDisposition.Paused);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = "Project unshelved by Klives (paused — resume to set it working).",
                    });
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Rename (agents learn of it via the log + wake seed, item 4) ──
            await parent.CreateAPIRoute("/projects/rename", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string newName = ((string?)body?.name ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        await req.ReturnResponse("name required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    string oldName = project!.Name;
                    if (newName == oldName) { await req.ReturnResponse(Json(project)); return; }
                    project.Name = newName;
                    parent.Store.SaveProject(project);
                    // Logged as a Klives message so it lands in the Commander's recent-events window
                    // and it registers the rename on its next wake (the wake seed reads project.Name).
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.KlivesMessage,
                        Author = "klives",
                        Text = $"Project renamed from \"{oldName}\" to \"{newName}\".",
                    });
                    if (parent.DiscordManager != null)
                    {
                        try { await parent.DiscordManager.RenameProjectChannelAsync(project); }
                        catch (Exception dex) { _ = parent.ServiceLogError(dex, "Projects: rename Discord channel failed"); }
                    }
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Klives-side budget editing (the Commander's own path stays request_budget_increase).
            // POST { projectID, tokenBudgetUsd?, moneyBudgetUsd?, moneyAutonomousThresholdUsd?, subAgentCap? }
            // Only supplied fields change. Raising the token budget above current spend un-pauses a
            // BudgetPaused project; the change is logged as a KlivesMessage so the Commander sees it.
            await parent.CreateAPIRoute("/projects/budget/update", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    double? tokenBudget = (double?)body?.tokenBudgetUsd;
                    double? moneyBudget = (double?)body?.moneyBudgetUsd;
                    double? moneyThreshold = (double?)body?.moneyAutonomousThresholdUsd;
                    int? agentCap = (int?)body?.subAgentCap;

                    if (tokenBudget is <= 0)
                    {
                        await req.ReturnResponse("tokenBudgetUsd must be > 0 — a Project is a goal AND a budget", code: HttpStatusCode.BadRequest);
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
                    if (agentCap.HasValue)
                    {
                        int activeAgents = parent.SubAgents.ListActive(project!.ProjectID).Count;
                        if (agentCap.Value < activeAgents)
                        {
                            await req.ReturnResponse($"subAgentCap cannot be lowered below the active roster ({activeAgents}); retire agents first",
                                code: HttpStatusCode.Conflict);
                            return;
                        }
                    }

                    var changes = new List<string>();
                    if (tokenBudget.HasValue && tokenBudget.Value != project!.TokenBudgetUsd)
                    { changes.Add($"token budget ${project.TokenBudgetUsd:0.##} → ${tokenBudget.Value:0.##}"); project.TokenBudgetUsd = tokenBudget.Value; }
                    if (moneyBudget.HasValue && moneyBudget.Value != project!.MoneyBudgetUsd)
                    { changes.Add($"money budget ${project.MoneyBudgetUsd:0.##} → ${moneyBudget.Value:0.##}"); project.MoneyBudgetUsd = moneyBudget.Value; }
                    if (moneyThreshold.HasValue && moneyThreshold.Value != project!.MoneyAutonomousThresholdUsd)
                    { changes.Add($"autonomous threshold ${project.MoneyAutonomousThresholdUsd:0.##} → ${moneyThreshold.Value:0.##}"); project.MoneyAutonomousThresholdUsd = moneyThreshold.Value; }
                    if (agentCap.HasValue && agentCap.Value != project!.SubAgentCap)
                    { changes.Add($"agent cap {project.SubAgentCap} → {agentCap.Value}"); project.SubAgentCap = agentCap.Value; }

                    if (changes.Count == 0) { await req.ReturnResponse(Json(project)); return; }
                    parent.Store.SaveProject(project!);

                    // Re-arm the 80% warning for the new budget; un-pause if spend is back within it.
                    bool withinBudget = parent.Budget.NotifyBudgetChanged(project!.ProjectID);
                    if (project.Status == ProjectStatus.BudgetPaused && withinBudget && tokenBudget.HasValue)
                    {
                        // Return to where it was paused — a never-approved plan resumes to Planning.
                        project.Status = parent.GrandPlans.HasApprovedPlan(project.ProjectID)
                            ? ProjectStatus.Active : ProjectStatus.Planning;
                        project.HaltedFromStatus = null; // budget-resume overrides any remembered fleet-halt state
                        parent.Store.SaveProject(project);
                        parent.RuntimeState.SetDisposition(project.ProjectID, ProjectExecutionDisposition.Running);
                        parent.RuntimeState.ClearBlocker(project.ProjectID);
                        changes.Add("project resumed from budget-pause");
                    }

                    // KlivesMessage (not Status) so it lands in the Commander's recent-events window.
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.KlivesMessage,
                        Author = "klives",
                        Text = $"Budgets updated by Klives: {string.Join(", ", changes)}.",
                    });
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/resume", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    if (project!.Status == ProjectStatus.Completed)
                    {
                        await req.ReturnResponse("completed projects cannot be resumed", code: HttpStatusCode.Conflict);
                        return;
                    }
                    if (project.Status == ProjectStatus.Archived)
                    {
                        await req.ReturnResponse("unarchive the project before resuming it", code: HttpStatusCode.Conflict);
                        return;
                    }
                    if (project.Status == ProjectStatus.BudgetPaused && !parent.Budget.IsWithinTokenBudget(project.ProjectID))
                    {
                        await req.ReturnResponse("raise the token budget above current spend before resuming", code: HttpStatusCode.Conflict);
                        return;
                    }
                    // A project paused mid-planning resumes to Planning (the Grand Plan gate still stands).
                    bool wasPlanning = !parent.GrandPlans.HasApprovedPlan(project.ProjectID);
                    project!.Status = wasPlanning ? ProjectStatus.Planning : ProjectStatus.Active;
                    bool wasBlocked = project.BlockedAt.HasValue || !string.IsNullOrWhiteSpace(project.BlockedReason);
                    project.BlockedAt = null;
                    project.BlockedReason = null;
                    project.HaltedFromStatus = null; // individual resume overrides any remembered fleet-halt state
                    parent.Store.SaveProject(project);
                    parent.RuntimeState.SetDisposition(project.ProjectID, ProjectExecutionDisposition.Running);
                    parent.RuntimeState.ClearBlocker(project.ProjectID);
                    parent.RuntimeState.CloseCircuit(project.ProjectID);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = wasBlocked ? ProjectEventTypes.ProjectUnblocked : ProjectEventTypes.Status,
                        Author = "klives",
                        Text = "Project resumed by Klives.",
                    });
                    parent.CommanderRunner.Wake(project, wasPlanning
                        ? "Project resumed by Klives — still in PLANNING. Continue converging on a Grand Plan and submit it for approval."
                        : "Project resumed by Klives. Rehydrate current state and continue with the next concrete step.");
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Fleet-wide controls (broadcast + global halt) ──

            // Deliver one message to every live project's Commander. POST { text }.
            await parent.CreateAPIRoute("/projects/broadcast", async req =>
            {
                try
                {
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string text = (string?)body?.text ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var affected = parent.BroadcastMessage(text);
                    await req.ReturnResponse(Json(new { ok = true, delivered = affected.Count, projectIDs = affected }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Halt every project that isn't already halted (or terminal), remembering each project's
            // pre-halt status so unhalt-all restores it exactly. POST (no body).
            await parent.CreateAPIRoute("/projects/halt-all", async req =>
            {
                try
                {
                    var halted = new List<string>();
                    foreach (var p in parent.Store.ListProjects())
                        if (parent.HaltProject(p.ProjectID)) halted.Add(p.ProjectID);
                    await req.ReturnResponse(Json(new { ok = true, halted = halted.Count, projectIDs = halted }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Restore every globally-halted project to the exact status it held before the halt.
            // POST (no body).
            await parent.CreateAPIRoute("/projects/unhalt-all", async req =>
            {
                try
                {
                    var restored = new List<string>();
                    foreach (var p in parent.Store.ListProjects())
                        if (parent.UnhaltProject(p.ProjectID)) restored.Add(p.ProjectID);
                    await req.ReturnResponse(Json(new { ok = true, restored = restored.Count, projectIDs = restored }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Timeline ──
            // Read for the website's timeline/conversation panels:
            //   GET /projects/events?projectID=..&tail=true&max=..  — initial backlog (most-recent N)
            //   GET /projects/events?projectID=..&since=..&max=..    — incremental page-forward
            // tail=true is load-bearing for the initial load: reading forward from since=0 returns the
            // OLDEST `max` events while the client advances its cursor to lastSequence, so any project
            // with more than `max` events showed ancient history and silently skipped everything after
            // it. The tail path returns the newest events so the panels open on current activity.
            await parent.CreateAPIRoute("/projects/events", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    long since = long.TryParse(req.userParameters?.Get("since"), out var s) ? s : 0;
                    int max = int.TryParse(req.userParameters?.Get("max"), out var m) ? Math.Clamp(m, 1, 2000) : 500;
                    bool tail = bool.TryParse(req.userParameters?.Get("tail"), out var t) && t;
                    var events = (tail && since <= 0)
                        ? parent.EventLog.ReadTail(project!.ProjectID, max)
                        : parent.EventLog.ReadSince(project!.ProjectID, since, max);
                    await req.ReturnResponse(Json(new
                    {
                        events,
                        lastSequence = parent.EventLog.GetLastSequence(project.ProjectID),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // Lossless CSV export of the whole event log over a timeframe — the website's
            // "Download history" button. Every stored field is a column, so nothing is lost
            // relative to the on-disk JSONL (PayloadJson keeps its 32 KB write-time truncation;
            // media is referenced by artifact ID exactly as the log stores it).
            //   GET /projects/events/export?projectID=..&from=<ISO8601>&to=<ISO8601>
            // from/to are optional and open-ended when omitted; both are treated as UTC.
            await parent.CreateAPIRoute("/projects/events/export", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    DateTime? from = ParseTimestamp(req.userParameters?.Get("from"));
                    DateTime? to = ParseTimestamp(req.userParameters?.Get("to"));

                    var sb = new StringBuilder();
                    sb.Append("sequence,eventID,timestampUtc,type,author,agentID,wakeID,toolName,toolCallId,gateID,stimulusID,artifactIDs,text,payloadJson\r\n");
                    long count = 0;
                    foreach (var e in parent.EventLog.EnumerateRange(project!.ProjectID, from, to))
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

                    string fileName = $"project-{Sanitize(project.Name)}-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
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
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/digest", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(parent.Digests.GetDigest(project!.ProjectID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/ledger", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(parent.Budget.GetLedger(project!.ProjectID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // Agent roster (org chart) for the workspace's Agents panel.
            await parent.CreateAPIRoute("/projects/agents", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var agents = parent.SubAgents.ListActive(project!.ProjectID).Select(a => new
                    {
                        a.AgentID,
                        a.Role,
                        Tier = a.Tier.ToString(),
                        a.ParentAgentID,
                        a.CreatedAt,
                    }).ToList();
                    await req.ReturnResponse(Json(agents));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // Observables (the agents' live dashboard for this project). History is trimmed to the
            // last N samples server-side so the 1s-debounced refresh stays cheap; ?history=0 = values only.
            await parent.CreateAPIRoute("/projects/observables", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    int history = 120;
                    if (int.TryParse(req.userParameters?.Get("history"), out var h))
                        history = Math.Clamp(h, 0, ProjectObservableStore.MaxHistorySamples);
                    var list = parent.Observables.List(project!.ProjectID).Select(o => new
                    {
                        o.ObservableID,
                        o.Name,
                        Type = o.Type.ToString(),
                        Format = o.Format.ToString(),
                        o.Unit,
                        o.Description,
                        o.NumericValue,
                        o.TextValue,
                        DisplayValue = ProjectObservableStore.FormatValue(o),
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
            }, HttpMethod.Get, KMPermissions.Klives);

            // Manual cleanup of an agent-created observable (agents own the values; Klives can only prune).
            await parent.CreateAPIRoute("/projects/observables/delete", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    string name = req.userParameters?.Get("name") ?? "";
                    if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        try { name = (string?)Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent)["name"] ?? ""; }
                        catch { /* body isn't a JSON object with name */ }
                    }
                    bool deleted = parent.Observables.Delete(project!.ProjectID, name);
                    if (deleted)
                        parent.EventLog.Append(new ProjectEvent
                        {
                            ProjectID = project.ProjectID,
                            Type = ProjectEventTypes.ObservableChanged,
                            Author = "klives",
                            Text = $"{name}: deleted by Klives",
                        });
                    await req.ReturnResponse(Json(new { deleted }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Councils (adversarial deliberation transcripts) ──
            await parent.CreateAPIRoute("/projects/councils", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var list = parent.Councils.List(project!.ProjectID).Select(c => new
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
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/councils/get", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    string councilID = req.userParameters?.Get("councilID") ?? "";
                    var council = parent.Councils.Get(project!.ProjectID, councilID);
                    if (council == null) { await req.ReturnResponse("no such council", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(Json(council));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // ── Grand Plan (the approved strategic north star + version history) ──
            await parent.CreateAPIRoute("/projects/grandplan", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var doc = parent.GrandPlans.Get(project!.ProjectID);
                    var current = parent.GrandPlans.GetCurrentApproved(project.ProjectID);
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
            }, HttpMethod.Get, KMPermissions.Klives);

            // A project's desktop containers, so the live-view can offer them (and map agent → desktop).
            await parent.CreateAPIRoute("/projects/containers", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var containers = (parent.Desktops?.Registry.ForProject(project!.ProjectID) ?? new())
                        .Select(c => new { c.ContainerID, c.AgentID, c.Width, c.Height }).ToList();
                    await req.ReturnResponse(Json(containers));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // ── Per-project settings (Projects' own setting system, not OmniSettings) ──
            await parent.CreateAPIRoute("/projects/settings", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(parent.Settings.Get(project!.ProjectID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            // Patch one or more settings. Route values are ordered JSON arrays; scalar settings
            // retain their natural JSON type.
            await parent.CreateAPIRoute("/projects/settings/update", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var patch = JObject.Parse(req.userMessageContent ?? "{}");
                    var settings = parent.Settings.Get(project!.ProjectID);
                    var applied = new List<string>();
                    var unknown = new List<string>();
                    foreach (var kv in patch.Properties())
                    {
                        if (kv.Name.Equals("projectID", StringComparison.OrdinalIgnoreCase)) continue; // routing field, not a setting
                        (settings.TrySet(kv.Name, kv.Value) ? applied : unknown).Add(kv.Name);
                    }
                    parent.Settings.Save(settings);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = $"Settings updated: {string.Join(", ", applied)}." + (unknown.Count > 0 ? $" Unknown keys ignored: {string.Join(", ", unknown)}." : ""),
                    });
                    await req.ReturnResponse(Json(new { applied, unknown, settings }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── System default settings (what NEW projects inherit) — Projects' own config, not OmniSettings ──
            await parent.CreateAPIRoute("/projects/system/settings", async req =>
            {
                try { await req.ReturnResponse(Json(parent.Settings.GetSystemDefaults())); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/system/settings/update", async req =>
            {
                try
                {
                    var patch = JObject.Parse(req.userMessageContent ?? "{}");
                    var defaults = parent.Settings.GetSystemDefaults();
                    var applied = new List<string>();
                    var unknown = new List<string>();
                    foreach (var kv in patch.Properties())
                    {
                        if (kv.Name.Equals("projectID", StringComparison.OrdinalIgnoreCase)) continue;
                        (defaults.TrySet(kv.Name, kv.Value) ? applied : unknown).Add(kv.Name);
                    }
                    parent.Settings.SaveSystemDefaults(defaults);
                    await req.ReturnResponse(Json(new { applied, unknown, settings = defaults }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // Klives → Commander message (website chat). Logged, and wakes the Commander so it
            // responds. (Once P4's bus exists this becomes a durable stimulus; the wake call
            // is idempotent — a no-op if a wake is already active.)
            await parent.CreateAPIRoute("/projects/message", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string text = (string?)body?.text ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    // Logs the Klives message and steers/wakes the Commander (lands within a live wake).
                    parent.MessageProject(project!.ProjectID, text);
                    await req.ReturnResponse(Json(new { ok = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Approvals ──
            await parent.CreateAPIRoute("/projects/gates", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    await req.ReturnResponse(Json(parent.Gates.ListPending(project!.ProjectID)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/gates/resolve", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
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
                        bool opened = parent.Gates.BeginDiscussion(project!.ProjectID, gateID, discussion);
                        if (!opened)
                        {
                            await req.ReturnResponse(Json(new { ok = false, pending = false }));
                            return;
                        }
                        parent.MessageProject(project!.ProjectID, discussion);
                        await req.ReturnResponse(Json(new { ok = true, pending = true }));
                        return;
                    }
                    bool ok = parent.Gates.ResolveGate(project!.ProjectID, gateID, new GateResolution(decision, comment, "klives"));
                    await req.ReturnResponse(Json(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Stimulus hooks (Klives-side CRUD; the Commander does the same via tools in a later build) ──
            await parent.CreateAPIRoute("/projects/hooks", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var enriched = parent.Hooks.List(project!.ProjectID).Select(h =>
                    {
                        var arm = h.Enabled ? parent.Adapters.GetArmInfo(h.HookID) : null;
                        return new
                        {
                            h.HookID,
                            h.ProjectID,
                            h.OwningAgentID,
                            h.SourceKind,
                            h.SourceSpecJson,
                            h.RecognitionCriterion,
                            h.DestinationAgentID,
                            IngressToken = h.SourceKind == "webhook" ? h.IngressToken : null,
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
            }, HttpMethod.Get, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/hooks/create", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var hook = JsonConvert.DeserializeObject<StimulusHookRecord>(req.userMessageContent ?? "{}") ?? new StimulusHookRecord();
                    hook.ProjectID = project!.ProjectID;
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
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/hooks/delete", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    bool ok = parent.Hooks.Delete(project!.ProjectID, hookID);
                    parent.Adapters.ArmAll();
                    await req.ReturnResponse(Json(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            await parent.CreateAPIRoute("/projects/hooks/token/rotate", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    if (string.IsNullOrWhiteSpace(hookID) && !string.IsNullOrWhiteSpace(req.userMessageContent))
                    {
                        try { hookID = (string?)Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent)["hookID"] ?? ""; }
                        catch { }
                    }
                    string token = parent.Hooks.RotateIngressToken(project!.ProjectID, hookID);
                    await req.ReturnResponse(Json(new { hookID, ingressToken = token }));
                }
                catch (InvalidOperationException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.NotFound); }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Artifacts (screenshots/clips referenced by timeline events) ──
            await parent.CreateAPIRoute("/projects/artifacts/get", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    string artifactID = req.userParameters?.Get("artifactID") ?? "";
                    var record = parent.Artifacts.GetRecord(project!.ProjectID, artifactID);
                    if (record == null)
                    {
                        await req.ReturnResponse("artifact not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    var bytes = parent.Artifacts.GetBytes(project.ProjectID, artifactID);
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
            }, HttpMethod.Get, KMPermissions.Klives);

            // Webhook ingest: POST /projects/hooks/webhook?projectID=..&hookID=.. with a raw body.
            // Guest-level so external services can call it; the hook's criterion + triage gate it.
            await parent.CreateAPIRoute("/projects/hooks/webhook", async req =>
            {
                try
                {
                    string projectID = req.userParameters?.Get("projectID") ?? "";
                    string hookID = req.userParameters?.Get("hookID") ?? "";
                    var hook = parent.Hooks.Get(projectID, hookID);
                    if (hook == null || hook.SourceKind != "webhook")
                    {
                        await req.ReturnResponse("unknown webhook", code: HttpStatusCode.NotFound);
                        return;
                    }
                    string suppliedToken = req.req?.Headers?["X-Project-Hook-Token"]
                        ?? req.userParameters?.Get("token") ?? "";
                    if (!SecureEquals(suppliedToken, hook.IngressToken))
                    {
                        await req.ReturnResponse("invalid webhook token", code: HttpStatusCode.Unauthorized);
                        return;
                    }
                    if (!AllowWebhookRequest(projectID + "/" + hookID))
                    {
                        await req.ReturnResponse("webhook rate limit exceeded", code: HttpStatusCode.TooManyRequests);
                        return;
                    }
                    string? deliveryID = req.req?.Headers?["X-Webhook-Delivery"];
                    if (!string.IsNullOrWhiteSpace(deliveryID) && !AcceptDeliveryID(projectID, hookID, deliveryID))
                    {
                        await req.ReturnResponse(Json(new { accepted = true, duplicate = true }));
                        return;
                    }
                    await parent.Adapters.IngestForHookAsync(projectID, hookID, req.userMessageContent ?? "");
                    await req.ReturnResponse(Json(new { accepted = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);
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

        private bool AcceptDeliveryID(string projectID, string hookID, string deliveryID)
        {
            DateTime now = DateTime.UtcNow;
            if (webhookDeliveryIds.Count > 5000)
                foreach (var old in webhookDeliveryIds.Where(kv => now - kv.Value > TimeSpan.FromHours(24)).Take(1000))
                    webhookDeliveryIds.TryRemove(old.Key, out _);
            return webhookDeliveryIds.TryAdd($"{projectID}/{hookID}/{deliveryID}", now);
        }

        private static bool SecureEquals(string supplied, string expected)
        {
            if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
            byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private bool RequireProject(Services.KliveAPI.KliveAPI.UserRequest req, out Project? project)
        {
            // projectID may arrive on the query string (GETs, some POSTs) OR in the JSON body
            // (the website's POSTs send it there). userParameters is query-only, so fall back to
            // the body — otherwise every body-projectID POST 404s.
            string projectID = req.userParameters?.Get("projectID") ?? "";
            if (string.IsNullOrWhiteSpace(projectID) && !string.IsNullOrWhiteSpace(req.userMessageContent))
            {
                try { projectID = (string?)Newtonsoft.Json.Linq.JObject.Parse(req.userMessageContent)["projectID"] ?? ""; }
                catch { /* body isn't a JSON object with projectID */ }
            }
            project = string.IsNullOrWhiteSpace(projectID) ? null : parent.Store.GetProject(projectID);
            if (project == null)
            {
                _ = req.ReturnResponse("unknown projectID", code: HttpStatusCode.NotFound);
                return false;
            }
            return true;
        }

        private object ToSummary(Project p)
        {
            var ledger = parent.Budget.GetLedger(p.ProjectID);
            var runtime = parent.RuntimeState.Get(p.ProjectID);
            return new
            {
                p.ProjectID,
                p.Name,
                p.Goal,
                Status = p.Status.ToString(),
                Halted = p.HaltedFromStatus.HasValue,
                HaltedFromStatus = p.HaltedFromStatus?.ToString(),
                p.CreatedAt,
                p.TokenBudgetUsd,
                p.MoneyBudgetUsd,
                p.SubAgentCap,
                TokenSpendUsd = ledger.TokenSpendUsd,
                MoneySpendUsd = ledger.MoneySpendUsd,
                PendingApprovals = parent.Gates.ListPending(p.ProjectID).Count,
                ExecutionDisposition = runtime.Disposition.ToString(),
                ExecutionHealth = runtime.Health.Status.ToString(),
                Blocker = runtime.Blocker?.Summary ?? p.BlockedReason,
                NextRetryAt = runtime.Health.Circuit.RetryAt ?? runtime.Blocker?.NextRetryAt,
                ActiveMilestoneIDs = runtime.Checkpoint.ActiveMilestoneIDs,
                CheckpointRevision = runtime.Checkpoint.Revision,
                lastSequence = parent.EventLog.GetLastSequence(p.ProjectID),
            };
        }

        private static async Task Err(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(ex.Message, code: HttpStatusCode.InternalServerError);
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

        // Reduces a project name to a filesystem-safe slug for the Content-Disposition filename.
        private static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "project";
            var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return string.IsNullOrEmpty(slug) ? "project" : (slug.Length > 60 ? slug[..60] : slug);
        }
    }
}
