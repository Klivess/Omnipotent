using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects;

/// <summary>The canonical call a model-authored tool call resolves to, or the error to return instead.</summary>
public sealed class ProjectToolUnfoldResult
{
    private ProjectToolUnfoldResult(string toolName, string argumentsJson, string? errorText,
        IReadOnlyList<string>? warnings)
    {
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        ErrorText = errorText;
        Warnings = warnings ?? Array.Empty<string>();
    }

    /// <summary>Canonical tool name to validate, audit and dispatch (never a folded name).</summary>
    public string ToolName { get; }
    /// <summary>Canonical arguments with the group selector removed (or preserved for tools that own an 'op').</summary>
    public string ArgumentsJson { get; }
    /// <summary>Set when the call cannot be resolved to a capability; return this instead of dispatching.</summary>
    public string? ErrorText { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsValid => ErrorText == null;

    internal static ProjectToolUnfoldResult Ok(string toolName, string argumentsJson, IReadOnlyList<string>? warnings = null)
        => new(toolName, argumentsJson, null, warnings);
    internal static ProjectToolUnfoldResult Fail(ProjectToolContractError error)
        => new("", "", error.ToToolResult(), null);
}

/// <summary>
/// Folds the canonical project tool surface into a smaller offered surface without removing any
/// capability. Related canonical tools are presented to the model as ONE tool with an 'op'
/// selector; every call is unfolded back to its canonical (name, arguments) pair before contract
/// validation, tier gating, auditing and dispatch — so the event log, the audit redaction rules,
/// the desktop-interaction policy and the dispatcher continue to see canonical tool names only.
///
/// The offered surface must stay at or below <see cref="OfferedToolLimit"/>: several providers
/// reject or silently degrade requests carrying more tool definitions than that.
/// </summary>
public static class ProjectToolFacade
{
    /// <summary>Hard cap on the number of tool definitions offered to any project agent.</summary>
    public const int OfferedToolLimit = 64;

    /// <summary>A member capability of a folded tool: one 'op' value bound to one canonical tool.</summary>
    private sealed record FoldMember(string Op, string CanonicalTool, bool CanonicalOwnsOp);

    private sealed record FoldGroup(string Name, string Summary, IReadOnlyList<FoldMember> Members);

    /// <summary>
    /// The fold table. Each entry lists the canonical tools a folded tool covers, in the order the
    /// ops are presented to the model. The sentinel op "*" means "expand this canonical tool's own
    /// 'op' enum into the group", which is how tools that already dispatch on an op (update_checkpoint,
    /// update_observable) fold without changing their argument contract.
    /// </summary>
    private static readonly (string Name, string Summary, (string Op, string Tool)[] Ops)[] FoldTable =
    {
        ("project_directive",
            "Work with Klives' durable project directives — his rules, tasks and steering receipts. These records survive wake compaction; active rules are non-negotiable.",
            new[]
            {
                ("list", "list_project_directives"),
                ("acknowledge", "acknowledge_project_directive"),
                ("complete", "complete_project_directive"),
            }),

        ("checkpoint",
            "Read or update the machine-owned project handoff state. Unlike digest prose, checkpoints survive compaction without reinterpretation and are seeded into every wake's TYPED EXECUTION STATE.",
            new[]
            {
                ("get", "get_checkpoint"),
                ("*", "update_checkpoint"),
            }),

        ("observable",
            "Maintain the Observables dashboard — the live named values shown to Klives at the top of this project's page. Every change is timestamped into a bounded history so he sees trends.",
            new[]
            {
                ("list", "list_observables"),
                ("*", "update_observable"),
            }),

        ("manage_agents",
            "Command your task force: spawn workers (a 'task' mission for a bounded deliverable, a 'standing' mission for an ongoing beat someone must keep owning), assign them Grand Plan milestones, and retire finished ones to free slots. Keep the roster near its cap while the plan has unowned dependency-ready work. Message an existing agent with send_agent_message instead.",
            new[]
            {
                ("spawn", "spawn_sub_agent"),
                ("assign_work", "assign_plan_work"),
                ("retire", "retire_sub_agent"),
            }),

        ("vault",
            "The project vault for scratch secrets. Real service accounts belong in the shared account registry instead.",
            new[]
            {
                ("save", "vault_save"),
                ("list", "vault_list"),
            }),

        ("account",
            "The GLOBAL shared account registry — every project and KliveAgent share it. Always list before signing up anywhere: the account may already exist.",
            new[]
            {
                ("list", "account_list"),
                ("register", "account_register"),
                ("update", "account_update"),
            }),

        ("klivemail",
            "Built-in catch-all @klive.dev email, driven in-process against the live KliveMail service: no HTTP call, auth header, password, reflection or DNS diagnosis.",
            new[]
            {
                ("create_mailbox", "klivemail_create_mailbox"),
                ("list_messages", "klivemail_list_messages"),
                ("get_message", "klivemail_get_message"),
                ("wait_for_code", "klivemail_wait_for_code"),
            }),

        ("memory",
            "Klives' shared memory and reusable operating recipes. It spans every project and interactive KliveAgent, so what you save here reaches the rest of you.",
            new[]
            {
                ("recall", "recall_memories"),
                ("recall_by_tag", "recall_memories_by_tag"),
                ("save", "save_memory"),
                ("delete", "delete_memory"),
                ("save_shortcut", "save_shortcut"),
                ("list_shortcuts", "get_shortcuts"),
            }),

        ("knowledge",
            "Search Klives' whole cross-system knowledge base — other projects' decisions and outcomes, KliveAgent conversations and memories, Omniscience person facts, repo docs, cached web. Check here before spawning a research sub-agent.",
            new[]
            {
                ("search", "search_knowledge"),
                ("read_doc", "read_knowledge_doc"),
            }),

        ("web",
            "The LIVE web via self-hosted SearXNG (no API key). Prefer this over spawning a research sub-agent for a quick lookup.",
            new[]
            {
                ("search", "web_search"),
                ("fetch", "web_fetch"),
            }),

        ("manage_files",
            "Inspect and administer the shared /project filesystem. Content and browsing stay on read_file, write_file and list_files; this tool covers everything else.",
            new[]
            {
                ("stat", "stat_file"),
                ("resolve_path", "resolve_project_path"),
                ("make_directory", "make_directory"),
                ("move", "move_file"),
                ("copy", "copy_file"),
                ("delete", "delete_file"),
                ("mark_important", "mark_file_important"),
            }),

        ("repo",
            "Read Omnipotent's own REPOSITORY SOURCE and runtime paths. This is the host codebase, not the shared workspace — use grep, read_file and list_files for /project.",
            new[]
            {
                ("search", "search_code"),
                ("read_file", "read_code_file"),
                ("list_directory", "list_code_directory"),
                ("global_path", "get_global_path"),
            }),

        ("run_shell",
            "Run a shell script on the HOST machine (where Omnipotent runs), in its security context — elevated if Omnipotent is. This is the host, NOT your desktop container; use computer_terminal for container work.",
            new[]
            {
                ("powershell", "run_powershell"),
                ("bash", "run_bash"),
            }),

        ("browser",
            "Inspect and operate the agent's one persistent visible Chromium session. Every operation has a structured text result; screenshots are an optional extra channel when this agent has image input.",
            new[]
            {
                ("open", "computer_open_browser"),
                ("navigate", "computer_navigate"),
                ("inspect", "computer_browser_inspect"),
                // Structured op=click is framebuffer-independent. Keep the real VNC route for
                // sites whose trusted-event handling specifically requires physical desktop input.
                ("physical_click", "computer_click_browser_control"),
                ("upload", "computer_upload_file"),
                ("*", "computer_browser_action"),
            }),

        ("desktop",
            "Inspect and operate the agent's isolated Linux desktop. OCR, window state and input operations remain usable by text-only agents; screenshot is offered only when raw image input is enabled.",
            new[]
            {
                ("ensure_ready", "ensure_desktop_ready"),
                ("window_state", "computer_window_state"),
                ("read_screen", "computer_read_screen"),
                ("screenshot", "computer_screenshot"),
                ("find_text", "computer_find_text"),
                ("click_text", "computer_click_text"),
                ("move", "computer_move"),
                ("move_relative", "computer_mouse_move_relative"),
                ("click", "computer_click"),
                ("drag", "computer_drag"),
                ("mouse_down", "computer_mouse_down"),
                ("mouse_up", "computer_mouse_up"),
                ("scroll", "computer_scroll"),
                ("type", "computer_type"),
                ("key", "computer_key"),
                ("key_down", "computer_key_down"),
                ("key_up", "computer_key_up"),
                ("release_all", "computer_release_all"),
                ("wait", "computer_wait"),
                ("focus_window", "computer_focus_window"),
                ("launch_app", "computer_launch_app"),
                ("terminal", "computer_terminal"),
                ("clipboard_get", "computer_clipboard_get"),
                ("clipboard_set", "computer_clipboard_set"),
                ("confirm_action", "computer_confirm_action"),
                ("confirm_and_click", "computer_confirm_and_click"),
            }),

        ("stimulus_hook",
            "Durable stimulus subscriptions — the tool that decides what wakes you. A well-hooked project reacts to its world instead of polling it.",
            new[]
            {
                ("create", "create_stimulus_hook"),
                ("list", "list_stimulus_hooks"),
                ("delete", "delete_stimulus_hook"),
            }),

        ("grand_plan",
            "The strategic Grand Plan Klives approves — the project's north star. Tick live progress against it with update_plan_progress, which never re-opens approval.",
            new[]
            {
                ("get", "get_grand_plan"),
                ("submit", "submit_grand_plan"),
                ("amend", "amend_grand_plan"),
            }),
    };

    /// <summary>
    /// Canonical tools that are dropped from the offered surface because another offered tool
    /// dispatches identically. A model that still names one is silently routed to the survivor.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AbsorbedAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Both names hit the same `case` in ProjectCommanderTools: one live C# console.
            ["run_script"] = "execute_csharp",
        };

    private static readonly Lazy<IReadOnlyList<FoldGroup>> groups = new(BuildGroups);
    private static readonly Lazy<IReadOnlyDictionary<string, JObject>> canonicalSchemas = new(() =>
        ProjectCommanderAgent.BuildCoreToolDefinitions()
            .Concat(ProjectCommanderAgent.BuildComputerToolDefinitions())
            .Where(t => !string.IsNullOrEmpty(t.function?.name))
            .GroupBy(t => t.function.name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => SchemaOf(g.First()), StringComparer.Ordinal));

    /// <summary>Every canonical tool the folded surface still reaches, folded or not.</summary>
    public static IReadOnlyCollection<string> CoveredCanonicalTools =>
        groups.Value.SelectMany(g => g.Members).Select(m => m.CanonicalTool)
            .Concat(AbsorbedAliases.Keys).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The folded names offered to the model, in fold-table order.</summary>
    public static IReadOnlyList<string> FoldedToolNames => groups.Value.Select(g => g.Name).ToList();

    private static IReadOnlyList<FoldGroup> BuildGroups()
    {
        var schemas = ProjectCommanderAgent.BuildCoreToolDefinitions()
            .Concat(ProjectCommanderAgent.BuildComputerToolDefinitions())
            .Where(t => !string.IsNullOrEmpty(t.function?.name))
            .GroupBy(t => t.function.name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => SchemaOf(g.First()), StringComparer.Ordinal);

        var built = new List<FoldGroup>();
        foreach (var (name, summary, ops) in FoldTable)
        {
            var members = new List<FoldMember>();
            foreach (var (op, tool) in ops)
            {
                if (op != "*")
                {
                    members.Add(new FoldMember(op, tool, false));
                    continue;
                }
                // Expand the canonical tool's own op enum so its argument contract is untouched.
                if (!schemas.TryGetValue(tool, out var schema)) continue;
                var ownOps = (schema["properties"]?["op"]?["enum"] as JArray)?
                    .Values<string>().Where(v => !string.IsNullOrWhiteSpace(v)) ?? Enumerable.Empty<string>();
                foreach (string ownOp in ownOps) members.Add(new FoldMember(ownOp!, tool, true));
            }
            if (members.Count > 0) built.Add(new FoldGroup(name, summary, members));
        }
        return built;
    }

    /// <summary>
    /// Produces the tool list to offer the model: every tool not covered by the fold table passes
    /// through untouched, and each covered group becomes one tool positioned where its first
    /// member was. Ops whose canonical tool is absent (tier gating) are dropped from the group, so
    /// a worker is never offered an operation it is not allowed to perform.
    /// </summary>
    public static List<HFWrapper.HFTool> Fold(IReadOnlyList<HFWrapper.HFTool> canonicalTools)
    {
        ArgumentNullException.ThrowIfNull(canonicalTools);
        var available = canonicalTools.Where(t => !string.IsNullOrEmpty(t?.function?.name))
            .GroupBy(t => t.function.name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var groupOfTool = new Dictionary<string, FoldGroup>(StringComparer.Ordinal);
        var presentMembers = new Dictionary<string, List<FoldMember>>(StringComparer.Ordinal);
        foreach (var group in groups.Value)
        {
            var present = group.Members.Where(m => available.ContainsKey(m.CanonicalTool)).ToList();
            if (present.Count == 0) continue;
            presentMembers[group.Name] = present;
            foreach (var member in present) groupOfTool[member.CanonicalTool] = group;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var offered = new List<HFWrapper.HFTool>();
        foreach (var tool in canonicalTools)
        {
            string name = tool?.function?.name ?? "";
            if (name.Length == 0) continue;
            // An absorbed alias is reachable through its survivor; never offer both.
            if (AbsorbedAliases.TryGetValue(name, out var survivor) && available.ContainsKey(survivor)) continue;
            if (!groupOfTool.TryGetValue(name, out var group))
            {
                offered.Add(tool);
                continue;
            }
            if (!emitted.Add(group.Name)) continue;
            offered.Add(BuildFoldedTool(group, presentMembers[group.Name], available));
        }
        return offered;
    }

    /// <summary>
    /// Resolves a model-authored call to the canonical tool that implements it. Canonical names are
    /// passed through unchanged, so guidance text and resumed sessions that still name a folded-away
    /// tool keep working.
    /// </summary>
    public static ProjectToolUnfoldResult Unfold(string toolName, string? argumentsJson)
    {
        string arguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson!;
        if (AbsorbedAliases.TryGetValue(toolName, out var survivor))
            return ProjectToolUnfoldResult.Ok(survivor, arguments);

        var group = groups.Value.FirstOrDefault(g => string.Equals(g.Name, toolName, StringComparison.Ordinal));
        if (group == null) return ProjectToolUnfoldResult.Ok(toolName, arguments);

        JObject args;
        try
        {
            var token = JToken.Parse(arguments);
            if (token is not JObject obj)
                return ProjectToolUnfoldResult.Fail(new ProjectToolContractError(
                    ProjectToolContract.ExpectedObject, "$", "Tool arguments must be a JSON object."));
            args = obj;
        }
        catch (JsonException)
        {
            return ProjectToolUnfoldResult.Fail(new ProjectToolContractError(
                ProjectToolContract.InvalidJson, "$", "Tool arguments are not valid JSON."));
        }

        var warnings = new List<string>();
        string allowed = string.Join(", ", group.Members.Select(m => m.Op).Distinct(StringComparer.Ordinal));
        string? op = args["op"]?.Type == JTokenType.String ? args["op"]!.Value<string>()?.Trim() : null;

        if (string.IsNullOrEmpty(op))
        {
            var inferred = InferMember(group, args);
            if (inferred == null)
                return ProjectToolUnfoldResult.Fail(new ProjectToolContractError(
                    ProjectToolContract.MissingRequired, "$.op",
                    $"'{group.Name}' requires 'op' — it selects which operation to perform. Allowed: {allowed}."));
            if (inferred.CanonicalOwnsOp)
            {
                // The canonical tool owns its own op vocabulary. Preserve the missing selector so
                // its established normalization can infer the operation, while still removing empty
                // placeholders that belong to other members of the folded union.
                return NormalizeArgumentsForMember(group, inferred, args, warnings, canonicalOp: null);
            }
            op = inferred.Op;
            warnings.Add($"Assumed op '{op}' for '{group.Name}' from the arguments supplied; pass 'op' explicitly.");
        }

        var member = group.Members.FirstOrDefault(m => string.Equals(m.Op, op, StringComparison.OrdinalIgnoreCase));
        if (member == null)
            return ProjectToolUnfoldResult.Fail(new ProjectToolContractError(
                ProjectToolContract.EnumMismatch, "$.op",
                $"'{op}' is not an operation of '{group.Name}'. Allowed: {allowed}.",
                group.Members.Select(m => m.Op).FirstOrDefault(o =>
                    o.Contains(op!, StringComparison.OrdinalIgnoreCase) || op!.Contains(o, StringComparison.OrdinalIgnoreCase))));

        return NormalizeArgumentsForMember(group, member, args, warnings,
            member.CanonicalOwnsOp ? member.Op : null);
    }

    /// <summary>Picks the only member whose argument contract the supplied arguments can satisfy.</summary>
    private static FoldMember? InferMember(FoldGroup group, JObject args)
    {
        // Some providers populate every optional property in a union-shaped tool schema with an
        // empty placeholder. Those placeholders cannot identify an operation and must not make an
        // otherwise unambiguous call look ambiguous.
        var supplied = args.Properties()
            .Where(p => !string.Equals(p.Name, "op", StringComparison.Ordinal)
                        && !IsEmptyPlaceholder(p.Value))
            .Select(p => p.Name).ToList();
        var candidates = new List<FoldMember>();
        foreach (var byTool in group.Members.GroupBy(m => m.CanonicalTool, StringComparer.Ordinal))
        {
            if (!canonicalSchemas.Value.TryGetValue(byTool.Key, out var schema)) continue;
            var properties = schema["properties"] as JObject;
            var required = (schema["required"] as JArray)?.Values<string>()
                .Where(r => !string.IsNullOrEmpty(r) && r != "op") ?? Enumerable.Empty<string>();
            if (supplied.Any(name => properties?.Property(name, StringComparison.Ordinal) == null)) continue;
            if (required.Any(name => args.Property(name!, StringComparison.Ordinal) is not { } property
                                     || IsEmptyPlaceholder(property.Value))) continue;
            candidates.Add(byTool.First());
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Projects a folded union payload onto the selected canonical operation. Some providers fill
    /// every property in the flat union schema, including sibling-operation fields with typed
    /// defaults such as false, zero, or the first enum value. Once an explicit op has selected the
    /// canonical member those sibling fields are provider artefacts, regardless of their value, and
    /// are discarded with an audit warning. Names that no member declares still fail so typos are
    /// never silently swallowed.
    /// </summary>
    private static ProjectToolUnfoldResult NormalizeArgumentsForMember(
        FoldGroup group, FoldMember member, JObject args, List<string> warnings, string? canonicalOp)
    {
        var canonicalArgs = (JObject)args.DeepClone();
        if (member.CanonicalOwnsOp)
        {
            if (canonicalOp == null) canonicalArgs.Remove("op");
            else canonicalArgs["op"] = canonicalOp;
        }
        else canonicalArgs.Remove("op");

        if (!canonicalSchemas.Value.TryGetValue(member.CanonicalTool, out var schema))
            return ProjectToolUnfoldResult.Ok(member.CanonicalTool,
                canonicalArgs.ToString(Formatting.None), warnings);

        var dropped = new List<string>();
        foreach (var property in canonicalArgs.Properties().ToList())
        {
            if (SchemaAcceptsProperty(schema, property.Name)) continue;

            // The outer op is the discriminator. Any real field belonging only to another member
            // came from the provider's saturated union payload and cannot apply to this operation.
            // Unknown names still receive the normal typo/error treatment for every value.
            if (GroupDeclaresProperty(group, property.Name))
            {
                dropped.Add(property.Name);
                property.Remove();
                continue;
            }

            var names = (schema["properties"] as JObject)?.Properties()
                .Select(p => p.Name).Where(name => name != "op").ToList() ?? new List<string>();
            string allowed = names.Count == 0 ? "none" : string.Join(", ", names);
            return ProjectToolUnfoldResult.Fail(new ProjectToolContractError(
                ProjectToolContract.UnknownProperty,
                "$." + property.Name,
                $"Argument '{property.Name}' does not apply to '{group.Name}' op '{member.Op}'. Allowed arguments: {allowed}."));
        }

        if (dropped.Count > 0)
            warnings.Add($"Ignored arguments not used by '{group.Name}' op '{member.Op}': {string.Join(", ", dropped)}.");

        return ProjectToolUnfoldResult.Ok(member.CanonicalTool,
            canonicalArgs.ToString(Formatting.None), warnings);
    }

    private static bool GroupDeclaresProperty(FoldGroup group, string name) =>
        group.Members.Select(m => m.CanonicalTool).Distinct(StringComparer.Ordinal)
            .Any(tool => canonicalSchemas.Value.TryGetValue(tool, out var schema)
                         && (schema["properties"] as JObject)?.Property(name, StringComparison.Ordinal) != null);

    private static bool SchemaAcceptsProperty(JObject schema, string name)
    {
        if ((schema["properties"] as JObject)?.Property(name, StringComparison.Ordinal) != null) return true;
        var additional = schema["additionalProperties"];
        if (additional is JObject) return true;
        if (additional?.Type == JTokenType.Boolean) return additional.Value<bool>();
        return schema["properties"] == null;
    }

    private static bool IsEmptyPlaceholder(JToken token) => token.Type switch
    {
        JTokenType.Null or JTokenType.Undefined => true,
        JTokenType.String => string.IsNullOrWhiteSpace(token.Value<string>()),
        JTokenType.Array => token.Children().All(IsEmptyPlaceholder),
        JTokenType.Object => token.Children<JProperty>().All(p => IsEmptyPlaceholder(p.Value)),
        _ => false,
    };

    private static HFWrapper.HFTool BuildFoldedTool(FoldGroup group, IReadOnlyList<FoldMember> members,
        IReadOnlyDictionary<string, HFWrapper.HFTool> available)
    {
        var opValues = members.Select(m => m.Op).Distinct(StringComparer.Ordinal).ToList();
        var properties = new JObject
        {
            ["op"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(opValues),
                ["description"] = "Which operation to perform. Only arguments belonging to the selected op are forwarded; provider-populated fields for other ops are ignored.",
            },
        };

        // Property descriptions carry their own op scope, so a union of arguments stays unambiguous.
        var variants = new Dictionary<string, List<(string Description, List<string> Ops)>>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var byTool in members.GroupBy(m => m.CanonicalTool, StringComparer.Ordinal))
        {
            var tool = available[byTool.Key];
            var schema = SchemaOf(tool);
            var toolOps = byTool.Select(m => m.Op).ToList();
            bool ownsOp = byTool.First().CanonicalOwnsOp;

            var required = ((schema["required"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrEmpty(r) && r != "op").ToList();
            lines.Add($"• op={string.Join(" | ", toolOps)} — {tool.function?.description?.Trim()}" +
                      (required.Count > 0 ? $" (requires: {string.Join(", ", required)})" : ""));

            if (schema["properties"] is not JObject toolProperties) continue;
            foreach (var property in toolProperties.Properties())
            {
                if (ownsOp && property.Name == "op") continue;
                if (!properties.ContainsKey(property.Name))
                    properties[property.Name] = property.Value.DeepClone();

                string description = (property.Value as JObject)?["description"]?.Value<string>()?.Trim() ?? "";
                if (!variants.TryGetValue(property.Name, out var list))
                    variants[property.Name] = list = new List<(string, List<string>)>();
                var existing = list.FirstOrDefault(v => string.Equals(v.Description, description, StringComparison.Ordinal));
                if (existing.Ops != null) existing.Ops.AddRange(toolOps);
                else list.Add((description, new List<string>(toolOps)));
            }
        }

        foreach (var (name, list) in variants)
        {
            if (properties[name] is not JObject schema) continue;
            schema["description"] = string.Join(" ", list
                .Select(v => (ScopePrefix(v.Ops, opValues) + v.Description).Trim())
                .Where(s => s.Length > 0));
        }

        return new HFWrapper.HFTool
        {
            function = new HFWrapper.HFFunctionDefinition
            {
                name = group.Name,
                description = group.Summary + "\n" + string.Join("\n", lines),
                parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JArray("op"),
                },
            },
        };
    }

    /// <summary>
    /// Names which ops an argument belongs to, stated whichever way round is shorter. A union of
    /// arguments is only unambiguous if each one says where it applies, but spelling out eight ops
    /// on every property costs more prompt than it explains.
    /// </summary>
    private static string ScopePrefix(IReadOnlyCollection<string> ops, IReadOnlyCollection<string> allOps)
    {
        var scoped = ops.Distinct(StringComparer.Ordinal).ToList();
        if (scoped.Count >= allOps.Count) return "";
        var excluded = allOps.Where(o => !scoped.Contains(o, StringComparer.Ordinal)).ToList();
        return excluded.Count < scoped.Count
            ? $"(not for op={string.Join("|", excluded)}) "
            : $"(op={string.Join("|", scoped)}) ";
    }

    private static JObject SchemaOf(HFWrapper.HFTool tool)
    {
        if (tool.function?.parameters == null) return new JObject();
        return tool.function.parameters is JObject schema
            ? schema
            : JObject.Parse(JsonConvert.SerializeObject(tool.function.parameters));
    }
}
