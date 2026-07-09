using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects.Stimulus;
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
        /// <summary>Marks the project completed (archives Discord channel, stops containers).</summary>
        public Func<Task>? CompleteProjectAsync { get; set; }
        /// <summary>Disposes a retired agent's own desktop container (frees ~1 GB immediately, not at project end).</summary>
        public Func<string, Task>? DisposeAgentDesktopAsync { get; set; }
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

        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

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

        public async Task<CommanderToolResult> DispatchAsync(string tool, string argsJson, CancellationToken ct)
        {
            JObject a;
            try { a = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson); }
            catch { a = new JObject(); }

            switch (tool)
            {
                case "update_plan":
                {
                    string plan = (string?)a["plan"] ?? "";
                    var digest = digests.GetDigest(project.ProjectID);
                    digest.CurrentPlan = plan;
                    digests.SaveDigest(digest);
                    eventLog.Append(Evt(ProjectEventTypes.Status, "commander", $"Plan updated: {Trunc(plan, 200)}"));
                    return new CommanderToolResult("Plan updated.");
                }

                case "report_progress":
                {
                    string note = (string?)a["note"] ?? "";
                    eventLog.Append(Evt(ProjectEventTypes.CommanderMessage, "commander", note));
                    return new CommanderToolResult("Progress recorded.");
                }

                case "spawn_sub_agent":
                {
                    string role = (string?)a["role"] ?? "worker";
                    string tierStr = (string?)a["tier"] ?? "Text";
                    string objective = (string?)a["objective"] ?? "";
                    if (!Enum.TryParse<ProjectAgentTier>(tierStr, ignoreCase: true, out var tier))
                        return new CommanderToolResult($"Unknown tier '{tierStr}'. Use Text, TextImage, TextImageVideo, or TextImageVideoAudio.");
                    try
                    {
                        var agent = subAgents.Spawn(project.ProjectID, actingAgentID, tier, role);
                        eventLog.Append(Evt(ProjectEventTypes.Status, "commander", $"Objective for {agent.AgentID}: {objective}"));
                        return new CommanderToolResult($"Spawned {tier} agent '{role}' with ID {agent.AgentID}.");
                    }
                    catch (InvalidOperationException ex) { return new CommanderToolResult(ex.Message); }
                }

                case "retire_sub_agent":
                {
                    string id = (string?)a["agentID"] ?? "";
                    bool ok = subAgents.Retire(project.ProjectID, id);
                    // Free the retired agent's own desktop immediately rather than leaking it until
                    // the project completes.
                    if (ok && DisposeAgentDesktopAsync != null)
                        try { await DisposeAgentDesktopAsync(id); } catch { }
                    return new CommanderToolResult(ok ? $"Retired agent {id}." : $"No active agent {id}.");
                }

                case "send_agent_message":
                {
                    string toId = (string?)a["agentID"] ?? "";
                    string message = (string?)a["message"] ?? "";
                    if (SendAgentMessageAsync != null) await SendAgentMessageAsync(project.ProjectID, actingAgentID, toId, message);
                    else eventLog.Append(new ProjectEvent { ProjectID = project.ProjectID, WakeID = wakeID, AgentID = toId, Type = ProjectEventTypes.AgentMessage, Author = "commander", Text = $"→{toId}: {message}" });
                    return new CommanderToolResult($"Message sent to {toId}.");
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
                            switch (kind.ToLowerInvariant())
                            {
                                case "tokens": p.TokenBudgetUsd = amount; if (p.Status == ProjectStatus.BudgetPaused) p.Status = ProjectStatus.Active; break;
                                case "money": p.MoneyBudgetUsd = amount; break;
                                case "agents": p.SubAgentCap = (int)amount; break;
                            }
                            projectStore.SaveProject(p);
                        }
                    }
                    return new CommanderToolResult($"Klives {res.Decision}: {res.Comment}");
                }

                case "record_money_spend":
                {
                    double amount = (double?)a["amount"] ?? 0;
                    string description = (string?)a["description"] ?? "";
                    if (amount <= 0) return new CommanderToolResult("Provide a positive 'amount' in USD.");
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
                    var (path, err) = ResolveVolumePath((string?)a["path"]);
                    if (err != null) return new CommanderToolResult(err);
                    if (!File.Exists(path)) return new CommanderToolResult($"No file at {a["path"]}.");
                    string text = await File.ReadAllTextAsync(path!, ct);
                    return new CommanderToolResult(ProjectsContextBudget.TruncateToTokens(text, ProjectsContextBudget.ToolResultBudget));
                }

                case "write_file":
                {
                    var (path, err) = ResolveVolumePath((string?)a["path"]);
                    if (err != null) return new CommanderToolResult(err);
                    string content = (string?)a["content"] ?? "";
                    Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                    await File.WriteAllTextAsync(path!, content, ct);
                    return new CommanderToolResult($"Wrote {content.Length} chars to {a["path"]}.");
                }

                case "list_files":
                {
                    var (path, err) = ResolveVolumePath((string?)a["path"] ?? ".");
                    if (err != null) return new CommanderToolResult(err);
                    if (!Directory.Exists(path)) return new CommanderToolResult("Directory does not exist (the volume starts empty — write_file creates paths).");
                    var entries = Directory.EnumerateFileSystemEntries(path!).Take(200)
                        .Select(e => (Directory.Exists(e) ? "[dir] " : "") + Path.GetRelativePath(VolumeRoot(), e));
                    return new CommanderToolResult(string.Join("\n", entries) is { Length: > 0 } s ? s : "(empty)");
                }

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
                    string sourceKind = (string?)a["sourceKind"] ?? "";
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
                    return new CommanderToolResult($"Hook {hook.HookID} created ({sourceKind} → {hook.DestinationAgentID}).");
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

        // ── script execution (narrow Roslyn surface, same philosophy as KliveAgent) ──

        /// <summary>Globals a Commander work-script can use: HTTP, project-volume file IO, output buffer.</summary>
        public sealed class WorkScriptGlobals
        {
            private readonly StringBuilder buffer = new();
            private readonly string volumeRoot;
            public HttpClient Http { get; }
            public CancellationToken CancellationToken { get; init; }

            public WorkScriptGlobals(string volumeRoot, HttpClient http) { this.volumeRoot = volumeRoot; Http = http; }

            public void Output(object? value) => buffer.AppendLine(value?.ToString() ?? "null");
            public string ReadFile(string relative) => File.ReadAllText(Scoped(relative));
            public void WriteFile(string relative, string content)
            {
                string p = Scoped(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, content);
            }
            public string[] ListFiles(string relative = ".") =>
                Directory.Exists(Scoped(relative)) ? Directory.GetFileSystemEntries(Scoped(relative)) : Array.Empty<string>();

            private string Scoped(string relative)
            {
                string full = Path.GetFullPath(Path.Combine(volumeRoot, relative ?? ""));
                if (!full.StartsWith(volumeRoot, StringComparison.OrdinalIgnoreCase))
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
            var globals = new WorkScriptGlobals(VolumeRoot(), http) { CancellationToken = ct };
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
            if (!full.StartsWith(VolumeRoot(), StringComparison.OrdinalIgnoreCase))
                return (null, "Path escapes the project volume — use a relative path inside it.");
            return (full, null);
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

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
