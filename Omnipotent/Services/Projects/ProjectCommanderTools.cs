using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects.Stimulus;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.KliveAgent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>Result of a Commander tool call: text observation fed back to the model, and control flags.</summary>
    public record CommanderToolResult(string ResultText)
    {
        /// <summary>Machine-readable outcome used by renewable-work accounting. A failed command
        /// must never earn another context slice merely because its prose lacks a known marker.</summary>
        public bool Succeeded { get; init; } = true;
        /// <summary>Optional redacted text for the durable event log. ResultText still reaches the
        /// live model; short-lived codes and bearer tokens must not be persisted in CSV/history.</summary>
        public string? AuditText { get; init; }
        /// <summary>When set, the wake ends after this tool (e.g. a denied gate the Commander must respect).</summary>
        public bool EndWake { get; init; }
        /// <summary>Optional frame (screenshot after a computer_* action) for the vision return path.</summary>
        public byte[]? Jpeg { get; init; }
        /// <summary>Ordered visual action frames. The final entry is gridded and current; earlier
        /// entries are motion-significant context for transient states.</summary>
        public List<ComputerFrame> Frames { get; init; } = new();
        /// <summary>Artifacts produced by this call, referenced on the tool-result event.</summary>
        public List<string> ArtifactIDs { get; init; } = new();
    }

    /// <summary>
    /// Dispatches the Commander's non-computer tools against the project subsystems. Computer-use
    /// tools are handled separately by the runner (they need the acting agent's container
    /// adapter and tier gating). Stimulus-hook and inter-agent-message tools are wired to
    /// delegates so P4 can supply the bus without this class depending on it.
    /// </summary>
    public class ProjectCommanderTools
    {
        private readonly Project project;
        private readonly ProjectEventLogStore eventLog;
        private readonly ProjectDigestStore digests;
        private readonly ProjectSubAgentManager subAgents;
        private readonly ProjectGateManager gates;
        private readonly ProjectBudgetLedger budget;
        private readonly ProjectVault vault;
        private readonly ProjectStore projectStore;
        private readonly string actingAgentID;
        private readonly string wakeID;

        /// <summary>Delivers a message to another agent via the stimulus bus: (projectID, fromAgent, toAgent, message).</summary>
        public Func<string, string, string, string, Task>? SendAgentMessageAsync { get; set; }
        /// <summary>Optional P5 hook: surface a human-only obstacle through Discord.</summary>
        public Func<string, Task>? RequestHumanAsync { get; set; }
        /// <summary>Stimulus hook CRUD (null-safe: tools report unavailable when unset).</summary>
        public StimulusHookStore? HookStore { get; set; }
        /// <summary>Re-arms adapters after a hook mutation.</summary>
        public Action? RearmAdapters { get; set; }
        /// <summary>Runtime arm status for a hook (armed / passive / error), so listings show inert hooks.</summary>
        public Func<string, HookArmInfo?>? GetHookArmInfo { get; set; }
        /// <summary>Artifact storage for files the agent wants kept on the timeline.</summary>
        public ProjectArtifactStore? Artifacts { get; set; }
        /// <summary>Managed shared filesystem (the same bytes mounted at /project in every container).</summary>
        public ProjectFileStore? Files { get; set; }
        /// <summary>Named live values the agents maintain for Klives' at-a-glance dashboard.</summary>
        public ProjectObservableStore? Observables { get; set; }
        /// <summary>Machine-owned runtime/checkpoint state, separate from model-authored digest prose.</summary>
        public ProjectRuntimeStateStore? RuntimeState { get; set; }
        /// <summary>Durable Klives rules/tasks/steering receipts, never folded into the digest.</summary>
        public ProjectDirectiveStore? Directives { get; set; }
        /// <summary>Surfaces verified directive deliverables to Klives (Discord/UI) after completion.</summary>
        public Func<ProjectDirective, IReadOnlyList<string>, string, Task>? NotifyDirectiveCompletedAsync { get; set; }
        /// <summary>Marks the project completed (archives Discord channel, stops containers).</summary>
        public Func<Task>? CompleteProjectAsync { get; set; }
        /// <summary>Renames the project's Discord channel to match a new project name (null-safe).</summary>
        public Func<Task>? RenameDiscordChannelAsync { get; set; }
        /// <summary>Disposes a retired agent's own desktop container (frees ~1 GB immediately, not at project end).</summary>
        public Func<string, Task>? DisposeAgentDesktopAsync { get; set; }
        /// <summary>Immediately starts a newly-created agent on its assigned objective.</summary>
        public Func<ProjectAgentRecord, string, Task>? StartAgentAsync { get; set; }
        /// <summary>Cancels a retiring agent's in-flight wake before its resources are released.</summary>
        public Func<string, bool>? CancelAgentWake { get; set; }
        /// <summary>Recall from KliveAgent's shared memory (Projects is part of KliveAgent): (query, max, sinceUtc, untilUtc) → formatted results.</summary>
        public Func<string, int, DateTime?, DateTime?, Task<string>>? RecallMemoriesAsync { get; set; }
        /// <summary>Save to KliveAgent's shared memory: (content, tags) → confirmation.</summary>
        public Func<string, string[], Task<string>>? SaveMemoryAsync { get; set; }
        /// <summary>Cross-system knowledge search (KliveRAG): (query, max) → formatted cited results.</summary>
        public Func<string, int, Task<string>>? SearchKnowledgeAsync { get; set; }
        /// <summary>Open a knowledge document by id: (docId, maxTokens) → full text.</summary>
        public Func<string, int, Task<string>>? ReadKnowledgeDocAsync { get; set; }
        /// <summary>Live web search (SearXNG): (query, maxResults, fetchTop, timeRange) → formatted results.</summary>
        public Func<string, int, int, string?, Task<string>>? WebSearchAsync { get; set; }
        /// <summary>Fetch+index one web page: (url) → extracted text.</summary>
        public Func<string, Task<string>>? WebFetchAsync { get; set; }
        /// <summary>Runs an adversarial council synchronously: (topic, briefing, roles, urgency, purpose) → formatted verdict.</summary>
        public Func<string, string, string[]?, string, string, CancellationToken, Task<string>>? ConveneCouncilAsync { get; set; }
        /// <summary>Versioned Grand Plan store — the strategic north star Klives approves before work begins.</summary>
        public ProjectGrandPlanStore? GrandPlans { get; set; }
        /// <summary>Global shared account registry (accounts across all projects + KliveAgent). Null when the service is down.</summary>
        public Omnipotent.Services.AccountRegistry.AccountRegistry? Accounts { get; set; }
        /// <summary>The same live KliveAgent instance used by interactive execute_csharp. When
        /// present, Project scripts inherit the full ScriptGlobals service-discovery API.</summary>
        public Omnipotent.Services.KliveAgent.KliveAgent? KliveAgentService { get; set; }
        /// <summary>Flips a Planning project to Active once its Grand Plan is approved (also lifts the in-wake tool gate).</summary>
        public Func<Task>? ActivateProjectAsync { get; set; }

        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> humanRequestGates = new(StringComparer.Ordinal);
        // CommanderToolDispatch constructs a dispatcher per tool call. Keep the Roslyn state in a
        // bounded wake-scoped table so separate calls in one wake still behave like KliveAgent's
        // ContinueWithAsync session, without allowing old project wakes to retain live globals forever.
        private sealed class ScriptSession
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public WorkScriptGlobals? Globals;
            public ScriptState<object>? State;
            public DateTime LastUsedUtc = DateTime.UtcNow;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScriptSession> scriptSessions = new(StringComparer.Ordinal);

        /// <summary>Returns the durable audit form of tool arguments. File contents are deliberately
        /// omitted: their path/hash/size belong in the project-file audit, not duplicated into JSONL.</summary>
        public static string? AuditPayload(string toolName, string argsJson)
        {
            if (toolName.StartsWith("computer_", StringComparison.Ordinal)) return null;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                if (toolName == "write_file" && args["content"] is JToken content)
                {
                    string value = content.Type == JTokenType.String ? content.Value<string>() ?? "" : content.ToString(Formatting.None);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
                    args["content"] = $"[omitted {value.Length} chars; sha256={hash}]";
                }
                else if (toolName == "vault_save" && args["value"] != null)
                    args["value"] = "[redacted]";
                else if (toolName == "account_register" && args["secrets"] is JObject secrets)
                    foreach (var property in secrets.Properties().ToList()) property.Value = "[redacted]";
                else if (toolName == "account_update" && args["addSecretValue"] != null)
                    args["addSecretValue"] = "[redacted]";
                else if (toolName == "http_request" && args["body"] is JToken body)
                {
                    string value = body.Type == JTokenType.String ? body.Value<string>() ?? "" : body.ToString(Formatting.None);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
                    args["body"] = $"[omitted {value.Length} chars; sha256={hash}]";
                }
                if (args["url"]?.Type == JTokenType.String)
                    args["url"] = ComputerAudit.RedactUrl(args["url"]!.Value<string>() ?? "");
                return args.ToString(Formatting.None);
            }
            catch { return "{\"arguments\":\"[omitted: invalid arguments]\"}"; }
        }

        public ProjectCommanderTools(
            Project project, ProjectEventLogStore eventLog, ProjectDigestStore digests,
            ProjectSubAgentManager subAgents, ProjectGateManager gates, ProjectBudgetLedger budget,
            ProjectVault vault, ProjectStore projectStore, string actingAgentID, string wakeID)
        {
            this.project = project;
            this.eventLog = eventLog;
            this.digests = digests;
            this.subAgents = subAgents;
            this.gates = gates;
            this.budget = budget;
            this.vault = vault;
            this.projectStore = projectStore;
            this.actingAgentID = actingAgentID;
            this.wakeID = wakeID;
        }

        private static readonly HashSet<string> FileMutationTools = new(StringComparer.Ordinal)
        {
            "write_file", "make_directory", "move_file", "copy_file", "delete_file", "mark_file_important",
        };

        public async Task<CommanderToolResult> DispatchAsync(string tool, string argsJson, CancellationToken ct)
        {
            JObject a;
            try { a = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson); }
            catch { a = new JObject(); }

            // The offered-tool list is not a security boundary: a worker can still produce an
            // arbitrary function name. Enforce strategic/human-gate tools here as well so no
            // sub-agent can indirectly pause a project by opening a gate.
            if (actingAgentID != "commander" && ProjectTierRouter.IsCommanderOnly(tool))
                return FailedResult($"'{tool}' is reserved to the Commander. Report evidence and a recommended next action instead.");

            if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived && FileMutationTools.Contains(tool))
                return new CommanderToolResult("This project's shared filesystem is browse-only after completion or archive.")
                { Succeeded = false };

            switch (tool)
            {
                case "update_plan":
                {
                    string focus = ((string?)a["focus"] ?? "").Trim();
                    var steps = ((a["nextSteps"] as JArray) ?? new JArray())
                        .Select(t => ((string?)t ?? "").Trim()).Where(s => s.Length > 0).ToList();
                    string plan = ((string?)a["plan"] ?? "").Trim();
                    var digest = digests.GetDigest(project.ProjectID);
                    if (focus.Length > 0) digest.CurrentFocus = focus;
                    if (steps.Count > 0) digest.NextSteps = steps;
                    // Keep CurrentPlan populated for legacy display / wake seeds.
                    if (plan.Length > 0) digest.CurrentPlan = plan;
                    else if (focus.Length > 0 || steps.Count > 0) digest.CurrentPlan = ComposePlanText(focus, steps);
                    digests.SaveDigest(digest);
                    string logMsg = plan.Length > 0 ? plan : ComposePlanText(focus, steps);
                    eventLog.Append(Evt(ProjectEventTypes.Status, "commander", $"Plan updated: {Trunc(logMsg, 200)}"));
                    return new CommanderToolResult("Plan updated.");
                }

                case "report_progress":
                {
                    string note = (string?)a["note"] ?? "";
                    eventLog.Append(Evt(ProjectEventTypes.CommanderMessage, "commander", note));
                    return new CommanderToolResult("Progress recorded.");
                }

                case "list_project_directives":
                {
                    if (Directives == null) return FailedResult("Durable project memory is unavailable.");
                    bool includeResolved = (bool?)a["includeResolved"] ?? false;
                    var items = Directives.List(project.ProjectID, includeResolved)
                        .Where(x => ProjectDirectiveStore.ScopeAppliesTo(x, actingAgentID)).ToList();
                    if (items.Count == 0) return new CommanderToolResult("No durable Klives directives are recorded.");
                    var lines = items.Select(x =>
                        $"[{x.DirectiveID}] {x.Kind}/{x.Status} scope={x.Scope}: {Trunc(x.Text, 500)}" +
                        (x.ExpectedArtifactPaths.Count == 0 ? "" : $" | required: {string.Join(", ", x.ExpectedArtifactPaths)}"));
                    return new CommanderToolResult(string.Join("\n", lines));
                }

                case "acknowledge_project_directive":
                {
                    if (Directives == null) return FailedResult("Durable project memory is unavailable.");
                    string directiveID = ((string?)a["directiveID"] ?? "").Trim();
                    if (directiveID.Length == 0) return FailedResult("Provide directiveID.");
                    string note = ((string?)a["note"] ?? "").Trim();
                    var before = Directives.Get(project.ProjectID, directiveID);
                    if (before == null) return FailedResult($"No directive '{directiveID}' exists for this project.");
                    if (!ProjectDirectiveStore.AppliesTo(before, actingAgentID))
                        return FailedResult($"Directive '{directiveID}' is not assigned to {actingAgentID}.");
                    var updated = Directives.Acknowledge(project.ProjectID, directiveID, actingAgentID, note);
                    if (updated == null || updated.Status != ProjectDirectiveStatus.Acknowledged ||
                        !string.Equals(updated.AcknowledgedBy, actingAgentID, StringComparison.OrdinalIgnoreCase))
                        return FailedResult($"Directive '{directiveID}' cannot be acknowledged by {actingAgentID} (it may be a rule, resolved, or owned by another agent).");
                    eventLog.Append(Evt(ProjectEventTypes.DirectiveAcknowledged, actingAgentID,
                        $"Directive {directiveID} acknowledged by {actingAgentID}."));
                    return new CommanderToolResult($"Acknowledged directive {directiveID}.");
                }

                case "complete_project_directive":
                {
                    if (Directives == null) return FailedResult("Durable project memory is unavailable.");
                    string directiveID = ((string?)a["directiveID"] ?? "").Trim();
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    if (directiveID.Length == 0 || summary.Length == 0)
                        return FailedResult("complete_project_directive requires directiveID and summary.");
                    var directive = Directives.Get(project.ProjectID, directiveID);
                    if (directive == null) return FailedResult($"No directive '{directiveID}' exists for this project.");
                    if (directive.Kind == ProjectDirectiveKind.Rule)
                        return FailedResult("Standing rules cannot be completed; Klives must revoke or replace them.");
                    if (!ProjectDirectiveStore.AppliesTo(directive, actingAgentID))
                        return FailedResult($"Directive '{directiveID}' is not assigned to {actingAgentID}.");
                    if (directive.Status != ProjectDirectiveStatus.Acknowledged ||
                        !string.Equals(directive.AcknowledgedBy, actingAgentID, StringComparison.OrdinalIgnoreCase))
                        return FailedResult($"DIRECTIVE_ACK_REQUIRED: acknowledge directive {directiveID} as {actingAgentID} before completing it.");

                    var artifactPaths = ((a["artifactPaths"] as JArray) ?? new JArray())
                        .Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var verifiedArtifacts = new List<string>();
                    if (artifactPaths.Count > 0)
                    {
                        if (Files == null) return FailedResult("Shared file store is unavailable; supplied deliverables cannot be verified.");
                        foreach (string path in artifactPaths)
                        {
                            var entry = Files.Stat(project.ProjectID, path);
                            if (entry?.Kind == ProjectFileKind.File) verifiedArtifacts.Add(path);
                        }
                    }
                    if (directive.ExpectedArtifactPaths.Count > 0)
                    {
                        if (Files == null) return FailedResult("Shared file store is unavailable; required deliverables cannot be verified.");
                        foreach (string expected in directive.ExpectedArtifactPaths)
                        {
                            bool verified = verifiedArtifacts.Any(path => ExpectedArtifactMatches(expected, path) &&
                                IsExpectedArtifactVerified(Files, project.ProjectID, expected, path));
                            if (!verified)
                                return FailedResult($"DIRECTIVE_DELIVERABLE_MISSING: directive {directiveID} requires '{expected}'. Pass an existing matching /project path in artifactPaths after verification.");
                        }
                    }
                    var completed = Directives.Complete(project.ProjectID, directiveID, actingAgentID, summary, verifiedArtifacts);
                    if (completed == null || completed.Status != ProjectDirectiveStatus.Completed)
                        return FailedResult($"Directive '{directiveID}' cannot be completed by {actingAgentID}.");
                    var completedEvent = Evt(ProjectEventTypes.DirectiveCompleted, actingAgentID,
                        $"Directive {directiveID} completed by {actingAgentID}: {Trunc(summary, 500)}");
                    completedEvent.StimulusID = directiveID;
                    completedEvent.PayloadJson = JsonConvert.SerializeObject(new { directiveID, artifactPaths = verifiedArtifacts, batchID = completed.BatchID });
                    eventLog.Append(completedEvent);
                    if (NotifyDirectiveCompletedAsync != null)
                    {
                        try { await NotifyDirectiveCompletedAsync(completed, verifiedArtifacts, summary); }
                        catch { /* verified completion remains visible in the event log and project files */ }
                    }
                    return new CommanderToolResult($"Completed directive {directiveID}. Verified deliverables: " +
                        (verifiedArtifacts.Count == 0 ? "none recorded" : string.Join(", ", verifiedArtifacts)) + ".");
                }

                case "get_checkpoint":
                {
                    if (RuntimeState == null) return new CommanderToolResult("Typed checkpoint state is unavailable.");
                    return new CommanderToolResult(RuntimeState.DescribeForWake(project.ProjectID));
                }

                case "update_checkpoint":
                {
                    if (RuntimeState == null) return new CommanderToolResult("Typed checkpoint state is unavailable.");
                    string op = ((string?)a["op"] ?? "").Trim().ToLowerInvariant();
                    string key = ((string?)a["key"] ?? "").Trim();
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    var evidence = BuildCheckpointEvidence(a);
                    ProjectRuntimeMutationResult changed;

                    switch (op)
                    {
                        case "set_resume":
                            if (summary.Length == 0) return new CommanderToolResult("set_resume requires 'summary' with one exact next action.");
                            changed = RuntimeState.SetResumeAction(project.ProjectID, new ProjectResumeAction
                            {
                                Kind = "resume",
                                Summary = summary,
                                RecordedBy = actingAgentID,
                                NotBefore = ParseUtc((string?)a["notBefore"]),
                                Preconditions = (a["preconditions"] as JArray)?.Values<string>()
                                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? new(),
                                Evidence = evidence,
                            });
                            break;
                        case "clear_resume":
                            changed = RuntimeState.ClearResumeAction(project.ProjectID);
                            break;
                        case "upsert_fact":
                            if (key.Length == 0 || a["value"] == null) return new CommanderToolResult("upsert_fact requires 'key' and 'value'.");
                            if (evidence.Count == 0) return new CommanderToolResult("upsert_fact requires a stable evidenceReference and/or evidenceEventSequence.");
                            changed = RuntimeState.UpsertVerifiedFact(project.ProjectID, new ProjectVerifiedFact
                            {
                                Key = key,
                                Value = (string?)a["value"] ?? "",
                                Description = summary.Length == 0 ? null : summary,
                                ValidUntil = ParseUtc((string?)a["validUntil"]),
                                Evidence = evidence,
                            });
                            break;
                        case "invalidate_fact":
                            if (key.Length == 0) return new CommanderToolResult("invalidate_fact requires fact 'key'.");
                            changed = RuntimeState.InvalidateVerifiedFact(project.ProjectID, key,
                                summary.Length == 0 ? "Invalidated by project agent." : summary);
                            break;
                        case "register_artifact":
                        {
                            if (key.Length == 0) return new CommanderToolResult("register_artifact requires logical role in 'key'.");
                            string? path = (string?)a["projectPath"];
                            string? artifactID = (string?)a["artifactID"];
                            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(artifactID))
                                return new CommanderToolResult("register_artifact requires projectPath and/or artifactID.");
                            ProjectFileEntry? entry = null;
                            if (!string.IsNullOrWhiteSpace(path) && Files != null)
                            {
                                path = ProjectWorkspaceLocator.NormalizeRelative(project.ProjectID, path);
                                entry = Files.Stat(project.ProjectID, path);
                                if (entry == null) return new CommanderToolResult($"No project file exists at '{path}'; canonical artifacts must be verifiable.");
                                evidence.Add(new ProjectEvidenceReference { Kind = ProjectEvidenceKind.ProjectFile, Reference = path, ContentHash = entry.Sha256 });
                            }
                            string? expectedHash = ((string?)a["contentHash"] ?? "").Trim();
                            ProjectArtifactStore.ArtifactRecord? timelineArtifact = null;
                            if (!string.IsNullOrWhiteSpace(artifactID))
                            {
                                if (Artifacts == null) return new CommanderToolResult("Artifact store unavailable.");
                                timelineArtifact = Artifacts.GetRecord(project.ProjectID, artifactID);
                                if (timelineArtifact == null)
                                    return new CommanderToolResult($"No timeline artifact exists with id '{artifactID}'.");
                                bool hashMatches = expectedHash.Length == 0 || string.Equals(expectedHash, timelineArtifact.Sha256, StringComparison.OrdinalIgnoreCase);
                                timelineArtifact = Artifacts.Validate(project.ProjectID, artifactID, hashMatches,
                                    hashMatches ? "Validated while registering canonical artifact." : "Expected content hash did not match.");
                                evidence.Add(new ProjectEvidenceReference
                                {
                                    Kind = ProjectEvidenceKind.Artifact,
                                    Reference = artifactID,
                                    ContentHash = timelineArtifact?.Sha256,
                                    Description = timelineArtifact?.ValidationSummary,
                                });
                            }
                            changed = RuntimeState.UpsertCanonicalArtifact(project.ProjectID, new ProjectCanonicalArtifact
                            {
                                Role = key,
                                ProjectPath = path,
                                ArtifactID = artifactID,
                                ContentHash = entry?.Sha256 ?? timelineArtifact?.Sha256 ?? (expectedHash.Length == 0 ? null : expectedHash),
                                ValidationStatus = entry != null && (expectedHash.Length == 0 || string.Equals(expectedHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                                    || timelineArtifact?.State == ProjectArtifactStore.ArtifactLifecycleState.Validated
                                    ? ProjectArtifactValidationStatus.Valid
                                    : timelineArtifact?.State == ProjectArtifactStore.ArtifactLifecycleState.Rejected
                                        ? ProjectArtifactValidationStatus.Invalid : ProjectArtifactValidationStatus.Pending,
                                ValidatedAt = entry != null || timelineArtifact?.ValidatedAt != null ? DateTime.UtcNow : null,
                                Evidence = evidence,
                            });
                            break;
                        }
                        case "remove_artifact":
                            if (key.Length == 0) return new CommanderToolResult("remove_artifact requires logical role in 'key'.");
                            changed = RuntimeState.RemoveCanonicalArtifact(project.ProjectID, key);
                            break;
                        case "set_blocker":
                        case "clear_blocker":
                            return FailedResult("Project agents cannot set or clear project blockers. Report the obstacle, mitigation, and next action to Klives or the Commander instead.");
                        case "set_active_milestones":
                            changed = RuntimeState.SetActiveMilestones(project.ProjectID, (int?)a["grandPlanVersion"],
                                (a["milestoneIDs"] as JArray)?.Values<string>()
                                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!) ?? Array.Empty<string>());
                            break;
                        case "record_success":
                            if (summary.Length == 0 || evidence.Count == 0)
                                return new CommanderToolResult("record_success requires 'summary' and evidence.");
                            changed = RuntimeState.SetLastSuccessfulAction(project.ProjectID, new ProjectActionCheckpoint
                            {
                                Kind = "verified-progress",
                                Summary = summary,
                                RecordedBy = actingAgentID,
                                Evidence = evidence,
                            });
                            break;
                        default:
                            return new CommanderToolResult($"Unknown checkpoint op '{op}'.");
                    }

                    if (!changed.Applied) return new CommanderToolResult(changed.Reason ?? "Checkpoint update was rejected.");
                    string correlationKey = key.Length > 0 ? key : op switch
                    {
                        "set_resume" => changed.State.Checkpoint.ResumeAction?.ActionID ?? "resume-action",
                        "clear_resume" => "resume-action",
                        "set_active_milestones" => "active-milestones",
                        "record_success" => changed.State.Checkpoint.LastSuccessfulAction?.ActionID ?? "last-success",
                        _ => op,
                    };
                    var evt = Evt(ProjectEventTypes.CheckpointChanged, actingAgentID == "commander" ? "commander" : "agent",
                        $"Typed checkpoint updated: {op} ({correlationKey}).");
                    evt.PayloadJson = JsonConvert.SerializeObject(new { op, key = correlationKey, runtimeRevision = changed.State.Revision, checkpointRevision = changed.State.Checkpoint.Revision });
                    eventLog.Append(evt);
                    return new CommanderToolResult($"Checkpoint updated ({op}); runtime revision {changed.State.Revision}.");
                }

                case "list_observables":
                {
                    if (Observables == null) return new CommanderToolResult("Observables unavailable.");
                    var list = Observables.List(project.ProjectID);
                    if (list.Count == 0)
                        return new CommanderToolResult("No observables yet. Create one with update_observable(op:'set').");
                    return new CommanderToolResult(Observables.DescribeAll(project.ProjectID));
                }

                case "update_observable":
                {
                    if (Observables == null) return new CommanderToolResult("Observables unavailable.");
                    string name = ((string?)a["name"] ?? "").Trim();
                    string op = ((string?)a["op"] ??
                        (a["value"] != null || a["textValue"] != null ? "set" : "")).Trim().ToLowerInvariant();
                    if (name.Length == 0) return new CommanderToolResult("Provide 'name'.");
                    try
                    {
                        switch (op)
                        {
                            case "delete":
                            {
                                if (!Observables.Delete(project.ProjectID, name))
                                    return new CommanderToolResult($"No observable named '{name}'.");
                                AppendObservableEvent(name, op, $"{name}: deleted", null);
                                return new CommanderToolResult($"Deleted observable '{name}'.");
                            }
                            case "set":
                            {
                                double? num = (double?)a["value"];
                                string? text = (string?)a["textValue"];
                                ObservableFormat? fmt =
                                    Enum.TryParse<ObservableFormat>((string?)a["format"] ?? "", true, out var f) ? f : null;
                                DateTime? observedAt = ParseUtc((string?)a["observedAt"]);
                                TimeSpan? staleAfter = (double?)a["staleAfterSeconds"] is double staleSeconds && staleSeconds > 0
                                    ? TimeSpan.FromSeconds(staleSeconds) : null;
                                ObservableValidity validity = Enum.TryParse<ObservableValidity>((string?)a["validity"] ?? "", true, out var parsedValidity)
                                    ? parsedValidity : ObservableValidity.Unknown;
                                var change = Observables.Set(project.ProjectID, name, num, text, fmt,
                                    (string?)a["unit"], (string?)a["description"], actingAgentID,
                                    observedAt, staleAfter, ObservableSourceKind.Agent, validity,
                                    (long?)a["evidenceEventSequence"],
                                    (a["evidenceArtifactIDs"] as JArray)?.Values<string>()
                                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
                                AppendObservableEvent(change.Observable.Name, op,
                                    $"{change.Observable.Name}: {change.PreviousDisplay ?? "(new)"} → {change.NewDisplay} (set)", change);
                                return new CommanderToolResult($"Observable '{change.Observable.Name}' = {change.NewDisplay}.");
                            }
                            case "add": case "subtract": case "multiply": case "divide":
                            {
                                double? operand = (double?)a["value"];
                                if (operand == null) return new CommanderToolResult($"Provide numeric 'value' for op '{op}'.");
                                var change = Observables.Adjust(project.ProjectID, name, op, operand.Value, actingAgentID);
                                AppendObservableEvent(change.Observable.Name, op,
                                    $"{change.Observable.Name}: {change.PreviousDisplay} → {change.NewDisplay} ({op} {operand.Value})", change, operand);
                                return new CommanderToolResult($"Observable '{change.Observable.Name}' = {change.NewDisplay}.");
                            }
                            default:
                                return new CommanderToolResult($"Unknown op '{op}'. Use set, add, subtract, multiply, divide, or delete.");
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new CommanderToolResult(ex.Message);
                    }
                }

                case "update_project":
                {
                    string? newName = (string?)a["name"];
                    string? newDescription = (string?)a["description"];
                    newName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
                    newDescription = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription.Trim();
                    if (newName == null && newDescription == null)
                        return new CommanderToolResult("Provide 'name' and/or 'description' to change. Nothing was updated.");

                    var p = projectStore.GetProject(project.ProjectID);
                    if (p == null) return new CommanderToolResult("Project record not found.");

                    var changes = new List<string>();
                    bool nameChanged = false;
                    if (newName != null && newName != p.Name)
                    {
                        changes.Add($"name \"{p.Name}\" → \"{newName}\"");
                        // Mutate the persisted record AND the in-wake snapshot (the Discord-rename
                        // delegate and this wake's later event authoring both read the snapshot).
                        p.Name = newName;
                        project.Name = newName;
                        nameChanged = true;
                    }
                    if (newDescription != null && newDescription != p.Goal)
                    {
                        changes.Add("description revised");
                        p.Goal = newDescription;
                        project.Goal = newDescription;
                    }
                    if (changes.Count == 0)
                        return new CommanderToolResult("No change — the new values already match the current name/description.");

                    projectStore.SaveProject(p);
                    // Logged so Klives sees the identity change on the timeline and it seeds the next wake.
                    eventLog.Append(Evt(ProjectEventTypes.Status, "commander", $"Project details updated: {string.Join("; ", changes)}."));

                    // Mirror a name change to the project's Discord channel, exactly as the Klives-side rename does.
                    if (nameChanged && RenameDiscordChannelAsync != null)
                        try { await RenameDiscordChannelAsync(); } catch { }

                    return new CommanderToolResult($"Updated {string.Join("; ", changes)}. The change is live and seeds your next wake.");
                }

                case "spawn_sub_agent":
                {
                    string role = (string?)a["role"] ?? "worker";
                    string tierStr = (string?)a["tier"] ?? "Text";
                    string objective = (string?)a["objective"] ?? "";
                    if (string.IsNullOrWhiteSpace(objective))
                        return new CommanderToolResult("Provide a concrete non-empty objective for the new agent.");
                    if (!Enum.TryParse<ProjectAgentTier>(tierStr, ignoreCase: true, out var tier))
                        return new CommanderToolResult($"Unknown tier '{tierStr}'. Use Text, TextImage, TextImageVideo, or TextImageVideoAudio.");
                    try
                    {
                        var agent = subAgents.Spawn(project.ProjectID, actingAgentID, tier, role, objective);
                        eventLog.Append(new ProjectEvent
                        {
                            ProjectID = project.ProjectID, WakeID = wakeID, AgentID = agent.AgentID,
                            Type = ProjectEventTypes.Status,
                            Author = actingAgentID == "commander" ? "commander" : "agent",
                            Text = $"Objective assigned to {agent.AgentID}: {objective}",
                        });
                        if (StartAgentAsync != null) await StartAgentAsync(agent, objective);
                        return new CommanderToolResult($"Spawned and started {tier} agent '{role}' with ID {agent.AgentID}.");
                    }
                    catch (InvalidOperationException ex) { return new CommanderToolResult(ex.Message); }
                }

                case "retire_sub_agent":
                {
                    string id = (string?)a["agentID"] ?? "";
                    try
                    {
                        CancelAgentWake?.Invoke(id);
                        bool ok = subAgents.Retire(project.ProjectID, id);
                        // Free the retired agent's own desktop immediately rather than leaking it until
                        // the project completes.
                        if (ok && DisposeAgentDesktopAsync != null)
                            try { await DisposeAgentDesktopAsync(id); } catch { }
                        return new CommanderToolResult(ok ? $"Retired agent {id}." : $"No active agent {id}.");
                    }
                    catch (InvalidOperationException ex) { return new CommanderToolResult(ex.Message); }
                }

                case "assign_plan_work":
                {
                    if (actingAgentID != "commander") return new CommanderToolResult("Only the Commander assigns Grand Plan work.");
                    if (GrandPlans == null || !GrandPlans.HasApprovedPlan(project.ProjectID))
                        return new CommanderToolResult("No approved Grand Plan is available.");
                    string milestoneRef = ((string?)a["milestoneId"] ?? "").Trim();
                    string target = ((string?)a["agentID"] ?? "").Trim();
                    string objective = ((string?)a["objective"] ?? "").Trim();
                    var deliverables = (a["deliverablePaths"] as JArray)?.Values<string>()
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => ProjectWorkspaceLocator.NormalizeRelative(project.ProjectID, x)).ToList() ?? new();
                    var ready = GrandPlans.GetReadyMilestones(project.ProjectID)
                        .FirstOrDefault(m => string.Equals(m.ID, milestoneRef, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.Title, milestoneRef, StringComparison.OrdinalIgnoreCase));
                    if (ready == null) return new CommanderToolResult($"Milestone '{milestoneRef}' is not dependency-ready.");
                    if (!subAgents.TryResolveActiveTarget(project.ProjectID, target, out var worker, out var error)
                        || worker == null || worker.AgentID == "commander")
                        return new CommanderToolResult("Work not assigned: " + (error.Length > 0 ? error : "choose an active worker, not the Commander."));
                    if (!subAgents.AssignObjective(project.ProjectID, worker.AgentID, objective, new[] { ready.ID }, deliverables))
                        return new CommanderToolResult("Worker became unavailable before assignment.");
                    GrandPlans.UpdateMilestoneStatus(project.ProjectID, ready.ID, MilestoneStatus.InProgress,
                        ownerAgentID: worker.AgentID);
                    RuntimeState?.SetActiveMilestones(project.ProjectID,
                        GrandPlans.GetCurrentApproved(project.ProjectID)?.Version,
                        GrandPlans.GetCurrentApproved(project.ProjectID)?.Content?.Milestones
                            .Where(m => m.Status is MilestoneStatus.InProgress or MilestoneStatus.Blocked).Select(m => m.ID)
                            ?? Array.Empty<string>());
                    eventLog.Append(Evt(ProjectEventTypes.GrandPlanProgress, "commander",
                        $"Assigned dependency-ready milestone {ready.ID} '{ready.Title}' to {worker.Role} ({worker.AgentID})."));
                    if (StartAgentAsync != null) await StartAgentAsync(worker, objective);
                    return new CommanderToolResult($"Assigned {ready.ID} '{ready.Title}' to {worker.Role} ({worker.AgentID}); worker started.");
                }

                case "send_agent_message":
                {
                    string target = ((string?)a["agentID"] ?? "").Trim();
                    string message = ((string?)a["message"] ?? "").Trim();
                    if (message.Length == 0)
                        return new CommanderToolResult("Message not sent: provide a non-empty message.");
                    if (!subAgents.TryResolveActiveTarget(project.ProjectID, target, out var recipient, out var error))
                        return new CommanderToolResult($"Message not sent: {error}");

                    string toId = recipient!.AgentID;
                    if (SendAgentMessageAsync != null)
                        await SendAgentMessageAsync(project.ProjectID, actingAgentID, toId, message);
                    else
                        eventLog.Append(new ProjectEvent
                        {
                            ProjectID = project.ProjectID,
                            WakeID = wakeID,
                            AgentID = toId,
                            Type = ProjectEventTypes.AgentMessage,
                            Author = actingAgentID == "commander" ? "commander" : "agent",
                            Text = $"{actingAgentID} → {toId}: {message}",
                        });
                    return new CommanderToolResult($"Message sent to {recipient.Role} (agent ID {toId}).");
                }

                case "request_user_approval":
                {
                    if (actingAgentID != "commander")
                        return FailedResult("Only the Commander may open a project approval gate. Report the recommendation and evidence to the Commander instead.");
                    var gate = new ProjectGate
                    {
                        ProjectID = project.ProjectID,
                        WakeID = wakeID,
                        AgentID = actingAgentID,
                        Kind = "action",
                        Title = (string?)a["title"] ?? "Approval requested",
                        Description = (string?)a["description"] ?? "",
                        Rationale = (string?)a["rationale"] ?? "",
                    };
                    var res = await gates.OpenGateAndWaitAsync(gate, ct);
                    // A denial is a hard constraint — surface it but let the Commander adapt in-wake.
                    return new CommanderToolResult($"Klives {res.Decision}: {res.Comment}");
                }

                case "request_budget_increase":
                {
                    string kind = (string?)a["kind"] ?? "tokens";
                    double amount = (double?)a["amount"] ?? 0;
                    kind = kind.Trim().ToLowerInvariant();
                    if (kind is not ("tokens" or "money" or "agents") || !double.IsFinite(amount) || amount <= 0 ||
                        (kind == "agents" && amount != Math.Truncate(amount)))
                        return new CommanderToolResult("Use kind tokens, money, or agents with a positive finite limit (agents must be an integer).");
                    var gate = new ProjectGate
                    {
                        ProjectID = project.ProjectID,
                        WakeID = wakeID,
                        AgentID = actingAgentID,
                        Kind = "budget",
                        Title = $"Budget increase: {kind} → {amount}",
                        Description = $"Requesting {kind} limit of {amount}.",
                        Rationale = (string?)a["rationale"] ?? "",
                    };
                    var res = await gates.OpenGateAndWaitAsync(gate, ct);
                    if (res.Decision == GateDecision.Approve)
                    {
                        var p = projectStore.GetProject(project.ProjectID);
                        if (p != null)
                        {
                            switch (kind)
                            {
                                case "tokens":
                                    p.TokenBudgetUsd = amount;
                                    projectStore.SaveProject(p);
                                    if (p.Status == ProjectStatus.BudgetPaused && budget.NotifyBudgetChanged(p.ProjectID))
                                    {
                                        // Resume to where it left off: a project that never finished planning
                                        // returns to Planning (the Grand Plan gate still stands), not straight to work.
                                        ProjectStatus fromStatus = p.Status;
                                        p.Status = (GrandPlans?.HasApprovedPlan(p.ProjectID) ?? true)
                                            ? ProjectStatus.Active : ProjectStatus.Planning;
                                        projectStore.SaveProject(p);
                                        eventLog.Append(new ProjectEvent
                                        {
                                            ProjectID = p.ProjectID,
                                            WakeID = wakeID,
                                            AgentID = actingAgentID,
                                            Type = ProjectEventTypes.Status,
                                            Author = "klives",
                                            Text = "Approved token budget increase resumed the budget-paused project.",
                                            PayloadJson = ProjectLifecycleEvents.Payload(
                                                fromStatus, p.Status, "approved-budget-increase-resume"),
                                        });
                                    }
                                    break;
                                case "money": p.MoneyBudgetUsd = amount; break;
                                case "agents": p.SubAgentCap = (int)amount; break;
                            }
                            if (kind != "tokens") projectStore.SaveProject(p);
                        }
                    }
                    return new CommanderToolResult($"Klives {res.Decision}: {res.Comment}");
                }

                case "record_money_spend":
                {
                    double amount = (double?)a["amount"] ?? 0;
                    string description = (string?)a["description"] ?? "";
                    if (!double.IsFinite(amount) || amount <= 0)
                        return new CommanderToolResult("Provide a positive finite 'amount' in USD.");
                    if (string.IsNullOrWhiteSpace(description)) return new CommanderToolResult("Provide a 'description' of the spend.");

                    // Autonomous (within per-action threshold and remaining budget) → record now.
                    // Otherwise open an approval gate; record only on approval.
                    if (budget.IsMoneySpendAutonomous(project.ProjectID, amount))
                    {
                        budget.RecordMoneySpend(project.ProjectID, amount, description);
                        return new CommanderToolResult($"Recorded autonomous spend ${amount:0.##}: {description}. {budget.DescribeState(project.ProjectID)}");
                    }

                    var p = projectStore.GetProject(project.ProjectID);
                    double threshold = p?.MoneyAutonomousThresholdUsd ?? 0;
                    var gate = new ProjectGate
                    {
                        ProjectID = project.ProjectID,
                        WakeID = wakeID,
                        AgentID = actingAgentID,
                        Kind = "money",
                        Title = $"Approve real-money spend: ${amount:0.##}",
                        Description = $"{description} (${amount:0.##}).",
                        Rationale = $"Above the autonomous threshold (${threshold:0.##}) or would exceed the money budget.",
                    };
                    var res = await gates.OpenGateAndWaitAsync(gate, ct);
                    if (res.Decision != GateDecision.Approve)
                        return new CommanderToolResult($"Klives {res.Decision}: {res.Comment} — spend NOT recorded.");
                    budget.RecordMoneySpend(project.ProjectID, amount, description);
                    return new CommanderToolResult($"Approved and recorded ${amount:0.##}: {description}. {budget.DescribeState(project.ProjectID)}");
                }

                case "vault_save":
                {
                    string name = (string?)a["name"] ?? "";
                    string value = (string?)a["value"] ?? "";
                    if (string.IsNullOrWhiteSpace(name)) return new CommanderToolResult("Provide a name.");
                    vault.Save(project.ProjectID, name, value);
                    return new CommanderToolResult($"Saved secret '{name}'. Reference it as {{{name}}} in typed text; its value is never shown to you again.");
                }

                case "vault_list":
                {
                    var names = vault.ListNames(project.ProjectID);
                    return new CommanderToolResult(names.Count == 0 ? "Vault is empty." : "Vault secrets: " + string.Join(", ", names));
                }

                case "request_human":
                {
                    string what = ((string?)a["what"] ?? (string?)a["description"] ?? "").Trim();
                    string title = ((string?)a["title"] ?? "").Trim();
                    string rationale = ((string?)a["rationale"] ?? "").Trim();
                    if (what.Length == 0)
                        return new CommanderToolResult("Provide 'what' or 'description' with the exact human-only action.") { Succeeded = false };
                    if (IsAutomaticProviderRecoveryRequest(what, title, rationale))
                        return FailedResult(
                            "INFRASTRUCTURE_REQUEST_REJECTED: an LLM provider rate limit or temporary provider failure is retried automatically. " +
                            "It does not make project tools or files inaccessible, so do not ask Klives to read files or perform work. Continue independent work and let the retry circuit recover.");
                    if (title.Length > 0) what = title + ": " + what;
                    if (rationale.Length > 0) what += "\nWhy a human is required: " + rationale;
                    if (RequestHumanAsync == null)
                        return new CommanderToolResult("Human-assistance delivery is unavailable; the request was not sent.") { Succeeded = false };
                    var deliveryGate = humanRequestGates.GetOrAdd(project.ProjectID, _ => new SemaphoreSlim(1, 1));
                    await deliveryGate.WaitAsync(ct);
                    try
                    {
                        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            Regex.Replace(what, @"\s+", " ").Trim().ToLowerInvariant())));
                        var humanThreadEvents = eventLog.EnumerateRange(project.ProjectID, null, null)
                            .Where(e => e.Type is ProjectEventTypes.HumanAssistanceRequested or ProjectEventTypes.KlivesMessage)
                            .ToList();
                        var prior = humanThreadEvents.LastOrDefault(e => e.Type == ProjectEventTypes.HumanAssistanceRequested
                            && TryReadPayloadString(e.PayloadJson, "requestFingerprint") == fingerprint);
                        if (prior != null && !humanThreadEvents.Any(e => e.Sequence > prior.Sequence && e.Type == ProjectEventTypes.KlivesMessage))
                            return FailedResult(
                                $"HUMAN_REQUEST_ALREADY_OPEN: the same request was delivered at event #{prior.Sequence} and Klives has not replied. " +
                                "Keep it as an open thread and make progress elsewhere; do not send another notification.");
                        await RequestHumanAsync(what);
                        var requestEvent = Evt(ProjectEventTypes.HumanAssistanceRequested, "commander", $"Human assistance requested: {what}");
                        requestEvent.PayloadJson = JsonConvert.SerializeObject(new { requestFingerprint = fingerprint });
                        eventLog.Append(requestEvent);
                        return new CommanderToolResult("Requested human assistance (surfaced to Klives).");
                    }
                    finally { deliveryGate.Release(); }
                }

                // ── Shared account registry (global across all projects + KliveAgent) ──
                case "account_register":
                {
                    if (Accounts == null) return FailedResult("Account registry unavailable.");
                    string service = (string?)a["service"] ?? "";
                    string username = (string?)a["username"] ?? "";
                    string? email = (string?)a["email"];
                    if (AccountUsesDisposableMail(service, email))
                        return new CommanderToolResult("DISPOSABLE_MAIL_PROHIBITED: Projects use native KliveMail for verification email. Create/reuse an @klive.dev mailbox with klivemail_create_mailbox and register the target service account against that address; do not create mail.tm fallback accounts.")
                        { Succeeded = false };
                    string? description = (string?)a["description"];
                    bool allowDuplicate = (bool?)a["allowDuplicate"] ?? false;
                    string? reason = (string?)a["reason"];
                    var secrets = new Dictionary<string, string>();
                    if (a["secrets"] is Newtonsoft.Json.Linq.JObject so)
                        foreach (var prop in so.Properties())
                            secrets[prop.Name] = (string?)prop.Value ?? "";
                    var registration = await Accounts.RegisterAccountDetailedAsync(
                        service, username, email, secrets, description,
                        createdBy: "project:" + project.ProjectID, owner: "project:" + project.ProjectID,
                        allowDuplicate, reason);
                    if (registration.Created)
                        AppendAccountEvent(registration.Account?.ServiceKey ?? service,
                            registration.Account?.Username ?? username, allowDuplicate ? "register-duplicate" : "register");
                    return new CommanderToolResult(registration.Message) { Succeeded = !registration.Failed };
                }

                case "account_list":
                {
                    if (Accounts == null) return FailedResult("Account registry unavailable.");
                    string? service = (string?)a["service"];
                    return new CommanderToolResult(await Accounts.DescribeAccountsAsync("project:" + project.ProjectID, service));
                }

                case "account_update":
                {
                    if (Accounts == null) return FailedResult("Account registry unavailable.");
                    string accountID = (string?)a["accountID"] ?? "";
                    if (string.IsNullOrWhiteSpace(accountID)) return FailedResult("Provide accountID (from account_list).");
                    if (Accounts.Get(accountID) == null) return FailedResult($"No account with id '{accountID}'.");
                    var done = new List<string>();
                    if (a["status"] != null && Enum.TryParse<Omnipotent.Services.AccountRegistry.AccountStatus>((string?)a["status"], true, out var st)
                        && Accounts.UpdateStatus(accountID, st)) done.Add($"status={st}");
                    if (a["notes"] != null && Accounts.UpdateNotes(accountID, (string?)a["notes"])) done.Add("notes");
                    string? secretName = (string?)a["addSecretName"];
                    string? secretValue = (string?)a["addSecretValue"];
                    if (!string.IsNullOrWhiteSpace(secretName) && secretValue != null)
                    { if (Accounts.AddSecret(accountID, secretName, secretValue)) done.Add($"secret '{secretName}'"); }
                    if ((bool?)a["claim"] == true)
                    { if (Accounts.ClaimForOwner(accountID, "project:" + project.ProjectID)) done.Add("claimed"); }
                    var updated = Accounts.Get(accountID);
                    if (done.Count > 0 && updated != null)
                        AppendAccountEvent(updated.ServiceKey, updated.Username, "update");
                    return new CommanderToolResult(done.Count == 0 ? "Nothing changed (provide valid status, notes, addSecretName+addSecretValue, or claim)." : "Updated: " + string.Join(", ", done) + ".")
                    { Succeeded = done.Count > 0 };
                }

                // ── KliveAgent shared memory (Projects is part of KliveAgent — memory transfers) ──
                case "recall_memories":
                {
                    if (RecallMemoriesAsync == null) return new CommanderToolResult("Memory unavailable.");
                    string query = (string?)a["query"] ?? "";
                    int max = (int?)a["max"] ?? 8;
                    var nowRecall = DateTime.UtcNow;
                    DateTime? since = TemporalParse.TryParsePastInstant((string?)a["since"], nowRecall, out var s) ? s : null;
                    DateTime? until = TemporalParse.TryParsePastInstant((string?)a["until"], nowRecall, out var u) ? u : null;
                    return new CommanderToolResult(await RecallMemoriesAsync(query, Math.Clamp(max, 1, 25), since, until));
                }

                case "recall_memories_by_tag":
                {
                    if (KliveAgentService == null) return new CommanderToolResult("Shared KliveAgent memory is unavailable.");
                    string tag = ((string?)a["tag"] ?? "").Trim();
                    if (tag.Length == 0) return new CommanderToolResult("Provide 'tag'.");
                    var memories = await SharedGlobals(ct).RecallMemoriesByTag(tag);
                    return new CommanderToolResult(FormatMemories(memories));
                }

                case "query_events":
                {
                    var now = DateTime.UtcNow;
                    string? fromText = (string?)a["from"];
                    string? toText = (string?)a["to"];
                    DateTime? from = null, to = null;
                    if (!string.IsNullOrWhiteSpace(fromText))
                    {
                        if (!TemporalParse.TryParsePastInstant(fromText, now, out var f))
                            return new CommanderToolResult($"Could not parse 'from' ('{fromText}'). Use a UTC date-time (\"2026-07-10 06:00\") or a lookback (\"24h\", \"7d\"). Current time: {TemporalFormat.NowStamp()}.");
                        from = f;
                    }
                    if (!string.IsNullOrWhiteSpace(toText))
                    {
                        if (!TemporalParse.TryParsePastInstant(toText, now, out var t))
                            return new CommanderToolResult($"Could not parse 'to' ('{toText}'). Use a UTC date-time or a lookback (\"24h\"). Current time: {TemporalFormat.NowStamp()}.");
                        to = t;
                    }
                    string? contains = (string?)a["contains"];
                    string? typeFilter = (string?)a["type"];
                    string? author = (string?)a["author"];
                    int maxEvents = Math.Clamp((int?)a["max"] ?? 40, 1, 200);

                    // Stream the range, filter, and keep only the newest max — a bounded window over
                    // an unbounded log, biased toward what happened most recently within it.
                    var matched = new LinkedList<ProjectEvent>();
                    long totalMatched = 0;
                    foreach (var e in eventLog.EnumerateRange(project.ProjectID, from, to))
                    {
                        if (!string.IsNullOrWhiteSpace(typeFilter)
                            && !e.Type.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(author)
                            && !string.Equals(e.Author, author, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(contains)
                            && (e.Text == null || !e.Text.Contains(contains, StringComparison.OrdinalIgnoreCase))) continue;
                        totalMatched++;
                        matched.AddLast(e);
                        if (matched.Count > maxEvents) matched.RemoveFirst();
                    }
                    if (totalMatched == 0)
                        return new CommanderToolResult(
                            $"No events matched between {(from.HasValue ? TemporalFormat.Stamp(from.Value) : "log start")} and {(to.HasValue ? TemporalFormat.Stamp(to.Value) : "now")}.");
                    var sbEvents = new StringBuilder();
                    sbEvents.AppendLine($"{totalMatched} event(s) between {(from.HasValue ? TemporalFormat.Stamp(from.Value) : "log start")} and {(to.HasValue ? TemporalFormat.Stamp(to.Value) : $"now ({TemporalFormat.NowStamp()})")}"
                        + (totalMatched > matched.Count ? $"; showing the most recent {matched.Count}:" : ":"));
                    foreach (var e in matched)
                        sbEvents.AppendLine(ProjectCommanderPrompts.DescribeEvent(e));
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(sbEvents.ToString().TrimEnd(), ProjectsContextBudget.ToolResultBudget));
                }

                case "save_memory":
                {
                    if (SaveMemoryAsync == null) return new CommanderToolResult("Memory unavailable.");
                    string content = (string?)a["content"] ?? "";
                    if (string.IsNullOrWhiteSpace(content)) return new CommanderToolResult("Provide 'content' to remember.");
                    var tags = a["tags"] is JArray arr ? arr.Select(t => t.ToString()).ToArray() : Array.Empty<string>();
                    return new CommanderToolResult(await SaveMemoryAsync(content, tags));
                }

                case "save_shortcut":
                {
                    if (KliveAgentService == null) return new CommanderToolResult("Shared KliveAgent memory is unavailable.");
                    string title = ((string?)a["title"] ?? "").Trim();
                    string content = ((string?)a["content"] ?? "").Trim();
                    if (title.Length == 0 || content.Length == 0) return new CommanderToolResult("Provide 'title' and 'content'.");
                    var tags = a["tags"] is JArray arr ? arr.Select(t => t.ToString()).ToArray() : Array.Empty<string>();
                    string id = await SharedGlobals(ct).SaveShortcut(title, content, tags);
                    return new CommanderToolResult($"Saved shared shortcut '{title}' ({ShortId(id)}).");
                }

                case "get_shortcuts":
                    return KliveAgentService == null
                        ? new CommanderToolResult("Shared KliveAgent memory is unavailable.")
                        : new CommanderToolResult(await SharedGlobals(ct).GetShortcuts());

                case "delete_memory":
                {
                    if (KliveAgentService == null) return new CommanderToolResult("Shared KliveAgent memory is unavailable.");
                    string id = ((string?)a["id"] ?? "").Trim();
                    if (id.Length == 0) return new CommanderToolResult("Provide 'id'.");
                    bool deleted = await SharedGlobals(ct).DeleteMemory(id);
                    return new CommanderToolResult(deleted ? $"Deleted shared memory {id}." : $"No unique shared memory matches '{id}'.");
                }

                case "search_knowledge":
                {
                    if (SearchKnowledgeAsync == null) return new CommanderToolResult("Knowledge service unavailable.");
                    string query = (string?)a["query"] ?? "";
                    if (string.IsNullOrWhiteSpace(query)) return new CommanderToolResult("Provide a 'query'.");
                    int max = (int?)a["max"] ?? 8;
                    return new CommanderToolResult(await SearchKnowledgeAsync(query, Math.Clamp(max, 1, 20)));
                }

                case "read_knowledge_doc":
                {
                    if (ReadKnowledgeDocAsync == null) return new CommanderToolResult("Knowledge service unavailable.");
                    string docId = (string?)a["docId"] ?? "";
                    if (string.IsNullOrWhiteSpace(docId)) return new CommanderToolResult("Provide a 'docId'.");
                    int maxTokens = (int?)a["maxTokens"] ?? 1500;
                    return new CommanderToolResult(await ReadKnowledgeDocAsync(docId, Math.Clamp(maxTokens, 200, 3000)));
                }

                case "web_search":
                {
                    if (WebSearchAsync == null) return new CommanderToolResult("Knowledge service unavailable.");
                    string query = (string?)a["query"] ?? "";
                    if (string.IsNullOrWhiteSpace(query)) return new CommanderToolResult("Provide a 'query'.");
                    int maxResults = (int?)a["maxResults"] ?? 6;
                    int fetchTop = (int?)a["fetchTop"] ?? 2;
                    string? timeRange = (string?)a["timeRange"];
                    return new CommanderToolResult(await WebSearchAsync(query, maxResults, fetchTop, timeRange));
                }

                case "web_fetch":
                {
                    if (WebFetchAsync == null) return new CommanderToolResult("Knowledge service unavailable.");
                    string url = (string?)a["url"] ?? "";
                    if (string.IsNullOrWhiteSpace(url)) return new CommanderToolResult("Provide a 'url'.");
                    return new CommanderToolResult(await WebFetchAsync(url));
                }

                // ── KliveMail: first-class inbox tools (in-process; NO HTTP call, NO auth header,
                //    NO service reflection — resolve the live service and drive its repository). ──

                case "klivemail_create_mailbox":
                {
                    var repo = GetKliveMailRepo(out var kmErr);
                    if (repo == null) return FailedResult(kmErr!);
                    string address = ((string?)a["address"] ?? "").Trim();
                    if (address.Length == 0)
                        return FailedResult("Provide an 'address'. KliveMail is catch-all on @klive.dev; the domain is added if you omit it (e.g. 'tiktok.memesquad' → tiktok.memesquad@klive.dev).");
                    string? displayName = (string?)a["displayName"];
                    string purpose = NormalizeMailboxPurpose((string?)a["purpose"]);
                    string normalized = Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.NormalizeAddress(address);
                    if (!normalized.EndsWith("@" + Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase))
                        return FailedResult($"'{normalized}' isn't a KliveMail address — KliveMail only accepts its own @{Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.MailDomain} domain.");
                    try
                    {
                        bool created = await repo.CreateMailboxAsync(normalized, displayName, ct);
                        RecordMailboxAvailable(normalized, purpose);
                        return new CommanderToolResult(created
                            ? $"Created canonical mailbox {normalized}{(purpose.Length == 0 ? "" : $" for purpose '{purpose}'")}. It receives mail immediately (catch-all). Poll it with klivemail_list_messages, or block for a code with klivemail_wait_for_code."
                            : $"Canonical mailbox {normalized}{(purpose.Length == 0 ? "" : $" for purpose '{purpose}'")} already exists and is ready to receive. Use this exact address.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        RecordKliveMailHealth(false, ex.GetType().Name, ex.Message);
                        return new CommanderToolResult($"klivemail_create_mailbox failed: {ex.Message}") { Succeeded = false };
                    }
                }

                case "klivemail_list_messages":
                {
                    var repo = GetKliveMailRepo(out var kmErr);
                    if (repo == null) return FailedResult(kmErr!);
                    string mailbox = ((string?)a["mailbox"] ?? "").Trim();
                    int limit = Math.Clamp((int?)a["limit"] ?? 20, 1, 100);
                    bool unreadOnly = (bool?)a["unreadOnly"] ?? false;
                    try
                    {
                        string? mb = mailbox.Length == 0 ? null : Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.NormalizeAddress(mailbox);
                        var msgs = await repo.ListMessagesAsync(mb, unreadOnly, false, false, 1, limit, ct);
                        RecordKliveMailHealth(true, "RepositoryReachable", mb == null
                            ? "The live KliveMail repository was queried successfully."
                            : $"The live KliveMail repository was queried successfully for {mb}.");
                        if (msgs.Count == 0)
                            return new CommanderToolResult(mb == null ? "No messages in KliveMail." : $"No messages for {mb} yet.");
                        var sb = new StringBuilder();
                        sb.AppendLine($"{msgs.Count} message(s){(mb == null ? "" : " for " + mb)} (newest first):");
                        foreach (var m in msgs)
                            sb.AppendLine($"- id={m.Id} | {m.ReceivedUtc:yyyy-MM-dd HH:mm}Z | from {m.FromAddress} → {m.ToAddress} | {(m.IsRead ? "" : "[unread] ")}{Trunc(m.Subject ?? "(no subject)", 80)} — {Trunc(m.Snippet ?? "", 100)}");
                        return new CommanderToolResult(sb.ToString().TrimEnd())
                        {
                            AuditText = $"KliveMail listed {msgs.Count} message summary record(s){(mb == null ? "" : " for " + mb)}; ids/from/to retained, subjects and snippets omitted from durable history. " +
                                string.Join("; ", msgs.Select(m => $"id={m.Id}, from={m.FromAddress}, to={m.ToAddress}, received={m.ReceivedUtc:O}")),
                        };
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        RecordKliveMailHealth(false, ex.GetType().Name, ex.Message);
                        return new CommanderToolResult($"klivemail_list_messages failed: {ex.Message}") { Succeeded = false };
                    }
                }

                case "klivemail_get_message":
                {
                    var repo = GetKliveMailRepo(out var kmErr);
                    if (repo == null) return FailedResult(kmErr!);
                    string id = ((string?)a["id"] ?? "").Trim();
                    if (id.Length == 0) return FailedResult("Provide the message 'id' (from klivemail_list_messages).");
                    try
                    {
                        var m = await repo.GetMessageAsync(id, ct);
                        if (m == null) return FailedResult($"No message with id '{id}'.");
                        RecordKliveMailHealth(true, "RepositoryReachable", "The live KliveMail message store returned a message successfully.");
                        string bodyText = !string.IsNullOrWhiteSpace(m.BodyText) ? m.BodyText : (StripHtml(m.BodyHtml) ?? "");
                        var sb = new StringBuilder();
                        sb.AppendLine($"From: {m.FromName} <{m.FromAddress}>");
                        sb.AppendLine($"To: {m.ToAddress}");
                        sb.AppendLine($"Date: {m.ReceivedUtc:yyyy-MM-dd HH:mm}Z");
                        sb.AppendLine($"Subject: {m.Subject}");
                        if (m.HasAttachments) sb.AppendLine($"Attachments: {m.Attachments.Count}");
                        sb.AppendLine();
                        sb.Append(bodyText);
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(sb.ToString(), ProjectsContextBudget.ToolResultBudget))
                        {
                            AuditText = $"KliveMail message read: id={m.Id}, from={m.FromAddress}, to={m.ToAddress}; subject and body omitted from durable history.",
                        };
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        RecordKliveMailHealth(false, ex.GetType().Name, ex.Message);
                        return new CommanderToolResult($"klivemail_get_message failed: {ex.Message}") { Succeeded = false };
                    }
                }

                case "klivemail_wait_for_code":
                {
                    var repo = GetKliveMailRepo(out var kmErr);
                    if (repo == null) return FailedResult(kmErr!);
                    string mailbox = ((string?)a["mailbox"] ?? "").Trim();
                    if (mailbox.Length == 0) return FailedResult("Provide the 'mailbox' to watch (the signup email).");
                    string senderContains = ((string?)a["senderContains"] ?? "").Trim();
                    string purpose = NormalizeMailboxPurpose((string?)a["purpose"]);
                    int timeoutSeconds = Math.Clamp((int?)a["timeoutSeconds"] ?? 180, 5, 600);
                    int lookbackSeconds = Math.Clamp((int?)a["lookbackSeconds"] ?? 600, 0, 3600);
                    string mb = Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.NormalizeAddress(mailbox);
                    if (!mb.EndsWith("@" + Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase))
                        return new CommanderToolResult($"'{mb}' is not a KliveMail address.") { Succeeded = false };
                    // Include a bounded lookback so a context/tool rollover immediately after the
                    // site's Send-code click cannot make a fresh message invisible.
                    DateTime floor = DateTime.UtcNow.AddSeconds(-lookbackSeconds);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                    try
                    {
                        while (DateTime.UtcNow < deadline)
                        {
                            var msgs = await repo.ListMessagesAsync(mb, false, false, false, 1, 20, ct);
                            foreach (var summary in msgs)
                            {
                                if (summary.ReceivedUtc < floor) continue;
                                if (senderContains.Length > 0
                                    && !((summary.FromAddress ?? "").Contains(senderContains, StringComparison.OrdinalIgnoreCase)
                                         || (summary.Subject ?? "").Contains(senderContains, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                var full = await repo.GetMessageAsync(summary.Id, ct);
                                string haystack = (full?.Subject ?? "") + "\n" + (!string.IsNullOrWhiteSpace(full?.BodyText) ? full!.BodyText : (StripHtml(full?.BodyHtml) ?? ""));
                                string? code = ExtractVerificationCode(haystack);
                                if (code != null)
                                {
                                    RecordMailboxAvailable(mb, purpose);
                                    RecordMailboxDelivery(mb, true, "VerificationCodeReceived",
                                        $"A verification message from {summary.FromAddress} arrived at {mb}.");
                                    return new CommanderToolResult($"Verification code {code} (from {summary.FromAddress}, subject \"{Trunc(summary.Subject ?? "", 80)}\", message id={summary.Id}).")
                                    {
                                        AuditText = $"Verification code retrieved from {summary.FromAddress}, message id={summary.Id}; subject and code redacted from durable history.",
                                    };
                                }
                            }

                            // Catch the exact dotted/undotted or one-character mailbox typo that
                            // previously caused an agent to poll an empty address while the code sat
                            // in the same catch-all inbox. Never search arbitrary mailboxes: only a
                            // near-identical local part at the same @klive.dev domain qualifies.
                            var nearby = await repo.ListMessagesAsync(null, false, false, false, 1, 100, ct);
                            foreach (var summary in nearby.Where(x => x.ReceivedUtc >= floor &&
                                         !string.Equals(x.ToAddress, mb, StringComparison.OrdinalIgnoreCase) &&
                                         LikelyMailboxVariant(mb, x.ToAddress)))
                            {
                                if (senderContains.Length > 0
                                    && !((summary.FromAddress ?? "").Contains(senderContains, StringComparison.OrdinalIgnoreCase)
                                         || (summary.Subject ?? "").Contains(senderContains, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                var full = await repo.GetMessageAsync(summary.Id, ct);
                                string haystack = (full?.Subject ?? "") + "\n" + (!string.IsNullOrWhiteSpace(full?.BodyText)
                                    ? full!.BodyText : (StripHtml(full?.BodyHtml) ?? ""));
                                string? code = ExtractVerificationCode(haystack);
                                if (code != null)
                                {
                                    RecordMailboxDelivery(mb, false, "MailboxAddressMismatch",
                                        $"The requested mailbox was {mb}, but matching mail arrived at {summary.ToAddress}.");
                                    RecordMailboxAvailable(summary.ToAddress, purpose);
                                    RecordMailboxDelivery(summary.ToAddress, true, "VerificationCodeReceived",
                                        $"A verification message from {summary.FromAddress} arrived at this canonical address.");
                                    return new CommanderToolResult(
                                        $"Verification code {code}. ADDRESS MISMATCH: mail arrived at {summary.ToAddress}, not requested {mb}. " +
                                        $"Use {summary.ToAddress} as the canonical signup mailbox (message id={summary.Id}).")
                                    {
                                        AuditText = $"Verification code retrieved with ADDRESS MISMATCH: mail arrived at {summary.ToAddress}, not requested {mb}; message id={summary.Id}; code redacted from durable history.",
                                    };
                                }
                            }
                            await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        }
                        RecordMailboxDelivery(mb, false, "NoVerificationMessage",
                            $"KliveMail was queried successfully, but no matching code was observed within {timeoutSeconds}s. This observation does not identify whether the sender, Internet mail route, or receiver caused the absence.");
                        return new CommanderToolResult($"No verification code was observed at {mb} within {timeoutSeconds}s. KliveMail itself was queried successfully, but this result does not prove whether the site declined to send, Internet delivery failed, or the message is delayed. Keep this exact address canonical, inspect klivemail_list_messages, and arm an email stimulus hook for a later arrival.") { Succeeded = false };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        RecordKliveMailHealth(false, ex.GetType().Name, ex.Message);
                        return new CommanderToolResult($"klivemail_wait_for_code failed: {ex.Message}") { Succeeded = false };
                    }
                }

                // ── work tools: scripts / HTTP / files on the project volume ──

                case "http_request":
                {
                    string url = (string?)a["url"] ?? "";
                    string method = ((string?)a["method"] ?? "GET").ToUpperInvariant();
                    string? body = (string?)a["body"];
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                        return new CommanderToolResult("Provide an absolute http(s) 'url'.") { Succeeded = false };
                    try
                    {
                        using var msg = new HttpRequestMessage(new HttpMethod(method), uri);
                        if (body != null && method != "GET") msg.Content = new StringContent(body, Encoding.UTF8,
                            (string?)a["contentType"] ?? "application/json");
                        using var resp = await http.SendAsync(msg, ct);
                        string text = await resp.Content.ReadAsStringAsync(ct);
                        return new CommanderToolResult($"HTTP {(int)resp.StatusCode} {resp.StatusCode}\n{ProjectsContextBudget.TruncateToTokens(text, ProjectsContextBudget.ToolResultBudget)}")
                        { Succeeded = resp.IsSuccessStatusCode };
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { return new CommanderToolResult($"HTTP request failed: {ex.Message}") { Succeeded = false }; }
                }

                case "read_file":
                {
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    int startLine = Math.Max(1, (int?)a["startLine"] ?? 1);
                    int maxLines = Math.Clamp((int?)a["maxLines"] ?? 400, 1, 4000);
                    if (Files == null)
                    {
                        var (physical, error) = ResolveVolumePath(path);
                        if (error != null) return new CommanderToolResult(error);
                        if (!File.Exists(physical)) return new CommanderToolResult($"No file at {path}.");
                        string fallbackText = await File.ReadAllTextAsync(physical!, ct);
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(
                            SliceTextLines(fallbackText, startLine, maxLines), ProjectsContextBudget.ToolResultBudget));
                    }
                    try
                    {
                        string text = await Files.ReadTextAsync(project.ProjectID, path, 2 * 1024 * 1024, ct);
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(
                            SliceTextLines(text, startLine, maxLines), ProjectsContextBudget.ToolResultBudget));
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "write_file":
                {
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    string content = (string?)a["content"] ?? "";
                    if (Files == null)
                    {
                        var (physical, error) = ResolveVolumePath(path);
                        if (error != null) return new CommanderToolResult(error);
                        Directory.CreateDirectory(Path.GetDirectoryName(physical!)!);
                        await File.WriteAllTextAsync(physical!, content, ct);
                        return new CommanderToolResult($"Wrote {content.Length} chars to {path}.");
                    }
                    try
                    {
                        var actor = FileActor();
                        var entry = await Files.WriteTextAsync(project.ProjectID, path, content, actor, ct);
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Write,
                            [entry.Path], entry.Size, wakeID: wakeID);
                        return new CommanderToolResult($"Wrote {entry.Size} bytes to {entry.Path}; the shared-file index and provenance were updated.");
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "list_files":
                {
                    if (Files == null)
                    {
                        var (physical, error) = ResolveVolumePath((string?)a["path"] ?? ".");
                        if (error != null) return new CommanderToolResult(error);
                        if (!Directory.Exists(physical)) return new CommanderToolResult("Directory does not exist (the volume starts empty — write_file creates paths).");
                        var fallbackEntries = Directory.EnumerateFileSystemEntries(physical!).Take(200)
                            .Select(e => (Directory.Exists(e) ? "[dir] " : "") + Path.GetRelativePath(VolumeRoot(), e));
                        return new CommanderToolResult(string.Join("\n", fallbackEntries) is { Length: > 0 } fallback ? fallback : "(empty)");
                    }
                    try
                    {
                        int limit = Math.Clamp((int?)a["limit"] ?? 100, 1, 500);
                        int offset = DecodeFileCursor((string?)a["cursor"]);
                        string directory = ((string?)a["path"] ?? "").Trim();
                        if (directory == ".") directory = "";
                        var result = Files.List(project.ProjectID, new ProjectFileListRequest
                        {
                            Directory = directory,
                            Recursive = (bool?)a["recursive"] ?? false,
                            Search = (string?)a["query"],
                            Glob = (string?)a["glob"],
                            Offset = offset,
                            Limit = limit,
                        });
                        if (result.Entries.Count == 0)
                            return new CommanderToolResult(result.Total == 0 ? "(empty)" : $"No entries on this page (total {result.Total}).");
                        var lines = result.Entries.Select(e => FormatFileEntry(e)).ToList();
                        int next = result.Offset + result.Entries.Count;
                        lines.Add(next < result.Total
                            ? $"-- showing {result.Offset + 1}-{next} of {result.Total}; next cursor: {EncodeFileCursor(next)}"
                            : $"-- showing {result.Offset + 1}-{next} of {result.Total}; end of results");
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(
                            string.Join("\n", lines), ProjectsContextBudget.ToolResultBudget));
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "stat_file":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    try
                    {
                        var entry = Files.Stat(project.ProjectID, path);
                        return entry == null ? new CommanderToolResult($"No file or directory at '{path}'.")
                            : new CommanderToolResult(FormatFileEntry(entry, detailed: true));
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "resolve_project_path":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string supplied = ((string?)a["path"] ?? "").Trim();
                    if (supplied.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    try
                    {
                        string relative = ProjectWorkspaceLocator.NormalizeRelative(project.ProjectID, supplied);
                        string host = ProjectWorkspaceLocator.HostPath(project.ProjectID, relative);
                        string container = ProjectWorkspaceLocator.ContainerPath(relative);
                        var entry = relative.Length == 0 ? null : Files.Stat(project.ProjectID, relative);
                        var sb = new StringBuilder()
                            .AppendLine($"project_relative={relative}")
                            .AppendLine($"container_path={container}")
                            .AppendLine($"host_path={host}")
                            .AppendLine($"exists={entry != null || Directory.Exists(host)}");
                        if (entry != null) sb.AppendLine(FormatFileEntry(entry, detailed: true));
                        else if (Directory.Exists(host)) sb.AppendLine("kind=Directory; project workspace root");
                        else sb.AppendLine("kind=Missing; create it with write_file or make_directory");
                        return new CommanderToolResult(sb.ToString().TrimEnd());
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "make_directory":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    try
                    {
                        var existing = Files.Stat(project.ProjectID, path);
                        if (existing?.Kind == ProjectFileKind.Directory)
                            return new CommanderToolResult($"Directory already exists at {existing.Path}.");
                        var actor = FileActor();
                        var entry = Files.CreateDirectory(project.ProjectID, path, actor);
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.CreateDirectory,
                            [entry.Path], wakeID: wakeID);
                        return new CommanderToolResult($"Directory ready at {entry.Path}.");
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "move_file":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string source = ((string?)a["path"] ?? "").Trim();
                    string destination = ((string?)a["destination"] ?? "").Trim();
                    if (source.Length == 0 || destination.Length == 0) return new CommanderToolResult("Provide 'path' and 'destination'.");
                    try
                    {
                        var actor = FileActor();
                        var entry = Files.Move(project.ProjectID, source, destination, actor);
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Move,
                            [entry.Path], entry.Size, previousPath: source, wakeID: wakeID);
                        return new CommanderToolResult($"Moved {source} to {entry.Path}.");
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "copy_file":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string source = ((string?)a["path"] ?? "").Trim();
                    string destination = ((string?)a["destination"] ?? "").Trim();
                    if (source.Length == 0 || destination.Length == 0) return new CommanderToolResult("Provide 'path' and 'destination'.");
                    try
                    {
                        var actor = FileActor();
                        var entry = Files.Copy(project.ProjectID, source, destination, actor);
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Copy,
                            [entry.Path], entry.Size, previousPath: source, wakeID: wakeID);
                        return new CommanderToolResult($"Copied {source} to {entry.Path}.");
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "delete_file":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    try
                    {
                        var actor = FileActor();
                        string normalized = Files.NormalizeProjectPath(project.ProjectID, path);
                        bool deleted = Files.Delete(project.ProjectID, normalized, (bool?)a["recursive"] ?? false, actor);
                        if (!deleted) return new CommanderToolResult($"Nothing exists at {normalized}.");
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Delete,
                            [normalized], wakeID: wakeID);
                        return new CommanderToolResult($"Deleted {normalized}; its provenance remains in the shared-file audit.");
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                case "mark_file_important":
                {
                    if (Files == null) return new CommanderToolResult("Shared project files are unavailable.");
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    try
                    {
                        var actor = FileActor();
                        bool important = (bool?)a["important"] ?? true;
                        string? description = a.TryGetValue("description", out JToken? descriptionToken)
                            ? (descriptionToken.Type == JTokenType.Null ? "" : (string?)descriptionToken ?? "") : null;
                        var entry = Files.SetMetadata(project.ProjectID, path, important, description, actor);
                        ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Metadata,
                            [entry.Path], wakeID: wakeID);
                        return new CommanderToolResult($"Updated {entry.Path}: important={entry.Important}" +
                            (string.IsNullOrWhiteSpace(entry.Description) ? "." : $", description='{entry.Description}'."));
                    }
                    catch (Exception ex) { return FileToolError(ex); }
                }

                // Host script execution is UNGATED (Klives' explicit call: approvals are for plans,
                // money, and the escalation bar — not routine work). Every script is still fully
                // visible after the fact: the ToolCall event carries the complete args payload on
                // the timeline. The prompt's escalation bar owns judgment for consequential scripts.
                case "execute_csharp":
                case "run_script":
                {
                    string code = (string?)a["code"] ?? "";
                    if (string.IsNullOrWhiteSpace(code)) return new CommanderToolResult("Provide 'code' — a C# script body.");
                    return await RunScriptAsync(code, ct);
                }

                case "grep":
                {
                    string pattern = (string?)a["pattern"] ?? "";
                    if (string.IsNullOrWhiteSpace(pattern)) return new CommanderToolResult("Provide 'pattern'.");
                    string path = (string?)a["path"] ?? "";
                    int max = Math.Clamp((int?)a["maxResults"] ?? 30, 1, 200);
                    return await GrepProjectAsync(pattern, path, max,
                        fixedString: (bool?)a["fixedString"] == true,
                        caseSensitive: (bool?)a["caseSensitive"] == true, ct);
                }

                case "search_code":
                {
                    string query = (string?)a["query"] ?? "";
                    if (string.IsNullOrWhiteSpace(query))
                        return new CommanderToolResult("Provide 'query'.") { Succeeded = false };
                    string path = (string?)a["subfolder"] ?? "";
                    int max = Math.Clamp((int?)a["maxResults"] ?? 30, 1, 200);
                    string result = (bool?)a["fixedString"] == true
                        ? SharedGlobals(ct).SearchCode(query, path, max)
                        : SharedGlobals(ct).SearchCodeRegex(query, path, max);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(result, ProjectsContextBudget.ToolResultBudget));
                }

                case "read_code_file":
                {
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    int start = Math.Max(1, (int?)a["startLine"] ?? 1);
                    int lines = Math.Clamp((int?)a["maxLines"] ?? 200, 1, 1000);
                    return new CommanderToolResult(SharedGlobals(ct).ReadFile(path, start, lines));
                }

                case "list_code_directory":
                    return new CommanderToolResult(SharedGlobals(ct).ListDirectory((string?)a["path"] ?? ""));

                case "get_global_path":
                {
                    string key = ((string?)a["key"] ?? "").Trim();
                    if (key.Length == 0) return new CommanderToolResult("Provide 'key'.");
                    return new CommanderToolResult(SharedGlobals(ct).GetGlobalPath(key));
                }

                case "run_powershell":
                {
                    string ps = (string?)a["script"] ?? "";
                    if (string.IsNullOrWhiteSpace(ps)) return new CommanderToolResult("Provide 'script' — a PowerShell script body.");
                    int secs = (int?)a["timeoutSeconds"] ?? 120;
                    var (workingDirectory, workingError) = ResolveHostWorkingDirectory((string?)a["workingDirectory"]);
                    if (workingError != null) return new CommanderToolResult(workingError) { Succeeded = false };
                    var r = await HostShell.RunPowerShellAsync(ps, TimeSpan.FromSeconds(Math.Clamp(secs, 1, 900)), workingDir: workingDirectory, ct: ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(r.Format(), ProjectsContextBudget.ToolResultBudget)) { Succeeded = r.Success };
                }

                case "run_bash":
                {
                    string bash = (string?)a["script"] ?? "";
                    if (string.IsNullOrWhiteSpace(bash)) return new CommanderToolResult("Provide 'script' — a bash script body.");
                    int secs = (int?)a["timeoutSeconds"] ?? 120;
                    var (workingDirectory, workingError) = ResolveHostWorkingDirectory((string?)a["workingDirectory"]);
                    if (workingError != null) return new CommanderToolResult(workingError) { Succeeded = false };
                    var r = await HostShell.RunBashAsync(bash, TimeSpan.FromSeconds(Math.Clamp(secs, 1, 900)), workingDir: workingDirectory, ct: ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(r.Format(), ProjectsContextBudget.ToolResultBudget)) { Succeeded = r.Success };
                }

                // ── stimulus hook CRUD (§5.1 — Commander side) ──

                case "create_stimulus_hook":
                {
                    if (HookStore == null) return new CommanderToolResult("Hook store unavailable.") { Succeeded = false };
                    string sourceKind = ((string?)a["sourceKind"] ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(sourceKind))
                        return new CommanderToolResult("Provide 'sourceKind' (timer | webhook | file-watch | screen-diff | script | email | discord | process-exit).") { Succeeded = false };
                    if (sourceKind is not ("timer" or "webhook" or "file-watch" or "screen-diff"
                        or "script" or "email" or "discord" or "process-exit"))
                        return new CommanderToolResult($"Unsupported stimulus sourceKind '{sourceKind}'.") { Succeeded = false };
                    string destination = ((string?)a["destinationAgentID"] ?? actingAgentID).Trim();
                    if (destination.Length == 0) destination = actingAgentID;
                    if (!destination.Equals("commander", StringComparison.Ordinal)
                        && !subAgents.ListActive(project.ProjectID).Any(x => x.AgentID == destination))
                        return new CommanderToolResult($"Destination agent '{destination}' is not active in this project.") { Succeeded = false };
                    var hook = HookStore.Create(new StimulusHookRecord
                    {
                        ProjectID = project.ProjectID,
                        OwningAgentID = actingAgentID,
                        SourceKind = sourceKind,
                        SourceSpecJson = a["sourceSpec"]?.ToString(Formatting.None) ?? "{}",
                        RecognitionCriterion = (string?)a["criterion"] ?? "",
                        DestinationAgentID = destination,
                        Durability = sourceKind == "screen-diff" ? StimulusDurability.SupersedingByKey : StimulusDurability.Standard,
                    });
                    RearmAdapters?.Invoke();
                    var arm = GetHookArmInfo?.Invoke(hook.HookID);
                    string tokenNote = sourceKind == "webhook"
                        ? $" Ingress token (store it now): {hook.IngressToken}"
                        : "";
                    string armNote = arm == null ? " Arm state is not available."
                        : $" {arm.State}: {arm.Detail}";
                    return new CommanderToolResult($"Hook {hook.HookID} created ({sourceKind} → {hook.DestinationAgentID}).{tokenNote}{armNote}")
                    {
                        Succeeded = arm != null && arm.State != HookArmState.Error,
                        AuditText = $"Hook {hook.HookID} created ({sourceKind} → {hook.DestinationAgentID})." +
                            (sourceKind == "webhook" ? " Ingress token returned live but omitted from durable history." : "") + armNote,
                    };
                }

                case "list_stimulus_hooks":
                {
                    if (HookStore == null) return new CommanderToolResult("Hook store unavailable.") { Succeeded = false };
                    var hooks = HookStore.List(project.ProjectID);
                    return new CommanderToolResult(hooks.Count == 0 ? "No hooks." : string.Join("\n",
                        hooks.Select(h =>
                        {
                            var arm = h.Enabled ? GetHookArmInfo?.Invoke(h.HookID) : null;
                            string armText = h.Enabled
                                ? (arm == null ? "not armed" : $"{arm.State}: {arm.Detail}")
                                : "disabled";
                            return $"{h.HookID}: {h.SourceKind} → {h.DestinationAgentID} [{armText}] created {Data_Handling.TemporalFormat.StampWithAge(h.CreatedAt)} · criterion: {Trunc(h.RecognitionCriterion, 80)}";
                        })));
                }

                case "delete_stimulus_hook":
                {
                    if (HookStore == null) return new CommanderToolResult("Hook store unavailable.");
                    bool ok = HookStore.Delete(project.ProjectID, (string?)a["hookID"] ?? "");
                    RearmAdapters?.Invoke();
                    return new CommanderToolResult(ok ? "Hook deleted." : "No such hook.");
                }

                // ── strategy: councils + the Grand Plan ──

                case "convene_council":
                {
                    if (ConveneCouncilAsync == null) return FailedResult("Councils are unavailable.");
                    string topic = ((string?)a["topic"] ?? "").Trim();
                    string briefing = ((string?)a["briefing"] ?? "").Trim();
                    if (topic.Length == 0) return FailedResult("Provide 'topic' — the decision the council must weigh.");
                    if (briefing.Length == 0)
                        return FailedResult("Provide 'briefing' — the panel sees ONLY what you put here, so include everything they need to reason well.");
                    string[]? roles = (a["roles"] as JArray)?.Select(t => (string?)t ?? "").Where(s => s.Length > 0).ToArray();
                    string urgency = (string?)a["urgency"] ?? "routine";
                    string purpose = (string?)a["purpose"] ?? "decision";
                    string verdict = await ConveneCouncilAsync(topic, briefing, roles, urgency, purpose, ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(verdict, 2000))
                    { Succeeded = verdict.StartsWith("COUNCIL VERDICT", StringComparison.Ordinal) };
                }

                case "submit_grand_plan":
                {
                    if (GrandPlans == null) return FailedResult("Grand Plan store unavailable.");
                    var content = ParsePlanContent(a);
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    if (content.Mission.Length == 0) return FailedResult("Provide 'mission' — the plan needs a mission statement.");
                    if (content.Milestones.Count == 0) return FailedResult("Provide at least one concrete milestone; execution is milestone-driven.");
                    if (content.SuccessCriteria.Count == 0) return FailedResult("Provide at least one objectively checkable success criterion.");
                    if (summary.Length == 0) return FailedResult("Provide 'summary' — a ≤150-word summary for the approval card and wake seeds.");
                    string? externalControlsError = ValidateExternalOperationPlan(content);
                    if (externalControlsError != null) return FailedResult(externalControlsError);
                    return await SubmitPlanForApprovalAsync(content, summary, changeNote: null, isAmendment: false, ct);
                }

                case "amend_grand_plan":
                {
                    if (GrandPlans == null) return FailedResult("Grand Plan store unavailable.");
                    if (!GrandPlans.HasApprovedPlan(project.ProjectID))
                        return FailedResult("No approved Grand Plan to amend yet. Use submit_grand_plan first.");
                    var content = ParsePlanContent(a);
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    string changeNote = ((string?)a["changeNote"] ?? "").Trim();
                    if (content.Mission.Length == 0) return FailedResult("Provide 'mission' — the revised plan needs a mission statement.");
                    if (content.Milestones.Count == 0) return FailedResult("The revised plan must contain at least one milestone.");
                    if (content.SuccessCriteria.Count == 0) return FailedResult("The revised plan must contain at least one success criterion.");
                    if (summary.Length == 0) return FailedResult("Provide 'summary' — a ≤150-word summary of the revised plan.");
                    string? externalControlsError = ValidateExternalOperationPlan(content);
                    if (externalControlsError != null) return FailedResult(externalControlsError);
                    bool material = ParseBool((string?)a["material"], defaultValue: false);
                    if (!material)
                    {
                        var v = GrandPlans.SubmitVersion(project.ProjectID, content, summary, changeNote, material: false, wakeID);
                        var evt = Evt(ProjectEventTypes.GrandPlanAmended, "commander",
                            $"Grand Plan amended (v{v.Version}, non-material): {Trunc(changeNote.Length > 0 ? changeNote : summary, 200)}");
                        evt.PayloadJson = JsonConvert.SerializeObject(new { version = v.Version, material = false, summary });
                        eventLog.Append(evt);
                        return new CommanderToolResult($"Grand Plan amended to v{v.Version} (non-material, applied immediately). It seeds your next wake.");
                    }
                    return await SubmitPlanForApprovalAsync(content, summary, changeNote, isAmendment: true, ct);
                }

                case "update_plan_progress":
                {
                    if (GrandPlans == null) return new CommanderToolResult("Grand Plan store unavailable.");
                    if (!GrandPlans.HasApprovedPlan(project.ProjectID))
                        return new CommanderToolResult("No approved Grand Plan yet — nothing to progress. Submit one and get it approved first (submit_grand_plan).");
                    var results = new List<string>();
                    string evidenceText = ((string?)a["evidence"] ?? "").Trim();
                    long? evidenceSequence = (long?)a["evidenceEventSequence"];
                    var evidenceArtifacts = (a["evidenceArtifactIDs"] as JArray)?.Values<string>()
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() ?? new();
                    PlanEvidence? progressEvidence = evidenceText.Length == 0 ? null : new PlanEvidence
                    {
                        Summary = evidenceText,
                        EventSequence = evidenceSequence,
                        ArtifactIDs = evidenceArtifacts,
                        RecordedBy = actingAgentID,
                    };
                    string mRef = ((string?)a["milestoneId"] ?? "").Trim();
                    string cRef = ((string?)a["criterionId"] ?? "").Trim();
                    string pRef = ((string?)a["preconditionId"] ?? "").Trim();
                    string rRef = ((string?)a["riskId"] ?? "").Trim();
                    int targetCount = new[] { mRef, cRef, pRef, rRef }.Count(x => x.Length > 0);
                    if (targetCount != 1)
                        return FailedResult(targetCount == 0
                            ? "Provide exactly one plan target: milestoneId, criterionId, preconditionId, or riskId."
                            : "Update one plan target per call so the transition is atomic and independently auditable.");
                    var requestedMilestoneStatus = ParseMilestoneStatus((string?)a["milestoneStatus"]);
                    bool requestedCriterionMet = ParseBool((string?)a["criterionMet"], defaultValue: true);
                    string requestedBlockReason = ((string?)a["blockReason"] ?? "").Trim();
                    bool terminalTransition = mRef.Length > 0 && requestedMilestoneStatus == MilestoneStatus.Done
                         || cRef.Length > 0 && requestedCriterionMet || pRef.Length > 0 || rRef.Length > 0;
                    if (terminalTransition && progressEvidence == null)
                        return FailedResult("Marking a milestone done, success criterion met, precondition verified/failed, or risk resolved requires 'evidence'.");
                    if (terminalTransition)
                    {
                        string? evidenceError = ValidatePlanEvidenceReferences(evidenceSequence, evidenceArtifacts);
                        if (evidenceError != null) return FailedResult(evidenceError);
                    }
                    if (mRef.Length > 0)
                    {
                        var m = GrandPlans.UpdateMilestoneStatus(project.ProjectID, mRef, requestedMilestoneStatus,
                            progressEvidence, requestedBlockReason, (string?)a["ownerAgentID"]);
                        results.Add(m == null ? $"No milestone matched '{mRef}'." : $"Milestone '{Trunc(m.Title, 60)}' → {m.Status}.");
                    }
                    if (cRef.Length > 0)
                    {
                        var cc = GrandPlans.SetCriterionMet(project.ProjectID, cRef, requestedCriterionMet, progressEvidence);
                        results.Add(cc == null ? $"No success criterion matched '{cRef}'." : $"Criterion '{Trunc(cc.Text, 60)}' → {(cc.Met ? "met" : "unmet")}.");
                    }
                    if (pRef.Length > 0)
                    {
                        var status = ParsePreconditionStatus((string?)a["preconditionStatus"]);
                        if (status == PlanPreconditionStatus.Unverified)
                            return FailedResult("preconditionStatus must be 'verified' or 'failed', backed by evidence.");
                        var pc = GrandPlans.SetPreconditionStatus(project.ProjectID, pRef, status, progressEvidence!);
                        results.Add(pc == null ? $"No precondition matched '{pRef}'." : $"Precondition '{Trunc(pc.Description, 60)}' → {pc.Status}.");
                    }
                    if (rRef.Length > 0)
                    {
                        var status = ParseRiskStatus((string?)a["riskStatus"]);
                        if (status != PlanRiskStatus.Mitigated)
                            return FailedResult("riskStatus must be 'mitigated' with evidence. Risk acceptance or reopening requires a material amend_grand_plan approval.");
                        var risk = GrandPlans.SetRiskStatus(project.ProjectID, rRef, status, progressEvidence!);
                        results.Add(risk == null ? $"No risk matched '{rRef}'." : $"Risk '{Trunc(risk.Description, 60)}' → {risk.Status}.");
                    }
                    string note = ((string?)a["note"] ?? "").Trim();
                    string msg = string.Join(" ", results);
                    var progressEvent = Evt(ProjectEventTypes.GrandPlanProgress, "commander", note.Length > 0 ? $"{msg} {note}" : msg);
                    progressEvent.PayloadJson = JsonConvert.SerializeObject(new { evidence = evidenceText, evidenceSequence, evidenceArtifacts });
                    eventLog.Append(progressEvent);
                    if (RuntimeState != null)
                    {
                        var approved = GrandPlans.GetCurrentApproved(project.ProjectID);
                        var active = approved?.Content?.Milestones
                            .Where(m => m.Status is MilestoneStatus.InProgress or MilestoneStatus.Blocked)
                            .Select(m => m.ID).ToList() ?? new();
                        if (active.Count == 0)
                        {
                            string? next = approved?.Content?.Milestones.OrderBy(m => m.Order)
                                .FirstOrDefault(m => m.Status == MilestoneStatus.Pending)?.ID;
                            if (next != null) active.Add(next);
                        }
                        RuntimeState.SetActiveMilestones(project.ProjectID, approved?.Version, active);
                    }
                    return new CommanderToolResult(msg);
                }

                case "get_grand_plan":
                {
                    if (GrandPlans == null) return new CommanderToolResult("Grand Plan store unavailable.");
                    var v = GrandPlans.GetCurrentApproved(project.ProjectID);
                    if (v == null) return new CommanderToolResult("No approved Grand Plan yet.");
                    return new CommanderToolResult(
                        $"GRAND PLAN v{v.Version} (approved {Data_Handling.TemporalFormat.StampWithAge(v.ResolvedAt ?? v.SubmittedAt)}):\n\n"
                        + ProjectsContextBudget.TruncateToTokens(DescribeGrandPlanForModel(v), 2500));
                }

                case "complete_project":
                {
                    if (IsIndefiniteOngoingOperation())
                        return FailedResult(
                            "ONGOING_OPERATION_REMAINS_ACTIVE: this goal is to run/manage/grow a continuing external operation without a stated end condition. " +
                            "Account setup or initial publications cannot complete it; keep the queue, recurring timers and measurement/review loop operating until Klives explicitly pauses or archives the project.");
                    if (GrandPlans != null)
                    {
                        var issues = GrandPlans.GetCompletionReadinessIssues(project.ProjectID);
                        if (issues.Count > 0)
                            return FailedResult("Project is not ready for completion:\n- " +
                                string.Join("\n- ", issues.Take(20)) +
                                "\nUpdate milestones/criteria with evidence before requesting completion.");
                    }
                    // Completing is consequential and irreversible-ish: gate it.
                    var gate = new ProjectGate
                    {
                        ProjectID = project.ProjectID,
                        WakeID = wakeID,
                        AgentID = actingAgentID,
                        Kind = "action",
                        Title = "Complete the project?",
                        Description = (string?)a["summary"] ?? "The Commander believes the goal is achieved.",
                        Rationale = "Completion archives the Discord channel and releases the desktops.",
                    };
                    var res = await gates.OpenGateAndWaitAsync(gate, ct);
                    if (res.Decision != GateDecision.Approve)
                        return new CommanderToolResult($"Klives {res.Decision}: {res.Comment} — project stays active.");
                    if (CompleteProjectAsync != null) await CompleteProjectAsync();
                    return new CommanderToolResult("Project completed. This is the final wake.") { EndWake = true };
                }

                default:
                    return new CommanderToolResult($"Unknown tool '{tool}'.");
            }
        }

        /// <summary>
        /// Stores a material Grand Plan version and opens a "plan" approval gate, blocking until Klives
        /// resolves it (exactly like request_user_approval). On approval the version is marked approved
        /// and — if the project is still Planning — activated. On denial the version is rejected and the
        /// Commander is told to revise and resubmit within this same wake.
        /// </summary>
        private async Task<CommanderToolResult> SubmitPlanForApprovalAsync(GrandPlanContent content, string summary, string? changeNote, bool isAmendment, CancellationToken ct)
        {
            var version = GrandPlans!.SubmitVersion(project.ProjectID, content, summary, changeNote, material: true, wakeID);
            var submittedEvt = Evt(ProjectEventTypes.GrandPlanSubmitted, "commander",
                $"Grand Plan v{version.Version} submitted for approval: {Trunc(summary, 200)}");
            submittedEvt.PayloadJson = JsonConvert.SerializeObject(new { version = version.Version, isAmendment, summary });
            eventLog.Append(submittedEvt);

            var gate = new ProjectGate
            {
                ProjectID = project.ProjectID,
                WakeID = wakeID,
                AgentID = actingAgentID,
                Kind = "plan",
                Title = isAmendment
                    ? $"Grand Plan v{version.Version} amendment — approve to apply"
                    : $"Grand Plan v{version.Version} — approve to begin work",
                Description = summary,
                Rationale = changeNote ?? "Klives approves the strategic plan before the fleet executes it.",
                ProposalJson = JsonConvert.SerializeObject(new { version = version.Version, markdown = version.Markdown, content = version.Content }),
            };
            var res = await gates.OpenGateAndWaitAsync(gate, ct);

            if (res.Decision == GateDecision.Approve)
            {
                GrandPlans.MarkApproved(project.ProjectID, version.Version, gate.GateID, res.Comment);
                if (RuntimeState != null)
                {
                    var approved = GrandPlans.GetCurrentApproved(project.ProjectID);
                    var active = approved?.Content?.Milestones
                        .Where(m => m.Status is MilestoneStatus.InProgress or MilestoneStatus.Blocked)
                        .Select(m => m.ID).ToList() ?? new();
                    if (active.Count == 0)
                    {
                        string? first = approved?.Content?.Milestones.OrderBy(m => m.Order)
                            .FirstOrDefault(m => m.Status == MilestoneStatus.Pending)?.ID;
                        if (first != null) active.Add(first);
                    }
                    RuntimeState.SetActiveMilestones(project.ProjectID, version.Version, active);
                }
                var approvedEvt = Evt(ProjectEventTypes.GrandPlanApproved, "klives",
                    $"Grand Plan v{version.Version} approved by Klives.{(string.IsNullOrWhiteSpace(res.Comment) ? "" : $" \"{Trunc(res.Comment, 160)}\"")}");
                approvedEvt.PayloadJson = JsonConvert.SerializeObject(new { version = version.Version });
                eventLog.Append(approvedEvt);

                if (project.Status == ProjectStatus.Planning && ActivateProjectAsync != null)
                {
                    await ActivateProjectAsync();
                    return new CommanderToolResult(
                        $"Grand Plan v{version.Version} approved — the project is now ACTIVE. Execute it: create the hooks you need and take the first concrete steps.");
                }
                return new CommanderToolResult($"Grand Plan v{version.Version} approved and in effect.");
            }

            GrandPlans.MarkRejected(project.ProjectID, version.Version, gate.GateID, res.Comment);
            eventLog.Append(Evt(ProjectEventTypes.GrandPlanRevisionRequested, "klives",
                $"Klives {res.Decision} Grand Plan v{version.Version}: {res.Comment}"));
            return new CommanderToolResult(
                $"Klives {res.Decision}: {res.Comment} — revise the plan accordingly and resubmit ({(isAmendment ? "amend_grand_plan" : "submit_grand_plan")}).");
        }

        /// <summary>
        /// Account/channel operations have external go/no-go facts and policy exposure that a
        /// generic checklist can otherwise omit. Continuing operations additionally need durable
        /// work state, wall-clock cadence and a measured feedback loop; without those, a one-off
        /// signup or first post can be mistaken for the goal.
        /// </summary>
        private string? ValidateExternalOperationPlan(GrandPlanContent content)
        {
            string objective = project.Goal + "\n" + content.Mission;
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            bool accountSubject = Regex.IsMatch(objective,
                @"\b(account|social[ -]?media|channel|mailbox|storefront|seller[ -]?profile|tiktok|instagram|youtube|facebook|linkedin|twitter|threads|pinterest|reddit|shopify|etsy)\b", options);
            bool operatingAction = Regex.IsMatch(objective,
                @"\b(create|register|sign[ -]?up|open|run|manage|operate|grow|post|publish|upload|schedule|decorate|market|monetize)\w*\b", options);
            if (!accountSubject || !operatingAction) return null;

            bool hasLiveCapabilityPrecondition = content.Preconditions.Any(p => Regex.IsMatch(
                p.Description + " " + p.Verification,
                @"\b(account|registration|sign[ -]?up|log[ -]?in|email|mail|deliver\w*|verification|phone|eligib\w*|access|credential|region|age|permission|platform)\b",
                options));
            if (!hasLiveCapabilityPrecondition)
                return "EXTERNAL_OPERATION_PLAN_INCOMPLETE: add an unverified go/no-go precondition with an exact live test for account access, registration eligibility, verification delivery, or another real external dependency.";

            bool hasPolicyRisk = content.Risks.Any(r => !string.IsNullOrWhiteSpace(r.Mitigation)
                && Regex.IsMatch(r.Description + " " + r.Mitigation,
                    @"\b(terms|policy|right|copyright|licen[cs]e|consent|privacy|legal|eligib\w*|automation|spam|content|reputation)\b", options));
            if (!hasPolicyRisk)
                return "EXTERNAL_OPERATION_PLAN_INCOMPLETE: add a documented risk, with a concrete mitigation, covering the live platform terms/policy, account eligibility, content rights, privacy, or comparable external constraint.";

            bool ongoing = Regex.IsMatch(objective,
                @"\b(run|manage|operate|grow|schedule|ongoing|continuous|regular\w*|daily|weekly|posting|campaign)\b", options);
            if (!ongoing) return null;

            string operatingPlan = string.Join(" ", content.Milestones.SelectMany(m => new[] { m.Title, m.Detail, m.Target ?? "" })
                .Concat(content.SuccessCriteria.Select(c => c.Text)));
            var missing = new List<string>();
            if (!Regex.IsMatch(operatingPlan, @"\b(queue|ledger|backlog|calendar)\b", options))
                missing.Add("a durable work queue/ledger");
            if (!Regex.IsMatch(operatingPlan, @"\b(schedule|timer|recurring|cadence)\b", options))
                missing.Add("a recurring wall-clock schedule/timer");
            if (!Regex.IsMatch(operatingPlan, @"\b(analytics|metric|performance|review|experiment|growth|reach|conversion)\w*\b", options))
                missing.Add("a measured analytics/review feedback loop");
            return missing.Count == 0 ? null
                : "ONGOING_OPERATION_PLAN_INCOMPLETE: explicitly include " + string.Join(", ", missing) +
                  " in milestones or success criteria; initial account setup or a first publication is not completion.";
        }

        private bool IsIndefiniteOngoingOperation()
        {
            string objective = project.Goal + "\n" +
                (GrandPlans?.GetCurrentApproved(project.ProjectID)?.Content?.Mission ?? "");
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            bool accountSubject = Regex.IsMatch(objective,
                @"\b(account|social[ -]?media|channel|storefront|seller[ -]?profile|tiktok|instagram|youtube|facebook|linkedin|twitter|threads|pinterest|reddit|shopify|etsy)\b", options);
            bool continuingAction = Regex.IsMatch(objective,
                @"\b(run|manage|operate|grow|schedule|ongoing|continuous|regular\w*|daily|weekly|posting|campaign)\b", options);
            bool bounded = Regex.IsMatch(objective,
                @"\b(for\s+\d+\s+(?:days?|weeks?|months?)|until\s+\d{4}-\d{2}-\d{2}|through\s+\d{4}-\d{2}-\d{2}|by\s+\d{4}-\d{2}-\d{2}|first\s+\d+\s+(?:posts?|uploads?)|(?:publish|post|upload)\s+\d+\b|campaign\s+(?:ends?|ending))", options);
            return accountSubject && continuingAction && !bounded;
        }

        private static bool ParseBool(string? v, bool defaultValue) =>
            string.IsNullOrWhiteSpace(v) ? defaultValue : v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

        private static string? TryReadPayloadString(string? payloadJson, string property)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return null;
            try { return (string?)JObject.Parse(payloadJson)[property]; }
            catch { return null; }
        }

        private static DateTime? ParseUtc(string? value) =>
            DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime() : null;

        private string? ValidatePlanEvidenceReferences(long? eventSequence, IReadOnlyCollection<string> artifactIDs)
        {
            if (eventSequence is <= 0)
                return "evidenceEventSequence must be a positive sequence from this project's durable event log.";
            if (eventSequence.HasValue)
            {
                var referenced = eventLog.ReadSince(project.ProjectID, eventSequence.Value - 1, 1)
                    .FirstOrDefault(e => e.Sequence == eventSequence.Value);
                if (referenced == null)
                    return $"Evidence event #{eventSequence.Value} does not exist in this project; inspect the timeline and cite a real sequence.";
                var evidenceTypes = new HashSet<string>(StringComparer.Ordinal)
                {
                    ProjectEventTypes.ToolResult, ProjectEventTypes.ArtifactAdded,
                    ProjectEventTypes.ProjectFileChanged, ProjectEventTypes.AccountChanged,
                    ProjectEventTypes.MoneySpent, ProjectEventTypes.Stimulus,
                    ProjectEventTypes.ApprovalResolved, ProjectEventTypes.KlivesMessage,
                };
                if (!evidenceTypes.Contains(referenced.Type))
                    return $"Event #{eventSequence.Value} is narrative/control state ({referenced.Type}), not outcome evidence. Cite a successful tool result, artifact/file/account event, external stimulus, approval, or Klives message.";
                if (referenced.Type == ProjectEventTypes.ToolResult)
                {
                    bool? succeeded = null;
                    try { succeeded = (bool?)JObject.Parse(referenced.PayloadJson ?? "{}")["succeeded"]; } catch { }
                    if (succeeded == false
                        || !ProjectWorkProgress.IsProductiveResult(referenced.Text, referenced.ArtifactIDs))
                        return $"Tool-result event #{eventSequence.Value} records a failed/interrupted/non-productive call and cannot prove terminal plan progress.";
                }
            }
            if (artifactIDs.Count > 0)
            {
                if (Artifacts == null) return "Artifact evidence cannot be verified because the artifact store is unavailable.";
                string? missing = artifactIDs.FirstOrDefault(id => Artifacts.GetRecord(project.ProjectID, id) == null);
                if (missing != null) return $"Evidence artifact '{Trunc(missing, 80)}' does not exist in this project.";
            }
            if (!eventSequence.HasValue && artifactIDs.Count == 0)
                return "Terminal plan progress requires a real evidenceEventSequence and/or evidenceArtifactIDs, not prose alone.";
            return null;
        }

        private static CommanderToolResult FailedResult(string text) => new(text) { Succeeded = false };

        private static List<ProjectEvidenceReference> BuildCheckpointEvidence(JObject a)
        {
            string reference = ((string?)a["evidenceReference"] ?? "").Trim();
            long? sequence = (long?)a["evidenceEventSequence"];
            if (reference.Length == 0 && !sequence.HasValue) return new();
            string rawKind = ((string?)a["evidenceKind"] ?? "other").Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
            var kind = rawKind switch
            {
                "event" => ProjectEvidenceKind.Event,
                "artifact" => ProjectEvidenceKind.Artifact,
                "projectfile" => ProjectEvidenceKind.ProjectFile,
                "toolresult" => ProjectEvidenceKind.ToolResult,
                "externalobservation" => ProjectEvidenceKind.ExternalObservation,
                "userconfirmation" => ProjectEvidenceKind.UserConfirmation,
                _ => ProjectEvidenceKind.Other,
            };
            return new List<ProjectEvidenceReference>
            {
                new()
                {
                    Kind = kind,
                    Reference = reference.Length > 0 ? reference : $"event-sequence:{sequence}",
                    EventSequence = sequence,
                }
            };
        }

        private static ProjectBlockerCategory ParseBlockerCategory(string? value) =>
            (value ?? "").Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
            {
                "approval" => ProjectBlockerCategory.Approval,
                "budget" => ProjectBlockerCategory.Budget,
                "externaldependency" => ProjectBlockerCategory.ExternalDependency,
                "capacity" => ProjectBlockerCategory.Capacity,
                "configuration" => ProjectBlockerCategory.Configuration,
                "manualintervention" => ProjectBlockerCategory.ManualIntervention,
                "invariantviolation" => ProjectBlockerCategory.InvariantViolation,
                _ => ProjectBlockerCategory.Unknown,
            };

        private static MilestoneStatus ParseMilestoneStatus(string? s) =>
            (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
            {
                "in_progress" or "inprogress" or "started" or "wip" => MilestoneStatus.InProgress,
                "done" or "complete" or "completed" or "finished" => MilestoneStatus.Done,
                // Legacy model output is treated as an in-progress handoff with an optional
                // obstacle note; a project agent cannot turn it into an execution stop.
                "blocked" or "stuck" => MilestoneStatus.InProgress,
                _ => MilestoneStatus.Pending,
            };

        private static RiskSeverity ParseSeverity(string? s) =>
            (s ?? "").Trim().ToLowerInvariant() switch
            {
                "high" or "critical" or "severe" => RiskSeverity.High,
                "low" or "minor" => RiskSeverity.Low,
                _ => RiskSeverity.Medium,
            };

        private static PlanPreconditionStatus ParsePreconditionStatus(string? s) =>
            (s ?? "").Trim().ToLowerInvariant() switch
            {
                "verified" or "passed" or "proven" => PlanPreconditionStatus.Verified,
                "failed" or "invalid" or "disproven" => PlanPreconditionStatus.Failed,
                _ => PlanPreconditionStatus.Unverified,
            };

        private static PlanRiskStatus ParseRiskStatus(string? s) =>
            (s ?? "").Trim().ToLowerInvariant() switch
            {
                "mitigated" or "resolved" => PlanRiskStatus.Mitigated,
                "accepted" or "acknowledged" => PlanRiskStatus.Accepted,
                _ => PlanRiskStatus.Open,
            };

        /// <summary>Composes a compact tactical-plan text from focus + next steps (kept in the digest for legacy display/seeds).</summary>
        private static string ComposePlanText(string focus, List<string> steps)
        {
            var sb = new StringBuilder();
            if (focus.Length > 0) sb.Append("Focus: ").Append(focus);
            if (steps.Count > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Next: ").Append(string.Join("; ", steps));
            }
            return sb.ToString();
        }

        /// <summary>Parses the structured Grand Plan tool arguments (submit/amend) into a <see cref="GrandPlanContent"/>.</summary>
        private static GrandPlanContent ParsePlanContent(JObject a)
        {
            var c = new GrandPlanContent
            {
                Mission = ((string?)a["mission"] ?? "").Trim(),
                BudgetPlan = ((string?)a["budgetPlan"] ?? "").Trim(),
            };
            foreach (var w in (a["workstreams"] as JArray) ?? new JArray())
            {
                string name = ((string?)w?["name"] ?? "").Trim();
                if (name.Length == 0) continue;
                c.Workstreams.Add(new PlanWorkstream { Name = name, Description = ((string?)w?["description"] ?? "").Trim() });
            }
            foreach (var m in (a["milestones"] as JArray) ?? new JArray())
            {
                string title = ((string?)m?["title"] ?? "").Trim();
                if (title.Length == 0) continue;
                string target = ((string?)m?["target"] ?? "").Trim();
                c.Milestones.Add(new PlanMilestone
                {
                    Title = title,
                    Detail = ((string?)m?["detail"] ?? "").Trim(),
                    Target = target.Length == 0 ? null : target,
                    Status = ParseMilestoneStatus((string?)m?["status"]),
                    BlockReason = string.IsNullOrWhiteSpace((string?)m?["blockReason"])
                        ? null : ((string?)m?["blockReason"])!.Trim(),
                    DependsOn = (m?["dependsOn"] as JArray)?.Values<string>()
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList() ?? new(),
                    OwnerAgentID = string.IsNullOrWhiteSpace((string?)m?["ownerAgentID"]) ? null : ((string?)m?["ownerAgentID"])!.Trim(),
                });
            }
            foreach (var r in (a["risks"] as JArray) ?? new JArray())
            {
                string desc = ((string?)r?["description"] ?? "").Trim();
                if (desc.Length == 0) continue;
                c.Risks.Add(new PlanRisk
                {
                    Description = desc,
                    Severity = ParseSeverity((string?)r?["severity"]),
                    Mitigation = ((string?)r?["mitigation"] ?? "").Trim(),
                    // Retain risk visibility, but never permit model-authored risk metadata to
                    // become an execution gate.
                    BlocksExecution = false,
                    Status = ParseRiskStatus((string?)r?["status"]),
                });
            }
            foreach (var p in (a["preconditions"] as JArray) ?? new JArray())
            {
                string description = ((string?)p?["description"] ?? "").Trim();
                string verification = ((string?)p?["verification"] ?? "").Trim();
                if (description.Length == 0 || verification.Length == 0) continue;
                c.Preconditions.Add(new PlanPrecondition
                {
                    Description = description,
                    Verification = verification,
                    Status = ParsePreconditionStatus((string?)p?["status"]),
                });
            }
            foreach (var x in (a["successCriteria"] as JArray) ?? new JArray())
            {
                // Accept an object {text, met} or a bare string.
                string text; bool met = false;
                if (x is JObject xo) { text = ((string?)xo["text"] ?? "").Trim(); met = ParseBool((string?)xo["met"], defaultValue: false); }
                else { text = ((string?)x ?? "").Trim(); }
                if (text.Length == 0) continue;
                c.SuccessCriteria.Add(new PlanCriterion { Text = text, Met = met });
            }
            return c;
        }

        /// <summary>Renders an approved plan for the model with stable ids and live status inline (for get_grand_plan).</summary>
        private static string DescribeGrandPlanForModel(GrandPlanVersion v)
        {
            var c = v.Content;
            if (c == null) return v.Markdown; // legacy version
            var sb = new StringBuilder();
            sb.Append("MISSION: ").AppendLine(c.Mission).AppendLine();
            if (c.Workstreams.Count > 0)
            {
                sb.AppendLine("WORKSTREAMS:");
                foreach (var w in c.Workstreams) sb.AppendLine($"- {w.Name}: {w.Description}");
                sb.AppendLine();
            }
            if (c.Milestones.Count > 0)
            {
                sb.AppendLine("MILESTONES (id · status · title):");
                foreach (var m in c.Milestones)
                    sb.AppendLine($"- {m.ID} · {m.Status} · {m.Title}"
                        + (string.IsNullOrWhiteSpace(m.Detail) ? "" : $" — {m.Detail}")
                        + (string.IsNullOrWhiteSpace(m.Target) ? "" : $" (target: {m.Target})"));
                sb.AppendLine();
            }
            if (c.Preconditions.Count > 0)
            {
                sb.AppendLine("PRECONDITIONS (id · status · assumption · verification):");
                foreach (var p in c.Preconditions)
                    sb.AppendLine($"- {p.ID} · {p.Status} · {p.Description}" +
                        (string.IsNullOrWhiteSpace(p.Verification) ? "" : $" — verify: {p.Verification}") +
                        (p.Evidence.Count > 0 ? $" [evidence: {p.Evidence.Count}]" : ""));
                sb.AppendLine();
            }
            if (c.Risks.Count > 0)
            {
                sb.AppendLine("RISKS:");
                foreach (var r in c.Risks) sb.AppendLine($"- [{r.Severity}] {r.Description}" + (string.IsNullOrWhiteSpace(r.Mitigation) ? "" : $" → {r.Mitigation}"));
                sb.AppendLine();
            }
            if (c.SuccessCriteria.Count > 0)
            {
                sb.AppendLine("SUCCESS CRITERIA (id · met · text):");
                foreach (var x in c.SuccessCriteria) sb.AppendLine($"- {x.ID} · {(x.Met ? "MET" : "unmet")} · {x.Text}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(c.BudgetPlan)) sb.Append("BUDGET PLAN: ").AppendLine(c.BudgetPlan);
            return sb.ToString().TrimEnd();
        }

        private ProjectFileActor FileActor()
        {
            if (string.Equals(actingAgentID, "commander", StringComparison.OrdinalIgnoreCase))
                return new ProjectFileActor(ProjectFileActorType.Commander, "commander", "Commander");
            var record = subAgents.ListActive(project.ProjectID)
                .FirstOrDefault(x => string.Equals(x.AgentID, actingAgentID, StringComparison.OrdinalIgnoreCase));
            string display = record == null || string.IsNullOrWhiteSpace(record.Role)
                ? $"Agent {actingAgentID}" : $"{record.Role} ({actingAgentID})";
            return new ProjectFileActor(ProjectFileActorType.Agent, actingAgentID, display);
        }

        private static CommanderToolResult FileToolError(Exception ex)
        {
            if (ex is OperationCanceledException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            return ex switch
            {
                FileNotFoundException => new CommanderToolResult("Shared file not found: " + ex.Message) { Succeeded = false },
                ProjectFileConflictException => new CommanderToolResult("Shared-file conflict: " + ex.Message) { Succeeded = false },
                ProjectFileException or UnauthorizedAccessException or IOException =>
                    new CommanderToolResult("Shared-file operation failed: " + ex.Message) { Succeeded = false },
                _ => new CommanderToolResult($"Shared-file operation failed ({ex.GetType().Name}): {ex.Message}") { Succeeded = false },
            };
        }

        private static string FormatFileEntry(ProjectFileEntry entry, bool detailed = false)
        {
            string kind = entry.Kind == ProjectFileKind.Directory ? "[dir]" : ProjectFileTimeline.FormatBytes(entry.Size);
            string important = entry.Important ? " ★" : "";
            string createdBy = entry.CreatedBy.Type == ProjectFileActorType.Unknown
                ? "Unknown" : entry.CreatedBy.DisplayName;
            string summary = $"{kind} {entry.Path}{important} | added {entry.CreatedUtc:yyyy-MM-dd HH:mm}Z by {createdBy}";
            string modifiedBy = entry.ModifiedBy.Type == ProjectFileActorType.Unknown
                ? "Unknown" : entry.ModifiedBy.DisplayName;
            if (entry.ModifiedUtc - entry.CreatedUtc > TimeSpan.FromSeconds(1) || entry.ModifiedBy != entry.CreatedBy)
                summary += $" | changed {entry.ModifiedUtc:yyyy-MM-dd HH:mm}Z by {modifiedBy}";
            if (!detailed)
                return summary + (string.IsNullOrWhiteSpace(entry.Description) ? "" : $" | {entry.Description}");
            return summary +
                $"\nkind={entry.Kind}; MIME={entry.MimeType}; origin={entry.Origin}; modified={entry.ModifiedUtc:O} by {modifiedBy}" +
                (string.IsNullOrWhiteSpace(entry.Sha256) ? "" : $"\nsha256={entry.Sha256}") +
                (string.IsNullOrWhiteSpace(entry.Description) ? "" : $"\ndescription={entry.Description}");
        }

        private static int DecodeFileCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            try
            {
                string value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (int.TryParse(value, out int offset) && offset >= 0) return offset;
            }
            catch { }
            throw new ProjectFileException("Invalid file-list cursor.");
        }

        private static string EncodeFileCursor(int offset) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // ── script execution (narrow Roslyn surface, same philosophy as KliveAgent) ──

        /// <summary>Globals a Commander work-script can use: HTTP, project-volume file IO, output buffer.</summary>
        public sealed class WorkScriptGlobals : ScriptGlobals
        {
            private readonly StringBuilder buffer = new();
            private readonly string volumeRoot;
            private readonly ProjectFileStore? files;
            private readonly string? projectID;
            private readonly ProjectFileActor actor;
            private readonly Action<ProjectFileEntry>? onWritten;
            public HttpClient Http { get; }

            public WorkScriptGlobals(Omnipotent.Services.KliveAgent.KliveAgent? kliveAgentService, string volumeRoot, HttpClient http, ProjectFileStore? files = null,
                string? projectID = null, ProjectFileActor? actor = null, Action<ProjectFileEntry>? onWritten = null)
                : base(kliveAgentService!, CancellationToken.None)
            {
                this.volumeRoot = volumeRoot;
                Http = http;
                this.files = files;
                this.projectID = projectID;
                this.actor = actor ?? ProjectFileActor.Unknown;
                this.onWritten = onWritten;
            }

            public void Output(object? value) => buffer.AppendLine(value?.ToString() ?? "null");
            public void Output<T>(Task<T> value) => buffer.AppendLine(value.GetAwaiter().GetResult()?.ToString() ?? "null");
            public void Output(Task value)
            {
                value.GetAwaiter().GetResult();
                object? result = value.GetType().IsGenericType ? value.GetType().GetProperty("Result")?.GetValue(value) : null;
                buffer.AppendLine(result?.ToString() ?? "(task completed)");
            }
            /// <summary>Project-volume compatibility name. Use ReadCodeFile for repository source.</summary>
            public string ReadFile(string relative) => ReadProjectFile(relative);
            public string ReadProjectFile(string relative) => files != null && projectID != null
                ? files.ReadTextAsync(projectID, relative, 8 * 1024 * 1024, CancellationToken).GetAwaiter().GetResult()
                : File.ReadAllText(Scoped(relative));
            public string ReadCodeFile(string repoRelativePath, int startLine = 1, int maxLines = 200) =>
                base.ReadFile(repoRelativePath, startLine, maxLines);
            public string ListCodeDirectory(string repoRelativePath = "") => base.ListDirectory(repoRelativePath);
            public void WriteFile(string relative, string content)
            {
                if (files != null && projectID != null)
                {
                    var entry = files.WriteTextAsync(projectID, relative, content, actor, CancellationToken).GetAwaiter().GetResult();
                    onWritten?.Invoke(entry);
                    return;
                }
                string physical = Scoped(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
                File.WriteAllText(physical, content);
            }
            public string[] ListFiles(string relative = ".")
            {
                if (files != null && projectID != null)
                {
                    string directory = string.IsNullOrWhiteSpace(relative) || relative == "." ? "" : relative;
                    return files.List(projectID, new ProjectFileListRequest
                    {
                        Directory = directory, Limit = 500,
                    }).Entries.Select(x => x.Path).ToArray();
                }
                return Directory.Exists(Scoped(relative)) ? Directory.GetFileSystemEntries(Scoped(relative)) : Array.Empty<string>();
            }

            public List<ProjectScriptFileEntry> ListFileEntries(string relative = ".")
            {
                if (files != null && projectID != null)
                {
                    string directory = string.IsNullOrWhiteSpace(relative) || relative == "." ? "" : relative;
                    return files.List(projectID, new ProjectFileListRequest { Directory = directory, Limit = 500 })
                        .Entries.Select(x => new ProjectScriptFileEntry
                        {
                            Name = Path.GetFileName(x.Path), Path = x.Path,
                            IsDirectory = x.Kind == ProjectFileKind.Directory, Size = x.Size,
                        }).ToList();
                }
                string scoped = Scoped(relative);
                if (!Directory.Exists(scoped)) return new();
                return Directory.GetFileSystemEntries(scoped).Select(x => new ProjectScriptFileEntry
                {
                    Name = Path.GetFileName(x),
                    Path = Path.GetRelativePath(volumeRoot, x).Replace('\\', '/'),
                    IsDirectory = Directory.Exists(x),
                    Size = File.Exists(x) ? new FileInfo(x).Length : 0,
                }).ToList();
            }

            private string Scoped(string relative)
            {
                string full = projectID != null
                    ? ProjectWorkspaceLocator.HostPath(projectID, relative)
                    : Path.GetFullPath(Path.Combine(volumeRoot, ProjectWorkspaceLocator.NormalizeRelative(relative)));
                if (!IsWithinRoot(volumeRoot, full))
                    throw new UnauthorizedAccessException("Path escapes the project volume.");
                return full;
            }
            public string DrainOutput()
            {
                string output = buffer.ToString();
                buffer.Clear();
                return output;
            }
        }

        public sealed class ProjectScriptFileEntry
        {
            public string Name { get; init; } = "";
            public string Path { get; init; } = "";
            public bool IsDirectory { get; init; }
            public long Size { get; init; }
        }

        // Roslyn needs the referenced ASSEMBLIES, not just the imported namespaces — otherwise
        // System.Net.Http / System.Text.Json names fail to bind (CS0234). Built once.
        private static readonly ScriptOptions ScriptOpts = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,                       // System.Private.CoreLib
                typeof(Enumerable).Assembly,                    // System.Linq
                typeof(System.Net.Http.HttpClient).Assembly,    // System.Net.Http
                typeof(System.Text.Json.JsonSerializer).Assembly, // System.Text.Json
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly, // dynamic Globals compatibility
                typeof(WorkScriptGlobals).Assembly)             // Omnipotent (the globals type)
            .WithImports("System", "System.IO", "System.Linq", "System.Collections", "System.Collections.Generic", "System.Net.Http",
                "System.Text", "System.Text.Json", "System.Threading", "System.Threading.Tasks",
                "Omnipotent.Services.KliveAgent", "Omnipotent.Services.KliveAgent.Models",
                "Omnipotent.Services.Projects");

        private async Task<CommanderToolResult> RunScriptAsync(string code, CancellationToken ct)
        {
            if (DetectNonCSharpLanguage(code) is { } language)
                return new CommanderToolResult(
                    $"run_script executes C# only, but this looks like {language}. Use computer_terminal for Linux/Python/Node commands, run_bash for host Bash, or run_powershell for host PowerShell.")
                { Succeeded = false };

            var session = GetScriptSession();
            await session.Gate.WaitAsync(ct);
            try
            {
                session.LastUsedUtc = DateTime.UtcNow;
                var globals = session.Globals ??= CreateScriptGlobals(ct);
                globals.CancellationToken = ct;
                globals.ConversationId = $"project:{project.ProjectID}:{actingAgentID}";
                if (session.State == null)
                    session.State = await CSharpScript.RunAsync<object>(code, ScriptOpts, globals, typeof(WorkScriptGlobals), ct);
                else
                    session.State = await session.State.ContinueWithAsync<object>(code, ScriptOpts, ct);
                string output = string.Join("\n", new[] { globals.DrainOutput(), globals.TakeOutput() }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                object? returnValue = await UnwrapScriptValueAsync(session.State.ReturnValue, ct);
                string ret = returnValue?.ToString() ?? "";
                string combined = string.Join("\n", new[] { output, ret }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return new CommanderToolResult(string.IsNullOrWhiteSpace(combined)
                    ? "Script ran with no output. Use Output(...) to report results."
                    : ProjectsContextBudget.TruncateToTokens(combined, ProjectsContextBudget.ToolResultBudget));
            }
            catch (CompilationErrorException ex)
            {
                return new CommanderToolResult("Script compile error:\n" + string.Join("\n", ex.Diagnostics.Take(8)))
                { Succeeded = false };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new CommanderToolResult($"Script threw {ex.GetType().Name}: {ex.Message}") { Succeeded = false };
            }
            finally { session.Gate.Release(); }
        }

        private static string? DetectNonCSharpLanguage(string code)
        {
            string sample = (code ?? "").TrimStart();
            if (sample.StartsWith("#!", StringComparison.Ordinal))
                return sample.StartsWith("#!/usr/bin/env python", StringComparison.OrdinalIgnoreCase) ? "Python" : "a shell script";
            if (Regex.IsMatch(sample, @"^(?:import\s+[A-Za-z_][\w.]*|from\s+[A-Za-z_][\w.]*\s+import\s+|def\s+[A-Za-z_]\w*\s*\()"))
                return "Python";
            if (Regex.IsMatch(sample, @"^(?:set\s+-[a-z]+\s*$|export\s+[A-Za-z_]\w*=|apt(?:-get)?\s+|python3?\s+)", RegexOptions.Multiline))
                return "Bash";
            if (Regex.IsMatch(sample, @"^(?:param\s*\(|\$ErrorActionPreference\s*=|Get-[A-Za-z]+\s+)", RegexOptions.IgnoreCase))
                return "PowerShell";
            if (Regex.IsMatch(sample, @"^(?:(?:const|let)\s+[A-Za-z_$][\w$]*\s*=|document\.querySelector)", RegexOptions.Multiline))
                return "JavaScript";
            return null;
        }

        private static async Task<object?> UnwrapScriptValueAsync(object? value, CancellationToken ct)
        {
            if (value is not Task task) return value;
            await task.WaitAsync(ct);
            return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
        }

        private ScriptSession GetScriptSession()
        {
            string key = $"{project.ProjectID}:{wakeID}:{actingAgentID}";
            foreach (var stale in scriptSessions.Where(kv => DateTime.UtcNow - kv.Value.LastUsedUtc > TimeSpan.FromHours(2)).ToList())
                scriptSessions.TryRemove(stale.Key, out _);
            return scriptSessions.GetOrAdd(key, _ => new ScriptSession());
        }

        private WorkScriptGlobals CreateScriptGlobals(CancellationToken ct)
        {
            var actor = FileActor();
            return new WorkScriptGlobals(KliveAgentService, VolumeRoot(), http, Files, project.ProjectID, actor,
                entry => ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Write,
                    [entry.Path], entry.Size, wakeID: wakeID))
            {
                CancellationToken = ct,
                ConversationId = $"project:{project.ProjectID}:{actingAgentID}",
            };
        }

        private ScriptGlobals SharedGlobals(CancellationToken ct) => new(KliveAgentService!, ct)
        {
            ConversationId = $"project:{project.ProjectID}:{actingAgentID}",
        };

        private static string FormatMemories(IEnumerable<Omnipotent.Services.KliveAgent.Models.AgentMemoryEntry> memories)
        {
            var list = memories?.ToList() ?? new();
            if (list.Count == 0) return "No shared memories matched.";
            return string.Join("\n", list.Select(m =>
                $"[{ShortId(m.Id)}] {m.Title ?? m.MemoryType} · {m.CreatedAt:yyyy-MM-dd HH:mm}Z · tags={string.Join(",", m.Tags ?? new List<string>())}\n{m.Content}"));
        }

        private static string ShortId(string? id) => string.IsNullOrWhiteSpace(id) ? "?" : id.Length <= 8 ? id : id[..8];

        private string VolumeRoot()
        {
            string dir = ProjectWorkspaceLocator.HostRoot(project.ProjectID);
            Directory.CreateDirectory(dir);
            return Path.GetFullPath(dir);
        }

        private (string? path, string? error) ResolveHostWorkingDirectory(string? requested)
        {
            string relative = (requested ?? "").Trim().Replace('\\', '/');
            if (relative.Length == 0 || relative == "/project") return (VolumeRoot(), null);
            if (relative.StartsWith("/project/", StringComparison.Ordinal)) relative = relative[9..];
            else if (relative.StartsWith("/", StringComparison.Ordinal))
                return (null, "workingDirectory must stay under the shared /project volume.");

            var (path, error) = ResolveVolumePath(relative);
            if (error != null) return (null, error);
            if (!Directory.Exists(path))
                return (null, $"workingDirectory does not exist: {requested}. Create it first or use /project.");
            return (path, null);
        }

        private static string SliceTextLines(string text, int startLine, int maxLines)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            int start = Math.Clamp(startLine - 1, 0, lines.Length);
            int count = Math.Min(Math.Max(0, maxLines), lines.Length - start);
            string body = string.Join("\n", lines.Skip(start).Take(count));
            if (start == 0 && count == lines.Length) return body;
            string shown = count == 0 ? "no lines" : $"lines {start + 1}-{start + count}";
            return $"[{shown} of {lines.Length}]\n{body}";
        }

        private async Task<CommanderToolResult> GrepProjectAsync(string pattern, string requestedPath, int maxResults,
            bool fixedString, bool caseSensitive, CancellationToken ct)
        {
            var (physical, error) = ResolveVolumePath(string.IsNullOrWhiteSpace(requestedPath) ? "." : requestedPath);
            if (error != null) return new CommanderToolResult(error) { Succeeded = false };
            if (!File.Exists(physical) && !Directory.Exists(physical))
                return new CommanderToolResult($"Project path not found: {requestedPath}") { Succeeded = false };

            Regex? regex = null;
            if (!fixedString)
            {
                try
                {
                    regex = new Regex(pattern,
                        (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    return new CommanderToolResult($"Invalid grep regex: {ex.Message}") { Succeeded = false };
                }
            }

            IEnumerable<string> candidates;
            if (File.Exists(physical)) candidates = new[] { physical! };
            else
            {
                candidates = Directory.EnumerateFiles(physical!, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }

            var matches = new List<string>();
            int filesScanned = 0, filesSkipped = 0;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (string candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(candidate);
                    if (info.Length > 8L * 1024 * 1024) { filesSkipped++; continue; }
                    filesScanned++;
                    using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                    int lineNumber = 0;
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        lineNumber++;
                        bool hit = fixedString ? line.Contains(pattern, comparison) : regex!.IsMatch(line);
                        if (!hit) continue;
                        string relative = Path.GetRelativePath(VolumeRoot(), candidate).Replace('\\', '/');
                        string excerpt = line.Length <= 600 ? line : line[..599] + "…";
                        matches.Add($"{relative}:{lineNumber}: {excerpt}");
                        if (matches.Count >= maxResults) break;
                    }
                }
                catch (DecoderFallbackException) { filesSkipped++; }
                catch (RegexMatchTimeoutException)
                {
                    return new CommanderToolResult("grep regex exceeded the per-line safety timeout; simplify the expression or use fixedString=true.")
                    { Succeeded = false };
                }
                catch (IOException) { filesSkipped++; }
                catch (UnauthorizedAccessException) { filesSkipped++; }
                if (matches.Count >= maxResults) break;
            }

            string result = matches.Count == 0
                ? $"No matches in /project/{ProjectWorkspaceLocator.NormalizeRelative(project.ProjectID, requestedPath)} ({filesScanned} text files scanned, {filesSkipped} skipped)."
                : string.Join("\n", matches) + $"\n-- {matches.Count} match(es); {filesScanned} text files scanned, {filesSkipped} skipped";
            return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(result, ProjectsContextBudget.ToolResultBudget));
        }

        private (string? path, string? error) ResolveVolumePath(string? relative)
        {
            if (relative == null) return (null, "Provide 'path' (relative to the project volume).");
            try { return (ProjectWorkspaceLocator.HostPath(project.ProjectID, relative), null); }
            catch (Exception ex) { return (null, ex.Message); }
        }

        private static bool IsWithinRoot(string root, string candidate)
        {
            string relative = Path.GetRelativePath(root, candidate);
            return relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }

        private ProjectEvent Evt(string type, string author, string text) => new()
        {
            ProjectID = project.ProjectID,
            WakeID = wakeID,
            AgentID = actingAgentID,
            Type = type,
            Author = author,
            Text = text,
        };

        /// <summary>
        /// Logs an observable mutation to the timeline (which also pushes it to the UI over the
        /// event WS stream). Payload is the small structured change only — never the history.
        /// </summary>
        private void AppendObservableEvent(string name, string op, string text,
            ProjectObservableStore.ObservableChange? change, double? operand = null)
        {
            string author = actingAgentID == "commander" ? "commander" : "agent";
            var evt = Evt(ProjectEventTypes.ObservableChanged, author, text);
            evt.PayloadJson = JsonConvert.SerializeObject(new
            {
                name,
                op,
                operand,
                observableID = change?.Observable.ObservableID,
                type = change?.Observable.Type.ToString(),
                format = change?.Observable.Format.ToString(),
                unit = change?.Observable.Unit,
                previous = change?.PreviousDisplay,
                current = change?.NewDisplay,
            });
            eventLog.Append(evt);
        }

        /// <summary>Logs an account-registry mutation to the timeline (never any secret value).</summary>
        private void AppendAccountEvent(string service, string username, string op)
        {
            string author = actingAgentID == "commander" ? "commander" : "agent";
            string serviceKey = Omnipotent.Services.AccountRegistry.AccountRegistryStore.NormalizeService(service);
            var evt = Evt(ProjectEventTypes.AccountChanged, author, $"account {op}: {serviceKey} · {username}");
            evt.PayloadJson = JsonConvert.SerializeObject(new { serviceKey, username, op });
            eventLog.Append(evt);
        }

        private static bool IsAutomaticProviderRecoveryRequest(params string?[] parts)
        {
            string text = string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (Regex.IsMatch(text, @"\b(?:captcha|sms|(?:two|2)[\s-]?factor|phone\s+verification|hardware[\s-]?key|physical(?:[\s-]?world)?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            if (Regex.IsMatch(text,
                @"\b(?:rate[\s-]?limit(?:ed)?|ratelimit(?:ed)?|too\s+many\s+requests|risk_control)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
            return Regex.IsMatch(text, @"\b(?:llm\s+provider|openrouter|model\s+route)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && Regex.IsMatch(text, @"\b(?:unavailable|failure|failed|throttl|cooldown|retry)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool AccountUsesDisposableMail(string? service, string? email)
        {
            string key = Omnipotent.Services.AccountRegistry.AccountRegistryStore.NormalizeService(service ?? "");
            if (key is "mail-tm" or "mail.tm") return true;
            if (!string.IsNullOrWhiteSpace(email) && email.EndsWith("@mail.tm", StringComparison.OrdinalIgnoreCase)) return true;
            // mail.tm issues inboxes on rotating domains, but every such account is still explicitly
            // registered as the mail.tm service in this harness. We intentionally avoid a stale
            // hard-coded domain list here.
            return false;
        }

        /// <summary>Matches either an exact requested project path or a required suffix such as
        /// ".pdf". The caller still verifies that the supplied path exists in ProjectFileStore.</summary>
        private static bool ExpectedArtifactMatches(string expected, string candidate)
        {
            expected = (expected ?? "").Trim().Replace('\\', '/');
            candidate = (candidate ?? "").Trim().Replace('\\', '/');
            if (expected.Length == 0 || candidate.Length == 0) return false;
            return expected.StartsWith(".", StringComparison.Ordinal)
                ? candidate.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
                : string.Equals(expected.TrimStart('/'), candidate.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>PDF-named output must be an actual regular PDF file, not merely a path whose
        /// extension looks right. Other expected artifacts still require an existing regular file.</summary>
        private static bool IsExpectedArtifactVerified(ProjectFileStore files, string projectID, string expected, string path)
        {
            var entry = files.Stat(projectID, path);
            if (entry?.Kind != ProjectFileKind.File) return false;
            bool expectsPdf = expected.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!expectsPdf) return true;
            try
            {
                using var stream = files.OpenRead(projectID, path);
                Span<byte> header = stackalloc byte[5];
                return stream.Read(header) == header.Length &&
                    header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D' &&
                    header[3] == (byte)'F' && header[4] == (byte)'-';
            }
            catch { return false; }
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

        /// <summary>Resolves the live KliveMail repository through the shared KliveAgent service graph
        /// (the in-process handle the klivemail_* tools drive), or returns null with an actionable
        /// message. This is deliberately NOT reflection/HTTP — it's the same service the SMTP server
        /// writes into, so a mailbox created here receives real mail.</summary>
        private Omnipotent.Services.KliveMail.Persistence.KliveMailRepository? GetKliveMailRepo(out string? error)
        {
            error = null;
            if (KliveAgentService == null)
            {
                error = "KliveMail is unavailable (the KliveAgent service bridge is down on this host).";
                RecordKliveMailHealth(false, "ServiceBridgeUnavailable", error);
                return null;
            }
            var svc = KliveAgentService.GetActiveServices()
                .OfType<Omnipotent.Services.KliveMail.KliveMail>()
                .FirstOrDefault(s => s.IsServiceActive());
            if (svc == null)
            {
                error = "The KliveMail service is not running on this host.";
                RecordKliveMailHealth(false, "ServiceNotRunning", error);
                return null;
            }
            var repo = svc.Repo;
            if (repo == null)
            {
                error = "KliveMail is still starting (its store isn't ready). Retry shortly.";
                RecordKliveMailHealth(false, "RepositoryNotReady", error);
                return null;
            }
            RecordKliveMailHealth(true, "RepositoryReady", "The in-process KliveMail repository is ready.");
            return repo;
        }

        private void RecordKliveMailHealth(bool healthy, string code, string summary)
        {
            try { RuntimeState?.RecordDependencyHealth(project.ProjectID, "klivemail", healthy, code, summary); }
            catch { }
        }

        private void RecordMailboxAvailable(string address, string? purpose = null)
        {
            address = address.ToLowerInvariant();
            purpose = NormalizeMailboxPurpose(purpose);
            RecordKliveMailHealth(true, "RepositoryReady", $"KliveMail mailbox {address} is available in the live repository.");
            try
            {
                RuntimeState?.UpsertVerifiedFact(project.ProjectID, new ProjectVerifiedFact
                {
                    Key = "klivemail.mailbox." + address,
                    Value = address,
                    Description = "Canonical KliveMail mailbox identity; use this exact address without dotted/undotted substitutions.",
                    Evidence = new List<ProjectEvidenceReference>
                    {
                        new()
                        {
                            Kind = ProjectEvidenceKind.ExternalObservation,
                            Reference = "klivemail:" + address,
                            Description = "Observed directly in the in-process KliveMail repository.",
                        },
                    },
                    InvalidationKeys = new List<string> { "klivemail", "mailbox:" + address },
                });
                if (!string.IsNullOrWhiteSpace(purpose))
                {
                    RuntimeState?.UpsertVerifiedFact(project.ProjectID, new ProjectVerifiedFact
                    {
                        Key = "klivemail.canonical." + purpose,
                        Value = address,
                        Description = $"Authoritative KliveMail address for purpose '{purpose}'. This purpose binding supersedes stale prose and agent-authored observables.",
                        Evidence = new List<ProjectEvidenceReference>
                        {
                            new()
                            {
                                Kind = ProjectEvidenceKind.ExternalObservation,
                                Reference = "klivemail:" + address,
                                Description = "Mailbox identity observed directly in the in-process KliveMail repository.",
                            },
                        },
                        InvalidationKeys = new List<string> { "klivemail", "mailbox-purpose:" + purpose },
                    });

                    if (Observables != null)
                    {
                        string observableName = "canonical mailbox: " + purpose;
                        var existing = Observables.Get(project.ProjectID, observableName);
                        if (existing == null || existing.Type != ObservableType.Text
                            || !string.Equals(existing.TextValue, address, StringComparison.OrdinalIgnoreCase)
                            || existing.Validity != ObservableValidity.Valid
                            || existing.SourceKind != ObservableSourceKind.System)
                        {
                            var change = Observables.Set(project.ProjectID, observableName,
                                null, address, null, null,
                                "System-owned mailbox binding; typed KliveMail state is authoritative over stale agent status text.",
                                "system:klivemail", DateTime.UtcNow, null, ObservableSourceKind.System,
                                ObservableValidity.Valid);
                            AppendObservableEvent(change.Observable.Name, "set",
                                $"{change.Observable.Name}: {change.PreviousDisplay ?? "(new)"} → {change.NewDisplay} (canonical)", change);
                        }
                    }
                }
            }
            catch { }
        }

        private static string NormalizeMailboxPurpose(string? value)
        {
            string purpose = Regex.Replace((value ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return purpose.Length <= 60 ? purpose : purpose[..60].TrimEnd('-');
        }

        private void RecordMailboxDelivery(string address, bool healthy, string code, string summary)
        {
            try
            {
                RuntimeState?.RecordDependencyHealth(project.ProjectID,
                    "email-delivery/" + address.ToLowerInvariant(), healthy, code, summary);
            }
            catch { }
        }

        /// <summary>Pulls a 4–8 digit verification code out of an email, preferring a digit run next to
        /// a "code/verify/OTP" cue and falling back to the first standalone run.</summary>
        internal static string? ExtractVerificationCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var cued = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:code|verification|verify|confirm|otp|one[- ]?time)[^0-9]{0,40}([0-9]{4,8})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cued.Success) return cued.Groups[1].Value;
            var any = System.Text.RegularExpressions.Regex.Match(text, @"(?<![0-9])([0-9]{4,8})(?![0-9])");
            return any.Success ? any.Groups[1].Value : null;
        }

        /// <summary>
        /// Identifies the narrow class of address mistakes that can strand a verification email
        /// in a catch-all mailbox: dotted/undotted equivalents or one mistyped local-part
        /// character. The domain must be KliveMail's domain and must match exactly.
        /// </summary>
        internal static bool LikelyMailboxVariant(string expected, string? actual)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            string left = Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.NormalizeAddress(expected);
            string right = Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.NormalizeAddress(actual);
            int leftAt = left.LastIndexOf('@');
            int rightAt = right.LastIndexOf('@');
            if (leftAt <= 0 || rightAt <= 0) return false;
            string leftDomain = left[(leftAt + 1)..];
            string rightDomain = right[(rightAt + 1)..];
            if (!leftDomain.Equals(rightDomain, StringComparison.OrdinalIgnoreCase)
                || !leftDomain.Equals(Omnipotent.Services.KliveMail.Persistence.KliveMailRepository.MailDomain,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            string leftLocal = left[..leftAt];
            string rightLocal = right[..rightAt];
            if (leftLocal.Equals(rightLocal, StringComparison.OrdinalIgnoreCase)) return false;
            if (leftLocal.Replace(".", "", StringComparison.Ordinal)
                .Equals(rightLocal.Replace(".", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
                return true;
            return IsSingleEditApart(leftLocal, rightLocal);
        }

        private static bool IsSingleEditApart(string left, string right)
        {
            if (Math.Abs(left.Length - right.Length) > 1) return false;
            if (left.Length > right.Length) (left, right) = (right, left);
            int i = 0, j = 0, edits = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.ToLowerInvariant(left[i]) == char.ToLowerInvariant(right[j])) { i++; j++; continue; }
                if (++edits > 1) return false;
                if (left.Length == right.Length) { i++; j++; }
                else j++;
            }
            if (i < left.Length || j < right.Length) edits++;
            return edits == 1;
        }

        /// <summary>Best-effort plain text from an HTML email body (tags stripped, entities decoded), for
        /// display and code extraction when a message has no text/plain part.</summary>
        private static string? StripHtml(string? html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            string noTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(noTags);
        }
    }
}
