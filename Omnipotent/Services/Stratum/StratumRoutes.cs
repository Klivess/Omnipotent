using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// REST routes for Stratum. All routes require an authenticated KMProfile;
    /// project ownership is enforced server-side (a user cannot access another user's projects).
    /// </summary>
    public class StratumRoutes
    {
        private readonly Stratum parent;

        public StratumRoutes(Stratum parent)
        {
            this.parent = parent;
        }

        public async Task RegisterRoutes()
        {
            // ── Projects ──
            await parent.CreateAPIRoute("/stratum/projects", async req =>
            {
                try
                {
                    if (!RequireUser(req, out var user)) return;
                    var list = parent.Storage.ListProjectsForUser(user.UserID).Select(ToProjectSummary).ToList();
                    await req.ReturnResponse(JsonConvert.SerializeObject(list));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/projects/create", async req =>
            {
                try
                {
                    if (!RequireUser(req, out var user)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string name = (string?)body?.name ?? "";
                    string description = (string?)body?.description ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await req.ReturnResponse("name required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var p = parent.Storage.CreateProject(user.UserID, name, description);
                    await req.ReturnResponse(JsonConvert.SerializeObject(ToProjectDetail(p)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/projects/get", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    await req.ReturnResponse(JsonConvert.SerializeObject(ToProjectDetail(project)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/projects/rename", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string? newName = (string?)body?.name;
                    string? newDesc = (string?)body?.description;
                    parent.Storage.RenameProject(project.ProjectID, newName ?? project.Name, newDesc ?? project.Description);
                    await req.ReturnResponse(JsonConvert.SerializeObject(ToProjectDetail(parent.Storage.GetProject(project.ProjectID)!)));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/projects/delete", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    bool ok = parent.Storage.DeleteProject(project.ProjectID, user.UserID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // ── Revisions ──
            await parent.CreateAPIRoute("/stratum/revisions/create", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string title = (string?)body?.title ?? "";
                    string notes = (string?)body?.notes ?? "";
                    var rev = parent.Storage.CreateRevision(project.ProjectID, title, notes, user.UserID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(rev));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // ── Artifacts ──
            // Upload binary as raw body. Query params: projectID, revisionID, kind, fileName, contentType.
            await parent.CreateAPIRoute("/stratum/artifacts/upload", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string revisionID = req.userParameters?.Get("revisionID") ?? "";
                    string fileName = req.userParameters?.Get("fileName") ?? "";
                    string contentType = req.userParameters?.Get("contentType") ?? "application/octet-stream";
                    string kindStr = req.userParameters?.Get("kind") ?? "Other";

                    if (string.IsNullOrWhiteSpace(revisionID) || string.IsNullOrWhiteSpace(fileName))
                    {
                        await req.ReturnResponse("revisionID and fileName required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!Enum.TryParse<StratumArtifactKind>(kindStr, true, out var kind)) kind = StratumArtifactKind.Other;
                    byte[] data = req.userMessageBytes ?? Array.Empty<byte>();
                    if (data.Length == 0)
                    {
                        await req.ReturnResponse("empty body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var art = parent.Storage.AddArtifact(project.ProjectID, revisionID, kind, fileName, contentType, data);
                    await req.ReturnResponse(JsonConvert.SerializeObject(art));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/artifacts/download", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string artifactID = req.userParameters?.Get("artifactID") ?? "";
                    var resolved = parent.Storage.ResolveArtifact(project.ProjectID, artifactID);
                    if (resolved == null)
                    {
                        await req.ReturnResponse("artifact not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    var (_, _, art, blobPath) = resolved.Value;
                    if (!File.Exists(blobPath))
                    {
                        await req.ReturnResponse("artifact blob missing", code: HttpStatusCode.Gone);
                        return;
                    }
                    byte[] bytes = await File.ReadAllBytesAsync(blobPath);
                    await req.ReturnBinaryResponse(bytes, art.ContentType);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            // ── Attachments ──
            await parent.CreateAPIRoute("/stratum/attachments/upload", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string fileName = req.userParameters?.Get("fileName") ?? "";
                    string contentType = req.userParameters?.Get("contentType") ?? "application/octet-stream";
                    string? caption = req.userParameters?.Get("caption");

                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        await req.ReturnResponse("fileName required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    byte[] data = req.userMessageBytes ?? Array.Empty<byte>();
                    if (data.Length == 0)
                    {
                        await req.ReturnResponse("empty body", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var att = parent.Storage.AddAttachment(project.ProjectID, fileName, contentType, data, user.UserID, caption);
                    await req.ReturnResponse(JsonConvert.SerializeObject(att));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/attachments/download", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string attachmentID = req.userParameters?.Get("attachmentID") ?? "";
                    var resolved = parent.Storage.ResolveAttachment(project.ProjectID, attachmentID);
                    if (resolved == null)
                    {
                        await req.ReturnResponse("attachment not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    var (_, att, blobPath) = resolved.Value;
                    if (!File.Exists(blobPath))
                    {
                        await req.ReturnResponse("attachment blob missing", code: HttpStatusCode.Gone);
                        return;
                    }
                    byte[] bytes = await File.ReadAllBytesAsync(blobPath);
                    await req.ReturnBinaryResponse(bytes, att.ContentType);
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/attachments/delete", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string attachmentID = req.userParameters?.Get("attachmentID") ?? "";
                    bool ok = parent.Storage.DeleteAttachment(project.ProjectID, attachmentID, user.UserID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // ── Agent Runs ──
            await parent.CreateAPIRoute("/stratum/runs/start", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string agentTypeStr = (string?)body?.agentType ?? "Planning";
                    string prompt = (string?)body?.prompt ?? "";
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        await req.ReturnResponse("prompt required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!Enum.TryParse<StratumAgentType>(agentTypeStr, true, out var agentType))
                    {
                        await req.ReturnResponse($"unknown agentType '{agentTypeStr}'", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    // Phase 2: only Planning is implemented. Reject other types up-front so
                    // the UI can show a clear "coming in later phase" message.
                    if (agentType != StratumAgentType.Planning && agentType != StratumAgentType.Mechanical && agentType != StratumAgentType.Simulation && agentType != StratumAgentType.Electronics && agentType != StratumAgentType.Firmware)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = $"Agent type {agentType} not yet implemented." }),
                            code: HttpStatusCode.NotImplemented);
                        return;
                    }

                    // Phase 7: per-user concurrency cap + rate limit.
                    string? denyReason = parent.AgentManager.CheckAndRecordStart(user.UserID);
                    if (denyReason != null)
                    {
                        await req.ReturnResponse(
                            JsonConvert.SerializeObject(new { error = denyReason }),
                            code: HttpStatusCode.TooManyRequests);
                        return;
                    }

                    var attachmentIDs = new List<string>();
                    if (body?.attachmentIDs != null)
                    {
                        foreach (var id in body.attachmentIDs)
                        {
                            string? s = (string?)id;
                            if (!string.IsNullOrWhiteSpace(s)) attachmentIDs.Add(s);
                        }
                    }

                    string targetRevID = project.Revisions.LastOrDefault()?.RevisionID ?? "";

                    var run = new StratumAgentRun
                    {
                        RunID = Guid.NewGuid().ToString("N"),
                        ProjectID = project.ProjectID,
                        OwnerUserID = user.UserID,
                        AgentType = agentType,
                        UserPrompt = prompt,
                        AttachmentIDs = attachmentIDs,
                        TargetRevisionID = targetRevID,
                        CreatedAt = DateTime.UtcNow,
                        Status = StratumRunStatus.Pending,
                    };

                    if (agentType == StratumAgentType.Planning)
                    {
                        var plannerAgent = new StratumPlanningAgent();
                        parent.AgentManager.StartRun(run, ctx => plannerAgent.RunAsync(ctx));
                    }
                    else if (agentType == StratumAgentType.Mechanical)
                    {
                        var mechAgent = new StratumMechanicalAgent(parent.PythonRunner);
                        parent.AgentManager.StartRun(run, ctx => mechAgent.RunAsync(ctx));
                    }
                    else if (agentType == StratumAgentType.Electronics)
                    {
                        var catalog = await parent.GetPartsCatalogAsync();
                        var elecAgent = new StratumElectronicsAgent(catalog);
                        parent.AgentManager.StartRun(run, ctx => elecAgent.RunAsync(ctx));
                    }
                    else if (agentType == StratumAgentType.Firmware)
                    {
                        var fwAgent = new StratumFirmwareAgent(parent.PythonRunner);
                        parent.AgentManager.StartRun(run, ctx => fwAgent.RunAsync(ctx));
                    }
                    else // Simulation
                    {
                        var simAgent = new StratumSimulationAgent(parent.ToolManager);
                        parent.AgentManager.StartRun(run, ctx => simAgent.RunAsync(ctx));
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        runID = run.RunID,
                        status = run.Status.ToString(),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/runs/list", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    var runs = parent.RunStore.ListRunsForProject(project.ProjectID)
                        .Where(r => string.Equals(r.OwnerUserID, user.UserID, StringComparison.Ordinal))
                        .Select(r => new
                        {
                            runID = r.RunID,
                            agentType = r.AgentType.ToString(),
                            status = r.Status.ToString(),
                            createdAt = r.CreatedAt,
                            startedAt = r.StartedAt,
                            completedAt = r.CompletedAt,
                            wallClockSeconds = (r.StartedAt.HasValue
                                ? ((r.CompletedAt ?? DateTime.UtcNow) - r.StartedAt.Value).TotalSeconds
                                : (double?)null),
                            currentGateID = r.CurrentGateID,
                            iteration = r.Iteration,
                            userPrompt = r.UserPrompt,
                            errorMessage = r.ErrorMessage,
                        })
                        .ToList();
                    await req.ReturnResponse(JsonConvert.SerializeObject(runs));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/runs/get", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string runID = req.userParameters?.Get("runID") ?? "";
                    var run = parent.RunStore.LoadRun(project.ProjectID, runID);
                    if (run == null || !string.Equals(run.OwnerUserID, user.UserID, StringComparison.Ordinal))
                    {
                        await req.ReturnResponse("run not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    StratumApprovalGate? gate = null;
                    if (!string.IsNullOrWhiteSpace(run.CurrentGateID))
                        gate = parent.RunStore.LoadGate(project.ProjectID, run.CurrentGateID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { run, currentGate = gate }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/runs/events", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string runID = req.userParameters?.Get("runID") ?? "";
                    long since = 0;
                    long.TryParse(req.userParameters?.Get("since") ?? "0", out since);

                    // Owner check — without loading the run we'd leak event existence to other users.
                    var run = parent.RunStore.LoadRun(project.ProjectID, runID);
                    if (run == null || !string.Equals(run.OwnerUserID, user.UserID, StringComparison.Ordinal))
                    {
                        await req.ReturnResponse("run not found", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var events = parent.RunStore.ReadEventsSince(project.ProjectID, runID, since);
                    long lastSeq = events.Count > 0 ? events[^1].Sequence : since;
                    StratumApprovalGate? gate = null;
                    if (!string.IsNullOrWhiteSpace(run.CurrentGateID))
                        gate = parent.RunStore.LoadGate(project.ProjectID, run.CurrentGateID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        runID,
                        status = run.Status.ToString(),
                        currentGate = gate,
                        lastSequence = lastSeq,
                        events,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/runs/cancel", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    string runID = req.userParameters?.Get("runID") ?? "";
                    var run = parent.RunStore.LoadRun(project.ProjectID, runID);
                    if (run == null || !string.Equals(run.OwnerUserID, user.UserID, StringComparison.Ordinal))
                    {
                        await req.ReturnResponse("run not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    parent.AgentManager.CancelRun(runID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            await parent.CreateAPIRoute("/stratum/gates/resolve", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string gateID = (string?)body?.gateID ?? "";
                    string runID = (string?)body?.runID ?? "";
                    string decisionStr = (string?)body?.decision ?? "";
                    string comment = (string?)body?.comment ?? "";

                    if (string.IsNullOrWhiteSpace(gateID) || string.IsNullOrWhiteSpace(runID))
                    {
                        await req.ReturnResponse("gateID and runID required", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!Enum.TryParse<StratumGateDecision>(decisionStr, true, out var decision))
                    {
                        await req.ReturnResponse($"decision must be Approve or Reject", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    // Owner check via run record.
                    var run = parent.RunStore.LoadRun(project.ProjectID, runID);
                    if (run == null || !string.Equals(run.OwnerUserID, user.UserID, StringComparison.Ordinal))
                    {
                        await req.ReturnResponse("run not found", code: HttpStatusCode.NotFound);
                        return;
                    }

                    bool ok = parent.AgentManager.ResolveGate(project.ProjectID, gateID, new StratumAgentManager.GateResolution
                    {
                        Decision = decision,
                        Comment = comment,
                        ResolvedByUserID = user.UserID,
                    });
                    if (!ok)
                    {
                        await req.ReturnResponse("gate not awaiting (already resolved or unknown)", code: HttpStatusCode.Conflict);
                        return;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // ── Python runtime status (informational; bootstrap is triggered lazily by the Mechanical agent) ──
            await parent.CreateAPIRoute("/stratum/python/status", async req =>
            {
                try
                {
                    if (!RequireUser(req, out var _)) return;
                    var (venvExists, cqInstalled, hostPython) = parent.PythonRunner.Status();
                    bool pioInstalled = parent.PythonRunner.IsPlatformIOInstalled();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        venvExists,
                        cadqueryInstalled = cqInstalled,
                        platformioInstalled = pioInstalled,
                        hostPython,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            // ── Native tool status (gmsh, ccx) ──
            await parent.CreateAPIRoute("/stratum/tools/status", async req =>
            {
                try
                {
                    if (!RequireUser(req, out var _)) return;
                    var s = parent.ToolManager.Status();
                    await req.ReturnResponse(JsonConvert.SerializeObject(s));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            // ── Electronics catalog status ──
            await parent.CreateAPIRoute("/stratum/catalog/status", async req =>
            {
                try
                {
                    if (!RequireUser(req, out var _)) return;
                    var cat = await parent.GetPartsCatalogAsync();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        moduleCount = StratumModuleLibrary.Modules.Count,
                        mouserEnabled = cat.MouserEnabled,
                        categories = StratumModuleLibrary.Modules.GroupBy(m => m.Category).Select(g => new { category = g.Key, count = g.Count() }).ToList(),
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);
        }

        // ── Helpers ──
        private static bool RequireUser(KliveAPI.KliveAPI.UserRequest req, out KMProfile user)
        {
            user = req.user!;
            if (req.user == null)
            {
                req.ReturnResponse("authentication required", code: HttpStatusCode.Unauthorized).GetAwaiter().GetResult();
                return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves and authorises a project from query param `projectID`. Owner-only.
        /// Returns false (and writes a response) on any failure — caller should just return.
        /// </summary>
        private bool RequireProject(KliveAPI.KliveAPI.UserRequest req, out KMProfile user, out StratumProject project)
        {
            project = null!;
            user = null!;
            if (!RequireUser(req, out user)) return false;

            string? projectID = req.userParameters?.Get("projectID");
            if (string.IsNullOrWhiteSpace(projectID))
            {
                req.ReturnResponse("projectID required", code: HttpStatusCode.BadRequest).GetAwaiter().GetResult();
                return false;
            }
            var p = parent.Storage.GetProject(projectID);
            if (p == null)
            {
                req.ReturnResponse("project not found", code: HttpStatusCode.NotFound).GetAwaiter().GetResult();
                return false;
            }
            if (!string.Equals(p.OwnerUserID, user.UserID, StringComparison.Ordinal))
            {
                // Don't leak existence to non-owners.
                req.ReturnResponse("project not found", code: HttpStatusCode.NotFound).GetAwaiter().GetResult();
                return false;
            }
            project = p;
            return true;
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(
                JsonConvert.SerializeObject(new { error = ex.Message }),
                code: HttpStatusCode.InternalServerError);
        }

        // Lightweight DTOs so we don't leak unintended internals (and so UI gets a stable shape).
        private static object ToProjectSummary(StratumProject p) => new
        {
            projectID = p.ProjectID,
            name = p.Name,
            description = p.Description,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
            revisionCount = p.Revisions.Count,
            attachmentCount = p.Attachments.Count,
        };

        private static object ToProjectDetail(StratumProject p) => new
        {
            projectID = p.ProjectID,
            name = p.Name,
            description = p.Description,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
            revisions = p.Revisions,
            attachments = p.Attachments,
        };
    }
}
