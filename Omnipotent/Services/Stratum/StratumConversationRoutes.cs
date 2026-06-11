using Newtonsoft.Json;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// REST routes for the unified Stratum Engineer conversation. One persistent conversation
    /// per project; one active turn at a time. The frontend drives everything through these
    /// four endpoints plus the existing artifact-download route.
    /// </summary>
    public class StratumConversationRoutes
    {
        private readonly Stratum parent;
        private readonly StratumTimelineStore timeline;
        private readonly StratumEngineerTurnRunner turnRunner;

        public StratumConversationRoutes(Stratum parent, StratumTimelineStore timeline, StratumEngineerTurnRunner turnRunner)
        {
            this.parent = parent;
            this.timeline = timeline;
            this.turnRunner = turnRunner;
        }

        public async Task RegisterRoutes()
        {
            // Send a user message → starts an Engineer turn. Body: { text }.
            await parent.CreateAPIRoute("/stratum/conversation/send", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string text = ((string?)body?.text ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await req.ReturnResponse("text required", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    string? denyReason = parent.AgentManager.CheckAndRecordStart(user.UserID);
                    if (denyReason != null)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = denyReason }), code: HttpStatusCode.TooManyRequests);
                        return;
                    }

                    StratumAgentRun run;
                    try { run = turnRunner.StartTurn(project, user.UserID, text); }
                    catch (InvalidOperationException ex)
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.Conflict);
                        return;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { turnID = run.RunID }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Long-poll the timeline. Query: projectID, since.
            await parent.CreateAPIRoute("/stratum/conversation/events", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    long since = long.TryParse(req.userParameters?.Get("since"), out var s) ? s : 0;
                    var events = timeline.ReadSince(project.ProjectID, since, max: 300);
                    long lastSeq = events.Count > 0 ? events[^1].Sequence : timeline.GetLastSequence(project.ProjectID);

                    var meta = timeline.GetMeta(project.ProjectID);
                    object? activeTurn = null;
                    object? currentGate = null;
                    if (!string.IsNullOrWhiteSpace(meta.ActiveTurnID))
                    {
                        var run = parent.AgentManager.LoadOrGetRun(project.ProjectID, meta.ActiveTurnID);
                        if (run != null)
                        {
                            activeTurn = new { turnID = run.RunID, status = run.Status.ToString(), startedAt = run.StartedAt };
                            if (!string.IsNullOrWhiteSpace(run.CurrentGateID))
                            {
                                var gate = parent.RunStore.LoadGate(project.ProjectID, run.CurrentGateID);
                                if (gate != null && gate.Status == StratumGateStatus.Awaiting)
                                    currentGate = new
                                    {
                                        gateID = gate.GateID,
                                        runID = gate.RunID,
                                        title = gate.Title,
                                        description = gate.Description,
                                        rationale = gate.AgentRationale,
                                        proposalJson = gate.ProposalJson,
                                        artifactIDs = gate.ProposalArtifactIDs,
                                    };
                            }
                        }
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        events,
                        lastSequence = lastSeq,
                        activeTurn,
                        currentGate,
                    }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Resolve the current gate. Body: { gateID, decision: "Approve"|"Reject", comment }.
            await parent.CreateAPIRoute("/stratum/conversation/approve", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    dynamic body = JsonConvert.DeserializeObject<dynamic>(req.userMessageContent ?? "{}") ?? new System.Dynamic.ExpandoObject();
                    string gateID = (string?)body?.gateID ?? "";
                    string decisionStr = (string?)body?.decision ?? "";
                    string comment = (string?)body?.comment ?? "";
                    if (string.IsNullOrWhiteSpace(gateID) || !Enum.TryParse<StratumGateDecision>(decisionStr, true, out var decision))
                    {
                        await req.ReturnResponse("gateID and decision (Approve|Reject) required", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var gate = parent.RunStore.LoadGate(project.ProjectID, gateID);
                    if (gate == null)
                    {
                        await req.ReturnResponse("gate not found", code: HttpStatusCode.NotFound);
                        return;
                    }
                    bool ok = parent.AgentManager.ResolveGate(project.ProjectID, gateID, new StratumAgentManager.GateResolution
                    {
                        Decision = decision,
                        Comment = comment,
                        ResolvedByUserID = user.UserID,
                    });
                    if (ok)
                    {
                        timeline.Append(new StratumTimelineEvent
                        {
                            ProjectID = project.ProjectID,
                            TurnID = gate.RunID,
                            Type = StratumTimelineEventTypes.GateResolved,
                            Author = "user",
                            Text = $"{decision}: {gate.Title}{(string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment}")}",
                            GateID = gateID,
                        });
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Cancel the active turn.
            await parent.CreateAPIRoute("/stratum/conversation/cancel-turn", async req =>
            {
                try
                {
                    if (!RequireProject(req, out var user, out var project)) return;
                    var meta = timeline.GetMeta(project.ProjectID);
                    if (string.IsNullOrWhiteSpace(meta.ActiveTurnID))
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = false, error = "no active turn" }), code: HttpStatusCode.Conflict);
                        return;
                    }
                    parent.AgentManager.CancelRun(meta.ActiveTurnID);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { ok = true }));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Guest);
        }

        // ── helpers (same contract as StratumRoutes) ──

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
            if (p == null || !string.Equals(p.OwnerUserID, user.UserID, StringComparison.Ordinal))
            {
                req.ReturnResponse("project not found", code: HttpStatusCode.NotFound).GetAwaiter().GetResult();
                return false;
            }
            project = p;
            return true;
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            await req.ReturnResponse(JsonConvert.SerializeObject(new { error = ex.Message }), code: HttpStatusCode.InternalServerError);
        }
    }
}
