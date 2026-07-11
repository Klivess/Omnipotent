using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects.Stimulus;
using Omnipotent.Services.ComputerControl;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.Projects
{
    /// <summary>Result of a Commander tool call: text observation fed back to the model, and control flags.</summary>
    public record CommanderToolResult(string ResultText)
    {
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
        /// <summary>Recall from KliveAgent's shared memory (Projects is part of KliveAgent): (query, max) → formatted results.</summary>
        public Func<string, int, Task<string>>? RecallMemoriesAsync { get; set; }
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
        /// <summary>Flips a Planning project to Active once its Grand Plan is approved (also lifts the in-wake tool gate).</summary>
        public Func<Task>? ActivateProjectAsync { get; set; }

        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>Returns the durable audit form of tool arguments. File contents are deliberately
        /// omitted: their path/hash/size belong in the project-file audit, not duplicated into JSONL.</summary>
        public static string? AuditPayload(string toolName, string argsJson)
        {
            if (toolName.StartsWith("computer_", StringComparison.Ordinal)) return null;
            if (!string.Equals(toolName, "write_file", StringComparison.Ordinal)) return argsJson;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                if (args["content"] is JToken content)
                {
                    string value = content.Type == JTokenType.String ? content.Value<string>() ?? "" : content.ToString(Formatting.None);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
                    args["content"] = $"[omitted {value.Length} chars; sha256={hash}]";
                }
                return args.ToString(Formatting.None);
            }
            catch { return "{\"content\":\"[omitted: invalid arguments]\"}"; }
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

        /// <summary>Execution tools locked while a project is in the PLANNING phase (awaiting Grand Plan approval).
        /// Research, memory, planning, councils, observables and messaging Klives stay available.</summary>
        private static readonly HashSet<string> PlanningBlockedTools = new(StringComparer.Ordinal)
        {
            "spawn_sub_agent", "run_script", "run_powershell", "run_bash",
            "record_money_spend", "complete_project", "write_file", "make_directory",
            "move_file", "copy_file", "delete_file", "mark_file_important",
        };
        private static readonly HashSet<string> FileMutationTools = new(StringComparer.Ordinal)
        {
            "write_file", "make_directory", "move_file", "copy_file", "delete_file", "mark_file_important",
        };

        public async Task<CommanderToolResult> DispatchAsync(string tool, string argsJson, CancellationToken ct)
        {
            JObject a;
            try { a = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson); }
            catch { a = new JObject(); }

            if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived && FileMutationTools.Contains(tool))
                return new CommanderToolResult("This project's shared filesystem is browse-only after completion or archive.");

            if (project.Status == ProjectStatus.Planning && PlanningBlockedTools.Contains(tool))
                return new CommanderToolResult(
                    "This project is in the PLANNING phase — execution tools unlock once Klives approves your Grand Plan " +
                    "(submit_grand_plan). Available now: research (web_search/search_knowledge/recall_memories), councils " +
                    "(convene_council), planning (update_plan), observables, and messaging Klives.");

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

                case "list_observables":
                {
                    if (Observables == null) return new CommanderToolResult("Observables unavailable.");
                    var list = Observables.List(project.ProjectID);
                    if (list.Count == 0)
                        return new CommanderToolResult("No observables yet. Create one with update_observable(op:'set').");
                    return new CommanderToolResult(string.Join("\n", list.Select(o =>
                        $"{o.Name} = {ProjectObservableStore.FormatValue(o)}"
                        + (string.IsNullOrWhiteSpace(o.Description) ? "" : $" — {o.Description}")
                        + $" (updated {o.UpdatedAt:MM-dd HH:mm}Z by {o.UpdatedBy})")));
                }

                case "update_observable":
                {
                    if (Observables == null) return new CommanderToolResult("Observables unavailable.");
                    string name = ((string?)a["name"] ?? "").Trim();
                    string op = ((string?)a["op"] ?? "").Trim().ToLowerInvariant();
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
                                var change = Observables.Set(project.ProjectID, name, num, text, fmt,
                                    (string?)a["unit"], (string?)a["description"], actingAgentID);
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
                        var agent = subAgents.Spawn(project.ProjectID, actingAgentID, tier, role);
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
                    CancelAgentWake?.Invoke(id);
                    bool ok = subAgents.Retire(project.ProjectID, id);
                    // Free the retired agent's own desktop immediately rather than leaking it until
                    // the project completes.
                    if (ok && DisposeAgentDesktopAsync != null)
                        try { await DisposeAgentDesktopAsync(id); } catch { }
                    return new CommanderToolResult(ok ? $"Retired agent {id}." : $"No active agent {id}.");
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
                                        p.Status = (GrandPlans?.HasApprovedPlan(p.ProjectID) ?? true)
                                            ? ProjectStatus.Active : ProjectStatus.Planning;
                                        projectStore.SaveProject(p);
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
                    string what = (string?)a["what"] ?? "";
                    if (RequestHumanAsync != null) await RequestHumanAsync(what);
                    eventLog.Append(Evt(ProjectEventTypes.Status, "commander", $"Human assistance requested: {what}"));
                    return new CommanderToolResult("Requested human assistance (surfaced to Klives).");
                }

                // ── Shared account registry (global across all projects + KliveAgent) ──
                case "account_register":
                {
                    if (Accounts == null) return new CommanderToolResult("Account registry unavailable.");
                    string service = (string?)a["service"] ?? "";
                    string username = (string?)a["username"] ?? "";
                    string? email = (string?)a["email"];
                    string? description = (string?)a["description"];
                    bool allowDuplicate = (bool?)a["allowDuplicate"] ?? false;
                    string? reason = (string?)a["reason"];
                    var secrets = new Dictionary<string, string>();
                    if (a["secrets"] is Newtonsoft.Json.Linq.JObject so)
                        foreach (var prop in so.Properties())
                            secrets[prop.Name] = (string?)prop.Value ?? "";
                    string result = await Accounts.RegisterAccountAsync(
                        service, username, email, secrets, description,
                        createdBy: "project:" + project.ProjectID, owner: "project:" + project.ProjectID,
                        allowDuplicate, reason);
                    AppendAccountEvent(service, username, allowDuplicate ? "register-duplicate" : "register");
                    return new CommanderToolResult(result);
                }

                case "account_list":
                {
                    if (Accounts == null) return new CommanderToolResult("Account registry unavailable.");
                    string? service = (string?)a["service"];
                    return new CommanderToolResult(await Accounts.DescribeAccountsAsync("project:" + project.ProjectID, service));
                }

                case "account_update":
                {
                    if (Accounts == null) return new CommanderToolResult("Account registry unavailable.");
                    string accountID = (string?)a["accountID"] ?? "";
                    if (string.IsNullOrWhiteSpace(accountID)) return new CommanderToolResult("Provide accountID (from account_list).");
                    if (Accounts.Get(accountID) == null) return new CommanderToolResult($"No account with id '{accountID}'.");
                    var done = new List<string>();
                    if (a["status"] != null && Enum.TryParse<Omnipotent.Services.AccountRegistry.AccountStatus>((string?)a["status"], true, out var st))
                    { Accounts.UpdateStatus(accountID, st); done.Add($"status={st}"); }
                    if (a["notes"] != null) { Accounts.UpdateNotes(accountID, (string?)a["notes"]); done.Add("notes"); }
                    string? secretName = (string?)a["addSecretName"];
                    string? secretValue = (string?)a["addSecretValue"];
                    if (!string.IsNullOrWhiteSpace(secretName) && secretValue != null)
                    { Accounts.AddSecret(accountID, secretName, secretValue); done.Add($"secret '{secretName}'"); }
                    if ((bool?)a["claim"] == true)
                    { if (Accounts.ClaimForOwner(accountID, "project:" + project.ProjectID)) done.Add("claimed"); }
                    AppendAccountEvent(Accounts.Get(accountID)?.ServiceKey ?? "", Accounts.Get(accountID)?.Username ?? "", "update");
                    return new CommanderToolResult(done.Count == 0 ? "Nothing to update (provide status, notes, addSecretName+addSecretValue, or claim)." : "Updated: " + string.Join(", ", done) + ".");
                }

                // ── KliveAgent shared memory (Projects is part of KliveAgent — memory transfers) ──
                case "recall_memories":
                {
                    if (RecallMemoriesAsync == null) return new CommanderToolResult("Memory unavailable.");
                    string query = (string?)a["query"] ?? "";
                    int max = (int?)a["max"] ?? 8;
                    return new CommanderToolResult(await RecallMemoriesAsync(query, Math.Clamp(max, 1, 25)));
                }

                case "save_memory":
                {
                    if (SaveMemoryAsync == null) return new CommanderToolResult("Memory unavailable.");
                    string content = (string?)a["content"] ?? "";
                    if (string.IsNullOrWhiteSpace(content)) return new CommanderToolResult("Provide 'content' to remember.");
                    var tags = a["tags"] is JArray arr ? arr.Select(t => t.ToString()).ToArray() : Array.Empty<string>();
                    return new CommanderToolResult(await SaveMemoryAsync(content, tags));
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

                // ── work tools: scripts / HTTP / files on the project volume ──

                case "http_request":
                {
                    string url = (string?)a["url"] ?? "";
                    string method = ((string?)a["method"] ?? "GET").ToUpperInvariant();
                    string? body = (string?)a["body"];
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                        return new CommanderToolResult("Provide an absolute http(s) 'url'.");
                    try
                    {
                        using var msg = new HttpRequestMessage(new HttpMethod(method), uri);
                        if (body != null && method != "GET") msg.Content = new StringContent(body, Encoding.UTF8,
                            (string?)a["contentType"] ?? "application/json");
                        using var resp = await http.SendAsync(msg, ct);
                        string text = await resp.Content.ReadAsStringAsync(ct);
                        return new CommanderToolResult($"HTTP {(int)resp.StatusCode} {resp.StatusCode}\n{ProjectsContextBudget.TruncateToTokens(text, ProjectsContextBudget.ToolResultBudget)}");
                    }
                    catch (Exception ex) { return new CommanderToolResult($"HTTP request failed: {ex.Message}"); }
                }

                case "read_file":
                {
                    string path = ((string?)a["path"] ?? "").Trim();
                    if (path.Length == 0) return new CommanderToolResult("Provide 'path'.");
                    if (Files == null)
                    {
                        var (physical, error) = ResolveVolumePath(path);
                        if (error != null) return new CommanderToolResult(error);
                        if (!File.Exists(physical)) return new CommanderToolResult($"No file at {path}.");
                        string fallbackText = await File.ReadAllTextAsync(physical!, ct);
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(fallbackText, ProjectsContextBudget.ToolResultBudget));
                    }
                    try
                    {
                        string text = await Files.ReadTextAsync(project.ProjectID, path, 2 * 1024 * 1024, ct);
                        return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(text, ProjectsContextBudget.ToolResultBudget));
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
                        string normalized = Files.NormalizeRelativePath(path);
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
                case "run_script":
                {
                    string code = (string?)a["code"] ?? "";
                    if (string.IsNullOrWhiteSpace(code)) return new CommanderToolResult("Provide 'code' — a C# script body.");
                    return await RunScriptAsync(code, ct);
                }

                case "run_powershell":
                {
                    string ps = (string?)a["script"] ?? "";
                    if (string.IsNullOrWhiteSpace(ps)) return new CommanderToolResult("Provide 'script' — a PowerShell script body.");
                    int secs = (int?)a["timeoutSeconds"] ?? 120;
                    var r = await HostShell.RunPowerShellAsync(ps, TimeSpan.FromSeconds(Math.Clamp(secs, 1, 900)), workingDir: VolumeRoot(), ct: ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(r.Format(), ProjectsContextBudget.ToolResultBudget));
                }

                case "run_bash":
                {
                    string bash = (string?)a["script"] ?? "";
                    if (string.IsNullOrWhiteSpace(bash)) return new CommanderToolResult("Provide 'script' — a bash script body.");
                    int secs = (int?)a["timeoutSeconds"] ?? 120;
                    var r = await HostShell.RunBashAsync(bash, TimeSpan.FromSeconds(Math.Clamp(secs, 1, 900)), workingDir: VolumeRoot(), ct: ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(r.Format(), ProjectsContextBudget.ToolResultBudget));
                }

                // ── stimulus hook CRUD (§5.1 — Commander side) ──

                case "create_stimulus_hook":
                {
                    if (HookStore == null) return new CommanderToolResult("Hook store unavailable.");
                    string sourceKind = ((string?)a["sourceKind"] ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(sourceKind)) return new CommanderToolResult("Provide 'sourceKind' (timer | webhook | file-watch | screen-diff | script).");
                    var hook = HookStore.Create(new StimulusHookRecord
                    {
                        ProjectID = project.ProjectID,
                        OwningAgentID = actingAgentID,
                        SourceKind = sourceKind,
                        SourceSpecJson = a["sourceSpec"]?.ToString(Formatting.None) ?? "{}",
                        RecognitionCriterion = (string?)a["criterion"] ?? "",
                        DestinationAgentID = (string?)a["destinationAgentID"] ?? actingAgentID,
                        Durability = sourceKind == "screen-diff" ? StimulusDurability.SupersedingByKey : StimulusDurability.Standard,
                    });
                    RearmAdapters?.Invoke();
                    string tokenNote = sourceKind == "webhook"
                        ? $" Ingress token (store it now): {hook.IngressToken}"
                        : "";
                    return new CommanderToolResult($"Hook {hook.HookID} created ({sourceKind} → {hook.DestinationAgentID}).{tokenNote}");
                }

                case "list_stimulus_hooks":
                {
                    if (HookStore == null) return new CommanderToolResult("Hook store unavailable.");
                    var hooks = HookStore.List(project.ProjectID);
                    return new CommanderToolResult(hooks.Count == 0 ? "No hooks." : string.Join("\n",
                        hooks.Select(h =>
                        {
                            var arm = h.Enabled ? GetHookArmInfo?.Invoke(h.HookID) : null;
                            string armText = h.Enabled
                                ? (arm == null ? "not armed" : $"{arm.State}: {arm.Detail}")
                                : "disabled";
                            return $"{h.HookID}: {h.SourceKind} → {h.DestinationAgentID} [{armText}] criterion: {Trunc(h.RecognitionCriterion, 80)}";
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
                    if (ConveneCouncilAsync == null) return new CommanderToolResult("Councils are unavailable.");
                    string topic = ((string?)a["topic"] ?? "").Trim();
                    string briefing = ((string?)a["briefing"] ?? "").Trim();
                    if (topic.Length == 0) return new CommanderToolResult("Provide 'topic' — the decision the council must weigh.");
                    if (briefing.Length == 0)
                        return new CommanderToolResult("Provide 'briefing' — the panel sees ONLY what you put here, so include everything they need to reason well.");
                    string[]? roles = (a["roles"] as JArray)?.Select(t => (string?)t ?? "").Where(s => s.Length > 0).ToArray();
                    string urgency = (string?)a["urgency"] ?? "routine";
                    string purpose = (string?)a["purpose"] ?? "decision";
                    string verdict = await ConveneCouncilAsync(topic, briefing, roles, urgency, purpose, ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(verdict, 2000));
                }

                case "submit_grand_plan":
                {
                    if (GrandPlans == null) return new CommanderToolResult("Grand Plan store unavailable.");
                    var content = ParsePlanContent(a);
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    if (content.Mission.Length == 0) return new CommanderToolResult("Provide 'mission' — the plan needs a mission statement.");
                    if (summary.Length == 0) return new CommanderToolResult("Provide 'summary' — a ≤150-word summary for the approval card and wake seeds.");
                    return await SubmitPlanForApprovalAsync(content, summary, changeNote: null, isAmendment: false, ct);
                }

                case "amend_grand_plan":
                {
                    if (GrandPlans == null) return new CommanderToolResult("Grand Plan store unavailable.");
                    if (!GrandPlans.HasApprovedPlan(project.ProjectID))
                        return new CommanderToolResult("No approved Grand Plan to amend yet. Use submit_grand_plan first.");
                    var content = ParsePlanContent(a);
                    string summary = ((string?)a["summary"] ?? "").Trim();
                    string changeNote = ((string?)a["changeNote"] ?? "").Trim();
                    if (content.Mission.Length == 0) return new CommanderToolResult("Provide 'mission' — the revised plan needs a mission statement.");
                    if (summary.Length == 0) return new CommanderToolResult("Provide 'summary' — a ≤150-word summary of the revised plan.");
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
                    string mRef = ((string?)a["milestoneId"] ?? "").Trim();
                    if (mRef.Length > 0)
                    {
                        var m = GrandPlans.UpdateMilestoneStatus(project.ProjectID, mRef, ParseMilestoneStatus((string?)a["milestoneStatus"]));
                        results.Add(m == null ? $"No milestone matched '{mRef}'." : $"Milestone '{Trunc(m.Title, 60)}' → {m.Status}.");
                    }
                    string cRef = ((string?)a["criterionId"] ?? "").Trim();
                    if (cRef.Length > 0)
                    {
                        var cc = GrandPlans.SetCriterionMet(project.ProjectID, cRef, ParseBool((string?)a["criterionMet"], defaultValue: true));
                        results.Add(cc == null ? $"No success criterion matched '{cRef}'." : $"Criterion '{Trunc(cc.Text, 60)}' → {(cc.Met ? "met" : "unmet")}.");
                    }
                    if (results.Count == 0)
                        return new CommanderToolResult("Provide milestoneId (+milestoneStatus) and/or criterionId (+criterionMet).");
                    string note = ((string?)a["note"] ?? "").Trim();
                    string msg = string.Join(" ", results);
                    eventLog.Append(Evt(ProjectEventTypes.GrandPlanProgress, "commander", note.Length > 0 ? $"{msg} {note}" : msg));
                    return new CommanderToolResult(msg);
                }

                case "get_grand_plan":
                {
                    if (GrandPlans == null) return new CommanderToolResult("Grand Plan store unavailable.");
                    var v = GrandPlans.GetCurrentApproved(project.ProjectID);
                    if (v == null) return new CommanderToolResult("No approved Grand Plan yet.");
                    return new CommanderToolResult(
                        $"GRAND PLAN v{v.Version} (approved {(v.ResolvedAt ?? v.SubmittedAt):MM-dd}):\n\n"
                        + ProjectsContextBudget.TruncateToTokens(DescribeGrandPlanForModel(v), 2500));
                }

                case "complete_project":
                {
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

        private static bool ParseBool(string? v, bool defaultValue) =>
            string.IsNullOrWhiteSpace(v) ? defaultValue : v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

        private static MilestoneStatus ParseMilestoneStatus(string? s) =>
            (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
            {
                "in_progress" or "inprogress" or "started" or "wip" => MilestoneStatus.InProgress,
                "done" or "complete" or "completed" or "finished" => MilestoneStatus.Done,
                "blocked" or "stuck" => MilestoneStatus.Blocked,
                _ => MilestoneStatus.Pending,
            };

        private static RiskSeverity ParseSeverity(string? s) =>
            (s ?? "").Trim().ToLowerInvariant() switch
            {
                "high" or "critical" or "severe" => RiskSeverity.High,
                "low" or "minor" => RiskSeverity.Low,
                _ => RiskSeverity.Medium,
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
                });
            }
            foreach (var r in (a["risks"] as JArray) ?? new JArray())
            {
                string desc = ((string?)r?["description"] ?? "").Trim();
                if (desc.Length == 0) continue;
                c.Risks.Add(new PlanRisk { Description = desc, Severity = ParseSeverity((string?)r?["severity"]), Mitigation = ((string?)r?["mitigation"] ?? "").Trim() });
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

        private static CommanderToolResult FileToolError(Exception ex) => ex switch
        {
            FileNotFoundException => new CommanderToolResult("Shared file not found: " + ex.Message),
            ProjectFileConflictException => new CommanderToolResult("Shared-file conflict: " + ex.Message),
            ProjectFileException or UnauthorizedAccessException or IOException =>
                new CommanderToolResult("Shared-file operation failed: " + ex.Message),
            _ => new CommanderToolResult($"Shared-file operation failed ({ex.GetType().Name}): {ex.Message}"),
        };

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
        public sealed class WorkScriptGlobals
        {
            private readonly StringBuilder buffer = new();
            private readonly string volumeRoot;
            private readonly ProjectFileStore? files;
            private readonly string? projectID;
            private readonly ProjectFileActor actor;
            private readonly Action<ProjectFileEntry>? onWritten;
            public HttpClient Http { get; }
            public CancellationToken CancellationToken { get; init; }

            public WorkScriptGlobals(string volumeRoot, HttpClient http, ProjectFileStore? files = null,
                string? projectID = null, ProjectFileActor? actor = null, Action<ProjectFileEntry>? onWritten = null)
            {
                this.volumeRoot = volumeRoot;
                Http = http;
                this.files = files;
                this.projectID = projectID;
                this.actor = actor ?? ProjectFileActor.Unknown;
                this.onWritten = onWritten;
            }

            public void Output(object? value) => buffer.AppendLine(value?.ToString() ?? "null");
            public string ReadFile(string relative) => files != null && projectID != null
                ? files.ReadTextAsync(projectID, relative, 8 * 1024 * 1024, CancellationToken).GetAwaiter().GetResult()
                : File.ReadAllText(Scoped(relative));
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

            private string Scoped(string relative)
            {
                string full = Path.GetFullPath(Path.Combine(volumeRoot, relative ?? ""));
                if (!IsWithinRoot(volumeRoot, full))
                    throw new UnauthorizedAccessException("Path escapes the project volume.");
                return full;
            }
            public string DrainOutput() => buffer.ToString();
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
                typeof(WorkScriptGlobals).Assembly)             // Omnipotent (the globals type)
            .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Net.Http",
                "System.Text", "System.Text.Json", "System.Threading.Tasks");

        private async Task<CommanderToolResult> RunScriptAsync(string code, CancellationToken ct)
        {
            var actor = FileActor();
            var globals = new WorkScriptGlobals(VolumeRoot(), http, Files, project.ProjectID, actor,
                entry => ProjectFileTimeline.Append(eventLog, project.ProjectID, actor, ProjectFileOperation.Write,
                    [entry.Path], entry.Size, wakeID: wakeID)) { CancellationToken = ct };
            try
            {
                var result = await CSharpScript.EvaluateAsync<object>(code, ScriptOpts, globals, typeof(WorkScriptGlobals), ct);
                string output = globals.DrainOutput();
                string ret = result?.ToString() ?? "";
                string combined = string.Join("\n", new[] { output, ret }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return new CommanderToolResult(string.IsNullOrWhiteSpace(combined)
                    ? "Script ran with no output. Use Output(...) to report results."
                    : ProjectsContextBudget.TruncateToTokens(combined, ProjectsContextBudget.ToolResultBudget));
            }
            catch (CompilationErrorException ex)
            {
                return new CommanderToolResult("Script compile error:\n" + string.Join("\n", ex.Diagnostics.Take(8)));
            }
            catch (Exception ex)
            {
                return new CommanderToolResult($"Script threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string VolumeRoot()
        {
            string dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsVolumesDirectory), project.ProjectID);
            Directory.CreateDirectory(dir);
            return Path.GetFullPath(dir);
        }

        private (string? path, string? error) ResolveVolumePath(string? relative)
        {
            if (relative == null) return (null, "Provide 'path' (relative to the project volume).");
            string full = Path.GetFullPath(Path.Combine(VolumeRoot(), relative));
            if (!IsWithinRoot(VolumeRoot(), full))
                return (null, "Path escapes the project volume — use a relative path inside it.");
            return (full, null);
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

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
