using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Omnipotent.Services.Projects.Stimulus;
using System.Net;
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

        private static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
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

                    var p = parent.Store.CreateProject(name, goal, tokenBudget, moneyBudget, moneyThreshold, agentCap);
                    parent.Settings.EnsureCreated(p.ProjectID); // seed this project's own settings with defaults
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = p.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = $"Project initialised. Goal: {goal} — token budget ${tokenBudget:0.##}, money budget ${moneyBudget:0.##} (autonomous ≤ ${moneyThreshold:0.##}), agent cap {agentCap}.",
                    });
                    // Create the project's Discord channel (best-effort; the website works regardless).
                    if (parent.DiscordManager != null)
                    {
                        try { await parent.DiscordManager.CreateProjectChannelAsync(p); }
                        catch (Exception dex) { _ = parent.ServiceLogError(dex, "Projects: create Discord channel failed"); }
                    }
                    await req.ReturnResponse(Json(p));
                }
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

            await parent.CreateAPIRoute("/projects/pause", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    project!.Status = ProjectStatus.Paused;
                    parent.Store.SaveProject(project);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = "Project paused by Klives.",
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
                    project!.Status = ProjectStatus.Active;
                    parent.Store.SaveProject(project);
                    parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project.ProjectID,
                        Type = ProjectEventTypes.Status,
                        Author = "klives",
                        Text = "Project resumed by Klives.",
                    });
                    await req.ReturnResponse(Json(project));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);

            // ── Timeline ──
            // Incremental read for the website's timeline/conversation panels:
            // GET /projects/events?projectID=..&since=..&max=..
            await parent.CreateAPIRoute("/projects/events", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    long since = long.TryParse(req.userParameters?.Get("since"), out var s) ? s : 0;
                    int max = int.TryParse(req.userParameters?.Get("max"), out var m) ? Math.Clamp(m, 1, 2000) : 500;
                    var events = parent.EventLog.ReadSince(project!.ProjectID, since, max);
                    await req.ReturnResponse(Json(new
                    {
                        events,
                        lastSequence = parent.EventLog.GetLastSequence(project.ProjectID),
                    }));
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

            // Patch one or more settings: POST body { "key": "value", ... } (keys per ProjectSettings.TrySet).
            await parent.CreateAPIRoute("/projects/settings/update", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var project)) return;
                    var patch = JsonConvert.DeserializeObject<Dictionary<string, string>>(req.userMessageContent ?? "{}") ?? new();
                    var settings = parent.Settings.Get(project!.ProjectID);
                    var applied = new List<string>();
                    var unknown = new List<string>();
                    foreach (var kv in patch)
                        (settings.TrySet(kv.Key, kv.Value) ? applied : unknown).Add(kv.Key);
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
                    var evt = parent.EventLog.Append(new ProjectEvent
                    {
                        ProjectID = project!.ProjectID,
                        Type = ProjectEventTypes.KlivesMessage,
                        Author = "klives",
                        Text = text,
                    });
                    if (project.Status == ProjectStatus.Active)
                        parent.CommanderRunner.Wake(project, $"Message from Klives: {text}");
                    await req.ReturnResponse(Json(evt));
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
                    await req.ReturnResponse(Json(parent.Hooks.List(project!.ProjectID)));
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
                    await parent.Adapters.IngestForHookAsync(projectID, hookID, req.userMessageContent ?? "");
                    await req.ReturnResponse(Json(new { accepted = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);
        }

        private bool RequireProject(Services.KliveAPI.KliveAPI.UserRequest req, out Project? project)
        {
            string projectID = req.userParameters?.Get("projectID") ?? "";
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
            return new
            {
                p.ProjectID,
                p.Name,
                p.Goal,
                Status = p.Status.ToString(),
                p.CreatedAt,
                p.TokenBudgetUsd,
                p.MoneyBudgetUsd,
                p.SubAgentCap,
                TokenSpendUsd = ledger.TokenSpendUsd,
                MoneySpendUsd = ledger.MoneySpendUsd,
                PendingApprovals = parent.Gates.ListPending(p.ProjectID).Count,
                lastSequence = parent.EventLog.GetLastSequence(p.ProjectID),
            };
        }

        private static async Task Err(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(ex.Message, code: HttpStatusCode.InternalServerError);
        }
    }
}
