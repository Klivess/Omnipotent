using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.ServiceTools;

/// <summary>
/// Turns the registry into the tool definitions offered to the model, and into the prompt text that
/// tells it what exists.
///
/// A service's ops are folded into ONE tool per group carrying an 'op' selector, in the same shape
/// Projects' ProjectToolFacade uses: a union of every member's properties, each description prefixed
/// with the ops it applies to. The union is presentation only - a call is validated against the
/// individual operation's own schema by <see cref="OmniToolInvoker"/>, so a property that two ops
/// declare differently is still checked correctly for whichever op was named.
/// </summary>
public static class OmniToolCatalog
{
    public const string UniversalServiceTool = "omniservice";
    public const string UniversalApiTool = "omni_api";

    // -- Dedicated per-service tools --

    /// <summary>Builds the dedicated tools for the pinned services (or for every annotated service when
    /// <paramref name="offerAll"/> is set). Order is deterministic: the offered array must be
    /// byte-identical between runs with the same settings or every request misses the prefix cache.</summary>
    public static List<HFWrapper.HFTool> BuildServiceTools(OmniToolRegistry registry,
        IEnumerable<string> pinnedKeys, bool offerAll)
    {
        var pinned = new HashSet<string>(
            pinnedKeys?.Select(k => k?.Trim() ?? "").Where(k => k.Length > 0) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var tools = new List<HFWrapper.HFTool>();
        foreach (var tool in registry.Tools.OrderBy(t => t.ToolName, StringComparer.Ordinal))
        {
            if (!offerAll && !IsPinned(tool, pinned)) continue;
            tools.Add(BuildFoldedTool(tool));
        }
        return tools;
    }

    /// <summary>A tool is pinned by its own name ("omniscience_people"), by its service key
    /// ("omniscience", which pins every group), or by a trailing-wildcard key ("omniscience_*").</summary>
    private static bool IsPinned(OmniToolGroup tool, HashSet<string> pinned)
    {
        if (pinned.Contains(tool.ToolName) || pinned.Contains(tool.Service.Key)) return true;
        foreach (var entry in pinned)
        {
            if (!entry.EndsWith("*", StringComparison.Ordinal)) continue;
            var prefix = entry[..^1];
            if (prefix.Length > 0 && tool.ToolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static HFWrapper.HFTool BuildFoldedTool(OmniToolGroup tool)
    {
        var ops = tool.Operations;
        var opNames = ops.Select(o => o.Op).ToList();

        var properties = new JObject
        {
            ["op"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(opNames.Cast<object>()),
                ["description"] = "Which operation to perform.",
            },
        };

        // Union every op's properties. First declaration wins the schema; the description records
        // which ops the property belongs to so the model does not send it with the wrong op.
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var op in ops)
        {
            var opProps = op.ParameterSchema["properties"] as JObject;
            if (opProps == null) continue;
            foreach (var prop in opProps.Properties())
            {
                if (!owners.TryGetValue(prop.Name, out var list))
                {
                    owners[prop.Name] = list = new List<string>();
                    properties[prop.Name] = prop.Value.DeepClone();
                }
                list.Add(op.Op);
            }
        }

        foreach (var (name, ownerOps) in owners)
        {
            if (properties[name] is not JObject schema) continue;
            var prefix = ScopePrefix(ownerOps, opNames);
            if (prefix == null) continue;
            var existing = schema["description"]?.Value<string>();
            schema["description"] = string.IsNullOrWhiteSpace(existing) ? prefix : $"{prefix} {existing}";
        }

        var description = new StringBuilder();
        description.Append(tool.Summary.TrimEnd());
        description.Append("\nOperations:");
        foreach (var op in ops)
        {
            description.Append("\n• op=").Append(op.Op).Append(" — ").Append(op.Description.TrimEnd());
            var required = (op.ParameterSchema["required"] as JArray)?
                .Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (required is { Count: > 0 })
                description.Append(" (requires: ").Append(string.Join(", ", required)).Append(')');
            if (op.Destructive) description.Append(" [IRREVERSIBLE — needs Klives' approval]");
            else if (op.Mutating) description.Append(" [writes]");
            if (!op.Verified) description.Append(" [unverified]");
        }

        return new HFWrapper.HFTool
        {
            type = "function",
            function = new HFWrapper.HFFunctionDefinition
            {
                name = tool.ToolName,
                description = description.ToString(),
                parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JArray("op"),
                },
            },
        };
    }

    /// <summary>Writes the shortest honest note about which ops a property belongs to: "(op=a|b)" when
    /// it belongs to few, "(not for op=c)" when it belongs to nearly all, nothing when it belongs to
    /// every op. Lifted from the same idea in ProjectToolFacade.ScopePrefix.</summary>
    private static string? ScopePrefix(List<string> owners, List<string> allOps)
    {
        if (owners.Count >= allOps.Count) return null;

        var excluded = allOps.Where(o => !owners.Contains(o, StringComparer.Ordinal)).ToList();
        var include = "(op=" + string.Join("|", owners) + ")";
        var exclude = "(not for op=" + string.Join("|", excluded) + ")";
        return include.Length <= exclude.Length ? include : exclude;
    }

    // -- Universal tools --

    /// <summary>The two always-offered tools that reach everything the dedicated tools do not. Their
    /// definitions are constant, so they never move the prompt cache.</summary>
    public static List<HFWrapper.HFTool> BuildUniversalTools(bool includeApi)
    {
        var tools = new List<HFWrapper.HFTool>
        {
            new()
            {
                type = "function",
                function = new HFWrapper.HFFunctionDefinition
                {
                    name = UniversalServiceTool,
                    description =
                        "Reach ANY Omnipotent service directly, including ones with no dedicated tool. This is how you "
                        + "read and drive the platform — prefer it over writing C# in execute_csharp.\n"
                        + "• op=list — every service and what it does. Start here when unsure which service owns something.\n"
                        + "• op=describe — one service's operations with their exact arguments (requires: service). "
                        + "Call this before op=call on a service you have not used this turn; it costs one step and "
                        + "removes all guessing.\n"
                        + "• op=call — run one operation (requires: service, method; args as needed).\n"
                        + "Operations marked unverified are read-only: they were derived from a method signature, not "
                        + "written by hand, so their descriptions are thin and they may not be used to change anything.",
                    parameters = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["op"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("list", "describe", "call"),
                                ["description"] = "Which operation to perform.",
                            },
                            ["service"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "(op=describe|call) Service key from op=list, e.g. \"omniscience\".",
                            },
                            ["method"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "(op=call) The operation name from op=describe.",
                            },
                            ["args"] = new JObject
                            {
                                ["type"] = "object",
                                ["description"] = "(op=call) The operation's arguments as an object.",
                            },
                        },
                        ["required"] = new JArray("op"),
                    },
                },
            },
        };

        if (!includeApi) return tools;

        tools.Add(new HFWrapper.HFTool
        {
            type = "function",
            function = new HFWrapper.HFFunctionDefinition
            {
                name = UniversalApiTool,
                description =
                    "Call one of Omnipotent's own HTTP API routes in-process (loopback, authenticated as Klives). This "
                    + "reaches every registered route — the exact surface the Klives Management website uses — including "
                    + "routes no tool covers yet.\n"
                    + "Prefer a dedicated tool or omniservice when one exists: those are typed, validated and return "
                    + "compact results. Reach for this when the capability is only exposed as a route.\n"
                    + "Paths are server-relative and must start with '/', e.g. \"/omniscience/stats/overview\". This is "
                    + "NOT for the public internet — use web_fetch for that.",
                parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["method"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("GET", "POST", "PUT", "DELETE", "PATCH"),
                            ["description"] = "HTTP method. Anything other than GET is treated as a write.",
                        },
                        ["path"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] = "Server-relative route path, e.g. \"/omniscience/persons\".",
                        },
                        ["query"] = new JObject
                        {
                            ["type"] = "object",
                            ["description"] = "Query-string parameters as an object, e.g. {\"personId\":\"abc\"}.",
                            ["additionalProperties"] = new JObject { ["type"] = "string" },
                        },
                        ["body"] = new JObject
                        {
                            ["type"] = "object",
                            ["description"] = "JSON request body, for POST/PUT/PATCH.",
                        },
                    },
                    ["required"] = new JArray("method", "path"),
                },
            },
        });

        return tools;
    }

    // -- Prompt text --

    /// <summary>The [Service Surface] block: one line per service. Goes ABOVE the cache breakpoint —
    /// it is identical on every run, and it is what stops the agent having to discover that a service
    /// exists before it can use it.</summary>
    public static string BuildServiceSurfaceBlock(OmniToolRegistry registry, IEnumerable<string> pinnedKeys, bool offerAll)
    {
        var pinned = new HashSet<string>(
            pinnedKeys?.Select(k => k?.Trim() ?? "").Where(k => k.Length > 0) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("[Service Surface]");
        sb.AppendLine("Omnipotent is one process running these services. You can read and drive all of them:");
        sb.AppendLine("dedicated tools where listed, otherwise omniservice (op=describe then op=call).");
        sb.AppendLine("Do NOT reach for execute_csharp to call a service — that is the slow path and it is easy to get wrong.");
        sb.AppendLine();

        foreach (var service in registry.Services.OrderByDescending(s => s.Annotated).ThenBy(s => s.Key, StringComparer.Ordinal))
        {
            var dedicated = service.Groups
                .Where(g => offerAll || IsPinned(g, pinned))
                .Select(g => g.ToolName)
                .ToList();

            sb.Append("  ").Append(service.Key).Append(" — ").Append(FirstSentence(service.Summary));
            sb.Append(' ').Append('(').Append(service.Operations.Count).Append(" ops");
            if (!service.Annotated) sb.Append(", unverified/read-only");
            sb.Append(')');
            if (dedicated.Count > 0) sb.Append(" → tools: ").Append(string.Join(", ", dedicated));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>omniservice op=list.</summary>
    public static string RenderServiceList(OmniToolRegistry registry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{registry.Services.Count} services, {registry.Operations.Count} operations.");
        sb.AppendLine("Call omniservice op=describe with a service key for its operations and arguments.");
        sb.AppendLine();
        foreach (var service in registry.Services.OrderByDescending(s => s.Annotated).ThenBy(s => s.Key, StringComparer.Ordinal))
        {
            sb.Append(service.Key).Append("  [").Append(service.Operations.Count).Append(service.Annotated ? " ops]" : " ops, unverified]");
            sb.Append("  ").AppendLine(FirstSentence(service.Summary));
        }
        return sb.ToString();
    }

    /// <summary>omniservice op=describe — the full schemas, so the next call can be right first time.</summary>
    public static string RenderServiceDescription(OmniServiceSurface service)
    {
        var sb = new StringBuilder();
        sb.Append(service.Key).Append(" — ").AppendLine(service.Summary);
        if (!service.Annotated)
            sb.AppendLine("These operations were derived reflectively from method signatures: descriptions are thin "
                        + "and they are read-only.");
        if (service.Groups.Count > 0)
            sb.Append("Dedicated tools: ").AppendLine(string.Join(", ", service.Groups.Select(g => g.ToolName)));
        sb.AppendLine();

        foreach (var op in service.Operations)
        {
            sb.Append("• ").Append(op.Op);
            if (op.Destructive) sb.Append("  [IRREVERSIBLE — needs approval]");
            else if (op.Mutating) sb.Append("  [writes]");
            if (!op.Verified) sb.Append("  [unverified, read-only]");
            sb.AppendLine();
            sb.Append("    ").AppendLine(op.Description);

            var properties = op.ParameterSchema["properties"] as JObject;
            var required = (op.ParameterSchema["required"] as JArray)?
                .Select(t => t.Value<string>()).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();

            if (properties == null || properties.Count == 0)
            {
                sb.AppendLine("    args: (none)");
                continue;
            }

            sb.AppendLine("    args:");
            foreach (var prop in properties.Properties())
            {
                var schema = prop.Value as JObject;
                var type = schema?["type"]?.Value<string>() ?? "string";
                var enumValues = schema?["enum"] as JArray;
                sb.Append("      ").Append(prop.Name).Append(": ").Append(type);
                if (enumValues is { Count: > 0 })
                    sb.Append(" (").Append(string.Join('|', enumValues.Select(v => v.Value<string>()))).Append(')');
                if (required.Contains(prop.Name)) sb.Append("  REQUIRED");
                var desc = schema?["description"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(desc)) sb.Append(" — ").Append(desc);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var trimmed = text.Trim();
        var stop = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (stop < 0) stop = trimmed.IndexOf('\n');
        var sentence = stop > 0 ? trimmed[..(stop + 1)].Trim() : trimmed;
        return sentence.Length > 180 ? sentence[..177] + "..." : sentence;
    }
}
